using Microsoft.Xna.Framework;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.ComponentModel;
#if DEBUG
using System.IO;
#else
using Barotrauma.IO;
#endif
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;

namespace Barotrauma
{
    [Flags]
    public enum SubmarineTag
    {
        [Description("Shuttle")]
        Shuttle = 1,
        [Description("Hide in menus")]
        HideInMenus = 2
    }

    public enum SubmarineType { Player, Outpost, OutpostModule, Wreck, BeaconStation, EnemySubmarine, Ruin }

    partial class SubmarineInfo : IDisposable
    {
        private static List<SubmarineInfo> savedSubmarines = new List<SubmarineInfo>();
        public static IEnumerable<SubmarineInfo> SavedSubmarines => savedSubmarines;

        private Task hashTask;
        private Md5Hash hash;

        public readonly DateTime LastModifiedTime;

        public SubmarineTag Tags { get; private set; }

        public int RecommendedCrewSizeMin = 1, RecommendedCrewSizeMax = 2;
        
        public enum CrewExperienceLevel
        {
            Unknown,
            CrewExperienceLow,
            CrewExperienceMid,
            CrewExperienceHigh
        }
        public CrewExperienceLevel RecommendedCrewExperience;

        public int Tier
        {
            get;
            set;
        }

        /// <summary>
        /// A random int that gets assigned when saving the sub. Used in mp campaign to verify that sub files match
        /// </summary>
        public int EqualityCheckVal { get; private set; }

        public HashSet<string> RequiredContentPackages = new HashSet<string>();

        public string Name
        {
            get;
            set;
        }

        public LocalizedString DisplayName
        {
            get;
            set;
        }

        public LocalizedString Description
        {
            get;
            set;
        }

        public int Price
        {
            get;
            set;
        }

        public bool InitialSuppliesSpawned
        {
            get;
            set;
        }

        public bool NoItems
        {
            get;
            set;
        }

        /// <summary>
        /// Note: Refreshed for loaded submarines when they are saved, when they are loaded, and on round end. If you need to refresh it, please use Submarine.CheckFuel() method!
        /// </summary>
        public bool LowFuel
        {
            get;
            set;
        }

        public Version GameVersion
        {
            get;
            set;
        }

        public SubmarineType Type { get; set; }

        public bool IsManuallyOutfitted { get; set; }

        private Identifier classIdentifier;
        public SubmarineClass Class
        {
            get => SubmarineClass.Find(classIdentifier) ?? SubmarineClass.Undefined;
            set => classIdentifier = value.Identifier;
        }

        public OutpostModuleInfo OutpostModuleInfo { get; set; }
        public BeaconStationInfo BeaconStationInfo { get; set; }
        public WreckInfo WreckInfo { get; set; }
        public EnemySubmarineInfo EnemySubmarineInfo { get; set; }

        public ExtraSubmarineInfo GetExtraSubmarineInfo => BeaconStationInfo ?? WreckInfo as ExtraSubmarineInfo;

        public ImmutableHashSet<Identifier> OutpostTags { get; set; } = ImmutableHashSet<Identifier>.Empty;

        public bool IsOutpost => Type == SubmarineType.Outpost || Type == SubmarineType.OutpostModule;

        public bool IsWreck => Type == SubmarineType.Wreck;
        public bool IsBeacon => Type == SubmarineType.BeaconStation;
        public bool IsEnemySubmarine => Type == SubmarineType.EnemySubmarine;
        public bool IsPlayer => Type == SubmarineType.Player;
        public bool IsRuin => Type == SubmarineType.Ruin;
        
        /// <summary>
        /// Ruin modules are of type SubmarineType.OutpostModule, until the ruin generator (or the test game mode) sets them as ruins.
        /// This is a helper workaround check intended to be used only in the context of the sub editor and the test game mode, where ruins aren't generated.
        /// </summary>
        public bool ShouldBeRuin => Type is SubmarineType.Ruin or SubmarineType.OutpostModule && 
                                            (OutpostModuleInfo.ModuleFlags.Contains("ruin".ToIdentifier()) || 
                                             OutpostModuleInfo.ModuleFlags.Contains("ruinentrance".ToIdentifier()) ||
                                             OutpostModuleInfo.ModuleFlags.Contains("ruinvault".ToIdentifier()) ||
                                             OutpostModuleInfo.ModuleFlags.Contains("ruinworkshop".ToIdentifier()) ||
                                             OutpostModuleInfo.ModuleFlags.Contains("ruinshrine".ToIdentifier()));

        public bool IsCampaignCompatible => IsPlayer && !HasTag(SubmarineTag.Shuttle) && !HasTag(SubmarineTag.HideInMenus) && Class.UsableInCampaign;
        public bool IsCampaignCompatibleIgnoreClass => IsPlayer && !HasTag(SubmarineTag.Shuttle) && !HasTag(SubmarineTag.HideInMenus);

        public bool AllowPreviewImage => Type == SubmarineType.Player;

        public Md5Hash MD5Hash
        {
            get
            {
                if (hash == null)
                {
                    if (hashTask == null)
                    {
                        XDocument doc = OpenFile(FilePath);
                        StartHashDocTask(doc);
                    }
                    hashTask.Wait();
                    hashTask = null;
                }

                return hash;
            }
        }

        public bool CalculatingHash
        {
            get { return hashTask != null && !hashTask.IsCompleted; }
        }

        public Vector2 Dimensions
        {
            get;
            private set;
        }

        public int CargoCapacity
        {
            get;
            private set;
        }

        public string FilePath
        {
            get;
            set;
        }

        public XElement SubmarineElement
        {
            get;
            private set;
        }

        public override string ToString()
        {
            return "Barotrauma.SubmarineInfo (" + Name + ")";
        }

        public bool IsFileCorrupted
        {
            get;
            private set;
        }

        private bool? requiredContentPackagesInstalled;
        public bool RequiredContentPackagesInstalled
        {
            get
            {
                if (requiredContentPackagesInstalled.HasValue) { return requiredContentPackagesInstalled.Value; }
                return RequiredContentPackages.All(reqName => ContentPackageManager.EnabledPackages.All.Any(contentPackage => contentPackage.NameMatches(reqName)));
            }
            set
            {
                requiredContentPackagesInstalled = value;
            }
        }

        private bool? subsLeftBehind;
        public bool SubsLeftBehind
        {
            get
            {
                if (subsLeftBehind.HasValue) { return subsLeftBehind.Value; }
                CheckSubsLeftBehind(SubmarineElement);
                return subsLeftBehind.Value;
            }
        }
        
        public readonly List<ushort> LeftBehindDockingPortIDs = new List<ushort>();
        public readonly List<ushort> BlockedDockingPortIDs = new List<ushort>();

        public bool LeftBehindSubDockingPortOccupied
        {
            get; private set;
        }

        public OutpostGenerationParams OutpostGenerationParams;

        public readonly Dictionary<Identifier, List<Character>> OutpostNPCs = new Dictionary<Identifier, List<Character>>();

        /// <summary>
        /// Names of layers that get automatically hidden when loading the sub
        /// </summary>
        public HashSet<Identifier> LayersHiddenByDefault { get; private set; } = new HashSet<Identifier>();

        //constructors & generation ----------------------------------------------------
        public SubmarineInfo()
        {
            FilePath = null;
            DisplayName = TextManager.Get("UnspecifiedSubFileName");
            Name = DisplayName.Value;
            IsFileCorrupted = false;
            RequiredContentPackages = new HashSet<string>();
        }

        public SubmarineInfo(string filePath, string hash = "", XElement element = null, bool tryLoad = true)
        {
            FilePath = filePath;
            if (!string.IsNullOrEmpty(filePath) && File.Exists(filePath))
            {
                LastModifiedTime = File.GetLastWriteTime(filePath);
            }
            try
            {
                DisplayName = Path.GetFileNameWithoutExtension(filePath);
                Name = DisplayName.Value;
            }
            catch (Exception e)
            {
                DebugConsole.ThrowError("Error loading submarine " + filePath + "!", e);
            }

            if (!string.IsNullOrWhiteSpace(hash))
            {
                this.hash = Md5Hash.StringAsHash(hash);
            }

            IsFileCorrupted = false;

            RequiredContentPackages = new HashSet<string>();

            if (element == null && tryLoad)
            {
                Reload();
            }
            else
            {
                SubmarineElement = element;
            }

            Name = SubmarineElement.GetAttributeString("name", null) ?? Name;

            Init();
        }

        public SubmarineInfo(Submarine sub) : this(sub.Info)
        {
            GameVersion = GameMain.Version;
            SubmarineElement = new XElement("Submarine");
            sub.SaveToXElement(SubmarineElement);
            Init();
        }

        public SubmarineInfo(SubmarineInfo original)
        {
            Name = original.Name;
            DisplayName = original.DisplayName;
            Description = original.Description;
            Price = original.Price;
            InitialSuppliesSpawned = original.InitialSuppliesSpawned;
            NoItems = original.NoItems;
            LowFuel = original.LowFuel;
            GameVersion = original.GameVersion;
            Type = original.Type;
            Class = original.Class;
            hash = !string.IsNullOrEmpty(original.FilePath) && File.Exists(original.FilePath) ? original.MD5Hash : null;
            Dimensions = original.Dimensions;
            CargoCapacity = original.CargoCapacity;
            FilePath = original.FilePath;
            RequiredContentPackages = new HashSet<string>(original.RequiredContentPackages);
            IsFileCorrupted = original.IsFileCorrupted;
            SubmarineElement = original.SubmarineElement;
            EqualityCheckVal = original.EqualityCheckVal;
            RecommendedCrewExperience = original.RecommendedCrewExperience;
            RecommendedCrewSizeMin = original.RecommendedCrewSizeMin;
            RecommendedCrewSizeMax = original.RecommendedCrewSizeMax;
            Tier = original.Tier;
            IsManuallyOutfitted = original.IsManuallyOutfitted;
            Tags = original.Tags;
            OutpostGenerationParams = original.OutpostGenerationParams;
            LayersHiddenByDefault = original.LayersHiddenByDefault;
            OutpostTags = original.OutpostTags;
            if (original.OutpostModuleInfo != null)
            {
                OutpostModuleInfo = new OutpostModuleInfo(original.OutpostModuleInfo);
            }
            else if (original.BeaconStationInfo != null)
            {
                BeaconStationInfo = new BeaconStationInfo(original.BeaconStationInfo);
            }
            else if (original.EnemySubmarineInfo != null)
            {
                EnemySubmarineInfo = new EnemySubmarineInfo(original.EnemySubmarineInfo);
            }
            else if (original.WreckInfo != null)
            {
                WreckInfo = new WreckInfo(original.WreckInfo);
            }
#if CLIENT
            PreviewImage = original.PreviewImage != null ? new Sprite(original.PreviewImage) : null;
#endif
        }

        public void Reload()
        {
            XDocument doc = null;
            int maxLoadRetries = 4;
            for (int i = 0; i <= maxLoadRetries; i++)
            {
                doc = OpenFile(FilePath, out Exception e);
                if (e != null && !(e is System.IO.IOException)) { break; }
                if (doc != null || i == maxLoadRetries || !File.Exists(FilePath)) { break; }
                DebugConsole.NewMessage("Opening submarine file \"" + FilePath + "\" failed, retrying in 250 ms...");
                Thread.Sleep(250);
            }
            if (doc?.Root == null)
            {
                IsFileCorrupted = true;
                return;
            }
            if (hash == null)
            {
                StartHashDocTask(doc);
            }
            SubmarineElement = doc.Root;
        }

        private void Init()
        {
            DisplayName = TextManager.Get("Submarine.Name." + Name).Fallback(Name);

            Description = TextManager.Get("Submarine.Description." + Name).Fallback(SubmarineElement.GetAttributeString("description", ""));

            EqualityCheckVal = SubmarineElement.GetAttributeInt("checkval", 0);

            Price = SubmarineElement.GetAttributeInt("price", 1000);

            InitialSuppliesSpawned = SubmarineElement.GetAttributeBool("initialsuppliesspawned", false);
            NoItems = SubmarineElement.GetAttributeBool("noitems", false);
            LowFuel = SubmarineElement.GetAttributeBool("lowfuel", false);
            IsManuallyOutfitted = SubmarineElement.GetAttributeBool("ismanuallyoutfitted", false);

            GameVersion = new Version(SubmarineElement.GetAttributeString("gameversion", "0.0.0.0"));
            if (Enum.TryParse(SubmarineElement.GetAttributeString("tags", ""), out SubmarineTag tags))
            {
                Tags = tags;
            }
            Dimensions = SubmarineElement.GetAttributeVector2("dimensions", Vector2.Zero);
            CargoCapacity = SubmarineElement.GetAttributeInt("cargocapacity", -1);
            RecommendedCrewSizeMin = SubmarineElement.GetAttributeInt("recommendedcrewsizemin", 0);
            RecommendedCrewSizeMax = SubmarineElement.GetAttributeInt("recommendedcrewsizemax", 0);
            var recommendedCrewExperience = SubmarineElement.GetAttributeIdentifier("recommendedcrewexperience", CrewExperienceLevel.Unknown.ToIdentifier());

            foreach (Identifier hiddenLayer in SubmarineElement.GetAttributeIdentifierArray("layerhiddenbydefault", Array.Empty<Identifier>()))
            {
                LayersHiddenByDefault.Add(hiddenLayer);
            }

            // Backwards compatibility
            if (recommendedCrewExperience == "Beginner")
            {
                RecommendedCrewExperience = CrewExperienceLevel.CrewExperienceLow;
            }
            else if (recommendedCrewExperience == "Intermediate")
            {
                RecommendedCrewExperience = CrewExperienceLevel.CrewExperienceMid;
            }
            else if (recommendedCrewExperience == "Experienced")
            {
                RecommendedCrewExperience = CrewExperienceLevel.CrewExperienceHigh;
            }
            else
            {
                Enum.TryParse(recommendedCrewExperience.Value, ignoreCase: true, out RecommendedCrewExperience);
            }
            Tier = SubmarineElement.GetAttributeInt("tier", GetDefaultTier(Price));

            OutpostTags = SubmarineElement.GetAttributeIdentifierImmutableHashSet(nameof(OutpostTags), ImmutableHashSet<Identifier>.Empty);

            if (SubmarineElement?.Attribute("type") != null)
            {
                if (Enum.TryParse(SubmarineElement.GetAttributeString("type", ""), out SubmarineType type))
                {
                    Type = type;
                    if (Type == SubmarineType.OutpostModule)
                    {
                        OutpostModuleInfo = new OutpostModuleInfo(this, SubmarineElement);
                    }
                    else if (Type == SubmarineType.BeaconStation)
                    {
                        BeaconStationInfo = new BeaconStationInfo(this, SubmarineElement);
                    }
                    else if (Type == SubmarineType.EnemySubmarine)
                    {
                        EnemySubmarineInfo = new EnemySubmarineInfo(this, SubmarineElement);
                    }
                    else if (Type == SubmarineType.Wreck)
                    {
                        WreckInfo = new WreckInfo(this, SubmarineElement);
                    }
                }
            }

            if (Type == SubmarineType.Player)
            {
                classIdentifier = SubmarineElement.GetAttributeIdentifier("class", SubmarineClass.UndefinedIdentifier);
            }
            else
            {
                classIdentifier = SubmarineClass.UndefinedIdentifier;
            }

            RequiredContentPackages.Clear();
            string[] contentPackageNames = SubmarineElement.GetAttributeStringArray("requiredcontentpackages", Array.Empty<string>());
            foreach (string contentPackageName in contentPackageNames)
            {
                RequiredContentPackages.Add(contentPackageName);
            }

            InitProjectSpecific();
        }

        partial void InitProjectSpecific();

        public void Dispose()
        {
#if CLIENT
            PreviewImage?.Remove();
            PreviewImage = null;
#endif
            if (savedSubmarines.Contains(this)) { savedSubmarines.Remove(this); }
        }

        public bool IsVanillaSubmarine()
        {
            if (FilePath == null) { return false; }
            var vanilla = GameMain.VanillaContent;
            if (vanilla != null)
            {
                var vanillaSubs = vanilla.GetFiles<BaseSubFile>();
                string pathToCompare = FilePath.CleanUpPath();
                if (vanillaSubs.Any(sub => sub.Path == pathToCompare))
                {
                    return true;
                }
            }
            return false;
        }

        public void StartHashDocTask(XDocument doc)
        {
            if (hash != null) { return; }
            if (hashTask != null) { return; }

            hashTask = new Task(() =>
            {
                hash = Md5Hash.CalculateForString(doc.ToString(), Md5Hash.StringHashOptions.IgnoreWhitespace);
            });
            hashTask.Start();
        }

        public bool HasTag(SubmarineTag tag)
        {
            return Tags.HasFlag(tag);
        }

        public void AddTag(SubmarineTag tag)
        {
            if (Tags.HasFlag(tag)) return;

            Tags |= tag;
        }

        public void RemoveTag(SubmarineTag tag)
        {
            if (!Tags.HasFlag(tag)) return;

            Tags &= ~tag;
        }

        public void CheckSubsLeftBehind(XElement element = null)
        {
            if (element == null) { element = SubmarineElement; }

            subsLeftBehind = false;
            LeftBehindSubDockingPortOccupied = false;
            LeftBehindDockingPortIDs.Clear();
            BlockedDockingPortIDs.Clear();
            foreach (var subElement in element.Elements())
            {
                if (!subElement.Name.ToString().Equals("linkedsubmarine", StringComparison.OrdinalIgnoreCase)) { continue; }
                if (subElement.Attribute("location") == null) { continue; }

                subsLeftBehind = true;
                ushort targetDockingPortID = (ushort)subElement.GetAttributeInt("originallinkedto", 0);
                LeftBehindDockingPortIDs.Add(targetDockingPortID);
                XElement targetPortElement = targetDockingPortID == 0 ? null :
                    element.Elements().FirstOrDefault(e => e.GetAttributeInt("ID", 0) == targetDockingPortID);
                if (targetPortElement != null && targetPortElement.GetAttributeIntArray("linked", Array.Empty<int>()).Length > 0)
                {
                    BlockedDockingPortIDs.Add(targetDockingPortID);
                    LeftBehindSubDockingPortOccupied = true;
                }
            }
        }

        /// <summary>
        /// Calculated from <see cref="SubmarineElement"/>. Can be used when the sub hasn't been loaded and we can't access <see cref="Submarine.RealWorldCrushDepth"/>.
        /// </summary>
        public bool IsCrushDepthDefinedInStructures(out float realWorldCrushDepth)
        {
            if (SubmarineElement == null)
            {
                realWorldCrushDepth = Level.DefaultRealWorldCrushDepth;
                return false;
            }
            bool structureCrushDepthsDefined = false;
            realWorldCrushDepth = float.PositiveInfinity;
            foreach (var structureElement in SubmarineElement.GetChildElements("structure"))
            {
                string name = structureElement.Attribute("name")?.Value ?? "";
                Identifier identifier = structureElement.GetAttributeIdentifier("identifier", "");
                var structurePrefab = Structure.FindPrefab(name, identifier);
                if (structurePrefab == null || !structurePrefab.Body) { continue; }
                if (!structureCrushDepthsDefined && structureElement.Attribute("crushdepth") != null)
                {
                    structureCrushDepthsDefined = true;
                }
                float structureCrushDepth = structureElement.GetAttributeFloat("crushdepth", float.PositiveInfinity);
                realWorldCrushDepth = Math.Min(structureCrushDepth, realWorldCrushDepth);
            }
            if (!structureCrushDepthsDefined)
            {
                realWorldCrushDepth = Level.DefaultRealWorldCrushDepth;
            }
            return structureCrushDepthsDefined;
        }
        public void AddOutpostNPCIdentifierOrTag(Character npc, Identifier idOrTag)
        {
            if (!OutpostNPCs.ContainsKey(idOrTag))
            {
                OutpostNPCs.Add(idOrTag, new List<Character>());
            }
            OutpostNPCs[idOrTag].Add(npc);
        }

        //saving/loading ----------------------------------------------------
        public void SaveAs(string filePath, System.IO.MemoryStream previewImage = null)
        {
            var newElement = new XElement(
                SubmarineElement.Name, 
                SubmarineElement.Attributes()
                    .Where(a => 
                        !string.Equals(a.Name.LocalName, "previewimage", StringComparison.InvariantCultureIgnoreCase) &&
                        !string.Equals(a.Name.LocalName, "name", StringComparison.InvariantCultureIgnoreCase)), 
                SubmarineElement.Elements());

            if (Type == SubmarineType.OutpostModule)
            {
                OutpostModuleInfo.Save(newElement);
                OutpostModuleInfo = new OutpostModuleInfo(this, newElement);
            }
            else if (Type == SubmarineType.BeaconStation)
            {
                BeaconStationInfo.Save(newElement);
                BeaconStationInfo = new BeaconStationInfo(this, newElement);
            }
            else if (Type == SubmarineType.EnemySubmarine)
            {
                EnemySubmarineInfo.Save(newElement);
                EnemySubmarineInfo = new EnemySubmarineInfo(this, newElement);
            }
            else if (Type == SubmarineType.Wreck)
            {
                WreckInfo.Save(newElement);
                WreckInfo = new WreckInfo(this, newElement);
            }
            XDocument doc = new XDocument(newElement);

            doc.Root.Add(new XAttribute("name", Name));
            if (previewImage != null && AllowPreviewImage)
            {
                doc.Root.Add(new XAttribute("previewimage", Convert.ToBase64String(previewImage.ToArray())));
            }

            SaveUtil.CompressStringToFile(filePath, doc.ToString());
        }

        public static void AddToSavedSubs(SubmarineInfo subInfo)
        {
            savedSubmarines.Add(subInfo);
        }

        public static void RemoveSavedSub(string filePath)
        {
            string fullPath = Path.GetFullPath(filePath);
            for (int i = savedSubmarines.Count - 1; i >= 0; i--)
            {
                if (Path.GetFullPath(savedSubmarines[i].FilePath) == fullPath)
                {
                    savedSubmarines[i].Dispose();
                }
            }
        }

        public static void RefreshSavedSub(string filePath)
        {
            RemoveSavedSub(filePath);
            if (File.Exists(filePath))
            {
                var subInfo = new SubmarineInfo(filePath);
                if (!subInfo.IsFileCorrupted)
                {
                    savedSubmarines.Add(subInfo);
                }
                savedSubmarines = savedSubmarines.OrderBy(s => s.FilePath ?? "").ToList();
            }
        }

        public static void RefreshSavedSubs()
        {
            var contentPackageSubs = ContentPackageManager.EnabledPackages.All.SelectMany(c => c.GetFiles<BaseSubFile>());

            for (int i = savedSubmarines.Count - 1; i >= 0; i--)
            {
                if (File.Exists(savedSubmarines[i].FilePath))
                {
                    bool isDownloadedSub = Path.GetFullPath(Path.GetDirectoryName(savedSubmarines[i].FilePath)) == Path.GetFullPath(SaveUtil.SubmarineDownloadFolder);
                    bool isInContentPackage = contentPackageSubs.Any(f => f.Path == savedSubmarines[i].FilePath);
                    if (isDownloadedSub) { continue; }
                    if (savedSubmarines[i].LastModifiedTime == File.GetLastWriteTime(savedSubmarines[i].FilePath) && isInContentPackage) { continue; }
                }
                savedSubmarines[i].Dispose();
            }

            List<string> filePaths = new List<string>();
            foreach (BaseSubFile subFile in contentPackageSubs)
            {
                if (!File.Exists(subFile.Path.Value)) { continue; }
                if (!filePaths.Any(fp => fp == subFile.Path))
                {
                    filePaths.Add(subFile.Path.Value);
                }
            }

            filePaths.RemoveAll(p => savedSubmarines.Any(sub => sub.FilePath == p));

            foreach (string path in filePaths)
            {
                var subInfo = new SubmarineInfo(path);
                if (!subInfo.IsFileCorrupted)
                {
                    savedSubmarines.Add(subInfo);
                }
            }
        }

        public static XDocument OpenFile(string file)
        {
            return OpenFile(file, out _);
        }

        public static XDocument OpenFile(string file, out Exception exception)
        {
            XDocument doc = null;
            string extension = "";
            exception = null;

            try
            {
                extension = System.IO.Path.GetExtension(file);
            }
            catch
            {
                //no file extension specified: try using the default one
                file += ".sub";
            }

            if (string.IsNullOrWhiteSpace(extension))
            {
                extension = ".sub";
                file += ".sub";
            }

            if (extension == ".sub")
            {
                System.IO.Stream stream;
                try
                {
                    stream = SaveUtil.DecompressFileToStream(file);
                }
                catch (System.IO.FileNotFoundException e)
                {
                    exception = e;
                    DebugConsole.ThrowError("Loading submarine \"" + file + "\" failed! (File not found) " + Environment.StackTrace.CleanupStackTrace(), e);
                    return null;
                }
                catch (Exception e)
                {
                    exception = e;
                    DebugConsole.ThrowError("Loading submarine \"" + file + "\" failed!", e);
                    return null;
                }

                try
                {
                    stream.Position = 0;
                    using (var reader = XMLExtensions.CreateReader(stream))
                    {
                        doc = XDocument.Load(reader);
                    }
                    stream.Close();
                    stream.Dispose();
                }

                catch (Exception e)
                {
                    exception = e;
                    DebugConsole.ThrowError("Loading submarine \"" + file + "\" failed! (" + e.Message + ")");
                    return null;
                }
            }
            else if (extension == ".xml")
            {
                try
                {
                    ToolBox.IsProperFilenameCase(file);
                    using var stream = File.Open(file, System.IO.FileMode.Open, System.IO.FileAccess.Read);
                    using var reader = XMLExtensions.CreateReader(stream);
                    doc = XDocument.Load(reader);
                }
                catch (Exception e)
                {
                    exception = e;
                    DebugConsole.ThrowError("Loading submarine \"" + file + "\" failed! (" + e.Message + ")");
                    return null;
                }
            }
            else
            {
                DebugConsole.ThrowError("Couldn't load submarine \"" + file + "! (Unrecognized file extension)");
                return null;
            }

            return doc;
        }

        public int GetPrice(Location location = null, ImmutableHashSet<Character> characterList = null)
        {
            if (location is null)
            {
                if (GameMain.GameSession?.Campaign?.Map?.CurrentLocation is { } currentLocation)
                {
                    location = currentLocation;
                }
                else
                {

                    return Price;
                }
            }

            characterList ??= GameSession.GetSessionCrewCharacters(CharacterType.Both);

            float price = Price;

            // Adjust by campaign difficulty settings
            if (GameMain.GameSession?.Campaign is CampaignMode campaign)
            {
                price *= campaign.Settings.ShipyardPriceMultiplier;
            }

            if (characterList.Any())
            {
                if (location.Faction is { } faction && Faction.GetPlayerAffiliationStatus(faction) is FactionAffiliation.Positive)
                {
                    price *= 1f - characterList.Max(static c => c.GetStatValue(StatTypes.ShipyardBuyMultiplierAffiliated));
                }
                price *= 1f - characterList.Max(static c => c.GetStatValue(StatTypes.ShipyardBuyMultiplier));
            }

            return (int)price;
        }

        public static int GetDefaultTier(int price) => price > 20000 ? HighestTier : price > 10000 ? 2 : 1;

        public const int HighestTier = 3;
    }
}

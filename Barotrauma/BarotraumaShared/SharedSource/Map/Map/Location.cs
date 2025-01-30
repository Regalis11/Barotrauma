using Barotrauma.Abilities;
using Barotrauma.Extensions;
using Microsoft.Xna.Framework;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Xml.Linq;

namespace Barotrauma
{
    partial class Location
    {
        public class TakenItem
        {
            public readonly ushort OriginalID;
            public readonly ushort ModuleIndex;
            public readonly Identifier Identifier;
            public readonly int OriginalContainerIndex;

            public TakenItem(Identifier identifier, UInt16 originalID, int originalContainerIndex, ushort moduleIndex)
            {
                OriginalID = originalID;
                OriginalContainerIndex = originalContainerIndex;
                ModuleIndex = moduleIndex;
                Identifier = identifier;
            }

            public TakenItem(Item item)
            {
                System.Diagnostics.Debug.Assert(item.OriginalModuleIndex >= 0, "Trying to add a non-outpost item to a location's taken items");

                OriginalContainerIndex = item.OriginalContainerIndex;
                OriginalID = item.ID;
                ModuleIndex = (ushort) item.OriginalModuleIndex;
                Identifier = ((MapEntity)item).Prefab.Identifier;
            }

            public bool IsEqual(TakenItem obj)
            {
                return obj.OriginalID == OriginalID && obj.OriginalContainerIndex == OriginalContainerIndex && obj.ModuleIndex == ModuleIndex && obj.Identifier == Identifier;
            }

            public bool Matches(Item item)
            {
                if (item.OriginalContainerIndex != Entity.NullEntityID)
                {
                    return item.OriginalContainerIndex == OriginalContainerIndex && item.OriginalModuleIndex == ModuleIndex && ((MapEntity)item).Prefab.Identifier == Identifier;
                }
                else
                {
                    return item.ID == OriginalID && item.OriginalModuleIndex == ModuleIndex && ((MapEntity)item).Prefab.Identifier == Identifier;
                }
            }
        }

        public readonly List<LocationConnection> Connections = new List<LocationConnection>();

        public LocalizedString DisplayName { get; private set; }

        public Identifier NameIdentifier => nameIdentifier;

        private int nameFormatIndex;
        private Identifier nameIdentifier;

        public int NameFormatIndex => nameFormatIndex;

        /// <summary>
        /// For backwards compatibility: a non-localizable name from the old text files.
        /// </summary>
        private string rawName;

        private LocationType addInitialMissionsForType;

        public bool Discovered => GameMain.GameSession?.Map?.IsDiscovered(this) ?? false;

        public bool Visited => GameMain.GameSession?.Map?.IsVisited(this) ?? false;

        public readonly Dictionary<LocationTypeChange.Requirement, int> ProximityTimer = new Dictionary<LocationTypeChange.Requirement, int>();
        public (LocationTypeChange typeChange, int delay, MissionPrefab parentMission)? PendingLocationTypeChange;
        public int LocationTypeChangeCooldown;

        /// <summary>
        /// Is some mission blocking this location from changing its type, or have location type changes been forcibly disabled on the location?
        /// </summary>
        public bool LocationTypeChangesBlocked => DisallowLocationTypeChanges || availableMissions.Any(m => !m.Completed && m.Prefab.BlockLocationTypeChanges);

        public bool DisallowLocationTypeChanges;

        public Biome Biome { get; set; }

        public Vector2 MapPosition { get; private set; }

        public LocationType Type { get; private set; }

        public LocationType OriginalType { get; private set; }

        public LevelData LevelData { get; set; }

        public int PortraitId { get; private set; }

        public Faction Faction { get; set; }

        public Faction SecondaryFaction { get; set; }

        public Reputation Reputation => Faction?.Reputation;

        public bool IsFactionHostile => Faction?.Reputation.NormalizedValue < Reputation.HostileThreshold;

        public int TurnsInRadiation { get; set; }

        #region Store

        public class StoreInfo
        {
            public Identifier Identifier { get; }
            public Identifier MerchantFaction { get; private set; }
            public int Balance { get; set; }
            public List<PurchasedItem> Stock { get; } = new List<PurchasedItem>();
            public List<ItemPrefab> DailySpecials { get; } = new List<ItemPrefab>();
            public List<ItemPrefab> RequestedGoods { get; } = new List<ItemPrefab>();
            /// <summary>
            /// In percentages. Larger values make buying more expensive and selling less profitable, and vice versa.
            /// </summary>
            public int PriceModifier { get; set; }
            public Location Location { get; }

            /// <summary>
            /// The maximum effect positive reputation can have on store prices (e.g. 0.5 = 50% discount with max reputation).
            /// </summary>
            private float MaxReputationModifier => Location.StoreMaxReputationModifier;

            /// <summary>
            /// The maximum effect negative reputation can have on store prices (e.g. 0.5 = 50% price increase with minimum reputation).
            /// </summary>
            private float MinReputationModifier => Location.StoreMinReputationModifier;

            private StoreInfo(Location location)
            {
                Location = location;
            }

            /// <summary>
            /// Create new StoreInfo
            /// </summary>
            public StoreInfo(Location location, Identifier identifier) : this(location)
            {
                Identifier = identifier;
                Balance = location.StoreInitialBalance;
                Stock = CreateStock();
                GenerateSpecials();
                GeneratePriceModifier();
            }

            /// <summary>
            /// Load previously saved StoreInfo
            /// </summary>
            public StoreInfo(Location location, XElement storeElement) : this(location)
            {
                Identifier = storeElement.GetAttributeIdentifier("identifier", "");
                MerchantFaction = storeElement.GetAttributeIdentifier(nameof(MerchantFaction), "");
                Balance = storeElement.GetAttributeInt("balance", location.StoreInitialBalance);
                PriceModifier = storeElement.GetAttributeInt("pricemodifier", 0);
                // Backwards compatibility: before introducing support for multiple stores, this value was saved as a store element attribute
                if (storeElement.Attribute("stepssincespecialsupdated") != null)
                {
                    location.StepsSinceSpecialsUpdated = storeElement.GetAttributeInt("stepssincespecialsupdated", 0);
                }
                foreach (var stockElement in storeElement.GetChildElements("stock"))
                {
                    var identifier = stockElement.GetAttributeIdentifier("id", Identifier.Empty);
                    if (identifier.IsEmpty || ItemPrefab.FindByIdentifier(identifier) is not ItemPrefab prefab) { continue; }
                    int qty = stockElement.GetAttributeInt("qty", 0);
                    if (qty < 1) { continue; }
                    Stock.Add(new PurchasedItem(prefab, qty, buyer: null));
                }
                if (storeElement.GetChildElement("dailyspecials") is XElement specialsElement)
                {
                    var loadedDailySpecials = LoadStoreSpecials(specialsElement);
                    DailySpecials.AddRange(loadedDailySpecials);
                }
                if (storeElement.GetChildElement("requestedgoods") is XElement goodsElement)
                {
                    var loadedRequestedGoods = LoadStoreSpecials(goodsElement);
                    RequestedGoods.AddRange(loadedRequestedGoods);
                }

                static List<ItemPrefab> LoadStoreSpecials(XElement element)
                {
                    var specials = new List<ItemPrefab>();
                    foreach (var childElement in element.GetChildElements("item"))
                    {
                        var id = childElement.GetAttributeIdentifier("id", Identifier.Empty);
                        if (id.IsEmpty || ItemPrefab.FindByIdentifier(id) is not ItemPrefab prefab) { continue; }
                        specials.Add(prefab);
                    }
                    return specials;
                }
            }

            public static PurchasedItem CreateInitialStockItem(ItemPrefab itemPrefab, PriceInfo priceInfo)
            {
                int quantity = Rand.Range(priceInfo.MinAvailableAmount, priceInfo.MaxAvailableAmount + 1);
                return new PurchasedItem(itemPrefab, quantity, buyer: null);
            }

            public List<PurchasedItem> CreateStock()
            {
                var stock = new List<PurchasedItem>();
                foreach (var prefab in ItemPrefab.Prefabs)
                {
                    if (!prefab.CanBeBoughtFrom(this, out var priceInfo)) { continue; }
                    stock.Add(CreateInitialStockItem(prefab, priceInfo));
                }
                return stock;
            }

            public void AddStock(List<SoldItem> items)
            {
                if (items == null || items.None()) { return; }
                DebugConsole.NewMessage($"Adding items to stock for \"{Identifier}\" at \"{Location}\"", Color.Purple, debugOnly: true);
                foreach (var item in items)
                {
                    if (Stock.FirstOrDefault(i => i.ItemPrefab == item.ItemPrefab) is PurchasedItem stockItem)
                    {
                        stockItem.Quantity += 1;
                        DebugConsole.NewMessage($"Added 1x {item.ItemPrefab.Name}, new total: {stockItem.Quantity}", Color.Cyan, debugOnly: true);
                    }
                    else
                    {
                        DebugConsole.NewMessage($"{item.ItemPrefab.Name} not sold at location, can't add", Color.Cyan, debugOnly: true);
                    }
                }
            }

            public void RemoveStock(List<PurchasedItem> items)
            {
                if (items == null || items.None()) { return; }
                DebugConsole.NewMessage($"Removing items from stock for \"{Identifier}\" at \"{Location}\"", Color.Purple, debugOnly: true);
                foreach (PurchasedItem item in items)
                {
                    if (Stock.FirstOrDefault(i => i.ItemPrefab == item.ItemPrefab) is PurchasedItem stockItem)
                    {
                        stockItem.Quantity = Math.Max(stockItem.Quantity - item.Quantity, 0);
                        DebugConsole.NewMessage($"Removed {item.Quantity}x {item.ItemPrefab.Name}, new total: {stockItem.Quantity}", Color.Cyan, debugOnly: true);
                    }
                }
            }

            public void GenerateSpecials()
            {
                var availableStock = new Dictionary<ItemPrefab, float>();
                foreach (var stockItem in Stock)
                {
                    if (stockItem.Quantity < 1) { continue; }
                    float weight = 1.0f;
                    if (stockItem.ItemPrefab.GetPriceInfo(this) is PriceInfo priceInfo)
                    {
                        if (!priceInfo.CanBeSpecial) { continue; }
                        var baseQuantity = priceInfo.MinAvailableAmount;
                        weight += (float)(stockItem.Quantity - baseQuantity) / baseQuantity;
                        if (weight < 0.0f) { continue; }
                    }
                    availableStock.Add(stockItem.ItemPrefab, weight);
                }
                DailySpecials.Clear();
                int extraSpecialSalesCount = GetExtraSpecialSalesCount();
                for (int i = 0; i < Location.DailySpecialsCount + extraSpecialSalesCount; i++)
                {
                    if (availableStock.None()) { break; }
                    var item = ToolBox.SelectWeightedRandom(availableStock.Keys.ToList(), availableStock.Values.ToList(), Rand.RandSync.Unsynced);
                    if (item == null) { break; }
                    DailySpecials.Add(item);
                    availableStock.Remove(item);
                }
                RequestedGoods.Clear();
                for (int i = 0; i < Location.RequestedGoodsCount; i++)
                {
                    var item = ItemPrefab.Prefabs.GetRandom(p =>
                        p.CanBeSold && !RequestedGoods.Contains(p) &&
                        p.GetPriceInfo(this) is PriceInfo pi && pi.CanBeSpecial, Rand.RandSync.Unsynced);
                    if (item == null) { break; }
                    RequestedGoods.Add(item);
                }
                Location.StepsSinceSpecialsUpdated = 0;
            }

            public void GeneratePriceModifier()
            {
                PriceModifier = Rand.Range(-Location.StorePriceModifierRange, Location.StorePriceModifierRange + 1);
            }

            /// <param name="priceInfo">If null, item.GetPriceInfo() will be used to get it.</param>
            /// /// <param name="considerDailySpecials">If false, the price won't be affected by <see cref="DailySpecialPriceModifier"/></param>
            public int GetAdjustedItemBuyPrice(ItemPrefab item, PriceInfo priceInfo = null, bool considerDailySpecials = true)
            {
                priceInfo ??= item?.GetPriceInfo(this);
                if (priceInfo == null) { return 0; }
                float price = priceInfo.Price;
                // Adjust by random price modifier
                price = (100 + PriceModifier) / 100.0f * price;
                price *= priceInfo.BuyingPriceMultiplier;
                // Adjust by daily special status
                if (considerDailySpecials && DailySpecials.Contains(item))
                {
                    price = Location.DailySpecialPriceModifier * price;
                }
                // Adjust by current reputation
                price *= GetReputationModifier(true);

                // Adjust by campaign difficulty settings
                if (GameMain.GameSession?.Campaign is CampaignMode campaign)
                {
                    price *= campaign.Settings.ShopPriceMultiplier;
                }

                var characters = GameSession.GetSessionCrewCharacters(CharacterType.Both);
                if (characters.Any())
                {
                    var faction = GetMerchantOrLocationFactionIdentifier();
                    if (!faction.IsEmpty && GameMain.GameSession.Campaign.GetFactionAffiliation(faction) is FactionAffiliation.Positive)
                    {
                        price *= 1f - characters.Max(static c => c.GetStatValue(StatTypes.StoreBuyMultiplierAffiliated, includeSaved: false));
                        price *= 1f - characters.Max(static c => c.Info.GetSavedStatValue(StatTypes.StoreBuyMultiplierAffiliated, Tags.StatIdentifierTargetAll));
                        price *= 1f - characters.Max(c => item.Tags.Sum(tag => c.Info.GetSavedStatValue(StatTypes.StoreBuyMultiplierAffiliated, tag)));
                    }
                    price *= 1f - characters.Max(static c => c.GetStatValue(StatTypes.StoreBuyMultiplier, includeSaved: false));
                    price *= 1f - characters.Max(c => item.Tags.Sum(tag => c.Info.GetSavedStatValue(StatTypes.StoreBuyMultiplier, tag)));
                }
                // Price should never go below 1 mk
                return Math.Max((int)price, 1);
            }

            /// <param name="priceInfo">If null, item.GetPriceInfo() will be used to get it.</param>
            /// <param name="considerRequestedGoods">If false, the price won't be affected by <see cref="RequestGoodPriceModifier"/></param>
            public int GetAdjustedItemSellPrice(ItemPrefab item, PriceInfo priceInfo = null, bool considerRequestedGoods = true)
            {
                priceInfo ??= item?.GetPriceInfo(this);
                if (priceInfo == null) { return 0; }
                float price = Location.StoreSellPriceModifier * priceInfo.Price;
                // Adjust by random price modifier
                price = (100 - PriceModifier) / 100.0f * price;
                // Adjust by requested good status
                if (considerRequestedGoods && RequestedGoods.Contains(item))
                {
                    price = Location.RequestGoodPriceModifier * price;
                }
                // Adjust by location reputation
                price *= GetReputationModifier(false);

                var characters = GameSession.GetSessionCrewCharacters(CharacterType.Both);
                if (characters.Any())
                {
                    price *= 1f + characters.Max(static c => c.GetStatValue(StatTypes.StoreSellMultiplier, includeSaved: false));
                    price *= 1f + characters.Max(c => item.Tags.Sum(tag => c.Info.GetSavedStatValueWithAll(StatTypes.StoreSellMultiplier, tag)));
                }

                // Price should never go below 1 mk
                return Math.Max((int)price, 1);
            }

            public void SetMerchantFaction(Identifier factionIdentifier)
            {
                MerchantFaction = factionIdentifier;
            }

            public Identifier GetMerchantOrLocationFactionIdentifier()
            {
                return MerchantFaction.IfEmpty(Location.Faction?.Prefab.Identifier ?? Identifier.Empty);
            }

            public float GetReputationModifier(bool buying)
            {
                var factionIdentifier = GetMerchantOrLocationFactionIdentifier();
                var reputation = GameMain.GameSession.Campaign.GetFaction(factionIdentifier)?.Reputation;
                if (reputation == null) { return 1.0f; }
                if (buying)
                {
                    if (reputation.Value > 0.0f)
                    {
                        return MathHelper.Lerp(1.0f, 1.0f - MaxReputationModifier, reputation.Value / reputation.MaxReputation);
                    }
                    else
                    {
                        return MathHelper.Lerp(1.0f, 1.0f + MinReputationModifier, reputation.Value / reputation.MinReputation);
                    }
                }
                else
                {
                    if (reputation.Value > 0.0f)
                    {
                        return MathHelper.Lerp(1.0f, 1.0f + MaxReputationModifier, reputation.Value / reputation.MaxReputation);
                    }
                    else
                    {
                        return MathHelper.Lerp(1.0f, 1.0f - MinReputationModifier, reputation.Value / reputation.MinReputation);
                    }
                }
            }

            public override string ToString()
            {
                return Identifier.Value;
            }
        }

        public Dictionary<Identifier, StoreInfo> Stores { get; set; }

        private float StoreMaxReputationModifier => Type.StoreMaxReputationModifier;
        private float StoreMinReputationModifier => Type.StoreMinReputationModifier;
        private float StoreSellPriceModifier => Type.StoreSellPriceModifier;
        private float DailySpecialPriceModifier => Type.DailySpecialPriceModifier;
        private float RequestGoodPriceModifier => Type.RequestGoodPriceModifier;
        public int StoreInitialBalance => Type.StoreInitialBalance;
        private int StorePriceModifierRange => Type.StorePriceModifierRange;

        /// <summary>
        /// How many map progress steps it takes before the discounts should be updated.
        /// </summary>
        private const int SpecialsUpdateInterval = 3;
        public int DailySpecialsCount => Type.DailySpecialsCount;
        public int RequestedGoodsCount => Type.RequestedGoodsCount;
        private int StepsSinceSpecialsUpdated { get; set; }
        public HashSet<Identifier> StoreIdentifiers { get; } = new HashSet<Identifier>();

        #endregion

        private const float MechanicalMaxDiscountPercentage = 50.0f;
        private const float HealMaxDiscountPercentage = 10.0f;

        private readonly List<TakenItem> takenItems = new List<TakenItem>();
        public IEnumerable<TakenItem> TakenItems
        {
            get { return takenItems; }
        }

        private readonly HashSet<int> killedCharacterIdentifiers = new HashSet<int>();
        public IEnumerable<int> KilledCharacterIdentifiers
        {
            get { return killedCharacterIdentifiers; }
        }

        private readonly List<Mission> availableMissions = new List<Mission>();
        public IEnumerable<Mission> AvailableMissions
        {
            get 
            {
                availableMissions.RemoveAll(m => m.Completed || (m.Failed && !m.Prefab.AllowRetry));
                return availableMissions; 
            }
        }

        private readonly List<Mission> selectedMissions = new List<Mission>();
        public IEnumerable<Mission> SelectedMissions
        {
            get 
            {
                selectedMissions.RemoveAll(m => !availableMissions.Contains(m));
                return selectedMissions; 
            }
        }



        public void SelectMission(Mission mission)
        {
            if (!SelectedMissions.Contains(mission) && mission != null)
            {
                selectedMissions.Add(mission);
                selectedMissions.Sort((m1, m2) => availableMissions.IndexOf(m1).CompareTo(availableMissions.IndexOf(m2)));
            }
        }

        public void DeselectMission(Mission mission)
        {
            selectedMissions.Remove(mission);
        }


        public List<int> GetSelectedMissionIndices()
        {
            List<int> selectedMissionIndices = new List<int>();
            foreach (Mission mission in SelectedMissions)
            {
                if (availableMissions.Contains(mission))
                {
                    selectedMissionIndices.Add(availableMissions.IndexOf(mission));
                }
            }
            return selectedMissionIndices;
        }

        public void SetSelectedMissionIndices(IEnumerable<int> missionIndices)
        {
            selectedMissions.Clear();
            foreach (int missionIndex in missionIndices)
            {
                if (missionIndex < 0 || missionIndex >= availableMissions.Count)
                {
                    DebugConsole.ThrowError($"Failed to select a mission in location \"{DisplayName}\". Mission index out of bounds ({missionIndex}, available missions: {availableMissions.Count})");
                    break;
                }
                selectedMissions.Add(availableMissions[missionIndex]);
            }
        }

        private float priceMultiplier = 1.0f;
        public float PriceMultiplier
        {
            get { return priceMultiplier; }
            set { priceMultiplier = MathHelper.Clamp(value, 0.1f, 10.0f); }
        }
        
        private float mechanicalpriceMultiplier = 1.0f;
        public float MechanicalPriceMultiplier
        {
            get => mechanicalpriceMultiplier;
            set => mechanicalpriceMultiplier = MathHelper.Clamp(value, 0.1f, 10.0f);
        }

        public string LastTypeChangeMessage;

        public int TimeSinceLastTypeChange;

        public bool IsGateBetweenBiomes;

        private readonly struct LoadedMission
        {
            public readonly MissionPrefab MissionPrefab;
            public readonly int TimesAttempted;
            public readonly int OriginLocationIndex;
            public readonly int DestinationIndex;
            public readonly bool SelectedMission;

            public LoadedMission(XElement element)
            {
                var id = element.GetAttributeIdentifier("prefabid", Identifier.Empty);
                MissionPrefab = MissionPrefab.Prefabs.TryGet(id, out var prefab) ? prefab : null;                
                TimesAttempted = element.GetAttributeInt("timesattempted", 0);
                OriginLocationIndex = element.GetAttributeInt("origin", -1);
                DestinationIndex = element.GetAttributeInt("destinationindex", -1);
                SelectedMission = element.GetAttributeBool("selected", false);
            }
        }

        private List<LoadedMission> loadedMissions;

        public HireManager HireManager;
                
        public override string ToString()
        {
            return $"Location ({DisplayName ?? "null"})";
        }

        public Location(Vector2 mapPosition, int? zone, Random rand, bool requireOutpost = false, LocationType forceLocationType = null, IEnumerable<Location> existingLocations = null)
        {
            Type = OriginalType = forceLocationType ?? LocationType.Random(rand, zone, requireOutpost);
            CreateRandomName(Type, rand, existingLocations);
            MapPosition = mapPosition;
            PortraitId = ToolBox.StringToInt(nameIdentifier.Value);
            Connections = new List<LocationConnection>(); 
        }

        /// <summary>
        /// Create a location from save data
        /// </summary>
        public Location(CampaignMode campaign, XElement element)
        {
            Identifier locationTypeId = element.GetAttributeIdentifier("type", "");
            bool typeNotFound = GetTypeOrFallback(locationTypeId, out LocationType type);
            Type = type;

            Identifier originalLocationTypeId = element.GetAttributeIdentifier("originaltype", locationTypeId);
            GetTypeOrFallback(originalLocationTypeId, out LocationType originalType);
            OriginalType = originalType;

            nameIdentifier = element.GetAttributeIdentifier(nameof(nameIdentifier), "");
            if (nameIdentifier.IsEmpty)
            {
                //backwards compatibility
                DisplayName = element.GetAttributeString("name", "");
                rawName = element.GetAttributeString("rawname", element.GetAttributeString("basename", DisplayName.Value));
                nameIdentifier = rawName.ToIdentifier();
            }
            else
            {
                nameFormatIndex = element.GetAttributeInt(nameof(nameFormatIndex), 0); 
                DisplayName = GetName(Type, nameFormatIndex, nameIdentifier);
            }

            MapPosition = element.GetAttributeVector2("position", Vector2.Zero);

            PriceMultiplier = element.GetAttributeFloat("pricemultiplier", 1.0f);
            IsGateBetweenBiomes = element.GetAttributeBool("isgatebetweenbiomes", false);
            MechanicalPriceMultiplier = element.GetAttributeFloat("mechanicalpricemultipler", 1.0f);
            TurnsInRadiation = element.GetAttributeInt(nameof(TurnsInRadiation).ToLower(), 0);
            StepsSinceSpecialsUpdated = element.GetAttributeInt("stepssincespecialsupdated", 0);

            var factionIdentifier = element.GetAttributeIdentifier("faction", Identifier.Empty);
            if (!factionIdentifier.IsEmpty)
            {
                Faction = campaign.Factions.Find(f => f.Prefab.Identifier == factionIdentifier);
            }
            var secondaryFactionIdentifier = element.GetAttributeIdentifier("secondaryfaction", Identifier.Empty);
            if (!secondaryFactionIdentifier.IsEmpty)
            {
                SecondaryFaction = campaign.Factions.Find(f => f.Prefab.Identifier == secondaryFactionIdentifier);
            }
            Identifier biomeId = element.GetAttributeIdentifier("biome", Identifier.Empty);
            if (biomeId != Identifier.Empty)
            {
                if (Biome.Prefabs.TryGet(biomeId, out Biome biome))
                {
                    Biome = biome;
                }
                else
                {
                    DebugConsole.ThrowError($"Error while loading the campaign map: could not find a biome with the identifier \"{biomeId}\".");
                }
            }

            if (!typeNotFound)
            {
                for (int i = 0; i < Type.CanChangeTo.Count; i++)
                {
                    for (int j = 0; j < Type.CanChangeTo[i].Requirements.Count; j++)
                    {
                        ProximityTimer.Add(Type.CanChangeTo[i].Requirements[j], element.GetAttributeInt("proximitytimer" + i + "-" + j, 0));
                    }
                }

                LoadLocationTypeChange(element);
            }

            string[] takenItemStr = element.GetAttributeStringArray("takenitems", Array.Empty<string>());
            foreach (string takenItem in takenItemStr)
            {
                string[] takenItemSplit = takenItem.Split(';');
                if (takenItemSplit.Length != 4)
                {
                    DebugConsole.ThrowError($"Error in saved location: could not parse taken item data \"{takenItem}\"");
                    continue;
                }
                if (!ushort.TryParse(takenItemSplit[1], out ushort id))
                {
                    DebugConsole.ThrowError($"Error in saved location: could not parse taken item id \"{takenItemSplit[1]}\"");
                    continue;
                }
                if (!int.TryParse(takenItemSplit[2], out int containerIndex))
                {
                    DebugConsole.ThrowError($"Error in saved location: could not parse taken container index \"{takenItemSplit[2]}\"");
                    continue;
                }
                if (!ushort.TryParse(takenItemSplit[3], out ushort moduleIndex))
                {
                    DebugConsole.ThrowError($"Error in saved location: could not parse taken item module index \"{takenItemSplit[3]}\"");
                    continue;
                }
                takenItems.Add(new TakenItem(takenItemSplit[0].ToIdentifier(), id, containerIndex, moduleIndex));
            }

            killedCharacterIdentifiers = element.GetAttributeIntArray("killedcharacters", Array.Empty<int>()).ToHashSet();

            System.Diagnostics.Debug.Assert(Type != null, $"Could not find the location type \"{locationTypeId}\"!");
            Type ??= LocationType.Prefabs.First();

            LevelData = new LevelData(element.GetChildElement("Level"), clampDifficultyToBiome: true);

            PortraitId = ToolBox.StringToInt(!rawName.IsNullOrEmpty() ? rawName : nameIdentifier.Value);

            LoadStores(element);
            LoadMissions(element);

            bool GetTypeOrFallback(Identifier identifier, out LocationType type)
            {
                if (!LocationType.Prefabs.TryGet(identifier, out type))
                {
                    //turn lairs into abandoned outposts
                    if (identifier == "lair")
                    {
                        LocationType.Prefabs.TryGet("Abandoned".ToIdentifier(), out type);
                        addInitialMissionsForType = Type;
                    }
                    if (type == null)
                    {
                        DebugConsole.AddWarning($"Could not find location type \"{identifier}\". Using location type \"None\" instead.");
                        LocationType.Prefabs.TryGet("None".ToIdentifier(), out type);                
                        type ??= LocationType.Prefabs.First();                        
                    }
                    if (type != null)
                    {
                        element.SetAttributeValue("type", type.Identifier.ToString());
                    }
                    return false;
                }
                return true;
            }
        }

        public void LoadLocationTypeChange(XElement locationElement)
        {
            TimeSinceLastTypeChange = locationElement.GetAttributeInt("timesincelasttypechange", 0);
            LocationTypeChangeCooldown = locationElement.GetAttributeInt("locationtypechangecooldown", 0);
            foreach (var subElement in locationElement.Elements())
            {
                switch (subElement.Name.ToString())
                {
                    case "pendinglocationtypechange":
                        int timer = subElement.GetAttributeInt("timer", 0);
                        if (subElement.Attribute("index") != null)
                        {
                            int locationTypeChangeIndex = subElement.GetAttributeInt("index", 0);
                            if (locationTypeChangeIndex < 0 || locationTypeChangeIndex >= Type.CanChangeTo.Count)
                            {
                                DebugConsole.AddWarning($"Failed to activate a location type change in the location \"{DisplayName}\". Location index out of bounds ({locationTypeChangeIndex}).");
                                continue;
                            }
                            PendingLocationTypeChange = (Type.CanChangeTo[locationTypeChangeIndex], timer, null);
                        }
                        else
                        {
                            Identifier missionIdentifier = subElement.GetAttributeIdentifier("missionidentifier", "");
                            var mission = MissionPrefab.Prefabs[missionIdentifier];
                            if (mission == null)
                            {
                                DebugConsole.AddWarning($"Failed to activate a location type change from the mission \"{missionIdentifier}\" in location \"{DisplayName}\". Matching mission not found.");
                                continue;
                            }
                            PendingLocationTypeChange = (mission.LocationTypeChangeOnCompleted, timer, mission);
                        }
                        break;
                }
            }
        }

        public void LoadMissions(XElement locationElement)
        {
            if (locationElement.GetChildElement("missions") is XElement missionsElement)
            {
                loadedMissions = new List<LoadedMission>();
                foreach (XElement childElement in missionsElement.GetChildElements("mission"))
                {
                    var loadedMission = new LoadedMission(childElement);
                    if (loadedMission.MissionPrefab != null)
                    {
                        loadedMissions.Add(loadedMission);
                    }
                }
            }
        }

        public static Location CreateRandom(Vector2 position, int? zone, Random rand, bool requireOutpost, LocationType forceLocationType = null, IEnumerable<Location> existingLocations = null)
        {
            return new Location(position, zone, rand, requireOutpost, forceLocationType, existingLocations);
        }

        public void ChangeType(CampaignMode campaign, LocationType newType, bool createStores = true)
        {
            if (newType == Type) { return; }

            if (newType == null)
            {
                DebugConsole.ThrowError($"Failed to change the type of the location \"{DisplayName}\" to null.\n" + Environment.StackTrace.CleanupStackTrace());
                return;
            }

            Type = newType;
            if (rawName != null)
            {
                DebugConsole.Log($"Location {rawName} changed it's type from {Type} to {newType}");
                DisplayName =
                    Type.NameFormats == null || !Type.NameFormats.Any() ?
                    rawName :
                    Type.NameFormats[nameFormatIndex % Type.NameFormats.Count].Replace("[name]", rawName);
            }
            else
            {
                DebugConsole.Log($"Location {DisplayName.Value} changed it's type from {Type} to {newType}");
                DisplayName =
                    Type.NameFormats == null || !Type.NameFormats.Any() ?
                    TextManager.Get(nameIdentifier) :
                    Type.NameFormats[nameFormatIndex % Type.NameFormats.Count].Replace("[name]", TextManager.Get(nameIdentifier).Value);
            }

            TryAssignFactionBasedOnLocationType(campaign);
            if (Type.HasOutpost && Type.OutpostTeam == CharacterTeamType.FriendlyNPC)
            {
                if (Type.Faction == Identifier.Empty) { Faction ??= campaign.GetRandomFaction(Rand.RandSync.Unsynced); }
                if (Type.SecondaryFaction == Identifier.Empty) { SecondaryFaction ??= campaign.GetRandomSecondaryFaction(Rand.RandSync.Unsynced); }
            }
            else
            {
                if (Type.Faction == Identifier.Empty) { Faction = null; }
                if (Type.SecondaryFaction == Identifier.Empty) { SecondaryFaction = null; }
            }

            UnlockInitialMissions(Rand.RandSync.Unsynced);

            if (createStores)
            {
                CreateStores(force: true);
            }
        }

        public void TryAssignFactionBasedOnLocationType(CampaignMode campaign)
        {
            if (campaign == null) { return; }
            if (Type.Faction != Identifier.Empty)
            {
                Faction = Type.Faction == "None" ? null : TryFindFaction(Type.Faction);
            }
            if (Type.SecondaryFaction != Identifier.Empty)
            {
                SecondaryFaction = Type.SecondaryFaction == "None" ? null : TryFindFaction(Type.SecondaryFaction);
            }

            Faction TryFindFaction(Identifier identifier)
            {
                var faction = campaign.GetFaction(identifier);
                if (faction == null)
                {
                    DebugConsole.ThrowError($"Error in location type \"{Type.Identifier}\": failed to find a faction with the identifier \"{identifier}\".",
                        contentPackage: Type.ContentPackage);
                }
                return faction;
            }
        }

        public void UnlockInitialMissions(Rand.RandSync randSync = Rand.RandSync.ServerAndClient)
        {
            if (Type.MissionIdentifiers.Any())
            {
                UnlockMissionByIdentifier(Type.MissionIdentifiers.GetRandom(randSync), invokingContentPackage: Type.ContentPackage);
            }
            if (Type.MissionTags.Any())
            {
                UnlockMissionByTag(Type.MissionTags.GetRandom(randSync), invokingContentPackage: Type.ContentPackage);
            }
        }

        public void UnlockMission(MissionPrefab missionPrefab, LocationConnection connection)
        {
            if (AvailableMissions.Any(m => m.Prefab == missionPrefab)) { return; }
            if (AvailableMissions.Any(m => !m.Prefab.AllowOtherMissionsInLevel)) { return; }
            AddMission(InstantiateMission(missionPrefab, connection));
        }

        public void UnlockMission(MissionPrefab missionPrefab)
        {
            if (AvailableMissions.Any(m => m.Prefab == missionPrefab)) { return; }
            if (AvailableMissions.Any(m => !m.Prefab.AllowOtherMissionsInLevel)) { return; }
            AddMission(InstantiateMission(missionPrefab));
        }

        public Mission UnlockMissionByIdentifier(Identifier identifier, ContentPackage invokingContentPackage = null)
        {
            if (AvailableMissions.Any(m => m.Prefab.Identifier == identifier)) { return null; }
            if (AvailableMissions.Any(m => !m.Prefab.AllowOtherMissionsInLevel)) { return null; }

            var missionPrefab = MissionPrefab.Prefabs.Find(mp => mp.Identifier == identifier);
            if (missionPrefab == null)
            {
                DebugConsole.ThrowError($"Failed to unlock a mission with the identifier \"{identifier}\": matching mission not found.", 
                    contentPackage: invokingContentPackage);
            }
            else
            {
                var mission = InstantiateMission(missionPrefab, out LocationConnection connection);
                //don't allow duplicate missions in the same connection
                if (AvailableMissions.Any(m => m.Prefab == missionPrefab && m.Locations.Contains(mission.Locations[0]) && m.Locations.Contains(mission.Locations[1])))
                {
                    return null;
                }
                AddMission(mission);
                DebugConsole.NewMessage($"Unlocked a mission by \"{identifier}\".", debugOnly: true);
                return mission;
            }
            return null;
        }

        public Mission UnlockMissionByTag(Identifier tag, Random random = null, ContentPackage invokingContentPackage = null)
        {
            if (AvailableMissions.Any(m => !m.Prefab.AllowOtherMissionsInLevel)) { return null; }
            var matchingMissions = MissionPrefab.Prefabs.Where(mp => mp.Tags.Contains(tag));
            if (matchingMissions.None())
            {
                DebugConsole.ThrowError($"Failed to unlock a mission with the tag \"{tag}\": no matching missions found.", contentPackage: invokingContentPackage);
            }
            else
            {
                var unusedMissions = matchingMissions.Where(m => availableMissions.None(mission => mission.Prefab == m));
                if (unusedMissions.Any())
                {
                    var suitableMissions = unusedMissions.Where(m => Connections.Any(c => m.IsAllowed(this, c.OtherLocation(this)) || m.IsAllowed(this, this)));
                    if (suitableMissions.None())
                    {
                        suitableMissions = unusedMissions;
                    }
                    var filteredMissions = suitableMissions.Where(m => LevelData.Difficulty >= m.MinLevelDifficulty && LevelData.Difficulty <= m.MaxLevelDifficulty);
                    if (filteredMissions.None())
                    {
                        DebugConsole.AddWarning($"No suitable mission matching the level difficulty {LevelData.Difficulty} found with the tag \"{tag}\". Ignoring the restriction.", 
                            contentPackage: invokingContentPackage);
                    }
                    else
                    {
                        suitableMissions = filteredMissions;
                    }
                    MissionPrefab missionPrefab = 
                        random != null ? 
                        ToolBox.SelectWeightedRandom(suitableMissions, m => m.Commonness, random) :
                        ToolBox.SelectWeightedRandom(suitableMissions, m => m.Commonness, Rand.RandSync.Unsynced);

                    var mission = InstantiateMission(missionPrefab, out LocationConnection connection);
                    //don't allow duplicate missions in the same connection
                    if (AvailableMissions.Any(m => m.Prefab == missionPrefab && m.Locations.Contains(mission.Locations[0]) && m.Locations.Contains(mission.Locations[1])))
                    {
                        return null;
                    }
                    AddMission(mission);
                    DebugConsole.NewMessage($"Unlocked a random mission by \"{tag}\": {mission.Prefab.Identifier} (difficulty level: {LevelData.Difficulty})", debugOnly: true);
                    return mission;
                }
                else
                {
                    DebugConsole.AddWarning($"Failed to unlock a mission with the tag \"{tag}\": all available missions have already been unlocked.", 
                        contentPackage: invokingContentPackage);
                }
            }

            return null;
        }

        private void AddMission(Mission mission)
        {
            if (!mission.Prefab.AllowOtherMissionsInLevel)
            {
                availableMissions.Clear();
            }
            availableMissions.Add(mission);
#if CLIENT
            GameMain.GameSession?.Campaign?.CampaignUI?.RefreshLocationInfo();
#else
            (GameMain.GameSession?.Campaign as MultiPlayerCampaign)?.IncrementLastUpdateIdForFlag(MultiPlayerCampaign.NetFlags.MapAndMissions);
#endif
        }

        private Mission InstantiateMission(MissionPrefab prefab, out LocationConnection connection)
        {
            if (prefab.IsAllowed(this, this))
            {
                connection = null;
                return InstantiateMission(prefab);
            }

            var suitableConnections = Connections.Where(c => prefab.IsAllowed(this, c.OtherLocation(this)));
            if (suitableConnections.None())
            {
                suitableConnections = Connections.ToList();
            }
            //prefer connections that haven't been passed through, and connections with fewer available missions
            connection = ToolBox.SelectWeightedRandom(
                suitableConnections.ToList(),
                suitableConnections.Select(c => GetConnectionWeight(this, c)).ToList(),
                Rand.RandSync.Unsynced);    
            
            static float GetConnectionWeight(Location location, LocationConnection c)
            {
                Location destination = c.OtherLocation(location);
                if (destination == null) { return 0; }
                float minWeight = 0.0001f;
                float lowWeight = 0.2f;
                float normalWeight = 1.0f;
                float maxWeight = 2.0f;
                float weight = c.Passed ? lowWeight : normalWeight;
                if (location.Biome.AllowedZones.Contains(1))
                {
                    // In the first biome, give a stronger preference for locations that are farther to the right)
                    float diff = destination.MapPosition.X - location.MapPosition.X;
                    if (diff < 0)
                    {
                        weight *= 0.1f;
                    }
                    else
                    {
                        float maxRelevantDiff = 300;
                        weight = MathHelper.Lerp(weight, maxWeight, MathUtils.InverseLerp(0, maxRelevantDiff, diff));
                    }
                }
                else if (destination.MapPosition.X > location.MapPosition.X)
                {
                    weight *= 2.0f;
                }
                int missionCount = location.availableMissions.Count(m => m.Locations.Contains(destination));
                if (missionCount > 0) 
                { 
                    weight /= missionCount * 2;
                }
                if (destination.IsRadiated())
                {
                    weight *= 0.001f;
                }
                return MathHelper.Clamp(weight, minWeight, maxWeight);
            }

            return InstantiateMission(prefab, connection);
        }

        private Mission InstantiateMission(MissionPrefab prefab, LocationConnection connection)
        {
            Location destination = connection.OtherLocation(this);
            var mission = prefab.Instantiate(new Location[] { this, destination }, Submarine.MainSub);
            mission.AdjustLevelData(connection.LevelData);
            return mission;
        }

        private Mission InstantiateMission(MissionPrefab prefab)
        {
            var mission = prefab.Instantiate(new Location[] { this, this }, Submarine.MainSub);
            mission.AdjustLevelData(LevelData);
            return mission;
        }

        public void InstantiateLoadedMissions(Map map)
        {
            availableMissions.Clear();
            selectedMissions.Clear();
            if (loadedMissions != null && loadedMissions.Any()) 
            { 
                foreach (LoadedMission loadedMission in loadedMissions)
                {
                    Location destination;
                    if (loadedMission.DestinationIndex >= 0 && loadedMission.DestinationIndex < map.Locations.Count)
                    {
                        destination = map.Locations[loadedMission.DestinationIndex];
                    }
                    else
                    {
                        destination = Connections.First().OtherLocation(this);
                    }
                    var mission = loadedMission.MissionPrefab.Instantiate(new Location[] { this, destination }, Submarine.MainSub);
                    if (loadedMission.OriginLocationIndex >= 0 && loadedMission.OriginLocationIndex < map.Locations.Count)
                    {
                        mission.OriginLocation = map.Locations[loadedMission.OriginLocationIndex];
                    }
                    mission.TimesAttempted = loadedMission.TimesAttempted;
                    availableMissions.Add(mission);
                    if (loadedMission.SelectedMission) { selectedMissions.Add(mission); }
                }
                loadedMissions = null;
            }
            if (addInitialMissionsForType != null)
            {
                if (addInitialMissionsForType.MissionIdentifiers.Any())
                {
                    UnlockMissionByIdentifier(addInitialMissionsForType.MissionIdentifiers.GetRandomUnsynced(), invokingContentPackage: Type.ContentPackage);
                }
                if (addInitialMissionsForType.MissionTags.Any())
                {
                    UnlockMissionByTag(addInitialMissionsForType.MissionTags.GetRandomUnsynced(), invokingContentPackage: Type.ContentPackage);
                }
                addInitialMissionsForType = null;
            }
        }

        /// <summary>
        /// Removes all unlocked missions from the location
        /// </summary>
        public void ClearMissions()
        {
            availableMissions.Clear();
            selectedMissions.Clear();
        }

        public bool HasOutpost()
        {
            if (!Type.HasOutpost) { return false; }

            return !IsCriticallyRadiated();
        }

        public bool IsCriticallyRadiated()
        {
            if (GameMain.GameSession?.Map?.Radiation != null)
            {
                return TurnsInRadiation > GameMain.GameSession.Map.Radiation.Params.CriticalRadiationThreshold;
            }

            return false;
        }

        public LocationType GetLocationType()
        {
            if (IsCriticallyRadiated() && !Type.ReplaceInRadiation.IsEmpty)
            {
                if (LocationType.Prefabs.TryGet(Type.ReplaceInRadiation, out LocationType newLocationType))
                {
                    return newLocationType;
                }
                else
                {
                    DebugConsole.ThrowError($"Error when trying to get a new location type for an irradiated location - location type \"{newLocationType}\" not found.");
                }
            }
            return Type;
        }

        public IEnumerable<Mission> GetMissionsInConnection(LocationConnection connection)
        {
            System.Diagnostics.Debug.Assert(Connections.Contains(connection));
            return AvailableMissions.Where(m => m.Locations[1] == connection.OtherLocation(this));
        }
        
        public void RemoveHireableCharacter(CharacterInfo character)
        {
            if (!Type.HasHireableCharacters)
            {
                DebugConsole.ThrowErrorLocalized("Cannot hire a character from location \"" + DisplayName + "\" - the location has no hireable characters.\n" + Environment.StackTrace.CleanupStackTrace());
                return;
            }
            if (HireManager == null)
            {
                DebugConsole.ThrowErrorLocalized("Cannot hire a character from location \"" + DisplayName + "\" - hire manager has not been instantiated.\n" + Environment.StackTrace.CleanupStackTrace());
                return;
            }

            HireManager.RemoveCharacter(character);
        }

        public IEnumerable<CharacterInfo> GetHireableCharacters()
        {
            if (!Type.HasHireableCharacters)
            {
                return Enumerable.Empty<CharacterInfo>();
            }

            HireManager ??= new HireManager();

            if (!HireManager.AvailableCharacters.Any())
            {
                HireManager.GenerateCharacters(location: this, amount: HireManager.MaxAvailableCharacters);
            }
            return HireManager.AvailableCharacters;
        }

        public void ForceHireableCharacters(IEnumerable<CharacterInfo> hireableCharacters)
        {
            HireManager ??= new HireManager();
            HireManager.AvailableCharacters = hireableCharacters.ToList();
        }

        private void CreateRandomName(LocationType type, Random rand, IEnumerable<Location> existingLocations)
        {
            if (!type.ForceLocationName.IsEmpty)
            {
                nameIdentifier = type.ForceLocationName;
                DisplayName = TextManager.Get(nameIdentifier).Fallback(nameIdentifier.Value);
                return;
            }
            nameIdentifier = type.GetRandomNameId(rand, existingLocations);
            if (nameIdentifier.IsEmpty)
            {
                //backwards compatibility
                rawName = type.GetRandomRawName(rand, existingLocations);
                if (rawName.IsNullOrEmpty())
                {
                    DebugConsole.ThrowError($"Failed to generate a name for a location of the type {type.Identifier}. No names found in localization files or the .txt files.");
                    rawName = "none";
                }
                nameIdentifier = rawName.ToIdentifier();
                if (type.NameFormats == null || !type.NameFormats.Any())
                {
                    DisplayName = rawName;
                }
                else
                {
                    nameFormatIndex = rand.Next() % type.NameFormats.Count;
                    DisplayName = type.NameFormats[nameFormatIndex].Replace("[name]", rawName);
                }
            }
            else
            {
                if (type.NameFormats == null || !type.NameFormats.Any())
                {
                    DisplayName = TextManager.Get(nameIdentifier).Fallback(nameIdentifier.Value);
                    return;
                }
                nameFormatIndex = rand.Next() % type.NameFormats.Count;
                DisplayName = GetName(Type, nameFormatIndex, nameIdentifier);
            }
        }

        public static LocalizedString GetName(Identifier locationTypeIdentifier, int nameFormatIndex, Identifier nameId)
        {
            if (LocationType.Prefabs.TryGet(locationTypeIdentifier, out LocationType locationType))
            {
                return GetName(locationType, nameFormatIndex, nameId);
            }
            else
            {
                DebugConsole.ThrowError($"Could not find the location type {locationTypeIdentifier}.\n" + Environment.StackTrace.CleanUpPath());
                return new RawLString(nameId.Value);
            }
        }

        public static LocalizedString GetName(LocationType type, int nameFormatIndex, Identifier nameId)
        {
            if (type?.NameFormats == null || !type.NameFormats.Any() || nameFormatIndex < 0)
            {
                return TextManager.Get(nameId);
            }
            return type.NameFormats[nameFormatIndex % type.NameFormats.Count].Replace("[name]", TextManager.Get(nameId).Value);
        }

        public void ForceName(Identifier nameId)
        {
            rawName = string.Empty;
            nameIdentifier = nameId;
            DisplayName = TextManager.Get(nameId).Fallback(nameId.Value);
        }

        public void LoadStores(XElement locationElement)
        {
            UpdateStoreIdentifiers();
            Stores?.Clear();
            foreach (var storeElement in locationElement.GetChildElements("store"))
            {
                Stores ??= new Dictionary<Identifier, StoreInfo>();
                var identifier = storeElement.GetAttributeIdentifier("identifier", "");
                if (identifier.IsEmpty)
                {
                    // Previously saved store data (with no identifier) is discarded and new store data will be created
                    continue;
                }
                if (StoreIdentifiers.Contains(identifier))
                {
                    if (!Stores.ContainsKey(identifier))
                    {
                        Stores.Add(identifier, new StoreInfo(this, storeElement));
                    }
                    else
                    {
                        string msg = $"Error loading store info for \"{identifier}\" at location {DisplayName} of type \"{Type.Identifier}\": duplicate identifier.";
                        DebugConsole.ThrowError(msg);
                        GameAnalyticsManager.AddErrorEventOnce("Location.LoadStore:DuplicateStoreInfo", GameAnalyticsManager.ErrorSeverity.Error, msg);
                        continue;
                    }
                }
                else
                {
                    string msg = $"Error loading store info for \"{identifier}\" at location {DisplayName} of type \"{Type.Identifier}\": location shouldn't contain a store with this identifier.";
                    DebugConsole.ThrowError(msg);
                    GameAnalyticsManager.AddErrorEventOnce("Location.LoadStore:IncorrectStoreIdentifier", GameAnalyticsManager.ErrorSeverity.Error, msg);
                    continue;
                }
            }
            // Backwards compatibility: create new stores for any identifiers not present in the save data
            foreach (var id in StoreIdentifiers)
            {
                AddNewStore(id);
            }
        }

        public bool IsRadiated() => GameMain.GameSession?.Map?.Radiation != null && GameMain.GameSession.Map.Radiation.Enabled && GameMain.GameSession.Map.Radiation.Contains(this);

        /// <summary>
        /// Mark the items that have been taken from the outpost to prevent them from spawning when re-entering the outpost
        /// </summary>
        public void RegisterTakenItems(IEnumerable<Item> items)
        {
            foreach (Item item in items)
            {
                if (takenItems.Any(it => it.Matches(item) && it.OriginalID == item.ID)) { continue; }
                if (item.IsSalvageMissionItem) { continue; }
                if (item.OriginalModuleIndex < 0)
                {
                    DebugConsole.ThrowError("Tried to register a non-outpost item as being taken from the outpost.");
                    continue;
                }
                takenItems.Add(new TakenItem(item));
            }
        }

        /// <summary>
        /// Mark the characters who have been killed to prevent them from spawning when re-entering the outpost
        /// </summary>
        public void RegisterKilledCharacters(IEnumerable<Character> characters)
        {
            foreach (Character character in characters)
            {
                if (character?.Info == null) { continue; }
                killedCharacterIdentifiers.Add(character.Info.GetIdentifier());
            }
        }

        public void RemoveTakenItems()
        {
            foreach (TakenItem takenItem in takenItems)
            {
                Item item = Item.ItemList.Find(it => takenItem.Matches(it));
                item?.Remove();
            }
        }

        public int GetAdjustedMechanicalCost(int cost)
        {
            float discount = 0.0f;
            if (Reputation != null)
            {
                discount = Reputation.Value / Reputation.MaxReputation * (MechanicalMaxDiscountPercentage / 100.0f);
            }
            return (int)Math.Ceiling((1.0f - discount) * cost * MechanicalPriceMultiplier);
        }

        public int GetAdjustedHealCost(int cost)
        {
            float discount = 0.0f;
            if (Reputation != null)
            {
                discount = Reputation.Value / Reputation.MaxReputation * (HealMaxDiscountPercentage / 100.0f);
            }           
            return (int) Math.Ceiling((1.0f - discount) * cost * PriceMultiplier);
        }

        public StoreInfo GetStore(Identifier identifier)
        {
            if (Stores != null && Stores.TryGetValue(identifier, out var store))
            {
                return store;
            }
            return null;
        }

        /// <param name="force">If true, the stores will be recreated if they already exists.</param>
        public void CreateStores(bool force = false)
        {
            // In multiplayer, stores should be created by the server and loaded from save data by clients
            if (GameMain.NetworkMember != null && GameMain.NetworkMember.IsClient) { return; }
            if (!force && Stores != null) { return; }
            UpdateStoreIdentifiers();
            if (Stores != null)
            {
                // Remove any stores with no corresponding merchants at the location
                foreach (var storeIdentifier in Stores.Keys)
                {
                    if (!StoreIdentifiers.Contains(storeIdentifier))
                    {
                        Stores.Remove(storeIdentifier);
                    }
                }
                foreach (var identifier in StoreIdentifiers)
                {
                    if (Stores.TryGetValue(identifier, out var store))
                    {
                        store.Balance = Math.Max(store.Balance, StoreInitialBalance);
                        var newStock = store.CreateStock();
                        foreach (var oldStockItem in store.Stock)
                        {
                            if (newStock.Find(i => i.ItemPrefab == oldStockItem.ItemPrefab) is { } newStockItem)
                            {
                                if (oldStockItem.Quantity > newStockItem.Quantity)
                                {
                                    newStockItem.Quantity = oldStockItem.Quantity;
                                }
                            }
                        }
                        store.Stock.Clear();
                        store.Stock.AddRange(newStock);
                        store.GenerateSpecials();
                        store.GeneratePriceModifier();
                    }
                    else
                    {
                        AddNewStore(identifier);
                    }
                }
            }
            else
            {
                foreach (var identifier in StoreIdentifiers)
                {
                    AddNewStore(identifier);
                }
            }
        }

        public void UpdateStores()
        {
            // In multiplayer, stores should be updated by the server and loaded from save data by clients
            if (GameMain.NetworkMember is { IsClient: true }) { return; }
            if (Stores == null)
            {
                CreateStores();
                return;
            }
            var storesToRemove = new HashSet<Identifier>();
            foreach (var store in Stores.Values)
            {
                if (!StoreIdentifiers.Contains(store.Identifier))
                {
                    storesToRemove.Add(store.Identifier);
                    continue;
                }
                if (store.Balance < StoreInitialBalance)
                {
                    store.Balance = Math.Min(store.Balance + (int)(StoreInitialBalance / 10.0f), StoreInitialBalance);
                }
                var stock = new List<PurchasedItem>(store.Stock);
                var stockToRemove = new List<PurchasedItem>();

                foreach (var itemPrefab in ItemPrefab.Prefabs)
                {
                    var existingStock = stock.FirstOrDefault(s => s.ItemPrefab == itemPrefab);
                    if (itemPrefab.CanBeBoughtFrom(store, out PriceInfo priceInfo))
                    {
                        if (existingStock == null)
                        {
                            //can be bought from the location, but not in stock - some new item added by an update or mod?
                            stock.Add(StoreInfo.CreateInitialStockItem(itemPrefab, priceInfo));
                        }
                        else
                        {
                            existingStock.Quantity =
                                Math.Min(
                                    existingStock.Quantity + 1, 
                                    priceInfo.MaxAvailableAmount);
                        }
                    }
                    else if (existingStock != null)
                    {
                        stockToRemove.Add(existingStock);
                    }
                }

                stockToRemove.ForEach(i => stock.Remove(i));
                store.Stock.Clear();
                store.Stock.AddRange(stock);
                store.GeneratePriceModifier();
            }

            StepsSinceSpecialsUpdated++;
            foreach (var identifier in storesToRemove)
            {
                Stores.Remove(identifier);
            }
            foreach (var identifier in StoreIdentifiers)
            {
                AddNewStore(identifier);
            }
        }

        public void UpdateSpecials()
        {
            if (GameMain.NetworkMember is { IsClient: true } || Stores is null) { return; }

            int extraSpecialSalesCount = GetExtraSpecialSalesCount();

            foreach (StoreInfo store in Stores.Values)
            {
                if (StepsSinceSpecialsUpdated < SpecialsUpdateInterval && store.DailySpecials.Count == DailySpecialsCount + extraSpecialSalesCount) { continue; }

                store.GenerateSpecials();
            }
        }

        private void UpdateStoreIdentifiers()
        {
            StoreIdentifiers.Clear();
            foreach (var outpostParam in OutpostGenerationParams.OutpostParams)
            {
                if (!outpostParam.AllowedLocationTypes.Contains(Type.Identifier)) { continue; }
                foreach (var identifier in outpostParam.GetStoreIdentifiers())
                {
                    StoreIdentifiers.Add(identifier);
                }
            }
        }

        private void AddNewStore(Identifier identifier)
        {
            Stores ??= new Dictionary<Identifier, StoreInfo>();
            if (Stores.ContainsKey(identifier)) { return; }
            var newStore = new StoreInfo(this, identifier);
            Stores.Add(identifier, newStore);
        }

        public void AddStock(Dictionary<Identifier, List<SoldItem>> items)
        {
            if (items == null) { return; }
            foreach (var storeItems in items)
            {
                if (GetStore(storeItems.Key) is { } store)
                {
                    store.AddStock(storeItems.Value);
                }
            }
        }

        public void RemoveStock(Dictionary<Identifier, List<PurchasedItem>> items)
        {
            if (items == null) { return; }
            foreach (var storeItems in items)
            {
                if (GetStore(storeItems.Key) is { } store)
                {
                    store.RemoveStock(storeItems.Value);
                }
            }
        }

        public static int GetExtraSpecialSalesCount()
        {
            var characters = GameSession.GetSessionCrewCharacters(CharacterType.Both);
            if (!characters.Any()) { return 0; }
            return characters.Max(static c => (int)c.GetStatValue(StatTypes.ExtraSpecialSalesCount));
        }

        public bool CanHaveSubsForSale()
        {
            return HasOutpost() && CanHaveCampaignInteraction(CampaignMode.InteractionType.PurchaseSub);
        }

        public int HighestSubmarineTierAvailable(SubmarineClass submarineClass = null)
        {
            if (CanHaveSubsForSale())
            {
                return Biome?.HighestSubmarineTierAvailable(submarineClass, Type.Identifier) ?? SubmarineInfo.HighestTier;
            }
            return 0;
        }

        public bool IsSubmarineAvailable(SubmarineInfo info)
        {
            return Biome?.IsSubmarineAvailable(info, Type.Identifier) ?? true;
        }

        private  bool CanHaveCampaignInteraction(CampaignMode.InteractionType interactionType)
        {
            return LevelData != null &&
                LevelData.OutpostGenerationParamsExist &&
                LevelData.GetSuitableOutpostGenerationParams(this, LevelData).Any(p => p.CanHaveCampaignInteraction(interactionType));
        }

        public void Reset(CampaignMode campaign)
        {
            if (Type != OriginalType && !DisallowLocationTypeChanges)
            {
                ChangeType(campaign, OriginalType);
                PendingLocationTypeChange = null;
            }
            CreateStores(force: true);
            ClearMissions();
            LevelData?.EventHistory?.Clear();
            UnlockInitialMissions();
        }

        public XElement Save(Map map, XElement parentElement)
        {
            var locationElement = new XElement("location",
                new XAttribute("type", Type.Identifier),
                new XAttribute("originaltype", (Type ?? OriginalType).Identifier),
                /*not used currently (we load the nameIdentifier instead), 
                 * but could make sense to include still for backwards compatibility reasons*/
                new XAttribute("name", DisplayName),
                new XAttribute("biome", Biome?.Identifier.Value ?? string.Empty),
                new XAttribute("position", XMLExtensions.Vector2ToString(MapPosition)),
                new XAttribute("pricemultiplier", PriceMultiplier),
                new XAttribute("isgatebetweenbiomes", IsGateBetweenBiomes),
                new XAttribute("mechanicalpricemultipler", MechanicalPriceMultiplier),
                new XAttribute("timesincelasttypechange", TimeSinceLastTypeChange),
                new XAttribute(nameof(TurnsInRadiation).ToLower(), TurnsInRadiation),
                new XAttribute("stepssincespecialsupdated", StepsSinceSpecialsUpdated));

            if (!rawName.IsNullOrEmpty())
            {
                locationElement.Add(new XAttribute(nameof(rawName), rawName));
            }
            else
            {
                locationElement.Add(new XAttribute(nameof(nameIdentifier), nameIdentifier));
                locationElement.Add(new XAttribute(nameof(nameFormatIndex), nameFormatIndex));
            }

            if (Faction != null)
            {
                locationElement.Add(new XAttribute("faction", Faction.Prefab.Identifier));
            }
            if (SecondaryFaction != null)
            {
                locationElement.Add(new XAttribute("secondaryfaction", SecondaryFaction.Prefab.Identifier));
            }

            LevelData.Save(locationElement);

            for (int i = 0; i < Type.CanChangeTo.Count; i++)
            {
                for (int j = 0; j < Type.CanChangeTo[i].Requirements.Count; j++)
                {
                    if (ProximityTimer.ContainsKey(Type.CanChangeTo[i].Requirements[j]))
                    {
                        locationElement.Add(new XAttribute("proximitytimer" + i + "-" + j, ProximityTimer[Type.CanChangeTo[i].Requirements[j]]));
                    }
                }
            }

            if (PendingLocationTypeChange.HasValue)
            {
                var changeElement = new XElement("pendinglocationtypechange", new XAttribute("timer", PendingLocationTypeChange.Value.delay));
                if (PendingLocationTypeChange.Value.parentMission != null)
                {
                    changeElement.Add(new XAttribute("missionidentifier", PendingLocationTypeChange.Value.parentMission.Identifier));
                    locationElement.Add(changeElement);
                }
                else
                {
                    int index = Type.CanChangeTo.IndexOf(PendingLocationTypeChange.Value.typeChange);
                    changeElement.Add(new XAttribute("index", index));
                    if (index == -1)
                    {
                        DebugConsole.AddWarning($"Invalid location type change in the location \"{DisplayName}\". Unknown type change ({PendingLocationTypeChange.Value.typeChange.ChangeToType}).");
                    }
                    else
                    {
                        locationElement.Add(changeElement);
                    }
                }
            }

            if (LocationTypeChangeCooldown > 0)
            {
                locationElement.Add(new XAttribute("locationtypechangecooldown", LocationTypeChangeCooldown));
            }

            if (takenItems.Any())
            {
                locationElement.Add(new XAttribute(
                    "takenitems",
                    string.Join(',', takenItems.Select(it => it.Identifier + ";" + it.OriginalID + ";" + it.OriginalContainerIndex + ";" + it.ModuleIndex))));
            }
            if (killedCharacterIdentifiers.Any())
            {
                locationElement.Add(new XAttribute("killedcharacters", string.Join(',', killedCharacterIdentifiers)));
            }

            if (Stores != null)
            {
                foreach (var store in Stores.Values)
                {
                    var storeElement = new XElement("store",
                        new XAttribute("identifier", store.Identifier.Value),
                        new XAttribute(nameof(store.MerchantFaction), store.MerchantFaction),
                        new XAttribute("balance", store.Balance),
                        new XAttribute("pricemodifier", store.PriceModifier));
                    foreach (PurchasedItem item in store.Stock)
                    {
                        if (item?.ItemPrefab == null) { continue; }
                        storeElement.Add(new XElement("stock",
                            new XAttribute("id", item.ItemPrefab.Identifier),
                            new XAttribute("qty", item.Quantity)));
                    }
                    if (store.DailySpecials.Any())
                    {
                        var dailySpecialElement = new XElement("dailyspecials");
                        foreach (var item in store.DailySpecials)
                        {
                            dailySpecialElement.Add(new XElement("item",
                                new XAttribute("id", item.Identifier)));
                        }
                        storeElement.Add(dailySpecialElement);
                    }
                    if (store.RequestedGoods.Any())
                    {
                        var requestedGoodsElement = new XElement("requestedgoods");
                        foreach (var item in store.RequestedGoods)
                        {
                            requestedGoodsElement.Add(new XElement("item",
                                new XAttribute("id", item.Identifier)));
                        }
                        storeElement.Add(requestedGoodsElement);
                    }
                    locationElement.Add(storeElement);
                }
            }

            if (AvailableMissions is List<Mission> missions && missions.Any())
            {
                var missionsElement = new XElement("missions");
                foreach (Mission mission in missions)
                {
                    var location = mission.Locations.All(l => l == this) ? this : mission.Locations.FirstOrDefault(l => l != this);
                    var destinationIndex = map.Locations.IndexOf(location);
                    var originIndex = map.Locations.IndexOf(mission.OriginLocation);
                    missionsElement.Add(new XElement("mission",
                        new XAttribute("prefabid", mission.Prefab.Identifier),
                        new XAttribute("destinationindex", destinationIndex),
                        new XAttribute(nameof(Mission.TimesAttempted), mission.TimesAttempted),
                        new XAttribute("origin", originIndex),
                        new XAttribute("selected", selectedMissions.Contains(mission))));
                }
                locationElement.Add(missionsElement);
            }

            parentElement.Add(locationElement);

            return locationElement;
        }

        public void Remove()
        {
            RemoveProjSpecific();
        }

        public void RemoveProjSpecific()
        {
            HireManager?.Remove();
        }

        public class AbilityLocation : AbilityObject, IAbilityLocation
        {
            public AbilityLocation(Location location)
            {
                Location = location;
            }

            public Location Location { get; set; }
        }
    }
}

#nullable enable
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.Linq;
using System.Xml.Linq;
using Barotrauma.Extensions;
using Microsoft.Xna.Framework;

namespace Barotrauma
{
    internal readonly struct UpgradePrice
    {
        public readonly int BasePrice;

        public readonly int IncreaseLow;

        public readonly int IncreaseHigh;

        public UpgradePrice(UpgradePrefab prefab, ContentXElement element)
        {
            IncreaseLow = UpgradePrefab.ParsePercentage(element.GetAttributeString("increaselow", string.Empty)!,
                "IncreaseLow".ToIdentifier(), element, suppressWarnings: prefab.SuppressWarnings);

            IncreaseHigh = UpgradePrefab.ParsePercentage(element.GetAttributeString("increasehigh", string.Empty)!,
                "IncreaseHigh".ToIdentifier(), element, suppressWarnings: prefab.SuppressWarnings);

            BasePrice = element.GetAttributeInt("baseprice", -1);

            if (BasePrice == -1)
            {
                if (!prefab.SuppressWarnings)
                {
                    DebugConsole.AddWarning($"Price attribute \"baseprice\" is not defined for {prefab?.Identifier}.\n " +
                                            "The value has been assumed to be '1000'.",
                                            prefab!.ContentPackage);
                    BasePrice = 1000;
                }
            }
        }

        public int GetBuyPrice(UpgradePrefab prefab, int level, Location? location = null, ImmutableHashSet<Character>? characterList = null)
        {
            float price = BasePrice;

            int maxLevel = prefab.MaxLevel;

            float lerpAmount = maxLevel is 0
                ? level // avoid division by 0
                : level / (float)maxLevel;

            float priceMultiplier = MathHelper.Lerp(IncreaseLow, IncreaseHigh, lerpAmount);
            price += price * (priceMultiplier / 100f);

            price = location?.GetAdjustedMechanicalCost((int)price) ?? price;

            // Adjust by campaign difficulty settings
            if (GameMain.GameSession?.Campaign is CampaignMode campaign)
            {
                price *= campaign.Settings.ShipyardPriceMultiplier;
            }

            characterList ??= GameSession.GetSessionCrewCharacters(CharacterType.Both);

            if (characterList.Any())
            {
                if (location?.Faction is { } faction && Faction.GetPlayerAffiliationStatus(faction) is FactionAffiliation.Positive)
                {
                    price *= 1f - characterList.Max(static c => c.GetStatValue(StatTypes.ShipyardBuyMultiplierAffiliated));
                }
                price *= 1f - characterList.Max(static c => c.GetStatValue(StatTypes.ShipyardBuyMultiplier));
            }
            return (int)price;
        }
    }

    abstract class UpgradeContentPrefab : Prefab
    {
        public static readonly PrefabCollection<UpgradeContentPrefab> AllPrefabs = new PrefabCollection<UpgradeContentPrefab>(
            onAdd: (prefab, isOverride) =>
            {
                if (prefab is UpgradePrefab upgradePrefab)
                {
                    UpgradePrefab.Prefabs.Add(upgradePrefab, isOverride);
                }
                else if (prefab is UpgradeCategory upgradeCategory)
                {
                    UpgradeCategory.Categories.Add(upgradeCategory, isOverride);
                }
                else if (prefab is SubmarineClass submarineClass)
                {
                    SubmarineClass.Classes.Add(submarineClass, isOverride);
                }
            },
            onRemove: (prefab) =>
            {
                if (prefab is UpgradePrefab upgradePrefab)
                {
                    UpgradePrefab.Prefabs.Remove(upgradePrefab);
                }
                else if (prefab is UpgradeCategory upgradeCategory)
                {
                    UpgradeCategory.Categories.Remove(upgradeCategory);
                }
                else if (prefab is SubmarineClass submarineClass)
                {
                    SubmarineClass.Classes.Remove(submarineClass);
                }
            },
            onSort: () =>
            {
                UpgradePrefab.Prefabs.SortAll();
                UpgradeCategory.Categories.SortAll();
                SubmarineClass.Classes.SortAll();
            },
            onAddOverrideFile: (file) =>
            {
                UpgradePrefab.Prefabs.AddOverrideFile(file);
                UpgradeCategory.Categories.AddOverrideFile(file);
                SubmarineClass.Classes.AddOverrideFile(file);
            },
            onRemoveOverrideFile: (file) =>
            {
                UpgradePrefab.Prefabs.RemoveOverrideFile(file);
                UpgradeCategory.Categories.RemoveOverrideFile(file);
                SubmarineClass.Classes.RemoveOverrideFile(file);
            });

        public UpgradeContentPrefab(ContentXElement element, UpgradeModulesFile file) : base(file, element) { }
    }

    internal partial class SubmarineClass : UpgradeContentPrefab
    {
        public static readonly PrefabCollection<SubmarineClass> Classes = new();
        
        public static Identifier UndefinedIdentifier = new("undefined");
        public static SubmarineClass Undefined => Classes[UndefinedIdentifier];

        public readonly ImmutableHashSet<Identifier> AltIdentifiers;
        public readonly Identifier TextIdentifier;
        public readonly LocalizedString Name, Description;
        public readonly bool Purchasable, UsableInCampaign;
        
        public SubmarineClass(ContentXElement element, UpgradeModulesFile file) : base(element, file)
        {
            AltIdentifiers = element.GetAttributeIdentifierArray(Array.Empty<Identifier>(), "altidentifiers", "altidentifier", "oldidentifiers", "oldidentifier").ToImmutableHashSet();
            
            TextIdentifier = element.GetAttributeIdentifier("textidentifier", Identifier);
            Name = TextManager.Get($"SubmarineClass.{TextIdentifier}").Fallback(element.GetAttributeString("name", Identifier.Value));
            Description = TextManager.Get($"SubmarineClass.{TextIdentifier}.Description").Fallback(element.GetAttributeString("description", ""));
            
            Purchasable = element.GetAttributeBool("purchasable", true);
            UsableInCampaign = element.GetAttributeBool("usableincampaign", true);

#if CLIENT
            foreach (ContentXElement subElement in element.Elements())
            {
                switch (subElement.Name.ToString().ToLowerInvariant())
                {
                    case "locationindicator":
                    {
                        LocationIndicator = new Sprite(subElement);
                        break;
                    }
                    case "availabilityicon":
                    {
                        AvailabilityIcon = new Sprite(subElement);
                        break;
                    }
                }
            }
#endif
        }

        public static SubmarineClass? Find(Identifier identifier)
        {
            if (identifier.IsEmpty) { return null; }
            if (Classes.TryGet(identifier, out SubmarineClass? submarineClass)) { return submarineClass; }
            return Classes.Find(subClass => subClass.AltIdentifiers.Contains(identifier));
        }
        
        public override void Dispose() { }
    }

    internal class UpgradeCategory : UpgradeContentPrefab
    {
        public static readonly PrefabCollection<UpgradeCategory> Categories = new PrefabCollection<UpgradeCategory>();

        private readonly ImmutableHashSet<Identifier> selfItemTags;
        private readonly HashSet<Identifier> prefabsThatAllowUpgrades = new HashSet<Identifier>();
        public readonly bool IsWallUpgrade;
        public readonly LocalizedString Name;

        private readonly object mutex = new object();

        public readonly IEnumerable<Identifier> ItemTags;

        public UpgradeCategory(ContentXElement element, UpgradeModulesFile file) : base(element, file)
        {
            selfItemTags = element.GetAttributeIdentifierArray("items", Array.Empty<Identifier>())?.ToImmutableHashSet() ?? ImmutableHashSet<Identifier>.Empty;
            Name = element.GetAttributeString("name", string.Empty)!;
            IsWallUpgrade = element.GetAttributeBool("wallupgrade", false);

            ItemTags = selfItemTags.CollectionConcat(prefabsThatAllowUpgrades);

            Identifier nameIdentifier = element.GetAttributeIdentifier("nameidentifier", Identifier.Empty);
            if (!nameIdentifier.IsEmpty)
            {
                Name = TextManager.Get($"{nameIdentifier}");
            }
            else if (Name.IsNullOrWhiteSpace())
            {
                Name = TextManager.Get($"UpgradeCategory.{Identifier}");
            }
        }

        public void DeterminePrefabsThatAllowUpgrades()
        {
            lock (mutex)
            {
                prefabsThatAllowUpgrades.Clear();
                prefabsThatAllowUpgrades.UnionWith(ItemPrefab.Prefabs
                    .Where(it => it.GetAllowedUpgrades().Contains(Identifier))
                    .Select(it => it.Identifier));
            }
        }

        public bool CanBeApplied(MapEntity item, UpgradePrefab? upgradePrefab)
        {
            if (upgradePrefab != null && item.Submarine is { Info: var info } && !upgradePrefab.IsApplicable(info)) { return false; }

            bool isStructure = item is Structure;
            switch (IsWallUpgrade)
            {
                case true:
                    return isStructure;
                case false when isStructure:
                    return false;
            }

            if (upgradePrefab != null && upgradePrefab.IsDisallowed(item)) { return false; }

            lock (mutex)
            {
                return item.Prefab.GetAllowedUpgrades().Contains(Identifier) ||
                       ItemTags.Any(tag => item.Prefab.Tags.Contains(tag) || item.Prefab.Identifier == tag);
            }
        }

        public static UpgradeCategory? Find(Identifier identifier)
        {
            return !identifier.IsEmpty ? Categories.Find(category => category.Identifier == identifier) : null;
        }

        public override void Dispose() { }
    }

    internal readonly struct UpgradeMaxLevelMod
    {
        private enum MaxLevelModType
        {
            Invalid,
            Increase,
            Set
        }

        private readonly Either<Identifier, int> tierOrClass;
        private readonly int value;
        private readonly MaxLevelModType type;

        public int GetLevelAfter(int level) =>
            type switch
            {
                MaxLevelModType.Invalid => level,
                MaxLevelModType.Increase => level + value,
                MaxLevelModType.Set => value,
                _ => throw new ArgumentOutOfRangeException()
            };

        public bool AppliesTo(SubmarineClass subClass, int subTier)
        {
            if (type is MaxLevelModType.Invalid) { return false; }

            if (GameMain.GameSession?.Campaign?.CampaignMetadata is { } metadata)
            {
                int modifier = metadata.GetInt(new Identifier("tiermodifieroverride"), 0);

                subTier = Math.Max(modifier, subTier);
            }

            if (tierOrClass.TryGet(out int tier))
            {
                return subTier == tier;
            }

            if (tierOrClass.TryGet(out Identifier targetClass))
            {
                return subClass.Identifier == targetClass;
            }

            return false;
        }

        public UpgradeMaxLevelMod(ContentXElement element)
        {
            bool isValid = true;

            Identifier subClass = element.GetAttributeIdentifier("class", Identifier.Empty);
            int tier = element.GetAttributeInt("tier", 0);
            if (subClass != Identifier.Empty)
            {
                tierOrClass = subClass;
            }
            else
            {
                tierOrClass = tier;
            }

            string stringValue = element.GetAttributeString("level", null) ?? string.Empty;
            value = 0;

            if (string.IsNullOrWhiteSpace(stringValue)) { isValid = false; }

            char firstChar = stringValue[0];

            if (!int.TryParse(stringValue, NumberStyles.Any, CultureInfo.InvariantCulture, out var intValue)) { isValid = false; }
            value = intValue;

            if (firstChar.Equals('+') || firstChar.Equals('-'))
            {
                type = MaxLevelModType.Increase;
            }
            else
            {
                type = MaxLevelModType.Set;
            }

            if (!isValid) { type = MaxLevelModType.Invalid; }
        }
    }

    internal readonly struct UpgradeResourceCost
    {
        public readonly int Amount;
        private readonly ImmutableArray<Identifier> targetTags;
        public readonly Range<int> TargetLevels;

        public UpgradeResourceCost(ContentXElement element)
        {
            Amount = element.GetAttributeInt("amount", 0);
            targetTags = element.GetAttributeIdentifierArray("item", Array.Empty<Identifier>())!.ToImmutableArray();
            TargetLevels = element.GetAttributeRange("levels", new Range<int>(0, 99));
        }

        public bool AppliesForLevel(int currentLevel) => TargetLevels.Contains(currentLevel);

        public bool AppliesForLevel(Range<int> newLevels) => newLevels.Start <= TargetLevels.End && newLevels.End >= TargetLevels.Start;

        public bool MatchesItem(Item item) => MatchesItem(item.Prefab);

        public bool MatchesItem(ItemPrefab item)
        {
            foreach (Identifier tag in targetTags)
            {
                if (tag.Equals(item.Identifier) || item.Tags.Contains(tag)) { return true; }
            }

            return false;
        }
    }

    internal readonly struct ApplicableResourceCollection
    {
        public readonly ImmutableArray<ItemPrefab> MatchingItems;
        public readonly UpgradeResourceCost Cost;
        public readonly int Count;

        public ApplicableResourceCollection(IEnumerable<ItemPrefab> matchingItems, int count, UpgradeResourceCost cost)
        {
            MatchingItems = matchingItems.ToImmutableArray();
            Count = count;
            Cost = cost;
        }

        public static ApplicableResourceCollection CreateFor(UpgradeResourceCost cost)
        {
            return new ApplicableResourceCollection(ItemPrefab.Prefabs.Where(cost.MatchesItem), cost.Amount, cost);
        }
    }

    internal sealed partial class UpgradePrefab : UpgradeContentPrefab
    {
        public static readonly PrefabCollection<UpgradePrefab> Prefabs = new PrefabCollection<UpgradePrefab>(
            onAdd: static (prefab, isOverride) =>
            {
                if (!prefab.SuppressWarnings && !isOverride)
                {
                    foreach (UpgradePrefab matchingPrefab in Prefabs?.Where(p => p != prefab && p.TargetItems.Any(s => prefab.TargetItems.Contains(s))) ?? throw new NullReferenceException("Honestly I have no clue why this could be null..."))
                    {
                        if (matchingPrefab.isOverride) { continue; }

                        var upgradePrefab = matchingPrefab.targetProperties;
                        string key = string.Empty;

                        if (upgradePrefab.Keys.Any(s => prefab.targetProperties.Keys.Any(s1 => s == (key = s1))))
                        {
                            if (upgradePrefab.ContainsKey(key) && upgradePrefab[key].Any(s => prefab.targetProperties[key].Contains(s)))
                            {
                                DebugConsole.AddWarning($"Upgrade \"{prefab.Identifier}\" is affecting a property that is also being affected by \"{matchingPrefab.Identifier}\".\n" +
                                                        "This is unsupported and might yield unexpected results if both upgrades are applied at the same time to the same item.\n" +
                                                        "Add the attribute suppresswarnings=\"true\" to your XML element to disable this warning if you know what you're doing.",
                                                        prefab.ContentPackage);
                            }
                        }
                    }
                }
            },
            onRemove: null,
            onSort: null,
            onAddOverrideFile: null,
            onRemoveOverrideFile: null
        );

        /// <summary>
        /// Maximum upgrade level without taking submarine tier or class restrictions into account
        /// </summary>
        public readonly int MaxLevel;

        public LocalizedString Name { get; }

        public LocalizedString Description { get; }

        public float IncreaseOnTooltip { get; }

        private readonly ImmutableHashSet<Identifier> upgradeCategoryIdentifiers;

        public IEnumerable<UpgradeCategory> UpgradeCategories
        {
            get
            {
                foreach (var id in upgradeCategoryIdentifiers)
                {
                    if (UpgradeCategory.Categories.TryGet(id, out var category)) { yield return category!; }
                }
            }
        }

        public UpgradePrice Price { get; }

        private bool isOverride => Prefabs.IsOverride(this);

        public ContentXElement SourceElement { get; }

        public bool SuppressWarnings { get; }

        public bool HideInMenus { get; }

        public IEnumerable<Identifier> TargetItems => UpgradeCategories.SelectMany(u => u.ItemTags);

        public bool IsWallUpgrade => UpgradeCategories.All(u => u.IsWallUpgrade);

        private Dictionary<string, string[]> targetProperties { get; }
        private readonly ImmutableArray<UpgradeMaxLevelMod> MaxLevelsMods;
        public readonly ImmutableHashSet<UpgradeResourceCost> ResourceCosts;
        
        public const int CrushDepthDefaultUpgradePrc = 15;
        private static int? crushDepthUpgradePrc;
        
        public static int CrushDepthUpgradePrc
        {
            get
            {
                if (crushDepthUpgradePrc == null)
                {
                    if (Find("increasewallhealth".ToIdentifier()) is UpgradePrefab hullUpgradePrefab)
                    {
                        string updateValueStr = hullUpgradePrefab.SourceElement?.GetChildElement("Structure")?.GetAttributeString("crushdepth", null) ?? string.Empty;
                        if (!string.IsNullOrEmpty(updateValueStr))
                        {
                            crushDepthUpgradePrc = ParsePercentage(updateValueStr, Identifier.Empty, suppressWarnings: true);
                        }
                    }
                }
                
                return crushDepthUpgradePrc ?? CrushDepthDefaultUpgradePrc;
            }
        }
        
        public const int IncreaseWallHealthDefaultMaxLevel = 6;
        private static int? increaseWallHealthMaxLevel;
        
        public static int IncreaseWallHealthMaxLevel
        {
            get
            {
                if (increaseWallHealthMaxLevel == null)
                {
                    if (Find("increasewallhealth".ToIdentifier()) is UpgradePrefab hullUpgradePrefab)
                    {
                        increaseWallHealthMaxLevel = hullUpgradePrefab.MaxLevel;
                    }
                }
                
                return increaseWallHealthMaxLevel ?? IncreaseWallHealthDefaultMaxLevel;
            }
        }
        
        public UpgradePrefab(ContentXElement element, UpgradeModulesFile file) : base(element, file)
        {
            Name = element.GetAttributeString(nameof(Name), string.Empty)!;
            Description = element.GetAttributeString(nameof(Description), string.Empty)!;
            MaxLevel = element.GetAttributeInt(nameof(MaxLevel), 1);
            SuppressWarnings = element.GetAttributeBool(nameof(SuppressWarnings), false);
            HideInMenus = element.GetAttributeBool(nameof(HideInMenus), false);
            SourceElement = element;

            var targetProperties = new Dictionary<string, string[]>();
            var maxLevels = new List<UpgradeMaxLevelMod>();
            var resourceCosts = new HashSet<UpgradeResourceCost>();

            Identifier nameIdentifier = element.GetAttributeIdentifier("nameidentifier", "");
            if (!nameIdentifier.IsEmpty)
            {
                Name = TextManager.Get($"UpgradeName.{nameIdentifier}");
            }
            else if (Name.IsNullOrWhiteSpace())
            {
                Name = TextManager.Get($"UpgradeName.{Identifier}");
            }

            Identifier descriptionIdentifier = element.GetAttributeIdentifier("descriptionidentifier", "");
            if (!descriptionIdentifier.IsEmpty)
            {
                Description = TextManager.Get($"UpgradeDescription.{descriptionIdentifier}");
            }
            else if (Description.IsNullOrWhiteSpace())
            {
                Description = TextManager.Get($"UpgradeDescription.{Identifier}");
            }

            IncreaseOnTooltip = element.GetAttributeFloat("increaseontooltip", 0f);

            DebugConsole.Log("    " + Name);

#if CLIENT
            var decorativeSprites = new List<DecorativeSprite>();
#endif
            foreach (var subElement in element.Elements())
            {
                switch (subElement.Name.ToString().ToLowerInvariant())
                {
                    case "price":
                    {
                        Price = new UpgradePrice(this, subElement);
                        break;
                    }
                    case "maxlevel":
                    {
                        maxLevels.Add(new UpgradeMaxLevelMod(subElement));
                        break;
                    }
                    case "resourcecost":
                    {
                        resourceCosts.Add(new UpgradeResourceCost(subElement));
                        break;
                    }
#if CLIENT
                    case "decorativesprite":
                    {
                        decorativeSprites.Add(new DecorativeSprite(subElement));
                        break;
                    }
                    case "sprite":
                    {
                        Sprite = new Sprite(subElement);
                        break;
                    }
#else
                    case "decorativesprite":
                    case "sprite":
                        break;
#endif
                    default:
                    {
                        IEnumerable<string> properties = subElement.Attributes().Select(attribute => attribute.Name.ToString());
                        targetProperties.Add(subElement.Name.ToString(), properties.ToArray());
                        break;
                    }
                }
            }

#if CLIENT
            DecorativeSprites = decorativeSprites.ToImmutableArray();
#endif

            this.targetProperties = targetProperties;
            MaxLevelsMods = maxLevels.ToImmutableArray();
            ResourceCosts = resourceCosts.ToImmutableHashSet();

            upgradeCategoryIdentifiers = element.GetAttributeIdentifierArray("categories", Array.Empty<Identifier>())?
                .ToImmutableHashSet() ?? ImmutableHashSet<Identifier>.Empty;
        }

        /// <summary>
        /// Returns the maximum upgrade level for the current sub, taking tier and class restrictions into account
        /// </summary>
        public int GetMaxLevelForCurrentSub()
        {
            Submarine? sub = GameMain.GameSession?.Submarine ?? Submarine.MainSub;
            return sub is { Info: var info } ? GetMaxLevel(info) : MaxLevel;
        }

        /// <summary>
        /// Returns the maximum upgrade level for the specified sub, taking tier and class restrictions into account
        /// </summary>
        public int GetMaxLevel(SubmarineInfo info)
        {
            int level = MaxLevel;

            int tier = info.Tier;

            if (GameMain.GameSession?.Campaign?.CampaignMetadata is { } metadata)
            {
                int modifier = metadata.GetInt(new Identifier($"tiermodifiers.{Identifier}"), 0);
                tier += modifier;
            }

            tier = Math.Clamp(tier, 1, SubmarineInfo.HighestTier);

            foreach (UpgradeMaxLevelMod mod in MaxLevelsMods)
            {
                if (mod.AppliesTo(info.Class, tier)) { level = mod.GetLevelAfter(level); }
            }

            return level;
        }

        public bool IsApplicable(SubmarineInfo? info)
        {
            if (info is null) { return false; }

            return GetMaxLevel(info) > 0;
        }

        public bool HasResourcesToUpgrade(Character? character, int currentLevel)
        {
            if (character is null) { return false; }
            if (!ResourceCosts.Any()) { return true; }

            var allItems = CargoManager.FindAllItemsOnPlayerAndSub(character);

            return ResourceCosts.Where(cost => cost.AppliesForLevel(currentLevel)).All(cost => cost.Amount <= allItems.Count(cost.MatchesItem));
        }

        // ReSharper disable PossibleMultipleEnumeration
        public bool TryTakeResources(Character character, int currentLevel)
        {
            var costs = ResourceCosts.Where(cost => cost.AppliesForLevel(currentLevel));

            if (!costs.Any()) { return true; }

            var inventoryItems = CargoManager.FindAllItemsOnPlayerAndSub(character);
            HashSet<Item> itemsToRemove = new HashSet<Item>();

            foreach (UpgradeResourceCost cost in costs)
            {
                int amountNeeded = cost.Amount;
                foreach (Item item in inventoryItems.Where(cost.MatchesItem))
                {
                    itemsToRemove.Add(item);
                    amountNeeded--;
                    if (amountNeeded <= 0) { break; }
                }

                if (amountNeeded > 0) { return false; }
            }

            foreach (Item item in itemsToRemove)
            {
                Entity.Spawner.AddItemToRemoveQueue(item);
            }

            if (GameMain.IsMultiplayer) { character.Inventory.CreateNetworkEvent(); }

            return true;
        }

        public ImmutableArray<ApplicableResourceCollection> GetApplicableResources(int level)
        {
            var applicableCosts = ResourceCosts.Where(cost => cost.AppliesForLevel(level)).ToImmutableHashSet();

            var costs = applicableCosts.Any()
                ? applicableCosts.Select(ApplicableResourceCollection.CreateFor).ToImmutableArray()
                : ImmutableArray<ApplicableResourceCollection>.Empty;

            return costs;
        }

        public bool IsDisallowed(MapEntity item)
        {
            return item.DisallowedUpgradeSet.Contains(Identifier)
                   || UpgradeCategories.Any(c => item.DisallowedUpgradeSet.Contains(c.Identifier));
        }

        public static UpgradePrefab? Find(Identifier identifier)
        {
            return identifier != Identifier.Empty ? Prefabs.Find(prefab => prefab.Identifier == identifier) : null;
        }

        /// <summary>
        /// Parse a integer value from a string that is formatted like a percentage increase / decrease.
        /// </summary>
        /// <param name="value">String to parse</param>
        /// <param name="attribute">What XML attribute the value originates from, only used for warning formatting.</param>
        /// <param name="sourceElement">What XMLElement the value originates from, only used for warning formatting.</param>
        /// <param name="suppressWarnings">Whether or not to suppress warnings if both "attribute" and "sourceElement" are defined.</param>
        /// <returns></returns>
        /// <example>
        /// This sample returns -15 as an integer.
        /// <code>
        /// XElement element = new XElement("change", new XAttribute("increase", "-15%"));
        /// ParsePercentage(element.GetAttributeString("increase", string.Empty));
        /// </code>
        /// </example>
        public static int ParsePercentage(string value, Identifier attribute = default, XElement? sourceElement = null, bool suppressWarnings = false)
        {
            string? line = sourceElement?.ToString().Split('\n')[0].Trim();
            bool doWarnings = !suppressWarnings && !attribute.IsEmpty && sourceElement != null && line != null;

            if (string.IsNullOrWhiteSpace(value))
            {
                if (doWarnings)
                {
                    DebugConsole.AddWarning($"Attribute \"{attribute}\" not found at {sourceElement!.Document?.ParseContentPathFromUri()} @ '{line}'.\n " +
                                            "Value has been assumed to be '0'.");
                }

                return 1;
            }

            if (!int.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out var price))
            {
                string str = value;

                if (str.Length > 1 && str[0] == '+') { str = str.Substring(1); }

                if (str.Length > 1 && str[^1] == '%') { str = str.Substring(0, str.Length - 1); }

                if (int.TryParse(str, out price))
                {
                    return price;
                }
            }
            else
            {
                return price;
            }

            if (doWarnings)
            {
                DebugConsole.AddWarning($"Value in attribute \"{attribute}\" is not formatted correctly\n " +
                                        $"at {sourceElement!.Document?.ParseContentPathFromUri()} @ '{line}'.\n " +
                                        "It should be an integer with optionally a '+' or '-' at the front and/or '%' at the end.\n" +
                                        "The value has been assumed to be '0'.");
            }

            return 1;
        }

        public override void Dispose()
        {
#if CLIENT
            Sprite?.Remove();
            Sprite = null;
            DecorativeSprites.ForEach(sprite => sprite.Remove());
            targetProperties.Clear();
#endif
        }
    }
}
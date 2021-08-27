using Barotrauma.Extensions;
using Barotrauma.Items.Components;
using FarseerPhysics;
using Microsoft.Xna.Framework;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Xml.Linq;
using Barotrauma.Networking;

namespace Barotrauma
{
    class DurationListElement
    {
        public readonly StatusEffect Parent;
        public readonly Entity Entity;
        public float Duration
        {
            get;
            private set;
        }
        public readonly List<ISerializableEntity> Targets;
        public Character User { get; private set; }

        public float Timer;

        public DurationListElement(StatusEffect parentEffect, Entity parentEntity, IEnumerable<ISerializableEntity> targets, float duration, Character user)
        {
            Parent = parentEffect;
            Entity = parentEntity;
            Targets = new List<ISerializableEntity>(targets);
            Timer = Duration = duration;
            User = user;
        }

        public void Reset(float duration, Character newUser)
        {
            Timer = Duration = duration;
            User = newUser;
        }
    }

    class AITrigger : ISerializableEntity
    {
        public string Name => "ai trigger";

        public Dictionary<string, SerializableProperty> SerializableProperties { get; set; }

        [Serialize(AIState.Idle, false)]
        public AIState State { get; private set; }

        [Serialize(0f, false)]
        public float Duration { get; private set; }

        [Serialize(1f, false)]
        public float Probability { get; private set; }

        [Serialize(0f, false)]
        public float MinDamage { get; private set; }

        [Serialize(true, false)]
        public bool AllowToOverride { get; private set; }

        [Serialize(true, false)]
        public bool AllowToBeOverridden { get; private set; }

        public bool IsTriggered { get; private set; }

        public float Timer { get; private set; } = -1;

        public bool IsActive { get; private set; }

        public void Launch()
        {
            IsTriggered = true;
            IsActive = true;
            Timer = Duration;
        }

        public void Reset()
        {
            IsTriggered = false;
            IsActive = false;
            Timer = 0;
        }

        public void UpdateTimer(float deltaTime)
        {
            Timer -= deltaTime;
            if (Timer < 0)
            {
                Timer = 0;
                IsActive = false;
            }
        }

        public AITrigger(XElement element)
        {
            SerializableProperties = SerializableProperty.DeserializeProperties(this, element);
        }
    }

    partial class StatusEffect
    {
        [Flags]
        public enum TargetType
        {
            This = 1,
            Parent = 2,
            Character = 4,
            Contained = 8,
            NearbyCharacters = 16,
            NearbyItems = 32,
            UseTarget = 64,
            Hull = 128,
            Limb = 256,
            AllLimbs = 512,
            LastLimb = 1024
        }

        class ItemSpawnInfo
        {
            public enum SpawnPositionType
            {
                This,
                ThisInventory,
                ContainedInventory
            }

            public enum SpawnRotationType
            {
                Fixed,
                Target,
                Limb,
                MainLimb,
                Collider
            }

            public readonly ItemPrefab ItemPrefab;
            public readonly SpawnPositionType SpawnPosition;
            public readonly float Speed;
            public readonly float Rotation;
            public readonly int Count;
            public readonly float Spread;
            public readonly SpawnRotationType RotationType;
            public readonly float AimSpread;

            public ItemSpawnInfo(XElement element, string parentDebugName)
            {
                if (element.Attribute("name") != null)
                {
                    //backwards compatibility
                    DebugConsole.ThrowError("Error in StatusEffect config (" + element.ToString() + ") - use item identifier instead of the name.");
                    string itemPrefabName = element.GetAttributeString("name", "");
                    ItemPrefab = ItemPrefab.Prefabs.Find(m => m.NameMatches(itemPrefabName, StringComparison.InvariantCultureIgnoreCase) || m.Tags.Contains(itemPrefabName));
                    if (ItemPrefab == null)
                    {
                        DebugConsole.ThrowError("Error in StatusEffect \"" + parentDebugName + "\" - item prefab \"" + itemPrefabName + "\" not found.");
                    }
                }
                else
                {
                    string itemPrefabIdentifier = element.GetAttributeString("identifier", "");
                    if (string.IsNullOrEmpty(itemPrefabIdentifier)) itemPrefabIdentifier = element.GetAttributeString("identifiers", "");
                    if (string.IsNullOrEmpty(itemPrefabIdentifier))
                    {
                        DebugConsole.ThrowError("Invalid item spawn in StatusEffect \"" + parentDebugName + "\" - identifier not found in the element \"" + element.ToString() + "\"");
                    }
                    ItemPrefab = ItemPrefab.Prefabs.Find(m => m.Identifier == itemPrefabIdentifier);
                    if (ItemPrefab == null)
                    {
                        DebugConsole.ThrowError("Error in StatusEffect config - item prefab with the identifier \"" + itemPrefabIdentifier + "\" not found.");
                        return;
                    }
                }

                Speed = element.GetAttributeFloat("speed", 0.0f);

                Rotation = element.GetAttributeFloat("rotation", 0.0f);
                Count = element.GetAttributeInt("count", 1);
                Spread = element.GetAttributeFloat("spread", 0f);
                AimSpread = element.GetAttributeFloat("aimspread", 0f);

                string spawnTypeStr = element.GetAttributeString("spawnposition", "This");
                if (!Enum.TryParse(spawnTypeStr, ignoreCase: true, out SpawnPosition))
                {
                    DebugConsole.ThrowError("Error in StatusEffect config - \"" + spawnTypeStr + "\" is not a valid spawn position.");
                }
                string rotationTypeStr = element.GetAttributeString("rotationtype", Rotation != 0 ? "Fixed" : "Target");
                if (!Enum.TryParse(rotationTypeStr, ignoreCase: true, out RotationType))
                {
                    DebugConsole.ThrowError("Error in StatusEffect config - \"" + rotationTypeStr + "\" is not a valid rotation type.");
                }
            }
        }

        public class CharacterSpawnInfo : ISerializableEntity
        {
            public string Name => $"Character Spawn Info ({SpeciesName})";
            public Dictionary<string, SerializableProperty> SerializableProperties { get; set; }

            [Serialize("", false)]
            public string SpeciesName { get; private set; }

            [Serialize(1, false)]
            public int Count { get; private set; }

            [Serialize(0f, false)]
            public float Spread { get; private set; }

            [Serialize("0,0", false)]
            public Vector2 Offset { get; private set; }

            public CharacterSpawnInfo(XElement element, string parentDebugName)
            {
                SerializableProperties = SerializableProperty.DeserializeProperties(this, element);
                if (string.IsNullOrEmpty(SpeciesName))
                {
                    DebugConsole.ThrowError($"Invalid character spawn ({Name}) in StatusEffect \"{parentDebugName}\" - identifier not found in the element \"{element}\"");
                }
            }
        }

        private readonly TargetType targetTypes;
        protected HashSet<string> targetIdentifiers;

        private readonly List<RelatedItem> requiredItems;

        public readonly string[] propertyNames;
        public readonly object[] propertyEffects;

        private readonly PropertyConditional.Comparison conditionalComparison = PropertyConditional.Comparison.Or;
        private readonly List<PropertyConditional> propertyConditionals;
        public bool HasConditions => propertyConditionals != null && propertyConditionals.Any();

        private readonly bool setValue;

        private readonly bool disableDeltaTime;

        private readonly HashSet<string> tags;

        private readonly float duration;
        private readonly float lifeTime;
        private float lifeTimer;

        public static readonly List<DurationListElement> DurationList = new List<DurationListElement>();

        public readonly bool CheckConditionalAlways; //Always do the conditional checks for the duration/delay. If false, only check conditional on apply.

        public readonly bool Stackable = true; //Can the same status effect be applied several times to the same targets?

#if CLIENT
        private readonly bool playSoundOnRequiredItemFailure = false;
#endif

        private readonly int useItemCount;

        private readonly bool removeItem, removeCharacter, reviveCharacter, breakLimb, hideLimb;
        private readonly float hideLimbTimer;
        private readonly bool reviveNoSeveredLimbs;

        public readonly ActionType type = ActionType.OnActive;

        public readonly List<Explosion> Explosions;

        private readonly List<ItemSpawnInfo> spawnItems;
        private readonly List<CharacterSpawnInfo> spawnCharacters;
        private readonly List<AITrigger> aiTriggers;

        private readonly List<EventPrefab> triggeredEvents;
        private readonly string triggeredEventTargetTag = "statuseffecttarget", 
                                triggeredEventEntityTag = "statuseffectentity";

        private Character user;

        public readonly float FireSize;

        public readonly LimbType[] targetLimbs;

        public readonly float SeverLimbsProbability;

        public PhysicsBody sourceBody;

        public readonly bool OnlyInside;
        public readonly bool OnlyOutside;
        // Currently only used for OnDamaged. TODO: is there a better, more generic way to do this?
        public readonly bool OnlyPlayerTriggered;

        /// <summary>
        /// Can the StatusEffect be applied when the item applying it is broken
        /// </summary>
        public readonly bool AllowWhenBroken = false;

        public HashSet<string> TargetIdentifiers
        {
            get { return targetIdentifiers; }
        }

        public HashSet<string> AllowedAfflictions { get; private set; }

        public List<Affliction> Afflictions
        {
            get;
            private set;
        }

        public IEnumerable<CharacterSpawnInfo> SpawnCharacters
        {
            get { return spawnCharacters; }
        }

        public readonly List<Pair<string, float>> ReduceAffliction;

        public float Duration => duration;

        //only applicable if targeting NearbyCharacters or NearbyItems
        public float Range
        {
            get;
            private set;
        }

        public Vector2 Offset { get; private set; }

        public string Tags
        {
            get { return string.Join(",", tags); }
            set
            {
                tags.Clear();
                if (value == null) return;

                string[] newTags = value.Split(',');
                foreach (string tag in newTags)
                {
                    string newTag = tag.Trim();
                    if (!tags.Contains(newTag)) tags.Add(newTag);
                }
            }
        }

        public static StatusEffect Load(XElement element, string parentDebugName)
        {
            if (element.Attribute("delay") != null || element.Attribute("delaytype") != null)
            {
                return new DelayedEffect(element, parentDebugName);
            }

            return new StatusEffect(element, parentDebugName);
        }

        protected StatusEffect(XElement element, string parentDebugName)
        {
            requiredItems = new List<RelatedItem>();
            spawnItems = new List<ItemSpawnInfo>();
            spawnCharacters = new List<CharacterSpawnInfo>();
            aiTriggers = new List<AITrigger>();
            Afflictions = new List<Affliction>();
            Explosions = new List<Explosion>();
            triggeredEvents = new List<EventPrefab>();
            ReduceAffliction = new List<Pair<string, float>>();
            tags = new HashSet<string>(element.GetAttributeString("tags", "").Split(','));
            OnlyInside = element.GetAttributeBool("onlyinside", false);
            OnlyOutside = element.GetAttributeBool("onlyoutside", false);
            OnlyPlayerTriggered = element.GetAttributeBool("onlyplayertriggered", false);
            AllowWhenBroken = element.GetAttributeBool("allowwhenbroken", false);

            Range = element.GetAttributeFloat("range", 0.0f);
            Offset = element.GetAttributeVector2("offset", Vector2.Zero);
            string[] targetLimbNames = element.GetAttributeStringArray("targetlimb", null) ?? element.GetAttributeStringArray("targetlimbs", null);
            if (targetLimbNames != null)
            {
                List<LimbType> targetLimbs = new List<LimbType>();
                foreach (string targetLimbName in targetLimbNames)
                {
                    if (Enum.TryParse(targetLimbName, ignoreCase: true, out LimbType targetLimb)) { targetLimbs.Add(targetLimb); }
                }
                if (targetLimbs.Count > 0) { this.targetLimbs = targetLimbs.ToArray(); }
            }

            IEnumerable<XAttribute> attributes = element.Attributes();
            List<XAttribute> propertyAttributes = new List<XAttribute>();
            propertyConditionals = new List<PropertyConditional>();

            string[] targetTypesStr = 
                element.GetAttributeStringArray("target", null) ?? 
                element.GetAttributeStringArray("targettype", new string[0]);
            foreach (string s in targetTypesStr)
            {
                if (!Enum.TryParse(s, true, out TargetType targetType))
                {
                    DebugConsole.ThrowError("Invalid target type \"" + s + "\" in StatusEffect (" + parentDebugName + ")");
                }
                else
                {
                    targetTypes |= targetType;
                }
            }

            foreach (XAttribute attribute in attributes)
            {
                switch (attribute.Name.ToString().ToLowerInvariant())
                {
                    case "type":
                        if (!Enum.TryParse(attribute.Value, true, out type))
                        {
                            DebugConsole.ThrowError("Invalid action type \"" + attribute.Value + "\" in StatusEffect (" + parentDebugName + ")");
                        }
                        break;
                    case "targettype":
                    case "target":
                        break;
                    case "disabledeltatime":
                        disableDeltaTime = attribute.GetAttributeBool(false);
                        break;
                    case "setvalue":
                        setValue = attribute.GetAttributeBool(false);
                        break;
                    case "severlimbs":
                    case "severlimbsprobability":
                        SeverLimbsProbability = MathHelper.Clamp(attribute.GetAttributeFloat(0.0f), 0.0f, 1.0f);
                        break;
                    case "targetnames":
                    case "targets":
                    case "targetidentifiers":
                    case "targettags":
                        string[] identifiers = attribute.Value.Split(',');
                        targetIdentifiers = new HashSet<string>();
                        for (int i = 0; i < identifiers.Length; i++)
                        {
                            targetIdentifiers.Add(identifiers[i].Trim().ToLowerInvariant());
                        }
                        break;
                    case "allowedafflictions":
                        string[] types = attribute.Value.Split(',');
                        AllowedAfflictions = new HashSet<string>();
                        for (int i = 0; i < types.Length; i++)
                        {
                            AllowedAfflictions.Add(types[i].Trim().ToLowerInvariant());
                        }
                        break;
                    case "duration":
                        duration = attribute.GetAttributeFloat(0.0f);
                        break;
                    case "stackable":
                        Stackable = attribute.GetAttributeBool(true);
                        break;
                    case "lifetime":
                        lifeTime = attribute.GetAttributeFloat(0);
                        lifeTimer = lifeTime;
                        break;
                    case "eventtargettag":
                        triggeredEventTargetTag = attribute.Value;
                        break;
                    case "evententitytag":
                        triggeredEventEntityTag = attribute.Value;
                        break;
                    case "checkconditionalalways":
                        CheckConditionalAlways = attribute.GetAttributeBool(false);
                        break;
                    case "conditionalcomparison":
                    case "comparison":
                        if (!Enum.TryParse(attribute.Value, ignoreCase: true, out conditionalComparison))
                        {
                            DebugConsole.ThrowError("Invalid conditional comparison type \"" + attribute.Value + "\" in StatusEffect (" + parentDebugName + ")");
                        }
                        break;
#if CLIENT
                    case "playsoundonrequireditemfailure":
                        playSoundOnRequiredItemFailure = attribute.GetAttributeBool(false);
                        break;
#endif
                    case "sound":
                        DebugConsole.ThrowError("Error in StatusEffect " + element.Parent.Name.ToString() +
                            " - sounds should be defined as child elements of the StatusEffect, not as attributes.");
                        break;
                    case "delay":
                        break;
                    case "range":
                        if (!HasTargetType(TargetType.NearbyCharacters) && !HasTargetType(TargetType.NearbyItems))
                        {
                            propertyAttributes.Add(attribute);
                        }
                        break;
                    default:
                        propertyAttributes.Add(attribute);
                        break;
                }
            }

            int count = propertyAttributes.Count;
            propertyNames = new string[count];
            propertyEffects = new object[count];

            int n = 0;
            foreach (XAttribute attribute in propertyAttributes)
            {
                propertyNames[n] = attribute.Name.ToString().ToLowerInvariant();
                propertyEffects[n] = XMLExtensions.GetAttributeObject(attribute);
                n++;
            }

            foreach (XElement subElement in element.Elements())
            {
                switch (subElement.Name.ToString().ToLowerInvariant())
                {
                    case "explosion":
                        Explosions.Add(new Explosion(subElement, parentDebugName));
                        break;
                    case "fire":
                        FireSize = subElement.GetAttributeFloat("size", 10.0f);
                        break;
                    case "use":
                    case "useitem":
                        useItemCount++;
                        break;
                    case "remove":
                    case "removeitem":
                        removeItem = true;
                        break;
                    case "removecharacter":
                        removeCharacter = true;
                        break;
                    case "revivecharacter":
                        reviveCharacter = true;
                        reviveNoSeveredLimbs = subElement.GetAttributeBool("noseveredlimbs", false);
                        break;
                    case "breaklimb":
                        breakLimb = true;
                        break;
                    case "hidelimb":
                        hideLimb = true;
                        hideLimbTimer = subElement.GetAttributeFloat("duration", 0);
                        break;
                    case "requireditem":
                    case "requireditems":
                        RelatedItem newRequiredItem = RelatedItem.Load(subElement, returnEmpty: false, parentDebugName: parentDebugName);
                        if (newRequiredItem == null)
                        {
                            DebugConsole.ThrowError("Error in StatusEffect config - requires an item with no identifiers.");
                            continue;
                        }
                        requiredItems.Add(newRequiredItem);
                        break;
                    case "conditional":
                        foreach (XAttribute attribute in subElement.Attributes())
                        {
                            if (PropertyConditional.IsValid(attribute))
                            {
                                propertyConditionals.Add(new PropertyConditional(attribute));
                            }
                        }
                        break;
                    case "affliction":
                        AfflictionPrefab afflictionPrefab;
                        if (subElement.Attribute("name") != null)
                        {
                            DebugConsole.ThrowError("Error in StatusEffect (" + parentDebugName + ") - define afflictions using identifiers instead of names.");
                            string afflictionName = subElement.GetAttributeString("name", "").ToLowerInvariant();
                            afflictionPrefab = AfflictionPrefab.List.FirstOrDefault(ap => ap.Name.ToLowerInvariant() == afflictionName);
                            if (afflictionPrefab == null)
                            {
                                DebugConsole.ThrowError("Error in StatusEffect (" + parentDebugName + ") - Affliction prefab \"" + afflictionName + "\" not found.");
                                continue;
                            }
                        }
                        else
                        {
                            string afflictionIdentifier = subElement.GetAttributeString("identifier", "").ToLowerInvariant();
                            afflictionPrefab = AfflictionPrefab.List.FirstOrDefault(ap => ap.Identifier.ToLowerInvariant() == afflictionIdentifier);
                            if (afflictionPrefab == null)
                            {
                                DebugConsole.ThrowError("Error in StatusEffect (" + parentDebugName + ") - Affliction prefab with the identifier \"" + afflictionIdentifier + "\" not found.");
                                continue;
                            }
                        }

                        Affliction afflictionInstance = afflictionPrefab.Instantiate(subElement.GetAttributeFloat(1.0f, "amount", "strength"));
                        afflictionInstance.Probability = subElement.GetAttributeFloat(1.0f, "probability");
                        Afflictions.Add(afflictionInstance);

                        break;
                    case "reduceaffliction":
                        if (subElement.Attribute("name") != null)
                        {
                            DebugConsole.ThrowError("Error in StatusEffect (" + parentDebugName + ") - define afflictions using identifiers or types instead of names.");
                            ReduceAffliction.Add(new Pair<string, float>(
                                subElement.GetAttributeString("name", "").ToLowerInvariant(),
                                subElement.GetAttributeFloat(1.0f, "amount", "strength", "reduceamount")));
                        }
                        else
                        {
                            string name = subElement.GetAttributeString("identifier", null) ?? subElement.GetAttributeString("type", null);
                            name = name.ToLowerInvariant();

                            if (AfflictionPrefab.List.Any(ap => ap.Identifier == name || ap.AfflictionType == name))
                            {
                                ReduceAffliction.Add(new Pair<string, float>(
                                    name,
                                    subElement.GetAttributeFloat(1.0f, "amount", "strength", "reduceamount")));
                            }
                            else
                            {
                                DebugConsole.ThrowError("Error in StatusEffect (" + parentDebugName + ") - Affliction prefab with the identifier or type \"" + name + "\" not found.");
                            }
                        }
                        break;
                    case "spawnitem":
                        var newSpawnItem = new ItemSpawnInfo(subElement, parentDebugName);
                        if (newSpawnItem.ItemPrefab != null) { spawnItems.Add(newSpawnItem); }
                        break;
                    case "triggerevent":
                        string identifier = subElement.GetAttributeString("identifier", null);
                        if (!string.IsNullOrWhiteSpace(identifier))
                        {
                            EventPrefab prefab = EventSet.GetEventPrefab(identifier);
                            if (prefab != null)
                            {
                                triggeredEvents.Add(prefab);
                            }
                        }
                        foreach (XElement eventElement in subElement.Elements())
                        {
                            if (!eventElement.Name.ToString().Equals("ScriptedEvent", StringComparison.OrdinalIgnoreCase)) { continue; }
                            triggeredEvents.Add(new EventPrefab(eventElement));
                        }
                        break;
                    case "spawncharacter":
                        var newSpawnCharacter = new CharacterSpawnInfo(subElement, parentDebugName);
                        if (!string.IsNullOrWhiteSpace(newSpawnCharacter.SpeciesName)) { spawnCharacters.Add(newSpawnCharacter); }
                        break;
                    case "aitrigger":
                        aiTriggers.Add(new AITrigger(subElement));
                        break;
                }
            }
            InitProjSpecific(element, parentDebugName);
        }

        partial void InitProjSpecific(XElement element, string parentDebugName);

        public bool HasTargetType(TargetType targetType)
        {
            return (targetTypes & targetType) != 0;
        }

        public bool ReducesItemCondition()
        {
            for (int i = 0; i < propertyNames.Length; i++)
            {
                if (propertyNames[i] != "condition") { continue; }
                if (propertyEffects[i].GetType() == typeof(float))
                {
                    return (float)propertyEffects[i] < 0.0f || (setValue && (float)propertyEffects[i] <= 0.0f);
                }
            }
            return false;
        }

        public virtual bool HasRequiredItems(Entity entity)
        {
            if (entity == null) { return true; }
            foreach (RelatedItem requiredItem in requiredItems)
            {
                if (entity is Item item)
                {
                    if (!requiredItem.CheckRequirements(null, item)) { return false; }
                }
                else if (entity is Character character)
                {
                    if (!requiredItem.CheckRequirements(character, null)) { return false; }
                }
            }
            return true;
        }

        public IEnumerable<ISerializableEntity> GetNearbyTargets(Vector2 worldPosition, List<ISerializableEntity> targets = null)
        {
            targets ??= new List<ISerializableEntity>();
            if (Range <= 0.0f) { return targets; }
            if (HasTargetType(TargetType.NearbyCharacters))
            {
                foreach (Character c in Character.CharacterList)
                {
                    if (c.Enabled && !c.Removed && CheckDistance(c) && IsValidTarget(c))
                    {
                        targets.Add(c);
                    }
                }
            }
            if (HasTargetType(TargetType.NearbyItems))
            {
                //optimization for powered components that can be easily fetched from Powered.PoweredList
                if (targetIdentifiers?.Count == 1 &&
                    (targetIdentifiers.Contains("powered") || targetIdentifiers.Contains("junctionbox") || targetIdentifiers.Contains("relaycomponent")))
                {
                    foreach (Powered powered in Powered.PoweredList)
                    {
                        Item item = powered.Item;
                        if (!item.Removed && CheckDistance(item) && IsValidTarget(item))
                        {
                            targets.AddRange(item.AllPropertyObjects);
                        }
                    }
                }
                else
                {
                    foreach (Item item in Item.ItemList)
                    {
                        if (!item.Removed && CheckDistance(item) && IsValidTarget(item))
                        {
                            targets.AddRange(item.AllPropertyObjects);
                        }
                    }
                }
            }
            return targets;

            bool CheckDistance(ISpatialEntity e)
            {
                float xDiff = Math.Abs(e.WorldPosition.X - worldPosition.X);
                if (xDiff > Range) { return false; }
                float yDiff = Math.Abs(e.WorldPosition.Y - worldPosition.Y);
                if (yDiff > Range) { return false; }
                if (xDiff * xDiff + yDiff * yDiff < Range * Range)
                {
                    return true;
                }
                return false;
            }
        }

        public bool HasRequiredConditions(IEnumerable<ISerializableEntity> targets)
        {
            return HasRequiredConditions(targets, propertyConditionals);
        }

        private bool HasRequiredConditions(IEnumerable<ISerializableEntity> targets, IEnumerable<PropertyConditional> conditionals, bool targetingContainer = false)
        {
            if (conditionals.None()) { return true; }
            if (requiredItems.Any() && requiredItems.All(ri => ri.MatchOnEmpty) && targets.None()) { return true; }
            switch (conditionalComparison)
            {
                case PropertyConditional.Comparison.Or:
                    foreach (PropertyConditional pc in conditionals)
                    {
                        if (pc.TargetContainer && !targetingContainer)
                        {
                            var target = targets.FirstOrDefault(t => t is Item || t is ItemComponent);
                            var targetItem = target as Item ?? (target as ItemComponent)?.Item;
                            if (targetItem?.ParentInventory == null) { continue; }
                            var owner = targetItem.ParentInventory.Owner;
                            if (pc.TargetGrandParent && owner is Item ownerItem)
                            {
                                owner = ownerItem.ParentInventory?.Owner;
                            }
                            if (owner is Item container) 
                            { 
                                if (pc.Type == PropertyConditional.ConditionType.HasTag)
                                {
                                    //if we're checking for tags, just check the Item object, not the ItemComponents
                                    if (HasRequiredConditions((container as ISerializableEntity).ToEnumerable(), pc.ToEnumerable(), targetingContainer: true)) { return true; }
                                }
                                else
                                {
                                    if (HasRequiredConditions(container.AllPropertyObjects, pc.ToEnumerable(), targetingContainer: true)) { return true; } 
                                }                                
                            }
                            if (owner is Character character && HasRequiredConditions(character.ToEnumerable(), pc.ToEnumerable(), targetingContainer: true)) { return true; }
                        }
                        else
                        {
                            foreach (ISerializableEntity target in targets)
                            {
                                if (!string.IsNullOrEmpty(pc.TargetItemComponentName))
                                {
                                    if (!(target is ItemComponent ic) || ic.Name != pc.TargetItemComponentName)
                                    {
                                        continue;
                                    }
                                }
                                if (pc.Matches(target)) { return true; }
                            }
                        }
                    }
                    return false;
                case PropertyConditional.Comparison.And:
                    foreach (PropertyConditional pc in conditionals)
                    {
                        if (pc.TargetContainer && !targetingContainer)
                        {
                            var target = targets.FirstOrDefault(t => t is Item || t is ItemComponent);
                            var targetItem = target as Item ?? (target as ItemComponent)?.Item;
                            if (targetItem?.ParentInventory == null) { return false; }
                            var owner = targetItem.ParentInventory.Owner;
                            if (pc.TargetGrandParent && owner is Item ownerItem)
                            {
                                owner = ownerItem.ParentInventory?.Owner;
                            }
                            if (owner is Item container && !HasRequiredConditions(container.AllPropertyObjects, pc.ToEnumerable(), targetingContainer: true)) { return false; }
                            if (owner is Character character && !HasRequiredConditions(character.ToEnumerable(), pc.ToEnumerable(), targetingContainer: true)) { return false; }
                        }
                        else
                        {
                            foreach (ISerializableEntity target in targets)
                            {
                                if (!string.IsNullOrEmpty(pc.TargetItemComponentName))
                                {
                                    if (!(target is ItemComponent ic) || ic.Name != pc.TargetItemComponentName)
                                    {
                                        continue;
                                    }
                                }
                            }
                            if (targets.None(t => pc.Matches(t))) { return false; }
                        }
                    }
                    return true;
                default:
                    throw new NotImplementedException();
            }
        }

        protected bool IsValidTarget(ISerializableEntity entity)
        {
            if (entity is Item item)
            {
                return IsValidTarget(item);
            }
            else if (entity is ItemComponent itemComponent)
            {
                return IsValidTarget(itemComponent);
            }
            else if (entity is Structure structure)
            {
                if (targetIdentifiers == null) { return true; }
                if (targetIdentifiers.Contains("structure")) { return true; }
                if (targetIdentifiers.Any(id => id.Equals(structure.Prefab.Identifier, StringComparison.OrdinalIgnoreCase))) { return true; }
            }
            else if (entity is Character character)
            {
                return IsValidTarget(character);
            }
            if (targetIdentifiers == null) { return true; }
            return targetIdentifiers.Any(id => id.Equals(entity.Name, StringComparison.OrdinalIgnoreCase));
        }

        protected bool IsValidTarget(ItemComponent itemComponent)
        {
            if (OnlyInside && itemComponent.Item.CurrentHull == null) { return false; }
            if (OnlyOutside && itemComponent.Item.CurrentHull != null) { return false; }
            if (targetIdentifiers == null) { return true; }
            if (targetIdentifiers.Contains("itemcomponent")) { return true; }
            if (itemComponent.Item.HasTag(targetIdentifiers)) { return true; }
            return targetIdentifiers.Any(id => id.Equals(itemComponent.Item.Prefab.Identifier, StringComparison.OrdinalIgnoreCase));
        }

        protected bool IsValidTarget(Item item)
        {
            if (OnlyInside && item.CurrentHull == null) { return false; }
            if (OnlyOutside && item.CurrentHull != null) { return false; }
            if (targetIdentifiers == null) { return true; }
            if (targetIdentifiers.Contains("item")) { return true; }
            if (item.HasTag(targetIdentifiers)) { return true; }
            return targetIdentifiers.Any(id => id.Equals(item.Prefab.Identifier, StringComparison.OrdinalIgnoreCase));
        }

        protected bool IsValidTarget(Character character)
        {
            if (OnlyInside && character.CurrentHull == null) { return false; }
            if (OnlyOutside && character.CurrentHull != null) { return false; }
            if (targetIdentifiers == null) { return true; }
            if (targetIdentifiers.Contains("character")) { return true; }
            return targetIdentifiers.Any(id => id.Equals(character.SpeciesName, StringComparison.OrdinalIgnoreCase));
        }

        public void SetUser(Character user)
        {
            this.user = user;
            foreach (Affliction affliction in Afflictions)
            {
                affliction.Source = user;
            }
        }

        public virtual void Apply(ActionType type, float deltaTime, Entity entity, ISerializableEntity target, Vector2? worldPosition = null)
        {
            if (this.type != type || !HasRequiredItems(entity)) { return; }

            if (!IsValidTarget(target)) { return; }

            if (duration > 0.0f && !Stackable)
            {
                //ignore if not stackable and there's already an identical statuseffect
                DurationListElement existingEffect = DurationList.Find(d => d.Parent == this && d.Targets.FirstOrDefault() == target);
                if (existingEffect != null)
                {
                    existingEffect.Reset(Math.Max(existingEffect.Timer, duration), user);
                    return;
                }
            }

            if (!HasRequiredConditions(target.ToEnumerable())) { return; }
            Apply(deltaTime, entity, target.ToEnumerable(), worldPosition);
        }

        protected readonly List<ISerializableEntity> currentTargets = new List<ISerializableEntity>();
        public virtual void Apply(ActionType type, float deltaTime, Entity entity, IEnumerable<ISerializableEntity> targets, Vector2? worldPosition = null)
        {
            if (this.type != type) { return; }

            currentTargets.Clear();
            foreach (ISerializableEntity target in targets)
            {
                if (!IsValidTarget(target)) { continue; }
                currentTargets.Add(target);
            }

            if (targetIdentifiers != null && currentTargets.Count == 0) { return; }

            bool hasRequiredItems = HasRequiredItems(entity);
            if (!hasRequiredItems || !HasRequiredConditions(currentTargets))
            {
#if CLIENT
                if (!hasRequiredItems && playSoundOnRequiredItemFailure)
                {
                    PlaySound(entity, GetHull(entity), GetPosition(entity, targets, worldPosition));
                }
#endif
                return; 
            }

            if (duration > 0.0f && !Stackable)
            {
                //ignore if not stackable and there's already an identical statuseffect
                DurationListElement existingEffect = DurationList.Find(d => d.Parent == this && d.Targets.SequenceEqual(currentTargets));
                if (existingEffect != null)
                {
                    existingEffect?.Reset(Math.Max(existingEffect.Timer, duration), user);
                    return;
                }
            }

            Apply(deltaTime, entity, currentTargets, worldPosition);
        }

        private Hull GetHull(Entity entity)
        {
            Hull hull = null;
            if (entity is Character character)
            {
                hull = character.AnimController.CurrentHull;
            }
            else if (entity is Item item)
            {
                hull = item.CurrentHull;
            }
            return hull;
        }

        private Vector2 GetPosition(Entity entity, IEnumerable<ISerializableEntity> targets, Vector2? worldPosition = null)
        {
            Vector2 position = worldPosition ?? (entity == null || entity.Removed ? Vector2.Zero : entity.WorldPosition);
            if (worldPosition == null)
            {
                if (entity is Character character && !character.Removed && targetLimbs?.FirstOrDefault(l => l != LimbType.None) is LimbType limbType)
                {
                    Limb limb = character.AnimController.GetLimb(limbType);
                    if (limb != null && !limb.Removed)
                    {
                        position = limb.WorldPosition;
                    }
                }
                else
                {
                    if (targets.FirstOrDefault(t => t is Limb) is Limb targetLimb && !targetLimb.Removed)
                    {
                        position = targetLimb.WorldPosition;
                    }
                    else if (HasTargetType(TargetType.Contained))
                    {
                        if (targets.FirstOrDefault(t => t is Item) is Item targetItem)
                        {
                            position = targetItem.WorldPosition;
                        }
                    }
                }
            }
            position += Offset;
            return position;
        }

        protected void Apply(float deltaTime, Entity entity, IEnumerable<ISerializableEntity> targets, Vector2? worldPosition = null)
        {
            if (lifeTime > 0)
            {
                lifeTimer -= deltaTime;
                if (lifeTimer <= 0) { return; }
            }

            Hull hull = GetHull(entity);
            Vector2 position = GetPosition(entity, targets, worldPosition);
            foreach (ISerializableEntity serializableEntity in targets)
            {
                if (!(serializableEntity is Item item)) { continue; }

                Character targetCharacter = targets.FirstOrDefault(t => t is Character character && !character.Removed) as Character;
                if (targetCharacter == null)
                {
                    foreach (var target in targets)
                    {
                        if (target is Limb limb && limb.character != null && !limb.character.Removed)
                        {
                            targetCharacter = ((Limb)target).character;
                        }
                    }
                }
                for (int i = 0; i < useItemCount; i++)
                {
                    if (item.Removed) { continue; }
                    item.Use(deltaTime, targetCharacter, targets.FirstOrDefault(t => t is Limb) as Limb);
                }
            }

            if (removeItem)
            {
                foreach (var target in targets)
                {
                    if (target is Item item) { Entity.Spawner?.AddToRemoveQueue(item); }
                }
            }
            if (removeCharacter)
            {
                foreach (var target in targets)
                {
                    if (target is Character character) { Entity.Spawner?.AddToRemoveQueue(character); }
                }
            }
            if (reviveCharacter)
            {
                foreach (var target in targets)
                {
                    if (target is Character revivedcharacter) 
                    {
                        if (reviveNoSeveredLimbs && revivedcharacter.AnimController?.LimbJoints != null)
                        {
                            foreach (var limbJoint in revivedcharacter.AnimController.LimbJoints)
                            {
                                if (limbJoint.IsSevered) { return; }
                            }
                        }
                        revivedcharacter.Revive();
#if SERVER
                        foreach (Client c in GameMain.Server.ConnectedClients)
                        {
                            if (c.Deadcharacter == revivedcharacter)
                            { 
                                GameMain.Server.SetClientCharacter(c, revivedcharacter); 
                            }
                        }
#endif
                    }
                }
            }
            if (breakLimb || hideLimb)
            {
                foreach (var target in targets)
                {
                    if (target is Character character)
                    {
                        var matchingLimb = character.AnimController.Limbs.FirstOrDefault(l => l.body == sourceBody);
                        if (matchingLimb != null)
                        {
                            if (breakLimb)
                            {
                                character.TrySeverLimbJoints(matchingLimb, severLimbsProbability: 100, damage: 100, allowBeheading: true);
                            }
                            else
                            {
                                matchingLimb.HideAndDisable(hideLimbTimer);
                            }
                        }
                    }
                }
            }

            if (duration > 0.0f)
            {
                DurationList.Add(new DurationListElement(this, entity, targets, duration, user));
            }
            else
            {
                foreach (ISerializableEntity target in targets)
                {
                    if (target == null) { continue; }

                    if (target is Entity targetEntity)
                    {
                        if (targetEntity.Removed) { continue; }
                    }
                    else if (target is Limb limb)
                    {
                        if (limb.Removed) { continue; }
                        position = limb.WorldPosition + Offset;
                    }

                    for (int i = 0; i < propertyNames.Length; i++)
                    {
                        if (target == null || target.SerializableProperties == null || !target.SerializableProperties.TryGetValue(propertyNames[i], out SerializableProperty property))
                        {
                            continue;
                        }
                        ApplyToProperty(target, property, propertyEffects[i], deltaTime);
                    }
                }
            }

            foreach (Explosion explosion in Explosions)
            {
                explosion.Explode(position, damageSource: entity, attacker: user);
            }

            foreach (ISerializableEntity target in targets)
            {
                //if the effect has a duration, these will be done in the UpdateAll method
                if (duration > 0) { break; }
                if (target == null) { continue; }
                foreach (Affliction affliction in Afflictions)
                {
                    if (Rand.Value(Rand.RandSync.Unsynced) > affliction.Probability) { continue; }
                    Affliction newAffliction = affliction;
                    if (!disableDeltaTime && !setValue)
                    {
                        newAffliction = affliction.CreateMultiplied(deltaTime);
                    }
                    if (target is Character character)
                    {
                        if (character.Removed) { continue; }
                        character.LastDamageSource = entity;
                        foreach (Limb limb in character.AnimController.Limbs)
                        {
                            if (limb.Removed) { continue; }
                            if (limb.IsSevered) { continue; }
                            if (targetLimbs != null && !targetLimbs.Contains(limb.type)) { continue; }
                            AttackResult result = limb.character.DamageLimb(position, limb, newAffliction.ToEnumerable(), stun: 0.0f, playSound: false, attackImpulse: 0.0f, attacker: affliction.Source, allowStacking: !setValue);
                            limb.character.TrySeverLimbJoints(limb, SeverLimbsProbability, disableDeltaTime ? result.Damage : result.Damage / deltaTime, allowBeheading: true);
                            //only apply non-limb-specific afflictions to the first limb
                            if (!affliction.Prefab.LimbSpecific) { break; }
                        }
                    }
                    else if (target is Limb limb)
                    {
                        if (limb.IsSevered) { continue; }
                        if (limb.character.Removed || limb.Removed) { continue; }
                        AttackResult result = limb.character.DamageLimb(position, limb, newAffliction.ToEnumerable(), stun: 0.0f, playSound: false, attackImpulse: 0.0f, attacker: affliction.Source, allowStacking: !setValue);
                        limb.character.TrySeverLimbJoints(limb, SeverLimbsProbability, disableDeltaTime ? result.Damage : result.Damage / deltaTime, allowBeheading: true);
                    }
                }

                foreach (Pair<string, float> reduceAffliction in ReduceAffliction)
                {
                    float reduceAmount = disableDeltaTime || setValue ? reduceAffliction.Second : reduceAffliction.Second * deltaTime;
                    Limb targetLimb = null;
                    Character targetCharacter = null;
                    if (target is Character character)
                    {
                        targetCharacter = character;
                    }
                    else if (target is Limb limb && !limb.Removed)
                    {
                        targetLimb = limb;
                        targetCharacter = limb.character;
                    }
                    if (targetCharacter != null && !targetCharacter.Removed)
                    {
                        float prevVitality = targetCharacter.Vitality;
                        targetCharacter.CharacterHealth.ReduceAffliction(targetLimb, reduceAffliction.First, reduceAmount);
                        if (user != null && user != targetCharacter)
                        {
                            if (!targetCharacter.IsDead)
                            {
                                targetCharacter.TryAdjustAttackerSkill(user, targetCharacter.Vitality - prevVitality);
                            }
                        };
#if SERVER
                        GameMain.Server.KarmaManager.OnCharacterHealthChanged(targetCharacter, user, prevVitality - targetCharacter.Vitality, 0.0f);
#endif
                    }
                }

                if (aiTriggers.Any())
                {
                    Character targetCharacter = target as Character;
                    if (targetCharacter == null)
                    {
                        if (target is Limb targetLimb && !targetLimb.Removed)
                        {
                            targetCharacter = targetLimb.character;
                        }
                    }
                    if (targetCharacter != null && !targetCharacter.Removed && !targetCharacter.IsPlayer)
                    {
                        if (targetCharacter.AIController is EnemyAIController enemyAI)
                        {
                            foreach (AITrigger trigger in aiTriggers)
                            {
                                if (Rand.Value(Rand.RandSync.Unsynced) > trigger.Probability) { continue; }
                                if (target is Limb targetLimb && targetCharacter.LastDamage.HitLimb != targetLimb) { continue; }
                                if (targetCharacter.LastDamage.Damage < trigger.MinDamage) { continue; }
                                enemyAI.LaunchTrigger(trigger);
                                break;
                            }
                        }
                    }
                }
            }

            if (FireSize > 0.0f && entity != null)
            {
                var fire = new FireSource(position, hull);
                fire.Size = new Vector2(FireSize, fire.Size.Y);
            }

            bool isNotClient = GameMain.NetworkMember == null || !GameMain.NetworkMember.IsClient;
            if (isNotClient && GameMain.GameSession?.EventManager is { } eventManager)
            {
                foreach (EventPrefab eventPrefab in triggeredEvents)
                {
                    Event ev = eventPrefab.CreateInstance();
                    if (ev == null) { continue; }
                    eventManager.QueuedEvents.Enqueue(ev);

                    if (ev is ScriptedEvent scriptedEvent)
                    {
                        if (!string.IsNullOrWhiteSpace(triggeredEventTargetTag))
                        {
                            List<Entity> eventTargets = targets.Where(t => t is Entity).Cast<Entity>().ToList();

                            if (eventTargets.Any())
                            {
                                scriptedEvent.Targets.Add(triggeredEventTargetTag, eventTargets);
                            }
                        }

                        if (!string.IsNullOrWhiteSpace(triggeredEventEntityTag) && entity != null)
                        {
                            scriptedEvent.Targets.Add(triggeredEventEntityTag, new List<Entity> { entity });
                        }
                    }
                }
            }

            if (isNotClient && entity != null && Entity.Spawner != null) //clients are not allowed to spawn entities
            {
                foreach (CharacterSpawnInfo characterSpawnInfo in spawnCharacters)
                {
                    var characters = new List<Character>();
                    for (int i = 0; i < characterSpawnInfo.Count; i++)
                    {
                        Entity.Spawner.AddToSpawnQueue(characterSpawnInfo.SpeciesName, position + Rand.Vector(characterSpawnInfo.Spread, Rand.RandSync.Server) + characterSpawnInfo.Offset, 
                            onSpawn: newCharacter =>
                            {
                                if (newCharacter.AIController is EnemyAIController enemyAi &&
                                    enemyAi.PetBehavior != null &&
                                    entity is Item item &&
                                    item.ParentInventory is CharacterInventory inv)
                                {
                                    enemyAi.PetBehavior.Owner = inv.Owner as Character;
                                }
                                characters.Add(newCharacter);
                                if (characters.Count == characterSpawnInfo.Count)
                                {
                                    SwarmBehavior.CreateSwarm(characters.Cast<AICharacter>());
                                }
                            });
                    }
                }
                foreach (ItemSpawnInfo itemSpawnInfo in spawnItems)
                {
                    for (int i = 0; i < itemSpawnInfo.Count; i++)
                    {
                        switch (itemSpawnInfo.SpawnPosition)
                        {
                            case ItemSpawnInfo.SpawnPositionType.This:
                                Entity.Spawner.AddToSpawnQueue(itemSpawnInfo.ItemPrefab, position + Rand.Vector(itemSpawnInfo.Spread, Rand.RandSync.Server), onSpawned: newItem =>
                                {
                                    Projectile projectile = newItem.GetComponent<Projectile>();
                                    if (projectile != null && user != null && sourceBody != null && entity != null)
                                    {
                                        var rope = newItem.GetComponent<Rope>();
                                        if (rope != null && sourceBody.UserData is Limb sourceLimb)
                                        {
                                            rope.Attach(sourceLimb, newItem);
                                        }

                                        float spread = MathHelper.ToRadians(Rand.Range(-itemSpawnInfo.AimSpread, itemSpawnInfo.AimSpread));
                                        var worldPos = sourceBody.Position;
                                        float rotation = itemSpawnInfo.Rotation;
                                        if (user.Submarine != null)
                                        {
                                            worldPos += user.Submarine.Position;
                                        }
                                        switch (itemSpawnInfo.RotationType)
                                        {
                                            case ItemSpawnInfo.SpawnRotationType.Fixed:
                                                rotation = sourceBody.TransformRotation(itemSpawnInfo.Rotation);
                                                break;
                                            case ItemSpawnInfo.SpawnRotationType.Target:
                                                rotation = MathUtils.VectorToAngle(entity.WorldPosition - worldPos);
                                                break;
                                            case ItemSpawnInfo.SpawnRotationType.Limb:
                                                rotation = sourceBody.TransformedRotation;
                                                break;
                                            case ItemSpawnInfo.SpawnRotationType.Collider:
                                                rotation = user.AnimController.Collider.Rotation;
                                                break;
                                            case ItemSpawnInfo.SpawnRotationType.MainLimb:
                                                rotation = user.AnimController.MainLimb.body.TransformedRotation;
                                                break;
                                            default:
                                                throw new NotImplementedException("Not implemented: " + itemSpawnInfo.RotationType);
                                        }
                                        rotation += MathHelper.ToRadians(itemSpawnInfo.Rotation * user.AnimController.Dir);
                                        projectile.Shoot(user, ConvertUnits.ToSimUnits(worldPos), ConvertUnits.ToSimUnits(worldPos), rotation + spread, ignoredBodies: user.AnimController.Limbs.Where(l => !l.IsSevered).Select(l => l.body.FarseerBody).ToList(), createNetworkEvent: true);
                                    }
                                    else
                                    {
                                        newItem.body?.ApplyLinearImpulse(Rand.Vector(1) * itemSpawnInfo.Speed);
                                        newItem.Rotation = itemSpawnInfo.Rotation;
                                    }
                                });
                                break;
                            case ItemSpawnInfo.SpawnPositionType.ThisInventory:
                                {
                                    Inventory inventory = null;
                                    if (entity is Character character && character.Inventory != null)
                                    {
                                        inventory = character.Inventory;
                                    }
                                    else if (entity is Item item)
                                    {
                                        inventory = item?.GetComponent<ItemContainer>()?.Inventory;
                                    }
                                    if (inventory != null && inventory.CanBePut(itemSpawnInfo.ItemPrefab))
                                    {
                                        Entity.Spawner.AddToSpawnQueue(itemSpawnInfo.ItemPrefab, inventory, spawnIfInventoryFull: false);                                        
                                    }
                                }
                                break;
                            case ItemSpawnInfo.SpawnPositionType.ContainedInventory:
                                {
                                    Inventory thisInventory = null;
                                    if (entity is Character character)
                                    {
                                        thisInventory = character.Inventory;
                                    }
                                    else if (entity is Item item)
                                    {
                                        thisInventory = item?.GetComponent<ItemContainer>()?.Inventory;
                                    }
                                    if (thisInventory != null)
                                    {
                                        foreach (Item item in thisInventory.AllItems)
                                        {
                                            Inventory containedInventory = item.GetComponent<ItemContainer>()?.Inventory;
                                            if (containedInventory != null && containedInventory.CanBePut(itemSpawnInfo.ItemPrefab))
                                            {
                                                Entity.Spawner.AddToSpawnQueue(itemSpawnInfo.ItemPrefab, containedInventory, spawnIfInventoryFull: false);
                                            }
                                            break;
                                        }
                                    }
                                }
                                break;
                        }
                    }
                }
            }

            ApplyProjSpecific(deltaTime, entity, targets, hull, position, playSound: true);
        }

        partial void ApplyProjSpecific(float deltaTime, Entity entity, IEnumerable<ISerializableEntity> targets, Hull currentHull, Vector2 worldPosition, bool playSound);

        private void ApplyToProperty(ISerializableEntity target, SerializableProperty property, object value, float deltaTime)
        {
            if (disableDeltaTime || setValue) { deltaTime = 1.0f; }
            Type type = value.GetType();
            if (type == typeof(float) || (type == typeof(int) && property.GetValue(target) is float))
            {
                float floatValue = Convert.ToSingle(value) * deltaTime;
                if (!setValue)
                {
                    floatValue += (float)property.GetValue(target);
                }
                property.TrySetValue(target, floatValue);
            }
            else if (type == typeof(int) && value is int)
            {
                int intValue = (int)((int)value * deltaTime);
                if (!setValue)
                {
                    intValue += (int)property.GetValue(target);
                }
                property.TrySetValue(target, intValue);
            }
            else if (type == typeof(bool) && value is bool)
            {
                property.TrySetValue(target, (bool)value);
            }
            else if (type == typeof(string))
            {
                property.TrySetValue(target, (string)value);
            }
            else
            {
                DebugConsole.ThrowError("Couldn't apply value " + value.ToString() + " (" + type + ") to property \"" + property.Name + "\" (" + property.GetValue(target).GetType() + ")! "
                    + "Make sure the type of the value set in the config files matches the type of the property.");
            }
        }

        public static void UpdateAll(float deltaTime)
        {
            UpdateAllProjSpecific(deltaTime);

            DelayedEffect.Update(deltaTime);
            for (int i = DurationList.Count - 1; i >= 0; i--)
            {
                DurationListElement element = DurationList[i];

                if (element.Parent.CheckConditionalAlways && !element.Parent.HasRequiredConditions(element.Targets))
                {
                    DurationList.RemoveAt(i);
                    continue;
                }

                element.Targets.RemoveAll(t =>
                    (t is Entity entity && entity.Removed) ||
                    (t is Limb limb && (limb.character == null || limb.character.Removed)));
                if (element.Targets.Count == 0)
                {
                    DurationList.RemoveAt(i);
                    continue;
                }

                foreach (ISerializableEntity target in element.Targets)
                {
                    for (int n = 0; n < element.Parent.propertyNames.Length; n++)
                    {
                        if (target == null ||
                            target.SerializableProperties == null ||
                            !target.SerializableProperties.TryGetValue(element.Parent.propertyNames[n], out SerializableProperty property))
                        {
                            continue;
                        }
                        element.Parent.ApplyToProperty(target, property, element.Parent.propertyEffects[n], CoroutineManager.UnscaledDeltaTime);
                    }

                    foreach (Affliction affliction in element.Parent.Afflictions)
                    {
                        Affliction multipliedAffliction = affliction;
                        if (!element.Parent.disableDeltaTime && !element.Parent.setValue) { multipliedAffliction = affliction.CreateMultiplied(deltaTime); }

                        if (target is Character character)
                        {
                            if (character.Removed) { continue; }
                            character.AddDamage(character.WorldPosition, multipliedAffliction.ToEnumerable(), stun: 0.0f, playSound: false, attacker: element.User);
                        }
                        else if (target is Limb limb)
                        {
                            if (limb.character.Removed || limb.Removed) { continue; }
                            limb.character.DamageLimb(limb.WorldPosition, limb, multipliedAffliction.ToEnumerable(), stun: 0.0f, playSound: false, attackImpulse: 0.0f, attacker: element.User);
                        }
                    }

                    foreach (Pair<string, float> reduceAffliction in element.Parent.ReduceAffliction)
                    {
                        Limb targetLimb = null;
                        Character targetCharacter = null;
                        if (target is Character character)
                        {
                            targetCharacter = character;
                        }
                        else if (target is Limb limb)
                        {
                            targetLimb = limb;
                            targetCharacter = limb.character;
                        }
                        if (targetCharacter != null && !targetCharacter.Removed)
                        {
                            float prevVitality = targetCharacter.Vitality;
                            targetCharacter.CharacterHealth.ReduceAffliction(targetLimb, reduceAffliction.First, reduceAffliction.Second * deltaTime);
                            if (element.User != null && element.User != targetCharacter)
                            {
                                if (!targetCharacter.IsDead)
                                {
                                    targetCharacter.TryAdjustAttackerSkill(element.User, targetCharacter.Vitality - prevVitality);
                                }
                            };
#if SERVER
                            GameMain.Server.KarmaManager.OnCharacterHealthChanged(targetCharacter, element.User, prevVitality - targetCharacter.Vitality, 0.0f);
#endif
                        }
                    }
                }

                element.Parent.ApplyProjSpecific(deltaTime, 
                    element.Entity, 
                    element.Targets, 
                    element.Parent.GetHull(element.Entity), 
                    element.Parent.GetPosition(element.Entity, element.Targets),
                    playSound: element.Timer >= element.Duration);

                element.Timer -= deltaTime;

                if (element.Timer > 0.0f) { continue; }
                DurationList.Remove(element);
            }
        }

        static partial void UpdateAllProjSpecific(float deltaTime);

        public static void StopAll()
        {
            CoroutineManager.StopCoroutines("statuseffect");
            DelayedEffect.DelayList.Clear();
            DurationList.Clear();
        }

        public void AddTag(string tag)
        {
            if (tags.Contains(tag)) { return; }
            tags.Add(tag);
        }

        public bool HasTag(string tag)
        {
            if (tag == null) { return true; }

            return (tags.Contains(tag) || tags.Contains(tag.ToLowerInvariant()));
        }
    }
}

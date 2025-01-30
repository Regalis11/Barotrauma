using Microsoft.Xna.Framework;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Xml.Linq;
using Barotrauma.Extensions;
using Barotrauma.IO;
using Barotrauma.Networking;
#if CLIENT
using Microsoft.Xna.Framework.Graphics;
using Barotrauma.Sounds;
#endif

namespace Barotrauma.Items.Components
{
    interface IDrawableComponent
    {
#if CLIENT
        /// <summary>
        /// The extents of the sprites or other graphics this component needs to draw. Used to determine which items are visible on the screen.
        /// </summary>
        Vector2 DrawSize { get; }

        void Draw(SpriteBatch spriteBatch, bool editing, float itemDepth = -1, Color? overrideColor = null);
#endif
    }

    /// <summary>
    /// The base class for components holding the different functionalities of the item
    /// </summary>
    partial class ItemComponent : ISerializableEntity
    {
        protected Item item;

        protected string name;

        private bool isActive;

        protected bool characterUsable;

        protected bool canBePicked;
        protected bool canBeSelected;
        protected bool canBeCombined;
        protected bool removeOnCombined;

        public bool WasUsed, WasSecondaryUsed;

        public readonly Dictionary<ActionType, List<StatusEffect>> statusEffectLists;

        public Dictionary<RelatedItem.RelationType, List<RelatedItem>> RequiredItems;
        public readonly List<RelatedItem> DisabledRequiredItems = new List<RelatedItem>();

        public readonly List<Skill> RequiredSkills = new List<Skill>();

        private ItemComponent parent;
        public ItemComponent Parent
        {
            get { return parent; }
            set
            {
                if (parent == value) { return; }
                if (InheritParentIsActive)
                {
                    if (parent != null) { parent.OnActiveStateChanged -= SetActiveState; }
                    if (value != null) { value.OnActiveStateChanged += SetActiveState; }
                }
                parent = value;
            }
        }


        [Serialize(true, IsPropertySaveable.No, description: "If this is a child component of another component, should this component inherit the IsActive state of the parent?")]
        public bool InheritParentIsActive { get; set; }

        public readonly ContentXElement originalElement;

        /// <summary>
        /// The default delay for delayed client-side corrections (see <see cref="StartDelayedCorrection"/>.
        /// </summary>
        protected const float CorrectionDelay = 1.0f;
        protected CoroutineHandle delayedCorrectionCoroutine;

        /// <summary>
        /// If enabled, the contents of the item are not transferred when the player transfers items between subs.
        /// Use this if this component uses item containers in a way where removing the item from the container via external means would cause problems.
        /// </summary>
        public virtual bool DontTransferInventoryBetweenSubs => false;

        /// <summary>
        /// If enabled, the items inside any of the item containers on this item cannot be sold at an outpost.
        /// Use in similar cases as <see cref="DontTransferInventoryBetweenSubs"/>.
        /// </summary>
        public virtual bool DisallowSellingItemsFromContainer => false;

        [Editable, Serialize(0.0f, IsPropertySaveable.No, description: "How long it takes to pick up the item (in seconds).")]
        public float PickingTime
        {
            get;
            set;
        }

        [Serialize("", IsPropertySaveable.No, description: "What to display on the progress bar when this item is being picked.")]
        public string PickingMsg
        {
            get;
            set;
        }

        public Dictionary<Identifier, SerializableProperty> SerializableProperties { get; protected set; }

        public Action<bool> OnActiveStateChanged;

        public virtual bool IsActive
        {
            get { return isActive; }
            set
            {
#if CLIENT
                if (!value)
                {
                    IsActiveTimer = 0.0f;
                    if (isActive)
                    {
                        StopSounds(ActionType.OnActive);
                    }
                }
#endif
                if (value != IsActive) { OnActiveStateChanged?.Invoke(value); }
                isActive = value;
            }
        }

        private bool drawable = true;

        [Serialize(PropertyConditional.LogicalOperatorType.And, IsPropertySaveable.No)]
        public PropertyConditional.LogicalOperatorType IsActiveConditionalComparison
        {
            get;
            set;
        }

        public List<PropertyConditional> IsActiveConditionals;

        public bool Drawable
        {
            get { return drawable; }
            set
            {
                if (value == drawable) { return; }
                if (this is not IDrawableComponent)
                {
                    DebugConsole.ThrowError("Couldn't make \"" + this + "\" drawable (the component doesn't implement the IDrawableComponent interface)");
                    return;
                }

                drawable = value;
                if (drawable)
                {
                    item.EnableDrawableComponent((IDrawableComponent)this);
                }
                else
                {
                    item.DisableDrawableComponent((IDrawableComponent)this);
                }
            }
        }

        [Editable, Serialize(false, IsPropertySaveable.No, description: "Can the item be picked up (or interacted with, if the pick action does something else than picking up the item).")] //Editable for doors to do their magic
        public bool CanBePicked
        {
            get { return canBePicked; }
            set { canBePicked = value; }
        }

        [Serialize(false, IsPropertySaveable.No, description: "Should the interface of the item (if it has one) be drawn when the item is equipped.")]
        public bool DrawHudWhenEquipped
        {
            get;
            protected set;
        }

        [Serialize(false, IsPropertySaveable.Yes), ConditionallyEditable(ConditionallyEditable.ConditionType.OnlyByStatusEffectsAndNetwork, onlyInEditors: false)]
        public bool LockGuiFramePosition { get; set; }

        [Serialize("0,0", IsPropertySaveable.Yes), ConditionallyEditable(ConditionallyEditable.ConditionType.OnlyByStatusEffectsAndNetwork, onlyInEditors: false)]
        public Point GuiFrameOffset { get; set; }

        [Serialize(false, IsPropertySaveable.No, description: "Can the item be selected by interacting with it.")]
        public bool CanBeSelected
        {
            get { return canBeSelected; }
            set { canBeSelected = value; }
        }

        [Serialize(false, IsPropertySaveable.No, description: "Can the item be combined with other items of the same type.")]
        public bool CanBeCombined
        {
            get { return canBeCombined; }
            set { canBeCombined = value; }
        }

        [Serialize(false, IsPropertySaveable.No, description: "Should the item be removed if combining it with an other item causes the condition of this item to drop to 0.")]
        public bool RemoveOnCombined
        {
            get { return removeOnCombined; }
            set { removeOnCombined = value; }
        }

        [Serialize(false, IsPropertySaveable.No, description: "Can the \"Use\" action of the item be triggered by characters or just other items/StatusEffects.")]
        public bool CharacterUsable
        {
            get { return characterUsable; }
            set { characterUsable = value; }
        }

        //Remove item if combination results in 0 condition
        [Serialize(true, IsPropertySaveable.No, description: "Can the properties of the component be edited in-game (only applicable if the component has in-game editable properties)."), Editable()]
        public bool AllowInGameEditing
        {
            get;
            set;
        }

        public InputType PickKey
        {
            get;
            protected set;
        }

        public InputType SelectKey
        {
            get;
            protected set;
        }

        [Serialize(false, IsPropertySaveable.No, description: "Should the item be deleted when it's used.")]
        public bool DeleteOnUse
        {
            get;
            set;
        }

        public Item Item
        {
            get { return item; }
        }

        public string Name
        {
            get { return name; }
        }

        [Editable, Serialize("", IsPropertySaveable.Yes, translationTextTag: "ItemMsg", description: "A text displayed next to the item when it's highlighted (generally instructs how to interact with the item, e.g. \"[Mouse1] Pick up\").")]
        public string Msg
        {
            get;
            set;
        }

        [Serialize(false, IsPropertySaveable.Yes, description: "If set to true, the component's message will also be displayed while in the controlled character's inventory.")]
        public bool ShowMsgInOwnInventory
        {
            get;
            set;
        }

        [Serialize(false, IsPropertySaveable.Yes, description: "If set to true, the component's message will also be displayed while in an inventory not owned by the controlled character.")]
        public bool ShowMsgInOtherInventory
        {
            get;
            set;
        }

        public LocalizedString DisplayMsg
        {
            get;
            set;
        }

        [Serialize(0f, IsPropertySaveable.No, description: "How useful the item is in combat? Used by AI to decide which item it should use as a weapon. For the sake of clarity, use a value between 0 and 100 (not forced). Note that there's also a generic BotPriority for all item prefabs.")]
        public float CombatPriority { get; private set; }

        /// <summary>
        /// Which sound should be played when manual sound selection type is selected? Not [Editable] because we don't want this visible in the editor for every component.
        /// </summary>
        [Serialize(0, IsPropertySaveable.Yes, alwaysUseInstanceValues: true)]
        public int ManuallySelectedSound { get; private set; }

        /// <summary>
        /// Can be used by status effects or conditionals to the speed of the item
        /// </summary>
        public float Speed => item.Speed;

        public readonly record struct ItemUseInfo(Item Item, Character User);
        public readonly NamedEvent<ItemUseInfo> OnUsed = new();

        public readonly bool InheritStatusEffects;

        public ItemComponent(Item item, ContentXElement element)
        {
            this.item = item;
            originalElement = element;
            name = element.Name.ToString();
            SerializableProperties = SerializableProperty.GetProperties(this);
            RequiredItems = new Dictionary<RelatedItem.RelationType, List<RelatedItem>>();
#if CLIENT
            hasSoundsOfType = new bool[Enum.GetValues(typeof(ActionType)).Length];
            sounds = new Dictionary<ActionType, List<ItemSound>>();
#endif

            SelectKey = InputType.Select;

            try
            {
                string selectKeyStr = element.GetAttributeString("selectkey", "Select");
                selectKeyStr = ToolBox.ConvertInputType(selectKeyStr);
                SelectKey = (InputType)Enum.Parse(typeof(InputType), selectKeyStr, true);
            }
            catch (Exception e)
            {
                DebugConsole.ThrowError("Invalid select key in " + element + "!", e,
                    contentPackage: element.ContentPackage);
            }

            PickKey = InputType.Select;

            try
            {
                string pickKeyStr = element.GetAttributeString("pickkey", "Select");
                pickKeyStr = ToolBox.ConvertInputType(pickKeyStr);
                PickKey = (InputType)Enum.Parse(typeof(InputType), pickKeyStr, true);
            }
            catch (Exception e)
            {
                DebugConsole.ThrowError("Invalid pick key in " + element + "!", e,
                    contentPackage: element.ContentPackage);
            }

            SerializableProperties = SerializableProperty.DeserializeProperties(this, element);
            ParseMsg();

            string inheritRequiredSkillsFrom = element.GetAttributeString("inheritrequiredskillsfrom", "");
            if (!string.IsNullOrEmpty(inheritRequiredSkillsFrom))
            {
                var component = item.Components.Find(ic => ic.Name.Equals(inheritRequiredSkillsFrom, StringComparison.OrdinalIgnoreCase));
                if (component == null)
                {
                    DebugConsole.ThrowError($"Error in item \"{item.Name}\" - component \"{name}\" is set to inherit its required skills from \"{inheritRequiredSkillsFrom}\", but a component of that type couldn't be found.",
                        contentPackage: element.ContentPackage);
                }
                else
                {
                    RequiredSkills = component.RequiredSkills;
                }
            }

            string inheritStatusEffectsFrom = element.GetAttributeString("inheritstatuseffectsfrom", "");
            if (!string.IsNullOrEmpty(inheritStatusEffectsFrom))
            {
                InheritStatusEffects = true;
                var component = item.Components.Find(ic => ic.Name.Equals(inheritStatusEffectsFrom, StringComparison.OrdinalIgnoreCase));
                if (component == null)
                {
                    DebugConsole.ThrowError($"Error in item \"{item.Name}\" - component \"{name}\" is set to inherit its StatusEffects from \"{inheritStatusEffectsFrom}\", but a component of that type couldn't be found.", 
                        contentPackage: element.ContentPackage);
                }
                else if (component.statusEffectLists != null)
                {
                    statusEffectLists ??= new Dictionary<ActionType, List<StatusEffect>>();
                    foreach (KeyValuePair<ActionType, List<StatusEffect>> kvp in component.statusEffectLists)
                    {
                        if (!statusEffectLists.TryGetValue(kvp.Key, out List<StatusEffect> effectList))
                        {
                            effectList = new List<StatusEffect>();
                            statusEffectLists.Add(kvp.Key, effectList);
                        }
                        effectList.AddRange(kvp.Value);
                    }
                }
            }

            foreach (var subElement in element.Elements())
            {
                switch (subElement.Name.ToString().ToLowerInvariant())
                {
                    case "activeconditional":
                    case "isactiveconditional":
                    case "isactive":
                        IsActiveConditionals ??= new List<PropertyConditional>();
                        IsActiveConditionals.AddRange(PropertyConditional.FromXElement(subElement));
                        break;
                    case "requireditem":
                    case "requireditems":
                        SetRequiredItems(subElement);
                        break;
                    case "requiredskill":
                    case "requiredskills":
                        if (subElement.GetAttribute("name") != null)
                        {
                            DebugConsole.ThrowError("Error in item config \"" + item.ConfigFilePath + "\" - skill requirement in component " + GetType().ToString() + " should use a skill identifier instead of the name of the skill.",
                                contentPackage: element.ContentPackage);
                            continue;
                        }

                        Identifier skillIdentifier = subElement.GetAttributeIdentifier("identifier", "");
                        RequiredSkills.Add(new Skill(skillIdentifier, subElement.GetAttributeInt("level", 0)));
                        break;
                    case "statuseffect":
                        statusEffectLists ??= new Dictionary<ActionType, List<StatusEffect>>();
                        LoadStatusEffect(subElement);
                        break;
                    default:
                        if (LoadElemProjSpecific(subElement)) { break; }
                        ItemComponent ic = Load(subElement, item, false);
                        if (ic == null) { break; }

                        ic.Parent = this;
                        if (ic.InheritParentIsActive)
                        {
                            ic.IsActive = isActive;
                            OnActiveStateChanged += ic.SetActiveState;
                        }

                        item.AddComponent(ic);
                        break;
                }
            }

            void LoadStatusEffect(ContentXElement subElement)
            {
                var statusEffect = StatusEffect.Load(subElement, item.Name + ", " + GetType().Name);
                if (!statusEffectLists.TryGetValue(statusEffect.type, out List<StatusEffect> effectList))
                {
                    effectList = new List<StatusEffect>();
                    statusEffectLists.Add(statusEffect.type, effectList);
                }
                effectList.Add(statusEffect);
            }
        }

        private void SetActiveState(bool isActive)
        {
            IsActive = isActive;
        }

        public void SetRequiredItems(ContentXElement element, bool allowEmpty = false)
        {
            bool returnEmpty = false;
#if CLIENT
            returnEmpty = Screen.Selected == GameMain.SubEditorScreen;
#endif
            RelatedItem ri = RelatedItem.Load(element, returnEmpty, item.Name);
            if (ri != null)
            {
                if (ri.Identifiers.Count == 0)
                {
                    DisabledRequiredItems.Add(ri);
                }
                else
                {
                    if (!RequiredItems.ContainsKey(ri.Type))
                    {
                        RequiredItems.Add(ri.Type, new List<RelatedItem>());
                    }
                    RequiredItems[ri.Type].Add(ri);
                }
            }
            else if (!allowEmpty)
            {
                DebugConsole.ThrowError("Error in item config \"" + item.ConfigFilePath + "\" - component " + GetType().ToString() + " requires an item with no identifiers.", 
                    contentPackage: element.ContentPackage);
            }
        }

        public virtual void Move(Vector2 amount, bool ignoreContacts = false) { }

        /// <summary>a Character has picked the item</summary>
        public virtual bool Pick(Character picker)
        {
            return false;
        }

        public virtual bool Select(Character character)
        {
            return CanBeSelected;
        }

        /// <summary>a Character has dropped the item</summary>
        public virtual void Drop(Character dropper, bool setTransform = true) { }

        /// <returns>true if the operation was completed</returns>
        public virtual bool CrewAIOperate(float deltaTime, Character character, AIObjectiveOperateItem objective)
        {
            return false;
        }

        public virtual bool UpdateWhenInactive => false;

        //called when isActive is true and condition > 0.0f
        public virtual void Update(float deltaTime, Camera cam) 
        {
            ApplyStatusEffects(ActionType.OnActive, deltaTime);
        }

        //called when isActive is true and condition == 0.0f
        public virtual void UpdateBroken(float deltaTime, Camera cam)
        {
#if CLIENT
            StopSounds(ActionType.OnActive);
#endif
        }

        //called when the item is equipped and the "use" key is pressed
        //returns true if the item was used succesfully (not out of ammo, reloading, etc)
        public virtual bool Use(float deltaTime, Character character = null)
        {
            return characterUsable || character == null;
        }

        //called when the item is equipped and the "aim" key is pressed or when the item is selected if it doesn't require aiming.
        public virtual bool SecondaryUse(float deltaTime, Character character = null)
        {
            return false;
        }

        //called when the item is placed in a "limbslot"
        public virtual void Equip(Character character) { }

        //called then the item is dropped or dragged out of a "limbslot"
        public virtual void Unequip(Character character) { }

        public virtual void ReceiveSignal(Signal signal, Connection connection)
        {
            switch (connection.Name)
            {
                case "activate":
                case "use":
                case "trigger_in":
                    if (signal.value != "0")
                    {
                        item.Use(1.0f, user: signal.sender);
                    }
                    break;
                case "toggle":
                    if (signal.value != "0")
                    {
                        IsActive = !isActive;
                    }
                    break;
                case "set_active":
                case "set_state":
                    IsActive = signal.value != "0";
                    break;
            }
        }

        public virtual bool Combine(Item item, Character user)
        {
            if (canBeCombined && this.item.Prefab == item.Prefab && 
                item.Condition > 0.0f && this.item.Condition > 0.0f &&
                !item.IsFullCondition && !this.item.IsFullCondition)
            {
                float transferAmount = Math.Min(item.Condition, this.item.MaxCondition - this.item.Condition);

                if (MathUtils.NearlyEqual(transferAmount, 0.0f)) { return false; }
                if (removeOnCombined)
                {
                    if (item.Condition - transferAmount <= 0.0f)
                    {
                        if (item.ParentInventory != null)
                        {
                            if (item.ParentInventory.Owner is Character owner && owner.HeldItems.Contains(item))
                            {
                                item.Unequip(owner);
                            }
                            item.ParentInventory.RemoveItem(item);
                        }
                        RemoveItem(item);
                    }
                    else
                    {
                        item.Condition -= transferAmount;
                    }
                    if (this.Item.Condition + transferAmount <= 0.0f)
                    {
                        if (this.Item.ParentInventory != null)
                        {
                            if (this.Item.ParentInventory.Owner is Character owner && owner.HeldItems.Contains(this.Item))
                            {
                                this.Item.Unequip(owner);
                            }
                            this.Item.ParentInventory.RemoveItem(this.Item);
                        }
                        RemoveItem(this.Item);
                    }
                    else
                    {
                        this.Item.Condition += transferAmount;
                    }
                    static void RemoveItem(Item item)
                    {
                        if (Screen.Selected is { IsEditor: true })
                        {
                            item?.Remove();
                        }
                        else
                        {
                            Entity.Spawner?.AddItemToRemoveQueue(item);
                        }
                    }
                }
                else
                {
                    this.Item.Condition += transferAmount;
                    item.Condition -= transferAmount;
                }
                return true;
            }
            return false;
        }

        public void Remove()
        {
#if CLIENT
            if (loopingSoundChannel != null)
            {
                loopingSoundChannel.Dispose();
                loopingSoundChannel = null;
            }

            //no need to Dispose these - SoundManager will do it when it when it needs a free channel and the sound has stopped playing 
            //disposing immediately on Remove will for example prevent explosives from playing a sound if the explosion removes the item
            /*foreach (SoundChannel channel in playingOneshotSoundChannels)
            {
                channel.Dispose();
            }*/

            if (GuiFrame != null)
            {
                GUI.RemoveFromUpdateList(GuiFrame, true);
                GuiFrame.RectTransform.Parent = null;
                GuiFrame = null;
            }
#endif

            if (delayedCorrectionCoroutine != null)
            {
                CoroutineManager.StopCoroutines(delayedCorrectionCoroutine);
                delayedCorrectionCoroutine = null;
            }

            RemoveComponentSpecific();
        }

        /// <summary>
        /// Remove the component so that it doesn't appear to exist in the game world (stop sounds, remove bodies etc)
        /// but don't reset anything that's required for cloning the item
        /// </summary>
        public void ShallowRemove()
        {
#if CLIENT
            if (loopingSoundChannel != null)
            {
                loopingSoundChannel.Dispose();
                loopingSoundChannel = null;
            }
#endif

            ShallowRemoveComponentSpecific();
        }

        protected virtual void ShallowRemoveComponentSpecific()
        {
            RemoveComponentSpecific();
        }


        protected virtual void RemoveComponentSpecific()
        {
#if CLIENT
            HUDOverlay?.Remove(); 
            HUDOverlay = null;
#endif
        }
        
        protected string GetTextureDirectory(ContentXElement subElement) => item.Prefab.GetTexturePath(subElement, item.Prefab.ParentPrefab);

        public bool HasRequiredSkills(Character character)
        {
            return HasRequiredSkills(character, out Skill temp);
        }

        public bool HasRequiredSkills(Character character, out Skill insufficientSkill)
        {
            foreach (Skill skill in RequiredSkills)
            {
                float characterLevel = character.GetSkillLevel(skill.Identifier);
                if (characterLevel < skill.Level * GetSkillMultiplier())
                {
                    insufficientSkill = skill;
                    return false;
                }
            }
            insufficientSkill = null;
            return true;
        }

        public virtual float GetSkillMultiplier() { return 1; }

        /// <summary>
        /// Returns 0.0f-1.0f based on how well the Character can use the itemcomponent
        /// </summary>
        /// <returns>0.5f if all the skills meet the skill requirements exactly, 1.0f if they're way above and 0.0f if way less</returns>
        public float DegreeOfSuccess(Character character)
        {
            return DegreeOfSuccess(character, RequiredSkills);
        }

        /// <summary>
        /// Returns 0.0f-1.0f based on how well the Character can use the itemcomponent
        /// </summary>
        /// <returns>0.5f if all the skills meet the skill requirements exactly, 1.0f if they're way above and 0.0f if way less</returns>
        public float DegreeOfSuccess(Character character, List<Skill> requiredSkills)
        {
            if (requiredSkills.Count == 0) return 1.0f;

            if (character == null)
            {
                string errorMsg = "ItemComponent.DegreeOfSuccess failed (character was null).\n" + Environment.StackTrace.CleanupStackTrace();
                DebugConsole.ThrowError(errorMsg);
                GameAnalyticsManager.AddErrorEventOnce("ItemComponent.DegreeOfSuccess:CharacterNull", GameAnalyticsManager.ErrorSeverity.Error, errorMsg);
                return 0.0f;
            }

            float skillSuccessSum = 0.0f;
            for (int i = 0; i < requiredSkills.Count; i++)
            {
                float characterLevel = character.GetSkillLevel(requiredSkills[i].Identifier);
                skillSuccessSum += (characterLevel - requiredSkills[i].Level);
            }
            float average = skillSuccessSum / requiredSkills.Count;

            return ((average + 100.0f) / 2.0f) / 100.0f;
        }

        public virtual void FlipX(bool relativeToSub) { }

        public virtual void FlipY(bool relativeToSub) { }

        /// <summary>
        /// Returns true if the item is lacking required contained items, or if there's nothing with a non-zero condition inside.
        /// </summary>
        public bool IsEmpty(Character user) => 
            !HasRequiredContainedItems(user, addMessage: false) ||
            (Item.OwnInventory != null && !Item.OwnInventory.AllItems.Any(i => i.Condition > 0));

        public bool HasRequiredContainedItems(Character user, bool addMessage, LocalizedString msg = null)
        {
            if (!RequiredItems.ContainsKey(RelatedItem.RelationType.Contained)) { return true; }
            if (item.OwnInventory == null) { return false; }

            foreach (RelatedItem ri in RequiredItems[RelatedItem.RelationType.Contained])
            {
                if (!ri.CheckRequirements(user, item))
                {
#if CLIENT
                    msg ??= ri.Msg;
                    if (addMessage && !msg.IsNullOrEmpty())
                    {
                        GUI.AddMessage(msg, Color.Red);
                    }
#endif
                    return false;
                }
            }

            return true;
        }

        /// <summary>
        /// Only checks if any of the Picked requirements are matched (used for checking id card(s)). Much simpler and a bit different than HasRequiredItems.
        /// </summary>
        public virtual bool HasAccess(Character character)
        {
            if (character.IsBot && item.IgnoreByAI(character)) { return false; }
            if (!item.IsInteractable(character)) { return false; }
            if (RequiredItems.Count == 0) { return true; }
            if (character.Inventory != null && RequiredItems.TryGetValue(RelatedItem.RelationType.Picked, out List<RelatedItem> relatedItems))
            {
                foreach (RelatedItem relatedItem in relatedItems)
                {
                    foreach (Item otherItem in character.Inventory.AllItems)
                    {
                        if (relatedItem.MatchesItem(otherItem))
                        {
                            if (otherItem.GetComponent<IdCard>() is IdCard idCard)
                            {
                                if (!CheckIdCardAccess(relatedItem, idCard))
                                {
                                    continue;
                                }
                            }
                            return true;
                        }
                    }
                }
            }
            return false;
        }

        /// <summary>
        /// Presumes that matching is already checked.
        /// </summary>
        private bool CheckIdCardAccess(RelatedItem relatedItem, IdCard idCard)
        {
            if (item.Submarine is { IsRespawnShuttle: false })
            {
                //id cards don't work in enemy subs (except on items that only require the default "idcard" tag)
                if (idCard.TeamID != CharacterTeamType.None && idCard.TeamID != item.Submarine.TeamID && relatedItem.Identifiers.Any(id => id != "idcard"))
                {
                    if (GameMain.GameSession?.GameMode is PvPMode)
                    {
                        if (item.Submarine.TeamID != CharacterTeamType.FriendlyNPC && item.Submarine.TeamID != CharacterTeamType.None)
                        {
                            // In PvP, always allow access also to FriendlyNPC and None -> restrict access only to the enemy sub.
                            {
                                return false;
                            }
                        }
                    }
                    else
                    {
                        return false;
                    }
                }
                else if (idCard.SubmarineSpecificID != 0 && item.Submarine.SubmarineSpecificIDTag != idCard.SubmarineSpecificID)
                {
                    return false;
                }
            }
            return true;
        }

        public virtual bool HasRequiredItems(Character character, bool addMessage, LocalizedString msg = null)
        {
            if (RequiredItems.None()) { return true; }
            if (character.Inventory == null) { return false; }
            bool hasRequiredItems = false;
            bool canContinue = true;
            if (RequiredItems.ContainsKey(RelatedItem.RelationType.Equipped))
            {
                foreach (RelatedItem ri in RequiredItems[RelatedItem.RelationType.Equipped])
                {
                    canContinue = CheckItems(ri, character.HeldItems);
                    if (!canContinue) { break; }
                }
            }
            if (canContinue)
            {
                if (RequiredItems.ContainsKey(RelatedItem.RelationType.Picked))
                {
                    foreach (RelatedItem ri in RequiredItems[RelatedItem.RelationType.Picked])
                    {
                        if (!CheckItems(ri, character.Inventory.AllItems)) { break; }
                    }
                }
            }

#if CLIENT
            if (!hasRequiredItems && addMessage && !msg.IsNullOrEmpty())
            {
                GUI.AddMessage(msg, Color.Red);
            }
#endif
            return hasRequiredItems;

            bool CheckItems(RelatedItem relatedItem, IEnumerable<Item> itemList)
            {
                bool Predicate(Item it) 
                {
                    if (it == null || it.Condition <= 0.0f || !relatedItem.MatchesItem(it)) { return false; }
                    if (it.GetComponent<IdCard>() is IdCard idCard)
                    {
                        if (!CheckIdCardAccess(relatedItem, idCard))
                        {
                            return false;
                        }
                    }
                    return true;
                };
                bool shouldBreak = false;
                bool inEditor = false;
#if CLIENT
                inEditor = Screen.Selected == GameMain.SubEditorScreen;
#endif
                if (relatedItem.IgnoreInEditor && inEditor)
                {
                    hasRequiredItems = true;
                }
                else if (relatedItem.IsOptional)
                {
                    if (!hasRequiredItems)
                    {
                        hasRequiredItems = itemList.Any(Predicate);
                    }
                }
                else
                {
                    if (itemList.Any(Predicate))
                    {
                        hasRequiredItems = !relatedItem.RequireEmpty;
                    }
                    else
                    {
                        hasRequiredItems = relatedItem.MatchOnEmpty || relatedItem.RequireEmpty;
                    }
                    if (!hasRequiredItems)
                    {
                        shouldBreak = true;
                    }
                }
                if (!hasRequiredItems)
                {
                    if (msg == null && !relatedItem.Msg.IsNullOrEmpty())
                    {
                        msg = relatedItem.Msg;
                    }
                }
                return !shouldBreak;
            }
        }

        public void ApplyStatusEffects(ActionType type, float deltaTime, Character character = null, Limb targetLimb = null, Entity useTarget = null, Character user = null, Vector2? worldPosition = null, float afflictionMultiplier = 1.0f)
        {
            if (statusEffectLists == null) { return; }

            if (!statusEffectLists.TryGetValue(type, out List<StatusEffect> statusEffects)) { return; }

            bool broken = item.Condition <= 0.0f;
            bool reducesCondition = false;
            foreach (StatusEffect effect in statusEffects)
            {
                if (broken && !effect.AllowWhenBroken && effect.type != ActionType.OnBroken) { continue; }
                if (user != null) { effect.SetUser(user); }
                effect.AfflictionMultiplier = afflictionMultiplier;
                var c = character;
                if (user != null && effect.HasTargetType(StatusEffect.TargetType.Character) && !effect.HasTargetType(StatusEffect.TargetType.UseTarget))
                {
                    // A bit hacky, but fixes MeleeWeapons targeting the use target instead of the attacker. Also applies to Projectiles and Throwables, or other callers that passes the user.
                    c = user;
                }
                item.ApplyStatusEffect(effect, type, deltaTime, c, targetLimb, useTarget, isNetworkEvent: false, checkCondition: false, worldPosition);
                effect.AfflictionMultiplier = 1.0f;
                reducesCondition |= effect.ReducesItemCondition();
            }
            //if any of the effects reduce the item's condition, set the user for OnBroken effects as well
            if (reducesCondition && user != null && type != ActionType.OnBroken)
            {
                foreach (ItemComponent ic in item.Components)
                {
                    if (ic.statusEffectLists == null || !ic.statusEffectLists.TryGetValue(ActionType.OnBroken, out List<StatusEffect> brokenEffects)) { continue; }
                    foreach (var brokenEffect in brokenEffects)
                    {
                        brokenEffect.SetUser(user);
                    }
                }
            }

#if CLIENT
            HintManager.OnStatusEffectApplied(this, type, character);
#endif
        }

        public virtual void Load(ContentXElement componentElement, bool usePrefabValues, IdRemap idRemap, bool isItemSwap)
        {
            if (componentElement != null) 
            { 
                foreach (XAttribute attribute in componentElement.Attributes())
                {
                    if (!SerializableProperties.TryGetValue(attribute.NameAsIdentifier(), out SerializableProperty property)) { continue; }
                    if (property.OverridePrefabValues || 
                        !usePrefabValues || 
                        (isItemSwap && property.GetAttribute<Editable>() is { TransferToSwappedItem: true }))
                    {
                        property.TrySetValue(this, attribute.Value);
                    }
                }
                ParseMsg();
                OverrideRequiredItems(componentElement);
            }
#if CLIENT
            if (GuiFrame != null)
            {
                GuiFrame.RectTransform.ScreenSpaceOffset = GuiFrameOffset;
                if (guiFrameDragHandle != null)
                { 
                    guiFrameDragHandle.Enabled = !LockGuiFramePosition;
                }
            }
#endif
            if (item.Submarine != null) { SerializableProperty.UpgradeGameVersion(this, originalElement, item.Submarine.Info.GameVersion); }
        }

        /// <summary>
        /// Called when all items have been loaded. Use to initialize connections between items.
        /// </summary>
        public virtual void OnMapLoaded() { }

        /// <summary>
        /// Called when all the components of the item have been loaded. Use to initialize connections between components and such.
        /// </summary>
        public virtual void OnItemLoaded() { }

        public virtual void OnScaleChanged() { }

        /// <summary>
        /// Called when the item has an ItemContainer and the contents inside of it changed.
        /// </summary>
        public virtual void OnInventoryChanged() { }

        public static ItemComponent Load(ContentXElement element, Item item, bool errorMessages = true)
        {
            Type type;
            Identifier typeName = element.NameAsIdentifier();
            try
            {
                // Get the type of a specified class.
                type = ReflectionUtils.GetDerivedNonAbstract<ItemComponent>().Append(typeof(ItemComponent)).FirstOrDefault(t => t.Name == typeName);
                if (type == null)
                {
                    if (errorMessages)
                    {
                        DebugConsole.ThrowError($"Could not find the component \"{typeName}\" ({item.Prefab.ContentFile.Path})", 
                            contentPackage: element.ContentPackage);
                    }
                    return null;
                }
            }
            catch (Exception e)
            {
                if (errorMessages)
                {
                    DebugConsole.ThrowError($"Could not find the component \"{typeName}\" ({item.Prefab.ContentFile.Path})", e,
                        contentPackage: element.ContentPackage);
                }
                return null;
            }

            ConstructorInfo constructor;
            try
            {
                if (type != typeof(ItemComponent) && !type.IsSubclassOf(typeof(ItemComponent))) { return null; }
                constructor = type.GetConstructor(new Type[] { typeof(Item), typeof(ContentXElement) });
                if (constructor == null)
                {
                    DebugConsole.ThrowError(
                        $"Could not find the constructor of the component \"{typeName}\" ({item.Prefab.ContentFile.Path})", 
                        contentPackage: element.ContentPackage);
                    return null;
                }
            }
            catch (Exception e)
            {
                DebugConsole.ThrowError(
                    $"Could not find the constructor of the component \"{typeName}\" ({item.Prefab.ContentFile.Path})", e,
                    contentPackage: element.ContentPackage);
                return null;
            }
            ItemComponent ic = null;
            try
            {
                object[] lobject = new object[] { item, element };
                object component = constructor.Invoke(lobject);
                ic = (ItemComponent)component;
                ic.name = element.Name.ToString();
            }
            catch (TargetInvocationException e)
            {
                DebugConsole.ThrowError($"Error while loading component of the type {type}.", e.InnerException, contentPackage: element.ContentPackage);
                GameAnalyticsManager.AddErrorEventOnce(
                    $"ItemComponent.Load:TargetInvocationException{item.Name}{element.Name}",
                    GameAnalyticsManager.ErrorSeverity.Error,
                    $"Error while loading entity of the type {type} ({e.InnerException})\n{Environment.StackTrace.CleanupStackTrace()}");
            }

            return ic;
        }

        public virtual XElement Save(XElement parentElement)
        {
            XElement componentElement = new XElement(name);

            foreach (var kvp in RequiredItems)
            {
                foreach (RelatedItem ri in kvp.Value)
                {
                    XElement newElement = new XElement("requireditem");
                    ri.Save(newElement);
                    componentElement.Add(newElement);
                }
            }
            foreach (RelatedItem ri in DisabledRequiredItems)
            {
                XElement newElement = new XElement("requireditem");
                ri.Save(newElement);
                componentElement.Add(newElement);
            }


            SerializableProperty.SerializeProperties(this, componentElement);

            parentElement.Add(componentElement);
            return componentElement;
        }

        public virtual void Reset()
        {
            SerializableProperties = SerializableProperty.DeserializeProperties(this, originalElement);
            if (this is Pickable) { canBePicked = true; }
            ParseMsg();
            OverrideRequiredItems(originalElement);
        }

        private void OverrideRequiredItems(ContentXElement element)
        {
            var prevRequiredItems = new Dictionary<RelatedItem.RelationType, List<RelatedItem>>(RequiredItems);
            RequiredItems.Clear();

            bool returnEmptyRequirements = false;
#if CLIENT
            returnEmptyRequirements = Screen.Selected == GameMain.SubEditorScreen;
#endif
            foreach (var subElement in element.Elements())
            {
                switch (subElement.Name.ToString().ToLowerInvariant())
                {
                    case "requireditem":
                    case "requireditems":
                        RelatedItem newRequiredItem = RelatedItem.Load(subElement, returnEmptyRequirements, item.Name);
                        if (newRequiredItem == null) continue;

                        var prevRequiredItem = prevRequiredItems.ContainsKey(newRequiredItem.Type) ?
                            prevRequiredItems[newRequiredItem.Type].Find(ri => ri.JoinedIdentifiers == newRequiredItem.JoinedIdentifiers) : null;
                        if (prevRequiredItem != null)
                        {
                            newRequiredItem.StatusEffects = prevRequiredItem.StatusEffects;
                            newRequiredItem.Msg = prevRequiredItem.Msg;
                            newRequiredItem.IsOptional = prevRequiredItem.IsOptional;
                            newRequiredItem.IgnoreInEditor = prevRequiredItem.IgnoreInEditor;
                        }

                        if (!RequiredItems.ContainsKey(newRequiredItem.Type))
                        {
                            RequiredItems[newRequiredItem.Type] = new List<RelatedItem>();
                        }
                        RequiredItems[newRequiredItem.Type].Add(newRequiredItem);
                        break;
                }
            }
        }

        public virtual void ParseMsg()
        {
            LocalizedString msg = TextManager.Get(Msg);
            if (msg.Loaded)
            {
                msg = TextManager.ParseInputTypes(msg);
                DisplayMsg = msg;
            }
            else
            {
                DisplayMsg = Msg;
            }
        }

        public interface IEventData { }

        public virtual bool ValidateEventData(NetEntityEvent.IData data)
            => true;
        
        protected T ExtractEventData<T>(NetEntityEvent.IData data) where T : IEventData
            => TryExtractEventData(data, out T componentData)
                ? componentData
                : throw new Exception($"Malformed item component state event for {item.Name} " +
                                      $"(item ID {item.ID}, component type {GetType().Name}): " +
                                      $"could not extract ComponentData of type {typeof(T).Name}");

        protected bool TryExtractEventData<T>(NetEntityEvent.IData data, out T componentData)
        {
            componentData = default;
            if (data is Item.ComponentStateEventData { ComponentData: T nestedData })
            {
                componentData = nestedData;
                return true;
            }

            return false;
        }
        
        #region AI related
        protected const float AIUpdateInterval = 0.2f;
        protected float aiUpdateTimer;

        protected AIObjectiveContainItem AIContainItems<T>(ItemContainer container, Character character, AIObjective currentObjective, int itemCount, bool equip, bool removeEmpty, bool spawnItemIfNotFound = false, bool dropItemOnDeselected = false) where T : ItemComponent
        {
            AIObjectiveContainItem containObjective = null;
            if (character.AIController is HumanAIController aiController)
            {
                containObjective = new AIObjectiveContainItem(character, container.ContainableItemIdentifiers, container, currentObjective.objectiveManager, spawnItemIfNotFound: spawnItemIfNotFound)
                {
                    ItemCount = itemCount,
                    Equip = equip,
                    RemoveEmpty = removeEmpty,
                    GetItemPriority = i =>
                    {
                        if (i.ParentInventory?.Owner is Item)
                        {
                            //don't take items from other items of the same type
                            if (((Item)i.ParentInventory.Owner).GetComponent<T>() != null)
                            {
                                return 0.0f;
                            }
                        }
                        // Prefer items with the same identifier as the contained items'
                        return container.ContainsItemsWithSameIdentifier(i) ? 1.0f : 0.5f;
                    }
                };
                containObjective.Abandoned += () => aiController.IgnoredItems.Add(container.Item);
                if (dropItemOnDeselected)
                {
                    currentObjective.Deselected += () =>
                    {
                        if (containObjective == null) { return; }
                        if (containObjective.IsCompleted) { return; }
                        Item item = containObjective.ItemToContain;
                        if (item != null && character.CanInteractWith(item, checkLinked: false))
                        {
                            item.Drop(character);
                        }
                    };
                }
                currentObjective.AddSubObjective(containObjective);
            }
            return containObjective;
        }
        #endregion
    }
}

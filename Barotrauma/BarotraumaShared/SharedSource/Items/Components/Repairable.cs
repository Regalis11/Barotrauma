using Barotrauma.Extensions;
using Barotrauma.Networking;
using Microsoft.Xna.Framework;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Xml.Linq;

namespace Barotrauma.Items.Components
{
    partial class Repairable : ItemComponent, IServerSerializable, IClientSerializable
    {
        private string header;

        private float deteriorationTimer;
        private float deteriorateAlwaysResetTimer;

        private float repairBoost;

        bool wasBroken;
        bool wasGoodCondition;

        public float LastActiveTime;

        [Serialize(0.0f, true, description: "How fast the condition of the item deteriorates per second."), Editable(MinValueFloat = 0.0f, MaxValueFloat = 100.0f, DecimalCount = 2)]
        public float DeteriorationSpeed
        {
            get;
            set;
        }

        [Serialize(0.0f, true, description: "Minimum initial delay before the item starts to deteriorate."), Editable(MinValueFloat = 0.0f, MaxValueFloat = 1000.0f, DecimalCount = 2)]
        public float MinDeteriorationDelay
        {
            get;
            set;
        }

        [Serialize(0.0f, true, description: "Maximum initial delay before the item starts to deteriorate."), Editable(MinValueFloat = 0.0f, MaxValueFloat = 1000.0f, DecimalCount = 2)]
        public float MaxDeteriorationDelay
        {
            get;
            set;
        }

        [Serialize(50.0f, true, description: "The item won't deteriorate spontaneously if the condition is below this value. For example, if set to 10, the condition will spontaneously drop to 10 and then stop dropping (unless the item is damaged further by external factors). Percentages of max condition."), Editable(MinValueFloat = 0.0f, MaxValueFloat = 100.0f)]
        public float MinDeteriorationCondition
        {
            get;
            set;
        }

        [Serialize(0f, true, description: "How low a traitor must get the item's condition for it to start breaking down.")]
        public float MinSabotageCondition
        {
            get;
            set;
        }

        [Serialize(80.0f, true, description: "The condition of the item has to be below this for it to become repairable. Percentages of max condition."), Editable(MinValueFloat = 0.0f, MaxValueFloat = 100.0f)]
        public float RepairThreshold
        {
            get;
            set;
        }

        [Serialize(100.0f, true, description: "The amount of time it takes to fix the item with insufficient skill levels."), Editable(MinValueFloat = 0.0f, MaxValueFloat = 100.0f)]
        public float FixDurationLowSkill
        {
            get;
            set;
        }

        [Serialize(10.0f, true, description: "The amount of time it takes to fix the item with sufficient skill levels."), Editable(MinValueFloat = 0.0f, MaxValueFloat = 100.0f)]
        public float FixDurationHighSkill
        {
            get;
            set;
        }

        [Serialize(false, false, description: "If set to true, the deterioration timer will always run regardless if the item is being used or not.")]
        public bool DeteriorateAlways
        {
            get;
            set;
        }

        private float skillRequirementMultiplier;
        
        [Serialize(1.0f, true)]
        public float SkillRequirementMultiplier 
        { 
            get { return skillRequirementMultiplier; }
            set
            {
                var oldValue = skillRequirementMultiplier;
                skillRequirementMultiplier = value;
#if CLIENT
                if (!MathUtils.NearlyEqual(oldValue, skillRequirementMultiplier))
                {
                    RecreateGUI();
                }
#endif
            } 
        }

        public float RepairIconThreshold
        {
            get { return RepairThreshold / 2; }
        }

        public Character CurrentFixer { get; private set; }

        public enum FixActions : int
        {
            None = 0,
            Repair = 1,
            Sabotage = 2
        }

        private FixActions currentFixerAction = FixActions.None;
        public FixActions CurrentFixerAction
        {
            get => currentFixerAction;
            private set { currentFixerAction = value; }
        }

        public Repairable(Item item, XElement element)
            : base(item, element)
        {
            IsActive = true;
            canBeSelected = true;

            this.item = item;
            header = 
                TextManager.Get(element.GetAttributeString("header", ""), returnNull: true) ??
                TextManager.Get(item.Prefab.ConfigElement.GetAttributeString("header", ""), returnNull: true) ??
                element.GetAttributeString("name", "");

            //backwards compatibility
            var repairThresholdAttribute = 
                element.Attributes().FirstOrDefault(a => a.Name.ToString().Equals("showrepairuithreshold", StringComparison.OrdinalIgnoreCase)) ??
                element.Attributes().FirstOrDefault(a => a.Name.ToString().Equals("airepairthreshold", StringComparison.OrdinalIgnoreCase));
            if (repairThresholdAttribute != null)
            {
                if (float.TryParse(repairThresholdAttribute.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out float repairThreshold))
                {
                    RepairThreshold = repairThreshold;
                }
            }

            InitProjSpecific(element);
        }

        public override void OnItemLoaded()
        {
            deteriorationTimer = Rand.Range(MinDeteriorationDelay, MaxDeteriorationDelay);
        }

        partial void InitProjSpecific(XElement element);

        /// <summary>
        /// Check if the character manages to succesfully repair the item
        /// </summary>
        public bool CheckCharacterSuccess(Character character)
        {
            if (character == null) { return false; }

            if (statusEffectLists == null || statusEffectLists.None(s => s.Key == ActionType.OnFailure)) { return true; }

            // unpowered (electrical) items can be repaired without a risk of electrical shock
            if (requiredSkills.Any(s => s != null && s.Identifier.Equals("electrical", StringComparison.OrdinalIgnoreCase)) &&
                item.GetComponent<Powered>() is Powered powered && powered.Voltage < 0.1f) { return true; }

            if (Rand.Range(0.0f, 0.5f) < RepairDegreeOfSuccess(character, requiredSkills)) { return true; }

            ApplyStatusEffects(ActionType.OnFailure, 1.0f, character);
            return false;
        }

        public override float GetSkillMultiplier()
        {
            return SkillRequirementMultiplier;
        }

        public float RepairDegreeOfSuccess(Character character, List<Skill> skills)
        {
            if (skills.Count == 0) return 1.0f;

            float skillSum = (from t in skills let characterLevel = character.GetSkillLevel(t.Identifier) select (characterLevel - (t.Level * SkillRequirementMultiplier))).Sum();
            float average = skillSum / skills.Count;

            return ((average + 100.0f) / 2.0f) / 100.0f;
        }

        public void RepairBoost(bool QTESuccess)
        {
            if (QTESuccess)
            {
                repairBoost = RepairDegreeOfSuccess(CurrentFixer, requiredSkills) * 3 * (currentFixerAction == FixActions.Repair ? 1.0f : -1.0f);
            }
            else
            {
                repairBoost = (1 - RepairDegreeOfSuccess(CurrentFixer, requiredSkills)) * 10 * (currentFixerAction == FixActions.Repair ? -1.0f : 1.0f);
            }
        }

        public bool StartRepairing(Character character, FixActions action)
        {
            if (character == null || character.IsDead || action == FixActions.None)
            {
                DebugConsole.ThrowError("Invalid repair command!");
                return false;
            }
            else
            {
#if SERVER
                if (CurrentFixer != character || currentFixerAction != action)
                {
                    if (!CheckCharacterSuccess(character))
                    {
                        GameServer.Log($"{GameServer.CharacterLogName(character)} failed to {(action == FixActions.Sabotage ? "sabotage" : "repair")} {item.Name}", ServerLog.MessageType.ItemInteraction);
                        GameMain.Server?.CreateEntityEvent(item, new object[] { NetEntityEvent.Type.ApplyStatusEffect, ActionType.OnFailure, this, character.ID });
                        return false;
                    }

                    GameServer.Log($"{GameServer.CharacterLogName(character)} started {(action == FixActions.Sabotage ? "sabotaging" : "repairing")} {item.Name}", ServerLog.MessageType.ItemInteraction);
                    item.CreateServerEvent(this);
                }
#else
                if (GameMain.Client == null && (CurrentFixer != character || currentFixerAction != action) && !CheckCharacterSuccess(character)) { return false; }
#endif
                CurrentFixer = character;
                CurrentFixerAction = action;
                return true;
            }
        }

        public bool StopRepairing(Character character)
        {
            if (CurrentFixer == character)
            {
#if SERVER
                if (CurrentFixer != character || currentFixerAction != FixActions.None)
                {
                    item.CreateServerEvent(this);
                }
#endif
                CurrentFixer.AnimController.Anim = AnimController.Animation.None;
                CurrentFixer = null;
                currentFixerAction = FixActions.None;
#if CLIENT
                repairSoundChannel?.FadeOutAndDispose();
                repairSoundChannel = null;                
#endif
                return true;
            }
            else
            {
                return false;
            }
        }

        public override void UpdateBroken(float deltaTime, Camera cam)
        {
            Update(deltaTime, cam);
        }

        public void ResetDeterioration()
        {
            deteriorationTimer = Rand.Range(MinDeteriorationDelay, MaxDeteriorationDelay);
            item.Condition = item.MaxCondition;
#if SERVER
            //let the clients know the deterioration delay
            item.CreateServerEvent(this);
#endif
        }

        public override void Update(float deltaTime, Camera cam)
        {
            UpdateProjSpecific(deltaTime);
            
            if (CurrentFixer == null)
            {
                if (deteriorateAlwaysResetTimer > 0.0f)
                {
                    deteriorateAlwaysResetTimer -= deltaTime;
                    if (deteriorateAlwaysResetTimer <= 0.0f)
                    {
                        DeteriorateAlways = false;
#if SERVER
                        //let the clients know the deterioration delay
                        item.CreateServerEvent(this);
#endif
                    }
                }
                if (!ShouldDeteriorate()) { return; }
                if (item.Condition > 0.0f)
                {
                    if (deteriorationTimer > 0.0f)
                    {
                        if (GameMain.NetworkMember == null || !GameMain.NetworkMember.IsClient)
                        {
                            deteriorationTimer -= deltaTime * GetDeteriorationDelayMultiplier();
#if SERVER
                            if (deteriorationTimer <= 0.0f) { item.CreateServerEvent(this); }
#endif
                        }
                        return;
                    }

                    if (item.ConditionPercentage > MinDeteriorationCondition)
                    {
                        item.Condition -= DeteriorationSpeed * deltaTime;
                    }
                }
                return;
            }

            UpdateFixAnimation(CurrentFixer);

            if (GameMain.NetworkMember != null && GameMain.NetworkMember.IsClient) { return; }

            if (CurrentFixer != null && (CurrentFixer.SelectedConstruction != item || !CurrentFixer.CanInteractWith(item) || CurrentFixer.IsDead))
            {
                StopRepairing(CurrentFixer);
                return;
            }

            float successFactor = requiredSkills.Count == 0 ? 1.0f : RepairDegreeOfSuccess(CurrentFixer, requiredSkills);

            //item must have been below the repair threshold for the player to get an achievement or XP for repairing it
            if (item.ConditionPercentage < RepairThreshold)
            {
                wasBroken = true;
            }
            if (item.ConditionPercentage > MinSabotageCondition)
            {
                wasGoodCondition = true;
            }

            if (repairBoost != 0.0f)
            {
                item.Condition += repairBoost;
                repairBoost = 0.0f;
            }

            float fixDuration = MathHelper.Lerp(FixDurationLowSkill, FixDurationHighSkill, successFactor);
            if (currentFixerAction == FixActions.Repair)
            {
                if (fixDuration <= 0.0f)
                {
                    item.Condition = item.MaxCondition;
                }
                else
                {
                    float conditionIncrease = deltaTime / (fixDuration / item.MaxCondition);
                    item.Condition += conditionIncrease;
#if SERVER
                    GameMain.Server.KarmaManager.OnItemRepaired(CurrentFixer, this, conditionIncrease);
#endif
                }

                if (item.IsFullCondition)
                {
                    if (wasBroken)
                    {
                        foreach (Skill skill in requiredSkills)
                        {
                            float characterSkillLevel = CurrentFixer.GetSkillLevel(skill.Identifier);
                            CurrentFixer.Info?.IncreaseSkillLevel(skill.Identifier,
                                SkillSettings.Current.SkillIncreasePerRepair / Math.Max(characterSkillLevel, 1.0f),
                                CurrentFixer.Position + Vector2.UnitY * 100.0f);
                        }
                        SteamAchievementManager.OnItemRepaired(item, CurrentFixer);
                    }
                    deteriorationTimer = Rand.Range(MinDeteriorationDelay, MaxDeteriorationDelay);
                    wasBroken = false;                    
                    StopRepairing(CurrentFixer);
                }
            }
            else if (currentFixerAction == FixActions.Sabotage)
            {
                if (fixDuration <= 0.0f)
                {
                    item.Condition = item.MaxCondition * (MinSabotageCondition / 100);
                }
                else
                {
                    float conditionDecrease = deltaTime / (fixDuration / item.MaxCondition);
                    item.Condition -= conditionDecrease;
                }

                if (item.ConditionPercentage <= MinSabotageCondition)
                {
                    if (wasGoodCondition)
                    {
                        foreach (Skill skill in requiredSkills)
                        {
                            float characterSkillLevel = CurrentFixer.GetSkillLevel(skill.Identifier);
                            CurrentFixer.Info?.IncreaseSkillLevel(skill.Identifier,
                                SkillSettings.Current.SkillIncreasePerSabotage / Math.Max(characterSkillLevel, 1.0f),
                                CurrentFixer.Position + Vector2.UnitY * 100.0f);
                        }

                        deteriorationTimer = 0.0f;
                        deteriorateAlwaysResetTimer = item.Condition / DeteriorationSpeed;
                        DeteriorateAlways = true;
                        item.Condition = item.MaxCondition * (MinSabotageCondition / 100);
                        wasGoodCondition = false;
                    }
                    StopRepairing(CurrentFixer);
                }
            }
            else
            {
                throw new NotImplementedException(currentFixerAction.ToString());
            }
        }

        partial void UpdateProjSpecific(float deltaTime);

        public void AdjustPowerConsumption(ref float powerConsumption)
        {
            if (item.ConditionPercentage < RepairThreshold)
            {
                powerConsumption *= MathHelper.Lerp(1.5f, 1.0f, item.Condition / item.MaxCondition);
            }
        }

        private bool ShouldDeteriorate()
        {
            if (Level.IsLoadedOutpost) { return false; }

            if (LastActiveTime > Timing.TotalTime) { return true; }
            foreach (ItemComponent ic in item.Components)
            {
                if (ic is Fabricator || ic is Deconstructor)
                {
                    //fabricators and deconstructors rely on LastActiveTime
                    return false;
                }
                else if (ic is PowerTransfer pt)
                {
                    //power transfer items (junction boxes, relays) don't deteriorate if they're no carrying any power 
                    if (Math.Abs(pt.CurrPowerConsumption) > 0.1f) { return true; }
                }
                else if (ic is Engine engine)
                {
                    //engines don't deteriorate if they're not running
                    if (Math.Abs(engine.Force) > 1.0f) { return true; }
                }
                else if (ic is Pump pump)
                {
                    //pumps don't deteriorate if they're not running
                    if (Math.Abs(pump.FlowPercentage) > 1.0f && pump.IsActive) { return true; }
                }
                else if (ic is Reactor reactor)
                {
                    //reactors don't deteriorate if they're not powered up
                    if (reactor.Temperature > 0.1f) { return true; }
                }
                else if (ic is OxygenGenerator oxyGenerator)
                {
                    //oxygen generators don't deteriorate if they're not running
                    if (oxyGenerator.CurrFlow > 0.1f) { return true; }
                }
                else if (ic is Powered powered && !(powered is LightComponent))
                {
                    if (powered.Voltage >= powered.MinVoltage) { return true; }
                }
            }

            return DeteriorateAlways;
        }

        private float GetDeteriorationDelayMultiplier()
        {
            foreach (ItemComponent ic in item.Components)
            {
                if (ic is Engine engine)
                {
                    return Math.Abs(engine.Force) / 100.0f;
                }
                else if (ic is Pump pump)
                {
                    return Math.Abs(pump.FlowPercentage) / 100.0f;
                }
                else if (ic is Reactor reactor)
                {
                    return (reactor.FissionRate + reactor.TurbineOutput) / 200.0f;
                }
            }
            return 1.0f;
        }

        private void UpdateFixAnimation(Character character)
        {
            if (character == null || character.IsDead || character.IsIncapacitated) { return; }
            character.AnimController.UpdateUseItem(false, item.WorldPosition + new Vector2(0.0f, 100.0f) * ((item.Condition / item.MaxCondition) % 0.1f));
        }

        public override void ReceiveSignal(Signal signal, Connection connection)
        {
            //do nothing
            //Repairables should always stay active, so we don't want to use the default behavior 
            //where set_active/set_state signals can disable the component
        }
    }
}

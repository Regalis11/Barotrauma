using Barotrauma.Networking;
using Microsoft.Xna.Framework;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Xml.Linq;
using Barotrauma.Extensions;
using System.Globalization;

namespace Barotrauma.Items.Components
{
    partial class Reactor : Powered, IServerSerializable, IClientSerializable
    {
        const float NetworkUpdateIntervalHigh = 0.5f;
        const float NetworkUpdateIntervalLow = 10.0f;

        //the rate at which the reactor is being run on (higher rate -> higher temperature)
        private float fissionRate;
        
        //how much of the generated steam is used to spin the turbines and generate power
        private float turbineOutput;
        
        private float temperature;
        
        //is automatic temperature control on
        //(adjusts the fission rate and turbine output automatically to keep the
        //amount of power generated balanced with the load)
        private bool autoTemp;
        
        //automatical adjustment to the power output when 
        //turbine output and temperature are in the optimal range
        private float autoAdjustAmount;
        
        private float fuelConsumptionRate;

        private float meltDownTimer, meltDownDelay;
        private float fireTimer, fireDelay;

        private float maxPowerOutput;

        private Queue<float> loadQueue = new Queue<float>();
        private float load;

        private float fuelLeft;
        
        private bool unsentChanges;
        private float sendUpdateTimer;

        private float degreeOfSuccess;

        private Vector2 optimalTemperature, allowedTemperature;
        private Vector2 optimalFissionRate, allowedFissionRate;
        private Vector2 optimalTurbineOutput, allowedTurbineOutput;

        private bool _powerOn;

        [Serialize(defaultValue: false, isSaveable: true)]
        public bool PowerOn
        {
            get { return _powerOn; }
            set
            {
                _powerOn = value;
#if CLIENT
                UpdateUIElementStates();
#endif
            }
        }

        public Character LastAIUser { get; private set; }

        [Serialize(defaultValue: false, isSaveable: true)]
        public bool LastUserWasPlayer { get; private set; }

        private Character lastUser;
        public Character LastUser
        {
            get { return lastUser; }
            private set
            {
                if (lastUser == value) { return; }
                lastUser = value;
                degreeOfSuccess = lastUser == null ? 0.0f : DegreeOfSuccess(lastUser);
                LastUserWasPlayer = lastUser.IsPlayer;
            }
        }
        
        [Editable(0.0f, float.MaxValue), Serialize(10000.0f, true, description: "How much power (kW) the reactor generates when operating at full capacity.", alwaysUseInstanceValues: true)]
        public float MaxPowerOutput
        {
            get { return maxPowerOutput; }
            set
            {
                maxPowerOutput = Math.Max(0.0f, value);
            }
        }
        
        [Editable(0.0f, float.MaxValue), Serialize(120.0f, true, description: "How long the temperature has to stay critical until a meltdown occurs.")]
        public float MeltdownDelay
        {
            get { return meltDownDelay; }
            set { meltDownDelay = Math.Max(value, 0.0f); }
        }

        [Editable(0.0f, float.MaxValue), Serialize(30.0f, true, description: "How long the temperature has to stay critical until the reactor catches fire.")]
        public float FireDelay
        {
            get { return fireDelay; }
            set { fireDelay = Math.Max(value, 0.0f); }
        }

        [Serialize(0.0f, true, description: "Current temperature of the reactor (0% - 100%). Indended to be used by StatusEffect conditionals.")]
        public float Temperature
        {
            get { return temperature; }
            set
            {
                if (!MathUtils.IsValid(value)) return;
                temperature = MathHelper.Clamp(value, 0.0f, 100.0f);
            }
        }

        [Serialize(0.0f, true, description: "Current fission rate of the reactor (0% - 100%). Intended to be used by StatusEffect conditionals (setting the value from XML is not recommended).")]
        public float FissionRate
        {
            get { return fissionRate; }
            set 
            {
                if (!MathUtils.IsValid(value)) return;
                fissionRate = MathHelper.Clamp(value, 0.0f, 100.0f); 
            }
        }

        [Serialize(0.0f, true, description: "Current turbine output of the reactor (0% - 100%). Intended to be used by StatusEffect conditionals (setting the value from XML is not recommended).")]
        public float TurbineOutput
        {
            get { return turbineOutput; }
            set 
            {
                if (!MathUtils.IsValid(value)) return;
                turbineOutput = MathHelper.Clamp(value, 0.0f, 100.0f); 
            }
        }
        
        [Serialize(0.2f, true, description: "How fast the condition of the contained fuel rods deteriorates per second."), Editable(0.0f, 1000.0f, decimals: 3)]
        public float FuelConsumptionRate
        {
            get { return fuelConsumptionRate; }
            set
            {
                if (!MathUtils.IsValid(value)) return;
                fuelConsumptionRate = Math.Max(value, 0.0f);
            }
        }

        [Serialize(false, true, description: "Is the temperature currently critical. Intended to be used by StatusEffect conditionals (setting the value from XML has no effect).")]
        public bool TemperatureCritical
        {
            get { return temperature > allowedTemperature.Y; }
            set { /*do nothing*/ }
        }

        private float correctTurbineOutput;

        private float targetFissionRate;
        private float targetTurbineOutput;
        
        [Serialize(false, true, description: "Is the automatic temperature control currently on. Indended to be used by StatusEffect conditionals (setting the value from XML is not recommended).")]
        public bool AutoTemp
        {
            get { return autoTemp; }
            set 
            { 
                autoTemp = value;
#if CLIENT
                UpdateUIElementStates();
#endif
            }
        }
        
        private float prevAvailableFuel;

        [Serialize(0.0f, true)]
        public float AvailableFuel { get; set; }

        public Reactor(Item item, XElement element)
            : base(item, element)
        {         
            IsActive = true;
            InitProjSpecific(element);
        }

        partial void InitProjSpecific(XElement element);
                
        public override void Update(float deltaTime, Camera cam)
        {
#if SERVER
            if (GameMain.Server != null && nextServerLogWriteTime != null)
            {
                if (Timing.TotalTime >= (float)nextServerLogWriteTime)
                {
                    GameServer.Log(GameServer.CharacterLogName(lastUser) + " adjusted reactor settings: " +
                            "Temperature: " + (int)(temperature * 100.0f) +
                            ", Fission rate: " + (int)targetFissionRate +
                            ", Turbine output: " + (int)targetTurbineOutput +
                            (autoTemp ? ", Autotemp ON" : ", Autotemp OFF"),
                            ServerLog.MessageType.ItemInteraction);

                    nextServerLogWriteTime = null;
                    lastServerLogWriteTime = (float)Timing.TotalTime;
                }
            }
#endif

            //if an AI character was using the item on the previous frame but not anymore, turn autotemp on
            // (= bots turn autotemp back on when leaving the reactor)
            if (LastAIUser != null)
            {
                if (LastAIUser.SelectedConstruction != item && LastAIUser.CanInteractWith(item))
                {
                    AutoTemp = true;
                    if (GameMain.NetworkMember?.IsServer ?? false) { unsentChanges = true; }
                    LastAIUser = null;
                }
            }

#if CLIENT
            if(PowerOn && AvailableFuel < 1)
            {
                HintManager.OnReactorOutOfFuel(this);
            }
#endif

            prevAvailableFuel = AvailableFuel;
            ApplyStatusEffects(ActionType.OnActive, deltaTime, null);

            //use a smoothed "correct output" instead of the actual correct output based on the load
            //so the player doesn't have to keep adjusting the rate impossibly fast when the load fluctuates heavily
            if (!MathUtils.NearlyEqual(MaxPowerOutput, 0.0f))
            {
                correctTurbineOutput += MathHelper.Clamp((load / MaxPowerOutput * 100.0f) - correctTurbineOutput, -10.0f, 10.0f) * deltaTime;
            }

            //calculate tolerances of the meters based on the skills of the user
            //more skilled characters have larger "sweet spots", making it easier to keep the power output at a suitable level
            float tolerance = MathHelper.Lerp(2.5f, 10.0f, degreeOfSuccess);
            optimalTurbineOutput = new Vector2(correctTurbineOutput - tolerance, correctTurbineOutput + tolerance);
            tolerance = MathHelper.Lerp(5.0f, 20.0f, degreeOfSuccess);
            allowedTurbineOutput = new Vector2(correctTurbineOutput - tolerance, correctTurbineOutput + tolerance);

            optimalTemperature = Vector2.Lerp(new Vector2(40.0f, 60.0f), new Vector2(30.0f, 70.0f), degreeOfSuccess);
            allowedTemperature = Vector2.Lerp(new Vector2(30.0f, 70.0f), new Vector2(10.0f, 90.0f), degreeOfSuccess);
            
            optimalFissionRate = Vector2.Lerp(new Vector2(30, AvailableFuel - 20), new Vector2(20, AvailableFuel - 10), degreeOfSuccess);
            optimalFissionRate.X = Math.Min(optimalFissionRate.X, optimalFissionRate.Y - 10);
            allowedFissionRate = Vector2.Lerp(new Vector2(20, AvailableFuel), new Vector2(10, AvailableFuel), degreeOfSuccess);
            allowedFissionRate.X = Math.Min(allowedFissionRate.X, allowedFissionRate.Y - 10);

            float heatAmount = GetGeneratedHeat(fissionRate);

            float temperatureDiff = (heatAmount - turbineOutput) - Temperature;
            Temperature += MathHelper.Clamp(Math.Sign(temperatureDiff) * 10.0f * deltaTime, -Math.Abs(temperatureDiff), Math.Abs(temperatureDiff));
            //if (item.InWater && AvailableFuel < 100.0f) Temperature -= 12.0f * deltaTime;

            FissionRate = MathHelper.Lerp(fissionRate, Math.Min(targetFissionRate, AvailableFuel), deltaTime);

            TurbineOutput = MathHelper.Lerp(turbineOutput, targetTurbineOutput, deltaTime);

            float temperatureFactor = Math.Min(temperature / 50.0f, 1.0f);
            currPowerConsumption = -MaxPowerOutput * Math.Min(turbineOutput / 100.0f, temperatureFactor);

            //if the turbine output and coolant flow are the optimal range, 
            //make the generated power slightly adjust according to the load
            //  (-> the reactor can automatically handle small changes in load as long as the values are roughly correct)
            if (turbineOutput > optimalTurbineOutput.X && turbineOutput < optimalTurbineOutput.Y && 
                temperature > optimalTemperature.X && temperature < optimalTemperature.Y)
            {
                float maxAutoAdjust = maxPowerOutput * 0.1f;
                autoAdjustAmount = MathHelper.Lerp(
                    autoAdjustAmount, 
                    MathHelper.Clamp(-load - currPowerConsumption, -maxAutoAdjust, maxAutoAdjust), 
                    deltaTime * 10.0f);
            }
            else
            {
                autoAdjustAmount = MathHelper.Lerp(autoAdjustAmount, 0.0f, deltaTime * 10.0f);
            }
            currPowerConsumption += autoAdjustAmount;

            if (!PowerOn)
            {
                targetFissionRate = 0.0f;
                targetTurbineOutput = 0.0f;
            }
            else if (autoTemp)
            {
                UpdateAutoTemp(2.0f, deltaTime);
            }
            float currentLoad = 0.0f;
            List<Connection> connections = item.Connections;
            if (connections != null && connections.Count > 0)
            {
                foreach (Connection connection in connections)
                {
                    if (!connection.IsPower) { continue; }
                    foreach (Connection recipient in connection.Recipients)
                    {
                        if (!(recipient.Item is Item it)) { continue; }

                        PowerTransfer pt = it.GetComponent<PowerTransfer>();
                        if (pt == null) { continue; }

                        //calculate how much external power there is in the grid 
                        //(power coming from somewhere else than this reactor, e.g. batteries)
                        float externalPower = Math.Max(CurrPowerConsumption - pt.CurrPowerConsumption, 0) * 0.95f;
                        //reduce the external power from the load to prevent overloading the grid
                        currentLoad = Math.Max(currentLoad, pt.PowerLoad - externalPower);
                    }
                }
            }

            if (!loadQueue.Any() && PowerOn)
            {
                //loadQueue is empty, round must've just started
                //reset the fission rate, turbine output and
                //temperature to optimal levels to prevent fires
                //at the start of the round
                correctTurbineOutput = MathUtils.NearlyEqual(MaxPowerOutput, 0.0f) ? 0.0f : currentLoad / MaxPowerOutput * 100.0f;
                tolerance = MathHelper.Lerp(2.5f, 10.0f, degreeOfSuccess);
                optimalTurbineOutput = new Vector2(correctTurbineOutput - tolerance, correctTurbineOutput + tolerance);
                tolerance = MathHelper.Lerp(5.0f, 20.0f, degreeOfSuccess);
                allowedTurbineOutput = new Vector2(correctTurbineOutput - tolerance, correctTurbineOutput + tolerance);

                DebugConsole.Log($"Degree of success: {degreeOfSuccess}");
                DebugConsole.Log($"Current load: {currentLoad}");
                DebugConsole.Log($"Max power output: {MaxPowerOutput}");
                DebugConsole.Log($"Available fuel: {AvailableFuel}");

                float desiredTurbineOutput = MathHelper.Clamp(correctTurbineOutput, 0.0f, 100.0f);
                DebugConsole.Log($"Turbine output reset: {targetTurbineOutput}, {turbineOutput} -> {desiredTurbineOutput}");
                targetTurbineOutput = desiredTurbineOutput;
                turbineOutput = desiredTurbineOutput;

                float desiredTemperature = (optimalTemperature.X + optimalTemperature.Y) / 2.0f;
                DebugConsole.Log($"Temperature reset: {temperature} -> {desiredTemperature}");
                temperature = desiredTemperature;

                float desiredFissionRate = GetFissionRateForTargetTemperatureAndTurbineOutput(desiredTemperature, desiredTurbineOutput);
                DebugConsole.Log($"Fission rate reset: {targetFissionRate}, {fissionRate} -> {desiredFissionRate}");
                targetFissionRate = desiredFissionRate;
                fissionRate = desiredFissionRate;
            }

            loadQueue.Enqueue(currentLoad);
            while (loadQueue.Count() > 60.0f)
            {
                load = loadQueue.Average();
                loadQueue.Dequeue();
            }

            var containedItems = item.OwnInventory?.AllItems;
            if (containedItems == null) {fuelLeft = 0.0f;}
            else
            {
                bool fuelcheck = true;
                foreach (Item item in containedItems)
                {
                    if (!item.HasTag("reactorfuel")) { continue; }
                    if(fissionRate > 0.0f) {item.Condition -= fissionRate / 100.0f * fuelConsumptionRate * deltaTime;}
                    if(fuelcheck) 
                    {
                        fuelLeft = 100.0f;
                        fuelcheck = false;
                    }
                    if(item.Condition < fuelLeft){fuelLeft = item.Condition;}
                }
            }

            if (fissionRate > 0.0f)
            {
                if (item.CurrentHull != null)
                {
                    var aiTarget = item.CurrentHull.AiTarget;
                    if (aiTarget != null && MaxPowerOutput > 0)
                    {
                        float range = Math.Abs(currPowerConsumption) / MaxPowerOutput;
                        float noise = MathHelper.Lerp(aiTarget.MinSoundRange, aiTarget.MaxSoundRange, range);
                        aiTarget.SoundRange = Math.Max(aiTarget.SoundRange, noise);
                    }
                }

                if (item.AiTarget != null && MaxPowerOutput > 0)
                {
                    var aiTarget = item.AiTarget;
                    float range = Math.Abs(currPowerConsumption) / MaxPowerOutput;
                    aiTarget.SoundRange = MathHelper.Lerp(aiTarget.MinSoundRange, aiTarget.MaxSoundRange, range);
                }
            }
            
            item.SendSignal(((int)(temperature * 100.0f)).ToString(), "temperature_out");
            item.SendSignal(((int)-CurrPowerConsumption).ToString(), "power_value_out");
            item.SendSignal(((int)load).ToString(), "load_value_out");
            item.SendSignal(((int)AvailableFuel).ToString(), "fuel_out");
            item.SendSignal(((int)fuelLeft).ToString(), "fuel_left");

            UpdateFailures(deltaTime);
#if CLIENT
            UpdateGraph(deltaTime);
#endif
            AvailableFuel = 0.0f;


            sendUpdateTimer -= deltaTime;
#if CLIENT
            if (unsentChanges && sendUpdateTimer <= 0.0f)
#else
            if (sendUpdateTimer < -NetworkUpdateIntervalLow || (unsentChanges && sendUpdateTimer <= 0.0f))
#endif
            {
#if SERVER
                if (GameMain.Server != null)
                {
                    item.CreateServerEvent(this);
                }
#endif
#if CLIENT
                if (GameMain.Client != null)
                {
                    item.CreateClientEvent(this);
                }
#endif
                sendUpdateTimer = NetworkUpdateIntervalHigh;
                unsentChanges = false;
            }
        }

        private float GetGeneratedHeat(float fissionRate)
        {
            return fissionRate * (prevAvailableFuel / 100.0f) * 2.0f;
        }

        private float GetFissionRateForTargetTemperatureAndTurbineOutput(float temperature, float turbineOutput)
        {
            if (MathUtils.NearlyEqual(AvailableFuel, 0f)) { return 0f; }
            return (temperature + turbineOutput) / (AvailableFuel / 100f) / 2f;
        }

        /// <summary>
        /// Do we need more fuel to generate enough power to match the current load.
        /// </summary>
        /// <param name="minimumOutputRatio">How low we allow the output/load ratio to go before loading more fuel. 
        /// 1.0 = always load more fuel when maximum output is too low, 0.5 = load more if max output is 50% of the load</param>
        private bool NeedMoreFuel(float minimumOutputRatio, float minCondition = 0)
        {
            float remainingFuel = item.ContainedItems.Sum(i => i.Condition);
            if (remainingFuel <= minCondition && load > 0.0f)
            {
                return true;
            }

            //fission rate is clamped to the amount of available fuel
            float maxFissionRate = Math.Min(prevAvailableFuel, 100.0f);
            if (maxFissionRate >= 100.0f) { return false; }

            float maxTurbineOutput = 100.0f;

            //calculate the maximum output if the fission rate is cranked as high as it goes and turbine output is at max
            float theoreticalMaxHeat = GetGeneratedHeat(fissionRate: maxFissionRate);             
            float temperatureFactor = Math.Min(theoreticalMaxHeat / 50.0f, 1.0f);
            float theoreticalMaxOutput = Math.Min(maxTurbineOutput / 100.0f, temperatureFactor) * MaxPowerOutput;

            //maximum output not enough, we need more fuel
            return theoreticalMaxOutput < load * minimumOutputRatio;
        }

        private bool TooMuchFuel()
        {
            var containedItems = item.OwnInventory?.AllItems;
            if (containedItems != null && containedItems.Count() <= 1) { return false; }

            //get the amount of heat we'd generate if the fission rate was at the low end of the optimal range
            float minimumHeat = GetGeneratedHeat(optimalFissionRate.X);

            //if we need a very high turbine output to keep the engine from overheating, there's too much fuel
            return minimumHeat > Math.Min(correctTurbineOutput * 1.5f, 90);
        }

        private void UpdateFailures(float deltaTime)
        {
            if (temperature > allowedTemperature.Y)
            {
                item.SendSignal("1", "meltdown_warning");
                //faster meltdown if the item is in a bad condition
                meltDownTimer += MathHelper.Lerp(deltaTime * 2.0f, deltaTime, item.Condition / item.MaxCondition);

                if (meltDownTimer > MeltdownDelay)
                {
                    MeltDown();
                    return;
                }
            }
            else
            {
                item.SendSignal("0", "meltdown_warning");
                meltDownTimer = Math.Max(0.0f, meltDownTimer - deltaTime);
            }

            if (temperature > optimalTemperature.Y)
            {
                float prevFireTimer = fireTimer;
                fireTimer += MathHelper.Lerp(deltaTime * 2.0f, deltaTime, item.Condition / item.MaxCondition);


#if SERVER
                if (fireTimer > Math.Min(5.0f, FireDelay / 2) && blameOnBroken?.Character?.SelectedConstruction == item)
                {
                    GameMain.Server.KarmaManager.OnReactorOverHeating(blameOnBroken.Character, deltaTime);
                }
#endif

                if (fireTimer >= FireDelay && prevFireTimer < fireDelay)
                {
                    new FireSource(item.WorldPosition);
                }
            }
            else
            {
                fireTimer = Math.Max(0.0f, fireTimer - deltaTime);
            }
        }

        public void UpdateAutoTemp(float speed, float deltaTime)
        {
            float desiredTurbineOutput = (optimalTurbineOutput.X + optimalTurbineOutput.Y) / 2.0f;
            targetTurbineOutput += MathHelper.Clamp(desiredTurbineOutput - targetTurbineOutput, -speed, speed) * deltaTime;
            targetTurbineOutput = MathHelper.Clamp(targetTurbineOutput, 0.0f, 100.0f);

            float desiredFissionRate = (optimalFissionRate.X + optimalFissionRate.Y) / 2.0f;
            targetFissionRate += MathHelper.Clamp(desiredFissionRate - targetFissionRate, -speed, speed) * deltaTime;

            if (temperature > (optimalTemperature.X + optimalTemperature.Y) / 2.0f)
            {
                targetFissionRate = Math.Min(targetFissionRate - speed * 2 * deltaTime, allowedFissionRate.Y);
            }
            else if (-currPowerConsumption < load)
            {
                targetFissionRate = Math.Min(targetFissionRate + speed * 2 * deltaTime, 100.0f);
            }
            targetFissionRate = MathHelper.Clamp(targetFissionRate, 0.0f, 100.0f);

            //don't push the target too far from the current fission rate
            //otherwise we may "overshoot", cranking the target fission rate all the way up because it takes a while
            //for the actual fission rate and temperature to follow
            targetFissionRate = MathHelper.Clamp(targetFissionRate, FissionRate - 5, FissionRate + 5);
        }

        public void PowerUpImmediately()
        {
            PowerOn = true;
            AutoTemp = true;
            prevAvailableFuel = AvailableFuel;
            for (int i = 0; i < 100; i++)
            {
                Update((float)(Timing.Step * 10.0f), cam: null);
                UpdateAutoTemp(100.0f, (float)(Timing.Step * 10.0f));
                AvailableFuel = prevAvailableFuel;
            }
        }
        
        public override void UpdateBroken(float deltaTime, Camera cam)
        {
            base.UpdateBroken(deltaTime, cam);

            item.SendSignal(((int)(temperature * 100.0f)).ToString(), "temperature_out");

            currPowerConsumption = 0.0f;
            Temperature -= deltaTime * 1000.0f;
            targetFissionRate = Math.Max(targetFissionRate - deltaTime * 10.0f, 0.0f);
            targetTurbineOutput = Math.Max(targetTurbineOutput - deltaTime * 10.0f, 0.0f);
#if CLIENT
            FissionRateScrollBar.BarScroll = 1.0f - FissionRate / 100.0f;
            TurbineOutputScrollBar.BarScroll = 1.0f - TurbineOutput / 100.0f;
            UpdateGraph(deltaTime);
#endif
        }

        private void MeltDown()
        {
            if (item.Condition <= 0.0f) { return; }
            if (GameMain.NetworkMember != null && GameMain.NetworkMember.IsClient) { return; }

            item.Condition = 0.0f;
            fireTimer = 0.0f;
            meltDownTimer = 0.0f;

            var containedItems = item.OwnInventory?.AllItems;
            if (containedItems != null)
            {
                foreach (Item containedItem in containedItems)
                {
                    containedItem.Condition = 0.0f;
                }
            }

#if SERVER
            GameServer.Log("Reactor meltdown!", ServerLog.MessageType.ItemInteraction);
            if (GameMain.Server != null)
            {
                GameMain.Server.KarmaManager.OnReactorMeltdown(blameOnBroken?.Character);
            }
#endif
        }

        public override bool Pick(Character picker)
        {
            return picker != null;
        }

        public override bool AIOperate(float deltaTime, Character character, AIObjectiveOperateItem objective)
        {
            if (GameMain.NetworkMember != null && GameMain.NetworkMember.IsClient) { return false; }
            character.AIController.SteeringManager.Reset();
            bool shutDown = objective.Option.Equals("shutdown", StringComparison.OrdinalIgnoreCase);

            IsActive = true;

            if (!shutDown)
            {
                float degreeOfSuccess = DegreeOfSuccess(character);
                float refuelLimit = 0.3f;
                //characters with insufficient skill levels don't refuel the reactor
                if (degreeOfSuccess > refuelLimit)
                {
                    if (aiUpdateTimer > 0.0f)
                    {
                        aiUpdateTimer -= deltaTime;
                        return false;
                    }
                    aiUpdateTimer = AIUpdateInterval;
                    // load more fuel if the current maximum output is only 50% of the current load
                    // or if the fuel rod is (almost) deplenished 
                    float minCondition = fuelConsumptionRate * MathUtils.Pow2((degreeOfSuccess - refuelLimit) * 2);
                    if (NeedMoreFuel(minimumOutputRatio: 0.5f, minCondition: minCondition))
                    {
                        bool outOfFuel = false;
                        var container = item.GetComponent<ItemContainer>();
                        if (objective.SubObjectives.None())
                        {
                            var containObjective = AIContainItems<Reactor>(container, character, objective, itemCount: 1, equip: true, removeEmpty: true, spawnItemIfNotFound: character.TeamID == CharacterTeamType.FriendlyNPC, dropItemOnDeselected: true);
                            containObjective.Completed += ReportFuelRodCount;
                            containObjective.Abandoned += ReportFuelRodCount;
                            character.Speak(TextManager.Get("DialogReactorFuel"), null, 0.0f, "reactorfuel", 30.0f);

                            void ReportFuelRodCount()
                            {
                                if (!character.IsOnPlayerTeam) { return; }
                                if (character.Submarine != Submarine.MainSub) { return; }
                                int remainingFuelRods = Submarine.MainSub.GetItems(false).Count(i => i.HasTag("reactorfuel") && i.Condition > 1);
                                if (remainingFuelRods == 0)
                                {
                                    character.Speak(TextManager.Get("DialogOutOfFuelRods"), null, 0.0f, "outoffuelrods", 30.0f);
                                    outOfFuel = true;
                                }
                                else if (remainingFuelRods < 3)
                                {
                                    character.Speak(TextManager.Get("DialogLowOnFuelRods"), null, 0.0f, "lowonfuelrods", 30.0f);
                                }
                            }
                        }
                        return outOfFuel;
                    }
                    else
                    {
                        if (Item.ConditionPercentage <= 0 && AIObjectiveRepairItems.IsValidTarget(Item, character))
                        {
                            if (Item.Repairables.Average(r => r.DegreeOfSuccess(character)) > 0.4f)
                            {
                                objective.AddSubObjective(new AIObjectiveRepairItem(character, Item, objective.objectiveManager, isPriority: true));
                                return false;
                            }
                            else
                            {
                                character.Speak(TextManager.Get("DialogReactorIsBroken"), identifier: "reactorisbroken", minDurationBetweenSimilar: 30.0f);
                            }
                        }
                        if (TooMuchFuel())
                        {
                            DropFuel(minCondition: 0.1f, maxCondition: 100);
                        }
                        else
                        {
                            DropFuel(minCondition: 0, maxCondition: 0);
                        }
                    }
                }
            }

            if (objective.Override)
            {
                if (lastUser != null && lastUser != character && lastUser != LastAIUser)
                {
                    if (lastUser.SelectedConstruction == item && character.IsOnPlayerTeam)
                    {
                        character.Speak(TextManager.Get("DialogReactorTaken"), null, 0.0f, "reactortaken", 10.0f);
                    }
                }
            }
            else if (LastUserWasPlayer && lastUser != null && lastUser.TeamID == character.TeamID)
            {
                return true;
            }

            LastUser = LastAIUser = character;

            bool prevAutoTemp = autoTemp;
            bool prevPowerOn = _powerOn;
            float prevFissionRate = targetFissionRate;
            float prevTurbineOutput = targetTurbineOutput;

            if (shutDown)
            {
                PowerOn = false;
                AutoTemp = false;
                targetFissionRate = 0.0f;
                targetTurbineOutput = 0.0f;
                unsentChanges = true;
                return true;
            }
            else
            {
                PowerOn = true;
                if (objective.Override || !autoTemp)
                {
                    //characters with insufficient skill levels simply set the autotemp on instead of trying to adjust the temperature manually
                    if (degreeOfSuccess < 0.5f)
                    {
                        AutoTemp = true;
                    }
                    else
                    {
                        AutoTemp = false;
                        UpdateAutoTemp(MathHelper.Lerp(0.5f, 2.0f, degreeOfSuccess), 1.0f);
                    }
                }
#if CLIENT
                FissionRateScrollBar.BarScroll = FissionRate / 100.0f;
                TurbineOutputScrollBar.BarScroll = TurbineOutput / 100.0f;
#endif
                if (autoTemp != prevAutoTemp ||
                    prevPowerOn != _powerOn ||
                    Math.Abs(prevFissionRate - targetFissionRate) > 1.0f ||
                    Math.Abs(prevTurbineOutput - targetTurbineOutput) > 1.0f)
                {
                    unsentChanges = true;
                }
                aiUpdateTimer = AIUpdateInterval;
                return false;
            }


            void DropFuel(float minCondition, float maxCondition)
            {
                if (item.OwnInventory?.AllItems != null)
                {
                    var container = item.GetComponent<ItemContainer>();
                    foreach (Item item in item.OwnInventory.AllItemsMod)
                    {
                        if (item.ConditionPercentage <= maxCondition && item.ConditionPercentage >= minCondition)
                        {
                            item.Drop(character);
                            break;
                        }
                    }
                }
            }
        }

        public override void OnMapLoaded()
        {
            prevAvailableFuel = AvailableFuel;
        }

        public override void ReceiveSignal(Signal signal, Connection connection)
        {
            switch (connection.Name)
            {
                case "shutdown":
                    if (targetFissionRate > 0.0f || targetTurbineOutput > 0.0f)
                    {
                        PowerOn = false;
                        AutoTemp = false;
                        targetFissionRate = 0.0f;
                        targetTurbineOutput = 0.0f;
                        if (GameMain.NetworkMember?.IsServer ?? false) { unsentChanges = true; }
                    }
                    break;
                case "set_fissionrate":
                    if (PowerOn && float.TryParse(signal.value, NumberStyles.Float, CultureInfo.InvariantCulture, out float newFissionRate))
                    {
                        targetFissionRate = MathHelper.Clamp(newFissionRate, 0.0f, 100.0f);
                        if (GameMain.NetworkMember?.IsServer ?? false) { unsentChanges = true; }
#if CLIENT
                        FissionRateScrollBar.BarScroll = targetFissionRate / 100.0f;
#endif
                    }
                    break;
                case "set_turbineoutput":
                    if (PowerOn && float.TryParse(signal.value, NumberStyles.Float, CultureInfo.InvariantCulture, out float newTurbineOutput))
                    {
                        targetTurbineOutput = MathHelper.Clamp(newTurbineOutput, 0.0f, 100.0f);
                        if (GameMain.NetworkMember?.IsServer ?? false) { unsentChanges = true; }                       
#if CLIENT
                        TurbineOutputScrollBar.BarScroll = targetTurbineOutput / 100.0f;
#endif
                    }
                    break;
            }
        }
    }
}

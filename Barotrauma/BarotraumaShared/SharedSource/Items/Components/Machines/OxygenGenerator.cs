using Microsoft.Xna.Framework;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Barotrauma.Networking;

namespace Barotrauma.Items.Components
{
    internal partial class OxygenGenerator : Powered
    {
        private const int GenerationRatioSteps = 10;
        private const float GenerationRatioStep = 1f / GenerationRatioSteps;
        
        private float generatedAmount;
        private float generationRatio;

        //key = vent, float = total volume of the hull the vent is in and the hulls connected to it
        private List<(Vent vent, float hullVolume)> ventList;

        private float totalHullVolume;

        private float ventUpdateTimer;
        const float VentUpdateInterval = 5.0f;

        private float controlLockTimer;
        
        [Serialize(0f, IsPropertySaveable.No, "The current adjusted oxygen output of the generator. Setting this value in XML has no effect.")]
        public float CurrFlow
        {
            get;
            private set;
        }

        [Editable, Serialize(400.0f, IsPropertySaveable.Yes, description: "How much oxygen the machine generates when operating at full power.", alwaysUseInstanceValues: true)]
        public float GeneratedAmount
        {
            get { return generatedAmount; }
            set { generatedAmount = MathHelper.Clamp(value, -10000.0f, 10000.0f); }
        }
        
        [Editable(0f, 1f), Serialize(1f, IsPropertySaveable.Yes, "The ratio of the max generation capacity this machine is currently outputting.", alwaysUseInstanceValues: true)]
        public float GenerationRatio
        {
            get => generationRatio;
            set
            {
                if (!MathUtils.IsValid(value)) { return; }
                generationRatio = MathUtils.RoundTowardsClosest(MathHelper.Clamp(value, 0f, 1f), GenerationRatioStep);
#if CLIENT
                UpdateSlider();
#endif
            }
        }

        public OxygenGenerator(Item item, ContentXElement element)
            : base(item, element)
        {
            //randomize update timer so all oxygen generators don't update at the same time
            ventUpdateTimer = Rand.Range(0.0f, VentUpdateInterval);
            InitProjSpecific();
            IsActive = true;
        }
        
        partial void InitProjSpecific();

        public override bool Pick(Character picker) => picker != null;

        public override void Update(float deltaTime, Camera cam)
        {
            UpdateOnActiveEffects(deltaTime);

            controlLockTimer -= deltaTime;

            CurrFlow = 0.0f;

            if (item.CurrentHull == null) { return; }
            
            if (!HasPower && PowerConsumption > 0)
            {
                return;
            }

            CurrFlow = Math.Min(PowerConsumption > 0 ? Voltage : 1.0f, MaxOverVoltageFactor) * generatedAmount * generationRatio * 100f;
            float conditionMult = item.Condition / item.MaxCondition;
            //100% condition = 100% oxygen
            //50% condition = 25% oxygen
            //20% condition = 4%
            CurrFlow *= conditionMult * conditionMult;

            UpdateVents(CurrFlow, deltaTime);
            
            item.SendSignal(MathUtils.RoundToInt(generationRatio * 100).ToString(), "rate_out");
        }

        /// <summary>
        /// Power consumption of the Oxygen Generator. Only consume power when active and adjust consumption based on condition.
        /// </summary>
        public override float GetCurrentPowerConsumption(Connection connection = null)
        {
            if (connection != this.powerIn || !IsActive)
            {
                return 0;
            }

            float consumption = powerConsumption * generationRatio;

            //consume more power when in a bad condition
            item.GetComponent<Repairable>()?.AdjustPowerConsumption(ref consumption);
            return consumption;
        }

        public override void ReceiveSignal(Signal signal, Connection connection)
        {
            if (connection.IsPower) { return; }
            switch (connection.Name)
            {
                case "set_rate":
                    if (float.TryParse(signal.value, NumberStyles.Any, CultureInfo.InvariantCulture, out float newRate) && MathUtils.IsValid(newRate))
                    {
                        controlLockTimer = 0.1f;
                        GenerationRatio = MathHelper.Clamp(newRate / 100f, 0f, 1f);
                    }
                    break;
            }
        }
        
        public override void UpdateBroken(float deltaTime, Camera cam)
        {
            base.UpdateBroken(deltaTime, cam);
            CurrFlow = 0.0f;
        }

        private void GetVents()
        {
            totalHullVolume = 0.0f;
            ventList ??= new List<(Vent vent, float hullVolume)>();
            ventList.Clear();
            foreach (MapEntity entity in item.linkedTo)
            {
                if (entity is not Item linkedItem) { continue; }

                Vent vent = linkedItem.GetComponent<Vent>();
                if (vent?.Item.CurrentHull == null) { continue; }

                totalHullVolume += vent.Item.CurrentHull.Volume;
                ventList.Add((vent, vent.Item.CurrentHull.Volume));
            }

            for (int i = 0; i < ventList.Count; i++)
            {
                Vent vent = ventList[i].vent;
                foreach (Hull connectedHull in vent.Item.CurrentHull.GetConnectedHulls(includingThis: false, searchDepth: 3, ignoreClosedGaps: true))
                {
                    //another vent in the connected hull -> don't add it to this vent's total hull volume
                    if (ventList.Any(v => v.vent != vent && v.vent.Item.CurrentHull == connectedHull)) { continue; }
                    totalHullVolume += connectedHull.Volume;
                    ventList[i] = (ventList[i].vent, ventList[i].hullVolume + connectedHull.Volume);
                }
            }
        }

        private void UpdateVents(float deltaOxygen, float deltaTime)
        {
            if (ventList == null || ventUpdateTimer < 0.0f)
            {
                GetVents();
                ventUpdateTimer = VentUpdateInterval;
            }
            ventUpdateTimer -= deltaTime;

            if (!ventList.Any() || totalHullVolume <= 0.0f) { return; }

            foreach ((Vent vent, float hullVolume) in ventList)
            {
                if (vent.Item.CurrentHull == null) { continue; }

                vent.OxygenFlow = deltaOxygen * (hullVolume / totalHullVolume);
                vent.IsActive = true;
            }
        }

        public float GetVentOxygenFlow(Vent targetVent)
        {
            if (ventList == null)
            {
                GetVents();
            }
            foreach ((Vent vent, float hullVolume) in ventList)
            {
                if (vent != targetVent) { continue; }
                return generatedAmount * 100.0f * (hullVolume / totalHullVolume);
            }
            return 0.0f;
        }
        
        #region Networking
        private static float ReadGenerationRatio(IReadMessage msg) => msg.ReadRangedInteger(0, GenerationRatioSteps) * GenerationRatioStep;
        private void WriteGenerationRatio(IWriteMessage msg) => msg.WriteRangedInteger(MathUtils.RoundToInt(generationRatio / GenerationRatioStep), 0, GenerationRatioSteps);
        #endregion
    }
}

using Microsoft.Xna.Framework;
using System;
using System.Globalization;
using System.Xml.Linq;
using Barotrauma.Networking;
using FarseerPhysics.Dynamics;

namespace Barotrauma.Items.Components
{
    partial class Engine : Powered, IServerSerializable, IClientSerializable, IDeteriorateUnderStress
    {
        private float force;

        /// <summary>
        /// Latest signal the set_force connection received, used to set <see cref="targetForce"/> in the Update method.
        /// We use a separate variable, because otherwise specific item update orders and sending multiple signals to set_force would lead to bugs:
        /// targetForce could be set to 0, then a power grid might update as if the engine was off and mark the voltage of the grid as 1, 
        /// then another item could set the targetForce to 100 and make it run without power.
        /// </summary>
        private float? lastReceivedTargetForce;

        /// <summary>
        /// The amount of force the engine is aiming for (the actual force may be less than this, 
        /// depending on the amount of power, the condition of the engine or boosts from talents)
        /// </summary>
        private float targetForce;

        /// <summary>
        /// Power demand of a marine engine is proportional with the cube of the square root of the thrusting force.
        /// In practice meaning lower thrust is more effective at conserving power than it would be if the relationship between thrust and power consumption was linear.
        /// Reverse exponent defined for use with overvoltage calculation: Supplying 2x power will result in 59% more force, 26% more speed, therefore 2x power.
        /// </summary>
        private const float ForceToPowerExponent = 3f / 2f;
        private const float PowerToForceExponent = 1.0f / ForceToPowerExponent;

        private float maxForce;
        
        private readonly Attack propellerDamage;

        private float damageTimer;

        private bool hasPower;

        private float prevVoltage;

        private float controlLockTimer;

        public Character User;

        [Editable(0.0f, 10000000.0f), 
        Serialize(500.0f, IsPropertySaveable.Yes, description: "The amount of force exerted on the submarine when the engine is operating at full power.")]
        public float MaxForce
        {
            get => maxForce;
            set => maxForce = Math.Max(0.0f, value);
        }

        [Editable, Serialize("0.0,0.0", IsPropertySaveable.Yes, 
            description: "The position of the propeller as an offset from the item's center (in pixels)."+
            " Determines where the particles spawn and the position that causes characters to take damage from the engine if the PropellerDamage is defined.")]
        public Vector2 PropellerPos
        {
            get;
            set;
        }

        [Editable, Serialize(false, IsPropertySaveable.Yes)]
        public bool DisablePropellerDamage
        {
            get;
            set;
        }

        public float Force
        {
            get { return force;}
            set { force = MathHelper.Clamp(value, -100.0f, 100.0f); }
        }

        public float CurrentVolume => CurrentStress;

        public float CurrentBrokenVolume
        {
            get 
            {
                if (item.ConditionPercentage > 10.0f) { return 0.0f; }
                return Math.Abs(targetForce / 100.0f) * (1.0f - item.ConditionPercentage / 10.0f); 
            }
        }

        public float CurrentStress => Math.Abs((force / 100.0f) * (MinVoltage <= 0.0f ? 1.0f : Math.Min(prevVoltage, 1.0f)));
    
        private const float TinkeringForceIncrease = 1.5f;

        public Engine(Item item, ContentXElement element)
            : base(item, element)
        {
            IsActive = true;
            
            foreach (var subElement in element.Elements())
            {
                switch (subElement.Name.ToString().ToLowerInvariant())
                {
                    case "propellerdamage":
                        propellerDamage = new Attack(subElement, item.Name + ", Engine");
                        break;
                }
            }

            InitProjSpecific(element);
        }

        partial void InitProjSpecific(ContentXElement element);

        public override void Update(float deltaTime, Camera cam)
        {
            UpdateOnActiveEffects(deltaTime);

            UpdateAnimation(deltaTime);

            controlLockTimer -= deltaTime;

            if (powerConsumption == 0.0f)
            {
                prevVoltage = 1;
                hasPower = true;
            }
            else
            {
                hasPower = HasPower;
            }

            if (lastReceivedTargetForce.HasValue)
            {
                targetForce = lastReceivedTargetForce.Value;
            }
            Force = MathHelper.Lerp(force, (Voltage < MinVoltage) ? 0.0f : targetForce, deltaTime * 10.0f);
            if (Math.Abs(Force) > 1.0f)
            {
                float voltageFactor = MinVoltage <= 0.0f ? 1.0f : Math.Min(Voltage, MaxOverVoltageFactor);
                float currForce = force * MathF.Pow(voltageFactor, PowerToForceExponent);
                float condition = item.MaxCondition <= 0.0f ? 0.0f : item.Condition / item.MaxCondition;
                // Broken engine makes more noise.
                float noise = Math.Abs(currForce) * MathHelper.Lerp(1.5f, 1f, condition);
                UpdateAITargets(noise);
                //arbitrary multiplier that was added to changes in submarine mass without having to readjust all engines
                float forceMultiplier = 0.1f;
                if (item.Submarine.SubBody.Body != null)
                {
                    float mass = item.Submarine.SubBody.Body.Mass;
                    float newDragCorrectionFactor = MathF.Pow(mass / SubmarineBody.InertiaReferenceMass, SubmarineBody.MassToAreaExponent) * SubmarineBody.InertiaReferenceMass / mass;
                    forceMultiplier *= newDragCorrectionFactor;
                }
                if (User != null)
                {
                    forceMultiplier *= MathHelper.Lerp(0.5f, 2.0f, (float)Math.Sqrt(User.GetSkillLevel(Tags.HelmSkill) / 100));
                }
                currForce *= item.StatManager.GetAdjustedValueMultiplicative(ItemTalentStats.EngineMaxSpeed, MaxForce) * forceMultiplier;
                if (item.GetComponent<Repairable>() is { IsTinkering: true } repairable)
                {
                    currForce *= 1f + repairable.TinkeringStrength * TinkeringForceIncrease;
                }

                currForce = item.StatManager.GetAdjustedValueMultiplicative(ItemTalentStats.EngineSpeed, currForce);

                //less effective when in a bad condition
                currForce *= MathHelper.Lerp(0.5f, 2.0f, condition);
                if (item.Submarine.FlippedX) { currForce *= -1; }
                Vector2 forceVector = new Vector2(currForce, 0);
                item.Submarine.ApplyForce(forceVector * deltaTime * Timing.FixedUpdateRate);
                UpdatePropellerDamage(deltaTime);
#if CLIENT
                float particleInterval = 1.0f / particlesPerSec;
                particleTimer += deltaTime;
                while (particleTimer > particleInterval)
                {
                    Vector2 particleVel = -forceVector.ClampLength(5000.0f) / 5.0f;
                    GameMain.ParticleManager.CreateParticle("bubbles", item.WorldPosition + PropellerPos * item.Scale,
                        particleVel * Rand.Range(0.8f, 1.1f),
                        0.0f, item.CurrentHull);
                    particleTimer -= particleInterval;
                }
#endif
            }
        }

        /// <summary>
        /// Power consumption of the engine. Only consume power when active and adjust consumption based on condition and target force.
        /// </summary>
        public override float GetCurrentPowerConsumption(Connection connection = null)
        {
            if (connection != this.powerIn || !IsActive)
            {
                return 0;
            }

            currPowerConsumption = MathF.Pow(Math.Abs(targetForce) / 100.0f, ForceToPowerExponent) * powerConsumption;
            //engines consume more power when in a bad condition
            item.GetComponent<Repairable>()?.AdjustPowerConsumption(ref currPowerConsumption);
            return currPowerConsumption;
        }

        /// <summary>
        /// When grid is resolved update the previous voltage
        /// </summary>
        public override void GridResolved(Connection connection) 
        {
            if (connection == powerIn)
            {
                prevVoltage = Voltage;
            }
        }

        private void UpdateAITargets(float noise)
        {
            if (item.AiTarget != null)
            {
                item.AiTarget.SoundRange = MathHelper.Lerp(item.AiTarget.MinSoundRange, item.AiTarget.MaxSoundRange, noise / 100);
                if (item.CurrentHull != null && item.CurrentHull.AiTarget != null)
                {
                    // It's possible that some other item increases the hull's soundrange more than the engine.
                    item.CurrentHull.AiTarget.SoundRange = Math.Max(item.CurrentHull.AiTarget.SoundRange, item.AiTarget.SoundRange);
                }
            }
        }

        private void UpdatePropellerDamage(float deltaTime)
        {
            if (DisablePropellerDamage) { return; }

            damageTimer += deltaTime;
            if (damageTimer < 0.5f) { return; }
            damageTimer = 0.1f;

            if (propellerDamage == null) { return; }

            float scaledDamageRange = propellerDamage.DamageRange * item.Scale;

            Vector2 propellerWorldPos = item.WorldPosition + PropellerPos * item.Scale; 
            float broadRange = Math.Max(scaledDamageRange * 2, 500);
            foreach (Character character in Character.CharacterList)
            {
                if (!character.Enabled || character.Removed) { continue; }
                if (Math.Abs(character.WorldPosition.X - propellerWorldPos.X) > broadRange) { continue; }
                if (Math.Abs(character.WorldPosition.Y - propellerWorldPos.Y) > broadRange) { continue; }

                foreach (Limb limb in character.AnimController.Limbs)
                {
                    if (limb.IsSevered || !limb.body.Enabled) { continue; }
                    float distSqr = Vector2.DistanceSquared(limb.WorldPosition, propellerWorldPos);
                    if (distSqr > scaledDamageRange * scaledDamageRange) { continue; }
                    character.LastDamageSource = item;
                    propellerDamage.DoDamage(null, character, propellerWorldPos, 1.0f, true);
                    break;
                }
            }
        }

        partial void UpdateAnimation(float deltaTime);
        
        public override void UpdateBroken(float deltaTime, Camera cam)
        {
            base.UpdateBroken(deltaTime, cam);
            force = MathHelper.Lerp(force, 0.0f, 0.1f);
        }

        public override void FlipX(bool relativeToSub)
        {
            PropellerPos = new Vector2(-PropellerPos.X, PropellerPos.Y);
        }

        public override void FlipY(bool relativeToSub)
        {
            PropellerPos = new Vector2(PropellerPos.X, -PropellerPos.Y);
        }

        public override void ReceiveSignal(Signal signal, Connection connection)
        {
            base.ReceiveSignal(signal, connection);

            if (connection.Name == "set_force")
            {
                if (float.TryParse(signal.value, NumberStyles.Float, CultureInfo.InvariantCulture, out float tempForce))
                {
                    controlLockTimer = 0.1f;
                    lastReceivedTargetForce = MathHelper.Clamp(tempForce, -100.0f, 100.0f);
                    User = signal.sender;
                }
            }  
        }

        public override XElement Save(XElement parentElement)
        {
            Vector2 prevPropellerPos = PropellerPos;
            //undo flipping before saving
            if (item.FlippedX) { PropellerPos = new Vector2(-PropellerPos.X, PropellerPos.Y); }
            if (item.FlippedY) { PropellerPos = new Vector2(PropellerPos.X, -PropellerPos.Y); }
            XElement element = base.Save(parentElement);
            PropellerPos = prevPropellerPos;
            return element;
        }
    }
}

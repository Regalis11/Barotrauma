using Microsoft.Xna.Framework;

namespace Barotrauma
{
    class HumanWalkParams : HumanGroundedParams
    {
        public static HumanWalkParams GetDefaultAnimParams(Character character) => GetDefaultAnimParams<HumanWalkParams>(character, AnimationType.Walk);
        public static HumanWalkParams GetAnimParams(Character character, Either<string, ContentPath> file, bool throwErrors = true)
        {
            return GetAnimParams<HumanWalkParams>(character, AnimationType.Walk, file, throwErrors);
        }

        public override void StoreSnapshot() => StoreSnapshot<HumanWalkParams>();
    }

    class HumanRunParams : HumanGroundedParams
    {
        public static HumanRunParams GetDefaultAnimParams(Character character) => GetDefaultAnimParams<HumanRunParams>(character, AnimationType.Run);
        public static HumanRunParams GetAnimParams(Character character, Either<string, ContentPath> file, bool throwErrors = true)
        {
            return GetAnimParams<HumanRunParams>(character, AnimationType.Run, file, throwErrors);
        }

        public override void StoreSnapshot() => StoreSnapshot<HumanRunParams>();
    }

    class HumanCrouchParams : HumanGroundedParams
    {
        [Serialize(0.0f, IsPropertySaveable.Yes, description: "How much lower the character's head and torso move when stationary."), Editable(MinValueFloat = 0, MaxValueFloat = 2, DecimalCount = 2)]
        public float MoveDownAmountWhenStationary { get; set; }

        [Serialize(0.0f, IsPropertySaveable.Yes), Editable(-360f, 360f)]
        public float ExtraHeadAngleWhenStationary { get; set; }

        [Serialize(0.0f, IsPropertySaveable.Yes), Editable(-360f, 360f)]
        public float ExtraTorsoAngleWhenStationary { get; set; }

        public static HumanCrouchParams GetDefaultAnimParams(Character character) => GetDefaultAnimParams<HumanCrouchParams>(character, AnimationType.Crouch);
        public static HumanCrouchParams GetAnimParams(Character character, Either<string, ContentPath> file, bool throwErrors = true)
        {
            return GetAnimParams<HumanCrouchParams>(character, AnimationType.Crouch, file, throwErrors);
        }

        public override void StoreSnapshot() => StoreSnapshot<HumanCrouchParams>();
    }

    class HumanSwimFastParams: HumanSwimParams
    {
        public static HumanSwimFastParams GetDefaultAnimParams(Character character) => GetDefaultAnimParams<HumanSwimFastParams>(character, AnimationType.SwimFast);
        public static HumanSwimFastParams GetAnimParams(Character character, Either<string, ContentPath> file, bool throwErrors = true)
        {
            return GetAnimParams<HumanSwimFastParams>(character, AnimationType.SwimFast, file, throwErrors);
        }


        public override void StoreSnapshot() => StoreSnapshot<HumanSwimFastParams>();
    }

    class HumanSwimSlowParams : HumanSwimParams
    {
        public static HumanSwimSlowParams GetDefaultAnimParams(Character character) => GetDefaultAnimParams<HumanSwimSlowParams>(character, AnimationType.SwimSlow);
        public static HumanSwimSlowParams GetAnimParams(Character character, Either<string, ContentPath> file, bool throwErrors = true)
        {
            return GetAnimParams<HumanSwimSlowParams>(character, AnimationType.SwimSlow, file, throwErrors);
        }

        public override void StoreSnapshot() => StoreSnapshot<HumanSwimSlowParams>();
    }

    abstract class HumanSwimParams : SwimParams, IHumanAnimation
    {
        [Header("Legs")]
        [Serialize(0.5f, IsPropertySaveable.Yes), Editable(DecimalCount = 2)]
        public float LegMoveAmount { get; set; }

        [Serialize(5.0f, IsPropertySaveable.Yes), Editable]
        public float LegCycleLength { get; set; }

        /// <summary>
        /// In degrees.
        /// </summary>
        [Serialize(0.0f, IsPropertySaveable.Yes), Editable(-360f, 360f)]
        public float FootAngle
        {
            get => MathHelper.ToDegrees(FootAngleInRadians);
            set
            {
                FootAngleInRadians = MathHelper.ToRadians(value);
            }
        }

        public float FootAngleInRadians { get; private set; }

        [Header("Arms")]
        [Serialize("0.5, 0.1", IsPropertySaveable.Yes), Editable(DecimalCount = 2)]
        public Vector2 HandMoveAmount { get; set; }

        [Serialize(5.0f, IsPropertySaveable.Yes), Editable]
        public float HandCycleSpeed { get; set; }

        [Serialize("0.0, 0.0", IsPropertySaveable.Yes), Editable(DecimalCount = 2)]
        public Vector2 HandMoveOffset { get; set; }

        [Serialize(1f, IsPropertySaveable.Yes, description: "How much force is used to move the arms."), Editable(MinValueFloat = 0, MaxValueFloat = 20, DecimalCount = 2)]
        public float ArmMoveStrength { get; set; }

        [Serialize(1f, IsPropertySaveable.Yes, description: "How much force is used to move the hands."), Editable(MinValueFloat = 0, MaxValueFloat = 10, DecimalCount = 2)]
        public float HandMoveStrength { get; set; }

        [Header("Other")]
        [Serialize(true, IsPropertySaveable.Yes, description: "Is the head angle fixed or does the angle follow the mouse position?"), Editable]
        public bool FixedHeadAngle { get; set; }
    }

    abstract class HumanGroundedParams : GroundedMovementParams, IHumanAnimation
    {
        [Header("Standing")]
        [Serialize(0.3f, IsPropertySaveable.Yes, description: "How much force is used to force the character upright."), Editable(MinValueFloat = 0, MaxValueFloat = 1, DecimalCount = 2)]
        public float GetUpForce { get; set; }

        [Serialize(0.25f, IsPropertySaveable.Yes, description: "How much the character's head leans forwards when moving."), Editable(DecimalCount = 2)]
        public float HeadLeanAmount { get; set; }

        [Serialize(0.25f, IsPropertySaveable.Yes, description: "How much the character's torso leans forwards when moving."), Editable(DecimalCount = 2)]
        public float TorsoLeanAmount { get; set; }

        [Header("Legs")]
        [Serialize(15.0f, IsPropertySaveable.Yes, description: "How much force is used to move the feet to the correct position."), Editable(MinValueFloat = 0, MaxValueFloat = 100)]
        public float FootMoveStrength { get; set; }

        [Serialize(0f, IsPropertySaveable.Yes, description: "How much the horizontal difference of waist and the foot positions has an effect to lifting the foot."), Editable(DecimalCount = 2, ValueStep = 0.1f, MinValueFloat = 0f, MaxValueFloat = 1f)]
        public float FootLiftHorizontalFactor { get; set; }

        [Serialize("0,0", IsPropertySaveable.Yes, description: "Normally the character's feet are positioned at a scaled-down version of it's normal step position - this can be used to override that value if you want to e.g. make the character to spread out it's feet more when standing."), Editable(DecimalCount = 2, ValueStep = 0.01f)]
        public Vector2 StepSizeWhenStanding
        {
            get;
            set;
        }

        /// <summary>
        /// In degrees.
        /// </summary>
        [Serialize(0.0f, IsPropertySaveable.Yes), Editable(-360f, 360f)]
        public float FootAngle
        {
            get => MathHelper.ToDegrees(FootAngleInRadians);
            set
            {
                FootAngleInRadians = MathHelper.ToRadians(value);
            }
        }
        public float FootAngleInRadians { get; private set; }

        [Serialize("0.0, 0.0", IsPropertySaveable.Yes, description: "Added to the calculated foot positions, e.g. a value of {-1.0, 0.0f} would make the character \"drag\" their feet one unit behind them."), Editable(DecimalCount = 2)]
        public Vector2 FootMoveOffset { get; set; }

        [Serialize(10.0f, IsPropertySaveable.Yes, description: "How much torque is used to bend the characters legs when taking a step."), Editable(MinValueFloat = 0, MaxValueFloat = 100)]
        public float LegBendTorque { get; set; }

        [Header("Arms")]
        [Serialize("0.4, 0.15", IsPropertySaveable.Yes, description: "How much the hands move along each axis."), Editable(DecimalCount = 2)]
        public Vector2 HandMoveAmount { get; set; }

        [Serialize("-0.15, 0.0", IsPropertySaveable.Yes, description: "Added to the calculated hand positions, e.g. a value of {-1.0, 0.0f} would make the character \"drag\" their hands one unit behind them."), Editable(DecimalCount = 2)]
        public Vector2 HandMoveOffset { get; set; }

        [Serialize(false, IsPropertySaveable.Yes, description: "Should the Hand Move Offset values affect the hands even when stationary?"), Editable]
        public bool ApplyHandMoveOffsetWhenStationary { get; set; }

        [Serialize(-1.0f, IsPropertySaveable.Yes, description: "The position of the hands is clamped below this (relative to the position of the character's torso)."), Editable(DecimalCount = 2)]
        public float HandClampY { get; set; }

        [Serialize(1f, IsPropertySaveable.Yes, description: "How much force is used to move the arms."), Editable(MinValueFloat = 0, MaxValueFloat = 10, DecimalCount = 2)]
        public float ArmMoveStrength { get; set; }

        [Serialize(1f, IsPropertySaveable.Yes, description: "How much force is used to move the hands."), Editable(MinValueFloat = 0, MaxValueFloat = 10, DecimalCount = 2)]
        public float HandMoveStrength { get; set; }

        [Header("Other")]
        [Serialize(true, IsPropertySaveable.Yes, description: "Is the head angle fixed or does the angle follow the mouse position?"), Editable]
        public bool FixedHeadAngle { get; set; }
    }

    public interface IHumanAnimation
    {
        float FootAngle { get; set; }
        float FootAngleInRadians { get; }

        float ArmMoveStrength { get; set; }

        float HandMoveStrength { get; set; }

        bool FixedHeadAngle { get; set; }
    }
}

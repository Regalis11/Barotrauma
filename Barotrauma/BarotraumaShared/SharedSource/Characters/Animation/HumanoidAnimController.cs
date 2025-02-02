using Barotrauma.Extensions;
using Barotrauma.Items.Components;
using Barotrauma.Networking;
using FarseerPhysics;
using Microsoft.Xna.Framework;
using System;
using System.Linq;

namespace Barotrauma
{
    class HumanoidAnimController : AnimController
    {
        private const float SteepestWalkableSlopeAngleDegrees = 55f;
        private const float SlowlyWalkableSlopeAngleDegrees = 30f;

        private static readonly float SteepestWalkableSlopeNormalX = MathF.Sin(MathHelper.ToRadians(SteepestWalkableSlopeAngleDegrees));
        private static readonly float SlowlyWalkableSlopeNormalX = MathF.Sin(MathHelper.ToRadians(SlowlyWalkableSlopeAngleDegrees));

        private const float MaxSpeedOnStairs = 1.7f;
        private const float SteepSlopePushMagnitude = MaxSpeedOnStairs;

        public const float BreakFromGrabDistance = 1.4f;

        public override RagdollParams RagdollParams
        {
            get { return HumanRagdollParams; }
            protected set { HumanRagdollParams = value as HumanRagdollParams; }
        }

        private HumanRagdollParams _ragdollParams;
        public HumanRagdollParams HumanRagdollParams
        {
            get
            {
                if (character.Info == null)
                {
                    if (_ragdollParams == null)
                    {
                        _ragdollParams = HumanRagdollParams.GetDefaultRagdollParams(character);
                    }
                    return _ragdollParams;
                }
                return character.Info.Ragdoll as HumanRagdollParams;                
            }
            protected set
            {
                if (character.Info == null)
                {
                    _ragdollParams = value;
                }
                else
                {
                    character.Info.Ragdoll = value;
                }
            }
        }

        private HumanWalkParams _humanWalkParams;
        public HumanWalkParams HumanWalkParams
        {
            get
            {
                if (_humanWalkParams == null)
                {
                    _humanWalkParams = HumanWalkParams.GetDefaultAnimParams(character);
                }
                return _humanWalkParams;
            }
            set { _humanWalkParams = value; }
        }

        private HumanRunParams _humanRunParams;
        public HumanRunParams HumanRunParams
        {
            get
            {
                if (_humanRunParams == null)
                {
                    _humanRunParams = HumanRunParams.GetDefaultAnimParams(character);
                }
                return _humanRunParams;
            }
            set { _humanRunParams = value; }
        }

        private HumanCrouchParams _humanCrouchParams;
        public HumanCrouchParams HumanCrouchParams
        {
            get
            {
                if (_humanCrouchParams == null)
                {
                    _humanCrouchParams = HumanCrouchParams.GetDefaultAnimParams(character);
                }
                return _humanCrouchParams;
            }
            set { _humanCrouchParams = value; }
        }

        private HumanSwimSlowParams _humanSwimSlowParams;
        public HumanSwimSlowParams HumanSwimSlowParams
        {
            get
            {
                if (_humanSwimSlowParams == null)
                {
                    _humanSwimSlowParams = HumanSwimSlowParams.GetDefaultAnimParams(character);
                }
                return _humanSwimSlowParams;
            }
            set { _humanSwimSlowParams = value; }
        }

        private HumanSwimFastParams _humanSwimFastParams;
        public HumanSwimFastParams HumanSwimFastParams
        {
            get
            {
                if (_humanSwimFastParams == null)
                {
                    _humanSwimFastParams = HumanSwimFastParams.GetDefaultAnimParams(character);
                }
                return _humanSwimFastParams;
            }
            set { _humanSwimFastParams = value; }
        }

        public new HumanGroundedParams CurrentGroundedParams => base.CurrentGroundedParams as HumanGroundedParams;

        public new HumanSwimParams CurrentSwimParams => base.CurrentSwimParams as HumanSwimParams;

        public IHumanAnimation CurrentHumanAnimParams => CurrentAnimationParams as IHumanAnimation;

        public override GroundedMovementParams WalkParams
        {
            get { return HumanWalkParams; }
            set { HumanWalkParams = value as HumanWalkParams; }
        }

        public override GroundedMovementParams RunParams
        {
            get { return HumanRunParams; }
            set { HumanRunParams = value as HumanRunParams; }
        }

        public override SwimParams SwimSlowParams
        {
            get { return HumanSwimSlowParams; }
            set { HumanSwimSlowParams = value as HumanSwimSlowParams; }
        }

        public override SwimParams SwimFastParams
        {
            get { return HumanSwimFastParams; }
            set { HumanSwimFastParams = value as HumanSwimFastParams; }
        }

        public bool Crouching { get; set; }

        private float upperLegLength = 0.0f, lowerLegLength = 0.0f;

        private readonly float movementLerp;

        private float cprAnimTimer, cprPumpTimer;

        private float fallingProneAnimTimer;
        const float FallingProneAnimDuration = 1.0f;

        private bool swimming;
        //time until the character can switch from walking to swimming or vice versa
        //prevents rapid switches between swimming/walking if the water level is fluctuating around the minimum swimming depth
        private float swimmingStateLockTimer;

        public float HeadLeanAmount => CurrentGroundedParams.HeadLeanAmount;
        public float TorsoLeanAmount => CurrentGroundedParams.TorsoLeanAmount;
        public Vector2 FootMoveOffset => CurrentGroundedParams.FootMoveOffset * RagdollParams.JointScale;
        public float LegBendTorque => CurrentGroundedParams.LegBendTorque * RagdollParams.JointScale;
        public Vector2 HandMoveOffset => CurrentGroundedParams.HandMoveOffset * RagdollParams.JointScale;

        public override Vector2 AimSourceSimPos
        {
            get
            {
                float shoulderHeight = Collider.Height / 2.0f;
                if (inWater)
                {
                    shoulderHeight += 0.4f;
                }
                else if (Crouching)
                {
                    shoulderHeight -= 0.15f;
                    if (Crouching)
                    {
                        bool movingHorizontally = !MathUtils.NearlyEqual(TargetMovement.X, 0.0f);
                        if (!movingHorizontally)
                        {
                            shoulderHeight -= HumanCrouchParams.MoveDownAmountWhenStationary;
                        }
                    }
                }

                return Collider.SimPosition + new Vector2(
                    (float)Math.Sin(-Collider.Rotation),
                    (float)Math.Cos(-Collider.Rotation)) * shoulderHeight;
            }
        }

        public HumanoidAnimController(Character character, string seed, HumanRagdollParams ragdollParams = null) : base(character, seed, ragdollParams)
        {
            // TODO: load from the character info file?
            movementLerp = RagdollParams?.MainElement?.GetAttributeFloat("movementlerp", 0.4f) ?? 0f;
        }

        public override void Recreate(RagdollParams ragdollParams = null)
        {
            base.Recreate(ragdollParams);
            CalculateLegLengths();
        }

        private void CalculateLegLengths()
        {
            //calculate upper and lower leg length (atm this assumes that both legs are the same size)
            LimbType upperLegType = LimbType.RightThigh;
            LimbType lowerLegType = LimbType.RightLeg;
            LimbType footType = LimbType.RightFoot;

            var waistJoint = GetJointBetweenLimbs(LimbType.Waist, upperLegType) ?? GetJointBetweenLimbs(LimbType.Torso, upperLegType);
            Vector2 localAnchorWaist = Vector2.Zero;
            Vector2 localAnchorKnee = Vector2.Zero;
            if (waistJoint != null)
            {
                localAnchorWaist = waistJoint.LimbA.type == upperLegType ? waistJoint.LocalAnchorA : waistJoint.LocalAnchorB;
            }
            LimbJoint kneeJoint = GetJointBetweenLimbs(upperLegType, lowerLegType);
            if (kneeJoint != null)
            {
                localAnchorKnee = kneeJoint.LimbA.type == upperLegType ? kneeJoint.LocalAnchorA : kneeJoint.LocalAnchorB;
            }
            upperLegLength = Vector2.Distance(localAnchorWaist, localAnchorKnee);

            LimbJoint ankleJoint = GetJointBetweenLimbs(lowerLegType, footType);
            if (ankleJoint == null || kneeJoint == null) { return; }
            lowerLegLength = Vector2.Distance(
                kneeJoint.LimbA.type == lowerLegType ? kneeJoint.LocalAnchorA : kneeJoint.LocalAnchorB,
                ankleJoint.LimbA.type == lowerLegType ? ankleJoint.LocalAnchorA : ankleJoint.LocalAnchorB);
            lowerLegLength += Vector2.Distance(
                ankleJoint.LimbA.type == footType ? ankleJoint.LocalAnchorA : ankleJoint.LocalAnchorB,
                GetLimb(footType).PullJointLocalAnchorA);
        }

        protected override void UpdateAnim(float deltaTime)
        {
            if (Frozen) { return; }
            if (MainLimb == null) { return; }
            UpdateConstantTorque(deltaTime);
            UpdateBlink(deltaTime);
            levitatingCollider = !IsHangingWithRope;
            if (onGround && character.CanMove)
            {
                if ((character.SelectedItem?.GetComponent<Controller>()?.ControlCharacterPose ?? false) ||
                    (character.SelectedSecondaryItem?.GetComponent<Controller>()?.ControlCharacterPose ?? false) ||
                    character.SelectedSecondaryItem?.GetComponent<Ladder>() != null ||
                    (ForceSelectAnimationType != AnimationType.Crouch && ForceSelectAnimationType != AnimationType.NotDefined))
                {
                    Crouching = false;
                }
                ColliderIndex = Crouching && !swimming ? 1 : 0;
            }

            //stun (= disable the animations) if the ragdoll receives a large enough impact
            if (strongestImpact > 0.0f)
            {
                character.SetStun(MathHelper.Min(strongestImpact * 0.5f, 5.0f));
                strongestImpact = 0.0f;
                return;
            }

            if (character.IsDead)
            {
                if (deathAnimTimer < deathAnimDuration)
                {
                    deathAnimTimer += deltaTime;
                    //the force/torque used to move the limbs goes from 1 to 0 during the death anim duration
                    UpdateFallingProne(1.0f - deathAnimTimer / deathAnimDuration);
                }
            }
            else
            {
                deathAnimTimer = 0.0f;
            } 

            if (!character.CanMove)
            {
                if (fallingProneAnimTimer < FallingProneAnimDuration && onGround)
                {
                    fallingProneAnimTimer += deltaTime;
                    UpdateFallingProne(1.0f);
                }
                UpdateRagdollControlsMovement();
                return;
            }
            fallingProneAnimTimer = 0.0f;

            //re-enable collider
            if (!Collider.Enabled)
            {
                var lowestLimb = FindLowestLimb();

                Collider.SetTransformIgnoreContacts(new Vector2(
                    Collider.SimPosition.X,
                    Math.Max(lowestLimb.SimPosition.Y + (Collider.Radius + Collider.Height / 2), Collider.SimPosition.Y)),
                    Collider.Rotation);
                
                Collider.FarseerBody.ResetDynamics();
                Collider.FarseerBody.LinearVelocity = MainLimb.LinearVelocity;
                Collider.Enabled = true;
            }            

            if (swimming)
            {
                Collider.FarseerBody.FixedRotation = false;
            }
            else if (!Collider.FarseerBody.FixedRotation)
            {
                if (Math.Abs(MathUtils.GetShortestAngle(Collider.Rotation, 0.0f)) > 0.001f)
                {
                    //rotate collider back upright
                    Collider.AngularVelocity = MathUtils.GetShortestAngle(Collider.Rotation, 0.0f) * 10.0f;
                    Collider.FarseerBody.FixedRotation = false;
                }
                else
                {
                    Collider.FarseerBody.FixedRotation = true;
                }
            }
            else
            {
                float angleDiff = MathUtils.GetShortestAngle(Collider.Rotation, 0.0f);
                if (Math.Abs(angleDiff) > 0.001f)
                {
                    Collider.SetTransformIgnoreContacts(Collider.SimPosition, Collider.Rotation + angleDiff);
                }
            }
             
            if (character.AnimController.AnimationTestPose)
            {
                ApplyTestPose();
            }
            else if (character.SelectedBy == null)
            {
                if (character.LockHands)
                {
                    var leftHand = GetLimb(LimbType.LeftHand);
                    var rightHand = GetLimb(LimbType.RightHand);

                    var waist = GetLimb(LimbType.Waist) ?? GetLimb(LimbType.Torso);

                    rightHand.Disabled = true;
                    leftHand.Disabled = true;

                    Vector2 midPos = waist.SimPosition;
                    Matrix torsoTransform = Matrix.CreateRotationZ(waist.Rotation);

                    midPos += Vector2.Transform(new Vector2(-0.3f * Dir, -0.2f), torsoTransform);
                    if (rightHand.PullJointEnabled) midPos = (midPos + rightHand.PullJointWorldAnchorB) / 2.0f;
                    HandIK(rightHand, midPos, CurrentAnimationParams.ArmIKStrength, CurrentAnimationParams.HandIKStrength);
                    HandIK(leftHand, midPos, CurrentAnimationParams.ArmIKStrength, CurrentAnimationParams.HandIKStrength);
                }
                if (Anim != Animation.UsingItem)
                {
                    if (Anim != Animation.UsingItemWhileClimbing)
                    {
                        ResetPullJoints();
                    }
                    else
                    {
                        ResetPullJoints(l => l.IsLowerBody);
                    }
                }
            }

            if (SimplePhysicsEnabled)
            {
                UpdateStandingSimple();
                StopHangingWithRope();
                StopHoldingToRope();
                StopGettingDraggedWithRope();
                return;
            }

            if (character.SelectedCharacter != null)
            {
                DragCharacter(character.SelectedCharacter, deltaTime);
            }

            if (Anim != Animation.CPR)
            {
                cprAnimTimer = 0.0f;
                cprPumpTimer = 0.0f;
            }

            switch (Anim)
            {
                case Animation.Climbing:
                case Animation.UsingItemWhileClimbing:
                    levitatingCollider = false;
                    UpdateClimbing();
                    UpdateUseItemTimer();
                    break;
                case Animation.CPR:
                    UpdateCPR(deltaTime);
                    break;
                case Animation.UsingItem:
                default:
                    UpdateUseItemTimer();
                    swimmingStateLockTimer -= deltaTime;
                    if (forceStanding || character.AnimController.AnimationTestPose)
                    {
                        swimming = false;
                    }
                    else if (swimming != inWater && swimmingStateLockTimer <= 0.0f)
                    {
                        //0.5 second delay for switching between swimming and walking
                        //prevents rapid switches between swimming/walking if the water level is fluctuating around the minimum swimming depth
                        swimming = inWater;
                        swimmingStateLockTimer = 0.5f;
                    }
                    if (character.SelectedItem?.Prefab is { GrabWhenSelected: true } && 
                        character.SelectedItem.ParentInventory == null &&
                        character.SelectedItem.body is not { Enabled: true } &&
                        character.SelectedItem.GetComponent<Repairable>()?.CurrentFixer != character)
                    {
                        bool moving = character.IsKeyDown(InputType.Left) || character.IsKeyDown(InputType.Right);
                        moving |= (character.InWater || character.IsClimbing) && (character.IsKeyDown(InputType.Up) || character.IsKeyDown(InputType.Down));
                        if (!moving)
                        {
                            Vector2 handPos = character.SelectedItem.WorldPosition - Vector2.UnitY * ConvertUnits.ToDisplayUnits(ArmLength / 2);
                            handPos.Y = Math.Max(handPos.Y, character.SelectedItem.WorldRect.Y - character.SelectedItem.WorldRect.Height);
                            UpdateUseItem(
                                allowMovement: false,
                                handPos);
                        }
                    }
                    if (swimming)
                    {
                        UpdateSwimming();
                    }
                    else if (character.SelectedItem == null || !(character.SelectedSecondaryItem?.GetComponent<Controller>() is { } controller) ||
                             !controller.ControlCharacterPose || !controller.UserInCorrectPosition)
                    {
                        UpdateStanding();
                    }
                    break;
            }

            void UpdateUseItemTimer()
            {
                if (IsUsingItem)
                {
                    useItemTimer -= deltaTime;
                    if (useItemTimer <= 0.0f)
                    {
                        StopUsingItem();
                    }
                }
            }

            if (Timing.TotalTime > FlipLockTime && TargetDir != dir && !IsStuck)
            {
                Flip();
            }

            foreach (Limb limb in Limbs)
            {
                limb.Disabled = false;
            }
            wasAiming = aiming;
            aiming = false;
            wasAimingMelee = aimingMelee;
            aimingMelee = false;
            if (!shouldHangWithRope)
            {
                StopHangingWithRope();
            }
            if (!shouldHoldToRope)
            {
                StopHoldingToRope();
            }
            if (!shouldBeDraggedWithRope)
            {
                StopGettingDraggedWithRope();
            }
            shouldHoldToRope = false;
            shouldHangWithRope = false;
            shouldBeDraggedWithRope = false;
        }

        void UpdateStanding()
        {
            var currentGroundedParams = CurrentGroundedParams;
            if (currentGroundedParams == null) { return; }
            Vector2 handPos;

            Limb leftFoot = GetLimb(LimbType.LeftFoot);
            Limb rightFoot = GetLimb(LimbType.RightFoot);
            Limb head = GetLimb(LimbType.Head);
            Limb torso = GetLimb(LimbType.Torso);

            Limb waist = GetLimb(LimbType.Waist);

            Limb leftHand = GetLimb(LimbType.LeftHand);
            Limb rightHand = GetLimb(LimbType.RightHand);

            Limb leftLeg = GetLimb(LimbType.LeftLeg);
            Limb rightLeg = GetLimb(LimbType.RightLeg);

            bool onSlopeThatMakesSlow = Math.Abs(floorNormal.X) > SlowlyWalkableSlopeNormalX;
            bool slowedDownBySlope = onSlopeThatMakesSlow && Math.Sign(floorNormal.X) == -Math.Sign(TargetMovement.X);
            bool onSlopeTooSteepToClimb = Math.Abs(floorNormal.X) > SteepestWalkableSlopeNormalX;

            float walkCycleMultiplier = 1.0f;
            if (Stairs != null || slowedDownBySlope)
            {
                TargetMovement = new Vector2(MathHelper.Clamp(TargetMovement.X, -MaxSpeedOnStairs, MaxSpeedOnStairs), TargetMovement.Y);
                walkCycleMultiplier *= 1.5f;                
            }

            float getUpForce = currentGroundedParams.GetUpForce / RagdollParams.JointScale;

            Vector2 colliderPos = GetColliderBottom();
            if (Math.Abs(TargetMovement.X) > 1.0f)
            {
                float slowdownAmount = 0.0f;
                if (currentHull != null)
                {
                    //TODO: take into account that the feet aren't necessarily in CurrentHull
                    //full slowdown (1.5f) when water is up to the torso
                    surfaceY = ConvertUnits.ToSimUnits(currentHull.Surface);
                    float bottomPos = Math.Max(colliderPos.Y, ConvertUnits.ToSimUnits(currentHull.Rect.Y - currentHull.Rect.Height));
                    slowdownAmount = MathHelper.Clamp((surfaceY - bottomPos) / TorsoPosition.Value, 0.0f, 1.0f) * 1.5f;
                }

                float maxSpeed = Math.Max(TargetMovement.Length() - slowdownAmount, 1.0f);
                TargetMovement = Vector2.Normalize(TargetMovement) * maxSpeed;
            }

            float walkPosX = (float)Math.Cos(WalkPos);
            float walkPosY = (float)Math.Sin(WalkPos);
            
            Vector2 stepSize = StepSize.Value;
            stepSize.X *= walkPosX;
            stepSize.Y *= walkPosY;

            float footMid = colliderPos.X;

            var herpes = character.CharacterHealth.GetAffliction("spaceherpes", false);
            float herpesAmount = herpes == null ? 0 : herpes.Strength / herpes.Prefab.MaxStrength;
            float legDamage = character.GetLegPenalty(startSum: -0.1f) * 1.1f;
            float limpAmount = MathHelper.Lerp(0, 1, legDamage + herpesAmount);
            if (limpAmount > 0.0f)
            {
                //make the footpos oscillate when limping
                footMid += (Math.Max(Math.Abs(walkPosX) * limpAmount, 0.0f) * Math.Min(Math.Abs(TargetMovement.X), 0.3f)) * Dir;
            }

            movement = overrideTargetMovement ?? MathUtils.SmoothStep(movement, TargetMovement, movementLerp);

            if (Math.Abs(movement.X) < 0.005f)
            {
                movement.X = 0.0f;
            }

            movement.Y = 0.0f;

            if (head == null) { return; }
            if (torso == null) { return; }

            bool isNotRemote = true;
            if (GameMain.NetworkMember != null && GameMain.NetworkMember.IsClient) { isNotRemote = !character.IsRemotelyControlled; }

            if (onGround && isNotRemote)
            {
                //move slower if collider isn't upright
                float rotationFactor = (float)Math.Abs(Math.Cos(Collider.Rotation));

                Collider.LinearVelocity = new Vector2(
                        movement.X * rotationFactor,
                        Collider.LinearVelocity.Y > 0.0f ? Collider.LinearVelocity.Y * 0.5f : Collider.LinearVelocity.Y);
            }

            getUpForce *= Math.Max(head.SimPosition.Y - colliderPos.Y, 0.5f);

            torso.PullJointEnabled = true;
            head.PullJointEnabled = true;
            if (waist != null)
            {
                waist.PullJointEnabled = true;
            }
            
            bool onSlope = Math.Abs(movement.X) > 0.01f && Math.Abs(floorNormal.X) > 0.1f && Math.Sign(floorNormal.X) != Math.Sign(movement.X);

            bool movingHorizontally = !MathUtils.NearlyEqual(TargetMovement.X, 0.0f);

            if (Stairs == null && onSlopeTooSteepToClimb)
            {
                if (Math.Sign(targetMovement.X) != Math.Sign(floorNormal.X))
                {
                    targetMovement.X = Math.Sign(floorNormal.X) * SteepSlopePushMagnitude;
                    movement = targetMovement;
                }
            }

            if (Stairs != null || onSlope)
            {
                torso.PullJointWorldAnchorB = new Vector2(
                    MathHelper.SmoothStep(torso.SimPosition.X, footMid + movement.X * TorsoLeanAmount, getUpForce * 0.8f),
                    MathHelper.SmoothStep(torso.SimPosition.Y, colliderPos.Y + TorsoPosition.Value - Math.Abs(walkPosX * 0.05f), getUpForce * 2.0f));

                head.PullJointWorldAnchorB = new Vector2(
                    MathHelper.SmoothStep(head.SimPosition.X, footMid + movement.X * HeadLeanAmount, getUpForce * 0.8f),
                    MathHelper.SmoothStep(head.SimPosition.Y, colliderPos.Y + HeadPosition.Value - Math.Abs(walkPosX * 0.05f), getUpForce * 2.0f));

                if (waist != null)
                {
                    waist.PullJointWorldAnchorB = waist.SimPosition - movement * 0.06f;
                }
            }
            else
            {
                if (!onGround)
                {
                    movement = Vector2.Zero;
                }

                float offset = MathHelper.Pi * currentGroundedParams.StepLiftOffset;
                if (character.AnimController.Dir < 0)
                {
                    offset += MathHelper.Pi * currentGroundedParams.StepLiftFrequency;
                }
                float stepLift = TargetMovement.X == 0.0f ? 0 : 
                    (float)Math.Sin(WalkPos * Dir * currentGroundedParams.StepLiftFrequency + offset) * (currentGroundedParams.StepLiftAmount / 100);

                float y = colliderPos.Y + stepLift;

                if (!torso.Disabled)
                {
                    if (TorsoPosition.HasValue)  { y += TorsoPosition.Value;  }
                    if (Crouching && !movingHorizontally) { y -= HumanCrouchParams.MoveDownAmountWhenStationary; }
                    torso.PullJointWorldAnchorB =
                        MathUtils.SmoothStep(torso.SimPosition,
                        new Vector2(footMid + movement.X * TorsoLeanAmount, y), getUpForce);
                }

                if (!head.Disabled)
                {
                    y = colliderPos.Y + stepLift * currentGroundedParams.StepLiftHeadMultiplier;
                    if (HeadPosition.HasValue) { y += HeadPosition.Value; }
                    if (Crouching && !movingHorizontally) { y -= HumanCrouchParams.MoveDownAmountWhenStationary; }
                    head.PullJointWorldAnchorB =
                        MathUtils.SmoothStep(head.SimPosition,
                        new Vector2(footMid + movement.X * HeadLeanAmount, y), getUpForce * 1.2f);
                }

                if (waist != null && !waist.Disabled)
                {
                    waist.PullJointWorldAnchorB = waist.SimPosition + movement * 0.06f;
                }
            }

            if (TorsoAngle.HasValue && !torso.Disabled)
            {
                float torsoAngle = TorsoAngle.Value;
                float herpesStrength = character.CharacterHealth.GetAfflictionStrengthByType(AfflictionPrefab.SpaceHerpesType);
                if (Crouching && !movingHorizontally && !Aiming) { torsoAngle -= HumanCrouchParams.ExtraTorsoAngleWhenStationary; }
                torsoAngle -= herpesStrength / 150.0f;
                torso.body.SmoothRotate(torsoAngle * Dir, currentGroundedParams.TorsoTorque);
            }
            if (!head.Disabled)
            {
                if (!Aiming && currentGroundedParams.FixedHeadAngle && HeadAngle.HasValue)
                {
                    float headAngle = HeadAngle.Value;
                    if (Crouching && !movingHorizontally) { headAngle -= HumanCrouchParams.ExtraHeadAngleWhenStationary; }
                    head.body.SmoothRotate(headAngle * Dir, currentGroundedParams.HeadTorque);
                }
                else
                {
                    RotateHead(head);
                }
            }

            if (!onGround)
            {
                const float MaxFootVelocityDiff = 5.0f;
                const float MaxFootVelocityDiffSqr = MaxFootVelocityDiff * MaxFootVelocityDiff;
                //if the feet have a significantly different velocity from the main limb, try moving them back to a neutral pose below the torso
                //this can happen e.g. when jumping over an obstacle: the feet can have a large upwards velocity during the walk/run animation,
                //and just "letting go of the animations" would let them keep moving upwards, twisting the character to a weird pose
                if ((leftFoot != null && (MainLimb.LinearVelocity - leftFoot.LinearVelocity).LengthSquared() > MaxFootVelocityDiffSqr) ||
                    (rightFoot != null && (MainLimb.LinearVelocity - rightFoot.LinearVelocity).LengthSquared() > MaxFootVelocityDiffSqr))
                {
                    UpdateFallingProne(10.0f, moveHands: false, moveTorso: false, moveLegs: true);
                }
                return;
            }

            Vector2 waistPos = waist != null ? waist.SimPosition : torso.SimPosition;
            int currentDirectionSign = character.IsFlipped ? -1 : 1;

            if (movingHorizontally)
            {
                //progress the walking animation
                WalkPos -= MathHelper.ToRadians(CurrentAnimationParams.CycleSpeed) * walkCycleMultiplier * movement.X;

                for (int i = -1; i < 2; i += 2)
                {
                    Limb foot = i == -1 ? leftFoot : rightFoot;
                    if (foot == null) { continue; }

                    Vector2 footPos = stepSize * -i;
                    footPos += new Vector2(Math.Sign(movement.X) * FootMoveOffset.X, FootMoveOffset.Y);

                    if (footPos.Y < 0.0f) { footPos.Y = -0.15f; }

                    //make the character limp if the feet are damaged
                    float footAfflictionStrength = character.CharacterHealth.GetAfflictionStrength(AfflictionPrefab.DamageType, foot, true);
                    footPos.X *= MathHelper.Lerp(1.0f, 0.75f, MathHelper.Clamp(footAfflictionStrength / 50.0f, 0.0f, 1.0f));

                    if (currentGroundedParams.FootLiftHorizontalFactor > 0)
                    {
                        // Calculate the foot y dynamically based on the foot position relative to the waist,
                        // so that the foot aims higher when it's behind the waist and lower when it's in the front.
                        float xDiff = (foot.SimPosition.X - waistPos.X + FootMoveOffset.X) * Dir;
                        float min = MathUtils.InverseLerp(1, 0, currentGroundedParams.FootLiftHorizontalFactor);
                        float max = 1 + MathUtils.InverseLerp(0, 1, currentGroundedParams.FootLiftHorizontalFactor);
                        float xFactor = MathHelper.Lerp(min, max, MathUtils.InverseLerp(RagdollParams.JointScale, -RagdollParams.JointScale, xDiff));
                        footPos.Y *= xFactor;
                    }

                    if (onSlope && Stairs == null)
                    {
                        footPos.Y *= 2.0f;
                    }
                    footPos.Y = Math.Min(waistPos.Y - colliderPos.Y - 0.4f, footPos.Y);

#if CLIENT
                    if ((i == 1 && Math.Sign(Math.Sin(WalkPos)) > 0 && Math.Sign(walkPosY) < 0) ||
                        (i == -1 && Math.Sign(Math.Sin(WalkPos)) < 0 && Math.Sign(walkPosY) > 0))
                    {
                        PlayImpactSound(foot);
                    }

#endif

                    if (!foot.Disabled)
                    {
                        foot.DebugRefPos = colliderPos;
                        foot.DebugTargetPos = colliderPos + footPos;
                        MoveLimb(foot, colliderPos + footPos, currentGroundedParams.FootMoveStrength);
                        FootIK(foot, colliderPos + footPos, 
                            currentGroundedParams.LegBendTorque, currentGroundedParams.FootTorque, currentGroundedParams.FootAngleInRadians);
                    }
                }
            }
            else
            {
                for (int i = -1; i < 2; i += 2)
                {
                    Vector2 footPos = colliderPos;
                    if (Crouching)
                    {
                        footPos = new Vector2(Math.Sign(stepSize.X * i) * Dir * 0.35f, colliderPos.Y);
                        if (Math.Sign(footPos.X) != Math.Sign(Dir))
                        {
                            //lift the foot at the back up a bit
                            footPos.Y += 0.15f;
                        }
                        footPos.X += colliderPos.X;
                    }
                    else
                    {
                        float footPosX = stepSize.X * i * 0.2f;
                        if (CurrentGroundedParams.StepSizeWhenStanding != Vector2.Zero)
                        {
                            footPosX = Math.Sign(stepSize.X) * CurrentGroundedParams.StepSizeWhenStanding.X * i;
                        }
                        footPos = new Vector2(colliderPos.X + footPosX, colliderPos.Y - 0.1f);
                    }
                    if (Stairs == null && !onSlopeThatMakesSlow)
                    {
                        footPos.Y = Math.Max(Math.Min(FloorY, footPos.Y + 0.5f), footPos.Y);
                    }
                    var foot = i == -1 ? rightFoot : leftFoot;
                    if (foot != null && !foot.Disabled)
                    {
                        foot.DebugRefPos = colliderPos;
                        foot.DebugTargetPos = footPos;
                        float footMoveForce = currentGroundedParams.FootMoveStrength;
                        float legBendTorque = currentGroundedParams.LegBendTorque;
                        if (Crouching)
                        {
                            // Keeps the pose
                            legBendTorque = 100;
                            footMoveForce *= 2;
                        }
                        MoveLimb(foot, footPos, footMoveForce);
                        FootIK(foot, footPos, legBendTorque, currentGroundedParams.FootTorque, currentGroundedParams.FootAngleInRadians);
                    }
                }
            }

            if(!movingHorizontally && !currentGroundedParams.ApplyHandMoveOffsetWhenStationary)
            {
                for (int i = 0; i < 2; i++)
                {
                    var hand = i == 0 ? rightHand : leftHand;
                    if (hand == null || hand.Disabled) { continue; }

                    var armType = i == 0 ? LimbType.RightArm : LimbType.LeftArm;
                    var foreArmType = i == 0 ? LimbType.RightForearm : LimbType.LeftForearm;

                    //get the upper arm to point downwards
                    var arm = GetLimb(armType);
                    if (arm != null && Math.Abs(arm.body.AngularVelocity) < 10.0f)
                    {
                        arm.body.SmoothRotate(MathHelper.Clamp(-arm.body.AngularVelocity, -0.5f, 0.5f), arm.Mass * 50.0f * currentGroundedParams.ArmMoveStrength);
                    }

                    //get the elbow to a neutral rotation
                    if (Math.Abs(hand.body.AngularVelocity) < 10.0f)
                    {
                        var forearm = GetLimb(foreArmType) ?? hand;
                        LimbJoint elbow = GetJointBetweenLimbs(armType, foreArmType) ?? GetJointBetweenLimbs(armType, hand.type);
                        if (elbow != null)
                        {
                            float diff = elbow.JointAngle - (Dir > 0 ? elbow.LowerLimit : elbow.UpperLimit);
                            forearm.body.ApplyTorque(MathHelper.Clamp(-diff, -MathHelper.PiOver2, MathHelper.PiOver2) * forearm.Mass * 100.0f * currentGroundedParams.ArmMoveStrength);
                        }
                    }
                    // Try to keep the wrist straight
                    LimbJoint wrist = GetJointBetweenLimbs(foreArmType, hand.type);
                    if (wrist != null)
                    {
                        hand.body.ApplyTorque(MathHelper.Clamp(-wrist.JointAngle, -MathHelper.PiOver2, MathHelper.PiOver2) * hand.Mass * 100f * currentGroundedParams.HandMoveStrength);
                    }
                }
            }
            else
            {
                //calculate the positions of hands
                handPos = torso.SimPosition;
                handPos.X = -walkPosX * currentGroundedParams.HandMoveAmount.X;

                float lowerY = currentGroundedParams.HandClampY;

                handPos.Y = lowerY + (float)(Math.Abs(Math.Sin(WalkPos - Math.PI * 1.5f) * currentGroundedParams.HandMoveAmount.Y));

                Vector2 posAddition = new Vector2(currentDirectionSign * HandMoveOffset.X, HandMoveOffset.Y);

                if (rightHand != null && !rightHand.Disabled)
                {
                    HandIK(rightHand,
                        torso.SimPosition + posAddition + new Vector2(-handPos.X, (currentDirectionSign == Math.Sign(Dir)) ? handPos.Y : lowerY),
                        currentGroundedParams.ArmMoveStrength, currentGroundedParams.HandMoveStrength);
                }
                if (leftHand != null && !leftHand.Disabled)
                {
                    HandIK(leftHand,
                        torso.SimPosition + posAddition + new Vector2(handPos.X, (currentDirectionSign == Math.Sign(-Dir)) ? handPos.Y : lowerY),
                        currentGroundedParams.ArmMoveStrength, currentGroundedParams.HandMoveStrength);
                }
            }
        }

        void UpdateStandingSimple()
        {
            if (Math.Abs(movement.X) < 0.005f)
            {
                movement.X = 0.0f;
            }
            movement = MathUtils.SmoothStep(movement, TargetMovement, movementLerp);

            if (InWater)
            {
                Collider.LinearVelocity = movement;
            }
            else if (onGround && (!character.IsRemotelyControlled || (GameMain.NetworkMember != null && GameMain.NetworkMember.IsServer)))
            {
                Collider.LinearVelocity = new Vector2(
                        movement.X,
                        Collider.LinearVelocity.Y > 0.0f ? Collider.LinearVelocity.Y * 0.5f : Collider.LinearVelocity.Y);                
            }
        }
        
        private float handCyclePos;
        private float legCyclePos;
        void UpdateSwimming()
        {
            if (CurrentSwimParams == null) { return; }
            IgnorePlatforms = true;

            Vector2 footPos, handPos;

            float surfaceLimiter = 1.0f;

            Limb head = GetLimb(LimbType.Head);
            Limb torso = GetLimb(LimbType.Torso);
            if (head == null) { return; }
            if (torso == null) { return; }

            if (currentHull != null && character.CurrentHull != null)
            {
                float surfacePos = GetSurfaceY();
                float surfaceThreshold = ConvertUnits.ToDisplayUnits(Collider.SimPosition.Y + 1.0f);
                surfaceLimiter = Math.Max(1.0f, surfaceThreshold - surfacePos);
            }          

            Limb leftHand = GetLimb(LimbType.LeftHand);
            Limb rightHand = GetLimb(LimbType.RightHand);

            Limb leftFoot = GetLimb(LimbType.LeftFoot);
            Limb rightFoot = GetLimb(LimbType.RightFoot);
            
            float rotation = MathHelper.WrapAngle(Collider.Rotation);
            rotation = MathHelper.ToDegrees(rotation);
            if (rotation < 0.0f)
            {
                rotation += 360;
            }
            float targetSpeed = TargetMovement.Length();
            if (targetSpeed > 0.1f && !character.IsRemotelyControlled && !Aiming)
            {
                if (!IsUsingItem &&
                    !(character.SelectedItem?.GetComponent<Controller>()?.ControlCharacterPose ?? false) &&
                    !(character.SelectedSecondaryItem?.GetComponent<Controller>()?.ControlCharacterPose ?? false))
                {
                    if (rotation > 20 && rotation < 170)
                    {
                        TargetDir = Direction.Left;
                    }
                    else if (rotation > 190 && rotation < 340)
                    {
                        TargetDir = Direction.Right;
                    }
                }
            }
            if (Aiming)
            {
                Vector2 mousePos = ConvertUnits.ToSimUnits(character.CursorPosition);
                Vector2 diff = (mousePos - torso.SimPosition) * Dir;
                if (diff.LengthSquared() > MathUtils.Pow2(0.4f))
                {
                    float newRotation = MathHelper.WrapAngle(MathUtils.VectorToAngle(diff) - MathHelper.PiOver4 * Dir);
                    Collider.SmoothRotate(newRotation, CurrentSwimParams.SteerTorque * character.SpeedMultiplier);
                }
            }
            else if (targetSpeed > 0.1f)
            {
                float newRotation = MathUtils.VectorToAngle(TargetMovement) - MathHelper.PiOver2;
                Collider.SmoothRotate(newRotation, CurrentSwimParams.SteerTorque * character.SpeedMultiplier);
            }

            torso.body.MoveToPos(Collider.SimPosition + new Vector2((float)Math.Sin(-Collider.Rotation), (float)Math.Cos(-Collider.Rotation)) * 0.4f, 5.0f);

            movement = MathUtils.SmoothStep(movement, TargetMovement, 0.3f);

            if (TorsoAngle.HasValue)
            {
                torso.body.SmoothRotate(Collider.Rotation + TorsoAngle.Value * Dir, CurrentSwimParams.TorsoTorque);
            }
            else
            {
                torso.body.SmoothRotate(Collider.Rotation, CurrentSwimParams.TorsoTorque);
            }

            if (!Aiming && CurrentSwimParams.FixedHeadAngle && HeadAngle.HasValue)
            {
                head.body.SmoothRotate(Collider.Rotation + HeadAngle.Value * Dir, CurrentSwimParams.HeadTorque);
            }
            else if (character.FollowCursor)
            {
                RotateHead(head);
            }

            const float DisableMovementAboveSurfaceThreshold = 50.0f;
            //dont try to move upwards if head is already out of water
            if (surfaceLimiter > 1.0f && TargetMovement.Y > 0.0f)
            {
                if (TargetMovement.X == 0.0f)
                {
                    //pull head above water
                    head.body.SmoothRotate(0.0f, 5.0f);
                    WalkPos += 0.05f;
                }
                else
                {
                    TargetMovement = new Vector2(
                        (float)Math.Sqrt(targetSpeed * targetSpeed - TargetMovement.Y * TargetMovement.Y)
                        * Math.Sign(TargetMovement.X),
                        Math.Max(TargetMovement.Y, TargetMovement.Y * 0.2f));

                    //turn head above the water
                    head.body.ApplyTorque(Dir);
                }
                movement.Y *= Math.Max(0, 1.0f - ((surfaceLimiter - 1.0f) / DisableMovementAboveSurfaceThreshold));

            }

            bool isNotRemote = true;
            if (GameMain.NetworkMember != null && GameMain.NetworkMember.IsClient) { isNotRemote = !character.IsRemotelyControlled; }

            if (isNotRemote)
            {
                float t = movementLerp;
                if (targetSpeed > 0.00001f && !SimplePhysicsEnabled)
                {
                    Vector2 forward = VectorExtensions.Forward(Collider.Rotation + MathHelper.PiOver2);
                    float dot = Vector2.Dot(forward, Vector2.Normalize(movement));
                    if (dot < 0)
                    {
                        // Reduce the linear movement speed when not facing the movement direction
                        t = MathHelper.Clamp((1 + dot) / 10, 0.01f, 0.1f);
                    }
                }
                Vector2 targetVelocity = movement;
                //if we're too high above the surface, don't touch the vertical velocity of the collider unless we're heading down
                if (surfaceLimiter > DisableMovementAboveSurfaceThreshold)
                {
                    targetVelocity.Y = Math.Min(Collider.LinearVelocity.Y, movement.Y);
                };
                Collider.LinearVelocity = Vector2.Lerp(Collider.LinearVelocity, targetVelocity, t);
            }

            WalkPos += movement.Length();
            legCyclePos += Math.Min(movement.LengthSquared() + Collider.AngularVelocity, 1.0f);
            handCyclePos += MathHelper.ToRadians(CurrentSwimParams.HandCycleSpeed) * Math.Sign(movement.X);

            float legMoveMultiplier = 1.0f;
            if (movement.LengthSquared() < 0.001f)
            {
                // Swimming in place (TODO: expose?)
                legMoveMultiplier = 0.3f;
                legCyclePos += 0.4f;
                handCyclePos += 0.1f;
            }

            var waist = GetLimb(LimbType.Waist) ?? GetLimb(LimbType.Torso);
            footPos = waist == null ? Vector2.Zero : waist.SimPosition - new Vector2((float)Math.Sin(-Collider.Rotation), (float)Math.Cos(-Collider.Rotation)) * (upperLegLength + lowerLegLength);
            Vector2 transformedFootPos = new Vector2((float)Math.Sin(legCyclePos / CurrentSwimParams.LegCycleLength) * CurrentSwimParams.LegMoveAmount * legMoveMultiplier, 0.0f);
            transformedFootPos = Vector2.Transform(transformedFootPos, Matrix.CreateRotationZ(Collider.Rotation));

            float legTorque = CurrentSwimParams.LegTorque * character.SpeedMultiplier * (1.2f - character.GetLegPenalty());
            if (rightFoot != null && !rightFoot.Disabled)
            {
                FootIK(rightFoot, footPos - transformedFootPos, legTorque, CurrentSwimParams.FootTorque, CurrentSwimParams.FootAngleInRadians);
            }
            if (leftFoot != null && !leftFoot.Disabled)
            {
                FootIK(leftFoot, footPos + transformedFootPos, legTorque, CurrentSwimParams.FootTorque, CurrentSwimParams.FootAngleInRadians);
            }

            handPos = (torso.SimPosition + head.SimPosition) / 2.0f;

            //at the surface, not moving sideways OR not moving at all
            // -> hands just float around
            if ((!headInWater && TargetMovement.X == 0.0f && TargetMovement.Y > 0) || TargetMovement.LengthSquared() < 0.001f)
            {
                handPos += MathUtils.RotatePoint(Vector2.UnitX * Dir * 0.2f, torso.Rotation);

                float wobbleAmount = 0.1f;

                if (rightHand != null && !rightHand.Disabled)
                {
                    MoveLimb(rightHand, new Vector2(
                        handPos.X + (float)Math.Sin(handCyclePos / 1.5f) * wobbleAmount,
                        handPos.Y + (float)Math.Sin(handCyclePos / 3.5f) * wobbleAmount - 0.25f), CurrentSwimParams.ArmMoveStrength);
                }

                if (leftHand != null && !leftHand.Disabled)
                {
                    MoveLimb(leftHand, new Vector2(
                        handPos.X + (float)Math.Sin(handCyclePos / 2.0f) * wobbleAmount,
                        handPos.Y + (float)Math.Sin(handCyclePos / 3.0f) * wobbleAmount - 0.25f), CurrentSwimParams.ArmMoveStrength);
                }

                return;
            }

            handPos += head.LinearVelocity.ClampLength(1.0f) * 0.1f;

            // Not sure why the params has to be flipped, but it works.
            var handMoveAmount = CurrentSwimParams.HandMoveAmount.Flip();
            var handMoveOffset = CurrentSwimParams.HandMoveOffset.Flip();
            float handPosX = (float)Math.Cos(handCyclePos) * handMoveAmount.X * CurrentAnimationParams.CycleSpeed;
            float handPosY = (float)Math.Sin(handCyclePos) * handMoveAmount.Y * CurrentAnimationParams.CycleSpeed;

            Matrix rotationMatrix = Matrix.CreateRotationZ(torso.Rotation);

            if (rightHand != null && !rightHand.Disabled)
            {
                Vector2 rightHandPos = new Vector2(-handPosX, -handPosY) + handMoveOffset;
                rightHandPos.X = (Dir == 1.0f) ? Math.Max(0.3f, rightHandPos.X) : Math.Min(-0.3f, rightHandPos.X);
                rightHandPos = Vector2.Transform(rightHandPos, rotationMatrix);
                float speedMultiplier = Math.Min(character.SpeedMultiplier * (1 - Character.GetRightHandPenalty()), 1.0f);
                if (character.Inventory != null && character.Inventory.GetItemInLimbSlot(InvSlotType.RightHand) != null)
                {
                    speedMultiplier = Math.Min(speedMultiplier, 0.1f);
                }
                HandIK(rightHand, handPos + rightHandPos, CurrentSwimParams.ArmMoveStrength * speedMultiplier, CurrentSwimParams.HandMoveStrength * speedMultiplier);
                // Try to keep the wrist straight
                LimbJoint wrist = GetJointBetweenLimbs(LimbType.RightForearm, LimbType.RightHand);
                if (wrist != null)
                {
                    rightHand.body.ApplyTorque(MathHelper.Clamp(-wrist.JointAngle, -MathHelper.PiOver2, MathHelper.PiOver2) * rightHand.Mass * 100f * CurrentSwimParams.HandMoveStrength);
                }
            }

            if (leftHand != null && !leftHand.Disabled)
            {
                Vector2 leftHandPos = new Vector2(handPosX, handPosY) + handMoveOffset;
                leftHandPos.X = (Dir == 1.0f) ? Math.Max(0.3f, leftHandPos.X) : Math.Min(-0.3f, leftHandPos.X);
                leftHandPos = Vector2.Transform(leftHandPos, rotationMatrix);
                float speedMultiplier = Math.Min(character.SpeedMultiplier * (1 - Character.GetLeftHandPenalty()), 1.0f);
                if (character.Inventory != null && character.Inventory.GetItemInLimbSlot(InvSlotType.LeftHand) != null)
                {
                    speedMultiplier = Math.Min(speedMultiplier, 0.1f);
                }
                HandIK(leftHand, handPos + leftHandPos, CurrentSwimParams.ArmMoveStrength * speedMultiplier, CurrentSwimParams.HandMoveStrength * speedMultiplier);
                // Try to keep the wrist straight
                LimbJoint wrist = GetJointBetweenLimbs(LimbType.LeftForearm, LimbType.LeftHand);
                if (wrist != null)
                {
                    leftHand.body.ApplyTorque(MathHelper.Clamp(-wrist.JointAngle, -MathHelper.PiOver2, MathHelper.PiOver2) * leftHand.Mass * 100f * CurrentSwimParams.HandMoveStrength);
                }
            }
        }

        void UpdateFallingProne(float strength, bool moveHands = true, bool moveTorso = true, bool moveLegs = true)
        {
            if (strength <= 0.0f) { return; }

            Limb head = GetLimb(LimbType.Head);
            Limb torso = GetLimb(LimbType.Torso);

            if (moveHands && head != null && head.LinearVelocity.LengthSquared() > 1.0f && !head.IsSevered)
            {
                //if the head is moving, try to protect it with the hands
                Limb leftHand = GetLimb(LimbType.LeftHand);
                Limb rightHand = GetLimb(LimbType.RightHand);

                //move hands in front of the head in the direction of the movement
                Vector2 protectPos = head.SimPosition + Vector2.Normalize(head.LinearVelocity);
                if (rightHand != null && !rightHand.IsSevered)
                {
                    HandIK(rightHand, protectPos, strength * 0.1f);
                }
                if (leftHand != null && !leftHand.IsSevered)
                {
                    HandIK(leftHand, protectPos, strength * 0.1f);
                }
            }

            if (torso == null) { return; }

            //make the torso tip over
            //otherwise it tends to just drop straight down, pinning the characters legs in a weird pose
            if (moveTorso && !InWater)
            {
                //prefer tipping over in the same direction the torso is rotating
                //or moving
                //or lastly, in the direction it's facing if it's not moving/rotating
                float fallDirection = Math.Sign(torso.body.AngularVelocity - torso.body.LinearVelocity.X - Dir * 0.01f);
                float torque = MathF.Cos(torso.Rotation) * fallDirection * 5.0f * strength;
                torso.body.ApplyTorque(torque * torso.body.Mass);
            }

            //attempt to make legs stay in a straight line with the torso to prevent the character from doing a split
            if (moveLegs)
            {
                for (int i = 0; i < 2; i++)
                {
                    var thigh = i == 0 ? GetLimb(LimbType.LeftThigh) : GetLimb(LimbType.RightThigh);
                    if (thigh == null) { continue; }
                    if (thigh.IsSevered) { continue; }
                    float thighDiff = Math.Abs(MathUtils.GetShortestAngle(torso.Rotation, thigh.Rotation));
                    float diff = torso.Rotation - thigh.Rotation;
                    if (MathUtils.IsValid(diff))
                    {
                        float thighTorque = thighDiff * thigh.Mass * Math.Sign(diff) * 5.0f;
                        thigh.body.ApplyTorque(thighTorque * strength);
                    }               

                    var leg = i == 0 ? GetLimb(LimbType.LeftLeg) : GetLimb(LimbType.RightLeg);
                    if (leg == null || leg.IsSevered) { continue; }
                    float legDiff = Math.Abs(MathUtils.GetShortestAngle(torso.Rotation, leg.Rotation));
                    diff = torso.Rotation - leg.Rotation;
                    if (MathUtils.IsValid(diff))
                    {
                        float legTorque = legDiff * leg.Mass * Math.Sign(diff) * 5.0f;
                        leg.body.ApplyTorque(legTorque * strength);
                    }
                }
            }
        }

        private float lastReviveTime;

        private void UpdateCPR(float deltaTime)
        {
            if (character.SelectedCharacter == null || 
                (!character.SelectedCharacter.IsUnconscious && !character.SelectedCharacter.IsDead && character.SelectedCharacter.Stun <= 0.0f))
            {
                Anim = Animation.None;
                return;
            }

            Character target = character.SelectedCharacter;

            Crouching = true;

            Vector2 offset = Vector2.UnitX * -Dir * 0.75f;
            Vector2 diff = (target.SimPosition + offset) - character.SimPosition;
            Limb targetHead = target.AnimController.GetLimb(LimbType.Head);
            Limb targetTorso = target.AnimController.GetLimb(LimbType.Torso);
            if (targetTorso == null)
            {
                Anim = Animation.None;
                return;
            }

            Limb head = GetLimb(LimbType.Head);
            Limb torso = GetLimb(LimbType.Torso);

            Vector2 headDiff = targetHead == null ? diff : targetHead.SimPosition - character.SimPosition;
            targetMovement = new Vector2(diff.X, 0.0f);
            const float CloseEnough = 0.1f;
            if (Math.Abs(targetMovement.X) < CloseEnough)
            {
                targetMovement.X = 0.0f;
            }

            TargetDir = headDiff.X > 0.0f ? Direction.Right : Direction.Left;
            //if the target's in some weird pose, we may not be able to flip it so it's facing up,
            //so let's only try it once so we don't end up constantly flipping it
            if (cprAnimTimer <= 0.0f && target.AnimController.Direction == TargetDir)
            {
                target.AnimController.Flip();
            }
            (target.AnimController as HumanoidAnimController)?.UpdateFallingProne(strength: 1.0f, moveHands: false, moveTorso: false);

            head.Disabled = true;
            torso.Disabled = true;

            UpdateStanding();

            Vector2 handPos = targetTorso.SimPosition + Vector2.UnitY * 0.2f;

            Grab(handPos, handPos);

            Vector2 colliderPos = GetColliderBottom();

            float prevVitality = target.Vitality;
            bool wasCritical = prevVitality < 0.0f;

            if (GameMain.NetworkMember == null || !GameMain.NetworkMember.IsClient) //Serverside code
            {
                target.Oxygen += deltaTime * 0.5f; //Stabilize them        
            }

            float cprBoost = character.GetStatValue(StatTypes.CPRBoost);

            int skill = (int)character.GetSkillLevel(Tags.MedicalSkill);

            if (GameMain.NetworkMember is not { IsClient: true })
            {
                if (cprBoost >= 1f)
                {
                    //prevent the patient from suffocating no matter how fast their oxygen level is dropping
                    target.Oxygen = Math.Max(target.Oxygen, -10.0f);
                }
            }

            //Serverside code
            if (GameMain.NetworkMember is not { IsClient: true })
            {
                if (target.Oxygen < -10.0f)
                {
                    //stabilize the oxygen level but don't allow it to go positive and revive the character yet
                    float stabilizationAmount = skill * CPRSettings.Active.StabilizationPerSkill;
                    stabilizationAmount = MathHelper.Clamp(stabilizationAmount, CPRSettings.Active.StabilizationMin, CPRSettings.Active.StabilizationMax);
                    character.Oxygen -= 1.0f / stabilizationAmount * deltaTime; //Worse skill = more oxygen required
                    if (character.Oxygen > 0.0f) { target.Oxygen += stabilizationAmount * deltaTime; } //we didn't suffocate yet did we
                }
            }

            if (targetHead != null && head != null)
            {
                head.PullJointWorldAnchorB = new Vector2(targetHead.SimPosition.X, targetHead.SimPosition.Y + 0.8f);
                head.PullJointEnabled = true;
            }

            torso.PullJointWorldAnchorB = new Vector2(torso.SimPosition.X, colliderPos.Y + (TorsoPosition.Value - 0.1f));
            torso.PullJointEnabled = true;

            if (cprPumpTimer >= 1)
            {
                torso.body.ApplyLinearImpulse(new Vector2(0, -20f), maxVelocity: NetConfig.MaxPhysicsBodyVelocity);
                targetTorso.body.ApplyLinearImpulse(new Vector2(0, -20f), maxVelocity: NetConfig.MaxPhysicsBodyVelocity);
                //the pumping animation can sometimes cause impact damage, prevent that by briefly disabling it
                target.DisableImpactDamageTimer = 0.15f;
                cprPumpTimer = 0;

                if (skill < CPRSettings.Active.DamageSkillThreshold)
                {
                    target.LastDamageSource = null;
                    target.DamageLimb(
                        targetTorso.WorldPosition, targetTorso,
                        new[] { CPRSettings.Active.InsufficientSkillAffliction.Instantiate((CPRSettings.Active.DamageSkillThreshold - skill) * CPRSettings.Active.DamageSkillMultiplier, source: character) },
                        stun: 0.0f,
                        playSound: true,
                        attackImpulse: Vector2.Zero,
                        attacker: null);
                }
                //need to CPR for at least a couple of seconds before the target can be revived
                //(reviving the target when the CPR has barely started looks strange)
                if (cprAnimTimer > 2.0f && GameMain.NetworkMember is not { IsClient: true })
                {
                    float reviveChance = skill * CPRSettings.Active.ReviveChancePerSkill;
                    reviveChance = (float)Math.Pow(reviveChance, CPRSettings.Active.ReviveChanceExponent);
                    reviveChance = MathHelper.Clamp(reviveChance, CPRSettings.Active.ReviveChanceMin, CPRSettings.Active.ReviveChanceMax);
                    reviveChance *= 1f + cprBoost;

                    if (Rand.Range(0.0f, 1.0f, Rand.RandSync.ServerAndClient) <= reviveChance)
                    {
                        //increase oxygen and clamp it above zero 
                        // -> the character should be revived if there are no major afflictions in addition to lack of oxygen
                        target.Oxygen = Math.Max(target.Oxygen + 10.0f, 10.0f);
                    }
                }
            }
            cprPumpTimer += deltaTime;
            cprAnimTimer += deltaTime;

            //got the character back into a non-critical state, increase medical skill
            //BUT only if it has been more than 10 seconds since the character revived someone
            //otherwise it's easy to abuse the system by repeatedly reviving in a low-oxygen room 
            if (!target.IsDead)
            {
                target.CharacterHealth.RecalculateVitality();
                if (wasCritical && target.Vitality > 0.0f && Timing.TotalTime > lastReviveTime + 10.0f)
                {
                    character.Info?.ApplySkillGain(Tags.MedicalSkill, SkillSettings.Current.SkillIncreasePerCprRevive);
                    AchievementManager.OnCharacterRevived(target, character);
                    lastReviveTime = (float)Timing.TotalTime;
#if SERVER
                    GameMain.Server?.KarmaManager?.OnCharacterHealthChanged(target, character, damage: Math.Min(prevVitality - target.Vitality, 0.0f), stun: 0.0f);
#endif
                    //reset attacker, we don't want the character to start attacking us
                    //because we caused a bit of damage to them during CPR
                    target.ForgiveAttacker(character);
                }
            }
        }

        public override void DragCharacter(Character target, float deltaTime)
        {
            if (target == null) { return; }

            Limb torso = GetLimb(LimbType.Torso);
            Limb leftHand = GetLimb(LimbType.LeftHand);
            Limb rightHand = GetLimb(LimbType.RightHand);

            Limb targetLeftHand = 
                target.AnimController.GetLimb(LimbType.LeftForearm) ?? 
                target.AnimController.GetLimb(LimbType.Torso) ?? 
                target.AnimController.MainLimb;

            Limb targetRightHand = 
                target.AnimController.GetLimb(LimbType.RightForearm) ??
                target.AnimController.GetLimb(LimbType.Torso) ?? 
                target.AnimController.MainLimb;

            if (!target.AllowInput)
            {
                target.AnimController.ResetPullJoints();
            }

            bool targetPoseControlled = 
                target.SelectedItem?.GetComponent<Controller>() is { ControlCharacterPose: true } ||
                target.SelectedSecondaryItem?.GetComponent<Controller>() is { ControlCharacterPose: true };

            if (IsClimbing)
            {
                //cannot drag up ladders if the character is conscious
                if (target.AllowInput && (GameMain.NetworkMember == null || !GameMain.NetworkMember.IsClient))
                {
                    character.DeselectCharacter();
                    return;
                }
                Limb targetTorso = target.AnimController.GetLimb(LimbType.Torso);
                targetTorso ??= target.AnimController.MainLimb;
                if (target.AnimController.Dir != Dir)
                {
                    target.AnimController.Flip();
                }
                Vector2 transformedTorsoPos = torso.SimPosition;
                if (character.Submarine == null && target.Submarine != null)
                {
                    transformedTorsoPos -= target.Submarine.SimPosition;
                }
                else if (character.Submarine != null && target.Submarine == null)
                {
                    transformedTorsoPos += character.Submarine.SimPosition;
                }
                else if (character.Submarine != null && target.Submarine != null && character.Submarine != target.Submarine)
                {
                    transformedTorsoPos += character.Submarine.SimPosition;
                    transformedTorsoPos -= target.Submarine.SimPosition;
                }

                targetTorso.PullJointEnabled = true;
                targetTorso.PullJointWorldAnchorB = transformedTorsoPos + (Vector2.UnitX * -Dir) * 0.2f;
                targetTorso.PullJointMaxForce = 5000.0f;

                if (!targetLeftHand.IsSevered)
                {
                    targetLeftHand.PullJointEnabled = true;
                    targetLeftHand.PullJointWorldAnchorB = transformedTorsoPos + (new Vector2(1 * Dir, 1)) * 0.2f;
                    targetLeftHand.PullJointMaxForce = 5000.0f;
                }
                if (!targetRightHand.IsSevered)
                {
                    targetRightHand.PullJointEnabled = true;
                    targetRightHand.PullJointWorldAnchorB = transformedTorsoPos + (new Vector2(1 * Dir, 1)) * 0.2f;
                    targetRightHand.PullJointMaxForce = 5000.0f;
                }

                if (GameMain.NetworkMember != null && GameMain.NetworkMember.IsClient)
                {
                    Collider.ResetDynamics();
                }

                target.AnimController.IgnorePlatforms = true;
            }
            else
            {
                //only grab with one hand when swimming
                leftHand.Disabled = true;
                if (!inWater)
                {
                    rightHand.Disabled = true;
                }

                for (int i = 0; i < 2; i++)
                {
                    Limb targetLimb = target.AnimController.GetLimb(LimbType.Torso);
                    if (i == 0)
                    {
                        if (!targetLeftHand.IsSevered)
                        {
                            targetLimb = targetLeftHand;
                        }
                        else if (!targetRightHand.IsSevered)
                        {
                            targetLimb = targetRightHand;
                        }
                    }
                    else
                    {
                        if (!targetRightHand.IsSevered)
                        {
                            targetLimb = targetRightHand;
                        }
                        else if (!targetLeftHand.IsSevered)
                        {
                            targetLimb = targetLeftHand;
                        }
                    }

                    Limb pullLimb = i == 0 ? leftHand : rightHand;

                    if (GameMain.NetworkMember == null || !GameMain.NetworkMember.IsClient)
                    {
                        //stop dragging if there's something between the pull limb and the target limb
                        Vector2 sourceSimPos = pullLimb.SimPosition;
                        Vector2 targetSimPos = targetLimb.SimPosition;
                        if (character.Submarine != null && character.SelectedCharacter.Submarine == null)
                        {
                            targetSimPos -= character.Submarine.SimPosition;
                        }
                        else if (character.Submarine == null && character.SelectedCharacter.Submarine != null)
                        {
                            sourceSimPos -= character.SelectedCharacter.Submarine.SimPosition;
                        }
                        else if (character.Submarine != null && character.SelectedCharacter.Submarine != null && character.Submarine != character.SelectedCharacter.Submarine)
                        {
                            targetSimPos += character.SelectedCharacter.Submarine.SimPosition;
                            targetSimPos -= character.Submarine.SimPosition;
                        }
                        var body = Submarine.CheckVisibility(sourceSimPos, targetSimPos, ignoreSubs: true);
                        if (body != null)
                        {
                            character.DeselectCharacter();
                            return;
                        }
                    }

                    //only pull with one hand when swimming
                    if (i > 0 && inWater) { continue; }
                    
                    Vector2 diff = ConvertUnits.ToSimUnits(targetLimb.WorldPosition - pullLimb.WorldPosition);

                    Vector2 targetAnchor;
                    float targetForce;
                    pullLimb.PullJointEnabled = true;
                    if (targetLimb.type == LimbType.Torso || targetLimb == target.AnimController.MainLimb)
                    {
                        pullLimb.PullJointMaxForce = 5000.0f;
                        if (!character.CanRunWhileDragging())
                        {
                            targetMovement *= MathHelper.Clamp(Mass / target.Mass, 0.5f, 1.0f);
                        }

                        Vector2 shoulderPos = rightShoulder.WorldAnchorA;
                        float targetDist = Vector2.Distance(targetLimb.SimPosition, shoulderPos);
                        Vector2 dragDir = (targetLimb.SimPosition - shoulderPos) / targetDist;
                        if (!MathUtils.IsValid(dragDir)) { dragDir = -Vector2.UnitY; }
                        if (!InWater) 
                        { 
                            //lerp the arm downwards when not swimming
                            dragDir = Vector2.Lerp(dragDir, -Vector2.UnitY, 0.5f); 
                        }

                        Vector2 pullLimbAnchor = shoulderPos + dragDir * Math.Min(targetDist, (upperArmLength + forearmLength) * 2);
                        targetAnchor = shoulderPos + dragDir * (upperArmLength + forearmLength);
                        targetForce = 200.0f;
                        if (target.Submarine != character.Submarine)
                        {
                            if (character.Submarine == null)
                            {
                                pullLimbAnchor += target.Submarine.SimPosition;
                                targetAnchor -= target.Submarine.SimPosition;
                            }
                            else if (target.Submarine == null)
                            {
                                pullLimbAnchor -= character.Submarine.SimPosition;
                                targetAnchor += character.Submarine.SimPosition;
                            }
                            else
                            {
                                pullLimbAnchor -= target.Submarine.SimPosition;
                                pullLimbAnchor += character.Submarine.SimPosition;
                                targetAnchor -= character.Submarine.SimPosition;
                                targetAnchor += target.Submarine.SimPosition;
                            }
                        }
                        if (Vector2.DistanceSquared(pullLimb.PullJointWorldAnchorA, pullLimbAnchor) > 50.0f * 50.0f)
                        {
                            //there's a similar error check in the PullJointWorldAnchorB setter, but we seem to be getting quite a lot of
                            //errors specifically from this method, so let's use a more consistent error message here to prevent clogging GA with
                            //different error messages that all include a different coordinate
                            string errorMsg =
                                $"Attempted to move the anchor B of a limb's pull joint extremely far from the limb in {nameof(DragCharacter)}. " +
                                $"Character in sub: {character.Submarine != null}, target in sub: {target.Submarine != null}.";
                            GameAnalyticsManager.AddErrorEventOnce("DragCharacter:PullJointTooFar", GameAnalyticsManager.ErrorSeverity.Error, errorMsg);
#if DEBUG
                            DebugConsole.ThrowError(errorMsg);
#endif
                        }
                        else
                        {
                            pullLimb.PullJointWorldAnchorB = pullLimbAnchor;
                        }
                    }
                    else
                    {
                        pullLimb.PullJointWorldAnchorB = pullLimb.SimPosition + diff;
                        pullLimb.PullJointMaxForce = 5000.0f;
                        targetAnchor = targetLimb.SimPosition - diff;
                        targetForce = 5000.0f;
                    }

                    if (!targetPoseControlled)
                    {
                        targetLimb.PullJointEnabled = true;
                        targetLimb.PullJointMaxForce = targetForce;
                        targetLimb.PullJointWorldAnchorB = targetAnchor;
                        targetLimb.Disabled = true;
                        target.AnimController.movement = -diff;
                    }
                }

                float dist = ConvertUnits.ToSimUnits(Vector2.Distance(target.WorldPosition, WorldPosition));
                //let the target break free if it's moving away and gets far enough
                if ((GameMain.NetworkMember == null || !GameMain.NetworkMember.IsClient) && dist > BreakFromGrabDistance && target.AllowInput &&
                    Vector2.Dot(target.WorldPosition - WorldPosition, target.AnimController.TargetMovement) > 0)
                {
                    character.DeselectCharacter();
                    return;
                }

                //limit movement if moving away from the target
                if (!character.CanRunWhileDragging() && Vector2.Dot(target.WorldPosition - WorldPosition, targetMovement) < 0)
                {
                    targetMovement *= MathHelper.Clamp(1.5f - dist, 0.0f, 1.0f);
                }
                
                if (!target.AllowInput)
                {
                    target.AnimController.Stairs = Stairs;
                    target.AnimController.IgnorePlatforms = IgnorePlatforms;
                    target.AnimController.TargetMovement = TargetMovement;
                }
                else if (target is AICharacter && target != Character.Controlled && !targetPoseControlled)
                {
                    if (target.AnimController.Dir > 0 == WorldPosition.X > target.WorldPosition.X)
                    {
                        target.AnimController.LockFlipping(0.5f);
                    }
                    else
                    {
                        target.AnimController.TargetDir = WorldPosition.X > target.WorldPosition.X ? Direction.Right : Direction.Left;
                    }
                    //make the target stand 0.5 meters away from this character, on the side they're currently at
                    Vector2 movement = (character.SimPosition + Vector2.UnitX * 0.5f * Math.Sign(target.SimPosition.X - character.SimPosition.X)) - target.SimPosition;
                    target.AnimController.TargetMovement = movement.LengthSquared() > 0.01f ? movement : Vector2.Zero;
                }
            }
        }
        
        public void Crouch()
        {
            Crouching = true;
            character.SetInput(InputType.Crouch, hit: false, held: true);
        }

        private void FootIK(Limb foot, Vector2 pos, float legTorque, float footTorque, float footAngle)
        {
            if (!MathUtils.IsValid(pos))
            {
                string errorMsg = "Invalid foot position in FootIK (" + pos + ")\n" + Environment.StackTrace.CleanupStackTrace();
#if DEBUG
                DebugConsole.ThrowError(errorMsg);
#endif
                GameAnalyticsManager.AddErrorEventOnce("FootIK:InvalidPos", GameAnalyticsManager.ErrorSeverity.Error, errorMsg);
                return;
            }

            Limb upperLeg, lowerLeg;
            if (foot.type == LimbType.LeftFoot)
            {
                upperLeg = GetLimb(LimbType.LeftThigh);
                lowerLeg = GetLimb(LimbType.LeftLeg);
            }
            else
            {
                upperLeg = GetLimb(LimbType.RightThigh);
                lowerLeg = GetLimb(LimbType.RightLeg);
            }
            Limb torso = GetLimb(LimbType.Torso);
            LimbJoint waistJoint = GetJointBetweenLimbs(LimbType.Waist, upperLeg.type) ?? GetJointBetweenLimbs(LimbType.Torso, upperLeg.type);
            Vector2 waistPos = Vector2.Zero;
            if (waistJoint != null)
            {
                waistPos = waistJoint.LimbA == upperLeg ? waistJoint.WorldAnchorA : waistJoint.WorldAnchorB;
            }

            //distance from waist joint to the target position
            float c = Vector2.Distance(pos, waistPos);
            c = Math.Max(c, Math.Abs(upperLegLength - lowerLegLength));

            float legAngle = MathUtils.VectorToAngle(pos - waistPos) + MathHelper.PiOver2;
            if (!MathUtils.IsValid(legAngle))
            {
                string errorMsg = "Invalid leg angle (" + legAngle + ") in FootIK. Waist pos: " + waistPos + ", target pos: " + pos + "\n" + Environment.StackTrace.CleanupStackTrace();
#if DEBUG
                DebugConsole.ThrowError(errorMsg);
#endif
                GameAnalyticsManager.AddErrorEventOnce("FootIK:InvalidAngle", GameAnalyticsManager.ErrorSeverity.Error, errorMsg);
                return;
            }

            //make sure the angle "has the same number of revolutions" as the torso
            //(e.g. we don't want to rotate the legs to 0 if the torso is at 360, because that'd blow up the hip joints) 
            while (torso.Rotation - legAngle > MathHelper.Pi)
            {
                legAngle += MathHelper.TwoPi;
            }
            while (torso.Rotation - legAngle < -MathHelper.Pi)
            {
                legAngle -= MathHelper.TwoPi;
            }
            
            //if the distance is longer than the length of the upper and lower leg, we'll just have to extend them directly towards the target
            float upperLegAngle = c >= upperLegLength + lowerLegLength ? 0.0f : MathUtils.SolveTriangleSSS(lowerLegLength, upperLegLength, c);
            float lowerLegAngle = c >= upperLegLength + lowerLegLength ? 0.0f : MathUtils.SolveTriangleSSS(upperLegLength, lowerLegLength, c);

            upperLeg.body.SmoothRotate((legAngle + upperLegAngle * Dir), upperLeg.Mass * legTorque, wrapAngle: false);
            lowerLeg.body.SmoothRotate((legAngle - lowerLegAngle * Dir), lowerLeg.Mass * legTorque, wrapAngle: false);
            foot.body.SmoothRotate((legAngle - (lowerLegAngle + footAngle) * Dir), foot.Mass * footTorque, wrapAngle: false);
        }

        public override void Flip()
        {
            if (Character == null || Character.Removed)
            {
                LogAccessedRemovedCharacterError();
                return;
            }

            base.Flip();

            WalkPos = -WalkPos;

            Limb torso = GetLimb(LimbType.Torso);
            if (torso == null) { return; }

            Matrix torsoTransform = Matrix.CreateRotationZ(torso.Rotation);

            Vector2 difference;
            foreach (Item heldItem in character.HeldItems)
            {
                if (heldItem?.body != null && !heldItem.Removed && heldItem.GetComponent<Holdable>() != null)
                {
                    heldItem.FlipX(relativeToSub: false);
                }
            }

            foreach (Limb limb in Limbs)
            {
                if (limb.IsSevered) { continue; }

                bool mirror = false;
                bool flipAngle = false;
                bool wrapAngle = false;

                switch (limb.type)
                {
                    case LimbType.LeftHand:
                    case LimbType.LeftArm:
                    case LimbType.LeftForearm:
                    case LimbType.RightHand:
                    case LimbType.RightArm:
                    case LimbType.RightForearm:
                        flipAngle = true;
                        break;
                    case LimbType.LeftThigh:
                    case LimbType.LeftLeg:
                    case LimbType.LeftFoot:
                    case LimbType.RightThigh:
                    case LimbType.RightLeg:
                    case LimbType.RightFoot:
                        mirror = Crouching && !inWater;
                        flipAngle = (limb.DoesFlip || Crouching) && !inWater;
                        wrapAngle = !inWater;
                        break;
                    default:
                        flipAngle = limb.DoesFlip && !inWater;
                        wrapAngle = !inWater;
                        break;
                }

                Vector2 position = limb.SimPosition;

                if (!limb.PullJointEnabled && mirror)
                {
                    difference = limb.body.SimPosition - torso.SimPosition;
                    difference = Vector2.Transform(difference, torsoTransform);
                    difference.Y = -difference.Y;

                    position = torso.SimPosition + Vector2.Transform(difference, -torsoTransform);

                    //TrySetLimbPosition(limb, limb.SimPosition, );
                }

                float angle = flipAngle ? -limb.body.Rotation : limb.body.Rotation;
                if (wrapAngle) { angle = MathUtils.WrapAnglePi(angle); }
                
                TrySetLimbPosition(limb, Collider.SimPosition, position, angle);
            }
        }

        public override float GetSpeed(AnimationType type)
        {
            if (type == AnimationType.Crouch)
            {
                if (!CanWalk)
                {
                    DebugConsole.ThrowError($"{character.SpeciesName} cannot crouch!");
                    return 0;
                }
                return IsMovingBackwards ? HumanCrouchParams.MovementSpeed * HumanCrouchParams.BackwardsMovementMultiplier : HumanCrouchParams.MovementSpeed;
            }
            return base.GetSpeed(type);
        }
    }
}

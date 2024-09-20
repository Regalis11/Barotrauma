﻿using Barotrauma.Abilities;
using Barotrauma.Networking;
using FarseerPhysics;
using FarseerPhysics.Dynamics;
using Microsoft.Xna.Framework;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Barotrauma.Items.Components
{
    partial class RangedWeapon : ItemComponent
    {
        public enum BurstType
        {
            /// <summary>
            /// The weapon will not perform a burst.
            /// </summary>
            None,
            /// <summary>
            /// The burst index will reset when the trigger is released.
            /// </summary>
            Reset,
            /// <summary>
            /// The burst index will not reset when the trigger is released.
            /// </summary>
            Save,
            /// <summary>
            /// The burst will continue even if the trigger is released.
            /// </summary>
            Forced
        }

        private float reload;
        public float ReloadTimer { get; private set; }

        private Vector2 barrelPos;

        [Serialize("0.0,0.0", IsPropertySaveable.No, description: "The position of the barrel as an offset from the item's center (in pixels). Determines where the projectiles spawn.")]
        public string BarrelPos
        {
            get { return XMLExtensions.Vector2ToString(ConvertUnits.ToDisplayUnits(barrelPos)); }
            set { barrelPos = ConvertUnits.ToSimUnits(XMLExtensions.ParseVector2(value)); }
        }

        [Serialize(1.0f, IsPropertySaveable.No, description: "How long the user has to wait before they can fire the weapon again (in seconds).")]
        public float Reload
        {
            get { return reload; }
            set { reload = Math.Max(value, 0.0f); }
        }

        [Serialize(0f, IsPropertySaveable.No, description: "Weapons skill requirement to reload at normal speed.")]
        public float ReloadSkillRequirement
        {
            get;
            set;
        }

        [Serialize(1.0f, IsPropertySaveable.No, description: "Reload time at 0 skill level. Reload time scales with skill level up to the Weapons skill requirement.")]
        public float ReloadNoSkill
        {
            get;
            set;
        }

        [Serialize(false, IsPropertySaveable.No, description: "Tells the AI to hold the trigger down when it uses this weapon")]
        public bool HoldTrigger
        {
            get;
            set;
        }

        [Serialize("None", IsPropertySaveable.No, description: "The type of burst the weapon performs.")]
        public BurstType BurstMode { get; set; }

        [Serialize(0, IsPropertySaveable.No, description: "The amount of shots fired in a single burst.")]
        public int BurstCount { get; set; }

        private float burstReload;
        [Serialize(1f, IsPropertySaveable.No, description: "How long the user has to wait before they can fire the weapon again after a burst (in seconds).")]
        public float BurstReload
        {
            get => burstReload;
            set => burstReload = Math.Max(value, 0f);
        }

        [Serialize(1f, IsPropertySaveable.No, description: "Burst reload time at 0 skill level. Reload time scales with skill level up to the Weapons skill requirement.")]
        public float BurstReloadNoSkill { get; set; }

        [Serialize(1, IsPropertySaveable.No, description: "How many projectiles the weapon launches when fired once.")]
        public int ProjectileCount
        {
            get;
            set;
        }

        [Serialize(0.0f, IsPropertySaveable.No, description: "Random spread applied to the firing angle of the projectiles when used by a character with sufficient skills to use the weapon (in degrees).")]
        public float Spread
        {
            get;
            set;
        }

        [Serialize(0.0f, IsPropertySaveable.No, description: "Random spread applied to the firing angle of the projectiles when used by a character with insufficient skills to use the weapon (in degrees).")]
        public float UnskilledSpread
        {
            get;
            set;
        }

        [Serialize(0.0f, IsPropertySaveable.No, description: "The impulse applied to the physics body of the projectile (the higher the impulse, the faster the projectiles are launched). Sum of weapon + projectile.")]
        public float LaunchImpulse
        {
            get;
            set;
        }

        [Serialize(0.0f, IsPropertySaveable.Yes, description: "Percentage of damage mitigation ignored when hitting armored body parts (deflecting limbs). Sum of weapon + projectile."), Editable(MinValueFloat = 0.0f, MaxValueFloat = 1f)]
        public float Penetration { get; private set; }

        [Serialize(1f, IsPropertySaveable.Yes, description: "Weapon's damage modifier")]
        public float WeaponDamageModifier
        {
            get;
            private set;
        }

        [Serialize(0f, IsPropertySaveable.Yes, description: "The time required for a charge-type turret to charge up before able to fire.")]
        public float MaxChargeTime
        {
            get;
            private set;
        }
        
        [Serialize(defaultValue: 1f, IsPropertySaveable.Yes, description: "Penalty multiplier to reload time when dual-wielding.")]
        public float DualWieldReloadTimePenaltyMultiplier
        {
            get;
            private set;
        }
        
        [Serialize(defaultValue: 0f, IsPropertySaveable.Yes, description: "Additive penalty to accuracy (spread angle) when dual-wielding.")]
        public float DualWieldAccuracyPenalty
        {
            get;
            private set;
        }

        private bool waitingForTriggerRelease;
        [Serialize(false, IsPropertySaveable.No, "Does the user have to release the shoot key in order to fire again?")]
        public bool RequireTriggerRelease { get; set; }

        private readonly IReadOnlySet<Identifier> suitableProjectiles;

        private enum ChargingState
        {
            Inactive,
            WindingUp,
            WindingDown,
        }
        private ChargingState currentChargingState;

        public Vector2 TransformedBarrelPos
        {
            get
            {
                Matrix bodyTransform = Matrix.CreateRotationZ(item.body == null ? item.RotationRad : item.body.Rotation);
                Vector2 flippedPos = barrelPos;
                if (item.body != null && item.body.Dir < 0.0f) { flippedPos.X = -flippedPos.X; }
                return Vector2.Transform(flippedPos, bodyTransform) * item.Scale;
            }
        }

        public Projectile LastProjectile { get; private set; }

        private float currentChargeTime;
        private bool tryingToCharge;

        private Character prevUser;

        private int burstIndex;
        private int BurstIndex
        {
            get => burstIndex;
            set
            {
                burstIndex = value;
#if SERVER
                item.CreateServerEvent(this);
#endif
            }
        }

        private bool ShouldForceFire => BurstMode == BurstType.Forced && BurstIndex > 0;

        public RangedWeapon(Item item, ContentXElement element)
            : base(item, element)
        {
            item.IsShootable = true;
            if (element.Parent is { } parent)
            {
                item.RequireAimToUse = parent.GetAttributeBool(nameof(item.RequireAimToUse), true);
            }

            characterUsable = true;
            suitableProjectiles = element.GetAttributeIdentifierArray(nameof(suitableProjectiles), Array.Empty<Identifier>()).ToHashSet();
            if (ReloadSkillRequirement > 0)
            {
                if (ReloadNoSkill <= Reload)
                {
                    DebugConsole.AddWarning($"Invalid XML in {item.Name}: ReloadNoSkill is less than or equal to Reload, despite having ReloadSkillRequirement.", item.Prefab.ContentPackage);
                }
                if (BurstMode != BurstType.None && BurstReloadNoSkill <= BurstReload)
                {
                    DebugConsole.AddWarning($"Invalid XML in {item.Name}: BurstReloadNoSkill is less than or equal to BurstReload, despite having ReloadSkillRequirement.", item.Prefab.ContentPackage);
                }
            }

            InitProjSpecific(element);
        }

        partial void InitProjSpecific(ContentXElement rangedWeaponElement);

        public override void Equip(Character character)
        {
            //clamp above 1 to prevent rapid-firing by swapping weapons
            ReloadTimer = Math.Max(Math.Min(reload, 1.0f), ReloadTimer);
            IsActive = true;
        }

        public override void Update(float deltaTime, Camera cam)
        {
            ReloadTimer -= deltaTime;

            if ((BurstMode == BurstType.Reset || RequireTriggerRelease) && (prevUser == null || !prevUser.HeldItems.Contains(Item) || !prevUser.IsKeyDown(InputType.Shoot)))
            {
                if (BurstMode == BurstType.Reset)
                {
                    BurstIndex = 0;
                    ReloadTimer = MathF.Min(ReloadTimer, prevUser == null ? Reload : GetReloadTimer(prevUser, Reload, ReloadNoSkill));
                }
                if (RequireTriggerRelease && BurstIndex == 0)
                {
                    waitingForTriggerRelease = false;
                }
            }

            if (ReloadTimer <= 0f)
            {
                ReloadTimer = 0f;
                if (ShouldForceFire)
                {
                    if (!HasRequiredContainedItems(prevUser, false))
                    {
                        BurstIndex = 0;
                    }
                    else if (Use(deltaTime, prevUser))
                    {
                        WasUsed = true;
#if CLIENT
                        PlaySound(ActionType.OnUse, prevUser);
#endif
                        ApplyStatusEffects(ActionType.OnUse, deltaTime, prevUser, user: prevUser);
                        OnUsed.Invoke(new(item, prevUser));
                        return;
                    }
                }
                if (MaxChargeTime <= 0f)
                {
                    IsActive = false;
                    return;
                }
            }

            float previousChargeTime = currentChargeTime;

            float chargeDeltaTime = tryingToCharge && ReloadTimer <= 0f ? deltaTime : -deltaTime;
            currentChargeTime = Math.Clamp(currentChargeTime + chargeDeltaTime, 0f, MaxChargeTime);

            tryingToCharge = false;

            if (currentChargeTime == 0f)
            {
                currentChargingState = ChargingState.Inactive;
            }
            else if (currentChargeTime < previousChargeTime)
            {
                currentChargingState = ChargingState.WindingDown;
            }
            else
            {
                // if we are charging up or at maxed charge, remain winding up
                currentChargingState = ChargingState.WindingUp;
            }

            UpdateProjSpecific(deltaTime);
        }

        partial void UpdateProjSpecific(float deltaTime);

        private float GetSpread(Character user)
        {
            float degreeOfFailure = MathHelper.Clamp(1.0f - DegreeOfSuccess(user), 0.0f, 1.0f);
            degreeOfFailure *= degreeOfFailure;
            float spread = MathHelper.Lerp(Spread, UnskilledSpread, degreeOfFailure) / (1f + user.GetStatValue(StatTypes.RangedSpreadReduction));
            if (user.IsDualWieldingRangedWeapons())
            {
                spread += Math.Max(0f, ApplyDualWieldPenaltyReduction(user, DualWieldAccuracyPenalty, neutralValue: 0f));
            }
            return MathHelper.ToRadians(spread);
        }

        /// <summary>
        /// Lerps between the original penalty and a neutral value, which should be 1 for multipliers and 0 for additive penalties.
        /// </summary>
        /// <param name="character">The character to get stat values from</param>
        /// <param name="originalPenalty">The original penalty value</param>
        /// <param name="neutralValue">Neutral value to lerp towards. Should be 1 for multipliers and 0 for additives.</param>
        /// <returns></returns>
        private static float ApplyDualWieldPenaltyReduction(Character character, float originalPenalty, float neutralValue)
        {
            float statAdjustmentPrc = character.GetStatValue(StatTypes.DualWieldingPenaltyReduction);
            statAdjustmentPrc = MathHelper.Clamp(statAdjustmentPrc, 0f, 1f);
            float reducedPenaltyMultiplier = MathHelper.Lerp(originalPenalty, neutralValue, statAdjustmentPrc);
            return reducedPenaltyMultiplier;
        }

        private float GetReloadTimer(Character character, float skilledReload, float unskilledReload)
        {
            float newReload = skilledReload;
            float weaponSkill = character.GetSkillLevel("weapons");

            bool applyReloadFailure = ReloadSkillRequirement > 0 && unskilledReload > skilledReload && weaponSkill < ReloadSkillRequirement;
            if (applyReloadFailure)
            {
                //Examples, assuming 40 weapon skill required: 1 - 40/40 = 0 ... 1 - 0/40 = 1 ... 1 - 20 / 40 = 0.5
                float reloadFailure = MathHelper.Clamp(1 - weaponSkill / ReloadSkillRequirement, 0, 1);
                newReload = MathHelper.Lerp(skilledReload, unskilledReload, reloadFailure);
            }

            if (character.IsDualWieldingRangedWeapons())
            {
                newReload *= Math.Max(1f, ApplyDualWieldPenaltyReduction(character, DualWieldReloadTimePenaltyMultiplier, 1f));
            }

            newReload /= 1f + character?.GetStatValue(StatTypes.RangedAttackSpeed) ?? 0f;
            newReload /= 1f + item.GetQualityModifier(Quality.StatType.FiringRateMultiplier);
            return newReload;
        }

        private readonly List<Body> ignoredBodies = new List<Body>();
        public override bool Use(float deltaTime, Character character = null)
        {
            if (character == null || character.Removed) { return false; }
            if (ReloadTimer > 0f || waitingForTriggerRelease) { return false; }

            if (!ShouldForceFire)
            {
                tryingToCharge = true;
                if (item.RequireAimToUse && !character.IsKeyDown(InputType.Aim)) { return false; }
                if (currentChargeTime < MaxChargeTime) { return false; }
            }

            IsActive = true;

            ReloadTimer = GetReloadTimer(character, Reload, ReloadNoSkill);
            currentChargeTime = 0f;

            var abilityRangedWeapon = new AbilityRangedWeapon(item);
            character.CheckTalents(AbilityEffectType.OnUseRangedWeapon, abilityRangedWeapon);

            if (item.AiTarget != null)
            {
                item.AiTarget.SoundRange = item.AiTarget.MaxSoundRange;
                item.AiTarget.SightRange = item.AiTarget.MaxSightRange;
            }

            float degreeOfFailure = 1.0f - DegreeOfSuccess(character);
            degreeOfFailure *= degreeOfFailure;
            if (degreeOfFailure > Rand.Range(0.0f, 1.0f))
            {
                ApplyStatusEffects(ActionType.OnFailure, 1.0f, character);
            }

            for (int i = 0; i < ProjectileCount; i++)
            {
                Projectile projectile = FindProjectile(triggerOnUseOnContainers: true);
                if (projectile != null)
                {
                    Vector2 barrelPos = TransformedBarrelPos + item.body.SimPosition;
                    float rotation = (Item.body.Dir == 1.0f) ? Item.body.Rotation : Item.body.Rotation - MathHelper.Pi;
                    float spread = GetSpread(character) * projectile.GetSpreadFromPool();

                    var lastProjectile = LastProjectile;
                    if (lastProjectile != projectile)
                    {
                        lastProjectile?.Item.GetComponent<Rope>()?.Snap();
                    }
                    float damageMultiplier = (1f + item.GetQualityModifier(Quality.StatType.FirepowerMultiplier)) * WeaponDamageModifier;
                    projectile.Launcher = item;

                    ignoredBodies.Clear();
                    if (!projectile.DamageUser)
                    {
                        foreach (Limb l in character.AnimController.Limbs)
                        {
                            if (l.IsSevered) { continue; }
                            ignoredBodies.Add(l.body.FarseerBody);
                        }

                        foreach (Item heldItem in character.HeldItems)
                        {
                            var holdable = heldItem.GetComponent<Holdable>();
                            if (holdable?.Pusher != null)
                            {
                                ignoredBodies.Add(holdable.Pusher.FarseerBody);
                            }
                        }
                    }
                    projectile.Shoot(character, character.AnimController.AimSourceSimPos, barrelPos, rotation + spread, ignoredBodies: ignoredBodies.ToList(), createNetworkEvent: false, damageMultiplier, LaunchImpulse);
                    projectile.Item.GetComponent<Rope>()?.Attach(Item, projectile.Item);
                    if (projectile.Item.body != null)
                    {
                        if (i == 0)
                        {
                            Item.body.ApplyLinearImpulse(new Vector2((float)Math.Cos(projectile.Item.body.Rotation), (float)Math.Sin(projectile.Item.body.Rotation)) * Item.body.Mass * -50.0f, maxVelocity: NetConfig.MaxPhysicsBodyVelocity);
                        }
                        projectile.Item.body.ApplyTorque(projectile.Item.body.Mass * degreeOfFailure * 20.0f * projectile.GetSpreadFromPool());
                    }
                    Item.RemoveContained(projectile.Item);
                }
                LastProjectile = projectile;
            }

            LaunchProjSpecific();
            prevUser = character;

            if (BurstMode != BurstType.None)
            {
                BurstIndex++;
                if (BurstIndex >= BurstCount)
                {
                    BurstIndex = 0;
                    ReloadTimer = GetReloadTimer(character, BurstReload, BurstReloadNoSkill);
                    if (RequireTriggerRelease) { waitingForTriggerRelease = true; }
                }
            }
            else if (RequireTriggerRelease) { waitingForTriggerRelease = true; }

            return true;
        }

        public override bool SecondaryUse(float deltaTime, Character character = null)
        {
            return characterUsable || character == null;
        }

        public Projectile FindProjectile(bool triggerOnUseOnContainers = false)
        {
            foreach (ItemContainer container in item.GetComponents<ItemContainer>())
            {
                foreach (Item containedItem in container.Inventory.AllItemsMod)
                {
                    if (containedItem == null) { continue; }
                    Projectile projectile = containedItem.GetComponent<Projectile>();
                    if (IsSuitableProjectile(projectile)) { return projectile; }

                    //projectile not found, see if the contained item contains projectiles
                    var containedSubItems = containedItem.OwnInventory?.AllItemsMod;
                    if (containedSubItems == null) { continue; }
                    foreach (Item subItem in containedSubItems)
                    {
                        if (subItem == null) { continue; }
                        Projectile subProjectile = subItem.GetComponent<Projectile>();
                        //apply OnUse statuseffects to the container in case it has to react to it somehow
                        //(play a sound, spawn more projectiles, reduce condition...)
                        if (triggerOnUseOnContainers && subItem.Condition > 0.0f)
                        {
                            subItem.GetComponent<ItemContainer>()?.Item.ApplyStatusEffects(ActionType.OnUse, 1.0f);
                        }
                        if (IsSuitableProjectile(subProjectile)) { return subProjectile; }
                    }                    
                }
            }
            return null;
        }

        private bool IsSuitableProjectile(Projectile projectile)
        {
            if (projectile?.Item == null) { return false; }
            if (!suitableProjectiles.Any()) { return true; }
            return suitableProjectiles.Any(s => projectile.Item.Prefab.Identifier == s || projectile.Item.HasTag(s));
        }

        partial void LaunchProjSpecific();
    }
    class AbilityRangedWeapon : AbilityObject, IAbilityItem
    {
        public AbilityRangedWeapon(Item item)
        {
            Item = item;
        }
        public Item Item { get; set; }
    }
}

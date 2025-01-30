using Barotrauma.Extensions;
using FarseerPhysics;
using FarseerPhysics.Dynamics;
using FarseerPhysics.Dynamics.Contacts;
using Microsoft.Xna.Framework;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;

namespace Barotrauma.Items.Components
{
    class MeleeWeapon : Holdable
    {
        private float hitPos;

        private bool hitting;

        private float range;
        private float reload;

        private float reloadTimer;

        public Attack Attack { get; private set; }

        private readonly HashSet<Entity> hitTargets = new HashSet<Entity>();

        private readonly Queue<Fixture> impactQueue = new Queue<Fixture>();

        public Character User { get; private set; }

        [Serialize(0.0f, IsPropertySaveable.No, description: "An estimation of how close the item has to be to the target for it to hit. Used by AI characters to determine when they're close enough to hit a target.")]
        public float Range
        {
            get { return ConvertUnits.ToDisplayUnits(range); }
            set { range = ConvertUnits.ToSimUnits(value); }
        }

        [Serialize(0.5f, IsPropertySaveable.No, description: "How long the user has to wait before they can hit with the weapon again (in seconds).")]
        public float Reload
        {
            get { return reload; }
            set { reload = Math.Max(0.0f, value); }
        }

        [Serialize(false, IsPropertySaveable.No, description: "Can the weapon hit multiple targets per swing.")]
        public bool AllowHitMultiple
        {
            get;
            set;
        }

        [Serialize(false, IsPropertySaveable.No, description: "Disable to make the weapon ignore all hit effects when it collides with walls, doors, or other items.")]
        public bool HitOnlyCharacters
        {
            get;
            set;
        }

        [Editable, Serialize(true, IsPropertySaveable.No)]
        public bool Swing { get; set; }

        [Editable, Serialize("2.0, 0.0", IsPropertySaveable.No)]
        public Vector2 SwingPos { get; set; }

        [Editable, Serialize("3.0, -1.0", IsPropertySaveable.No)]
        public Vector2 SwingForce { get; set; }

        public bool Hitting { get { return hitting; } }

        /// <summary>
        /// Defines items that boost the weapon functionality, like battery cell for stun batons.
        /// </summary>
        public readonly ImmutableHashSet<Identifier> PreferredContainedItems;

        public MeleeWeapon(Item item, ContentXElement element)
            : base(item, element)
        {
            foreach (var subElement in element.Elements())
            {
                if (!subElement.Name.ToString().Equals("attack", StringComparison.OrdinalIgnoreCase)) { continue; }
                Attack = new Attack(subElement, item.Name + ", MeleeWeapon", item)
                {
                    DamageRange = item.body == null ? 10.0f : ConvertUnits.ToDisplayUnits(item.body.GetMaxExtent())
                };
            }
            item.IsShootable = true;
            item.RequireAimToUse = element.Parent.GetAttributeBool("requireaimtouse", true);
            PreferredContainedItems = element.GetAttributeIdentifierArray("preferredcontaineditems", Array.Empty<Identifier>()).ToImmutableHashSet();
        }

        public override void Equip(Character character)
        {
            base.Equip(character);
            //force a wait of at least 1 second when equipping the weapon, so you can't "rapid-fire" by swapping between weapons
            const float forcedDelayOnEquip = 1.0f;
            reloadTimer = Math.Max(Math.Min(reload, forcedDelayOnEquip), reloadTimer);
            IsActive = true;
        }

        public override bool Use(float deltaTime, Character character = null)
        {
            if (character == null || reloadTimer > 0.0f) { return false; }
#if CLIENT
            if (!Item.RequireAimToUse && character.IsPlayer && (GUI.MouseOn != null || character.Inventory.visualSlots.Any(s => s.MouseOn()) || Inventory.DraggingItems.Any())) { return false; }
#endif
            if (Item.RequireAimToUse && !character.IsKeyDown(InputType.Aim) || hitting) { return false; }

            //don't allow hitting if the character is already hitting with another weapon
            foreach (Item heldItem in character.HeldItems)
            {
                var otherWeapon = heldItem.GetComponent<MeleeWeapon>();
                if (otherWeapon == null) { continue; }
                if (otherWeapon.hitting) { return false; }
            }

            SetUser(character);

            if (Item.RequireAimToUse && hitPos < MathHelper.PiOver4) { return false; }

            ActivateNearbySleepingCharacters();
            reloadTimer = reload;
            reloadTimer /= 1f + character.GetStatValue(StatTypes.MeleeAttackSpeed);
            reloadTimer /= 1f + item.GetQualityModifier(Quality.StatType.StrikingSpeedMultiplier);
            character.AnimController.LockFlipping();

            item.body.FarseerBody.CollisionCategories = Physics.CollisionProjectile;
            item.body.FarseerBody.CollidesWith = Physics.CollisionCharacter | Physics.CollisionWall | Physics.CollisionItemBlocking;
            item.body.FarseerBody.OnCollision += OnCollision;
            item.body.FarseerBody.IsBullet = true;
            item.body.PhysEnabled = true;

            if (Swing && !character.AnimController.InWater)
            {
                foreach (Limb l in character.AnimController.Limbs)
                {
                    if (l.IsSevered) { continue; }
                    Vector2 force = new Vector2(character.AnimController.Dir * SwingForce.X, SwingForce.Y) * l.Mass;
                    switch (l.type)
                    {
                        case LimbType.Torso:
                            force *= 2;
                            break;
                        case LimbType.Legs:
                        case LimbType.LeftFoot:
                        case LimbType.LeftThigh:
                        case LimbType.LeftLeg:
                        case LimbType.RightFoot:
                        case LimbType.RightThigh:
                        case LimbType.RightLeg:
                            force = Vector2.Zero;
                            break;
                    }
                    l.body.ApplyLinearImpulse(force);
                }
            }
            
            hitting = true;
            hitTargets.Clear();

            IsActive = true;

            if (item.AiTarget != null)
            {
                item.AiTarget.SoundRange = item.AiTarget.MaxSoundRange;
                item.AiTarget.SightRange = item.AiTarget.MaxSightRange;
            }
            return false;
        }

        public override bool SecondaryUse(float deltaTime, Character character = null)
        {
            return characterUsable || character == null;
        }

        public override void Drop(Character dropper, bool setTransform = true)
        {
            //end hit first (which sets the weapon to the "held" state, with disabled physics and no special collision detection)
            EndHit();
            //ensure the physics body is enabled
            item.body.PhysEnabled = true;
            base.Drop(dropper, setTransform);
        }

        public override void UpdateBroken(float deltaTime, Camera cam)
        {
            Update(deltaTime, cam);
        }
        
        public override void Update(float deltaTime, Camera cam)
        {
            if (!item.body.Enabled)
            {
                impactQueue.Clear();
                return;
            }
            if (picker == null || !picker.HeldItems.Contains(item))
            {
                impactQueue.Clear();
                IsActive = false;
            }
            while (impactQueue.Count > 0)
            {
                var impact = impactQueue.Dequeue();
                HandleImpact(impact);
            }
            //in case handling the impact does something to the picker
            if (picker == null) { return; }
            reloadTimer -= deltaTime;
            if (reloadTimer < 0)
            {
                reloadTimer = 0;
            }
            if (!picker.IsKeyDown(InputType.Aim) && !hitting)
            {
                hitPos = 0.0f;
            }
            ApplyStatusEffects(ActionType.OnActive, deltaTime, picker);
            if (item.body.Dir != picker.AnimController.Dir)
            {
                item.FlipX(relativeToSub: false);
            }
            AnimController ac = picker.AnimController;
            if (!hitting)
            {
                bool aim = item.RequireAimToUse && picker.AllowInput && picker.IsKeyDown(InputType.Aim) && reloadTimer <= 0 && picker.CanAim && !UsageDisabledByRangedWeapon(picker);
                if (aim)
                {
                    UpdateSwing(deltaTime, out Vector2 swingPos, out float swingAngle);
                    hitPos = MathUtils.WrapAnglePi(Math.Min(hitPos + deltaTime * 3f, MathHelper.PiOver4));
                    ac.HoldItem(deltaTime, item, handlePos, itemPos: aimPos + swingPos, aim: false, hitPos, holdAngle + hitPos + aimAngle, aimMelee: true, armAngle: swingAngle);
                    if (ac.InWater)
                    {
                        ac.LockFlipping();
                    }
                }
                else
                {
                    hitPos = 0;
                    ac.HoldItem(deltaTime, item, handlePos, itemPos: holdPos, aim: false, holdAngle);
                }
            }
            else
            {
                // TODO: We might want to make this configurable
                hitPos -= deltaTime * 15f;
                if (Swing)
                {
                    ac.HoldItem(deltaTime, item, handlePos, itemPos: SwingPos, aim: false, hitPos, holdAngle);
                }
                else
                {
                    ac.HoldItem(deltaTime, item, handlePos, itemPos: holdPos, aim: false, holdAngle);
                }
                if (hitPos < -MathHelper.Pi)
                {
                    EndHit();
                }
            }
        }

        /// <summary>
        /// Activate sleeping ragdolls that are close enough to hit with the weapon (otherwise the collision will not be registered)
        /// </summary>
        private void ActivateNearbySleepingCharacters()
        {
            foreach (Character c in Character.CharacterList)
            {
                if (!c.Enabled || !c.AnimController.BodyInRest) { continue; }
                //do a broad check first
                if (Math.Abs(c.WorldPosition.X - item.WorldPosition.X) > 1000.0f) { continue; }
                if (Math.Abs(c.WorldPosition.Y - item.WorldPosition.Y) > 1000.0f) { continue; }

                foreach (Limb limb in c.AnimController.Limbs)
                {
                    float hitRange = 2.0f;
                    if (Vector2.DistanceSquared(limb.SimPosition, item.SimPosition) < hitRange * hitRange)
                    {
                        c.AnimController.BodyInRest = false;
                        break;
                    }
                }
            }
        }

        private void SetUser(Character character)
        {
            if (User == character) { return; }
            if (User != null && User.Removed) { User = null; }

            User = character;
        }

        private void EndHit()
        {
            RestoreCollision();
            hitting = false;
            hitTargets.Clear();
            hitPos = 0;
        }

        private void RestoreCollision()
        {
            impactQueue.Clear();
            item.body.FarseerBody.OnCollision -= OnCollision;
            item.body.CollisionCategories = Physics.CollisionItem;
            item.body.CollidesWith = Physics.DefaultItemCollidesWith;
            item.body.FarseerBody.IsBullet = false;
            item.body.PhysEnabled = false;
        }

        private bool OnCollision(Fixture f1, Fixture f2, Contact contact)
        {
            if (User == null || User.Removed)
            {
                impactQueue.Enqueue(f2);
                return true;
            }

            contact.GetWorldManifold(out Vector2 normal, out var points);

            //ignore collision if there's a wall between the user and the contact point to prevent hitting through walls
            if (Submarine.PickBody(User.AnimController.AimSourceSimPos,
                points[0],
                collisionCategory: Physics.CollisionWall | Physics.CollisionLevel | Physics.CollisionItemBlocking,
                allowInsideFixture: true,
                customPredicate: (Fixture fixture) => { return fixture.CollidesWith.HasFlag(Physics.CollisionItem) && fixture.Body != f2.Body; }) != null)
            {
                return false;
            }

            if (f2.Body.UserData is Limb targetLimb)
            {
                if (targetLimb.IsSevered || targetLimb.character == null || targetLimb.character == User) { return false; }
                if (targetLimb.character.IgnoreMeleeWeapons) { return false; }
                var targetCharacter = targetLimb.character;
                if (targetCharacter == picker) { return false; }
                if (AllowHitMultiple)
                {
                    if (hitTargets.Contains(targetCharacter)) { return false; }
                }
                else
                {
                    if (hitTargets.Any(t => t is Character)) { return false; }
                }
                hitTargets.Add(targetCharacter);
            }
            else if (f2.Body.UserData is Character targetCharacter)
            {
                if (targetCharacter == picker || targetCharacter == User) { return false; }
                if (targetCharacter.IgnoreMeleeWeapons) { return false; }
                targetLimb = targetCharacter.AnimController.GetLimb(LimbType.Torso); //Otherwise armor can be bypassed in strange ways
                if (AllowHitMultiple)
                {
                    if (hitTargets.Contains(targetCharacter)) { return false; }
                }
                else
                {
                    if (hitTargets.Any(t => t is Character)) { return false; }
                }
                hitTargets.Add(targetCharacter);
            }
            else if (!HitOnlyCharacters)
            {
                if ((f2.Body.UserData as Structure ?? f2.UserData as Structure) is Structure targetStructure)
                {
                    if (AllowHitMultiple)
                    {
                        if (hitTargets.Contains(targetStructure)) { return true; }
                    }
                    else
                    {
                        if (hitTargets.Any(t => t is Structure)) { return true; }
                    }
                    hitTargets.Add(targetStructure);
                }
                else if ((f2.Body.UserData as Item ?? f2.UserData as Item) is Item targetItem)
                {
                    if (AllowHitMultiple)
                    {
                        if (hitTargets.Contains(targetItem)) { return true; }
                    }
                    else
                    {
                        if (hitTargets.Any(t => t is Item)) { return true; }
                    }
                    hitTargets.Add(targetItem);
                }
                else if (f2.Body.UserData is Holdable holdable && holdable.CanPush)
                {
                    if (holdable.Item.GetRootInventoryOwner() == User) { return false; }
                    hitTargets.Add(holdable.Item);
                }
            }
            else
            {
                return false;
            }

            impactQueue.Enqueue(f2);

            return true;
        }

        private System.Text.StringBuilder serverLogger;
        private void HandleImpact(Fixture targetFixture)
        {
            var target = targetFixture.Body;
            if (User == null || User.Removed || target == null)
            {
                RestoreCollision();
                hitting = false;
                User = null;
                return;
            }

            float damageMultiplier = 1 + User.GetStatValue(StatTypes.MeleeAttackMultiplier);
            damageMultiplier *= 1.0f + item.GetQualityModifier(Quality.StatType.StrikingPowerMultiplier);

            Character user = User;
            Limb targetLimb = target.UserData as Limb;
            Character targetCharacter = targetLimb?.character ?? target.UserData as Character;
            Structure targetStructure = target.UserData as Structure ?? targetFixture.UserData as Structure;
            Item targetItem = target.UserData is Holdable h ? h.Item : target.UserData as Item ?? targetFixture.UserData as Item;
            Entity targetEntity = targetCharacter ?? targetStructure ?? targetItem ?? target.UserData as Entity;
            if (Attack != null)
            {
                Attack.SetUser(user);
                Attack.DamageMultiplier = damageMultiplier;
                if (targetLimb != null)
                {
                    if (targetLimb.character.Removed) { return; }
                    targetLimb.character.LastDamageSource = item;
                    Attack.DoDamageToLimb(user, targetLimb, item.WorldPosition, 1.0f);
                }
                else if (targetCharacter != null)
                {
                    if (targetCharacter.Removed) { return; }
                    targetCharacter.LastDamageSource = item;
                    Attack.DoDamage(user, targetCharacter, item.WorldPosition, 1.0f);
                }
                else if (targetStructure != null)
                {
                    if (targetStructure.Removed) { return; }
                    Attack.DoDamage(user, targetStructure, item.WorldPosition, 1.0f);
                }
                else if (targetItem != null && targetItem.Prefab.DamagedByMeleeWeapons && targetItem.Condition > 0)
                {
                    if (targetItem.Removed) { return; }
                    var attackResult = Attack.DoDamage(user, targetItem, item.WorldPosition, 1.0f);
#if CLIENT
                    if (attackResult.Damage > 0.0f && targetItem.Prefab.ShowHealthBar && Character.Controlled != null &&
                        (user == Character.Controlled || Character.Controlled.CanSeeTarget(item)))
                    {
                        Character.Controlled.UpdateHUDProgressBar(targetItem,
                            targetItem.WorldPosition,
                            targetItem.Condition / targetItem.MaxCondition,
                            emptyColor: GUIStyle.HealthBarColorLow,
                            fullColor: GUIStyle.HealthBarColorHigh,
                            textTag: targetItem.Prefab.ShowNameInHealthBar ? targetItem.Name : string.Empty);
                    }
#endif
                }
                else if (target.UserData is Holdable { CanPush: true } holdable)
                {
                    if (holdable.Item.Removed) { return; }
                    RestoreCollision();
                    hitting = false;
                    User = null;
                }
                else
                {
                    return;
                }
            }

            if (GameMain.NetworkMember != null && GameMain.NetworkMember.IsClient) { return; }

            ActionType conditionalActionType = ActionType.OnSuccess;
            if (user != null && Rand.Range(0.0f, 0.5f) > DegreeOfSuccess(user))
            {
                conditionalActionType = ActionType.OnFailure;
            }
            if (GameMain.NetworkMember is { IsServer: true } server && targetEntity != null)
            {
                server.CreateEntityEvent(item, new Item.ApplyStatusEffectEventData(conditionalActionType, targetItemComponent: this, targetCharacter, targetLimb, useTarget: targetEntity));
                server.CreateEntityEvent(item, new Item.ApplyStatusEffectEventData(ActionType.OnUse, targetItemComponent: this, targetCharacter, targetLimb, useTarget: targetEntity));
                serverLogger ??= new System.Text.StringBuilder();
                serverLogger.Clear();
                serverLogger.Append($"{picker?.LogName} used {item.Name}");
                if (item.ContainedItems != null && item.ContainedItems.Any())
                {
                    serverLogger.Append($"({string.Join(", ", item.ContainedItems.Select(i => i?.Name))})");
                }
                string targetName;
                if (targetCharacter != null)
                {
                    targetName = targetCharacter.LogName;
                }
                else if (targetItem != null)
                {
                    targetName = targetItem.Name;
                }
                else if (targetStructure != null)
                {
                    targetName = targetStructure.Name;
                }
                else
                {
                    targetName = targetEntity.ToString();
                }
                serverLogger.Append($" on {targetName}.");
#if SERVER
                Networking.GameServer.Log(serverLogger.ToString(), Networking.ServerLog.MessageType.Attack);
#endif
            }
            if (targetEntity != null)
            {
                ApplyStatusEffects(conditionalActionType, 1.0f, targetCharacter, targetLimb, useTarget: targetEntity, user: user, afflictionMultiplier: damageMultiplier);
                ApplyStatusEffects(ActionType.OnUse, 1.0f, targetCharacter, targetLimb, useTarget: targetEntity, user: user, afflictionMultiplier: damageMultiplier);
            }

            if (DeleteOnUse)
            {
                Entity.Spawner.AddItemToRemoveQueue(item);
            }
        }
    }
}

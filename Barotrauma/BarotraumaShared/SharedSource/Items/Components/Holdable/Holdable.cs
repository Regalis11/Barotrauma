using Barotrauma.Abilities;
using Barotrauma.Networking;
using FarseerPhysics;
using FarseerPhysics.Dynamics;
using FarseerPhysics.Dynamics.Contacts;
using Microsoft.Xna.Framework;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Xml.Linq;

namespace Barotrauma.Items.Components
{
    partial class Holdable : Pickable, IServerSerializable, IClientSerializable
    {
        private readonly struct AttachEventData : IEventData
        {
            public readonly Vector2 AttachPos;
            public readonly Character Attacher;

            public AttachEventData(Vector2 attachPos, Character attacher)
            {
                AttachPos = attachPos;
                Attacher = attacher;
            }
        }

        private const float MaxAttachDistance = ItemPrefab.DefaultInteractDistance * 0.95f;

        //the position(s) in the item that the Character grabs
        protected Vector2[] handlePos;
        private readonly Vector2[] scaledHandlePos;

        private readonly InputType prevPickKey;
        private LocalizedString prevMsg;
        private Dictionary<RelatedItem.RelationType, List<RelatedItem>> prevRequiredItems;

        private float swingState;

        private Character prevEquipper;

        public override bool IsAttached => Attached;

        private bool attachable, attached, attachedByDefault;
        private Voronoi2.VoronoiCell attachTargetCell;
        private PhysicsBody body;

        public readonly ImmutableDictionary<StatTypes, float> HoldableStatValues;

        public PhysicsBody Pusher
        {
            get;
            private set;
        }
        [Serialize(true, IsPropertySaveable.Yes, description: "Is the item currently able to push characters around? True by default. Only valid if blocksplayers is set to true.")]
        public bool CanPush
        {
            get;
            set;
        }

        public PhysicsBody Body
        {
            get { return item.body ?? body; }
        }

        [Serialize(false, IsPropertySaveable.Yes, description: "Is the item currently attached to a wall (only valid if Attachable is set to true).")]
        public bool Attached
        {
            get { return attached && item.ParentInventory == null; }
            set
            {
                attached = value;
                item.CheckCleanable();
                item.SetActiveSprite();
            }
        }

        [Serialize(true, IsPropertySaveable.Yes, description: "Can the item be pointed to a specific direction or do the characters always hold it in a static pose.")]
        public bool Aimable
        {
            get;
            set;
        }

        [Serialize(0f, IsPropertySaveable.Yes, description: "Camera offset to apply when aiming this item. Only valid if Aimable is set to true.")]
        public float CameraAimOffset { get; set; }

        [Serialize(false, IsPropertySaveable.No, description: "Should the character adjust its pose when aiming with the item. Most noticeable underwater, where the character will rotate its entire body to face the direction the item is aimed at.")]
        public bool ControlPose
        {
            get;
            set;
        }

        [Serialize(false, IsPropertySaveable.No, description: "Use the hand rotation instead of torso rotation for the item hold angle. Enable this if you want the item just to follow with the arm when not aiming instead of forcing the arm to a hold pose.")]
        public bool UseHandRotationForHoldAngle
        {
            get;
            set;
        }

        [Serialize(false, IsPropertySaveable.No, description: "Can the item be attached to walls.")]
        public bool Attachable
        {
            get { return attachable; }
            set { attachable = value; }
        }

        [Serialize(true, IsPropertySaveable.No, description: "Can the item be reattached to walls after it has been deattached (only valid if Attachable is set to true).")]
        public bool Reattachable
        {
            get;
            set;
        }

        [Serialize(false, IsPropertySaveable.No, description: "Can the item only be attached in limited amount? Uses permanent stat values to check for legibility.")]
        public bool LimitedAttachable
        {
            get;
            set;
        }

        [Serialize(false, IsPropertySaveable.No, description: "Should the item be attached to a wall by default when it's placed in the submarine editor.")]
        public bool AttachedByDefault
        {
            get { return attachedByDefault; }
            set { attachedByDefault = value; }
        }

        [Serialize("0.0,0.0", IsPropertySaveable.No, description: "The position the character holds the item at (in pixels, as an offset from the character's shoulder)."+
            " For example, a value of 10,-100 would make the character hold the item 100 pixels below the shoulder and 10 pixels forwards.")]
        public Vector2 HoldPos
        {
            get { return ConvertUnits.ToDisplayUnits(holdPos); }
            set { holdPos = ConvertUnits.ToSimUnits(value); }
        }
        //the distance from the holding characters elbow to center of the physics body of the item
        protected Vector2 holdPos;
        

        [Serialize("0.0,0.0", IsPropertySaveable.No, description: "The position the character holds the item at when aiming (in pixels, as an offset from the character's shoulder)."+
            " Works similarly as HoldPos, except that the position is rotated according to the direction the player is aiming at. For example, a value of 10,-100 would make the character hold the item 100 pixels below the shoulder and 10 pixels forwards when aiming directly to the right.")]
        public Vector2 AimPos
        {
            get { return ConvertUnits.ToDisplayUnits(aimPos); }
            set { aimPos = ConvertUnits.ToSimUnits(value); }
        }
        protected Vector2 aimPos;

        protected float holdAngle;
#if DEBUG
        [Editable, Serialize(0.0f, IsPropertySaveable.No, description: "The rotation at which the character holds the item (in degrees, relative to the rotation of the character's hand).")]
#else
        [Serialize(0.0f, IsPropertySaveable.No)] 
#endif
        public float HoldAngle
        {
            get { return MathHelper.ToDegrees(holdAngle); }
            set { holdAngle = MathHelper.ToRadians(value); }
        }

        protected float aimAngle;
#if DEBUG
        [Editable, Serialize(0.0f, IsPropertySaveable.No, description: "The rotation at which the character holds the item while aiming (in degrees, relative to the rotation of the character's hand).")]
#else
        [Serialize(0.0f, IsPropertySaveable.No)] 
#endif
        public float AimAngle
        {
            get { return MathHelper.ToDegrees(aimAngle); }
            set { aimAngle = MathHelper.ToRadians(value); }
        }

        private Vector2 swingAmount;
#if DEBUG
        [Editable, Serialize("0.0,0.0", IsPropertySaveable.No, description: "How much the item swings around when aiming/holding it (in pixels, as an offset from AimPos/HoldPos).")]
#else
        [Serialize("0.0,0.0", IsPropertySaveable.No)] 
#endif
        public Vector2 SwingAmount
        {
            get { return ConvertUnits.ToDisplayUnits(swingAmount); }
            set { swingAmount = ConvertUnits.ToSimUnits(value); }
        }
#if DEBUG
        [Editable, Serialize(0.0f, IsPropertySaveable.No, description: "How fast the item swings around when aiming/holding it (only valid if SwingAmount is set).")]
#else
        [Serialize(0.0f, IsPropertySaveable.No)]
#endif

        public float SwingSpeed { get; set; }

#if DEBUG
        [Editable, Serialize(false, IsPropertySaveable.No, description: "Should the item swing around when it's being held.")]
#else
        [Serialize(false, IsPropertySaveable.No)]
#endif
        public bool SwingWhenHolding { get; set; }

#if DEBUG
        [Editable, Serialize(false, IsPropertySaveable.No, description: "Should the item swing around when it's being aimed.")]
#else
        [Serialize(false, IsPropertySaveable.No)]
#endif
        public bool SwingWhenAiming { get; set; }

#if DEBUG
        [Editable, Serialize(false, IsPropertySaveable.No, description: "Should the item swing around when it's being used (for example, when firing a weapon or a welding tool).")]
#else
        [Serialize(false, IsPropertySaveable.No)]
#endif
        public bool SwingWhenUsing { get; set; }

#if DEBUG
        [Editable, Serialize(false, IsPropertySaveable.No)]
#else
        [Serialize(false, IsPropertySaveable.No)]
#endif
        public bool DisableHeadRotation { get; set; }

        [Serialize(false, IsPropertySaveable.No, description: "If true, this item can't be used if the character is also holding a ranged weapon.")]
        public bool DisableWhenRangedWeaponEquipped { get; set; }

        [ConditionallyEditable(ConditionallyEditable.ConditionType.Attachable, MinValueFloat = 0.0f, MaxValueFloat = 0.999f, DecimalCount = 3), Serialize(0.55f, IsPropertySaveable.No, description: "Sprite depth that's used when the item is NOT attached to a wall.")]
        public float SpriteDepthWhenDropped
        {
            get;
            set;
        }

        /// <summary>
        /// For setting the handle positions using status effects
        /// </summary>
        public Vector2 Handle1
        {
            get { return ConvertUnits.ToDisplayUnits(handlePos[0]); }
            set 
            { 
                handlePos[0] = ConvertUnits.ToSimUnits(value); 
                if (item.FlippedX)
                {
                    handlePos[0].X = -handlePos[0].X;
                }
                if (!secondHandlePosDefined)
                {
                    Handle2 = value;
                }
            }
        }

        /// <summary>
        /// For setting the handle positions using status effects
        /// </summary>
        public Vector2 Handle2
        {
            get { return ConvertUnits.ToDisplayUnits(handlePos[1]); }
            set 
            { 
                handlePos[1] = ConvertUnits.ToSimUnits(value);
                if (item.FlippedX)
                {
                    handlePos[1].X = -handlePos[1].X;
                }
            }
        }

        private bool secondHandlePosDefined;

        public Holdable(Item item, ContentXElement element)
            : base(item, element)
        {
            body = item.body;

            Pusher = null;
            if (element.GetAttributeBool("blocksplayers", false))
            {
                Pusher = new PhysicsBody(item.body.Width, item.body.Height, item.body.Radius, 
                    item.body.Density,
                    BodyType.Dynamic,
                    Physics.CollisionItemBlocking, 
                    Physics.CollisionCharacter | Physics.CollisionProjectile)
                {
                    Enabled = false,
                    UserData = this
                };
                Pusher.FarseerBody.OnCollision += OnPusherCollision;
                Pusher.FarseerBody.FixedRotation = false;
                Pusher.FarseerBody.IgnoreGravity = true;
            }

            handlePos = new Vector2[2];
            scaledHandlePos = new Vector2[2];
            Vector2 previousValue = Vector2.Zero;
            for (int i = 1; i < 3; i++)
            {
                int index = i - 1;
                string attributeName = "handle" + i;
                // If no value is defind for handle2, use the value of handle1.
                Vector2 value = previousValue;
                var attribute = element.GetAttribute(attributeName);
                if (attribute != null)
                {
                    secondHandlePosDefined = i > 1;
                    value = ConvertUnits.ToSimUnits(XMLExtensions.ParseVector2(attribute.Value));
                }
                handlePos[index] = value;
                previousValue = value;
            }

            canBePicked = true;
            prevRequiredItems = new Dictionary<RelatedItem.RelationType, List<RelatedItem>>(RequiredItems);
            
            if (attachable)
            {
                prevMsg = DisplayMsg;
                prevPickKey = PickKey;
                                
                if (item.Submarine != null)
                {
                    if (item.Submarine.Loading)
                    {
                        AttachToWall();
                        Attached = false;
                    }
                    else //the submarine is not being loaded, which means we're either in the sub editor or the item has been spawned mid-round
                    {
                        if (Screen.Selected == GameMain.SubEditorScreen)
                        {
                            //in the sub editor, attach
                            AttachToWall();
                        }
                        else
                        {
                            //spawned mid-round, deattach
                            DeattachFromWall();
                        }
                    }
                }
            }
            characterUsable = element.GetAttributeBool("characterusable", true);

            Dictionary<StatTypes, float> statValues = new Dictionary<StatTypes, float>();
            foreach (var subElement in element.GetChildElements("statvalue"))
            {
                StatTypes statType = CharacterAbilityGroup.ParseStatType(subElement.GetAttributeString("stattype", ""), Name);
                float statValue = subElement.GetAttributeFloat("value", 0f);
                if (statValues.ContainsKey(statType))
                {
                    statValues[statType] += statValue;
                }
                else
                {
                    statValues.TryAdd(statType, statValue);
                }                
            }
            HoldableStatValues = statValues.ToImmutableDictionary();
        }

        private bool OnPusherCollision(Fixture sender, Fixture other, Contact contact)
        {
            if (other.Body.UserData is Character character)
            {
                if (!IsActive) { return false; }
                if (!CanPush) { return false; }
                return character != picker;
            }
            else
            {
                return true;
            }
        }

        private bool loadedFromInstance;
        public override void Load(ContentXElement componentElement, bool usePrefabValues, IdRemap idRemap, bool isItemSwap)
        {
            base.Load(componentElement, usePrefabValues, idRemap, isItemSwap);

            loadedFromInstance = true;

            if (usePrefabValues)
            {
                //this needs to be loaded regardless
                Attached = componentElement.GetAttributeBool("attached", attached);
            }

            if (attachable)
            {
                prevMsg = DisplayMsg;
                prevRequiredItems = new Dictionary<RelatedItem.RelationType, List<RelatedItem>>(RequiredItems);
            }
        }

        public override void Drop(Character dropper, bool setTransform = true)
        {
            Drop(true, dropper, setTransform);
        }

        private void Drop(bool dropConnectedWires, Character dropper, bool setTransform = true)
        {
            GetRope()?.Snap();
            if (dropConnectedWires)
            {
                DropConnectedWires(dropper);
            }

            if (attachable)
            {
                if (body != null)
                {
                    item.body = body;
                }
                DeattachFromWall();
            }

            if (Pusher != null) { Pusher.Enabled = false; }
            if (item.body != null) { item.body.Enabled = true; }

            IsActive = false;
            attachTargetCell = null;

            if (picker == null || picker.Removed)
            {
                if (dropper == null || dropper.Removed) { return; }
                picker = dropper;
            }
            if (picker.Inventory == null) { return; }

            item.Submarine = picker.Submarine;

            if (item.body != null && setTransform)
            {
                if (item.body.Removed)
                {
                    DebugConsole.ThrowError(
                        "Failed to drop the Holdable component of the item \"" + item.Name + "\" (body has been removed"
                        + (item.Removed ? ", item has been removed)" : ")"));
                }
                else
                {
                    item.body.ResetDynamics();
                    Limb heldHand, arm;
                    if (picker.Inventory.IsInLimbSlot(item, InvSlotType.LeftHand))
                    {
                        heldHand = picker.AnimController.GetLimb(LimbType.LeftHand);
                        arm = picker.AnimController.GetLimb(LimbType.LeftArm);
                    }
                    else
                    {
                        heldHand = picker.AnimController.GetLimb(LimbType.RightHand);
                        arm = picker.AnimController.GetLimb(LimbType.RightArm);
                    }
                    if (heldHand != null && !heldHand.Removed && arm != null && !arm.Removed)
                    {
                        //hand simPosition is actually in the wrist so need to move the item out from it slightly
                        Vector2 diff = new Vector2(
                            (heldHand.SimPosition.X - arm.SimPosition.X) / 2f,
                            (heldHand.SimPosition.Y - arm.SimPosition.Y) / 2.5f);
                        item.SetTransform(heldHand.SimPosition + diff, 0.0f);
                    }
                    else
                    {
                        item.SetTransform(picker.SimPosition, 0.0f);
                    }     
                }           
            }

            picker.Inventory.RemoveItem(item);
            picker = null;
        }

        public override void Equip(Character character)
        {
            //if the item has multiple Pickable components (e.g. Holdable and Wearable, check that we don't equip it in hands when the item is worn or vice versa)
            if (item.GetComponents<Pickable>().Count() > 0)
            {
                bool inSuitableSlot = false;
                for (int i = 0; i < character.Inventory.Capacity; i++)
                {
                    if (character.Inventory.GetItemsAt(i).Contains(item))
                    {
                        if (character.Inventory.SlotTypes[i] != InvSlotType.Any && 
                            allowedSlots.Any(a => a.HasFlag(character.Inventory.SlotTypes[i])))
                        {
                            inSuitableSlot = true;
                            break;
                        }
                    }
                }
                if (!inSuitableSlot) { return; }
            }

            picker = character;

            if (item.Removed)
            {
                DebugConsole.ThrowError($"Attempted to equip a removed item ({item.Name})\n" + Environment.StackTrace.CleanupStackTrace());
                return;
            }

            //cannot hold and wear an item at the same time
            //(unless the slot in which it's held and worn are equal - e.g. a suit with built-in tool or weapon on one hand)
            var wearable = item.GetComponent<Wearable>();
            if (wearable != null && !wearable.AllowedSlots.SequenceEqual(allowedSlots))
            {
                wearable.Unequip(character);
            }

            if (character != null) { item.Submarine = character.Submarine; }
            if (item.body == null)
            {
                if (body != null)
                {
                    item.body = body;
                }
                else
                {
                    return;
                }
            }

            if (!item.body.Enabled)
            {
                Limb hand = picker.AnimController.GetLimb(LimbType.RightHand) ?? picker.AnimController.GetLimb(LimbType.LeftHand);
                item.SetTransform(hand != null ? hand.SimPosition : character.SimPosition, 0.0f);
            }

            bool alreadyEquipped = character.HasEquippedItem(item);
            if (picker.HasEquippedItem(item))
            {
                item.body.Enabled = true;
                item.body.PhysEnabled = false;
                IsActive = true;

#if SERVER
                if (picker != prevEquipper) { GameServer.Log(GameServer.CharacterLogName(character) + " equipped " + item.Name, ServerLog.MessageType.ItemInteraction); }
#endif
                prevEquipper = picker;
            }
            else
            {
                prevEquipper = null;
            }
        }

        public override void Unequip(Character character)
        {
#if SERVER
            if (prevEquipper != null)
            {
                GameServer.Log(GameServer.CharacterLogName(character) + " unequipped " + item.Name, ServerLog.MessageType.ItemInteraction);
            }
#endif
            prevEquipper = null;
            if (picker == null) { return; }
            item.body.PhysEnabled = true;
            item.body.Enabled = false;
            IsActive = false;
        }

        public bool CanBeAttached(Character user)
        {
            if (!attachable || !Reattachable) { return false; }

            //can be attached anywhere in sub editor
            if (Screen.Selected == GameMain.SubEditorScreen) { return true; }

            Vector2 attachPos = user == null ? item.WorldPosition : GetAttachPosition(user, useWorldCoordinates: true);

            //can be attached anywhere inside hulls
            if (item.CurrentHull != null && Submarine.RectContains(item.CurrentHull.WorldRect, attachPos)) { return true; }

            return Structure.GetAttachTarget(attachPos) != null || GetAttachTargetCell(100.0f) != null;
        }

        public bool CanBeDeattached()
        {
            if (!attachable || !attached) { return true; }

            //allow deattaching everywhere in sub editor
            if (Screen.Selected == GameMain.SubEditorScreen) { return true; }

            if (item.GetComponent<LevelResource>() != null) { return true; }

            if (item.GetComponent<Planter>() is { } planter && planter.GrowableSeeds.Any(seed => seed != null)) { return false; } 

            //if the item has a connection panel and rewiring is disabled, don't allow deattaching
            var connectionPanel = item.GetComponent<ConnectionPanel>();
            if (connectionPanel != null && !connectionPanel.AlwaysAllowRewiring && (connectionPanel.Locked || !(GameMain.NetworkMember?.ServerSettings?.AllowRewiring ?? true)))
            {
                return false;
            }

            if (item.CurrentHull == null)
            {
                return attachTargetCell != null || Structure.GetAttachTarget(item.WorldPosition) != null;
            }
            else
            {
                return true;
            }
        }

        public override bool Pick(Character picker)
        {
            if (item.Removed)
            {
                DebugConsole.ThrowError($"Attempted to pick up a removed item ({item.Name})\n" + Environment.StackTrace.CleanupStackTrace());
                return false;
            }

            if (!attachable)
            {
                return base.Pick(picker);
            }

            if (!CanBeDeattached()) { return false; }

            if (Attached)
            {
                return base.Pick(picker);
            }
            else
            {

                //not attached -> pick the item instantly, ignoring picking time
                return OnPicked(picker);
            }
        }

        public override bool OnPicked(Character picker)
        {
#if CLIENT
            if (GameMain.NetworkMember != null && GameMain.NetworkMember.IsClient)
            {
                if (!picker.Inventory.CanBeAutoMovedToCorrectSlots(item))
                {
                    picker.Inventory.FlashAllowedSlots(item, Color.Red);
                }
                return false;
            }
#endif
            bool wasAttached = IsAttached;
            if (base.OnPicked(picker))
            {
                DeattachFromWall();

#if SERVER
                if (GameMain.Server != null && attachable)
                {
                    item.CreateServerEvent(this);
                    if (picker != null && wasAttached)
                    {
                        GameServer.Log(GameServer.CharacterLogName(picker) + " detached " + item.Name + " from a wall", ServerLog.MessageType.ItemInteraction);
                    }
                }
#endif
                return true;
            }

            return false;
        }

        public void AttachToWall()
        {
            if (!attachable) { return; }

            if (body == null)
            {
                throw new InvalidOperationException($"Tried to attach an item with no physics body to a wall ({item.Prefab.Identifier}).");
            }

            body.Enabled = false;
            body.SetTransformIgnoreContacts(body.SimPosition, rotation: 0.0f);
            item.body = null;

            //outside hulls/subs -> we need to check if the item is being attached on a structure outside the sub
            if (item.CurrentHull == null && item.Submarine == null)
            {
                Structure attachTarget = Structure.GetAttachTarget(item.WorldPosition);
                if (attachTarget != null)
                {
                    if (attachTarget.Submarine != null)
                    {
                        //set to submarine-relative position
                        item.SetTransform(ConvertUnits.ToSimUnits(item.WorldPosition - attachTarget.Submarine.Position), 0.0f, false);
                        body.SetTransformIgnoreContacts(item.SimPosition, 0.0f);
                    }
                    item.Submarine = attachTarget.Submarine;
                }
                else
                {
                    attachTargetCell = GetAttachTargetCell(150.0f);
                    if (attachTargetCell != null && attachTargetCell.IsDestructible) 
                    {
                        attachTargetCell.OnDestroyed += () =>
                        {
                            if (attachTargetCell != null && attachTargetCell.CellType != Voronoi2.CellType.Solid)
                            {
                                Drop(dropConnectedWires: true, dropper: null);
                            }
                        };
                    }
                }
            }

            var containedItems = item.OwnInventory?.AllItems;
            if (containedItems != null)
            {
                foreach (Item contained in containedItems)
                {
                    if (contained?.body == null) { continue; }
                    contained.SetTransform(item.SimPosition, contained.body.Rotation);
                }
            }

            DisplayMsg = prevMsg;
            PickKey = prevPickKey;
            RequiredItems = new Dictionary<RelatedItem.RelationType, List<RelatedItem>>(prevRequiredItems);

            Attached = true;
#if CLIENT
            item.DrawDepthOffset = 0.0f;
#endif
        }

        public void DeattachFromWall()
        {
            if (!attachable) { return; }

            Attached = false;
            attachTargetCell = null;
#if CLIENT
            item.DrawDepthOffset = 0.0f;
#endif
            //make the item pickable with the default pick key and with no specific tools/items when it's deattached
            RequiredItems.Clear();
            DisplayMsg = "";
            PickKey = InputType.Select;
#if CLIENT
            item.DrawDepthOffset = SpriteDepthWhenDropped - item.SpriteDepth;
#endif
            foreach (LightComponent light in item.GetComponents<LightComponent>())
            {
                light.CheckIfNeedsUpdate();
            }
        }

        public override void ParseMsg()
        {
            base.ParseMsg();
            if (Attachable)
            {
                prevMsg = DisplayMsg;
            }
        }

        public override bool Use(float deltaTime, Character character = null)
        {
            if (UsageDisabledByRangedWeapon(character)) { return false; }
            if (!attachable || item.body == null) { return character == null || (character.IsKeyDown(InputType.Aim) && characterUsable); }
            if (character != null)
            {
                if (!characterUsable && !attachable) { return false; }
                if (!character.IsKeyDown(InputType.Aim)) { return false; }
                if (!CanBeAttached(character)) { return false; }

                if (LimitedAttachable)
                {
                    if (character?.Info == null) 
                    {
                        DebugConsole.AddWarning("Character without CharacterInfo attempting to attach a limited attachable item!");
                        return false; 
                    }
                    Vector2 attachPos = GetAttachPosition(character, useWorldCoordinates: true);
                    Submarine attachSubmarine = Structure.GetAttachTarget(attachPos)?.Submarine ?? item.Submarine;
                    int maxAttachableCount = (int)character.Info.GetSavedStatValueWithBotsInMp(StatTypes.MaxAttachableCount, item.Prefab.Identifier);

                    int currentlyAttachedCount = Item.ItemList.Count(
                        i => i.Submarine == attachSubmarine && i.GetComponent<Holdable>() is Holdable holdable && holdable.Attached && i.Prefab.Identifier == item.Prefab.Identifier);
                    if (maxAttachableCount == 0)
                    {
#if CLIENT
                        if (character == Character.Controlled)
                        {
                            GUI.AddMessage(TextManager.Get("itemmsgrequiretraining"), Color.Red);
                        }
#endif
                        return false;
                    }
                    else if (currentlyAttachedCount >= maxAttachableCount)
                    {
#if CLIENT
                        if (character == Character.Controlled)
                        {
                            GUI.AddMessage($"{TextManager.Get("itemmsgtotalnumberlimited")} ({currentlyAttachedCount}/{maxAttachableCount})", Color.Red);
                        }
#endif
                        return false;
                    }
                }

                if (GameMain.NetworkMember != null)
                {
#if CLIENT
                    if (character == Character.Controlled)
                    {
                        Vector2 attachPos = ConvertUnits.ToSimUnits(GetAttachPosition(character));
                        item.CreateClientEvent(this, new AttachEventData(attachPos, character));
                    }
#endif
                    //don't attach at this point in MP: instead rely on the network events created above
                    return false;
                }
                else
                {
                    item.Drop(character);
                    item.SetTransform(ConvertUnits.ToSimUnits(GetAttachPosition(character)), 0.0f, findNewHull: false);
                    //the light source won't get properly updated if lighting is disabled (even though the light sprite is still drawn when lighting is disabled)
                    //so let's ensure the light source is up-to-date
                    RefreshLightSources(item);
                }
                AttachToWall();
            }
            return true;

            static void RefreshLightSources(Item item)
            {
                item.body?.UpdateDrawPosition();
                foreach (var light in item.GetComponents<LightComponent>())
                {
                    light.SetLightSourceTransform();
                }
                item.GetComponent<ItemContainer>()?.SetContainedItemPositions();
                foreach (var containedItem in item.ContainedItems)
                {
                    RefreshLightSources(containedItem);
                }
            }
        }


        public override bool SecondaryUse(float deltaTime, Character character = null)
        {
            return true;
        }

        private Vector2 GetAttachPosition(Character user, bool useWorldCoordinates = false)
        {
            if (user == null) { return useWorldCoordinates ? item.WorldPosition : item.Position; }

            Vector2 mouseDiff = user.CursorWorldPosition - user.WorldPosition;
            mouseDiff = mouseDiff.ClampLength(MaxAttachDistance);

            Vector2 userPos = useWorldCoordinates ? user.WorldPosition : user.Position;
            Vector2 attachPos = userPos + mouseDiff;

            //offset the position by half the size of the grid to get the item to adhere to the grid in the same way as in the sub editor
            //in the sub editor, we align the top-left corner of the item with the grid
            //but here the origin of the item is placed at the attach position, so we need to offset it
            Vector2 offset = new Vector2(
                -(item.Rect.Width / 2) % Submarine.GridSize.X,
                (item.Rect.Height / 2) % Submarine.GridSize.Y);

            if (user.Submarine != null)
            {
                //we must add some "padding" to the raycast to ensure it reaches all the way to a wall
                //otherwise the cursor might be outside a wall, but the grid cell it's in might be partially inside
                Vector2 padding =  Submarine.GridSize * new Vector2(Math.Sign(mouseDiff.X), Math.Sign(mouseDiff.Y));

                if (Submarine.PickBody(
                    ConvertUnits.ToSimUnits(user.Position), 
                    ConvertUnits.ToSimUnits(user.Position + mouseDiff + padding), collisionCategory: Physics.CollisionWall) != null)
                {
                    attachPos = userPos + mouseDiff * Submarine.LastPickedFraction + offset;

                    //round down if we're placing on the right side and vice versa: ensures we don't round the position inside a wall
                    return
                        new Vector2(
                            (mouseDiff.X > 0 ? MathF.Floor(attachPos.X / Submarine.GridSize.X) : MathF.Ceiling(attachPos.X / Submarine.GridSize.X)) * Submarine.GridSize.X,
                            (mouseDiff.Y > 0 ? MathF.Floor(attachPos.Y / Submarine.GridSize.Y) : MathF.Ceiling(attachPos.Y / Submarine.GridSize.Y)) * Submarine.GridSize.Y)
                         - offset;
                }
            }
            else if (Level.Loaded != null)
            {
                bool edgeFound = false;
                foreach (var cell in Level.Loaded.GetCells(attachPos))
                {
                    if (cell.CellType != Voronoi2.CellType.Solid) { continue; }
                    foreach (var edge in cell.Edges)
                    {
                        if (!edge.IsSolid) { continue; }
                        if (MathUtils.GetLineSegmentIntersection(edge.Point1, edge.Point2, user.WorldPosition, attachPos, out Vector2 intersection))
                        {
                            attachPos = intersection;
                            edgeFound = true;
                            break;
                        }
                    }
                    if (edgeFound) { break; }
                }
            }

            return new Vector2(
                    MathUtils.RoundTowardsClosest(attachPos.X + offset.X, Submarine.GridSize.X),
                    MathUtils.RoundTowardsClosest(attachPos.Y + offset.Y, Submarine.GridSize.Y)) - offset;
        }

        private Voronoi2.VoronoiCell GetAttachTargetCell(float maxDist)
        {
            if (Level.Loaded == null) { return null; }
            foreach (var cell in Level.Loaded.GetCells(item.WorldPosition, searchDepth: 1))
            {
                if (cell.CellType != Voronoi2.CellType.Solid) { continue; }
                Vector2 diff = cell.Center - item.WorldPosition;
                if (diff.LengthSquared() > 0.0001f) { diff = Vector2.Normalize(diff); }
                if (cell.IsPointInside(item.WorldPosition + diff * maxDist))
                {
                    return cell;
                }
            }
            return null;
        }

        public override void UpdateBroken(float deltaTime, Camera cam)
        {
            Update(deltaTime, cam);
        }

        public Rope GetRope()
        {
            var rangedWeapon = Item.GetComponent<RangedWeapon>();
            if (rangedWeapon != null)
            {
                var lastProjectile = rangedWeapon.LastProjectile;
                if (lastProjectile != null)
                {
                    return lastProjectile.Item.GetComponent<Rope>();
                }
            }
            return null;
        }

        public override void Update(float deltaTime, Camera cam)
        {
            if (item.body == null || !item.body.Enabled) { return; }

            Character owner = picker ?? item.GetRootInventoryOwner() as Character;

            if (owner != null)
            {
                ApplyStatusEffects(ActionType.OnActive, deltaTime, owner);
            }

            if (picker == null || !picker.HasEquippedItem(item))
            {
                if (Pusher != null) { Pusher.Enabled = false; }
                if (attachTargetCell == null && owner == null) { IsActive = false; }
                return;
            }

            if (picker == Character.Controlled && picker.IsKeyDown(InputType.Aim) && CanBeAttached(picker))
            {
                Drawable = true;
            }

            UpdateSwingPos(deltaTime, out Vector2 swingPos);
            if (item.body.Dir != picker.AnimController.Dir) 
            {
                item.FlipX(relativeToSub: false);
            }

            item.Submarine = picker.Submarine;
            
            if (picker.HeldItems.Contains(item))
            {
                scaledHandlePos[0] = handlePos[0] * item.Scale;
                scaledHandlePos[1] = handlePos[1] * item.Scale;
                bool aim = picker.IsKeyDown(InputType.Aim) && aimPos != Vector2.Zero && picker.CanAim && !UsageDisabledByRangedWeapon(picker);
                if (aim)
                {
                    if (picker.AnimController.IsHoldingToRope && GetRope() is { Snapped: false } rope)
                    {
                        Vector2 targetPos = Submarine.GetRelativeSimPosition(picker, rope.Item);
                        picker.AnimController.HoldItem(deltaTime, item, scaledHandlePos, itemPos: aimPos, aim: true, holdAngle, aimAngle, targetPos: targetPos);
                    }
                    else
                    {
                        picker.AnimController.HoldItem(deltaTime, item, scaledHandlePos, itemPos: aimPos + swingPos, aim: true, holdAngle, aimAngle);   
                    }
                }
                else
                {
                    picker.AnimController.HoldItem(deltaTime, item, scaledHandlePos, itemPos: holdPos + swingPos, aim: false, holdAngle);
                    if (GetRope() is { SnapWhenNotAimed: true } rope)
                    {
                        if (rope.Item.ParentInventory == null)
                        {
                            rope.Snap();   
                        }
                    }
                }
            }
            else
            {
                GetRope()?.Snap();
                Limb equipLimb = null;
                if (picker.Inventory.IsInLimbSlot(item, InvSlotType.Headset) || picker.Inventory.IsInLimbSlot(item, InvSlotType.Head))
                {
                    equipLimb = picker.AnimController.GetLimb(LimbType.Head);
                }
                else if (picker.Inventory.IsInLimbSlot(item, InvSlotType.InnerClothes) || 
                    picker.Inventory.IsInLimbSlot(item, InvSlotType.OuterClothes))
                {
                    equipLimb = picker.AnimController.GetLimb(LimbType.Torso);
                }

                if (equipLimb != null && !equipLimb.Removed)
                {
                    float itemAngle = (equipLimb.Rotation + holdAngle * picker.AnimController.Dir);

                    Matrix itemTransfrom = Matrix.CreateRotationZ(equipLimb.Rotation);
                    Vector2 transformedHandlePos = Vector2.Transform(handlePos[0] * item.Scale, itemTransfrom);

                    item.body.ResetDynamics();
                    item.SetTransform(equipLimb.SimPosition - transformedHandlePos, itemAngle);
                }
            }
        }

        public void UpdateSwingPos(float deltaTime, out Vector2 swingPos)
        {
            swingPos = Vector2.Zero;
            if (swingAmount != Vector2.Zero && !picker.IsUnconscious && picker.Stun <= 0.0f)
            {
                swingState += deltaTime;
                swingState %= 1.0f;
                if (SwingWhenHolding ||
                    (SwingWhenAiming && picker.IsKeyDown(InputType.Aim)) ||
                    (SwingWhenUsing && picker.IsKeyDown(InputType.Aim) && picker.IsKeyDown(InputType.Shoot)))
                {
                    swingPos = swingAmount * new Vector2(
                        PerlinNoise.GetPerlin(swingState * SwingSpeed * 0.1f, swingState * SwingSpeed * 0.1f) - 0.5f,
                        PerlinNoise.GetPerlin(swingState * SwingSpeed * 0.1f + 0.5f, swingState * SwingSpeed * 0.1f + 0.5f) - 0.5f);
                }
            }
        }

        protected bool UsageDisabledByRangedWeapon(Character character)
        {
            if (DisableWhenRangedWeaponEquipped && character != null)
            {
                if (character.HeldItems.Any(it => it.GetComponent<RangedWeapon>() != null)) { return true; }
            }
            return false;
        }

        public override void ReceiveSignal(Signal signal, Connection connection)
        {
            //do nothing
        }

        public override void FlipX(bool relativeToSub)
        {
            handlePos[0].X = -handlePos[0].X;
            handlePos[1].X = -handlePos[1].X;
            if (item.body != null)
            {
                item.body.Dir = -item.body.Dir;
            }
        }

        public override void OnItemLoaded()
        {
            if (item.Submarine != null && item.Submarine.Loading) { return; }
            OnMapLoaded();
            item.SetActiveSprite();
        }

        public override void OnMapLoaded()
        {
            if (!attachable) { return; }
            
            //the Holdable component didn't get loaded from an instance of the item, just the prefab xml = a mod or update must've made the item movable/detachable
            if (!loadedFromInstance)
            {
                if (attachedByDefault)
                {
                    AttachToWall();
                    return;
                }
            }

            if (Attached)
            {
                AttachToWall();
            }
            else
            {
                if (body != null)
                {
                    body.SetTransformIgnoreContacts(item.SimPosition, item.Rotation);
                    item.body = body;
                    body.Enabled = item.ParentInventory == null;
                }                
                DeattachFromWall();
            }
        }

        protected override void RemoveComponentSpecific()
        {
            base.RemoveComponentSpecific();
            attachTargetCell = null;
            if (Pusher != null)
            {
                Pusher.Remove();
                Pusher = null;
            }
            body = null; 
        }

        public override XElement Save(XElement parentElement)
        {
            if (!attachable)
            {
                return base.Save(parentElement);
            }

            var tempMsg = DisplayMsg;
            var tempRequiredItems = RequiredItems;

            DisplayMsg = prevMsg;
            RequiredItems = prevRequiredItems;
            
            XElement saveElement = base.Save(parentElement);

            DisplayMsg = tempMsg;
            RequiredItems = tempRequiredItems;

            return saveElement;
        }       

    }
}

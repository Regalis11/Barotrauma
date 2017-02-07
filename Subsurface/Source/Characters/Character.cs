﻿
using FarseerPhysics;
using FarseerPhysics.Dynamics;
using FarseerPhysics.Dynamics.Joints;
using Lidgren.Network;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Barotrauma.Networking;
using Barotrauma.Particles;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Xml.Linq;

namespace Barotrauma
{
    class Character : Entity, IDamageable, IPropertyObject, IClientSerializable, IServerSerializable
    {
        public static List<Character> CharacterList = new List<Character>();
        
        public static bool DisableControls;

        /*private UInt32 netStateID;
        public UInt32 NetStateID
        {
            get { return netStateID; }
        }*/
        
        [Flags]
        private enum InputNetFlags : ushort
        {
            None = 0x0,
            Left = 0x1,
            Right = 0x2,
            Up = 0x4,
            Down = 0x8,
            FacingLeft = 0x10,
            Run = 0x20,
            Select = 0x40,
            Use = 0x80,
            Aim = 0x100,
            Attack = 0x200,

            MaxVal = 0x3FF
        }
        private InputNetFlags dequeuedInput = 0;
        private InputNetFlags prevDequeuedInput = 0;
        
        private struct NetInputMem
        {
            public InputNetFlags states; //keys pressed/other boolean states at this step
            public UInt16 intAim; //aim angle, represented as an unsigned short where 0=0º, 65535=just a bit under 360º
            public UInt16 interact; //id of the item being interacted with
        }

        private List<NetInputMem> memInput = new List<NetInputMem>();
        //private List<Vector2> memMousePos = new List<Vector2>();

        private List<PosInfo> memPos = new List<PosInfo>();
        private List<PosInfo> memLocalPos = new List<PosInfo>();

        private bool networkUpdateSent;
        
        //the Character that the player is currently controlling
        private static Character controlled;

        public static Character Controlled
        {
            get { return controlled; }
            set 
            {
                if (controlled == value) return;
                controlled = value;
                CharacterHUD.Reset();
            }
        }

        public List<Item> SpawnItems = new List<Item>();

        public bool isSynced = false;

        private bool enabled;

        public bool Enabled
        {
            get
            {
                return enabled;
            }
            set
            {
                enabled = value;

                foreach (Limb limb in AnimController.Limbs)
                {
                    limb.body.Enabled = enabled;
                }
            }
        }

        public Hull PreviousHull = null;
        public Hull CurrentHull = null;

        public readonly bool IsRemotePlayer;

        private CharacterInventory inventory;

        public UInt16 LastNetworkUpdateID = 0;

        protected float lastRecvPositionUpdateTime;

        public readonly Dictionary<string, ObjectProperty> Properties;
        public Dictionary<string, ObjectProperty> ObjectProperties
        {
            get { return Properties; }
        }

        protected Key[] keys;
        
        private Item selectedConstruction;
        private Item[] selectedItems;

        public byte TeamID = 0;

        public AnimController AnimController;

        private Vector2 cursorPosition;

        protected bool needsAir;
        protected float oxygen, oxygenAvailable;
        protected float drowningTime;

        private float health;
        protected float minHealth, maxHealth;

        protected Item closestItem;
        private Character closestCharacter, selectedCharacter;

        private Dictionary<object, HUDProgressBar> hudProgressBars;

        private bool isDead;
        private CauseOfDeath lastAttackCauseOfDeath;
        private CauseOfDeath causeOfDeath;
        
        public readonly bool IsHumanoid;

        //the name of the species (e.q. human)
        public readonly string SpeciesName;

        protected float soundTimer;
        protected float soundInterval;

        private float bleeding;

        private List<CharacterSound> sounds;

        private float attackCoolDown;

        public Entity ViewTarget
        {
            get;
            set;
        }        

        private CharacterInfo info;
        public CharacterInfo Info
        {
            get
            { 
                return info;
            }
            set 
            {
                if (info != null && info != value) info.Remove();

                info = value;
                if (info != null) info.Character = this;
            }
        }

        public string Name
        {
            get
            {
                return info != null && !string.IsNullOrWhiteSpace(info.Name) ? info.Name : SpeciesName;
            }
        }

        public string ConfigPath
        {
            get;
            private set;
        }

        public float Mass
        {
            get { return AnimController.Mass; }
        }

        public CharacterInventory Inventory
        {
            get { return inventory; }
        }
        
        private Color speechBubbleColor;
        private float speechBubbleTimer;

        private float lockHandsTimer;
        public bool LockHands
        {
            get
            {
                return lockHandsTimer > 0.0f;
            }
            set
            {
                lockHandsTimer = MathHelper.Clamp(lockHandsTimer + (value ? 1.0f : -0.5f), 0.0f, 10.0f);
            }
        }

        public bool AllowInput
        {
            get { return !IsUnconscious && Stun <= 0.0f && !isDead; }
        }

        public Vector2 CursorPosition
        {
            get { return cursorPosition; }
            set 
            { 
                if (!MathUtils.IsValid(value)) return;
                cursorPosition = value;
            }
        }

        public Vector2 CursorWorldPosition
        {
            get { return Submarine == null ? cursorPosition : cursorPosition + Submarine.Position; }
        }

        public List<PosInfo> MemPos
        {
            get { return memPos; }
        }


        public List<PosInfo> MemLocalPos
        {
            get { return memLocalPos; }
        }

        public Character ClosestCharacter
        {
            get { return closestCharacter; }
        }

        public Character SelectedCharacter
        {
            get { return selectedCharacter; }
        }

        private float lowPassMultiplier;
        public float LowPassMultiplier
        {
            get { return lowPassMultiplier; }
            set { lowPassMultiplier = MathHelper.Clamp(value, 0.0f, 1.0f); }
        }

        private float obstructVisionAmount;
        public bool ObstructVision
        {
            get
            {
                return obstructVisionAmount > 0.5f;
            }
            set
            {
                obstructVisionAmount = 1.0f;
            }
        }

        public float SoundRange
        {
            get { return aiTarget.SoundRange; }
        }

        public float SightRange
        {
            get { return aiTarget.SightRange; }
        }

        private float pressureProtection;
        public float PressureProtection
        {
            get { return pressureProtection; }
            set
            {
                pressureProtection = MathHelper.Clamp(value, 0.0f, 100.0f);
            }
        }

        public bool IsUnconscious
        {
            get { return (needsAir && oxygen <= 0.0f) || health <= 0.0f; }
        }

        public bool NeedsAir
        {
            get { return needsAir; }
            set { needsAir = value; }
        }
        
        public float Oxygen
        {
            get { return oxygen; }
            set 
            {
                if (!MathUtils.IsValid(value)) return;
                oxygen = MathHelper.Clamp(value, -100.0f, 100.0f);
                if (oxygen == -100.0f) Kill(AnimController.InWater ? CauseOfDeath.Drowning : CauseOfDeath.Suffocation);
            }
        }

        public float OxygenAvailable
        {
            get { return oxygenAvailable; }
            set { oxygenAvailable = MathHelper.Clamp(value, 0.0f, 100.0f); }
        }

        public float Stun
        {
            get { return AnimController.StunTimer; }
            set
            {
                if (GameMain.Client != null) return;

                StartStun(value); 
            }
        }

        public float Health
        {
            get { return health; }
            set
            {
                if (!MathUtils.IsValid(value)) return;
                if (GameMain.Client != null) return;

                float newHealth = MathHelper.Clamp(value, minHealth, maxHealth);
                if (newHealth == health) return;

                health = newHealth;

                if (GameMain.Server != null)
                    GameMain.Server.CreateEntityEvent(this, new object[] { NetEntityEvent.Type.Status });
            }
        }
    
        public float MaxHealth
        {
            get { return maxHealth; }
        }

        public float Bleeding
        {
            get { return bleeding; }
            set 
            {
                if (!MathUtils.IsValid(value)) return;
                if (GameMain.Client != null) return;

                float newBleeding =  MathHelper.Clamp(value, 0.0f, 5.0f);
                if (newBleeding == bleeding) return;

                bleeding = newBleeding;
                
                if (GameMain.Server != null)
                    GameMain.Server.CreateEntityEvent(this, new object[] { NetEntityEvent.Type.Status });
            }
        }

        public Dictionary<object, HUDProgressBar> HUDProgressBars
        {
            get { return hudProgressBars; }
        }

        public HuskInfection huskInfection;
        public float HuskInfectionState
        {
            get 
            { 
                return huskInfection == null ? 0.0f : huskInfection.IncubationTimer; 
            }
            set
            {
                if (ConfigPath != humanConfigFile) return;
                
                if (value <= 0.0f)
                {
                    if (huskInfection != null)
                    {
                        //already active, can't cure anymore
                        if (huskInfection.State == HuskInfection.InfectionState.Active) return;
                        huskInfection.Remove(this);
                        huskInfection = null;
                    }
                }
                else
                {
                    if (huskInfection == null) huskInfection = new HuskInfection(this);
                    huskInfection.IncubationTimer = MathHelper.Clamp(value, 0.0f, 1.0f);
                }
            }
        }

        public bool CanSpeak
        {
            get
            {
                return !IsUnconscious && Stun <= 0.0f && (huskInfection == null || huskInfection.CanSpeak);
            }
        }

        public bool DoesBleed
        {
            get;
            private set;
        }

        public float BleedingDecreaseSpeed
        {
            get;
            private set;
        }

        public float PressureTimer
        {
            get;
            private set;
        }

        public float DisableImpactDamageTimer
        {
            get;
            set;
        }

        public float SpeedMultiplier
        {
            get;
            set;
        }
        
        public Item[] SelectedItems
        {
            get { return selectedItems; }
        }

        public Item SelectedConstruction
        {
            get { return selectedConstruction; }
            set { selectedConstruction = value; }
        }

        public Item ClosestItem
        {
            get { return closestItem; }
        }

        public virtual AIController AIController
        {
            get { return null; }
        }

        public bool IsDead
        {
            get { return isDead; }
        }

        public CauseOfDeath CauseOfDeath
        {
            get { return causeOfDeath; }
        }

        public bool CanBeSelected
        {
            get
            {
                return isDead || Stun > 0.0f || LockHands;
            }
        }

        public override Vector2 SimPosition
        {
            get { return AnimController.Collider.SimPosition; }
        }

        public override Vector2 Position
        {
            get { return ConvertUnits.ToDisplayUnits(SimPosition); }
        }

        public override Vector2 DrawPosition
        {
            get { return AnimController.MainLimb.body.DrawPosition; }
        }

        public delegate void OnDeathHandler(Character character, CauseOfDeath causeOfDeath);
        public OnDeathHandler OnDeath;
        
        public static Character Create(CharacterInfo characterInfo, Vector2 position, bool isRemotePlayer = false, bool hasAi=true)
        {
            return Create(characterInfo.File, position, characterInfo, isRemotePlayer, hasAi);
        }

        public static Character Create(string file, Vector2 position, CharacterInfo characterInfo = null, bool isRemotePlayer = false, bool hasAi=true)
        {
#if LINUX            
            if (!System.IO.File.Exists(file)) 
            {

                //if the file was not found, attempt to convert the name of the folder to upper case
                var splitPath = file.Split('/');
                if (splitPath.Length > 2)
                {
                    splitPath[splitPath.Length-2] = 
                        splitPath[splitPath.Length-2].First().ToString().ToUpper() + splitPath[splitPath.Length-2].Substring(1);
                    
                    file = string.Join("/", splitPath);
                }

                if (!System.IO.File.Exists(file))
                {
                    DebugConsole.ThrowError("Spawning a character failed - file \""+file+"\" not found!");
                    return null;
                }
            }
#else
            if (!System.IO.File.Exists(file))
            {
                DebugConsole.ThrowError("Spawning a character failed - file \""+file+"\" not found!");
                return null;
            }
#endif
            

            if (file != humanConfigFile)
            {
                var enemyCharacter = new AICharacter(file, position, characterInfo, isRemotePlayer);
                var ai = new EnemyAIController(enemyCharacter, file);
                enemyCharacter.SetAI(ai);

                enemyCharacter.minHealth = 0.0f;

                return enemyCharacter;
            }
            else if (hasAi)
            {
                var aiCharacter = new AICharacter(file, position, characterInfo, isRemotePlayer);
                var ai = new HumanAIController(aiCharacter);
                aiCharacter.SetAI(ai);

                aiCharacter.minHealth = -100.0f;

                return aiCharacter;
            }

            var character = new Character(file, position, characterInfo, isRemotePlayer);
            character.minHealth = -100.0f;

            return character;
        }

        protected Character(string file, Vector2 position, CharacterInfo characterInfo = null, bool isRemotePlayer = false)
            : base(null)
        {
            keys = new Key[Enum.GetNames(typeof(InputType)).Length];

            for (int i = 0; i < Enum.GetNames(typeof(InputType)).Length; i++)
            {
                keys[i] = new Key(GameMain.Config.KeyBind((InputType)i));
            }

            ConfigPath = file;
            
            selectedItems = new Item[2];

            hudProgressBars = new Dictionary<object, HUDProgressBar>();

            IsRemotePlayer = isRemotePlayer;

            oxygen = 100.0f;
            oxygenAvailable = 100.0f;
            aiTarget = new AITarget(this);

            lowPassMultiplier = 1.0f;

            Properties = ObjectProperty.GetProperties(this);

            Info = characterInfo;
            if (file == humanConfigFile && characterInfo == null)
            {
                Info = new CharacterInfo(file);
            }

            XDocument doc = ToolBox.TryLoadXml(file);
            if (doc == null || doc.Root == null) return;
            
            SpeciesName = ToolBox.GetAttributeString(doc.Root, "name", "Unknown");
            
            IsHumanoid = ToolBox.GetAttributeBool(doc.Root, "humanoid", false);
            
            if (IsHumanoid)
            {
                AnimController = new HumanoidAnimController(this, doc.Root.Element("ragdoll"));
                AnimController.TargetDir = Direction.Right;
                inventory = new CharacterInventory(16, this);
            }
            else
            {
                AnimController = new FishAnimController(this, doc.Root.Element("ragdoll"));
                PressureProtection = 100.0f;
            }

            AnimController.SetPosition(ConvertUnits.ToSimUnits(position));
            
            maxHealth = ToolBox.GetAttributeFloat(doc.Root, "health", 100.0f);
            health = maxHealth;

            DoesBleed = ToolBox.GetAttributeBool(doc.Root, "doesbleed", true);
            BleedingDecreaseSpeed = ToolBox.GetAttributeFloat(doc.Root, "bleedingdecreasespeed", 0.05f);

            needsAir = ToolBox.GetAttributeBool(doc.Root, "needsair", false);
            drowningTime = ToolBox.GetAttributeFloat(doc.Root, "drowningtime", 10.0f);

            soundInterval = ToolBox.GetAttributeFloat(doc.Root, "soundinterval", 10.0f);

            var soundElements = doc.Root.Elements("sound").ToList();

            sounds = new List<CharacterSound>();
            foreach (XElement soundElement in soundElements)
            {
                sounds.Add(new CharacterSound(soundElement));
            }
            

            if (file == humanConfigFile)
            {
                if (Info.PickedItemIDs.Any())
                {
                    for (ushort i = 0; i < Info.PickedItemIDs.Count; i++ )
                    {
                        if (Info.PickedItemIDs[i] == 0) continue;

                        Item item = FindEntityByID(Info.PickedItemIDs[i]) as Item;

                        System.Diagnostics.Debug.Assert(item != null);
                        if (item == null) continue;

                        item.Pick(this, true, true, true);
                        inventory.TryPutItem(item, i, false, false);
                    }
                }
            }


            AnimController.FindHull(null);
            if (AnimController.CurrentHull != null) Submarine = AnimController.CurrentHull.Submarine;

            CharacterList.Add(this);

            Enabled = true;
        }

        private static string humanConfigFile;
        public static string HumanConfigFile
        {
            get 
            {
                if (string.IsNullOrEmpty(humanConfigFile))
                {
                    var characterFiles = GameMain.SelectedPackage.GetFilesOfType(ContentType.Character);

                    humanConfigFile = characterFiles.Find(c => c.EndsWith("human.xml"));
                    if (humanConfigFile == null)
                    {
                        DebugConsole.ThrowError("Couldn't find a config file for humans from the selected content package!");
                        DebugConsole.ThrowError("(The config file must end with \"human.xml\")");
                        return "";
                    }
                }
                return humanConfigFile; 
            }
        }

        public bool IsKeyHit(InputType inputType)
        {
            if (GameMain.Server != null && Character.Controlled != this)
            {
                switch (inputType)
                {
                    case InputType.Left:
                        return !(dequeuedInput.HasFlag(InputNetFlags.Left)) && (prevDequeuedInput.HasFlag(InputNetFlags.Left));
                        //break;
                    case InputType.Right:
                        return !(dequeuedInput.HasFlag(InputNetFlags.Right)) && (prevDequeuedInput.HasFlag(InputNetFlags.Right));
                        //break;
                    case InputType.Up:
                        return !(dequeuedInput.HasFlag(InputNetFlags.Up)) && (prevDequeuedInput.HasFlag(InputNetFlags.Up));
                        //break;
                    case InputType.Down:
                        return !(dequeuedInput.HasFlag(InputNetFlags.Down)) && (prevDequeuedInput.HasFlag(InputNetFlags.Down));
                    //break;
                    case InputType.Run:
                        return !(dequeuedInput.HasFlag(InputNetFlags.Run)) && (prevDequeuedInput.HasFlag(InputNetFlags.Run));
                    //break;
                    case InputType.Select:
                        return dequeuedInput.HasFlag(InputNetFlags.Select); //TODO: clean up the way this input is registered
                    //break;
                    default:
                        return false;
                        //break;
                }
            }

            return keys[(int)inputType].Hit;
        }

        public bool IsKeyDown(InputType inputType)
        {
            if (GameMain.Server != null && Character.Controlled != this)
            {
                switch (inputType)
                {
                    case InputType.Left:
                        return dequeuedInput.HasFlag(InputNetFlags.Left);
                    case InputType.Right:
                        return dequeuedInput.HasFlag(InputNetFlags.Right);
                    case InputType.Up:
                        return dequeuedInput.HasFlag(InputNetFlags.Up);                        
                    case InputType.Down:
                        return dequeuedInput.HasFlag(InputNetFlags.Down);                        
                    case InputType.Run:
                        return dequeuedInput.HasFlag(InputNetFlags.Run);                        
                    case InputType.Select:
                        return false; //TODO: clean up the way this input is registered
                    case InputType.Aim:
                        return dequeuedInput.HasFlag(InputNetFlags.Aim);
                    case InputType.Use:
                        return dequeuedInput.HasFlag(InputNetFlags.Use);
                    case InputType.Attack:
                        return dequeuedInput.HasFlag(InputNetFlags.Attack);
                }
                return false;
            }
            
            return keys[(int)inputType].Held;
        }

        public void SetInput(InputType inputType, bool hit, bool held)
        {
            keys[(int)inputType].Hit = hit;
            keys[(int)inputType].Held = held;
        }

        public void ClearInput(InputType inputType)
        {
            keys[(int)inputType].Hit = false;
            keys[(int)inputType].Held = false;            
        }

        public void ClearInputs()
        {
            foreach (Key key in keys)
            {
                key.Hit = false;
                key.Held = false;
            }
        }

        public override string ToString()
        {
            return (info != null && !string.IsNullOrWhiteSpace(info.Name)) ? info.Name : SpeciesName;
        }

        public void GiveJobItems(WayPoint spawnPoint)
        {
            if (info == null || info.Job == null) return;

            info.Job.GiveJobItems(this, spawnPoint);
        }

        public int GetSkillLevel(string skillName)
        {
            return (Info==null || Info.Job==null) ? 0 : Info.Job.GetSkillLevel(skillName);
        }

        float findClosestTimer;

        public Vector2 GetTargetMovement()
        {
            Vector2 targetMovement = Vector2.Zero;
            if (IsKeyDown(InputType.Left))  targetMovement.X -= 1.0f;
            if (IsKeyDown(InputType.Right)) targetMovement.X += 1.0f;
            if (IsKeyDown(InputType.Up))    targetMovement.Y += 1.0f;
            if (IsKeyDown(InputType.Down))  targetMovement.Y -= 1.0f;

            //the vertical component is only used for falling through platforms and climbing ladders when not in water,
            //so the movement can't be normalized or the Character would walk slower when pressing down/up
            if (AnimController.InWater)
            {
                float length = targetMovement.Length();
                if (length > 0.0f) targetMovement = targetMovement / length;
            }

            if (IsKeyDown(InputType.Run))
            {
                //can't run if
                //  - not a humanoid
                //  - dragging someone
                //  - crouching
                //  - moving backwards
                if (AnimController is HumanoidAnimController &&
                    selectedCharacter == null &&
                    !((HumanoidAnimController)AnimController).Crouching &&
                    Math.Sign(targetMovement.X) != -Math.Sign(AnimController.Dir))
                {
                    targetMovement *= 3.0f;
                }
            }


            targetMovement *= SpeedMultiplier;
            SpeedMultiplier = 1.0f;

            return targetMovement;
        }

        public void Control(float deltaTime, Camera cam)
        {
            ViewTarget = null;
            if (!AllowInput) return;

            if (!(this is AICharacter) || controlled == this)
            {
                Vector2 targetMovement = GetTargetMovement();

                AnimController.TargetMovement = targetMovement;
                AnimController.IgnorePlatforms = AnimController.TargetMovement.Y < 0.0f;
            }

            if (AnimController is HumanoidAnimController)
            {
                ((HumanoidAnimController) AnimController).Crouching = IsKeyDown(InputType.Crouch);
            }

            if (AnimController.onGround &&
                !AnimController.InWater &&
                AnimController.Anim != AnimController.Animation.UsingConstruction &&
                AnimController.Anim != AnimController.Animation.CPR)
            {
                //Limb head = AnimController.GetLimb(LimbType.Head);

                if (cursorPosition.X < AnimController.Collider.Position.X - 10.0f)
                {
                    AnimController.TargetDir = Direction.Left;
                }
                else if (cursorPosition.X > AnimController.Collider.Position.X + 10.0f)
                {
                    AnimController.TargetDir = Direction.Right;
                }
            }

            if (GameMain.Server != null && Character.Controlled != this)
            {
                if (dequeuedInput.HasFlag(InputNetFlags.FacingLeft))
                {
                    AnimController.TargetDir = Direction.Left;
                }
                else
                {
                    AnimController.TargetDir = Direction.Right;
                }
            }
            else if (GameMain.Client != null && Character.controlled != this)
            {
                if (memPos.Count > 0)
                {
                    AnimController.TargetDir = memPos[0].Direction;
                }
            }

            if (attackCoolDown >0.0f)
            {
                attackCoolDown -= deltaTime;
            }
            else if (IsKeyDown(InputType.Attack))
            {
                var attackLimb = AnimController.Limbs.FirstOrDefault(l => l.attack != null);

                if (attackLimb != null)
                {
                    Vector2 attackPos =
                        attackLimb.SimPosition + Vector2.Normalize(cursorPosition - attackLimb.Position) * ConvertUnits.ToSimUnits(attackLimb.attack.Range);

                    var body = Submarine.PickBody(
                        attackLimb.SimPosition,
                        attackPos,
                        AnimController.Limbs.Select(l => l.body.FarseerBody).ToList(),
                        Physics.CollisionCharacter | Physics.CollisionWall);
                    
                    IDamageable attackTarget = null;
                    if (body != null)
                    {
                        attackPos = Submarine.LastPickedPosition;

                        if (body.UserData is Submarine)
                        {
                            var sub = ((Submarine)body.UserData);

                            body = Submarine.PickBody(
                                attackLimb.SimPosition - ((Submarine)body.UserData).SimPosition,
                                attackPos - ((Submarine)body.UserData).SimPosition,
                                AnimController.Limbs.Select(l => l.body.FarseerBody).ToList(),
                                Physics.CollisionWall);

                            if (body != null)
                            {
                                attackPos = Submarine.LastPickedPosition + sub.SimPosition;
                                attackTarget = body.UserData as IDamageable;
                            }
                        }
                        else
                        {
                            if (body.UserData is IDamageable)
                            {
                                attackTarget = (IDamageable)body.UserData;
                            }
                            else if (body.UserData is Limb)
                            {
                                attackTarget = ((Limb)body.UserData).character;                            
                            }                            
                        }
                    }

                    attackLimb.UpdateAttack(deltaTime, attackPos, attackTarget);

                    if (attackLimb.AttackTimer > attackLimb.attack.Duration)
                    {
                        attackLimb.AttackTimer = 0.0f;
                        attackCoolDown = 1.0f;
                    }
                }
            }

            for (int i = 0; i < selectedItems.Length; i++ )
            {
                if (selectedItems[i] == null) continue;
                if (i == 1 && selectedItems[0] == selectedItems[1]) continue;

                if (IsKeyDown(InputType.Use)) selectedItems[i].Use(deltaTime, this);
                if (IsKeyDown(InputType.Aim) && selectedItems[i] != null) selectedItems[i].SecondaryUse(deltaTime, this);                
            }

            if (selectedConstruction != null)
            {
                if (IsKeyDown(InputType.Use)) selectedConstruction.Use(deltaTime, this);
                if (selectedConstruction != null && IsKeyDown(InputType.Aim)) selectedConstruction.SecondaryUse(deltaTime, this);
            }

            if (selectedCharacter!=null)
            {
                if (Vector2.DistanceSquared(selectedCharacter.WorldPosition, WorldPosition) > 90000.0f || !selectedCharacter.CanBeSelected)
                {
                    DeselectCharacter();
                }
            }

                  
            if (IsRemotePlayer)
            {
                foreach (Key key in keys)
                {
                    key.ResetHit();
                }
            }
        }
        
        public bool HasEquippedItem(Item item)
        {
            return !inventory.IsInLimbSlot(item, InvSlotType.Any);
        }

        public bool HasSelectedItem(Item item)
        {
            return selectedItems.Contains(item);
        }

        public bool TrySelectItem(Item item)
        {
            bool rightHand = inventory.IsInLimbSlot(item, InvSlotType.RightHand);
            bool leftHand = inventory.IsInLimbSlot(item, InvSlotType.LeftHand);

            bool selected = false;
            if (rightHand && SelectedItems[0] == null)
            {
                selectedItems[0] = item;
                selected = true;
            }
            if (leftHand && SelectedItems[1] == null)
            {
                selectedItems[1] = item;
                selected = true;
            }

            return selected;
        }

        public bool TrySelectItem(Item item, int index)
        {
            if (selectedItems[index] != null) return false;

            selectedItems[index] = item;
            return true;
        }

        public void DeselectItem(Item item)
        {
            for (int i = 0; i < selectedItems.Length; i++)
            {
                if (selectedItems[i] == item) selectedItems[i] = null;
            }
        }

        public bool CanAccessInventory(Inventory inventory)
        {
            if (!AllowInput || LockHands) return false;

            if (inventory.Owner is Character && inventory.Owner != this)
            {
                var owner = (Character)inventory.Owner;

                return owner.isDead || owner.IsUnconscious || owner.Stun > 0.0f || owner.LockHands;
            }

            if (inventory.Owner is Item)
            {
                var owner = (Item)inventory.Owner;
                if (!CanAccessItem(owner))
                {
                    return false;
                }
            }
            return true;
        }

        public bool CanAccessItem(Item item)
        {
            if (!AllowInput || LockHands) return false;

            if (item.ParentInventory != null)
            {
                return CanAccessInventory(item.ParentInventory);
            }

            float maxDist = item.PickDistance * 1.2f;
            if (maxDist <= 0.01f)
            {
                maxDist = 150.0f;
            }

            if (Vector2.DistanceSquared(WorldPosition, item.WorldPosition) < maxDist*maxDist ||
                item.IsInsideTrigger(WorldPosition))
            {
                return true;
            }

            return item.GetComponent<Items.Components.Ladder>() != null;
        }

        private Item FindClosestItem(Vector2 mouseSimPos, out float distance)
        {
            distance = 0.0f;

            Limb torso = AnimController.GetLimb(LimbType.Torso);

            if (torso == null) return null;

            Vector2 pos = (torso.body.TargetPosition != null) ? (Vector2)torso.body.TargetPosition : torso.SimPosition;
            Vector2 pickPos = mouseSimPos;

            if (Submarine != null)
            {
                pos += Submarine.SimPosition;
                pickPos += Submarine.SimPosition;
            }

            if (selectedConstruction != null) pickPos = ConvertUnits.ToSimUnits(selectedConstruction.WorldPosition);

            return Item.FindPickable(pos, pickPos, AnimController.CurrentHull, selectedItems, out distance);
        }

        private Character FindClosestCharacter(Vector2 mouseSimPos, float maxDist = 150.0f)
        {
            Character closestCharacter = null;
            float closestDist = 0.0f;

            maxDist = ConvertUnits.ToSimUnits(maxDist);
            
            foreach (Character c in CharacterList)
            {
                if (c == this || !c.enabled) continue;

                if (Vector2.DistanceSquared(SimPosition, c.SimPosition) > maxDist*maxDist) continue;

                float dist = Vector2.DistanceSquared(mouseSimPos, c.SimPosition);
                if (dist < maxDist*maxDist && (closestCharacter==null || dist<closestDist))
                {
                    closestCharacter = c;
                    closestDist = dist;
                    continue;
                }
            }

            return closestCharacter;
        }

        private void TransformCursorPos()
        {
            if (Submarine == null)
            {
                //character is outside but cursor position inside
                if (cursorPosition.Y > Level.Loaded.Size.Y)
                {
                    var sub = Submarine.FindContaining(cursorPosition);
                    if (sub != null) cursorPosition += sub.Position;
                }
            }
            else
            {
                //character is inside but cursor position is outside
                if (cursorPosition.Y < Level.Loaded.Size.Y)
                {
                    cursorPosition -= Submarine.Position;
                }
            }
        }

        private void SelectCharacter(Character character)
        {
            if (character == null) return;

            selectedCharacter = character;
        }

        private void DeselectCharacter()
        {
            if (selectedCharacter == null) return;
            
            foreach (Limb limb in selectedCharacter.AnimController.Limbs)
            {
                if (limb.pullJoint != null) limb.pullJoint.Enabled = false;
            }

            selectedCharacter = null;
        }

        /// <summary>
        /// Control the Character according to player input
        /// </summary>
        public void ControlLocalPlayer(float deltaTime, Camera cam, bool moveCam = true)
        {
            if (!DisableControls)
            {
                for (int i = 0; i < keys.Length; i++ )
                {
                    keys[i].SetState();
                }
            }
            else
            {
                foreach (Key key in keys)
                {
                    if (key == null) continue;
                    key.Reset();
                }
            }

            if (moveCam && needsAir)
            {
                if (pressureProtection < 80.0f && 
                    (AnimController.CurrentHull == null || AnimController.CurrentHull.LethalPressure > 50.0f))
                {
                    float pressure = AnimController.CurrentHull == null ? 100.0f : AnimController.CurrentHull.LethalPressure;

                    cam.Zoom = MathHelper.Lerp(cam.Zoom,
                        (pressure / 50.0f) * Rand.Range(1.0f, 1.05f),
                        (pressure - 50.0f) / 50.0f);
                }
                cam.OffsetAmount = MathHelper.Lerp(cam.OffsetAmount, 250.0f, 0.05f);
            }
                        
            cursorPosition = cam.ScreenToWorld(PlayerInput.MousePosition);
            if (AnimController.CurrentHull != null && AnimController.CurrentHull.Submarine != null)
            {
                cursorPosition -= AnimController.CurrentHull.Submarine.Position;
            }

            Vector2 mouseSimPos = ConvertUnits.ToSimUnits(cursorPosition);

            if (Lights.LightManager.ViewTarget == this && Vector2.DistanceSquared(AnimController.Limbs[0].SimPosition, mouseSimPos) > 1.0f)
            {
                Body body = Submarine.PickBody(AnimController.Limbs[0].SimPosition, mouseSimPos);
                Structure structure = null;
                if (body != null) structure = body.UserData as Structure;
                if (structure != null)
                {
                    if (!structure.CastShadow && moveCam)
                    {
                        cam.OffsetAmount = MathHelper.Lerp(cam.OffsetAmount, 500.0f, 0.05f);
                    }
                }
            }

            if (!LockHands)
            {
                //find the closest item if selectkey has been hit, or if the Character is being
                //controlled by the player (in order to highlight it)

                if (findClosestTimer <= 0.0f || Screen.Selected == GameMain.EditMapScreen)
                {
                    closestCharacter = FindClosestCharacter(mouseSimPos);
                    if (closestCharacter != null && closestCharacter.info==null)
                    {
                        closestCharacter = null;
                    }

                    float closestItemDist = 0.0f;
                    closestItem = FindClosestItem(mouseSimPos, out closestItemDist);

                    if (closestCharacter != null && closestItem != null)
                    {
                        if (Vector2.DistanceSquared(closestCharacter.SimPosition, mouseSimPos) < ConvertUnits.ToSimUnits(closestItemDist)*ConvertUnits.ToSimUnits(closestItemDist))
                        {
                            if (selectedConstruction != closestItem) closestItem = null;
                        }
                        else
                        {
                            closestCharacter = null;
                        }
                    }

                    findClosestTimer = 0.1f;
                }
                else
                {
                    findClosestTimer -= deltaTime;
                }

                if (selectedCharacter == null && closestItem != null)
                {
                    closestItem.IsHighlighted = true;
                    if (!LockHands && closestItem.Pick(this))
                    {
                        
                    }
                }

                if (IsKeyHit(InputType.Select))
                {
                    if (selectedCharacter != null)
                    {
                        DeselectCharacter();
                    }
                    else if (closestCharacter != null && closestCharacter.IsHumanoid && closestCharacter.CanBeSelected)
                    {
                        SelectCharacter(closestCharacter);
                    }
                }
            }
            else
            {
                if (selectedCharacter != null) DeselectCharacter();
                selectedConstruction = null;
                closestItem = null;
                closestCharacter = null;
            }

            DisableControls = false;
        }
        

        public static void UpdateAnimAll(float deltaTime)
        {
            foreach (Character c in CharacterList)
            {
                if (!c.Enabled || c.AnimController.Frozen) continue;
                
                c.AnimController.UpdateAnim(deltaTime);
            }
        }
        
        public static void AddAllToGUIUpdateList()
        {
            for (int i = 0; i < CharacterList.Count; i++)
            {
                CharacterList[i].AddToGUIUpdateList();
            }
        }

        public static void UpdateAll(Camera cam, float deltaTime)
        {
            //if (NewCharacterQueue.Count>0)
            //{
            //    new Character(NewCharacterQueue.Dequeue(), Vector2.Zero);
            //}

            for (int i = 0; i<CharacterList.Count; i++)
            {
                CharacterList[i].Update(cam, deltaTime);
            }
        }

        public virtual void AddToGUIUpdateList()
        {
            if (controlled == this)
            {
                CharacterHUD.AddToGUIUpdateList(this);
            }
        }

        public virtual void Update(Camera cam, float deltaTime)
        {
            if (GameMain.Client != null && this == Controlled && !isSynced) return;

            if (!Enabled) return;
            
            PreviousHull = CurrentHull;
            CurrentHull = Hull.FindHull(WorldPosition, CurrentHull, true);
            //if (PreviousHull != CurrentHull && Character.Controlled == this) Hull.DetectItemVisibility(this); //WIP item culling

            speechBubbleTimer = Math.Max(0.0f, speechBubbleTimer - deltaTime);

            obstructVisionAmount = Math.Max(obstructVisionAmount - deltaTime, 0.0f);
            
            if (inventory!=null)
            {
                foreach (Item item in inventory.Items)
                {
                    if (item == null || item.body == null || item.body.Enabled) continue;

                    item.SetTransform(SimPosition, 0.0f);
                    item.Submarine = Submarine;
                }
            }

            if (huskInfection != null) huskInfection.Update(deltaTime, this);
            
            if (isDead) return;

            if (this != Character.Controlled)
            {
                if (GameMain.Client != null)
                {
                    //freeze AI characters if more than 1 seconds have passed since last update from the server
                    if (lastRecvPositionUpdateTime < NetTime.Now - 1.0f)
                    {
                        AnimController.Frozen = true; 
                        memPos.Clear();
                        //hide after 2 seconds
                        if (lastRecvPositionUpdateTime < NetTime.Now - 2.0f)
                        {
                            Enabled = false;
                            return;
                        }
                    }                       
                }
                else if (GameMain.Server != null && !(this is AICharacter))
                {
                    if (!AllowInput)
                    {
                        AnimController.Frozen = false;
                    }
                    else if (memInput.Count == 0)
                    {
                        AnimController.Frozen = true;
                    }
                    else
                    {
                        AnimController.Frozen = false;
                        prevDequeuedInput = dequeuedInput;

                        dequeuedInput = memInput[memInput.Count - 1].states;

                        double aimAngle = ((double)memInput[memInput.Count - 1].intAim / 65535.0) * 2.0 * Math.PI;
                        cursorPosition = (ViewTarget == null ? AnimController.Collider.Position : ViewTarget.Position)
                            + new Vector2((float)Math.Cos(aimAngle), (float)Math.Sin(aimAngle)) * 60.0f;

                        var closestEntity = Entity.FindEntityByID(memInput[memInput.Count - 1].interact);
                        if (closestEntity is Item)
                        {
                            closestItem = closestEntity as Item;
                        }
                        else if (closestEntity is Character)
                        {
                            closestCharacter = closestEntity as Character;
                        }

                        memInput.RemoveAt(memInput.Count - 1);

                        TransformCursorPos();

                        if ((dequeuedInput == InputNetFlags.None || dequeuedInput == InputNetFlags.FacingLeft) && Math.Abs(AnimController.Collider.LinearVelocity.X) < 0.005f && Math.Abs(AnimController.Collider.LinearVelocity.Y) < 0.2f)
                        {
                            while (memInput.Count > 5 && memInput[memInput.Count - 1].states == dequeuedInput)
                            {
                                //remove inputs where the player is not moving at all
                                //helps the server catch up, shouldn't affect final position
                                memInput.RemoveAt(memInput.Count - 1);
                            }
                        }
                    }
                }
            }
            else if (GameMain.Client != null)
            {
                memLocalPos.Add(new PosInfo(SimPosition, AnimController.TargetDir, LastNetworkUpdateID));
                
                InputNetFlags newInput = InputNetFlags.None;
                if (IsKeyDown(InputType.Left))      newInput |= InputNetFlags.Left;
                if (IsKeyDown(InputType.Right))     newInput |= InputNetFlags.Right;
                if (IsKeyDown(InputType.Up))        newInput |= InputNetFlags.Up;
                if (IsKeyDown(InputType.Down))      newInput |= InputNetFlags.Down;
                if (IsKeyDown(InputType.Run))       newInput |= InputNetFlags.Run;
                if (IsKeyHit(InputType.Select))     newInput |= InputNetFlags.Select; //TODO: clean up the way this input is registered
                if (IsKeyDown(InputType.Use))       newInput |= InputNetFlags.Use;
                if (IsKeyDown(InputType.Aim))       newInput |= InputNetFlags.Aim;
                if (IsKeyDown(InputType.Attack))    newInput |= InputNetFlags.Attack;
                    
                if (AnimController.TargetDir == Direction.Left) newInput |= InputNetFlags.FacingLeft;

                Vector2 relativeCursorPos = cursorPosition - (ViewTarget == null ? AnimController.Collider.Position : ViewTarget.Position);
                relativeCursorPos.Normalize();
                UInt16 intAngle = (UInt16)(65535.0*Math.Atan2(relativeCursorPos.Y,relativeCursorPos.X)/(2.0*Math.PI));
                
                NetInputMem newMem = new NetInputMem();
                newMem.states = newInput;
                newMem.intAim = intAngle;
                if (closestItem != null)
                {
                    newMem.interact = closestItem.ID;
                }
                else if (closestCharacter != null)
                {
                    newMem.interact = closestCharacter.ID;
                }

                memInput.Insert(0, newMem);
                LastNetworkUpdateID++;
                if (memInput.Count > 60)
                {
                    memInput.RemoveRange(60, memInput.Count - 60);
                }                
            }
            else
            {
                //this == Character.Controlled && GameMain.Client == null
                AnimController.Frozen = false;
            }

            if (networkUpdateSent)
            {
                foreach (Key key in keys)
                {
                    key.DequeueHit();
                    key.DequeueHeld();
                }

                networkUpdateSent = false;
            }

            DisableImpactDamageTimer -= deltaTime;
            
            if (needsAir)
            {
                bool protectedFromPressure = PressureProtection > 0.0f;
                
                protectedFromPressure = protectedFromPressure && WorldPosition.Y > SubmarineBody.DamageDepth;
                           
                if (!protectedFromPressure && 
                    (AnimController.CurrentHull == null || AnimController.CurrentHull.LethalPressure >= 80.0f))
                {
                    PressureTimer += ((AnimController.CurrentHull == null) ?
                        100.0f : AnimController.CurrentHull.LethalPressure) * deltaTime;

                    if (PressureTimer >= 100.0f)
                    {
                        if (controlled == this) cam.Zoom = 5.0f;
                        Implode();
                        return;
                    }
                }
                else
                {
                    PressureTimer = 0.0f;
                }
            }

            if (controlled == this)
            {
                Lights.LightManager.ViewTarget = this;
                CharacterHUD.Update(deltaTime, this);

                foreach (HUDProgressBar progressBar in hudProgressBars.Values)
                {
                    progressBar.Update(deltaTime);
                }

                foreach (var pb in hudProgressBars.Where(pb => pb.Value.FadeTimer<=0.0f).ToList())
                {
                    hudProgressBars.Remove(pb.Key);
                }
            }

            if (IsUnconscious)
            {
                UpdateUnconscious(deltaTime);
                return;
            }

            if (controlled == this)
            {
                ControlLocalPlayer(deltaTime, cam);
            }

            Control(deltaTime, cam);
                        
            if (selectedConstruction != null && !selectedConstruction.IsInPickRange(WorldPosition))
            {
                selectedConstruction = null;
            }
            
            if (controlled != this && !(this is AICharacter))
            {
                if (!LockHands)
                {
                    Vector2 mouseSimPos = ConvertUnits.ToSimUnits(cursorPosition);

                    if (IsKeyHit(InputType.Select) && GameMain.Server==null)
                    {
                        closestCharacter = FindClosestCharacter(mouseSimPos);
                        if (closestCharacter != null && closestCharacter.info == null)
                        {
                            closestCharacter = null;
                        }

                        float closestItemDist = 0.0f;
                        closestItem = FindClosestItem(mouseSimPos, out closestItemDist);

                        if (closestCharacter != null && closestItem != null)
                        {
                            if (closestItem != null) closestItemDist = (closestItem.Position - AnimController.Collider.Position).Length();
                            if (Vector2.Distance(closestCharacter.SimPosition, mouseSimPos) < ConvertUnits.ToSimUnits(closestItemDist))
                            {
                                if (selectedConstruction != closestItem) closestItem = null;
                            }
                            else
                            {
                                closestCharacter = null;
                            }
                        }
                    }
                    
                    if (selectedCharacter == null && closestItem != null)
                    {
                        //DebugConsole.NewMessage(closestItem.ToString(), Color.Yellow);
                        //closestItem.IsHighlighted = true;
                        if (!LockHands && closestItem.Pick(this))
                        {
                            if (AnimController.Anim == AnimController.Animation.Climbing)
                            {
                                //DebugConsole.NewMessage("ladder woo",Color.Lime);
                            }
                        }
                    }

                    if (IsKeyHit(InputType.Select))
                    {
                        if (selectedCharacter != null)
                        {
                            DeselectCharacter();
                        }
                        else if (closestCharacter != null && closestCharacter.IsHumanoid && closestCharacter.CanBeSelected)
                        {
                            SelectCharacter(closestCharacter);
                        }
                    }
                }
                else
                {
                    if (selectedCharacter != null) DeselectCharacter();
                    selectedConstruction = null;
                    closestItem = null;
                    closestCharacter = null;
                }
            }

            if (selectedCharacter != null && AnimController.Anim == AnimController.Animation.CPR)
            {
                if (GameMain.Client == null) selectedCharacter.Oxygen += (GetSkillLevel("Medical") / 10.0f) * deltaTime;
            }

            UpdateSightRange();
            if (aiTarget != null) aiTarget.SoundRange = 0.0f;

            lowPassMultiplier = MathHelper.Lerp(lowPassMultiplier, 1.0f, 0.1f);

            if (needsAir) UpdateOxygen(deltaTime);

            Health -= bleeding * deltaTime;
            Bleeding -= BleedingDecreaseSpeed * deltaTime;

            if (health <= minHealth) Kill(CauseOfDeath.Bloodloss);

            if (!IsDead) LockHands = false;
        }

        private void UpdateOxygen(float deltaTime)
        {
            float prevOxygen = oxygen;
            Oxygen += deltaTime * (oxygenAvailable < 30.0f ? -5.0f : 10.0f);

            if (prevOxygen > 0.0f && Oxygen <= 0.0f && controlled == this)
            {
                SoundPlayer.PlaySound("drown");                
            }

            PressureProtection -= deltaTime * 100.0f;

            float hullAvailableOxygen = 0.0f;

            if (!AnimController.HeadInWater && AnimController.CurrentHull != null)
            {
                hullAvailableOxygen = AnimController.CurrentHull.OxygenPercentage;

                AnimController.CurrentHull.Oxygen -= Hull.OxygenConsumptionSpeed * deltaTime;
            }

            OxygenAvailable += Math.Sign(hullAvailableOxygen - oxygenAvailable) * deltaTime * 50.0f;
        }

        private void UpdateUnconscious(float deltaTime)
        {
            Stun = Math.Max(5.0f, Stun);

            AnimController.ResetPullJoints();
            selectedConstruction = null;

            if (oxygen <= 0.0f) Oxygen -= deltaTime * 0.5f;

            if (health <= 0.0f)
            {
                AddDamage(bleeding > 0.5f ? CauseOfDeath.Bloodloss : CauseOfDeath.Damage, Math.Max(bleeding, 1.0f) * deltaTime, null);
            }
        }

        private void UpdateSightRange()
        {
            if (aiTarget == null) return;

            aiTarget.SightRange = Mass*100.0f + AnimController.Collider.LinearVelocity.Length()*500.0f;
        }
        
        public void ShowSpeechBubble(float duration, Color color)
        {
            speechBubbleTimer = Math.Max(speechBubbleTimer, duration);
            speechBubbleColor = color;
        }

        public void Draw(SpriteBatch spriteBatch)
        {
            if (!Enabled) return;

            AnimController.Draw(spriteBatch);
        }

        public void DrawHUD(SpriteBatch spriteBatch, Camera cam)
        {
            CharacterHUD.Draw(spriteBatch, this, cam);
        }

        public virtual void DrawFront(SpriteBatch spriteBatch, Camera cam)
        {
            if (!Enabled) return;

            if (GameMain.DebugDraw)
            {
                AnimController.DebugDraw(spriteBatch);

                if (aiTarget != null) aiTarget.Draw(spriteBatch);
            }
            
            if (memPos != null && memPos.Count > 0 && controlled == this)
            {
                PosInfo serverPos = memPos.Last();
                Vector2 remoteVec = ConvertUnits.ToDisplayUnits(serverPos.Position);
                if (Submarine != null)
                {
                    remoteVec += Submarine.DrawPosition;
                }
                remoteVec.Y = -remoteVec.Y;

                PosInfo localPos = memLocalPos.Find(m => m.ID == serverPos.ID);
                int mpind = memLocalPos.FindIndex(lp => lp.ID == localPos.ID);
                PosInfo? localPos1 = mpind > 0 ? memLocalPos[mpind - 1] : (PosInfo?)null;
                PosInfo? localPos2 = mpind < memLocalPos.Count-1 ? memLocalPos[mpind + 1] : (PosInfo?)null;

                Vector2 localVec = ConvertUnits.ToDisplayUnits(localPos.Position);
                Vector2 localVec1 = localPos1 != null ? ConvertUnits.ToDisplayUnits(((PosInfo)localPos1).Position) : Vector2.Zero;
                Vector2 localVec2 = localPos2 != null ? ConvertUnits.ToDisplayUnits(((PosInfo)localPos2).Position) : Vector2.Zero;
                if (Submarine != null)
                {
                    localVec += Submarine.DrawPosition;
                    localVec1 += Submarine.DrawPosition;
                    localVec2 += Submarine.DrawPosition;
                }
                localVec.Y = -localVec.Y;
                localVec1.Y = -localVec1.Y;
                localVec2.Y = -localVec2.Y;

                GUI.DrawLine(spriteBatch, remoteVec, localVec, Color.Yellow, 0, 10);
                if (localPos1 != null) GUI.DrawLine(spriteBatch, remoteVec, localVec1, Color.Lime, 0, 6);
                if (localPos2 != null) GUI.DrawLine(spriteBatch, remoteVec, localVec2, Color.Red, 0, 3);
            }

            Vector2 mouseDrawPos = CursorWorldPosition;
            mouseDrawPos.Y = -mouseDrawPos.Y;
            GUI.DrawLine(spriteBatch, mouseDrawPos - new Vector2(0, 5), mouseDrawPos + new Vector2(0, 5), Color.Red, 0, 10);

            Vector2 closestItemPos = closestItem != null ? closestItem.DrawPosition : Vector2.Zero;
            closestItemPos.Y = -closestItemPos.Y;
            GUI.DrawLine(spriteBatch, closestItemPos - new Vector2(0, 5), closestItemPos + new Vector2(0, 5), Color.Lime, 0, 10);
            
            Vector2 pos = DrawPosition;
            pos.Y = -pos.Y;

            if (speechBubbleTimer > 0.0f)
            {
                GUI.SpeechBubbleIcon.Draw(spriteBatch, pos - Vector2.UnitY * 100.0f,
                    speechBubbleColor * Math.Min(speechBubbleTimer, 1.0f), 0.0f,
                    Math.Min((float)speechBubbleTimer, 1.0f));
            }

            if (this == controlled) return;


            if (info != null)
            {
                Vector2 namePos = new Vector2(pos.X, pos.Y - 110.0f - (5.0f / cam.Zoom)) - GUI.Font.MeasureString(Info.Name) * 0.5f / cam.Zoom;
                Color nameColor = Color.White;

                if (Character.Controlled != null && TeamID != Character.Controlled.TeamID)
                {
                    nameColor = Color.Red;
                }
                spriteBatch.DrawString(GUI.Font, Info.Name, namePos + new Vector2(1.0f / cam.Zoom, 1.0f / cam.Zoom), Color.Black, 0.0f, Vector2.Zero, 1.0f / cam.Zoom, SpriteEffects.None, 0.001f);
                spriteBatch.DrawString(GUI.Font, Info.Name, namePos, nameColor, 0.0f, Vector2.Zero, 1.0f / cam.Zoom, SpriteEffects.None, 0.0f);

                if (GameMain.DebugDraw)
                {
                    spriteBatch.DrawString(GUI.Font, ID.ToString(), namePos - new Vector2(0.0f, 20.0f), Color.White);
                }
            }

            if (isDead) return;

            if (health < maxHealth * 0.98f)
            {
                Vector2 healthBarPos = new Vector2(pos.X - 50, DrawPosition.Y + 100.0f);
            
                GUI.DrawProgressBar(spriteBatch, healthBarPos, new Vector2(100.0f, 15.0f), health / maxHealth, Color.Lerp(Color.Red, Color.Green, health / maxHealth) * 0.8f);
            }
        }

        /// <summary>
        /// Creates a progress bar that's "linked" to the specified object (or updates an existing one if there's one already linked to the object)
        /// The progress bar will automatically fade out after 1 sec if the method hasn't been called during that time
        /// </summary>
        public HUDProgressBar UpdateHUDProgressBar(object linkedObject, Vector2 worldPosition, float progress, Color emptyColor, Color fullColor)
        {
            if (controlled != this) return null;

            HUDProgressBar progressBar = null;
            if (!hudProgressBars.TryGetValue(linkedObject, out progressBar))
            {
                progressBar = new HUDProgressBar(worldPosition, Submarine, emptyColor, fullColor);
                hudProgressBars.Add(linkedObject, progressBar);
            }

            progressBar.WorldPosition = worldPosition;
            progressBar.FadeTimer = Math.Max(progressBar.FadeTimer, 1.0f);
            progressBar.Progress = progress;

            return progressBar;
        }

        public void PlaySound(CharacterSound.SoundType soundType)
        {
            if (sounds == null || sounds.Count == 0) return;

            var matchingSounds = sounds.FindAll(s => s.Type == soundType);
            if (matchingSounds.Count == 0) return;

            var selectedSound = matchingSounds[Rand.Int(matchingSounds.Count)];
            selectedSound.Sound.Play(1.0f, selectedSound.Range, AnimController.WorldPosition);
        }

        public virtual void AddDamage(CauseOfDeath causeOfDeath, float amount, IDamageable attacker)
        {
            Health = health-amount;
            if (amount > 0.0f)
            {
                lastAttackCauseOfDeath = causeOfDeath;
                if (controlled == this) CharacterHUD.TakeDamage(amount);
            }
            if (health <= minHealth) Kill(causeOfDeath);
        }

        public virtual AttackResult AddDamage(IDamageable attacker, Vector2 worldPosition, Attack attack, float deltaTime, bool playSound = false)
        {
            var attackResult = AddDamage(worldPosition, attack.DamageType, attack.GetDamage(deltaTime), attack.GetBleedingDamage(deltaTime), attack.Stun, playSound, attack.TargetForce);

            var attackingCharacter = attacker as Character;
            if (attackingCharacter != null && attackingCharacter.AIController == null)
            {
                GameServer.Log(Name + " attacked by " + attackingCharacter.Name+". Damage: "+attackResult.Damage+" Bleeding damage: "+attackResult.Bleeding, Color.Orange);
            }

            return attackResult;
        }

        public AttackResult AddDamage(Vector2 worldPosition, DamageType damageType, float amount, float bleedingAmount, float stun, bool playSound, float attackForce = 0.0f)
        {
            StartStun(stun);

            Limb closestLimb = null;
            float closestDistance = 0.0f;
            foreach (Limb limb in AnimController.Limbs)
            {
                float distance = Vector2.Distance(worldPosition, limb.WorldPosition);
                if (closestLimb == null || distance < closestDistance)
                {
                    closestLimb = limb;
                    closestDistance = distance;
                }
            }
            
            if (Math.Abs(attackForce) > 0.0f)
            {
                closestLimb.body.ApplyForce((closestLimb.WorldPosition - worldPosition) * attackForce);
            }

            AttackResult attackResult = closestLimb.AddDamage(worldPosition, damageType, amount, bleedingAmount, playSound);

            AddDamage(damageType == DamageType.Burn ? CauseOfDeath.Burn : causeOfDeath, attackResult.Damage, null);
                        
            //health -= attackResult.Damage;
            //if (health <= 0.0f && damageType == DamageType.Burn) Kill(CauseOfDeath.Burn);
            if (DoesBleed)
            {
                Bleeding += attackResult.Bleeding;
            }
            
            return attackResult;
        }

        public void StartStun(float stunTimer, bool allowStunDecrease = false)
        {
            if ((stunTimer <= AnimController.StunTimer && !allowStunDecrease) || !MathUtils.IsValid(stunTimer)) return;

            if (GameMain.Server != null &&
                (Math.Sign(stunTimer) != Math.Sign(AnimController.StunTimer) || Math.Abs(stunTimer - AnimController.StunTimer) > 0.1f))
            {
                GameMain.Server.CreateEntityEvent(this, new object[] { NetEntityEvent.Type.Status });
            }

            if (Math.Sign(stunTimer) != Math.Sign(AnimController.StunTimer)) AnimController.ResetPullJoints();
            AnimController.StunTimer = Math.Max(AnimController.StunTimer, stunTimer);
                
            selectedConstruction = null;
        }

        private void Implode(bool isNetworkMessage = false)
        {
            if (!isNetworkMessage)
            {
                if (GameMain.Client != null) return; 
            }
            
            Health = minHealth;

            BreakJoints();

            Kill(CauseOfDeath.Pressure, isNetworkMessage);
        }

        public void BreakJoints()
        {
            Vector2 centerOfMass = AnimController.GetCenterOfMass();

            foreach (Limb limb in AnimController.Limbs)
            {
                limb.AddDamage(limb.SimPosition, DamageType.Blunt, 500.0f, 0.0f, false);

                Vector2 diff = centerOfMass - limb.SimPosition;
                if (diff == Vector2.Zero) continue;
                limb.body.ApplyLinearImpulse(diff * 10.0f);
                // limb.Damage = 100.0f;
            }

            SoundPlayer.PlaySound("implode", 1.0f, 150.0f, WorldPosition);

            for (int i = 0; i < 10; i++)
            {
                Particle p = GameMain.ParticleManager.CreateParticle("waterblood",
                    ConvertUnits.ToDisplayUnits(centerOfMass) + Rand.Vector(5.0f),
                    Rand.Vector(10.0f));
                if (p != null) p.Size *= 2.0f;

                GameMain.ParticleManager.CreateParticle("bubbles",
                    ConvertUnits.ToDisplayUnits(centerOfMass) + Rand.Vector(5.0f),
                    new Vector2(Rand.Range(-50f, 50f), Rand.Range(-100f, 50f)));
            }

            foreach (var joint in AnimController.limbJoints)
            {
                joint.LimitEnabled = false;
            }
        }
        
        public void Kill(CauseOfDeath causeOfDeath, bool isNetworkMessage = false)
        {
            if (isDead) return;

            //clients aren't allowed to kill characters unless they receive a network message
            if (!isNetworkMessage && GameMain.Client != null)
            {
                return;
            }

            if (GameMain.NetworkMember != null)
            {
                if (Character.controlled == this)
                {
                    string chatMessage = InfoTextManager.GetInfoText("Self_CauseOfDeath." + causeOfDeath.ToString());
                    if (GameMain.Client!=null) chatMessage += " Your chat messages will only be visible to other dead players.";

                    GameMain.NetworkMember.AddChatMessage(chatMessage, ChatMessageType.Dead);
                    GameMain.LightManager.LosEnabled = false;
                    controlled = null;
                }

                if (GameMain.Server != null)
                    GameMain.Server.CreateEntityEvent(this, new object[] { NetEntityEvent.Type.Status });
            }

            AnimController.Frozen = false;

            GameServer.Log(Name+" has died (Cause of death: "+causeOfDeath+")", Color.Red);

            if (OnDeath != null) OnDeath(this, causeOfDeath);
            
            PlaySound(CharacterSound.SoundType.Die);
            
            isDead = true;
            
            this.causeOfDeath = causeOfDeath;
            AnimController.movement = Vector2.Zero;
            AnimController.TargetMovement = Vector2.Zero;

            for (int i = 0; i < selectedItems.Length; i++ )
            {
                if (selectedItems[i] != null) selectedItems[i].Drop(this);            
            }

            if (aiTarget != null)
            {
                aiTarget.Remove();
                aiTarget = null;
            }

            foreach (Limb limb in AnimController.Limbs)
            {
                if (limb.pullJoint == null) continue;
                limb.pullJoint.Enabled = false;
            }

            foreach (RevoluteJoint joint in AnimController.limbJoints)
            {
                joint.MotorEnabled = false;
            }

            if (GameMain.GameSession != null)
            {
                GameMain.GameSession.KillCharacter(this);
            }
        }

        public void Revive(bool isNetworkMessage)
        {
            isDead = false;

            aiTarget = new AITarget(this);

            Health = Math.Max(maxHealth * 0.1f, health);

            foreach (RevoluteJoint joint in AnimController.limbJoints)
            {
                joint.MotorEnabled = true;
            }

            if (GameMain.GameSession != null)
            {
                GameMain.GameSession.ReviveCharacter(this);
            }
        }
        
        public override void Remove()
        {
            base.Remove();

            if (info != null) info.Remove();

            CharacterList.Remove(this);

            if (controlled == this) controlled = null;

            if (GameMain.Client != null && GameMain.Client.Character == this) GameMain.Client.Character = null;

            if (aiTarget != null) aiTarget.Remove();

            if (AnimController != null) AnimController.Remove();

            if (Lights.LightManager.ViewTarget == this) Lights.LightManager.ViewTarget = null;
        }

        public virtual void ClientWrite(NetBuffer msg, object[] extraData = null) 
        {
            if (GameMain.Server != null) return;

            if (extraData != null && (NetEntityEvent.Type)extraData[0] == NetEntityEvent.Type.InventoryState)
            {
                inventory.ClientWrite(msg, extraData);
            }
            else
            {
                msg.Write((byte)ClientNetObject.CHARACTER_INPUT);

                if (memInput.Count > 60)
                {
                    memInput.RemoveRange(60,memInput.Count - 60);
                }

                msg.Write(LastNetworkUpdateID);
                byte inputCount = Math.Min((byte)memInput.Count, (byte)60);
                msg.Write(inputCount);
                for (int i = 0; i < inputCount; i++)
                {
                    msg.WriteRangedInteger(0, (int)InputNetFlags.MaxVal,(int)memInput[i].states);
                    if (memInput[i].states.HasFlag(InputNetFlags.Aim))
                    {
                        msg.Write(memInput[i].intAim);
                    }
                    if (memInput[i].states.HasFlag(InputNetFlags.Select))
                    {
                        msg.Write(memInput[i].interact);
                    }
                    /*if (memInput[i].HasFlag(InputNetFlags.Select) || memInput[i].HasFlag(InputNetFlags.Aim))
                    {
                        msg.Write(memMousePos[i].X);
                        msg.Write(memMousePos[i].Y);
                    }*/
                }
            }
        }
        public virtual void ServerRead(ClientNetObject type, NetIncomingMessage msg, Client c) 
        {
            if (GameMain.Server == null) return;

            if (c.Character != this)
            {
#if DEBUG
                DebugConsole.Log("Received a character update message from a client who's not controlling the character");
#endif
                return;
            }

            switch (type)
            {
                case ClientNetObject.CHARACTER_INPUT:
                    
                    UInt16 networkUpdateID  = msg.ReadUInt16();
                    byte inputCount         = msg.ReadByte();
                                            
                    for (int i = 0; i < inputCount; i++)
                    {
                        InputNetFlags newInput = (InputNetFlags)msg.ReadRangedInteger(0, (int)InputNetFlags.MaxVal);
                        //Vector2 newMousePos = Position;
                        UInt16 newAim = 0;
                        UInt16 newInteract = 0;

                        if (newInput.HasFlag(InputNetFlags.Aim))
                        {
                            newAim = msg.ReadUInt16();
                        }
                        if (newInput.HasFlag(InputNetFlags.Select))
                        {
                            newInteract = msg.ReadUInt16();
                        }

                        if (AllowInput)
                        {
                            /*if (newInput.HasFlag(InputNetFlags.Select) || newInput.HasFlag(InputNetFlags.Aim))
                            {
                                newMousePos.X = msg.ReadSingle();
                                newMousePos.Y = msg.ReadSingle();
                            }*/



                            if (NetIdUtils.IdMoreRecent((ushort)(networkUpdateID - i), LastNetworkUpdateID) && (i < 60))
                            {
                                NetInputMem newMem = new NetInputMem();
                                newMem.states = newInput;
                                newMem.intAim = newAim;
                                newMem.interact = newInteract;
                                
                                memInput.Insert(i, newMem);
                            }
                        }
                    }
            
                    if (NetIdUtils.IdMoreRecent(networkUpdateID, LastNetworkUpdateID))
                    {
                        LastNetworkUpdateID = networkUpdateID;
                    }
                    if (memInput.Count > 60)
                    {
                        //deleting inputs from the queue here means the server is way behind and data needs to be dropped
                        //we'll make the server drop down to 30 inputs for good measure
                        memInput.RemoveRange(30, memInput.Count - 30);
                    }
                    break;

                case ClientNetObject.ENTITY_STATE:
                    inventory.ServerRead(type, msg, c);
                    break;
            }
        }

        public virtual void ServerWrite(NetBuffer msg, Client c, object[] extraData = null) 
        {
            if (GameMain.Server == null) return;
            
            if (extraData != null)
            {
                switch ((NetEntityEvent.Type)extraData[0])
                {
                    case NetEntityEvent.Type.InventoryState:
                        msg.Write(true);
                        inventory.ClientWrite(msg, extraData);
                        break;
                    case NetEntityEvent.Type.Status:
                        msg.Write(false);
                        WriteStatus(msg);
                        break;
                }
            }
            else
            {
                msg.Write(ID);

                NetBuffer tempBuffer = new NetBuffer();

                if (this == c.Character)
                {
                    tempBuffer.Write(true);
                    if (LastNetworkUpdateID < memInput.Count + 1)
                    {
                        tempBuffer.Write((UInt16)0);
                    }
                    else
                    {
                        tempBuffer.Write((UInt16)(LastNetworkUpdateID - memInput.Count - 1));
                    }
                }
                else
                {
                    tempBuffer.Write(false);

                    bool aiming = false;
                    bool use    = false;
                    bool attack = false;

                    if (IsRemotePlayer)
                    {
                        aiming  = dequeuedInput.HasFlag(InputNetFlags.Aim);
                        use     = dequeuedInput.HasFlag(InputNetFlags.Use);

                        attack  = dequeuedInput.HasFlag(InputNetFlags.Attack);
                    }
                    else
                    {
                        aiming  = keys[(int)InputType.Aim].GetHeldQueue;
                        use     = keys[(int)InputType.Use].GetHeldQueue;

                        attack  = keys[(int)InputType.Attack].GetHeldQueue;

                        networkUpdateSent = true;
                    }

                    tempBuffer.Write(aiming);
                    tempBuffer.Write(use);

                    if (AnimController.Limbs.Any(l => l != null && l.attack != null))
                    {
                        tempBuffer.Write(attack);
                    }

                    if (selectedCharacter != null || selectedConstruction != null)
                    {
                        tempBuffer.Write(true);
                        tempBuffer.Write(selectedCharacter != null ? selectedCharacter.ID : selectedConstruction.ID);
                    }
                    else
                    {
                        tempBuffer.Write(false);
                    }

                    if (aiming)
                    {
                        Vector2 relativeCursorPos = cursorPosition - (ViewTarget == null ? AnimController.Collider.Position : ViewTarget.Position);
                        tempBuffer.Write((UInt16)(65535.0 * Math.Atan2(relativeCursorPos.Y, relativeCursorPos.X) / (2.0 * Math.PI)));
                    }

                    tempBuffer.Write(AnimController.TargetDir == Direction.Right);
                }

                tempBuffer.Write(SimPosition.X);
                tempBuffer.Write(SimPosition.Y);

                msg.Write((byte)tempBuffer.LengthBytes);
                msg.Write(tempBuffer);
            }
        }

        public virtual void ClientRead(ServerNetObject type, NetIncomingMessage msg, float sendingTime) 
        {
            if (GameMain.Server != null) return;

            switch (type)
            {
                case ServerNetObject.ENTITY_POSITION:
                    bool facingRight = AnimController.Dir > 0.0f;

                    lastRecvPositionUpdateTime = (float)NetTime.Now;

                    AnimController.Frozen = false;
                    Enabled = true;

                    UInt16 networkUpdateID = 0;
                    if (msg.ReadBoolean())
                    {
                        networkUpdateID = msg.ReadUInt16();
                    }
                    else
                    {
                        bool aimInput = msg.ReadBoolean();                        
                        keys[(int)InputType.Aim].Held = aimInput;
                        keys[(int)InputType.Aim].SetState(false, aimInput);

                        bool useInput = msg.ReadBoolean();
                        keys[(int)InputType.Use].Held = useInput;
                        keys[(int)InputType.Use].SetState(false, useInput);

                        if (AnimController.Limbs.Any(l => l != null && l.attack != null))
                        {
                            bool attackInput = msg.ReadBoolean();
                            keys[(int)InputType.Attack].Held = attackInput;
                            keys[(int)InputType.Attack].SetState(false, attackInput);
                        }

                        bool entitySelected = msg.ReadBoolean();
                        if (entitySelected)
                        {
                            ushort entityID = msg.ReadUInt16();
                            Entity selectedEntity = Entity.FindEntityByID(entityID);
                            if (selectedEntity is Character)
                            {
                                SelectCharacter((Character)selectedEntity);
                            }
                            else if (selectedEntity is Item)
                            {
                                var newSelectedConstruction = (Item)selectedEntity;
                                if (newSelectedConstruction != null && selectedConstruction != newSelectedConstruction)
                                {
                                    newSelectedConstruction.Pick(this, true, true);
                                }
                            }
                        }
                        else
                        {
                            if (selectedCharacter != null) DeselectCharacter();
                            selectedConstruction = null;
                        }
                                                
                        if (aimInput)
                        {
                            double aimAngle = ((double)msg.ReadUInt16() / 65535.0) * 2.0 * Math.PI;
                            cursorPosition = (ViewTarget == null ? AnimController.Collider.Position : ViewTarget.Position)
                                + new Vector2((float)Math.Cos(aimAngle), (float)Math.Sin(aimAngle)) * 60.0f;
                            
                            TransformCursorPos();
                        }
                        facingRight = msg.ReadBoolean();
                    }

                    Vector2 pos = new Vector2(
                        msg.ReadFloat(), 
                        msg.ReadFloat());
                        
                    var posInfo = new PosInfo(pos, facingRight ? Direction.Right : Direction.Left, networkUpdateID, sendingTime);

                    int index = 0;
                    if (GameMain.NetworkMember.Character == this && AllowInput)
                    {
                        while (index < memPos.Count && NetIdUtils.IdMoreRecent(posInfo.ID, memPos[index].ID)) 
                            index++;                        
                    }
                    else
                    {
                        while (index < memPos.Count && posInfo.Timestamp > memPos[index].Timestamp) 
                            index++;                        
                    }

                    memPos.Insert(index, posInfo);
                    break;
                case ServerNetObject.ENTITY_STATE:
                    bool isInventoryUpdate = msg.ReadBoolean();

                    if (isInventoryUpdate)
                    {
                        inventory.ClientRead(type, msg, sendingTime);
                    }
                    else
                    {
                        ReadStatus(msg);
                    }

                    break;
            }
        }

        private void WriteStatus(NetBuffer msg)
        {
            if (GameMain.Client != null)
            {
                DebugConsole.ThrowError("Client attempted to write character status to a networked message");
                return;
            }

            msg.Write(isDead);
            if (isDead)
            {
                msg.Write((byte)causeOfDeath);
            }
            else
            {
                msg.WriteRangedSingle(health, minHealth, maxHealth, 8);

                msg.Write(oxygen < 100.0f);
                if (oxygen < 100.0f)
                {
                    msg.WriteRangedSingle(oxygen, -100.0f, 100.0f, 8);
                }

                msg.Write(bleeding > 0.0f);
                if (bleeding > 0.0f)
                {
                    msg.WriteRangedSingle(bleeding, 0.0f, 5.0f, 8);
                }

                msg.Write(Stun > 0.0f);
                if (Stun > 0.0f)
                {
                    Stun = MathHelper.Clamp(Stun, 0.0f, 60.0f);
                    msg.WriteRangedSingle(Stun, 0.0f, 60.0f, 8);
                }
            }
        }

        private void ReadStatus(NetBuffer msg)
        {
            if (GameMain.Server != null)
            {
                DebugConsole.ThrowError("Server attempted to read character status from a networked message");
                return;
            }

            bool isDead = msg.ReadBoolean();
            if (isDead)
            {
                causeOfDeath = (CauseOfDeath)msg.ReadByte();
                if (causeOfDeath == CauseOfDeath.Pressure)
                {
                    Implode(true);
                }
                else
                {
                    Kill(causeOfDeath, true);
                }
            }
            else
            {
                health = msg.ReadRangedSingle(minHealth, maxHealth, 8);

                bool lowOxygen = msg.ReadBoolean();
                if (lowOxygen)
                {
                    Oxygen = msg.ReadRangedSingle(-100.0f, 100.0f, 8);
                }
                else
                {
                    Oxygen = 100.0f;
                }

                bool isBleeding = msg.ReadBoolean();
                if (isBleeding)
                {
                    bleeding = msg.ReadRangedSingle(0.0f, 5.0f, 8);
                }
                else
                {
                    bleeding = 0.0f;
                }

                bool stunned = msg.ReadBoolean();
                if (stunned)
                {
                    float newStunTimer = msg.ReadRangedSingle(0.0f, 60.0f, 8);
                    StartStun(newStunTimer, true);
                }
                else
                {
                    StartStun(0.0f, true);
                }
            }
        }

        public void WriteSpawnData(NetBuffer msg)
        {
            if (GameMain.Server == null) return;

            msg.Write(Info == null);
            msg.Write(ID);
            msg.Write(ConfigPath);

            msg.Write(WorldPosition.X);
            msg.Write(WorldPosition.Y);

            msg.Write(Enabled);

            //character with no characterinfo (e.g. some monster)
            if (Info == null) return;
            
            Client ownerClient = GameMain.Server.ConnectedClients.Find(c => c.Character == this);
            if (ownerClient != null)
            {
                msg.Write(true);
                msg.Write(ownerClient.ID);
            }
            else if (GameMain.Server.Character == this)
            {
                msg.Write(true);
                msg.Write((byte)0);
            }
            else
            {
                msg.Write(false);
            }
            
            msg.Write(Info.Name);
            msg.Write(TeamID);

            msg.Write(this is AICharacter);
            msg.Write(Info.Gender == Gender.Female);
            msg.Write((byte)Info.HeadSpriteId);
            msg.Write(Info.Job == null ? "" : Info.Job.Name);            
        }

        public static Character ReadSpawnData(NetIncomingMessage inc, bool spawn=true)
        {
            if (GameMain.Server != null) return null;

            bool noInfo         = inc.ReadBoolean();
            ushort id           = inc.ReadUInt16();
            string configPath   = inc.ReadString();

            Vector2 position    = new Vector2(inc.ReadFloat(), inc.ReadFloat());

            bool enabled        = inc.ReadBoolean();

            DebugConsole.Log("Received spawn data for "+configPath);

            Character character = null;
            if (noInfo)
            {
                if (!spawn) return null;

                character = Character.Create(configPath, position, null, true);
                character.ID = id;
            }
            else
            {
                bool hasOwner       = inc.ReadBoolean();
                int ownerId         = hasOwner ? inc.ReadByte() : -1;
                

                string newName      = inc.ReadString();
                byte teamID         = inc.ReadByte();

                bool hasAi          = inc.ReadBoolean();
                bool isFemale       = inc.ReadBoolean();
                int headSpriteID    = inc.ReadByte();
                string jobName      = inc.ReadString();

                if (!spawn) return null;

                JobPrefab jobPrefab = JobPrefab.List.Find(jp => jp.Name == jobName);

                CharacterInfo ch = new CharacterInfo(configPath, newName, isFemale ? Gender.Female : Gender.Male, jobPrefab);
                ch.HeadSpriteId = headSpriteID;

                character = Character.Create(configPath, position, ch, GameMain.Client.ID != ownerId, hasAi);
                character.ID = id;
                character.TeamID = teamID;

                if (GameMain.Client.ID == ownerId)
                {
                    GameMain.Client.Character = character;
                    Controlled = character;

                    GameMain.LightManager.LosEnabled = true;

                    character.memInput.Clear();
                    character.memPos.Clear();
                    character.memLocalPos.Clear();
                }
                else
                {
                    var ownerClient = GameMain.Client.ConnectedClients.Find(c => c.ID == ownerId);
                    if (ownerClient != null)
                    {
                        ownerClient.Character = character;
                    }
                }

                if (configPath == Character.HumanConfigFile)
                {
                    GameMain.GameSession.CrewManager.characters.Add(character);
                }
            }

            character.Enabled = enabled;

            return character;
        }
    }
}

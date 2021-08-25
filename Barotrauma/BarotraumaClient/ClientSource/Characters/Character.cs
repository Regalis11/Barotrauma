using Barotrauma.Items.Components;
using Barotrauma.Networking;
using Barotrauma.Particles;
using Barotrauma.Sounds;
using FarseerPhysics;
using FarseerPhysics.Dynamics;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Xml.Linq;
using Barotrauma.Extensions;

namespace Barotrauma
{
    partial class Character
    {
        public static bool DisableControls;

        public static bool DebugDrawInteract;

        protected float soundTimer;
        protected float soundInterval;
        protected float hudInfoTimer = 1.0f;
        protected bool hudInfoVisible = false;

        private float pressureParticleTimer;

        private float findFocusedTimer;

        protected float lastRecvPositionUpdateTime;

        private float hudInfoHeight = 100.0f;

        private List<CharacterSound> sounds;
        
        public bool ExternalHighlight;

        /// <summary>
        /// Is the character currently visible on the camera. Refresh the value by calling DoVisibilityCheck.
        /// </summary>
        public bool IsVisible
        {
            get;
            private set;
        } = true;

        //the Character that the player is currently controlling
        private static Character controlled;
        
        public static Character Controlled
        {
            get { return controlled; }
            set
            {
                if (controlled == value) return;
                if ((!(controlled is null)) && (!(Screen.Selected?.Cam is null)) && value is null)
                {
                    Screen.Selected.Cam.TargetPos = Vector2.Zero;
                    Lights.LightManager.ViewTarget = null;
                }
                controlled = value;
                if (controlled != null) controlled.Enabled = true;
                CharacterHealth.OpenHealthWindow = null;                
            }
        }

        private Dictionary<object, HUDProgressBar> hudProgressBars;
        private readonly List<KeyValuePair<object, HUDProgressBar>> progressBarRemovals = new List<KeyValuePair<object, HUDProgressBar>>();

        public Dictionary<object, HUDProgressBar> HUDProgressBars
        {
            get { return hudProgressBars; }
        }

        private float blurStrength;
        public float BlurStrength
        {
            get { return blurStrength; }
            set { blurStrength = MathHelper.Clamp(value, 0.0f, 1.0f); }
        }

        private float distortStrength;
        public float DistortStrength
        {
            get { return distortStrength; }
            set { distortStrength = MathHelper.Clamp(value, 0.0f, 1.0f); }
        }

        private float radialDistortStrength;
        public float RadialDistortStrength
        {
            get { return radialDistortStrength; }
            set { radialDistortStrength = MathHelper.Clamp(value, 0.0f, 100.0f); }
        }

        private float chromaticAberrationStrength;
        public float ChromaticAberrationStrength
        {
            get { return chromaticAberrationStrength; }
            set { chromaticAberrationStrength = MathHelper.Clamp(value, 0.0f, 100.0f); }
        }
        
        public Color GrainColor { get; set; }
        
        private float grainStrength;
        public float GrainStrength
        {
            get => grainStrength;
            set => grainStrength = Math.Max(0, value);
        }

        private readonly List<ParticleEmitter> bloodEmitters = new List<ParticleEmitter>();
        public IEnumerable<ParticleEmitter> BloodEmitters
        {
            get { return bloodEmitters; }
        }

        private readonly List<ParticleEmitter> damageEmitters = new List<ParticleEmitter>();
        public IEnumerable<ParticleEmitter> DamageEmitters
        {
            get { return damageEmitters; }
        }

        private readonly List<ParticleEmitter> gibEmitters = new List<ParticleEmitter>();
        public IEnumerable<ParticleEmitter> GibEmitters
        {
            get { return gibEmitters; }
        }

        public static bool IsMouseOnUI => GUI.MouseOn != null ||
                    (CharacterInventory.IsMouseOnInventory() && !CharacterInventory.DraggingItemToWorld);

        public class ObjectiveEntity
        {
            public Entity Entity;
            public Sprite Sprite;
            public Color Color;

            public ObjectiveEntity(Entity entity, Sprite sprite, Color? color = null)
            {
                Entity = entity;
                Sprite = sprite;
                if (color.HasValue)
                {
                    Color = color.Value;
                }
                else
                {
                    Color = Color.White;
                }
            }
        }

        private readonly List<ObjectiveEntity> activeObjectiveEntities = new List<ObjectiveEntity>();
        public IEnumerable<ObjectiveEntity> ActiveObjectiveEntities
        {
            get { return activeObjectiveEntities; }
        }

        partial void InitProjSpecific(XElement mainElement)
        {
            soundInterval = mainElement.GetAttributeFloat("soundinterval", 10.0f);
            soundTimer = Rand.Range(0.0f, soundInterval);

            sounds = new List<CharacterSound>();
            Params.Sounds.ForEach(s => sounds.Add(new CharacterSound(s)));

            foreach (XElement subElement in mainElement.Elements())
            {
                switch (subElement.Name.ToString().ToLowerInvariant())
                {
                    case "damageemitter":
                        damageEmitters.Add(new ParticleEmitter(subElement));
                        break;
                    case "bloodemitter":
                        bloodEmitters.Add(new ParticleEmitter(subElement));
                        break;
                    case "gibemitter":
                        gibEmitters.Add(new ParticleEmitter(subElement));
                        break;
                }
            }

            hudProgressBars = new Dictionary<object, HUDProgressBar>();
        }

        partial void UpdateLimbLightSource(Limb limb)
        {
            if (limb.LightSource != null)
            {
                limb.LightSource.Enabled = enabled;
            }
        }

        private bool wasFiring;

        /// <summary>
        /// Control the Character according to player input
        /// </summary>
        public void ControlLocalPlayer(float deltaTime, Camera cam, bool moveCam = true)
        {
            if (DisableControls || GUI.InputBlockingMenuOpen)
            {
                foreach (Key key in keys)
                {
                    if (key == null) { continue; }
                    key.Reset();
                }
                if (GUI.InputBlockingMenuOpen)
                {
                    cursorPosition = 
                        Position + PlayerInput.MouseSpeed.ClampLength(10.0f); //apply a little bit of movement to the cursor pos to prevent AFK kicking
                }
            }
            else
            {
                wasFiring |= keys[(int)InputType.Aim].Held && keys[(int)InputType.Shoot].Held;
                for (int i = 0; i < keys.Length; i++)
                {
                    keys[i].SetState();
                }
                //if we were firing (= pressing the aim and shoot keys at the same time)
                //and the fire key is the same as Select or Use, reset the key to prevent accidentally selecting/using items
                if (wasFiring && !keys[(int)InputType.Shoot].Held)
                {
                    if (GameMain.Config.KeyBind(InputType.Shoot).Equals(GameMain.Config.KeyBind(InputType.Select)))
                    {
                        keys[(int)InputType.Select].Reset();
                    }
                    if (GameMain.Config.KeyBind(InputType.Shoot).Equals(GameMain.Config.KeyBind(InputType.Use)))
                    {
                        keys[(int)InputType.Use].Reset();
                    }
                    wasFiring = false;
                }

                float targetOffsetAmount = 0.0f;
                if (moveCam)
                {
                    if (NeedsAir && !IsProtectedFromPressure() &&
                        (AnimController.CurrentHull == null || AnimController.CurrentHull.LethalPressure > 0.0f))
                    {
                        float pressure = AnimController.CurrentHull == null ? 100.0f : AnimController.CurrentHull.LethalPressure;
                        if (pressure > 0.0f)
                        {
                            float zoomInEffectStrength = MathHelper.Clamp(pressure / 100.0f, 0.1f, 1.0f);
                            cam.Zoom = MathHelper.Lerp(cam.Zoom,
                                cam.DefaultZoom + (Math.Max(pressure, 10) / 150.0f) * Rand.Range(0.9f, 1.1f),
                                zoomInEffectStrength);

                            pressureParticleTimer += pressure * deltaTime;
                            if (pressureParticleTimer > 10.0f)
                            {
                                GameMain.ParticleManager.CreateParticle(Params.BleedParticleWater, WorldPosition + Rand.Vector(5.0f), Rand.Vector(10.0f));
                                pressureParticleTimer = 0.0f;
                            }
                        }
                    }

                    if (IsHumanoid)
                    {
                        cam.OffsetAmount = 250.0f;// MathHelper.Lerp(cam.OffsetAmount, 250.0f, deltaTime);
                    }
                    else
                    {
                        // Visibility for monsters depends on character's sightrange but will always be slightly more than humans
                        if (AIController is EnemyAIController enemyAI) 
                        { 
                            cam.OffsetAmount = MathHelper.Clamp(enemyAI.GetSightRange() * 350f, 300.0f, 2000.0f);
                        }
                    }
                }

                cursorPosition = cam.ScreenToWorld(PlayerInput.MousePosition);
                if (AnimController.CurrentHull?.Submarine != null)
                {
                    cursorPosition -= AnimController.CurrentHull.Submarine.Position;
                }

                Vector2 mouseSimPos = ConvertUnits.ToSimUnits(cursorPosition);
                if (GUI.PauseMenuOpen)
                {
                    cam.OffsetAmount = targetOffsetAmount = 0.0f;
                }
                else if (Lights.LightManager.ViewTarget is Item item && item.Prefab.FocusOnSelected)
                {
                    cam.OffsetAmount = targetOffsetAmount = item.Prefab.OffsetOnSelected * item.OffsetOnSelectedMultiplier;
                }
                else if (SelectedConstruction != null && ViewTarget == null &&
                    SelectedConstruction.Components.Any(ic => ic?.GuiFrame != null && ic.ShouldDrawHUD(this)))
                {
                    cam.OffsetAmount = targetOffsetAmount = 0.0f;
                    cursorPosition =
                        SelectedConstruction.Position +
                        PlayerInput.MouseSpeed.ClampLength(10.0f); //apply a little bit of movement to the cursor pos to prevent AFK kicking
                }
                else if (!GameMain.Config.EnableMouseLook)
                {
                    cam.OffsetAmount = targetOffsetAmount = 0.0f;
                }
                else if (Lights.LightManager.ViewTarget == this)
                {
                    if (GUI.PauseMenuOpen || IsUnconscious)
                    {
                        if (deltaTime > 0.0f)
                        {
                            cam.OffsetAmount = targetOffsetAmount = 0.0f;
                        }
                    }
                    else if (IsMouseOnUI)
                    {
                        targetOffsetAmount = cam.OffsetAmount;
                    }
                    else if (Vector2.DistanceSquared(AnimController.Limbs[0].SimPosition, mouseSimPos) > 1.0f)
                    {
                        Body body = Submarine.CheckVisibility(AnimController.Limbs[0].SimPosition, mouseSimPos);
                        Structure structure = body?.UserData as Structure;

                        float sightDist = Submarine.LastPickedFraction;
                        if (body?.UserData is Structure && !((Structure)body.UserData).CastShadow)
                        {
                            sightDist = 1.0f;
                        }
                        targetOffsetAmount = Math.Max(250.0f, sightDist * 500.0f);
                    }
                }

                cam.OffsetAmount = MathHelper.Lerp(cam.OffsetAmount, targetOffsetAmount, 0.05f);
                DoInteractionUpdate(deltaTime, mouseSimPos);
            }

            if (!GUI.InputBlockingMenuOpen)
            {
                if (SelectedConstruction != null &&
                    (SelectedConstruction.ActiveHUDs.Any(ic => ic.GuiFrame != null && HUD.CloseHUD(ic.GuiFrame.Rect)) ||
                    ((ViewTarget as Item)?.Prefab.FocusOnSelected ?? false) && PlayerInput.KeyHit(Microsoft.Xna.Framework.Input.Keys.Escape)))
                {
                    if (GameMain.Client != null)
                    {
                        //emulate a Select input to get the character to deselect the item server-side
                        //keys[(int)InputType.Select].Hit = true;
                        keys[(int)InputType.Deselect].Hit = true;
                    }
                    //reset focus to prevent us from accidentally interacting with another entity
                    focusedItem = null;
                    FocusedCharacter = null;
                    findFocusedTimer = 0.2f;
                    SelectedConstruction = null;
                }
            }

            DisableControls = false;
        }

        partial void UpdateControlled(float deltaTime, Camera cam)
        {
            if (controlled != this) return;
            
            ControlLocalPlayer(deltaTime, cam);

            Lights.LightManager.ViewTarget = this;
            CharacterHUD.Update(deltaTime, this, cam);
            
            if (hudProgressBars.Any())
            {
                foreach (var progressBar in hudProgressBars)
                {
                    if (progressBar.Value.FadeTimer <= 0.0f)
                    {
                        progressBarRemovals.Add(progressBar);
                        continue;
                    }
                    progressBar.Value.Update(deltaTime);
                }
                if (progressBarRemovals.Any())
                {
                    progressBarRemovals.ForEach(pb => hudProgressBars.Remove(pb.Key));
                    progressBarRemovals.Clear();
                }
            }
        }
        
        partial void OnAttackedProjSpecific(Character attacker, AttackResult attackResult, float stun)
        {
            if (IsDead) { return; }
            if (attacker != null)
            {
                if (attackResult.Damage <= 0.01f) { return; }
            }
            else
            {
                if (attackResult.Damage <= 1.0f) { return; }
            }

            if (soundTimer < soundInterval * 0.5f)
            {
                PlaySound(CharacterSound.SoundType.Damage);
                soundTimer = soundInterval;
            }
        }

        partial void KillProjSpecific(CauseOfDeathType causeOfDeath, Affliction causeOfDeathAffliction, bool log)
        {
            HintManager.OnCharacterKilled(this);

            if (GameMain.NetworkMember != null && controlled == this)
            {
                string chatMessage = CauseOfDeath.Type == CauseOfDeathType.Affliction ?
                    CauseOfDeath.Affliction.SelfCauseOfDeathDescription :
                    TextManager.Get("Self_CauseOfDeathDescription." + CauseOfDeath.Type.ToString(), fallBackTag: "Self_CauseOfDeathDescription.Damage");

                if (GameMain.Client != null) { chatMessage += " " + TextManager.Get("DeathChatNotification"); }

                if (GameMain.NetworkMember.RespawnManager?.UseRespawnPrompt ?? false)
                {
                    CoroutineManager.InvokeAfter(() =>
                    {
                        if (controlled != null || (!(GameMain.GameSession?.IsRunning ?? false))) { return; }
                        var respawnPrompt = new GUIMessageBox(
                            TextManager.Get("tutorial.tryagainheader"), TextManager.Get("respawnquestionprompt"),
                            new string[] { TextManager.Get("respawnquestionpromptrespawn"), TextManager.Get("respawnquestionpromptwait") });
                        respawnPrompt.Buttons[0].OnClicked += (btn, userdata) =>
                        {
                            GameMain.Client?.SendRespawnPromptResponse(waitForNextRoundRespawn: false);
                            respawnPrompt.Close();
                            return true;
                        };
                        respawnPrompt.Buttons[1].OnClicked += (btn, userdata) =>
                        {
                            GameMain.Client?.SendRespawnPromptResponse(waitForNextRoundRespawn: true);
                            respawnPrompt.Close();
                            return true;
                        };
                    }, delay: 5.0f);
                }

                GameMain.NetworkMember.AddChatMessage(chatMessage, ChatMessageType.Dead);
                GameMain.LightManager.LosEnabled = false;
                controlled = null;
                if (!(Screen.Selected?.Cam is null))
                {
                    Screen.Selected.Cam.TargetPos = Vector2.Zero;
                    Lights.LightManager.ViewTarget = null;
                }
            }

            PlaySound(CharacterSound.SoundType.Die);
        }

        partial void DisposeProjSpecific()
        {
            if (controlled == this)
            {
                controlled = null;
                if (!(Screen.Selected?.Cam is null))
                {
                    Screen.Selected.Cam.TargetPos = Vector2.Zero;
                    Lights.LightManager.ViewTarget = null;
                }
            }

            if (GameMain.GameSession?.CrewManager != null &&
                GameMain.GameSession.CrewManager.GetCharacters().Contains(this))
            {
                GameMain.GameSession.CrewManager.RemoveCharacter(this);
            }
            
            if (GameMain.Client?.Character == this) GameMain.Client.Character = null;

            if (Lights.LightManager.ViewTarget == this) Lights.LightManager.ViewTarget = null;
        }


        private List<Item> debugInteractablesInRange = new List<Item>();
        private List<Item> debugInteractablesAtCursor = new List<Item>();
        private List<Pair<Item, float>> debugInteractablesNearCursor = new List<Pair<Item, float>>();

        /// <summary>
        ///   Finds the front (lowest depth) interactable item at a position. "Interactable" in this case means that the character can "reach" the item.
        /// </summary>
        /// <param name="character">The Character who is looking for the interactable item, only items that are close enough to this character are returned</param>
        /// <param name="simPosition">The item at the simPosition, with the lowest depth, is returned</param>
        /// <param name="allowFindingNearestItem">If this is true and an item cannot be found at simPosition then a nearest item will be returned if possible</param>
        /// <param name="hull">If a hull is specified, only items within that hull are returned</param>
        public Item FindItemAtPosition(Vector2 simPosition, float aimAssistModifier = 0.0f, Item[] ignoredItems = null)
        {
            if (Submarine != null)
            {
                simPosition += Submarine.SimPosition;
            }

            debugInteractablesInRange.Clear();
            debugInteractablesAtCursor.Clear();
            debugInteractablesNearCursor.Clear();

            bool draggingItemToWorld = CharacterInventory.DraggingItemToWorld;

            //reduce the amount of aim assist if an item has been selected 
            //= can't switch selection to another item without deselecting the current one first UNLESS the cursor is directly on the item
            //otherwise it would be too easy to accidentally switch the selected item when rewiring items
            float aimAssistAmount = SelectedConstruction == null ? 100.0f * aimAssistModifier : 1.0f;

            Vector2 displayPosition = ConvertUnits.ToDisplayUnits(simPosition);

            //use the list of visible entities if it exists
            var entityList = Submarine.VisibleEntities ?? Item.ItemList;

            Item closestItem = null;
            float closestItemDistance = Math.Max(aimAssistAmount, 2.0f);
            foreach (MapEntity entity in entityList)
            {
                if (!(entity is Item item))
                {
                    continue;
                }
                if (item.body != null && !item.body.Enabled) { continue; }
                if (item.ParentInventory != null) { continue; }
                if (ignoredItems != null && ignoredItems.Contains(item)) { continue; }
                if (item.Prefab.RequireCampaignInteract && item.CampaignInteractionType == CampaignMode.InteractionType.None) { continue; }
                if (Screen.Selected is SubEditorScreen editor && editor.WiringMode && item.GetComponent<ConnectionPanel>() == null) { continue; }

                if (draggingItemToWorld)
                {
                    if (item.OwnInventory == null || 
                        !item.OwnInventory.Container.AllowDragAndDrop ||
                        !item.OwnInventory.CanBePut(CharacterInventory.DraggingItems.First()) ||
                        !CanAccessInventory(item.OwnInventory))
                    {
                        continue;
                    }
                }
                
                float distanceToItem = float.PositiveInfinity;
                if (item.IsInsideTrigger(displayPosition, out Rectangle transformedTrigger))
                {
                    debugInteractablesAtCursor.Add(item);
                    //distance is between 0-1 when the cursor is directly on the item
                    distanceToItem =
                        Math.Abs(transformedTrigger.Center.X - displayPosition.X) / transformedTrigger.Width +
                        Math.Abs((transformedTrigger.Y - transformedTrigger.Height / 2.0f) - displayPosition.Y) / transformedTrigger.Height;
                    //modify the distance based on the size of the trigger (preferring smaller items)
                    distanceToItem *= MathHelper.Lerp(0.05f, 2.0f, (transformedTrigger.Width + transformedTrigger.Height) / 250.0f);
                }
                else if (!item.Prefab.RequireCursorInsideTrigger)
                {
                    Rectangle itemDisplayRect = new Rectangle(item.InteractionRect.X, item.InteractionRect.Y - item.InteractionRect.Height, item.InteractionRect.Width, item.InteractionRect.Height);

                    if (itemDisplayRect.Contains(displayPosition))
                    {
                        debugInteractablesAtCursor.Add(item);
                        //distance is between 0-1 when the cursor is directly on the item
                        distanceToItem =
                            Math.Abs(itemDisplayRect.Center.X - displayPosition.X) / itemDisplayRect.Width +
                            Math.Abs(itemDisplayRect.Center.Y - displayPosition.Y) / itemDisplayRect.Height;
                        //modify the distance based on the size of the item (preferring smaller ones)
                        distanceToItem *= MathHelper.Lerp(0.05f, 2.0f, (itemDisplayRect.Width + itemDisplayRect.Height) / 250.0f);
                    }
                    else
                    {
                        if (closestItemDistance < 2.0f) { continue; }
                        //get the point on the itemDisplayRect which is closest to the cursor
                        Vector2 rectIntersectionPoint = new Vector2(
                            MathHelper.Clamp(displayPosition.X, itemDisplayRect.X, itemDisplayRect.Right),
                            MathHelper.Clamp(displayPosition.Y, itemDisplayRect.Y, itemDisplayRect.Bottom));
                        distanceToItem = 2.0f + Vector2.Distance(rectIntersectionPoint, displayPosition);
                    }
                }

                if (distanceToItem > closestItemDistance) { continue; }
                if (!CanInteractWith(item)) { continue; }
                
                debugInteractablesNearCursor.Add(new Pair<Item, float>(item, 1.0f - distanceToItem / (100.0f * aimAssistModifier)));
                closestItem = item;
                closestItemDistance = distanceToItem;
            }
            
            return closestItem;
        }

        private Character FindCharacterAtPosition(Vector2 mouseSimPos, float maxDist = 150.0f)
        {
            Character closestCharacter = null;
            float closestDist = 0.0f;

            maxDist = ConvertUnits.ToSimUnits(maxDist);

            foreach (Character c in CharacterList)
            {
                if (!CanInteractWith(c, checkVisibility: false) || (c.AnimController?.SimplePhysicsEnabled ?? true)) { continue; }

                float dist = Vector2.DistanceSquared(mouseSimPos, c.SimPosition);
                if (dist < maxDist * maxDist && (closestCharacter == null || dist < closestDist))
                {
                    closestCharacter = c;
                    closestDist = dist;
                }

                /*FarseerPhysics.Common.Transform transform;
                c.AnimController.Collider.FarseerBody.GetTransform(out transform);
                for (int i = 0; i < c.AnimController.Collider.FarseerBody.FixtureList.Count; i++)
                {
                    if (c.AnimController.Collider.FarseerBody.FixtureList[i].Shape.TestPoint(ref transform, ref mouseSimPos))
                    {
                        Console.WriteLine("Hit: " + i);
                        closestCharacter = c;
                    }
                }*/
            }

            return closestCharacter;
        }

        public bool ShouldLockHud()
        {
            if (this != controlled) { return false; }
            if (GameMain.GameSession?.Campaign != null && GameMain.GameSession.Campaign.ShowCampaignUI) { return true; }
            var controller = SelectedConstruction?.GetComponent<Controller>();
            //lock if using a controller, except if we're also using a connection panel in the same item
            return
                SelectedConstruction != null &&
                controller?.User == this && controller.HideHUD &&
                SelectedConstruction?.GetComponent<ConnectionPanel>()?.User != this;
        }


        partial void UpdateProjSpecific(float deltaTime, Camera cam)
        {
            if (InvisibleTimer > 0.0f)
            {
                if (Controlled == null || Controlled == this || (Controlled.CharacterHealth.GetAffliction("psychosis")?.Strength ?? 0.0f) <= 0.0f)
                {
                    InvisibleTimer = 0.0f;
                }
                else
                {
                    InvisibleTimer -= deltaTime;
                }
            }

            if (!enabled) { return; }

            if (!IsDead && !IsIncapacitated)
            {
                if (soundTimer > 0)
                {
                    soundTimer -= deltaTime;
                }
                else if (AIController is EnemyAIController enemyAI)
                {
                    switch (enemyAI.State)
                    {
                        case AIState.Attack:
                            PlaySound(CharacterSound.SoundType.Attack);
                            break;
                        default:
                            var petBehavior = enemyAI.PetBehavior;
                            if (petBehavior != null && petBehavior.Happiness < petBehavior.MaxHappiness * 0.25f)
                            {
                                PlaySound(CharacterSound.SoundType.Unhappy);
                            }
                            else
                            {
                                PlaySound(CharacterSound.SoundType.Idle);

                            }
                            break;
                    }
                }
            }

            if (info != null || Vitality < MaxVitality * 0.98f || IsPet)
            {
                hudInfoTimer -= deltaTime;
                if (hudInfoTimer <= 0.0f)
                {
                    if (controlled == null)
                    {
                        hudInfoVisible = true;
                    }

                    //if the character is not in the camera view, the name can't be visible and we can avoid the expensive visibility checks
                    else if (WorldPosition.X < cam.WorldView.X || WorldPosition.X > cam.WorldView.Right || 
                            WorldPosition.Y > cam.WorldView.Y || WorldPosition.Y < cam.WorldView.Y - cam.WorldView.Height)
                    {
                        hudInfoVisible = false;
                    }
                    else
                    {
                        //Ideally it shouldn't send the character entirely if we can't see them but /shrug, this isn't the most hacker-proof game atm
                        hudInfoVisible = controlled.CanSeeTarget(this, controlled.ViewTarget);
                    }
                    hudInfoTimer = Rand.Range(0.5f, 1.0f);
                }
            }

            CharacterHealth.UpdateClientSpecific(deltaTime);
            if (controlled == this)
            {
                CharacterHealth.UpdateHUD(deltaTime);
            }
        }

        partial void SetOrderProjSpecific(Order order, string orderOption, int priority)
        {
            GameMain.GameSession?.CrewManager?.AddCurrentOrderIcon(this, order, orderOption, priority);
        }

        public static void AddAllToGUIUpdateList()
        {
            for (int i = 0; i < CharacterList.Count; i++)
            {
                CharacterList[i].AddToGUIUpdateList();
            }
        }

        public virtual void AddToGUIUpdateList()
        {
            if (controlled == this)
            {
                CharacterHUD.AddToGUIUpdateList(this);
                CharacterHealth.AddToGUIUpdateList();
            }
        }

        public void DoVisibilityCheck(Camera cam)
        { 
            IsVisible = false;
            if (!Enabled || AnimController.SimplePhysicsEnabled) { return; }

            foreach (Limb limb in AnimController.Limbs)
            {
                float maxExtent = ConvertUnits.ToDisplayUnits(limb.body.GetMaxExtent());
                if (limb.LightSource != null) { maxExtent = Math.Max(limb.LightSource.Range, maxExtent); }
                if (limb.body.DrawPosition.X < cam.WorldView.X - maxExtent || limb.body.DrawPosition.X > cam.WorldView.Right + maxExtent) { continue; }
                if (limb.body.DrawPosition.Y < cam.WorldView.Y - cam.WorldView.Height - maxExtent || limb.body.DrawPosition.Y > cam.WorldView.Y + maxExtent) { continue; }
                IsVisible = true;
                return;
            }
        }
        
        public void Draw(SpriteBatch spriteBatch, Camera cam)
        {
            if (!Enabled || InvisibleTimer > 0.0f) { return; }
            AnimController.Draw(spriteBatch, cam);
        }

        public void DrawHUD(SpriteBatch spriteBatch, Camera cam, bool drawHealth = true)
        {
            CharacterHUD.Draw(spriteBatch, this, cam);
            if (drawHealth && !CharacterHUD.IsCampaignInterfaceOpen) { CharacterHealth.DrawHUD(spriteBatch); }
        }
        
        public virtual void DrawFront(SpriteBatch spriteBatch, Camera cam)
        {
            if (!Enabled || InvisibleTimer > 0.0f || (AnimController?.SimplePhysicsEnabled ?? true)) { return; }

            if (GameMain.DebugDraw)
            {
                AnimController.DebugDraw(spriteBatch);
            }

            if (GUI.DisableHUD) { return; }

            if (Controlled != null &&
                Controlled != this &&
                Submarine != null &&
                Controlled.Submarine == Submarine &&
                GameMain.Config.LosMode != LosMode.None)
            {
                float yPos = Controlled.AnimController.FloorY - 1.5f;

                if (Controlled.AnimController.Stairs != null)
                {
                    yPos = Controlled.AnimController.Stairs.SimPosition.Y - Controlled.AnimController.Stairs.RectHeight * 0.5f;
                }

                foreach (var ladder in Ladder.List)
                {
                    if (CanInteractWith(ladder.Item) && Controlled.CanInteractWith(ladder.Item))
                    {
                        float xPos = ladder.Item.SimPosition.X;
                        if (Math.Abs(xPos - SimPosition.X) < 3.0)
                        {
                            yPos = ladder.Item.SimPosition.Y - ladder.Item.RectHeight * 0.5f;
                        }
                        break;
                    }
                }
                if (AnimController.FloorY < yPos) { return; }
            }

            Vector2 pos = DrawPosition;
            pos.Y += hudInfoHeight;

            if (CurrentHull != null && DrawPosition.Y > CurrentHull.WorldRect.Y - 130.0f)
            {
                float lowerAmount = DrawPosition.Y - (CurrentHull.WorldRect.Y - 130.0f);
                hudInfoHeight = MathHelper.Lerp(hudInfoHeight, 100.0f - lowerAmount, 0.1f);
                hudInfoHeight = Math.Max(hudInfoHeight, 20.0f);
            }
            else
            {
                hudInfoHeight = MathHelper.Lerp(hudInfoHeight, 100.0f, 0.1f);
            }

            pos.Y = -pos.Y;

            if (speechBubbleTimer > 0.0f)
            {
                GUI.SpeechBubbleIcon.Draw(spriteBatch, pos - Vector2.UnitY * 5,
                    speechBubbleColor * Math.Min(speechBubbleTimer, 1.0f), 0.0f,
                    Math.Min(speechBubbleTimer, 1.0f));
            }

            if (this == controlled)
            {
                if (DebugDrawInteract)
                {
                    Vector2 cursorPos = cam.ScreenToWorld(PlayerInput.MousePosition);
                    cursorPos.Y = -cursorPos.Y;
                    foreach (Item item in debugInteractablesAtCursor)
                    {
                        GUI.DrawLine(spriteBatch, cursorPos,
                            new Vector2(item.DrawPosition.X, -item.DrawPosition.Y), Color.LightGreen, width: 4);
                    }
                    foreach (Item item in debugInteractablesInRange)
                    {
                        GUI.DrawLine(spriteBatch, new Vector2(DrawPosition.X, -DrawPosition.Y),
                            new Vector2(item.DrawPosition.X, -item.DrawPosition.Y), Color.White * 0.1f, width: 4);
                    }
                    foreach (Pair<Item, float> item in debugInteractablesNearCursor)
                    {
                        GUI.DrawLine(spriteBatch,
                            cursorPos,
                            new Vector2(item.First.DrawPosition.X, -item.First.DrawPosition.Y),
                            ToolBox.GradientLerp(item.Second, GUI.Style.Red, GUI.Style.Orange, GUI.Style.Green), width: 2);
                    }
                }
                return;
            }

            float hoverRange = 300.0f;
            float fadeOutRange = 200.0f;
            float cursorDist = Vector2.Distance(WorldPosition, cam.ScreenToWorld(PlayerInput.MousePosition));
            float hudInfoAlpha = 
                CampaignInteractionType == CampaignMode.InteractionType.None ?
                MathHelper.Clamp(1.0f - (cursorDist - (hoverRange - fadeOutRange)) / fadeOutRange, 0.2f, 1.0f) :
                1.0f;
            
            if (!GUI.DisableCharacterNames && hudInfoVisible &&
                (controlled == null || this != controlled.FocusedCharacter || IsPet) && cam.Zoom > 0.4f)
            {
                if (info != null)
                {
                    string name = Info.DisplayName;
                    if (controlled == null && name != Info.Name) { name += " " + TextManager.Get("Disguised"); }

                    Vector2 nameSize = GUI.Font.MeasureString(name);
                    Vector2 namePos = new Vector2(pos.X, pos.Y - 10.0f - (5.0f / cam.Zoom)) - nameSize * 0.5f / cam.Zoom;

                    Vector2 screenSize = new Vector2(GameMain.GraphicsWidth, GameMain.GraphicsHeight);
            	    Vector2 viewportSize = new Vector2(cam.WorldView.Width, cam.WorldView.Height);
                    namePos.X -= cam.WorldView.X; namePos.Y += cam.WorldView.Y;
            	    namePos *= screenSize / viewportSize;
            	    namePos.X = (float)Math.Floor(namePos.X); namePos.Y = (float)Math.Floor(namePos.Y);
            	    namePos *= viewportSize / screenSize;
            	    namePos.X += cam.WorldView.X; namePos.Y -= cam.WorldView.Y;

                    Color nameColor = Color.White;
                    if (Controlled != null && TeamID != Controlled.TeamID)
                    {
                        if (TeamID == CharacterTeamType.FriendlyNPC)
                        {
                            nameColor = UniqueNameColor ?? Color.SkyBlue;
                        }
                        else
                        {
                            nameColor = GUI.Style.Red;
                        }
                    }
                    if (CampaignInteractionType != CampaignMode.InteractionType.None && AllowCustomInteract)
                    {
                        var iconStyle = GUI.Style.GetComponentStyle("CampaignInteractionBubble." + CampaignInteractionType);
                        if (iconStyle != null)
                        {
                            Vector2 headPos = AnimController.GetLimb(LimbType.Head)?.body?.DrawPosition ?? DrawPosition + Vector2.UnitY * 100.0f;
                            Vector2 iconPos = headPos;
                            iconPos.Y = -iconPos.Y;
                            nameColor = iconStyle.Color;
                            var icon = iconStyle.Sprites[GUIComponent.ComponentState.None].First();
                            float iconScale = (30.0f / icon.Sprite.size.X / cam.Zoom) * GUI.Scale;
                            icon.Sprite.Draw(spriteBatch, iconPos + new Vector2(-35.0f, -25.0f), iconStyle.Color * hudInfoAlpha, scale: iconScale);
                        }
                    }

                    GUI.Font.DrawString(spriteBatch, name, namePos + new Vector2(1.0f / cam.Zoom, 1.0f / cam.Zoom), Color.Black, 0.0f, Vector2.Zero, 1.0f / cam.Zoom, SpriteEffects.None, 0.001f);
                    GUI.Font.DrawString(spriteBatch, name, namePos, nameColor * hudInfoAlpha, 0.0f, Vector2.Zero, 1.0f / cam.Zoom, SpriteEffects.None, 0.0f);
                    if (GameMain.DebugDraw)
                    {
                        GUI.Font.DrawString(spriteBatch, ID.ToString(), namePos - new Vector2(0.0f, 20.0f), Color.White);
                    }
                }
                
                var petBehavior = (AIController as EnemyAIController)?.PetBehavior;
                if (petBehavior != null && !IsDead && !IsUnconscious)
                {
                    var petStatus = petBehavior.GetCurrentStatusIndicatorType();
                    var iconStyle = GUI.Style.GetComponentStyle("PetIcon." + petStatus);
                    if (iconStyle != null)
                    {
                        Vector2 headPos = AnimController.GetLimb(LimbType.Head)?.body?.DrawPosition ?? DrawPosition + Vector2.UnitY * 100.0f;
                        Vector2 iconPos = headPos;
                        iconPos.Y = -iconPos.Y;
                        var icon = iconStyle.Sprites[GUIComponent.ComponentState.None].First();
                        float iconScale = 30.0f / icon.Sprite.size.X / cam.Zoom;
                        icon.Sprite.Draw(spriteBatch, iconPos + new Vector2(-35.0f, -25.0f), iconStyle.Color * hudInfoAlpha, scale: iconScale);
                    }
                }
            }

            if (IsDead) { return; }
            
            if (CharacterHealth.DisplayedVitality < MaxVitality * 0.98f && hudInfoVisible)
            {
                hudInfoAlpha = Math.Max(hudInfoAlpha, Math.Min(CharacterHealth.DamageOverlayTimer, 1.0f));

                Vector2 healthBarPos = new Vector2(pos.X - 50, -pos.Y);
                GUI.DrawProgressBar(spriteBatch, healthBarPos, new Vector2(100.0f, 15.0f),
                    CharacterHealth.DisplayedVitality / MaxVitality, 
                    Color.Lerp(GUI.Style.Red, GUI.Style.Green, CharacterHealth.DisplayedVitality / MaxVitality) * 0.8f * hudInfoAlpha,
                    new Color(0.5f, 0.57f, 0.6f, 1.0f) * hudInfoAlpha);
            }
        }

        /// <summary>
        /// Creates a progress bar that's "linked" to the specified object (or updates an existing one if there's one already linked to the object)
        /// The progress bar will automatically fade out after 1 sec if the method hasn't been called during that time
        /// </summary>
        public HUDProgressBar UpdateHUDProgressBar(object linkedObject, Vector2 worldPosition, float progress, Color emptyColor, Color fullColor, string textTag = "")
        {
            if (controlled != this) { return null; }

            if (!hudProgressBars.TryGetValue(linkedObject, out HUDProgressBar progressBar))
            {
                progressBar = new HUDProgressBar(worldPosition, Submarine, emptyColor, fullColor, textTag);
                hudProgressBars.Add(linkedObject, progressBar);
            }
            else
            {
                progressBar.TextTag = textTag;
            }

            progressBar.WorldPosition = worldPosition;
            progressBar.FadeTimer = Math.Max(progressBar.FadeTimer, 1.0f);
            progressBar.Progress = progress;

            return progressBar;
        }

        private readonly List<CharacterSound> matchingSounds = new List<CharacterSound>();
        private SoundChannel soundChannel;
        public void PlaySound(CharacterSound.SoundType soundType, float soundIntervalFactor = 1.0f)
        {
            if (sounds == null || sounds.Count == 0) { return; }
            if (soundChannel != null && soundChannel.IsPlaying) { return; }
            if (GameMain.SoundManager?.Disabled ?? true) { return; }
            if (soundTimer > soundInterval * soundIntervalFactor) { return; }
            matchingSounds.Clear();
            foreach (var s in sounds)
            {
                if (s.Type == soundType && (s.Gender == Gender.None || (info != null && info.Gender == s.Gender)))
                {
                    matchingSounds.Add(s);
                }
            }
            var selectedSound = matchingSounds.GetRandom();
            if (selectedSound?.Sound == null) { return; }
            soundChannel = SoundPlayer.PlaySound(selectedSound.Sound, AnimController.WorldPosition, selectedSound.Volume, selectedSound.Range, hullGuess: CurrentHull, ignoreMuffling: selectedSound.IgnoreMuffling);
            soundTimer = soundInterval;
        }

        public void AddActiveObjectiveEntity(Entity entity, Sprite sprite, Color? color = null)
        {
            if (activeObjectiveEntities.Any(aoe => aoe.Entity == entity)) return;
            ObjectiveEntity objectiveEntity = new ObjectiveEntity(entity, sprite, color);
            activeObjectiveEntities.Add(objectiveEntity);
        }

        public void RemoveActiveObjectiveEntity(Entity entity)
        {
            ObjectiveEntity found = activeObjectiveEntities.Find(aoe => aoe.Entity == entity);
            if (found == null) return;
            activeObjectiveEntities.Remove(found);
        }

        /// <summary>
        /// Note that when a predicate is provided, the random option uses Linq.Where() extension method, which creates a new collection.
        /// </summary>
        public CharacterSound GetSound(Func<CharacterSound, bool> predicate = null, bool random = false) => random ? sounds.GetRandom(predicate) : sounds.FirstOrDefault(predicate);

        partial void ImplodeFX()
        {
            Vector2 centerOfMass = AnimController.GetCenterOfMass();

            SoundPlayer.PlaySound("implode", WorldPosition);

            for (int i = 0; i < 10; i++)
            {
                Particle p = GameMain.ParticleManager.CreateParticle("waterblood",
                    WorldPosition + Rand.Vector(5.0f),
                    Rand.Vector(10.0f));
                if (p != null) p.Size *= 2.0f;

                GameMain.ParticleManager.CreateParticle("bubbles",
                    ConvertUnits.ToDisplayUnits(centerOfMass) + Rand.Vector(5.0f),
                    new Vector2(Rand.Range(-50f, 50f), Rand.Range(-100f, 50f)));

                GameMain.ParticleManager.CreateParticle("gib",
                    WorldPosition + Rand.Vector(Rand.Range(0.0f, 50.0f)),
                    Rand.Range(0.0f, MathHelper.TwoPi),
                    Rand.Range(200.0f, 700.0f), null);
            }

            for (int i = 0; i < 30; i++)
            {
                GameMain.ParticleManager.CreateParticle("heavygib",
                    WorldPosition + Rand.Vector(Rand.Range(0.0f, 50.0f)),
                    Rand.Range(0.0f, MathHelper.TwoPi),
                    Rand.Range(50.0f, 500.0f), null);
            }
        }
    }
}

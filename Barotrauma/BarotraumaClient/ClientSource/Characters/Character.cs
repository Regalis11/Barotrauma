using Barotrauma.Extensions;
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

namespace Barotrauma
{
    partial class Character
    {
        public static bool DisableControls;

        public static bool DebugDrawInteract;

        protected float soundTimer;
        protected float hudInfoTimer = 1.0f;
        protected bool hudInfoVisible = false;

        private float findFocusedTimer;

        protected float lastRecvPositionUpdateTime;

        private const float DefaultHudInfoHeight = 78.0f;
        private float hudInfoHeight = DefaultHudInfoHeight;

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

        public bool ShowInteractionLabels { get; private set; }

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

        /// <summary>
        /// Can be used by status effects
        /// </summary>
        public float CollapseEffectStrength
        {
            get { return Level.Loaded?.Renderer?.CollapseEffectStrength ?? 0.0f; }
            set
            {
                if (Level.Loaded?.Renderer == null) { return; }
                if (Controlled == this)
                {
                    float strength = MathHelper.Clamp(value, 0.0f, 1.0f);
                    Level.Loaded.Renderer.CollapseEffectStrength = strength;
                    Level.Loaded.Renderer.CollapseEffectOrigin = Submarine?.WorldPosition ?? WorldPosition;
                    Screen.Selected.Cam.Shake = Math.Max(MathF.Pow(strength, 3) * 100.0f, Screen.Selected.Cam.Shake);
                    Screen.Selected.Cam.Rotation = strength * (PerlinNoise.GetPerlin((float)Timing.TotalTime * 0.01f, (float)Timing.TotalTime * 0.05f) - 0.5f);
                    Level.Loaded.Renderer.ChromaticAberrationStrength = value * 50.0f;
                }
            }
        }
        /// <summary>
        /// Can be used to set camera shake from status effects
        /// </summary>
        public float CameraShake
        {
            get { return Screen.Selected?.Cam?.Shake ?? 0.0f; }
            set
            {
                if (!MathUtils.IsValid(value)) { return; }
                if (this != Controlled) { return; }
                if (Screen.Selected?.Cam != null)
                {
                    Screen.Selected.Cam.Shake = value;
                }
            }
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

        private class GUIMessage
        {
            public string RawText;
            public Identifier Identifier;
            public string Text;

            private int _value;
            public int Value
            {
                get { return _value; }
                set
                {
                    _value = value;
                    Text = RawText.Replace("[value]", _value.ToString());
                    Size = GUIStyle.Font.MeasureString(Text);
                }
            }

            public Color Color;
            public float Lifetime;
            public float Timer;

            public Vector2 Size;

            public bool PlaySound;

            public GUIMessage(string rawText, Color color, float delay, Identifier identifier = default, int? value = null, float lifeTime = 3.0f)
            {
                RawText = Text = rawText;
                if (value.HasValue)
                {
                    Text = rawText.Replace("[value]", value.Value.ToString());
                    Value = value.Value;
                }
                Timer = -delay;
                Size = GUIStyle.Font.MeasureString(Text);
                Color = color;
                Identifier = identifier;
                Lifetime = lifeTime;
            }
        }

        private List<GUIMessage> guiMessages = new List<GUIMessage>();

        public static bool IsMouseOnUI => GUI.MouseOn != null ||
                    (CharacterInventory.IsMouseOnInventory && !CharacterInventory.DraggingItemToWorld);

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

        private static readonly List<SpeechBubble> speechBubbles = new List<SpeechBubble>();

        private SpeechBubble textlessSpeechBubble;

        sealed class SpeechBubble
        {
            public float LifeTime;
            public Vector2 PrevPosition;
            public Vector2 Position;
            public Vector2 DrawPosition;
            public float MoveUpAmount;
            public readonly RichString Text;
            public readonly Character Character;
            public readonly Submarine Submarine;
            public readonly Vector2 TextSize;

            public Color Color;
            public bool Moving;

            public SpeechBubble(Character character, float lifeTime, Color color, string text = "")
            {
                Text = RichString.Rich(ToolBox.WrapText(text, GUI.IntScale(300), GUIStyle.SmallFont.GetFontForStr(text)));
                TextSize = GUIStyle.SmallFont.MeasureString(Text);

                Character = character;
                Position = GetDesiredPosition();
                Submarine = character.Submarine;
                LifeTime = lifeTime;
                Color = color;
            }

            public Vector2 GetDesiredPosition()
            {
                return Character.Position + Vector2.UnitY * 100;
            }
        }

        private float pressureEffectTimer;

        partial void InitProjSpecific(ContentXElement mainElement)
        {
            soundTimer = Rand.Range(0.0f, Params.SoundInterval);

            sounds = new List<CharacterSound>();
            Params.Sounds.ForEach(s => sounds.Add(new CharacterSound(s)));

            foreach (var subElement in mainElement.Elements())
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

        private readonly List<Item> previousInteractablesInRange = new();
        private readonly List<Item> interactablesInRange = new();
        
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

                if (CharacterInventory.IsMouseOnInventory && 
                    !keys[(int)InputType.Aim].Held &&
                    CharacterHUD.ShouldDrawInventory(this))
                {
                    ResetInputIfPrimaryMouse(InputType.Use);
                    ResetInputIfPrimaryMouse(InputType.Shoot);
                    ResetInputIfPrimaryMouse(InputType.Select);
                    void ResetInputIfPrimaryMouse(InputType inputType)
                    {
                        if (GameSettings.CurrentConfig.KeyMap.Bindings[inputType].MouseButton == MouseButton.PrimaryMouse)
                        {
                            keys[(int)inputType].Reset();
                        }
                    }
                }

                ShowInteractionLabels = keys[(int)InputType.ShowInteractionLabels].Held;

                if (ShowInteractionLabels)
                {
                    focusedItem = InteractionLabelManager.HoveredItem;
                }

                //if we were firing (= pressing the aim and shoot keys at the same time)
                //and the fire key is the same as Select or Use, reset the key to prevent accidentally selecting/using items
                if (wasFiring && !keys[(int)InputType.Shoot].Held)
                {
                    if (GameSettings.CurrentConfig.KeyMap.Bindings[InputType.Shoot] == GameSettings.CurrentConfig.KeyMap.Bindings[InputType.Select])
                    {
                        keys[(int)InputType.Select].Reset();
                    }
                    if (GameSettings.CurrentConfig.KeyMap.Bindings[InputType.Shoot] == GameSettings.CurrentConfig.KeyMap.Bindings[InputType.Use])
                    {
                        keys[(int)InputType.Use].Reset();
                    }
                    wasFiring = false;
                }

                float targetOffsetAmount = 0.0f;
                if (moveCam)
                {
                    if (!IsProtectedFromPressure && (AnimController.CurrentHull == null || AnimController.CurrentHull.LethalPressure > 0.0f))
                    {
                        //wait until the character has been in pressure for one second so the zoom doesn't
                        //"flicker" in and out if the pressure fluctuates around the minimum threshold
                        pressureEffectTimer += deltaTime;
                        if (pressureEffectTimer > 1.0f)
                        {
                            float pressure = AnimController.CurrentHull == null ? 100.0f : AnimController.CurrentHull.LethalPressure;
                            float zoomInEffectStrength = MathHelper.Clamp(pressure / 100.0f, 0.0f, 1.0f);
                            cam.Zoom = MathHelper.Lerp(cam.Zoom,
                                cam.DefaultZoom + (Math.Max(pressure, 10) / 150.0f) * Rand.Range(0.9f, 1.1f),
                                zoomInEffectStrength);
                        }
                    }
                    else
                    {
                        pressureEffectTimer = 0.0f;
                    }

                    if (IsHumanoid)
                    {
                        cam.OffsetAmount = 250.0f;// MathHelper.Lerp(cam.OffsetAmount, 250.0f, deltaTime);
                    }
                    else
                    {
                        //increased visibility range when controlling large a non-humanoid
                        cam.OffsetAmount = MathHelper.Clamp(Mass, 250.0f, 1500.0f);
                    }
                }

                UpdateLocalCursor(cam);

                if (IsKeyHit(InputType.ToggleRun))
                {
                    ToggleRun = !ToggleRun;
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
                else if (SelectedItem != null && ViewTarget == null &&
                    SelectedItem.Components.Any(ic => ic?.GuiFrame != null && ic.ShouldDrawHUD(this)))
                {
                    cam.OffsetAmount = targetOffsetAmount = 0.0f;
                    cursorPosition =
                        Position +
                        PlayerInput.MouseSpeed.ClampLength(10.0f); //apply a little bit of movement to the cursor pos to prevent AFK kicking
                }
                else if (!GameSettings.CurrentConfig.EnableMouseLook)
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
                if (SelectedItem != null &&
                    (SelectedItem.ActiveHUDs.Any(ic => ic.GuiFrame != null && ic.CloseByClickingOutsideGUIFrame && HUD.CloseHUD(ic.GuiFrame.Rect)) ||
                    ((ViewTarget as Item)?.Prefab.FocusOnSelected ?? false) && PlayerInput.KeyHit(Microsoft.Xna.Framework.Input.Keys.Escape)))
                {
                    if (GameMain.Client != null)
                    {
                        //emulate a Deselect input to get the character to deselect the item server-side
                        EmulateInput(InputType.Deselect);
                    }
                    //reset focus to prevent us from accidentally interacting with another entity
                    focusedItem = null;
                    FocusedCharacter = null;
                    findFocusedTimer = 0.2f;
                    SelectedItem = null;
                }
            }

            DisableControls = false;
        }

        public void UpdateLocalCursor(Camera cam)
        {
            cursorPosition = cam.ScreenToWorld(PlayerInput.MousePosition);
            if (AnimController.CurrentHull?.Submarine != null)
            {
                cursorPosition -= AnimController.CurrentHull.Submarine.DrawPosition;
            }
        }

        partial void UpdateControlled(float deltaTime, Camera cam)
        {
            if (controlled != this) { return; }
            
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

        public void EmulateInput(InputType input)
        {
            keys[(int)input].Hit = true;
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
            if (AIState != AIState.PlayDead)
            {
                PlaySound(CharacterSound.SoundType.Damage, maxInterval: 2);
            }
        }

        partial void KillProjSpecific(CauseOfDeathType causeOfDeath, Affliction causeOfDeathAffliction, bool log)
        {
            HintManager.OnCharacterKilled(this);

            if (GameMain.NetworkMember != null && controlled == this)
            {
                LocalizedString chatMessage = CauseOfDeath.Type == CauseOfDeathType.Affliction ?
                    CauseOfDeath.Affliction.SelfCauseOfDeathDescription :
                    TextManager.Get("Self_CauseOfDeathDescription." + CauseOfDeath.Type.ToString(), "Self_CauseOfDeathDescription.Damage");

                if (GameMain.Client != null) { chatMessage += " " + TextManager.Get("DeathChatNotification"); }

                RespawnManager.ShowDeathPromptIfNeeded();

                GameMain.NetworkMember.AddChatMessage(chatMessage.Value, ChatMessageType.Dead);
                GameMain.LightManager.LosEnabled = false;
                controlled = null;
                if (Screen.Selected?.Cam is Camera cam)
                {
                    cam.TargetPos = Vector2.Zero;
                    //briefly lock moving the camera with arrow keys
                    //(it's annoying to have the camera fly off when you die while trying to move to safety)
                    cam.MovementLockTimer = 2.0f;                    
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
                if (Screen.Selected?.Cam is not null)
                {
                    Screen.Selected.Cam.TargetPos = Vector2.Zero;
                    Lights.LightManager.ViewTarget = null;
                }
            }

            sounds.Clear();

            if (GameMain.GameSession?.CrewManager != null &&
                GameMain.GameSession.CrewManager.GetCharacters().Contains(this))
            {
                GameMain.GameSession.CrewManager.RemoveCharacter(this);
            }

            if (GameMain.Client?.Character == this) { GameMain.Client.Character = null; }

            if (Lights.LightManager.ViewTarget == this) { Lights.LightManager.ViewTarget = null; }
        }

        private void UpdateInteractablesInRange()
        {
            // keep two lists to detect changes to the current state of interactables in range
            previousInteractablesInRange.Clear();
            previousInteractablesInRange.AddRange(interactablesInRange);

            interactablesInRange.Clear();
            
            //use the list of visible entities if it exists
            var entityList = Submarine.VisibleEntities ?? Item.ItemList;

            foreach (MapEntity entity in entityList)
            {
                if (entity is not Item item) { continue; }

                if (item.body != null && !item.body.Enabled) { continue; }

                if (item.ParentInventory != null) { continue; }

                if (item.Prefab.RequireCampaignInteract &&
                    item.CampaignInteractionType == CampaignMode.InteractionType.None)
                {
                    continue;
                }

                if (Screen.Selected is SubEditorScreen { WiringMode: true } &&
                    item.GetComponent<ConnectionPanel>() == null)
                {
                    continue;
                }

                if (CanInteractWith(item))
                {
                    interactablesInRange.Add(item);
                }
            }
            
            if (!interactablesInRange.SequenceEqual(previousInteractablesInRange))
            {
                InteractionLabelManager.RefreshInteractablesInRange(interactablesInRange);
            }
        }

        private readonly List<Item> debugInteractablesInRange = new List<Item>();
        private readonly List<Item> debugInteractablesAtCursor = new List<Item>();
        private readonly List<(Item item, float dist)> debugInteractablesNearCursor = new List<(Item item, float dist)>();

        /// <summary>
        /// Finds the front (lowest depth) interactable item at a position. "Interactable" in this case means that the character can "reach" the item.
        /// </summary>
        /// <param name="itemCollection">Item collection to look in</param>
        /// <param name="simPosition">sim position for distance comparison (such as mouse position)</param>
        /// <param name="aimAssistModifier">aim assist modifier</param>
        /// <returns></returns>
        public Item FindClosestItem(List<Item> itemCollection, Vector2 simPosition, float aimAssistModifier = 0.0f)
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
            float aimAssistAmount = SelectedItem == null ? 100.0f * aimAssistModifier : 1.0f;

            Vector2 displayPosition = ConvertUnits.ToDisplayUnits(simPosition);
            
            Item closestItem = null;
            float closestItemDistance = Math.Max(aimAssistAmount, 2.0f);
            foreach (var item in itemCollection)
            {
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
                
                debugInteractablesNearCursor.Add((item, 1.0f - distanceToItem / (100.0f * aimAssistModifier)));
                closestItem = item;
                closestItemDistance = distanceToItem;
            }
            
            return closestItem;
        }

        private Character FindCharacterAtPosition(Vector2 mouseSimPos, float maxDist = MaxHighlightDistance)
        {
            Character closestCharacter = null;

            maxDist = ConvertUnits.ToSimUnits(maxDist);
            float closestDist = maxDist;
            foreach (Character c in CharacterList)
            {
                if (!CanInteractWith(c, checkVisibility: false) || (c.AnimController?.SimplePhysicsEnabled ?? true)) { continue; }

                float dist = c.GetDistanceToClosestLimb(mouseSimPos);
                if (dist < closestDist || 
                    (c.CampaignInteractionType != CampaignMode.InteractionType.None && closestCharacter?.CampaignInteractionType == CampaignMode.InteractionType.None && dist * 0.9f < closestDist))
                {
                    closestCharacter = c;
                    closestDist = dist;
                }
            }

            return closestCharacter;
        }

        public bool ShouldLockHud()
        {
            if (this != controlled) { return false; }
            if (GameMain.GameSession?.Campaign != null && GameMain.GameSession.Campaign.ShowCampaignUI) { return true; }
            var controller = SelectedItem?.GetComponent<Controller>();
            //lock if using a controller, except if we're also using a connection panel in the same item
            return
                SelectedItem != null &&
                controller?.User == this && controller.HideHUD &&
                SelectedItem?.GetComponent<ConnectionPanel>()?.User != this;
        }


        partial void UpdateProjSpecific(float deltaTime, Camera cam)
        {
            foreach (GUIMessage message in guiMessages)
            {
                bool wasPending = message.Timer < 0.0f;
                message.Timer += deltaTime;
                if (wasPending && message.Timer >= 0.0f && message.PlaySound)
                {
                    SoundPlayer.PlayUISound(GUISoundType.UIMessage);
                }
            }
            guiMessages.RemoveAll(m => m.Timer >= m.Lifetime);

            if (textlessSpeechBubble != null)
            {
                textlessSpeechBubble.LifeTime -= deltaTime;
                if (textlessSpeechBubble.LifeTime <= 0) { textlessSpeechBubble = null; }
            }

            if (!enabled) { return; }

            if (!IsIncapacitated)
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
                            if (Rand.Value() > 0.5f)
                            {
                                PlaySound(CharacterSound.SoundType.Attack);
                            }
                            else
                            {
                                PlaySound(CharacterSound.SoundType.Idle);
                            }
                            break;
                        case AIState.PlayDead:
                        case AIState.Freeze:
                        case AIState.Hiding:
                            break;
                        default:
                            if (enemyAI.PetBehavior is PetBehavior petBehavior && 
                                (petBehavior.Happiness < petBehavior.UnhappyThreshold || petBehavior.Hunger > petBehavior.HungryThreshold))
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

        partial void SetOrderProjSpecific(Order order)
        {
            GameMain.GameSession?.CrewManager?.AddCurrentOrderIcon(this, order);
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

        public void DrawGUIMessages(SpriteBatch spriteBatch, Camera cam)
        {
            if (info == null || !Enabled || InvisibleTimer > 0.0f)
            {
                return;
            }

            Vector2 messagePos = DrawPosition;
            messagePos.Y += hudInfoHeight;
            messagePos = cam.WorldToScreen(messagePos) - Vector2.UnitY * GUI.IntScale(60);
            foreach (GUIMessage message in guiMessages)
            {
                if (message.Timer < 0) { continue; }
                Vector2 drawPos = messagePos + Vector2.UnitX * (GUI.IntScale(60) - message.Size.X);
                drawPos = new Vector2((int)drawPos.X, (int)drawPos.Y);
                float alpha = MathHelper.SmoothStep(1.0f, 0.0f, message.Timer / message.Lifetime);
                GUI.DrawString(spriteBatch, drawPos, message.Text, message.Color * alpha);
                messagePos -= Vector2.UnitY * message.Size.Y * 1.2f;
            }            
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
                GameSettings.CurrentConfig.Graphics.LosMode != LosMode.None &&
                //less restrictions on name tag visibility in PvP mode (always show them if the character is visible)
                GameMain.GameSession?.GameMode is not PvPMode)
            {
                float yPos = Controlled.AnimController.FloorY - 1.5f;

                if (Controlled.AnimController.Stairs != null)
                {
                    //consider the bottom of the stairs the "floor of the room the controlled character is in"
                    yPos = Controlled.AnimController.Stairs.SimPosition.Y - Controlled.AnimController.Stairs.RectHeight * 0.5f;
                }

                //don't show the HUD texts if the character is below the floor of the room the controlled character is in
                if (AnimController.FloorY < yPos) { return; }
            }

            Vector2 pos = DrawPosition;
            pos.Y += hudInfoHeight;

            float paddingBelowCeiling = 30.0f;
            if (CurrentHull != null && DrawPosition.Y + DefaultHudInfoHeight > CurrentHull.WorldRect.Y - paddingBelowCeiling)
            {
                float lowerAmount = (DrawPosition.Y + DefaultHudInfoHeight) - (CurrentHull.WorldRect.Y - paddingBelowCeiling);
                hudInfoHeight = MathHelper.Lerp(hudInfoHeight, DefaultHudInfoHeight - lowerAmount, 0.1f);
                hudInfoHeight = Math.Max(hudInfoHeight, 20.0f);
            }
            else
            {
                hudInfoHeight = MathHelper.Lerp(hudInfoHeight, DefaultHudInfoHeight, 0.1f);
            }

            pos.Y = -pos.Y;

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
                    foreach ((Item item, float dist) in debugInteractablesNearCursor)
                    {
                        GUI.DrawLine(spriteBatch,
                            cursorPos,
                            new Vector2(item.DrawPosition.X, -item.DrawPosition.Y),
                            ToolBox.GradientLerp(dist, GUIStyle.Red, GUIStyle.Orange, GUIStyle.Green), width: 2);
                    }
                }
            }
            else
            {

                float hoverRange = 300.0f;
                float fadeOutRange = 200.0f;
                float cursorDist = Vector2.Distance(WorldPosition, cam.ScreenToWorld(PlayerInput.MousePosition));
                float hudInfoAlpha =
                    CampaignInteractionType == CampaignMode.InteractionType.None ?
                    MathHelper.Clamp(1.0f - (cursorDist - (hoverRange - fadeOutRange)) / fadeOutRange, 0.2f, 1.0f) :
                    1.0f;
                //full name tag visibility in PvP mode to make it easier to tell who's an enemy
                float nameTextAlpha = GameMain.GameSession?.GameMode is PvPMode ? 1.0f : hudInfoAlpha;

                if (!GUI.DisableCharacterNames && hudInfoVisible &&
                    (controlled == null || this != controlled.FocusedCharacter || IsPet) && cam.Zoom > 0.4f)
                {
                    if (info != null)
                    {
                        LocalizedString name = Info.DisplayName;
                        if (controlled == null && name != Info.Name)
                        {
                            name += " " + TextManager.Get("Disguised");
                        }
                        else if (Info.Title != null && TeamID != CharacterTeamType.Team1)
                        {
                            name += '\n' + Info.Title;
                        }

                        Vector2 nameSize = GUIStyle.Font.MeasureString(name);
                        Vector2 namePos = new Vector2(pos.X, pos.Y - 5.0f - (5.0f / cam.Zoom)) - nameSize * 0.5f / cam.Zoom;
                        Color nameColor = GetNameColor();

                        Vector2 screenSize = new Vector2(GameMain.GraphicsWidth, GameMain.GraphicsHeight);
                        Vector2 viewportSize = new Vector2(cam.WorldView.Width, cam.WorldView.Height);
                        namePos.X -= cam.WorldView.X; namePos.Y += cam.WorldView.Y;
                        namePos *= screenSize / viewportSize;
                        namePos.X = (float)Math.Floor(namePos.X); namePos.Y = (float)Math.Floor(namePos.Y);
                        namePos *= viewportSize / screenSize;
                        namePos.X += cam.WorldView.X; namePos.Y -= cam.WorldView.Y;

                        if (CampaignInteractionType != CampaignMode.InteractionType.None && AllowCustomInteract)
                        {
                            var iconStyle = GUIStyle.GetComponentStyle("CampaignInteractionBubble." + CampaignInteractionType);
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

                        GUIStyle.Font.DrawString(spriteBatch, name, namePos + new Vector2(1.0f / cam.Zoom, 1.0f / cam.Zoom), Color.Black, 0.0f, Vector2.Zero, 1.0f / cam.Zoom, SpriteEffects.None, 0.001f);
                        GUIStyle.Font.DrawString(spriteBatch, name, namePos, nameColor * nameTextAlpha, 0.0f, Vector2.Zero, 1.0f / cam.Zoom, SpriteEffects.None, 0.0f);
                        if (GameMain.DebugDraw)
                        {
                            GUIStyle.Font.DrawString(spriteBatch, ID.ToString(), namePos - new Vector2(0.0f, 20.0f), Color.White);
                        }
                    }

                    var petBehavior = (AIController as EnemyAIController)?.PetBehavior;
                    if (petBehavior != null && !IsDead && !IsUnconscious)
                    {
                        var petStatus = petBehavior.GetCurrentStatusIndicatorType();
                        if (petStatus != PetBehavior.StatusIndicatorType.None)
                        {
                            var iconStyle = GUIStyle.GetComponentStyle("PetIcon." + petStatus);
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
                }

                if (IsDead) { return; }

                var healthBarMode = GameMain.NetworkMember?.ServerSettings.ShowEnemyHealthBars ?? GameSettings.CurrentConfig.ShowEnemyHealthBars;
                if (healthBarMode != EnemyHealthBarMode.ShowAll)
                {
                    if (Controlled == null)
                    {
                        if (!IsOnPlayerTeam) { return; }
                    }
                    else
                    {
                        if (!HumanAIController.IsFriendly(Controlled, this) ||
                            (AIController is HumanAIController humanAi && humanAi.ObjectiveManager.CurrentObjective is AIObjectiveCombat combatObjective && HumanAIController.IsFriendly(Controlled, combatObjective.Enemy)))
                        {
                            return;
                        }
                    }
                }

                if (Params.ShowHealthBar && CharacterHealth.DisplayedVitality < MaxVitality * 0.98f && hudInfoVisible && AIState != AIState.PlayDead && AIState != AIState.Hiding)
                {
                    hudInfoAlpha = Math.Max(hudInfoAlpha, Math.Min(CharacterHealth.DamageOverlayTimer, 1.0f));

                    Vector2 healthBarPos = new Vector2(pos.X - 50, -pos.Y);
                    GUI.DrawProgressBar(spriteBatch, healthBarPos, new Vector2(100.0f, 15.0f),
                        CharacterHealth.DisplayedVitality / MaxVitality,
                        Color.Lerp(GUIStyle.Red, GUIStyle.Green, CharacterHealth.DisplayedVitality / MaxVitality) * 0.8f * hudInfoAlpha,
                        new Color(0.5f, 0.57f, 0.6f, 1.0f) * hudInfoAlpha);
                }
            }

            if (textlessSpeechBubble != null)
            {
                Vector2 iconPos = pos - Vector2.UnitY * 5;
           
                GUIStyle.SpeechBubbleIcon.Value.Sprite.Draw(spriteBatch, iconPos,
                    textlessSpeechBubble.Color * Math.Min(textlessSpeechBubble.LifeTime, 1.0f), 0.0f,
                    Math.Min(textlessSpeechBubble.LifeTime, 1.0f));
            }
        }

        public void ShowSpeechBubble(Color color, string text)
        {
            if (!GameSettings.CurrentConfig.ChatSpeechBubbles)
            {
                ShowTextlessSpeechBubble(1.0f, color);
                return;
            }
            float duration = MathHelper.Lerp(1.0f, 8.0f, Math.Min(text.Length / 100.0f, 1.0f));
            speechBubbles.Add(new SpeechBubble(this, duration, color, text));
            textlessSpeechBubble = null;
        }

        public void ShowTextlessSpeechBubble(float duration, Color color)
        {
            if (speechBubbles.Any(sb => sb.Character == this)) { return; }
            if (textlessSpeechBubble == null)
            {
                textlessSpeechBubble = new SpeechBubble(this, duration, color);
            }
            else
            {
                textlessSpeechBubble.Color = color;
                textlessSpeechBubble.LifeTime = Math.Max(textlessSpeechBubble.LifeTime, duration);
            }
        }

        public static void DrawSpeechBubbles(SpriteBatch spriteBatch, Camera cam)
        {
            spriteBatch.Begin(SpriteSortMode.Deferred, BlendState.NonPremultiplied, SamplerState.LinearWrap, DepthStencilState.None, null, null, cam.Transform);
            foreach (var bubble in speechBubbles)
            {
                Vector2 iconPos = Timing.Interpolate(bubble.PrevPosition, bubble.Position);
                iconPos += Vector2.UnitY * bubble.MoveUpAmount;
                if (bubble.Submarine != null)
                {
                    iconPos += bubble.Submarine.DrawPosition;
                }

                float alpha = 1.0f;
                float mouseDist = Vector2.Distance(cam.WorldToScreen(iconPos), PlayerInput.MousePosition);
                //treat the size of the bubble from corner to corner as the 
                float textSize = bubble.TextSize.Length();
                if (mouseDist < textSize)
                {
                    alpha *= Math.Max(mouseDist / textSize, 0.5f);
                }

                iconPos.Y = -iconPos.Y;
                if (GUIStyle.SpeechBubbleIconSliced.Value is { } speechBubbleIconSliced)
                {
                    Vector2 bubbleSize = bubble.TextSize + Vector2.One * GUI.IntScale(15);
                    speechBubbleIconSliced.Draw(spriteBatch, new RectangleF(iconPos - bubbleSize / 2, bubbleSize), bubble.Color * Math.Min(bubble.LifeTime, 1.0f) * alpha);
                }
                GUI.DrawStringWithColors(spriteBatch, iconPos - bubble.TextSize / 2, bubble.Text.SanitizedValue, bubble.Color * Math.Min(bubble.LifeTime, 1.0f) * alpha, bubble.Text.RichTextData, font: GUIStyle.SmallFont);
            }
            spriteBatch.End();
        }

        static partial void UpdateSpeechBubbles(float deltaTime)
        {
            for (int i = speechBubbles.Count - 1; i >= 0; i--)
            {
                var bubble = speechBubbles[i];
                bubble.LifeTime -= deltaTime;
                if (bubble.LifeTime <= 0 || bubble.Character is { Removed: true })
                {
                    speechBubbles.RemoveAt(i);
                    continue;
                }

                bubble.PrevPosition = bubble.Position;

                Vector2 desiredPos = bubble.GetDesiredPosition();
                Vector2 diff = desiredPos - bubble.Position;
                float dist = diff.Length();
                //how far the bubble needs to be from the desired position to start moving
                const float MoveThreshold = 100.0f;
                const float MaxSpeed = 1000.0f;
                if (dist < 1)
                {
                    bubble.Moving = false;
                }
                else if (dist > MoveThreshold || bubble.Moving)
                {
                    Vector2 moveAmount = diff / dist * MathHelper.Clamp(dist * 5, 0, MaxSpeed) * deltaTime;
                    //slower vertical movement (don't want to interfere too much with the bubbles floating up
                    //and the overlap prevention which works vertically)
                    moveAmount.Y *= 0.1f;
                    bubble.Position += moveAmount;
                    bubble.Moving = true;
                }

                bubble.MoveUpAmount += deltaTime * 5.0f;
                //go through the newer bubbles, move this one out of the way if one is overlapping
                for (int j = i + 1; j < speechBubbles.Count; j++)
                {
                    var otherBubble = speechBubbles[j];
                    {
                        if (Math.Abs(bubble.Position.X - otherBubble.Position.X) < (bubble.TextSize.X + otherBubble.TextSize.X) / 2 &&
                            Math.Abs(bubble.Position.Y - otherBubble.Position.Y) < (bubble.TextSize.Y + otherBubble.TextSize.Y) / 2 + 10)
                        {
                            bubble.Position += Vector2.UnitY * deltaTime * 50.0f;
                        }
                    }
                }
            }
        }

        public Color GetNameColor()
        {
            CharacterTeamType team = teamID;
            if (Info is { IsDisguisedAsAnother: true })
            {
                var idCard = Inventory.GetItemInLimbSlot(InvSlotType.Card)?.GetComponent<IdCard>();
                if (idCard != null)
                {
                    if (team == CharacterTeamType.Team2 && idCard.TeamID != CharacterTeamType.Team2)
                    {
                        team = CharacterTeamType.Team1;
                    }
                    else if (team == CharacterTeamType.Team1 && idCard.TeamID == CharacterTeamType.Team2)
                    {
                        team = CharacterTeamType.Team2;
                    }
                }
            }

            CharacterTeamType myTeam = 
                Controlled?.TeamID ?? 
                GameMain.Client?.MyClient?.TeamID ??
                CharacterTeamType.Team1;

            Color nameColor = GUIStyle.TextColorNormal;
            if (TeamID == CharacterTeamType.FriendlyNPC)
            {
                nameColor = UniqueNameColor ?? Color.SkyBlue;
            }
            else if (team != myTeam)
            {
                //opposing team is red when controlling a character
                nameColor = GUIStyle.Red;
            }

            return nameColor;
        }

        public void AddMessage(string rawText, Color color, bool playSound, Identifier identifier = default, int? value = null, float lifetime = 3.0f)
        {
            GUIMessage existingMessage = null;

            float delay = 0.0f;
            if (guiMessages.Any())
            {
                delay = guiMessages.Min(m => m.Timer) - 0.5f;
                if (delay < 0)
                {
                    delay = -delay;
                    if (guiMessages.Count > 5)
                    {
                        //reduce delays if there's lots of messages
                        guiMessages.Where(m => m.Timer < 0.0f).ForEach(m => m.Timer *= 0.9f);
                    }
                }
                else
                {
                    delay = 0;
                }
            }

            if (identifier != null)
            {
                existingMessage = guiMessages.Find(m => m.Identifier == identifier && m.Timer < m.Lifetime * 0.5f);
            }
            if (existingMessage == null || !value.HasValue)
            {
                var newMessage = new GUIMessage(rawText, color, delay, identifier, value, lifetime);
                guiMessages.Insert(0, newMessage);
                if (playSound)
                {
                    if (delay > 0.0f) 
                    { 
                        newMessage.PlaySound = true;
                    }
                    else
                    {
                        SoundPlayer.PlayUISound(GUISoundType.UIMessage);
                    }
                }
            }
            else
            {
                existingMessage.Value += value.Value;
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
        public void PlaySound(CharacterSound.SoundType soundType, float soundIntervalFactor = 1.0f, float maxInterval = 0)
        {
            if (Removed) { return; }
            if (sounds == null || sounds.Count == 0) { return; }
            if (soundChannel != null && soundChannel.IsPlaying) { return; }
            if (GameMain.SoundManager?.Disabled ?? true) { return; }
            if (soundTimer > Params.SoundInterval * soundIntervalFactor) { return; }
            if (Params.SoundInterval - soundTimer < maxInterval) { return; }
            matchingSounds.Clear();
            foreach (var s in sounds)
            {
                if (s.Type == soundType && (s.TagSet.None() || (info != null && s.TagSet.IsSubsetOf(info.Head.Preset.TagSet))))
                {
                    matchingSounds.Add(s);
                }
            }
            var selectedSound = matchingSounds.GetRandomUnsynced();
            if (selectedSound?.Sound == null) { return; }
            soundChannel = SoundPlayer.PlaySound(selectedSound.Sound, AnimController.WorldPosition, selectedSound.Volume, selectedSound.Range, hullGuess: CurrentHull, ignoreMuffling: selectedSound.IgnoreMuffling, freqMult: selectedSound.GetRandomFrequencyMultiplier());
            soundTimer = Params.SoundInterval;
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
        public CharacterSound GetSound(Func<CharacterSound, bool> predicate = null, bool random = false) => random ? sounds.GetRandomUnsynced(predicate) : sounds.FirstOrDefault(predicate);

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

        partial void OnMoneyChanged(int prevAmount, int newAmount) { }

        partial void OnTalentGiven(TalentPrefab talentPrefab)
        {
            if (!talentPrefab.IsHiddenExtraTalent)
            {
                AddMessage(TextManager.Get("talentname." + talentPrefab.Identifier).Value, GUIStyle.Yellow, playSound: this == Controlled);
            }
        }
    }
}

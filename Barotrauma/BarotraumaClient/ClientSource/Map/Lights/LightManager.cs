using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using System.Collections.Generic;
using System.Linq;
using System;
using Barotrauma.Items.Components;
using Barotrauma.Extensions;
using System.Threading;

namespace Barotrauma.Lights
{
    class LightManager
    {
        /// <summary>
        /// How many light sources are allowed to recalculate their light volumes per frame. 
        /// Pending calculations will be done on subsequent frames, starting from the light sources that have been waiting for a recalculation the longest. 
        /// </summary>
        const int MaxLightVolumeRecalculationsPerFrame = 5;

        /// <summary>
        /// If zoomed further out than this, characters no longer obstruct lights behind them. 
        /// Improves performance, and isn't very noticeable if we do it after zoomed far out enough.
        /// </summary>
        const float ObstructLightsBehindCharactersZoomThreshold = 0.5f;

        private Thread rayCastThread;
        private Queue<RayCastTask> pendingRayCasts = new Queue<RayCastTask>();

        public static Entity ViewTarget { get; set; }

        private float currLightMapScale;

        public Color AmbientLight;

        public RenderTarget2D LightMap
        {
            get;
            private set;
        }
        public RenderTarget2D LimbLightMap
        {
            get;
            private set;
        }
        public RenderTarget2D LosTexture
        {
            get;
            private set;
        }
        public RenderTarget2D HighlightMap
        {
            get;
            private set;
        }

        private readonly Texture2D highlightRaster;

        private BasicEffect lightEffect;

        public Effect LosEffect { get; private set; }
        public Effect SolidColorEffect { get; private set; }

        private readonly List<LightSource> lights;

        public bool DebugLos;

        public bool LosEnabled = true;
        public float LosAlpha = 1f;
        public LosMode LosMode = LosMode.Transparent;

        public bool LightingEnabled = true;

        public float ObstructVisionAmount;

        private readonly Texture2D visionCircle;

        private readonly Texture2D gapGlowTexture;

        private Vector2 losOffset;

        private int recalculationCount;

        private float time;

        public IEnumerable<LightSource> Lights
        {
            get { return lights; }
        }

        public LightManager(GraphicsDevice graphics)
        {
            lights = new List<LightSource>(100);

            AmbientLight = new Color(20, 20, 20, 255);

            rayCastThread = new Thread(UpdateRayCasts)
            {
                Name = "LightManager Raycast thread",
                IsBackground = true //this should kill the thread if the game crashes
            };
            rayCastThread.Start();

            visionCircle = Sprite.LoadTexture("Content/Lights/visioncircle.png");
            highlightRaster = Sprite.LoadTexture("Content/UI/HighlightRaster.png");
            gapGlowTexture = Sprite.LoadTexture("Content/Lights/pointlight_rays.png");

            GameMain.Instance.ResolutionChanged += () =>
            {
                CreateRenderTargets(graphics);
            };

            CrossThread.RequestExecutionOnMainThread(() =>
            {
                CreateRenderTargets(graphics);

                LosEffect = EffectLoader.Load("Effects/losshader");
                SolidColorEffect = EffectLoader.Load("Effects/solidcolor");

                lightEffect ??= new BasicEffect(GameMain.Instance.GraphicsDevice)
                    {
                        VertexColorEnabled = true,
                        TextureEnabled = true,
                        Texture = LightSource.LightTexture
                    };
            });
        }

        private void CreateRenderTargets(GraphicsDevice graphics)
        {
            var pp = graphics.PresentationParameters;

            currLightMapScale = GameSettings.CurrentConfig.Graphics.LightMapScale;

            LightMap?.Dispose();
            LightMap = CreateRenderTarget();

            LimbLightMap?.Dispose();
            LimbLightMap = CreateRenderTarget();

            HighlightMap?.Dispose();
            HighlightMap = CreateRenderTarget();

            RenderTarget2D CreateRenderTarget()
            {
               return new RenderTarget2D(graphics,
                       (int)(GameMain.GraphicsWidth * GameSettings.CurrentConfig.Graphics.LightMapScale), (int)(GameMain.GraphicsHeight * GameSettings.CurrentConfig.Graphics.LightMapScale), false,
                       pp.BackBufferFormat, pp.DepthStencilFormat, pp.MultiSampleCount,
                       RenderTargetUsage.DiscardContents);
            }

            LosTexture?.Dispose();
            LosTexture = new RenderTarget2D(graphics,
                (int)(GameMain.GraphicsWidth * GameSettings.CurrentConfig.Graphics.LightMapScale),
                (int)(GameMain.GraphicsHeight * GameSettings.CurrentConfig.Graphics.LightMapScale), false, SurfaceFormat.Color, DepthFormat.None);
        }

        public void AddLight(LightSource light)
        {
            if (!lights.Contains(light)) { lights.Add(light); }
        }

        public void RemoveLight(LightSource light)
        {
            lights.Remove(light);
        }

        public void OnMapLoaded()
        {
            foreach (LightSource light in lights)
            {
                light.HullsUpToDate.Clear();
                light.NeedsRecalculation = true;
            }
        }

        private readonly List<LightSource> activeLights = new List<LightSource>(capacity: 100);
        private readonly List<LightSource> activeShadowCastingLights = new List<LightSource>(capacity: 100);

        public static int ActiveLightCount { get; private set; }

        public void Update(float deltaTime)
        {
            //wrap around if the timer gets very large, otherwise we'd start running into floating point accuracy issues
            time = (time + deltaTime) % 100000.0f;
            foreach (LightSource light in activeLights)
            {
                if (!light.Enabled) { continue; }
                light.Update(time);
            }
        }

        private sealed class RayCastTask
        {
            public LightSource LightSource;
            public Vector2 DrawPos;
            public float Rotation;

            public RayCastTask(LightSource lightSource, Vector2 drawPos, float rotation)
            {
                LightSource = lightSource;
                DrawPos = drawPos;
                Rotation = rotation;
            }

            public void Calculate()
            {
                LightSource.RayCastTask(DrawPos, Rotation);
            }
        }

        private static readonly object mutex = new object();

        public void AddRayCastTask(LightSource lightSource, Vector2 drawPos, float rotation)
        {
            lock (mutex)
            {
                if (pendingRayCasts.Any(p => p.LightSource == lightSource)) { return; }
                pendingRayCasts.Enqueue(new RayCastTask(lightSource, drawPos, rotation));
            }
        }

        private void UpdateRayCasts()
        {
            while (true)
            {
                lock (mutex)
                {
                    while (pendingRayCasts.Count > 0)
                    {
                        pendingRayCasts.Dequeue().Calculate();
                    }
                }
                Thread.Sleep(10);
            }
        }

        public void DebugDrawVertices(SpriteBatch spriteBatch)
        {
            foreach (LightSource light in lights)
            {
                if (!light.Enabled) { continue; }
                light.DebugDrawVertices(spriteBatch);
            }
        }

        /// <param name="backgroundObstructor">A render target that contains the structures that should obstruct lights in the background. If not given, damageable walls and hulls are rendered to obstruct the background lights.</param>
        public void RenderLightMap(GraphicsDevice graphics, SpriteBatch spriteBatch, Camera cam, RenderTarget2D backgroundObstructor = null)
        {
            if (!LightingEnabled) { return; }

            if (Math.Abs(currLightMapScale - GameSettings.CurrentConfig.Graphics.LightMapScale) > 0.01f)
            {
                //lightmap scale has changed -> recreate render targets
                CreateRenderTargets(graphics);
            }

            Matrix spriteBatchTransform = cam.Transform * Matrix.CreateScale(new Vector3(GameSettings.CurrentConfig.Graphics.LightMapScale, GameSettings.CurrentConfig.Graphics.LightMapScale, 1.0f));
            Matrix transform = cam.ShaderTransform
                * Matrix.CreateOrthographic(GameMain.GraphicsWidth, GameMain.GraphicsHeight, -1, 1) * 0.5f;

            bool highlightsVisible = UpdateHighlights(graphics, spriteBatch, spriteBatchTransform, cam);

            Rectangle viewRect = cam.WorldView;
            viewRect.Y -= cam.WorldView.Height;
            //check which lights need to be drawn
            recalculationCount = 0;
            activeLights.Clear();
            foreach (LightSource light in lights)
            {
                if (!light.Enabled) { continue; }
                if ((light.Color.A < 1 || light.Range < 1.0f) && !light.LightSourceParams.OverrideLightSpriteAlpha.HasValue) { continue; }

                if (light.ParentBody != null)
                {
                    light.ParentBody.UpdateDrawPosition();

                    Vector2 pos =  light.ParentBody.DrawPosition;
                    if (light.ParentSub != null) { pos -= light.ParentSub.DrawPosition; }
                    light.Position = pos;
                }

                //above the top boundary of the level (in an inactive respawn shuttle?)
                if (Level.IsPositionAboveLevel(light.WorldPosition)) { continue; }

                float range = light.LightSourceParams.TextureRange;
                if (light.LightSprite != null)
                {
                    float spriteRange = Math.Max(
                        light.LightSprite.size.X * light.SpriteScale.X * (0.5f + Math.Abs(light.LightSprite.RelativeOrigin.X - 0.5f)),
                        light.LightSprite.size.Y * light.SpriteScale.Y * (0.5f + Math.Abs(light.LightSprite.RelativeOrigin.Y - 0.5f)));

                    float targetSize = Math.Max(light.LightTextureTargetSize.X, light.LightTextureTargetSize.Y);
                    range = Math.Max(Math.Max(spriteRange, targetSize), range);
                }
                if (!MathUtils.CircleIntersectsRectangle(light.WorldPosition, range, viewRect)) { continue; }

                light.Priority = lightPriority(range, light);

                int i = 0;
                while (i < activeLights.Count && light.Priority < activeLights[i].Priority)
                {
                    i++;
                }
                activeLights.Insert(i, light);
            }
            ActiveLightCount = activeLights.Count;

            float lightPriority(float range, LightSource light)
            {
                return
                    range *
                    ((Character.Controlled?.Submarine != null && light.ParentSub == Character.Controlled?.Submarine) ? 2.0f : 1.0f) *
                    (light.CastShadows ? 10.0f : 1.0f) *
                    (light.LightSourceParams.OverrideLightSpriteAlpha ?? (light.Color.A / 255.0f)) *
                    light.PriorityMultiplier;
            }

            //find the lights with an active light volume
            activeShadowCastingLights.Clear();
            foreach (var activeLight in activeLights)
            {
                if (!activeLight.CastShadows) { continue; }
                if (activeLight.Range < 1.0f || activeLight.Color.A < 1 || activeLight.CurrentBrightness <= 0.0f) { continue; }
                activeShadowCastingLights.Add(activeLight);
            }

            //remove some lights with a light volume if there's too many of them
            if (activeShadowCastingLights.Count > GameSettings.CurrentConfig.Graphics.VisibleLightLimit && Screen.Selected is { IsEditor: false })
            {
                for (int i = GameSettings.CurrentConfig.Graphics.VisibleLightLimit; i < activeShadowCastingLights.Count; i++)
                {
                    activeLights.Remove(activeShadowCastingLights[i]);
                }
            }
            activeLights.Sort((l1, l2) => l1.LastRecalculationTime.CompareTo(l2.LastRecalculationTime));

            //draw light sprites attached to characters
            //render into a separate rendertarget using alpha blending (instead of on top of everything else with alpha blending)
            //to prevent the lights from showing through other characters or other light sprites attached to the same character
            //---------------------------------------------------------------------------------------------------
            graphics.SetRenderTarget(LimbLightMap);
            graphics.Clear(Color.Black);
            graphics.BlendState = BlendState.NonPremultiplied;
            spriteBatch.Begin(SpriteSortMode.BackToFront, BlendState.NonPremultiplied, transformMatrix: spriteBatchTransform);
            foreach (LightSource light in activeLights)
            {
                if (light.IsBackground || light.CurrentBrightness <= 0.0f) { continue; }
                //draw limb lights at this point, because they were skipped over previously to prevent them from being obstructed
                if (light.ParentBody?.UserData is Limb limb && !limb.Hide) { light.DrawSprite(spriteBatch, cam); }
            }
            spriteBatch.End();

            //draw background lights
            //---------------------------------------------------------------------------------------------------
            graphics.SetRenderTarget(LightMap);
            graphics.Clear(AmbientLight);
            graphics.BlendState = BlendState.Additive;
            spriteBatch.Begin(SpriteSortMode.Deferred, BlendState.Additive, transformMatrix: spriteBatchTransform);
            Level.Loaded?.BackgroundCreatureManager?.DrawLights(spriteBatch, cam);
            foreach (LightSource light in activeLights)
            {
                if (!light.IsBackground || light.CurrentBrightness <= 0.0f) { continue; }
                light.DrawLightVolume(spriteBatch, lightEffect, transform, recalculationCount < MaxLightVolumeRecalculationsPerFrame, ref recalculationCount);
                light.DrawSprite(spriteBatch, cam);
            }
            GameMain.ParticleManager.Draw(spriteBatch, true, null, Particles.ParticleBlendState.Additive);
            spriteBatch.End();

            //draw a black rectangle on hulls to hide background lights behind subs
            //---------------------------------------------------------------------------------------------------

            spriteBatch.Begin(SpriteSortMode.Deferred, BlendState.Opaque, transformMatrix: spriteBatchTransform);
            Dictionary<Hull, Rectangle> visibleHulls = GetVisibleHulls(cam);
            foreach (KeyValuePair<Hull, Rectangle> hull in visibleHulls)
            {
                GUI.DrawRectangle(spriteBatch,
                    new Vector2(hull.Value.X, -hull.Value.Y),
                    new Vector2(hull.Value.Width, hull.Value.Height),
                    hull.Key.AmbientLight == Color.TransparentBlack ? Color.Black : hull.Key.AmbientLight.Multiply(hull.Key.AmbientLight.A / 255.0f), true);
            }
            spriteBatch.End();

            spriteBatch.Begin(SpriteSortMode.Deferred, BlendState.Additive, transformMatrix: spriteBatchTransform);
            Vector3 glowColorHSV = ToolBox.RGBToHSV(AmbientLight);
            glowColorHSV.Z = Math.Max(glowColorHSV.Z, 0.4f);
            Color glowColor = ToolBoxCore.HSVToRGB(glowColorHSV.X, glowColorHSV.Y, glowColorHSV.Z);
            Vector2 glowSpriteSize = new Vector2(gapGlowTexture.Width, gapGlowTexture.Height);
            foreach (var gap in Gap.GapList)
            {
                if (gap.IsRoomToRoom || gap.Open <= 0.0f || gap.ConnectedWall == null) { continue; }

                float a = MathHelper.Lerp(0.5f, 1.0f,
                    PerlinNoise.GetPerlin((float)Timing.TotalTime * 0.05f, gap.GlowEffectT));

                float scale = MathHelper.Lerp(0.5f, 2.0f,
                    PerlinNoise.GetPerlin((float)Timing.TotalTime * 0.01f, gap.GlowEffectT));

                float rot = PerlinNoise.GetPerlin((float)Timing.TotalTime * 0.001f, gap.GlowEffectT) * MathHelper.TwoPi;

                Vector2 spriteScale = new Vector2(gap.Rect.Width, gap.Rect.Height) / glowSpriteSize;
                Vector2 drawPos = new Vector2(gap.DrawPosition.X, -gap.DrawPosition.Y);

                spriteBatch.Draw(gapGlowTexture,
                    drawPos,
                    null,
                    glowColor * a,
                    rot,
                    glowSpriteSize / 2,
                    scale: Math.Max(spriteScale.X, spriteScale.Y) * scale,
                    SpriteEffects.None,
                    layerDepth: 0);
            }
            spriteBatch.End();

            if (backgroundObstructor != null)
            {
                spriteBatch.Begin(SpriteSortMode.Immediate, BlendState.NonPremultiplied, SamplerState.LinearWrap, transformMatrix: Matrix.Identity, effect: GameMain.GameScreen.DamageEffect);
                spriteBatch.Draw(backgroundObstructor, new Rectangle(0, 0,
                    (int)(GameMain.GraphicsWidth * currLightMapScale), (int)(GameMain.GraphicsHeight * currLightMapScale)), Color.Black);
                spriteBatch.End();
            }
            else
            {
                GameMain.GameScreen.DamageEffect.CurrentTechnique = GameMain.GameScreen.DamageEffect.Techniques["StencilShaderSolidColor"];
                GameMain.GameScreen.DamageEffect.Parameters["solidColor"].SetValue(Color.Black.ToVector4());
                spriteBatch.Begin(SpriteSortMode.Immediate, BlendState.NonPremultiplied, SamplerState.LinearWrap, transformMatrix: spriteBatchTransform, effect: GameMain.GameScreen.DamageEffect);
                Submarine.DrawDamageable(spriteBatch, GameMain.GameScreen.DamageEffect);
                spriteBatch.End();
            }

            graphics.BlendState = BlendState.Additive;

            //draw the focused item and character to highlight them,
            //and light sprites (done before drawing the actual light volumes so we can make characters obstruct the highlights and sprites)
            //---------------------------------------------------------------------------------------------------
            spriteBatch.Begin(SpriteSortMode.Deferred, BlendState.Additive, transformMatrix: spriteBatchTransform);
            foreach (LightSource light in activeLights)
            {
                //don't draw limb lights at this point, they need to be drawn after lights have been obstructed by characters
                if (light.IsBackground || light.ParentBody?.UserData is Limb || light.CurrentBrightness <= 0.0f) { continue; }
                light.DrawSprite(spriteBatch, cam);
            }
            spriteBatch.End();

            if (highlightsVisible)
            {
                spriteBatch.Begin(SpriteSortMode.Deferred, BlendState.Additive);
                spriteBatch.Draw(HighlightMap, Vector2.Zero, Color.White);
                spriteBatch.End();
            }

            //draw characters to obstruct the highlighted items/characters and light sprites
            //---------------------------------------------------------------------------------------------------
            if (cam.Zoom > ObstructLightsBehindCharactersZoomThreshold)
            {
                SolidColorEffect.CurrentTechnique = SolidColorEffect.Techniques["SolidVertexColor"];
                spriteBatch.Begin(SpriteSortMode.Deferred, BlendState.NonPremultiplied, effect: SolidColorEffect, transformMatrix: spriteBatchTransform);
                DrawCharacters(spriteBatch, cam, drawDeformSprites: false);
                spriteBatch.End();

                DeformableSprite.Effect.CurrentTechnique = DeformableSprite.Effect.Techniques["DeformShaderSolidVertexColor"];
                DeformableSprite.Effect.CurrentTechnique.Passes[0].Apply();
                spriteBatch.Begin(SpriteSortMode.Deferred, BlendState.NonPremultiplied, transformMatrix: spriteBatchTransform);
                DrawCharacters(spriteBatch, cam, drawDeformSprites: true);
                spriteBatch.End();
            }

            static void DrawCharacters(SpriteBatch spriteBatch, Camera cam, bool drawDeformSprites)
            {
                foreach (Character character in Character.CharacterList)
                {
                    if (character.CurrentHull == null || !character.Enabled || !character.IsVisible || character.InvisibleTimer > 0.0f) { continue; }
                    if (Character.Controlled?.FocusedCharacter == character) { continue; }
                    Color lightColor = character.CurrentHull.AmbientLight == Color.TransparentBlack ?
                        Color.Black :
                        character.CurrentHull.AmbientLight.Multiply(character.CurrentHull.AmbientLight.A / 255.0f).Opaque();
                    foreach (Limb limb in character.AnimController.Limbs)
                    {
                        if (drawDeformSprites == (limb.DeformSprite == null)) { continue; }
                        limb.Draw(spriteBatch, cam, lightColor);
                    }
                    foreach (var heldItem in character.HeldItems)
                    {
                        heldItem.Draw(spriteBatch, editing: false, overrideColor: Color.Black);
                    }
                }
            }

            DeformableSprite.Effect.CurrentTechnique = DeformableSprite.Effect.Techniques["DeformShader"];
            graphics.BlendState = BlendState.Additive;

            //draw the actual light volumes, additive particles, hull ambient lights and the halo around the player
            //---------------------------------------------------------------------------------------------------
            spriteBatch.Begin(SpriteSortMode.Deferred, BlendState.Additive, transformMatrix: spriteBatchTransform);

            spriteBatch.Draw(LimbLightMap, new Rectangle(cam.WorldView.X, -cam.WorldView.Y, cam.WorldView.Width, cam.WorldView.Height), Color.White);

            foreach (ElectricalDischarger discharger in ElectricalDischarger.List)
            {
                discharger.DrawElectricity(spriteBatch);
            }

            foreach (LightSource light in activeLights)
            {
                if (light.IsBackground || light.CurrentBrightness <= 0.0f) { continue; }
                light.DrawLightVolume(spriteBatch, lightEffect, transform, recalculationCount < MaxLightVolumeRecalculationsPerFrame, ref recalculationCount);
            }

            if (ConnectionPanel.ShouldDebugDrawWiring)
            {
                foreach (MapEntity e in (Submarine.VisibleEntities ?? MapEntity.MapEntityList))
                {
                    if (e is Item item && !item.IsHidden && item.GetComponent<Wire>() is Wire wire)
                    {
                        wire.DebugDraw(spriteBatch, alpha: 0.4f);
                    }
                }
            }

            lightEffect.World = transform;

            GameMain.ParticleManager.Draw(spriteBatch, false, null, Particles.ParticleBlendState.Additive);

            if (Character.Controlled != null)
            {
                DrawHalo(Character.Controlled);
            }
            else
            {
                foreach (Character character in Character.CharacterList)
                {
                    if (character.Submarine == null || character.IsDead || !character.IsHuman) { continue; }
                    DrawHalo(character);
                }
            }

            void DrawHalo(Character character)
            {
                if (character == null || character.Removed) { return; }
                Vector2 haloDrawPos = character.DrawPosition;
                haloDrawPos.Y = -haloDrawPos.Y;

                //ambient light decreases the brightness of the halo (no need for a bright halo if the ambient light is bright enough)
                float ambientBrightness = (AmbientLight.R + AmbientLight.B + AmbientLight.G) / 255.0f / 3.0f;
                Color haloColor = Color.White.Multiply(0.3f - ambientBrightness);
                if (character.HasNightVision && character == Character.Controlled)
                {
                    haloColor.A = 180;
                }
                if (haloColor.A > 0)
                {
                    float scale = 512.0f / LightSource.LightTexture.Width;
                    if (character.HasNightVision && character == Character.Controlled)
                    {
                        scale = 5120.0f;
                    }
                    spriteBatch.Draw(
                        LightSource.LightTexture, haloDrawPos, null, haloColor, 0.0f,
                        new Vector2(LightSource.LightTexture.Width, LightSource.LightTexture.Height) / 2, scale, SpriteEffects.None, 0.0f);
                }
            }

            spriteBatch.End();

            //draw the actual light volumes, additive particles, hull ambient lights and the halo around the player
            //---------------------------------------------------------------------------------------------------

            graphics.SetRenderTarget(null);
            graphics.BlendState = BlendState.NonPremultiplied;
        }

        private readonly List<Entity> highlightedEntities = new List<Entity>();

        private bool UpdateHighlights(GraphicsDevice graphics, SpriteBatch spriteBatch, Matrix spriteBatchTransform, Camera cam)
        {
            if (GUI.DisableItemHighlights) { return false; }

            highlightedEntities.Clear();
            if (Character.Controlled != null && (!Character.Controlled.IsKeyDown(InputType.Aim) || Character.Controlled.HeldItems.Any(it => it.GetComponent<Sprayer>() == null)))
            {
                if (Character.Controlled.FocusedItem != null)
                {
                    highlightedEntities.Add(Character.Controlled.FocusedItem);
                }
                if (Character.Controlled.FocusedCharacter != null)
                {
                    highlightedEntities.Add(Character.Controlled.FocusedCharacter);
                }
                foreach (MapEntity me in MapEntity.HighlightedEntities)
                {
                    if (me is Item item && item != Character.Controlled.FocusedItem)
                    {
                        highlightedEntities.Add(item);
                    }
                }
            }
            if (highlightedEntities.Count == 0) { return false; }

            //draw characters in light blue first
            graphics.SetRenderTarget(HighlightMap);
            SolidColorEffect.CurrentTechnique = SolidColorEffect.Techniques["SolidColor"];
            SolidColorEffect.Parameters["color"].SetValue(Color.LightBlue.ToVector4());
            SolidColorEffect.CurrentTechnique.Passes[0].Apply();
            DeformableSprite.Effect.CurrentTechnique = DeformableSprite.Effect.Techniques["DeformShaderSolidColor"];
            DeformableSprite.Effect.Parameters["solidColor"].SetValue(Color.LightBlue.ToVector4());
            DeformableSprite.Effect.CurrentTechnique.Passes[0].Apply();
            spriteBatch.Begin(SpriteSortMode.Deferred, BlendState.Additive, samplerState: SamplerState.LinearWrap, effect: SolidColorEffect, transformMatrix: spriteBatchTransform);
            foreach (Entity highlighted in highlightedEntities)
            {
                if (highlighted is Item item)
                {
                    if (item.IconStyle != null && (item != Character.Controlled.FocusedItem || Character.Controlled.FocusedItem == null))
                    {
                        //wait until next pass
                    }
                    else
                    {
                        item.Draw(spriteBatch, false, true);
                    }
                }
                else if (highlighted is Character character)
                {
                    character.Draw(spriteBatch, cam);
                }
            }
            spriteBatch.End();

            //draw items with iconstyles in the style's color
            spriteBatch.Begin(SpriteSortMode.Immediate, BlendState.Additive, samplerState: SamplerState.LinearWrap, effect: SolidColorEffect, transformMatrix: spriteBatchTransform);
            foreach (Entity highlighted in highlightedEntities)
            {
                if (highlighted is Item item)
                {
                    if (item.IconStyle != null && (item != Character.Controlled.FocusedItem || Character.Controlled.FocusedItem == null))
                    {
                        SolidColorEffect.Parameters["color"].SetValue(item.IconStyle.Color.ToVector4());
                        SolidColorEffect.CurrentTechnique.Passes[0].Apply();
                        item.Draw(spriteBatch, false, true);
                    }
                }
            }
            spriteBatch.End();

            //draw characters in black with a bit of blur, leaving the white edges visible
            float phase = (float)(Math.Sin(Timing.TotalTime * 3.0f) + 1.0f) / 2.0f; //phase oscillates between 0 and 1
            Vector4 overlayColor = Color.Black.ToVector4() * MathHelper.Lerp(0.5f, 0.9f, phase);
            SolidColorEffect.Parameters["color"].SetValue(overlayColor);
            SolidColorEffect.CurrentTechnique = SolidColorEffect.Techniques["SolidColorBlur"];
            SolidColorEffect.CurrentTechnique.Passes[0].Apply();
            DeformableSprite.Effect.Parameters["solidColor"].SetValue(overlayColor);
            DeformableSprite.Effect.CurrentTechnique.Passes[0].Apply();
            spriteBatch.Begin(SpriteSortMode.Immediate, BlendState.NonPremultiplied, samplerState: SamplerState.LinearWrap, effect: SolidColorEffect, transformMatrix: spriteBatchTransform);
            foreach (Entity highlighted in highlightedEntities)
            {
                if (highlighted is Item item)
                {
                    SolidColorEffect.Parameters["blurDistance"].SetValue(0.02f);
                    item.Draw(spriteBatch, false, true);
                }
                else if (highlighted is Character character)
                {
                    SolidColorEffect.Parameters["blurDistance"].SetValue(0.05f);
                    character.Draw(spriteBatch, cam);
                }
            }
            spriteBatch.End();

            //raster pattern on top of everything
            spriteBatch.Begin(blendState: BlendState.NonPremultiplied, samplerState: SamplerState.LinearWrap);
            spriteBatch.Draw(highlightRaster,
                new Rectangle(0, 0, HighlightMap.Width, HighlightMap.Height),
                new Rectangle(0, 0, (int)(HighlightMap.Width / currLightMapScale * 0.5f), (int)(HighlightMap.Height / currLightMapScale * 0.5f)),
                Color.White * 0.5f);
            spriteBatch.End();

            DeformableSprite.Effect.CurrentTechnique = DeformableSprite.Effect.Techniques["DeformShader"];

            return true;
        }

        private readonly Dictionary<Hull, Rectangle> visibleHulls = new Dictionary<Hull, Rectangle>();
        private Dictionary<Hull, Rectangle> GetVisibleHulls(Camera cam)
        {
            visibleHulls.Clear();
            foreach (Hull hull in Hull.HullList)
            {
                if (hull.IsHidden) { continue; }
                var drawRect =
                    hull.Submarine == null ?
                    hull.Rect :
                    new Rectangle((int)(hull.Submarine.DrawPosition.X + hull.Rect.X), (int)(hull.Submarine.DrawPosition.Y + hull.Rect.Y), hull.Rect.Width, hull.Rect.Height);

                if (drawRect.Right < cam.WorldView.X || drawRect.X > cam.WorldView.Right ||
                    drawRect.Y - drawRect.Height > cam.WorldView.Y || drawRect.Y < cam.WorldView.Y - cam.WorldView.Height)
                {
                    continue;
                }
                visibleHulls.Add(hull, drawRect);
            }
            return visibleHulls;
        }
        
        private static readonly List<VertexPositionColor> ShadowVertices = new List<VertexPositionColor>(500);
        private static readonly List<VertexPositionTexture> PenumbraVertices = new List<VertexPositionTexture>(500);

        public void UpdateObstructVision(GraphicsDevice graphics, SpriteBatch spriteBatch, Camera cam, Vector2 lookAtPosition)
        {
            if ((!LosEnabled || LosMode == LosMode.None) && ObstructVisionAmount <= 0.0f) { return; }
            if (ViewTarget == null) { return; }

            graphics.SetRenderTarget(LosTexture);

            if (ObstructVisionAmount > 0.0f)
            {
                graphics.Clear(Color.Black);
                Vector2 diff = lookAtPosition - ViewTarget.WorldPosition;
                diff.Y = -diff.Y;
                if (diff.LengthSquared() > 20.0f * 20.0f) { losOffset = diff; }
                float rotation = MathUtils.VectorToAngle(losOffset);

                //the visible area stretches to the maximum when the cursor is this far from the character
                const float MaxOffset = 256.0f;
                //the magic numbers here are just based on experimentation
                float MinHorizontalScale = MathHelper.Lerp(3.5f, 1.5f, ObstructVisionAmount);
                float MaxHorizontalScale = MinHorizontalScale * 1.25f;
                float VerticalScale = MathHelper.Lerp(4.0f, 1.25f, ObstructVisionAmount);

                //Starting point and scale-based modifier that moves the point of origin closer to the edge of the texture if the player moves their mouse further away, or vice versa.
                float relativeOriginStartPosition = 0.1f; //Increasing this value moves the origin further behind the character
                float originStartPosition = visionCircle.Width * relativeOriginStartPosition * MinHorizontalScale;
                float relativeOriginLookAtPosModifier = -0.055f; //Increase this value increases how much the vision changes by moving the mouse
                float originLookAtPosModifier = visionCircle.Width * relativeOriginLookAtPosModifier;

                Vector2 scale = new Vector2(
                    MathHelper.Clamp(losOffset.Length() / MaxOffset, MinHorizontalScale, MaxHorizontalScale), VerticalScale);

                spriteBatch.Begin(SpriteSortMode.Deferred, transformMatrix: cam.Transform * Matrix.CreateScale(new Vector3(GameSettings.CurrentConfig.Graphics.LightMapScale, GameSettings.CurrentConfig.Graphics.LightMapScale, 1.0f)));
                spriteBatch.Draw(visionCircle, new Vector2(ViewTarget.WorldPosition.X, -ViewTarget.WorldPosition.Y), null, Color.White, rotation,
                    new Vector2(originStartPosition + (scale.X * originLookAtPosModifier), visionCircle.Height / 2), scale, SpriteEffects.None, 0.0f);
                spriteBatch.End();
            }
            else
            {
                graphics.Clear(Color.White);
            }


            //--------------------------------------

            if (LosEnabled && LosMode != LosMode.None && ViewTarget != null)
            {
                Vector2 pos = ViewTarget.DrawPosition;
                bool centeredOnHead = false;
                if (ViewTarget is Character character &&
                    character.AnimController?.GetLimb(LimbType.Head) is Limb head &&
                    !head.IsSevered && !head.Removed)
                {
                    pos = head.body.DrawPosition;
                    centeredOnHead = true;
                }

                Rectangle camView = new Rectangle(cam.WorldView.X, cam.WorldView.Y - cam.WorldView.Height, cam.WorldView.Width, cam.WorldView.Height);
                Matrix shadowTransform = cam.ShaderTransform
                    * Matrix.CreateOrthographic(GameMain.GraphicsWidth, GameMain.GraphicsHeight, -1, 1) * 0.5f;

                var convexHulls = ConvexHull.GetHullsInRange(ViewTarget.Position, cam.WorldView.Width * 0.75f, ViewTarget.Submarine);

                //make sure the head isn't peeking through any LOS segments, and if it is,
                //center the LOS on the character's collider instead
                if (centeredOnHead)
                {
                    foreach (var ch in convexHulls)
                    {
                        if (!ch.Enabled) { continue; }
                        Vector2 currentViewPos = pos;
                        Vector2 defaultViewPos = ViewTarget.DrawPosition;
                        if (ch.ParentEntity?.Submarine != null)
                        {
                            defaultViewPos -= ch.ParentEntity.Submarine.DrawPosition;
                            currentViewPos -= ch.ParentEntity.Submarine.DrawPosition;
                        }
                        //check if a line from the character's collider to the head intersects with the los segment (= head poking through it)
                        if (ch.LosIntersects(defaultViewPos, currentViewPos))
                        {
                            pos = ViewTarget.DrawPosition;
                        }
                    }
                }

                if (convexHulls != null)
                {
                    ShadowVertices.Clear();
                    PenumbraVertices.Clear();
                    foreach (ConvexHull convexHull in convexHulls)
                    {
                        if (!convexHull.Intersects(camView)) { continue; }

                        Vector2 relativeViewPos = pos;
                        if (convexHull.ParentEntity?.Submarine != null) 
                        { 
                            relativeViewPos -= convexHull.ParentEntity.Submarine.DrawPosition;
                        }

                        convexHull.CalculateLosVertices(relativeViewPos);

                        for (int i = 0; i < convexHull.ShadowVertexCount; i++)
                        {
                            ShadowVertices.Add(convexHull.ShadowVertices[i]);
                        }

                        for (int i = 0; i < convexHull.PenumbraVertexCount; i++)
                        {
                            PenumbraVertices.Add(convexHull.PenumbraVertices[i]);
                        }
                    }

                    if (ShadowVertices.Count > 0)
                    {
                        ConvexHull.shadowEffect.World = shadowTransform;
                        ConvexHull.shadowEffect.CurrentTechnique.Passes[0].Apply();
                        graphics.DrawUserPrimitives(PrimitiveType.TriangleList, ShadowVertices.ToArray(), 0, ShadowVertices.Count / 3, VertexPositionColor.VertexDeclaration);

                        if (PenumbraVertices.Count > 0)
                        {
                            ConvexHull.penumbraEffect.World = shadowTransform;
                            ConvexHull.penumbraEffect.CurrentTechnique.Passes[0].Apply();
                            graphics.DrawUserPrimitives(PrimitiveType.TriangleList, PenumbraVertices.ToArray(), 0, PenumbraVertices.Count / 3, VertexPositionTexture.VertexDeclaration);
                        }
                    }
                }
            }
            graphics.SetRenderTarget(null);
        }

        public void DebugDrawLos(SpriteBatch spriteBatch, Camera cam)
        {
            Vector2 pos = ViewTarget?.Position ?? cam.Position;
            spriteBatch.Begin(SpriteSortMode.Deferred, transformMatrix: cam.Transform);
            var convexHulls = ConvexHull.GetHullsInRange(pos, cam.WorldView.Width * 0.75f, ViewTarget?.Submarine);
            Rectangle camView = new Rectangle(cam.WorldView.X, cam.WorldView.Y - cam.WorldView.Height, cam.WorldView.Width, cam.WorldView.Height);
            foreach (ConvexHull convexHull in convexHulls)
            {
                if (!convexHull.Enabled || !convexHull.Intersects(camView)) { continue; }
                if (convexHull.ParentEntity is Structure { CastShadow: false }) { continue; }
                convexHull.DebugDraw(spriteBatch);
            }
            spriteBatch.End();
        }

        public void ClearLights()
        {
            activeLights.Clear();
            activeShadowCastingLights.Clear();
            lights.Clear();
        }
    }


    class CustomBlendStates
    {
        static CustomBlendStates()
        {
            Multiplicative = new BlendState
            {
                ColorSourceBlend = Blend.DestinationColor,
                ColorDestinationBlend = Blend.SourceColor,
                ColorBlendFunction = BlendFunction.Add
            };
        }
        public static BlendState Multiplicative { get; private set; }
    }

}

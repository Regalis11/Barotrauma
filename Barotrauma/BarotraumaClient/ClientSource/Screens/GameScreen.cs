using Barotrauma.Extensions;
using Barotrauma.Lights;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using System;
using System.Diagnostics;
using System.Linq;
using System.Transactions;

namespace Barotrauma
{
    partial class GameScreen : Screen
    {
        private RenderTarget2D renderTargetBackground;
        private RenderTarget2D renderTarget;
        private RenderTarget2D renderTargetWater;
        private RenderTarget2D renderTargetFinal;

        private RenderTarget2D renderTargetDamageable;

        public readonly Effect DamageEffect;
        private readonly Texture2D damageStencil;
        private readonly Texture2D distortTexture;        

        private float fadeToBlackState;

        public Effect PostProcessEffect { get; private set; }
        public Effect GradientEffect { get; private set; }
        public Effect GrainEffect { get; private set; }
        public Effect ThresholdTintEffect { get; private set; }
        public Effect BlueprintEffect { get; set; }

        public GameScreen(GraphicsDevice graphics)
        {
            cam = new Camera();
            cam.Translate(new Vector2(-10.0f, 50.0f));

            CreateRenderTargets(graphics);
            GameMain.Instance.ResolutionChanged += () =>
            {
                CreateRenderTargets(graphics);
            };

            //var blurEffect = LoadEffect("Effects/blurshader");
            DamageEffect = EffectLoader.Load("Effects/damageshader");
            PostProcessEffect = EffectLoader.Load("Effects/postprocess");
            GradientEffect = EffectLoader.Load("Effects/gradientshader");
            GrainEffect = EffectLoader.Load("Effects/grainshader");
            ThresholdTintEffect = EffectLoader.Load("Effects/thresholdtint");
            BlueprintEffect = EffectLoader.Load("Effects/blueprintshader");

            damageStencil = TextureLoader.FromFile("Content/Map/walldamage.png");
            DamageEffect.Parameters["xStencil"].SetValue(damageStencil);
            DamageEffect.Parameters["aMultiplier"].SetValue(50.0f);
            DamageEffect.Parameters["cMultiplier"].SetValue(200.0f);

            distortTexture = TextureLoader.FromFile("Content/Effects/distortnormals.png");
            PostProcessEffect.Parameters["xDistortTexture"].SetValue(distortTexture);
        }

        private void CreateRenderTargets(GraphicsDevice graphics)
        {
            renderTarget?.Dispose();
            renderTargetBackground?.Dispose();
            renderTargetWater?.Dispose();
            renderTargetFinal?.Dispose();
            renderTarget = new RenderTarget2D(graphics, GameMain.GraphicsWidth, GameMain.GraphicsHeight, false, SurfaceFormat.Color, DepthFormat.None, 0, RenderTargetUsage.PreserveContents);
            renderTargetBackground = new RenderTarget2D(graphics, GameMain.GraphicsWidth, GameMain.GraphicsHeight);
            renderTargetWater = new RenderTarget2D(graphics, GameMain.GraphicsWidth, GameMain.GraphicsHeight);
            renderTargetFinal = new RenderTarget2D(graphics, GameMain.GraphicsWidth, GameMain.GraphicsHeight, false, SurfaceFormat.Color, DepthFormat.None);
            renderTargetDamageable = new RenderTarget2D(graphics, GameMain.GraphicsWidth, GameMain.GraphicsHeight, false, SurfaceFormat.Color, DepthFormat.None);
        }

        public override void AddToGUIUpdateList()
        {
            if (Character.Controlled != null)
            {
                if (Character.Controlled.SelectedItem is { } selectedItem && Character.Controlled.CanInteractWith(selectedItem))
                {
                    selectedItem.AddToGUIUpdateList();
                }
                if (Character.Controlled.SelectedSecondaryItem is { } selectedSecondaryItem && Character.Controlled.CanInteractWith(selectedSecondaryItem))
                {
                    selectedSecondaryItem.AddToGUIUpdateList();
                }
                if (Character.Controlled.Inventory != null)
                {
                    foreach (Item item in Character.Controlled.Inventory.AllItems)
                    {
                        if (Character.Controlled.HasEquippedItem(item))
                        {
                            item.AddToGUIUpdateList();
                        }
                    }
                }
            }
            GameMain.GameSession?.AddToGUIUpdateList();
            Character.AddAllToGUIUpdateList();
        }
        
        public override void Draw(double deltaTime, GraphicsDevice graphics, SpriteBatch spriteBatch)
        {
            cam.UpdateTransform(true);
            Submarine.CullEntities(cam);

            foreach (Character c in Character.CharacterList)
            {
                c.AnimController.Limbs.ForEach(l => l.body.UpdateDrawPosition());
                bool wasVisible = c.IsVisible;
                c.DoVisibilityCheck(cam);
                if (c.IsVisible != wasVisible)
                {
                    foreach (var limb in c.AnimController.Limbs)
                    {
                        if (limb.LightSource is LightSource light)
                        {
                            light.Enabled = c.IsVisible;
                        }
                    }
                }
            }

            Stopwatch sw = new Stopwatch();
            sw.Start();

            DrawMap(graphics, spriteBatch, deltaTime);

            sw.Stop();
            GameMain.PerformanceCounter.AddElapsedTicks("Draw:Map", sw.ElapsedTicks);
            sw.Restart();

            spriteBatch.Begin(SpriteSortMode.Deferred, null, GUI.SamplerState, null, GameMain.ScissorTestEnable);

            if (Character.Controlled != null && cam != null) { Character.Controlled.DrawHUD(spriteBatch, cam); }

            if (GameMain.GameSession != null) { GameMain.GameSession.Draw(spriteBatch); }

            if (Character.Controlled == null && !GUI.DisableHUD)
            {
                DrawPositionIndicators(spriteBatch);
            }

            if (!GUI.DisableHUD)
            {
                foreach (Character c in Character.CharacterList)
                {
                    c.DrawGUIMessages(spriteBatch, cam);
                }
            }

            GUI.Draw(cam, spriteBatch);

            spriteBatch.End();

            sw.Stop();
            GameMain.PerformanceCounter.AddElapsedTicks("Draw:HUD", sw.ElapsedTicks);
            sw.Restart();
        }
        
        private void DrawPositionIndicators(SpriteBatch spriteBatch)
        {
            Sprite subLocationSprite = GUIStyle.SubLocationIcon.Value?.Sprite;
            Sprite shuttleSprite = GUIStyle.ShuttleIcon.Value?.Sprite;
            Sprite wreckSprite = GUIStyle.WreckIcon.Value?.Sprite;
            Sprite caveSprite = GUIStyle.CaveIcon.Value?.Sprite;
            Sprite outpostSprite = GUIStyle.OutpostIcon.Value?.Sprite;
            Sprite ruinSprite = GUIStyle.RuinIcon.Value?.Sprite;
            Sprite enemySprite = GUIStyle.EnemyIcon.Value?.Sprite;
            Sprite corpseSprite = GUIStyle.CorpseIcon.Value?.Sprite;
            Sprite beaconSprite = GUIStyle.BeaconIcon.Value?.Sprite;
            
            for (int i = 0; i < Submarine.MainSubs.Length; i++)
            {
                if (Submarine.MainSubs[i] == null) { continue; }
                if (Level.Loaded != null && Submarine.MainSubs[i].WorldPosition.Y < Level.MaxEntityDepth) { continue; }
                
                Vector2 position = Submarine.MainSubs[i].SubBody != null ? Submarine.MainSubs[i].WorldPosition : Submarine.MainSubs[i].HiddenSubPosition;
                
                Color indicatorColor = i == 0 ? Color.LightBlue * 0.5f : GUIStyle.Red * 0.5f;
                Sprite displaySprite = Submarine.MainSubs[i].Info.HasTag(SubmarineTag.Shuttle) ? shuttleSprite : Submarine.MainSubs[i].Info.Class.LocationIndicator ?? subLocationSprite;
                if (displaySprite != null)
                {
                    GUI.DrawIndicator(
                        spriteBatch, position, cam,
                        Math.Max(Submarine.MainSubs[i].Borders.Width, Submarine.MainSubs[i].Borders.Height),
                        displaySprite, indicatorColor);
                }
            }
            
            if (!GameMain.DevMode) { return;}
            
            if (Level.Loaded != null)
            {
                foreach (Level.Cave cave in Level.Loaded.Caves)
                {
                    Vector2 position = cave.StartPos.ToVector2();
                    
                    Color indicatorColor = Color.Yellow * 0.5f;
                    if (caveSprite != null)
                    {
                        GUI.DrawIndicator(
                            spriteBatch, position, cam, hideDist: 3000f,
                            caveSprite, indicatorColor);
                    }
                }
            }
            
            foreach (Submarine submarine in Submarine.Loaded)
            {
                if (Submarine.MainSubs.Contains(submarine)) { continue; }
                
                Vector2 position = submarine.WorldPosition;

                Color teamColorIndicator = submarine.TeamID switch
                {
                    CharacterTeamType.Team1 => Color.LightBlue * 0.5f,
                    CharacterTeamType.Team2 => GUIStyle.Red * 0.5f,
                    CharacterTeamType.FriendlyNPC => GUIStyle.Yellow * 0.5f,
                    _ => Color.Green * 0.5f
                };
                
                Color indicatorColor = submarine.Info.Type switch
                {
                    SubmarineType.Outpost => Color.LightGreen,
                    SubmarineType.Wreck => Color.SaddleBrown,
                    SubmarineType.BeaconStation => Color.Azure,
                    SubmarineType.Ruin => Color.Purple,
                    _ => teamColorIndicator
                };
                
                Sprite displaySprite = submarine.Info.Type switch
                {
                    SubmarineType.Outpost => outpostSprite,
                    SubmarineType.Wreck => wreckSprite,
                    SubmarineType.BeaconStation => beaconSprite,
                    SubmarineType.Ruin => ruinSprite,
                    _ => submarine.Info.Class?.LocationIndicator ?? subLocationSprite
                };

                indicatorColor *= displaySprite.Color.A / 255f;
                
                if (displaySprite != null)
                {
                    GUI.DrawIndicator(
                        spriteBatch, position, cam, hideDist: Math.Max(submarine.Borders.Width, submarine.Borders.Height),
                        displaySprite, indicatorColor);
                }
            }
            
            // markers for all enemies and corpses
            foreach (Character character in Character.CharacterList)
            {
                Vector2 position = character.WorldPosition;
                Color indicatorColor = Color.DarkRed * 0.5f;
                if (character.IsDead) { indicatorColor = Color.DarkGray * 0.5f; }
                
                if (character.TeamID != CharacterTeamType.None) { continue;}
                
                Sprite displaySprite = character.IsDead ? corpseSprite : enemySprite;
                
                if (displaySprite != null)
                {
                    GUI.DrawIndicator(
                        spriteBatch, position, cam, hideDist: 3000f,
                        displaySprite, indicatorColor);
                }
            }
        }
        
        public void DrawMap(GraphicsDevice graphics, SpriteBatch spriteBatch, double deltaTime)
        {
            foreach (Submarine sub in Submarine.Loaded)
            {
                sub.UpdateTransform();
            }

            GameMain.ParticleManager.UpdateTransforms();

            Stopwatch sw = new Stopwatch();
            sw.Start();

            if (Character.Controlled != null && 
                (Character.Controlled.ViewTarget == Character.Controlled || Character.Controlled.ViewTarget == null))
            {
                GameMain.LightManager.ObstructVisionAmount = Character.Controlled.ObstructVisionAmount;
            }
            else
            {
                GameMain.LightManager.ObstructVisionAmount = 0.0f;
            }

            GameMain.LightManager.UpdateObstructVision(graphics, spriteBatch, cam, Character.Controlled?.CursorWorldPosition ?? Vector2.Zero);

            sw.Stop();
            GameMain.PerformanceCounter.AddElapsedTicks("Draw:Map:LOS", sw.ElapsedTicks);
            sw.Restart();


            static bool IsFromOutpostDrawnBehindSubs(Entity e)
                => e.Submarine is { Info.OutpostGenerationParams.DrawBehindSubs: true };

            //------------------------------------------------------------------------
            graphics.SetRenderTarget(renderTarget);
            graphics.Clear(Color.Transparent);
            //Draw background structures and wall background sprites 
            //(= the background texture that's revealed when a wall is destroyed) into the background render target
            //These will be visible through the LOS effect.
            spriteBatch.Begin(SpriteSortMode.BackToFront, BlendState.NonPremultiplied, depthStencilState: DepthStencilState.None, transformMatrix: cam.Transform);
            Submarine.DrawBack(spriteBatch, false, e => e is Structure s && (e.SpriteDepth >= 0.9f || s.Prefab.BackgroundSprite != null) && !IsFromOutpostDrawnBehindSubs(e));
            Submarine.DrawPaintedColors(spriteBatch, false);
            spriteBatch.End();

            sw.Stop();
            GameMain.PerformanceCounter.AddElapsedTicks("Draw:Map:BackStructures", sw.ElapsedTicks);
            sw.Restart();

            graphics.SetRenderTarget(renderTargetDamageable);
            graphics.Clear(Color.Transparent);
            DamageEffect.CurrentTechnique = DamageEffect.Techniques["StencilShader"];
            spriteBatch.Begin(SpriteSortMode.BackToFront, BlendState.NonPremultiplied, SamplerState.LinearWrap, effect: DamageEffect, transformMatrix: cam.Transform);
            Submarine.DrawDamageable(spriteBatch, DamageEffect, false);
            spriteBatch.End();

            sw.Stop();
            GameMain.PerformanceCounter.AddElapsedTicks("Draw:Map:FrontDamageable", sw.ElapsedTicks);
            sw.Restart();

            graphics.SetRenderTarget(null);
            GameMain.LightManager.RenderLightMap(graphics, spriteBatch, cam, renderTargetDamageable);

            sw.Stop();
            GameMain.PerformanceCounter.AddElapsedTicks("Draw:Map:Lighting", sw.ElapsedTicks);
            sw.Restart();

            //------------------------------------------------------------------------
            graphics.SetRenderTarget(renderTargetBackground);
            if (Level.Loaded == null)
            {
                graphics.Clear(new Color(11, 18, 26, 255));
            }
            else
            {
                //graphics.Clear(new Color(255, 255, 255, 255));
                Level.Loaded.DrawBack(graphics, spriteBatch, cam);
            }

            spriteBatch.Begin(SpriteSortMode.BackToFront, BlendState.NonPremultiplied, depthStencilState: DepthStencilState.None, transformMatrix: cam.Transform);
            Submarine.DrawBack(spriteBatch, false, e => e is Structure s && (e.SpriteDepth >= 0.9f || s.Prefab.BackgroundSprite != null) && IsFromOutpostDrawnBehindSubs(e));
            spriteBatch.End();

            //draw alpha blended particles that are in water and behind subs
#if LINUX || OSX
			spriteBatch.Begin(SpriteSortMode.Deferred, BlendState.NonPremultiplied, depthStencilState: DepthStencilState.None, transformMatrix: cam.Transform);
#else
            spriteBatch.Begin(SpriteSortMode.Deferred, BlendState.NonPremultiplied, depthStencilState: DepthStencilState.None, transformMatrix: cam.Transform);
#endif
			GameMain.ParticleManager.Draw(spriteBatch, true, false, Particles.ParticleBlendState.AlphaBlend);
			spriteBatch.End();
            
            //draw additive particles that are in water and behind subs
            spriteBatch.Begin(SpriteSortMode.Deferred, BlendState.Additive, depthStencilState: DepthStencilState.None, transformMatrix: cam.Transform);
            GameMain.ParticleManager.Draw(spriteBatch, true, false, Particles.ParticleBlendState.Additive);
            spriteBatch.End();
            spriteBatch.Begin(SpriteSortMode.Deferred, BlendState.NonPremultiplied, depthStencilState: DepthStencilState.None);
            spriteBatch.Draw(renderTarget, new Rectangle(0, 0, GameMain.GraphicsWidth, GameMain.GraphicsHeight), Color.White);
            spriteBatch.End();

            sw.Stop();
            GameMain.PerformanceCounter.AddElapsedTicks("Draw:Map:BackLevel", sw.ElapsedTicks);
            sw.Restart();

            //----------------------------------------------------------------------------

            //Start drawing to the normal render target (stuff that can't be seen through the LOS effect)
            graphics.SetRenderTarget(renderTarget);

            graphics.BlendState = BlendState.NonPremultiplied;
            graphics.SamplerStates[0] = SamplerState.LinearWrap;
            GraphicsQuad.UseBasicEffect(renderTargetBackground);
            GraphicsQuad.Render();

            //Draw the rest of the structures, characters and front structures
            spriteBatch.Begin(SpriteSortMode.BackToFront, BlendState.NonPremultiplied, depthStencilState: DepthStencilState.None, transformMatrix: cam.Transform);
            Submarine.DrawBack(spriteBatch, false, e => e is not Structure || e.SpriteDepth < 0.9f);
            DrawCharacters(deformed: false, firstPass: true);
            spriteBatch.End();

            sw.Stop();
            GameMain.PerformanceCounter.AddElapsedTicks("Draw:Map:BackCharactersItems", sw.ElapsedTicks);
            sw.Restart();

            spriteBatch.Begin(SpriteSortMode.Deferred, BlendState.NonPremultiplied, depthStencilState: DepthStencilState.None, transformMatrix: cam.Transform);
            DrawCharacters(deformed: true, firstPass: true);
            DrawCharacters(deformed: true, firstPass: false);
            DrawCharacters(deformed: false, firstPass: false);
            spriteBatch.End();

            void DrawCharacters(bool deformed, bool firstPass)
            {
                //backwards order to render the most recently spawned characters in front (characters spawned later have a larger sprite depth)
                for (int i = Character.CharacterList.Count - 1; i >= 0; i--)
                {
                    Character c = Character.CharacterList[i];
                    if (!c.IsVisible) { continue; }
                    if (c.Params.DrawLast == firstPass) { continue; }
                    if (deformed)
                    {
                        if (c.AnimController.Limbs.All(l => l.DeformSprite == null)) { continue; }
                    }
                    else
                    {
                        if (c.AnimController.Limbs.Any(l => l.DeformSprite != null)) { continue; }
                    }
                    c.Draw(spriteBatch, Cam);
                }
            }

            sw.Stop();
            GameMain.PerformanceCounter.AddElapsedTicks("Draw:Map:DeformableCharacters", sw.ElapsedTicks);
            sw.Restart();

            Level.Loaded?.DrawFront(spriteBatch, cam);

            sw.Stop();
            GameMain.PerformanceCounter.AddElapsedTicks("Draw:Map:FrontLevel", sw.ElapsedTicks);
            sw.Restart();

            //draw the rendertarget and particles that are only supposed to be drawn in water into renderTargetWater
            graphics.SetRenderTarget(renderTargetWater);

            graphics.BlendState = BlendState.Opaque;
            graphics.SamplerStates[0] = SamplerState.LinearWrap;
            GraphicsQuad.UseBasicEffect(renderTarget);
            GraphicsQuad.Render();

            //draw alpha blended particles that are inside a sub
            spriteBatch.Begin(SpriteSortMode.Deferred, BlendState.NonPremultiplied, depthStencilState: DepthStencilState.DepthRead, transformMatrix: cam.Transform);
			GameMain.ParticleManager.Draw(spriteBatch, true, true, Particles.ParticleBlendState.AlphaBlend);
			spriteBatch.End();

			graphics.SetRenderTarget(renderTarget);

			//draw alpha blended particles that are not in water
            spriteBatch.Begin(SpriteSortMode.Deferred, BlendState.NonPremultiplied, depthStencilState: DepthStencilState.DepthRead, transformMatrix: cam.Transform);
			GameMain.ParticleManager.Draw(spriteBatch, false, null, Particles.ParticleBlendState.AlphaBlend);
			spriteBatch.End();

			//draw additive particles that are not in water
			spriteBatch.Begin(SpriteSortMode.Deferred, BlendState.Additive, depthStencilState: DepthStencilState.None, transformMatrix: cam.Transform);
			GameMain.ParticleManager.Draw(spriteBatch, false, null, Particles.ParticleBlendState.Additive);
			spriteBatch.End();
            
            graphics.DepthStencilState = DepthStencilState.DepthRead;
            graphics.SetRenderTarget(renderTargetFinal);
                        
            WaterRenderer.Instance.ResetBuffers();
            Hull.UpdateVertices(cam, WaterRenderer.Instance);			
            WaterRenderer.Instance.RenderWater(spriteBatch, renderTargetWater, cam);
            WaterRenderer.Instance.RenderAir(graphics, cam, renderTarget, Cam.ShaderTransform);
            graphics.DepthStencilState = DepthStencilState.None;

            sw.Stop();
            GameMain.PerformanceCounter.AddElapsedTicks("Draw:Map:FrontParticles", sw.ElapsedTicks);
            sw.Restart();

            GraphicsQuad.UseBasicEffect(renderTargetDamageable);
            GraphicsQuad.Render();

            spriteBatch.Begin(SpriteSortMode.BackToFront, BlendState.NonPremultiplied, depthStencilState: DepthStencilState.None, transformMatrix: cam.Transform);
            Submarine.DrawFront(spriteBatch, false, null);
            spriteBatch.End();

            sw.Stop();
            GameMain.PerformanceCounter.AddElapsedTicks("Draw:Map:FrontStructuresItems", sw.ElapsedTicks);
            sw.Restart();

            //draw additive particles that are inside a sub
            spriteBatch.Begin(SpriteSortMode.Deferred, BlendState.Additive, depthStencilState: DepthStencilState.Default, transformMatrix: cam.Transform);
			GameMain.ParticleManager.Draw(spriteBatch, true, true, Particles.ParticleBlendState.Additive);
            foreach (var discharger in Items.Components.ElectricalDischarger.List)
            {
                discharger.DrawElectricity(spriteBatch);
            }
            spriteBatch.End();
			if (GameMain.LightManager.LightingEnabled)
			{
                graphics.DepthStencilState = DepthStencilState.None;
                graphics.SamplerStates[0] = SamplerState.LinearWrap;
                graphics.BlendState = CustomBlendStates.Multiplicative;
                GraphicsQuad.UseBasicEffect(GameMain.LightManager.LightMap);
                GraphicsQuad.Render();
            }

			spriteBatch.Begin(SpriteSortMode.Deferred, BlendState.NonPremultiplied, SamplerState.LinearWrap, depthStencilState: DepthStencilState.None, transformMatrix: cam.Transform);
            foreach (Character c in Character.CharacterList)
            {
                c.DrawFront(spriteBatch, cam);
            }

            GameMain.LightManager.DebugDrawVertices(spriteBatch);

            Level.Loaded?.DrawDebugOverlay(spriteBatch, cam);            
            if (GameMain.DebugDraw)
            {
                MapEntity.MapEntityList.ForEach(me => me.AiTarget?.Draw(spriteBatch));
                Character.CharacterList.ForEach(c => c.AiTarget?.Draw(spriteBatch));
                if (GameMain.GameSession?.EventManager != null)
                {
                    GameMain.GameSession.EventManager.DebugDraw(spriteBatch);
                }
            }
            spriteBatch.End();

            sw.Stop();
            GameMain.PerformanceCounter.AddElapsedTicks("Draw:Map:FrontMisc", sw.ElapsedTicks);
            sw.Restart();

            if (GameMain.LightManager.LosEnabled && GameMain.LightManager.LosMode != LosMode.None && Lights.LightManager.ViewTarget != null)
            {
                GameMain.LightManager.LosEffect.CurrentTechnique = GameMain.LightManager.LosEffect.Techniques["LosShader"];

                GameMain.LightManager.LosEffect.Parameters["blurDistance"].SetValue(0.005f);
                GameMain.LightManager.LosEffect.Parameters["xTexture"].SetValue(renderTargetBackground);
                GameMain.LightManager.LosEffect.Parameters["xLosTexture"].SetValue(GameMain.LightManager.LosTexture);
                GameMain.LightManager.LosEffect.Parameters["xLosAlpha"].SetValue(GameMain.LightManager.LosAlpha);

                Color losColor;
                if (GameMain.LightManager.LosMode == LosMode.Transparent)
                {
                    //convert the los color to HLS and make sure the luminance of the color is always the same
                    //as the luminance of the ambient light color
                    float r = Character.Controlled?.CharacterHealth == null ?
                        0.0f : Math.Min(Character.Controlled.CharacterHealth.DamageOverlayTimer * 0.5f, 0.5f);
                    Vector3 ambientLightHls = GameMain.LightManager.AmbientLight.RgbToHLS();
                    Vector3 losColorHls = Color.Lerp(GameMain.LightManager.AmbientLight, Color.Red, r).RgbToHLS();
                    losColorHls.Y = ambientLightHls.Y;
                    losColor = ToolBox.HLSToRGB(losColorHls);
                }
                else
                {
                    losColor = Color.Black;
                }

                GameMain.LightManager.LosEffect.Parameters["xColor"].SetValue(losColor.ToVector4());

                graphics.BlendState = BlendState.NonPremultiplied;
                graphics.SamplerStates[0] = SamplerState.PointClamp;
                graphics.SamplerStates[1] = SamplerState.PointClamp;
                GameMain.LightManager.LosEffect.CurrentTechnique.Passes[0].Apply();
                GraphicsQuad.Render();
                graphics.SamplerStates[0] = SamplerState.LinearWrap;
                graphics.SamplerStates[1] = SamplerState.LinearWrap;
            }

            if (Character.Controlled is { } character)
            {
                float grainStrength = character.GrainStrength;
                Rectangle screenRect = new Rectangle(0, 0, GameMain.GraphicsWidth, GameMain.GraphicsHeight);
                spriteBatch.Begin(SpriteSortMode.Deferred, BlendState.NonPremultiplied, effect: GrainEffect);
                GUI.DrawRectangle(spriteBatch, screenRect, Color.White, isFilled: true);
                GrainEffect.Parameters["seed"].SetValue(Rand.Range(0f, 1f, Rand.RandSync.Unsynced));
                GrainEffect.Parameters["intensity"].SetValue(grainStrength);
                GrainEffect.Parameters["grainColor"].SetValue(character.GrainColor.ToVector4());
                spriteBatch.End();
            }

            graphics.SetRenderTarget(null);

            float BlurStrength = 0.0f;
            float DistortStrength = 0.0f;
            Vector3 chromaticAberrationStrength = GameSettings.CurrentConfig.Graphics.ChromaticAberration ?
                new Vector3(-0.02f, -0.01f, 0.0f) : Vector3.Zero;

            if (Level.Loaded?.Renderer != null)
            {
                chromaticAberrationStrength += new Vector3(-0.03f, -0.015f, 0.0f) * Level.Loaded.Renderer.ChromaticAberrationStrength;
            }

            if (Character.Controlled != null)
            {
                BlurStrength = Character.Controlled.BlurStrength * 0.005f;
                DistortStrength = Character.Controlled.DistortStrength;
                if (GameSettings.CurrentConfig.Graphics.RadialDistortion)
                {
                    chromaticAberrationStrength -= Vector3.One * Character.Controlled.RadialDistortStrength;
                }
                chromaticAberrationStrength += new Vector3(-0.03f, -0.015f, 0.0f) * Character.Controlled.ChromaticAberrationStrength;
            }
            else
            {
                BlurStrength = 0.0f;
                DistortStrength = 0.0f;
            }

            string postProcessTechnique = "";
            if (BlurStrength > 0.0f)
            {
                postProcessTechnique += "Blur";
                PostProcessEffect.Parameters["blurDistance"].SetValue(BlurStrength);
            }
            if (chromaticAberrationStrength != Vector3.Zero)
            {
                postProcessTechnique += "ChromaticAberration";
                PostProcessEffect.Parameters["chromaticAberrationStrength"].SetValue(chromaticAberrationStrength);
            }
            if (DistortStrength > 0.0f)
            {
                postProcessTechnique += "Distort";
                PostProcessEffect.Parameters["distortScale"].SetValue(Vector2.One * DistortStrength);
                PostProcessEffect.Parameters["distortUvOffset"].SetValue(WaterRenderer.Instance.WavePos * 0.001f);
            }

            graphics.BlendState = BlendState.Opaque;
            graphics.SamplerStates[0] = SamplerState.LinearClamp;
            graphics.DepthStencilState = DepthStencilState.None;
            if (string.IsNullOrEmpty(postProcessTechnique))
            {
                GraphicsQuad.UseBasicEffect(renderTargetFinal);
            }
            else
            {
                PostProcessEffect.Parameters["MatrixTransform"].SetValue(Matrix.Identity);
                PostProcessEffect.Parameters["xTexture"].SetValue(renderTargetFinal);
                PostProcessEffect.CurrentTechnique = PostProcessEffect.Techniques[postProcessTechnique];
                PostProcessEffect.CurrentTechnique.Passes[0].Apply();
            }
            GraphicsQuad.Render();

            Character.DrawSpeechBubbles(spriteBatch, cam);

            if (fadeToBlackState > 0.0f)
            {
                spriteBatch.Begin(SpriteSortMode.Deferred);
                GUI.DrawRectangle(spriteBatch, new Rectangle(0, 0, GameMain.GraphicsWidth, GameMain.GraphicsHeight), Color.Lerp(Color.TransparentBlack, Color.Black, fadeToBlackState), isFilled: true);
                spriteBatch.End();
            }

            if (GameMain.LightManager.DebugLos)
            {
                GameMain.LightManager.DebugDrawLos(spriteBatch, cam);
            }

            sw.Stop();
            GameMain.PerformanceCounter.AddElapsedTicks("Draw:Map:PostProcess", sw.ElapsedTicks);
            sw.Restart();
        }

        partial void UpdateProjSpecific(double deltaTime)
        {
            if (ConversationAction.FadeScreenToBlack)
            {
                fadeToBlackState = Math.Min(fadeToBlackState + (float)deltaTime, 1.0f);
            }
            else
            {
                fadeToBlackState = Math.Max(fadeToBlackState - (float)deltaTime, 0.0f);
            }

            if (!PlayerInput.PrimaryMouseButtonHeld())
            {
                Inventory.DraggingSlot = null;
                Inventory.DraggingItems.Clear();
            }
        }
    }
}

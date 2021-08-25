using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Content;
using Microsoft.Xna.Framework.Graphics;
using System;
using FarseerPhysics;
using System.Diagnostics;
using System.Linq;
using Barotrauma.Extensions;

namespace Barotrauma
{
    partial class GameScreen : Screen
    {
        private RenderTarget2D renderTargetBackground;
        private RenderTarget2D renderTarget;
        private RenderTarget2D renderTargetWater;
        private RenderTarget2D renderTargetFinal;

        private Effect damageEffect;
        private Texture2D damageStencil;
        private Texture2D distortTexture;        

        private float fadeToBlackState;

        public Effect PostProcessEffect { get; private set; }
        public Effect GradientEffect { get; private set; }
        public Effect GrainEffect { get; private set; }

        public GameScreen(GraphicsDevice graphics, ContentManager content)
        {
            cam = new Camera();
            cam.Translate(new Vector2(-10.0f, 50.0f));

            CreateRenderTargets(graphics);
            GameMain.Instance.ResolutionChanged += () =>
            {
                CreateRenderTargets(graphics);
            };

#if LINUX || OSX
            //var blurEffect = content.Load<Effect>("Effects/blurshader_opengl");
            damageEffect = content.Load<Effect>("Effects/damageshader_opengl");
            PostProcessEffect = content.Load<Effect>("Effects/postprocess_opengl");
            GradientEffect = content.Load<Effect>("Effects/gradientshader_opengl");
            GrainEffect = content.Load<Effect>("Effects/grainshader_opengl");
#else
            //var blurEffect = content.Load<Effect>("Effects/blurshader");
            damageEffect = content.Load<Effect>("Effects/damageshader");
            PostProcessEffect = content.Load<Effect>("Effects/postprocess");
            GradientEffect = content.Load<Effect>("Effects/gradientshader");
            GrainEffect = content.Load<Effect>("Effects/grainshader");
#endif

            damageStencil = TextureLoader.FromFile("Content/Map/walldamage.png");
            damageEffect.Parameters["xStencil"].SetValue(damageStencil);
            damageEffect.Parameters["aMultiplier"].SetValue(50.0f);
            damageEffect.Parameters["cMultiplier"].SetValue(200.0f);

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
        }

        public override void AddToGUIUpdateList()
        {
            if (Character.Controlled != null && Character.Controlled.SelectedConstruction != null && Character.Controlled.CanInteractWith(Character.Controlled.SelectedConstruction))
            {
                Character.Controlled.SelectedConstruction.AddToGUIUpdateList();
            }
            if (Character.Controlled?.Inventory != null)
            {
                foreach (Item item in Character.Controlled.Inventory.AllItems)
                {
                    if (Character.Controlled.HasEquippedItem(item))
                    {
                        item.AddToGUIUpdateList();
                    }
                }
            }

            if (GameMain.GameSession != null) GameMain.GameSession.AddToGUIUpdateList();

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
                    c.AnimController.Limbs.ForEach(l =>
                    {
                        if (l.LightSource != null)
                        {
                            l.LightSource.Enabled = c.IsVisible;
                        }
                    });
                }
            }

            Stopwatch sw = new Stopwatch();
            sw.Start();

            DrawMap(graphics, spriteBatch, deltaTime);

            sw.Stop();
            GameMain.PerformanceCounter.AddElapsedTicks("DrawMap", sw.ElapsedTicks);
            sw.Restart();

            spriteBatch.Begin(SpriteSortMode.Deferred, null, GUI.SamplerState, null, GameMain.ScissorTestEnable);

            if (Character.Controlled != null && cam != null) { Character.Controlled.DrawHUD(spriteBatch, cam); }

            if (GameMain.GameSession != null) { GameMain.GameSession.Draw(spriteBatch); }

            if ((Character.Controlled == null || !Character.Controlled.IsHuman) && !GUI.DisableHUD) // Spectators and player monsters can see the submarine icons
            {
                for (int i = 0; i < Submarine.MainSubs.Length; i++)
                {
                    if (Submarine.MainSubs[i] == null) continue;
                    if (Level.Loaded != null && Submarine.MainSubs[i].WorldPosition.Y < Level.MaxEntityDepth) continue;

                    Vector2 position = Submarine.MainSubs[i].SubBody != null ? Submarine.MainSubs[i].WorldPosition : Submarine.MainSubs[i].HiddenSubPosition;

                    Color indicatorColor = i == 0 ? Color.LightBlue * 0.5f : GUI.Style.Red * 0.5f;
                    GUI.DrawIndicator(
                        spriteBatch, position, cam, 
                        Math.Max(Submarine.MainSub.Borders.Width, Submarine.MainSub.Borders.Height), 
                        GUI.SubmarineIcon, indicatorColor); 
                }
            }

            GUI.Draw(cam, spriteBatch);

            spriteBatch.End();

            sw.Stop();
            GameMain.PerformanceCounter.AddElapsedTicks("DrawHUD", sw.ElapsedTicks);
            sw.Restart();
        }

        public void DrawMap(GraphicsDevice graphics, SpriteBatch spriteBatch, double deltaTime)
        {
            foreach (Submarine sub in Submarine.Loaded)
            {
                sub.UpdateTransform();
            }

            GameMain.ParticleManager.UpdateTransforms();

            GameMain.LightManager.ObstructVision = 
                Character.Controlled != null && 
                Character.Controlled.ObstructVision && 
                (Character.Controlled.ViewTarget == Character.Controlled || Character.Controlled.ViewTarget == null);

            GameMain.LightManager.UpdateObstructVision(graphics, spriteBatch, cam, Character.Controlled?.CursorWorldPosition ?? Vector2.Zero);

            //------------------------------------------------------------------------
            graphics.SetRenderTarget(renderTarget);
            graphics.Clear(Color.Transparent);
            //Draw background structures and wall background sprites 
            //(= the background texture that's revealed when a wall is destroyed) into the background render target
            //These will be visible through the LOS effect.
            spriteBatch.Begin(SpriteSortMode.BackToFront, BlendState.NonPremultiplied, null, DepthStencilState.None, null, null, cam.Transform);
            Submarine.DrawBack(spriteBatch, false, e => e is Structure s && (e.SpriteDepth >= 0.9f || s.Prefab.BackgroundSprite != null));
            Submarine.DrawPaintedColors(spriteBatch, false);
            spriteBatch.End();

            graphics.SetRenderTarget(null);
            GameMain.LightManager.RenderLightMap(graphics, spriteBatch, cam, renderTarget);

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

			//draw alpha blended particles that are in water and behind subs
#if LINUX || OSX
			spriteBatch.Begin(SpriteSortMode.Deferred, BlendState.NonPremultiplied, null, DepthStencilState.None, null, null, cam.Transform);
#else
            spriteBatch.Begin(SpriteSortMode.Deferred, BlendState.NonPremultiplied, null, DepthStencilState.None, null, null, cam.Transform);
#endif
			GameMain.ParticleManager.Draw(spriteBatch, true, false, Particles.ParticleBlendState.AlphaBlend);
			spriteBatch.End();
            
            //draw additive particles that are in water and behind subs
            spriteBatch.Begin(SpriteSortMode.Deferred, BlendState.Additive, null, DepthStencilState.None, null, null, cam.Transform);
            GameMain.ParticleManager.Draw(spriteBatch, true, false, Particles.ParticleBlendState.Additive);
            spriteBatch.End();
            spriteBatch.Begin(SpriteSortMode.Deferred, BlendState.NonPremultiplied, null, DepthStencilState.None);
            spriteBatch.Draw(renderTarget, new Rectangle(0, 0, GameMain.GraphicsWidth, GameMain.GraphicsHeight), Color.White);
            spriteBatch.End();

            //----------------------------------------------------------------------------

            //Start drawing to the normal render target (stuff that can't be seen through the LOS effect)
            graphics.SetRenderTarget(renderTarget);

            graphics.BlendState = BlendState.NonPremultiplied;
            graphics.SamplerStates[0] = SamplerState.LinearWrap;
            Quad.UseBasicEffect(renderTargetBackground);
            Quad.Render();

            //Draw the rest of the structures, characters and front structures
            spriteBatch.Begin(SpriteSortMode.BackToFront, BlendState.NonPremultiplied, null, DepthStencilState.None, null, null, cam.Transform);
            Submarine.DrawBack(spriteBatch, false, e => !(e is Structure) || e.SpriteDepth < 0.9f);
            foreach (Character c in Character.CharacterList)
            {
                if (!c.IsVisible || c.AnimController.Limbs.Any(l => l.DeformSprite != null)) { continue; }
                c.Draw(spriteBatch, Cam);
            }
            spriteBatch.End();

            //draw characters with deformable limbs last, because they can't be batched into SpriteBatch
            //pretty hacky way of preventing draw order issues between normal and deformable sprites
            spriteBatch.Begin(SpriteSortMode.Deferred, BlendState.NonPremultiplied, null, DepthStencilState.None, null, null, cam.Transform);
            //backwards order to render the most recently spawned characters in front (characters spawned later have a larger sprite depth)
            for (int i = Character.CharacterList.Count - 1; i >= 0; i--)
            {
                Character c = Character.CharacterList[i];
                if (!c.IsVisible || c.AnimController.Limbs.All(l => l.DeformSprite == null)) { continue; }
                c.Draw(spriteBatch, Cam);
            }
            spriteBatch.End();

            Level.Loaded?.DrawFront(spriteBatch, cam);

            //draw the rendertarget and particles that are only supposed to be drawn in water into renderTargetWater
            graphics.SetRenderTarget(renderTargetWater);

            graphics.BlendState = BlendState.Opaque;
            graphics.SamplerStates[0] = SamplerState.LinearWrap;
            Quad.UseBasicEffect(renderTarget);
            Quad.Render();

            //draw alpha blended particles that are inside a sub
            spriteBatch.Begin(SpriteSortMode.Deferred, BlendState.NonPremultiplied, null, DepthStencilState.DepthRead, null, null, cam.Transform);
			GameMain.ParticleManager.Draw(spriteBatch, true, true, Particles.ParticleBlendState.AlphaBlend);
			spriteBatch.End();

			graphics.SetRenderTarget(renderTarget);

			//draw alpha blended particles that are not in water
            spriteBatch.Begin(SpriteSortMode.Deferred, BlendState.NonPremultiplied, null, DepthStencilState.DepthRead, null, null, cam.Transform);
			GameMain.ParticleManager.Draw(spriteBatch, false, null, Particles.ParticleBlendState.AlphaBlend);
			spriteBatch.End();

			//draw additive particles that are not in water
			spriteBatch.Begin(SpriteSortMode.Deferred, BlendState.Additive, null, DepthStencilState.None, null, null, cam.Transform);
			GameMain.ParticleManager.Draw(spriteBatch, false, null, Particles.ParticleBlendState.Additive);
			spriteBatch.End();
            
            graphics.DepthStencilState = DepthStencilState.DepthRead;
            graphics.SetRenderTarget(renderTargetFinal);
                        
            WaterRenderer.Instance.ResetBuffers();
            Hull.UpdateVertices(cam, WaterRenderer.Instance);			
            WaterRenderer.Instance.RenderWater(spriteBatch, renderTargetWater, cam);
            WaterRenderer.Instance.RenderAir(graphics, cam, renderTarget, Cam.ShaderTransform);
            graphics.DepthStencilState = DepthStencilState.None;

            spriteBatch.Begin(SpriteSortMode.Immediate,
                BlendState.NonPremultiplied, SamplerState.LinearWrap,
                null, null,
                damageEffect,
                cam.Transform);
            Submarine.DrawDamageable(spriteBatch, damageEffect, false);
            spriteBatch.End();

            spriteBatch.Begin(SpriteSortMode.BackToFront, BlendState.NonPremultiplied, null, DepthStencilState.None, null, null, cam.Transform);
            Submarine.DrawFront(spriteBatch, false, null);
            spriteBatch.End();

            //draw additive particles that are inside a sub
            spriteBatch.Begin(SpriteSortMode.Deferred, BlendState.Additive, null, DepthStencilState.Default, null, null, cam.Transform);
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
                graphics.BlendState = Lights.CustomBlendStates.Multiplicative;
                Quad.UseBasicEffect(GameMain.LightManager.LightMap);
                Quad.Render();
            }

			spriteBatch.Begin(SpriteSortMode.Deferred, BlendState.NonPremultiplied, SamplerState.LinearWrap, DepthStencilState.None, null, null, cam.Transform);
            foreach (Character c in Character.CharacterList)
            {
                c.DrawFront(spriteBatch, cam);
            }

            Level.Loaded?.DrawDebugOverlay(spriteBatch, cam);            
            if (GameMain.DebugDraw)
            {
                MapEntity.mapEntityList.ForEach(me => me.AiTarget?.Draw(spriteBatch));
                Character.CharacterList.ForEach(c => c.AiTarget?.Draw(spriteBatch));
                if (GameMain.GameSession?.EventManager != null)
                {
                    GameMain.GameSession.EventManager.DebugDraw(spriteBatch);
                }
            }
            spriteBatch.End();

            if (GameMain.LightManager.LosEnabled && GameMain.LightManager.LosMode != LosMode.None && Lights.LightManager.ViewTarget != null)
            {
                GameMain.LightManager.LosEffect.CurrentTechnique = GameMain.LightManager.LosEffect.Techniques["LosShader"];

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
                GameMain.LightManager.LosEffect.CurrentTechnique.Passes[0].Apply();
                Quad.Render();
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
            Vector3 chromaticAberrationStrength = GameMain.Config.ChromaticAberrationEnabled ?
                new Vector3(-0.02f, -0.01f, 0.0f) : Vector3.Zero;

            if (Character.Controlled != null)
            {
                BlurStrength = Character.Controlled.BlurStrength * 0.005f;
                DistortStrength = Character.Controlled.DistortStrength;
                if (GameMain.Config.EnableRadialDistortion)
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
                Quad.UseBasicEffect(renderTargetFinal);
            }
            else
            {
                PostProcessEffect.Parameters["MatrixTransform"].SetValue(Matrix.Identity);
                PostProcessEffect.Parameters["xTexture"].SetValue(renderTargetFinal);
                PostProcessEffect.CurrentTechnique = PostProcessEffect.Techniques[postProcessTechnique];
                PostProcessEffect.CurrentTechnique.Passes[0].Apply();
            }
            Quad.Render();

            if (fadeToBlackState > 0.0f)
            {
                spriteBatch.Begin(SpriteSortMode.Deferred);
                GUI.DrawRectangle(spriteBatch, new Rectangle(0, 0, GameMain.GraphicsWidth, GameMain.GraphicsHeight), Color.Lerp(Color.TransparentBlack, Color.Black, fadeToBlackState), isFilled: true);
                spriteBatch.End();
            }
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

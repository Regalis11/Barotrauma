using Barotrauma.Networking;
using Lidgren.Network;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Input;
using System;

namespace Barotrauma
{
    class Camera
    {
        public static bool FollowSub = true;

        private float? defaultZoom;
        public float DefaultZoom
        {
            get { return defaultZoom ?? (GameSettings.CurrentConfig.EnableMouseLook ? 1.3f : 1.0f); }
            set
            {
                defaultZoom = MathHelper.Clamp(value, 0.5f, 2.0f);
            }
        }

        private float zoomSmoothness = 8.0f;
        public float ZoomSmoothness
        {
            get { return zoomSmoothness; }
            set { zoomSmoothness = Math.Max(value, 0.01f); }
        }
        private float moveSmoothness = 8.0f;
        public float MoveSmoothness
        {
            get { return moveSmoothness; }
            set { moveSmoothness = Math.Max(value, 0.01f); }
        }

        private float minZoom = 0.1f;
        public float MinZoom
        {
            get { return minZoom; }
            set { minZoom = MathHelper.Clamp(value, 0.001f, 10.0f); }
        }

        private float maxZoom = 2.0f;
        public float MaxZoom
        {
            get { return maxZoom; }
            set { maxZoom = MathHelper.Clamp(value, 1.0f, 10.0f); }
        }

        public float FreeCamMoveSpeed = 1.0f;

        private float zoom;

        private Matrix transform, shaderTransform, viewMatrix;
        private Vector2 position;
        private float rotation;

        private float angularVelocity;
        private float angularDamping;
        private float angularSpring;

        private Vector2 prevPosition;
        private float prevZoom;

        public float Shake;

        /// <summary>
        /// Should the camera's transform matrices be automatically updated to match the screen resolution?
        /// </summary>
        public bool AutoUpdateToScreenResolution = true;

        public Vector2 ShakePosition { get; private set; }
        private float shakeTimer;

        public float MovementLockTimer;

        private float globalZoomScale = 1.0f;

        //used to smooth out the movement when in freecam
        private float targetZoom;
        private Vector2 velocity;

        public float Zoom
        {
            get { return zoom; }
            set 
            {
                zoom = MathHelper.Clamp(value, GameMain.DebugDraw ? 0.01f : MinZoom, MaxZoom);
                
                Vector2 center = WorldViewCenter;
                float newWidth = Resolution.X / zoom;
                float newHeight = Resolution.Y / zoom;

                WorldView = new Rectangle(
                    (int)(center.X - newWidth / 2.0f),
                    (int)(center.Y + newHeight / 2.0f),
                    (int)newWidth,
                    (int)newHeight);

                //UpdateTransform();
            }
        }

        public float Rotation
        {
            get { return rotation; }
            set
            {
                if (!MathUtils.IsValid(value)) return;
                rotation = value;
            }
        }

        public float AngularVelocity
        {
            get { return angularVelocity; }
            set
            {
                if (!MathUtils.IsValid(value)) return;
                angularVelocity = value;
            }
        }

        public float OffsetAmount { get; set; }

        public Point Resolution { get; private set; }

        //the area of the world inside the camera view
        public Rectangle WorldView { get; private set; }

        public Vector2 WorldViewCenter
        {
            get
            {
                return new Vector2(
                    WorldView.X + WorldView.Width / 2.0f,
                    WorldView.Y - WorldView.Height / 2.0f);
            }
        }

        public Matrix Transform
        {
            get { return transform; }
        }

        public Matrix ShaderTransform
        {
            get { return shaderTransform; }
        }

        public Camera()
        {
            zoom = prevZoom = targetZoom = 1.0f;
            rotation = 0.0f;
            position = Vector2.Zero;

            CreateMatrices();

            UpdateTransform(false);
        }

        public Vector2 TargetPos { get; set; }

        public Vector2 GetPosition()
        {
            return position;
        }
                
        // Auxiliary function to move the camera
        public void Translate(Vector2 amount)
        {
            position += amount;
        }

        public void ClientWrite(in SegmentTableWriter<ClientNetSegment> segmentTableWriter, IWriteMessage msg)
        {
            if (Character.Controlled != null && !Character.Controlled.IsDead) { return; }

            segmentTableWriter.StartNewSegment(ClientNetSegment.SpectatingPos);
            msg.WriteSingle(position.X);
            msg.WriteSingle(position.Y);
        }

        private void CreateMatrices()
        {
            SetResolution(new Point(GameMain.GraphicsWidth, GameMain.GraphicsHeight));
        }

        public void SetResolution(Point res)
        {
            Resolution = res;

            WorldView = new Rectangle(0, 0, res.X, res.Y);
            viewMatrix = Matrix.CreateTranslation(new Vector3(res.X / 2.0f, res.Y / 2.0f, 0));
            float newGlobalZoomScale = (float)new Vector2(GUI.UIWidth, Resolution.Y).Length() / GUI.ReferenceResolution.Length();
            if (globalZoomScale > 0.0f)
            {
                Zoom *= newGlobalZoomScale / globalZoomScale;
                targetZoom *= newGlobalZoomScale / globalZoomScale;
                prevZoom *= newGlobalZoomScale / globalZoomScale;
            }
            globalZoomScale = newGlobalZoomScale;
        }

        public void UpdateTransform(bool interpolate = true, bool updateListener = true)
        {
            if (AutoUpdateToScreenResolution)
            {
                if (GameMain.GraphicsWidth != Resolution.X ||
                    GameMain.GraphicsHeight != Resolution.Y)
                {
                    CreateMatrices();
                }
            }

            Vector2 interpolatedPosition = interpolate ? Timing.Interpolate(prevPosition, position) : position;

            float interpolatedZoom = interpolate ? Timing.Interpolate(prevZoom, zoom) : zoom;

            WorldView = new Rectangle((int)(interpolatedPosition.X - WorldView.Width / 2.0),
                                      (int)(interpolatedPosition.Y + WorldView.Height / 2.0),
                                      WorldView.Width, WorldView.Height);
            
            transform = Matrix.CreateTranslation(
                new Vector3(-interpolatedPosition.X, interpolatedPosition.Y, 0)) *
                Matrix.CreateScale(new Vector3(interpolatedZoom, interpolatedZoom, 1)) *
                Matrix.CreateRotationZ(rotation) * viewMatrix;

            shaderTransform = Matrix.CreateTranslation(
                new Vector3(
                    -interpolatedPosition.X - Resolution.X / interpolatedZoom / 2.0f,
                    -interpolatedPosition.Y - Resolution.Y / interpolatedZoom / 2.0f, 0)) *
                Matrix.CreateScale(new Vector3(interpolatedZoom, interpolatedZoom, 1)) *

                viewMatrix * Matrix.CreateRotationZ(-rotation);

            if (updateListener)
            {
                if (Character.Controlled == null)
                {
                    GameMain.SoundManager.ListenerPosition = new Vector3(WorldViewCenter.X, WorldViewCenter.Y, -(100.0f / zoom));
                }
                else
                {
                    GameMain.SoundManager.ListenerPosition = new Vector3(Character.Controlled.WorldPosition.X, Character.Controlled.WorldPosition.Y, -(100.0f / zoom));
                }
            }
            

            if (!interpolate)
            {
                prevPosition = position;
                prevZoom = zoom;
            }
        }

        private Vector2 previousOffset;
        
        /// <summary>
        /// Resets to false each time the MoveCamera method is called.
        /// </summary>
        public bool Freeze { get; set; }

        public void MoveCamera(float deltaTime, bool allowMove = true, bool allowZoom = true, bool allowInput = true, bool? followSub = null)
        {
            prevPosition = position;
            prevZoom = zoom;

            float moveSpeed = 20.0f / zoom;

            MovementLockTimer -= deltaTime;

            Vector2 moveCam = Vector2.Zero;
            if (TargetPos == Vector2.Zero)
            {
                Vector2 moveInput = Vector2.Zero;
                if (allowMove && !Freeze)
                {
                    if (GUI.KeyboardDispatcher.Subscriber == null && allowInput && MovementLockTimer <= 0.0f)
                    {
                        if (PlayerInput.KeyDown(Keys.LeftShift)) { moveSpeed *= 2.0f; }
                        if (PlayerInput.KeyDown(Keys.LeftControl)) { moveSpeed *= 0.5f; }

                        if (GameSettings.CurrentConfig.KeyMap.Bindings[InputType.Left].IsDown()) { moveInput.X -= 1.0f; }
                        if (GameSettings.CurrentConfig.KeyMap.Bindings[InputType.Right].IsDown()) { moveInput.X += 1.0f; }
                        if (GameSettings.CurrentConfig.KeyMap.Bindings[InputType.Down].IsDown()) { moveInput.Y -= 1.0f; }
                        if (GameSettings.CurrentConfig.KeyMap.Bindings[InputType.Up].IsDown()) { moveInput.Y += 1.0f; }
                    }

                    velocity = Vector2.Lerp(velocity, moveInput, deltaTime * 10.0f);
                    moveCam = velocity * moveSpeed * deltaTime * FreeCamMoveSpeed * 60.0f;

                    if (Screen.Selected == GameMain.GameScreen && (followSub ?? FollowSub) && GameMain.Instance is not { Paused: true })
                    {
                        var closestSub = Submarine.FindClosest(WorldViewCenter);
                        if (closestSub != null)
                        {
                            moveCam += FarseerPhysics.ConvertUnits.ToDisplayUnits(closestSub.Velocity * deltaTime);
                        }
                    }                    
                }
                 
                if (allowZoom)
                {
                    Vector2 mouseInWorld = ScreenToWorld(PlayerInput.MousePosition);
                    Vector2 diffViewCenter;
                    diffViewCenter = (mouseInWorld - Position) * Zoom;
                    targetZoom = MathHelper.Clamp(
                        targetZoom + PlayerInput.ScrollWheelSpeed / 1000.0f * zoom, 
                        GameMain.DebugDraw ? MinZoom * 0.1f : MinZoom, 
                        MaxZoom);

                    if (PlayerInput.KeyDown(Keys.LeftControl)) 
                    {  
                        Zoom += (targetZoom - zoom) / (ZoomSmoothness * 10.0f);
                    }
                    else
                    {
                        Zoom = MathHelper.Lerp(Zoom, targetZoom, deltaTime * 10.0f);
                    }
                    if (!PlayerInput.KeyDown(Keys.F)) { Position = mouseInWorld - (diffViewCenter / Zoom); }
                }
            }
            else if (allowMove)
            {
                Vector2 mousePos = PlayerInput.MousePosition;
                Vector2 offset = mousePos - Resolution.ToVector2() / 2;
                offset.X = offset.X / (Resolution.X * 0.4f);
                offset.Y = -offset.Y / (Resolution.Y * 0.3f);
                if (offset.LengthSquared() > 1.0f) offset.Normalize();
                float offsetUnscaledLen = offset.Length();
                offset *= OffsetAmount;
                // Freeze the camera movement by default, when the cursor is on top of an ui element.
                // Setting a positive value to the OffsetAmount, will override this behaviour.
                if (GUI.MouseOn != null && OffsetAmount > 0)
                {
                    Freeze = true;
                }
                if (CharacterHealth.OpenHealthWindow != null || CrewManager.IsCommandInterfaceOpen || ConversationAction.IsDialogOpen)
                {
                    offset *= 0;
                    Freeze = false;
                }
                if (Freeze)
                {
                    if (offset.LengthSquared() > 0.001f) { offset = previousOffset; }
                }
                else
                {
                    previousOffset = offset;
                }

                if (allowZoom)
                {
                    //how much to zoom out (zoom completely out when offset is 1000)
                    float zoomOutAmount = GetZoomAmount(offset);
                    //scaled zoom amount
                    float scaledZoom = MathHelper.Lerp(DefaultZoom, MinZoom, zoomOutAmount) * globalZoomScale;
                    //zoom in further if zoomOutAmount is low and resolution is lower than reference
                    float newZoom = scaledZoom * (MathHelper.Lerp(0.3f * (1f - Math.Min(globalZoomScale, 1f)), 0f,
                        (GameSettings.CurrentConfig.EnableMouseLook) ? (float)Math.Sqrt(offsetUnscaledLen) : 0.3f) + 1f);

                    Zoom += (newZoom - zoom) / ZoomSmoothness;
                }

                //force targetzoom to the current zoom value, so the camera stays at the same zoom when switching to freecam
                targetZoom = Zoom;

                Vector2 diff = (TargetPos + offset) - position;

                moveCam = diff / MoveSmoothness;
            }
            rotation += angularVelocity * deltaTime;
            angularVelocity *= (1.0f - angularDamping);
            angularVelocity += -rotation * angularSpring;

            angularDamping = 0.05f;
            angularSpring = 0.2f;

            if (Shake < 0.01f)
            {
                ShakePosition = Vector2.Zero;
                shakeTimer = 0.0f;
            }
            else
            {
                shakeTimer += deltaTime * 5.0f;
                Vector2 noisePos = new Vector2((float)PerlinNoise.CalculatePerlin(shakeTimer, shakeTimer, 0) - 0.5f, (float)PerlinNoise.CalculatePerlin(shakeTimer, shakeTimer, 0.5f) - 0.5f);

                ShakePosition = noisePos * Shake * GameSettings.CurrentConfig.Graphics.ScreenShake * 2.0f;
                Shake = MathHelper.Lerp(Shake, 0.0f, deltaTime * 2.0f);
            }

            Translate(moveCam + ShakePosition);
            Freeze = false;
        }
        
        public void StopMovement()
        {
            targetZoom = zoom;
            velocity = Vector2.Zero;
            angularVelocity = 0.0f;
            rotation = 0.0f;
        }

        public Vector2 Position
        {
            get { return position; }
            set 
            { 
                if (!MathUtils.IsValid(value))
                {
                    return;
                }
                position = value; 
            }
        }
                
        public Vector2 ScreenToWorld(Vector2 coords)
        {
            Vector2 worldCoords = Vector2.Transform(coords, Matrix.Invert(transform));
            return new Vector2(worldCoords.X, -worldCoords.Y);
        }

        public Vector2 WorldToScreen(Vector2 coords)
        {
            coords.Y = -coords.Y;
            //Vector2 screenCoords = Vector2.Transform(coords, transform);
            return Vector2.Transform(coords, transform);
        }

        private float GetZoomAmount(Vector2 offset)
        {
            return Math.Min(offset.Length() / 1000.0f, 1.0f);
        }

        public float GetZoomAmountFromPrevious()
        {
            return GetZoomAmount(previousOffset);
        }
    }
}

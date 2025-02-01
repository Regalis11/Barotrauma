using Barotrauma.Extensions;
using Barotrauma.Items.Components;
using FarseerPhysics;
using FarseerPhysics.Common;
using FarseerPhysics.Dynamics;
using FarseerPhysics.Dynamics.Contacts;
using FarseerPhysics.Dynamics.Joints;
using Microsoft.Xna.Framework;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Voronoi2;

namespace Barotrauma
{
    partial class SubmarineBody
    {
        public const float NeutralBallastPercentage = 0.07f;

        public const Category CollidesWith =
                Physics.CollisionItem |
                Physics.CollisionLevel |
                Physics.CollisionCharacter |
                Physics.CollisionProjectile |
                Physics.CollisionWall;

        const float HorizontalDrag = 0.01f;
        const float VerticalDrag = 0.05f;
        const float MaxDrag = 0.1f;

        private const float ImpactDamageMultiplier = 10.0f;

        //limbs with a mass smaller than this won't cause an impact when they hit the sub
        private const float MinImpactLimbMass = 10.0f;
        //impacts smaller than this are ignored
        private const float MinCollisionImpact = 3.0f;
        //impacts are clamped below this value
        private const float MaxCollisionImpact = 5.0f;
        private const float Friction = 0.2f, Restitution = 0.0f;

        private readonly List<Contact> levelContacts = new List<Contact>();

        public List<Vector2> HullVertices
        {
            get;
            private set;
        }

        private float depthDamageTimer = 10.0f;
        private float damageSoundTimer = 10.0f;

        private readonly Submarine submarine;

        public readonly PhysicsBody Body;

        private readonly List<PosInfo> positionBuffer = new List<PosInfo>();

        private readonly Queue<Impact> impactQueue = new Queue<Impact>();

        private float forceUpwardsTimer;
        private const float ForceUpwardsDelay = 30.0f;

        // submarines greater than "InertialReferenceMass" will accelerate slower, and vica versa. 30000 is about the size of the Humpback.
        public const float InertiaReferenceMass = 30000f;
        public const float MassToAreaExponent = 2f/3f;

        struct Impact
        {
            public Fixture Target;
            public Vector2 Velocity;
            public Vector2 ImpactPos;
            public Vector2 Normal;

            public Impact(Fixture f1, Fixture f2, Contact contact)
            {
                Target = f2;
                contact.GetWorldManifold(out Vector2 contactNormal, out FixedArray2<Vector2> points);
                if (contact.FixtureA.Body == f1.Body) { contactNormal = -contactNormal; }
                ImpactPos = points[0];
                Normal = contactNormal;
                Velocity = f1.Body.LinearVelocity - f2.Body.LinearVelocity;
            }
        }

        /// <summary>
        /// Extents of the solid items/structures (ones with a physics body) and hulls
        /// </summary>
        public Rectangle Borders
        {
            get;
            private set;
        }

        /// <summary>
        /// Extents of all the visible items/structures/hulls (including ones without a physics body)
        /// </summary>
        public Rectangle VisibleBorders
        {
            get;
            private set;
        }

        public Vector2 Velocity
        {
            get { return Body.LinearVelocity; }
            set
            {
                if (!MathUtils.IsValid(value)) return;
                Body.LinearVelocity = value;
            }
        }

        public Vector2 Position
        {
            get { return ConvertUnits.ToDisplayUnits(Body.SimPosition); }
        }

        public List<PosInfo> PositionBuffer
        {
            get { return positionBuffer; }
        }
        
        public Submarine Submarine
        {
            get { return submarine; }
        }

        public SubmarineBody(Submarine sub, bool showErrorMessages = true)
        {
            this.submarine = sub;

            Vector2 minExtents = Vector2.Zero, maxExtents = Vector2.Zero;
            Vector2 visibleMinExtents = Vector2.Zero, visibleMaxExtents = Vector2.Zero;
            Body farseerBody = null;
            if (!Hull.HullList.Any(h => h.Submarine == sub))
            {
                farseerBody = GameMain.World.CreateRectangle(1.0f, 1.0f, 1.0f);
                if (showErrorMessages)
                {
                    DebugConsole.ThrowError($"No hulls found in the submarine \"{sub.Info.Name}\". Generating a physics body for the submarine failed.");
                }
            }
            else
            {
                List<Vector2> convexHull = GenerateConvexHull();
                for (int i = 0; i < convexHull.Count; i++)
                {
                    convexHull[i] = ConvertUnits.ToSimUnits(convexHull[i]);
                }
                HullVertices = convexHull;


                farseerBody = GameMain.World.CreateBody(findNewContacts: false, bodyType: BodyType.Dynamic);
                var collisionCategory = Physics.CollisionWall;
                var collidesWith =
                    Physics.CollisionItem |
                    Physics.CollisionLevel |
                    Physics.CollisionCharacter |
                    Physics.CollisionProjectile |
                    Physics.CollisionWall;
                farseerBody.CollisionCategories = collisionCategory;
                farseerBody.CollidesWith = collidesWith;
                farseerBody.Enabled = false;
                farseerBody.UserData = this;
                if (sub.Info.IsOutpost)
                {
                    farseerBody.BodyType = BodyType.Static;
                }
                foreach (var mapEntity in MapEntity.MapEntityList)
                {
                    if (mapEntity.Submarine != submarine || mapEntity is not Structure wall) { continue; }

                    bool hasCollider = wall.HasBody && !wall.IsPlatform && wall.StairDirection == Direction.None;
                    Rectangle rect = wall.Rect;

                    var transformedQuad = wall.GetTransformedQuad();
                    AddPointToExtents(transformedQuad.A, hasCollider: hasCollider);
                    AddPointToExtents(transformedQuad.B, hasCollider: hasCollider);
                    AddPointToExtents(transformedQuad.C, hasCollider: hasCollider);
                    AddPointToExtents(transformedQuad.D, hasCollider: hasCollider);
                    if (hasCollider)
                    {
                        farseerBody.CreateRectangle(
                              ConvertUnits.ToSimUnits(wall.BodyWidth),
                              ConvertUnits.ToSimUnits(wall.BodyHeight),
                              50.0f,
                              -wall.BodyRotation,
                              ConvertUnits.ToSimUnits(new Vector2(rect.X + rect.Width / 2, rect.Y - rect.Height / 2) + wall.BodyOffset),
                              collisionCategory,
                              collidesWith).UserData = wall;
                    }
                }

                foreach (Hull hull in Hull.HullList)
                {
                    if (hull.Submarine != submarine || hull.IdFreed) { continue; }

                    Rectangle rect = hull.Rect;
                    AddPointToExtents(new Vector2(rect.X, rect.Y - rect.Height), hasCollider: true);
                    AddPointToExtents(new Vector2(rect.Right, rect.Y), hasCollider: true);

                    farseerBody.CreateRectangle(
                        ConvertUnits.ToSimUnits(rect.Width),
                        ConvertUnits.ToSimUnits(rect.Height),
                        100.0f,
                        ConvertUnits.ToSimUnits(new Vector2(rect.X + rect.Width / 2, rect.Y - rect.Height / 2)),
                        collisionCategory,
                        collidesWith).UserData = hull;
                }

                foreach (Item item in Item.ItemList)
                {
                    if (item.Submarine != submarine) { continue; }

                    Vector2 simPos  = ConvertUnits.ToSimUnits(item.Position);
                    if (sub.FlippedX) { simPos.X = -simPos.X; }
                    if (item.GetComponent<Door>() is Door door)
                    {
                        door.OutsideSubmarineFixture = farseerBody.CreateRectangle(door.Body.Width, door.Body.Height, 5.0f, simPos, collisionCategory, collidesWith);
                        door.OutsideSubmarineFixture.UserData = item;
                    }

                    if (item.StaticBodyConfig == null) { continue; }

                    float radius    = item.StaticBodyConfig.GetAttributeFloat("radius", 0.0f) * item.Scale;
                    float width     = item.StaticBodyConfig.GetAttributeFloat("width", 0.0f) * item.Scale;
                    float height    = item.StaticBodyConfig.GetAttributeFloat("height", 0.0f) * item.Scale;

                    float simRadius = ConvertUnits.ToSimUnits(radius);
                    float simWidth  = ConvertUnits.ToSimUnits(width);
                    float simHeight = ConvertUnits.ToSimUnits(height);

                    if (radius > 0f || (width > 0f && height > 0f))
                    {
                        var transformedQuad = item.GetTransformedQuad();
                        AddPointToExtents(transformedQuad.A, hasCollider: true);
                        AddPointToExtents(transformedQuad.B, hasCollider: true);
                        AddPointToExtents(transformedQuad.C, hasCollider: true);
                        AddPointToExtents(transformedQuad.D, hasCollider: true);
                    }

                    if (width > 0.0f && height > 0.0f)
                    {
                        item.StaticFixtures.Add(farseerBody.CreateRectangle(simWidth, simHeight, 5.0f, simPos, collisionCategory, collidesWith));
                        AddPointToExtents(item.Position - new Vector2(width, height) / 2, hasCollider: true);
                        AddPointToExtents(item.Position + new Vector2(width, height) / 2, hasCollider: true);
                    }
                    else if (radius > 0.0f && width > 0.0f)
                    {
                        item.StaticFixtures.Add(farseerBody.CreateRectangle(simWidth, simRadius * 2, 5.0f, simPos, collisionCategory, collidesWith));
                        item.StaticFixtures.Add(farseerBody.CreateCircle(simRadius, 5.0f, simPos - Vector2.UnitX * simWidth / 2, collisionCategory, collidesWith));
                        item.StaticFixtures.Add(farseerBody.CreateCircle(simRadius, 5.0f, simPos + Vector2.UnitX * simWidth / 2, collisionCategory, collidesWith));
                        AddPointToExtents(item.Position - new Vector2(width / 2 + radius, height / 2), hasCollider: true);
                        AddPointToExtents(item.Position + new Vector2(width / 2 + radius, height / 2), hasCollider: true);
                    }
                    else if (radius > 0.0f && height > 0.0f)
                    {
                        item.StaticFixtures.Add(farseerBody.CreateRectangle(simRadius * 2, height, 5.0f, simPos, collisionCategory, collidesWith));
                        item.StaticFixtures.Add(farseerBody.CreateCircle(simRadius, 5.0f, simPos - Vector2.UnitY * simHeight / 2, collisionCategory, collidesWith));
                        item.StaticFixtures.Add(farseerBody.CreateCircle(simRadius, 5.0f, simPos + Vector2.UnitY * simHeight / 2, collisionCategory, collidesWith));
                        AddPointToExtents(item.Position - new Vector2(width / 2, height / 2 + radius), hasCollider: true);
                        AddPointToExtents(item.Position + new Vector2(width / 2, height / 2 + radius), hasCollider: true);
                    }
                    else if (radius > 0.0f)
                    {
                        item.StaticFixtures.Add(farseerBody.CreateCircle(simRadius, 5.0f, simPos, collisionCategory, collidesWith));
                        AddPointToExtents(item.Position - new Vector2(radius, radius), hasCollider: true);
                        AddPointToExtents(item.Position + new Vector2(radius, radius), hasCollider: true);
                    }
                    item.StaticFixtures.ForEach(f => f.UserData = item);
                }

                Borders = new Rectangle((int)minExtents.X, (int)maxExtents.Y, (int)(maxExtents.X - minExtents.X), (int)(maxExtents.Y - minExtents.Y));
                VisibleBorders = new Rectangle((int)visibleMinExtents.X, (int)visibleMaxExtents.Y, (int)(visibleMaxExtents.X - visibleMinExtents.X), (int)(visibleMaxExtents.Y - visibleMinExtents.Y));
            }

            farseerBody.Enabled = true;
            farseerBody.Restitution = Restitution;
            farseerBody.Friction = Friction;
            farseerBody.FixedRotation = true;
            farseerBody.Awake = true;
            farseerBody.SleepingAllowed = false;
            farseerBody.IgnoreGravity = true;
            farseerBody.OnCollision += OnCollision;
            farseerBody.UserData = submarine;

            Body = new PhysicsBody(farseerBody);

            void AddPointToExtents(Vector2 point, bool hasCollider)
            {
                visibleMinExtents.X = Math.Min(point.X, visibleMinExtents.X);
                visibleMinExtents.Y = Math.Min(point.Y, visibleMinExtents.Y);
                visibleMaxExtents.X = Math.Max(point.X, visibleMaxExtents.X);
                visibleMaxExtents.Y = Math.Max(point.Y, visibleMaxExtents.Y);
                if (hasCollider)
                {
                    minExtents.X = Math.Min(point.X, minExtents.X);
                    minExtents.Y = Math.Min(point.Y, minExtents.Y);
                    maxExtents.X = Math.Max(point.X, maxExtents.X);
                    maxExtents.Y = Math.Max(point.Y, maxExtents.Y);
                }
            }
        }

        private List<Vector2> GenerateConvexHull()
        {
            List<Structure> subWalls = Structure.WallList.FindAll(wall => wall.Submarine == submarine);

            if (subWalls.Count == 0)
            {
                return new List<Vector2> { new Vector2(-1.0f, 1.0f), new Vector2(1.0f, 1.0f), new Vector2(0.0f, -1.0f) };
            }

            List<Vector2> points = new List<Vector2>();

            foreach (Structure wall in subWalls)
            {
                points.Add(new Vector2(wall.Rect.X, wall.Rect.Y));
                points.Add(new Vector2(wall.Rect.X + wall.Rect.Width, wall.Rect.Y));
                points.Add(new Vector2(wall.Rect.X, wall.Rect.Y - wall.Rect.Height));
                points.Add(new Vector2(wall.Rect.X + wall.Rect.Width, wall.Rect.Y - wall.Rect.Height));
            }

            List<Vector2> hullPoints = MathUtils.GiftWrap(points);

            return hullPoints;
        }

        public void Update(float deltaTime)
        {
            while (impactQueue.Count > 0)
            {
                var impact = impactQueue.Dequeue();                

                if (impact.Target.UserData is VoronoiCell cell)
                {
                    HandleLevelCollision(impact, cell);
                }
                else if (impact.Target.Body.UserData is Structure)
                {
                    HandleLevelCollision(impact);
                }
                else if (impact.Target.Body.UserData is Submarine otherSub)
                {
                    HandleSubCollision(impact, otherSub);
                }
                else if (impact.Target.Body.UserData is Limb limb)
                {
                    HandleLimbCollision(impact, limb);
                }
            }

            //-------------------------

            if (Body.FarseerBody.BodyType == BodyType.Static) { return; }
            
            ClientUpdatePosition(deltaTime);
            if (GameMain.NetworkMember != null && GameMain.NetworkMember.IsClient) { return; }
            
            Vector2 totalForce = CalculateBuoyancy();

            //-------------------------

            //if outside left or right edge of the level
            if (Level.Loaded != null && (Position.X < 0 || Position.X > Level.Loaded.Size.X))
            {
                Rectangle worldBorders = Borders;
                worldBorders.Location += MathUtils.ToPoint(Position);

                //push the sub back below the upper "barrier" of the level
                if (worldBorders.Y > Level.Loaded.Size.Y)
                {
                    Body.LinearVelocity = new Vector2(
                        Body.LinearVelocity.X,
                        Math.Min(Body.LinearVelocity.Y, ConvertUnits.ToSimUnits(Level.Loaded.Size.Y - worldBorders.Y)));
                }
                else if (worldBorders.Y - worldBorders.Height < Level.Loaded.BottomPos)
                {
                    Body.LinearVelocity = new Vector2(
                        Body.LinearVelocity.X,
                        Math.Max(Body.LinearVelocity.Y, ConvertUnits.ToSimUnits(Level.Loaded.BottomPos - (worldBorders.Y - worldBorders.Height))));
                }

                float distance = Position.X < -Level.OutsideBoundsCurrentMargin ? 
                    Math.Abs(Position.X + Level.OutsideBoundsCurrentMargin) : 
                    Position.X - (Level.Loaded.Size.X + Level.OutsideBoundsCurrentMargin);
                if (distance > 0)
                {
                    if (distance > Level.OutsideBoundsCurrentHardLimit)
                    {
                        if (Position.X < 0)
                        {
                            Body.LinearVelocity = new Vector2(Math.Max(0, Body.LinearVelocity.X), Body.LinearVelocity.Y);
                        }
                        else
                        {
                            Body.LinearVelocity = new Vector2(Math.Min(0, Body.LinearVelocity.X), Body.LinearVelocity.Y);
                        }
                    }
                    if (distance > Level.OutsideBoundsCurrentMarginExponential)
                    {
                        distance += (float)Math.Pow((distance - Level.OutsideBoundsCurrentMarginExponential) * 0.01f, 2.0f);
                    }
                    float force = distance * 0.5f;
                    totalForce += (Position.X < 0 ? Vector2.UnitX : -Vector2.UnitX) * force;
                    if (Character.Controlled != null && Character.Controlled.Submarine == submarine)
                    {
                        GameMain.GameScreen.Cam.Shake = Math.Max(GameMain.GameScreen.Cam.Shake, Math.Min(force * 0.0001f, 5.0f));
                    }
                }
            }

            //-------------------------

            //if heading up and there's another sub on top of us, gradually force it upwards
            //(i.e. apply "artificial buoyancy" to it) to prevent us from getting pinned under it
            //only applies to enemy subs with no enemies inside them (like destroyed pirate subs)
            if (totalForce.Y > 0)
            {
                ContactEdge contactEdge = Body?.FarseerBody?.ContactList;
                while (contactEdge?.Contact != null)
                {
                    if (contactEdge.Contact.Enabled &&
                        contactEdge.Other.UserData is Submarine otherSubmarine &&
                        otherSubmarine.TeamID != Submarine.TeamID &&
                        contactEdge.Contact.IsTouching)
                    {
                        contactEdge.Contact.GetWorldManifold(out Vector2 _, out FixedArray2<Vector2> points);
                        if (points[0].Y > Body.SimPosition.Y &&
                            !Character.CharacterList.Any(c => c.Submarine == otherSubmarine && !c.IsIncapacitated && c.TeamID == otherSubmarine.TeamID))
                        {
                            otherSubmarine.GetConnectedSubs().ForEach(s => s.SubBody.forceUpwardsTimer += deltaTime);
                            break;
                        }
                    }
                    contactEdge = contactEdge.Next;
                }
            }

            //-------------------------

            if (Body.LinearVelocity.LengthSquared() > 0.0001f)
            {
                //TODO: sync current drag with clients?
                float attachedMass = 0.0f;
                JointEdge jointEdge = Body.FarseerBody.JointList;
                while (jointEdge != null)
                {
                    Body otherBody = jointEdge.Joint.BodyA == Body.FarseerBody ? jointEdge.Joint.BodyB : jointEdge.Joint.BodyA;
                    Character character = (otherBody.UserData as Limb)?.character;
                    if (character != null) attachedMass += character.Mass;

                    jointEdge = jointEdge.Next;
                }
                float surfaceArea = MathF.Pow(Body.Mass / InertiaReferenceMass, MassToAreaExponent) * InertiaReferenceMass;

                float horizontalDragCoefficient = MathHelper.Clamp(HorizontalDrag + attachedMass / 5000.0f, 0.0f, MaxDrag);
                totalForce.X -= Math.Sign(Body.LinearVelocity.X) * Body.LinearVelocity.X * Body.LinearVelocity.X * horizontalDragCoefficient * surfaceArea;

                float verticalDragCoefficient = MathHelper.Clamp(VerticalDrag + attachedMass / 5000.0f, 0.0f, MaxDrag);
                totalForce.Y -= Math.Sign(Body.LinearVelocity.Y) * Body.LinearVelocity.Y * Body.LinearVelocity.Y * verticalDragCoefficient * surfaceArea;
            }

            ApplyForce(totalForce);

            if (Velocity.LengthSquared() < 0.01f)
            {
                levelContacts.Clear();
                levelContacts.AddRange(GetLevelContacts(Body));
                for (int i = 0; i < levelContacts.Count; i++)
                {
                    for (int j = i + 1; j < levelContacts.Count; j++)
                    {
                        levelContacts[i].GetWorldManifold(out Vector2 normal1, out _);
                        levelContacts[j].GetWorldManifold(out Vector2 normal2, out _);

                        //normals pointing in different directions = sub lodged between two walls
                        if (Vector2.Dot(normal1, normal2) < 0)
                        {
                            //apply an extra force to hopefully dislodge the sub
                            ApplyForce(totalForce * 100.0f);
                            i = levelContacts.Count;
                            break;
                        }
                    }
                }
            }

            UpdateDepthDamage(deltaTime);

            forceUpwardsTimer = MathHelper.Clamp(forceUpwardsTimer - deltaTime * 0.1f, 0.0f, ForceUpwardsDelay);
        }

        partial void ClientUpdatePosition(float deltaTime);

        /// <summary>
        /// Moves away any character that is inside the bounding box of the sub (but not inside the sub)
        /// </summary>
        /// <param name="subTranslation">The translation that was applied to the sub before doing the displacement 
        /// (used for determining where to push the characters)</param>
        private void DisplaceCharacters(Vector2 subTranslation)
        {
            Rectangle worldBorders = Borders;
            worldBorders.Location += MathUtils.ToPoint(ConvertUnits.ToDisplayUnits(Body.SimPosition));

            Vector2 translateDir = Vector2.Normalize(subTranslation);
            if (!MathUtils.IsValid(translateDir)) translateDir = Vector2.UnitY;

            foreach (Character c in Character.CharacterList)
            {
                //character inside some sub, no need to displace
                if (c.Submarine != null) { continue; }

                foreach (Limb limb in c.AnimController.Limbs)
                {
                    if (limb.IsSevered) { continue; }
                    //if the character isn't inside the bounding box, continue
                    if (!Submarine.RectContains(worldBorders, limb.WorldPosition)) { continue; }

                    //cast a line from the position of the character to the same direction as the translation of the sub
                    //and see where it intersects with the bounding box
                    if (!MathUtils.GetLineRectangleIntersection(limb.WorldPosition,
                        limb.WorldPosition + translateDir * 100000.0f, worldBorders, out Vector2 intersection))
                    {
                        //should never happen when casting a line out from inside the bounding box
                        Debug.Assert(false);
                        continue;
                    }

                    //"+ translatedir" in order to move the character slightly away from the wall
                    c.AnimController.SetPosition(ConvertUnits.ToSimUnits(c.WorldPosition + (intersection - limb.WorldPosition)) + translateDir);

                    break;
                }
            }
        }

        private Vector2 CalculateBuoyancy()
        {
            if (Submarine.LockY) { return Vector2.Zero; }

            //calculate the buoyancy for all connected subs
            //doing it separately for each connected sub means e.g. a flooded drone barely
            //affects the buoyancy of the main sub even if there was as much water in the
            //drone as the whole ballast volume of the sub
            var connectedSubs = submarine.GetConnectedSubs();
            float waterVolume = 0.0f;
            float volume = 0.0f;
            float totalMass = connectedSubs.Sum(s => s.SubBody.Body.Mass);
            foreach (Hull hull in Hull.HullList)
            {
                if (hull.Submarine == null || !connectedSubs.Contains(hull.Submarine)) { continue; }
                if (hull.Submarine.PhysicsBody is not { BodyType: BodyType.Dynamic }) { continue; }
                waterVolume += hull.WaterVolume;
                volume += hull.Volume;                            
            }

            float waterPercentage = volume <= 0.0f ? 0.0f : waterVolume / volume;            
            float buoyancy = NeutralBallastPercentage - waterPercentage;
            float massRatio = Body.Mass / totalMass;
            if (buoyancy > 0.0f)
            {
                buoyancy *= 2.0f;
            }
            else
            {
                buoyancy = Math.Max(buoyancy, -0.5f);
            }
            if (forceUpwardsTimer > 0.0f)
            {
                buoyancy = MathHelper.Lerp(buoyancy, 0.1f, forceUpwardsTimer / ForceUpwardsDelay);
            }
            return new Vector2(0.0f, buoyancy * totalMass * 10.0f) * massRatio;
        }

        public void ApplyForce(Vector2 force)
        {
            Body.ApplyForce(force);
        }

        public void SetPosition(Vector2 position)
        {
            Body.SetTransform(ConvertUnits.ToSimUnits(position), 0.0f);
        }

        private void UpdateDepthDamage(float deltaTime)
        {
            if (GameMain.GameSession?.GameMode is TestGameMode) { return; }
            if (Level.Loaded == null) { return; }

            //camera shake and sounds start playing 500 meters before crush depth
            const float CosmeticEffectThreshold = -500.0f;
            //breaches won't get any more severe 500 meters below crush depth
            const float MaxEffectThreshold = 500.0f;
            const float MinWallDamageProbability = 0.1f;
            const float MaxWallDamageProbability = 1.0f;
            const float MinWallDamage = 50f;
            const float MaxWallDamage = 500.0f;
            const float MinCameraShake = 10f;
            const float MaxCameraShake = 50.0f;
            //delay at the start of the round during which you take no depth damage
            //(gives you a bit of time to react and return if you start the round in a level that's too deep)
            const float MinRoundDuration = 60.0f;

            if (Submarine.RealWorldDepth < Level.Loaded.RealWorldCrushDepth + CosmeticEffectThreshold || Submarine.RealWorldDepth < Submarine.RealWorldCrushDepth + CosmeticEffectThreshold)
            {
                return;
            }

            damageSoundTimer -= deltaTime;
            if (damageSoundTimer <= 0.0f)
            {
                const float PressureSoundRange = -CosmeticEffectThreshold;
                //Ratio between 0 (where the 'approaching crush depth' indication starts) and 1 (at crush depth or past it)
                float closenessToCrushDepthRatio = Math.Clamp((Submarine.RealWorldDepth - (Submarine.RealWorldCrushDepth + CosmeticEffectThreshold)) / PressureSoundRange, 0f, 1f);
#if CLIENT
                SoundPlayer.PlayDamageSound("pressure", MathHelper.Lerp(0f, 100f, closenessToCrushDepthRatio), submarine.WorldPosition + Rand.Vector(Rand.Range(0.0f, Math.Min(submarine.Borders.Width, submarine.Borders.Height))), 20000.0f, gain: 1f + closenessToCrushDepthRatio * 2);
#endif
                damageSoundTimer = Rand.Range(5.0f, 10.0f);
            }

            depthDamageTimer -= deltaTime;
            if (depthDamageTimer <= 0.0f && (GameMain.GameSession == null || GameMain.GameSession.RoundDuration > MinRoundDuration))
            {
                foreach (Structure wall in Structure.WallList)
                {
                    if (wall.Submarine != submarine) { continue; }

                    float wallCrushDepth = wall.CrushDepth;
                    float pastCrushDepth = submarine.RealWorldDepth - wallCrushDepth;
                    float pastCrushDepthRatio = Math.Clamp(pastCrushDepth / MaxEffectThreshold, 0.0f, 1.0f);

                    if (Rand.Range(0.0f, 1.0f) > MathHelper.Lerp(MinWallDamageProbability, MaxWallDamageProbability, pastCrushDepthRatio)) { continue; }

                    float damage = MathHelper.Lerp(MinWallDamage, MaxWallDamage, pastCrushDepthRatio);
                    if (pastCrushDepth > 0)
                    {
                        Explosion.RangedStructureDamage(wall.WorldPosition, 100.0f, damage, levelWallDamage: 0.0f);
#if CLIENT
                        SoundPlayer.PlayDamageSound("StructureBlunt", Rand.Range(0.0f, 100.0f), wall.WorldPosition, 2000.0f);
#endif
                    }
                    if (Character.Controlled != null && Character.Controlled.Submarine == submarine)
                    {
                        GameMain.GameScreen.Cam.Shake = Math.Max(GameMain.GameScreen.Cam.Shake, MathHelper.Lerp(MinCameraShake, MaxCameraShake, pastCrushDepthRatio));
                    }
                }
                depthDamageTimer = Rand.Range(5.0f, 10.0f);
            }
        }

        public void FlipX()
        {
            List<Vector2> convexHull = GenerateConvexHull();
            for (int i = 0; i < convexHull.Count; i++)
            {
                convexHull[i] = ConvertUnits.ToSimUnits(convexHull[i]);
            }
            HullVertices = convexHull;
        }

        public bool OnCollision(Fixture f1, Fixture f2, Contact contact)
        {
            if (f2.Body.UserData is Limb limb)
            {
                bool collision = CheckCharacterCollision(contact, limb.character);
                if (collision)
                {
                    lock (impactQueue)
                    {
                        impactQueue.Enqueue(new Impact(f1, f2, contact));
                    }
                }
                return collision;
            }
            else if (f2.Body.UserData is Character character)
            {
                return CheckCharacterCollision(contact, character);
            }
            else if (f1.UserData is Items.Components.DockingPort || f2.UserData is Items.Components.DockingPort)
            {
                return false;
            }
           
            lock (impactQueue)
            {
                impactQueue.Enqueue(new Impact(f1, f2, contact));
            }
            return true;
        }

        private bool CheckCharacterCollision(Contact contact, Character character)
        {
            if (character.Submarine != null) { return false; }
            switch (character.AnimController.CanEnterSubmarine)
            {
                case CanEnterSubmarine.False:
                    //characters that can't enter the sub always collide regardless of gaps
                    return true;
                case CanEnterSubmarine.Partial:
                    //characters that can partially enter the sub can poke their limbs inside, but not the collider
                    if (contact.FixtureB.Body == 
                        character.AnimController.Collider.FarseerBody)
                    {
                        return true;
                    }       
                    if (contact.FixtureB.Body.UserData is Limb limb && 
                        !limb.Params.CanEnterSubmarine)
                    {
                        return true;
                    }
                    break;
            }

            contact.GetWorldManifold(out Vector2 contactNormal, out FixedArray2<Vector2> points);

            Vector2 normalizedVel = character.AnimController.Collider.LinearVelocity == Vector2.Zero ?
                Vector2.Zero : Vector2.Normalize(character.AnimController.Collider.LinearVelocity);

            //try to find the hull right next to the contact point
            Vector2 targetPos = ConvertUnits.ToDisplayUnits(points[0] - contactNormal * 0.1f);
            Hull newHull = Hull.FindHull(targetPos, null);
            //not found, try searching a bit further
            if (newHull == null)
            {
                targetPos = ConvertUnits.ToDisplayUnits(points[0] - contactNormal);
                newHull = Hull.FindHull(targetPos, null);
            }
            //still not found, try searching in the direction the character is heading to
            if (newHull == null)
            {
                targetPos = ConvertUnits.ToDisplayUnits(points[0] + normalizedVel);
                newHull = Hull.FindHull(targetPos, null);
            }

            //if all the bodies of a wall have been disabled, we don't need to care about gaps (can always pass through)
            if (contact.FixtureA.UserData is not Structure wall || !wall.AllSectionBodiesDisabled())
            {
                var gaps = newHull?.ConnectedGaps ?? Gap.GapList.Where(g => g.Submarine == submarine);
                Gap adjacentGap = Gap.FindAdjacent(gaps, ConvertUnits.ToDisplayUnits(points[0]), 200.0f);
                if (adjacentGap == null) { return true; }
            }

            if (character.AnimController.CanEnterSubmarine == CanEnterSubmarine.Partial) 
            { 
                return contact.FixtureB.Body == character.AnimController.Collider.FarseerBody; 
            }
            else
            {
                if (newHull != null)
                {
                    CoroutineManager.Invoke(() =>
                    {
                        if (character != null && !character.Removed)
                        {
                            character.AnimController.FindHull(newHull.WorldPosition, setSubmarine: true);
                        }
                    });
                }
            }
            return false;
        }

        private void HandleLimbCollision(Impact collision, Limb limb)
        {
            if (limb?.body?.FarseerBody == null || limb.character == null) { return; }

            float impactMass = limb.Mass;
            var enemyAI = limb.character.AIController as EnemyAIController;
            float attackMultiplier = 1.0f;
            if (enemyAI?.ActiveAttack != null)
            {
                impactMass = Math.Max(Math.Max(limb.Mass, limb.character.AnimController.MainLimb.Mass), limb.character.AnimController.Collider.Mass);
                attackMultiplier = enemyAI.ActiveAttack.SubmarineImpactMultiplier;
            }

            if (impactMass * attackMultiplier > MinImpactLimbMass && Body.BodyType != BodyType.Static)
            {
                Vector2 normal = 
                    Vector2.DistanceSquared(Body.SimPosition, limb.SimPosition) < 0.0001f ?
                    Vector2.UnitY :
                    Vector2.Normalize(Body.SimPosition - limb.SimPosition);

                float impact = Math.Min(Vector2.Dot(collision.Velocity, -normal), 50.0f) * Math.Min(impactMass / 300.0f, 1);
                impact *= attackMultiplier;                

                ApplyImpact(impact, normal, collision.ImpactPos, applyDamage: false);
                foreach (Submarine dockedSub in submarine.DockedTo)
                {
                    dockedSub.SubBody.ApplyImpact(impact, normal, collision.ImpactPos, applyDamage: false);
                }
            }

            //find all contacts between the limb and level walls
            IEnumerable<Contact> levelContacts = GetLevelContacts(limb.body);
            int levelContactCount = levelContacts.Count();

            if (levelContactCount == 0) { return; }

            //if the limb is in contact with the level, apply an artifical impact to prevent the sub from bouncing on top of it
            //not a very realistic way to handle the collisions (makes it seem as if the characters were made of reinforced concrete),
            //but more realistic than bouncing and prevents using characters as "bumpers" that prevent all collision damage
            Vector2 avgContactNormal = Vector2.Zero;
            foreach (Contact levelContact in levelContacts)
            {
                levelContact.GetWorldManifold(out Vector2 contactNormal, out FixedArray2<Vector2> temp);

                //if the contact normal is pointing from the limb towards the level cell it's touching, flip the normal
                VoronoiCell cell = levelContact.FixtureB.UserData is VoronoiCell ?
                    ((VoronoiCell)levelContact.FixtureB.UserData) : ((VoronoiCell)levelContact.FixtureA.UserData);

                var cellDiff = ConvertUnits.ToDisplayUnits(limb.body.SimPosition) - cell.Center;
                if (Vector2.Dot(contactNormal, cellDiff) < 0)
                {
                    contactNormal = -contactNormal;
                }

                avgContactNormal += contactNormal;

                //apply impacts at the positions where this sub is touching the limb
                ApplyImpact((Vector2.Dot(-collision.Velocity, contactNormal) / 2.0f) / levelContactCount, contactNormal, collision.ImpactPos, applyDamage: false);
            }
            avgContactNormal /= levelContactCount;
            
            float contactDot = Vector2.Dot(Body.LinearVelocity, -avgContactNormal);
            if (contactDot > 0.001f)
            {
                Vector2 velChange = Vector2.Normalize(Body.LinearVelocity) * contactDot;
                if (!MathUtils.IsValid(velChange))
                {
                    GameAnalyticsManager.AddErrorEventOnce(
                        "SubmarineBody.HandleLimbCollision:" + submarine.ID,
                        GameAnalyticsManager.ErrorSeverity.Error,
                        "Invalid velocity change in SubmarineBody.HandleLimbCollision (submarine velocity: " + Body.LinearVelocity
                        + ", avgContactNormal: " + avgContactNormal
                        + ", contactDot: " + contactDot
                        + ", velChange: " + velChange + ")");
                    return;
                }

                Body.LinearVelocity -= velChange;

                if (contactDot > 0.1f)
                {
                    float damageAmount = contactDot * Body.Mass / limb.character.Mass;
                    limb.character.LastDamageSource = submarine;
                    limb.character.DamageLimb(ConvertUnits.ToDisplayUnits(collision.ImpactPos), limb, 
                        AfflictionPrefab.ImpactDamage.Instantiate(damageAmount).ToEnumerable(), 
                        stun: 0.0f, 
                        playSound: true, 
                        attackImpulse: Vector2.Zero);

                    if (limb.character.IsDead)
                    {
                        foreach (LimbJoint limbJoint in limb.character.AnimController.LimbJoints)
                        {
                            if (limbJoint.IsSevered || (limbJoint.LimbA != limb && limbJoint.LimbB != limb)) continue;
                            limb.character.AnimController.SeverLimbJoint(limbJoint);
                        }
                    }
                }
            }
        }

        private static IEnumerable<Contact> GetLevelContacts(PhysicsBody body)
        {
            ContactEdge contactEdge = body.FarseerBody.ContactList;
            while (contactEdge?.Contact != null)
            {
                if (contactEdge.Contact.Enabled &&
                    contactEdge.Contact.IsTouching &&
                    contactEdge.Other?.UserData is VoronoiCell)
                {
                   yield return contactEdge.Contact;
                }
                contactEdge = contactEdge.Next;
            }
        }

        private void HandleLevelCollision(Impact impact, VoronoiCell cell = null)
        {
            if (GameMain.GameSession != null && GameMain.GameSession.RoundDuration < 10)
            {
                //ignore level collisions for the first 10 seconds of the round in case the sub spawns in a way that causes it to hit a wall 
                //(e.g. level without outposts to dock to and an incorrectly configured ballast that makes the sub go up)
                return;
            }

            float wallImpact = Vector2.Dot(impact.Velocity, -impact.Normal);

            ApplyImpact(wallImpact, -impact.Normal, impact.ImpactPos);
            foreach (Submarine dockedSub in submarine.DockedTo)
            {
                dockedSub.SubBody.ApplyImpact(wallImpact, -impact.Normal, impact.ImpactPos);
            }

            if (cell != null && cell.IsDestructible && wallImpact > 0.0f)
            {
                var hitWall = Level.Loaded?.ExtraWalls.Find(w => w.Cells.Contains(cell));
                if (hitWall != null && hitWall.WallDamageOnTouch > 0.0f)
                {
                    var damagedStructures = Explosion.RangedStructureDamage(
                        ConvertUnits.ToDisplayUnits(impact.ImpactPos),
                        500.0f,
                        hitWall.WallDamageOnTouch, 
                        levelWallDamage: 0.0f);
#if CLIENT
                    PlayDamageSounds(damagedStructures, impact.ImpactPos, wallImpact, "StructureSlash");
#endif
                }
            }

            HandleLevelCollisionProjSpecific(impact);
        }


        partial void HandleLevelCollisionProjSpecific(Impact impact);

        private void HandleSubCollision(Impact impact, Submarine otherSub)
        {
            Debug.Assert(otherSub != submarine);

            //submarine outside the level (despawned respawn shuttle?)
            //no need to apply impacts between colliding subs
            if (submarine.IsAboveLevel)
            {
                return;
            }

            Vector2 normal = impact.Normal;
            if (impact.Target.Body == otherSub.SubBody.Body.FarseerBody)
            {
                normal = -normal;
            }

            float thisMass = Body.Mass + submarine.DockedTo.Sum(s => s.PhysicsBody.Mass);
            float otherMass = otherSub.PhysicsBody.Mass + otherSub.DockedTo.Sum(s => s.PhysicsBody.Mass);
            float massRatio = otherMass / (thisMass + otherMass);

            float impulse = (Vector2.Dot(impact.Velocity, normal) / 2.0f) * massRatio;

            //apply impact to this sub (the other sub takes care of this in its own collision callback)
            ApplyImpact(impulse, normal, impact.ImpactPos);
            foreach (Submarine dockedSub in submarine.DockedTo)
            {
                dockedSub.SubBody.ApplyImpact(impulse, normal, impact.ImpactPos);
            }

            //find all contacts between this sub and level walls
            IEnumerable<Contact> levelContacts = GetLevelContacts(Body);
            int levelContactCount = levelContacts.Count();
            if (levelContactCount == 0) { return; }
            
            //if this sub is in contact with the level, apply artifical impacts
            //to both subs to prevent the other sub from bouncing on top of this one 
            //and to fake the other sub "crushing" this one against a wall
            Vector2 avgContactNormal = Vector2.Zero;
            foreach (Contact levelContact in levelContacts)
            {
                levelContact.GetWorldManifold(out Vector2 contactNormal, out FixedArray2<Vector2> temp);

                //if the contact normal is pointing from the sub towards the level cell we collided with, flip the normal
                VoronoiCell cell = levelContact.FixtureB.UserData as VoronoiCell ?? levelContact.FixtureA.UserData as VoronoiCell;

                var cellDiff = ConvertUnits.ToDisplayUnits(Body.SimPosition) - cell.Center;
                if (Vector2.Dot(contactNormal, cellDiff) < 0)
                {
                    contactNormal = -contactNormal;
                }

                avgContactNormal += contactNormal;

                //apply impacts at the positions where this sub is touching the level
                ApplyImpact((Vector2.Dot(impact.Velocity, contactNormal) / 2.0f) * massRatio / levelContactCount, contactNormal, impact.ImpactPos);
            }
            avgContactNormal /= levelContactCount;

            //apply an impact to the other sub
            float contactDot = Vector2.Dot(otherSub.PhysicsBody.LinearVelocity, -avgContactNormal);
            if (contactDot > 0.0f)
            {
                if (otherSub.PhysicsBody.LinearVelocity.LengthSquared() > 0.0001f)
                {
                    otherSub.PhysicsBody.LinearVelocity -= Vector2.Normalize(otherSub.PhysicsBody.LinearVelocity) * contactDot;
                }

                impulse = Vector2.Dot(otherSub.Velocity, normal);
                otherSub.SubBody.ApplyImpact(impulse, normal, impact.ImpactPos);
                foreach (Submarine dockedSub in otherSub.DockedTo)
                {
                    dockedSub.SubBody.ApplyImpact(impulse, normal, impact.ImpactPos);
                }
            }            
        }

        private void ApplyImpact(float impact, Vector2 direction, Vector2 impactPos, bool applyDamage = true)
        {
            if (impact < MinCollisionImpact) { return; }
                        
            Vector2 impulse = direction * impact * 0.5f;            
            impulse = impulse.ClampLength(MaxCollisionImpact);

            float impulseMagnitude = impulse.Length();

            if (!MathUtils.IsValid(impulse))
            {
                string errorMsg =
                    "Invalid impulse in SubmarineBody.ApplyImpact: " + impulse +
                    ". Direction: " + direction + ", body position: " + Body.SimPosition + ", impact: " + impact + ".";
                if (GameMain.NetworkMember != null)
                {
                    errorMsg += GameMain.NetworkMember.IsClient ? " Playing as a client." : " Hosting a server.";
                }
                if (GameSettings.CurrentConfig.VerboseLogging) DebugConsole.ThrowError(errorMsg);
                GameAnalyticsManager.AddErrorEventOnce(
                    "SubmarineBody.ApplyImpact:InvalidImpulse",
                    GameAnalyticsManager.ErrorSeverity.Error,
                    errorMsg);
                return;
            }

#if CLIENT
            if (Character.Controlled != null && Character.Controlled.Submarine == submarine && Character.Controlled.KnockbackCooldownTimer <= 0.0f)
            {
                GameMain.GameScreen.Cam.Shake = Math.Max(impact * 10.0f, GameMain.GameScreen.Cam.Shake);
                if (submarine.Info.Type == SubmarineType.Player && !submarine.DockedTo.Any(s => s.Info.Type != SubmarineType.Player))
                {
                    float angularVelocity = 
                        (impactPos.X - Body.SimPosition.X) / ConvertUnits.ToSimUnits(submarine.Borders.Width / 2) * impulse.Y 
                        - (impactPos.Y - Body.SimPosition.Y) / ConvertUnits.ToSimUnits(submarine.Borders.Height / 2) * impulse.X;
                    GameMain.GameScreen.Cam.AngularVelocity = MathHelper.Clamp(angularVelocity * 0.1f, -1.0f, 1.0f);
                }
            }
#endif

            foreach (Character c in Character.CharacterList)
            {
                if (c.Submarine != submarine) { continue; }
                if (c.KnockbackCooldownTimer > 0.0f) { continue; }

                c.KnockbackCooldownTimer = Character.KnockbackCooldown;

                foreach (Limb limb in c.AnimController.Limbs)
                {
                    if (limb.IsSevered) { continue; }
                    limb.body.ApplyLinearImpulse(limb.Mass * impulse, 10.0f);
                }

                bool holdingOntoSomething = false;
                if (c.SelectedSecondaryItem != null)
                {
                    holdingOntoSomething = c.SelectedSecondaryItem.IsLadder ||
                        (c.SelectedSecondaryItem.GetComponent<Controller>()?.LimbPositions.Any() ?? false);
                }
                if (!holdingOntoSomething && c.SelectedItem != null)
                {
                    holdingOntoSomething = c.SelectedItem.GetComponent<Controller>()?.LimbPositions.Any() ?? false;
                }
                if (!holdingOntoSomething)
                {
                    c.AnimController.Collider.ApplyLinearImpulse(c.AnimController.Collider.Mass * impulse, 10.0f);
                    //stun for up to 2 second if the impact equal or higher to the maximum impact
                    if (impact >= MaxCollisionImpact)
                    {
                        float impactDamage = c.AnimController.GetImpactDamage(impact);
                        c.AddDamage(impactPos, AfflictionPrefab.ImpactDamage.Instantiate(impactDamage).ToEnumerable(), stun: Math.Min(impulse.Length() * 0.2f, 2.0f), playSound: true);
                    }
                }
            }

            foreach (Item item in Item.ItemList)
            {
                if (item.Submarine != submarine || item.CurrentHull == null || item.body == null || !item.body.Enabled) { continue; }
                if (item.body.Mass > impulseMagnitude) { continue; }

                item.body.ApplyLinearImpulse(impulse, 10.0f);
                item.PositionUpdateInterval = 0.0f;
            }

            float dmg = applyDamage ? impact * ImpactDamageMultiplier : 0.0f;
            var damagedStructures = Explosion.RangedStructureDamage(
                ConvertUnits.ToDisplayUnits(impactPos), 
                impact * 50.0f,
                dmg, dmg);

#if CLIENT
            PlayDamageSounds(damagedStructures, impactPos, impact, "StructureBlunt");
#endif
        }

        public void Remove()
        {
            Body.Remove();
        }
    }
}

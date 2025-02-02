using Barotrauma.Extensions;
using Barotrauma.Items.Components;
using Barotrauma.Networking;
using Barotrauma.RuinGeneration;
using FarseerPhysics;
using FarseerPhysics.Dynamics;
using Microsoft.Xna.Framework;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Xml.Linq;
using Voronoi2;

namespace Barotrauma
{
    partial class Level : Entity, IServerSerializable
    {
        public enum PlacementType
        {
            Top, Bottom
        }

        public enum EventType
        {
            SingleDestructibleWall,
            GlobalDestructibleWall
        }

        //all entities are disabled after they reach this depth
        public const int MaxEntityDepth = -1000000;
        public const float ShaftHeight = 1000.0f;

        /// <summary>
        /// How far outside the boundaries of the level the water current that pushes subs towards the level starts
        /// </summary>
        public const float OutsideBoundsCurrentMargin = 30000.0f;

        /// <summary>
        /// How far outside the boundaries of the level the strength of the current starts to increase exponentially
        /// </summary>
        public const float OutsideBoundsCurrentMarginExponential = 150000.0f;

        /// <summary>
        /// How far outside the boundaries of the level the current stops submarines entirely
        /// </summary>
        public const float OutsideBoundsCurrentHardLimit = 200000.0f;

        /// <summary>
        /// The level generator won't try to adjust the width of the main path above this limit.
        /// </summary>
        public const int MaxSubmarineWidth = 16000;

        private static Level loaded;
        public static Level Loaded 
        { 
            get { return loaded; }
            private set
            {
                if (loaded == value) { return; }
                loaded = value;
                GameAnalyticsManager.SetCurrentLevel(loaded?.LevelData);
            }
        }

        [Flags]
        public enum PositionType
        {
            None = 0,
            MainPath = 0x1,
            SidePath = 0x2,
            Cave = 0x4,
            Ruin = 0x8,
            Wreck = 0x10,
            BeaconStation = 0x20,
            Abyss = 0x40,
            AbyssCave = 0x80,
            Outpost = 0x100,
        }

        public struct InterestingPosition
        {
            public Point Position;
            public readonly PositionType PositionType;
            public bool IsValid;
            public Submarine Submarine;
            public Ruin Ruin;
            public Cave Cave;

            public InterestingPosition(Point position, PositionType positionType, Submarine submarine = null, bool isValid = true)
            {
                Position = position;
                PositionType = positionType;
                IsValid = isValid;
                Submarine = submarine;
                Ruin = null;
                Cave = null;
            }

            public InterestingPosition(Point position, PositionType positionType, Ruin ruin, bool isValid = true)
            {
                Position = position;
                PositionType = positionType;
                IsValid = isValid;
                Submarine = null;
                Ruin = ruin;
                Cave = null;
            }
            public InterestingPosition(Point position, PositionType positionType, Cave cave, bool isValid = true)
            {
                Position = position;
                PositionType = positionType;
                IsValid = isValid;
                Submarine = null;
                Ruin = null;
                Cave = cave;
            }

            /// <summary>
            /// Caves, ruins, outposts and similar enclosed areas
            /// </summary>
            public bool IsEnclosedArea()
            {
                return
                     PositionType == PositionType.Cave ||
                     PositionType == PositionType.Ruin ||
                     PositionType == PositionType.Outpost ||
                     PositionType == PositionType.BeaconStation ||
                     PositionType == PositionType.Wreck ||
                     PositionType == PositionType.AbyssCave;
            }
        }

        public enum TunnelType
        {
            MainPath, SidePath, Cave
        }

        public class Tunnel
        {
            public readonly Tunnel ParentTunnel;

            public readonly int MinWidth;

            public readonly TunnelType Type;

            public List<Point> Nodes
            {
                get;
                private set;
            }

            public List<VoronoiCell> Cells
            {
                get;
                private set;
            }

            public List<WayPoint> WayPoints
            {
                get;
                private set;
            }

            public Tunnel(TunnelType type, List<Point> nodes, int minWidth, Tunnel parentTunnel)
            {
                Type = type;
                MinWidth = minWidth;
                ParentTunnel = parentTunnel;
                Nodes = new List<Point>(nodes);
                Cells = new List<VoronoiCell>();
                WayPoints = new List<WayPoint>();
            }
        }

        public class Cave
        {
            public Rectangle Area;

            public readonly List<Tunnel> Tunnels = new List<Tunnel>();

            public Point StartPos, EndPos;

            public readonly HashSet<Mission> MissionsToDisplayOnSonar = new HashSet<Mission>();

            public readonly CaveGenerationParams CaveGenerationParams;

            public Cave(CaveGenerationParams caveGenerationParams, Rectangle area, Point startPos, Point endPos)
            {
                CaveGenerationParams = caveGenerationParams;
                Area = area;
                StartPos = startPos;
                EndPos = endPos;
            }
        }

        //how close the sub has to be to start/endposition to exit
        public const float ExitDistance = 6000.0f;
        public const int GridCellSize = 2000;
        private List<VoronoiCell>[,] cellGrid;
        private List<VoronoiCell> cells;

        public Rectangle AbyssArea
        {
            get;
            private set;
        }

        public int AbyssStart
        {
            get { return AbyssArea.Y + AbyssArea.Height; }
        }

        public int AbyssEnd
        {
            get { return AbyssArea.Y; }
        }

        public class AbyssIsland
        {
            public Rectangle Area;
            public readonly List<VoronoiCell> Cells;

            public AbyssIsland(Rectangle area, List<VoronoiCell> cells)
            {
                Debug.Assert(cells != null && cells.Any());
                Area = area;
                Cells = cells;
            }
        }
        public List<AbyssIsland> AbyssIslands = new List<AbyssIsland>();

        //TODO: make private
        public List<double> siteCoordsX, siteCoordsY;

        //TODO: make private
        public List<(Point point, double distance)> distanceField;

        private Point startPosition, endPosition;

        private readonly Rectangle borders;

        private List<Body> bodies;

        private List<Point> bottomPositions;

        //no need for frequent network updates, as currently the only thing that's synced
        //are the slowly moving ice chunks that move in a very predictable way
        const float NetworkUpdateInterval = 5.0f;
        private float networkUpdateTimer;

        public Vector2 StartPosition
        {
            get { return startPosition.ToVector2(); }
        }

        private Point startExitPosition;
        public Vector2 StartExitPosition
        {
            get { return startExitPosition.ToVector2(); }
        }

        public Point Size
        {
            get { return LevelData.Size; }
        }

        public Vector2 EndPosition
        {
            get { return endPosition.ToVector2(); }
        }

        private Point endExitPosition;
        public Vector2 EndExitPosition
        {
            get { return endExitPosition.ToVector2(); }
        }

        public int BottomPos
        {
            get;
            private set;
        }

        public int SeaFloorTopPos
        {
            get;
            private set;
        }

        public const float DefaultRealWorldCrushDepth = 3500.0f;

        /// <summary>
        /// The crush depth of a non-upgraded submarine in in-game coordinates. Note that this can be above the top of the level!
        /// </summary>
        public float CrushDepth
        {
            get
            {
                return LevelData.CrushDepth;
            }
        }

        /// <summary>
        /// The crush depth of a non-upgraded submarine in "real world units" (meters from the surface of Europa). Note that this can be above the top of the level!
        /// </summary>
        public float RealWorldCrushDepth
        {
            get
            {
                return LevelData.RealWorldCrushDepth;
            }
        }

        public LevelWall SeaFloor { get; private set; }

        public List<Ruin> Ruins { get; private set; }

        public List<Submarine> Wrecks { get; private set; }

        public Submarine BeaconStation { get; private set; }
        private Sonar beaconSonar;

        /// <summary>
        /// Special wall chunks that aren't part of the normal level geometry: includes things like the ocean floor, floating ice chunks and ice spires.
        /// </summary>
        public List<LevelWall> ExtraWalls { get; private set; }

        /// <summary>
        /// Purely decorative wall chunks whose positions don't need to be synced (e.g. the chunks created when a desctructible wall breaks)
        /// </summary>
        public List<LevelWall> UnsyncedExtraWalls { get; private set; }

        public List<Tunnel> Tunnels { get; private set; } = new List<Tunnel>();

        public List<Cave> Caves { get; private set; } = new List<Cave>();

        public List<InterestingPosition> PositionsOfInterest { get; private set; }

        public Submarine StartOutpost { get; private set; }
        public Submarine EndOutpost { get; private set; }

        private SubmarineInfo preSelectedStartOutpost;
        private SubmarineInfo preSelectedEndOutpost;

        public readonly LevelData LevelData;

        /// <summary>
        /// Random integers generated during the level generation. If these values differ between clients/server,
        /// it means the levels aren't identical for some reason and there will most likely be major ID mismatches.
        /// </summary>
        public enum LevelGenStage
        {
            LevelGenParams,
            Size,
            GenStart,
            TunnelGen1,
            TunnelGen2,
            AbyssGen,
            CaveGen,
            VoronoiGen,
            VoronoiGen2,
            VoronoiGen3,
            Ruins,
            Outposts,
            FloatingIce,
            LevelBodies,
            IceSpires,
            TopAndBottom,
            PlaceLevelObjects,
            GenerateItems,
            Finish
        }

        private readonly Dictionary<LevelGenStage, int> equalityCheckValues = Enum.GetValues(typeof(LevelGenStage))
            .Cast<LevelGenStage>()
            .Select(k => (k, 0))
            .ToDictionary();
        public IReadOnlyDictionary<LevelGenStage, int> EqualityCheckValues => equalityCheckValues;

        private void GenerateEqualityCheckValue(LevelGenStage stage)
        {
            equalityCheckValues[stage] = Rand.Int(int.MaxValue, Rand.RandSync.ServerAndClient);
        }

        private void SetEqualityCheckValue(LevelGenStage stage, int value)
        {
            equalityCheckValues[stage] = value;
        }

        private void ClearEqualityCheckValues()
        {
            foreach (LevelGenStage stage in Enum.GetValues(typeof(LevelGenStage)))
            {
                equalityCheckValues[stage] = 0;
            }
        }

        public List<Entity> EntitiesBeforeGenerate { get; private set; } = new List<Entity>();
        public int EntityCountBeforeGenerate { get; private set; }
        public int EntityCountAfterGenerate { get; private set; }

        public Body TopBarrier
        {
            get;
            private set;
        }

        public Body BottomBarrier
        {
            get;
            private set;
        }

        public LevelObjectManager LevelObjectManager { get; private set; }

        public bool Generating { get; private set; }

        public Location StartLocation { get; private set; }
        public Location EndLocation { get; private set; }

        public bool Mirrored
        {
            get;
            private set;
        }

        public string Seed 
        {
            get { return LevelData.Seed; }
        }


        public static float? ForcedDifficulty;
        public float Difficulty
        {
            get { return ForcedDifficulty ?? LevelData.Difficulty; }
        }
        
        /// <summary>
        /// Inclusive (matching the min an max values is accepted).
        /// </summary>
        public bool IsAllowedDifficulty(float minDifficulty, float maxDifficulty) => LevelData.IsAllowedDifficulty(minDifficulty, maxDifficulty);

        public LevelData.LevelType Type
        {
            get { return LevelData.Type; }
        }


        public bool IsEndBiome => LevelData.Biome != null && LevelData.Biome.IsEndBiome;

        /// <summary>
        /// Is there a loaded level set and is it an outpost?
        /// </summary>
        public static bool IsLoadedOutpost => Loaded?.Type == LevelData.LevelType.Outpost;

        /// <summary>
        /// Is there a loaded level set, and is it a friendly outpost (FriendlyNPC or Team1). Does not take reputation into account.
        /// </summary>
        public static bool IsLoadedFriendlyOutpost => 
            loaded?.Type == LevelData.LevelType.Outpost && 
            (loaded?.StartLocation?.Type?.OutpostTeam == CharacterTeamType.FriendlyNPC || loaded?.StartLocation?.Type?.OutpostTeam == CharacterTeamType.Team1);

        public LevelGenerationParams GenerationParams
        {
            get { return LevelData.GenerationParams; }
        }

        public Color BackgroundTextureColor
        {
            get { return LevelData.GenerationParams.BackgroundTextureColor; }
        }

        public Color BackgroundColor
        {
            get { return LevelData.GenerationParams.BackgroundColor; }
        }

        public Color WallColor
        {
            get { return LevelData.GenerationParams.WallColor; }
        }

        private Level(LevelData levelData) : base(null, 0)
        {
            LevelData = levelData;
            borders = new Rectangle(Point.Zero, levelData.Size);
        }

        public bool ShouldSpawnCrewInsideOutpost()
        {
            if (StartOutpost != null &&
                Type == LevelData.LevelType.Outpost &&
                ((StartOutpost.Info.OutpostGenerationParams is { SpawnCrewInsideOutpost: true } && StartOutpost.GetConnectedSubs().Any(s => s.Info.Type == SubmarineType.Player)) || Submarine.MainSub == null))
            {
                return GameMain.GameSession.Campaign?.CurrentLocation is not { IsFactionHostile: true };
            }
            return false;
        }

        public static Level Generate(LevelData levelData, bool mirror, Location startLocation, Location endLocation, SubmarineInfo startOutpost = null, SubmarineInfo endOutpost = null)
        {
            Debug.Assert(levelData.Biome != null);
            if (levelData.Biome == null) { throw new ArgumentException("Biome was null"); }
            if (levelData.Size.X <= 0) { throw new ArgumentException("Level width needs to be larger than zero."); }
            if (levelData.Size.Y <= 0) { throw new ArgumentException("Level height needs to be larger than zero."); }

            Level level = new Level(levelData)
            {
                preSelectedStartOutpost = startOutpost,
                preSelectedEndOutpost = endOutpost
            };
            level.Generate(mirror, startLocation, endLocation);
            return level;
        }

        private void Generate(bool mirror, Location startLocation, Location endLocation)
        {
            Loaded?.Remove();
            Loaded = this;
            Generating = true;
#if CLIENT
            Debug.Assert(GenerationParams.Identifier != "coldcavernstutorial" || GameMain.GameSession?.GameMode == null || GameMain.GameSession.GameMode is TutorialMode);
#endif
            Debug.Assert(GenerationParams.AnyBiomeAllowed || GenerationParams.AllowedBiomeIdentifiers.Contains(LevelData.Biome.Identifier), 
                "The selected generation parameters are not suitable for the current biome (resorted to a fallback due to no suitable parameters being found for this biome?)");
            DebugConsole.NewMessage("Level identifier: " + GenerationParams.Identifier);

            ClearEqualityCheckValues();
            EntitiesBeforeGenerate = GetEntities().ToList();
            EntityCountBeforeGenerate = EntitiesBeforeGenerate.Count();

            StartLocation = startLocation;
            EndLocation = endLocation;            

            ResetRandomSeed();

            GenerateEqualityCheckValue(LevelGenStage.GenStart);
            SetEqualityCheckValue(LevelGenStage.LevelGenParams, unchecked((int)GenerationParams.UintIdentifier));
            SetEqualityCheckValue(LevelGenStage.Size, borders.Width ^ borders.Height << 16);

            LevelObjectManager = new LevelObjectManager();

            if (Type == LevelData.LevelType.Outpost) { mirror = false; }
            Mirrored = mirror;

#if CLIENT
            if (backgroundCreatureManager == null)
            {
                var files = ContentPackageManager.EnabledPackages.All.SelectMany(p => p.GetFiles<BackgroundCreaturePrefabsFile>()).ToArray();
                backgroundCreatureManager = files.Any() ? new BackgroundCreatureManager(files) : new BackgroundCreatureManager("Content/BackgroundCreatures/BackgroundCreaturePrefabs.xml");
            }
#endif
            Stopwatch sw = new Stopwatch();
            sw.Start();
            
            PositionsOfInterest = new List<InterestingPosition>();
            ExtraWalls = new List<LevelWall>();
            UnsyncedExtraWalls = new List<LevelWall>();
            bodies = new List<Body>();
            List<Vector2> sites = new List<Vector2>();
            
            Voronoi voronoi = new Voronoi(1.0);

#if CLIENT
            renderer = new LevelRenderer(this);
#endif

            SeaFloorTopPos = GenerationParams.SeaFloorDepth + GenerationParams.MountainHeightMax + GenerationParams.SeaFloorVariance;

            int minMainPathWidth = Math.Min(GenerationParams.MinTunnelRadius, MaxSubmarineWidth);
            int minWidth = 500;
            if (Submarine.MainSub != null)
            {
                Rectangle dockedSubBorders = Submarine.MainSub.GetDockedBorders();
                dockedSubBorders.Inflate(dockedSubBorders.Size.ToVector2() * 0.15f);
                minWidth = Math.Max(dockedSubBorders.Width, dockedSubBorders.Height);
                minMainPathWidth = Math.Max(minMainPathWidth, minWidth);
                minMainPathWidth = Math.Min(minMainPathWidth, MaxSubmarineWidth);
            }
            minMainPathWidth = Math.Min(minMainPathWidth, borders.Width / 5);
            LevelData.MinMainPathWidth = minMainPathWidth;

            Rectangle pathBorders = borders;
            pathBorders.Inflate(
                -Math.Min(Math.Min(minMainPathWidth * 2, MaxSubmarineWidth), borders.Width / 5), 
                -Math.Min(minMainPathWidth * 2, borders.Height / 5));

            if (pathBorders.Width <= 0) { throw new InvalidOperationException($"The width of the level's path area is invalid ({pathBorders.Width})"); }
            if (pathBorders.Height <= 0) { throw new InvalidOperationException($"The height of the level's path area is invalid ({pathBorders.Height})"); }

            startPosition = new Point(
               (int)MathHelper.Lerp(minMainPathWidth, borders.Width - minMainPathWidth, GenerationParams.StartPosition.X),
               (int)MathHelper.Lerp(borders.Bottom - Math.Max(minMainPathWidth, ExitDistance * 1.5f), borders.Y + minMainPathWidth, GenerationParams.StartPosition.Y));
            startExitPosition = new Point(startPosition.X, borders.Bottom);

            endPosition = new Point(
               (int)MathHelper.Lerp(minMainPathWidth, borders.Width - minMainPathWidth, GenerationParams.EndPosition.X),
               (int)MathHelper.Lerp(borders.Bottom - Math.Max(minMainPathWidth, ExitDistance * 1.5f), borders.Y + minMainPathWidth, GenerationParams.EndPosition.Y));
            endExitPosition = new Point(endPosition.X, borders.Bottom);

            GenerateEqualityCheckValue(LevelGenStage.TunnelGen1);

            //----------------------------------------------------------------------------------
            //generate the initial nodes for the main path and smaller tunnels
            //----------------------------------------------------------------------------------

            Tunnel mainPath = new Tunnel(
                TunnelType.MainPath, 
                GeneratePathNodes(startPosition, endPosition, pathBorders, null, GenerationParams.MainPathVariance), 
                minMainPathWidth, parentTunnel: null);
            Tunnels.Add(mainPath);

            Tunnel startPath = null, endPath = null, endHole = null;
            if (GenerationParams.StartPosition.Y < 0.5f && (Mirrored ? !HasEndOutpost() : !HasStartOutpost()))
            {
                startPath = new Tunnel(
                    TunnelType.SidePath,
                    new List<Point>() { startExitPosition, startPosition }, 
                    minWidth, parentTunnel: mainPath);
                Tunnels.Add(startPath);
            }
            else
            {
                startExitPosition = startPosition;
            }
            if (GenerationParams.EndPosition.Y < 0.5f && (Mirrored ? !HasStartOutpost() : !HasEndOutpost()))
            {
                endPath = new Tunnel(
                    TunnelType.SidePath,
                    new List<Point>() { endPosition, endExitPosition },
                    minWidth, parentTunnel: mainPath);
                Tunnels.Add(endPath);
            }
            else
            {
                endExitPosition = endPosition;
            }

            if (GenerationParams.CreateHoleNextToEnd)
            {
                if (Mirrored)
                {
                    endHole = new Tunnel(
                        TunnelType.SidePath,
                        new List<Point>() { startPosition, new Point(0, startPosition.Y) },
                        minWidth, parentTunnel: mainPath);
                }
                else
                {
                    endHole = new Tunnel(
                        TunnelType.SidePath,
                        new List<Point>() { endPosition, new Point(Size.X, endPosition.Y) },
                        minWidth, parentTunnel: mainPath);
                }
                Tunnels.Add(endHole);
            }

            //create a tunnel from the lowest point in the main path to the abyss
            //to ensure there's a way to the abyss in all levels
            Tunnel abyssTunnel = null;
            if (GenerationParams.CreateHoleToAbyss)
            {
                Point lowestPoint = mainPath.Nodes.First();
                foreach (var pathNode in mainPath.Nodes)
                {
                    if (pathNode.Y < lowestPoint.Y) { lowestPoint = pathNode; }
                }
                abyssTunnel = new Tunnel(
                    TunnelType.SidePath,
                    new List<Point>() { lowestPoint, new Point(lowestPoint.X, 0) },
                    minWidth, parentTunnel: mainPath);
                Tunnels.Add(abyssTunnel);
            }

            int sideTunnelCount = Rand.Range(GenerationParams.SideTunnelCount.X, GenerationParams.SideTunnelCount.Y + 1, Rand.RandSync.ServerAndClient);

            for (int j = 0; j < sideTunnelCount; j++)
            {
                if (mainPath.Nodes.Count < 4) { break; }
                var validTunnels = Tunnels.FindAll(t => t.Type != TunnelType.Cave && t != startPath && t != endPath && t != endHole && t != abyssTunnel);

                Tunnel tunnelToBranchOff = validTunnels[Rand.Int(validTunnels.Count, Rand.RandSync.ServerAndClient)];
                if (tunnelToBranchOff == null) { tunnelToBranchOff = mainPath; }

                Point branchStart = tunnelToBranchOff.Nodes[Rand.Range(0, tunnelToBranchOff.Nodes.Count / 3, Rand.RandSync.ServerAndClient)];
                Point branchEnd = tunnelToBranchOff.Nodes[Rand.Range(tunnelToBranchOff.Nodes.Count / 3 * 2, tunnelToBranchOff.Nodes.Count - 1, Rand.RandSync.ServerAndClient)];

                var sidePathNodes = GeneratePathNodes(branchStart, branchEnd, pathBorders, tunnelToBranchOff, GenerationParams.SideTunnelVariance);
                //make sure the path is wide enough to pass through
                int pathWidth = Rand.Range(GenerationParams.MinSideTunnelRadius.X, GenerationParams.MinSideTunnelRadius.Y, Rand.RandSync.ServerAndClient);
                Tunnels.Add(new Tunnel(TunnelType.SidePath, sidePathNodes, pathWidth, parentTunnel: tunnelToBranchOff));
            }
            
            Debug.WriteLine("Tunnels after generating main tunnels: " + Tunnels.Count);
            
            CalculateTunnelDistanceField(null);
            GenerateSeaFloorPositions();

            GenerateEqualityCheckValue(LevelGenStage.TunnelGen2);

            GenerateAbyssArea();

            GenerateEqualityCheckValue(LevelGenStage.AbyssGen);

            GenerateCaves(mainPath);
            Debug.WriteLine("Tunnels after generating caves: " + Tunnels.Count);

            GenerateEqualityCheckValue(LevelGenStage.CaveGen);

            //----------------------------------------------------------------------------------
            //generate voronoi sites
            //----------------------------------------------------------------------------------

            GenerateVoronoiSites();
            Debug.WriteLine("Site coords after generating voronoi sites: " + siteCoordsX.Count);

            GenerateEqualityCheckValue(LevelGenStage.VoronoiGen);

            //----------------------------------------------------------------------------------
            // construct the voronoi graph and cells
            //----------------------------------------------------------------------------------

            Stopwatch sw2 = new Stopwatch();
            sw2.Start();

            Debug.Assert(siteCoordsX.Count == siteCoordsY.Count);

            int remainingRetries = 5;
            bool voronoiGraphInvalid = false;
            do
            {
                remainingRetries--;
                voronoiGraphInvalid = false;
                //construct voronoi cells based on the graph edges
                List<GraphEdge> graphEdges = voronoi.MakeVoronoiGraph(siteCoordsX.ToArray(), siteCoordsY.ToArray(), borders.Width, borders.Height);
                Debug.WriteLine("Graph edges after generating voronoi graph: " + graphEdges.Count);
                cells = CaveGenerator.GraphEdgesToCells(graphEdges, borders, GridCellSize, out cellGrid);
                for (int i = 0; i < cells.Count; i++)
                {
                    for (int j = i + 1; j < cells.Count; j++)
                    {
                        //sites can never be inside multiple cells in a voronoi graph by definition
                        //if they are, that'll cause severe issues with the rest of the level generation.

                        //There seems to be a very rare issue that sometimes causes the graph to generate incorrectly (see #10944 and #12980),
                        //leading to a crash due. I haven't been able to figure out what's causing that - there don't seem to be any issues in the sites,
                        //so I'm getting the feeling it could be a bug with the voronoi graph generation.

                        //If that happens, let's just retry a couple of times (re-randomizing the sites and regenerating
                        //the map seems to fix the issue in all cases I've seen)
                        if (cells[j].IsPointInside(cells[i].Center))
                        {
                            voronoiGraphInvalid = true;
                            break;
                        }
                        if (voronoiGraphInvalid) { break; }
                    }
                }
                if (voronoiGraphInvalid)
                {
                    string errorMsg = "Unknown error during level generation. Invalid voronoi graph: the same voronoi site was inside multiple cells.";
                    if (remainingRetries > 0)
                    {
                        DebugConsole.AddWarning(errorMsg + " Retrying...");
                        GenerateVoronoiSites();
                    }
                    else
                    {
                        //throw a console error and let the generation finish, hoping for the best
                        DebugConsole.ThrowError(errorMsg);
                    }
                }
            } while (remainingRetries > 0 && voronoiGraphInvalid);
            
            Debug.WriteLine("Cells after generating initial cells:" + cells.Count);

            GenerateAbyssGeometry();
            Debug.WriteLine("Tunnels after generating abyss geometry: " + Tunnels.Count);
            GenerateAbyssPositions();

            Debug.WriteLine("find cells: " + sw2.ElapsedMilliseconds + " ms");
            sw2.Restart();

            //----------------------------------------------------------------------------------
            // generate a path through the tunnel nodes
            //----------------------------------------------------------------------------------

            ResetRandomSeed();
            List<VoronoiCell> pathCells = new List<VoronoiCell>();
            foreach (Tunnel tunnel in Tunnels)
            {
                CaveGenerator.GeneratePath(tunnel, this);
                if (tunnel.Type == TunnelType.MainPath || tunnel.Type == TunnelType.SidePath)
                {
                    if (tunnel != startPath && tunnel != endPath && tunnel != endHole)
                    {
                        var distinctCells = tunnel.Cells.Distinct().ToList();
                        for (int i = 2; i < distinctCells.Count; i += 3)
                        {
                            PositionsOfInterest.Add(new InterestingPosition(
                                new Point((int)distinctCells[i].Site.Coord.X, (int)distinctCells[i].Site.Coord.Y),
                                tunnel.Type == TunnelType.MainPath ? PositionType.MainPath : PositionType.SidePath, 
                                Caves.Find(cave => cave.Tunnels.Contains(tunnel))));
                        }
                    }
                }

                bool connectToParentTunnel = tunnel.Type != TunnelType.Cave || tunnel.ParentTunnel.Type == TunnelType.Cave;
                GenerateWaypoints(tunnel, parentTunnel: connectToParentTunnel ? tunnel.ParentTunnel : null);
                
                EnlargePath(tunnel.Cells, tunnel.MinWidth);
                foreach (var pathCell in tunnel.Cells)
                {
                    MarkEdges(pathCell, tunnel.Type);
                    if (!pathCells.Contains(pathCell))
                    {
                        pathCells.Add(pathCell);
                    }
                }

                static void MarkEdges(VoronoiCell cell, TunnelType tunnelType)
                {
                    foreach (GraphEdge edge in cell.Edges)
                    {
                        switch (tunnelType)
                        {
                            case TunnelType.MainPath:
                                edge.NextToMainPath = true;
                                break;
                            case TunnelType.SidePath:
                                edge.NextToSidePath = true;
                                break;
                            case TunnelType.Cave:
                                edge.NextToCave = true;
                                break;
                        }
                    }
                }
            }

            var potentialIslands = new List<VoronoiCell>();
            foreach (var cell in pathCells)
            {
                if (GetDistToTunnel(cell.Center, mainPath) < minMainPathWidth || 
                    (startPath != null && GetDistToTunnel(cell.Center, startPath) < minMainPathWidth) ||
                    (endPath != null && GetDistToTunnel(cell.Center, endPath) < minMainPathWidth) ||
                    (endHole != null && GetDistToTunnel(cell.Center, endHole) < minMainPathWidth)) { continue; }
                if (cell.Edges.Any(e => e.AdjacentCell(cell)?.CellType != CellType.Path || e.NextToCave)) { continue; }
                if (PositionsOfInterest.Any(p => cell.IsPointInside(p.Position.ToVector2()))) { continue; }
                potentialIslands.Add(cell);
            }
            for (int i = 0; i < GenerationParams.IslandCount; i++)
            {
                if (potentialIslands.Count == 0) { break; }
                var island = potentialIslands.GetRandom(Rand.RandSync.ServerAndClient);
                island.CellType = CellType.Solid;
                island.Island = true;
                pathCells.Remove(island);
            }

            foreach (InterestingPosition positionOfInterest in PositionsOfInterest)
            {
                WayPoint wayPoint = new WayPoint(
                    positionOfInterest.Position.ToVector2(),
                    SpawnType.Enemy,
                    submarine: null)
                {
                    Cave = positionOfInterest.Cave,
                    Ruin = positionOfInterest.Ruin
                };
            }

            startPosition.X = (int)pathCells[0].Site.Coord.X;
            startExitPosition.X = startPosition.X;
            
            Debug.WriteLine("Waypoints after voronoi gen2: " + WayPoint.WayPointList.Count);

            GenerateEqualityCheckValue(LevelGenStage.VoronoiGen2);

            //----------------------------------------------------------------------------------
            // remove unnecessary cells and create some holes at the bottom of the level
            //----------------------------------------------------------------------------------

            if (GenerationParams.NoLevelGeometry)
            {
                cells.ForEach(c => c.CellType = CellType.Removed);
                cells.Clear();
            }

            cells = cells.Except(pathCells).ToList();
            //remove cells from the edges and bottom of the map because a clean-cut edge of the level looks bad
            cells.ForEachMod(c => 
            { 
                if (c.Edges.Any(e => !MathUtils.NearlyEqual(e.Point1.Y, Size.Y) && e.AdjacentCell(c) == null))
                {
                    c.CellType = CellType.Removed;
                    cells.Remove(c);
                }
            });

            int xPadding = borders.Width / 5;
            pathCells.AddRange(CreateHoles(GenerationParams.BottomHoleProbability, new Rectangle(xPadding, 0, borders.Width - xPadding * 2, Size.Y / 2), minMainPathWidth));

            foreach (VoronoiCell cell in cells)
            {
                if (cell.Site.Coord.Y < borders.Height / 2) { continue; }
                cell.Edges.ForEach(e => e.OutsideLevel = true);
            }

            foreach (AbyssIsland abyssIsland in AbyssIslands)
            {
                abyssIsland.Cells.RemoveAll(c => c.CellType == CellType.Path);
                cells.AddRange(abyssIsland.Cells);
            }
            
            // Reset the seed, because CreateHoles breaks the determinism between mirrored and non-mirrored levels. Happens because there's a different amount of cells, which leads to CreateHoles() doing different amount of calls to Rand.GetRNG() when iterating through the cells).
            ResetRandomSeed();
            
            Debug.WriteLine("Cells after creating holes: " + cells.Count);
            
            List<Point> ruinPositions = new List<Point>();
            int ruinCount = GenerationParams.UseRandomRuinCount() 
                ? Rand.Range(GenerationParams.MinRuinCount, GenerationParams.MaxRuinCount + 1, Rand.RandSync.ServerAndClient) 
                : GenerationParams.RuinCount;

            bool hasRuinMissions = GameMain.GameSession?.GameMode?.Missions.Any(m => m.Prefab.RequireRuin) ?? false;
            if (hasRuinMissions)
            {
                ruinCount = Math.Max(ruinCount, 1);
            }

            for (int i = 0; i < ruinCount; i++)
            {
                if (!hasRuinMissions) 
                { 
                    if (Rand.Range(0f, 1f, Rand.RandSync.ServerAndClient) >= GenerationParams.RuinSpawnProbability) { continue; } 
                } 
                
                Point ruinSize = new Point(5000);
                int limitLeft = Math.Max(startPosition.X, ruinSize.X / 2);
                int limitRight = Math.Min(endPosition.X, Size.X - ruinSize.X / 2);
                Rectangle limits = new Rectangle(limitLeft, ruinSize.Y, limitRight - limitLeft, Size.Y - ruinSize.Y);
                Debug.Assert(limits.Width > 0);
                Debug.Assert(limits.Height > 0);
                ruinPositions.Add(FindPosAwayFromMainPath((Math.Max(ruinSize.X, ruinSize.Y) + mainPath.MinWidth) * 1.2f, asCloseAsPossible: true, limits: limits));
                CalculateTunnelDistanceField(ruinPositions);
            }

            //----------------------------------------------------------------------------------
            // initialize the cells that are still left and insert them into the cell grid
            //----------------------------------------------------------------------------------

            foreach (VoronoiCell cell in pathCells)
            {
                cell.Edges.ForEach(e => e.OutsideLevel = false);
                cell.CellType = CellType.Path;
                cells.Remove(cell);
            }
            
            for (int x = 0; x < cellGrid.GetLength(0); x++)
            {
                for (int y = 0; y < cellGrid.GetLength(1); y++)
                {
                    cellGrid[x, y].Clear();
                }
            }

            //----------------------------------------------------------------------------------
            // mirror if needed
            //----------------------------------------------------------------------------------

            if (mirror)
            {
                HashSet<GraphEdge> mirroredEdges = new HashSet<GraphEdge>();
                HashSet<Site> mirroredSites = new HashSet<Site>();
                List<VoronoiCell> allCells = new List<VoronoiCell>(cells);
                allCells.AddRange(pathCells);
                foreach (VoronoiCell cell in allCells)
                {
                    foreach (GraphEdge edge in cell.Edges)
                    {
                        if (mirroredEdges.Contains(edge)) { continue; }
                        edge.Point1.X = borders.Width - edge.Point1.X;
                        edge.Point2.X = borders.Width - edge.Point2.X;
                        if (edge.Site1 != null && !mirroredSites.Contains(edge.Site1))
                        {
                            //make sure that sites right at the edge of a grid cell end up in the same cell as in the non-mirrored level
                            if (edge.Site1.Coord.X % GridCellSize < 1.0f &&
                                edge.Site1.Coord.X % GridCellSize >= 0.0f) { edge.Site1.Coord.X += 1.0f; }
                            edge.Site1.Coord.X = borders.Width - edge.Site1.Coord.X;
                            mirroredSites.Add(edge.Site1);
                        }
                        if (edge.Site2 != null && !mirroredSites.Contains(edge.Site2))
                        {
                            if (edge.Site2.Coord.X % GridCellSize < 1.0f &&
                                edge.Site2.Coord.X % GridCellSize >= 0.0f) { edge.Site2.Coord.X += 1.0f; }
                            edge.Site2.Coord.X = borders.Width - edge.Site2.Coord.X;
                            mirroredSites.Add(edge.Site2);
                        }
                        mirroredEdges.Add(edge);
                    }
                }

                foreach (AbyssIsland island in AbyssIslands)
                {
                    island.Area = new Rectangle(borders.Width - island.Area.Right, island.Area.Y, island.Area.Width, island.Area.Height);
                    foreach (var cell in island.Cells)
                    {
                        if (!mirroredSites.Contains(cell.Site))
                        {
                            if (cell.Site.Coord.X % GridCellSize < 1.0f &&
                                cell.Site.Coord.X % GridCellSize >= 0.0f) { cell.Site.Coord.X += 1.0f; }
                            cell.Site.Coord.X = borders.Width - cell.Site.Coord.X;
                            mirroredSites.Add(cell.Site);
                        }
                    }
                }

                for (int i = 0; i < ruinPositions.Count; i++)
                {
                    ruinPositions[i] = new Point(borders.Width - ruinPositions[i].X, ruinPositions[i].Y);
                }

                foreach (Cave cave in Caves)
                {
                    cave.Area = new Rectangle(borders.Width - cave.Area.Right, cave.Area.Y, cave.Area.Width, cave.Area.Height);
                    cave.StartPos = new Point(borders.Width - cave.StartPos.X, cave.StartPos.Y);
                    cave.EndPos = new Point(borders.Width - cave.EndPos.X, cave.EndPos.Y);
                }

                foreach (Tunnel tunnel in Tunnels)
                {
                    for (int i = 0; i < tunnel.Nodes.Count; i++)
                    {
                        tunnel.Nodes[i] = new Point(borders.Width - tunnel.Nodes[i].X, tunnel.Nodes[i].Y);
                    }
                }

                for (int i = 0; i < PositionsOfInterest.Count; i++)
                {
                    PositionsOfInterest[i] = new InterestingPosition(
                        new Point(borders.Width - PositionsOfInterest[i].Position.X, PositionsOfInterest[i].Position.Y),
                        PositionsOfInterest[i].PositionType)
                    {
                        Submarine = PositionsOfInterest[i].Submarine,
                        Cave = PositionsOfInterest[i].Cave,
                        Ruin = PositionsOfInterest[i].Ruin,
                    };
                }

                foreach (WayPoint waypoint in WayPoint.WayPointList)
                {
                    if (waypoint.Submarine != null) continue;
                    waypoint.Move(new Vector2((borders.Width / 2 - waypoint.Position.X) * 2, 0.0f));
                }

                for (int i = 0; i < bottomPositions.Count; i++)
                {
                    bottomPositions[i] = new Point(borders.Size.X - bottomPositions[i].X, bottomPositions[i].Y);
                }
                bottomPositions.Reverse();

                startPosition.X = borders.Width - startPosition.X;
                endPosition.X = borders.Width - endPosition.X;

                startExitPosition.X = borders.Width - startExitPosition.X;
                endExitPosition.X = borders.Width - endExitPosition.X;

                CalculateTunnelDistanceField(ruinPositions);
            }

            foreach (VoronoiCell cell in cells)
            {
                int x = (int)Math.Floor(cell.Site.Coord.X / GridCellSize);
                x = MathHelper.Clamp(x, 0, cellGrid.GetLength(0) - 1);
                int y = (int)Math.Floor(cell.Site.Coord.Y / GridCellSize);
                y = MathHelper.Clamp(y, 0, cellGrid.GetLength(1) - 1);

                cellGrid[x, y].Add(cell);
            }

            float destructibleWallRatio = MathHelper.Lerp(0.2f, 1.0f, LevelData.Difficulty / 100.0f);
            foreach (Cave cave in Caves)
            {
                if (cave.Area.Y > 0) 
                {
                    List<VoronoiCell> cavePathCells = CreatePathToClosestTunnel(cave.StartPos);

                    var mainTunnel = cave.Tunnels.Find(t => t.ParentTunnel.Type != TunnelType.Cave);

                    WayPoint prevWp = mainTunnel.WayPoints.First();
                    if (prevWp != null)
                    {
                        for (int i = 0; i < cavePathCells.Count; i++)
                        {
                            var connectingEdge = i > 0 ? cavePathCells[i].Edges.Find(e => e.AdjacentCell(cavePathCells[i]) == cavePathCells[i - 1]) : null;
                            if (connectingEdge != null)
                            {
                                var edgeWayPoint = new WayPoint(connectingEdge.Center, SpawnType.Path, submarine: null)
                                {
                                    Cave = cave
                                };
                                ConnectWaypoints(prevWp, edgeWayPoint, 500.0f);
                                prevWp = edgeWayPoint;
                            }
                            var newWaypoint = new WayPoint(cavePathCells[i].Center, SpawnType.Path, submarine: null)
                            {
                                Cave = cave
                            };
                            ConnectWaypoints(prevWp, newWaypoint, 500.0f);
                            prevWp = newWaypoint;
                        }
                        var closestPathPoint = FindClosestWayPoint(prevWp.WorldPosition, mainTunnel.ParentTunnel.WayPoints);
                        ConnectWaypoints(prevWp, closestPathPoint, 500.0f);
                    }
                }

                List<VoronoiCell> caveCells = new List<VoronoiCell>();
                caveCells.AddRange(cave.Tunnels.SelectMany(t => t.Cells));
                foreach (var caveCell in caveCells)
                {
                    if (PositionsOfInterest.Any(p => caveCell.IsPointInside(p.Position.ToVector2()))) { continue; }
                    if (Rand.Range(0.0f, 1.0f, Rand.RandSync.ServerAndClient) < destructibleWallRatio * cave.CaveGenerationParams.DestructibleWallRatio)
                    {
                        var chunk = CreateIceChunk(caveCell.Edges, caveCell.Center, health: 50.0f);
                        if (chunk != null)
                        {
                            chunk.Body.BodyType = BodyType.Static;
                            ExtraWalls.Add(chunk);
                        }
                    }
                }
            }

            GenerateEqualityCheckValue(LevelGenStage.VoronoiGen3);

            //----------------------------------------------------------------------------------
            // create some ruins
            //----------------------------------------------------------------------------------

            Ruins = new List<Ruin>();
            for (int i = 0; i < ruinPositions.Count; i++)
            {
                Rand.SetSyncedSeed(ToolBox.StringToInt(Seed) + i);
                GenerateRuin(ruinPositions[i], mirror, hasRuinMissions);
            }
            
            Debug.WriteLine("Waypoints after creating ruins: " + WayPoint.WayPointList.Count);
            
            GenerateEqualityCheckValue(LevelGenStage.Ruins);

            //----------------------------------------------------------------------------------
            // create floating ice chunks
            //----------------------------------------------------------------------------------

            if (GenerationParams.FloatingIceChunkCount > 0)
            {
                List<Point> iceChunkPositions = new List<Point>();
                foreach (InterestingPosition pos in PositionsOfInterest)
                {
                    if (pos.PositionType != PositionType.MainPath && pos.PositionType != PositionType.SidePath) { continue; }
                    if (pos.Position.X < pathBorders.X + minMainPathWidth || pos.Position.X > pathBorders.Right - minMainPathWidth) { continue; }
                    if (Math.Abs(pos.Position.X - startPosition.X) < minMainPathWidth * 2 || Math.Abs(pos.Position.X - endPosition.X) < minMainPathWidth * 2) { continue; }
                    if (GetTooCloseCells(pos.Position.ToVector2(), minMainPathWidth * 0.7f).Count > 0) { continue; }
                    iceChunkPositions.Add(pos.Position);
                }
                        
                for (int i = 0; i < GenerationParams.FloatingIceChunkCount; i++)
                {
                    if (iceChunkPositions.Count == 0) { break; }
                    Point selectedPos = iceChunkPositions[Rand.Int(iceChunkPositions.Count, Rand.RandSync.ServerAndClient)];
                    float chunkRadius = Rand.Range(500.0f, 1000.0f, Rand.RandSync.ServerAndClient);
                    var vertices = CaveGenerator.CreateRandomChunk(chunkRadius, 8, chunkRadius * 0.8f);
                    var chunk = CreateIceChunk(vertices, selectedPos.ToVector2());
                    chunk.MoveAmount = new Vector2(0.0f, minMainPathWidth * 0.7f);
                    chunk.MoveSpeed = Rand.Range(100.0f, 200.0f, Rand.RandSync.ServerAndClient);
                    ExtraWalls.Add(chunk);
                    iceChunkPositions.Remove(selectedPos);
                }
            }

            GenerateEqualityCheckValue(LevelGenStage.FloatingIce);

            //----------------------------------------------------------------------------------
            // generate the bodies and rendered triangles of the cells
            //----------------------------------------------------------------------------------

            foreach (VoronoiCell cell in cells)
            {
                foreach (GraphEdge ge in cell.Edges)
                {
                    VoronoiCell adjacentCell = ge.AdjacentCell(cell);
                    ge.IsSolid = adjacentCell == null || !cells.Contains(adjacentCell);
                }
            }

            List<VoronoiCell> cellsWithBody = new List<VoronoiCell>(cells);
            if (GenerationParams.CellRoundingAmount > 0.01f || GenerationParams.CellIrregularity > 0.01f)
            {
                foreach (VoronoiCell cell in cellsWithBody)
                {
                    CaveGenerator.RoundCell(cell,
                        minEdgeLength: GenerationParams.CellSubdivisionLength,
                        roundingAmount: GenerationParams.CellRoundingAmount,
                        irregularity: GenerationParams.CellIrregularity,
                        minThickness: GenerationParams.WallTextureExpandInwardsAmount);
                }
            }

#if CLIENT
            List<(List<VoronoiCell> cells, Cave parentCave)> cellBatches = new List<(List<VoronoiCell>, Cave)>
            {
                (cellsWithBody.ToList(), null)
            };
            foreach (Cave cave in Caves)
            {
                (List<VoronoiCell> cells, Cave parentCave) newCellBatch = (new List<VoronoiCell>(), cave);
                foreach (var caveCell in cave.Tunnels.SelectMany(t => t.Cells))
                {
                    foreach (var edge in caveCell.Edges)
                    {
                        if (!edge.NextToCave) { continue; }
                        if (edge.Cell1?.CellType == CellType.Solid && !newCellBatch.cells.Contains(edge.Cell1)) 
                        {
                            Debug.Assert(cellsWithBody.Contains(edge.Cell1));
                            cellBatches.ForEach(cb => cb.cells.Remove(edge.Cell1));
                            newCellBatch.cells.Add(edge.Cell1); 
                        }
                        if (edge.Cell2?.CellType == CellType.Solid && !newCellBatch.cells.Contains(edge.Cell2))
                        {
                            Debug.Assert(cellsWithBody.Contains(edge.Cell2));
                            cellBatches.ForEach(cb => cb.cells.Remove(edge.Cell2));
                            newCellBatch.cells.Add(edge.Cell2);
                        }
                    }
                }
                if (newCellBatch.cells.Any())
                {
                    cellBatches.Add(newCellBatch);
                }
            }
            cellBatches.RemoveAll(cb => !cb.cells.Any());

            int totalCellsInBatches = cellBatches.Sum(cb => cb.cells.Count);
            Debug.Assert(cellsWithBody.Count == totalCellsInBatches);

            List<List<Vector2[]>> triangleLists = new List<List<Vector2[]>>();
            foreach ((List<VoronoiCell> cells, Cave cave) cellBatch in cellBatches)
            {
                bodies.Add(CaveGenerator.GeneratePolygons(cellBatch.cells, this, out List<Vector2[]> triangles));
                triangleLists.Add(triangles);
            }
#else
            bodies.Add(CaveGenerator.GeneratePolygons(cellsWithBody, this, out List<Vector2[]> triangles));
#endif
            foreach (VoronoiCell cell in cells)
            {
                CompareCCW compare = new CompareCCW(cell.Center);
                foreach (GraphEdge edge in cell.Edges)
                {
                    //remove references to cells that we failed to generate a body for
                    if (edge.Cell1 != null && edge.Cell1.Body == null && edge.Cell1.CellType != CellType.Empty) { edge.Cell1 = null; }
                    if (edge.Cell2 != null && edge.Cell2.Body == null && edge.Cell2.CellType != CellType.Empty) { edge.Cell2 = null; }

                    //make the order of the points CCW
                    if (compare.Compare(edge.Point1, edge.Point2) == -1)
                    {
                        var temp = edge.Point1;
                        edge.Point1 = edge.Point2;
                        edge.Point2 = temp;
                    }
                }
            }

#if CLIENT
            Debug.Assert(triangleLists.Count == cellBatches.Count);
            for (int i = 0; i < triangleLists.Count; i++)
            {
                //the solid black inner part of the wall
                var wallVerts = CaveGenerator.GenerateWallEdgeVertices(cellBatches[i].cells,
                        expandOutwards: 0.0f, expandInwards: GenerationParams.WallTextureExpandInwardsAmount,
                        outerColor: GenerationParams.WallColor, innerColor: Color.Black,
                        this, zCoord: 0.9f, preventExpandThroughCell: true).ToArray();
                CaveGenerator.GenerateTextureCoordinates(wallVerts, GenerationParams.WallTextureSize);
                renderer.SetVertices(
                   wallVerts,
                    CaveGenerator.GenerateWallEdgeVertices(
                        cellBatches[i].cells, 
                        GenerationParams.WallEdgeExpandOutwardsAmount, GenerationParams.WallEdgeExpandInwardsAmount,
                        outerColor: GenerationParams.WallColor, innerColor: GenerationParams.WallColor,
                        this, zCoord: 0.9f).ToArray(),
                    CaveGenerator.GenerateWallVertices(triangleLists[i], Color.Black, zCoord: 0.9f).ToArray(),
                    cellBatches[i].parentCave?.CaveGenerationParams?.WallSprite == null ? GenerationParams.WallSprite.Texture : cellBatches[i].parentCave.CaveGenerationParams.WallSprite.Texture,
                    cellBatches[i].parentCave?.CaveGenerationParams?.WallEdgeSprite == null ? GenerationParams.WallEdgeSprite.Texture : cellBatches[i].parentCave.CaveGenerationParams.WallEdgeSprite.Texture);
            }
#endif

            GenerateEqualityCheckValue(LevelGenStage.LevelBodies);

            //----------------------------------------------------------------------------------
            // create ice spires
            //----------------------------------------------------------------------------------

            // Reset the seed, so that the level geometry doesn't affect the spires. Level geometry can be slightly different between mirrored and non-mirrored levels, and spires affect where wrecks and beacons can be placed.
            ResetRandomSeed();
            
            List<GraphEdge> usedSpireEdges = new List<GraphEdge>();
            for (int i = 0; i < GenerationParams.IceSpireCount; i++)
            {
                var spire = CreateIceSpire(usedSpireEdges);
                if (spire != null) { ExtraWalls.Add(spire); };
            }

            GenerateEqualityCheckValue(LevelGenStage.IceSpires);

            //----------------------------------------------------------------------------------
            // connect side paths and cave branches to their parents
            //----------------------------------------------------------------------------------

            foreach (Ruin ruin in Ruins)
            {
                GenerateRuinWayPoints(ruin);
            }

            foreach (Tunnel tunnel in Tunnels)
            {
                if (tunnel.ParentTunnel == null) { continue; }
                if (tunnel.Type == TunnelType.Cave && tunnel.ParentTunnel == mainPath) { continue; }
                ConnectWaypoints(tunnel, tunnel.ParentTunnel);
            }

            //----------------------------------------------------------------------------------
            // create outposts at the start and end of the level
            //----------------------------------------------------------------------------------

            CreateOutposts();

            GenerateEqualityCheckValue(LevelGenStage.Outposts);

            //----------------------------------------------------------------------------------
            // top barrier & sea floor
            //----------------------------------------------------------------------------------

            TopBarrier = GameMain.World.CreateEdge( 
                ConvertUnits.ToSimUnits(new Vector2(borders.X, 0)), 
                ConvertUnits.ToSimUnits(new Vector2(borders.Right, 0)));
            //for debugging purposes
            TopBarrier.UserData = "topbarrier";
            TopBarrier.SetTransform(ConvertUnits.ToSimUnits(new Vector2(0.0f, borders.Height)), 0.0f);                
            TopBarrier.BodyType = BodyType.Static;
            TopBarrier.CollisionCategories = Physics.CollisionLevel;

            bodies.Add(TopBarrier);

            GenerateSeaFloor();
            
            Debug.WriteLine("Waypoints after creating sea floor: " + WayPoint.WayPointList.Count);

            if (mirror)
            {
                Point tempP = startPosition;
                startPosition = endPosition;
                endPosition = tempP;

                tempP = startExitPosition;
                startExitPosition = endExitPosition;
                endExitPosition = tempP;
            }
            if (StartOutpost != null)
            {
                startExitPosition = StartOutpost.WorldPosition.ToPoint();
                startPosition = startExitPosition;
            }
            if (EndOutpost != null)
            {
                endExitPosition = EndOutpost.WorldPosition.ToPoint();
                endPosition = endExitPosition;
            }

            CreateWrecks();
            CreateBeaconStation();

            GenerateEqualityCheckValue(LevelGenStage.TopAndBottom);

            ResetRandomSeed();
            LevelObjectManager.PlaceObjects(this, GenerationParams.LevelObjectAmount);

            GenerateEqualityCheckValue(LevelGenStage.PlaceLevelObjects);

            GenerateItems();

            foreach (Submarine sub in Submarine.Loaded)
            {
                if (sub.Info.IsOutpost)
                {
#if CLIENT
                    if (GameMain.GameSession?.GameMode is TutorialMode) { continue; }
#endif
                    OutpostGenerator.PowerUpOutpost(sub);
                }
            }

            GenerateEqualityCheckValue(LevelGenStage.GenerateItems);

#if CLIENT
            backgroundCreatureManager.SpawnCreatures(this, GenerationParams.BackgroundCreatureAmount);
#endif

            foreach (VoronoiCell cell in cells)
            {
                foreach (GraphEdge edge in cell.Edges)
                {
                    //edge.Cell1 = null;
                    //edge.Cell2 = null;
                    edge.Site1 = null;
                    edge.Site2 = null;
                }
            }

            //initialize MapEntities that aren't in any sub (e.g. items inside ruins)
            MapEntity.MapLoaded(MapEntity.MapEntityList.FindAll(me => me.Submarine == null), false);

            Debug.WriteLine("Generatelevel: " + sw2.ElapsedMilliseconds + " ms");
            sw2.Restart();

            Debug.WriteLine("**********************************************************************************");
            Debug.WriteLine("Generated a map with " + siteCoordsX.Count + " sites in " + sw.ElapsedMilliseconds + " ms");
            Debug.WriteLine("Seed: " + Seed);
            Debug.WriteLine("**********************************************************************************");

            if (GameSettings.CurrentConfig.VerboseLogging)
            {
                DebugConsole.NewMessage("Generated level with the seed " + Seed + " (type: " + GenerationParams.Identifier + ")", Color.White);
            }

            EntityCountAfterGenerate = GetEntities().Count();

#if SERVER
            if (GameMain.Server.EntityEventManager.Events.Count() > 0)
            {
                DebugConsole.NewMessage("WARNING: Entity events have been created during level generation. Events should not be created until the round is fully initialized.");
            }
            GameMain.Server.EntityEventManager.Clear();
#endif

            GenerateEqualityCheckValue(LevelGenStage.Finish);
            //assign an ID to make entity events work
            //ID = FindFreeID();
            Generating = false;
        }


        private void GenerateVoronoiSites()
        {
            ResetRandomSeed();
            Point siteInterval = GenerationParams.VoronoiSiteInterval;
            int siteIntervalSqr = (siteInterval.X * siteInterval.X + siteInterval.Y * siteInterval.Y);
            Point siteVariance = GenerationParams.VoronoiSiteVariance;
            siteCoordsX = new List<double>((borders.Height / siteInterval.Y) * (borders.Width / siteInterval.Y));
            siteCoordsY = new List<double>((borders.Height / siteInterval.Y) * (borders.Width / siteInterval.Y));
            const int caveSiteInterval = 500;
            for (int x = siteInterval.X / 2; x < borders.Width - siteInterval.X / 2; x += siteInterval.X)
            {
                for (int y = siteInterval.Y / 2; y < borders.Height - siteInterval.Y / 2; y += siteInterval.Y)
                {
                    int siteX = x + Rand.Range(-siteVariance.X, siteVariance.X + 1, Rand.RandSync.ServerAndClient);
                    int siteY = y + Rand.Range(-siteVariance.Y, siteVariance.Y + 1, Rand.RandSync.ServerAndClient);

                    bool closeToTunnel = false;
                    bool closeToCave = false;
                    foreach (Tunnel tunnel in Tunnels)
                    {
                        float minDist = Math.Max(tunnel.MinWidth * 2.0f, Math.Max(siteInterval.X, siteInterval.Y));
                        for (int i = 1; i < tunnel.Nodes.Count; i++)
                        {
                            if (siteX < Math.Min(tunnel.Nodes[i - 1].X, tunnel.Nodes[i].X) - minDist) { continue; }
                            if (siteX > Math.Max(tunnel.Nodes[i - 1].X, tunnel.Nodes[i].X) + minDist) { continue; }
                            if (siteY < Math.Min(tunnel.Nodes[i - 1].Y, tunnel.Nodes[i].Y) - minDist) { continue; }
                            if (siteY > Math.Max(tunnel.Nodes[i - 1].Y, tunnel.Nodes[i].Y) + minDist) { continue; }

                            double tunnelDistSqr = MathUtils.LineSegmentToPointDistanceSquared(tunnel.Nodes[i - 1], tunnel.Nodes[i], new Point(siteX, siteY));
                            if (Math.Sqrt(tunnelDistSqr) < minDist)
                            {
                                closeToTunnel = true;
                                //tunnelDistSqr = MathUtils.LineSegmentToPointDistanceSquared(tunnel.Nodes[i - 1], tunnel.Nodes[i], new Point(siteX, siteY));
                                if (tunnel.Type == TunnelType.Cave)
                                {
                                    closeToCave = true;
                                }
                                break;
                            }
                        }
                    }

                    if (!closeToTunnel)
                    {
                        //make the graph less dense (90% less nodes) in areas far away from tunnels where we don't need a lot of geometry 
                        if (Rand.Range(0, 10, Rand.RandSync.ServerAndClient) != 0) { continue; }
                    }

                    if (!TooCloseToOtherSites(siteX, siteY))
                    {
                        siteCoordsX.Add(siteX);
                        siteCoordsY.Add(siteY);
                    }

                    if (closeToCave)
                    {
                        for (int x2 = x - siteInterval.X; x2 < x + siteInterval.X; x2 += caveSiteInterval)
                        {
                            for (int y2 = y - siteInterval.Y; y2 < y + siteInterval.Y; y2 += caveSiteInterval)
                            {
                                int caveSiteX = x2 + Rand.Int(caveSiteInterval / 2, Rand.RandSync.ServerAndClient);
                                int caveSiteY = y2 + Rand.Int(caveSiteInterval / 2, Rand.RandSync.ServerAndClient);

                                if (!TooCloseToOtherSites(caveSiteX, caveSiteY, caveSiteInterval))
                                {
                                    siteCoordsX.Add(caveSiteX);
                                    siteCoordsY.Add(caveSiteY);
                                }
                            }
                        }
                    }
                }
            }

            bool TooCloseToOtherSites(double siteX, double siteY, float minDistance = 10.0f)
            {
                float minDistanceSqr = minDistance * minDistance;
                for (int i = 0; i < siteCoordsX.Count; i++)
                {
                    if (MathUtils.DistanceSquared(siteCoordsX[i], siteCoordsY[i], siteX, siteY) < minDistanceSqr)
                    {
                        return true;
                    }
                }
                return false;
            }

            for (int i = 0; i < siteCoordsX.Count; i++)
            {
                Debug.Assert(
                    !double.IsNaN(siteCoordsX[i]) && !double.IsInfinity(siteCoordsX[i]),
                    $"Potential error in level generation: invalid voronoi site ({siteCoordsX[i]})");
                Debug.Assert(
                    !double.IsNaN(siteCoordsY[i]) && !double.IsInfinity(siteCoordsY[i]),
                    $"Potential error in level generation: invalid voronoi site ({siteCoordsY[i]})");
                Debug.Assert(
                    siteCoordsX[i] > 0 || siteCoordsY[i] > 0,
                    $"Potential error in level generation: a voronoi site was outside the bounds of the level ({siteCoordsX[i]}, {siteCoordsY[i]})");
                Debug.Assert(
                    siteCoordsX[i] < borders.Width || siteCoordsY[i] < borders.Height,
                    $"Potential error in level generation: a voronoi site was outside the bounds of the level ({siteCoordsX[i]}, {siteCoordsY[i]})");
                for (int j = i + 1; j < siteCoordsX.Count; j++)
                {
                    Debug.Assert(
                        MathUtils.DistanceSquared(siteCoordsX[i], siteCoordsY[i], siteCoordsX[j], siteCoordsY[j]) > 10.0f,
                        "Potential error in level generation: two voronoi sites are extremely close to each other.");
                }
            }
        }
        
        private bool isRandomHashSet;
        private int _randomHash;
        private string previousSeed;
        private int RandomHash
        {
            get
            {
                if (Seed != previousSeed)
                {
                    isRandomHashSet = false;
                }
                if (!isRandomHashSet)
                {
                    _randomHash = ToolBox.StringToInt(Seed);
                    isRandomHashSet = true;
                    previousSeed = Seed;
                }
                return _randomHash;
            }
        }
        private void ResetRandomSeed() => Rand.SetSyncedSeed(RandomHash);

        private List<Point> GeneratePathNodes(Point startPosition, Point endPosition, Rectangle pathBorders, Tunnel parentTunnel, float variance)
        {
            List<Point> pathNodes = new List<Point> { startPosition };

            Point nodeInterval = GenerationParams.MainPathNodeIntervalRange;

            for (int x = startPosition.X + nodeInterval.X;
                        x < endPosition.X - nodeInterval.X;
                        x += Rand.Range(nodeInterval.X, nodeInterval.Y, Rand.RandSync.ServerAndClient))
            {
                Point nodePos = new Point(x, Rand.Range(pathBorders.Y, pathBorders.Bottom, Rand.RandSync.ServerAndClient));

                //allow placing the 2nd main path node at any height regardless of variance
                //(otherwise low variance will always make the main path go through the upper part of the level)
                if (pathNodes.Count > 2 || parentTunnel != null)
                {
                    nodePos.Y = (int)MathHelper.Clamp(
                        nodePos.Y,
                        pathNodes.Last().Y - pathBorders.Height * variance * 0.5f,
                        pathNodes.Last().Y + pathBorders.Height * variance * 0.5f);
                }
                if (pathNodes.Count == 1)
                {
                    //if the path starts below the center of the level, head up and vice versa
                    //to utilize as much of the vertical space as possible
                    nodePos.Y = (int)(startPosition.Y + Math.Abs(nodePos.Y - startPosition.Y) * -Math.Sign(nodePos.Y - pathBorders.Center.Y));
                    nodePos.Y = MathHelper.Clamp(nodePos.Y, pathBorders.Y, pathBorders.Bottom);
                }

                //prevent intersections with other tunnels
                foreach (Tunnel tunnel in Tunnels)
                {
                    for (int i = 1; i < tunnel.Nodes.Count; i++)
                    {
                        Point node1 = tunnel.Nodes[i - 1];
                        Point node2 = tunnel.Nodes[i];
                        if (node1.X >= nodePos.X) { continue; }
                        if (node2.X <= pathNodes.Last().X) { continue; }
                        if (MathUtils.NearlyEqual(node1.X, pathNodes.Last().X)) { continue; }
                        if (Math.Abs(node1.Y - nodePos.Y) > tunnel.MinWidth && Math.Abs(node2.Y - nodePos.Y) > tunnel.MinWidth &&
                            !MathUtils.LineSegmentsIntersect(node1.ToVector2(), node2.ToVector2(), pathNodes.Last().ToVector2(), nodePos.ToVector2())) 
                        { 
                            continue; 
                        }

                        if (nodePos.Y < pathNodes.Last().Y)
                        {
                            nodePos.Y = Math.Min(Math.Max(node1.Y, node2.Y) + tunnel.MinWidth * 2, pathBorders.Bottom);
                        }
                        else
                        {
                            nodePos.Y = Math.Max(Math.Min(node1.Y, node2.Y) - tunnel.MinWidth * 2, pathBorders.Y);
                        }
                        break;
                    }
                }

                pathNodes.Add(nodePos);
            }

            if (pathNodes.Count == 1)
            {
                pathNodes.Add(new Point(pathBorders.Center.X, pathBorders.Y));
            }

            pathNodes.Add(endPosition);
            return pathNodes;
        }

        private List<VoronoiCell> CreateHoles(float holeProbability, Rectangle limits, int submarineSize)
        {
            List<VoronoiCell> toBeRemoved = new List<VoronoiCell>();
            foreach (VoronoiCell cell in cells)
            {
                if (cell.Edges.Any(e => e.NextToCave)) { continue; }
                if (Rand.Range(0.0f, 1.0f, Rand.RandSync.ServerAndClient) > holeProbability) { continue; }
                if (!limits.Contains(cell.Site.Coord.X, cell.Site.Coord.Y)) { continue; }

                float closestDist = 0.0f;
                Point? closestTunnelNode = null;
                foreach (Tunnel tunnel in Tunnels)
                {
                    foreach (Point node in tunnel.Nodes)
                    {
                        float dist = Math.Abs(cell.Center.X - node.X);
                        if (closestTunnelNode == null || dist < closestDist)
                        {
                            closestDist = dist;
                            closestTunnelNode = node;
                        }
                    } 
                }

                if (closestTunnelNode != null && closestTunnelNode.Value.Y < cell.Center.Y) { continue; }

                toBeRemoved.Add(cell);
            }

            return toBeRemoved;
        }

        private void EnlargePath(List<VoronoiCell> pathCells, float minWidth)
        {
            if (minWidth <= 0.0f) { return; }
            
            List<VoronoiCell> removedCells = GetTooCloseCells(pathCells, minWidth);
            foreach (VoronoiCell removedCell in removedCells)
            {
                if (removedCell.CellType == CellType.Path) { continue; }

                pathCells.Add(removedCell);
                removedCell.CellType = CellType.Path;
            }            
        }

        private void GenerateWaypoints(Tunnel tunnel, Tunnel parentTunnel)
        {
            if (tunnel.Cells.Count == 0) { return; }

            List<WayPoint> wayPoints = new List<WayPoint>();
            WayPoint prevWayPoint = null;
            for (int i = 0; i < tunnel.Cells.Count; i++)
            {
                tunnel.Cells[i].CellType = CellType.Path;
                var newWaypoint = new WayPoint(new Rectangle((int)tunnel.Cells[i].Site.Coord.X, (int)tunnel.Cells[i].Center.Y, 10, 10), null)
                {
                    Tunnel = tunnel
                };
                wayPoints.Add(newWaypoint);

                if (prevWayPoint != null)
                {
                    bool solidCellBetween = false;
                    foreach (GraphEdge edge in tunnel.Cells[i].Edges)
                    {
                        if (edge.AdjacentCell(tunnel.Cells[i])?.CellType == CellType.Solid && 
                            MathUtils.LineSegmentsIntersect(newWaypoint.WorldPosition, prevWayPoint.WorldPosition, edge.Point1, edge.Point2))
                        {
                            solidCellBetween = true;
                            break;
                        }
                    }

                    if (solidCellBetween)
                    {
                        //something between the previous waypoint and this one
                        // -> find the edge that connects the cells and place a waypoint there, instead of connecting the centers of the cells directly
                        var edgeBetweenCells = tunnel.Cells[i].Edges.Find(e => e.AdjacentCell(tunnel.Cells[i]) == tunnel.Cells[i - 1]);
                        if (edgeBetweenCells != null)
                        {
                            var edgeWaypoint = new WayPoint(new Rectangle((int)edgeBetweenCells.Center.X, (int)edgeBetweenCells.Center.Y, 10, 10), null)
                            {
                                Tunnel = tunnel
                            };
                            prevWayPoint.ConnectTo(edgeWaypoint);
                            prevWayPoint = edgeWaypoint;
                        }
                    }
                    prevWayPoint.ConnectTo(newWaypoint);

                    //look back at the tunnel cells before the previous one, and see if the current cell shares edges with them
                    //= if we can "skip" from cell #1 to cell #3, create a waypoint between them.
                    //Fixes there sometimes not being a path past a destructible ice chunk even if there's space to go past it.
                    for (int j = i - 2; j > 0 && j > i - 5; j--)
                    {
                        foreach (GraphEdge edge in tunnel.Cells[i].Edges)
                        {
                            if (Vector2.DistanceSquared(edge.Point1, edge.Point2) < 30.0f * 30.0f) { continue; }
                            if (!edge.IsSolid && edge.AdjacentCell(tunnel.Cells[i]) == tunnel.Cells[j])
                            {
                                var edgeWaypoint = new WayPoint(new Rectangle((int)edge.Center.X, (int)edge.Center.Y, 10, 10), null)
                                {
                                    Tunnel = tunnel
                                };
                                wayPoints[j].ConnectTo(edgeWaypoint);
                                edgeWaypoint.ConnectTo(newWaypoint);
                                break;
                            }
                        }
                    }
                }
                prevWayPoint = newWaypoint;
            }

            tunnel.WayPoints.AddRange(wayPoints);

            //connect to the tunnel we're branching off from
            if (parentTunnel != null)
            {
                var parentStart = FindClosestWayPoint(wayPoints.First().WorldPosition, parentTunnel);
                if (parentStart != null)
                {
                    wayPoints.First().ConnectTo(parentStart);
                }
                if (tunnel.Type != TunnelType.Cave || tunnel.ParentTunnel.Type == TunnelType.Cave)
                {
                    var parentEnd = FindClosestWayPoint(wayPoints.Last().WorldPosition, parentTunnel);
                    if (parentEnd != null)
                    {
                        wayPoints.Last().ConnectTo(parentEnd);
                    }
                }
            }
        }

        private void ConnectWaypoints(Tunnel tunnel, Tunnel parentTunnel)
        {
            foreach (WayPoint wayPoint in tunnel.WayPoints)
            {
                var closestWaypoint = FindClosestWayPoint(wayPoint.WorldPosition, parentTunnel);
                if (closestWaypoint == null) { continue; }
                if (Submarine.PickBody(
                    ConvertUnits.ToSimUnits(wayPoint.WorldPosition),
                    ConvertUnits.ToSimUnits(closestWaypoint.WorldPosition), collisionCategory: Physics.CollisionLevel | Physics.CollisionWall) == null)
                {
                    float step = ConvertUnits.ToDisplayUnits(Steering.AutopilotMinDistToPathNode) * 0.8f;
                    ConnectWaypoints(wayPoint, closestWaypoint, step).ForEach(wp => wp.Tunnel = tunnel);
                }
            }
        }

        private List<WayPoint> ConnectWaypoints(WayPoint wp1, WayPoint wp2, float interval)
        {
            List<WayPoint> newWaypoints = new List<WayPoint>();

            Vector2 diff = wp2.WorldPosition - wp1.WorldPosition;
            float dist = diff.Length();

            WayPoint prevWaypoint = wp1;
            for (float x = interval; x < dist - interval; x += interval)
            {
                var newWaypoint = new WayPoint(wp1.WorldPosition + (diff / dist * x), SpawnType.Path, submarine: null);
                prevWaypoint.ConnectTo(newWaypoint);
                prevWaypoint = newWaypoint;
                newWaypoints.Add(newWaypoint);
            }
            prevWaypoint.ConnectTo(wp2);

            return newWaypoints;
        }

        private static WayPoint FindClosestWayPoint(Vector2 worldPosition, Tunnel otherTunnel)
        {
            return FindClosestWayPoint(worldPosition, otherTunnel.WayPoints);
        }

        private static WayPoint FindClosestWayPoint(Vector2 worldPosition, IEnumerable<WayPoint> waypoints, Func<WayPoint, bool> filter = null)
        {
            float closestDist = float.PositiveInfinity;
            WayPoint closestWayPoint = null;
            foreach (WayPoint otherWayPoint in waypoints)
            {
                float dist = Vector2.DistanceSquared(otherWayPoint.WorldPosition, worldPosition);
                if (dist < closestDist)
                {
                    if (filter != null)
                    {
                        if (!filter(otherWayPoint)) { continue; }
                    }
                    closestDist = dist;
                    closestWayPoint = otherWayPoint;
                }
            }
            return closestWayPoint;
        }

        private List<VoronoiCell> GetTooCloseCells(List<VoronoiCell> emptyCells, float minDistance)
        {
            List<VoronoiCell> tooCloseCells = new List<VoronoiCell>();
            if (minDistance <= 0.0f) { return tooCloseCells; }
            foreach (var cell in emptyCells.Distinct())
            {
                foreach (var tooCloseCell in GetTooCloseCells(cell.Center, minDistance))
                {
                    if (!tooCloseCells.Contains(tooCloseCell))
                    {
                        tooCloseCells.Add(tooCloseCell);
                    }
                }
            }
            return tooCloseCells;
        }

        public List<VoronoiCell> GetTooCloseCells(Vector2 position, float minDistance)
        {
            HashSet<VoronoiCell> tooCloseCells = new HashSet<VoronoiCell>();
            var closeCells = GetCells(position, searchDepth: Math.Max((int)Math.Ceiling(minDistance / GridCellSize), 3));
            float minDistSqr = minDistance * minDistance;
            foreach (VoronoiCell cell in closeCells)
            {
                bool tooClose = false;

                //if the cell is very large, the position can be far away from the edges while being inside the cell
                //so we need to check that here too
                if (cell.IsPointInside(position))
                {
                    tooClose = true;
                }
                else
                {
                    foreach (GraphEdge edge in cell.Edges)
                    {
                        if (Vector2.DistanceSquared(edge.Point1, position) < minDistSqr ||
                            Vector2.DistanceSquared(edge.Point2, position) < minDistSqr ||
                            MathUtils.LineSegmentToPointDistanceSquared(edge.Point1.ToPoint(), edge.Point2.ToPoint(), position.ToPoint()) < minDistSqr)
                        {
                            tooClose = true;
                            break;
                        }
                    }
                }
                if (tooClose) { tooCloseCells.Add(cell); }
            }
            return tooCloseCells.ToList();
        }

        private void GenerateAbyssPositions()
        {
            int count = 10;
            for (int i = 0; i < count; i++)
            {
                float xPos = MathHelper.Lerp(borders.X, borders.Right, i / (float)(count - 1));
                float seaFloorPos = GetBottomPosition(xPos).Y;

                //above the bottom of the level = can't place a point here
                if (seaFloorPos > AbyssStart) { continue; }

                float yPos = MathHelper.Lerp(AbyssStart, Math.Max(seaFloorPos, AbyssArea.Y), Rand.Range(0.2f, 1.0f, Rand.RandSync.ServerAndClient));

                foreach (var abyssIsland in AbyssIslands)
                {
                    if (abyssIsland.Area.Contains(new Point((int)xPos, (int)yPos)))
                    {
                        xPos = abyssIsland.Area.Center.X + (int)(Rand.Int(1, Rand.RandSync.ServerAndClient) == 0 ? abyssIsland.Area.Width * -0.6f : 0.6f);
                    }
                }

                PositionsOfInterest.Add(new InterestingPosition(new Point((int)xPos, (int)yPos), PositionType.Abyss));
            }
        }

        private void GenerateAbyssArea()
        {
            int abyssStartY = borders.Y - 5000;
            int abyssEndY = Math.Max(abyssStartY - 100000, BottomPos + 1000);
            int abyssHeight = abyssStartY - abyssEndY;

            if (abyssHeight < 0)
            {
                abyssStartY = borders.Y;
                abyssEndY = BottomPos;
                if (abyssStartY - abyssEndY < 1000)
                {
#if DEBUG
                    DebugConsole.ThrowError("Not enough space to generate Abyss in the level. You may want to move the ocean floor deeper.");
#else
                    DebugConsole.AddWarning("Not enough space to generate Abyss in the level. You may want to move the ocean floor deeper.");
#endif
                }
            }
            else if (abyssHeight > 30000)
            {
                //if the bottom of the abyss area is below crush depth, try to move it up to keep (most) of the abyss content above crush depth
                //but only if start of the abyss is above crush depth (no point in doing this if all of it is below crush depth)
                if (abyssEndY + CrushDepth < 0 && abyssStartY > -CrushDepth)
                {
                    abyssEndY += Math.Min(-(abyssEndY + (int)CrushDepth), abyssHeight / 2);
                }

                if (abyssStartY - abyssEndY < 10000)
                {
                    abyssStartY = borders.Y;
                }
            }

            AbyssArea = new Rectangle(borders.X, abyssEndY, borders.Width, abyssStartY - abyssEndY);
        }

        private void GenerateAbyssGeometry()
        {
            ResetRandomSeed();
            //TODO: expose island parameters

            Voronoi voronoi = new Voronoi(1.0);
            Point siteInterval = new Point(500, 500);
            Point siteVariance = new Point(200, 200);

            Point islandSize = Vector2.Lerp(
                GenerationParams.AbyssIslandSizeMin.ToVector2(), 
                GenerationParams.AbyssIslandSizeMax.ToVector2(), 
                Rand.Range(0.0f, 1.0f, Rand.RandSync.ServerAndClient)).ToPoint();

            if (AbyssArea.Height < islandSize.Y) { return; }

            int createdCaves = 0;
            int islandCount = GenerationParams.AbyssIslandCount;
            for (int i = 0; i < islandCount; i++)
            {
                Point islandPosition = Point.Zero;
                Rectangle islandArea = new Rectangle(islandPosition, islandSize);

                //prevent overlaps
                int tries = 0;
                const int MaxTries = 20;
                do
                {
                    islandPosition = new Point(
                       Rand.Range(AbyssArea.X, AbyssArea.Right - islandSize.X, Rand.RandSync.ServerAndClient),
                       Rand.Range(AbyssArea.Y, AbyssArea.Bottom - islandSize.Y, Rand.RandSync.ServerAndClient));

                    //move the island above the sea floor geometry
                    islandPosition.Y = Math.Max(islandPosition.Y, (int)GetBottomPosition(islandPosition.X).Y + 500);
                    islandPosition.Y = Math.Max(islandPosition.Y, (int)GetBottomPosition(islandPosition.X + islandArea.Width).Y + 500);

                    islandArea.Location = islandPosition;

                    tries++;
                } while ((AbyssIslands.Any(island => island.Area.Intersects(islandArea)) || islandArea.Bottom > AbyssArea.Bottom) && tries < MaxTries);

                if (tries >= MaxTries)
                {
                    break;
                }

                bool createCave =
                    //force at least one abyss cave
                    (i == islandCount - 1 && createdCaves == 0) ||
                    Rand.Range(0.0f, 1.0f, Rand.RandSync.ServerAndClient) < GenerationParams.AbyssIslandCaveProbability;
                if (!createCave)
                {
                    //just create a chunk with no cave
                    float radiusVariance = Math.Min(islandArea.Width, islandArea.Height) * 0.1f;
                    var vertices = CaveGenerator.CreateRandomChunk(islandArea.Width - (int)(radiusVariance * 2), islandArea.Height - (int)(radiusVariance * 2), 16, radiusVariance: radiusVariance);
                    Vector2 position = islandArea.Center.ToVector2();
                    for (int j = 0; j < vertices.Count; j++)
                    {
                        vertices[j] += position;
                    }
                    var newChunk = new LevelWall(vertices, GenerationParams.WallColor, this, createBody: false);
                    AbyssIslands.Add(new AbyssIsland(islandArea, newChunk.Cells));
                    continue;
                }

                var siteCoordsX = new List<double>((islandSize.Y / siteInterval.Y) * (islandSize.X / siteInterval.Y));
                var siteCoordsY = new List<double>((islandSize.Y / siteInterval.Y) * (islandSize.X / siteInterval.Y));

                for (int x = islandArea.X; x < islandArea.Right; x += siteInterval.X)
                {
                    for (int y = islandArea.Y; y < islandArea.Bottom; y += siteInterval.Y)
                    {
                        siteCoordsX.Add(x + Rand.Range(-siteVariance.X, siteVariance.X, Rand.RandSync.ServerAndClient));
                        siteCoordsY.Add(y + Rand.Range(-siteVariance.Y, siteVariance.Y, Rand.RandSync.ServerAndClient));
                    }
                }

                var graphEdges = voronoi.MakeVoronoiGraph(siteCoordsX.ToArray(), siteCoordsY.ToArray(), islandArea);
                var islandCells = CaveGenerator.GraphEdgesToCells(graphEdges, islandArea, GridCellSize, out var cellGrid);

                //make the island elliptical
                for (int j = islandCells.Count - 1; j >= 0; j--)
                {
                    var cell = islandCells[j];
                    double xDiff = (cell.Site.Coord.X - islandArea.Center.X) / (islandArea.Width * 0.5);
                    double yDiff = (cell.Site.Coord.Y - islandArea.Center.Y) / (islandArea.Height * 0.5);

                    //a conical stalactite-like shape at the bottom
                    if (yDiff < 0) { xDiff += xDiff * Math.Abs(yDiff); }

                    double normalizedDist = Math.Sqrt(xDiff * xDiff + yDiff * yDiff);
                    if (normalizedDist > 0.95 ||
                        cell.Edges.Any(e => MathUtils.NearlyEqual(e.Point1.X, islandArea.X)) ||
                        cell.Edges.Any(e => MathUtils.NearlyEqual(e.Point1.X, islandArea.Right)) ||
                        cell.Edges.Any(e => MathUtils.NearlyEqual(e.Point1.Y, islandArea.Y)) ||
                        cell.Edges.Any(e => MathUtils.NearlyEqual(e.Point1.Y, islandArea.Bottom)))
                    {
                        islandCells[j].CellType = CellType.Removed;
                        islandCells.RemoveAt(j);
                    }
                }

                var caveParams = CaveGenerationParams.GetRandom(this, abyss: true, rand: Rand.RandSync.ServerAndClient);

                float caveScaleRelativeToIsland = 0.7f;
                GenerateCave(
                    caveParams, Tunnels.First(),
                    new Point(islandArea.Center.X, islandArea.Center.Y + (int)(islandArea.Size.Y * (1.0f - caveScaleRelativeToIsland)) / 2),
                    new Point((int)(islandArea.Size.X * caveScaleRelativeToIsland), (int)(islandArea.Size.Y * caveScaleRelativeToIsland)));
                AbyssIslands.Add(new AbyssIsland(islandArea, islandCells));
                createdCaves++;
            }
        }

        private void GenerateSeaFloorPositions()
        {
            ResetRandomSeed();
            BottomPos = GenerationParams.SeaFloorDepth;
            SeaFloorTopPos = BottomPos;

            bottomPositions = new List<Point>
            {
                new Point(0, BottomPos)
            };

            int mountainCount = Rand.Range(GenerationParams.MountainCountMin, GenerationParams.MountainCountMax + 1, Rand.RandSync.ServerAndClient);
            for (int i = 0; i < mountainCount; i++)
            {
                bottomPositions.Add(
                    new Point(Size.X / (mountainCount + 1) * (i + 1),
                        BottomPos + Rand.Range(GenerationParams.MountainHeightMin, GenerationParams.MountainHeightMax + 1, Rand.RandSync.ServerAndClient)));
            }
            bottomPositions.Add(new Point(Size.X, BottomPos));

            int minVertexInterval = 5000;
            float currInverval = Size.X / 2;
            while (currInverval > minVertexInterval)
            {
                for (int i = 0; i < bottomPositions.Count - 1; i++)
                {
                    bottomPositions.Insert(i + 1,
                        new Point(
                            (bottomPositions[i].X + bottomPositions[i + 1].X) / 2,
                            (bottomPositions[i].Y + bottomPositions[i + 1].Y) / 2 + Rand.Range(0, GenerationParams.SeaFloorVariance + 1, Rand.RandSync.ServerAndClient)));
                            i++;
                }

                currInverval /= 2;
            }

            SeaFloorTopPos = bottomPositions.Max(p => p.Y);
        }

        private void GenerateSeaFloor()
        {            
            SeaFloor = new LevelWall(bottomPositions.Select(p => p.ToVector2()).ToList(), new Vector2(0.0f, -2000.0f), GenerationParams.WallColor, this);
            ExtraWalls.Add(SeaFloor);

            BottomBarrier = GameMain.World.CreateEdge(
                ConvertUnits.ToSimUnits(new Vector2(borders.X, 0)),
                ConvertUnits.ToSimUnits(new Vector2(borders.Right, 0)));
            //for debugging purposes
            BottomBarrier.UserData = "bottombarrier";
            BottomBarrier.SetTransform(ConvertUnits.ToSimUnits(new Vector2(0.0f, BottomPos)), 0.0f);
            BottomBarrier.BodyType = BodyType.Static;
            BottomBarrier.CollisionCategories = Physics.CollisionLevel;

            bodies.Add(BottomBarrier);
        }

        private void GenerateCaves(Tunnel parentTunnel)
        {
            ResetRandomSeed();
            for (int i = 0; i < GenerationParams.CaveCount; i++)
            {
                var caveParams = CaveGenerationParams.GetRandom(this, abyss: false, rand: Rand.RandSync.ServerAndClient);
                Point caveSize = new Point(
                    Rand.Range(caveParams.MinWidth, caveParams.MaxWidth, Rand.RandSync.ServerAndClient),
                    Rand.Range(caveParams.MinHeight, caveParams.MaxHeight, Rand.RandSync.ServerAndClient));
                int padding = (int)(caveSize.X * 1.2f);
                Rectangle allowedArea = new Rectangle(padding, padding, Size.X - padding * 2, Size.Y - padding * 2);

                int radius = Math.Max(caveSize.X, caveSize.Y) / 2;
                var cavePos = FindPosAwayFromMainPath((parentTunnel.MinWidth + radius) * 1.25f, asCloseAsPossible: true, allowedArea);

                GenerateCave(caveParams, parentTunnel, cavePos, caveSize);

                CalculateTunnelDistanceField(null);
            }
        }

        private void GenerateCave(CaveGenerationParams caveParams, Tunnel parentTunnel, Point cavePos, Point caveSize)
        {
            Rectangle caveArea = new Rectangle(cavePos - new Point(caveSize.X / 2, caveSize.Y / 2), caveSize);
            Point closestParentNode = parentTunnel.Nodes.First();
            double closestDist = double.PositiveInfinity;
            foreach (Point node in parentTunnel.Nodes)
            {
                if (caveArea.Contains(node)) { continue; }
                double dist = MathUtils.DistanceSquared((double)node.X, (double)node.Y, (double)cavePos.X, (double)cavePos.Y);
                if (dist < closestDist)
                {
                    closestParentNode = node;
                    closestDist = dist;
                }
            }

            if (!MathUtils.GetLineRectangleIntersection(closestParentNode.ToVector2(), cavePos.ToVector2(), new Rectangle(caveArea.X, caveArea.Y + caveArea.Height, caveArea.Width, caveArea.Height), out Vector2 caveStartPosVector))
            {
                caveStartPosVector = caveArea.Location.ToVector2();
            }

            Point caveStartPos = caveStartPosVector.ToPoint();
            Point caveEndPos = cavePos - (caveStartPos - cavePos);

            Cave cave = new Cave(caveParams, caveArea, caveStartPos, caveEndPos);
            Caves.Add(cave);

            var caveSegments = MathUtils.GenerateJaggedLine(
                caveStartPos.ToVector2(), caveEndPos.ToVector2(),
                iterations: 3,
                offsetAmount: Vector2.Distance(caveStartPos.ToVector2(), caveEndPos.ToVector2()) * 0.75f,
                bounds: caveArea,
                rng: Rand.GetRNG(Rand.RandSync.ServerAndClient));

            if (!caveSegments.Any()) { return; }

            List<Tunnel> caveBranches = new List<Tunnel>();

            var tunnel = new Tunnel(TunnelType.Cave, SegmentsToNodes(caveSegments), 150, parentTunnel);
            Tunnels.Add(tunnel);
            caveBranches.Add(tunnel);

            int branches = Rand.Range(caveParams.MinBranchCount, caveParams.MaxBranchCount + 1, Rand.RandSync.ServerAndClient);
            for (int j = 0; j < branches; j++)
            {
                Tunnel parentBranch = caveBranches.GetRandom(Rand.RandSync.ServerAndClient);
                Vector2 branchStartPos = parentBranch.Nodes[Rand.Int(parentBranch.Nodes.Count / 2, Rand.RandSync.ServerAndClient)].ToVector2();
                Vector2 branchEndPos = parentBranch.Nodes[Rand.Range(parentBranch.Nodes.Count / 2, parentBranch.Nodes.Count, Rand.RandSync.ServerAndClient)].ToVector2();
                var branchSegments = MathUtils.GenerateJaggedLine(
                    branchStartPos, branchEndPos,
                    iterations: 3,
                    offsetAmount: Vector2.Distance(branchStartPos, branchEndPos) * 0.75f,
                    bounds: caveArea,
                    rng: Rand.GetRNG(Rand.RandSync.ServerAndClient));
                if (!branchSegments.Any()) { continue; }

                var branch = new Tunnel(TunnelType.Cave, SegmentsToNodes(branchSegments), 150, parentBranch);
                Tunnels.Add(branch);
                caveBranches.Add(branch);
            }

            foreach (Tunnel branch in caveBranches)
            {
                var node = branch.Nodes.Last();
                PositionsOfInterest.Add(new InterestingPosition(node, node.Y < AbyssArea.Bottom ? PositionType.AbyssCave : PositionType.Cave, cave));
                cave.Tunnels.Add(branch);
            }

            static List<Point> SegmentsToNodes(List<Vector2[]> segments)
            {
                List<Point> nodes = new List<Point>();
                foreach (Vector2[] segment in segments)
                {
                    nodes.Add(segment[0].ToPoint());
                }
                nodes.Add(segments.Last()[1].ToPoint());
                return nodes;
            }
        }

        private void GenerateRuin(Point ruinPos, bool mirror, bool requireMissionReadyRuin)
        {
            float GetWeight(RuinGenerationParams p)
            {
                if (p.PreferredDifficulty == -1) { return 100; }
                float diff = Math.Abs(Difficulty - p.PreferredDifficulty) / 100;
                float weight = MathUtils.Pow(1 - diff, 10);
                return Math.Max(weight, 0);
            }
            
            RuinGenerationParams ruinGenerationParams = null;
            
            if (LevelData.ForceRuinGenerationParams != null)
            {
                ruinGenerationParams = LevelData.ForceRuinGenerationParams;
            }
            else
            {
                IEnumerable<RuinGenerationParams> possibleRuinGenerationParams = RuinGenerationParams.RuinParams;
                if (requireMissionReadyRuin)
                {
                    possibleRuinGenerationParams = possibleRuinGenerationParams.Where(p => p.IsMissionReady);
                }
                if (possibleRuinGenerationParams.Multiple())
                {
                    // Sort by weight and choose from the closest 25% of the candidates.
                    // Prevents choosing from the "wrong" end, which would otherwise be possible (yet rare), because we use a weighted random for the pick.
                    possibleRuinGenerationParams = possibleRuinGenerationParams
                        /* the prefabs aren't in a consistent order, so we need to sort them first to ensure the clients and server choose the same one */
                        .OrderByDescending(p => p.UintIdentifier)
                        .ThenByDescending(GetWeight)
                        .Take((int)Math.Max(Math.Round(possibleRuinGenerationParams.Count() / 4f), 1));
                }
                ruinGenerationParams = possibleRuinGenerationParams.GetRandomByWeight(GetWeight, randSync: Rand.RandSync.ServerAndClient);
                if (ruinGenerationParams == null)
                {
                    DebugConsole.ThrowError("Failed to generate alien ruins. Could not find any RuinGenerationParameters!");
                    return;
                }
            }
            
            DebugConsole.NewMessage($"Creating alien ruins using {ruinGenerationParams.Identifier} (preferred difficulty: {ruinGenerationParams.PreferredDifficulty}, current difficulty {Difficulty})", color: Color.Yellow, debugOnly: true);
            
            LocationType locationType = StartLocation?.Type;
            if (locationType == null)
            {
                locationType = LocationType.Prefabs.GetRandom(Rand.RandSync.ServerAndClient);
                if (ruinGenerationParams.AllowedLocationTypes.Any())
                {
                    locationType = LocationType.Prefabs.Where(lt =>
                        ruinGenerationParams.AllowedLocationTypes.Any(allowedType =>
                            allowedType == "any" || lt.Identifier == allowedType)).GetRandom(Rand.RandSync.ServerAndClient);
                }
            }

            var ruin = new Ruin(this, ruinGenerationParams, locationType, ruinPos, mirror);
            if (ruin.Submarine != null)
            {
                SetLinkedSubCrushDepth(ruin.Submarine);
            }

            Ruins.Add(ruin);
            var tooClose = GetTooCloseCells(ruinPos.ToVector2(), Math.Max(ruin.Area.Width, ruin.Area.Height) * 4);

            foreach (VoronoiCell cell in tooClose)
            {
                if (cell.CellType == CellType.Empty) { continue; }
                if (ExtraWalls.Any(w => w.Cells.Contains(cell))) { continue; }
                foreach (GraphEdge e in cell.Edges)
                {
                    if (ruin.Area.Contains(e.Point1) || ruin.Area.Contains(e.Point2) ||
                        MathUtils.GetLineRectangleIntersection(e.Point1, e.Point2, ruin.Area, out _))
                    {
                        cell.CellType = CellType.Removed;
                        for (int x = 0; x < cellGrid.GetLength(0); x++)
                        {
                            for (int y = 0; y < cellGrid.GetLength(1); y++)
                            {
                                cellGrid[x, y].Remove(cell);
                            }
                        }
                        cells.Remove(cell);
                        break;
                    }
                }
            }

            ruin.PathCells = CreatePathToClosestTunnel(ruin.Area.Center);
        }

        private void GenerateRuinWayPoints(Ruin ruin)
        {
            var tooClose = GetTooCloseCells(ruin.Area.Center.ToVector2(), Math.Max(ruin.Area.Width, ruin.Area.Height) * 6);

            List<WayPoint> wayPoints = new List<WayPoint>();
            float outSideWaypointInterval = 500.0f;
            WayPoint[,] cornerWaypoint = new WayPoint[2, 2];
            Rectangle waypointArea = ruin.Area;
            waypointArea.Inflate(100, 100);

            //generate waypoints around the ruin
            for (int i = 0; i < 2; i++)
            {
                for (float x = waypointArea.X + outSideWaypointInterval; x < waypointArea.Right - outSideWaypointInterval; x += outSideWaypointInterval)
                {
                    var wayPoint = new WayPoint(new Vector2(x, waypointArea.Y + waypointArea.Height * i), SpawnType.Path, null)
                    {
                        Ruin = ruin
                    };
                    wayPoints.Add(wayPoint);
                    if (x == waypointArea.X + outSideWaypointInterval)
                    {
                        cornerWaypoint[i, 0] = wayPoint;
                    }
                    else
                    {
                        wayPoint.ConnectTo(wayPoints[wayPoints.Count - 2]);
                    }
                }
                cornerWaypoint[i, 1] = wayPoints[wayPoints.Count - 1];
            }

            for (int i = 0; i < 2; i++)
            {
                WayPoint wayPoint = null;
                for (float y = waypointArea.Y; y < waypointArea.Y + waypointArea.Height; y += outSideWaypointInterval)
                {
                    wayPoint = new WayPoint(new Vector2(waypointArea.X + waypointArea.Width * i, y), SpawnType.Path, null)
                    {
                        Ruin = ruin
                    };
                    wayPoints.Add(wayPoint);
                    if (y == waypointArea.Y)
                    {
                        wayPoint.ConnectTo(cornerWaypoint[0, i]);
                    }
                    else
                    {
                        wayPoint.ConnectTo(wayPoints[wayPoints.Count - 2]);
                    }
                }
                wayPoint.ConnectTo(cornerWaypoint[1, i]);
            }

            //remove waypoints that are inside walls
            for (int i = wayPoints.Count - 1; i >= 0; i--)
            {
                WayPoint wp = wayPoints[i];
                var overlappingCell = tooClose.Find(c => c.CellType != CellType.Removed && c.IsPointInside(wp.WorldPosition));
                if (overlappingCell == null) { continue; }
                if (wp.linkedTo.Count > 1)
                {
                    WayPoint linked1 = wp.linkedTo[0] as WayPoint;
                    WayPoint linked2 = wp.linkedTo[1] as WayPoint;
                    linked1.ConnectTo(linked2);
                }
                wp.Remove();
                wayPoints.RemoveAt(i);
            }

            Debug.Assert(wayPoints.Any(), "Couldn't generate waypoints around ruins.");

            //connect ruin entrances to the outside waypoints
            foreach (Gap g in Gap.GapList)
            {
                if (g.Submarine != ruin.Submarine || g.IsRoomToRoom || g.linkedTo.Count == 0) { continue; }
                var gapWaypoint = WayPoint.WayPointList.Find(wp => wp.ConnectedGap == g);
                if (gapWaypoint == null) { continue; }

                //place another waypoint in front of the entrance
                Vector2 entranceDir = Vector2.Zero;
                if (g.IsHorizontal)
                {
                    entranceDir = Vector2.UnitX * 2 * Math.Sign(g.WorldPosition.X - g.linkedTo[0].WorldPosition.X);
                }
                else
                {
                    entranceDir = Vector2.UnitY * 2 * Math.Sign(g.WorldPosition.Y - g.linkedTo[0].WorldPosition.Y);
                }
                var entranceWayPoint = new WayPoint(g.WorldPosition + entranceDir * 64.0f, SpawnType.Path, null)
                {
                    Ruin = ruin
                };
                entranceWayPoint.ConnectTo(gapWaypoint);
                var closestWp = FindClosestWayPoint(entranceWayPoint.WorldPosition, wayPoints, (wp) =>
                {
                    return Submarine.PickBody(
                        ConvertUnits.ToSimUnits(wp.WorldPosition),
                        ConvertUnits.ToSimUnits(entranceWayPoint.WorldPosition), collisionCategory: Physics.CollisionLevel | Physics.CollisionWall) == null;
                });
                if (closestWp == null) { continue; }
                ConnectWaypoints(entranceWayPoint, closestWp, outSideWaypointInterval);
            }

            //create a waypoint path from the ruin to the closest tunnel
            WayPoint prevWp = FindClosestWayPoint(ruin.PathCells.First().Center, wayPoints, (wp) =>
            {
                return Submarine.PickBody(
                    ConvertUnits.ToSimUnits(wp.WorldPosition),
                    ConvertUnits.ToSimUnits(ruin.PathCells.First().Center), collisionCategory: Physics.CollisionLevel | Physics.CollisionWall) == null;
            });
            if (prevWp != null)
            {
                for (int i = 0; i < ruin.PathCells.Count; i++)
                {
                    var connectingEdge = i > 0 ? ruin.PathCells[i].Edges.Find(e => e.AdjacentCell(ruin.PathCells[i]) == ruin.PathCells[i - 1]) : null;
                    if (connectingEdge != null)
                    {
                        var edgeWayPoint = new WayPoint(connectingEdge.Center, SpawnType.Path, submarine: null);
                        ConnectWaypoints(prevWp, edgeWayPoint, outSideWaypointInterval);
                        prevWp = edgeWayPoint;
                    }
                    var newWaypoint = new WayPoint(ruin.PathCells[i].Center, SpawnType.Path, submarine: null);
                    ConnectWaypoints(prevWp, newWaypoint, outSideWaypointInterval);
                    prevWp = newWaypoint;
                }
                var closestPathPoint = FindClosestWayPoint(prevWp.WorldPosition, Tunnels.SelectMany(t => t.WayPoints));
                ConnectWaypoints(prevWp, closestPathPoint, outSideWaypointInterval);
            }
        }

        private Point FindPosAwayFromMainPath(double minDistance, bool asCloseAsPossible, Rectangle? limits = null)
        {
            var pointsAboveBottom = distanceField.FindAll(d => d.point.Y > GetBottomPosition(d.point.X).Y + minDistance);
            if (pointsAboveBottom.Count == 0)
            {
                DebugConsole.ThrowError("Error in FindPosAwayFromMainPath: no valid positions above the bottom of the sea floor. Has the position of the sea floor been set too high up?");
                return distanceField[Rand.Int(distanceField.Count, Rand.RandSync.ServerAndClient)].point;
            }

            var validPoints = pointsAboveBottom.FindAll(d => d.distance >= minDistance && (limits == null || limits.Value.Contains(d.point)));
            if (!validPoints.Any())
            {
                DebugConsole.AddWarning("Failed to find a valid position far enough from the main path. Choosing the furthest possible position.\n" + Environment.StackTrace);
                if (limits != null)
                {
                    //try choosing something within the specified limits
                    validPoints = pointsAboveBottom.FindAll(d => limits.Value.Contains(d.point));
                }
                if (!validPoints.Any()) 
                { 
                    //couldn't find anything, let's just go with the furthest one
                    validPoints = pointsAboveBottom; 
                }
                (Point position, double distance) furthestPoint = validPoints.First();
                foreach (var point in validPoints)
                {
                    if (point.distance > furthestPoint.distance)
                    {
                        furthestPoint = point;
                    }
                }
                return furthestPoint.position;
            }
                        
            if (asCloseAsPossible)
            {
                if (!validPoints.Any()) { validPoints = distanceField; }
                (Point position, double distance)  closestPoint = validPoints.First(); 
                foreach (var point in validPoints)
                {
                    if (point.distance < closestPoint.distance)
                    {
                        closestPoint = point;
                    }
                }
                return closestPoint.position;
            }
            else
            {
                return validPoints[Rand.Int(validPoints.Count, Rand.RandSync.ServerAndClient)].point;
            }
        }

        private void CalculateTunnelDistanceField(List<Point> ruinPositions)
        {
            int density = 1000;
            distanceField = new List<(Point point, double distance)>();

            if (Mirrored)
            {
                for (int x = Size.X - 1; x >= 0; x -= density)
                {
                    for (int y = 0; y < Size.Y; y += density)
                    {
                        addPoint(x, y);
                    }
                }
            }
            else
            {
                for (int x = 0; x < Size.X; x += density)
                {
                    for (int y = 0; y < Size.Y; y += density)
                    {
                        addPoint(x, y);
                    }
                }
            }

            void addPoint(int x, int y)
            {
                Point point = new Point(x, y);
                double shortestDistSqr = double.PositiveInfinity;
                foreach (Tunnel tunnel in Tunnels)
                {
                    for (int i = 1; i < tunnel.Nodes.Count; i++)
                    {
                        shortestDistSqr = Math.Min(shortestDistSqr, MathUtils.LineSegmentToPointDistanceSquared(tunnel.Nodes[i - 1], tunnel.Nodes[i], point));
                    }
                }
                if (ruinPositions != null)
                {
                    int ruinSize = 10000;
                    foreach (Point ruinPos in ruinPositions)
                    {
                        double xDiff = Math.Abs(point.X - ruinPos.X);
                        double yDiff = Math.Abs(point.Y - ruinPos.Y);
                        if (xDiff < ruinSize && yDiff < ruinSize)
                        {
                            shortestDistSqr = 0.0f;
                        }
                        else
                        {
                            shortestDistSqr = Math.Min(xDiff * xDiff + yDiff * yDiff, shortestDistSqr);
                        }
                    }
                }
                shortestDistSqr = Math.Min(shortestDistSqr, MathUtils.DistanceSquared((double)point.X, (double)point.Y, (double)startPosition.X, (double)startPosition.Y));
                shortestDistSqr = Math.Min(shortestDistSqr, MathUtils.DistanceSquared((double)point.X, (double)point.Y, (double)startExitPosition.X, (double)borders.Bottom));
                shortestDistSqr = Math.Min(shortestDistSqr, MathUtils.DistanceSquared((double)point.X, (double)point.Y, (double)endPosition.X, (double)endPosition.Y));
                shortestDistSqr = Math.Min(shortestDistSqr, MathUtils.DistanceSquared((double)point.X, (double)point.Y, (double)endExitPosition.X, (double)borders.Bottom));
                distanceField.Add((point, Math.Sqrt(shortestDistSqr)));
            }
        }

        private double GetDistToTunnel(Vector2 position, Tunnel tunnel)
        {
            Point point = position.ToPoint();
            double shortestDistSqr = double.PositiveInfinity;
            for (int i = 1; i < tunnel.Nodes.Count; i++)
            {
                shortestDistSqr = Math.Min(shortestDistSqr, MathUtils.LineSegmentToPointDistanceSquared(tunnel.Nodes[i - 1], tunnel.Nodes[i], point));
            }
            return Math.Sqrt(shortestDistSqr);
        }

        private DestructibleLevelWall CreateIceChunk(IEnumerable<GraphEdge> edges, Vector2 position, float? health = null)
        {
            List<Vector2> vertices = new List<Vector2>();
            foreach (GraphEdge edge in edges)
            {
                if (!vertices.Any())
                {
                    vertices.Add(edge.Point1);
                }
                else if (!vertices.Any(v => v.NearlyEquals(edge.Point1)))
                {
                    vertices.Add(edge.Point1);
                }
                else if (!vertices.Any(v => v.NearlyEquals(edge.Point2)))
                {
                    vertices.Add(edge.Point2);
                }
            }
            if (vertices.Count < 3) { return null; }
            return CreateIceChunk(vertices.Select(v => v - position).ToList(), position, health);
        }

        private DestructibleLevelWall CreateIceChunk(List<Vector2> vertices, Vector2 position, float? health = null)
        {
            DestructibleLevelWall newChunk = new DestructibleLevelWall(vertices, Color.White, this, health, true);
            newChunk.Body.Position = ConvertUnits.ToSimUnits(position);
            newChunk.Cells.ForEach(c => c.Translation = position);
            newChunk.Body.BodyType = BodyType.Dynamic;
            newChunk.Body.FixedRotation = true;
            newChunk.Body.LinearDamping = 0.5f;
            newChunk.Body.IgnoreGravity = true;
            newChunk.Body.Mass *= 10.0f;
            return newChunk;
        }

        private DestructibleLevelWall CreateIceSpire(List<GraphEdge> usedSpireEdges)
        {
            const float maxLength = 15000.0f;
            float minEdgeLength = 100.0f;
            var mainPathPos = PositionsOfInterest.GetRandom(pos => pos.PositionType == PositionType.MainPath, Rand.RandSync.ServerAndClient);
            double closestDistSqr = double.PositiveInfinity;
            GraphEdge closestEdge = null;
            VoronoiCell closestCell = null;
            foreach (VoronoiCell cell in cells)
            {
                if (cell.CellType != CellType.Solid) { continue; }
                foreach (GraphEdge edge in cell.Edges)
                {
                    if (!edge.IsSolid || usedSpireEdges.Contains(edge) || edge.NextToCave) { continue; }
                    //don't spawn spires near the start/end of the level
                    if (edge.Center.Y > Size.Y / 2 && (edge.Center.X < Size.X * 0.3f || edge.Center.X > Size.X * 0.7f)) { continue; }
                    if (Vector2.DistanceSquared(edge.Center, StartPosition) < maxLength * maxLength) { continue; }
                    if (Vector2.DistanceSquared(edge.Center, EndPosition) < maxLength * maxLength) { continue; }
                    //don't spawn on very long or very short edges
                    float edgeLengthSqr = Vector2.DistanceSquared(edge.Point1, edge.Point2);
                    if (edgeLengthSqr > 1000.0f * 1000.0f || edgeLengthSqr < minEdgeLength * minEdgeLength) { continue; }
                    //don't spawn on edges facing away from the main path
                    if (Vector2.Dot(Vector2.Normalize(mainPathPos.Position.ToVector2()) - edge.Center, edge.GetNormal(cell)) < 0.5f) { continue; }
                    double distSqr = MathUtils.DistanceSquared(edge.Center.X, edge.Center.Y, mainPathPos.Position.X, mainPathPos.Position.Y);
                    if (distSqr < closestDistSqr)
                    {
                        closestDistSqr = distSqr;
                        closestEdge = edge; 
                        closestCell = cell;
                    }
                }
            }

            if (closestEdge == null) { return null; }

            usedSpireEdges.Add(closestEdge);

            Vector2 edgeNormal = closestEdge.GetNormal(closestCell);
            float spireLength = (float)Math.Min(Math.Sqrt(closestDistSqr), maxLength);
            spireLength *= MathHelper.Lerp(0.3f, 1.5f, Difficulty / 100.0f);

            Vector2 extrudedPoint1 = closestEdge.Point1 + edgeNormal * spireLength * Rand.Range(0.8f, 1.0f, Rand.RandSync.ServerAndClient);
            Vector2 extrudedPoint2 = closestEdge.Point2 + edgeNormal * spireLength * Rand.Range(0.8f, 1.0f, Rand.RandSync.ServerAndClient);
            List<Vector2> vertices = new List<Vector2>()
            {
                closestEdge.Point1,
                extrudedPoint1 + (extrudedPoint2 - extrudedPoint1) * Rand.Range(0.3f, 0.45f, Rand.RandSync.ServerAndClient),
                extrudedPoint2 + (extrudedPoint1 - extrudedPoint2) * Rand.Range(0.3f, 0.45f, Rand.RandSync.ServerAndClient),
                closestEdge.Point2,
            };
            Vector2 center = Vector2.Zero;
            vertices.ForEach(v => center += v);
            center /= vertices.Count;
            DestructibleLevelWall spire = new DestructibleLevelWall(vertices.Select(v => v - center).ToList(), Color.White, this, health: 100.0f, giftWrap: true);
#if CLIENT
            //make the edge at the bottom of the spire non-solid
            foreach (GraphEdge edge in spire.Cells[0].Edges)
            {
                if ((edge.Point1.NearlyEquals(closestEdge.Point1 - center) && edge.Point2.NearlyEquals(closestEdge.Point2 - center)) ||
                    (edge.Point1.NearlyEquals(closestEdge.Point2 - center) && edge.Point2.NearlyEquals(closestEdge.Point1 - center)))
                {
                    edge.IsSolid = false;
                    break;
                }
            }
            spire.GenerateVertices();
#endif
            spire.Body.Position = ConvertUnits.ToSimUnits(center);
            spire.Body.BodyType = BodyType.Static;
            spire.Body.FixedRotation = true;
            spire.Body.IgnoreGravity = true;
            spire.Body.Mass *= 10.0f;
            spire.Cells.ForEach(c => c.Translation = center);
            spire.WallDamageOnTouch = 50.0f;
            return spire;
        }

        // TODO: Improve this temporary level editor debug solution (or remove it)
        private static int nextPathPointId;
        public List<PathPoint> PathPoints { get; } = new List<PathPoint>();
        public struct PathPoint
        {
            public string Id { get; }
            public Vector2 Position { get; }
            public bool ShouldContainResources { get; set; }
            public float NextClusterProbability
            {
                get
                {
                    return ClusterLocations.Count switch
                    {
                        1 => 5.0f,
                        2 => 2.5f,
                        3 => 1.0f,
                        _ => 0.0f,
                    };
                }
            }
            public List<Identifier> ResourceTags { get; }
            public List<Identifier> ResourceIds { get; }
            public List<ClusterLocation> ClusterLocations { get; }
            public TunnelType TunnelType { get; }

            private PathPoint(string id, Vector2 position, bool shouldContainResources, TunnelType tunnelType, List<Identifier> resourceTags, List<Identifier> resourceIds, List<ClusterLocation> clusterLocations)
            {
                Id = id;
                Position = position;
                ShouldContainResources = shouldContainResources;
                ResourceTags = resourceTags;
                ResourceIds = resourceIds;
                ClusterLocations = clusterLocations;
                TunnelType = tunnelType;
            }

            public PathPoint(string id, Vector2 position, bool shouldContainResources, TunnelType tunnelType)
                : this(id, position, shouldContainResources, tunnelType, new List<Identifier>(), new List<Identifier>(), new List<ClusterLocation>())
            {

            }

            public PathPoint WithResources(bool containsResources)
            {
                return new PathPoint(Id, Position, containsResources, TunnelType, ResourceTags, ResourceIds, ClusterLocations);
            }
        }

        public List<ClusterLocation> AbyssResources { get; } = new List<ClusterLocation>();
        public struct ClusterLocation
        {
            public VoronoiCell Cell { get; }
            public GraphEdge Edge { get; }
            public Vector2 EdgeCenter { get; }
            /// <summary>
            /// Can be null unless initialized in constructor
            /// </summary>
            public List<Item> Resources { get; private set; }

            /// <param name="initializeResourceList">List is initialized only when specified, otherwise will be null</param>
            public ClusterLocation(VoronoiCell cell, GraphEdge edge, bool initializeResourceList = false)
            {
                Cell = cell;
                Edge = edge;
                EdgeCenter = edge.Center;
                Resources = initializeResourceList ? new List<Item>() : null;
            }

            public bool Equals(ClusterLocation anotherLocation) =>
                Cell == anotherLocation.Cell && Edge == anotherLocation.Edge;

            public bool Equals(VoronoiCell cell, GraphEdge edge) =>
                Cell == cell && Edge == edge;

            public void InitializeResources()
            {
                Resources = new List<Item>();
            }
        }

        // TODO: Take into account items which aren't ores or plants
        // Such as the exploding crystals in The Great Sea
        private void GenerateItems()
        {
            ResetRandomSeed();
            var levelResources = new List<(ItemPrefab itemPrefab, ItemPrefab.CommonnessInfo commonnessInfo)>();
            var fixedResources = new List<(ItemPrefab itemPrefab, ItemPrefab.FixedQuantityResourceInfo resourceInfo)>();
            Vector2 commonnessRange = new Vector2(float.MaxValue, float.MinValue), caveCommonnessRange = new Vector2(float.MaxValue, float.MinValue);
            foreach (ItemPrefab itemPrefab in ItemPrefab.Prefabs.OrderBy(p => p.UintIdentifier))
            {
                if (itemPrefab.GetCommonnessInfo(this) is { CanAppear: true } commonnessInfo)
                {
                    if (commonnessInfo.Commonness > 0.0)
                    {
                        if (commonnessInfo.Commonness < commonnessRange.X) { commonnessRange.X = commonnessInfo.Commonness; }
                        if (commonnessInfo.Commonness > commonnessRange.Y) { commonnessRange.Y = commonnessInfo.Commonness; }
                    }
                    if (commonnessInfo.CaveCommonness > 0.0)
                    {
                        if (commonnessInfo.CaveCommonness < caveCommonnessRange.X) { caveCommonnessRange.X = commonnessInfo.CaveCommonness; }
                        if (commonnessInfo.CaveCommonness > caveCommonnessRange.Y) { caveCommonnessRange.Y = commonnessInfo.CaveCommonness; }
                    }
                    levelResources.Add((itemPrefab, commonnessInfo));
                }
                else if (itemPrefab.LevelQuantity.TryGetValue(GenerationParams.Identifier, out var fixedQuantityResourceInfo) ||
                        itemPrefab.LevelQuantity.TryGetValue(LevelData.Biome.Identifier, out fixedQuantityResourceInfo) ||
                        itemPrefab.LevelQuantity.TryGetValue(Identifier.Empty, out fixedQuantityResourceInfo))
                {
                    fixedResources.Add((itemPrefab, fixedQuantityResourceInfo));
                }
            }

            DebugConsole.Log("Generating level resources...");
            var allValidLocations = GetAllValidClusterLocations();
            var maxResourceOverlap = 0.4f;

            foreach (var (itemPrefab, resourceInfo) in fixedResources)
            {
                for (int i = 0; i < resourceInfo.ClusterQuantity; i++)
                {
                    var location = allValidLocations.GetRandom(l =>
                    {
                        if (l.Cell == null || l.Edge == null) { return false; }
                        if (resourceInfo.IsIslandSpecific && !l.Cell.Island) { return false; }
                        if (!resourceInfo.AllowAtStart && l.EdgeCenter.Y > startPosition.Y && l.EdgeCenter.X < Size.X * 0.25f) { return false; }
                        if (l.Edge.Length < itemPrefab.Size.X) { return false; }
                        if (l.EdgeCenter.Y < AbyssArea.Bottom) { return false; }
                        return resourceInfo.ClusterSize <= GetMaxResourcesOnEdge(itemPrefab, l, out _);

                    }, randSync: Rand.RandSync.ServerAndClient);

                    if (location.Cell == null || location.Edge == null) { break; }

                    PlaceResources(itemPrefab, resourceInfo.ClusterSize, location, out _);
                    var locationIndex = allValidLocations.FindIndex(l => l.Equals(location));
                    allValidLocations.RemoveAt(locationIndex);
                }
            }

            // Abyss Resources
            AbyssResources.Clear();

            var abyssResourcePrefabs = levelResources.Where(r => r.commonnessInfo.AbyssCommonness > 0.0f);
            if (abyssResourcePrefabs.Any())
            {
                int abyssClusterCount = (int)MathHelper.Lerp(GenerationParams.AbyssResourceClustersMin, GenerationParams.AbyssResourceClustersMax, MathUtils.InverseLerp(LevelData.Biome.MinDifficulty, LevelData.Biome.AdjustedMaxDifficulty, Difficulty));
                for (int i = 0; i < abyssClusterCount; i++)
                {
                    var selectedPrefab = ToolBox.SelectWeightedRandom(
                        abyssResourcePrefabs.Select(r => r.itemPrefab).ToList(),
                        abyssResourcePrefabs.Select(r => r.commonnessInfo.AbyssCommonness).ToList(),
                        Rand.RandSync.ServerAndClient);

                    var location = allValidLocations.GetRandom(l =>
                    {
                        if (l.Cell == null || l.Edge == null) { return false; }
                        if (l.EdgeCenter.Y > AbyssArea.Bottom) { return false; }
                        if (l.Edge.Length < selectedPrefab.Size.X) { return false; }
                        l.InitializeResources();
                        return l.Resources.Count <= GetMaxResourcesOnEdge(selectedPrefab, l, out _);
                    }, randSync: Rand.RandSync.ServerAndClient);

                    if (location.Cell == null || location.Edge == null) { break; }

                    int clusterSize = Rand.Range(GenerationParams.ResourceClusterSizeRange.X, GenerationParams.ResourceClusterSizeRange.Y + 1, Rand.RandSync.ServerAndClient);
                    PlaceResources(selectedPrefab, clusterSize, location, out var placedResources, maxResourceOverlap: 0);
                    var abyssClusterLocation = new ClusterLocation(location.Cell, location.Edge, initializeResourceList: true);
                    abyssClusterLocation.Resources.AddRange(placedResources);
                    AbyssResources.Add(abyssClusterLocation);

                    var locationIndex = allValidLocations.FindIndex(l => l.Equals(location));
                    allValidLocations.RemoveAt(locationIndex);
                }
            }


            PathPoints.Clear();
            nextPathPointId = 0;

            foreach (Tunnel tunnel in Tunnels)
            {
                var tunnelLength = 0.0f;
                for (int i = 1; i < tunnel.Nodes.Count; i++)
                {
                    tunnelLength += Vector2.Distance(tunnel.Nodes[i - 1].ToVector2(), tunnel.Nodes[i].ToVector2());
                }

                var nextNodeIndex = 1;
                var positionOnPath = tunnel.Nodes.First().ToVector2();
                var lastNodePos = tunnel.Nodes.Last().ToVector2();
                var reachedLastNode = false;
                var intervalRange = tunnel.Type != TunnelType.Cave ? GenerationParams.ResourceIntervalRange : GenerationParams.CaveResourceIntervalRange;
                do
                {
                    var distance = Rand.Range(intervalRange.X, intervalRange.Y, sync: Rand.RandSync.ServerAndClient);
                    reachedLastNode = !CalculatePositionOnPath();
                    var id = Tunnels.IndexOf(tunnel) + ":" + nextPathPointId++;
                    var spawnChance = tunnel.Type == TunnelType.Cave || tunnel.ParentTunnel?.Type == TunnelType.Cave ?
                        GenerationParams.CaveResourceSpawnChance : GenerationParams.ResourceSpawnChance;
                    var containsResources = true;
                    if (spawnChance < 1.0f)
                    {
                        var spawnPointRoll = Rand.Range(0.0f, 1.0f, sync: Rand.RandSync.ServerAndClient);
                        containsResources = spawnPointRoll <= spawnChance;
                    }
                    var tunnelType = tunnel.Type;
                    if (tunnel.ParentTunnel != null && tunnel.ParentTunnel.Type == TunnelType.Cave) { tunnelType = TunnelType.Cave;  }
                    PathPoints.Add(new PathPoint(id, positionOnPath, containsResources, tunnel.Type));

                    bool CalculatePositionOnPath(float checkedDist = 0.0f)
                    {
                        if (nextNodeIndex >= tunnel.Nodes.Count) { return false; }
                        var distToNextNode = Vector2.Distance(positionOnPath, tunnel.Nodes[nextNodeIndex].ToVector2());
                        var lerpAmount = (distance - checkedDist) / distToNextNode;
                        if (lerpAmount <= 1.0f)
                        {
                            positionOnPath = Vector2.Lerp(positionOnPath, tunnel.Nodes[nextNodeIndex].ToVector2(), lerpAmount);
                            return true;
                        }
                        else
                        {
                            positionOnPath = tunnel.Nodes[nextNodeIndex++].ToVector2();
                            return CalculatePositionOnPath(checkedDist + distToNextNode);
                        }
                    }
                } while (!reachedLastNode && Vector2.DistanceSquared(positionOnPath, lastNodePos) > (intervalRange.Y * intervalRange.Y));
            }

            int itemCount = 0;
            Identifier[] exclusiveResourceTags = new Identifier[2] { "ore".ToIdentifier(), "plant".ToIdentifier() };

            var disabledPathPoints = new List<string>();
            // Create first cluster for each spawn point
            foreach (var pathPoint in PathPoints)
            {
                if (itemCount >= GenerationParams.ItemCount) { break; }
                if (!pathPoint.ShouldContainResources) { continue; }
                GenerateFirstCluster(pathPoint);
                if (pathPoint.ClusterLocations.Count > 0) { continue; }
                disabledPathPoints.Add(pathPoint.Id);
            }
            // Don't try to spawn more resource clusters for points for which the initial cluster could not be spawned
            foreach (string pathPointId in disabledPathPoints)
            {
                if (PathPoints.FirstOrNull(p => p.Id == pathPointId) is PathPoint pathPoint)
                {
                    PathPoints.RemoveAll(p => p.Id == pathPointId);
                    PathPoints.Add(pathPoint.WithResources(false));
                }
            }

            var excludedPathPointIds = new List<string>();
            while (itemCount < GenerationParams.ItemCount)
            {
                var availablePathPoints = PathPoints.Where(p =>
                    p.ShouldContainResources && p.NextClusterProbability > 0 &&
                    !excludedPathPointIds.Contains(p.Id)).ToList();

                if (availablePathPoints.None()) { break; }

                var pathPoint = ToolBox.SelectWeightedRandom(
                    availablePathPoints,
                    availablePathPoints.Select(p => p.NextClusterProbability).ToList(),
                    Rand.RandSync.ServerAndClient);

                GenerateAdditionalCluster(pathPoint);
            }

#if DEBUG
            int spawnPointsContainingResources = PathPoints.Where(p => p.ClusterLocations.Any()).Count();
            string percentage = string.Format(CultureInfo.InvariantCulture, "{0:P2}", (float)spawnPointsContainingResources / PathPoints.Count);
            DebugConsole.NewMessage($"Level resources spawned: {itemCount}\n" +
                $"   Spawn points containing resources: {spawnPointsContainingResources} ({percentage})\n" +
                $"   Total value: {PathPoints.Sum(p => p.ClusterLocations.Sum(c => c.Resources.Sum(r => r.Prefab.DefaultPrice?.Price ?? 0)))} mk");
            if (AbyssResources.Count > 0)
            {
                DebugConsole.NewMessage($"Abyss resources spawned: {AbyssResources.Sum(a => a.Resources.Count)}\n" +
                    $"   Total value: {AbyssResources.Sum(c => c.Resources.Sum(r => r.Prefab.DefaultPrice?.Price ?? 0))} mk");
            }
#endif

            DebugConsole.Log("Level resources generated");

            bool GenerateFirstCluster(PathPoint pathPoint)
            {
                var intervalRange = pathPoint.TunnelType != TunnelType.Cave ?
                    GenerationParams.ResourceIntervalRange : GenerationParams.CaveResourceIntervalRange;
                allValidLocations.Sort((x, y) =>
                    Vector2.DistanceSquared(pathPoint.Position, x.EdgeCenter)
                    .CompareTo(Vector2.DistanceSquared(pathPoint.Position, y.EdgeCenter)));
                var selectedLocationIndex = -1;
                var generatedCluster = false;
                for (int i = 0; i < allValidLocations.Count; i++)
                {
                    var validLocation = allValidLocations[i];
                    if (!IsNextToTunnelType(validLocation.Edge, pathPoint.TunnelType)) { continue; }
                    if (validLocation.EdgeCenter.Y < AbyssArea.Bottom) { continue; }
                    var distanceSquaredToEdge = Vector2.DistanceSquared(pathPoint.Position, validLocation.EdgeCenter);
                    // Edge isn't too far from the path point
                    if (distanceSquaredToEdge > 3.0f * (intervalRange.Y * intervalRange.Y)) { continue; }
                    // Edge is closer to the path point than the cell center
                    if (distanceSquaredToEdge > Vector2.DistanceSquared(pathPoint.Position, validLocation.Cell.Center)) { continue; }

                    var validComparedToOtherPathPoints = true;
                    // Make sure this path point is closest to 'validLocation'
                    foreach (var anotherPathPoint in PathPoints)
                    {
                        if (anotherPathPoint.Id == pathPoint.Id) { continue; }
                        if (Vector2.DistanceSquared(anotherPathPoint.Position, validLocation.EdgeCenter) < distanceSquaredToEdge)
                        {
                            validComparedToOtherPathPoints = false;
                            break;
                        }
                    }

                    foreach (var anotherPathPoint in PathPoints.Where(p => p.Id != pathPoint.Id && p.ClusterLocations.Any()))
                    {
                        if (!validComparedToOtherPathPoints) { break; }
                        foreach (var c in pathPoint.ClusterLocations)
                        {
                            if (IsInvalidComparedToExistingLocation())
                            {
                                validComparedToOtherPathPoints = false;
                                break;
                            }

                            bool IsInvalidComparedToExistingLocation()
                            {
                                if (c.Equals(validLocation)) { return true; }
                                // If there is a previously spawned cluster too near
                                if (Vector2.DistanceSquared(c.EdgeCenter, validLocation.EdgeCenter) > (intervalRange.X * intervalRange.X))  { return true; }
                                // If there is a line from a previous path point to one of its existing cluster locations
                                // which intersects with the line from this path point to the new possible cluster location
                                if (MathUtils.LineSegmentsIntersect(anotherPathPoint.Position, c.EdgeCenter, pathPoint.Position, validLocation.EdgeCenter))  { return true; }
                                return false;
                            }
                        }
                    }

                    if (!validComparedToOtherPathPoints) { continue; }
                    generatedCluster = CreateResourceCluster(pathPoint, validLocation);
                    selectedLocationIndex = i;
                    break;
                }

                if (selectedLocationIndex >= 0)
                {
                    allValidLocations.RemoveAt(selectedLocationIndex);
                }

                return generatedCluster;

                static bool IsNextToTunnelType(GraphEdge e, TunnelType t) =>
                    (e.NextToMainPath && t == TunnelType.MainPath) ||
                    (e.NextToSidePath && t == TunnelType.SidePath) ||
                    (e.NextToCave && t == TunnelType.Cave);
            }

            bool GenerateAdditionalCluster(PathPoint pathPoint)
            {
                var validLocations = new List<ClusterLocation>();
                // First check only the edges of the same cell
                // which are connected to one of the existing edges with clusters
                foreach (var clusterLocation in pathPoint.ClusterLocations)
                {
                    foreach (var anotherEdge in clusterLocation.Cell.Edges.Where(e => e != clusterLocation.Edge))
                    {
                        if (HaveConnectingEdgePoints(anotherEdge, clusterLocation.Edge))
                        {
                            AddIfValid(clusterLocation.Cell, anotherEdge);
                        }
                    }
                }

                // Only check edges of adjacent cells if no valid edges were found
                // on any of the cells with existing clusters
                if (validLocations.None())
                {
                    foreach (var clusterLocation in pathPoint.ClusterLocations)
                    {
                        foreach (var anotherEdge in clusterLocation.Cell.Edges.Where(e => e != clusterLocation.Edge))
                        {
                            var adjacentCell = anotherEdge.AdjacentCell(clusterLocation.Cell);
                            if (adjacentCell == null) { continue; }
                            foreach (var adjacentCellEdge in adjacentCell.Edges.Where(e => e != anotherEdge))
                            {
                                if (HaveConnectingEdgePoints(adjacentCellEdge, clusterLocation.Edge))
                                {
                                    AddIfValid(adjacentCell, adjacentCellEdge);
                                }
                            }
                        }
                    }
                }

                if (validLocations.Any())
                {
                    var location = validLocations.GetRandom(randSync: Rand.RandSync.ServerAndClient);
                    if (CreateResourceCluster(pathPoint, location))
                    {
                        var i = allValidLocations.FindIndex(l => l.Equals(location));
                        if (i >= 0)
                        {
                            allValidLocations.RemoveAt(i);
                        }
                        return true;
                    }
                    else
                    {
                        excludedPathPointIds.Add(pathPoint.Id);
                        return false;
                    }
                }
                else
                {
                    excludedPathPointIds.Add(pathPoint.Id);
                    return false;
                }

                static bool HaveConnectingEdgePoints(GraphEdge e1, GraphEdge e2) =>
                    e1.Point1.NearlyEquals(e2.Point1) || e1.Point1.NearlyEquals(e2.Point2) ||
                    e1.Point2.NearlyEquals(e2.Point1) || e1.Point2.NearlyEquals(e2.Point2);

                void AddIfValid(VoronoiCell c, GraphEdge e)
                {
                    if (IsAlreadyInList(e)) { return; }
                    if (allValidLocations.None(l => l.Equals(c, e))) { return; }
                    if (pathPoint.ClusterLocations.Any(cl => cl.Edge == e)) { return; }
                    validLocations.Add(new ClusterLocation(c, e));
                }

                bool IsAlreadyInList(GraphEdge edge) =>
                    validLocations.Any(l => l.Edge == edge);
            }

            bool CreateResourceCluster(PathPoint pathPoint, ClusterLocation location)
            {
                if (location.Cell == null || location.Edge == null) { return false; }

                ItemPrefab selectedPrefab;
                if (pathPoint.ClusterLocations.Count == 0)
                {
                    selectedPrefab = ToolBox.SelectWeightedRandom(
                        levelResources.Select(it => it.itemPrefab).ToList(),
                        levelResources.Select(it => it.commonnessInfo.GetCommonness(pathPoint.TunnelType)).ToList(),
                        Rand.RandSync.ServerAndClient);
                    selectedPrefab.Tags.ForEach(t =>
                    {
                        if (exclusiveResourceTags.Contains(t))
                        {
                            pathPoint.ResourceTags.Add(t);
                        }
                    });
                }
                else
                {
                    var filteredResources = pathPoint.ResourceTags.None() ? levelResources :
                        levelResources.Where(it => it.itemPrefab.Tags.Any(t => pathPoint.ResourceTags.Contains(t)));
                    selectedPrefab = ToolBox.SelectWeightedRandom( 
                        filteredResources.Select(it => it.itemPrefab).ToList(),
                        filteredResources.Select(it => it.commonnessInfo.GetCommonness(pathPoint.TunnelType)).ToList(),
                        Rand.RandSync.ServerAndClient);
                }

                if (selectedPrefab == null) { return false; }

                // Create resources for the cluster
                float commonness = levelResources.First(r => r.itemPrefab == selectedPrefab).commonnessInfo.GetCommonness(pathPoint.TunnelType);
                float lerpAmount = pathPoint.TunnelType != TunnelType.Cave ?
                    MathUtils.InverseLerp(commonnessRange.X, commonnessRange.Y, commonness) : 
                    MathUtils.InverseLerp(caveCommonnessRange.X, caveCommonnessRange.Y, commonness);
                var maxClusterSize = (int)MathHelper.Lerp(GenerationParams.ResourceClusterSizeRange.X, GenerationParams.ResourceClusterSizeRange.Y, lerpAmount);
                var maxFitOnEdge = GetMaxResourcesOnEdge(selectedPrefab, location, out var edgeLength);
                maxClusterSize = Math.Min(maxClusterSize, maxFitOnEdge);
                if (itemCount + maxClusterSize > GenerationParams.ItemCount)
                {
                    maxClusterSize += GenerationParams.ItemCount - (itemCount + maxClusterSize);
                }

                if (maxClusterSize < 1) { return false; }

                var minClusterSize = Math.Min(GenerationParams.ResourceClusterSizeRange.X, maxClusterSize);
                var resourcesInCluster = maxClusterSize == 1 ? 1 : Rand.Range(minClusterSize, maxClusterSize + 1, sync: Rand.RandSync.ServerAndClient);

                if (resourcesInCluster < 1) { return false; }

                PlaceResources(selectedPrefab, resourcesInCluster, location, out var placedResources, edgeLength: edgeLength);
                itemCount += resourcesInCluster;
                location.InitializeResources();
                location.Resources.AddRange(placedResources);
                pathPoint.ClusterLocations.Add(location);
                pathPoint.ResourceIds.Add(selectedPrefab.Identifier);

                return true;
            }

            int GetMaxResourcesOnEdge(ItemPrefab resourcePrefab, ClusterLocation location, out float edgeLength)
            {
                edgeLength = 0.0f;
                if (location.Cell == null || location.Edge == null) { return 0; } 
                edgeLength = Vector2.Distance(location.Edge.Point1, location.Edge.Point2);
                if (resourcePrefab == null) { return 0; }
                return (int)Math.Floor(edgeLength / ((1.0f - maxResourceOverlap) * resourcePrefab.Size.X));
            }
        }

        /// <param name="rotation">Used by clients to set the rotation for the resources</param>
        public List<Item> GenerateMissionResources(ItemPrefab prefab, int requiredAmount, PositionType positionType, IEnumerable<Cave> targetCaves = null)
        {
            var allValidLocations = GetAllValidClusterLocations();
            var placedResources = new List<Item>();

            // if there are no valid locations, don't place anything
            if (allValidLocations.None()) { return placedResources; }

            // Make sure not to pick a spot that already has other level resources
            for (int i = allValidLocations.Count - 1; i >= 0; i--)
            {
                if (HasResources(allValidLocations[i]))
                {
                    allValidLocations.RemoveAt(i);
                }

                bool HasResources(ClusterLocation clusterLocation)
                {
                    foreach (var p in PathPoints)
                    {
                        foreach (var c in p.ClusterLocations)
                        {
                            if (!c.Equals(clusterLocation)) { continue; }
                            foreach (var r in c.Resources)
                            {
                                if (r == null) { continue; }
                                if (r.Removed) { continue; }
                                if (!(r.GetComponent<Holdable>() is Holdable h) || (h.Attachable && h.Attached)) { return true; }
                            }
                        }
                    }
                    return false;
                }
            }

            if (PositionsOfInterest.None(p => p.PositionType == positionType))
            {
                DebugConsole.AddWarning($"Failed to find a position of the type \"{positionType}\" for mission resources.");
                foreach (var validType in MineralMission.ValidPositionTypes)
                {
                    if (validType != positionType && PositionsOfInterest.Any(p => p.PositionType == validType))
                    {
                        DebugConsole.AddWarning($"Placing in \"{validType}\" instead.");
                        positionType = validType;
                        break;
                    }
                }
            }

            try
            {
                RemoveInvalidLocations(positionType switch
                {
                    PositionType.MainPath => IsOnMainPath,
                    PositionType.SidePath => IsOnSidePath,
                    PositionType.Cave => IsInCave,
                    PositionType.AbyssCave => IsInAbyssCave,
                    _ => throw new NotImplementedException(),
                });
            }
            catch (NotImplementedException)
            {
                DebugConsole.ThrowError($"Unexpected PositionType (\"{positionType}\") for mineral mission resources: mineral spawning might not work as expected.");
            }

            if (targetCaves != null && targetCaves.Any())
            {
                // If resources are placed inside a cave, make sure all of them are placed inside the same one
                allValidLocations.RemoveAll(l => targetCaves.None(c => c.Area.Contains(l.EdgeCenter)));
            }

            var poi = PositionsOfInterest.GetRandom(p => p.PositionType == positionType, randSync: Rand.RandSync.ServerAndClient);
            Vector2 poiPos = poi.Position.ToVector2();
            allValidLocations.Sort((x, y) => Vector2.DistanceSquared(poiPos, x.EdgeCenter)
                .CompareTo(Vector2.DistanceSquared(poiPos, y.EdgeCenter)));
            float maxResourceOverlap = 0.4f;
            var selectedLocation = allValidLocations.FirstOrDefault(l =>
                Vector2.Distance(l.Edge.Point1, l.Edge.Point2) is float edgeLength &&
                !l.Edge.OutsideLevel &&
                ((l.Edge.Cell1?.IsDestructible ?? false) || (l.Edge.Cell2?.IsDestructible ?? false)) &&
                requiredAmount <= (int)Math.Floor(edgeLength / ((1.0f - maxResourceOverlap) * prefab.Size.X)));


            if (selectedLocation.Edge == null)
            {
                //couldn't find a long enough edge, find the largest one
                float longestEdge = 0.0f;
                foreach (var validLocation in allValidLocations)
                {
                    if (Vector2.Distance(validLocation.Edge.Point1, validLocation.Edge.Point2) is float edgeLength && edgeLength > longestEdge)
                    {
                        selectedLocation = validLocation;
                        longestEdge = edgeLength;
                    }
                }
            }
            if (selectedLocation.Edge == null)
            {
                throw new Exception("Failed to find a suitable level wall edge to place level resources on.");
            }
            PlaceResources(prefab, requiredAmount, selectedLocation, out placedResources);
            Vector2 edgeNormal = selectedLocation.Edge.GetNormal(selectedLocation.Cell);
            return placedResources;

            static bool IsOnMainPath(ClusterLocation location) => location.Edge.NextToMainPath;
            static bool IsOnSidePath(ClusterLocation location) => location.Edge.NextToSidePath;
            static bool IsInCave(ClusterLocation location) => location.Edge.NextToCave;
            bool IsInAbyssCave(ClusterLocation location) => location.EdgeCenter.Y < AbyssStart;
            void RemoveInvalidLocations(Predicate<ClusterLocation> match)
            {
                allValidLocations.RemoveAll(l => !match(l));
            }
        }

        private List<ClusterLocation> GetAllValidClusterLocations()
        {
            var subBorders = new List<Rectangle>();
            Wrecks.ForEach(AddBordersToList);
            AddBordersToList(BeaconStation);

            var locations = new List<ClusterLocation>();
            foreach (var c in GetAllCells())
            {
                if (c.CellType != CellType.Solid) { continue; }
                foreach (var e in c.Edges)
                {
                    if (IsValidEdge(e))
                    {
                        locations.Add(new ClusterLocation(c, e));
                    }
                }
            }
            return locations;

            void AddBordersToList(Submarine s)
            {
                if (s == null) { return; }
                var rect = Submarine.AbsRect(s.WorldPosition, s.Borders.Size.ToVector2());
                // range of piezo crystal discharge is 3500, pad the rect to ensure no such kind of hazards spawn near
                rect.Inflate(4000, 4000);
                subBorders.Add(rect);
            }

            bool IsValidEdge(GraphEdge e)
            {
                if (!e.IsSolid) { return false; }
                if (e.OutsideLevel) { return false; }
                var eCenter = e.Center;
                if (IsBlockedByWreckOrBeacon()) { return false; }
                if (IsBlockedByWall()) { return false; }
                return true;

                bool IsBlockedByWreckOrBeacon()
                {
                    foreach (var r in subBorders)
                    {
                        if (r.Contains(e.Point1)) { return true; }
                        if (r.Contains(e.Point2)) { return true; }
                        if (r.Contains(eCenter)) { return true; }

                        if (MathUtils.GetLineRectangleIntersection(e.Point1, e.Point2, r, out _))
                        {
                            return true;
                        }
                    }
                    return false;
                }

                bool IsBlockedByWall()
                {
                    foreach (var w in ExtraWalls)
                    {
                        foreach (var c in w.Cells)
                        {
                            if (c.IsPointInside(eCenter)) { return true; }
                            if (c.IsPointInside(eCenter - 100 * e.GetNormal(c))) { return true; }
                            if (c.Edges.Any(extraWallEdge => extraWallEdge == e)) { return true; }
                        }
                    }
                    return false;
                }
            }
        }

        private void PlaceResources(ItemPrefab resourcePrefab, int resourceCount, ClusterLocation location, out List<Item> placedResources,
            float? edgeLength = null, float maxResourceOverlap = 0.4f)
        {
            edgeLength ??= location.Edge.Length;
            Vector2 edgeDir = (location.Edge.Point2 - location.Edge.Point1) / edgeLength.Value;
            if (!MathUtils.IsValid(edgeDir))
            {
                edgeDir = Vector2.Zero;
            }
            float minResourceOverlap = -((edgeLength.Value - (resourceCount * resourcePrefab.Size.X)) / (resourceCount * resourcePrefab.Size.X));
            minResourceOverlap = Math.Clamp(minResourceOverlap, 0, maxResourceOverlap);
            float[] lerpAmounts = new float[resourceCount];
            lerpAmounts[0] = 0.0f;
            float lerpAmount = 0.0f;
            for (int i = 1; i < resourceCount; i++)
            {
                float overlap = Rand.Range(minResourceOverlap, maxResourceOverlap, sync: Rand.RandSync.ServerAndClient);
                lerpAmount = Math.Clamp(lerpAmount + (1.0f - overlap) * resourcePrefab.Size.X / edgeLength.Value, 0.0f, 1.0f);
                lerpAmounts[i] = lerpAmount;
            }

            var startOffset = Rand.Range(0.0f, 1.0f - lerpAmount, sync: Rand.RandSync.ServerAndClient);
            placedResources = new List<Item>();
            for (int i = 0; i < resourceCount; i++)
            {
                Vector2 selectedPos =
                    location.Edge.Length < resourcePrefab.Size.X ?
                    location.Edge.Center :
                    Vector2.Lerp(location.Edge.Point1 + edgeDir * resourcePrefab.Size.X / 2, location.Edge.Point2 - edgeDir * resourcePrefab.Size.X / 2, startOffset + lerpAmounts[i]);
                var item = new Item(resourcePrefab, selectedPos, submarine: null);
                Vector2 edgeNormal = location.Edge.GetNormal(location.Cell);
                float moveAmount = (item.body == null ? item.Rect.Height / 2 : ConvertUnits.ToDisplayUnits(item.body.GetMaxExtent() * 0.7f));
                moveAmount += (item.GetComponent<LevelResource>()?.RandomOffsetFromWall ?? 0.0f) * Rand.Range(-0.5f, 0.5f, Rand.RandSync.ServerAndClient);
                item.Move(edgeNormal * moveAmount);
                item.Rotation = MathHelper.ToDegrees(-MathUtils.VectorToAngle(edgeNormal) + MathHelper.PiOver2);
                if (item.GetComponent<Holdable>() is Holdable h)
                {
                    h.AttachToWall();
                }
                else if (item.body != null)
                {
                    item.body.SetTransformIgnoreContacts(item.body.SimPosition, MathUtils.VectorToAngle(edgeNormal) - MathHelper.PiOver2);
                }
                placedResources.Add(item);
            }
        }

        public Vector2 GetRandomItemPos(PositionType spawnPosType, float randomSpread, float minDistFromSubs, float offsetFromWall = 10.0f, Func<InterestingPosition, bool> filter = null)
        {
            if (!PositionsOfInterest.Any())
            {
                return new Vector2(Size.X / 2, Size.Y / 2);
            }

            Vector2 position = Vector2.Zero;
            int tries = 0;
            do
            {
                TryGetInterestingPosition(true, spawnPosType, minDistFromSubs, out InterestingPosition potentialPos, filter);

                Vector2 offset = Rand.Vector(Rand.Range(0.0f, randomSpread, Rand.RandSync.ServerAndClient), Rand.RandSync.ServerAndClient);
                Vector2 startPos = potentialPos.Position.ToVector2();
                if (!IsPositionInsideWall(startPos + offset))
                {
                    startPos += offset;
                }

                Vector2 endPos = startPos - Vector2.UnitY * Size.Y;

                //try to find a level wall below the position unless the position is indoors
                if (!potentialPos.PositionType.IsEnclosedArea())
                {
                    if (Submarine.PickBody(
                        ConvertUnits.ToSimUnits(startPos),
                        ConvertUnits.ToSimUnits(endPos),
                        ExtraWalls.Where(w => w.Body?.BodyType == BodyType.Dynamic || w is DestructibleLevelWall).Select(w => w.Body).Union(Submarine.Loaded.Where(s => s.Info.Type == SubmarineType.Player).Select(s => s.PhysicsBody.FarseerBody)), 
                        Physics.CollisionLevel | Physics.CollisionWall)?.UserData is VoronoiCell)
                    {
                        position = ConvertUnits.ToDisplayUnits(Submarine.LastPickedPosition) + Vector2.Normalize(startPos - endPos) * offsetFromWall;
                        break;
                    }
                }

                tries++;

                if (tries == 10)
                {
                    position = startPos;
                }

            } while (tries < 10);

            return position;
        }

        public bool TryGetInterestingPositionAwayFromPoint(bool useSyncedRand, PositionType positionType, float minDistFromSubs, out InterestingPosition position, Vector2 awayPoint, float minDistFromPoint, Func<InterestingPosition, bool> filter = null)
        {
            position = default;
            bool success = TryGetInterestingPosition(useSyncedRand, positionType, minDistFromSubs, out position, awayPoint, minDistFromPoint, filter);
            return success;
        }

        public bool TryGetInterestingPosition(bool useSyncedRand, PositionType positionType, float minDistFromSubs, out InterestingPosition position, Func<InterestingPosition, bool> filter = null, bool suppressWarning = false)
        {
            position = default;
            bool success = TryGetInterestingPosition(useSyncedRand, positionType, minDistFromSubs, out position, Vector2.Zero, minDistFromPoint: 0, filter, suppressWarning);
            return success;
        }

        public bool TryGetInterestingPosition(bool useSyncedRand, PositionType positionType, float minDistFromSubs, out InterestingPosition position, Vector2 awayPoint, float minDistFromPoint = 0f, Func<InterestingPosition, bool> filter = null, bool suppressWarning = false)
        {
            if (!PositionsOfInterest.Any())
            {
                position = default;
                return false;
            }

            List<InterestingPosition> suitablePositions = PositionsOfInterest.FindAll(p => positionType.HasFlag(p.PositionType));
            if (filter != null)
            {
                suitablePositions.RemoveAll(p => !filter(p));
            }
            if (positionType.HasFlag(PositionType.MainPath) || positionType.HasFlag(PositionType.SidePath) || positionType.HasFlag(PositionType.Abyss) || 
                positionType.HasFlag(PositionType.Cave) || positionType.HasFlag(PositionType.AbyssCave))
            {
#if DEBUG
                for (int i = 0; i < PositionsOfInterest.Count; i++)
                {
                    var pos = PositionsOfInterest[i];
                    if (!suitablePositions.Contains(pos)) { continue; }
                    if (IsInvalid(pos))
                    {
                        pos.IsValid = false;
                        PositionsOfInterest[i] = pos;
                    }
                }
#endif
                suitablePositions.RemoveAll(p => IsInvalid(p));
                bool IsInvalid(InterestingPosition p) => IsPositionInsideWall(p.Position.ToVector2());
            }
            if (!suitablePositions.Any())
            {
                if (!suppressWarning)
                {
                    string errorMsg = "Could not find a suitable position of interest. (PositionType: " + positionType + ", minDistFromSubs: " + minDistFromSubs + ")\n" + Environment.StackTrace.CleanupStackTrace();
                    GameAnalyticsManager.AddErrorEventOnce("Level.TryGetInterestingPosition:PositionTypeNotFound", GameAnalyticsManager.ErrorSeverity.Error, errorMsg);
#if DEBUG
                    DebugConsole.ThrowError(errorMsg);
#endif
                }
                position = PositionsOfInterest[Rand.Int(PositionsOfInterest.Count, (useSyncedRand ? Rand.RandSync.ServerAndClient : Rand.RandSync.Unsynced))];
                return false;
            }

            List<InterestingPosition> farEnoughPositions = new List<InterestingPosition>(suitablePositions);
            if (minDistFromSubs > 0.0f)
            {
                foreach (Submarine sub in Submarine.Loaded)
                {
                    if (sub.Info.Type != SubmarineType.Player) { continue; }
                    farEnoughPositions.RemoveAll(p => Vector2.DistanceSquared(p.Position.ToVector2(), sub.WorldPosition) < minDistFromSubs * minDistFromSubs);
                }
            }
            if (minDistFromPoint > 0.0f)
            {
                farEnoughPositions.RemoveAll(p => Vector2.DistanceSquared(p.Position.ToVector2(), awayPoint) < minDistFromPoint * minDistFromPoint);
            }

            if (!farEnoughPositions.Any())
            {
                string errorMsg = "Could not find a position of interest far enough from the submarines. (PositionType: " + positionType + ", minDistFromSubs: " + minDistFromSubs + ")\n" + Environment.StackTrace.CleanupStackTrace();
                GameAnalyticsManager.AddErrorEventOnce("Level.TryGetInterestingPosition:TooCloseToSubs", GameAnalyticsManager.ErrorSeverity.Error, errorMsg);
#if DEBUG
                DebugConsole.ThrowError(errorMsg);
#endif
                float maxDist = 0.0f;
                position = suitablePositions.First();
                foreach (InterestingPosition pos in suitablePositions)
                {
                    float dist = Submarine.Loaded.Sum(s => 
                        Submarine.MainSubs.Contains(s) ? Vector2.DistanceSquared(s.WorldPosition, pos.Position.ToVector2()) : 0.0f);
                    if (dist > maxDist)
                    {
                        position = pos;
                        maxDist = dist;
                    }
                }

                return false;
            }

            position = farEnoughPositions[Rand.Int(farEnoughPositions.Count, useSyncedRand ? Rand.RandSync.ServerAndClient : Rand.RandSync.Unsynced)];
            return true;
        }

        public bool IsPositionInsideWall(Vector2 worldPosition)
        {
            var closestCell = GetClosestCell(worldPosition);
            return closestCell != null && closestCell.IsPointInside(worldPosition);
        }

        public void Update(float deltaTime, Camera cam)
        {
            LevelObjectManager.Update(deltaTime);

            foreach (LevelWall wall in ExtraWalls) { wall.Update(deltaTime); }
            for (int i = UnsyncedExtraWalls.Count - 1; i >= 0; i--)
            {
                UnsyncedExtraWalls[i].Update(deltaTime);
            }

#if SERVER
            if (GameMain.NetworkMember is { IsServer: true })
            {
                foreach (LevelWall wall in ExtraWalls) 
                {
                    if (wall is DestructibleLevelWall { NetworkUpdatePending: true } destructibleWall)
                    {
                        GameMain.NetworkMember.CreateEntityEvent(this, new SingleLevelWallEventData(destructibleWall));
                        destructibleWall.NetworkUpdatePending = false;
                    }
                }
                networkUpdateTimer += deltaTime;
                if (networkUpdateTimer > NetworkUpdateInterval)
                {
                    if (ExtraWalls.Any(w => w.Body.BodyType != BodyType.Static))
                    {
                        GameMain.NetworkMember.CreateEntityEvent(this, new GlobalLevelWallEventData());
                    }
                    networkUpdateTimer = 0.0f;
                }
            }
#endif

#if CLIENT
            backgroundCreatureManager.Update(deltaTime, cam);
            WaterRenderer.Instance?.ScrollWater(Vector2.UnitY, (float)deltaTime);
            renderer.Update(deltaTime, cam);
#endif
        }

        public Vector2 GetBottomPosition(float xPosition)
        {
            float interval = Size.X / (bottomPositions.Count - 1);

            int index = (int)Math.Floor(xPosition / interval);
            if (index < 0 || index >= bottomPositions.Count - 1) { return new Vector2(xPosition, BottomPos); }

            float t = (xPosition - bottomPositions[index].X) / interval;
            t = MathHelper.Clamp(t, 0.0f, 1.0f);
            float yPos = MathHelper.Lerp(bottomPositions[index].Y, bottomPositions[index + 1].Y, t);

            return new Vector2(xPosition, yPos);
        }
        
        public List<VoronoiCell> GetAllCells()
        {
            List<VoronoiCell> cells = new List<VoronoiCell>();
            for (int x = 0; x < cellGrid.GetLength(0); x++)
            {
                for (int y = 0; y < cellGrid.GetLength(1); y++)
                {
                    cells.AddRange(cellGrid[x, y]);
                }
            }
            return cells;
        }

        private readonly List<VoronoiCell> tempCells = new List<VoronoiCell>();
        public List<VoronoiCell> GetCells(Vector2 worldPos, int searchDepth = 2)
        {
            tempCells.Clear();
            int gridPosX = (int)Math.Floor(worldPos.X / GridCellSize);
            int gridPosY = (int)Math.Floor(worldPos.Y / GridCellSize);

            int startX = Math.Max(gridPosX - searchDepth, 0);
            int endX = Math.Min(gridPosX + searchDepth, cellGrid.GetLength(0) - 1);

            int startY = Math.Max(gridPosY - searchDepth, 0);
            int endY = Math.Min(gridPosY + searchDepth, cellGrid.GetLength(1) - 1);

            for (int y = startY; y <= endY; y++)
            {
                for (int x = startX; x <= endX; x++)
                {
                    tempCells.AddRange(cellGrid[x, y]);
                }
            }
            
            foreach (LevelWall wall in ExtraWalls)
            {
                if (wall == SeaFloor)
                {
                    if (SeaFloorTopPos < worldPos.Y - searchDepth * GridCellSize) { continue; }
                }
                else
                {
                    if (wall is DestructibleLevelWall destructibleWall && destructibleWall.Destroyed) { continue; }
                    bool closeEnough = false;
                    foreach (VoronoiCell cell in wall.Cells)
                    {
                        if (cell.IsPointInsideAABB(worldPos, margin: (searchDepth + 1) * GridCellSize / 2))
                        { 
                            closeEnough = true;
                            break;
                        }
                    }
                    if (!closeEnough) { continue; }
                }
                foreach (VoronoiCell cell in wall.Cells)
                {
                    tempCells.Add(cell);
                }
            }

            foreach (var abyssIsland in AbyssIslands)
            {
                if (abyssIsland.Area.X > worldPos.X + searchDepth * GridCellSize) { continue; }
                if (abyssIsland.Area.Right < worldPos.X - searchDepth * GridCellSize) { continue; }
                if (abyssIsland.Area.Y > worldPos.Y + searchDepth * GridCellSize) { continue; }
                if (abyssIsland.Area.Bottom < worldPos.Y - searchDepth * GridCellSize) { continue; }

                tempCells.AddRange(abyssIsland.Cells);
            }
            
            return tempCells;
        }

        public VoronoiCell GetClosestCell(Vector2 worldPos)
        {
            double closestDist = double.MaxValue;
            VoronoiCell closestCell = null;
            int searchDepth = 2;
            while (searchDepth < 5)
            {
                foreach (var cell in GetCells(worldPos, searchDepth))
                {
                    double dist = MathUtils.DistanceSquared(cell.Site.Coord.X, cell.Site.Coord.Y, worldPos.X, worldPos.Y);
                    if (dist < closestDist)
                    {
                        closestDist = dist;
                        closestCell = cell;
                    }
                }
                if (closestCell != null) { break; }
                searchDepth++;
            }
            return closestCell;
        }

        private List<VoronoiCell> CreatePathToClosestTunnel(Point pos)
        {
            VoronoiCell closestPathCell = null;
            double closestDist = 0.0f;
            foreach (Tunnel tunnel in Tunnels)
            {
                if (tunnel.Type == TunnelType.Cave) { continue; }
                foreach (VoronoiCell cell in tunnel.Cells)
                {
                    double dist = MathUtils.DistanceSquared(cell.Site.Coord.X, cell.Site.Coord.Y, pos.X, pos.Y);
                    if (closestPathCell == null || dist < closestDist)
                    {
                        closestPathCell = cell;
                        closestDist = dist;
                    }
                }
            }

            //cast a ray from the closest path cell towards the position and remove the cells it hits
            List<VoronoiCell> validCells = cells.FindAll(c => c.CellType != CellType.Empty && c.CellType != CellType.Removed);
            List<VoronoiCell> pathCells = new List<VoronoiCell>() { closestPathCell };
            foreach (VoronoiCell cell in validCells)
            {
                foreach (GraphEdge e in cell.Edges)
                {
                    if (!MathUtils.LineSegmentsIntersect(closestPathCell.Center, pos.ToVector2(), e.Point1, e.Point2)) { continue; }
                    
                    cell.CellType = CellType.Removed;
                    for (int x = 0; x < cellGrid.GetLength(0); x++)
                    {
                        for (int y = 0; y < cellGrid.GetLength(1); y++)
                        {
                            cellGrid[x, y].Remove(cell);
                        }
                    }
                    pathCells.Add(cell);
                    cells.Remove(cell);

                    //go through the edges of this cell and find the ones that are next to a removed cell
                    foreach (var otherEdge in cell.Edges)
                    {
                        var otherAdjacent = otherEdge.AdjacentCell(cell);
                        if (otherAdjacent == null || otherAdjacent.CellType == CellType.Solid) { continue; }

                        //if the edge is very short, remove adjacent cells to prevent making the passage too narrow
                        if (Vector2.DistanceSquared(otherEdge.Point1, otherEdge.Point2) < 500.0f * 500.0f)
                        {
                            foreach (GraphEdge e2 in cell.Edges)
                            {
                                if (e2 == otherEdge || e2 == otherEdge) { continue; }
                                if (!MathUtils.NearlyEqual(otherEdge.Point1, e2.Point1) && !MathUtils.NearlyEqual(otherEdge.Point2, e2.Point1) && !MathUtils.NearlyEqual(otherEdge.Point2, e2.Point2))
                                {
                                    continue;
                                }
                                var adjacentCell = e2.AdjacentCell(cell);
                                if (adjacentCell == null || adjacentCell.CellType == CellType.Removed) { continue; }
                                adjacentCell.CellType = CellType.Removed;
                                for (int x = 0; x < cellGrid.GetLength(0); x++)
                                {
                                    for (int y = 0; y < cellGrid.GetLength(1); y++)
                                    {
                                        cellGrid[x, y].Remove(adjacentCell);
                                    }
                                }
                                cells.Remove(adjacentCell);
                            }
                        }
                    }
                    break;
                }
            }

            pathCells.Sort((c1, c2) => { return Vector2.DistanceSquared(c1.Center, pos.ToVector2()).CompareTo(Vector2.DistanceSquared(c2.Center, pos.ToVector2())); });
            return pathCells;
        }

        public bool IsCloseToStart(Vector2 position, float minDist) => IsCloseToStart(position.ToPoint(), minDist);
        public bool IsCloseToEnd(Vector2 position, float minDist) => IsCloseToEnd(position.ToPoint(), minDist);

        public bool IsCloseToStart(Point position, float minDist)
        {
            return MathUtils.LineSegmentToPointDistanceSquared(startPosition, startExitPosition, position) < minDist * minDist;
        }

        public bool IsCloseToEnd(Point position, float minDist)
        {
            return MathUtils.LineSegmentToPointDistanceSquared(endPosition, endExitPosition, position) < minDist * minDist;
        }

        /// <summary>
        /// Attempts to spawn a submarine (or a beacon station).
        /// </summary>
        /// <param name="subName">Name of submarine.</param>
        /// <param name="contentFile">Content file.</param>
        /// <param name="type">Submarine type.</param>
        /// <param name="thalamusSpawn">Optional parameter to control spawning of thalamus. Only implemented for wrecks.</param>
        /// <param name="spawnInTheMiddle">Should the spawn position be at (or as close as possible to) the middle of the level.</param>
        /// <returns>The spawned submarine.</returns>
        private Submarine SpawnSubOnPath(string subName, ContentFile contentFile, SubmarineType type, 
            LevelData.ThalamusSpawn thalamusSpawn = LevelData.ThalamusSpawn.Random,
            bool spawnInTheMiddle = false)
        {
            var tempSW = Stopwatch.StartNew();
            
            // Min distance between a sub and the start/end/other sub.
            const float minDistance = Sonar.DefaultSonarRange;
            SubmarineInfo info = new SubmarineInfo(contentFile.Path.Value)
            {
                Type = type
            };

            const float horizontalMargin = 1000;
            float distanceBetweenStartAndEnd = Math.Abs(endPosition.X - startPosition.X);
            bool spawnAwayFromStartAndEnd = distanceBetweenStartAndEnd > minDistance * 2 + horizontalMargin;
            var waypoints = WayPoint.WayPointList.Where(IsValidWaypoint).ToList();
            bool IsValidWaypoint(WayPoint wp)
            {
                if (wp.Submarine != null) { return false; }
                if (wp.SpawnType != SpawnType.Path) { return false; }
                if (wp.Tunnel is not { Type: TunnelType.MainPath }) { return false; }
                // Ensure that the wp is not farther than the end exit position.
                if (wp.WorldPosition.X > EndExitPosition.X) { return false; }
                if (spawnAwayFromStartAndEnd)
                {
                    // Make sure that the wp is horizontally far enough from the start position (skips some of the first waypoints)
                    if (Math.Abs(wp.WorldPosition.X - startPosition.X) < minDistance) { return false; }
                    // Also have to check the end position, because it's the start position on mirrored levels (skips some of the last waypoints)
                    if (Math.Abs(wp.WorldPosition.X - endPosition.X) < minDistance) { return false; }
                }
                if (IsCloseToStart(wp.WorldPosition, minDistance)) { return false; }
                if (IsCloseToEnd(wp.WorldPosition, minDistance)) { return false; }
                return true;
            }
            
            if (spawnInTheMiddle)
            {
                float horizontalMiddlePoint = Size.X / 2f;
                float GetHorizontalDistanceToMiddlePoint(WayPoint wp) => Math.Abs(wp.WorldPosition.X - horizontalMiddlePoint);
                waypoints.Sort((wp1, wp2) => GetHorizontalDistanceToMiddlePoint(wp2).CompareTo(GetHorizontalDistanceToMiddlePoint(wp1)));
            }
            else
            {
                // Randomize the list in advance, so that the initial waypoints are taken in the same order (determined by the random seed).
                // If we'd just get a random wp from it when we need, it's not guaranteed that there's equal amount of random calls between mirrored and non-mirrored levels.
                waypoints.Shuffle(Rand.RandSync.ServerAndClient);
            }
            
            if (waypoints.None())
            {
                DebugConsole.ThrowError("No valid waypoints to spawn sub: " + subName);
                return null;
            }
            Debug.WriteLine("Possible waypoints for positioning subs: " + waypoints.Count);
            Debug.WriteLine("First wp: " + waypoints.First().ID);
            Debug.WriteLine("Last wp: " + waypoints.Last().ID);

            var subDoc = SubmarineInfo.OpenFile(contentFile.Path.Value);
            Rectangle subBorders = Submarine.GetBorders(subDoc.Root);

            // Add some margin so that the sub doesn't block the path entirely. It's still possible that some larger subs can't pass by.
            int padding = 1500;
            Rectangle paddedBorders = new Rectangle(
                subBorders.X - padding,
                subBorders.Y + padding,
                subBorders.Width + padding * 2,
                subBorders.Height + padding * 2);

            var positions = new List<Vector2>();
            var rects = new List<Rectangle>();
            const int maxAttempts = 100;
            int attemptsLeft = maxAttempts;
            bool success = false;
            WayPoint wayPoint = null;
            Vector2 spawnPoint = Vector2.Zero;
            int attempt = 0;
            Loaded.GetAllCells();
            var placement = info.BeaconStationInfo?.Placement ?? PlacementType.Bottom;
            bool isReshuffled = false;
            while (attemptsLeft > 0)
            {
                if (attemptsLeft < maxAttempts)
                {
                    Debug.WriteLine($"Failed to position the sub {subName}. Trying again (attempt: {attempt}/{maxAttempts}).");
                }
                if (!isReshuffled && attemptsLeft < 10)
                {
                    DebugConsole.AddWarning($"Could not find a suitable position for {subName}. Reshuffling the waypoints and trying a few more times.");
                    waypoints.Shuffle(Rand.RandSync.ServerAndClient);
                    isReshuffled = true;
                }
                attemptsLeft--;
                if (TryGetWayPoint(ref wayPoint))
                {
                    attempt++;
                    spawnPoint = wayPoint.WorldPosition;
                    success = TryPositionSub(subBorders, subName, placement, ref spawnPoint);
                    positionHistory.TryAdd($"{info.Name}: {attempt}", positions.ToList());
                    positions.Clear();
                    if (success)
                    {
                        break;
                    }
                }
                else
                {
                    DebugConsole.NewMessage($"Failed to find any spawn point for the sub: {subName} (No valid waypoints left).", Color.Red);
                    break;
                }
            }
            tempSW.Stop();
            if (success)
            {
                Debug.WriteLine($"Sub {subName} successfully positioned to {spawnPoint} in {tempSW.ElapsedMilliseconds} (ms)");
                tempSW.Restart();
                Submarine sub = new Submarine(info);
                if (type == SubmarineType.Wreck)
                {
                    sub.MakeWreck();
                    Wrecks.Add(sub);
                    PositionsOfInterest.Add(new InterestingPosition(spawnPoint.ToPoint(), PositionType.Wreck, submarine: sub));
                    foreach (Hull hull in sub.GetHulls(false))
                    {
                        if (hull.WaterPercentage > 0)
                        {
                            // Don't override the water level set by the sub designer
                            continue;
                        }
                        if (Rand.Value(Rand.RandSync.ServerAndClient) <= Loaded.GenerationParams.WreckHullFloodingChance)
                        {
                            hull.WaterVolume =                                
                                Math.Max(hull.WaterVolume, hull.Volume * Rand.Range(Loaded.GenerationParams.WreckFloodingHullMinWaterPercentage, Loaded.GenerationParams.WreckFloodingHullMaxWaterPercentage, Rand.RandSync.ServerAndClient));
                        }
                    }
                    
                    bool spawnThalamusByChance = Rand.Value(Rand.RandSync.ServerAndClient) <= Loaded.GenerationParams.ThalamusProbability;
                    bool subHasThalamusItems = sub.GetItems(false).Any(i => i.Prefab.HasSubCategory("thalamus"));
                    bool spawnThalamus = thalamusSpawn switch
                    {
                        LevelData.ThalamusSpawn.Disabled => false,
                        LevelData.ThalamusSpawn.Forced => true,
                        LevelData.ThalamusSpawn.Random => spawnThalamusByChance,
                        _ => false
                    };
                    
                    if (spawnThalamus && subHasThalamusItems)
                    {
                        if (!sub.CreateWreckAI())
                        {
                            DebugConsole.NewMessage($"Failed to create wreck AI inside {subName}.", Color.Red);
                            sub.DisableWreckAI();
                        }
                    }
                    else
                    {
                        sub.DisableWreckAI();
                    }
                }
                else if (type == SubmarineType.BeaconStation)
                {
                    // todo: implement spawning of thalamus for beacon stations
                    PositionsOfInterest.Add(new InterestingPosition(spawnPoint.ToPoint(), PositionType.BeaconStation, submarine: sub));

                    sub.ShowSonarMarker = false;
                    sub.DockedTo.ForEach(s => s.ShowSonarMarker = false);
                    sub.PhysicsBody.FarseerBody.BodyType = BodyType.Static;
                    sub.TeamID = CharacterTeamType.None;
                }
                tempSW.Stop();
                Debug.WriteLine($"Sub {sub.Info.Name} loaded in { tempSW.ElapsedMilliseconds} (ms)");
                sub.SetPosition(spawnPoint, forceUndockFromStaticSubmarines: false);
                blockedRects.Add(sub, rects);
                return sub;
            }
            else
            {
                DebugConsole.NewMessage($"Failed to position wreck {subName}. Used {tempSW.ElapsedMilliseconds} (ms).", Color.Red);
                return null;
            }

            bool TryPositionSub(Rectangle subBorders, string subName, PlacementType placement, ref Vector2 spawnPoint)
            {
                positions.Add(spawnPoint);
                bool bottomFound = TryRaycast(subBorders, placement, ref spawnPoint);
                positions.Add(spawnPoint);
                bool leftSideBlocked = IsSideBlocked(subBorders, false);
                bool rightSideBlocked = IsSideBlocked(subBorders, true);
                int step = 5;
                if (rightSideBlocked && !leftSideBlocked)
                {
                    bottomFound = TryMove(subBorders, placement, ref spawnPoint, -step);
                }
                else if (leftSideBlocked && !rightSideBlocked)
                {
                    bottomFound = TryMove(subBorders, placement, ref spawnPoint, step);
                }
                else if (!bottomFound)
                {
                    if (!leftSideBlocked)
                    {
                        bottomFound = TryMove(subBorders, placement, ref spawnPoint, -step);
                    }
                    else if (!rightSideBlocked)
                    {
                        bottomFound = TryMove(subBorders, placement, ref spawnPoint, step);
                    }
                    else
                    {
                        Debug.WriteLine($"({info.Name}) Invalid position {spawnPoint}. Does not touch the ground.");
                        return false;
                    }
                }
                positions.Add(spawnPoint);
                //shrink the bounds a bit to allow the sub to go slightly inside the wall
                //(just enough that it doesn't look like it's floating)
                int shrinkAmount = step + 100;
                Rectangle shrunkenBorders = new Rectangle(
                    subBorders.X + shrinkAmount,
                    subBorders.Y - shrinkAmount,
                    subBorders.Width - shrinkAmount * 2,
                    subBorders.Height - shrinkAmount * 2);
                bool isBlocked = IsBlocked(spawnPoint, shrunkenBorders);
                if (isBlocked)
                {
                    rects.Add(ToolBox.GetWorldBounds(spawnPoint.ToPoint(), subBorders.Size));
                    Debug.WriteLine($"({info.Name}) Invalid position {spawnPoint}. Blocked by level walls.");
                }
                else if (!bottomFound)
                {
                    rects.Add(ToolBox.GetWorldBounds(spawnPoint.ToPoint(), subBorders.Size));
                    Debug.WriteLine($"({info.Name}) Invalid position {spawnPoint}. Does not touch the ground.");
                }
                else
                {
                    var sp = spawnPoint;
                    float wreckMinDist = minDistance * minDistance;
                    float startMinDist = wreckMinDist * 2;
                    if (Vector2.DistanceSquared(sp, startPosition.ToVector2()) < startMinDist)
                    {
                        Debug.WriteLine($"({info.Name}) Invalid position {spawnPoint}. Too close to the start pos.");
                        return false;
                    }
                    if (Vector2.DistanceSquared(sp, endPosition.ToVector2()) < startMinDist)
                    {
                        Debug.WriteLine($"({info.Name}) Invalid position {spawnPoint}. Too close to the end pos.");
                        return false;
                    }
                    if (Wrecks.Any(w => Vector2.DistanceSquared(sp, w.WorldPosition) < wreckMinDist))
                    {
                        Debug.WriteLine($"({info.Name}) Invalid position {spawnPoint}. Too close to other wreck(s).");
                        return false;
                    }
                }
                return !isBlocked && bottomFound;

                bool TryMove(Rectangle subBorders, PlacementType placement, ref Vector2 spawnPoint, float amount)
                {
                    float maxMovement = 5000;
                    float totalAmount = 0;
                    bool foundBottom = TryRaycast(subBorders, placement, ref spawnPoint);
                    //move until the side is no longer blocked
                    while (IsSideBlocked(subBorders, front: amount < 0))
                    {
                        foundBottom = TryRaycast(subBorders, placement, ref spawnPoint);
                        totalAmount += amount;
                        spawnPoint = new Vector2(spawnPoint.X + amount, spawnPoint.Y);
                        if (Math.Abs(totalAmount) > maxMovement)
                        {
                            Debug.WriteLine($"({info.Name}) Moving the sub {subName} failed.");
                            break;
                        }
                    }
                    return foundBottom;
                }
            }

            bool TryGetWayPoint(ref WayPoint wp)
            {
                while (waypoints.Any())
                {
                    WayPoint previousWp = wp;
                    // Get the first waypoint from the randomized collection and the start going further from it, so that we have higher chances to end up close to the original position (matters for mirrored levels where the geometry can be slightly different).
                    WayPoint newWp = previousWp == null ? waypoints.Last() : waypoints.OrderBy(wP => Vector2.DistanceSquared(wP.Position, previousWp.Position)).First();
                    waypoints.Remove(newWp);
                    if (!IsBlocked(newWp.WorldPosition, paddedBorders))
                    {
                        wp = newWp;
                        return true;
                    }
                }
                return false;
            }

            bool TryRaycast(Rectangle subBorders, PlacementType placement, ref Vector2 spawnPoint)
            {
                // Shoot five rays and pick the highest hit point.
                int rayCount = 5;
                var hitPositions = new Vector2[rayCount];
                bool hit = false;
                for (int i = 0; i < rayCount; i++)
                {
                    //cast rays starting from the left side of the sub, offset by 20% to 80% of the sub's width
                    //(ignoring the very back and front of the sub, it's fine if they overlap a bit)
                    float xOffset =
                        subBorders.Width * MathHelper.Lerp(0.2f, 0.8f, i / (float)(rayCount - 1));
                    Vector2 rayStart = new Vector2(
                        spawnPoint.X + subBorders.Location.X + xOffset,
                        spawnPoint.Y);
                    var simPos = ConvertUnits.ToSimUnits(rayStart);
                    var body = Submarine.PickBody(simPos, new Vector2(simPos.X, placement == PlacementType.Bottom ? -1 : Size.Y + 1),
                        customPredicate: f => f.Body == TopBarrier || f.Body == BottomBarrier || (f.Body?.UserData is VoronoiCell cell && cell.Body.BodyType == BodyType.Static && !ExtraWalls.Any(w => w.Body == f.Body)),
                        collisionCategory: Physics.CollisionLevel | Physics.CollisionWall);
                    if (body != null)
                    {
                        hitPositions[i] = ConvertUnits.ToDisplayUnits(Submarine.LastPickedPosition);
                        hit = true;
                    }
                }
                int dir = placement == PlacementType.Bottom ? -1 : 1;
                float highestPoint = placement == PlacementType.Bottom ? hitPositions.Max(p => p.Y) : hitPositions.Min(p => p.Y);
                float halfHeight = subBorders.Height / 2;
                float centerOffset = subBorders.Y - halfHeight;
                spawnPoint = new Vector2(spawnPoint.X, highestPoint + halfHeight * -dir - centerOffset);
                return hit;
            }

            bool IsSideBlocked(Rectangle subBorders, bool front)
            {
                Point centerOffset = new Point(subBorders.Center.X, subBorders.Y - subBorders.Height / 2);

                // Shoot three rays and check whether any of them hits.
                int rayCount = 3;
                int dir = front ? 1 : -1;
                Vector2 halfSize = subBorders.Size.ToVector2() / 2;
                Vector2 quarterSize = halfSize / 2;
                for (int i = 0; i < rayCount; i++)
                {
                    Vector2 rayStart, to;
                    switch (i)
                    {
                        case 1:
                        case 2:
                            float yOffset = quarterSize.Y * (i == 1 ? 1 : -1);
                            //from a position half-way towards the edge, to the edge.
                            //we start half-way towards the edge instead of the center, because we want to allow things to poke partially inside the sub
                            rayStart = new Vector2(spawnPoint.X + quarterSize.X * dir, spawnPoint.Y + yOffset);
                            to = new Vector2(spawnPoint.X + halfSize.X * dir, rayStart.Y);
                            break;
                        case 0:
                        default:
                            //center to center-right
                            rayStart = spawnPoint;
                            to = new Vector2(spawnPoint.X + halfSize.X * dir, rayStart.Y);
                            break;
                    }

                    rayStart += centerOffset.ToVector2();
                    to += centerOffset.ToVector2();

                    Vector2 simPos = ConvertUnits.ToSimUnits(rayStart);
                    if (Submarine.PickBody(simPos, ConvertUnits.ToSimUnits(to),
                        customPredicate: f => f.Body?.UserData is VoronoiCell cell,
                        collisionCategory: Physics.CollisionLevel | Physics.CollisionWall,
                        allowInsideFixture: true) != null)
                    {
                        return true;
                    }
                }
                return false;
            }

            bool IsBlocked(Vector2 pos, Rectangle submarineBounds)
            {
                Rectangle bounds = new Rectangle(
                    (int)pos.X + submarineBounds.X, (int)pos.Y + submarineBounds.Y,
                    submarineBounds.Width, submarineBounds.Height);
                if (Ruins.Any(r => ToolBox.GetWorldBounds(r.Area.Center, r.Area.Size).IntersectsWorld(bounds)))
                {
                    return true;
                }
                if (Caves.Any(c => 
                        ToolBox.GetWorldBounds(c.Area.Center, c.Area.Size).IntersectsWorld(bounds) || 
                        ToolBox.GetWorldBounds(c.StartPos, new Point(1500)).IntersectsWorld(bounds)))
                {
                    return true;
                }
                return cells.Any(c => 
                    c.Body != null && 
                    c.BodyVertices.Any(v => bounds.ContainsWorld(v)));
            }
        }

        // For debugging
        private readonly Dictionary<string, List<Vector2>> positionHistory = new Dictionary<string, List<Vector2>>();
        private readonly Dictionary<Submarine, List<Rectangle>> blockedRects = new Dictionary<Submarine, List<Rectangle>>();

        private readonly record struct PlaceableWreck(WreckFile WreckFile, WreckInfo WreckInfo)
        {
            public static Option<PlaceableWreck> TryCreate(WreckFile wreckFile)
            {
                var matchingSub = SubmarineInfo.SavedSubmarines.FirstOrDefault(i => i.FilePath == wreckFile.Path.Value);
                if (matchingSub?.WreckInfo is null)
                {
                    DebugConsole.ThrowError($"No matching submarine info found for the wreck file {wreckFile.Path.Value}");
                    return Option.None;
                }

                return Option.Some(new PlaceableWreck(wreckFile, matchingSub.WreckInfo));
            }
        }

        private void CreateWrecks()
        {
            var totalSW = new Stopwatch();
            totalSW.Start();
            
            // Reset the seed to prevent the level geometry changes affecting the outcome (-> should get the same wrecks with the same seed, regardless of the other parameters).
            ResetRandomSeed();
            
            if (LevelData.ConsoleForceWreck != null)
            {
                LevelData.ForceWreck = LevelData.ConsoleForceWreck;
            }
            
            if (GameMain.NetworkMember is { } networkMember && GameMain.GameSession?.GameMode is PvPMode && !networkMember.ServerSettings.PvPSpawnWrecks)
            {
                if (GameSettings.CurrentConfig.VerboseLogging)
                {
                    DebugConsole.NewMessage("PvP setting: Skipping wreck generation", Color.Yellow);
                }
                Wrecks = new List<Submarine>();
                return;
            }

            var placeableWrecks = ContentPackageManager.EnabledPackages.All
                .SelectMany(p => p.GetFiles<WreckFile>())
                .OrderBy(f => f.UintIdentifier)
                .Select(PlaceableWreck.TryCreate)
                .Where(w => w.IsSome())
                .Select(o => o.TryUnwrap(out var w) ? w : throw new InvalidOperationException())
                .ToList();
            
            if (LevelData.ForceWreck != null)
            {
                DebugConsole.NewMessage($"Level Generation - Forcing wreck {LevelData.ForceWreck.DisplayName}");
            }
            else
            {
                for (int i = placeableWrecks.Count - 1; i >= 0; i--)
                {
                    var wreckInfo = placeableWrecks[i].WreckInfo;
                    if (!IsAllowedDifficulty(wreckInfo.MinLevelDifficulty, wreckInfo.MaxLevelDifficulty))
                    {
                        placeableWrecks.RemoveAt(i);
                    }
                }
                if (placeableWrecks.None())
                {
                    DebugConsole.ThrowError($"No wreck files found for the level difficulty {LevelData.Difficulty}!");
                    Wrecks = new List<Submarine>();
                    return;
                }
                
                placeableWrecks.Shuffle(Rand.RandSync.ServerAndClient);
            }

            int minWreckCount = Math.Min(Loaded.GenerationParams.MinWreckCount, placeableWrecks.Count);
            int maxWreckCount = Math.Min(Loaded.GenerationParams.MaxWreckCount, placeableWrecks.Count);
            int wreckCount = Rand.Range(minWreckCount, maxWreckCount + 1, Rand.RandSync.ServerAndClient);
            bool requireThalamus = false;

            if (GameMain.GameSession?.GameMode?.Missions.Any(m => m.Prefab.RequireWreck) ?? false)
            {
                wreckCount = Math.Max(wreckCount, 1);
            }
            
            if (GameMain.GameSession?.GameMode?.Missions.Any(static m => m.Prefab.RequireThalamusWreck) ?? false)
            {
                requireThalamus = true;
            }
            
            if (LevelData.ForceWreck != null)
            {
                //force the desired wreck to be chosen first
                PlaceableWreck matchingFile =  placeableWrecks.FirstOrDefault(wreck => wreck.WreckFile.Path == LevelData.ForceWreck.FilePath);
                if (matchingFile.WreckFile != null)
                {
                    placeableWrecks.Remove(matchingFile);
                    placeableWrecks.Insert(0, matchingFile);
                    
                    if (LevelData.ForceThalamus == LevelData.ThalamusSpawn.Forced && matchingFile.WreckInfo.WreckContainsThalamus == WreckInfo.HasThalamus.No)
                    {
                        DebugConsole.ThrowError($"Forced wreck {LevelData.ForceWreck.DisplayName} can't have thalamus!");
                    }
                }
                wreckCount = Math.Max(wreckCount, 1);
            }
            else if (requireThalamus)
            {
                var thalamusWrecks = placeableWrecks
                    .Where(static w => w.WreckInfo.WreckContainsThalamus == WreckInfo.HasThalamus.Yes)
                    .ToList();

                if (thalamusWrecks.Any())
                {
                    thalamusWrecks.Shuffle(Rand.RandSync.ServerAndClient);

                    foreach (var wreck in thalamusWrecks)
                    {
                        placeableWrecks.Remove(wreck);
                        placeableWrecks.Insert(0, wreck);
                    }
                }
            }

            Wrecks = new List<Submarine>(wreckCount);
            for (int i = 0; i < wreckCount; i++)
            {
                //how many times we'll try placing another sub before giving up
                const int MaxSubsToTry = 2;
                int attempts = 0;
                while (placeableWrecks.Any() && attempts < MaxSubsToTry)
                {
                    var placeableWreck = placeableWrecks.First();
                    var wreckFile = placeableWreck.WreckFile;
                    if (LevelData.ForceWreck == null)
                    {
                        // If a wreck is forced, don't remove it -> only spawns those wrecks (makes testing them in the editor easier).
                        // Normally we don't want two instances of the same wreck to spawn in the same level, but when we test or debug certain wrecks, we want only them.
                        placeableWrecks.RemoveAt(0);   
                    }
                    LevelData.ThalamusSpawn thalamusSpawn = requireThalamus ? LevelData.ThalamusSpawn.Forced : LevelData.ThalamusSpawn.Random;
                    if (LevelData.ForceWreck != null) { thalamusSpawn = LevelData.ForceThalamus; }

                    // disable thalamus spawning in pvp if monster spawning is disabled
                    if (GameMain.NetworkMember is { } netMember && 
                        GameMain.GameSession?.GameMode is PvPMode && 
                        !netMember.ServerSettings.PvPSpawnMonsters)
                    {
                        thalamusSpawn = LevelData.ThalamusSpawn.Disabled;
                    }
                    if (wreckFile == null) { continue; }
                    string wreckName = System.IO.Path.GetFileNameWithoutExtension(wreckFile.Path.Value);
                    if (SpawnSubOnPath(wreckName, wreckFile, SubmarineType.Wreck, thalamusSpawn: thalamusSpawn) is Submarine wreck)
                    {
                        //placed successfully
                        
                        // Disabled at least for now, because labels etc. can cause issues.
                        // if (Mirrored)
                        // {
                        //     wreck.FlipX();
                        // }
                        break;
                    }
                    attempts++;
                }
            }
            totalSW.Stop();
            Debug.WriteLine($"{Wrecks.Count} wrecks created in { totalSW.ElapsedMilliseconds} (ms)");
        }

        private bool HasStartOutpost()
        {
            if (preSelectedStartOutpost != null) { return true; }
            if (LevelData.Type != LevelData.LevelType.Outpost)
            {
                //only create a starting outpost in campaign and tutorial modes
#if CLIENT
                if (Screen.Selected != GameMain.LevelEditorScreen && !IsModeStartOutpostCompatible())
                {
                    return false;
                }
#else
                if (!IsModeStartOutpostCompatible())
                {
                    return false;
                }
#endif
            }
            if (StartLocation != null && !StartLocation.Type.HasOutpost)
            {
                return false;
            }
            return true;
        }

        private bool HasEndOutpost()
        {
            if (preSelectedEndOutpost != null) { return true; }
            //don't create an end outpost for locations
            if (LevelData.Type == LevelData.LevelType.Outpost) { return false; }
            if (EndLocation != null && !EndLocation.Type.HasOutpost) { return false; }
            return true;
        }

        private void CreateOutposts()
        {
            // Reset the seed so the outposts keep the same on mirrored levels.
            ResetRandomSeed();
            
            var outpostFiles = ContentPackageManager.EnabledPackages.All
                .SelectMany(p => p.GetFiles<OutpostFile>())
                .OrderBy(f => f.UintIdentifier).ToList();
            if (!outpostFiles.Any() && !OutpostGenerationParams.OutpostParams.Any() && LevelData.ForceOutpostGenerationParams == null)
            {
                DebugConsole.ThrowError("No outpost files found in the selected content packages");
                return;
            }

            // This breaks the determinism between mirrored and non-mirrored levels, because the random calls will come in different order.
            // TODO: Should refactor the code so that the order keeps the same regardless of whether the level is mirrored or not (start outpost needs to be generated first, then the end outpost)
            for (int i = 0; i < 2; i++)
            {
                bool isStart = (i == 0) == !Mirrored;
                if (isStart)
                {
                    if (!HasStartOutpost()) { continue; }
                }
                else
                {
                    if (!HasEndOutpost()) { continue; }
                }

                SubmarineInfo outpostInfo;
                Submarine outpost = null;

                SubmarineInfo preSelectedOutpost = isStart ? preSelectedStartOutpost : preSelectedEndOutpost;
                if (preSelectedOutpost == null)
                {
                    if (LevelData.OutpostGenerationParamsExist)
                    {
                        Location location = isStart ? StartLocation : EndLocation;
                        OutpostGenerationParams outpostGenerationParams = null;
                        Identifier missionForcedOutpostParamsId = Identifier.Empty;
                        if (GameMain.GameSession?.GameMode?.Missions is IEnumerable<Mission> missions)
                        {
                            foreach (var mission in missions)
                            {
                                if (!mission.Prefab.ForceOutpostGenerationParameters.IsEmpty)
                                {
                                    missionForcedOutpostParamsId = mission.Prefab.ForceOutpostGenerationParameters;
                                    break;
                                }
                            }
                        }
                        if (missionForcedOutpostParamsId != null && 
                            OutpostGenerationParams.OutpostParams.TryGet(missionForcedOutpostParamsId, out var missionForcedOutpostParams))
                        {
                            outpostGenerationParams = missionForcedOutpostParams;
                        }
                        else if (LevelData.ForceOutpostGenerationParams != null)
                        {
                            outpostGenerationParams = LevelData.ForceOutpostGenerationParams;
                        }
                        else
                        {
                            outpostGenerationParams =
                                LevelData.ForceOutpostGenerationParams ??
                                LevelData.GetSuitableOutpostGenerationParams(location, LevelData).GetRandom(Rand.RandSync.ServerAndClient);
                        }

                        LocationType locationType = location?.Type;
                        if (locationType == null)
                        {
                            locationType = LocationType.Prefabs.GetRandom(Rand.RandSync.ServerAndClient);
                            if (outpostGenerationParams.AllowedLocationTypes.Any())
                            {
                                locationType = LocationType.Prefabs.GetRandom(lt => 
                                    outpostGenerationParams.AllowedLocationTypes.Any(allowedType =>
                                        allowedType == "any" || lt.Identifier == allowedType), Rand.RandSync.ServerAndClient);
                            }
                        }

                        if (location != null)
                        {
                            DebugConsole.NewMessage($"Generating an outpost for the {(isStart ? "start" : "end")} of the level... (Location: {location.DisplayName}, level type: {LevelData.Type})");
                            outpost = OutpostGenerator.Generate(outpostGenerationParams, location, onlyEntrance: LevelData.Type != LevelData.LevelType.Outpost, LevelData.AllowInvalidOutpost);
                        }
                        else
                        {
                            DebugConsole.NewMessage($"Generating an outpost for the {(isStart ? "start" : "end")} of the level... (Location type: {locationType}, level type: {LevelData.Type})");
                            outpost = OutpostGenerator.Generate(outpostGenerationParams, locationType, onlyEntrance: LevelData.Type != LevelData.LevelType.Outpost, LevelData.AllowInvalidOutpost);
                        }

                        foreach (string categoryToHide in locationType.HideEntitySubcategories)
                        {
                            foreach (MapEntity entityToHide in MapEntity.MapEntityList.Where(me => me.Submarine == outpost && (me.Prefab?.HasSubCategory(categoryToHide) ?? false)))
                            {
                                entityToHide.IsLayerHidden = true;
                            }                                
                        }
                    }
                    else
                    {
                        DebugConsole.NewMessage($"Loading a pre-built outpost for the {(isStart ? "start" : "end")} of the level...");
                        //backwards compatibility: if there are no generation params available, try to load an outpost file saved as a sub
                        ContentFile outpostFile = outpostFiles.GetRandom(Rand.RandSync.ServerAndClient);
                        outpostInfo = new SubmarineInfo(outpostFile.Path.Value)
                        {
                            Type = SubmarineType.Outpost
                        };
                        outpost = new Submarine(outpostInfo);
                    }
                }
                else
                {
                    DebugConsole.NewMessage($"Loading a pre-selected outpost for the {(isStart ? "start" : "end")} of the level...");
                    outpostInfo = preSelectedOutpost;
                    outpostInfo.Type = SubmarineType.Outpost;
                    outpost = new Submarine(outpostInfo);
                }
                // Outposts tend to have labels, flags, and other texts that can't be flipped.
                // if (Mirrored)
                // {
                //     outpost.FlipX();
                // }

                Point? minSize = null;
                DockingPort subPort = null;
                float closestDistance = float.MaxValue;
                if (Submarine.MainSub != null)
                {
                    Point subSize = Submarine.MainSub.GetDockedBorders().Size;
                    Point outpostSize = outpost.GetDockedBorders().Size;
                    minSize = new Point(Math.Max(subSize.X, outpostSize.X), subSize.Y + outpostSize.Y);

                    foreach (DockingPort port in DockingPort.List)
                    {
                        if (port.IsHorizontal || port.Docked) { continue; }
                        if (port.Item.Submarine != Submarine.MainSub) { continue; }
                        //the submarine port has to be at the top of the sub
                        if (port.Item.WorldPosition.Y < Submarine.MainSub.WorldPosition.Y) { continue; }
                        float dist = Math.Abs(port.Item.WorldPosition.X - Submarine.MainSub.WorldPosition.X);
                        if (dist < closestDistance || subPort.MainDockingPort)
                        {
                            subPort = port;
                            closestDistance = dist;
                        }
                    }
                }

                Vector2 spawnPos;
                if (GenerationParams.ForceOutpostPosition != Vector2.Zero)
                {
                    spawnPos = new Vector2(Size.X * GenerationParams.ForceOutpostPosition.X, Size.Y * GenerationParams.ForceOutpostPosition.Y);
                }
                else
                {
                    DockingPort outpostPort = null;
                    closestDistance = float.MaxValue;
                    foreach (DockingPort port in DockingPort.List)
                    {
                        if (port.IsHorizontal || port.Docked) { continue; }
                        if (port.Item.Submarine != outpost) { continue; }
                        //the outpost port has to be at the bottom of the outpost
                        if (port.Item.WorldPosition.Y > outpost.WorldPosition.Y) { continue; }
                        float dist = Math.Abs(port.Item.WorldPosition.X - outpost.WorldPosition.X);
                        if (dist < closestDistance)
                        {
                            outpostPort = port;
                            closestDistance = dist;
                        }
                    }

                    float subDockingPortOffset = subPort == null ? 0.0f : subPort.Item.WorldPosition.X - Submarine.MainSub.WorldPosition.X;
                    //don't try to compensate if the port is very far from the sub's center of mass
                    if (Math.Abs(subDockingPortOffset) > 5000.0f)
                    {
                        subDockingPortOffset = MathHelper.Clamp(subDockingPortOffset, -5000.0f, 5000.0f);
                        string warningMsg = "Docking port very far from the sub's center of mass (submarine: " + Submarine.MainSub.Info.Name + ", dist: " + subDockingPortOffset + "). The level generator may not be able to place the outpost so that docking is possible.";
                        DebugConsole.NewMessage(warningMsg, Color.Orange);
                        GameAnalyticsManager.AddErrorEventOnce("Lever.CreateOutposts:DockingPortVeryFar" + Submarine.MainSub.Info.Name, GameAnalyticsManager.ErrorSeverity.Warning, warningMsg);
                    }

                    float? outpostDockingPortOffset = null;
                    if (outpostPort != null)
                    {
                        outpostDockingPortOffset = subPort == null ? 0.0f : outpostPort.Item.WorldPosition.X - outpost.WorldPosition.X;
                        //don't try to compensate if the port is very far from the outpost's center of mass
                        if (Math.Abs(outpostDockingPortOffset.Value) > 5000.0f)
                        {
                            outpostDockingPortOffset = MathHelper.Clamp(outpostDockingPortOffset.Value, -5000.0f, 5000.0f);
                            string warningMsg = "Docking port very far from the outpost's center of mass (outpost: " + outpost.Info.Name + ", dist: " + outpostDockingPortOffset + "). The level generator may not be able to place the outpost so that docking is possible.";
                            DebugConsole.NewMessage(warningMsg, Color.Orange);
                            GameAnalyticsManager.AddErrorEventOnce("Lever.CreateOutposts:OutpostDockingPortVeryFar" + outpost.Info.Name, GameAnalyticsManager.ErrorSeverity.Warning, warningMsg);
                        }
                    }

                    Vector2 preferredSpawnPos = i == 0 ? StartPosition : EndPosition;
                    //if we're placing the outpost at the end of the level, close to the bottom-right,
                    //and there's a hole leading out the right side of the level, move the spawn position towards that hole.
                    //Makes outpost placement a little nicer in levels with lots of verticality: if there's a tall vertical
                    //shaft leading down to the end position, we don't want the outpost to be placed all the way up to wherever the
                    //ceiling is at the top of that shaft.
                    if (i == 1 && GenerationParams.CreateHoleNextToEnd && 
                        preferredSpawnPos.X > Size.X * 0.75f && 
                        preferredSpawnPos.Y < Size.Y * 0.25f)
                    {
                        preferredSpawnPos.X = (preferredSpawnPos.X + Size.X) / 2;
                    }

                    spawnPos = outpost.FindSpawnPos(preferredSpawnPos, minSize, outpostDockingPortOffset != null ? subDockingPortOffset - outpostDockingPortOffset.Value : 0.0f, verticalMoveDir: 1);
                    if (Type == LevelData.LevelType.Outpost)
                    {
                        spawnPos.Y = Math.Min(Size.Y - outpost.Borders.Height * 0.6f, spawnPos.Y + outpost.Borders.Height / 2);
                    }
                }

                outpost.SetPosition(spawnPos, forceUndockFromStaticSubmarines: false);
                SetLinkedSubCrushDepth(outpost);

                foreach (WayPoint wp in WayPoint.WayPointList)
                {
                    if (wp.Submarine == outpost && wp.SpawnType != SpawnType.Path)
                    {
                        PositionsOfInterest.Add(new InterestingPosition(wp.WorldPosition.ToPoint(), PositionType.Outpost, outpost));                        
                    }
                }

                if ((i == 0) == !Mirrored)
                {
                    StartOutpost = outpost;
                    if (StartLocation != null) 
                    {
                        outpost.TeamID = StartLocation.Type.OutpostTeam;
                        outpost.Info.Name = StartLocation.DisplayName.Value;
                    }
                }
                else
                {
                    EndOutpost = outpost;
                    if (EndLocation != null)
                    {
                        outpost.TeamID = EndLocation.Type.OutpostTeam; 
                        outpost.Info.Name = EndLocation.DisplayName.Value; 
                    }
                }
            }
        }

        private void CreateBeaconStation()
        {
            // Reset the seed to prevent the wreck generation affecting the outcome (-> should get the same beacons with the same seed, regardless of the other parameters).
            ResetRandomSeed();
            
            if (LevelData.ConsoleForceBeaconStation != null)
            {
                LevelData.ForceBeaconStation = LevelData.ConsoleForceBeaconStation;
            }

            bool missionRequiresBeaconStation = GameMain.GameSession?.GameMode?.Missions.Any(m => m.Prefab.RequireBeaconStation) ?? false;
            if (!missionRequiresBeaconStation &&!LevelData.HasBeaconStation && LevelData.ForceBeaconStation == null && string.IsNullOrEmpty(GenerationParams.ForceBeaconStation)) 
            { 
                return; 
            }

            bool spawnInMiddle = GameMain.GameSession?.GameMode?.Missions.Any(m => m.Prefab.RequireBeaconStation && m.Prefab.SpawnBeaconStationInMiddle) ?? false;

            var beaconStationFiles = ContentPackageManager.EnabledPackages.All
                .SelectMany(p => p.GetFiles<BeaconStationFile>())
                .OrderBy(f => f.UintIdentifier).ToList();
            if (beaconStationFiles.None())
            {
                DebugConsole.ThrowError("No BeaconStation files found in the selected content packages!");
                return;
            }

            var beaconInfos = SubmarineInfo.SavedSubmarines.Where(i => i.IsBeacon);
            ContentFile contentFile = null;
            if (!string.IsNullOrEmpty(GenerationParams.ForceBeaconStation))
            {
                var contentPath = ContentPath.FromRaw(GenerationParams.ContentPackage, GenerationParams.ForceBeaconStation);
                contentFile = beaconStationFiles.OrderBy(b => b.UintIdentifier).FirstOrDefault(f => f.Path == contentPath);
                if (contentFile == null)
                {
                    DebugConsole.ThrowError($"Failed to find the beacon station \"{GenerationParams.ForceBeaconStation}\". Using a random one instead...");
                }
            }
            else if (LevelData.ForceBeaconStation != null)
            {
                contentFile = beaconStationFiles.FirstOrDefault(b => b.Path == LevelData.ForceBeaconStation.FilePath);
            }

            if (contentFile == null)
            {
                for (int i = beaconStationFiles.Count - 1; i >= 0; i--)
                {
                    var beaconStationFile = beaconStationFiles[i];
                    var matchingInfo = beaconInfos.SingleOrDefault(info => info.FilePath == beaconStationFile.Path.Value);
                    Debug.Assert(matchingInfo != null);
                    if (matchingInfo?.BeaconStationInfo is BeaconStationInfo beaconInfo)
                    {
                        if (LevelData.Difficulty < beaconInfo.MinLevelDifficulty || LevelData.Difficulty > beaconInfo.MaxLevelDifficulty)
                        {
                            beaconStationFiles.RemoveAt(i);
                        }
                    }
                }
                if (beaconStationFiles.None())
                {
                    DebugConsole.ThrowError($"No BeaconStation files found for the level difficulty {LevelData.Difficulty}!");
                    return;
                }
                contentFile = beaconStationFiles.GetRandom(Rand.RandSync.ServerAndClient);
            }

            string beaconStationName = System.IO.Path.GetFileNameWithoutExtension(contentFile.Path.Value);
            BeaconStation = SpawnSubOnPath(beaconStationName, contentFile, SubmarineType.BeaconStation, spawnInTheMiddle: spawnInMiddle);
            if (BeaconStation == null) 
            {
                LevelData.HasBeaconStation = false;
                return; 
            }
            // Disabled at least for now, because labels etc. can cause issues.
            // if (Mirrored)
            // {
            //     BeaconStation.FlipX();
            // }

            Item sonarItem = Item.ItemList.Find(it => it.Submarine == BeaconStation && it.GetComponent<Sonar>() != null);
            if (sonarItem == null)
            {
                DebugConsole.ThrowError($"No sonar found in the beacon station \"{beaconStationName}\"!");
                return;
            }
            beaconSonar = sonarItem.GetComponent<Sonar>();
        }

        public void PrepareBeaconStation()
        {
            if (!LevelData.HasBeaconStation) { return; }
            if (GameMain.NetworkMember?.IsClient ?? false) { return; }

            if (BeaconStation == null)
            {
                throw new InvalidOperationException("Failed to prepare beacon station (no beacon station in the level).");
            }

            List<Item> beaconItems = Item.ItemList.FindAll(it => it.Submarine == BeaconStation);

            Item reactorItem = beaconItems.Find(it => it.GetComponent<Reactor>() != null);
            Reactor reactorComponent = null;
            ItemContainer reactorContainer = null;
            if (reactorItem != null)
            {
                reactorComponent = reactorItem.GetComponent<Reactor>();
                reactorComponent.FuelConsumptionRate = 0.0f;
                reactorContainer = reactorItem.GetComponent<ItemContainer>();
                Repairable repairable = reactorItem.GetComponent<Repairable>();
                if (repairable != null)
                {
                    repairable.DeteriorationSpeed = 0.0f;
                }
            }
            if (LevelData.IsBeaconActive)
            {
                if (reactorContainer != null && reactorContainer.Inventory.IsEmpty() && 
                    reactorContainer.ContainableItemIdentifiers.Any() && ItemPrefab.Prefabs.ContainsKey(reactorContainer.ContainableItemIdentifiers.FirstOrDefault()))
                {
                    ItemPrefab fuelPrefab = ItemPrefab.Prefabs[reactorContainer.ContainableItemIdentifiers.FirstOrDefault()];
                    Spawner.AddItemToSpawnQueue(
                        fuelPrefab, reactorContainer.Inventory,
                        onSpawned: (it) => reactorComponent.PowerUpImmediately());
                }
                if (beaconSonar == null)
                {
                    DebugConsole.AddWarning($"Beacon station \"{BeaconStation.Info.Name}\" has no sonar. Beacon missions might not work correctly with this beacon station.");
                }
                else
                {
                    beaconSonar.CurrentMode = Sonar.Mode.Active;
#if SERVER
                    beaconSonar.Item.CreateServerEvent(beaconSonar);
#endif
                }
            }
            SetLinkedSubCrushDepth(BeaconStation);
        }

        public void DisconnectBeaconStationWires(float disconnectWireProbability)
        {
            if (BeaconStation?.Info?.BeaconStationInfo is { AllowDisconnectedWires: false }) { return; }

            if (disconnectWireProbability <= 0.0f) { return; }
            List<Item> beaconItems = Item.ItemList.FindAll(it => it.Submarine == BeaconStation);
            foreach (Item item in beaconItems.Where(it => it.GetComponent<Wire>() != null).ToList())
            {
                if (item.NonInteractable || item.InvulnerableToDamage) { continue; }
                Wire wire = item.GetComponent<Wire>();
                if (wire.Locked) { continue; }
                if (wire.Connections[0] != null && (wire.Connections[0].Item.NonInteractable || wire.Connections[0].Item.GetComponent<ConnectionPanel>().Locked))
                {
                    continue;
                }
                if (wire.Connections[1] != null && (wire.Connections[1].Item.NonInteractable || wire.Connections[1].Item.GetComponent<ConnectionPanel>().Locked))
                {
                    continue;
                }
                if (Rand.Range(0f, 1.0f, Rand.RandSync.Unsynced) < disconnectWireProbability)
                {
                    foreach (Connection connection in wire.Connections)
                    {
                        if (connection != null)
                        {
                            connection.ConnectionPanel.DisconnectedWires.Add(wire);
                            wire.RemoveConnection(connection.Item);
#if SERVER
                            connection.ConnectionPanel.Item.CreateServerEvent(connection.ConnectionPanel);
                            wire.CreateNetworkEvent();
#endif
                        }
                    }
                }
            }
        }

        public void DamageBeaconStationDevices(float breakDeviceProbability)
        {
            if (BeaconStation?.Info?.BeaconStationInfo is { AllowDamagedDevices: false }) { return; }

            if (breakDeviceProbability <= 0.0f) { return; }
            //break powered items
            List<Item> beaconItems = Item.ItemList.FindAll(it => it.Submarine == BeaconStation);
            foreach (Item item in beaconItems.Where(it => it.Components.Any(c => c is Powered) && it.Components.Any(c => c is Repairable)))
            {
                if (item.NonInteractable || item.InvulnerableToDamage) { continue; }
                if (Rand.Range(0f, 1f, Rand.RandSync.Unsynced) < breakDeviceProbability)
                {
                    item.Condition *= Rand.Range(0.6f, 0.8f, Rand.RandSync.Unsynced);
                }
            }
        }

        public void DamageBeaconStationWalls(float damageWallProbability)
        {
            if (BeaconStation?.Info?.BeaconStationInfo is { AllowDamagedWalls: false }) { return; }

            if (damageWallProbability <= 0.0f) { return; }
            //poke holes in the walls
            foreach (Structure structure in Structure.WallList.Where(s => s.Submarine == BeaconStation))
            {
                if (Rand.Range(0f, 1f, Rand.RandSync.Unsynced) < damageWallProbability)
                {
                    int sectionIndex = Rand.Range(0, structure.SectionCount - 1, Rand.RandSync.Unsynced);
                    structure.AddDamage(sectionIndex, Rand.Range(structure.MaxHealth * 0.2f, structure.MaxHealth, Rand.RandSync.Unsynced));
                }
            }
        }

        public bool CheckBeaconActive()
        {
            if (beaconSonar == null) { return false; }
            return beaconSonar.HasPower && beaconSonar.CurrentMode == Sonar.Mode.Active;
        }

        private void SetLinkedSubCrushDepth(Submarine parentSub)
        {
            foreach (var connectedSub in parentSub.GetConnectedSubs())
            {
                connectedSub.SetCrushDepth(Math.Max(connectedSub.RealWorldCrushDepth, GetRealWorldDepth(0) + 1000));
            }
        }

        private static bool IsModeStartOutpostCompatible()
        {
#if CLIENT
            return GameMain.GameSession?.GameMode is CampaignMode || GameMain.GameSession?.GameMode is TutorialMode || GameMain.GameSession?.GameMode is TestGameMode;
#else
            return GameMain.GameSession?.GameMode is CampaignMode;
#endif
        }

        public void SpawnCorpses()
        {
            if (GameMain.NetworkMember != null && GameMain.NetworkMember.IsClient) { return; }

            foreach (Submarine wreck in Wrecks)
            {
                int corpseCount = Rand.Range(Loaded.GenerationParams.MinCorpseCount, Loaded.GenerationParams.MaxCorpseCount + 1);
                var allSpawnPoints = WayPoint.WayPointList.FindAll(wp => wp.Submarine == wreck && wp.CurrentHull != null);
                var pathPoints = allSpawnPoints.FindAll(wp => wp.SpawnType == SpawnType.Path);
                var corpsePoints = allSpawnPoints.FindAll(wp => wp.SpawnType == SpawnType.Corpse);
                if (!corpsePoints.Any() && !pathPoints.Any()) { continue; }
                pathPoints.Shuffle(Rand.RandSync.ServerAndClient);
                // Sort by job so that we first spawn those with a predefined job (might have special id cards)
                corpsePoints = corpsePoints.OrderBy(p => p.AssignedJob == null).ThenBy(p => Rand.Value()).ToList();
                var usedJobs = new HashSet<JobPrefab>();
                int spawnCounter = 0;
                for (int j = 0; j < corpseCount; j++)
                {
                    WayPoint sp = corpsePoints.FirstOrDefault() ?? pathPoints.FirstOrDefault();
                    JobPrefab job = sp?.AssignedJob;
                    CorpsePrefab selectedPrefab;
                    if (job == null)
                    {
                        selectedPrefab = GetCorpsePrefab(usedJobs);
                    }
                    else
                    {
                        selectedPrefab = GetCorpsePrefab(usedJobs, p => p.Job == "any" || p.Job == job.Identifier);
                        if (selectedPrefab == null)
                        {
                            corpsePoints.Remove(sp);
                            pathPoints.Remove(sp);
                            sp = corpsePoints.FirstOrDefault(sp => sp.AssignedJob == null) ?? pathPoints.FirstOrDefault(sp => sp.AssignedJob == null);
                            // Deduce the job from the selected prefab
                            selectedPrefab = GetCorpsePrefab(usedJobs);
                            if (selectedPrefab != null)
                            {
                                job = selectedPrefab.GetJobPrefab();
                            }
                        }
                    }
                    if (selectedPrefab == null) { continue; }
                    Vector2 worldPos;
                    if (sp == null)
                    {
                        if (!TryGetExtraSpawnPoint(out worldPos))
                        {
                            break;
                        }
                    }
                    else
                    {
                        worldPos = sp.WorldPosition;
                        corpsePoints.Remove(sp);
                        pathPoints.Remove(sp);
                    }

                    job ??= selectedPrefab.GetJobPrefab(predicate: p => !usedJobs.Contains(p));
                    if (job == null) { continue; }
                    if (job.Identifier == "captain" || job.Identifier == "engineer" || job.Identifier == "medicaldoctor" || job.Identifier == "securityofficer")
                    {
                        // Only spawn one of these jobs per wreck
                        usedJobs.Add(job);
                    }
                    var characterInfo = new CharacterInfo(CharacterPrefab.HumanSpeciesName, jobOrJobPrefab: job);
                    var corpse = Character.Create(CharacterPrefab.HumanSpeciesName, worldPos, ToolBox.RandomSeed(8), characterInfo, hasAi: true, createNetworkEvent: true);
                    corpse.AnimController.FindHull(worldPos, setSubmarine: true);
                    corpse.TeamID = CharacterTeamType.None;
                    corpse.EnableDespawn = false;
                    selectedPrefab.GiveItems(corpse, wreck, sp);
                    bool spawnAsHusk = Rand.Value() <= Loaded.GenerationParams.HuskProbability;
                    if(Rand.Value() <= 0.5)
                    {
                        corpse.TryFlipCharacter(); // Random chance to spawn flipped
                    }
                    if (spawnAsHusk)
                    {
                        corpse.TurnIntoHusk(playDead: true);
                    }
                    else
                    {
                        corpse.Kill(CauseOfDeathType.Unknown, causeOfDeathAffliction: null, log: false);
                        corpse.CharacterHealth.ApplyAffliction(corpse.AnimController.MainLimb, AfflictionPrefab.OxygenLow.Instantiate(AfflictionPrefab.OxygenLow.MaxStrength));
                        bool applyBurns = Rand.Value() < 0.1f;
                        bool applyDamage = Rand.Value() < 0.3f;
                        foreach (var limb in corpse.AnimController.Limbs)
                        {
                            if (applyDamage && (limb.type == LimbType.Head || Rand.Value() < 0.5f))
                            {
                                var prefab = AfflictionPrefab.BiteWounds;
                                float max = prefab.MaxStrength / prefab.DamageOverlayAlpha;
                                corpse.CharacterHealth.ApplyAffliction(limb, prefab.Instantiate(GetStrength(limb, max)));
                            }
                            if (applyBurns)
                            {
                                var prefab = AfflictionPrefab.Burn;
                                float max = prefab.MaxStrength / prefab.BurnOverlayAlpha;
                                corpse.CharacterHealth.ApplyAffliction(limb, prefab.Instantiate(GetStrength(limb, max)));
                            }

                            static float GetStrength(Limb limb, float max)
                            {
                                float strength = Rand.Range(0, max);
                                if (limb.type != LimbType.Head)
                                {
                                    strength = Math.Min(strength, Rand.Range(0, max));
                                }
                                return strength;
                            }
                        }
                        corpse.CharacterHealth.ForceUpdateVisuals();
                    }
                    if (selectedPrefab.MinMoney >= 0 && selectedPrefab.MaxMoney > 0)
                    {
                        corpse.Wallet.Give(Rand.Range(selectedPrefab.MinMoney, selectedPrefab.MaxMoney));
                    }
                    spawnCounter++;

                    static CorpsePrefab GetCorpsePrefab(HashSet<JobPrefab> usedJobs, Func<CorpsePrefab, bool> predicate = null)
                    {
                        IEnumerable<CorpsePrefab> filteredPrefabs = CorpsePrefab.Prefabs.Where(p =>
                            usedJobs.None(j => j.Identifier == p.Job.ToIdentifier()) &&
                            p.SpawnPosition == PositionType.Wreck &&
                            (predicate == null || predicate(p)));

                        return ToolBox.SelectWeightedRandom(filteredPrefabs.ToList(), filteredPrefabs.Select(p => p.Commonness).ToList(), Rand.RandSync.Unsynced);
                    }
                }
#if DEBUG
                DebugConsole.NewMessage($"{spawnCounter}/{corpseCount} corpses spawned in {wreck.Info.Name}.", spawnCounter == corpseCount ? Color.Green : Color.Yellow);
#endif
                bool TryGetExtraSpawnPoint(out Vector2 point)
                {
                    point = Vector2.Zero;
                    var hull = Hull.HullList.FindAll(h => h.Submarine == wreck).GetRandomUnsynced();
                    if (hull != null)
                    {
                        point = hull.WorldPosition;
                    }
                    return hull != null;
                }
            }
        }

        public void SpawnNPCs()
        {
            if (Type != LevelData.LevelType.Outpost) { return; }
            foreach (Submarine sub in Submarine.Loaded)
            {
                if (sub?.Info?.OutpostGenerationParams != null)
                {
                    OutpostGenerator.SpawnNPCs(StartLocation, sub);
                }
            }
        }

        /// <summary>
        /// Calculate the "real" depth in meters from the surface of Europa (the value you see on the nav terminal)
        /// </summary>
        public float GetRealWorldDepth(float worldPositionY)
        {
            if (GameMain.GameSession?.Campaign == null)
            {
                //ensure the levels aren't too deep to traverse in non-campaign modes where you don't have the option to upgrade/switch the sub
                return (-(worldPositionY - GenerationParams.Height) + 80000.0f) * Physics.DisplayToRealWorldRatio;
            }
            else
            {
                return (-(worldPositionY - GenerationParams.Height) + LevelData.InitialDepth) * Physics.DisplayToRealWorldRatio;
            }       
        }

        /// <summary>
        /// Is the position above the upper boundary of the level ("outside bounds", where nothing should be able to get to normally)?
        /// E.g. respawn shuttles are moved above the level when they despawn.
        /// </summary>
        public static bool IsPositionAboveLevel(Vector2 worldPosition)
        {
            return Loaded != null && worldPosition.Y > Loaded.Size.Y;
        }

        public void DebugSetStartLocation(Location newStartLocation)
        {
            StartLocation = newStartLocation;
        }

        public void DebugSetEndLocation(Location newEndLocation)
        {
            EndLocation = newEndLocation;
        }

        public override void Remove()
        {
            base.Remove();
#if CLIENT
            if (renderer != null) 
            {
                renderer.Dispose();
                renderer = null;
            }
#endif

            if (LevelObjectManager != null)
            {
                LevelObjectManager.Remove();
                LevelObjectManager = null;
            }

            AbyssIslands?.Clear();
            AbyssResources?.Clear();
            Caves?.Clear();
            Tunnels?.Clear();
            PathPoints?.Clear();
            PositionsOfInterest?.Clear();

            positionHistory?.Clear();
            Wrecks?.Clear();

            BeaconStation = null;
            beaconSonar = null;
            StartOutpost = null;
            EndOutpost = null;

            blockedRects?.Clear();

            EntitiesBeforeGenerate?.Clear();
            ClearEqualityCheckValues();

            if (Ruins != null)
            {
                Ruins.Clear();
                Ruins = null;
            }

            bottomPositions?.Clear();
            BottomBarrier = null;
            TopBarrier = null;
            SeaFloor = null;

            distanceField = null;

            if (ExtraWalls != null)
            {
                foreach (LevelWall w in ExtraWalls) { w.Dispose(); }
                ExtraWalls = null;
            }
            if (UnsyncedExtraWalls != null)
            {
                foreach (LevelWall w in UnsyncedExtraWalls) { w.Dispose(); }
                UnsyncedExtraWalls = null;
            }

            tempCells?.Clear();
            cells = null;
            cellGrid = null;
            
            if (bodies != null)
            {
                bodies.Clear();
                bodies = null;
            }

            StartLocation = null;
            EndLocation = null;

            Loaded = null;
        }
    }

    static class PositionTypeExtensions
    {
        /// <summary>
        /// Caves, ruins, outposts and similar enclosed areas
        /// </summary>
        public static bool IsEnclosedArea(this Level.PositionType positionType)
        {
            return
                 positionType == Level.PositionType.Cave ||
                 positionType == Level.PositionType.AbyssCave ||
                 positionType.IsIndoorsArea();
        }

        /// <summary>
        /// Area inside a submarine (outpost, wreck, beacon station)
        /// </summary>
        public static bool IsIndoorsArea(this Level.PositionType positionType)
        {
            return
                 positionType == Level.PositionType.Outpost ||
                 positionType == Level.PositionType.BeaconStation ||
                 positionType == Level.PositionType.Ruin ||
                 positionType == Level.PositionType.Wreck;
        }
    }
}

using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.Xna.Framework.Input;
using Barotrauma.Extensions;

namespace Barotrauma
{
    partial class Map
    {
        class MapAnim
        {
            public Location StartLocation;
            public Location EndLocation;
            public string StartMessage;
            public string EndMessage;

            /// <summary>
            /// Initial zoom (0 - 1, from min zoom to max)
            /// </summary>
            public float? StartZoom;
            /// <summary>
            /// Initial zoom (0 - 1, from min zoom to max)
            /// </summary>
            public float? EndZoom;

            private float startDelay;
            public float StartDelay
            {
                get { return startDelay; }
                set
                {
                    startDelay = value;
                    Timer = -startDelay;
                }
            }

            public Vector2? StartPos;

            public float Duration;
            public float Timer;

            public bool Finished;
        }

        private readonly Queue<MapAnim> mapAnimQueue = new Queue<MapAnim>();

        public Location HighlightedLocation { get; private set; }

        private static Sprite noiseOverlay;

        public Vector2 DrawOffset;
        private Vector2 drawOffsetNoise;

        private Vector2 currLocationIndicatorPos;

        private float zoom = 3.0f;
        private float targetZoom;

        private Rectangle borders;
        
        private Sprite[,] mapTiles;
        private bool[,] tileDiscovered;

        private float connectionHighlightState;

        private (Rectangle targetArea, RichString tip)? tooltip;

        private SubmarineInfo.PendingSubInfo pendingSubInfo;

        private RichString beaconStationActiveText, beaconStationInactiveText;

        private GUIComponent locationInfoOverlay;

        /*private (Rectangle targetArea, string tip)? connectionTooltip;
        private string sanitizedConnectionTooltip;
        private List<RichTextData> connectionTooltipRichTextData;
        private string prevConnectionTooltip;*/

#if DEBUG
        private GUIComponent editor;

        private void CreateEditor()
        {
            editor = new GUIFrame(new RectTransform(new Vector2(0.25f, 1.0f), GUI.Canvas, Anchor.TopRight, minSize: new Point(400, 0)));
            var paddedFrame = new GUILayoutGroup(new RectTransform(new Vector2(0.9f, 0.95f), editor.RectTransform, Anchor.Center))
            {
                Stretch = true,
                RelativeSpacing = 0.02f,
                CanBeFocused = false
            };

            var listBox = new GUIListBox(new RectTransform(new Vector2(1.0f, 0.95f), paddedFrame.RectTransform, Anchor.Center));
            new SerializableEntityEditor(listBox.Content.RectTransform, generationParams, false, true);

            new GUIButton(new RectTransform(new Vector2(1.0f, 0.05f), paddedFrame.RectTransform), "Generate")
            {
                OnClicked = (btn, userData) =>
                {
                    Rand.SetSyncedSeed(ToolBox.StringToInt(this.Seed));
                    Generate(GameMain.GameSession?.Campaign);
                    InitProjectSpecific();
                    return true;
                }
            };
        }
#endif

        partial void InitProjectSpecific()
        {
            noiseOverlay ??= new Sprite("Content/UI/noise.png", Vector2.Zero);

            OnLocationChanged.RegisterOverwriteExisting(
                "Map.InitProjSpecific".ToIdentifier(), 
                (locationChangeInfo) => LocationChanged(locationChangeInfo.PrevLocation, locationChangeInfo.NewLocation));

            borders = new Rectangle(
                (int)Locations.Min(l => l.MapPosition.X),
                (int)Locations.Min(l => l.MapPosition.Y),
                (int)Locations.Max(l => l.MapPosition.X),
                (int)Locations.Max(l => l.MapPosition.Y));
            borders.Width -= borders.X;
            borders.Height -= borders.Y;

            if (CurrentLocation != null)
            {
                DrawOffset = -CurrentLocation.MapPosition;
            }

            Vector2 tileSize = generationParams.MapTiles.Values.First().First().size * generationParams.MapTileScale;
            int tilesX = (int)Math.Ceiling(Width / tileSize.X);
            int tilesY = (int)Math.Ceiling(Height / tileSize.Y);
            mapTiles = new Sprite[tilesX, tilesY];
            tileDiscovered = new bool[tilesX, tilesY];
            HashSet<Biome> missingBiomes = new HashSet<Biome>();
            for (int x = 0; x < tilesX; x++)
            {
                for (int y = 0; y < tilesY; y++)
                {
                    var biome = GetBiome(x * tileSize.X);
                    ImmutableArray<Sprite> tileList;
                    if (generationParams.MapTiles.ContainsKey(biome.Identifier))
                    {
                        tileList = generationParams.MapTiles[biome.Identifier];
                    }
                    else
                    {
                        tileList = generationParams.MapTiles.Values.First();
                        missingBiomes.Add(biome);
                    }
                    mapTiles[x, y] = tileList[x % tileList.Length];
                }
            }

            foreach (var missingBiome in missingBiomes)
            {
                DebugConsole.ThrowError($"Could not find campaign map sprites for the biome \"{missingBiome.Identifier}\". Using the sprites of the first biome instead...");
            }

            beaconStationActiveText = RichString.Rich(TextManager.Get("BeaconStationActiveTooltip"));
            beaconStationInactiveText = RichString.Rich(TextManager.Get("BeaconStationInactiveTooltip"));

            RemoveFogOfWar(StartLocation);

            GenerateAllLocationConnectionVisuals();
        }

        partial void GenerateAllLocationConnectionVisuals()
        {
            foreach (LocationConnection connection in Connections)
            {
                GenerateLocationConnectionVisuals(connection);
            }
        }
        partial void GenerateLocationConnectionVisuals(LocationConnection connection)
        {
            Vector2 connectionStart = connection.Locations[0].MapPosition;
            Vector2 connectionEnd = connection.Locations[1].MapPosition;
            float connectionLength = Vector2.Distance(connectionStart, connectionEnd);
            int iterations = Math.Min((int)Math.Sqrt(connectionLength * generationParams.ConnectionIndicatorIterationMultiplier), 5);
            connection.CrackSegments.Clear();
            connection.CrackSegments.AddRange(MathUtils.GenerateJaggedLine(
                connectionStart, connectionEnd,
                iterations, connectionLength * generationParams.ConnectionIndicatorDisplacementMultiplier,
                rng: Rand.GetRNG(Rand.RandSync.ServerAndClient)));
        }

        private void LocationChanged(Location prevLocation, Location newLocation)
        {
            if (prevLocation == newLocation) { return; }
            //focus on starting location
            if (prevLocation != null)
            {
                mapAnimQueue.Enqueue(new MapAnim()
                {
                    EndZoom = 1.0f,
                    EndLocation = prevLocation,
                    Duration = MathHelper.Clamp(Vector2.Distance(-DrawOffset, prevLocation.MapPosition) / 1000.0f, 0.1f, 0.5f)
                });
                mapAnimQueue.Enqueue(new MapAnim()
                {
                    EndZoom = 0.5f,
                    StartLocation = prevLocation,
                    EndLocation = newLocation,
                    Duration = 2.0f,
                    StartDelay = 0.5f
                });
            }
            else
            {
                currLocationIndicatorPos = CurrentLocation.MapPosition;
            }

            if (newLocation.Visited)
            {
                RemoveFogOfWar(newLocation);
            }
        }

        partial void RemoveFogOfWarProjSpecific(Location location) => RemoveFogOfWar(location);

        private void RemoveFogOfWar(Location location, bool removeFromAdjacentLocations = true)
        {
            if (mapTiles == null) { return; }
            if (location == null) { return; }

            var mapTile = generationParams.MapTiles.Values.FirstOrDefault().FirstOrDefault();
            if (mapTile == null) { return; }

            Vector2 mapTileSize = mapTile.size * generationParams.MapTileScale;
            int startX = (int)Math.Max(Math.Floor(location.MapPosition.X / mapTileSize.X - 0.25f), 0);
            int startY = (int)Math.Max(Math.Floor(location.MapPosition.Y / mapTileSize.Y - 0.25f), 0);
            int endX = (int)Math.Min(Math.Floor(location.MapPosition.X / mapTileSize.X + 0.25f), mapTiles.GetLength(0) - 1);
            int endY = (int)Math.Min(Math.Floor(location.MapPosition.Y / mapTileSize.Y + 0.25f), mapTiles.GetLength(1) - 1);
            for (int x = startX; x <= endX; x++)
            {
                for (int y = startY; y <= endY; y++)
                {
                    tileDiscovered[x, y] = true;
                }
            }
            if (removeFromAdjacentLocations)
            {
                foreach (LocationConnection c in location.Connections)
                {
                    var otherLocation = c.OtherLocation(location);
                    RemoveFogOfWar(otherLocation, removeFromAdjacentLocations: false);
                }
            }
        }

        private bool IsInFogOfWar(Location location)
        {
            if (GameMain.DebugDraw) { return false; }
            Vector2 mapTileSize = mapTiles[0, 0].size * generationParams.MapTileScale;
            int x = (int)Math.Floor(location.MapPosition.X / mapTileSize.X);
            int y = (int)Math.Floor(location.MapPosition.Y / mapTileSize.Y);

            return !tileDiscovered[MathHelper.Clamp(x, 0, tileDiscovered.Length), MathHelper.Clamp(y, 0, tileDiscovered.Length)];
        }

        private class MapNotification
        {
            public readonly RichString Text;
            public readonly GUIFont Font;

            public readonly Vector2 TextSize;

            public int TimesShown;

            public float Offset;

            public readonly Location RelatedLocation;

            public bool IsCurrentlyVisible;

            public MapNotification(string text, GUIFont font, List<MapNotification> existingNotifications, Location relatedLocation)
            {
                Text = RichString.Rich(text);
                Font = font;
                TextSize = Font.MeasureString(Font.ForceUpperCase ? Text.SanitizedValue.ToUpper() : Text.SanitizedValue);
                if (existingNotifications.Any())
                {
                    Offset = existingNotifications.Max(n => n.Offset + n.TextSize.X + GUI.IntScale(60));
                }
                RelatedLocation = relatedLocation;
            }
        }

        private readonly List<MapNotification> mapNotifications = new List<MapNotification>();

        partial void ChangeLocationTypeProjSpecific(Location location, LocalizedString prevName, LocationTypeChange change)
        {
            var messages = change.GetMessages(location.Faction);
            if (!messages.Any()) { return; }

            string msg = messages.GetRandom(Rand.RandSync.Unsynced)
                .Replace("[previousname]", $"‖color:gui.yellow‖{prevName}‖end‖")
                .Replace("[name]", $"‖color:gui.yellow‖{location.DisplayName}‖end‖");
            location.LastTypeChangeMessage = msg;

            mapNotifications.Add(new MapNotification(msg, GUIStyle.SubHeadingFont, mapNotifications, location));           
        }

        public void DrawNotifications(SpriteBatch spriteBatch, GUICustomComponent container)
        {
            Vector2 pos = new Vector2(container.Rect.Right, container.Rect.Center.Y);
            foreach (var notification in mapNotifications)
            {
                Vector2 textPos = pos + new Vector2(notification.Offset, -notification.TextSize.Y / 2);

                notification.Font.DrawStringWithColors(
                    spriteBatch, 
                    notification.Text.SanitizedValue,
                   textPos, 
                    Color.White, 0.0f, Vector2.Zero, 1.0f, SpriteEffects.None, 0, 
                    notification.Text.RichTextData);

                int margin = container.Rect.Width / 5;
                notification.IsCurrentlyVisible = 
                    textPos.X < container.Rect.Right - margin &&
                    textPos.X + notification.TextSize.X > container.Rect.X + margin;
            }
        }

        private void UpdateNotifications(float deltaTime, GUICustomComponent mapContainer)
        {
            if (mapNotifications.Count < 5)
            {
                int maxIndex = 1;
                while (TextManager.ContainsTag("randomnews" + maxIndex))
                {
                    maxIndex++;
                }
                string textTag = "randomnews" + Rand.Range(0, maxIndex);
                if (TextManager.ContainsTag(textTag))
                {
                    mapNotifications.Add(new MapNotification(TextManager.Get(textTag).Value, GUIStyle.SubHeadingFont, mapNotifications, relatedLocation: null));
                }
            }

            for (int i = mapNotifications.Count - 1; i >= 0; i--)
            {
                var notification = mapNotifications[i];
                notification.Offset -= deltaTime * 75.0f;
                if (notification.Offset < -notification.TextSize.X - mapContainer.Rect.Width)
                {
                    notification.Offset = Math.Max(mapNotifications.Max(n => n.Offset + n.TextSize.X) + GUI.IntScale(60), 0);
                    notification.TimesShown++;
                    if (mapNotifications.Count > 5)
                    {
                        mapNotifications.RemoveAt(i);
                    }
                    else if (mapNotifications.Count > 3 && notification.TimesShown > 2)
                    {
                        mapNotifications.RemoveAt(i);
                    }
                }
            }
        }

        private void CreateLocationInfoOverlay(Location location)
        {
            locationInfoOverlay = new GUIFrame(new RectTransform(new Point(GUI.IntScale(350), GUI.IntScale(350)), GUI.Canvas), style: "GUIToolTip")
            {
                UserData = location
            };
            locationInfoOverlay.Color *= 0.8f;

            var content = new GUILayoutGroup(new RectTransform(new Vector2(0.9f, 0.85f), locationInfoOverlay.RectTransform, Anchor.Center))
            {
                Stretch = true,
                RelativeSpacing = 0.02f
            };

            bool showReputation = hudVisibility > 0.0f && location.Type.HasOutpost && location.Reputation != null;

            new GUITextBlock(new RectTransform(new Vector2(1.0f, 0.0f), content.RectTransform), location.DisplayName, font: GUIStyle.LargeFont) { Padding = Vector4.Zero };
            if (!location.Type.Name.IsNullOrEmpty())
            {
                new GUITextBlock(new RectTransform(new Vector2(1.0f, 0.0f), content.RectTransform), location.Type.Name, font: GUIStyle.SubHeadingFont) { Padding = Vector4.Zero };
            }

            CreateSpacing(10);

            if (!location.Type.Description.IsNullOrEmpty())
            {
                CreateTextWithIcon(location.Type.Description, location.Type.Sprite);
            }

            int highestSubTier = location.HighestSubmarineTierAvailable();
            List<(SubmarineClass subClass, int tier)> overrideTiers = null;
            if (location.CanHaveSubsForSale())
            {
                overrideTiers = new List<(SubmarineClass subClass, int tier)>();
                foreach (SubmarineClass subClass in SubmarineClass.Classes)
                {
                    int highestClassTier = location.HighestSubmarineTierAvailable(subClass);
                    if (highestClassTier > 0 && highestClassTier > highestSubTier)
                    {
                        overrideTiers.Add((subClass, highestClassTier));
                    }
                }
            }
            if (highestSubTier > 0)
            {
                CreateTextWithIcon(TextManager.GetWithVariable("advancedsub.all", "[tiernumber]", highestSubTier.ToString()), icon: null, style: "LocationOverlaySubmarineIcon");
            }
            if (overrideTiers != null)
            {
                foreach (var (subClass, tier) in overrideTiers)
                {
                    string textTag = $"advancedsub.{subClass.TextIdentifier.IfEmpty(in subClass.Identifier)}";
                    CreateTextWithIcon(TextManager.GetWithVariable(textTag, "[tiernumber]", tier.ToString()), icon: subClass.AvailabilityIcon, style: "LocationOverlaySubmarineIcon");
                }
            }

            CreateSpacing(10);

            void CreateTextWithIcon(LocalizedString text, Sprite icon, string style = null)
            {
                var textHolder = new GUILayoutGroup(new RectTransform(new Point(content.Rect.Width, (int)GUIStyle.Font.MeasureString(text).Y), content.RectTransform), isHorizontal: true)
                {
                    Stretch = true,
                    CanBeFocused = true
                };
                var guiIcon =
                    icon != null ? 
                    new GUIImage(new RectTransform(Vector2.One * 1.25f, textHolder.RectTransform, scaleBasis: ScaleBasis.BothHeight), icon, scaleToFit: true) { Color = icon.Color } :
                    new GUIImage(new RectTransform(Vector2.One * 1.25f, textHolder.RectTransform, scaleBasis: ScaleBasis.BothHeight), style);
                var textBlock = new GUITextBlock(new RectTransform(new Vector2(0.9f, 1.0f), textHolder.RectTransform), text);
                textBlock.RectTransform.MinSize = new Point((int)textBlock.TextSize.X, 0);
                textHolder.RectTransform.MinSize = new Point((int)textBlock.TextSize.X + guiIcon.Rect.Width, 0);
            }

            void CreateSpacing(int height)
            {
                new GUIFrame(new RectTransform(new Point(content.Rect.Width, GUI.IntScale(height)), content.RectTransform), style: null);
            }

            if (location.Faction != null)
            {
                new GUITextBlock(new RectTransform(new Vector2(1.0f, 0.0f), content.RectTransform),
                    RichString.Rich(TextManager.GetWithVariables("reputationgainnotification",
                        ("[value]", string.Empty),
                        ("[reputationname]", $"‖color:{XMLExtensions.ToStringHex(location.Faction.Prefab.IconColor)}‖{location.Faction.Prefab.Name}‖end‖"))))
                { 
                    Padding = Vector4.Zero 
                };

                CreateSpacing(10);

                var repBarHolder = new GUILayoutGroup(new RectTransform(new Point(content.Rect.Width, GUI.IntScale(25)), content.RectTransform), isHorizontal: true)
                {
                    Stretch = true,
                    RelativeSpacing = 0.05f
                };
                new GUICustomComponent(new RectTransform(new Vector2(0.6f, 1.0f), repBarHolder.RectTransform), onDraw: (sb, component) =>
                {
                    if (location.Reputation == null) { return; }
                    RoundSummary.DrawReputationBar(sb, component.Rect, 
                        location.Reputation.NormalizedValue, 
                        location.Reputation.MinReputation, location.Reputation.MaxReputation);
                });

                new GUITextBlock(new RectTransform(new Vector2(0.4f, 1.0f), repBarHolder.RectTransform),
                    location.Reputation.GetFormattedReputationText(), textAlignment: Alignment.CenterRight);

                new GUIImage(new RectTransform(new Vector2(0.25f, 0.5f), locationInfoOverlay.RectTransform, Anchor.BottomRight) { RelativeOffset = new Vector2(0.05f) }, 
                    location.Faction.Prefab.Icon, scaleToFit: true)
                {
                    Color = location.Faction.Prefab.IconColor * 0.5f
                };
                CreateSpacing(20);
            }

            locationInfoOverlay.RectTransform.NonScaledSize =
                new Point(
                    Math.Max(locationInfoOverlay.Rect.Width, (int)(content.Children.Max(c => c is GUITextBlock textBlock ? textBlock.TextSize.X : c.RectTransform.MinSize.X) * 1.2f)),
                    (int)(content.Children.Sum(c => c.Rect.Height) / content.RectTransform.RelativeSize.Y));
        }

        partial void ClearAnimQueue()
        {
            mapAnimQueue.Clear();
        }

        public void Update(CampaignMode campaign, float deltaTime, GUICustomComponent mapContainer)
        {
            Rectangle rect = mapContainer.Rect;

            UpdateNotifications(deltaTime, mapContainer);

            var currentDisplayLocation = campaign?.GetCurrentDisplayLocation();
            if (currentDisplayLocation != null)
            {
                if (!currentDisplayLocation.Discovered)
                {
                    RemoveFogOfWar(currentDisplayLocation);
                    Discover(currentDisplayLocation);
                    if (currentDisplayLocation.MapPosition.X > furthestDiscoveredLocation.MapPosition.X)
                    {
                        furthestDiscoveredLocation = currentDisplayLocation;
                    }
                }
            }

            Vector2 currentPosition = currentDisplayLocation.MapPosition;
            if (Level.Loaded?.Type == LevelData.LevelType.LocationConnection && Level.Loaded.StartLocation != null && Level.Loaded.EndLocation != null)
            {
                Vector2 startPos = currentDisplayLocation == Level.Loaded.StartLocation ? Level.Loaded.StartLocation.MapPosition : Level.Loaded.EndLocation.MapPosition;
                int moveDir = currentDisplayLocation == Level.Loaded.StartLocation ? 1 : -1;

                Vector2 diff = Level.Loaded.EndLocation.MapPosition - Level.Loaded.StartLocation.MapPosition;
                currentPosition = startPos + 
                    Vector2.Normalize(diff) * Math.Min(100, diff.Length() * 0.2f) * moveDir;
            }
            else
            {
                currentPosition += Vector2.UnitY * 35;
            }

            currLocationIndicatorPos = Vector2.Lerp(currLocationIndicatorPos, currentPosition, deltaTime);
#if DEBUG
            if (GameMain.DebugDraw)
            {
                if (editor == null) CreateEditor();
                editor.AddToGUIUpdateList(order: 1);
            }

            if (PlayerInput.KeyHit(Keys.Space))
            {
                Radiation?.OnStep();
            }
#endif

            Radiation?.MapUpdate(deltaTime);

            if (mapAnimQueue.Count > 0)
            {
                hudVisibility = Math.Max(hudVisibility - deltaTime, 0.0f);
                UpdateMapAnim(mapAnimQueue.Peek(), deltaTime);
                if (mapAnimQueue.Peek().Finished)
                {
                    mapAnimQueue.Dequeue();
                }
                return;
            }

            hudVisibility = Math.Min(hudVisibility + deltaTime, 0.75f + (float)Math.Sin(Timing.TotalTime * 3.0f) * 0.25f);
            
            Vector2 rectCenter = new Vector2(rect.Center.X, rect.Center.Y);
            Vector2 viewOffset = DrawOffset + drawOffsetNoise;
            if (HighlightedLocation != null)
            {
                Vector2 highlightedLocationDrawPos = rectCenter + (HighlightedLocation.MapPosition + viewOffset) * zoom;
                if (locationInfoOverlay == null || locationInfoOverlay.UserData != HighlightedLocation)
                {
                    CreateLocationInfoOverlay(HighlightedLocation);
                }

                Point offsetFromLocationIcon = new Point(GUI.IntScale(25));
                var locationInfoRt = locationInfoOverlay.RectTransform;
                if (locationInfoRt.Pivot == Pivot.BottomLeft || locationInfoRt.Pivot == Pivot.BottomRight)
                {
                    offsetFromLocationIcon.Y = -offsetFromLocationIcon.Y;
                }
                if (locationInfoRt.Pivot == Pivot.TopRight || locationInfoRt.Pivot == Pivot.BottomRight)
                {
                    offsetFromLocationIcon.X = -offsetFromLocationIcon.X;
                }
                locationInfoRt.ScreenSpaceOffset = highlightedLocationDrawPos.ToPoint() + offsetFromLocationIcon;
                if (locationInfoOverlay.Rect.Bottom > rect.Bottom)
                {
                    locationInfoRt.Pivot = Pivot.BottomLeft;
                }
                if (locationInfoOverlay.Rect.Right > rect.Right)
                {
                    locationInfoRt.Pivot = locationInfoRt.Pivot == Pivot.TopLeft ? Pivot.TopRight : Pivot.BottomRight;
                }
                locationInfoOverlay?.AddToGUIUpdateList(order: 1);
            }

            float closestDist = 0.0f;
            HighlightedLocation = null;
            if ((GUI.MouseOn == null || GUI.MouseOn == mapContainer))
            {
                for (int i = 0; i < Locations.Count; i++)
                {
                    Location location = Locations[i];
                    if (IsInFogOfWar(location) && !(currentDisplayLocation?.Connections.Any(c => c.Locations.Contains(location)) ?? false) && !GameMain.DebugDraw) { continue; }

                    Vector2 pos = rectCenter + (location.MapPosition + viewOffset) * zoom;
                    if (!rect.Contains(pos)) { continue; }

                    Sprite locationSprite = location.IsCriticallyRadiated() ? location.Type.RadiationSprite ?? location.Type.Sprite : location.Type.Sprite;
                    float iconScale = generationParams.LocationIconSize / locationSprite.size.X;
                    if (location == currentDisplayLocation) { iconScale *= 1.2f; }

                    Rectangle drawRect = locationSprite.SourceRect;
                    drawRect.Width = (int)(drawRect.Width * iconScale * zoom * 1.4f);
                    drawRect.Height = (int)(drawRect.Height * iconScale * zoom * 1.4f);
                    drawRect.X = (int)pos.X - drawRect.Width / 2;
                    drawRect.Y = (int)pos.Y - drawRect.Width / 2;

                    if (!drawRect.Contains(PlayerInput.MousePosition)) { continue; }

                    float dist = Vector2.Distance(PlayerInput.MousePosition, pos);
                    if (HighlightedLocation == null || dist < closestDist)
                    {
                        closestDist = dist;
                        HighlightedLocation = location; 
                    }
                }
            }

            if (SelectedConnection != null)
            {
                connectionHighlightState = Math.Min(connectionHighlightState + deltaTime, 1.0f);
            }
            else
            {
                connectionHighlightState = 0.0f;
            }

            if (GUI.KeyboardDispatcher.Subscriber == null)
            {
                float moveSpeed = 1000.0f;
                Vector2 moveAmount = Vector2.Zero;
                if (PlayerInput.KeyDown(InputType.Left)) { moveAmount += Vector2.UnitX; }
                if (PlayerInput.KeyDown(InputType.Right)) { moveAmount -= Vector2.UnitX; }
                if (PlayerInput.KeyDown(InputType.Up)) { moveAmount += Vector2.UnitY; }
                if (PlayerInput.KeyDown(InputType.Down)) { moveAmount -= Vector2.UnitY; }
                DrawOffset += moveAmount * moveSpeed / zoom * deltaTime;
            }

            targetZoom = MathHelper.Clamp(targetZoom, generationParams.MinZoom, generationParams.MaxZoom);
            zoom = MathHelper.Lerp(zoom, targetZoom * GUI.Scale, 0.1f);

            if (GUI.MouseOn == mapContainer)
            {
                foreach (LocationConnection connection in Connections)
                {
                    if (HighlightedLocation != currentDisplayLocation &&
                        connection.Locations.Contains(HighlightedLocation) && 
                        connection.Locations.Contains(currentDisplayLocation))
                    {
                        if (PlayerInput.PrimaryMouseButtonClicked() &&
                            SelectedLocation != HighlightedLocation && HighlightedLocation != null)
                        {
                            if (connection.Locked)
                            {
                                new GUIMessageBox(string.Empty, TextManager.Get("LockedPathTooltip"));
                            }
                            //clients aren't allowed to select the location without a permission
                            else if (CampaignMode.AllowedToManageCampaign(Networking.ClientPermissions.ManageMap))
                            {
                                connectionHighlightState = 0.0f;
                                SelectedConnection = connection;
                                SelectedLocation = HighlightedLocation;

                                OnLocationSelected?.Invoke(SelectedLocation, SelectedConnection);
                                GameMain.Client?.SendCampaignState();
                            }
                        }
                    }
                }            

                targetZoom += PlayerInput.ScrollWheelSpeed / 500.0f;

                if (PlayerInput.MidButtonHeld() || (HighlightedLocation == null && PlayerInput.PrimaryMouseButtonHeld()))
                {
                    DrawOffset += PlayerInput.MouseSpeed / zoom;
                }
                if (AllowDebugTeleport)
                {
                    if (PlayerInput.DoubleClicked() && HighlightedLocation != null)
                    {
                        var passedConnection = currentDisplayLocation.Connections.Find(c => c.OtherLocation(currentDisplayLocation) == HighlightedLocation);
                        if (passedConnection != null)
                        {
                            passedConnection.Passed = true;
                        }

                        Location prevLocation = currentDisplayLocation;
                        CurrentLocation = HighlightedLocation;
                        Level.Loaded.DebugSetStartLocation(CurrentLocation);
                        Level.Loaded.DebugSetEndLocation(null);

                        Discover(CurrentLocation);
                        Visit(CurrentLocation);
                        OnLocationChanged?.Invoke(new LocationChangeInfo(prevLocation, CurrentLocation));
                        SelectLocation(-1);
                        if (GameMain.Client == null)
                        {
                            CurrentLocation.CreateStores();
                            ProgressWorld(campaign);
                            Radiation?.OnStep(1);
                        }
                        else
                        {
                            GameMain.Client.SendCampaignState();
                        }
                    }

                    if (PlayerInput.PrimaryMouseButtonClicked() && HighlightedLocation == null)
                    {
                        SelectLocation(-1);
                    }
                }
            }
        }
        
        public void Draw(CampaignMode campaign, SpriteBatch spriteBatch, GUICustomComponent mapContainer)
        {
            tooltip = null;
            var currentDisplayLocation = campaign?.GetCurrentDisplayLocation();

            Rectangle rect = mapContainer.Rect;

            Vector2 viewSize = new Vector2(rect.Width / zoom, rect.Height / zoom);
            Vector2 edgeBuffer = new Vector2(rect.Width * 0.05f);
            DrawOffset.X = MathHelper.Clamp(DrawOffset.X, -Width - edgeBuffer.X + viewSize.X / 2.0f, edgeBuffer.X - viewSize.X / 2.0f);
            DrawOffset.Y = MathHelper.Clamp(DrawOffset.Y, -Height - edgeBuffer.Y + viewSize.Y / 2.0f, edgeBuffer.Y - viewSize.Y / 2.0f);

            drawOffsetNoise = new Vector2(
                (float)PerlinNoise.CalculatePerlin(Timing.TotalTime * 0.1f % 255, Timing.TotalTime * 0.1f % 255, 0) - 0.5f, 
                (float)PerlinNoise.CalculatePerlin(Timing.TotalTime * 0.2f % 255, Timing.TotalTime * 0.2f % 255, 0.5f) - 0.5f) * 10.0f;

            Vector2 viewOffset = DrawOffset + drawOffsetNoise;

            Vector2 rectCenter = new Vector2(rect.Center.X, rect.Center.Y);

            float missionIconScale = generationParams.MissionIcon != null ? 18.0f / generationParams.MissionIcon.SourceRect.Width : 1.0f;

            Rectangle prevScissorRect = GameMain.Instance.GraphicsDevice.ScissorRectangle;
            spriteBatch.End();
            spriteBatch.GraphicsDevice.ScissorRectangle = Rectangle.Intersect(prevScissorRect, rect);
            spriteBatch.Begin(SpriteSortMode.Deferred, samplerState: GUI.SamplerState, rasterizerState: GameMain.ScissorTestEnable);

            Vector2 topLeft = rectCenter + viewOffset - rect.Location.ToVector2();
            Vector2 bottomRight = topLeft + new Vector2(Width, Height);
            Vector2 mapTileSize = mapTiles[0, 0].size * generationParams.MapTileScale;

            int startX = (int)Math.Floor(-topLeft.X / mapTileSize.X) - 1;
            int startY = (int)Math.Floor(-topLeft.Y / mapTileSize.Y) - 1;
            int endX = (int)Math.Ceiling((-topLeft.X + rect.Width) / mapTileSize.X);
            int endY = (int)Math.Ceiling((-topLeft.Y + rect.Height) / mapTileSize.Y);

            float noiseT = (float)(Timing.TotalTime * 0.01f);
            cameraNoiseStrength = (float)PerlinNoise.CalculatePerlin(noiseT, noiseT * 0.5f, noiseT * 0.2f);
            float noiseScale = (float)PerlinNoise.CalculatePerlin(noiseT * 5.0f, noiseT * 2.0f, 0) * 5.0f;

            for (int x = startX; x <= endX; x++)
            {
                for (int y = startY; y <= endY; y++)
                {
                    int tileX = Math.Abs(x) % mapTiles.GetLength(0);
                    int tileY = Math.Abs(y) % mapTiles.GetLength(1);
                    Vector2 tilePos = rectCenter + (viewOffset + new Vector2(x, y) * mapTileSize) * zoom;
                    mapTiles[tileX, tileY].Draw(spriteBatch, tilePos, Color.White, origin: Vector2.Zero, scale: generationParams.MapTileScale * zoom);

                    if (GameMain.DebugDraw) { continue; }
                    if (!tileDiscovered[tileX, tileY] || x < 0 || y < 0 || x >= tileDiscovered.GetLength(0) || y >= tileDiscovered.GetLength(1))
                    {
                        generationParams.FogOfWarSprite?.Draw(spriteBatch, tilePos, Color.White * cameraNoiseStrength, origin: Vector2.Zero, scale: generationParams.MapTileScale * zoom);
                        noiseOverlay.DrawTiled(spriteBatch, tilePos, mapTileSize * zoom,
                            startOffset: new Vector2(Rand.Range(0.0f, noiseOverlay.SourceRect.Width), Rand.Range(0.0f, noiseOverlay.SourceRect.Height)),
                            color: Color.White * cameraNoiseStrength * 0.2f,
                            textureScale: Vector2.One * noiseScale);
                    }
                }
            }

            if (GameMain.DebugDraw)
            {
                if (topLeft.X > rect.X)
                    GUI.DrawRectangle(spriteBatch, new Rectangle(rect.X, rect.Y, (int)(topLeft.X - rect.X), rect.Height), Color.Black * 0.5f, true);
                if (topLeft.Y > rect.Y)
                    GUI.DrawRectangle(spriteBatch, new Rectangle((int)topLeft.X, rect.Y, (int)(bottomRight.X - topLeft.X), (int)(topLeft.Y - rect.Y)), Color.Black * 0.5f, true);
                if (bottomRight.X < rect.Right)
                    GUI.DrawRectangle(spriteBatch, new Rectangle((int)bottomRight.X, rect.Y, (int)(rect.Right - bottomRight.X), rect.Height), Color.Black * 0.5f, true);
                if (bottomRight.Y < rect.Bottom)
                    GUI.DrawRectangle(spriteBatch, new Rectangle((int)topLeft.X, (int)bottomRight.Y, (int)(bottomRight.X - topLeft.X), (int)(rect.Bottom - bottomRight.Y)), Color.Black * 0.5f, true);
            }

            float rawNoiseScale = 1.0f + PerlinNoise.GetPerlin((int)(Timing.TotalTime * 1 - 1), (int)(Timing.TotalTime * 1 - 1));
            DrawNoise(spriteBatch, rect, rawNoiseScale);

            Radiation?.Draw(spriteBatch, rect, zoom);

            if (generationParams.ShowLocations)
            {
                foreach (LocationConnection connection in Connections)
                {
                    if (IsInFogOfWar(connection.Locations[0]) && IsInFogOfWar(connection.Locations[1])) { continue; }
                    DrawConnection(spriteBatch, connection, rect, viewOffset, currentDisplayLocation);
                }
                
                for (int i = 0; i < Locations.Count; i++)
                {
                    Location location = Locations[i];
                    if (!location.Discovered && IsInFogOfWar(location)) { continue; }
                    bool isEndLocation = endLocations.Contains(location);
                    if (!GameMain.DebugDraw && isEndLocation && location != endLocations.First()) { continue; }
                    Vector2 pos = rectCenter + (location.MapPosition + viewOffset) * zoom;

                    Sprite locationSprite = location.IsCriticallyRadiated() ? location.Type.RadiationSprite ?? location.Type.Sprite : location.Type.Sprite;

                    Rectangle drawRect = locationSprite.SourceRect;
                    drawRect.X = (int)pos.X - drawRect.Width / 2;
                    drawRect.Y = (int)pos.Y - drawRect.Width / 2;

                    if (drawRect.X > rect.Right - GUI.IntScale(100) && generationParams.MissionIcon != null && location.AvailableMissions.Any())
                    {
                        Vector2 offScreenMissionIconPos = new Vector2(rect.Right - GUI.IntScale(50), drawRect.Center.Y);
                        generationParams.MissionIcon.Draw(spriteBatch,
                            offScreenMissionIconPos,
                            generationParams.IndicatorColor, scale: missionIconScale * zoom);
                        GUI.Arrow.Draw(spriteBatch,
                            offScreenMissionIconPos + Vector2.UnitX * generationParams.MissionIcon.size.X * missionIconScale * zoom,
                            generationParams.IndicatorColor, MathHelper.PiOver2, scale: 0.5f);
                    }


                    if (!rect.Intersects(drawRect)) { continue; }

                    Color color = location.Type.SpriteColor;
                    if (!location.Visited) { color = Color.White; }
                    if (location.Connections.Find(c => c.Locations.Contains(currentDisplayLocation)) == null)
                    {
                        color *= 0.5f;
                    }

                    float iconScale = location == currentDisplayLocation ? 1.2f : 1.0f;
                    if (location == HighlightedLocation) { iconScale *= 1.2f; }
                    if (isEndLocation) { iconScale *= 2.0f; }

                    float notificationPulseAmount = 1.0f;
                    float notificationColorLerp = 0.0f;
                    if (mapNotifications.Any(n => n.RelatedLocation == location && n.IsCurrentlyVisible))
                    {
                        float sin = MathF.Sin((float)Timing.TotalTime * 2.0f);
                        notificationPulseAmount = Math.Max(sin + 0.5f, 1.0f);
                        notificationColorLerp = (notificationPulseAmount - 1.0f) * 4.0f;
                        color = Color.Lerp(color, GUIStyle.Yellow, notificationColorLerp);
                        iconScale *= notificationPulseAmount;
                    }

                    locationSprite.Draw(spriteBatch, pos, color,
                        scale: generationParams.LocationIconSize / locationSprite.size.X * iconScale * zoom);

                    if (location.Faction != null)
                    {
                        float factionIconScale = iconScale * 0.7f;
                        Sprite factionIcon = location.Faction.Prefab.IconSmall ?? location.Faction.Prefab.Icon;
                        Color factionIconColor = Color.Lerp(color, location.Faction.Prefab.IconColor, notificationColorLerp);
                        factionIcon.Draw(spriteBatch, pos + new Vector2(-15, 15) * zoom, factionIconColor,
                            scale: generationParams.LocationIconSize / factionIcon.size.X * factionIconScale * zoom);
                    }

                    if (location == currentDisplayLocation)
                    {
                        if (SelectedLocation != null)
                        {
                            Vector2 dir = Vector2.Normalize(SelectedLocation.MapPosition - currLocationIndicatorPos);                                
                            GUI.Arrow.Draw(spriteBatch, 
                                rectCenter + (currLocationIndicatorPos + viewOffset) * zoom + dir * generationParams.LocationIconSize * 0.6f * zoom,
                                generationParams.IndicatorColor,
                                GUI.Arrow.Origin,
                                rotate: MathUtils.VectorToAngle(dir) + MathHelper.PiOver2,
                                new Vector2(0.5f, 1.0f) * zoom);
                        }  
                        generationParams.CurrentLocationIndicator.Draw(spriteBatch,
                            rectCenter + (currLocationIndicatorPos + viewOffset) * zoom,
                            generationParams.IndicatorColor,
                            generationParams.CurrentLocationIndicator.Origin, 0, Vector2.One * (generationParams.LocationIconSize / generationParams.CurrentLocationIndicator.size.X) * 0.8f * zoom);
                                                  
                    }

                    if (location == SelectedLocation)
                    {
                        generationParams.SelectedLocationIndicator.Draw(spriteBatch,
                            rectCenter + (location.MapPosition + viewOffset) * zoom,
                            generationParams.IndicatorColor,
                            generationParams.SelectedLocationIndicator.Origin, 0, Vector2.One * (generationParams.LocationIconSize / generationParams.SelectedLocationIndicator.size.X) * 1.7f * zoom);
                    }

                    if (location.TimeSinceLastTypeChange < 1 && !string.IsNullOrEmpty(location.LastTypeChangeMessage) && generationParams.TypeChangeIcon != null)
                    {
                        Vector2 typeChangeIconPos = pos + new Vector2(1.35f, -0.35f) * generationParams.LocationIconSize * 0.5f * zoom;
                        float typeChangeIconScale = 18.0f / generationParams.TypeChangeIcon.SourceRect.Width;
                        Color iconColor = GUIStyle.Red;
                        color = Color.Lerp(color, GUIStyle.Yellow, notificationColorLerp);
                        iconScale *= notificationPulseAmount;                        
                        generationParams.TypeChangeIcon.Draw(spriteBatch, typeChangeIconPos, iconColor, scale: typeChangeIconScale * zoom);
                        if (Vector2.Distance(PlayerInput.MousePosition, typeChangeIconPos) < generationParams.TypeChangeIcon.SourceRect.Width * zoom &&
                            (tooltip == null || IsPreferredTooltip(typeChangeIconPos)))
                        {
                            tooltip = (new Rectangle(typeChangeIconPos.ToPoint(), new Point(30)), RichString.Rich(location.LastTypeChangeMessage));
                        }
                    }
                    if (location != CurrentLocation && generationParams.MissionIcon != null)
                    {
                        if ((CurrentLocation == currentDisplayLocation && CurrentLocation.AvailableMissions.Any(m => m.Locations.Contains(location))) || 
                            location.AvailableMissions.Any(m => m.Locations[0] == m.Locations[1]))
                        {
                            Vector2 missionIconPos = pos + new Vector2(1.35f, 0.35f) * generationParams.LocationIconSize * 0.5f * zoom;
                            generationParams.MissionIcon.Draw(spriteBatch, missionIconPos, generationParams.IndicatorColor, scale: missionIconScale * zoom);
                            if (Vector2.Distance(PlayerInput.MousePosition, missionIconPos) < generationParams.MissionIcon.SourceRect.Width * zoom && IsPreferredTooltip(missionIconPos))
                            {
                                var availableMissions = CurrentLocation.AvailableMissions
                                    .Where(m => m.Locations.Contains(location))
                                    .Concat(location.AvailableMissions.Where(m => m.Locations[0] == m.Locations[1]))
                                    .Distinct();
                                tooltip = (new Rectangle(missionIconPos.ToPoint(), new Point(30)), TextManager.Get("mission") + '\n'+ string.Join('\n', availableMissions.Select(m => "- " + m.Name)));
                            }
                        }
                    }

                    if (GameMain.DebugDraw)
                    {
                        //move the debug texts upwards so they don't go under the info panel that appears when highlighted
                        Vector2 dPos = pos + new Vector2(15, -100);
                        if (location == HighlightedLocation)
                        {
                            GUI.DrawString(spriteBatch, dPos, "Faction: " + (location.Faction?.Prefab.Name ?? "none"), Color.White, Color.Black, font: GUIStyle.SubHeadingFont);
                            GUI.DrawString(spriteBatch, dPos + new Vector2(0, 18), "Secondary Faction: " + (location.SecondaryFaction?.Prefab.Name ?? "none"), Color.White, Color.Black, font: GUIStyle.SubHeadingFont);
                            dPos.Y += 50;

                            if (PlayerInput.KeyDown(Keys.LeftShift))
                            {
                                GUI.DrawString(spriteBatch, new Vector2(150, 150), "Dist: " +
                                    GetDistanceToClosestLocationOrConnection(CurrentLocation, int.MaxValue, loc => loc == location), Color.White, Color.Black, font: GUIStyle.SubHeadingFont);
                            }
                            GUI.DrawString(spriteBatch, dPos, $"Difficulty: {location.LevelData.Difficulty.FormatSingleDecimal()}",
                                ToolBox.GradientLerp(location.LevelData.Difficulty / 100.0f, GUIStyle.Blue, GUIStyle.Yellow, GUIStyle.Red), Color.Black * 0.8f, 4, font: GUIStyle.SmallFont);
                            
                            dPos.Y += 25;
                            GUI.DrawString(spriteBatch, dPos, $"Biome: {location.LevelData.Biome.DisplayName} ({location.LevelData.GenerationParams.Identifier})", Color.White, Color.Black, font: GUIStyle.SmallFont);
                        }
                    }
                }
            }

            DrawDecorativeHUD(spriteBatch, rect);

            bool drawRadiationTooltip = true;
            
            if (tooltip != null)
            {
                GUIComponent.DrawToolTip(spriteBatch, tooltip.Value.tip, tooltip.Value.targetArea);
                drawRadiationTooltip = false;
            }

            if (drawRadiationTooltip)
            {
                Radiation?.DrawFront(spriteBatch);
            }

            spriteBatch.End();
            GameMain.Instance.GraphicsDevice.ScissorRectangle = prevScissorRect;
            spriteBatch.Begin(SpriteSortMode.Deferred, samplerState: GUI.SamplerState, rasterizerState: GameMain.ScissorTestEnable);
        }

        public static void DrawNoise(SpriteBatch spriteBatch, Rectangle rect, float strength)
        {
            noiseOverlay ??= new Sprite("Content/UI/noise.png", Vector2.Zero);

            float noiseT = (float)(Timing.TotalTime * 0.01f);
            float noiseScale = (float)PerlinNoise.CalculatePerlin(noiseT * 5.0f, noiseT * 2.0f, 0) * 5.0f;

            float rawNoiseScale = 1.0f + GetPerlinNoise();

            noiseOverlay.DrawTiled(spriteBatch, rect.Location.ToVector2(), rect.Size.ToVector2(), 
                startOffset: new Vector2(Rand.Range(0.0f, noiseOverlay.SourceRect.Width), Rand.Range(0.0f, noiseOverlay.SourceRect.Height)),
                color : Color.White * strength * 0.1f,
                textureScale: Vector2.One * rawNoiseScale);

            noiseOverlay.DrawTiled(spriteBatch, rect.Location.ToVector2(), rect.Size.ToVector2(),
                startOffset: new Vector2(Rand.Range(0.0f, noiseOverlay.SourceRect.Width), Rand.Range(0.0f, noiseOverlay.SourceRect.Height)),
                color: new Color(20,20,20,50),
                textureScale: Vector2.One * rawNoiseScale * 2);

            noiseOverlay.DrawTiled(spriteBatch, Vector2.Zero, new Vector2(GameMain.GraphicsWidth, GameMain.GraphicsHeight),
                startOffset: new Vector2(Rand.Range(0.0f, noiseOverlay.SourceRect.Width), Rand.Range(0.0f, noiseOverlay.SourceRect.Height)),
                color: Color.White * strength * 0.1f,
                textureScale: Vector2.One * noiseScale);
        }

        private static float GetPerlinNoise() => PerlinNoise.GetPerlin((int)(Timing.TotalTime * 1 - 1), (int)(Timing.TotalTime * 1 - 1));

        private void DrawConnection(SpriteBatch spriteBatch, LocationConnection connection, Rectangle viewArea, Vector2 viewOffset, Location currentDisplayLocation, Color? overrideColor = null)
        {
            Color connectionColor;
            if (GameMain.DebugDraw)
            {
                float sizeFactor = MathUtils.InverseLerp(
                   generationParams.SmallLevelConnectionLength,
                   generationParams.LargeLevelConnectionLength,
                   connection.Length);
                connectionColor = ToolBox.GradientLerp(sizeFactor, Color.LightGreen, GUIStyle.Orange, GUIStyle.Red);
            }
            else if (overrideColor.HasValue)
            {
                connectionColor = overrideColor.Value;
            }
            else
            {
                connectionColor = connection.Passed ? generationParams.ConnectionColor : generationParams.UnvisitedConnectionColor;
            }

            int width = (int)(generationParams.LocationConnectionWidth * zoom);

            //current level
            if (Level.Loaded?.LevelData == connection.LevelData)
            {
                connectionColor = generationParams.HighlightedConnectionColor;
                width = (int)(width * 1.5f);
            }
            //selected connection
            if (SelectedLocation != currentDisplayLocation &&
                connection.Locations.Contains(SelectedLocation) && connection.Locations.Contains(currentDisplayLocation))
            {
                connectionColor = generationParams.HighlightedConnectionColor;
                width *= 2;
            }
            //highlighted connection
            else if (HighlightedLocation != currentDisplayLocation &&
                    connection.Locations.Contains(HighlightedLocation) && connection.Locations.Contains(currentDisplayLocation))
            {
                connectionColor = generationParams.HighlightedConnectionColor;
                width *= 2;
            }

            Vector2 rectCenter = viewArea.Center.ToVector2();

            int startIndex = connection.CrackSegments.Count > 2 ? 1 : 0;
            int endIndex = connection.CrackSegments.Count > 2 ? connection.CrackSegments.Count - 1 : connection.CrackSegments.Count;

            Vector2? connectionStart = null;
            Vector2? connectionEnd = null;
            for (int i = startIndex; i < endIndex; i++)
            {
                var segment = connection.CrackSegments[i];

                Vector2 start = rectCenter + (segment[0] + viewOffset) * zoom;
                if (!connectionStart.HasValue) { connectionStart = start; }
                Vector2 end =  rectCenter + (segment[1] + viewOffset) * zoom;
                connectionEnd = end;

                if (!viewArea.Contains(start) && !viewArea.Contains(end))
                {
                    continue;
                }
                else
                {
                    if (MathUtils.GetLineRectangleIntersection(start, end, new Rectangle(viewArea.X, viewArea.Y + viewArea.Height, viewArea.Width, viewArea.Height), out Vector2 intersection))
                    {
                        if (!viewArea.Contains(start))
                        {
                            start = intersection;
                        }
                        else
                        {
                            end = intersection;
                        }
                    }
                }

                float a = 1.0f;
                if (!connection.Locations[0].Visited && !connection.Locations[1].Visited)
                {
                    if (IsInFogOfWar(connection.Locations[0]))
                    {
                        a = (float)i / connection.CrackSegments.Count;
                    }
                    else if (IsInFogOfWar(connection.Locations[1]))
                    {
                        a = 1.0f - (float)i / connection.CrackSegments.Count;
                    }
                }
                float dist = Vector2.Distance(start, end);
                var connectionSprite = connection.Passed ? generationParams.PassedConnectionSprite : generationParams.ConnectionSprite;
                if (connectionSprite?.Texture == null) { continue; }

                Color segmentColor = connectionColor;
                int segmentWidth = width;
                if (connection == SelectedConnection)
                {
                    float t = (i - startIndex) / (float)(endIndex - startIndex - 1);
                    if (currentDisplayLocation == connection.Locations[1]) { t = 1.0f - t; }
                    if (t > connectionHighlightState) 
                    { 
                        segmentWidth /= 2; 
                        segmentColor = connection.Passed ? generationParams.ConnectionColor : generationParams.UnvisitedConnectionColor; 
                    }
                }

                spriteBatch.Draw(connectionSprite.Texture,
                    new Rectangle((int)start.X, (int)start.Y, (int)(dist - 1 * zoom), segmentWidth),
                    connectionSprite.SourceRect, segmentColor * a, 
                    MathUtils.VectorToAngle(end - start),
                    new Vector2(0, connectionSprite.size.Y / 2), SpriteEffects.None, 0.01f);
            }

            int iconCount = 0, iconIndex = 0;
            if (connectionStart.HasValue && connectionEnd.HasValue)
            {
                if (connection.LevelData.HasBeaconStation) { iconCount++; }
                if (connection.LevelData.HasHuntingGrounds) { iconCount++; }
                if (connection.Locked) { iconCount++; }
                string tooltip = null;

                float subCrushDepth = SubmarineInfo.GetSubCrushDepth(SubmarineSelection.CurrentOrPendingSubmarine(), ref pendingSubInfo);
                string crushDepthWarningIconStyle = null;

                var levelData = connection.LevelData;
                float spawnDepth =
                    levelData.InitialDepth +
                    //base the warning on the start or end position of the level, whichever is deeper
                    levelData.Size.Y * Math.Max(levelData.GenerationParams.StartPosition.Y, levelData.GenerationParams.EndPosition.Y);

                //"high warning" if the sub spawns at/below crush depth
                if (spawnDepth * Physics.DisplayToRealWorldRatio > subCrushDepth)
                {
                    iconCount++;
                    crushDepthWarningIconStyle = "CrushDepthWarningHighIcon";
                    tooltip = "crushdepthwarninghigh";
                }
                //"low warning" if the spawn position is less than the level's height away from crush depth
                //(i.e. the crush depth is pretty close to the spawn pos, possibly inside the level or at least close enough that many parts of the abyss are unreachable)
                else if ((spawnDepth + connection.LevelData.Size.Y) * Physics.DisplayToRealWorldRatio > subCrushDepth)
                {
                    iconCount++;
                    crushDepthWarningIconStyle = "CrushDepthWarningLowIcon";
                    tooltip = "crushdepthwarninglow";
                }

                if (connection.LevelData.HasBeaconStation)
                {
                    bool beaconActive =
                        connection.LevelData.IsBeaconActive ||
                        (Level.Loaded?.LevelData == connection.LevelData && Level.Loaded.CheckBeaconActive());
                    var beaconStationIconStyle = beaconActive ? "BeaconStationActive" : "BeaconStationInactive";
                    DrawIcon(beaconStationIconStyle, (int)(28 * zoom), beaconActive ? beaconStationActiveText : beaconStationInactiveText);
                }

                if (connection.Locked)
                {
                    var gateLocation = connection.Locations[0].IsGateBetweenBiomes ? connection.Locations[0] : connection.Locations[1];
                    var unlockEvent = EventPrefab.GetUnlockPathEvent(gateLocation.LevelData.Biome.Identifier, gateLocation.Faction);

                    if (unlockEvent != null)
                    {
                        Reputation unlockReputation = CurrentLocation.Reputation;
                        Faction unlockFaction = null;
                        if (!unlockEvent.Faction.IsEmpty)
                        {
                            unlockFaction = GameMain.GameSession.Campaign.Factions.Find(f => f.Prefab.Identifier == unlockEvent.Faction);
                            unlockReputation = unlockFaction?.Reputation;
                        }
                        if (unlockReputation != null)
                        {
                            DrawIcon(
                                "LockedLocationConnection", (int)(28 * zoom),
                                RichString.Rich(TextManager.GetWithVariables(unlockEvent.UnlockPathTooltip ?? "LockedPathTooltip",
                                  ("[requiredreputation]", Reputation.GetFormattedReputationText(MathUtils.InverseLerp(unlockReputation.MinReputation, unlockReputation.MaxReputation, unlockEvent.UnlockPathReputation), unlockEvent.UnlockPathReputation, addColorTags: true)),
                                  ("[currentreputation]", unlockReputation.GetFormattedReputationText(addColorTags: true)))));
                        }
                    }
                    else
                    {
                        DrawIcon("LockedLocationConnection", (int)(28 * zoom), TextManager.Get("LockedPathTooltip"));
                    }

                }

                if (connection.LevelData.HasHuntingGrounds)
                {
                    DrawIcon("HuntingGrounds", (int)(28 * zoom), RichString.Rich(TextManager.Get("HuntingGroundsTooltip")));
                }

                if (crushDepthWarningIconStyle != null)
                {
                    DrawIcon(crushDepthWarningIconStyle, (int)(32 * zoom), 
                        RichString.Rich(TextManager.GetWithVariables(tooltip,
                                ("[initialdepth]", $"‖color:gui.orange‖{(int)(connection.LevelData.InitialDepth * Physics.DisplayToRealWorldRatio)}‖end‖"),
                                ("[submarinecrushdepth]", $"‖color:gui.orange‖{(int)subCrushDepth}‖end‖"))));
                }
            }

            if (GameMain.DebugDraw && zoom > (1.0f * GUI.Scale) && generationParams.ShowLevelTypeNames)
            {
                Vector2 center = rectCenter + (connection.CenterPos + viewOffset) * zoom;
                if (viewArea.Contains(center) && connection.Biome != null)
                {
                    GUI.DrawString(spriteBatch, center - Vector2.UnitX * 50,
                        $"{(connection.LevelData?.GenerationParams?.Identifier ?? connection.Biome.Identifier)} ({connection.Difficulty.FormatSingleDecimal()})",
                        ToolBox.GradientLerp(connection.Difficulty / 100.0f, GUIStyle.Blue, GUIStyle.Yellow, GUIStyle.Red), backgroundColor: Color.Black * 0.7f, font: GUIStyle.SmallFont);
                }
            }

            void DrawIcon(string iconStyle, int iconSize, RichString tooltipText)
            {
                Vector2 iconPos = (connectionStart.Value + connectionEnd.Value) / 2;
                Vector2 iconDiff = Vector2.Normalize(connectionEnd.Value - connectionStart.Value) * iconSize;

                iconPos += (iconDiff * -(iconCount - 1) / 2.0f) + iconDiff * iconIndex;

                var style = GUIStyle.GetComponentStyle(iconStyle);
                bool mouseOn = Vector2.DistanceSquared(iconPos, PlayerInput.MousePosition) < iconSize * iconSize && IsPreferredTooltip(iconPos);
                Sprite iconSprite = style.GetDefaultSprite();
                iconSprite.Draw(spriteBatch, iconPos, (mouseOn ? style.HoverColor : style.Color) * 0.7f,
                    scale: iconSize / iconSprite.size.X);
                if (mouseOn)
                {
                    tooltip = (new Rectangle((iconPos - Vector2.One * iconSize / 2).ToPoint(), new Point(iconSize)), tooltipText);
                }
                iconIndex++;
            }
        }

        private bool IsPreferredTooltip(Vector2 tooltipPos)
        {
            return tooltip == null || Vector2.DistanceSquared(tooltipPos, PlayerInput.MousePosition) < Vector2.DistanceSquared(tooltip.Value.targetArea.Center.ToVector2(), PlayerInput.MousePosition);
        }

        private float hudVisibility;
        private float cameraNoiseStrength;

        private void DrawDecorativeHUD(SpriteBatch spriteBatch, Rectangle rect)
        {
            generationParams.DecorativeGraphSprite.Draw(spriteBatch, (int)((Timing.TotalTime * 5.0f) % generationParams.DecorativeGraphSprite.FrameCount),
                new Vector2(rect.X, rect.Bottom - (generationParams.DecorativeGraphSprite.FrameSize.Y + 30) * GUI.Scale), 
                Color.White, Vector2.Zero, 0, Vector2.One * GUI.Scale, SpriteEffects.FlipVertically);

            GUI.DrawString(spriteBatch,
                new Vector2(rect.Right - GUI.IntScale(170), rect.Y + GUI.IntScale(5)),
                "JOVIAN FLUX " + ((cameraNoiseStrength + Rand.Range(-0.02f, 0.02f)) * 500), generationParams.IndicatorColor * hudVisibility, font: GUIStyle.SmallFont);
            GUI.DrawString(spriteBatch,
                new Vector2(rect.X + GUI.IntScale(5), rect.Y + GUI.IntScale(5)),
                "LAT " + (-DrawOffset.Y / 100.0f) + "   LON " + (-DrawOffset.X / 100.0f), generationParams.IndicatorColor * hudVisibility, font: GUIStyle.SmallFont);
        }

        private void UpdateMapAnim(MapAnim anim, float deltaTime)
        {
            //pause animation while there are messageboxes (other than hints) on screen
            if (GUIMessageBox.MessageBoxes.Count(c => !(c is GUIMessageBox mb) || mb.MessageBoxType != GUIMessageBox.Type.Hint) > 0) { return; }

            if (!string.IsNullOrEmpty(anim.StartMessage))
            {
                new GUIMessageBox("", anim.StartMessage);
                anim.StartMessage = null;
                return;
            }

            float unscaledZoom = zoom / GUI.Scale;
            if (anim.StartZoom == null) { anim.StartZoom = MathUtils.InverseLerp(generationParams.MinZoom, generationParams.MaxZoom, unscaledZoom); }
            if (anim.EndZoom == null) { anim.EndZoom = MathUtils.InverseLerp(generationParams.MinZoom, generationParams.MaxZoom, unscaledZoom); }

            anim.StartPos = (anim.StartLocation == null) ? -DrawOffset : anim.StartLocation.MapPosition;

            anim.Timer = Math.Min(anim.Timer + deltaTime, anim.Duration);
            float t = anim.Duration <= 0.0f ? 1.0f : Math.Max(anim.Timer / anim.Duration, 0.0f);
            DrawOffset = -Vector2.SmoothStep(anim.StartPos.Value, anim.EndLocation.MapPosition, t);
            DrawOffset += new Vector2(
                (float)PerlinNoise.CalculatePerlin(Timing.TotalTime * 0.3f % 255, Timing.TotalTime * 0.4f % 255, 0) - 0.5f,
                (float)PerlinNoise.CalculatePerlin(Timing.TotalTime * 0.4f % 255, Timing.TotalTime * 0.3f % 255, 0.5f) - 0.5f) * 50.0f * (float)Math.Sin(t * MathHelper.Pi);

            zoom =
                MathHelper.Lerp(generationParams.MinZoom, generationParams.MaxZoom,
                    MathHelper.SmoothStep(anim.StartZoom.Value, anim.EndZoom.Value, t))
                        * GUI.Scale;

            if (anim.Timer >= anim.Duration)
            {
                if (!string.IsNullOrEmpty(anim.EndMessage))
                {
                    new GUIMessageBox("", anim.EndMessage);
                    anim.EndMessage = null;
                    return;
                }
                anim.Finished = true;
            }
        }

        /// <summary>
        /// Resets <see cref="pendingSubInfo"/> and forces crush depth to be calculated again for icon displaying purposes
        /// </summary>
        public void ResetPendingSub()
        {
            pendingSubInfo = new SubmarineInfo.PendingSubInfo();
        }

        partial void RemoveProjSpecific()
        {
            noiseOverlay?.Remove();
            noiseOverlay = null;
        }
    }
}

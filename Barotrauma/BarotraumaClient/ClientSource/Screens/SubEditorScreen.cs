using Barotrauma.Extensions;
using Barotrauma.IO;
using Barotrauma.Items.Components;
using Barotrauma.Steam;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using System.Xml.Linq;
using Barotrauma.Sounds;

namespace Barotrauma
{
    class SubEditorScreen : EditorScreen
    {
        public const int MaxStructures = 2000;
        public const int MaxWalls = 500;
        public const int MaxItems = 5000;
        public const int MaxLights = 600;
        public const int MaxShadowCastingLights = 100;

        private static Submarine MainSub
        {
            get => Submarine.MainSub;
            set => Submarine.MainSub = value;
        }

        private readonly record struct LayerData(bool IsVisible = true, bool IsGrouped = false);

        public enum Mode
        {
            Default,
            Wiring
        }

        public enum WarningType
        {
            NoWaypoints,
            NoHulls,
            DisconnectedVents,
            NoHumanSpawnpoints,
            NoCargoSpawnpoints,
            NoBallastTag,
            NonLinkedGaps,
            NoHiddenContainers,
            InsufficientFreeConnectionsWarning,
            StructureCount,
            WallCount,
            ItemCount,
            LightCount,
            ShadowCastingLightCount,
            WaterInHulls,
            LowOxygenOutputWarning,
            TooLargeForEndGame,
            NotEnoughContainers,
            NoSuitableBrainRooms
        }

        public static Vector2 MouseDragStart = Vector2.Zero;

        private readonly Point defaultPreviewImageSize = new Point(640, 368);

        private readonly Camera cam;
        private Vector2 camTargetFocus = Vector2.Zero;

        private SubmarineInfo backedUpSubInfo;

        private readonly HashSet<ulong> publishedWorkshopItemIds = new HashSet<ulong>();

        private Point screenResolution;

        private bool lightingEnabled;

        private bool wasSelectedBefore;

        public GUIComponent TopPanel;
        public GUIComponent showEntitiesPanel, entityCountPanel;
        private readonly List<GUITickBox> showEntitiesTickBoxes = new List<GUITickBox>();
        private readonly Dictionary<string, bool> hiddenSubCategories = new Dictionary<string, bool>();

        private GUITextBlock subNameLabel;

        public bool ShowThalamus { get; private set; } = true;

        private bool entityMenuOpen = true;
        private float entityMenuOpenState = 1.0f;
        private string lastFilter;
        public GUIComponent EntityMenu;
        private GUITextBox entityFilterBox;
        private GUIListBox categorizedEntityList, allEntityList;
        private GUIButton toggleEntityMenuButton;

        public GUIButton ToggleEntityMenuButton => toggleEntityMenuButton;

        private GUITickBox defaultModeTickBox, wiringModeTickBox;

        private GUIComponent loadFrame, saveFrame;

        private GUITextBox nameBox, descriptionBox;

        private GUIButton selectedCategoryButton;
        private GUITextBlock selectedCategoryText;
        private readonly List<GUIButton> entityCategoryButtons = new List<GUIButton>();
        private MapEntityCategory? selectedCategory;

        private GUIFrame hullVolumeFrame;

        private GUIFrame saveAssemblyFrame;
        private GUIFrame snapToGridFrame;

        const int PreviouslyUsedCount = 10;
        private GUIFrame previouslyUsedPanel;
        private GUIListBox previouslyUsedList;

        private GUIButton visibilityButton;
        private GUIFrame layerPanel;
        private GUIListBox layerList;
        private List<GUIButton> layerSpecificButtons = new List<GUIButton>();

        private GUIFrame undoBufferPanel;
        private GUIFrame undoBufferDisclaimer;
        private GUIListBox undoBufferList;

        private GUIDropDown linkedSubBox;

        private static GUIComponent autoSaveLabel;
        private static int MaxAutoSaves => GameSettings.CurrentConfig.MaxAutoSaves;

        public static readonly object ItemAddMutex = new object(), ItemRemoveMutex = new object();

        public static bool TransparentWiringMode = true;

        public static bool SkipInventorySlotUpdate;

        private static object bulkItemBufferinUse;

        public static object BulkItemBufferInUse
        {
            get => bulkItemBufferinUse;
            set
            {
                if (value != bulkItemBufferinUse && bulkItemBufferinUse != null)
                {
                    CommitBulkItemBuffer();
                }

                bulkItemBufferinUse = value;
            }
        }
        public static List<AddOrDeleteCommand> BulkItemBuffer = new List<AddOrDeleteCommand>();

        public static List<WarningType> SuppressedWarnings = new List<WarningType>();

        public static readonly EditorImageManager ImageManager = new EditorImageManager();

        public static bool ShouldDrawGrid = false;

        //a Character used for picking up and manipulating items
        private Character dummyCharacter;

        /// <summary>
        /// Prefab used for dragging from the item catalog into inventories
        /// <see cref="GUI.Draw"/>
        /// </summary>
        public static MapEntityPrefab DraggedItemPrefab;

        /// <summary>
        /// Currently opened hand-held item container like crates
        /// </summary>
        private Item OpenedItem;

        /// <summary>
        /// When opening an item we save the location of it so we can teleport the dummy character there
        /// </summary>
        private Vector2 oldItemPosition;

        /// <summary>
        /// Global undo/redo state for the sub editor and a selector index for it
        /// <see cref="Command"/>
        /// </summary>
        public static readonly List<Command> Commands = new List<Command>();
        private static int commandIndex;

        private GUIFrame wiringToolPanel;

        private Option<DateTime> editorSelectedTime;

        private GUIImage previewImage;
        private GUILayoutGroup previewImageButtonHolder;

        private const int submarineNameLimit = 30;
        private GUITextBlock submarineNameCharacterCount;

        private const int submarineDescriptionLimit = 500;
        private GUITextBlock submarineDescriptionCharacterCount;

        private Mode mode;

        private Vector2 MeasurePositionStart = Vector2.Zero;

        // Prevent the mode from changing
        private bool lockMode;

        private static bool isAutoSaving;

        private KeyOrMouse toggleEntityListBind; 

        public override Camera Cam => cam;

        public bool DrawCharacterInventory => dummyCharacter != null && WiringMode;

        public static XDocument AutoSaveInfo;
        private static readonly string autoSavePath = Path.Combine("Submarines", ".AutoSaves");
        private static readonly string autoSaveInfoPath = Path.Combine(autoSavePath, "autosaves.xml");

        private static string GetSubDescription()
        {
            if (MainSub?.Info != null)
            {
                LocalizedString localizedDescription = TextManager.Get($"submarine.description.{MainSub.Info.Name ?? ""}");
                if (!localizedDescription.IsNullOrEmpty()) { return localizedDescription.Value; }
                return MainSub.Info.Description?.Value ?? "";
            }
            return "";
        }

        private static LocalizedString GetTotalHullVolume()
        {
            return $"{TextManager.Get("TotalHullVolume")}:\n{Hull.HullList.Sum(h => h.Volume)}";
        }

        private static LocalizedString GetSelectedHullVolume()
        {
            float buoyancyVol = 0.0f;
            float selectedVol = 0.0f;
            float neutralPercentage = SubmarineBody.NeutralBallastPercentage;
            Hull.HullList.ForEach(h =>
            {
                buoyancyVol += h.Volume;
                if (h.IsSelected)
                {
                    selectedVol += h.Volume;
                }
            });
            buoyancyVol *= neutralPercentage;
            string retVal = $"{TextManager.Get("SelectedHullVolume")}:\n{selectedVol}";
            if (selectedVol > 0.0f && buoyancyVol > 0.0f)
            {
                if (buoyancyVol / selectedVol < 1.0f)
                {
                    retVal += $" ({TextManager.GetWithVariable("OptimalBallastLevel", "[value]", (buoyancyVol / selectedVol).ToString("0.0000"))})";
                }
                else
                {
                    retVal += $" ({TextManager.Get("InsufficientBallast")})";
                }
            }
            return retVal;
        }

        public bool WiringMode => mode == Mode.Wiring;

        private static readonly Dictionary<string, LayerData> Layers = new Dictionary<string, LayerData>();

        public SubEditorScreen()
        {
            cam = new Camera
            {
                MaxZoom = 10f
            };
            WayPoint.ShowWayPoints = false;
            WayPoint.ShowSpawnPoints = false;
            Hull.ShowHulls = false;
            Gap.ShowGaps = false;
            CreateUI();
        }

        private void CreateUI()
        {
            TopPanel = new GUIFrame(new RectTransform(new Vector2(GUI.Canvas.RelativeSize.X, 0.01f), GUI.Canvas) { MinSize = new Point(0, 35) }, "GUIFrameTop");

            GUILayoutGroup paddedTopPanel = new GUILayoutGroup(new RectTransform(new Vector2(0.98f, 0.8f), TopPanel.RectTransform, Anchor.Center),
                isHorizontal: true, childAnchor: Anchor.CenterLeft)
            {
                RelativeSpacing = 0.005f
            };

            new GUIButton(new RectTransform(new Vector2(0.9f, 0.9f), paddedTopPanel.RectTransform, scaleBasis: ScaleBasis.BothHeight), style: "GUIButtonToggleLeft")
            {
                ToolTip = TextManager.Get("back"),
                OnClicked = (b, d) =>
                {
                    var msgBox = new GUIMessageBox("", TextManager.Get("PauseMenuQuitVerificationEditor"), new[] { TextManager.Get("Yes"), TextManager.Get("Cancel") })
                    {
                        UserData = "verificationprompt"
                    };
                    msgBox.Buttons[0].OnClicked = (yesBtn, userdata) =>
                    {
                        GUIMessageBox.CloseAll();
                        GameMain.MainMenuScreen.Select();
                        return true;
                    };
                    msgBox.Buttons[0].OnClicked += msgBox.Close;
                    msgBox.Buttons[1].OnClicked = (_, userdata) =>
                    {
                        msgBox.Close();
                        return true;
                    };
                    return true;
                }
            };

            new GUIFrame(new RectTransform(new Vector2(0.01f, 0.9f), paddedTopPanel.RectTransform), style: "VerticalLine");

            new GUIButton(new RectTransform(new Vector2(0.9f, 0.9f), paddedTopPanel.RectTransform, scaleBasis: ScaleBasis.BothHeight), style: "OpenButton")
            {
                ToolTip = TextManager.Get("OpenSubButton"),
                OnClicked = (btn, data) =>
                {
                    saveFrame = null;
                    CreateLoadScreen();

                    return true;
                }
            };

            new GUIFrame(new RectTransform(new Vector2(0.01f, 0.9f), paddedTopPanel.RectTransform), style: "VerticalLine");

            new GUIButton(new RectTransform(new Vector2(0.9f, 0.9f), paddedTopPanel.RectTransform, scaleBasis: ScaleBasis.BothHeight), style: "SaveButton")
            {
                ToolTip = RichString.Rich(TextManager.Get("SaveSubButton") + "‖color:125,125,125‖\nCtrl + S‖color:end‖"),
                OnClicked = (btn, data) =>
                {
#if DEBUG
                    if (ContentPackageManager.EnabledPackages.All.Any(cp => cp != ContentPackageManager.VanillaCorePackage && cp.Files.Any(f => f is not BaseSubFile)))
                    {
                        var msgBox = new GUIMessageBox("DEBUG-ONLY WARNING", "You currently have some mods enabled. Are you sure you want to save the submarine? If the mods override any vanilla content, saving the submarine may cause unintended changes.",
                            new LocalizedString[] { "Yes, I know what I'm doing", "Cancel" });
                        msgBox.Buttons[0].OnClicked = (btn, data) => 
                        {
                            msgBox.Close();
                            loadFrame = null;
                            CreateSaveScreen();
                            return true;
                        };
                        msgBox.Buttons[1].OnClicked += msgBox.Close;
                        return false;
                    }
#endif
                    loadFrame = null;
                    CreateSaveScreen();
                    return true;
                }
            };

            new GUIFrame(new RectTransform(new Vector2(0.01f, 0.9f), paddedTopPanel.RectTransform), style: "VerticalLine");

            new GUIButton(new RectTransform(new Vector2(0.9f, 0.9f), paddedTopPanel.RectTransform, scaleBasis: ScaleBasis.BothHeight), style: "TestButton")
            {
                ToolTip = TextManager.Get("TestSubButton"),
                OnClicked = TestSubmarine
            };

            new GUIFrame(new RectTransform(new Vector2(0.01f, 0.9f), paddedTopPanel.RectTransform), style: "VerticalLine");

            visibilityButton = new GUIButton(new RectTransform(new Vector2(0.9f, 0.9f), paddedTopPanel.RectTransform, scaleBasis: ScaleBasis.BothHeight), "", style: "SetupVisibilityButton")
            {
                ToolTip = TextManager.Get("SubEditorVisibilityButton") + '\n' + TextManager.Get("SubEditorVisibilityToolTip"),
                OnClicked = (btn, userData) =>
                {
                    previouslyUsedPanel.Visible = false;
                    undoBufferPanel.Visible = false;
                    layerPanel.Visible = false;
                    showEntitiesPanel.Visible = !showEntitiesPanel.Visible;
                    showEntitiesPanel.RectTransform.AbsoluteOffset = new Point(Math.Max(Math.Max(btn.Rect.X, entityCountPanel.Rect.Right), saveAssemblyFrame.Rect.Right), TopPanel.Rect.Height);
                    return true;
                }
            };

            new GUIButton(new RectTransform(new Vector2(0.9f, 0.9f), paddedTopPanel.RectTransform, scaleBasis: ScaleBasis.BothHeight), "", style: "EditorLayerButton")
            {
                ToolTip = TextManager.Get("editor.layer.button") + '\n' + TextManager.Get("editor.layer.tooltip"),
                OnClicked = (btn, userData) =>
                {
                    previouslyUsedPanel.Visible = false;
                    showEntitiesPanel.Visible = false;
                    undoBufferPanel.Visible = false;
                    layerPanel.Visible = !layerPanel.Visible;
                    layerPanel.RectTransform.AbsoluteOffset = new Point(Math.Max(Math.Max(btn.Rect.X, entityCountPanel.Rect.Right), saveAssemblyFrame.Rect.Right), TopPanel.Rect.Height);
                    return true;
                }
            };

            var previouslyUsedButton = new GUIButton(new RectTransform(new Vector2(0.9f, 0.9f), paddedTopPanel.RectTransform, scaleBasis: ScaleBasis.BothHeight), "", style: "RecentlyUsedButton")
            {
                ToolTip = TextManager.Get("PreviouslyUsedLabel"),
                OnClicked = (btn, userData) =>
                {
                    showEntitiesPanel.Visible = false;
                    undoBufferPanel.Visible = false;
                    layerPanel.Visible = false;
                    previouslyUsedPanel.Visible = !previouslyUsedPanel.Visible;
                    previouslyUsedPanel.RectTransform.AbsoluteOffset = new Point(Math.Max(Math.Max(btn.Rect.X, entityCountPanel.Rect.Right), saveAssemblyFrame.Rect.Right), TopPanel.Rect.Height);
                    return true;
                }
            };

            var undoBufferButton = new GUIButton(new RectTransform(new Vector2(0.9f, 0.9f), paddedTopPanel.RectTransform, scaleBasis: ScaleBasis.BothHeight), "", style: "UndoHistoryButton")
            {
                ToolTip = TextManager.Get("Editor.UndoHistoryButton"),
                OnClicked = (btn, userData) =>
                {
                    showEntitiesPanel.Visible = false;
                    previouslyUsedPanel.Visible = false;
                    layerPanel.Visible = false;
                    undoBufferPanel.Visible = !undoBufferPanel.Visible;
                    undoBufferPanel.RectTransform.AbsoluteOffset = new Point(Math.Max(Math.Max(btn.Rect.X, entityCountPanel.Rect.Right), saveAssemblyFrame.Rect.Right), TopPanel.Rect.Height);
                    return true;
                }
            };

            new GUIFrame(new RectTransform(new Vector2(0.01f, 0.9f), paddedTopPanel.RectTransform), style: "VerticalLine");

            subNameLabel = new GUITextBlock(new RectTransform(new Vector2(0.3f, 0.9f), paddedTopPanel.RectTransform, Anchor.CenterLeft),
                TextManager.Get("unspecifiedsubfilename"), font: GUIStyle.LargeFont, textAlignment: Alignment.CenterLeft);

            linkedSubBox = new GUIDropDown(new RectTransform(new Vector2(0.15f, 0.9f), paddedTopPanel.RectTransform),
                TextManager.Get("AddSubButton"), elementCount: 20)
            {
                ToolTip = TextManager.Get("AddSubToolTip")
            };

            List<(string Name, SubmarineInfo Sub)> subs = new List<(string Name, SubmarineInfo Sub)>();

            foreach (SubmarineInfo sub in SubmarineInfo.SavedSubmarines)
            {
                if (sub.Type != SubmarineType.Player) { continue; }
                subs.Add((sub.Name, sub));
            }

            foreach (var (name, sub) in subs.OrderBy(tuple => tuple.Name))
            {
                linkedSubBox.AddItem(name, sub);
            }

            linkedSubBox.OnSelected += SelectLinkedSub;
            linkedSubBox.OnDropped += (component, obj) =>
            {
                MapEntity.SelectedList.Clear();
                return true;
            };

            var spacing = new GUIFrame(new RectTransform(new Vector2(0.02f, 1.0f), paddedTopPanel.RectTransform), style: null);
            new GUIFrame(new RectTransform(new Vector2(0.1f, 0.9f), spacing.RectTransform, Anchor.Center), style: "VerticalLine");

            defaultModeTickBox = new GUITickBox(new RectTransform(new Vector2(0.9f, 0.9f), paddedTopPanel.RectTransform, scaleBasis: ScaleBasis.BothHeight), "", style: "EditSubButton")
            {
                ToolTip = RichString.Rich(TextManager.Get("SubEditorEditingMode")　+ "‖color:125,125,125‖\nCtrl + 1‖color:end‖"),
                OnSelected = tBox =>
                {
                    if (!lockMode)
                    {
                        if (tBox.Selected) { SetMode(Mode.Default); }

                        return true;
                    }

                    return false;
                }
            };

            wiringModeTickBox = new GUITickBox(new RectTransform(new Vector2(0.9f, 0.9f), paddedTopPanel.RectTransform, scaleBasis: ScaleBasis.BothHeight), "", style: "WiringModeButton")
            {
                ToolTip = RichString.Rich(TextManager.Get("WiringModeButton") + '\n' + TextManager.Get("WiringModeToolTip") + "‖color:125,125,125‖\nCtrl + 2‖color:end‖"),
                OnSelected = tBox =>
                {
                    if (!lockMode)
                    {
                        SetMode(tBox.Selected ? Mode.Wiring : Mode.Default);
                        return true;
                    }

                    return false;
                }
            };

            spacing = new GUIFrame(new RectTransform(new Vector2(0.02f, 1.0f), paddedTopPanel.RectTransform), style: null);
            new GUIFrame(new RectTransform(new Vector2(0.1f, 0.9f), spacing.RectTransform, Anchor.Center), style: "VerticalLine");

            new GUIButton(new RectTransform(new Vector2(0.9f, 0.9f), paddedTopPanel.RectTransform, scaleBasis: ScaleBasis.BothHeight), "", style: "GenerateWaypointsButton")
            {
                ToolTip = TextManager.Get("GenerateWaypointsButton") + '\n' + TextManager.Get("GenerateWaypointsToolTip"),
                OnClicked = (btn, userdata) =>
                {
                    if (WayPoint.WayPointList.Any())
                    {
                        var generateWaypointsVerification = new GUIMessageBox("", TextManager.Get("generatewaypointsverification"), new[] { TextManager.Get("ok"), TextManager.Get("cancel") });
                        generateWaypointsVerification.Buttons[0].OnClicked = delegate
                        {
                            if (GenerateWaypoints())
                            {
                                GUI.AddMessage(TextManager.Get("waypointsgeneratedsuccesfully"), GUIStyle.Green);
                            }
                            WayPoint.ShowWayPoints = true;
                            var matchingTickBox = showEntitiesTickBoxes?.Find(tb => tb.UserData as string == "waypoint");
                            if (matchingTickBox != null)
                            {
                                matchingTickBox.Selected = true;
                            }
                            generateWaypointsVerification.Close();
                            return true;
                        };
                        generateWaypointsVerification.Buttons[1].OnClicked = generateWaypointsVerification.Close;
                    }
                    else
                    {
                        if (GenerateWaypoints())
                        {
                            GUI.AddMessage(TextManager.Get("waypointsgeneratedsuccesfully"), GUIStyle.Green);
                        }
                        WayPoint.ShowWayPoints = true;

                    }
                    return true;
                }
            };

            spacing = new GUIFrame(new RectTransform(new Vector2(0.02f, 1.0f), paddedTopPanel.RectTransform), style: null);

            var selectedLayerText = new GUITextBlock(new RectTransform(new Vector2(0.15f, 1.0f), paddedTopPanel.RectTransform),
                string.Empty, textAlignment: Alignment.Center);
            selectedLayerText.TextGetter = () =>
            {
                string selectedLayer = layerList.SelectedData as string;
                if (selectedLayer != prevSelectedLayer)
                {
                    prevSelectedLayer = selectedLayer;
                    return selectedLayer.IsNullOrEmpty() ? string.Empty : TextManager.GetWithVariable("editor.layer.editinglayer", "[layer]", selectedLayer);
                }
                return selectedLayerText.Text;
            };

            TopPanel.RectTransform.MinSize = new Point(0, (int)(paddedTopPanel.RectTransform.Children.Max(c => c.MinSize.Y) / paddedTopPanel.RectTransform.RelativeSize.Y));
            paddedTopPanel.Recalculate();

            //-----------------------------------------------

            previouslyUsedPanel = new GUIFrame(new RectTransform(new Vector2(0.1f, 0.2f), GUI.Canvas) { MinSize = new Point(200, 200) })
            {
                Visible = false
            };
            previouslyUsedList = new GUIListBox(new RectTransform(new Vector2(0.9f, 0.9f), previouslyUsedPanel.RectTransform, Anchor.Center))
            {
                PlaySoundOnSelect = true,
                ScrollBarVisible = true,
                OnSelected = SelectPrefab
            };

            //-----------------------------------------------

            layerPanel = new GUIFrame(new RectTransform(new Vector2(0.25f, 0.4f), GUI.Canvas, minSize: new Point(300, 320)))
            {
                Visible = false
            };

            GUILayoutGroup layerGroup = new GUILayoutGroup(new RectTransform(new Vector2(0.9f), layerPanel.RectTransform, anchor: Anchor.Center));

            layerList = new GUIListBox(new RectTransform(new Vector2(1f, 0.8f), layerGroup.RectTransform))
            {
                ScrollBarVisible = true,
                AutoHideScrollBar = false,
                OnSelected = (component, userdata) =>
                {
                    //toggling selection is not how listboxes normally work, need to do that manually here
                    SoundPlayer.PlayUISound(GUISoundType.Select);
                    if (layerList.SelectedData == userdata)
                    {
                        layerSpecificButtons.ForEach(btn => btn.Enabled = false);
                        layerList.Deselect();
                        return false;
                    }
                    else
                    {
                        layerSpecificButtons.ForEach(btn => btn.Enabled = true);
                        return true;
                    }
                }
            };

            GUILayoutGroup layerButtonGroup = new GUILayoutGroup(new RectTransform(new Vector2(1f, 0.2f), layerGroup.RectTransform));
            GUILayoutGroup layerButtonTopGroup = new GUILayoutGroup(new RectTransform(new Vector2(1f, 0.5f), layerButtonGroup.RectTransform), isHorizontal: true);
            GUILayoutGroup layerButtonBottomGroup = new GUILayoutGroup(new RectTransform(new Vector2(1f, 0.5f), layerButtonGroup.RectTransform), isHorizontal: true);

            GUIButton layerAddButton = new GUIButton(new RectTransform(new Vector2(0.5f, 1f), layerButtonTopGroup.RectTransform), text: TextManager.Get("editor.layer.newlayer"), style: "GUIButtonFreeScale")
            {
                OnClicked = (button, o) =>
                {
                    CreateNewLayer(null, MapEntity.SelectedList.ToList());
                    return true;
                }
            };

            GUIButton layerDeleteButton = new GUIButton(new RectTransform(new Vector2(0.5f, 1f), layerButtonTopGroup.RectTransform), text: TextManager.Get("editor.layer.deletelayer"), style: "GUIButtonFreeScale")
            {
                Enabled = false,
                OnClicked = (button, o) =>
                {
                    if (layerList.SelectedData is string layer)
                    {
                        RenameLayer(layer, null);
                    }
                    return true;
                }
            }; 
            layerSpecificButtons.Add(layerDeleteButton);

            GUIButton layerRenameButton = new GUIButton(new RectTransform(new Vector2(0.5f, 1.0f), layerButtonBottomGroup.RectTransform), text: TextManager.Get("editor.layer.renamelayer"), style: "GUIButtonFreeScale")
            {
                Enabled = false,
                OnClicked = (button, o) =>
                {
                    if (layerList.SelectedData is string layer)
                    {
                        GUI.PromptTextInput(TextManager.Get("editor.layer.renamelayer"), layer, newName =>
                        {
                            RenameLayer(layer, newName);
                        });
                    }
                    return true;
                }
            };
            layerSpecificButtons.Add(layerRenameButton);

            GUIButton selectLayerButton = new GUIButton(new RectTransform(new Vector2(0.5f, 1.0f), layerButtonBottomGroup.RectTransform), text: TextManager.Get("editor.layer.selectlayer"), style: "GUIButtonFreeScale")
            {
                Enabled = false,
                OnClicked = (button, o) =>
                {
                    if (layerList.SelectedData is string layer)
                    {
                        foreach (MapEntity entity in MapEntity.MapEntityList.Where(me => !me.Removed && me.Layer == layer))
                        {
                            if (entity.IsSelected) { continue; }
                            MapEntity.SelectedList.Add(entity);
                        }
                    }
                    return true;
                }
            };
            layerSpecificButtons.Add(selectLayerButton);

            GUITextBlock.AutoScaleAndNormalize(layerAddButton.TextBlock, layerDeleteButton.TextBlock, layerRenameButton.TextBlock, selectLayerButton.TextBlock);

            Vector2 subPanelSize = new Vector2(0.925f, 0.9f);

            undoBufferPanel = new GUIFrame(new RectTransform(new Vector2(0.15f, 0.2f), GUI.Canvas) { MinSize = new Point(200, 200) })
            {
                Visible = false
            };

            undoBufferList = new GUIListBox(new RectTransform(subPanelSize, undoBufferPanel.RectTransform, Anchor.Center))
            {
                PlaySoundOnSelect = true,
                ScrollBarVisible = true,
                OnSelected = (_, userData) =>
                {
                    int index;
                    if (userData is Command command)
                    {
                        index = Commands.IndexOf(command);
                    }
                    else
                    {
                        index = -1;
                    }

                    int diff = index- commandIndex;
                    int amount = Math.Abs(diff);

                    if (diff >= 0)
                    {
                        Redo(amount + 1);
                    }
                    else
                    {
                        Undo(amount - 1);
                    }

                    return true;
                }
            };

            undoBufferDisclaimer = new GUIFrame(new RectTransform(subPanelSize, undoBufferPanel.RectTransform, Anchor.Center), style: null)
            {
                Color = Color.Black,
                Visible = false
            };
            new GUITextBlock(new RectTransform(Vector2.One, undoBufferDisclaimer.RectTransform, Anchor.Center), text: TextManager.Get("editor.undounavailable"), textAlignment: Alignment.Center, wrap: true, font: GUIStyle.SubHeadingFont)
            {
                TextColor = GUIStyle.Orange
            };

            UpdateUndoHistoryPanel();

            //-----------------------------------------------

            showEntitiesPanel = new GUIFrame(new RectTransform(new Vector2(0.15f, 0.5f), GUI.Canvas)
            {
                MinSize = new Point(190, 0)
            })
            {
                Visible = false
            };

            GUILayoutGroup paddedShowEntitiesPanel = new GUILayoutGroup(new RectTransform(new Vector2(0.95f, 0.98f), showEntitiesPanel.RectTransform, Anchor.Center))
            {
                Stretch = true
            };

            var tickBox = new GUITickBox(new RectTransform(new Vector2(1.0f, 0.1f), paddedShowEntitiesPanel.RectTransform), TextManager.Get("ShowLighting"))
            {
                UserData = "lighting",
                Selected = lightingEnabled,
                OnSelected = (GUITickBox obj) =>
                {
                    lightingEnabled = obj.Selected;
                    return true;
                }
            };
            new GUITickBox(new RectTransform(new Vector2(1.0f, 0.1f), paddedShowEntitiesPanel.RectTransform), TextManager.Get("ShowWalls"))
            {
                UserData = "wall",
                Selected = Structure.ShowWalls,
                OnSelected = (GUITickBox obj) => { Structure.ShowWalls = obj.Selected; return true; }
            };
            new GUITickBox(new RectTransform(new Vector2(1.0f, 0.1f), paddedShowEntitiesPanel.RectTransform), TextManager.Get("ShowStructures"))
            {
                UserData = "structure",
                Selected = Structure.ShowStructures,
                OnSelected = (GUITickBox obj) => { Structure.ShowStructures = obj.Selected; return true; }
            };
            new GUITickBox(new RectTransform(new Vector2(1.0f, 0.1f), paddedShowEntitiesPanel.RectTransform), TextManager.Get("ShowItems"))
            {
                UserData = "item",
                Selected = Item.ShowItems,
                OnSelected = (GUITickBox obj) => { Item.ShowItems = obj.Selected; return true; }
            };
            new GUITickBox(new RectTransform(new Vector2(1.0f, 0.1f), paddedShowEntitiesPanel.RectTransform), TextManager.Get("ShowWires"))
            {
                UserData = "wire",
                Selected = Item.ShowWires,
                OnSelected = (GUITickBox obj) => { Item.ShowWires = obj.Selected; return true; }
            };
            new GUITickBox(new RectTransform(new Vector2(1.0f, 0.1f), paddedShowEntitiesPanel.RectTransform), TextManager.Get("ShowWaypoints"))
            {
                UserData = "waypoint",
                Selected = WayPoint.ShowWayPoints,
                OnSelected = (GUITickBox obj) => { WayPoint.ShowWayPoints = obj.Selected; return true; }
            };
            new GUITickBox(new RectTransform(new Vector2(1.0f, 0.1f), paddedShowEntitiesPanel.RectTransform), TextManager.Get("ShowSpawnpoints"))
            {
                UserData = "spawnpoint",
                Selected = WayPoint.ShowSpawnPoints,
                OnSelected = (GUITickBox obj) => { WayPoint.ShowSpawnPoints = obj.Selected; return true; }
            };
            new GUITickBox(new RectTransform(new Vector2(1.0f, 0.1f), paddedShowEntitiesPanel.RectTransform), TextManager.Get("ShowLinks"))
            {
                UserData = "link",
                Selected = Item.ShowLinks,
                OnSelected = (GUITickBox obj) => { Item.ShowLinks = obj.Selected; return true; }
            };
            new GUITickBox(new RectTransform(new Vector2(1.0f, 0.1f), paddedShowEntitiesPanel.RectTransform), TextManager.Get("ShowHulls"))
            {
                UserData = "hull",
                Selected = Hull.ShowHulls,
                OnSelected = (GUITickBox obj) => { Hull.ShowHulls = obj.Selected; return true; }
            };
            new GUITickBox(new RectTransform(new Vector2(1.0f, 0.1f), paddedShowEntitiesPanel.RectTransform), TextManager.Get("ShowGaps"))
            {
                UserData = "gap",
                Selected = Gap.ShowGaps,
                OnSelected = (GUITickBox obj) => { Gap.ShowGaps = obj.Selected; return true; },
            };
            showEntitiesTickBoxes.AddRange(paddedShowEntitiesPanel.Children.Select(c => c as GUITickBox));

            var subcategoryHeader = new GUITextBlock(new RectTransform(new Vector2(1.0f, 0.0f), paddedShowEntitiesPanel.RectTransform), TextManager.Get("subcategories"), font: GUIStyle.SubHeadingFont);
            subcategoryHeader.RectTransform.MinSize = new Point(0, (int)(subcategoryHeader.Rect.Height * 1.5f));

            var subcategoryList = new GUIListBox(new RectTransform(new Vector2(1.0f, 0.1f), paddedShowEntitiesPanel.RectTransform) { MinSize = new Point(0, showEntitiesPanel.Rect.Height / 3) });
            List<string> availableSubcategories = new List<string>();
            foreach (var prefab in MapEntityPrefab.List)
            {
                if (!string.IsNullOrEmpty(prefab.Subcategory) && !availableSubcategories.Contains(prefab.Subcategory))
                {
                    availableSubcategories.Add(prefab.Subcategory);
                }
            }
            foreach (string subcategory in availableSubcategories)
            {
                var tb = new GUITickBox(new RectTransform(new Vector2(1.0f, 0.15f), subcategoryList.Content.RectTransform),
                    TextManager.Get("subcategory." + subcategory).Fallback(subcategory), font: GUIStyle.SmallFont)
                {
                    UserData = subcategory,
                    Selected = !IsSubcategoryHidden(subcategory),
                    OnSelected = (GUITickBox obj) => { hiddenSubCategories[(string)obj.UserData] = !obj.Selected; return true; },
                };
                tb.TextBlock.Wrap = true;
            }

            GUITextBlock.AutoScaleAndNormalize(subcategoryList.Content.Children.Where(c => c is GUITickBox).Select(c => ((GUITickBox)c).TextBlock));
            foreach (GUIComponent child in subcategoryList.Content.Children)
            {
                if (child is GUITickBox tb && tb.TextBlock.TextSize.X > tb.TextBlock.Rect.Width * 1.25f)
                {
                    tb.ToolTip = tb.Text;
                    tb.Text = ToolBox.LimitString(tb.Text.Value, tb.Font, (int)(tb.TextBlock.Rect.Width * 1.25f));
                }
            }

            showEntitiesPanel.RectTransform.NonScaledSize =
                new Point(
                    (int)Math.Max(showEntitiesPanel.RectTransform.NonScaledSize.X, paddedShowEntitiesPanel.RectTransform.Children.Max(c => (int)((c.GUIComponent as GUITickBox)?.TextBlock.TextSize.X ?? 0)) / paddedShowEntitiesPanel.RectTransform.RelativeSize.X),
                    (int)(paddedShowEntitiesPanel.RectTransform.Children.Sum(c => c.MinSize.Y) / paddedShowEntitiesPanel.RectTransform.RelativeSize.Y));
            GUITextBlock.AutoScaleAndNormalize(paddedShowEntitiesPanel.Children.Where(c => c is GUITickBox).Select(c => ((GUITickBox)c).TextBlock));

            //-----------------------------------------------

            float longestTextWidth = GUIStyle.SmallFont.MeasureString(TextManager.Get("SubEditorShadowCastingLights")).X;
            entityCountPanel = new GUIFrame(new RectTransform(new Vector2(0.08f, 0.5f), GUI.Canvas)
            {
                MinSize = new Point(Math.Max(170, (int)(longestTextWidth * 1.5f)), 0),
                AbsoluteOffset = new Point(0, TopPanel.Rect.Height)
            });

            GUILayoutGroup paddedEntityCountPanel = new GUILayoutGroup(new RectTransform(new Vector2(0.95f, 0.95f), entityCountPanel.RectTransform, Anchor.Center))
            {
                Stretch = true,
                AbsoluteSpacing = (int)(GUI.Scale * 4)
            };

            var itemCountText = new GUITextBlock(new RectTransform(new Vector2(0.75f, 0.0f), paddedEntityCountPanel.RectTransform), TextManager.Get("Items"),
                textAlignment: Alignment.CenterLeft, font: GUIStyle.SmallFont);
            var itemCount = new GUITextBlock(new RectTransform(new Vector2(0.33f, 1.0f), itemCountText.RectTransform, Anchor.TopRight, Pivot.TopLeft), "", textAlignment: Alignment.CenterRight);
            itemCount.TextGetter = () =>
            {
                int count = Item.ItemList.Count;
                if (dummyCharacter?.Inventory != null)
                {
                    count -= dummyCharacter.Inventory.AllItems.Count();
                }
                itemCount.TextColor = count > MaxItems ? GUIStyle.Red : Color.Lerp(GUIStyle.Green, GUIStyle.Orange, count / (float)MaxItems);
                return count.ToString();
            };

            var structureCountText = new GUITextBlock(new RectTransform(new Vector2(0.75f, 0.0f), paddedEntityCountPanel.RectTransform), TextManager.Get("Structures"),
                textAlignment: Alignment.CenterLeft, font: GUIStyle.SmallFont);
            var structureCount = new GUITextBlock(new RectTransform(new Vector2(0.33f, 1.0f), structureCountText.RectTransform, Anchor.TopRight, Pivot.TopLeft), "", textAlignment: Alignment.CenterRight);
            structureCount.TextGetter = () =>
            {
                int count = MapEntity.MapEntityList.Count - Item.ItemList.Count - Hull.HullList.Count - WayPoint.WayPointList.Count - Gap.GapList.Count;
                structureCount.TextColor = count > MaxStructures ? GUIStyle.Red : Color.Lerp(GUIStyle.Green, GUIStyle.Orange, count / (float)MaxStructures);
                return count.ToString();
            };

            var wallCountText = new GUITextBlock(new RectTransform(new Vector2(0.75f, 0.0f), paddedEntityCountPanel.RectTransform), TextManager.Get("Walls"),
                textAlignment: Alignment.CenterLeft, font: GUIStyle.SmallFont);
            var wallCount = new GUITextBlock(new RectTransform(new Vector2(0.33f, 1.0f), wallCountText.RectTransform, Anchor.TopRight, Pivot.TopLeft), "", textAlignment: Alignment.CenterRight);
            wallCount.TextGetter = () =>
            {
                wallCount.TextColor = Structure.WallList.Count > MaxWalls ? GUIStyle.Red : Color.Lerp(GUIStyle.Green, GUIStyle.Orange, Structure.WallList.Count / (float)MaxWalls);
                return Structure.WallList.Count.ToString();
            };

            var lightCountLabel = new GUITextBlock(new RectTransform(new Vector2(0.75f, 0.0f), paddedEntityCountPanel.RectTransform), TextManager.Get("SubEditorLights"),
                textAlignment: Alignment.CenterLeft, font: GUIStyle.SmallFont);
            var lightCountText = new GUITextBlock(new RectTransform(new Vector2(0.33f, 1.0f), lightCountLabel.RectTransform, Anchor.TopRight, Pivot.TopLeft), "", textAlignment: Alignment.CenterRight);
            lightCountText.TextGetter = () =>
            {
                int lightCount = 0;
                foreach (Item item in Item.ItemList)
                {
                    if (item.ParentInventory != null) { continue; }
                    lightCount += item.GetComponents<LightComponent>().Count();
                }
                lightCountText.TextColor = lightCount > MaxLights ? GUIStyle.Red : Color.Lerp(GUIStyle.Green, GUIStyle.Orange, lightCount / (float)MaxLights);
                return lightCount.ToString() + "/" + MaxLights;
            };
            var shadowCastingLightCountLabel = new GUITextBlock(new RectTransform(new Vector2(0.75f, 0.0f), paddedEntityCountPanel.RectTransform), TextManager.Get("SubEditorShadowCastingLights"),
                textAlignment: Alignment.CenterLeft, font: GUIStyle.SmallFont, wrap: true);
            var shadowCastingLightCountText = new GUITextBlock(new RectTransform(new Vector2(0.33f, 1.0f), shadowCastingLightCountLabel.RectTransform, Anchor.TopRight, Pivot.TopLeft), "", textAlignment: Alignment.CenterRight);
            shadowCastingLightCountText.TextGetter = () =>
            {
                int lightCount = 0;
                foreach (Item item in Item.ItemList)
                {
                    if (item.ParentInventory != null) { continue; }
                    lightCount += item.GetComponents<LightComponent>().Count(l => l.CastShadows && !l.DrawBehindSubs);
                }
                shadowCastingLightCountText.TextColor = lightCount > MaxShadowCastingLights ? GUIStyle.Red : Color.Lerp(GUIStyle.Green, GUIStyle.Orange, lightCount / (float)MaxShadowCastingLights);
                return lightCount.ToString() + "/" + MaxShadowCastingLights;
            };
            entityCountPanel.RectTransform.NonScaledSize =
                new Point(
                    (int)(paddedEntityCountPanel.RectTransform.Children.Max(c => (int)((GUITextBlock) c.GUIComponent).TextSize.X / 0.75f) / paddedEntityCountPanel.RectTransform.RelativeSize.X),
                    (int)(paddedEntityCountPanel.RectTransform.Children.Sum(c => (int)(c.NonScaledSize.Y * 1.5f) + paddedEntityCountPanel.AbsoluteSpacing) / paddedEntityCountPanel.RectTransform.RelativeSize.Y));
            //GUITextBlock.AutoScaleAndNormalize(paddedEntityCountPanel.Children.Where(c => c is GUITextBlock).Cast<GUITextBlock>());

            //-----------------------------------------------

            hullVolumeFrame = new GUIFrame(new RectTransform(new Vector2(0.15f, 2.0f), TopPanel.RectTransform, Anchor.BottomLeft, Pivot.TopLeft, minSize: new Point(300, 85)) { AbsoluteOffset = new Point(entityCountPanel.Rect.Width, 0) }, "GUIToolTip")
            {
                Visible = false
            };
            GUITextBlock totalHullVolume = new GUITextBlock(new RectTransform(new Vector2(1.0f, 0.5f), hullVolumeFrame.RectTransform), "", font: GUIStyle.SmallFont)
            {
                TextGetter = GetTotalHullVolume
            };
            GUITextBlock selectedHullVolume = new GUITextBlock(new RectTransform(new Vector2(1.0f, 0.5f), hullVolumeFrame.RectTransform) { RelativeOffset = new Vector2(0.0f, 0.5f) }, "", font: GUIStyle.SmallFont)
            {
                TextGetter = GetSelectedHullVolume
            };

            saveAssemblyFrame = new GUIFrame(new RectTransform(new Vector2(0.08f, 0.5f), TopPanel.RectTransform, Anchor.BottomLeft, Pivot.TopLeft)
            { MinSize = new Point((int)(250 * GUI.Scale), (int)(80 * GUI.Scale)), AbsoluteOffset = new Point((int)(10 * GUI.Scale), -entityCountPanel.Rect.Height - (int)(10 * GUI.Scale)) }, "InnerFrame")
            {
                Visible = false
            };
            var saveAssemblyButton = new GUIButton(new RectTransform(new Vector2(0.9f, 0.8f), saveAssemblyFrame.RectTransform, Anchor.Center), TextManager.Get("SaveItemAssembly"));
            saveAssemblyButton.TextBlock.AutoScaleHorizontal = true;
            saveAssemblyButton.OnClicked += (btn, userdata) =>
            {
                CreateSaveAssemblyScreen();
                return true;
            };
            saveAssemblyFrame.RectTransform.MinSize = new Point(saveAssemblyFrame.Rect.Width, (int)(saveAssemblyButton.Rect.Height / saveAssemblyButton.RectTransform.RelativeSize.Y));

            snapToGridFrame = new GUIFrame(new RectTransform(new Vector2(0.08f, 0.5f), TopPanel.RectTransform, Anchor.BottomLeft, Pivot.TopLeft)
            { MinSize = new Point((int)(250 * GUI.Scale), (int)(80 * GUI.Scale)), AbsoluteOffset = new Point((int)(10 * GUI.Scale), -saveAssemblyFrame.Rect.Height - entityCountPanel.Rect.Height - (int)(10 * GUI.Scale)) }, "InnerFrame")
            {
                Visible = false
            };
            var saveStampButton = new GUIButton(new RectTransform(new Vector2(0.9f, 0.8f), snapToGridFrame.RectTransform, Anchor.Center), TextManager.Get("subeditor.snaptogrid", "spriteeditor.snaptogrid"));
            saveStampButton.TextBlock.AutoScaleHorizontal = true;
            saveStampButton.OnClicked += (btn, userdata) =>
            {
                SnapToGrid();
                return true;
            };
            snapToGridFrame.RectTransform.MinSize = new Point(snapToGridFrame.Rect.Width, (int)(saveStampButton.Rect.Height / saveStampButton.RectTransform.RelativeSize.Y));

            //Entity menu
            //------------------------------------------------

            EntityMenu = new GUIFrame(new RectTransform(new Point(GameMain.GraphicsWidth, (int)(359 * GUI.Scale)), GUI.Canvas, Anchor.BottomRight));

            toggleEntityMenuButton = new GUIButton(new RectTransform(new Vector2(0.15f, 0.08f), EntityMenu.RectTransform, Anchor.TopCenter, Pivot.BottomCenter) { MinSize = new Point(0, 15) },
                style: "UIToggleButtonVertical")
            {
                OnClicked = (btn, userdata) =>
                {
                    entityMenuOpen = !entityMenuOpen;
                    SetMode(Mode.Default);
                    foreach (GUIComponent child in btn.Children)
                    {
                        child.SpriteEffects = entityMenuOpen ? SpriteEffects.None : SpriteEffects.FlipVertically;
                    }
                    return true;
                }
            };

            var paddedTab = new GUILayoutGroup(new RectTransform(new Vector2(0.98f, 0.96f), EntityMenu.RectTransform, Anchor.BottomCenter), childAnchor: Anchor.TopCenter)
            {
                RelativeSpacing = 0.04f,
                Stretch = true
            };

            var entityMenuTop = new GUILayoutGroup(new RectTransform(new Vector2(0.95f, 0.13f), paddedTab.RectTransform), isHorizontal: true, childAnchor: Anchor.CenterLeft)
            {
                Stretch = true
            };

            selectedCategoryButton = new GUIButton(new RectTransform(new Vector2(1.0f, 1.0f), entityMenuTop.RectTransform, scaleBasis: ScaleBasis.BothHeight), "", style: "CategoryButton.All")
            {
                CanBeFocused = false
            };
            selectedCategoryText = new GUITextBlock(new RectTransform(new Vector2(0.2f, 1.0f), entityMenuTop.RectTransform), TextManager.Get("MapEntityCategory.All"), font: GUIStyle.LargeFont);

            var filterText = new GUITextBlock(new RectTransform(new Vector2(0.1f, 1.0f), entityMenuTop.RectTransform), TextManager.Get("serverlog.filter"), font: GUIStyle.SubHeadingFont);
            filterText.RectTransform.MaxSize = new Point((int)(filterText.TextSize.X * 1.5f), int.MaxValue);
            entityFilterBox = new GUITextBox(new RectTransform(new Vector2(0.17f, 1.0f), entityMenuTop.RectTransform), font: GUIStyle.Font, createClearButton: true);
            entityFilterBox.OnTextChanged += (textBox, text) =>
            {
                if (text == lastFilter) { return true; }
                lastFilter = text;
                FilterEntities(text);
                return true;
            };

            //spacing
            new GUIFrame(new RectTransform(new Vector2(0.075f, 1.0f), entityMenuTop.RectTransform), style: null);

            entityCategoryButtons.Clear();
            entityCategoryButtons.Add(
                new GUIButton(new RectTransform(new Vector2(1.0f, 1.0f), entityMenuTop.RectTransform, scaleBasis: ScaleBasis.BothHeight), "", style: "CategoryButton.All")
                {
                    OnClicked = (btn, userdata) =>
                    {
                        OpenEntityMenu(null);
                        return true;
                    }
                });

            foreach (MapEntityCategory category in Enum.GetValues(typeof(MapEntityCategory)))
            {
                if (category == MapEntityCategory.None) { continue; }
                entityCategoryButtons.Add(new GUIButton(new RectTransform(new Vector2(1.0f, 1.0f), entityMenuTop.RectTransform, scaleBasis: ScaleBasis.BothHeight),
                    "", style: "CategoryButton." + category.ToString())
                {
                    UserData = category,
                    ToolTip = TextManager.Get("MapEntityCategory." + category.ToString()),
                    OnClicked = (btn, userdata) =>
                    {
                        MapEntityCategory newCategory = (MapEntityCategory)userdata;
                        OpenEntityMenu(newCategory);
                        return true;
                    }
                });
            }
            entityCategoryButtons.ForEach(b => b.RectTransform.MaxSize = new Point(b.Rect.Height));

            new GUIFrame(new RectTransform(new Vector2(0.8f, 0.01f), paddedTab.RectTransform), style: "HorizontalLine");

            var entityListContainer = new GUIFrame(new RectTransform(new Vector2(1.0f, 0.9f), paddedTab.RectTransform), style: null);
            categorizedEntityList = new GUIListBox(new RectTransform(Vector2.One, entityListContainer.RectTransform), useMouseDownToSelect: true);
            allEntityList = new GUIListBox(new RectTransform(Vector2.One, entityListContainer.RectTransform), useMouseDownToSelect: true)
            {
                OnSelected = SelectPrefab,
                UseGridLayout = true,
                CheckSelected = MapEntityPrefab.GetSelected,
                Visible = false,
                PlaySoundOnSelect = true,
            };

            paddedTab.Recalculate();
            UpdateLayerPanel();
            screenResolution = new Point(GameMain.GraphicsWidth, GameMain.GraphicsHeight);
        }

        private bool TestSubmarine(GUIButton button, object obj)
        {
            List<LocalizedString> errorMsgs = new List<LocalizedString>();

            if (!Hull.HullList.Any())
            {
                errorMsgs.Add(TextManager.Get("NoHullsWarning"));
            }

            if (!WayPoint.WayPointList.Any(wp => wp.ShouldBeSaved && wp.SpawnType == SpawnType.Human))
            {
                errorMsgs.Add(TextManager.Get("NoHumanSpawnpointWarning"));
            }

            if (errorMsgs.Any())
            {
                new GUIMessageBox(TextManager.Get("Error"), LocalizedString.Join("\n\n", errorMsgs), new Vector2(0.25f, 0.0f), new Point(400, 200));
                return true;
            }

            CloseItem();

            backedUpSubInfo = new SubmarineInfo(MainSub);

            GameSession gameSession = new GameSession(backedUpSubInfo, Option.None, CampaignDataPath.Empty, GameModePreset.TestMode, CampaignSettings.Empty, null);
            
            // if testing an outpost module, we will generate an entire outpost in place of the main submarine down the line
            if (backedUpSubInfo.OutpostModuleInfo != null)
            {
                gameSession.ForceOutpostModule = new SubmarineInfo(MainSub);
            }
            
            GameMain.GameScreen.Select();
            
            gameSession.StartRound(null, false);
            
            foreach ((string layerName, LayerData layerData) in Layers)
            {
                Identifier identifier = layerName.ToIdentifier();
                bool enabled = layerData.IsVisible;
                MainSub.SetLayerEnabled(identifier, enabled);
            }
            
            if (gameSession.GameMode is TestGameMode testGameMode)
            {
                testGameMode.OnRoundEnd = () =>
                {
                    Submarine.Unload();
                    GameMain.SubEditorScreen.Select();
                };
            }

            return true;
        }

        public void ClearBackedUpSubInfo()
        {
            backedUpSubInfo = null;
        }

        private void UpdateEntityList()
        {
            categorizedEntityList.Content.ClearChildren();
            allEntityList.Content.ClearChildren();

            int maxTextWidth = (int)(GUIStyle.SubHeadingFont.MeasureString(TextManager.Get("mapentitycategory.misc")).X + GUI.IntScale(50));
            Dictionary<string, List<MapEntityPrefab>> entityLists = new Dictionary<string, List<MapEntityPrefab>>();
            Dictionary<string, MapEntityCategory> categoryKeys = new Dictionary<string, MapEntityCategory>();

            foreach (MapEntityCategory category in Enum.GetValues(typeof(MapEntityCategory)))
            {
                if (category == MapEntityCategory.None) { continue; }
                LocalizedString categoryName = TextManager.Get("MapEntityCategory." + category);
                maxTextWidth = (int)Math.Max(maxTextWidth, GUIStyle.SubHeadingFont.MeasureString(categoryName.Replace(" ", "\n")).X + GUI.IntScale(50));
                foreach (MapEntityPrefab ep in MapEntityPrefab.List)
                {
                    if (!ep.Category.HasFlag(category)) { continue; }

                    if (!entityLists.ContainsKey(category + ep.Subcategory))
                    {
                        entityLists[category + ep.Subcategory] = new List<MapEntityPrefab>();
                    }
                    entityLists[category + ep.Subcategory].Add(ep);
                    categoryKeys[category + ep.Subcategory] = category;
                    LocalizedString subcategoryName = TextManager.Get("subcategory." + ep.Subcategory).Fallback(ep.Subcategory);
                    if (subcategoryName != null)
                    {
                        maxTextWidth = (int)Math.Max(maxTextWidth, GUIStyle.SubHeadingFont.MeasureString(subcategoryName.Replace(" ", "\n")).X + GUI.IntScale(50));
                    }
                }
            }

            categorizedEntityList.Content.ClampMouseRectToParent = true;
            int entitiesPerRow = (int)Math.Ceiling(categorizedEntityList.Content.Rect.Width / Math.Max(125 * GUI.Scale, 60));
            foreach (string categoryKey in entityLists.Keys)
            {
                var categoryFrame = new GUIFrame(new RectTransform(Vector2.One, categorizedEntityList.Content.RectTransform), style: null)
                {
                    ClampMouseRectToParent = true,
                    UserData = categoryKeys[categoryKey]
                };

                new GUIFrame(new RectTransform(Vector2.One, categoryFrame.RectTransform), style: "HorizontalLine");

                LocalizedString categoryName = TextManager.Get("MapEntityCategory." + entityLists[categoryKey].First().Category);
                LocalizedString subCategoryName = entityLists[categoryKey].First().Subcategory;
                if (subCategoryName.IsNullOrEmpty())
                {
                    new GUITextBlock(new RectTransform(new Point(maxTextWidth, categoryFrame.Rect.Height), categoryFrame.RectTransform, Anchor.TopLeft),
                        categoryName, textAlignment: Alignment.TopLeft, font: GUIStyle.SubHeadingFont, wrap: true)
                    {
                        Padding = new Vector4(GUI.IntScale(10))
                    };

                }
                else
                {
                    subCategoryName = subCategoryName.IsNullOrEmpty() ?
                        TextManager.Get("mapentitycategory.misc") :
                        (TextManager.Get($"subcategory.{subCategoryName}").Fallback(subCategoryName));
                    var categoryTitle = new GUITextBlock(new RectTransform(new Point(maxTextWidth, categoryFrame.Rect.Height), categoryFrame.RectTransform, Anchor.TopLeft),
                        categoryName, textAlignment: Alignment.TopLeft, font: GUIStyle.Font, wrap: true)
                    {
                        Padding = new Vector4(GUI.IntScale(10))
                    };
                    new GUITextBlock(new RectTransform(new Point(maxTextWidth, categoryFrame.Rect.Height), categoryFrame.RectTransform, Anchor.TopLeft) { AbsoluteOffset = new Point(0, (int)(categoryTitle.TextSize.Y + GUI.IntScale(10))) },
                        subCategoryName, textAlignment: Alignment.TopLeft, font: GUIStyle.SubHeadingFont, wrap: true)
                    {
                        Padding = new Vector4(GUI.IntScale(10))
                    };
                }

                var entityListInner = new GUIListBox(new RectTransform(new Point(categoryFrame.Rect.Width - maxTextWidth, categoryFrame.Rect.Height), categoryFrame.RectTransform, Anchor.CenterRight),
                    style: null,
                    useMouseDownToSelect: true)
                {
                    ScrollBarVisible = false,
                    AutoHideScrollBar = false,
                    OnSelected = SelectPrefab,
                    UseGridLayout = true,
                    CheckSelected = MapEntityPrefab.GetSelected,
                    ClampMouseRectToParent = true,
                    PlaySoundOnSelect = true,
                };
                entityListInner.ContentBackground.ClampMouseRectToParent = true;
                entityListInner.Content.ClampMouseRectToParent = true;

                foreach (MapEntityPrefab ep in entityLists[categoryKey])
                {
#if !DEBUG
                    if ((ep.HideInMenus || ep.HideInEditors) && !GameMain.DebugDraw) { continue; }
#endif
                    CreateEntityElement(ep, entitiesPerRow, entityListInner.Content);
                }

                entityListInner.UpdateScrollBarSize();
                int contentHeight = (int)(entityListInner.TotalSize + entityListInner.Padding.Y + entityListInner.Padding.W);
                categoryFrame.RectTransform.NonScaledSize = new Point(categoryFrame.Rect.Width, contentHeight);
                categoryFrame.RectTransform.MinSize = new Point(0, contentHeight);
                entityListInner.RectTransform.NonScaledSize = new Point(entityListInner.Rect.Width, contentHeight);
                entityListInner.RectTransform.MinSize = new Point(0, contentHeight);
                
                entityListInner.Content.RectTransform.SortChildren((i1, i2) =>
                    string.Compare(((MapEntityPrefab)i1.GUIComponent.UserData)?.Name.Value, (i2.GUIComponent.UserData as MapEntityPrefab)?.Name.Value, StringComparison.Ordinal));
            }

            foreach (MapEntityPrefab ep in MapEntityPrefab.List)
            {
#if !DEBUG
                if ((ep.HideInMenus || ep.HideInEditors) && !GameMain.DebugDraw) { continue; }
#endif
                CreateEntityElement(ep, entitiesPerRow, allEntityList.Content);
            }
            allEntityList.Content.RectTransform.SortChildren((i1, i2) =>
               string.Compare(((MapEntityPrefab)i1.GUIComponent.UserData)?.Name.Value, (i2.GUIComponent.UserData as MapEntityPrefab)?.Name.Value, StringComparison.Ordinal));

        }

        private void CreateEntityElement(MapEntityPrefab ep, int entitiesPerRow, GUIComponent parent)
        {
            bool legacy = ep.Category.HasFlag(MapEntityCategory.Legacy);

            float relWidth = 1.0f / entitiesPerRow;
            GUIFrame frame = new GUIFrame(new RectTransform(
                new Vector2(relWidth, relWidth * ((float)parent.Rect.Width / parent.Rect.Height)),
                parent.RectTransform)
                { MinSize = new Point(0, 50) },
                style: "GUITextBox")
            {
                UserData = ep,
                ClampMouseRectToParent = true
            };
            frame.RectTransform.MinSize = new Point(0, frame.Rect.Width);
            frame.RectTransform.MaxSize = new Point(int.MaxValue, frame.Rect.Width);

            LocalizedString name = legacy ? TextManager.GetWithVariable("legacyitemformat", "[name]", ep.Name) : ep.Name;
            frame.ToolTip = ep.CreateTooltipText();

            if (ep.IsModded)
            {
                frame.Color = Color.Magenta;
            }

            if (ep.HideInMenus || ep.HideInEditors)
            {
                frame.Color = Color.Red;
                name = "[HIDDEN] " + name;
            }
            frame.ToolTip = RichString.Rich(frame.ToolTip);

            GUILayoutGroup paddedFrame = new GUILayoutGroup(new RectTransform(new Vector2(0.8f, 0.8f), frame.RectTransform, Anchor.Center), childAnchor: Anchor.TopCenter)
            {
                Stretch = true,
                RelativeSpacing = 0.03f,
                CanBeFocused = false
            };

            Sprite icon = ep.Sprite;
            Color iconColor = Color.White;
            if (ep is ItemPrefab itemPrefab)
            {
                if (itemPrefab.InventoryIcon != null)
                {
                    icon = itemPrefab.InventoryIcon;
                    iconColor = itemPrefab.InventoryIconColor;
                }
                else
                {
                    iconColor = itemPrefab.SpriteColor;
                }
            }
            GUIImage img = null;
            if (ep.Sprite != null)
            {
                img = new GUIImage(new RectTransform(new Vector2(1.0f, 0.8f),
                    paddedFrame.RectTransform, Anchor.TopCenter), icon)
                {
                    CanBeFocused = false,
                    LoadAsynchronously = true,
                    SpriteEffects = icon.effects,
                    Color = legacy ? iconColor * 0.6f : iconColor
                };
            }

            if (ep is ItemAssemblyPrefab itemAssemblyPrefab)
            {
                new GUICustomComponent(new RectTransform(new Vector2(1.0f, 0.75f),
                    paddedFrame.RectTransform, Anchor.TopCenter), onDraw: (sb, customComponent) =>
                    {
                        if (GUIImage.LoadingTextures) { return; }
                        itemAssemblyPrefab.DrawIcon(sb, customComponent);
                    })
                {
                    HideElementsOutsideFrame = true,
                    ToolTip = frame.ToolTip.SanitizedString
                };
            }

            GUITextBlock textBlock = new GUITextBlock(new RectTransform(new Vector2(1.0f, 0.0f), paddedFrame.RectTransform, Anchor.BottomCenter),
                text: RichString.Rich(name), textAlignment: Alignment.Center, font: GUIStyle.SmallFont)
            {
                CanBeFocused = false
            };
            if (legacy) { textBlock.TextColor *= 0.6f; }
            if (name.IsNullOrEmpty())
            {
                DebugConsole.AddWarning($"Entity \"{ep.Identifier.Value}\" has no name!",
                    contentPackage: ep.ContentPackage);
                textBlock.Text = frame.ToolTip = ep.Identifier.Value;
                textBlock.TextColor = GUIStyle.Red;
            }
            textBlock.Text = ToolBox.LimitString(textBlock.Text.SanitizedString, textBlock.Font, textBlock.Rect.Width);

            if (ep.Category == MapEntityCategory.ItemAssembly
                && ep.ContentPackage?.Files.Length == 1
                && ContentPackageManager.LocalPackages.Contains(ep.ContentPackage))
            {
                var deleteButton = new GUIButton(new RectTransform(new Vector2(1.0f, 0.2f), paddedFrame.RectTransform, Anchor.BottomCenter) { MinSize = new Point(0, 20) },
                    TextManager.Get("Delete"), style: "GUIButtonSmall")
                {
                    UserData = ep,
                    OnClicked = (btn, userData) =>
                    {
                        ItemAssemblyPrefab assemblyPrefab = (ItemAssemblyPrefab)userData;
                        if (assemblyPrefab != null)
                        {
                            var msgBox = new GUIMessageBox(
                                TextManager.Get("DeleteDialogLabel"),
                                TextManager.GetWithVariable("DeleteDialogQuestion", "[file]", assemblyPrefab.Name),
                                new[] { TextManager.Get("Yes"), TextManager.Get("Cancel") });
                            msgBox.Buttons[0].OnClicked += (deleteBtn, userData2) =>
                            {
                                try
                                {
                                    assemblyPrefab.Delete();
                                    OpenEntityMenu(MapEntityCategory.ItemAssembly);
                                }
                                catch (Exception e)
                                {
                                    DebugConsole.ThrowErrorLocalized(TextManager.GetWithVariable("DeleteFileError", "[file]", assemblyPrefab.Name), e);
                                }
                                return true;
                            };
                            msgBox.Buttons[0].OnClicked += msgBox.Close;
                            msgBox.Buttons[1].OnClicked += msgBox.Close;
                        }

                        return true;
                    }
                };
            }
            paddedFrame.Recalculate();
            if (img != null)
            {
                img.Scale = Math.Min(Math.Min(img.Rect.Width / img.Sprite.size.X, img.Rect.Height / img.Sprite.size.Y), 1.5f);
                img.RectTransform.NonScaledSize = new Point((int)(img.Sprite.size.X * img.Scale), img.Rect.Height);
            }
        }

        public override void Select()
        {
            Select(enableAutoSave: true);
        }

        public void Select(bool enableAutoSave = true)
        {
            base.Select();

            TaskPool.Add(
                $"DeterminePublishedItemIds",
                SteamManager.Workshop.GetPublishedItems(),
                t =>
                {
                    if (!t.TryGetResult(out ISet<Steamworks.Ugc.Item> items)) { return; }

                    publishedWorkshopItemIds.Clear();
                    publishedWorkshopItemIds.UnionWith(items.Select(it => it.Id.Value));
                });
            
            GUI.PreventPauseMenuToggle = false;
            if (!Directory.Exists(autoSavePath))
            {
                if (Directory.CreateDirectory(autoSavePath, catchUnauthorizedAccessExceptions: true) is { Exists: true } directoryInfo)
                {
                    directoryInfo.Attributes = System.IO.FileAttributes.Directory | System.IO.FileAttributes.Hidden;
                }
                else
                { 
                    DebugConsole.ThrowError("Failed to create auto save directory!");
                }
            }

            if (!File.Exists(autoSaveInfoPath))
            {
                try
                {
                    AutoSaveInfo = new XDocument(new XElement("AutoSaves"));
                    AutoSaveInfo.SaveSafe(autoSaveInfoPath, throwExceptions: true);
                }
                catch (Exception e)
                {
                    DebugConsole.ThrowError("Saving auto save info to \"" + autoSaveInfoPath + "\" failed!", e);
                }
            }
            else
            {
                AutoSaveInfo = XMLExtensions.TryLoadXml(autoSaveInfoPath);
            }

            GameMain.LightManager.AmbientLight =
                Level.Loaded?.GenerationParams?.AmbientLightColor ??
                new Color(3, 3, 3, 3);

            isAutoSaving = false;

            if (!wasSelectedBefore)
            {
                OpenEntityMenu(null);
                wasSelectedBefore = true;
            }
            else
            {
                OpenEntityMenu(selectedCategory);
            }

            if (backedUpSubInfo != null)
            {
                Submarine.Unload();
            }

            string name = (MainSub == null) ? TextManager.Get("unspecifiedsubfilename").Value : MainSub.Info.Name;
            if (backedUpSubInfo != null) { name = backedUpSubInfo.Name; }
            subNameLabel.Text = ToolBox.LimitString(name, subNameLabel.Font, subNameLabel.Rect.Width);

            editorSelectedTime = Option<DateTime>.Some(DateTime.Now);

            GUI.ForceMouseOn(null);
            SetMode(Mode.Default);

            if (backedUpSubInfo != null)
            {
                MainSub = new Submarine(backedUpSubInfo);
                if (previewImage != null && backedUpSubInfo.PreviewImage?.Texture != null && !backedUpSubInfo.PreviewImage.Texture.IsDisposed)
                {
                    previewImage.Sprite = backedUpSubInfo.PreviewImage;
                }
                backedUpSubInfo = null;
            }
            else if (MainSub == null)
            {
                var subInfo = new SubmarineInfo();
                MainSub = new Submarine(subInfo, showErrorMessages: false);
                ReconstructLayers();
            }

            MainSub.UpdateTransform(interpolate: false);
            cam.Position = MainSub.Position + MainSub.HiddenSubPosition;

            GameMain.SoundManager.SetCategoryGainMultiplier(SoundManager.SoundCategoryDefault, 0.0f);
            GameMain.SoundManager.SetCategoryGainMultiplier(SoundManager.SoundCategoryWaterAmbience, 0.0f);

            string downloadFolder = Path.GetFullPath(SaveUtil.SubmarineDownloadFolder);
            linkedSubBox.ClearChildren();

            List<(string Name, SubmarineInfo Sub)> subs = new List<(string Name, SubmarineInfo Sub)>();

            foreach (SubmarineInfo sub in SubmarineInfo.SavedSubmarines)
            {
                if (sub.Type != SubmarineType.Player) { continue; }
                if (Path.GetDirectoryName(Path.GetFullPath(sub.FilePath)) == downloadFolder) { continue; }
                subs.Add((sub.Name, sub));
            }

            foreach (var (subName, sub) in subs.OrderBy(tuple => tuple.Name))
            {
                linkedSubBox.AddItem(subName, sub);
            }

            cam.UpdateTransform();

            CreateDummyCharacter();

            if (GameSettings.CurrentConfig.EnableSubmarineAutoSave && enableAutoSave)
            {
                CoroutineManager.StartCoroutine(AutoSaveCoroutine(), "SubEditorAutoSave");
            }

            ImageManager.OnEditorSelected();
            if (Layers.None())
            {
                ReconstructLayers();
            }
        }

        public override void OnFileDropped(string filePath, string extension)
        {
            switch (extension)
            {
                case ".sub": // Submarine
                    SubmarineInfo info = new SubmarineInfo(filePath);
                    if (info.IsFileCorrupted)
                    {
                        DebugConsole.ThrowError($"Could not drag and drop the file. File \"{filePath}\" is corrupted!");
                        info.Dispose();
                        return;
                    }

                    LocalizedString body = TextManager.GetWithVariable("SubEditor.LoadConfirmBody", "[submarine]", info.Name);
                    GUI.AskForConfirmation(TextManager.Get("Load"), body, onConfirm: () => LoadSub(info), onDeny: () => info.Dispose());
                    break;

                case ".xml": // Item Assembly
                    string text = File.ReadAllText(filePath);
                    // PlayerInput.MousePosition doesn't update while the window is not active so we need to use this method
                    Vector2 mousePos = Mouse.GetState().Position.ToVector2();
                    PasteAssembly(text, cam.ScreenToWorld(mousePos));
                    break;

                case ".png": // submarine preview
                case ".jpg":
                case ".jpeg":
                    if (saveFrame == null) { break; }

                    Texture2D texture = Sprite.LoadTexture(filePath, compress: false);
                    previewImage.Sprite = new Sprite(texture, null, null);
                    if (MainSub != null)
                    {
                        MainSub.Info.PreviewImage = previewImage.Sprite;
                    }

                    break;

                default:
                    DebugConsole.ThrowError($"Could not drag and drop the file. \"{extension}\" is not a valid file extension! (expected .xml, .sub, .png or .jpg)");
                    break;
            }
        }

        /// <summary>
        /// Coroutine that waits 5 minutes and then runs itself recursively again to save the submarine into a temporary file
        /// </summary>
        /// <see cref="AutoSave"/>
        /// <returns></returns>
        private static IEnumerable<CoroutineStatus> AutoSaveCoroutine()
        {
            DateTime target = DateTime.Now.AddSeconds(GameSettings.CurrentConfig.AutoSaveIntervalSeconds);
            DateTime tempTarget = DateTime.Now;

            bool wasPaused = false;

            while (DateTime.Now < target && Selected is SubEditorScreen || GameMain.Instance.Paused || wasPaused)
            {
                if (GameMain.Instance.Paused && !wasPaused)
                {
                    AutoSave();
                    tempTarget = DateTime.Now;
                    wasPaused = true;
                }

                if (!GameMain.Instance.Paused && wasPaused)
                {
                    wasPaused = false;
                    target = target.AddSeconds((DateTime.Now - tempTarget).TotalSeconds);
                }
                yield return CoroutineStatus.Running;
            }

            if (Selected is SubEditorScreen)
            {
                AutoSave();
                CoroutineManager.StartCoroutine(AutoSaveCoroutine(), "SubEditorAutoSave");
            }
            yield return CoroutineStatus.Success;
        }

        protected override void DeselectEditorSpecific()
        {
            CloseItem();

            autoSaveLabel?.Parent?.RemoveChild(autoSaveLabel);
            autoSaveLabel = null;

            if (editorSelectedTime.TryUnwrap(out DateTime selectedTime))
            {
                TimeSpan timeInEditor = DateTime.Now - selectedTime;
                //this is intended for diagnosing why the "x hours in editor" achievement seems to sometimes trigger too soon
                //require the time in editor to be x1.5 higher to disregard any rounding errors or discrepancies in Datetime.Now and the game's own timekeeping
                if (timeInEditor.TotalSeconds > Timing.TotalTime * 1.5)
                {
                    DebugConsole.ThrowErrorAndLogToGA(
                        "SubEditorScreen.DeselectEditorSpecific:InvalidTimeInEditor",
                        $"Error in sub editor screen. Calculated time in editor {timeInEditor} was larger than the time the game has run ({Timing.TotalTime} s).");
                }
                else
                {
                    AchievementManager.IncrementStat(AchievementStat.HoursInEditor, (float)timeInEditor.TotalHours);
                    editorSelectedTime = Option<DateTime>.None();
                }
            }

            GUI.ForceMouseOn(null);

            if (ImageManager.EditorMode) { GameSettings.SaveCurrentConfig(); }

            MapEntityPrefab.Selected = null;

            saveFrame = null;
            loadFrame = null;

            MapEntity.DeselectAll();
            ClearUndoBuffer();

#if !DEBUG
            DebugConsole.DeactivateCheats();
#endif

            SetMode(Mode.Default);

            SoundPlayer.OverrideMusicType = Identifier.Empty;
            GameMain.SoundManager.SetCategoryGainMultiplier(SoundManager.SoundCategoryDefault, GameSettings.CurrentConfig.Audio.SoundVolume);
            GameMain.SoundManager.SetCategoryGainMultiplier(SoundManager.SoundCategoryWaterAmbience, GameSettings.CurrentConfig.Audio.SoundVolume);

            if (CoroutineManager.IsCoroutineRunning("SubEditorAutoSave"))
            {
                CoroutineManager.StopCoroutines("SubEditorAutoSave");
            }

            if (dummyCharacter != null)
            {
                dummyCharacter.Remove();
                dummyCharacter = null;
                GameMain.World.ProcessChanges();
            }

            GUIMessageBox.MessageBoxes.ForEachMod(component =>
            {
                if (component is GUIMessageBox { Closed: false, UserData: "colorpicker" } msgBox)
                {
                    foreach (GUIColorPicker colorPicker in msgBox.GetAllChildren<GUIColorPicker>())
                    {
                        colorPicker.Dispose();
                    }

                    msgBox.Close();
                }
            });

            ClearFilter();
        }

        private void CreateDummyCharacter()
        {
            if (dummyCharacter != null) { RemoveDummyCharacter(); }

            dummyCharacter = Character.Create(CharacterPrefab.HumanSpeciesName, Vector2.Zero, "", id: Entity.DummyID, hasAi: false);
            dummyCharacter.Info.Name = "Galldren";

            //make space for the entity menu
            for (int i = 0; i < dummyCharacter.Inventory.SlotPositions.Length; i++)
            {
                if (CharacterInventory.PersonalSlots.HasFlag(dummyCharacter.Inventory.SlotTypes[i])) { continue; }
                if (dummyCharacter.Inventory.SlotPositions[i].Y > GameMain.GraphicsHeight / 2)
                {
                    dummyCharacter.Inventory.SlotPositions[i].Y -= 50 * GUI.Scale;
                }
            }
            dummyCharacter.Inventory.CreateSlots();

            Character.Controlled = dummyCharacter;
            GameMain.World.ProcessChanges();
        }

        /// <summary>
        /// Saves the current main sub into a temporary file outside of the Submarines/ folder
        /// </summary>
        /// <see cref="LoadAutoSave"/>
        /// <remarks>The saving is ran in another thread to avoid lag spikes</remarks>
        private static void AutoSave()
        {
            if (MapEntity.MapEntityList.Any() && GameSettings.CurrentConfig.EnableSubmarineAutoSave && !isAutoSaving)
            {
                if (MainSub != null)
                {
                    isAutoSaving = true;
                    if (!Directory.Exists(autoSavePath)) { return; }

                    XDocument doc = new XDocument(new XElement("Submarine"));
                    MainSub.SaveToXElement(doc.Root);
                    Thread saveThread = new Thread(start =>
                    {
                        try
                        {
                            Barotrauma.IO.Validation.SkipValidationInDebugBuilds = true;
                            TimeSpan time = DateTime.UtcNow - DateTime.MinValue;
                            string filePath = Path.Combine(autoSavePath, $"AutoSave_{(ulong)time.TotalMilliseconds}.sub");
                            SaveUtil.CompressStringToFile(filePath, doc.ToString());

                            CrossThread.RequestExecutionOnMainThread(() =>
                            {
                                if (AutoSaveInfo?.Root == null || MainSub?.Info == null) { return; }

                                int saveCount = AutoSaveInfo.Root.Elements().Count();
                                while (AutoSaveInfo.Root.Elements().Count() > MaxAutoSaves)
                                {
                                    XElement min = AutoSaveInfo.Root.Elements().OrderBy(element => element.GetAttributeUInt64("time", 0)).FirstOrDefault();
                                    #warning TODO: revise
                                    string path = min.GetAttributeStringUnrestricted("file", "");
                                    if (string.IsNullOrWhiteSpace(path)) { continue; }

                                    if (IO.File.Exists(path)) { IO.File.Delete(path); }
                                    min?.Remove();
                                }

                                XElement newElement = new XElement("AutoSave",
                                    new XAttribute("file", filePath),
                                    new XAttribute("name", MainSub.Info.Name),
                                    new XAttribute("time", (ulong)time.TotalSeconds));
                                AutoSaveInfo.Root.Add(newElement);

                                try
                                {
                                    IO.SafeXML.SaveSafe(AutoSaveInfo, autoSaveInfoPath);
                                }
                                catch (Exception e)
                                {
                                    DebugConsole.ThrowError("Saving auto save info to \"" + autoSaveInfoPath + "\" failed!", e);
                                }
                            });

                            Barotrauma.IO.Validation.SkipValidationInDebugBuilds = false;
                            CrossThread.RequestExecutionOnMainThread(DisplayAutoSavePrompt);
                        }
                        catch (Exception e)
                        {
                            CrossThread.RequestExecutionOnMainThread(() => DebugConsole.ThrowError("Auto saving submarine failed!", e));
                        }
                        isAutoSaving = false;
                    }) { Name = "Auto Save Thread" };
                    saveThread.Start();
                }
            }
        }

        private static void DisplayAutoSavePrompt()
        {
            if (Selected != GameMain.SubEditorScreen) { return; }
            autoSaveLabel?.Parent?.RemoveChild(autoSaveLabel);

            LocalizedString label = TextManager.Get("AutoSaved");
            autoSaveLabel = new GUILayoutGroup(new RectTransform(new Point(GUI.IntScale(150), GUI.IntScale(32)), GameMain.SubEditorScreen.EntityMenu.RectTransform, Anchor.TopRight)
            {
                ScreenSpaceOffset = new Point(-GUI.IntScale(16), -GUI.IntScale(48))
            }, isHorizontal: true)
            {
                CanBeFocused = false
            };

            GUIImage checkmark = new GUIImage(new RectTransform(new Vector2(0.25f, 1f), autoSaveLabel.RectTransform), style: "MissionCompletedIcon", scaleToFit: true);
            GUITextBlock labelComponent = new GUITextBlock(new RectTransform(new Vector2(0.75f, 1f), autoSaveLabel.RectTransform), label, font: GUIStyle.SubHeadingFont, color: GUIStyle.Green)
            {
                Padding = Vector4.Zero,
                AutoScaleHorizontal = true,
                AutoScaleVertical = true
            };

            labelComponent.FadeOut(0.5f, true, 1f);
            checkmark.FadeOut(0.5f, true, 1f);
            autoSaveLabel?.FadeOut(0.5f, true, 1f);
        }

        private bool SaveSub(ContentPackage packageToSaveTo)
        {
            void handleExceptions(Action action)
            {
                try
                {
                    action();
                }
                catch (Exception e)
                {
                    DebugConsole.ThrowError($"An error occurred while trying to save {nameBox.Text}", e, createMessageBox: true);
                }
            }
            
            if (string.IsNullOrWhiteSpace(nameBox.Text))
            {
                GUI.AddMessage(TextManager.Get("SubNameMissingWarning"), GUIStyle.Red);
                nameBox.Flash();
                return false;
            }
            
            // when creating a new local package, prevent name clashes with existing packages
            if (packageToSaveTo == null)
            {
                var subFiles = ContentPackageManager.EnabledPackages.All
                    .SelectMany(p => p.GetFiles<SubmarineFile>());
                
                var nameConflictFile = subFiles.FirstOrDefault(file => Path.GetFileNameWithoutExtension(file.Path.Value).Equals(nameBox.Text, StringComparison.InvariantCultureIgnoreCase));
                
                if (nameConflictFile != null)
                {
                    new GUIMessageBox(TextManager.Get("error"), TextManager.GetWithVariable("subeditor.duplicatefilenameerror", "[packagename]", nameConflictFile.ContentPackage.Name));
                    return false;
                }
            }

            if (MainSub.Info.Type != SubmarineType.Player)
            {
                if (MainSub.Info.Type == SubmarineType.OutpostModule &&
                    MainSub.Info.OutpostModuleInfo != null)
                {
                    MainSub.Info.PreviewImage = null;
                }
            }
            else if (MainSub.Info.Class == SubmarineClass.Undefined && !MainSub.Info.HasTag(SubmarineTag.Shuttle))
            {
                var msgBox = new GUIMessageBox(TextManager.Get("warning"), TextManager.Get("undefinedsubmarineclasswarning"), new LocalizedString[] { TextManager.Get("yes"), TextManager.Get("no") });

                msgBox.Buttons[0].OnClicked = (bt, userdata) =>
                {
                    handleExceptions(() => SaveSubToFile(nameBox.Text, packageToSaveTo));
                    saveFrame = null;
                    msgBox.Close();
                    return true;
                };
                msgBox.Buttons[1].OnClicked = (bt, userdata) =>
                {
                    msgBox.Close();
                    return true;
                };
                return true;
            }

            bool result = false;
            handleExceptions(() => result = SaveSubToFile(nameBox.Text, packageToSaveTo));
            saveFrame = null;
            return result;
        }

        private void ReloadModifiedPackage(ContentPackage p)
        {
            if (p is null) { return; }
            p.ReloadSubsAndItemAssemblies();
            if (p.Files.Length == 0)
            {
                Directory.Delete(p.Dir, recursive: true);
                ContentPackageManager.LocalPackages.Refresh();
                ContentPackageManager.EnabledPackages.DisableRemovedMods();
            }
        }

        public static Type DetermineSubFileType(SubmarineType type)
            => type switch
            {
                SubmarineType.Outpost => typeof(OutpostFile),
                SubmarineType.OutpostModule => typeof(OutpostModuleFile),
                SubmarineType.Ruin => typeof(OutpostModuleFile),
                SubmarineType.Wreck => typeof(WreckFile),
                SubmarineType.BeaconStation => typeof(BeaconStationFile),
                SubmarineType.EnemySubmarine => typeof(EnemySubmarineFile),
                SubmarineType.Player => typeof(SubmarineFile),
                _ => null
            };

        private bool SaveSubToFile(string name, ContentPackage packageToSaveTo)
        {
            Type subFileType = DetermineSubFileType(MainSub?.Info.Type ?? SubmarineType.Player);

            static string getExistingFilePath(ContentPackage package, string fileName)
            {
                if (Submarine.MainSub?.Info == null) { return null; }
                if (package.Files.Any(f => f.Path == MainSub.Info.FilePath && Path.GetFileName(f.Path.Value) == fileName))
                {
                    return MainSub.Info.FilePath;
                }
                return null;
            }

            if (!GameMain.DebugDraw)
            {
                if (Submarine.GetLightCount() > MaxLights)
                {
                    new GUIMessageBox(TextManager.Get("error"), TextManager.GetWithVariable("subeditor.lightcounterror", "[max]", MaxLights.ToString()));
                    return false;
                }

                if (Submarine.GetShadowCastingLightCount() > MaxShadowCastingLights)
                {
                    new GUIMessageBox(TextManager.Get("error"), TextManager.GetWithVariable("subeditor.shadowcastinglightcounterror", "[max]", MaxShadowCastingLights.ToString()));
                    return false;
                }
            }

            if (string.IsNullOrWhiteSpace(name))
            {
                GUI.AddMessage(TextManager.Get("SubNameMissingWarning"), GUIStyle.Red);
                return false;
            }

            foreach (var illegalChar in Path.GetInvalidFileNameCharsCrossPlatform())
            {
                if (!name.Contains(illegalChar)) { continue; }
                GUI.AddMessage(TextManager.GetWithVariable("SubNameIllegalCharsWarning", "[illegalchar]", illegalChar.ToString()), GUIStyle.Red);
                return false;
            }

            name = name.Trim();

            string newLocalModDir = $"{ContentPackage.LocalModsDir}/{name}";

            string savePath = $"{name}.sub";
            string prevSavePath = null;
            if (packageToSaveTo != null)
            {
                var modProject = new ModProject(packageToSaveTo);                        
                var fileListPath = packageToSaveTo.Path;
                if (packageToSaveTo == ContentPackageManager.VanillaCorePackage)
                {
#if !DEBUG
                    throw new InvalidOperationException("Cannot save to Vanilla package");
#endif
                    savePath =
                        getExistingFilePath(packageToSaveTo, savePath) ??
                        string.Format((MainSub?.Info.Type ?? SubmarineType.Player) switch
                        {
                            SubmarineType.Player => "Content/Submarines/{0}",
                            SubmarineType.Outpost => "Content/Map/Outposts/{0}",
                            SubmarineType.Ruin => "Content/Submarines/{0}", //we don't seem to use this anymore...
                            SubmarineType.Wreck => "Content/Map/Wrecks/{0}",
                            SubmarineType.BeaconStation => "Content/Map/BeaconStations/{0}",
                            SubmarineType.EnemySubmarine => "Content/Map/EnemySubmarines/{0}",
                            SubmarineType.OutpostModule => MainSub.Info.FilePath.Contains("RuinModules") ? "Content/Map/RuinModules/{0}" : "Content/Map/Outposts/{0}",
                            _ => throw new InvalidOperationException()
                        }, savePath);
                    modProject.ModVersion = "";
                }
                else
                {
                    string existingFilePath = getExistingFilePath(packageToSaveTo, savePath);
                    //if we're trying to save a sub that's already included in the package with the same name as before, save directly in the same path
                    if (existingFilePath != null)
                    {
                        savePath = existingFilePath;
                    }
                    //otherwise make sure we're not trying to overwrite another sub in the same package
                    else
                    {
                        savePath = Path.Combine(packageToSaveTo.Dir, savePath);
                        if (File.Exists(savePath))
                        {
                            var verification = new GUIMessageBox(TextManager.Get("warning"), TextManager.Get("subeditor.duplicatesubinpackage"), 
                                new LocalizedString[] { TextManager.Get("yes"), TextManager.Get("no") });
                            verification.Buttons[0].OnClicked = (_, _) =>
                            {
                                addSubAndSave(modProject, savePath, fileListPath);
                                verification.Close();
                                return true;
                            };
                            verification.Buttons[1].OnClicked = verification.Close;
                            return false;
                        }
                    }
                }
                addSubAndSave(modProject, savePath, fileListPath);
            }
            else
            {
                savePath = Path.Combine(newLocalModDir, savePath);
                if (File.Exists(savePath))
                {
                    new GUIMessageBox(TextManager.Get("warning"), TextManager.GetWithVariable("subeditor.packagealreadyexists", "[name]", name));
                    return false;
                }
                else
                {
                    ModProject modProject = new ModProject { Name = name };
                    addSubAndSave(modProject, savePath, Path.Combine(Path.GetDirectoryName(savePath), ContentPackage.FileListFileName));
                }
            }

            void addSubAndSave(ModProject modProject, string filePath, string packagePath)
            {
                filePath = filePath.CleanUpPath();
                packagePath = packagePath.CleanUpPath();
                string packageDir = Path.GetDirectoryName(packagePath).CleanUpPathCrossPlatform(correctFilenameCase: false);
                if (filePath.StartsWith(packageDir))
                {
                    filePath = $"{ContentPath.ModDirStr}/{filePath[packageDir.Length..]}";
                }
                if (!modProject.Files.Any(f => f.Type == subFileType && f.Path == filePath))
                {
                    //check if there's a file with the same name but different filename case
                    var matchingFile = modProject.Files.FirstOrDefault(f => f.Type == subFileType && filePath.CleanUpPath().Equals(f.Path.CleanUpPath(), StringComparison.OrdinalIgnoreCase));
                    if (matchingFile != null)
                    {
                        File.Delete(matchingFile.Path.Replace(ContentPath.ModDirStr, packageDir, StringComparison.OrdinalIgnoreCase));
                        modProject.RemoveFile(matchingFile);
                    }
                    var newFile = ModProject.File.FromPath(filePath, subFileType);
                    modProject.AddFile(newFile);
                }

                using var _ = Validation.SkipInDebugBuilds();
                modProject.DiscardHashAndInstallTime();
                modProject.Save(packagePath);

                savePath = savePath.CleanUpPathCrossPlatform(correctFilenameCase: false);
                if (MainSub != null)
                {
                    Barotrauma.IO.Validation.SkipValidationInDebugBuilds = true;
                    if (previewImage?.Sprite?.Texture != null && !previewImage.Sprite.Texture.IsDisposed && MainSub.Info.Type != SubmarineType.OutpostModule)
                    {
                        bool savePreviewImage = true;
                        using System.IO.MemoryStream imgStream = new System.IO.MemoryStream();
                        try
                        {
                            previewImage.Sprite.Texture.SaveAsPng(imgStream, previewImage.Sprite.Texture.Width, previewImage.Sprite.Texture.Height);
                        }
                        catch (Exception e)
                        {
                            DebugConsole.ThrowError($"Saving the preview image of the submarine \"{MainSub.Info.Name}\" failed.", e);
                            savePreviewImage = false;
                        }
                        MainSub.TrySaveAs(savePath, savePreviewImage ? imgStream : null);
                    }
                    else
                    {
                        MainSub.TrySaveAs(savePath);
                    }
                    Barotrauma.IO.Validation.SkipValidationInDebugBuilds = false;

                    MainSub.CheckForErrors();

                    GUI.AddMessage(TextManager.GetWithVariable("SubSavedNotification", "[filepath]", savePath), GUIStyle.Green);

                    if (savePath.StartsWith(newLocalModDir))
                    {
                        ContentPackageManager.LocalPackages.Refresh();
                        var newPackage = ContentPackageManager.LocalPackages.FirstOrDefault(p => p.Path.StartsWith(newLocalModDir));
                        if (newPackage is RegularPackage regular)
                        {
                            ContentPackageManager.EnabledPackages.EnableRegular(regular);
                            GameSettings.SaveCurrentConfig();
                        }
                    }
                    if (packageToSaveTo != null) { ReloadModifiedPackage(packageToSaveTo); }
                    SubmarineInfo.RefreshSavedSub(savePath);
                    if (prevSavePath != null && prevSavePath != savePath) { SubmarineInfo.RefreshSavedSub(prevSavePath); }
                    MainSub.Info.PreviewImage = SubmarineInfo.SavedSubmarines.FirstOrDefault(s => s.FilePath == savePath)?.PreviewImage;

                    string downloadFolder = Path.GetFullPath(SaveUtil.SubmarineDownloadFolder);
                    linkedSubBox.ClearChildren();
                    foreach (SubmarineInfo sub in SubmarineInfo.SavedSubmarines)
                    {
                        if (sub.Type != SubmarineType.Player) { continue; }
                        if (Path.GetDirectoryName(Path.GetFullPath(sub.FilePath)) == downloadFolder) { continue; }
                        linkedSubBox.AddItem(sub.Name, sub);
                    }
                    subNameLabel.Text = ToolBox.LimitString(MainSub.Info.Name, subNameLabel.Font, subNameLabel.Rect.Width);
                }
            }

            return false;
        }

        private void CreateSaveScreen(bool quickSave = false)
        {
            if (saveFrame != null) { return; }

            if (!quickSave)
            {
                CloseItem();
                SetMode(Mode.Default);
            }

            saveFrame = new GUIFrame(new RectTransform(Vector2.One, GUI.Canvas, Anchor.Center), style: "GUIBackgroundBlocker");

            var innerFrame = new GUIFrame(new RectTransform(new Vector2(0.6f, 0.7f), saveFrame.RectTransform, Anchor.Center) { MinSize = new Point(750, 500) });
            var paddedSaveFrame = new GUILayoutGroup(new RectTransform(new Vector2(0.95f, 0.9f), innerFrame.RectTransform, Anchor.Center)) { Stretch = true, RelativeSpacing = 0.02f };

            var columnArea = new GUILayoutGroup(new RectTransform(new Vector2(1.0f, 0.9f), paddedSaveFrame.RectTransform), isHorizontal: true) { RelativeSpacing = 0.02f, Stretch = true };
            var leftColumn = new GUILayoutGroup(new RectTransform(new Vector2(0.55f, 1.0f), columnArea.RectTransform)) { RelativeSpacing = 0.01f, Stretch = true };
            var rightColumn = new GUILayoutGroup(new RectTransform(new Vector2(0.42f, 1.0f), columnArea.RectTransform)) { RelativeSpacing = 0.02f, Stretch = true };

            // left column -----------------------------------------------------------------------

            var nameHeaderGroup = new GUILayoutGroup(new RectTransform(new Vector2(.975f, 0.03f), leftColumn.RectTransform), true);
            var saveSubLabel = new GUITextBlock(new RectTransform(new Vector2(.5f, 1f), nameHeaderGroup.RectTransform),
                TextManager.Get("SaveSubDialogName"), font: GUIStyle.SubHeadingFont);

            submarineNameCharacterCount = new GUITextBlock(new RectTransform(new Vector2(.5f, 1f), nameHeaderGroup.RectTransform), string.Empty, textAlignment: Alignment.TopRight);

            nameBox = new GUITextBox(new RectTransform(new Vector2(1.0f, 0.05f), leftColumn.RectTransform))
            {
                OnEnterPressed = ChangeSubName
            };
            nameBox.OnTextChanged += (textBox, text) =>
            {
                if (text.Length > submarineNameLimit)
                {
                    nameBox.Text = text.Substring(0, submarineNameLimit);
                    nameBox.Flash(GUIStyle.Red);
                    return true;
                }

                submarineNameCharacterCount.Text = text.Length + " / " + submarineNameLimit;
                return true;
            };

            nameBox.Text = MainSub?.Info.Name ?? "";

            submarineNameCharacterCount.Text = nameBox.Text.Length + " / " + submarineNameLimit;

            var descriptionHeaderGroup = new GUILayoutGroup(new RectTransform(new Vector2(.975f, 0.03f), leftColumn.RectTransform), isHorizontal: true);

            new GUITextBlock(new RectTransform(new Vector2(0.5f, 1f), descriptionHeaderGroup.RectTransform), TextManager.Get("SaveSubDialogDescription"), font: GUIStyle.SubHeadingFont);
            submarineDescriptionCharacterCount = new GUITextBlock(new RectTransform(new Vector2(.5f, 1f), descriptionHeaderGroup.RectTransform), string.Empty, textAlignment: Alignment.TopRight);

            var descriptionContainer = new GUIListBox(new RectTransform(new Vector2(1.0f, 0.25f), leftColumn.RectTransform));
            descriptionBox = new GUITextBox(new RectTransform(Vector2.One, descriptionContainer.Content.RectTransform, Anchor.Center),
                font: GUIStyle.SmallFont, style: "GUITextBoxNoBorder", wrap: true, textAlignment: Alignment.TopLeft)
            {
                Padding = new Vector4(10 * GUI.Scale)
            };

            descriptionBox.OnTextChanged += (textBox, text) =>
            {
                if (text.Length > submarineDescriptionLimit)
                {
                    descriptionBox.Text = text.Substring(0, submarineDescriptionLimit);
                    descriptionBox.Flash(GUIStyle.Red);
                    return true;
                }

                Vector2 textSize = textBox.Font.MeasureString(descriptionBox.WrappedText);
                textBox.RectTransform.NonScaledSize = new Point(textBox.RectTransform.NonScaledSize.X, Math.Max(descriptionContainer.Content.Rect.Height, (int)textSize.Y + 10));
                descriptionContainer.UpdateScrollBarSize();
                descriptionContainer.BarScroll = 1.0f;
                ChangeSubDescription(textBox, text);
                return true;
            };

            descriptionBox.Text = GetSubDescription();

            var subTypeContainer = new GUILayoutGroup(new RectTransform(new Vector2(1.0f, 0.01f), leftColumn.RectTransform), isHorizontal: true, childAnchor: Anchor.CenterLeft)
            {
                Stretch = true
            };

            new GUITextBlock(new RectTransform(new Vector2(0.4f, 1f), subTypeContainer.RectTransform), TextManager.Get("submarinetype"));
            var subTypeDropdown = new GUIDropDown(new RectTransform(new Vector2(0.6f, 1f), subTypeContainer.RectTransform));
            subTypeContainer.RectTransform.MinSize = new Point(0, subTypeContainer.RectTransform.Children.Max(c => c.MinSize.Y));
            foreach (SubmarineType subType in Enum.GetValues(typeof(SubmarineType)))
            {
                if (subType == SubmarineType.Ruin) { continue; }
                string textTag = "SubmarineType." + subType;
                if (subType == SubmarineType.EnemySubmarine && !TextManager.ContainsTag(textTag))
                {
                    textTag = "MissionType.Pirate";
                }
                subTypeDropdown.AddItem(TextManager.Get(textTag), subType);
            }

            if (Layers.Any())
            {
                var layerVisibilityGroup = new GUILayoutGroup(new RectTransform(new Vector2(1.0f, 0.01f), leftColumn.RectTransform), isHorizontal: true, childAnchor: Anchor.CenterLeft);
                var visibleLayers = Layers.Where(l => !MainSub.Info.LayersHiddenByDefault.Contains(l.Key.ToIdentifier()));
                LocalizedString visibleLayersString = LocalizedString.Join(", ", visibleLayers.Select(l => TextManager.Capitalize(l.Key)) ?? ((LocalizedString)"None").ToEnumerable());
                new GUITextBlock(new RectTransform(new Vector2(0.5f, 1f), layerVisibilityGroup.RectTransform), TextManager.Get("editor.layer.visiblebydefault"), textAlignment: Alignment.CenterLeft);
                var layerVisibilityDropDown = new GUIDropDown(new RectTransform(new Vector2(0.5f, 1f), layerVisibilityGroup.RectTransform), text: visibleLayersString, selectMultiple: true);
                foreach (var layer in Layers)
                {
                    string layerName = layer.Key;
                    layerVisibilityDropDown.AddItem(TextManager.Capitalize(layerName), layerName);
                    if (visibleLayers.Contains(layer))
                    {
                        layerVisibilityDropDown.SelectItem(layerName);
                    }
                }
                layerVisibilityDropDown.AfterSelected += (button, _) =>
                {
                    MainSub.Info.LayersHiddenByDefault.Clear();
                    foreach (var layer in Layers)
                    {
                        if (!layerVisibilityDropDown.SelectedDataMultiple.Contains(layer.Key))
                        {
                            MainSub.Info.LayersHiddenByDefault.Add(layer.Key.ToIdentifier());
                        }
                    }
                    UpdateLayerPanel();
                    layerVisibilityDropDown.Text = ToolBox.LimitString(layerVisibilityDropDown.Text.Value, layerVisibilityDropDown.Font, layerVisibilityDropDown.Rect.Width);
                    return true;
                };
                layerVisibilityGroup.RectTransform.MinSize = layerVisibilityDropDown.RectTransform.MinSize = new Point(0, layerVisibilityDropDown.RectTransform.Children.Max(c => c.MinSize.Y));
            }

            //---------------------------------------

            var subTypeDependentSettingFrame = new GUIFrame(new RectTransform((1.0f, 0.6f), leftColumn.RectTransform), style: "InnerFrame");

            var outpostModuleSettingsContainer = new GUILayoutGroup(new RectTransform(Vector2.One, subTypeDependentSettingFrame.RectTransform))
            {
                CanBeFocused = true,
                Visible = false,
                Stretch = true
            };

            // module flags ---------------------

            var outpostModuleGroup = new GUILayoutGroup(new RectTransform(new Vector2(.975f, 0.1f), outpostModuleSettingsContainer.RectTransform), isHorizontal: true, childAnchor: Anchor.CenterLeft);

            new GUITextBlock(new RectTransform(new Vector2(0.5f, 1f), outpostModuleGroup.RectTransform), TextManager.Get("outpostmoduletype"), textAlignment: Alignment.CenterLeft);
            HashSet<Identifier> availableFlags = new HashSet<Identifier>();
            foreach (Identifier flag in OutpostGenerationParams.OutpostParams.SelectMany(p => p.ModuleCounts.Select(m => m.Identifier))) { availableFlags.Add(flag); }
            foreach (Identifier flag in RuinGeneration.RuinGenerationParams.RuinParams.SelectMany(p => p.ModuleCounts.Select(m => m.Identifier))) { availableFlags.Add(flag); }
            foreach (var sub in SubmarineInfo.SavedSubmarines)
            {
                if (sub.OutpostModuleInfo == null) { continue; }
                foreach (Identifier flag in sub.OutpostModuleInfo.ModuleFlags)
                {
                    if (flag == "none") { continue; }
                    availableFlags.Add(flag);
                }
            }
            if (MainSub?.Info?.OutpostModuleInfo is { } moduleInfo)
            {
                foreach (var moduleType in moduleInfo.ModuleFlags)
                {
                    availableFlags.Add(moduleType);
                }
            }
            var moduleTypeDropDown = new GUIDropDown(new RectTransform(new Vector2(0.5f, 1f), outpostModuleGroup.RectTransform),
                text: LocalizedString.Join(", ", MainSub?.Info?.OutpostModuleInfo?.ModuleFlags.Select(s => TextManager.Capitalize(s.Value)) ?? ((LocalizedString)"None").ToEnumerable()), selectMultiple: true);
            foreach (Identifier flag in availableFlags.OrderBy(f => f.Value, StringComparer.InvariantCultureIgnoreCase))
            {
                moduleTypeDropDown.AddItem(TextManager.Capitalize(flag.Value), flag);
                if (MainSub?.Info?.OutpostModuleInfo == null) { continue; }
                if (MainSub.Info.OutpostModuleInfo.ModuleFlags.Contains(flag))
                {
                    moduleTypeDropDown.SelectItem(flag);
                }
            }
            moduleTypeDropDown.AfterSelected += (_, __) =>
            {
                if (MainSub?.Info?.OutpostModuleInfo == null) { return false; }
                MainSub.Info.OutpostModuleInfo.SetFlags(moduleTypeDropDown.SelectedDataMultiple.Cast<Identifier>());
                moduleTypeDropDown.Text = ToolBox.LimitString(
                    MainSub.Info.OutpostModuleInfo.ModuleFlags.Any(f => f != "none") ? moduleTypeDropDown.Text : "None",
                    moduleTypeDropDown.Font, moduleTypeDropDown.Rect.Width);
                return true;
            };
            outpostModuleGroup.RectTransform.MinSize = new Point(0, outpostModuleGroup.RectTransform.Children.Max(c => c.MinSize.Y));

            var addTypeGroup = new GUILayoutGroup(new RectTransform(new Vector2(.975f, 0.1f), outpostModuleSettingsContainer.RectTransform), isHorizontal: true, childAnchor: Anchor.CenterLeft);
            new GUITextBlock(new RectTransform(new Vector2(0.5f, 1f), addTypeGroup.RectTransform), TextManager.Get("leveleditor.addmoduletype"), textAlignment: Alignment.CenterLeft);
            var textBox = new GUITextBox(new RectTransform(new Vector2(0.4f, 1f), addTypeGroup.RectTransform));

            new GUIButton(new RectTransform(new Vector2(0.1f, 0.9f), addTypeGroup.RectTransform), text: "+", style: "GUIButtonSmallFreeScale")
            {
                OnClicked = (btn, _) =>
                {
                    if (textBox.Text.IsNullOrEmpty())
                    {
                        textBox.Flash();
                        return false;
                    }
                    if (MainSub?.Info?.OutpostModuleInfo is { } moduleInfo)
                    {
                        moduleInfo.SetFlags(moduleInfo.ModuleFlags.Append(textBox.Text.ToIdentifier()).ToList());
                        //refresh
                        saveFrame = null;
                        CreateSaveScreen();
                    }
                    return true;
                }
            };
            addTypeGroup.RectTransform.MinSize = new Point(0, addTypeGroup.RectTransform.Children.Max(c => c.MinSize.Y));

            // module flags ---------------------

            var allowAttachGroup = new GUILayoutGroup(new RectTransform(new Vector2(.975f, 0.1f), outpostModuleSettingsContainer.RectTransform), isHorizontal: true, childAnchor: Anchor.CenterLeft);

            new GUITextBlock(new RectTransform(new Vector2(0.5f, 1f), allowAttachGroup.RectTransform), TextManager.Get("outpostmoduleallowattachto"), textAlignment: Alignment.CenterLeft);

            var allowAttachDropDown = new GUIDropDown(new RectTransform(new Vector2(0.5f, 1f), allowAttachGroup.RectTransform),
                text: LocalizedString.Join(", ", MainSub?.Info?.OutpostModuleInfo?.AllowAttachToModules.Select(s => TextManager.Capitalize(s.Value)) ?? ((LocalizedString)"Any").ToEnumerable()), selectMultiple: true);
            allowAttachDropDown.AddItem(TextManager.Capitalize("any"), "any".ToIdentifier());
            if (MainSub.Info.OutpostModuleInfo == null ||
                !MainSub.Info.OutpostModuleInfo.AllowAttachToModules.Any() ||
                MainSub.Info.OutpostModuleInfo.AllowAttachToModules.All(s => s == "any"))
            {
                allowAttachDropDown.SelectItem("any".ToIdentifier());
            }
            foreach (Identifier flag in availableFlags.OrderBy(f => f.Value, StringComparer.InvariantCultureIgnoreCase))
            {
                if (flag == "any" || flag == "none") { continue; }
                allowAttachDropDown.AddItem(TextManager.Capitalize(flag.Value), flag);
                if (MainSub?.Info?.OutpostModuleInfo == null) { continue; }
                if (MainSub.Info.OutpostModuleInfo.AllowAttachToModules.Contains(flag))
                {
                    allowAttachDropDown.SelectItem(flag);
                }
            }
            allowAttachDropDown.AfterSelected += (_, __) =>
            {
                if (MainSub?.Info?.OutpostModuleInfo == null) { return false; }
                MainSub.Info.OutpostModuleInfo.SetAllowAttachTo(allowAttachDropDown.SelectedDataMultiple.Cast<Identifier>());
                allowAttachDropDown.Text = ToolBox.LimitString(
                    MainSub.Info.OutpostModuleInfo.ModuleFlags.Any(f => f != "none") ? allowAttachDropDown.Text.Value : "None",
                    allowAttachDropDown.Font, allowAttachDropDown.Rect.Width);
                return true;
            };
            allowAttachGroup.RectTransform.MinSize = new Point(0, allowAttachGroup.RectTransform.Children.Max(c => c.MinSize.Y));

            // location types ---------------------

            var locationTypeGroup = new GUILayoutGroup(new RectTransform(new Vector2(.975f, 0.1f), outpostModuleSettingsContainer.RectTransform), isHorizontal: true, childAnchor: Anchor.CenterLeft);

            new GUITextBlock(new RectTransform(new Vector2(0.5f, 1f), locationTypeGroup.RectTransform), TextManager.Get("outpostmoduleallowedlocationtypes"), textAlignment: Alignment.CenterLeft);
            HashSet<Identifier> availableLocationTypes = new HashSet<Identifier>();
            foreach (LocationType locationType in LocationType.Prefabs) { availableLocationTypes.Add(locationType.Identifier); }

            var locationTypeDropDown = new GUIDropDown(new RectTransform(new Vector2(0.5f, 1f), locationTypeGroup.RectTransform),
                text: LocalizedString.Join(", ", MainSub?.Info?.OutpostModuleInfo?.AllowedLocationTypes.Select(lt => TextManager.Capitalize(lt.Value)) ?? ((LocalizedString)"any").ToEnumerable()), selectMultiple: true);
            locationTypeDropDown.AddItem(TextManager.Capitalize("any"), "any".ToIdentifier());
            foreach (Identifier locationType in availableLocationTypes.OrderBy(f => f.Value, StringComparer.InvariantCultureIgnoreCase))
            {
                locationTypeDropDown.AddItem(TextManager.Capitalize(locationType.Value), locationType);
                if (MainSub?.Info?.OutpostModuleInfo == null) { continue; }
                if (MainSub.Info.OutpostModuleInfo.AllowedLocationTypes.Contains(locationType))
                {
                    locationTypeDropDown.SelectItem(locationType);
                }
            }
            if (!MainSub.Info?.OutpostModuleInfo?.AllowedLocationTypes?.Any() ?? true) { locationTypeDropDown.SelectItem("any".ToIdentifier()); }

            locationTypeDropDown.AfterSelected += (_, __) =>
            {
                MainSub?.Info?.OutpostModuleInfo?.SetAllowedLocationTypes(locationTypeDropDown.SelectedDataMultiple.Cast<Identifier>());
                locationTypeDropDown.Text = ToolBox.LimitString(locationTypeDropDown.Text.Value, locationTypeDropDown.Font, locationTypeDropDown.Rect.Width);
                return true;
            };
            locationTypeGroup.RectTransform.MinSize = new Point(0, locationTypeGroup.RectTransform.Children.Max(c => c.MinSize.Y));

            // gap positions ---------------------

            var gapPositionGroup = new GUILayoutGroup(new RectTransform(new Vector2(.975f, 0.1f), outpostModuleSettingsContainer.RectTransform), isHorizontal: true, childAnchor: Anchor.CenterLeft);
            new GUITextBlock(new RectTransform(new Vector2(0.5f, 1f), gapPositionGroup.RectTransform), TextManager.Get("outpostmodulegappositions"), textAlignment: Alignment.CenterLeft);
            var gapPositionDropDown = new GUIDropDown(new RectTransform(new Vector2(0.5f, 1f), gapPositionGroup.RectTransform),
                text: "", selectMultiple: true);

            var outpostModuleInfo = MainSub.Info?.OutpostModuleInfo;
            if (outpostModuleInfo != null)
            {
                if (outpostModuleInfo.GapPositions == OutpostModuleInfo.GapPosition.None)
                {
                    outpostModuleInfo.DetermineGapPositions(MainSub);
                }
                foreach (OutpostModuleInfo.GapPosition gapPos in Enum.GetValues(typeof(OutpostModuleInfo.GapPosition)))
                {
                    if (gapPos == OutpostModuleInfo.GapPosition.None) { continue; }
                    gapPositionDropDown.AddItem(TextManager.Capitalize(gapPos.ToString()), gapPos);
                    if (outpostModuleInfo.GapPositions.HasFlag(gapPos))
                    {
                        gapPositionDropDown.SelectItem(gapPos);
                    }
                }
            }

            gapPositionDropDown.AfterSelected += (_, __) =>
            {
                if (MainSub.Info?.OutpostModuleInfo == null) { return false; }
                MainSub.Info.OutpostModuleInfo.GapPositions = OutpostModuleInfo.GapPosition.None;
                if (gapPositionDropDown.SelectedDataMultiple.Any())
                {
                    List<LocalizedString> gapPosTexts = new List<LocalizedString>();
                    foreach (OutpostModuleInfo.GapPosition gapPos in gapPositionDropDown.SelectedDataMultiple)
                    {
                        MainSub.Info.OutpostModuleInfo.GapPositions |= gapPos;
                        gapPosTexts.Add(TextManager.Capitalize(gapPos.ToString()));
                    }
                    gapPositionDropDown.Text = ToolBox.LimitString(string.Join(", ", gapPosTexts), gapPositionDropDown.Font, gapPositionDropDown.Rect.Width);
                }
                else
                {
                    gapPositionDropDown.Text = ToolBox.LimitString("None", gapPositionDropDown.Font, gapPositionDropDown.Rect.Width);
                }
                return true;
            };
            gapPositionGroup.RectTransform.MinSize = new Point(0, gapPositionGroup.RectTransform.Children.Max(c => c.MinSize.Y));

            var canAttachToPrevGroup = new GUILayoutGroup(new RectTransform(new Vector2(.975f, 0.1f), outpostModuleSettingsContainer.RectTransform), isHorizontal: true, childAnchor: Anchor.CenterLeft);
            new GUITextBlock(new RectTransform(new Vector2(0.5f, 1f), canAttachToPrevGroup.RectTransform), TextManager.Get("canattachtoprevious"), textAlignment: Alignment.CenterLeft)
            {
                ToolTip = TextManager.Get("canattachtoprevious.tooltip")
            };
            var canAttachToPrevDropDown = new GUIDropDown(new RectTransform(new Vector2(0.5f, 1f), canAttachToPrevGroup.RectTransform),
                text: "", selectMultiple: true);
            if (outpostModuleInfo != null)
            {
                foreach (OutpostModuleInfo.GapPosition gapPos in Enum.GetValues(typeof(OutpostModuleInfo.GapPosition)))
                {
                    if (gapPos == OutpostModuleInfo.GapPosition.None) { continue; }
                    canAttachToPrevDropDown.AddItem(TextManager.Capitalize(gapPos.ToString()), gapPos);
                    if (outpostModuleInfo.CanAttachToPrevious.HasFlag(gapPos))
                    {
                        canAttachToPrevDropDown.SelectItem(gapPos);
                    }
                }
            }

            canAttachToPrevDropDown.AfterSelected += (_, __) =>
            {
                if (Submarine.MainSub.Info?.OutpostModuleInfo == null) { return false; }
                Submarine.MainSub.Info.OutpostModuleInfo.CanAttachToPrevious = OutpostModuleInfo.GapPosition.None;
                if (canAttachToPrevDropDown.SelectedDataMultiple.Any())
                {
                    List<string> gapPosTexts = new List<string>();
                    foreach (OutpostModuleInfo.GapPosition gapPos in canAttachToPrevDropDown.SelectedDataMultiple)
                    {
                        Submarine.MainSub.Info.OutpostModuleInfo.CanAttachToPrevious |= gapPos;
                        gapPosTexts.Add(TextManager.Capitalize(gapPos.ToString()).Value);
                    }
                    canAttachToPrevDropDown.Text = ToolBox.LimitString(string.Join(", ", gapPosTexts), canAttachToPrevDropDown.Font, canAttachToPrevDropDown.Rect.Width);
                }
                else
                {
                    canAttachToPrevDropDown.Text = ToolBox.LimitString("None", canAttachToPrevDropDown.Font, canAttachToPrevDropDown.Rect.Width);
                }
                return true;
            };
            canAttachToPrevGroup.RectTransform.MinSize = new Point(0, gapPositionGroup.RectTransform.Children.Max(c => c.MinSize.Y));


            // -------------------

            var maxModuleCountGroup = new GUILayoutGroup(new RectTransform(new Vector2(1.0f, 0.05f), outpostModuleSettingsContainer.RectTransform), isHorizontal: true)
            {
                Stretch = true
            };
            new GUITextBlock(new RectTransform(new Vector2(0.6f, 1.0f), maxModuleCountGroup.RectTransform),
                TextManager.Get("OutPostModuleMaxCount"), textAlignment: Alignment.CenterLeft, wrap: true)
            {
                ToolTip = TextManager.Get("OutPostModuleMaxCountToolTip")
            };
            new GUINumberInput(new RectTransform(new Vector2(0.4f, 1.0f), maxModuleCountGroup.RectTransform), NumberType.Int)
            {
                ToolTip = TextManager.Get("OutPostModuleMaxCountToolTip"),
                IntValue = MainSub?.Info?.OutpostModuleInfo?.MaxCount ?? 1000,
                MinValueInt = 0,
                MaxValueInt = 1000,
                OnValueChanged = (numberInput) =>
                {
                    MainSub.Info.OutpostModuleInfo.MaxCount = numberInput.IntValue;
                }
            };
            maxModuleCountGroup.RectTransform.MinSize = new Point(0, maxModuleCountGroup.RectTransform.Children.Max(c => c.MinSize.Y));

            var commonnessGroup = new GUILayoutGroup(new RectTransform(new Vector2(1.0f, 0.05f), outpostModuleSettingsContainer.RectTransform), isHorizontal: true)
            {
                Stretch = true
            };
            new GUITextBlock(new RectTransform(new Vector2(0.6f, 1.0f), commonnessGroup.RectTransform),
                TextManager.Get("subeditor.outpostcommonness"), textAlignment: Alignment.CenterLeft, wrap: true);
            new GUINumberInput(new RectTransform(new Vector2(0.4f, 1.0f), commonnessGroup.RectTransform), NumberType.Float)
            {
                FloatValue = MainSub?.Info?.OutpostModuleInfo?.Commonness ?? 10,
                MinValueFloat = 0,
                MaxValueFloat = 100,
                OnValueChanged = (numberInput) =>
                {
                    MainSub.Info.OutpostModuleInfo.Commonness = numberInput.FloatValue;
                }
            };
            commonnessGroup.RectTransform.MinSize = new Point(0, commonnessGroup.RectTransform.Children.Max(c => c.MinSize.Y));

            outpostModuleSettingsContainer.RectTransform.MinSize = new Point(0, outpostModuleSettingsContainer.RectTransform.Children.Sum(c => c.Children.Any() ? c.Children.Max(c2 => c2.MinSize.Y) : 0));

            //---------------------------------------

            var extraSettingsContainer = new GUILayoutGroup(new RectTransform(new Vector2(1, 0.5f), subTypeDependentSettingFrame.RectTransform))
            {
                CanBeFocused = true,
                Visible = false,
                Stretch = true
            };

            var minDifficultyGroup = new GUILayoutGroup(new RectTransform(new Vector2(1.0f, 0.25f), extraSettingsContainer.RectTransform), isHorizontal: true)
            {
                Stretch = true
            };
            new GUITextBlock(new RectTransform(new Vector2(0.6f, 1.0f), minDifficultyGroup.RectTransform),
                TextManager.Get("minleveldifficulty"), textAlignment: Alignment.CenterLeft, wrap: true);
            var numInput = new GUINumberInput(new RectTransform(new Vector2(0.4f, 1.0f), minDifficultyGroup.RectTransform), NumberType.Int)
            {
                IntValue = (int)(MainSub?.Info?.GetExtraSubmarineInfo?.MinLevelDifficulty ?? 0),
                MinValueInt = 0,
                MaxValueInt = 100,
                OnValueChanged = (numberInput) =>
                {
                    MainSub.Info.GetExtraSubmarineInfo.MinLevelDifficulty = numberInput.IntValue;
                }
            };
            minDifficultyGroup.RectTransform.MaxSize = numInput.TextBox.RectTransform.MaxSize;
            var maxDifficultyGroup = new GUILayoutGroup(new RectTransform(new Vector2(1.0f, 0.25f), extraSettingsContainer.RectTransform), isHorizontal: true)
            {
                Stretch = true
            };
            new GUITextBlock(new RectTransform(new Vector2(0.6f, 1.0f), maxDifficultyGroup.RectTransform),
                TextManager.Get("maxleveldifficulty"), textAlignment: Alignment.CenterLeft, wrap: true);
            numInput = new GUINumberInput(new RectTransform(new Vector2(0.4f, 1.0f), maxDifficultyGroup.RectTransform), NumberType.Int)
            {
                IntValue = (int)(MainSub?.Info?.GetExtraSubmarineInfo?.MaxLevelDifficulty ?? 100),
                MinValueInt = 0,
                MaxValueInt = 100,
                OnValueChanged = (numberInput) =>
                {
                    MainSub.Info.GetExtraSubmarineInfo.MaxLevelDifficulty = numberInput.IntValue;
                }
            };
            maxDifficultyGroup.RectTransform.MaxSize = numInput.TextBox.RectTransform.MaxSize;


            //---------------------------------------

            var outpostSettingsContainer = new GUILayoutGroup(new RectTransform(Vector2.One, subTypeDependentSettingFrame.RectTransform))
            {
                CanBeFocused = true,
                Visible = false,
                Stretch = true
            };

            var outpostTagsGroup = new GUILayoutGroup(new RectTransform(new Vector2(1.0f, 0.25f), outpostSettingsContainer.RectTransform), isHorizontal: true)
            {
                Stretch = true
            };
            new GUITextBlock(new RectTransform(new Vector2(0.6f, 1.0f), outpostTagsGroup.RectTransform),
                TextManager.Get("sp.item.tags.name"), textAlignment: Alignment.CenterLeft, wrap: true);
            var outpostTagsBox = new GUITextBox(new RectTransform(new Vector2(0.4f, 1.0f), outpostTagsGroup.RectTransform))
            {
                OnEnterPressed = (GUITextBox textBox, string text) =>
                {
                    MainSub.Info.OutpostTags = text.ToIdentifiers().ToImmutableHashSet();
                    return true;
                },
                OverflowClip = true,
                Text = "default"
            };
            outpostTagsBox.OnDeselected += (textbox, _) =>
            {
                MainSub.Info.OutpostTags = outpostTagsBox.Text.ToIdentifiers().ToImmutableHashSet();
            };
            if (MainSub.Info.OutpostTags != null)
            {
                outpostTagsBox.Text = MainSub.Info.OutpostTags.ConvertToString();
            }

            outpostTagsGroup.RectTransform.MaxSize = outpostTagsBox.RectTransform.MaxSize;

            //---------------------------------------

            var enemySubmarineSettingsContainer = new GUILayoutGroup(new RectTransform(Vector2.One, subTypeDependentSettingFrame.RectTransform))
            {
                CanBeFocused = true,
                Visible = false,
                Stretch = true
            };

            // -------------------

            var enemySubmarineRewardGroup = new GUILayoutGroup(new RectTransform(new Vector2(1.0f, 0.25f), enemySubmarineSettingsContainer.RectTransform), isHorizontal: true)
            {
                Stretch = true
            };
            new GUITextBlock(new RectTransform(new Vector2(0.6f, 1.0f), enemySubmarineRewardGroup.RectTransform),
                TextManager.Get("enemysub.reward"), textAlignment: Alignment.CenterLeft, wrap: true);
            numInput = new GUINumberInput(new RectTransform(new Vector2(0.4f, 1.0f), enemySubmarineRewardGroup.RectTransform), NumberType.Int, buttonVisibility: GUINumberInput.ButtonVisibility.ForceHidden)
            {
                IntValue = (int)(MainSub?.Info?.EnemySubmarineInfo?.Reward ?? 4000),
                MinValueInt = 0,
                MaxValueInt = 999999,
                OnValueChanged = (numberInput) =>
                {
                    MainSub.Info.EnemySubmarineInfo.Reward = numberInput.IntValue;
                }
            };
            enemySubmarineRewardGroup.RectTransform.MaxSize = numInput.TextBox.RectTransform.MaxSize;
            var enemySubmarineDifficultyGroup = new GUILayoutGroup(new RectTransform(new Vector2(1.0f, 0.25f), enemySubmarineSettingsContainer.RectTransform), isHorizontal: true)
            {
                Stretch = true
            };
            new GUITextBlock(new RectTransform(new Vector2(0.6f, 1.0f), enemySubmarineDifficultyGroup.RectTransform),
                TextManager.Get("preferreddifficulty"), textAlignment: Alignment.CenterLeft, wrap: true);
            numInput = new GUINumberInput(new RectTransform(new Vector2(0.4f, 1.0f), enemySubmarineDifficultyGroup.RectTransform), NumberType.Int)
            {
                IntValue = (int)(MainSub?.Info?.EnemySubmarineInfo?.PreferredDifficulty ?? 50),
                MinValueInt = 0,
                MaxValueInt = 100,
                OnValueChanged = (numberInput) =>
                {
                    MainSub.Info.EnemySubmarineInfo.PreferredDifficulty = numberInput.IntValue;
                }
            };
            enemySubmarineDifficultyGroup.RectTransform.MaxSize = numInput.TextBox.RectTransform.MaxSize;
            var enemySubmarineTagsGroup = new GUILayoutGroup(new RectTransform(new Vector2(1.0f, 0.25f), enemySubmarineSettingsContainer.RectTransform), isHorizontal: true)
            {
                Stretch = true
            };
            new GUITextBlock(new RectTransform(new Vector2(0.6f, 1.0f), enemySubmarineTagsGroup.RectTransform),
                TextManager.Get("sp.item.tags.name"), textAlignment: Alignment.CenterLeft, wrap: true);
            var tagsBox = new GUITextBox(new RectTransform(new Vector2(0.4f, 1.0f), enemySubmarineTagsGroup.RectTransform))
            {
                OnEnterPressed = ChangeEnemySubTags,
                OverflowClip = true,
                Text = "default"
            };
            tagsBox.OnDeselected += (textbox, _) => ChangeEnemySubTags(textbox, textbox.Text);
            if (MainSub?.Info?.EnemySubmarineInfo?.MissionTags != null)
            {
                tagsBox.Text = string.Join(',', MainSub.Info.EnemySubmarineInfo.MissionTags);
            }

            enemySubmarineTagsGroup.RectTransform.MaxSize = tagsBox.RectTransform.MaxSize;
            enemySubmarineSettingsContainer.RectTransform.MinSize = new Point(0, enemySubmarineSettingsContainer.RectTransform.Children.Sum(c => c.Children.Any() ? c.Children.Max(c2 => c2.MinSize.Y) : 0));

            //--------------------------------------------------------

            var beaconSettingsContainer = new GUILayoutGroup(new RectTransform(Vector2.One, extraSettingsContainer.RectTransform))
            {
                CanBeFocused = true,
                Visible = false,
                Stretch = true
            };

            new GUITickBox(new RectTransform(new Vector2(1.0f, 0.25f), beaconSettingsContainer.RectTransform), TextManager.Get("allowdamagedwalls"))
            {
                Selected = MainSub?.Info?.BeaconStationInfo?.AllowDamagedWalls ?? true,
                OnSelected = (tb) =>
                {
                    MainSub.Info.BeaconStationInfo.AllowDamagedWalls = tb.Selected;
                    return true;
                }
            };
            new GUITickBox(new RectTransform(new Vector2(1.0f, 0.25f), beaconSettingsContainer.RectTransform), TextManager.Get("allowdamageddevices"))
            {
                Selected = MainSub?.Info?.BeaconStationInfo?.AllowDamagedDevices ?? true,
                OnSelected = (tb) =>
                {
                    MainSub.Info.BeaconStationInfo.AllowDamagedDevices = tb.Selected;
                    return true;
                }
            };
            new GUITickBox(new RectTransform(new Vector2(1.0f, 0.25f), beaconSettingsContainer.RectTransform), TextManager.Get("allowdisconnectedwires"))
            {
                Selected = MainSub?.Info?.BeaconStationInfo?.AllowDisconnectedWires ?? true,
                OnSelected = (tb) =>
                {
                    MainSub.Info.BeaconStationInfo.AllowDisconnectedWires = tb.Selected;
                    return true;
                }
            };
            new GUITickBox(new RectTransform(new Vector2(1.0f, 0.25f), beaconSettingsContainer.RectTransform), TextManager.Get("beaconstationplacement"))
            {
                Selected = MainSub.Info.BeaconStationInfo is { Placement: Level.PlacementType.Top },
                OnSelected = (tb) =>
                {
                    MainSub.Info.BeaconStationInfo.Placement = tb.Selected ? 
                        Level.PlacementType.Top :
                        Level.PlacementType.Bottom;
                    return true;
                }
            };
            beaconSettingsContainer.RectTransform.MinSize = new Point(0, beaconSettingsContainer.RectTransform.Children.Sum(c => c.Children.Any() ? c.Children.Max(c2 => c2.MinSize.Y) : 0));

            //------------------------------------------------------------------

            var subSettingsContainer = new GUILayoutGroup(new RectTransform(Vector2.One, subTypeDependentSettingFrame.RectTransform))
            {
                Stretch = true
            };

            var priceGroup = new GUILayoutGroup(new RectTransform(new Vector2(1.0f, 0.05f), subSettingsContainer.RectTransform), isHorizontal: true)
            {
                Stretch = true
            };
            new GUITextBlock(new RectTransform(new Vector2(0.6f, 1.0f), priceGroup.RectTransform),
                TextManager.Get("subeditor.price"), textAlignment: Alignment.CenterLeft, wrap: true);


            int basePrice = (GameMain.DebugDraw ? 0 : MainSub?.CalculateBasePrice()) ?? 1000;
            new GUINumberInput(new RectTransform(new Vector2(0.4f, 1.0f), priceGroup.RectTransform), NumberType.Int, buttonVisibility: GUINumberInput.ButtonVisibility.ForceHidden)
            {
                IntValue = Math.Max(MainSub?.Info?.Price ?? basePrice, basePrice),
                MinValueInt = basePrice,
                MaxValueInt = 999999,
                OnValueChanged = (numberInput) =>
                {
                    MainSub.Info.Price = numberInput.IntValue;
                }
            };
            if (MainSub?.Info != null)
            {
                MainSub.Info.Price = Math.Max(MainSub.Info.Price, basePrice);
            }

            var classGroup = new GUILayoutGroup(new RectTransform(new Vector2(1.0f, 0.05f), subSettingsContainer.RectTransform), isHorizontal: true, childAnchor: Anchor.CenterLeft)
            {
                Stretch = true
            };
            var classText = new GUITextBlock(new RectTransform(new Vector2(0.6f, 1.0f), classGroup.RectTransform),
                TextManager.Get("submarineclass"), textAlignment: Alignment.CenterLeft, wrap: true)
            {
                ToolTip = TextManager.Get("submarineclass.description")
            };
            GUIDropDown classDropDown = new GUIDropDown(new RectTransform(new Vector2(0.4f, 1.0f), classGroup.RectTransform));
            classDropDown.RectTransform.MinSize = new Point(0, subTypeContainer.RectTransform.Children.Max(c => c.MinSize.Y));
            foreach (SubmarineClass subClass in SubmarineClass.Classes)
            {
                classDropDown.AddItem(subClass.Name, subClass, toolTip: subClass.Description);
            }
            classDropDown.AddItem(TextManager.Get(nameof(SubmarineTag.Shuttle)), SubmarineTag.Shuttle);
            classDropDown.OnSelected += (selected, userdata) =>
            {
                switch (userdata)
                {
                    case SubmarineClass submarineClass:
                        MainSub.Info.RemoveTag(SubmarineTag.Shuttle);
                        MainSub.Info.Class = submarineClass;
                        break;
                    case SubmarineTag.Shuttle:
                        MainSub.Info.AddTag(SubmarineTag.Shuttle);
                        MainSub.Info.Class = SubmarineClass.Undefined;
                        break;
                }
                return true;
            };
            classDropDown.ListBox.Content.FindChild(SubmarineClass.Undefined).SetAsFirstChild();
            classDropDown.SelectItem(!MainSub.Info.HasTag(SubmarineTag.Shuttle) ? MainSub.Info.Class : (object)SubmarineTag.Shuttle);

            var tierGroup = new GUILayoutGroup(new RectTransform(new Vector2(1.0f, 0.05f), subSettingsContainer.RectTransform), isHorizontal: true)
            {
                Stretch = true
            };
            new GUITextBlock(new RectTransform(new Vector2(0.6f, 1.0f), tierGroup.RectTransform),
                TextManager.Get("subeditor.tier"), textAlignment: Alignment.CenterLeft, wrap: true)
            {
                ToolTip = TextManager.Get("submarinetier.description")
            };

            new GUINumberInput(new RectTransform(new Vector2(0.4f, 1.0f), tierGroup.RectTransform), NumberType.Int)
            {
                IntValue = MainSub.Info.Tier,
                MinValueInt = 1,
                MaxValueInt = SubmarineInfo.HighestTier,
                OnValueChanged = (numberInput) =>
                {
                    MainSub.Info.Tier = numberInput.IntValue;
                }
            };
            if (MainSub?.Info != null)
            {
                MainSub.Info.Tier = Math.Clamp(MainSub.Info.Tier, 1, SubmarineInfo.HighestTier);
            }

            var crewSizeArea = new GUILayoutGroup(new RectTransform(new Vector2(1.0f, 0.05f), subSettingsContainer.RectTransform), isHorizontal: true)
            {
                Stretch = true,
                AbsoluteSpacing = 5
            };

            new GUITextBlock(new RectTransform(new Vector2(0.6f, 1.0f), crewSizeArea.RectTransform),
                TextManager.Get("RecommendedCrewSize"), textAlignment: Alignment.CenterLeft, wrap: true);
            var crewSizeMin = new GUINumberInput(new RectTransform(new Vector2(0.17f, 1.0f), crewSizeArea.RectTransform), NumberType.Int, relativeButtonAreaWidth: 0.25f)
            {
                MinValueInt = 1,
                MaxValueInt = 128
            };
            new GUITextBlock(new RectTransform(new Vector2(0.06f, 1.0f), crewSizeArea.RectTransform), "-", textAlignment: Alignment.Center);
            var crewSizeMax = new GUINumberInput(new RectTransform(new Vector2(0.17f, 1.0f), crewSizeArea.RectTransform), NumberType.Int, relativeButtonAreaWidth: 0.25f)
            {
                MinValueInt = 1,
                MaxValueInt = 128
            };

            crewSizeMin.OnValueChanged += (numberInput) =>
            {
                crewSizeMax.IntValue = Math.Max(crewSizeMax.IntValue, numberInput.IntValue);
                MainSub.Info.RecommendedCrewSizeMin = crewSizeMin.IntValue;
                MainSub.Info.RecommendedCrewSizeMax = crewSizeMax.IntValue;
            };

            crewSizeMax.OnValueChanged += (numberInput) =>
            {
                crewSizeMin.IntValue = Math.Min(crewSizeMin.IntValue, numberInput.IntValue);
                MainSub.Info.RecommendedCrewSizeMin = crewSizeMin.IntValue;
                MainSub.Info.RecommendedCrewSizeMax = crewSizeMax.IntValue;
            };

            var crewExpArea = new GUILayoutGroup(new RectTransform(new Vector2(1.0f, 0.05f), subSettingsContainer.RectTransform), isHorizontal: true)
            {
                Stretch = true,
                AbsoluteSpacing = 5
            };

            new GUITextBlock(new RectTransform(new Vector2(0.6f, 1.0f), crewExpArea.RectTransform),
                TextManager.Get("RecommendedCrewExperience"), textAlignment: Alignment.CenterLeft, wrap: true);

            var toggleExpLeft = new GUIButton(new RectTransform(new Vector2(0.05f, 1.0f), crewExpArea.RectTransform), style: "GUIButtonToggleLeft");
            var experienceText = new GUITextBlock(new RectTransform(new Vector2(0.3f, 1.0f), crewExpArea.RectTransform),
                text: TextManager.Get(SubmarineInfo.CrewExperienceLevel.CrewExperienceLow.ToIdentifier()), textAlignment: Alignment.Center);
            var toggleExpRight = new GUIButton(new RectTransform(new Vector2(0.05f, 1.0f), crewExpArea.RectTransform), style: "GUIButtonToggleRight");

            toggleExpLeft.OnClicked += (btn, userData) =>
            {
                MainSub.Info.RecommendedCrewExperience--;
                if (MainSub.Info.RecommendedCrewExperience < SubmarineInfo.CrewExperienceLevel.CrewExperienceLow)
                {
                    MainSub.Info.RecommendedCrewExperience = SubmarineInfo.CrewExperienceLevel.CrewExperienceHigh;
                }
                experienceText.Text = TextManager.Get(MainSub.Info.RecommendedCrewExperience.ToIdentifier());
                return true;
            };

            toggleExpRight.OnClicked += (btn, userData) =>
            {
                MainSub.Info.RecommendedCrewExperience++;
                if (MainSub.Info.RecommendedCrewExperience > SubmarineInfo.CrewExperienceLevel.CrewExperienceHigh)
                {
                    MainSub.Info.RecommendedCrewExperience = SubmarineInfo.CrewExperienceLevel.CrewExperienceLow;
                }
                experienceText.Text = TextManager.Get(MainSub.Info.RecommendedCrewExperience.ToIdentifier());
                return true;
            };
            
            var hideInMenusArea = new GUILayoutGroup(new RectTransform(new Vector2(1.0f, 0.05f), subSettingsContainer.RectTransform), isHorizontal: true, childAnchor: Anchor.CenterLeft)
            {
                Stretch = true,
                AbsoluteSpacing = 5
            };
            new GUITextBlock(new RectTransform(new Vector2(0.6f, 1.0f), hideInMenusArea.RectTransform),
                TextManager.Get("HideInMenus"), textAlignment: Alignment.CenterLeft, wrap: true);

            new GUITickBox(new RectTransform((0.4f, 1.0f), hideInMenusArea.RectTransform), "")
            {
                Selected = MainSub.Info.HasTag(SubmarineTag.HideInMenus),
                OnSelected = box =>
                {
                    if (box.Selected)
                    {
                        MainSub.Info.AddTag(SubmarineTag.HideInMenus);
                    }
                    else
                    {
                        MainSub.Info.RemoveTag(SubmarineTag.HideInMenus);
                    }
                    return true;
                }
            };

            var outFittingArea = new GUILayoutGroup(new RectTransform(new Vector2(1.0f, 0.05f), subSettingsContainer.RectTransform), isHorizontal: true, childAnchor: Anchor.CenterLeft)
            {
                Stretch = true,
                AbsoluteSpacing = 5
            };
            new GUITextBlock(new RectTransform(new Vector2(0.6f, 1.0f), outFittingArea.RectTransform),
                TextManager.Get("ManuallyOutfitted"), textAlignment: Alignment.CenterLeft, wrap: true)
            {
                ToolTip = TextManager.Get("manuallyoutfittedtooltip")
            };
            new GUITickBox(new RectTransform((0.4f, 1.0f), outFittingArea.RectTransform), "")
            {
                ToolTip = TextManager.Get("manuallyoutfittedtooltip"),
                Selected = MainSub.Info.IsManuallyOutfitted,
                OnSelected = box =>
                {
                    MainSub.Info.IsManuallyOutfitted = box.Selected;
                    return true;
                }
            };

            if (MainSub != null)
            {
                int min =  MainSub.Info.RecommendedCrewSizeMin;
                int max = MainSub.Info.RecommendedCrewSizeMax;
                crewSizeMin.IntValue = min;
                crewSizeMax.IntValue = max;
                if (MainSub.Info.RecommendedCrewExperience == SubmarineInfo.CrewExperienceLevel.Unknown)
                {
                    MainSub.Info.RecommendedCrewExperience = SubmarineInfo.CrewExperienceLevel.CrewExperienceLow;
                }
                experienceText.Text = TextManager.Get(MainSub.Info.RecommendedCrewExperience.ToIdentifier());
            }

            subTypeDropdown.OnSelected += (selected, userdata) =>
            {
                SubmarineType type = (SubmarineType)userdata;
                MainSub.Info.Type = type;
                if (type == SubmarineType.OutpostModule)
                {
                    MainSub.Info.OutpostModuleInfo ??= new OutpostModuleInfo(MainSub.Info);
                }
                else if (type == SubmarineType.BeaconStation)
                {
                    MainSub.Info.BeaconStationInfo ??= new BeaconStationInfo(MainSub.Info);
                }
                else if (type == SubmarineType.Wreck)
                {
                    MainSub.Info.WreckInfo ??= new WreckInfo(MainSub.Info);
                }
                else if (type == SubmarineType.EnemySubmarine)
                {
                    MainSub.Info.EnemySubmarineInfo ??= new EnemySubmarineInfo(MainSub.Info);
                }
                previewImageButtonHolder.Children.ForEach(c => c.Enabled = MainSub.Info.AllowPreviewImage);
                outpostModuleSettingsContainer.Visible = type == SubmarineType.OutpostModule;
                extraSettingsContainer.Visible = type == SubmarineType.BeaconStation || type == SubmarineType.Wreck;
                beaconSettingsContainer.Visible = type == SubmarineType.BeaconStation;
                enemySubmarineSettingsContainer.Visible = type == SubmarineType.EnemySubmarine;
                subSettingsContainer.Visible = type == SubmarineType.Player;
                outpostSettingsContainer.Visible = type == SubmarineType.Outpost;
                return true;
            };
            subSettingsContainer.RectTransform.MinSize = new Point(0, subSettingsContainer.RectTransform.Children.Sum(c => c.Children.Any() ? c.Children.Max(c2 => c2.MinSize.Y) : 0));

            int minHeight = subSettingsContainer.Children.First().Children.Max(c => c.RectTransform.MinSize.Y);
            foreach (var child in subSettingsContainer.Children)
            {
                child.RectTransform.MinSize = new Point(0, minHeight);
            }

            // right column ---------------------------------------------------
            
            new GUITextBlock(new RectTransform(new Vector2(1.0f, 0.0f), rightColumn.RectTransform), TextManager.Get("SubPreviewImage"), font: GUIStyle.SubHeadingFont);

            var previewImageHolder = new GUIFrame(new RectTransform(new Vector2(1.0f, 0.4f), rightColumn.RectTransform), style: null) { Color = Color.Black, CanBeFocused = false };
            previewImage = new GUIImage(new RectTransform(Vector2.One, previewImageHolder.RectTransform), MainSub?.Info.PreviewImage, scaleToFit: true);

            previewImageButtonHolder = new GUILayoutGroup(new RectTransform(new Vector2(1.0f, 0.05f), rightColumn.RectTransform), isHorizontal: true) { Stretch = true, RelativeSpacing = 0.05f };

            new GUIButton(new RectTransform(new Vector2(0.5f, 1.0f), previewImageButtonHolder.RectTransform), TextManager.Get("SubPreviewImageCreate"), style: "GUIButtonSmall")
            {
                Enabled = MainSub?.Info.AllowPreviewImage ?? false,
                OnClicked = (btn, userdata) =>
                {
                    using (System.IO.MemoryStream imgStream = new System.IO.MemoryStream())
                    {
                        CreateImage(defaultPreviewImageSize.X, defaultPreviewImageSize.Y, imgStream);
                        previewImage.Sprite = new Sprite(TextureLoader.FromStream(imgStream, compress: false), null, null);
                        if (MainSub != null)
                        {
                            MainSub.Info.PreviewImage = previewImage.Sprite;
                        }
                    }
                    return true;
                }
            };

            new GUIButton(new RectTransform(new Vector2(0.5f, 1.0f), previewImageButtonHolder.RectTransform), TextManager.Get("SubPreviewImageBrowse"), style: "GUIButtonSmall")
            {
                Enabled = MainSub?.Info.AllowPreviewImage ?? false,
                OnClicked = (btn, userdata) =>
                {
                    FileSelection.OnFileSelected = (file) =>
                    {
                        if (new FileInfo(file).Length > 2048 * 2048)
                        {
                            new GUIMessageBox(TextManager.Get("Error"), TextManager.Get("WorkshopItemPreviewImageTooLarge"));
                            return;
                        }

                        previewImage.Sprite = new Sprite(file, sourceRectangle: null);
                        if (MainSub != null)
                        {
                            MainSub.Info.PreviewImage = previewImage.Sprite;
                        }
                    };
                    FileSelection.ClearFileTypeFilters();
                    FileSelection.AddFileTypeFilter("PNG", "*.png");
                    FileSelection.AddFileTypeFilter("JPEG", "*.jpg, *.jpeg");
                    FileSelection.AddFileTypeFilter("All files", "*.*");
                    FileSelection.SelectFileTypeFilter("*.png");
                    FileSelection.Open = true;
                    return false;
                }
            };

            previewImageButtonHolder.RectTransform.MinSize = new Point(0, previewImageButtonHolder.RectTransform.Children.Max(c => c.MinSize.Y));

            var contentPackageTabber = new GUILayoutGroup(new RectTransform((1.0f, 0.075f), rightColumn.RectTransform), isHorizontal: true);

            GUIButton createTabberBtn(string labelTag)
            {
                var btn = new GUIButton(new RectTransform((0.5f, 1.0f), contentPackageTabber.RectTransform, Anchor.BottomCenter, Pivot.BottomCenter), TextManager.Get(labelTag), style: "GUITabButton");
                btn.TextBlock.Wrap = true;
                btn.TextBlock.SetTextPos();
                btn.RectTransform.MaxSize = RectTransform.MaxPoint;
                btn.Children.ForEach(c => c.RectTransform.MaxSize = RectTransform.MaxPoint);
                btn.Font = GUIStyle.SmallFont;
                return btn;
            }

            var saveToPackageTabBtn = createTabberBtn("SaveToLocalPackage");
            saveToPackageTabBtn.Selected = true;
            var reqPackagesTabBtn = createTabberBtn("RequiredContentPackages");
            reqPackagesTabBtn.Selected = false;
            
            var horizontalArea = new GUIFrame(new RectTransform(new Vector2(1.0f, 0.45f), rightColumn.RectTransform), style: null);

            var saveInPackageLayout = new GUILayoutGroup(new RectTransform(Vector2.One,
                horizontalArea.RectTransform, Anchor.BottomRight))
            {
                Stretch = true
            };
            
            var packageToSaveInList = new GUIListBox(new RectTransform(new Vector2(1.0f, 1.0f),
                saveInPackageLayout.RectTransform));

            var packToSaveInFilter
                = new GUITextBox(new RectTransform((1.0f, 0.15f), saveInPackageLayout.RectTransform),
                    createClearButton: true);

            GUILayoutGroup addItemToPackageToSaveList(LocalizedString itemText, ContentPackage p)
            {
                var listItem = new GUIFrame(new RectTransform((1.0f, 0.15f), packageToSaveInList.Content.RectTransform),
                    style: "ListBoxElement")
                {
                    UserData = p
                };
                if (p != null && p != ContentPackageManager.VanillaCorePackage) { listItem.ToolTip = p.Dir; }
                var retVal =
                    new GUILayoutGroup(new RectTransform(Vector2.One, listItem.RectTransform),
                        isHorizontal: true) { Stretch = true };
                var iconFrame =
                    new GUIFrame(
                        new RectTransform(Vector2.One, retVal.RectTransform, scaleBasis: ScaleBasis.BothHeight),
                        style: null) { CanBeFocused = false };
                var pkgText = new GUITextBlock(new RectTransform(Vector2.One, retVal.RectTransform), itemText)
                    { CanBeFocused = false };
                return retVal;
            }

#if DEBUG
            //this is a debug-only option so I won't bother submitting it for localization
            var modifyVanillaListItem = addItemToPackageToSaveList("Modify Vanilla content package", ContentPackageManager.VanillaCorePackage);
            var modifyVanillaListIcon = modifyVanillaListItem.GetChild<GUIFrame>();
            GUIStyle.Apply(modifyVanillaListIcon, "WorkshopMenu.EditButton");
#endif
            
            var newPackageListItem = addItemToPackageToSaveList(TextManager.Get("CreateNewLocalPackage"), null);
            var newPackageListIcon = newPackageListItem.GetChild<GUIFrame>();
            var newPackageListText = newPackageListItem.GetChild<GUITextBlock>();
            GUIStyle.Apply(newPackageListIcon, "NewContentPackageIcon");
            new GUICustomComponent(new RectTransform(Vector2.Zero, saveInPackageLayout.RectTransform),
                onUpdate: (f, component) =>
                {
                    foreach (GUIComponent contentChild in packageToSaveInList.Content.Children)
                    {
                        contentChild.Visible &= !(contentChild.GetChild<GUILayoutGroup>()?.GetChild<GUITextBlock>() is GUITextBlock tb &&
                                                  !tb.Text.Contains(packToSaveInFilter.Text, StringComparison.OrdinalIgnoreCase));
                    }
                });
            ContentPackage ownerPkg = null;
            if (MainSub?.Info != null) { ownerPkg = GetLocalPackageThatOwnsSub(MainSub.Info); }
            foreach (var p in ContentPackageManager.LocalPackages)
            {
                var packageListItem = addItemToPackageToSaveList(p.Name, p);
                if (p == ownerPkg)
                {
                    var packageListIcon = packageListItem.GetChild<GUIFrame>();
                    var packageListText = packageListItem.GetChild<GUITextBlock>();
                    GUIStyle.Apply(packageListIcon, "WorkshopMenu.EditButton");
                    packageListText.Text = TextManager.GetWithVariable("UpdateExistingLocalPackage", "[mod]", p.Name);
                }
            }
            if (ownerPkg != null)
            {
                var element = packageToSaveInList.Content.FindChild(ownerPkg);
                element?.RectTransform.SetAsFirstChild();
            }
            packageToSaveInList.Select(0);

            var requiredContentPackagesLayout = new GUILayoutGroup(new RectTransform(Vector2.One,
                horizontalArea.RectTransform, Anchor.BottomRight))
            {
                Stretch = true,
                Visible = false
            };
            
            var requiredContentPackList = new GUIListBox(new RectTransform(new Vector2(1.0f, 1.0f),
                requiredContentPackagesLayout.RectTransform));

            var filterLayout = new GUILayoutGroup(
                new RectTransform((1.0f, 0.15f), requiredContentPackagesLayout.RectTransform),
                isHorizontal: true, childAnchor: Anchor.CenterLeft);
            
            var contentPackFilter
                = new GUITextBox(new RectTransform((0.6f, 1.0f), filterLayout.RectTransform),
                    createClearButton: true);
            contentPackFilter.OnTextChanged += (box, text) =>
            {
                requiredContentPackList.Content.Children.ForEach(c
                    => c.Visible = !(c is GUITickBox tb &&
                                     !tb.Text.Contains(text, StringComparison.OrdinalIgnoreCase)));
                return true;
            };

            var autoDetectBtn = new GUIButton(new RectTransform((0.4f, 1.0f), filterLayout.RectTransform),
                text: TextManager.Get("AutoDetectRequiredPackages"), style: "GUIButtonSmall")
            {
                OnClicked = (button, o) =>
                {
                    var requiredPackages = MapEntity.MapEntityList.Select(e => e?.Prefab?.ContentPackage)
                        .Where(cp => cp != null)
                        .Distinct().OfType<ContentPackage>().Select(p => p.Name).ToHashSet();
                    var tickboxes = requiredContentPackList.Content.Children.OfType<GUITickBox>().ToArray();
                    tickboxes.ForEach(tb => tb.Selected = requiredPackages.Contains(tb.UserData as string ?? ""));
                    return false;
                }
            };

            if (MainSub != null)
            {
                List<string> allContentPacks = MainSub.Info.RequiredContentPackages.ToList();
                foreach (ContentPackage contentPack in ContentPackageManager.AllPackages)
                {
                    //don't show content packages that only define submarine files
                    //(it doesn't make sense to require another sub to be installed to install this one)
                    if (contentPack.Files.All(f => f is SubmarineFile || f is ItemAssemblyFile)) { continue; }

                    if (!allContentPacks.Contains(contentPack.Name))
                    {
                        string altName = contentPack.AltNames.FirstOrDefault(n => allContentPacks.Contains(n));
                        if (!string.IsNullOrEmpty(altName))
                        {
                            if (MainSub.Info.RequiredContentPackages.Contains(altName))
                            {
                                MainSub.Info.RequiredContentPackages.Remove(altName);
                                MainSub.Info.RequiredContentPackages.Add(contentPack.Name);
                            }
                            allContentPacks.Remove(altName);
                        }
                        allContentPacks.Add(contentPack.Name);
                    }
                }

                foreach (string contentPackageName in allContentPacks)
                {
                    var cpTickBox = new GUITickBox(new RectTransform(new Vector2(1.0f, 0.2f), requiredContentPackList.Content.RectTransform), contentPackageName, font: GUIStyle.SmallFont)
                    {
                        Selected = MainSub.Info.RequiredContentPackages.Contains(contentPackageName),
                        UserData = contentPackageName
                    };
                    cpTickBox.OnSelected += tickBox =>
                    {
                        if (tickBox.Selected)
                        {
                            MainSub.Info.RequiredContentPackages.Add((string)tickBox.UserData);
                        }
                        else
                        {
                            MainSub.Info.RequiredContentPackages.Remove((string)tickBox.UserData);
                        }
                        return true;
                    };
                }
            }

            GUIButton.OnClickedHandler switchToTab(GUIButton tabBtn, GUIComponent tab)
                => (button, obj) =>
                {
                    horizontalArea.Children.ForEach(c => c.Visible = false);
                    contentPackageTabber.Children.ForEach(c => c.Selected = false);
                    tabBtn.Selected = true;
                    tab.Visible = true;
                    return false;
                };

            saveToPackageTabBtn.OnClicked = switchToTab(saveToPackageTabBtn, saveInPackageLayout);
            reqPackagesTabBtn.OnClicked = switchToTab(reqPackagesTabBtn, requiredContentPackagesLayout);
            
            var buttonArea = new GUIFrame(new RectTransform(new Vector2(1.0f, 0.05f), paddedSaveFrame.RectTransform, Anchor.BottomCenter, minSize: new Point(0, 30)), style: null);

            var cancelButton = new GUIButton(new RectTransform(new Vector2(0.3f, 1.0f), buttonArea.RectTransform, Anchor.BottomLeft),
                TextManager.Get("Cancel"))
            {
                OnClicked = (GUIButton btn, object userdata) =>
                {
                    saveFrame = null;
                    return true;
                }
            };

            var saveButton = new GUIButton(new RectTransform(new Vector2(0.3f, 1.0f), buttonArea.RectTransform, Anchor.BottomRight),
                TextManager.Get("SaveSubButton").Fallback(TextManager.Get("save")))
            {
                OnClicked = (button, o) => SaveSub(packageToSaveInList.SelectedData as ContentPackage)
            };
            paddedSaveFrame.Recalculate();
            leftColumn.Recalculate();

            subSettingsContainer.RectTransform.MinSize = outpostModuleSettingsContainer.RectTransform.MinSize = beaconSettingsContainer.RectTransform.MinSize =
                new Point(0, Math.Max(subSettingsContainer.Rect.Height, outpostModuleSettingsContainer.Rect.Height));
            subSettingsContainer.Recalculate();
            outpostModuleSettingsContainer.Recalculate();
            beaconSettingsContainer.Recalculate();
            enemySubmarineSettingsContainer.Recalculate();

            descriptionBox.Text = MainSub == null ? "" : MainSub.Info.Description.Value;
            submarineDescriptionCharacterCount.Text = descriptionBox.Text.Length + " / " + submarineDescriptionLimit;

            subTypeDropdown.SelectItem(MainSub.Info.Type);

            if (quickSave) { SaveSub(packageToSaveInList.SelectedData as ContentPackage); }
        }

        private void CreateSaveAssemblyScreen()
        {
            SetMode(Mode.Default);

            saveFrame = new GUIButton(new RectTransform(Vector2.One, GUI.Canvas, Anchor.Center), style: null)
            {
                OnClicked = (btn, userdata) => { if (GUI.MouseOn == btn || GUI.MouseOn == btn.TextBlock) saveFrame = null; return true; }
            };

            new GUIFrame(new RectTransform(GUI.Canvas.RelativeSize, saveFrame.RectTransform, Anchor.Center), style: "GUIBackgroundBlocker");

            var innerFrame = new GUIFrame(new RectTransform(new Vector2(0.25f, 0.35f), saveFrame.RectTransform, Anchor.Center) { MinSize = new Point(400, 350) });
            GUILayoutGroup paddedSaveFrame = new GUILayoutGroup(new RectTransform(new Vector2(0.9f, 0.9f), innerFrame.RectTransform, Anchor.Center))
            {
                AbsoluteSpacing = GUI.IntScale(5),
                Stretch = true
            };

            new GUITextBlock(new RectTransform(new Vector2(1.0f, 0.0f), paddedSaveFrame.RectTransform),
                TextManager.Get("SaveItemAssemblyDialogHeader"), font: GUIStyle.LargeFont);
            new GUITextBlock(new RectTransform(new Vector2(1.0f, 0.0f), paddedSaveFrame.RectTransform),
                TextManager.Get("SaveItemAssemblyDialogName"));
            nameBox = new GUITextBox(new RectTransform(new Vector2(0.6f, 0.1f), paddedSaveFrame.RectTransform));

#if DEBUG
            new GUITickBox(new RectTransform(new Vector2(1.0f, 0.1f), paddedSaveFrame.RectTransform), TextManager.Get("SaveItemAssemblyHideInMenus"))
            {
                UserData = "hideinmenus"
            };
#endif

            var descriptionContainer = new GUIListBox(new RectTransform(new Vector2(1.0f, 0.5f), paddedSaveFrame.RectTransform));
            descriptionBox = new GUITextBox(new RectTransform(Vector2.One, descriptionContainer.Content.RectTransform, Anchor.TopLeft),
                font: GUIStyle.SmallFont, style: "GUITextBoxNoBorder", wrap: true, textAlignment: Alignment.TopLeft)
            {
                Padding = new Vector4(10 * GUI.Scale)
            };

            descriptionBox.OnTextChanged += (textBox, text) =>
            {
                Vector2 textSize = textBox.Font.MeasureString(descriptionBox.WrappedText);
                textBox.RectTransform.NonScaledSize = new Point(textBox.RectTransform.NonScaledSize.X, Math.Max(descriptionContainer.Content.Rect.Height, (int)textSize.Y + 10));
                descriptionContainer.UpdateScrollBarSize();
                descriptionContainer.BarScroll = 1.0f;
                return true;
            };

            var buttonArea = new GUIFrame(new RectTransform(new Vector2(1.0f, 0.1f), paddedSaveFrame.RectTransform), style: null);
            new GUIButton(new RectTransform(new Vector2(0.25f, 1.0f), buttonArea.RectTransform, Anchor.BottomLeft),
                TextManager.Get("Cancel"))
            {
                OnClicked = (GUIButton btn, object userdata) =>
                {
                    saveFrame = null;
                    return true;
                }
            };
            new GUIButton(new RectTransform(new Vector2(0.25f, 1.0f), buttonArea.RectTransform, Anchor.BottomRight),
                TextManager.Get("SaveSubButton"))
            {
                OnClicked = SaveAssembly
            };
            buttonArea.RectTransform.MinSize = new Point(0, buttonArea.Children.First().RectTransform.MinSize.Y);
        }

        /// <summary>
        /// Loads an item assembly and only returns items which are not inside other inventories.
        /// This is to prevent us from trying to place for example Oxygen Tanks inside an inventory
        /// when it's already inside a diving suit.
        /// </summary>
        /// <param name="assemblyPrefab"></param>
        /// <returns></returns>
        private List<Item> LoadItemAssemblyInventorySafe(ItemAssemblyPrefab assemblyPrefab)
        {
            var realItems = assemblyPrefab.CreateInstance(Vector2.Zero, MainSub);
            var itemInstance = new List<Item>();
            realItems.ForEach(entity =>
            {
                if (entity is Item it && it.ParentInventory == null)
                {
                    itemInstance.Add(it);
                }
            });
            return itemInstance;
        }

        private bool SaveAssembly(GUIButton button, object obj)
        {
            if (string.IsNullOrWhiteSpace(nameBox.Text))
            {
                GUI.AddMessage(TextManager.Get("ItemAssemblyNameMissingWarning"), GUIStyle.Red);

                nameBox.Flash();
                return false;
            }

            foreach (char illegalChar in Path.GetInvalidFileNameCharsCrossPlatform())
            {
                if (nameBox.Text.Contains(illegalChar))
                {
                    GUI.AddMessage(TextManager.GetWithVariable("ItemAssemblyNameIllegalCharsWarning", "[illegalchar]", illegalChar.ToString()), GUIStyle.Red);
                    nameBox.Flash();
                    return false;
                }
            }

            nameBox.Text = nameBox.Text.Trim();

            bool hideInMenus = nameBox.Parent.GetChildByUserData("hideinmenus") is GUITickBox hideInMenusTickBox && hideInMenusTickBox.Selected;
            string saveFolder = Path.Combine(ContentPackage.LocalModsDir, nameBox.Text);
            string filePath = Path.Combine(saveFolder, $"{nameBox.Text}.xml").CleanUpPathCrossPlatform();
            if (File.Exists(filePath))
            {
                var msgBox = new GUIMessageBox(TextManager.Get("Warning"), TextManager.Get("ItemAssemblyFileExistsWarning"), new[] { TextManager.Get("Yes"), TextManager.Get("No") });
                msgBox.Buttons[0].OnClicked = (btn, userdata) =>
                {
                    msgBox.Close();
                    Save();
                    return true;
                };
                msgBox.Buttons[1].OnClicked = msgBox.Close;
            }
            else
            {
                Save();
            }

            void Save()
            {
                ContentPackage existingContentPackage = ContentPackageManager.LocalPackages.Regular.FirstOrDefault(p => p.Files.Any(f => f.Path == filePath));
                if (existingContentPackage == null)
                {
                    //content package doesn't exist, create one
                    ModProject modProject = new ModProject { Name = nameBox.Text };
                    var newFile = ModProject.File.FromPath<ItemAssemblyFile>(Path.Combine(ContentPath.ModDirStr, $"{nameBox.Text}.xml"));
                    modProject.AddFile(newFile);
                    string newPackagePath = ContentPackageManager.LocalPackages.SaveRegularMod(modProject);
                    existingContentPackage = ContentPackageManager.LocalPackages.GetRegularModByPath(newPackagePath);
                }

                XDocument doc = new XDocument(ItemAssemblyPrefab.Save(MapEntity.SelectedList.ToList(), nameBox.Text, descriptionBox.Text, hideInMenus));
                try
                {
                    doc.SaveSafe(filePath);
                }
                catch (Exception e)
                {
                    DebugConsole.ThrowError($"Failed to save the item assembly to \"{filePath}\".", e);
                    return;
                }

                var result = ContentPackageManager.ReloadContentPackage(existingContentPackage);
                if (!result.TryUnwrapSuccess(out var resultPackage))
                {
                    throw new Exception($"Failed to reload content package \"{existingContentPackage.Name}\"",
                        result.TryUnwrapFailure(out var exception) ? exception : null);
                }
                if (resultPackage is RegularPackage regularPackage
                    && !ContentPackageManager.EnabledPackages.Regular.Contains(regularPackage))
                {
                    ContentPackageManager.EnabledPackages.EnableRegular(regularPackage);
                    GameSettings.SaveCurrentConfig();
                }

                UpdateEntityList();
                OpenEntityMenu(selectedCategory);
            }

            saveFrame = null;
            return false;
        }

        private static void SnapToGrid()
        {
            // First move components
            foreach (MapEntity e in MapEntity.SelectedList)
            {
                // Items snap to centre of nearest grid square
                Vector2 offset = e.Position;
                offset = new Vector2((MathF.Floor(offset.X / Submarine.GridSize.X) + .5f) * Submarine.GridSize.X - offset.X, (MathF.Floor(offset.Y / Submarine.GridSize.Y) + .5f) * Submarine.GridSize.Y - offset.Y);
                if (e is Item item)
                {
                    var wire = item.GetComponent<Wire>();
                    if (wire != null) { continue; }
                    item.Move(offset);
                    if (item.GetComponent<Door>()?.LinkedGap is Gap linkedGap)
                    {
                        linkedGap.Move(item.Position - linkedGap.Position);
                    }
                }
                else if (e is Structure structure)
                {
                    structure.Move(offset);
                }
            }

            // Then move wires, separated as moving components also moves the start and end node of wires
            foreach (Item item in MapEntity.SelectedList.Where(entity => entity is Item).Cast<Item>())
            {
                var wire = item.GetComponent<Wire>();
                if (wire != null)
                {
                    for (int i = 0; i < wire.GetNodes().Count; i++)
                    {
                        // Items wire nodes to centre of nearest grid square
                        Vector2 offset = wire.GetNodes()[i] + Submarine.MainSub.HiddenSubPosition;
                        offset = new Vector2((MathF.Floor(offset.X / Submarine.GridSize.X) + .5f) * Submarine.GridSize.X - offset.X, (MathF.Floor(offset.Y / Submarine.GridSize.Y) + .5f) * Submarine.GridSize.Y - offset.Y);
                        wire.MoveNode(i, offset);
                    }
                }
            }
        }

        private static IEnumerable<SubmarineInfo> GetLoadableSubs()
        {
            string downloadFolder = Path.GetFullPath(SaveUtil.SubmarineDownloadFolder);
            return SubmarineInfo.SavedSubmarines.Where(s
                            => Path.GetDirectoryName(Path.GetFullPath(s.FilePath)) != downloadFolder);
        }
        
        private void CreateLoadScreen()
        {
            CloseItem();
            SubmarineInfo.RefreshSavedSubs();
            SetMode(Mode.Default);

            loadFrame = new GUIFrame(new RectTransform(GUI.Canvas.RelativeSize, GUI.Canvas, Anchor.Center), style: "GUIBackgroundBlocker");

            new GUIButton(new RectTransform(Vector2.One, loadFrame.RectTransform, Anchor.Center), style: null)
            {
                OnClicked = (_, _) =>
                {
                    loadFrame = null;
                    return true;
                }
            };

            var innerFrame = new GUIFrame(new RectTransform(new Vector2(0.53f, 0.75f), loadFrame.RectTransform, Anchor.Center, scaleBasis: ScaleBasis.Smallest) { MinSize = new Point(350, 500) });

            var paddedLoadFrame = new GUILayoutGroup(new RectTransform(new Vector2(0.9f, 0.9f), innerFrame.RectTransform, Anchor.Center)) { Stretch = true, RelativeSpacing = 0.01f };

            var deleteButtonHolder = new GUILayoutGroup(new RectTransform(new Vector2(1.0f, 0.2f), paddedLoadFrame.RectTransform, Anchor.Center))
            {
                RelativeSpacing = 0.1f,
                Stretch = true
            };

            var searchBox = new GUITextBox(new RectTransform(new Vector2(1.0f, 0.1f), paddedLoadFrame.RectTransform), font: GUIStyle.Font, createClearButton: true);
            var searchTitle = new GUITextBlock(new RectTransform(Vector2.One, searchBox.RectTransform), TextManager.Get("serverlog.filter"),
                textAlignment: Alignment.CenterLeft, font: GUIStyle.Font)
            {
                CanBeFocused = false,
                IgnoreLayoutGroups = true
            };
            searchTitle.TextColor *= 0.5f;

            var subList = new GUIListBox(new RectTransform(new Vector2(1.0f, 0.7f), paddedLoadFrame.RectTransform))
            {
                PlaySoundOnSelect = true,
                ScrollBarVisible = true,
                OnSelected = (GUIComponent selected, object userData) =>
                {
                    if (deleteButtonHolder.FindChild("delete") is GUIButton deleteBtn)
                    {
                        deleteBtn.ToolTip = string.Empty;
                        if (!(userData is SubmarineInfo subInfo))
                        {
                            deleteBtn.Enabled = false;
                            return true;
                        }

                        var package = GetLocalPackageThatOwnsSub(subInfo);
                        if (package != null)
                        {
                            deleteBtn.Enabled = true;
                        }
                        else
                        {
                            deleteBtn.Enabled = false;
                            if (IsVanillaSub(subInfo))
                            {
                                deleteBtn.ToolTip = TextManager.Get("cantdeletevanillasub");
                            }
                            else if (GetPackageThatOwnsSub(subInfo, ContentPackageManager.AllPackages) is ContentPackage subPackage)
                            {
                                deleteBtn.ToolTip = TextManager.GetWithVariable("cantdeletemodsub", "[modname]", subPackage.Name);
                            }
                        }
                    }
                    return true;
                }
            };

            searchBox.OnSelected += (sender, userdata) => { searchTitle.Visible = false; };
            searchBox.OnDeselected += (sender, userdata) => { searchTitle.Visible = sender.Text.IsNullOrEmpty(); };
            searchBox.OnTextChanged += (textBox, text) => { FilterSubs(subList, text); return true; };

            var sortedSubs = GetLoadableSubs()
                .OrderBy(s => s.Type)
                .ThenBy(s => s.Name)
                .ToList();

            SubmarineInfo prevSub = null;

            foreach (SubmarineInfo sub in sortedSubs)
            {
                if (prevSub == null || prevSub.Type != sub.Type)
                {
                    string textTag = "SubmarineType." + sub.Type;
                    if (sub.Type == SubmarineType.EnemySubmarine && !TextManager.ContainsTag(textTag))
                    {
                        textTag = "MissionType.Pirate";
                    }
                    new GUITextBlock(new RectTransform(new Vector2(1.0f, 0.0f), subList.Content.RectTransform) { MinSize = new Point(0, 35) },
                        TextManager.Get(textTag), font: GUIStyle.LargeFont, textAlignment: Alignment.Center, style: "ListBoxElement")
                    {
                        CanBeFocused = false
                    };
                    prevSub = sub;
                }

                string pathWithoutUserName = Path.GetFullPath(sub.FilePath);
                string saveFolder = Path.GetFullPath(SaveUtil.DefaultSaveFolder);
                if (pathWithoutUserName.StartsWith(saveFolder))
                {
                    pathWithoutUserName = "..." + pathWithoutUserName[saveFolder.Length..];
                }
                else
                {
                    pathWithoutUserName = sub.FilePath;
                }

                GUITextBlock textBlock = new GUITextBlock(new RectTransform(new Vector2(1.0f, 0.0f), subList.Content.RectTransform) { MinSize = new Point(0, 30) },
                    ToolBox.LimitString(sub.Name, GUIStyle.Font, subList.Rect.Width - 80))
                {
                    UserData = sub,
                    ToolTip = pathWithoutUserName
                };

                if (!(ContentPackageManager.VanillaCorePackage?.Files.Any(f => f.Path == sub.FilePath) ?? false))
                {
                    if (GetLocalPackageThatOwnsSub(sub) == null &&
                        ContentPackageManager.AllPackages.FirstOrDefault(p => p.Files.Any(f => f.Path == sub.FilePath)) is ContentPackage subPackage)
                    {
                        //workshop mod
                        textBlock.OverrideTextColor(Color.MediumPurple);
                    }
                    else
                    {
                        //local mod
                        textBlock.OverrideTextColor(GUIStyle.TextColorBright);
                    }
                }

                if (sub.HasTag(SubmarineTag.Shuttle))
                {
                    var shuttleText = new GUITextBlock(new RectTransform(new Vector2(0.2f, 1.0f), textBlock.RectTransform, Anchor.CenterRight),
                        TextManager.Get("Shuttle", "RespawnShuttle"), textAlignment: Alignment.CenterRight, font: GUIStyle.SmallFont)
                        {
                            TextColor = textBlock.TextColor * 0.8f,
                            ToolTip = textBlock.ToolTip.SanitizedString
                        };
                }
                else if (sub.IsPlayer)
                {
                    var classText = new GUITextBlock(new RectTransform(new Vector2(0.2f, 1.0f), textBlock.RectTransform, Anchor.CenterRight),
                    sub.Class.Name, textAlignment: Alignment.CenterRight, font: GUIStyle.SmallFont)
                    {
                        TextColor = textBlock.TextColor * 0.8f,
                        ToolTip = textBlock.ToolTip.SanitizedString
                    };
                }
            }

            var deleteButton = new GUIButton(new RectTransform(Vector2.One, deleteButtonHolder.RectTransform, Anchor.TopCenter),
                TextManager.Get("Delete"))
            {
                Enabled = false,
                UserData = "delete"
            };
            deleteButton.OnClicked = (btn, userdata) =>
            {
                if (subList.SelectedComponent != null)
                {
                    TryDeleteSub(subList.SelectedComponent.UserData as SubmarineInfo);
                }
                deleteButton.Enabled = false;
                return true;
            };


            if (AutoSaveInfo?.Root != null)
            {
                int min = Math.Min(6, AutoSaveInfo.Root.Elements().Count());
                var loadAutoSave = new GUIDropDown(new RectTransform(Vector2.One,  deleteButtonHolder.RectTransform, Anchor.BottomCenter), TextManager.Get("LoadAutoSave"), elementCount: min)
                {
                    ToolTip = TextManager.Get("LoadAutoSaveTooltip"),
                    UserData = "loadautosave",
                    OnSelected = (button, o) =>
                    {
                        LoadAutoSave(o);
                        return true;
                    }
                };
                foreach (XElement saveElement in AutoSaveInfo.Root.Elements().Reverse())
                {
                    DateTime time = DateTime.MinValue.AddSeconds(saveElement.GetAttributeUInt64("time", 0));
                    TimeSpan difference = DateTime.UtcNow - time;

                    LocalizedString tooltip = TextManager.GetWithVariables("subeditor.autosaveage",
                        ("[hours]", ((int)Math.Floor(difference.TotalHours)).ToString()),
                        ("[minutes]", difference.Minutes.ToString()),
                        ("[seconds]", difference.Seconds.ToString()));

                    string submarineName = saveElement.GetAttributeString("name", TextManager.Get("UnspecifiedSubFileName").Value);
                    LocalizedString timeFormat;

                    double totalMinutes = difference.TotalMinutes;

                    if (totalMinutes < 1)
                    {
                        timeFormat = TextManager.Get("subeditor.savedjustnow");
                    }
                    else if (totalMinutes > 60)
                    {
                        timeFormat = TextManager.Get("subeditor.savedmorethanhour");
                    }
                    else
                    {
                        timeFormat = TextManager.GetWithVariable("subeditor.saveageminutes", "[minutes]", difference.Minutes.ToString());
                    }

                    LocalizedString entryName = TextManager.GetWithVariables("subeditor.autosaveentry", ("[submarine]", submarineName), ("[saveage]", timeFormat));

                    loadAutoSave.AddItem(entryName, saveElement, tooltip);
                }
            }

            var controlBtnHolder = new GUILayoutGroup(new RectTransform(new Vector2(1.0f, 0.1f), paddedLoadFrame.RectTransform), isHorizontal: true) { RelativeSpacing = 0.2f, Stretch = true };

            new GUIButton(new RectTransform(new Vector2(0.5f, 1.0f), controlBtnHolder.RectTransform, Anchor.BottomLeft),
                TextManager.Get("Cancel"))
            {
                OnClicked = (GUIButton btn, object userdata) =>
                {
                    loadFrame = null;
                    return true;
                }
            };

            new GUIButton(new RectTransform(new Vector2(0.5f, 1.0f), controlBtnHolder.RectTransform, Anchor.BottomRight),
                TextManager.Get("Load"))
            {
                OnClicked = HitLoadSubButton
            };

            controlBtnHolder.RectTransform.MaxSize = new Point(int.MaxValue, controlBtnHolder.Children.First().Rect.Height);
        }

        private void FilterSubs(GUIListBox subList, string filter)
        {
            foreach (GUIComponent child in subList.Content.Children)
            {
                if (!(child.UserData is SubmarineInfo sub)) { continue; }
                child.Visible = string.IsNullOrEmpty(filter) || sub.Name.ToLower().Contains(filter.ToLower());
            }

            //go through the elements backwards, and disable the labels for sub categories if there's no subs visible in them
            bool subVisibleInCategory = false;
            foreach (GUIComponent child in subList.Content.Children.Reverse())
            {
                if (!(child.UserData is SubmarineInfo sub)) 
                { 
                    if (child.Enabled)
                    {
                        child.Visible = subVisibleInCategory;
                    }
                    subVisibleInCategory = false;
                }
                else
                {
                    subVisibleInCategory |= child.Visible;
                }
            }
        }

        /// <summary>
        /// Recovers the auto saved submarine
        /// <see cref="AutoSave"/>
        /// </summary>
        private void LoadAutoSave(object userData)
        {
            if (userData is not XElement element) { return; }

#warning TODO: revise
            string filePath = element.GetAttributeStringUnrestricted("file", "");
            if (string.IsNullOrWhiteSpace(filePath)) { return; }

            var loadedSub = Submarine.Load(new SubmarineInfo(filePath), true);

            try
            {
                loadedSub.Info.Name = loadedSub.Info.SubmarineElement.GetAttributeString("name", loadedSub.Info.Name);
            }
            catch (Exception e)
            {
                DebugConsole.ThrowError("Failed to find a name for the submarine.", e);
                var unspecifiedFileName = TextManager.Get("UnspecifiedSubFileName");
                loadedSub.Info.Name = unspecifiedFileName.Value;
            }
            MainSub = loadedSub;
            MainSub.SetPrevTransform(MainSub.Position);
            MainSub.UpdateTransform();
            MainSub.Info.Name = loadedSub.Info.Name;
            subNameLabel.Text = ToolBox.LimitString(loadedSub.Info.Name, subNameLabel.Font, subNameLabel.Rect.Width);

            ReconstructLayers();

            CreateDummyCharacter();

            cam.Position = MainSub.Position + MainSub.HiddenSubPosition;

            loadFrame = null;
        }

        private bool HitLoadSubButton(GUIButton button, object obj)
        {
            if (loadFrame == null)
            {
                DebugConsole.NewMessage("load frame null", Color.Red);
                return false;
            }

            GUIListBox subList = loadFrame.GetAnyChild<GUIListBox>();
            if (subList == null)
            {
                DebugConsole.NewMessage("Sublist null", Color.Red);
                return false;
            }

            if (!(subList.SelectedComponent?.UserData is SubmarineInfo selectedSubInfo)) { return false; }

            var ownerPackage = GetLocalPackageThatOwnsSub(selectedSubInfo);
            if (ownerPackage is null)
            {
                if (IsVanillaSub(selectedSubInfo))
                {
#if DEBUG
                    LoadSub(selectedSubInfo);
#else
                    AskLoadVanillaSub(selectedSubInfo);
#endif
                }
                else if (GetWorkshopPackageThatOwnsSub(selectedSubInfo) is ContentPackage workshopPackage)
                {
                    if (workshopPackage.TryExtractSteamWorkshopId(out var workshopId)
                        && publishedWorkshopItemIds.Contains(workshopId.Value))
                    {
                        AskLoadPublishedSub(selectedSubInfo, workshopPackage);
                    }
                    else
                    {
                        AskLoadSubscribedSub(selectedSubInfo);
                    }
                }
            }
            else
            {
                LoadSub(selectedSubInfo);
            }
            return false;
        }

        void AskLoadSub(SubmarineInfo info, LocalizedString header, LocalizedString desc)
        {
            var msgBox = new GUIMessageBox(
                header,
                desc,
                new[] { TextManager.Get("LoadAnyway"), TextManager.Get("Cancel") });
            msgBox.Buttons[0].OnClicked = (button, o) =>
            {
                LoadSub(info);
                msgBox.Close();
                return false;
            };
            msgBox.Buttons[1].OnClicked = msgBox.Close;
        }

        void AskLoadPublishedSub(SubmarineInfo info, ContentPackage pkg)
            => AskLoadSub(info,
                TextManager.Get("LoadingPublishedSubmarineHeader"),
                TextManager.GetWithVariable("LoadingPublishedSubmarineDesc", "[modname]", pkg.Name));
        
        void AskLoadSubscribedSub(SubmarineInfo info)
            => AskLoadSub(info,
                TextManager.Get("LoadingSubscribedSubmarineHeader"),
                TextManager.Get("LoadingSubscribedSubmarineDesc"));
        
        void AskLoadVanillaSub(SubmarineInfo info)
            => AskLoadSub(info,
                TextManager.Get("LoadingVanillaSubmarineHeader"),
                TextManager.Get("LoadingVanillaSubmarineDesc"));

        public void LoadSub(SubmarineInfo info, bool checkIdConflicts = true)
        {
            Submarine.Unload();
            Submarine selectedSub = null;

            if (checkIdConflicts)
            {
                Dictionary<int, Identifier> entities = new Dictionary<int, Identifier>();
                foreach (var subElement in info.SubmarineElement.Elements())
                {
                    int id = subElement.GetAttributeInt("ID", -1);
                    if (id == -1) { continue; }
                    Identifier identifier = subElement.GetAttributeIdentifier("identifier", string.Empty);
                    if (entities.TryGetValue(id, out Identifier duplicateEntity))
                    {
                        var errorMsg = new GUIMessageBox(
                            TextManager.Get("error"), 
                                TextManager.GetWithVariables("subeditor.duplicateiderror", 
                                    ("[entity1]", $"{duplicateEntity} ({id})"), 
                                    ("[entity2]", $"{identifier} ({id})")),
                                new LocalizedString[] { TextManager.Get("Yes"), TextManager.Get("No") });
                        errorMsg.Buttons[0].OnClicked = (bnt, userdata) =>
                        {
                            subElement.Remove();
                            LoadSub(info, checkIdConflicts: false);
                            errorMsg.Close();
                            return true;
                        };
                        errorMsg.Buttons[1].OnClicked = (bnt, userdata) =>
                        {
                            LoadSub(info, checkIdConflicts: false);
                            errorMsg.Close();
                            return true;
                        };
                        return;
                    }
                    entities.Add(id, identifier);
                }
            }

            try
            {
                selectedSub = new Submarine(info);
                MainSub = selectedSub;
                MainSub.UpdateTransform(interpolate: false);
            }
            catch (Exception e)
            {
                DebugConsole.ThrowError("Failed to load the submarine. The submarine file might be corrupted.", e);
                return;
            }
            ClearUndoBuffer();
            CreateDummyCharacter();

            string name = MainSub.Info.Name;
            subNameLabel.Text = ToolBox.LimitString(name, subNameLabel.Font, subNameLabel.Rect.Width);

            cam.Position = MainSub.Position + MainSub.HiddenSubPosition;

            loadFrame = null;

            if (selectedSub.Info.GameVersion < new Version("0.8.9.0"))
            {
                var adjustLightsPrompt = new GUIMessageBox(TextManager.Get("Warning"), TextManager.Get("AdjustLightsPrompt"),
                    new[] { TextManager.Get("Yes"), TextManager.Get("No") });
                adjustLightsPrompt.Buttons[0].OnClicked += adjustLightsPrompt.Close;
                adjustLightsPrompt.Buttons[0].OnClicked += (btn, userdata) =>
                {
                    foreach (Item item in Item.ItemList)
                    {
                        if (item.ParentInventory != null || item.body != null) continue;
                        var lightComponent = item.GetComponent<LightComponent>();
                        foreach (var light in item.GetComponents<LightComponent>())
                        {
                            light.LightColor = new Color(light.LightColor, light.LightColor.A / 255.0f * 0.5f);
                        }
                    }
                    new GUIMessageBox("", TextManager.Get("AdjustedLightsNotification"));
                    return true;
                };
                adjustLightsPrompt.Buttons[1].OnClicked += adjustLightsPrompt.Close;
            }

            ReconstructLayers();
        }

        private static ContentPackage GetPackageThatOwnsSub(SubmarineInfo sub, IEnumerable<ContentPackage> packages)
            => packages.FirstOrDefault(package => package.Files.Any(f => f.Path == sub.FilePath));

        private static ContentPackage GetLocalPackageThatOwnsSub(SubmarineInfo sub)
            => GetPackageThatOwnsSub(sub, ContentPackageManager.LocalPackages);

        private static ContentPackage GetWorkshopPackageThatOwnsSub(SubmarineInfo sub)
            => GetPackageThatOwnsSub(sub, ContentPackageManager.WorkshopPackages);

        private static bool IsVanillaSub(SubmarineInfo sub)
            => GetPackageThatOwnsSub(sub, ContentPackageManager.VanillaCorePackage.ToEnumerable()) != null;
        
        private void TryDeleteSub(SubmarineInfo sub)
        {
            if (sub == null) { return; }

            //If the sub is included in a content package that only defines that one sub,
            //check that it's a local content package and only allow deletion if it is.
            //(deleting from the Submarines folder is also currently allowed, but this is temporary)
            var subPackage = GetLocalPackageThatOwnsSub(sub);
            if (!ContentPackageManager.LocalPackages.Regular.Contains(subPackage)) { return; }
            
            var msgBox = new GUIMessageBox(
                TextManager.Get("DeleteDialogLabel"),
                TextManager.GetWithVariable("DeleteDialogQuestion", "[file]", sub.Name),
                new LocalizedString[] { TextManager.Get("Yes"), TextManager.Get("Cancel") });
            msgBox.Buttons[0].OnClicked += (btn, userData) =>
            {
                try
                {
                    if (subPackage != null)
                    {
                        File.Delete(sub.FilePath, catchUnauthorizedAccessExceptions: false);
                        ModProject modProject = new ModProject(subPackage);
                        modProject.RemoveFile(modProject.Files.First(f => ContentPath.FromRaw(subPackage, f.Path) == sub.FilePath));
                        modProject.Save(subPackage.Path, catchUnauthorizedAccessExceptions: true);
                        ReloadModifiedPackage(subPackage);
                        if (MainSub?.Info != null && MainSub.Info.FilePath == sub.FilePath)
                        {
                            MainSub.Info.FilePath = null;
                        }
                    }                    
                    sub.Dispose();
                    CreateLoadScreen();
                }
                catch (Exception e)
                {
                    DebugConsole.ThrowErrorLocalized(TextManager.GetWithVariable("DeleteFileError", "[file]", sub.FilePath), e);
                }
                return true;
            };
            msgBox.Buttons[0].OnClicked += msgBox.Close;
            msgBox.Buttons[1].OnClicked += msgBox.Close;
        }

        private void OpenEntityMenu(MapEntityCategory? entityCategory)
        {
            UpdateEntityList();

            foreach (GUIButton categoryButton in entityCategoryButtons)
            {
                categoryButton.Selected = entityCategory.HasValue ?
                    categoryButton.UserData is MapEntityCategory category && entityCategory.Value == category :
                    categoryButton.UserData == null;
                string categoryName = entityCategory.HasValue ? entityCategory.Value.ToString() : "All";
                selectedCategoryText.Text = TextManager.Get("MapEntityCategory." + categoryName);
                selectedCategoryButton.ApplyStyle(GUIStyle.GetComponentStyle("CategoryButton." + categoryName));
            }

            selectedCategory = entityCategory;

            SetMode(Mode.Default);

            saveFrame = null;
            loadFrame = null;

            foreach (GUIComponent child in toggleEntityMenuButton.Children)
            {
                child.SpriteEffects = entityMenuOpen ? SpriteEffects.None : SpriteEffects.FlipVertically;
            }

            foreach (GUIComponent child in categorizedEntityList.Content.Children)
            {
                child.Visible = !entityCategory.HasValue || (MapEntityCategory)child.UserData == entityCategory;
                var innerList = child.GetChild<GUIListBox>();
                foreach (GUIComponent grandChild in innerList.Content.Children)
                {
                    grandChild.Visible = true;
                }
            }

            if (!string.IsNullOrEmpty(entityFilterBox.Text))
            {
                FilterEntities(entityFilterBox.Text);
            }

            categorizedEntityList.UpdateScrollBarSize();
            categorizedEntityList.BarScroll = 0.0f;
            // categorizedEntityList.Visible = true;
            // allEntityList.Visible = false;
        }

        private void FilterEntities(string filter)
        {
            if (string.IsNullOrWhiteSpace(filter))
            {
                allEntityList.Visible = false;
                categorizedEntityList.Visible = true;

                foreach (GUIComponent child in categorizedEntityList.Content.Children)
                {
                    child.Visible = !selectedCategory.HasValue || selectedCategory == (MapEntityCategory)child.UserData;
                    if (!child.Visible) { return; }
                    var innerList = child.GetChild<GUIListBox>();
                    foreach (GUIComponent grandChild in innerList.Content.Children)
                    {
                        grandChild.Visible = ((MapEntityPrefab)grandChild.UserData).Name.Value.Contains(filter, StringComparison.OrdinalIgnoreCase);
                    }
                };
                categorizedEntityList.UpdateScrollBarSize();
                categorizedEntityList.BarScroll = 0.0f;
                return;
            }

            allEntityList.Visible = true;
            categorizedEntityList.Visible = false;
            filter = filter.ToLower();
            foreach (GUIComponent child in allEntityList.Content.Children)
            {
                child.Visible =
                    (!selectedCategory.HasValue || ((MapEntityPrefab)child.UserData).Category.HasFlag(selectedCategory)) &&
                    ((MapEntityPrefab)child.UserData).Name.Value.Contains(filter, StringComparison.OrdinalIgnoreCase);
            }
            allEntityList.UpdateScrollBarSize();
            allEntityList.BarScroll = 0.0f;
        }

        private void ClearFilter()
        {
            FilterEntities("");
            categorizedEntityList.UpdateScrollBarSize();
            categorizedEntityList.BarScroll = 0.0f;
            entityFilterBox.Text = "";
        }

        public void SetMode(Mode newMode)
        {
            if (newMode == mode) { return; }
            mode = newMode;

            lockMode = true;
            defaultModeTickBox.Selected = newMode == Mode.Default;
            wiringModeTickBox.Selected = newMode == Mode.Wiring;
            lockMode = false;

            MapEntity.ClearHighlightedEntities();

            MapEntity.DeselectAll();
            MapEntity.FilteredSelectedList.Clear();
            ClearUndoBuffer();

            CreateDummyCharacter();
            if (newMode == Mode.Wiring)
            {
                var item = new Item(MapEntityPrefab.Find(null, "screwdriver") as ItemPrefab, Vector2.Zero, null);
                dummyCharacter.Inventory.TryPutItem(item, null, new List<InvSlotType>() { InvSlotType.RightHand });
                Point wirePos = new Point((int)(10 * GUI.Scale), TopPanel.Rect.Height + entityCountPanel.Rect.Height + (int)(10 * GUI.Scale));
                wiringToolPanel = CreateWiringPanel(wirePos, SelectWire);
            }
        }

        private void RemoveDummyCharacter()
        {
            if (dummyCharacter == null || dummyCharacter.Removed) { return; }

            dummyCharacter.Inventory.AllItems.ForEachMod(it => it.Remove());
            dummyCharacter.Remove();
            dummyCharacter = null;
        }

        private void CreateContextMenu()
        {
            if (GUIContextMenu.CurrentContextMenu != null) { return; }

            List<MapEntity> targets = MapEntity.HighlightedEntities.Any(me => !MapEntity.SelectedList.Contains(me)) ?
                MapEntity.HighlightedEntities.ToList() :
                new List<MapEntity>(MapEntity.SelectedList);

            bool allowOpening = false;
            var targetItem = (targets.Count == 1 ? targets.Single() : null) as Item;
            // Do not offer the ability to open the inventory if the inventory should never be drawn
            allowOpening = targetItem is not null && targetItem.Components.Any(static ic =>
                ic is not ConnectionPanel &&
                ic is not Repairable &&
                ic is not ItemContainer { DrawInventory: false } &&
                ic.GuiFrame != null);

            bool hasTargets = targets.Count > 0;

            // Holding shift brings up special context menu options
            if (PlayerInput.IsShiftDown())
            {
                GUIContextMenu.CreateContextMenu(
                    new ContextMenuOption("SubEditor.EditBackgroundColor", isEnabled: true,  onSelected: CreateBackgroundColorPicker),
                    new ContextMenuOption("SubEditor.ToggleTransparency",  isEnabled: true,  onSelected: () => TransparentWiringMode = !TransparentWiringMode),
                    new ContextMenuOption("SubEditor.ToggleGrid",  isEnabled: true,  onSelected: () => ShouldDrawGrid = !ShouldDrawGrid),
                    new ContextMenuOption("SubEditor.PasteAssembly",  isEnabled: true,  () => PasteAssembly()),
                    new ContextMenuOption("Editor.SelectSame", isEnabled: hasTargets, onSelected: delegate
                    {
                        bool doorGapSelected = targets.Any(t => t is Gap gap && gap.ConnectedDoor != null);
                        foreach (MapEntity match in MapEntity.MapEntityList.Where(e => e.Prefab != null && targets.Any(t => t.Prefab?.Identifier == e.Prefab.Identifier) && !MapEntity.SelectedList.Contains(e)))
                        {
                            if (MapEntity.SelectedList.Contains(match)) { continue; }
                            if (match is Gap gap)
                            {
                                //don't add non-door gaps if we've selected a door gap (and vice versa)
                                if ((gap.ConnectedDoor == null) == doorGapSelected) { continue; }
                            }
                            else if (match is Item item)
                            {
                                //add door gaps too if we're selecting doors
                                var door = item.GetComponent<Door>();
                                if (door?.LinkedGap != null && !MapEntity.SelectedList.Contains(door.LinkedGap))
                                {
                                    MapEntity.SelectedList.Add(door.LinkedGap);
                                }
                            }
                            MapEntity.SelectedList.Add(match);
                        }
                    }),
                    new ContextMenuOption("SubEditor.AddImage",            isEnabled: true, onSelected: ImageManager.CreateImageWizard),
                    new ContextMenuOption("SubEditor.ToggleImageEditing",  isEnabled: true, onSelected: delegate
                    {
                        ImageManager.EditorMode = !ImageManager.EditorMode;
                        if (!ImageManager.EditorMode) { GameSettings.SaveCurrentConfig(); }
                    }));
            }
            else
            {
                List<ContextMenuOption> availableLayers = new List<ContextMenuOption>
                {
                    new ContextMenuOption("editor.layer.nolayer", true, onSelected: () => { MoveToLayer(null, targets); })
                };
                availableLayers.AddRange(Layers.Select(layer => new ContextMenuOption(layer.Key, true, onSelected: () => { MoveToLayer(layer.Key, targets); })));

                List<ContextMenuOption> availableLayerOptions = new List<ContextMenuOption>
                {
                    new ContextMenuOption("editor.layer.movetolayer", isEnabled: hasTargets, availableLayers.ToArray()),
                    new ContextMenuOption("editor.layer.createlayer", isEnabled: hasTargets, onSelected: () => { CreateNewLayer(null, targets); }),
                    new ContextMenuOption("editor.layer.selectall", isEnabled: hasTargets, onSelected: () =>
                    {
                        foreach (MapEntity match in MapEntity.MapEntityList.Where(e => targets.Any(t => !string.IsNullOrWhiteSpace(t.Layer) && t.Layer == e.Layer && !MapEntity.SelectedList.Contains(e))))
                        {
                            if (MapEntity.SelectedList.Contains(match)) { continue; }
                            MapEntity.SelectedList.Add(match);
                        }
                    }),
                };
                availableLayerOptions.AddRange(Layers.Select(layer => new ContextMenuOption(layer.Key, true, onSelected: () => { MoveToLayer(layer.Key, targets); })));

                GUIContextMenu.CreateContextMenu(
                    new ContextMenuOption("label.openlabel", isEnabled: allowOpening, onSelected: () => OpenItem(targetItem)),
                    new ContextMenuOption("editor.cut", isEnabled: hasTargets, onSelected: () => MapEntity.Cut(targets)),
                    new ContextMenuOption("editor.copytoclipboard", isEnabled: hasTargets, onSelected: () => MapEntity.Copy(targets)),
                    new ContextMenuOption("editor.paste", isEnabled: MapEntity.CopiedList.Any(), onSelected: () => MapEntity.Paste(cam.ScreenToWorld(PlayerInput.MousePosition))),
                    new ContextMenuOption("delete", isEnabled: hasTargets, onSelected: () =>
                    {
                        StoreCommand(new AddOrDeleteCommand(targets, true));
                        foreach (var me in targets)
                        {
                            if (!me.Removed) { me.Remove(); }
                        }
                    }),
                    new ContextMenuOption(string.Empty, isEnabled: false, onSelected: () => { /*do nothing*/ }),
                    new ContextMenuOption("editor.layer.movetoactivelayer", isEnabled: !(layerList?.SelectedData as string).IsNullOrEmpty(), onSelected: () => { MoveToLayer(layerList.SelectedData as string, targets); }),
                    new ContextMenuOption("editor.layer.removefromlayer", isEnabled: targets.Any(t => t.Layer != string.Empty), onSelected: () => { targets.ForEach(t => t.Layer = string.Empty); }),
                    new ContextMenuOption("editor.layeroptions", isEnabled: hasTargets, availableLayerOptions.ToArray()),
                    new ContextMenuOption(TextManager.GetWithVariable("editortip.shiftforextraoptions", "[button]", PlayerInput.SecondaryMouseLabel) + '\n' + TextManager.Get("editortip.altforruler"), isEnabled: false, onSelected: null));
            }
        }

        private void MoveToLayer(string layer, List<MapEntity> content)
        {
            layer ??= string.Empty;

            foreach (MapEntity entity in content)
            {
                if (MapEntity.SelectedList.Contains(entity))
                {
                    MapEntity.ResetEditingHUD();
                }
                entity.Layer = layer;
            }
        }

        private void CreateNewLayer(string name, List<MapEntity> content)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                name = TextManager.Get("editor.layer.newlayer").Value;
            }

            string incrementedName = name;

            for (int i = 1; Layers.ContainsKey(incrementedName); i++)
            {
                incrementedName = $"{name} ({i})";
            }

            name = incrementedName;

            if (content != null)
            {
                MoveToLayer(name, content);
            }

            Layers.Add(name, new LayerData());
            UpdateLayerPanel();
        }

        private void RenameLayer(string original, string newName)
        {
            Layers.Remove(original, out LayerData originalData);

            foreach (MapEntity entity in MapEntity.MapEntityList.Where(entity => entity.Layer == original))
            {
                entity.Layer = newName ?? string.Empty;
            }

            if (!string.IsNullOrWhiteSpace(newName))
            {
                Layers.TryAdd(newName, originalData);
            }
            UpdateLayerPanel();
        }

        public void ReconstructLayers()
        {
            ClearLayers();
            foreach (MapEntity entity in MapEntity.MapEntityList)
            {
                if (!string.IsNullOrWhiteSpace(entity.Layer))
                {
                    Layers.TryAdd(entity.Layer, new LayerData(!entity.IsLayerHidden));
                }
            }
            UpdateLayerPanel();
        }

        private void ClearLayers()
        {
            Layers.Clear();
            UpdateLayerPanel();
        }
        
        private static void SetLayerVisibility(string layerName, bool isVisible)
        {
            if (Layers.Remove(layerName, out LayerData layerData))
            {
                Layers.Add(layerName, layerData with { IsVisible = isVisible });
            }
            else
            {
                Layers.Add(layerName, new LayerData(isVisible));
            }
        }

        private void PasteAssembly(string text = null, Vector2? pos = null)
        {
            pos ??= cam.ScreenToWorld(PlayerInput.MousePosition);
            text ??= Clipboard.GetText();
            if (string.IsNullOrWhiteSpace(text))
            {
                DebugConsole.ThrowError("Unable to paste assembly: Clipboard content is empty.");
                return;
            }

            XElement element = null;

            try
            {
                element = XDocument.Parse(text).Root;
            }
            catch (Exception) { /* ignored */ }

            if (element == null)
            {
                DebugConsole.ThrowError("Unable to paste assembly: Clipboard content is not valid XML.");
                return;
            }

            Submarine sub = MainSub;
            List<MapEntity> entities;
            try
            {
                entities = ItemAssemblyPrefab.PasteEntities(pos.Value, sub, element, selectInstance: true);
            }
            catch (Exception e)
            {
                DebugConsole.ThrowError("Unable to paste assembly: Failed to load items.", e);
                return;
            }

            if (!entities.Any()) { return; }
            StoreCommand(new AddOrDeleteCommand(entities, false, handleInventoryBehavior: false));
        }

        public static GUIMessageBox CreatePropertyColorPicker(Color originalColor, SerializableProperty property, ISerializableEntity entity)
        {
            var entities = new List<(ISerializableEntity Entity, Color OriginalColor, SerializableProperty Property)> { (entity, originalColor, property) };

            foreach (ISerializableEntity selectedEntity in MapEntity.SelectedList.Where(selectedEntity => selectedEntity is ISerializableEntity && entity != selectedEntity).Cast<ISerializableEntity>())
            {
                switch (entity)
                {
                    case ItemComponent _ when selectedEntity is Item item:
                        foreach (var component in item.Components)
                        {
                            if (component.GetType() == entity.GetType() && component != entity)
                            {
                                entities.Add((component, (Color) property.GetValue(component), property));
                            }
                        }
                        break;
                    default:
                        if (selectedEntity.GetType() == entity.GetType())
                        {
                            entities.Add((selectedEntity, (Color) property.GetValue(selectedEntity), property));
                        }
                        else if (selectedEntity is { SerializableProperties: { } props} )
                        {
                            if (props.TryGetValue(property.Name.ToIdentifier(), out SerializableProperty foundProp))
                            {
                                entities.Add((selectedEntity, (Color) foundProp.GetValue(selectedEntity), foundProp));
                            }
                        }
                        break;
                }
            }

            bool setValues = true;
            object sliderMutex     = new object(),
                   sliderTextMutex = new object(),
                   pickerMutex     = new object(),
                   hexMutex        = new object();

            Vector2 relativeSize = new Vector2(0.4f * GUI.AspectRatioAdjustment, 0.3f);

            GUIMessageBox msgBox = new GUIMessageBox(string.Empty, string.Empty, Array.Empty<LocalizedString>(), relativeSize, type: GUIMessageBox.Type.Vote)
            {
                UserData = "colorpicker",
                Draggable = true
            };

            GUILayoutGroup contentLayout = new GUILayoutGroup(new RectTransform(Vector2.One, msgBox.Content.RectTransform));
            GUITextBlock headerText = new GUITextBlock(new RectTransform(new Vector2(1f, 0.1f), contentLayout.RectTransform), property.Name, font: GUIStyle.SubHeadingFont, textAlignment: Alignment.TopCenter)
            {
                AutoScaleVertical = true
            };

            GUILayoutGroup colorLayout = new GUILayoutGroup(new RectTransform(new Vector2(1f, 0.7f), contentLayout.RectTransform), isHorizontal: true);

            GUILayoutGroup buttonLayout = new GUILayoutGroup(new RectTransform(new Vector2(1f, 0.2f), contentLayout.RectTransform), childAnchor: Anchor.BottomLeft, isHorizontal: true)
            {
                RelativeSpacing = 0.1f,
                Stretch = true
            };

            GUIButton closeButton = new GUIButton(new RectTransform(new Vector2(0.5f, 1f), buttonLayout.RectTransform), TextManager.Get("OK"), textAlignment: Alignment.Center);
            GUIButton cancelButton = new GUIButton(new RectTransform(new Vector2(0.5f, 1f), buttonLayout.RectTransform), TextManager.Get("Cancel"), textAlignment: Alignment.Center);

            contentLayout.Recalculate();
            colorLayout.Recalculate();

            GUIColorPicker colorPicker = new GUIColorPicker(new RectTransform(new Point(colorLayout.Rect.Height), colorLayout.RectTransform));
            var (h, s, v) = ToolBox.RGBToHSV(originalColor);
            colorPicker.SelectedHue = float.IsNaN(h) ? 0f : h;
            colorPicker.SelectedSaturation = s;
            colorPicker.SelectedValue = v;

            colorLayout.Recalculate();

            GUILayoutGroup sliderLayout = new GUILayoutGroup(new RectTransform(new Vector2(1.0f - colorPicker.RectTransform.RelativeSize.X, 1f), colorLayout.RectTransform), childAnchor: Anchor.TopRight);

            float currentHue = colorPicker.SelectedHue / 360f;
            GUILayoutGroup hueSliderLayout = new GUILayoutGroup(new RectTransform(new Vector2(0.95f, 0.25f), sliderLayout.RectTransform), isHorizontal: true, childAnchor: Anchor.CenterLeft) { Stretch = true };
            new GUITextBlock(new RectTransform(new Vector2(0.1f, 0.2f), hueSliderLayout.RectTransform), text: "H:", font: GUIStyle.SubHeadingFont) { Padding = Vector4.Zero, ToolTip = "Hue" };
            GUIScrollBar hueScrollBar = new GUIScrollBar(new RectTransform(new Vector2(0.7f, 1f), hueSliderLayout.RectTransform), style: "GUISlider", barSize: 0.05f) { BarScroll = currentHue };
            GUINumberInput hueTextBox = new GUINumberInput(new RectTransform(new Vector2(0.2f, 1f), hueSliderLayout.RectTransform) { MinSize = new Point(GUI.IntScale(100), 0) }, 
                inputType: NumberType.Float) { FloatValue = currentHue, MaxValueFloat = 1f, MinValueFloat = 0f, DecimalsToDisplay = 2 };

            GUILayoutGroup satSliderLayout = new GUILayoutGroup(new RectTransform(new Vector2(0.95f, 0.2f), sliderLayout.RectTransform), isHorizontal: true, childAnchor: Anchor.CenterLeft) { Stretch = true };
            new GUITextBlock(new RectTransform(new Vector2(0.1f, 0.2f), satSliderLayout.RectTransform), text: "S:", font: GUIStyle.SubHeadingFont) { Padding = Vector4.Zero, ToolTip = "Saturation"};
            GUIScrollBar satScrollBar = new GUIScrollBar(new RectTransform(new Vector2(0.7f, 1f), satSliderLayout.RectTransform), style: "GUISlider", barSize: 0.05f) { BarScroll = colorPicker.SelectedSaturation };
            GUINumberInput satTextBox = new GUINumberInput(new RectTransform(new Vector2(0.2f, 1f), satSliderLayout.RectTransform) { MinSize = new Point(GUI.IntScale(100), 0) }, 
                inputType: NumberType.Float) { FloatValue = colorPicker.SelectedSaturation, MaxValueFloat = 1f, MinValueFloat = 0f, DecimalsToDisplay = 2 };

            GUILayoutGroup valueSliderLayout = new GUILayoutGroup(new RectTransform(new Vector2(0.95f, 0.2f), sliderLayout.RectTransform), isHorizontal: true, childAnchor: Anchor.CenterLeft) { Stretch = true };
            new GUITextBlock(new RectTransform(new Vector2(0.1f, 0.2f), valueSliderLayout.RectTransform), text: "V:", font: GUIStyle.SubHeadingFont) { Padding = Vector4.Zero, ToolTip = "Value"};
            GUIScrollBar valueScrollBar = new GUIScrollBar(new RectTransform(new Vector2(0.7f, 1f), valueSliderLayout.RectTransform), style: "GUISlider", barSize: 0.05f) { BarScroll = colorPicker.SelectedValue };
            GUINumberInput valueTextBox = new GUINumberInput(new RectTransform(new Vector2(0.2f, 1f), valueSliderLayout.RectTransform) { MinSize = new Point(GUI.IntScale(100), 0) }, 
                inputType: NumberType.Float) { FloatValue = colorPicker.SelectedValue, MaxValueFloat = 1f, MinValueFloat = 0f, DecimalsToDisplay = 2 };

            GUILayoutGroup colorInfoLayout = new GUILayoutGroup(new RectTransform(new Vector2(0.95f, 0.3f), sliderLayout.RectTransform), childAnchor: Anchor.CenterLeft, isHorizontal: true) 
            { 
                Stretch = true,
                RelativeSpacing = 0.1f 
            };

            new GUICustomComponent(new RectTransform(Vector2.One, colorInfoLayout.RectTransform, scaleBasis: ScaleBasis.BothHeight), (batch, component) =>
            {
                Rectangle rect = component.Rect;
                Point areaSize = new Point(rect.Width, rect.Height / 2);
                Rectangle newColorRect = new Rectangle(rect.Location, areaSize);
                Rectangle oldColorRect = new Rectangle(new Point(newColorRect.Left, newColorRect.Bottom), areaSize);

                GUI.DrawRectangle(batch, newColorRect, ToolBoxCore.HSVToRGB(colorPicker.SelectedHue, colorPicker.SelectedSaturation, colorPicker.SelectedValue), isFilled: true);
                GUI.DrawRectangle(batch, oldColorRect, originalColor, isFilled: true);
                GUI.DrawRectangle(batch, rect, Color.Black, isFilled: false);
            });

            GUITextBox hexValueBox = new GUITextBox(new RectTransform(new Vector2(0.3f, 1f), colorInfoLayout.RectTransform), text: ColorToHex(originalColor), createPenIcon: false) { OverflowClip = true };

            hueScrollBar.OnMoved = (bar, scroll) => { SetColor(sliderMutex); return true; };
            hueTextBox.OnValueChanged = input => { SetColor(sliderTextMutex); };

            satScrollBar.OnMoved = (bar, scroll) => { SetColor(sliderMutex); return true; };
            satTextBox.OnValueChanged = input => { SetColor(sliderTextMutex); };

            valueScrollBar.OnMoved = (bar, scroll) => { SetColor(sliderMutex); return true; };
            valueTextBox.OnValueChanged = input => { SetColor(sliderTextMutex); };

            colorPicker.OnColorSelected = (component, color) => { SetColor(pickerMutex); return true; };

            hexValueBox.OnEnterPressed = (box, text) => { SetColor(hexMutex); return true; };
            hexValueBox.OnDeselected += (sender, key) => { SetColor(hexMutex); };

            closeButton.OnClicked = (button, o) =>
            {
                colorPicker.Dispose();
                msgBox.Close();

                Color newColor = SetColor(null);

                if (!IsSubEditor()) { return true; }

                Dictionary<object, List<ISerializableEntity>> oldProperties = new Dictionary<object, List<ISerializableEntity>>();

                foreach (var (sEntity, color, _) in entities)
                {
                    if (sEntity is MapEntity { Removed: true }) { continue; }
                    if (!oldProperties.ContainsKey(color))
                    {
                        oldProperties.Add(color, new List<ISerializableEntity>());
                    }
                    oldProperties[color].Add(sEntity);
                }

                List<ISerializableEntity> affected = entities.Select(t => t.Entity).Where(se => se is MapEntity { Removed: false } || se is ItemComponent).ToList();
                StoreCommand(new PropertyCommand(affected, property.Name.ToIdentifier(), newColor, oldProperties));

                if (MapEntity.EditingHUD != null && (MapEntity.EditingHUD.UserData == entity || (!(entity is ItemComponent ic) || MapEntity.EditingHUD.UserData == ic.Item)))
                {
                    GUIListBox list = MapEntity.EditingHUD.GetChild<GUIListBox>();
                    if (list != null)
                    {
                        IEnumerable<SerializableEntityEditor> editors = list.Content.FindChildren(comp => comp is SerializableEntityEditor).Cast<SerializableEntityEditor>();
                        SerializableEntityEditor.LockEditing = true;
                        foreach (SerializableEntityEditor editor in editors)
                        {
                            if (editor.UserData == entity && editor.Fields.TryGetValue(property.Name.ToIdentifier(), out GUIComponent[] _))
                            {
                                editor.UpdateValue(property, newColor, flash: false);
                            }
                        }
                        SerializableEntityEditor.LockEditing = false;
                    }
                }
                return true;
            };

            cancelButton.OnClicked = (button, o) =>
            {
                colorPicker.Dispose();
                msgBox.Close();

                foreach (var (e, color, prop) in entities)
                {
                    if (e is MapEntity { Removed: true }) { continue; }
                    prop.TrySetValue(e, color);
                }
                return true;
            };

            return msgBox;

            Color SetColor(object source)
            {
                if (setValues)
                {
                    setValues = false;

                    if (source == sliderMutex)
                    {
                        Vector3 hsv = new Vector3(hueScrollBar.BarScroll * 360f, satScrollBar.BarScroll, valueScrollBar.BarScroll);
                        SetSliderTexts(hsv);
                        SetColorPicker(hsv);
                        SetHex(hsv);
                    }
                    else if (source == sliderTextMutex)
                    {
                        Vector3 hsv = new Vector3(hueTextBox.FloatValue * 360f, satTextBox.FloatValue, valueTextBox.FloatValue);
                        SetSliders(hsv);
                        SetColorPicker(hsv);
                        SetHex(hsv);
                    }
                    else if (source == pickerMutex)
                    {
                        Vector3 hsv = new Vector3(colorPicker.SelectedHue, colorPicker.SelectedSaturation, colorPicker.SelectedValue);
                        SetSliders(hsv);
                        SetSliderTexts(hsv);
                        SetHex(hsv);
                    }
                    else if (source == hexMutex)
                    {
                        Vector3 hsv = ToolBox.RGBToHSV(XMLExtensions.ParseColor(hexValueBox.Text, errorMessages: false));
                        if (float.IsNaN(hsv.X)) { hsv.X = 0f; }
                        SetSliders(hsv);
                        SetSliderTexts(hsv);
                        SetColorPicker(hsv);
                        SetHex(hsv);
                    }

                    setValues = true;
                }

                Color color = ToolBoxCore.HSVToRGB(colorPicker.SelectedHue, colorPicker.SelectedSaturation, colorPicker.SelectedValue);
                foreach (var (e, origColor, prop) in entities)
                {
                    if (e is MapEntity { Removed: true }) { continue; }
                    color.A = origColor.A;
                    prop.TrySetValue(e, color);
                }
                return color;

                void SetSliders(Vector3 hsv)
                {
                    hueScrollBar.BarScroll = hsv.X / 360f;
                    satScrollBar.BarScroll = hsv.Y;
                    valueScrollBar.BarScroll = hsv.Z;
                }

                void SetSliderTexts(Vector3 hsv)
                {
                    hueTextBox.FloatValue = hsv.X / 360f;
                    satTextBox.FloatValue = hsv.Y;
                    valueTextBox.FloatValue = hsv.Z;
                }

                void SetColorPicker(Vector3 hsv)
                {
                    bool hueChanged = !MathUtils.NearlyEqual(colorPicker.SelectedHue, hsv.X);
                    colorPicker.SelectedHue = hsv.X;
                    colorPicker.SelectedSaturation = hsv.Y;
                    colorPicker.SelectedValue = hsv.Z;
                    if (hueChanged) { colorPicker.RefreshHue(); }
                }

                void SetHex(Vector3 hsv)
                {
                    Color hexColor = ToolBoxCore.HSVToRGB(hsv.X, hsv.Y, hsv.Z);
                    hexValueBox!.Text = ColorToHex(hexColor);
                }
            }

            static string ColorToHex(Color color) => $"#{(color.R << 16 | color.G << 8 | color.B):X6}";
        }

        public static GUIFrame CreateWiringPanel(Point offset, GUIListBox.OnSelectedHandler onWireSelected)
        {
            GUIFrame frame = new GUIFrame(new RectTransform(new Vector2(0.03f, 0.35f), GUI.Canvas)
                { MinSize = new Point(120, 300), AbsoluteOffset = offset });

            GUIListBox listBox = new GUIListBox(new RectTransform(new Vector2(0.9f, 0.9f), frame.RectTransform, Anchor.Center))
            {
                PlaySoundOnSelect = true,
                OnSelected = onWireSelected,
                CanTakeKeyBoardFocus = false
            };

            List<ItemPrefab> wirePrefabs = new List<ItemPrefab>();

            foreach (ItemPrefab itemPrefab in ItemPrefab.Prefabs)
            {
                if (itemPrefab.Name.IsNullOrEmpty() || itemPrefab.HideInMenus || itemPrefab.HideInEditors) { continue; }
                if (!itemPrefab.Tags.Contains(Tags.WireItem)) { continue; }
                if (CircuitBox.IsInGame() && itemPrefab.Tags.Contains(Tags.Thalamus)) { continue; }

                wirePrefabs.Add(itemPrefab);
            }

            foreach (ItemPrefab itemPrefab in wirePrefabs.OrderBy(static w => !w.CanBeBought).ThenBy(static w => w.UintIdentifier))
            {
                GUIFrame imgFrame = new GUIFrame(new RectTransform(new Point(listBox.Content.Rect.Width, listBox.Rect.Width / 2), listBox.Content.RectTransform), style: "ListBoxElement")
                {
                    UserData = itemPrefab
                };
                var img = new GUIImage(new RectTransform(new Vector2(0.9f), imgFrame.RectTransform, Anchor.Center), itemPrefab.Sprite, scaleToFit: true)
                {
                    UserData = itemPrefab,
                    Color = itemPrefab.SpriteColor,
                    HoverColor = Color.Lerp(itemPrefab.SpriteColor, Color.White, 0.3f),
                    SelectedColor = Color.Lerp(itemPrefab.SpriteColor, Color.White, 0.6f)
                };
            }

            return frame;
        }

        private bool SelectLinkedSub(GUIComponent selected, object userData)
        {
            if (userData is not SubmarineInfo submarine) { return false; }
            var prefab = new LinkedSubmarinePrefab(submarine);
            MapEntityPrefab.SelectPrefab(prefab);
            return true;
        }

        private bool SelectWire(GUIComponent component, object userData)
        {
            if (dummyCharacter == null) return false;

            //if the same type of wire has already been selected, deselect it and return
            Item existingWire = dummyCharacter.HeldItems.FirstOrDefault(i => i.Prefab == userData as ItemPrefab);
            if (existingWire != null)
            {
                existingWire.Drop(null);
                existingWire.Remove();
                return false;
            }

            var wire = new Item(userData as ItemPrefab, Vector2.Zero, null);

            int slotIndex = dummyCharacter.Inventory.FindLimbSlot(InvSlotType.LeftHand);

            //if there's some other type of wire in the inventory, remove it
            existingWire = dummyCharacter.Inventory.GetItemAt(slotIndex);
            if (existingWire != null && existingWire.Prefab != userData as ItemPrefab)
            {
                existingWire.Drop(null);
                existingWire.Remove();
            }

            dummyCharacter.Inventory.TryPutItem(wire, slotIndex, false, false, dummyCharacter);

            return true;

        }

        /// <summary>
        /// Tries to open an item in the submarine editor using the dummy character
        /// </summary>
        /// <param name="item">The item we want to open</param>
        private void OpenItem(Item item)
        {
            if (dummyCharacter == null || item == null) { return; }

            if ((item.GetComponent<Holdable>() is { Attached: false } || item.GetComponent<Wearable>() != null) && item.GetComponent<ItemContainer>() != null)
            {
                // We teleport our dummy character to the item so it appears as the entity stays still when in reality the dummy is holding it
                oldItemPosition = item.SimPosition;
                TeleportDummyCharacter(oldItemPosition);

                // Override this so we can be sure the container opens
                var container = item.GetComponent<ItemContainer>();
                if (container != null) { container.KeepOpenWhenEquipped = true; }

                // We accept any slots except "Any" since that would take priority
                List<InvSlotType> allowedSlots = new List<InvSlotType>();
                item.AllowedSlots.ForEach(type =>
                {
                    if (type != InvSlotType.Any) { allowedSlots.Add(type); }
                });

                // Try to place the item in the dummy character's inventory
                bool success = dummyCharacter.Inventory.TryPutItem(item, dummyCharacter, allowedSlots);
                if (success) { OpenedItem = item; }
                else { return; }
            }
            MapEntity.SelectedList.Clear();
            MapEntity.FilteredSelectedList.Clear();
            MapEntity.SelectEntity(item);
            dummyCharacter.SelectedItem = item;
            FilterEntities(entityFilterBox.Text);
            MapEntity.StopSelection();
        }

        /// <summary>
        /// Close the currently opened item
        /// </summary>
        private void CloseItem()
        {
            if (dummyCharacter == null) { return; }
            //nothing to close -> return
            if (DraggedItemPrefab == null && dummyCharacter?.SelectedItem == null && OpenedItem == null) { return; }
            DraggedItemPrefab = null;
            dummyCharacter.SelectedItem = null;
            OpenedItem?.Drop(dummyCharacter);
            OpenedItem?.SetTransform(oldItemPosition, 0f);
            OpenedItem = null;
            FilterEntities(entityFilterBox.Text);
        }

        /// <summary>
        /// Teleports the dummy character to the specified position
        /// </summary>
        /// <param name="pos">The desired position</param>
        private void TeleportDummyCharacter(Vector2 pos)
        {
            if (dummyCharacter != null)
            {
                foreach (Limb limb in dummyCharacter.AnimController.Limbs)
                {
                    limb.body.SetTransform(pos, 0.0f);
                }
                dummyCharacter.AnimController.Collider.SetTransform(pos, 0);
            }
        }

        private bool ChangeSubName(GUITextBox textBox, string text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                textBox.Flash(GUIStyle.Red);
                return false;
            }

            if (MainSub != null) { MainSub.Info.Name = text; }
            textBox.Deselect();
            textBox.Text = text;
            textBox.Flash(GUIStyle.Green);

            return true;
        }

        private bool ChangeEnemySubTags(GUITextBox textBox, string text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                textBox.Flash(GUIStyle.Red);
                return false;
            }

            if (MainSub.Info.EnemySubmarineInfo is { } enemySubInfo) 
            {
                enemySubInfo.MissionTags.Clear();
                string[] tags = text.Split(',');
                foreach (string tag in tags)
                {
                    enemySubInfo.MissionTags.Add(tag.ToIdentifier());
                }
            }
            textBox.Text = text;
            textBox.Flash(GUIStyle.Green);

            return true;
        }

        private void ChangeSubDescription(GUITextBox textBox, string text)
        {
            if (MainSub != null)
            {
                MainSub.Info.Description = text;
            }
            else
            {
                textBox.UserData = text;
            }

            submarineDescriptionCharacterCount.Text = text.Length + " / " + submarineDescriptionLimit;
        }

        private bool SelectPrefab(GUIComponent component, object obj)
        {
            allEntityList.Deselect();
            categorizedEntityList.Deselect();
            if (GUI.MouseOn is GUIButton || GUI.MouseOn?.Parent is GUIButton) { return false; }

            AddPreviouslyUsed(obj as MapEntityPrefab);

            //if selecting a gap/hull/waypoint/spawnpoint, make sure the visibility is toggled on
            if (obj is CoreEntityPrefab prefab)
            {
                var matchingTickBox = showEntitiesTickBoxes.Find(tb => tb.UserData as string == prefab.Identifier);
                if (matchingTickBox != null && !matchingTickBox.Selected)
                {
                    previouslyUsedPanel.Visible = false;
                    showEntitiesPanel.Visible = true;
                    showEntitiesPanel.RectTransform.AbsoluteOffset = new Point(Math.Max(entityCountPanel.Rect.Right, saveAssemblyFrame.Rect.Right), TopPanel.Rect.Height);
                    matchingTickBox.Selected = true;
                    matchingTickBox.Flash(GUIStyle.Green);
                }
            }

            if (dummyCharacter?.SelectedItem != null)
            {
                var inv = dummyCharacter?.SelectedItem?.OwnInventory;
                if (inv != null)
                {
                    switch (obj)
                    {
                        case ItemAssemblyPrefab assemblyPrefab when PlayerInput.IsShiftDown():
                        {
                            var itemInstance = LoadItemAssemblyInventorySafe(assemblyPrefab);
                            var spawnedItem = false;

                            itemInstance.ForEach(newItem =>
                            {
                                if (newItem != null)
                                {
                                    var placedItem = inv.TryPutItem(newItem, dummyCharacter);
                                    spawnedItem |= placedItem;

                                    if (!placedItem)
                                    {
                                        // Remove everything inside of the item so we don't get the popup asking if we want to keep the contained items
                                        newItem.OwnInventory?.DeleteAllItems();
                                        newItem.Remove();
                                    }
                                }
                            });

                            List<MapEntity> placedEntities = itemInstance.Where(it => !it.Removed).Cast<MapEntity>().ToList();
                            if (placedEntities.Any())
                            {
                                StoreCommand(new AddOrDeleteCommand(placedEntities, false));
                            }
                            SoundPlayer.PlayUISound(spawnedItem ? GUISoundType.PickItem : GUISoundType.PickItemFail);
                            break;
                        }
                        case ItemPrefab itemPrefab when PlayerInput.IsShiftDown():
                        {
                            var item = new Item(itemPrefab, Vector2.Zero, MainSub);
                            if (!inv.TryPutItem(item, dummyCharacter))
                            {
                                // We failed, remove the item so it doesn't stay at x:0,y:0
                                SoundPlayer.PlayUISound(GUISoundType.PickItemFail);
                                item.Remove();
                            }
                            else
                            {
                                SoundPlayer.PlayUISound(GUISoundType.PickItem);
                            }

                            if (!item.Removed)
                            {
                                StoreCommand(new AddOrDeleteCommand(new List<MapEntity> { item }, false));
                            }
                            break;
                        }
                        case ItemAssemblyPrefab _:
                        case ItemPrefab _:
                        {
                            // Place the item into our hands
                            DraggedItemPrefab = (MapEntityPrefab)obj;
                            SoundPlayer.PlayUISound(GUISoundType.PickItem);
                            break;
                        }
                    }
                }
            }
            else
            {
                SoundPlayer.PlayUISound(GUISoundType.PickItem);
                MapEntityPrefab.SelectPrefab(obj);
            }

            return false;
        }

        private bool GenerateWaypoints()
        {
            if (MainSub == null) { return false; }
            return WayPoint.GenerateSubWaypoints(MainSub);
        }

        private void AddPreviouslyUsed(MapEntityPrefab mapEntityPrefab)
        {
            if (previouslyUsedList == null || mapEntityPrefab == null) { return; }

            previouslyUsedList.Deselect();

            if (previouslyUsedList.CountChildren == PreviouslyUsedCount)
            {
                previouslyUsedList.RemoveChild(previouslyUsedList.Content.Children.Last());
            }

            var existing = previouslyUsedList.Content.FindChild(mapEntityPrefab);
            if (existing != null) { previouslyUsedList.Content.RemoveChild(existing); }

            var textBlock = new GUITextBlock(new RectTransform(new Vector2(1.0f, 0.05f), previouslyUsedList.Content.RectTransform) { MinSize = new Point(0, 15) },
                ToolBox.LimitString(mapEntityPrefab.Name.Value, GUIStyle.SmallFont, previouslyUsedList.Content.Rect.Width), font: GUIStyle.SmallFont)
            {
                UserData = mapEntityPrefab
            };
            textBlock.RectTransform.SetAsFirstChild();
        }

        public void AutoHull()
        {
            for (int i = 0; i < MapEntity.MapEntityList.Count; i++)
            {
                MapEntity h = MapEntity.MapEntityList[i];
                if (h is Hull || h is Gap)
                {
                    h.Remove();
                    i--;
                }
            }

            List<Vector2> wallPoints = new List<Vector2>();
            Vector2 max;

            List<MapEntity> mapEntityList = new List<MapEntity>();

            foreach (MapEntity e in MapEntity.MapEntityList)
            {
                if (e is Item it)
                {
                    Door door = it.GetComponent<Door>();
                    if (door != null)
                    {
                        int halfW = it.WorldRect.Width / 2;
                        wallPoints.Add(new Vector2(it.WorldRect.X + halfW, -it.WorldRect.Y + it.WorldRect.Height));
                        mapEntityList.Add(it);
                    }
                    continue;
                }

                if (!(e is Structure)) continue;
                Structure s = e as Structure;
                if (!s.HasBody) continue;
                mapEntityList.Add(e);

                if (e.Rect.Width > e.Rect.Height)
                {
                    int halfH = e.WorldRect.Height / 2;
                    wallPoints.Add(new Vector2(e.WorldRect.X, -e.WorldRect.Y + halfH));
                    wallPoints.Add(new Vector2(e.WorldRect.X + e.WorldRect.Width, -e.WorldRect.Y + halfH));
                }
                else
                {
                    int halfW = e.WorldRect.Width / 2;
                    wallPoints.Add(new Vector2(e.WorldRect.X + halfW, -e.WorldRect.Y));
                    wallPoints.Add(new Vector2(e.WorldRect.X + halfW, -e.WorldRect.Y + e.WorldRect.Height));
                }
            }

            if (wallPoints.Count < 4)
            {
                DebugConsole.ThrowError("Generating hulls for the submarine failed. Not enough wall structures to generate hulls.");
                return;
            }

            var min = wallPoints[0];
            max = wallPoints[0];
            for (int i = 0; i < wallPoints.Count; i++)
            {
                min.X = Math.Min(min.X, wallPoints[i].X);
                min.Y = Math.Min(min.Y, wallPoints[i].Y);
                max.X = Math.Max(max.X, wallPoints[i].X);
                max.Y = Math.Max(max.Y, wallPoints[i].Y);
            }

            List<Rectangle> hullRects = new List<Rectangle>
            {
                new Rectangle((int)min.X, (int)min.Y, (int)(max.X - min.X), (int)(max.Y - min.Y))
            };
            foreach (Vector2 point in wallPoints)
            {
                MathUtils.SplitRectanglesHorizontal(hullRects, point);
                MathUtils.SplitRectanglesVertical(hullRects, point);
            }

            hullRects.Sort((a, b) =>
            {
                if (a.Y < b.Y) return -1;
                if (a.Y > b.Y) return 1;
                if (a.X < b.X) return -1;
                if (a.X > b.X) return 1;
                return 0;
            });

            for (int i = 0; i < hullRects.Count - 1; i++)
            {
                Rectangle rect = hullRects[i];
                if (hullRects[i + 1].Y > rect.Y) continue;

                Vector2 hullRPoint = new Vector2(rect.X + rect.Width - 8, rect.Y + rect.Height / 2);
                Vector2 hullLPoint = new Vector2(rect.X, rect.Y + rect.Height / 2);

                MapEntity container = null;
                foreach (MapEntity e in mapEntityList)
                {
                    Rectangle entRect = e.WorldRect;
                    entRect.Y = -entRect.Y;
                    if (entRect.Contains(hullRPoint))
                    {
                        if (!entRect.Contains(hullLPoint)) container = e;
                        break;
                    }
                }
                if (container == null)
                {
                    rect.Width += hullRects[i + 1].Width;
                    hullRects[i] = rect;
                    hullRects.RemoveAt(i + 1);
                    i--;
                }
            }

            foreach (MapEntity e in mapEntityList)
            {
                Rectangle entRect = e.WorldRect;
                if (entRect.Width < entRect.Height) continue;
                entRect.Y = -entRect.Y - 16;
                for (int i = 0; i < hullRects.Count; i++)
                {
                    Rectangle hullRect = hullRects[i];
                    if (entRect.Intersects(hullRect))
                    {
                        if (hullRect.Y < entRect.Y)
                        {
                            hullRect.Height = Math.Max((entRect.Y + 16 + entRect.Height / 2) - hullRect.Y, hullRect.Height);
                            hullRects[i] = hullRect;
                        }
                        else if (hullRect.Y + hullRect.Height <= entRect.Y + 16 + entRect.Height)
                        {
                            hullRects.RemoveAt(i);
                            i--;
                        }
                    }
                }
            }

            foreach (MapEntity e in mapEntityList)
            {
                Rectangle entRect = e.WorldRect;
                if (entRect.Width < entRect.Height) continue;
                entRect.Y = -entRect.Y;
                for (int i = 0; i < hullRects.Count; i++)
                {
                    Rectangle hullRect = hullRects[i];
                    if (entRect.Intersects(hullRect))
                    {
                        if (hullRect.Y >= entRect.Y - 8 && hullRect.Y + hullRect.Height <= entRect.Y + entRect.Height + 8)
                        {
                            hullRects.RemoveAt(i);
                            i--;
                        }
                    }
                }
            }

            for (int i = 0; i < hullRects.Count;)
            {
                Rectangle hullRect = hullRects[i];
                Vector2 point = new Vector2(hullRect.X+2, hullRect.Y+hullRect.Height/2);
                MapEntity container = null;
                foreach (MapEntity e in mapEntityList)
                {
                    Rectangle entRect = e.WorldRect;
                    entRect.Y = -entRect.Y;
                    if (entRect.Contains(point))
                    {
                        container = e;
                        break;
                    }
                }
                if (container == null)
                {
                    hullRects.RemoveAt(i);
                    continue;
                }

                while (hullRects[i].Y <= hullRect.Y)
                {
                    i++;
                    if (i >= hullRects.Count) break;
                }
            }

            for (int i = hullRects.Count-1; i >= 0;)
            {
                Rectangle hullRect = hullRects[i];
                Vector2 point = new Vector2(hullRect.X+hullRect.Width-2, hullRect.Y+hullRect.Height/2);
                MapEntity container = null;
                foreach (MapEntity e in mapEntityList)
                {
                    Rectangle entRect = e.WorldRect;
                    entRect.Y = -entRect.Y;
                    if (entRect.Contains(point))
                    {
                        container = e;
                        break;
                    }
                }
                if (container == null)
                {
                    hullRects.RemoveAt(i); i--;
                    continue;
                }

                while (hullRects[i].Y >= hullRect.Y)
                {
                    i--;
                    if (i < 0) break;
                }
            }

            hullRects.Sort((a, b) =>
            {
                if (a.X < b.X) return -1;
                if (a.X > b.X) return 1;
                if (a.Y < b.Y) return -1;
                if (a.Y > b.Y) return 1;
                return 0;
            });

            for (int i = 0; i < hullRects.Count - 1; i++)
            {
                Rectangle rect = hullRects[i];
                if (hullRects[i + 1].Width != rect.Width) continue;
                if (hullRects[i + 1].X > rect.X) continue;

                Vector2 hullBPoint = new Vector2(rect.X + rect.Width / 2, rect.Y + rect.Height - 8);
                Vector2 hullUPoint = new Vector2(rect.X + rect.Width / 2, rect.Y);

                MapEntity container = null;
                foreach (MapEntity e in mapEntityList)
                {
                    Rectangle entRect = e.WorldRect;
                    entRect.Y = -entRect.Y;
                    if (entRect.Contains(hullBPoint))
                    {
                        if (!entRect.Contains(hullUPoint)) container = e;
                        break;
                    }
                }
                if (container == null)
                {
                    rect.Height += hullRects[i + 1].Height;
                    hullRects[i] = rect;
                    hullRects.RemoveAt(i + 1);
                    i--;
                }
            }

            for (int i = 0; i < hullRects.Count;i++)
            {
                Rectangle rect = hullRects[i];
                rect.Y -= 16;
                rect.Height += 32;
                hullRects[i] = rect;
            }

            hullRects.Sort((a, b) =>
            {
                if (a.Y < b.Y) return -1;
                if (a.Y > b.Y) return 1;
                if (a.X < b.X) return -1;
                if (a.X > b.X) return 1;
                return 0;
            });

            for (int i = 0; i < hullRects.Count; i++)
            {
                for (int j = i+1; j < hullRects.Count; j++)
                {
                    if (hullRects[j].Y <= hullRects[i].Y) continue;
                    if (hullRects[j].Intersects(hullRects[i]))
                    {
                        Rectangle rect = hullRects[i];
                        rect.Height = hullRects[j].Y - rect.Y;
                        hullRects[i] = rect;
                        break;
                    }
                }
            }

            foreach (Rectangle rect in hullRects)
            {
                Rectangle hullRect = rect;
                hullRect.Y = -hullRect.Y;
                Hull newHull = new Hull(hullRect,
                                        MainSub);
            }

            foreach (MapEntity e in mapEntityList)
            {
                if (!(e is Structure)) continue;
                if (!(e as Structure).IsPlatform) continue;

                Rectangle gapRect = e.WorldRect;
                gapRect.Y -= 8;
                gapRect.Height = 16;
                new Gap(gapRect);
            }
        }

        public override void AddToGUIUpdateList()
        {
            if (GUI.DisableHUD) { return; }

            MapEntity.FilteredSelectedList.FirstOrDefault()?.AddToGUIUpdateList();
            EntityMenu.AddToGUIUpdateList();
            showEntitiesPanel.AddToGUIUpdateList();
            previouslyUsedPanel.AddToGUIUpdateList();
            undoBufferPanel.AddToGUIUpdateList();
            entityCountPanel.AddToGUIUpdateList();
            layerPanel.AddToGUIUpdateList();
            TopPanel.AddToGUIUpdateList();

            if (WiringMode)
            {
                wiringToolPanel.AddToGUIUpdateList();
            }

            if (MapEntity.HighlightedListBox != null)
            {
                MapEntity.HighlightedListBox.AddToGUIUpdateList();
            }

            if (dummyCharacter != null)
            {
                CharacterHUD.AddToGUIUpdateList(dummyCharacter);
                if (dummyCharacter.SelectedItem != null)
                {
                    dummyCharacter.SelectedItem.AddToGUIUpdateList();
                }
                else if (WiringMode && MapEntity.SelectedList.FirstOrDefault() is Item item && item.GetComponent<Wire>() != null)
                {
                    MapEntity.SelectedList.FirstOrDefault()?.AddToGUIUpdateList();
                }
            }
            if (loadFrame != null)
            {
                loadFrame.AddToGUIUpdateList();
            }
            else
            {
                saveFrame?.AddToGUIUpdateList();
            }
        }

        /// <summary>
        /// GUI.MouseOn doesn't get updated while holding primary mouse and we need it to
        /// </summary>
        public bool IsMouseOnEditorGUI()
        {
            if (GUI.MouseOn == null) { return false; }

            return (EntityMenu?.MouseRect.Contains(PlayerInput.MousePosition) ?? false)
                   || (entityCountPanel?.MouseRect.Contains(PlayerInput.MousePosition) ?? false)
                   || (MapEntity.EditingHUD?.MouseRect.Contains(PlayerInput.MousePosition) ?? false)
                   || (TopPanel?.MouseRect.Contains(PlayerInput.MousePosition) ?? false);
        }

        private static void Redo(int amount)
        {
            for (int i = 0; i < amount; i++)
            {
                if (commandIndex < Commands.Count)
                {
                    Command command = Commands[commandIndex++];
                    command.Execute();
                }
            }
            GameMain.SubEditorScreen.UpdateUndoHistoryPanel();
        }

        private static void Undo(int amount)
        {
            for (int i = 0; i < amount; i++)
            {
                if (commandIndex > 0)
                {
                    Command command = Commands[--commandIndex];
                    command.UnExecute();
                }
            }
            GameMain.SubEditorScreen.UpdateUndoHistoryPanel();
        }

        private static void ClearUndoBuffer()
        {
            SerializableEntityEditor.PropertyChangesActive = false;
            SerializableEntityEditor.CommandBuffer = null;
            Commands.ForEach(cmd => cmd.Cleanup());
            Commands.Clear();
            commandIndex = 0;
            GameMain.SubEditorScreen.UpdateUndoHistoryPanel();
        }

        public static void StoreCommand(Command command)
        {
            if (commandIndex != Commands.Count)
            {
                Commands.RemoveRange(commandIndex, Commands.Count - commandIndex);
            }
            Commands.Add(command);
            commandIndex++;

            // Start removing old commands
            if (Commands.Count > Math.Clamp(GameSettings.CurrentConfig.SubEditorUndoBuffer, 1, 10240))
            {
                Commands.First()?.Cleanup();
                Commands.RemoveRange(0, 1);
                commandIndex = Commands.Count;
            }

            GameMain.SubEditorScreen.UpdateUndoHistoryPanel();

            if (command is AddOrDeleteCommand addOrDelete)
            {
                GameMain.SubEditorScreen.EntityAddedOrDeleted(addOrDelete.Receivers);
            }
        }

        private string prevSelectedLayer;
        private void EntityAddedOrDeleted(IEnumerable<MapEntity> entities)
        {
            if (layerList?.SelectedData is string selectedLayer)
            {
                //add the created entities to the selected layer
                foreach (var entity in entities)
                {
                    if (!entity.Removed)
                    {
                        entity.Layer = selectedLayer;
                    }
                }
                var layerElement = layerList.Content.FindChild(selectedLayer);
                layerElement?.Flash(GUIStyle.Green);
            }
        }

        private void UpdateLayerPanel()
        {
            if (layerPanel is null || layerList is null) { return; }

            layerList.Content.ClearChildren();

            layerList.Deselect();
            layerSpecificButtons.ForEach(btn => btn.Enabled = false);
            GUILayoutGroup buttonHeaders = new GUILayoutGroup(new RectTransform(new Vector2(1f, 0.075f), layerList.Content.RectTransform), isHorizontal: true, childAnchor: Anchor.BottomLeft);

            new GUIButton(new RectTransform(new Vector2(0.25f, 1f), buttonHeaders.RectTransform), TextManager.Get("editor.layer.headervisible"), style: "GUIButtonSmallFreeScale") { ForceUpperCase = ForceUpperCase.Yes };
            new GUIButton(new RectTransform(new Vector2(0.15f, 1f), buttonHeaders.RectTransform), TextManager.Get("editor.layer.headerlink"), style: "GUIButtonSmallFreeScale")
            {
                ForceUpperCase = ForceUpperCase.Yes,
                ToolTip = TextManager.Get("editor.layer.headerlink.tooltip")
            };
            new GUIButton(new RectTransform(new Vector2(0.6f, 1f), buttonHeaders.RectTransform), TextManager.Get("name"), style: "GUIButtonSmallFreeScale") { ForceUpperCase = ForceUpperCase.Yes };

            foreach ((string layer, (bool isVisible, bool isGrouped)) in Layers)
            {
                GUIFrame parent = new GUIFrame(new RectTransform(new Vector2(1f, 0.1f), layerList.Content.RectTransform), style: "ListBoxElement")
                {
                    UserData = layer
                };

                GUILayoutGroup layerGroup = new GUILayoutGroup(new RectTransform(Vector2.One, parent.RectTransform), isHorizontal: true, childAnchor: Anchor.CenterLeft);

                GUILayoutGroup layerVisibilityLayout = new GUILayoutGroup(new RectTransform(new Vector2(0.25f, 1f), layerGroup.RectTransform), childAnchor: Anchor.Center);
                GUITickBox layerVisibleButton = new GUITickBox(new RectTransform(Vector2.One, layerVisibilityLayout.RectTransform, scaleBasis: ScaleBasis.BothHeight), string.Empty)
                {
                    Selected = isVisible,
                    OnSelected = box =>
                    {
                        if (!Layers.TryGetValue(layer, out LayerData data))
                        {
                            UpdateLayerPanel();
                            return false;
                        }
                        if (!box.Selected && layerList.SelectedData as string == layer)
                        {
                            //hiding a layer automatically deselects it (can't edit a hidden layer)
                            if (!box.Selected)
                            {
                                layerList.Deselect();
                            }
                        }
                        Layers[layer] = data with { IsVisible = box.Selected };
                        return true;
                    }
                };

                GUILayoutGroup layerChainLayout = new GUILayoutGroup(new RectTransform(new Vector2(0.15f, 1f), layerGroup.RectTransform), childAnchor: Anchor.Center);
                GUITickBox layerChainButton = new GUITickBox(new RectTransform(Vector2.One, layerChainLayout.RectTransform, scaleBasis: ScaleBasis.BothHeight), string.Empty)
                {
                    Selected = isGrouped,
                    OnSelected = box =>
                    {
                        if (!Layers.TryGetValue(layer, out LayerData data))
                        {
                            UpdateLayerPanel();
                            return false;
                        }

                        Layers[layer] = data with { IsGrouped = box.Selected };
                        return true;
                    }
                };

                layerGroup.Recalculate();

                var textBlock = new GUITextBlock(new RectTransform(new Vector2(0.6f, 1f), layerGroup.RectTransform), layer, textAlignment: Alignment.CenterLeft);
                if (textBlock.TextSize.X > textBlock.Rect.Width)
                {
                    textBlock.ToolTip = textBlock.Text;
                    textBlock.Text = ToolBox.LimitString(textBlock.Text, textBlock.Font, textBlock.Rect.Width);
                }

                layerGroup.Recalculate();
                layerChainLayout.Recalculate();
                layerVisibilityLayout.Recalculate();
            }

            layerList.RecalculateChildren();
            buttonHeaders.Recalculate();
            foreach (var child in buttonHeaders.Children)
            {
                var btn = child as GUIButton;
                string originalBtnText = btn.Text.Value;
                btn.Text = ToolBox.LimitString(btn.Text, btn.Font, btn.Rect.Width);
                if (originalBtnText != btn.Text && btn.ToolTip.IsNullOrEmpty())
                {
                    btn.ToolTip = originalBtnText;
                }
            }
        }

        public void UpdateUndoHistoryPanel()
        {
            if (undoBufferPanel is null) { return; }

            undoBufferDisclaimer.Visible = mode == Mode.Wiring;

            undoBufferList.Content.Children.ForEachMod(component =>
            {
                undoBufferList.Content.RemoveChild(component);
            });

            for (int i = 0; i < Commands.Count; i++)
            {
                Command command = Commands[i];
                LocalizedString description = command.GetDescription();
                CreateTextBlock(description, description, i + 1, command).RectTransform.SetAsFirstChild();
            }

            CreateTextBlock(TextManager.Get("undo.beginning"), TextManager.Get("undo.beginningtooltip"), 0, null);

            GUITextBlock CreateTextBlock(LocalizedString name, LocalizedString description, int index, Command command)
            {
                return new GUITextBlock(new RectTransform(new Vector2(1.0f, 0.05f), undoBufferList.Content.RectTransform) { MinSize = new Point(0, 15) },
                    ToolBox.LimitString(name.Value, GUIStyle.SmallFont, undoBufferList.Content.Rect.Width), font: GUIStyle.SmallFont, textColor: index == commandIndex ? GUIStyle.Green : (Color?) null)
                {
                    UserData = command,
                    ToolTip = description
                };
            }
        }

        private static void CommitBulkItemBuffer()
        {
            if (BulkItemBuffer.Any())
            {
                AddOrDeleteCommand master = BulkItemBuffer[0];
                for (int i = 1; i < BulkItemBuffer.Count; i++)
                {
                    AddOrDeleteCommand command = BulkItemBuffer[i];
                    command.MergeInto(master);
                }

                StoreCommand(master);
                BulkItemBuffer.Clear();
            }

            bulkItemBufferinUse = null;
        }

        /// <summary>
        /// Allows the game to run logic such as updating the world,
        /// checking for collisions, gathering input, and playing audio.
        /// </summary>
        public override void Update(double deltaTime)
        {
            SkipInventorySlotUpdate = false;
            ImageManager.Update((float)deltaTime);

            Hull.UpdateCheats((float)deltaTime, cam);

            if (GameMain.GraphicsWidth != screenResolution.X || GameMain.GraphicsHeight != screenResolution.Y)
            {
                saveFrame = null;
                loadFrame = null;
                saveAssemblyFrame = null;
                snapToGridFrame = null;
                CreateUI();
                UpdateEntityList();
            }

            if (OpenedItem != null && OpenedItem.Removed)
            {
                OpenedItem = null;
            }

            if (WiringMode && dummyCharacter != null)
            {
                Wire equippedWire =
                    Character.Controlled?.HeldItems.FirstOrDefault(it => it.GetComponent<Wire>() != null)?.GetComponent<Wire>() ??
                    Wire.DraggingWire;

                if (equippedWire == null)
                {
                    // Highlight wires when hovering over the entity selection box
                    if (MapEntity.HighlightedListBox != null)
                    {
                        var lBox = MapEntity.HighlightedListBox;
                        foreach (var child in lBox.Content.Children)
                        {
                            if (child.UserData is Item item)
                            {
                                item.ExternalHighlight = GUI.IsMouseOn(child);
                            }
                        }
                    }

                    var highlightedEntities = new List<MapEntity>();

                    // ReSharper disable once LoopCanBeConvertedToQuery
                    foreach (Item item in MapEntity.MapEntityList.Where(entity => entity is Item).Cast<Item>())
                    {
                        var wire = item.GetComponent<Wire>();
                        if (wire == null || !wire.IsMouseOn()) { continue; }
                        highlightedEntities.Add(item);
                    }

                    MapEntity.UpdateHighlighting(highlightedEntities, true);
                }
            }

            hullVolumeFrame.Visible = MapEntity.SelectedList.Any(s => s is Hull);
            hullVolumeFrame.RectTransform.AbsoluteOffset = new Point(Math.Max(showEntitiesPanel.Rect.Right, previouslyUsedPanel.Rect.Right), 0);
            bool isCircuitBoxOpened = dummyCharacter?.SelectedItem?.GetComponent<CircuitBox>() is not null;
            saveAssemblyFrame.Visible = MapEntity.SelectedList.Count > 0 && !WiringMode && !isCircuitBoxOpened;
            snapToGridFrame.Visible = MapEntity.SelectedList.Count > 0 && !WiringMode && !isCircuitBoxOpened;

            var offset = cam.WorldView.Top - cam.ScreenToWorld(new Vector2(0, GameMain.GraphicsHeight - EntityMenu.Rect.Top)).Y;

            // Move the camera towards to the focus point
            if (camTargetFocus != Vector2.Zero)
            {
                if (GameSettings.CurrentConfig.KeyMap.Bindings[InputType.Up].IsDown() || GameSettings.CurrentConfig.KeyMap.Bindings[InputType.Down].IsDown() ||
                    GameSettings.CurrentConfig.KeyMap.Bindings[InputType.Left].IsDown() || GameSettings.CurrentConfig.KeyMap.Bindings[InputType.Right].IsDown())
                {
                    camTargetFocus = Vector2.Zero;
                }
                else
                {
                    var targetWithOffset = new Vector2(camTargetFocus.X, camTargetFocus.Y - offset / 2);
                    if (Math.Abs(cam.Position.X - targetWithOffset.X) < 1.0f &&
                        Math.Abs(cam.Position.Y - targetWithOffset.Y) < 1.0f)
                    {
                        camTargetFocus = Vector2.Zero;
                    }
                    else
                    {
                        cam.Position += (targetWithOffset - cam.Position) / cam.MoveSmoothness;
                    }
                }
            }

            if (undoBufferPanel.Visible)
            {
                undoBufferList.Deselect();
            }

            if (GUI.KeyboardDispatcher.Subscriber == null
                || MapEntity.EditingHUD != null
                && GUI.KeyboardDispatcher.Subscriber is GUIComponent sub
                && MapEntity.EditingHUD.Children.Contains(sub))
            {
                if (PlayerInput.IsCtrlDown() && !WiringMode)
                {
                    if (PlayerInput.KeyHit(Keys.Z))
                    {
                        // Ctrl+Shift+Z redos while Ctrl+Z undos
                        if (PlayerInput.IsShiftDown()) { Redo(1); } else { Undo(1); }
                    }

                    // ctrl+Y redo
                    if (PlayerInput.KeyHit(Keys.Y))
                    {
                        Redo(1);
                    }
                }
            }

            if (GUI.KeyboardDispatcher.Subscriber == null)
            {
                if (WiringMode && dummyCharacter != null)
                {
                    if (wiringToolPanel.GetChild<GUIListBox>() is { } listBox)
                    {
                        if (!dummyCharacter.HeldItems.Any(it => it.HasTag(Tags.WireItem)))
                        {
                            listBox.Deselect();
                        }

                        List<Keys> numberKeys = PlayerInput.NumberKeys;
                        if (numberKeys.Find(PlayerInput.KeyHit) is { } key && key != Keys.None)
                        {
                            // treat 0 as the last key instead of first
                            int index = key == Keys.D0 ? numberKeys.Count : numberKeys.IndexOf(key) - 1;
                            if (index > -1 && index < listBox.Content.CountChildren)
                            {
                                listBox.Select(index);
                                SkipInventorySlotUpdate = true;
                            }
                        }
                    }
                }

                if (mode == Mode.Default)
                {
                    if (PlayerInput.KeyHit(InputType.Use))
                    {
                        if (dummyCharacter != null)
                        {
                            if (dummyCharacter.SelectedItem == null)
                            {
                                foreach (var entity in MapEntity.HighlightedEntities)
                                {
                                    if (entity is Item item && item.Components.Any(ic => ic is not ConnectionPanel && ic is not Repairable && ic.GuiFrame != null))
                                    {
                                        var container = item.GetComponents<ItemContainer>().ToList();
                                        if (!container.Any() || container.Any(ic => ic?.DrawInventory ?? false))
                                        {
                                            OpenItem(item);
                                            break;
                                        }
                                    }
                                }
                            }
                            else
                            {
                                CloseItem();
                            }
                        }
                    }

                    // Focus to selection
                    if (PlayerInput.KeyHit(Keys.F))
                    {
                        // content warning: contains coordinate system workarounds
                        var selected = MapEntity.SelectedList;
                        if (selected.Count > 0)
                        {
                            var dRect = selected.First().Rect;
                            var rect = new Rectangle(dRect.Left, dRect.Top, dRect.Width, dRect.Height * -1);
                            if (selected.Count > 1)
                            {
                                // Create one big rect out of our selection
                                selected.Skip(1).ForEach(me =>
                                {
                                    var wRect = me.Rect;
                                    rect = Rectangle.Union(rect, new Rectangle(wRect.Left, wRect.Top, wRect.Width, wRect.Height * -1));
                                });
                            }
                            camTargetFocus = rect.Center.ToVector2();
                        }
                    }
                    if (PlayerInput.KeyHit(Keys.Tab))
                    {
                        entityFilterBox.Select();
                    }
                }

                if (toggleEntityListBind != GameSettings.CurrentConfig.KeyMap.Bindings[InputType.ToggleInventory])
                {
                    toggleEntityMenuButton.ToolTip = RichString.Rich($"{TextManager.Get("EntityMenuToggleTooltip")}\n‖color:125,125,125‖{GameSettings.CurrentConfig.KeyMap.Bindings[InputType.ToggleInventory].Name}‖color:end‖");
                    toggleEntityListBind = GameSettings.CurrentConfig.KeyMap.Bindings[InputType.ToggleInventory];
                }
                if (GameSettings.CurrentConfig.KeyMap.Bindings[InputType.ToggleInventory].IsHit() && mode == Mode.Default)
                {
                    toggleEntityMenuButton.OnClicked?.Invoke(toggleEntityMenuButton, toggleEntityMenuButton.UserData);
                }

                if (PlayerInput.IsCtrlDown() && MapEntity.StartMovingPos == Vector2.Zero)
                {
                    cam.MoveCamera((float) deltaTime, allowMove: false, allowZoom: GUI.MouseOn == null);
                    // Save menu
                    if (PlayerInput.KeyHit(Keys.S))
                    {
                        if (PlayerInput.IsShiftDown())
                        {
                            // Quick-save, but only when we've set a custom name for our sub
                            CreateSaveScreen(subNameLabel != null && subNameLabel.Text != TextManager.Get("unspecifiedsubfilename"));
                        }
                        else
                        {
                            // Save menu
                            CreateSaveScreen();
                        }
                    }

                    // Select or deselect everything
                    if (PlayerInput.KeyHit(Keys.A) && mode == Mode.Default)
                    {
                        if (MapEntity.SelectedList.Any())
                        {
                            MapEntity.DeselectAll();
                        }
                        else
                        {
                            var selectables = MapEntity.MapEntityList.Where(entity => entity.SelectableInEditor).ToList();
                            foreach (var item in Item.ItemList)
                            {
                                //attached wires are not normally selectable (by clicking),
                                //but let's select them manually when selecting all
                                var wire = item.GetComponent<Wire>();
                                if (wire != null && wire.Connections.None(c => c == null) && !selectables.Contains(item))
                                {
                                    selectables.Add(item);
                                }
                            }
                            lock (selectables)
                            {
                                selectables.ForEach(MapEntity.AddSelection);
                            }
                        }
                    }

                    // 1-2 keys on the keyboard for switching modes
                    if (PlayerInput.KeyHit(Keys.D1)) { SetMode(Mode.Default); }
                    if (PlayerInput.KeyHit(Keys.D2)) { SetMode(Mode.Wiring); }
                }
                else
                {
                    cam.MoveCamera((float) deltaTime, allowMove: !CircuitBox.IsCircuitBoxSelected(dummyCharacter), allowZoom: GUI.MouseOn == null);
                }
            }
            else
            {
                cam.MoveCamera((float) deltaTime, allowMove: false, allowZoom: GUI.MouseOn == null);
            }

            if (PlayerInput.MidButtonHeld() && !CircuitBox.IsCircuitBoxSelected(dummyCharacter))
            {
                Vector2 moveSpeed = PlayerInput.MouseSpeed * (float)deltaTime * 60.0f / cam.Zoom;
                moveSpeed.X = -moveSpeed.X;
                cam.Position += moveSpeed;
                // break out of trying to focus
                camTargetFocus = Vector2.Zero;
            }

            if (PlayerInput.KeyHit(Keys.Escape) && dummyCharacter != null)
            {
                CloseItem();
            }

            if (lightingEnabled)
            {
                //turn off lights that are inside containers
                foreach (Item item in Item.ItemList)
                {
                    foreach (LightComponent lightComponent in item.GetComponents<LightComponent>())
                    {
                        lightComponent.Light.Color = 
                            (item.body == null || item.body.Enabled || item.ParentInventory is ItemInventory { Container.HideItems: false }) &&
                            /*the light is only visible when worn -> can't be visible in the editor*/
                            lightComponent.Parent is not Wearable ?
                                lightComponent.LightColor :
                                Color.Transparent;
                        lightComponent.Light.LightSpriteEffect = lightComponent.Item.SpriteEffects;
                    }
                }
                GameMain.LightManager?.Update((float)deltaTime);
            }

            if (dummyCharacter != null && Entity.FindEntityByID(dummyCharacter.ID) == dummyCharacter)
            {
                if (WiringMode)
                {
                    MapEntity.ClearHighlightedEntities();

                    if (dummyCharacter.SelectedItem == null)
                    {
                        List<Wire> wires = new List<Wire>();
                        foreach (Item item in Item.ItemList)
                        {
                            var wire = item.GetComponent<Wire>();
                            if (wire != null) wires.Add(wire);
                        }
                        Wire.UpdateEditing(wires);
                    }
                }

                if (!WiringMode)
                {
                    // Move all of our slots on top center of the entity list
                    // We use the slots to open item inventories and we want the position of them to be consisent
                    dummyCharacter.Inventory.visualSlots.ForEach(slot =>
                    {
                        slot.Rect.Y = EntityMenu.Rect.Top;
                        slot.Rect.X = EntityMenu.Rect.X + (EntityMenu.Rect.Width / 2) - (slot.Rect.Width /2);
                    });
                }

                if (dummyCharacter.SelectedItem == null ||
                    dummyCharacter.SelectedItem.GetComponent<Pickable>() != null)
                {
                    if (WiringMode && PlayerInput.IsShiftDown())
                    {
                        Wire equippedWire = Character.Controlled?.HeldItems.FirstOrDefault(i => i.GetComponent<Wire>() != null)?.GetComponent<Wire>();
                        if (equippedWire != null && equippedWire.GetNodes().Count > 0)
                        {
                            Vector2 lastNode = equippedWire.GetNodes().Last();
                            if (equippedWire.Item.Submarine != null)
                            {
                                lastNode += equippedWire.Item.Submarine.HiddenSubPosition + equippedWire.Item.Submarine.Position;
                            }

                            var (cursorX, cursorY) = dummyCharacter.CursorPosition;

                            bool isHorizontal = Math.Abs(cursorX - lastNode.X) < Math.Abs(cursorY - lastNode.Y);

                            float roundedY = MathUtils.Round(cursorY, Submarine.GridSize.Y / 2.0f);
                            float roundedX = MathUtils.Round(cursorX, Submarine.GridSize.X / 2.0f);

                            dummyCharacter.CursorPosition = isHorizontal
                                ? new Vector2(lastNode.X, roundedY)
                                : new Vector2(roundedX, lastNode.Y);
                        }
                    }

                    // Keep teleporting the dummy character to the opened item to make it look like the container didn't go anywhere
                    if (OpenedItem != null)
                    {
                        TeleportDummyCharacter(oldItemPosition);
                    }

                    if (WiringMode && dummyCharacter?.SelectedItem == null)
                    {
                        TeleportDummyCharacter(FarseerPhysics.ConvertUnits.ToSimUnits(dummyCharacter.CursorPosition));
                    }
                }

                if (WiringMode)
                {
                    dummyCharacter.ControlLocalPlayer((float)deltaTime, cam, false);
                    dummyCharacter.Control((float)deltaTime, cam);
                }

                cam.TargetPos = Vector2.Zero;
                dummyCharacter.Submarine = MainSub;
            }

            if (dummyCharacter?.SelectedItem != null)
            {
                // Deposit item from our "infinite stack" into inventory slots
                TryDragItemsToItem(dummyCharacter.SelectedItem);                
                foreach (Item linkedItem in dummyCharacter.SelectedItem.linkedTo.OfType<Item>())
                {
                    TryDragItemsToItem(linkedItem);                    
                }
            }

            void TryDragItemsToItem(Item item)
            {
                foreach (ItemContainer ic in item.GetComponents<ItemContainer>())
                {
                    if (ic.Inventory?.visualSlots != null)
                    {
                        TryDragItemsToInventory(ic.Inventory);
                    }                   
                }
            }

            void TryDragItemsToInventory(Inventory inv)
            {
                if (PlayerInput.IsCtrlDown()) { return; }

                var draggingMouse = MouseDragStart != Vector2.Zero && Vector2.Distance(PlayerInput.MousePosition, MouseDragStart) >= GUI.Scale * 20;

                // So we don't accidentally drag inventory items while doing this
                if (DraggedItemPrefab != null) { Inventory.DraggingItems.Clear(); }

                switch (DraggedItemPrefab)
                {
                    // regular item prefabs
                    case ItemPrefab itemPrefab when PlayerInput.PrimaryMouseButtonClicked() || draggingMouse:
                        {
                            bool spawnedItem = false;
                            for (var i = 0; i < inv.Capacity; i++)
                            {
                                var slot = inv.visualSlots[i];
                                var itemContainer = inv.GetItemAt(i)?.GetComponent<ItemContainer>();

                                // check if the slot is empty or if we can place the item into a container, for example an oxygen tank into a diving suit
                                if (Inventory.IsMouseOnSlot(slot))
                                {
                                    var newItem = new Item(itemPrefab, Vector2.Zero, MainSub);

                                    if (inv.CanBePutInSlot(itemPrefab, i, condition: null))
                                    {
                                        bool placedItem = inv.TryPutItem(newItem, i, false, true, dummyCharacter);
                                        spawnedItem |= placedItem;

                                        if (!placedItem)
                                        {
                                            newItem.Remove();
                                        }
                                    }
                                    else if (itemContainer != null && itemContainer.Inventory.CanProbablyBePut(itemPrefab))
                                    {
                                        bool placedItem = itemContainer.Inventory.TryPutItem(newItem, dummyCharacter);
                                        spawnedItem |= placedItem;

                                        // try to place the item into the inventory of the item we are hovering over
                                        if (!placedItem)
                                        {
                                            newItem.Remove();
                                        }
                                        else
                                        {
                                            slot.ShowBorderHighlight(GUIStyle.Green, 0.1f, 0.4f);
                                        }
                                    }
                                    else
                                    {
                                        newItem.Remove();
                                        slot.ShowBorderHighlight(GUIStyle.Red, 0.1f, 0.4f);
                                    }

                                    if (!newItem.Removed)
                                    {
                                        BulkItemBufferInUse = ItemAddMutex;
                                        BulkItemBuffer.Add(new AddOrDeleteCommand(new List<MapEntity> { newItem }, false));
                                    }

                                    if (!draggingMouse)
                                    {
                                        SoundPlayer.PlayUISound(spawnedItem ? GUISoundType.PickItem : GUISoundType.PickItemFail);
                                    }
                                }
                            }
                            break;
                        }
                    // item assemblies
                    case ItemAssemblyPrefab assemblyPrefab when PlayerInput.PrimaryMouseButtonClicked():
                        {
                            bool spawnedItems = false;
                            for (var i = 0; i < inv.visualSlots.Length; i++)
                            {
                                var slot = inv.visualSlots[i];
                                var item = inv?.GetItemAt(i);
                                var itemContainer = item?.GetComponent<ItemContainer>();
                                if (item == null && Inventory.IsMouseOnSlot(slot))
                                {
                                    // load the items
                                    var itemInstance = LoadItemAssemblyInventorySafe(assemblyPrefab);

                                    // counter for items that failed so we so we known that slot remained empty
                                    var failedCount = 0;

                                    for (var j = 0; j < itemInstance.Count; j++)
                                    {
                                        var newItem = itemInstance[j];
                                        var newSpot = i + j - failedCount;

                                        // try to find a valid slot to put the items
                                        while (inv.visualSlots.Length > newSpot)
                                        {
                                            if (inv.GetItemAt(newSpot) == null) { break; }
                                            newSpot++;
                                        }

                                        // valid slot found
                                        if (inv.visualSlots.Length > newSpot)
                                        {
                                            var placedItem = inv.TryPutItem(newItem, newSpot, false, true, dummyCharacter);
                                            spawnedItems |= placedItem;

                                            if (!placedItem)
                                            {
                                                failedCount++;
                                                // delete the included items too so we don't get a popup asking if we want to keep them
                                                newItem?.OwnInventory?.DeleteAllItems();
                                                newItem.Remove();
                                            }
                                        }
                                        else
                                        {
                                            var placedItem = inv.TryPutItem(newItem, dummyCharacter);
                                            spawnedItems |= placedItem;

                                            // if our while loop didn't find a valid slot then let the inventory decide where to put it as a last resort
                                            if (!placedItem)
                                            {
                                                // delete the included items too so we don't get a popup asking if we want to keep them
                                                newItem?.OwnInventory?.DeleteAllItems();
                                                newItem.Remove();
                                            }
                                        }
                                    }

                                    List<MapEntity> placedEntities = itemInstance.Where(it => !it.Removed).Cast<MapEntity>().ToList();
                                    if (placedEntities.Any())
                                    {
                                        BulkItemBufferInUse = ItemAddMutex;
                                        BulkItemBuffer.Add(new AddOrDeleteCommand(placedEntities, false));
                                    }
                                }
                            }

                            SoundPlayer.PlayUISound(spawnedItems ? GUISoundType.PickItem : GUISoundType.PickItemFail);
                            break;
                        }
                }
            }

            if (PlayerInput.PrimaryMouseButtonReleased() && BulkItemBufferInUse != null)
            {
                CommitBulkItemBuffer();
            }

            if (SerializableEntityEditor.PropertyChangesActive && (SerializableEntityEditor.NextCommandPush < DateTime.Now || MapEntity.EditingHUD == null))
            {
                SerializableEntityEditor.CommitCommandBuffer();
            }

            // Update our mouse dragging state so we can easily slide thru slots while holding the mouse button down to place lots of items
            if (PlayerInput.PrimaryMouseButtonHeld())
            {
                if (MouseDragStart == Vector2.Zero)
                {
                    MouseDragStart = PlayerInput.MousePosition;
                }
            }
            else
            {
                MouseDragStart = Vector2.Zero;
            }

            if ((GUI.MouseOn == null || !GUI.MouseOn.IsChildOf(TopPanel))
                && dummyCharacter?.SelectedItem == null && !WiringMode
                && (GUI.MouseOn == null || MapEntity.SelectedAny || MapEntity.SelectionPos != Vector2.Zero))
            {
                if (layerList is { Visible: true } && GUI.KeyboardDispatcher.Subscriber == layerList)
                {
                    GUI.KeyboardDispatcher.Subscriber = null;
                }

                MapEntity.UpdateSelecting(cam);
            }

            if (!PlayerInput.PrimaryMouseButtonHeld())
            {
                MeasurePositionStart = Vector2.Zero;
            }

            if (PlayerInput.KeyDown(Keys.LeftAlt) || PlayerInput.KeyDown(Keys.RightAlt))
            {
                if (PlayerInput.PrimaryMouseButtonDown())
                {
                    MeasurePositionStart = cam.ScreenToWorld(PlayerInput.MousePosition);
                }
            }

            if (!WiringMode)
            {
                bool shouldCloseHud = dummyCharacter?.SelectedItem != null && HUD.CloseHUD(dummyCharacter.SelectedItem.Rect) && DraggedItemPrefab == null;

                if (MapEntityPrefab.Selected != null)
                {
                    MapEntityPrefab.Selected.UpdatePlacing(cam);
                }
                else
                {
                    if (PlayerInput.SecondaryMouseButtonClicked() && !shouldCloseHud)
                    {
                        if (GUI.IsMouseOn(entityFilterBox))
                        {
                            ClearFilter();
                        }
                        else
                        {
                            if (dummyCharacter?.SelectedItem == null)
                            {
                                CreateContextMenu();
                            }
                            DraggedItemPrefab = null;
                        }
                    }

                    if (shouldCloseHud)
                    {
                        CloseItem();
                    }
                }
                MapEntity.UpdateEditor(cam, (float)deltaTime);
            }

            entityMenuOpenState = entityMenuOpen && !WiringMode ?
                (float)Math.Min(entityMenuOpenState + deltaTime * 5.0f, 1.0f) :
                (float)Math.Max(entityMenuOpenState - deltaTime * 5.0f, 0.0f);

            EntityMenu.RectTransform.ScreenSpaceOffset = Vector2.Lerp(new Vector2(0.0f, EntityMenu.Rect.Height - 10), Vector2.Zero, entityMenuOpenState).ToPoint();

            if (PlayerInput.PrimaryMouseButtonClicked() && !GUI.IsMouseOn(entityFilterBox))
            {
                entityFilterBox.Deselect();
            }

            if (loadFrame != null)
            {
                if (PlayerInput.SecondaryMouseButtonClicked())
                {
                    loadFrame = null;
                }
            }
            else if (saveFrame != null)
            {
                if (PlayerInput.SecondaryMouseButtonClicked())
                {
                    saveFrame = null;
                }
            }

            if (dummyCharacter != null)
            {
                dummyCharacter.AnimController.FindHull(dummyCharacter.CursorWorldPosition, setSubmarine: false);

                foreach (Item item in dummyCharacter.Inventory.AllItems)
                {
                    item.SetTransform(dummyCharacter.SimPosition, 0.0f);
                    item.UpdateTransform();
                    if (item.body != null)
                    {
                        item.SetTransform(item.body.SimPosition, 0.0f);
                    }

                    //wires need to be updated for the last node to follow the player during rewiring
                    Wire wire = item.GetComponent<Wire>();
                    wire?.Update((float)deltaTime, cam);
                }

                if (dummyCharacter.SelectedItem != null)
                {
                    if (MapEntity.SelectedList.Contains(dummyCharacter.SelectedItem) || WiringMode)
                    {
                        dummyCharacter.SelectedItem?.UpdateHUD(cam, dummyCharacter, (float)deltaTime);
                    }
                    else
                    {
                        // We somehow managed to unfocus the item, close it so our framerate doesn't go to 5 because the
                        // UpdateHUD() method keeps re-creating the editing HUD
                        CloseItem();
                    }
                }
                else if (MapEntity.SelectedList.Count == 1 && WiringMode && MapEntity.SelectedList.FirstOrDefault() is Item item)
                {
                    item.UpdateHUD(cam, dummyCharacter, (float)deltaTime);
                }

                CharacterHUD.Update((float)deltaTime, dummyCharacter, cam);
            }
        }

        /// <summary>
        /// This is called when the game should draw itself.
        /// </summary>
        public override void Draw(double deltaTime, GraphicsDevice graphics, SpriteBatch spriteBatch)
        {
            cam.UpdateTransform();
            if (lightingEnabled)
            {
                GameMain.LightManager.RenderLightMap(graphics, spriteBatch, cam);
            }

            foreach (Submarine sub in Submarine.Loaded)
            {
                sub.UpdateTransform();
            }

            graphics.Clear(BackgroundColor);

            ImageManager.Draw(spriteBatch, cam);

            spriteBatch.Begin(SpriteSortMode.BackToFront, BlendState.NonPremultiplied, transformMatrix: cam.Transform);

            if (GameMain.DebugDraw)
            {
                GUI.DrawLine(spriteBatch, new Vector2(MainSub.HiddenSubPosition.X, -cam.WorldView.Y), new Vector2(MainSub.HiddenSubPosition.X, -(cam.WorldView.Y - cam.WorldView.Height)), Color.White * 0.5f, 1.0f, (int)(2.0f / cam.Zoom));
                GUI.DrawLine(spriteBatch, new Vector2(cam.WorldView.X, -MainSub.HiddenSubPosition.Y), new Vector2(cam.WorldView.Right, -MainSub.HiddenSubPosition.Y), Color.White * 0.5f, 1.0f, (int)(2.0f / cam.Zoom));
            }
            Submarine.DrawBack(spriteBatch, true, e =>
                e is Structure s &&
                !IsSubcategoryHidden(e.Prefab?.Subcategory) &&
                (e.SpriteDepth >= 0.9f || s.Prefab.BackgroundSprite != null));
            Submarine.DrawPaintedColors(spriteBatch, true);
            spriteBatch.End();

            spriteBatch.Begin(SpriteSortMode.BackToFront, BlendState.NonPremultiplied, transformMatrix: cam.Transform);

            // When we "open" a wearable item with inventory it won't get rendered because the dummy character is invisible
            // So we are drawing a clone of it on the same position
            if (OpenedItem?.GetComponent<Wearable>() != null)
            {
                OpenedItem.Sprite.Draw(spriteBatch, new Vector2(OpenedItem.DrawPosition.X, -(OpenedItem.DrawPosition.Y)),
                                       scale: OpenedItem.Scale, color: OpenedItem.SpriteColor, depth: OpenedItem.SpriteDepth);
                GUI.DrawRectangle(spriteBatch,
                                  new Vector2(OpenedItem.WorldRect.X, -OpenedItem.WorldRect.Y),
                                  new Vector2(OpenedItem.Rect.Width, OpenedItem.Rect.Height),
                                  Color.White, false, 0, (int)Math.Max(2.0f / cam.Zoom, 1.0f));
            }

            Submarine.DrawBack(spriteBatch, true, e =>
                (!(e is Structure) || e.SpriteDepth < 0.9f) &&
                !IsSubcategoryHidden(e.Prefab?.Subcategory));
            spriteBatch.End();

            spriteBatch.Begin(SpriteSortMode.BackToFront, BlendState.NonPremultiplied, transformMatrix: cam.Transform);
            Submarine.DrawDamageable(spriteBatch, null, editing: true, e => !IsSubcategoryHidden(e.Prefab?.Subcategory));
            spriteBatch.End();

            spriteBatch.Begin(SpriteSortMode.BackToFront, BlendState.NonPremultiplied, transformMatrix: cam.Transform);
            Submarine.DrawFront(spriteBatch, editing: true, e => !IsSubcategoryHidden(e.Prefab?.Subcategory));
            if (!WiringMode)
            {
                MapEntityPrefab.Selected?.DrawPlacing(spriteBatch, cam);
                MapEntity.DrawSelecting(spriteBatch, cam);
            }
            if (dummyCharacter != null && WiringMode)
            {
                foreach (Item heldItem in dummyCharacter.HeldItems)
                {
                    heldItem.Draw(spriteBatch, editing: false, back: true);
                }
            }

            DrawGrid(spriteBatch);
            spriteBatch.End();

            ImageManager.DrawEditing(spriteBatch, cam);

            if (GameMain.LightManager.LightingEnabled && lightingEnabled)
            {
                spriteBatch.Begin(SpriteSortMode.Deferred, Lights.CustomBlendStates.Multiplicative, null, DepthStencilState.None);
                spriteBatch.Draw(GameMain.LightManager.LightMap, new Rectangle(0, 0, GameMain.GraphicsWidth, GameMain.GraphicsHeight), Color.White);
                spriteBatch.End();
            }

            if (GameMain.LightManager.DebugLos)
            {
                GameMain.LightManager.DebugDrawLos(spriteBatch, cam);
            }

            //-------------------- HUD -----------------------------

            spriteBatch.Begin(SpriteSortMode.Deferred, samplerState: GUI.SamplerState);

            if (MainSub != null && cam.Zoom < 5f)
            {
                Vector2 position = MainSub.SubBody != null ? MainSub.WorldPosition : MainSub.HiddenSubPosition;

                GUI.DrawIndicator(
                    spriteBatch, position, cam,
                    cam.WorldView.Width,
                    GUIStyle.SubmarineLocationIcon.Value.Sprite, Color.LightBlue * 0.5f);
            }

            var notificationIcon = GUIStyle.GetComponentStyle("GUINotificationButton");
            var tooltipStyle = GUIStyle.GetComponentStyle("GUIToolTip");
            foreach (Gap gap in Gap.GapList)
            {
                if (gap.linkedTo.Count == 2 && gap.linkedTo[0] == gap.linkedTo[1])
                {
                    Vector2 screenPos = Cam.WorldToScreen(gap.WorldPosition);
                    Rectangle rect = new Rectangle(screenPos.ToPoint() - new Point(20), new Point(40));
                    tooltipStyle.Sprites[GUIComponent.ComponentState.None][0].Draw(spriteBatch, rect, Color.White);
                    notificationIcon.Sprites[GUIComponent.ComponentState.None][0].Draw(spriteBatch, rect, GUIStyle.Orange);
                    if (Vector2.Distance(PlayerInput.MousePosition, screenPos) < 30 * Cam.Zoom)
                    {
                        GUIComponent.DrawToolTip(spriteBatch, TextManager.Get("gapinsidehullwarning"), new Rectangle(screenPos.ToPoint(), new Point(10)));
                    }
                }
            }

            if (DrawCharacterInventory)
            {
                dummyCharacter.DrawHUD(spriteBatch, cam, false);
                wiringToolPanel.DrawManually(spriteBatch);                
            }
            MapEntity.DrawEditor(spriteBatch, cam);

            GUI.Draw(Cam, spriteBatch);

            if (MeasurePositionStart != Vector2.Zero)
            {
                Vector2 startPos = MeasurePositionStart;
                Vector2 mouseWorldPos = cam.ScreenToWorld(PlayerInput.MousePosition);
                if (PlayerInput.IsShiftDown())
                {
                    startPos = RoundToGrid(startPos);
                    mouseWorldPos = RoundToGrid(mouseWorldPos);

                    static Vector2 RoundToGrid(Vector2 position)
                    {
                        position.X = (float) Math.Round(position.X / Submarine.GridSize.X) * Submarine.GridSize.X;
                        position.Y = (float) Math.Round(position.Y / Submarine.GridSize.Y) * Submarine.GridSize.Y;
                        return position;
                    }
                }

                GUI.DrawLine(spriteBatch, cam.WorldToScreen(startPos), cam.WorldToScreen(mouseWorldPos), GUIStyle.Green, width: 4);

                decimal realWorldDistance = decimal.Round((decimal) (Vector2.Distance(startPos, mouseWorldPos) * Physics.DisplayToRealWorldRatio), 2);

                Vector2 offset = new Vector2(GUI.IntScale(24));
                GUI.DrawString(spriteBatch, PlayerInput.MousePosition + offset, $"{realWorldDistance} m", GUIStyle.TextColorNormal, font: GUIStyle.Font, backgroundColor: Color.Black, backgroundPadding: 4);
            }

            spriteBatch.End();
        }

        private void CreateImage(int width, int height, System.IO.Stream stream)
        {
            MapEntity.SelectedList.Clear();
            MapEntity.ClearHighlightedEntities();

            var prevScissorRect = GameMain.Instance.GraphicsDevice.ScissorRectangle;

            Rectangle subDimensions = Submarine.MainSub.CalculateDimensions(onlyHulls: false);
            Vector2 viewPos = subDimensions.Center.ToVector2();
            float scale = Math.Min(width / (float)subDimensions.Width, height / (float)subDimensions.Height);

            var viewMatrix = Matrix.CreateTranslation(new Vector3(width / 2.0f, height / 2.0f, 0));
            var transform = Matrix.CreateTranslation(
                new Vector3(-viewPos.X, viewPos.Y, 0)) *
                Matrix.CreateScale(new Vector3(scale, scale, 1)) *
                viewMatrix;

            using (RenderTarget2D rt = new RenderTarget2D(
                 GameMain.Instance.GraphicsDevice,
                 width, height, false, SurfaceFormat.Color, DepthFormat.None))
            using (SpriteBatch spriteBatch = new SpriteBatch(GameMain.Instance.GraphicsDevice))
            {
                GameMain.Instance.GraphicsDevice.SetRenderTarget(rt);
                GameMain.Instance.GraphicsDevice.Clear(new Color(8, 13, 19));

                spriteBatch.Begin(SpriteSortMode.BackToFront, BlendState.NonPremultiplied, null, null, null, null, transform);
                Submarine.Draw(spriteBatch);
                Submarine.DrawFront(spriteBatch);
                Submarine.DrawDamageable(spriteBatch, null);
                spriteBatch.End();


                GameMain.Instance.GraphicsDevice.SetRenderTarget(null);
                rt.SaveAsPng(stream, width, height);
            }

            //for some reason setting the rendertarget changes the size of the viewport
            //but it doesn't change back to default when setting it back to null
            GameMain.Instance.ResetViewPort();
        }

        private static readonly Color gridBaseColor = Color.White * 0.1f;

        private void DrawGrid(SpriteBatch spriteBatch)
        {
            // don't render at high zoom levels because it would just turn the screen white
            if (!ShouldDrawGrid) { return; }

            var (gridX, gridY) = Submarine.GridSize;
            DrawGrid(spriteBatch, cam, gridX, gridY, zoomTreshold: true);
        }

        public static void DrawGrid(SpriteBatch spriteBatch, Camera cam, float sizeX, float sizeY, bool zoomTreshold)
        {
            if (zoomTreshold && cam.Zoom < 0.5f) { return; }
            int scale = Math.Max(1, GUI.IntScale(1));
            float zoom = cam.Zoom / 2f; // Don't ask
            float lineThickness = Math.Max(1, scale / zoom);

            Color gridColor = gridBaseColor;
            if (zoomTreshold && cam.Zoom < 1.0f)
            {
                // fade the grid when zooming out
                gridColor *= Math.Max(0, (cam.Zoom - 0.5f) * 2f);
            }

            Rectangle camRect = cam.WorldView;

            for (float x = snapX(camRect.X); x < snapX(camRect.X + camRect.Width) + sizeX; x += sizeX)
            {
                spriteBatch.DrawLine(new Vector2(x, -camRect.Y), new Vector2(x, -(camRect.Y - camRect.Height)), gridColor, thickness: lineThickness);
            }

            for (float y = snapY(camRect.Y); y >= snapY(camRect.Y - camRect.Height) - sizeY; y -= sizeY)
            {
                spriteBatch.DrawLine(new Vector2(camRect.X, -y), new Vector2(camRect.Right, -y), gridColor, thickness: lineThickness);
            }

            float snapX(int x) => (float) Math.Floor(x / sizeX) * sizeX;
            float snapY(int y) => (float) Math.Ceiling(y / sizeY) * sizeY;
        }

        public static void DrawOutOfBoundsArea(SpriteBatch spriteBatch, Camera cam, float playableAreaSize, Color color)
        {
            Rectangle camRect = cam.WorldView;

            RectangleF playableArea = new RectangleF(
                -playableAreaSize / 2f,
                -playableAreaSize / 2f,
                playableAreaSize,
                playableAreaSize
            );

            RectangleF topRect = new(
                camRect.Left,
                -camRect.Top,
                camRect.Width,
                playableArea.Top + camRect.Top
            );

            // idk why camRect.Bottom doesn't work here
            float camRectBottom = -camRect.Top + camRect.Height;

            RectangleF bottomRect = new(
                camRect.Left,
                playableArea.Bottom,
                camRect.Width,
                camRectBottom + playableArea.Bottom
            );

            RectangleF rightRect = new(
                playableArea.Right,
                playableArea.Top,
                camRect.Right - playableArea.Right,
                playableArea.Height
            );

            RectangleF leftRect = new(
                playableArea.Left,
                playableArea.Top,
                camRect.Left - playableArea.Left,
                playableArea.Height
            );

            GUI.DrawFilledRectangle(spriteBatch, topRect, color);
            GUI.DrawFilledRectangle(spriteBatch, leftRect, color);
            GUI.DrawFilledRectangle(spriteBatch, rightRect, color);
            GUI.DrawFilledRectangle(spriteBatch, bottomRect, color);
        }

        public void SaveScreenShot(int width, int height, string filePath)
        {
            System.IO.Stream stream = File.OpenWrite(filePath);
            CreateImage(width, height, stream);
            stream.Dispose();
        }

        public bool IsSubcategoryHidden(string subcategory)
        {
            if (string.IsNullOrEmpty(subcategory) || !hiddenSubCategories.ContainsKey(subcategory))
            {
                return false;
            }
            return hiddenSubCategories[subcategory];
        }

        public static bool IsSubEditor() => Screen.Selected is SubEditorScreen && !Submarine.Unloading;
        public static bool IsWiringMode() => Screen.Selected == GameMain.SubEditorScreen && GameMain.SubEditorScreen.WiringMode && !Submarine.Unloading;

        public static bool IsLayerVisible(MapEntity entity)
        {
            if (!IsSubEditor() || string.IsNullOrWhiteSpace(entity.Layer)) { return true; }

            if (!Layers.TryGetValue(entity.Layer, out LayerData data))
            {
                Layers.TryAdd(entity.Layer, new LayerData(!entity.IsLayerHidden));
                return true;
            }

            return data.IsVisible;
        }

        public static bool IsLayerLinked(MapEntity entity)
        {
            if (!IsSubEditor() || string.IsNullOrWhiteSpace(entity.Layer)) { return false; }

            if (!Layers.TryGetValue(entity.Layer, out LayerData data))
            {
                Layers.TryAdd(entity.Layer, new LayerData(!entity.IsLayerHidden));
                return true;
            }

            return data.IsGrouped;
        }

        public static ImmutableHashSet<MapEntity> GetEntitiesInSameLayer(MapEntity entity)
        {
            if (string.IsNullOrWhiteSpace(entity.Layer)) { return ImmutableHashSet<MapEntity>.Empty; }
            return MapEntity.MapEntityList.Where(me => me.Layer == entity.Layer).ToImmutableHashSet();
        }
    }
}

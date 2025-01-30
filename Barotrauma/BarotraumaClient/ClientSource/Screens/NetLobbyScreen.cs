using Barotrauma.Extensions;
using Barotrauma.Networking;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Linq;

namespace Barotrauma
{
    partial class NetLobbyScreen : Screen
    {
        private GUIListBox chatBox;
        private GUILayoutGroup chatRow;
        private GUIButton serverLogReverseButton;
        private GUIListBox serverLogBox, serverLogFilterTicks;

        private GUIComponent jobVariantTooltip;

        private GUIComponent playStyleIconContainer;
        
        private GUIDropDown chatSelector;
        public static bool TeamChatSelected = false;
        
        private GUITextBox chatInput;
        private GUITextBox serverLogFilter;
        public GUITextBox ChatInput
        {
            get
            {
                return chatInput;
            }
        }

        private GUIImage micIcon;

        private GUIScrollBar levelDifficultySlider;

        private readonly List<GUIComponent> traitorElements = new List<GUIComponent>();
        private GUIScrollBar traitorProbabilitySlider;
        private GUILayoutGroup traitorDangerGroup;

        private GUIDropDown outpostDropdown;
        private bool outpostDropdownUpToDate;

        public GUIFrame MissionTypeFrame { get; private set; }
        public GUIFrame CampaignSetupFrame { get; private set; }
        public GUIFrame CampaignFrame { get; private set; }

        public GUIButton QuitCampaignButton { get; private set; }

        private GUITickBox[] missionTypeTickBoxes;
        private GUIListBox missionTypeList;
        
        public GUITextBox LevelSeedBox { get; private set; }

        private GUIButton joinOnGoingRoundButton;
        /// <summary>
        /// Contains the elements that control starting the round (start button, spectate button, "ready to start" tickbox)
        /// </summary>
        private GUILayoutGroup roundControlsHolder;

        public GUIButton SettingsButton { get; private set; }
        public GUIButton ServerMessageButton { get; private set; }
        public static GUIButton JobInfoFrame { get; set; }

        private GUITickBox spectateBox;
        public bool Spectating => spectateBox is { Selected: true, Visible: true };

        public bool PermadeathMode => GameMain.Client?.ServerSettings?.RespawnMode == RespawnMode.Permadeath;
        public bool PermanentlyDead => campaignCharacterInfo?.PermanentlyDead ?? false;

        private GUILayoutGroup playerInfoContent;
        private GUIComponent changesPendingText;
        private bool createPendingChangesText = true;
        public GUIButton PlayerFrame { get; private set; }

        public GUIButton SubVisibilityButton { get; private set; }

        private GUITextBox subSearchBox;

        private GUIComponent subPreviewContainer;

        private GUITickBox autoRestartBox;
        private GUITextBlock autoRestartText;

        private GUITickBox shuttleTickBox;

        private Sprite backgroundSprite;

        private GUIButton jobPreferencesButton;
        private GUIButton appearanceButton;

        private GUIFrame characterInfoFrame;
        private GUIFrame appearanceFrame;

        private GUISelectionCarousel<RespawnMode> respawnModeSelection;
        private GUITextBlock respawnModeLabel;
        private GUIComponent respawnIntervalElement;
        
        private readonly List<GUIComponent> midRoundRespawnSettings = new List<GUIComponent>();
        private readonly List<GUIComponent> permadeathEnabledRespawnSettings = new List<GUIComponent>();
        private readonly List<GUIComponent> permadeathDisabledRespawnSettings = new List<GUIComponent>();
        private readonly List<GUIComponent> ironmanDisabledRespawnSettings = new List<GUIComponent>();
        private readonly List<GUIComponent> campaignDisabledElements = new List<GUIComponent>();
        private readonly List<GUIComponent> campaignHiddenElements = new List<GUIComponent>();
        private readonly List<GUIComponent> pvpOnlyElements = new();
        private readonly List<GUIComponent> disembarkPerkSettings = new();
        private readonly List<GUIComponent> respawnSettings = new();

        public CharacterInfo.AppearanceCustomizationMenu CharacterAppearanceCustomizationMenu { get; set; }
        public GUIFrame JobSelectionFrame { get; private set; }

        public GUIFrame JobPreferenceContainer { get; private set; }
        public GUIListBox JobList { get; private set; }

        private Identifier micIconStyle;
        private float micCheckTimer;
        const float MicCheckInterval = 1.0f;

        private float autoRestartTimer;

        //persistent characterinfo provided by the server
        //(character settings cannot be edited when this is set)
        private CharacterInfo campaignCharacterInfo;
        public bool CampaignCharacterDiscarded
        {
            get;
            set;
        }

        /// <summary>
        /// Elements that can only be used by the host or people with server settings management permissions (but are visible to everyone)
        /// </summary>
        private readonly List<GUIComponent> clientDisabledElements = new List<GUIComponent>();

        /// <summary>
        /// Elements that are only visible to the host or people with server settings management permissions
        /// </summary>
        private readonly List<GUIComponent> clientHiddenElements = new List<GUIComponent>();

        private readonly List<GUIComponent> botSettingsElements = new List<GUIComponent>();

        private readonly Dictionary<GUIComponent, string> settingAssignedComponents = new Dictionary<GUIComponent, string>(); 

        public GUIComponent FileTransferFrame { get; private set; }
        public GUITextBlock FileTransferTitle { get; private set; }
        public GUIProgressBar FileTransferProgressBar { get; private set; }
        public GUITextBlock FileTransferProgressText { get; private set; }

        public GUITickBox Favorite { get; private set; }

        public GUILayoutGroup LogButtons { get; private set; }

        /// <summary>
        /// Tab buttons above the chat panel (chat and server log tabs)
        /// </summary>
        private readonly List<GUIButton> chatPanelTabButtons = new List<GUIButton>();

        private GUITextBlock publicOrPrivateText, playstyleText;

        public GUIListBox SubList { get; private set; }
        public GUIDropDown ShuttleList { get; private set; }
        public GUIListBox ModeList { get; private set; }

        private int selectedModeIndex;
        public int SelectedModeIndex
        {
            get { return selectedModeIndex; }
            set
            {
                if (HighlightedModeIndex == selectedModeIndex)
                {
                    ModeList.Select(value);
                }
                selectedModeIndex = value;
            }
        }

        public int HighlightedModeIndex
        {
            get { return ModeList.SelectedIndex; }
            set
            {
                ModeList.Select(value, GUIListBox.Force.Yes);
            }
        }

        //No, this should not be static even though your IDE might say so! There's a server-side version of this which needs to be an instance method.
        public IReadOnlyList<SubmarineInfo> GetSubList()
            => (IReadOnlyList<SubmarineInfo>)GameMain.Client?.ServerSubmarines
               ?? Array.Empty<SubmarineInfo>();

        public GUIListBox PlayerList;
        
        public int Team1Count;
        public int Team2Count;

        public GUITextBox CharacterNameBox { get; private set; }

        public GUIListBox TeamPreferenceListBox { get; private set; }
        private GUITextBlock pvpTeamChoiceTeam1;
        private GUITextBlock pvpTeamChoiceMiddleButton;
        private GUITextBlock pvpTeamChoiceTeam2;

        private CharacterTeamType TeamPreference => SelectedMode == GameModePreset.PvP ? MultiplayerPreferences.Instance.TeamPreference : CharacterTeamType.Team1;

        public GUIButton StartButton { get; private set; }

        public GUITickBox ReadyToStartBox { get; private set; }

        [AllowNull, MaybeNull]
        public SubmarineInfo SelectedSub;

        [AllowNull, MaybeNull]
        public SubmarineInfo SelectedEnemySub;

        public SubmarineInfo SelectedShuttle => ShuttleList.SelectedData as SubmarineInfo;

        public MultiPlayerCampaignSetupUI CampaignSetupUI;

        public bool UsingShuttle
        {
            get { return shuttleTickBox.Selected && !PermadeathMode; }
            set { shuttleTickBox.Selected = value; }
        }

        public GameModePreset SelectedMode
        {
            get { return ModeList.SelectedData as GameModePreset; }
        }

        public IEnumerable<Identifier> MissionTypes
        {
            get
            {
                return missionTypeTickBoxes.Where(t => t.Selected).Select(t => (Identifier)t.UserData);
            }
            set
            {
                bool changed = false;
                foreach (var missionTypeTickBox in missionTypeTickBoxes)
                {
                    bool prevSelected = missionTypeTickBox.Selected;
                    missionTypeTickBox.Selected = value.Contains((Identifier)missionTypeTickBox.UserData);
                    if (prevSelected != missionTypeTickBox.Selected)
                    {
                        changed = true;
                    }
                }
                if (changed)
                {
                    RefreshOutpostDropdown();
                }
            }
        }

        public List<JobVariant> JobPreferences
        {
            get
            {
                // JobList if the server has already assigned the player a job
                // (e.g. the player has a pre-existing campaign character)
                if (JobList?.Content == null)
                {
                    return new List<JobVariant>();
                }

                List<JobVariant> jobPreferences = new List<JobVariant>();
                foreach (GUIComponent child in JobList.Content.Children)
                {
                    if (child.UserData is not JobVariant jobPrefab) { continue; }
                    jobPreferences.Add(jobPrefab);
                }
                return jobPreferences;
            }
        }

        public string LevelSeed
        {
            get
            {
                return levelSeed;
            }
            set
            {
                if (levelSeed == value) { return; }

                levelSeed = value;

                int intSeed = ToolBox.StringToInt(levelSeed);
                backgroundSprite = LocationType.Random(new MTRandom(intSeed), predicate: lt => lt.UsePortraitInRandomLoadingScreens)?.GetPortrait(intSeed);
                LevelSeedBox.Text = levelSeed;
            }
        }

        private const float MainPanelWidth = 0.7f;
        private const float SidePanelWidth = 0.3f;
        /// <summary>
        /// Spacing between different elements in the panels
        /// </summary>
        private const float PanelSpacing = 0.005f;

        /// <summary>
        /// Size of the outer border of the panels (= empty area round the contents of the panel)
        /// </summary>
        private static int PanelBorderSize => GUI.IntScale(20);

        private static Point GetSizeWithoutBorder(GUIComponent parent) => new Point(parent.Rect.Width - PanelBorderSize * 2, parent.Rect.Height - PanelBorderSize * 2);

        public NetLobbyScreen()
        {
            var contentArea = new GUILayoutGroup(new RectTransform(new Vector2(0.95f, 0.95f), Frame.RectTransform, Anchor.Center), isHorizontal: false)
            {
                Stretch = true,
                RelativeSpacing = PanelSpacing
            };

            var horizontalLayout = new GUILayoutGroup(new RectTransform(Vector2.One, contentArea.RectTransform, Anchor.Center), isHorizontal: true)
            {
                Stretch = true,
                RelativeSpacing = PanelSpacing
            };

            var mainPanel = new GUIFrame(new RectTransform(new Vector2(MainPanelWidth, 1.0f), horizontalLayout.RectTransform));

            var mainPanelLayout = new GUILayoutGroup(new RectTransform(new Point(mainPanel.Rect.Width, mainPanel.Rect.Height - PanelBorderSize), mainPanel.RectTransform, Anchor.TopCenter), childAnchor: Anchor.TopCenter)
            {
                Stretch = true,
                //more spacing to more clearly separate the top and bottom
                RelativeSpacing = PanelSpacing * 4
            };

            GUILayoutGroup serverInfoHolder = new GUILayoutGroup(new RectTransform(new Vector2(1.0f, 0.15f), mainPanelLayout.RectTransform), isHorizontal: true)
            {
                Stretch = true,
                RelativeSpacing = 0.025f
            };
            CreateServerInfoContents(serverInfoHolder);

            var mainPanelTopLayout = new GUILayoutGroup(new RectTransform(new Point(mainPanel.Rect.Width - PanelBorderSize * 2, mainPanel.Rect.Height / 2), mainPanelLayout.RectTransform, Anchor.Center), isHorizontal: true)
            {
                Stretch = true,
                RelativeSpacing = PanelSpacing
            };

            var mainPanelBottomLayout = new GUILayoutGroup(new RectTransform(new Point(mainPanel.Rect.Width - PanelBorderSize * 2, mainPanel.Rect.Height / 2), mainPanelLayout.RectTransform, Anchor.Center), isHorizontal: true)
            {
                Stretch = true,
                RelativeSpacing = PanelSpacing
            };

            //--------------------------------------------------------------------------------------------------------------------------------
            //top panel (game mode, submarine)
            //--------------------------------------------------------------------------------------------------------------------------------

            CreateGameModeDropdown(mainPanelTopLayout);
            CreateSubmarineListPanel(mainPanelTopLayout);
            CreateSubmarineInfoPanel(mainPanelTopLayout);

            //--------------------------------------------------------------------------------------------------------------------------------
            //bottom panel (settings) 
            //--------------------------------------------------------------------------------------------------------------------------------

            CreateGameModePanel(mainPanelBottomLayout);
            CreateGameModeSettingsPanel(mainPanelBottomLayout);
            CreateGeneralSettingsPanel(mainPanelBottomLayout);
            mainPanelBottomLayout.Recalculate();

            foreach (var child in mainPanelBottomLayout.GetAllChildren<GUIComponent>())
            {
                if (traitorDangerGroup.Children.Contains(child)) 
                { 
                    //don't touch the colors of the traitor danger indicators, they're intentionally very dim when disabled
                    continue;
                }
                //make the disabled colors slightly less dim (these should be readable, despite being non-interactable)
                child.DisabledColor = new Color(child.Color, child.Color.A / 255.0f * 0.8f);
                if (child is GUITextBlock textBlock)
                {
                    textBlock.DisabledTextColor = new Color(textBlock.TextColor, textBlock.TextColor.A / 255.0f * 0.8f);
                }
            }

            //--------------------------------------------------------------------------------------------------------------------------------
            //right panel (Character customization/Chat)
            //--------------------------------------------------------------------------------------------------------------------------------

            var sidePanel = new GUIFrame(new RectTransform(new Vector2(SidePanelWidth, 1.0f), horizontalLayout.RectTransform));
            GUILayoutGroup sidePanelLayout = new GUILayoutGroup(new RectTransform(GetSizeWithoutBorder(sidePanel),
                sidePanel.RectTransform, Anchor.Center))
            {
                RelativeSpacing = PanelSpacing * 4,
                Stretch = true
            };

            CreateSidePanelContents(sidePanelLayout);

            //--------------------------------------------------------------------------------------------------------------------------------
            // bottom panel (start round, quit, transfers, ready to start...) ------------------------------------------------------------
            //--------------------------------------------------------------------------------------------------------------------------------
            GUILayoutGroup bottomBar = new GUILayoutGroup(new RectTransform(new Vector2(1.0f, 0.05f), contentArea.RectTransform), childAnchor: Anchor.CenterLeft)
            {
                Stretch = true,
                IsHorizontal = true,
                RelativeSpacing = PanelSpacing
            };
            CreateBottomPanelContents(bottomBar);
        }

        private void AssignComponentToServerSetting(GUIComponent component, string settingName)
        {
            settingAssignedComponents[component] = settingName;
        }

        public void AssignComponentsToServerSettings()
        {
            settingAssignedComponents.ForEach(kvp => GameMain.Client.ServerSettings.AssignGUIComponent(kvp.Value, kvp.Key));
        }

        private void CreateServerInfoContents(GUIComponent parent)
        {
            GUIFrame serverInfoFrame = new GUIFrame(new RectTransform(Vector2.One, parent.RectTransform), style: null);
            var serverBanner = new GUICustomComponent(new RectTransform(Vector2.One, serverInfoFrame.RectTransform), DrawServerBanner)
            {
                HideElementsOutsideFrame = true,
                IgnoreLayoutGroups = true
            };

            GUIFrame serverInfoContent = new GUIFrame(new RectTransform(new Vector2(0.98f, 0.9f), serverInfoFrame.RectTransform, Anchor.Center), style: null);

            var serverLabelContainer = new GUILayoutGroup(new RectTransform(new Vector2(0.5f, 0.05f), serverInfoContent.RectTransform), isHorizontal: true) 
            { 
                AbsoluteSpacing = GUI.IntScale(5)
            };

            playstyleText = new GUITextBlock(new RectTransform(new Vector2(0.3f, 1.0f), serverLabelContainer.RectTransform),
                "", font: GUIStyle.SmallFont, textAlignment: Alignment.Center, textColor: Color.White, style: "GUISlopedHeader");
            publicOrPrivateText = new GUITextBlock(new RectTransform(new Vector2(0.3f, 1.0f), serverLabelContainer.RectTransform),
                "", font: GUIStyle.SmallFont, textAlignment: Alignment.Center, textColor: Color.White, style: "GUISlopedHeader");

            var serverNameShadow = new GUITextBlock(new RectTransform(new Vector2(0.2f, 0.3f), serverInfoContent.RectTransform, Anchor.CenterLeft) { AbsoluteOffset = new Point(GUI.IntScale(3)) },
                string.Empty, font: GUIStyle.LargeFont, textColor: Color.Black)
            {
                IgnoreLayoutGroups = true
            };
            var serverName = new GUITextBlock(new RectTransform(new Vector2(0.2f, 0.3f), serverInfoContent.RectTransform, Anchor.CenterLeft),
                string.Empty, font: GUIStyle.LargeFont, textColor: GUIStyle.TextColorBright)
            {
                IgnoreLayoutGroups = true,
                TextGetter = serverNameShadow.TextGetter = () => GameMain.Client?.ServerName
            };

            ServerMessageButton = new GUIButton(new RectTransform(new Vector2(0.2f, 0.15f), serverInfoContent.RectTransform, Anchor.BottomLeft),
                TextManager.Get("workshopitemdescription"), style: "GUIButtonSmall")
            {
                IgnoreLayoutGroups = true,
                OnClicked = (bt, userdata) => 
                {
                    if (GameMain.Client?.ServerSettings is { } serverSettings)
                    {
                        CreateServerMessagePopup(serverSettings.ServerName, serverSettings.ServerMessageText);
                    }
                    return true; 
                }
            };

            playStyleIconContainer = new GUILayoutGroup(new RectTransform(new Vector2(0.5f, 0.4f), serverInfoContent.RectTransform, Anchor.BottomRight), isHorizontal: true, childAnchor: Anchor.BottomRight) 
            { 
                AbsoluteSpacing = GUI.IntScale(5)
            };

            Favorite = new GUITickBox(new RectTransform(new Vector2(0.5f, 0.5f), serverInfoContent.RectTransform, Anchor.TopRight, scaleBasis: ScaleBasis.BothHeight),
                "", null, "GUIServerListFavoriteTickBox")
            {
                IgnoreLayoutGroups = true,
                Selected = false,
                ToolTip = TextManager.Get("addtofavorites"),
                OnSelected = (tickbox) =>
                {
                    if (GameMain.Client == null) { return true; }
                    ServerInfo info = GameMain.Client.CreateServerInfoFromSettings();
                    if (tickbox.Selected)
                    {
                        GameMain.ServerListScreen.AddToFavoriteServers(info);
                    }
                    else
                    {
                        GameMain.ServerListScreen.RemoveFromFavoriteServers(info);
                    }
                    tickbox.ToolTip = TextManager.Get(tickbox.Selected ? "removefromfavorites" : "addtofavorites");
                    return true;
                }
            };

            SettingsButton = new GUIButton(new RectTransform(new Vector2(0.25f, 0.4f), serverInfoContent.RectTransform, Anchor.TopRight),
                TextManager.Get("ServerSettingsButton"), style: "GUIButtonFreeScale");
        }

        private void CreateServerMessagePopup(string serverName, string message)
        {
            if (string.IsNullOrEmpty(message)) { return; }
            var popup = new GUIMessageBox(serverName, string.Empty, minSize: new Point(GUI.IntScale(650), GUI.IntScale(650)));
            //popup.Content.Stretch = true;
            popup.Header.Font = GUIStyle.LargeFont;
            popup.Header.RectTransform.MinSize = new Point(0, (int)popup.Header.TextSize.Y);
            var textListBox = new GUIListBox(new RectTransform(new Vector2(1.0f, 0.7f), popup.Content.RectTransform));
            var text = new GUITextBlock(new RectTransform(new Vector2(1.0f, 0.0f), textListBox.Content.RectTransform), message, wrap: true)
            {
                CanBeFocused = false
            };
            text.RectTransform.MinSize = new Point(0, (int)text.TextSize.Y);
        }

        public void RefreshPlaystyleIcons()
        {
            playStyleIconContainer?.ClearChildren();
            if (GameMain.Client?.ClientPeer?.ServerConnection is not { }  serverConnection || serverConnection.Endpoint == null) { return; }
            var serverInfo = ServerInfo.FromServerEndpoints(serverConnection.Endpoint.ToEnumerable().ToImmutableArray(), GameMain.Client.ServerSettings);

            var playStyleTags = serverInfo.GetPlayStyleTags();
            foreach (var tag in playStyleTags)
            {
                var playStyleIcon = GUIStyle.GetComponentStyle($"PlayStyleIcon.{tag}")
                    ?.GetSprite(GUIComponent.ComponentState.None);
                if (playStyleIcon is null) { continue; }

                new GUIImage(new RectTransform(Vector2.One, playStyleIconContainer.RectTransform, scaleBasis: ScaleBasis.BothHeight),
                    playStyleIcon, scaleToFit: true)
                {
                    ToolTip = TextManager.Get($"servertagdescription.{tag}"),
                    Color = Color.White
                };
            }
        }

        private void CreateGameModeDropdown(GUIComponent parent)
        {
            //------------------------------------------------------------------------------------------------------------------
            //   Gamemode panel
            //------------------------------------------------------------------------------------------------------------------

            GUILayoutGroup gameModeHolder = new GUILayoutGroup(new RectTransform(Vector2.One, parent.RectTransform))
            {
                Stretch = true,
                RelativeSpacing = 0.005f
            };

            var modeLabel = CreateSubHeader("GameMode", gameModeHolder);
            var voteText = new GUITextBlock(new RectTransform(new Vector2(0.5f, 1.0f), modeLabel.RectTransform, Anchor.TopRight),
                TextManager.Get("Votes"), textAlignment: Alignment.CenterRight)
            {
                UserData = "modevotes",
                Visible = false
            };
            ModeList = new GUIListBox(new RectTransform(Vector2.One, gameModeHolder.RectTransform))
            {
                PlaySoundOnSelect = true,
                OnSelected = VotableClicked
            };

            foreach (GameModePreset mode in GameModePreset.List)
            {
                if (mode.IsSinglePlayer) { continue; }

                var modeFrame = new GUIFrame(new RectTransform(new Vector2(1.0f, 0.25f), ModeList.Content.RectTransform), style: null)
                {
                    UserData = mode
                };

                var modeContent = new GUILayoutGroup(new RectTransform(new Vector2(0.76f, 0.9f), modeFrame.RectTransform, Anchor.CenterRight))
                {
                    AbsoluteSpacing = GUI.IntScale(5),
                    Stretch = true
                };

                var modeTitle = new GUITextBlock(new RectTransform(new Vector2(1.0f, 0.0f), modeContent.RectTransform), mode.Name, font: GUIStyle.SubHeadingFont);
                modeTitle.RectTransform.NonScaledSize = new Point(int.MaxValue, (int)modeTitle.TextSize.Y);
                modeTitle.RectTransform.IsFixedSize = true;
                var modeDescription = new GUITextBlock(new RectTransform(new Vector2(1.0f, 0.0f), modeContent.RectTransform), mode.Description, font: GUIStyle.SmallFont, wrap: true);
                //leave some padding for the vote count text
                modeDescription.Padding = new Vector4(modeDescription.Padding.X, modeDescription.Padding.Y, GUI.IntScale(30), modeDescription.Padding.W);
                modeTitle.HoverColor = modeDescription.HoverColor = modeTitle.SelectedColor = modeDescription.SelectedColor = Color.Transparent;
                modeTitle.HoverTextColor = modeDescription.HoverTextColor = modeTitle.TextColor;
                modeTitle.TextColor = modeDescription.TextColor = modeTitle.TextColor * 0.5f;
                modeFrame.OnAddedToGUIUpdateList = (c) =>
                {
                    modeTitle.State = modeDescription.State = c.State;
                };
                modeDescription.RectTransform.SizeChanged += () =>
                {
                    modeDescription.RectTransform.NonScaledSize = new Point(modeDescription.Rect.Width, (int)modeDescription.TextSize.Y);
                    modeFrame.RectTransform.MinSize = new Point(0, (int)(modeContent.Children.Sum(c => c.Rect.Height + modeContent.AbsoluteSpacing) / modeContent.RectTransform.RelativeSize.Y));
                };

                new GUIImage(new RectTransform(new Vector2(0.2f, 0.8f), modeFrame.RectTransform, Anchor.CenterLeft) { RelativeOffset = new Vector2(0.02f, 0.0f) },
                    style: "GameModeIcon." + mode.Identifier, scaleToFit: true);
            }
        }

        private void CreateSubmarineListPanel(GUIComponent parent)
        {
            var submarineListHolder = new GUILayoutGroup(new RectTransform(Vector2.One, parent.RectTransform))
            {
                Stretch = true,
                RelativeSpacing = 0.005f
            };

            var subLabel = CreateSubHeader("Submarine", submarineListHolder);
            SubVisibilityButton
                = new GUIButton(
                        new RectTransform(Vector2.One * 1.2f, subLabel.RectTransform, anchor: Anchor.CenterRight,
                            scaleBasis: ScaleBasis.BothHeight)
                        { AbsoluteOffset = new Point(0, GUI.IntScale(5)) },
                        style: "EyeButton")
                {
                    OnClicked = (button, o) =>
                    {
                        CreateSubmarineVisibilityMenu();
                        return false;
                    }
                };
            clientHiddenElements.Add(SubVisibilityButton);

            var filterContainer = new GUILayoutGroup(new RectTransform(new Vector2(1.0f, 0.05f), submarineListHolder.RectTransform), isHorizontal: true)
            {
                Stretch = true
            };
            var searchTitle = new GUITextBlock(new RectTransform(new Vector2(0.001f, 1.0f), filterContainer.RectTransform), TextManager.Get("serverlog.filter"), textAlignment: Alignment.CenterLeft, font: GUIStyle.Font);
            subSearchBox = new GUITextBox(new RectTransform(Vector2.One, filterContainer.RectTransform, Anchor.CenterRight), font: GUIStyle.Font, createClearButton: true);
            filterContainer.RectTransform.MinSize = subSearchBox.RectTransform.MinSize;
            subSearchBox.OnSelected += (sender, userdata) => { searchTitle.Visible = false; };
            subSearchBox.OnDeselected += (sender, userdata) => { searchTitle.Visible = true; };
            subSearchBox.OnTextChanged += (textBox, text) =>
            {
                UpdateSubVisibility();
                return true;
            };

            SubList = new GUIListBox(new RectTransform(new Vector2(1.0f, 0.93f), submarineListHolder.RectTransform))
            {
                PlaySoundOnSelect = true,
                OnSelected = VotableClicked
            };

            var voteText = new GUITextBlock(new RectTransform(new Vector2(0.5f, 1.0f), subLabel.RectTransform, Anchor.TopRight),
                TextManager.Get("Votes"), textAlignment: Alignment.CenterRight)
            {
                UserData = "subvotes",
                Visible = false,
                CanBeFocused = false
            };
        }

        private void CreateSubmarineInfoPanel(GUIComponent parent)
        {
            var submarineInfoHolder = new GUILayoutGroup(new RectTransform(Vector2.One, parent.RectTransform, Anchor.Center))
            {
                Stretch = true,
                RelativeSpacing = 0.005f
            };
            //submarine preview ------------------------------------------------------------------

            subPreviewContainer = new GUIFrame(new RectTransform(Vector2.One, submarineInfoHolder.RectTransform), style: null);
            subPreviewContainer.RectTransform.SizeChanged += () =>
            {
                if (SelectedSub != null) { CreateSubPreview(SelectedSub); }
            };
        }

        private GUIComponent CreateGameModePanel(GUIComponent parent)
        {
            var gameModeSpecificFrame = new GUIFrame(new RectTransform(Vector2.One, parent.RectTransform), style: null);
            CampaignSetupFrame = new GUIFrame(new RectTransform(Vector2.One, gameModeSpecificFrame.RectTransform), style: null)
            {
                Visible = false
            };
            CampaignFrame = new GUIFrame(new RectTransform(Vector2.One, gameModeSpecificFrame.RectTransform), style: null)
            {
                Visible = false
            };
            GUILayoutGroup campaignContent = new GUILayoutGroup(new RectTransform(new Vector2(0.9f, 0.5f), CampaignFrame.RectTransform, Anchor.Center))
            {
                RelativeSpacing = 0.05f,
                Stretch = true
            };
            new GUITextBlock(new RectTransform(new Vector2(1.0f, 0.3f), campaignContent.RectTransform),
                TextManager.Get("gamemode.multiplayercampaign"), font: GUIStyle.SubHeadingFont, textAlignment: Alignment.Center);

            QuitCampaignButton = new GUIButton(new RectTransform(new Vector2(1.0f, 0.3f), campaignContent.RectTransform),
                TextManager.Get("quitbutton"), textAlignment: Alignment.Center)
            {
                OnClicked = (_, __) =>
                {
                    if (GameMain.Client == null) { return false; }
                    if (GameMain.Client.GameStarted)
                    {
                        GameMain.Client.RequestRoundEnd(save: false);
                    }
                    else
                    {
                        GameMain.Client.RequestRoundEnd(save: false, quitCampaign: true);
                    }
                    return true;
                }
            };

            //mission type ------------------------------------------------------------------
            MissionTypeFrame = new GUIFrame(new RectTransform(Vector2.One, gameModeSpecificFrame.RectTransform), style: null);

            GUILayoutGroup missionHolder = new GUILayoutGroup(new RectTransform(Vector2.One, MissionTypeFrame.RectTransform))
            {
                Stretch = true
            };

            CreateSubHeader("MissionType", missionHolder);
            missionTypeList = new GUIListBox(new RectTransform(Vector2.One, missionHolder.RectTransform))
            {
                OnSelected = (component, obj) =>
                {
                    return false;
                }
            };
            clientDisabledElements.Add(missionTypeList);

            List<Identifier> missionTypes = MissionPrefab.GetAllMultiplayerSelectableMissionTypes().ToList();

            missionTypeTickBoxes = new GUITickBox[missionTypes.Count];
            int index = 0;
            foreach (var missionType in missionTypes.OrderBy(t => TextManager.Get("MissionType." + t.Value).Value))
            {
                GUIFrame frame = new GUIFrame(new RectTransform(new Vector2(1.0f, 0.05f), missionTypeList.Content.RectTransform) { MinSize = new Point(0, GUI.IntScale(30)) }, style: null)
                {
                    UserData = missionType,
                };

                missionTypeTickBoxes[index] = new GUITickBox(new RectTransform(Vector2.One, frame.RectTransform),
                    TextManager.Get("MissionType." + missionType.ToString()))
                {
                    UserData = missionType,
                    ToolTip = TextManager.Get("MissionTypeDescription." + missionType.ToString()),
                    OnSelected = (tickbox) =>
                    {
                        RefreshOutpostDropdown();
                        if (tickbox.Selected)
                        {
                            GameMain.Client?.ServerSettings.ClientAdminWrite(ServerSettings.NetFlags.Misc, addedMissionType: (Identifier)tickbox.UserData);
                        }
                        else
                        {
                            GameMain.Client?.ServerSettings.ClientAdminWrite(ServerSettings.NetFlags.Misc, removedMissionType: (Identifier)tickbox.UserData);
                        }
                        return true;
                    }
                };
                frame.RectTransform.MinSize = missionTypeTickBoxes[index].RectTransform.MinSize;
                index++;
            }
            clientDisabledElements.AddRange(missionTypeTickBoxes);

            return gameModeSpecificFrame;
        }
        
        private GUIFrame gameModeSettingsContent;
        private GUILayoutGroup gameModeSettingsLayout;

        private GUIComponent CreateGameModeSettingsPanel(GUIComponent parent)
        {
            //------------------------------------------------------------------
            // settings panel
            //------------------------------------------------------------------
            
            gameModeSettingsLayout = new GUILayoutGroup(new RectTransform(Vector2.One, parent.RectTransform))
            {
                Stretch = true
            };
            CreateSubHeader("GameModeSettings", gameModeSettingsLayout);

            gameModeSettingsContent = new GUIListBox(new RectTransform(Vector2.One, gameModeSettingsLayout.RectTransform)).Content;

            var winScoreHeader = new GUITextBlock(new RectTransform(new Vector2(1.0f, 0.0f), gameModeSettingsContent.RectTransform), TextManager.Get("ServerSettingsWinScorePvP"))
            {
                CanBeFocused = false
            };
            clientDisabledElements.Add(winScoreHeader);
            pvpOnlyElements.Add(winScoreHeader);

            var winScoreContainer = CreateLabeledSlider(gameModeSettingsContent, headerTag: string.Empty, valueLabelTag: string.Empty, tooltipTag: "ServerSettingsWinScorePvPTooltip",
                out var winScorePvPSlider, out var winScorePvPSliderLabel);
            winScorePvPSlider.Range = new Vector2(10, 1000);
            winScorePvPSlider.StepValue = 10;
            winScorePvPSlider.OnMoved = (scrollBar, _) =>
            {
                if (scrollBar.UserData is not GUITextBlock text) { return false; }
                text.Text = TextManager.GetWithVariable("ServerSettingsWinScoreValuePvP", "[value]", ((int)Math.Round(scrollBar.BarScrollValue, digits: 0)).ToString());
                return true;
            };
            winScorePvPSlider.OnReleased = (scrollBar, _) =>
            {
                GameMain.Client?.ServerSettings.ClientAdminWrite(ServerSettings.NetFlags.Properties);
                return true;
            };

            AssignComponentToServerSetting(winScorePvPSlider, nameof(ServerSettings.WinScorePvP));
            winScorePvPSlider.OnMoved(winScorePvPSlider, winScorePvPSlider.BarScroll);
            clientDisabledElements.AddRange(winScoreContainer.GetAllChildren());
            pvpOnlyElements.Add(winScoreContainer);

            //(pvp) stun resistance -------------------------------------------------
            var sliderContainer = CreateLabeledSlider(gameModeSettingsContent, headerTag: string.Empty, valueLabelTag: "gamemodesettings.stunresistance", tooltipTag: "gamemodesettings.stunresistancetooltip",
                out var slider, out var sliderLabel);
            LocalizedString stunResistLabel = sliderLabel.Text;
            slider.Step = 0.1f;
            slider.Range = new Vector2(0.0f, 1.0f);
            slider.OnReleased = (scrollbar, value) =>
            {
                GameMain.Client?.ServerSettings.ClientAdminWrite(ServerSettings.NetFlags.Properties);
                return true;
            };
            slider.OnMoved = (GUIScrollBar scrollBar, float barScroll) =>
            {
                ((GUITextBlock)scrollBar.UserData).Text = stunResistLabel.Replace("[percentage]", ((int)MathUtils.Round(scrollBar.BarScrollValue * 100.0f, 10.0f)).ToString());
                return true;
            };
            AssignComponentToServerSetting(slider, nameof(ServerSettings.PvPStunResist));
            slider.OnMoved(slider, slider.BarScroll);
            clientDisabledElements.AddRange(sliderContainer.GetAllChildren());
            pvpOnlyElements.Add(sliderContainer);

            //(pvp) mark enemy location toggle --------------------------------------
            var markApproximateEnemyLocationToggle = new GUITickBox(new RectTransform(new Vector2(0.4f, 0.06f), gameModeSettingsContent.RectTransform),
                TextManager.Get("ServerSettingsTrackOpponentInPvP"))
            {
                ToolTip = TextManager.Get("gamemodesettings.markenemylocationtooltip"),
                Selected = GameMain.Client != null && GameMain.Client.ServerSettings.TrackOpponentInPvP,
                OnSelected = (tt) =>
                {
                    GameMain.Client?.ServerSettings.ClientAdminWrite(ServerSettings.NetFlags.Properties);
                    return true;
                }
            };
            AssignComponentToServerSetting(markApproximateEnemyLocationToggle, nameof(ServerSettings.TrackOpponentInPvP));
            clientDisabledElements.Add(markApproximateEnemyLocationToggle);
            pvpOnlyElements.Add(markApproximateEnemyLocationToggle);

            //make the header use the height of the tickboxes to get the layout to be a little more uniform
            winScoreHeader.RectTransform.MinSize = new Point(0, markApproximateEnemyLocationToggle.RectTransform.MinSize.Y);

            //(pvp) spawn monsters tickbox -----------------------------------------
            var spawnMonstersTickbox = new GUITickBox(new RectTransform(Vector2.One, gameModeSettingsContent.RectTransform), TextManager.Get("gamemodesettings.spawnmonsters"))
            {
                ToolTip = TextManager.Get("gamemodesettings.spawnmonsterstooltip"),
                Selected = GameMain.Client != null && GameMain.Client.ServerSettings.PvPSpawnMonsters,
                OnSelected = (GUITickBox box) =>
                {
                    GameMain.Client?.ServerSettings.ClientAdminWrite(ServerSettings.NetFlags.Properties);
                    return true;
                }
            };
            AssignComponentToServerSetting(spawnMonstersTickbox, nameof(ServerSettings.PvPSpawnMonsters));
            clientDisabledElements.Add(spawnMonstersTickbox);
            pvpOnlyElements.Add(spawnMonstersTickbox);
            
            //(pvp) spawn wrecks tickbox -------------------------------------------
            var spawnWrecksTickbox = new GUITickBox(new RectTransform(Vector2.One, gameModeSettingsContent.RectTransform), TextManager.Get("gamemodesettings.spawnwrecks"))
            {
                ToolTip = TextManager.Get("gamemodesettings.spawnwreckstooltip"),
                Selected = GameMain.Client != null && GameMain.Client.ServerSettings.PvPSpawnWrecks,
                OnSelected = (GUITickBox box) =>
                {
                    GameMain.Client?.ServerSettings.ClientAdminWrite(ServerSettings.NetFlags.Properties);
                    return true;
                }
            };
            AssignComponentToServerSetting(spawnWrecksTickbox, nameof(ServerSettings.PvPSpawnWrecks));
            clientDisabledElements.Add(spawnWrecksTickbox);
            pvpOnlyElements.Add(spawnWrecksTickbox);

            // outpost -----------------------------------------------------------------------------
            GUILayoutGroup outpostHolder = new GUILayoutGroup(new RectTransform(new Vector2(1.0f, 0.05f), gameModeSettingsContent.RectTransform), isHorizontal: true)
            {
                Visible = false,
                Stretch = true
            };
            var outpostLabel = new GUITextBlock(new RectTransform(new Vector2(0.5f, 1.0f), outpostHolder.RectTransform), TextManager.Get("gamemodesettings.outpost"), wrap: true);
            outpostDropdown = new GUIDropDown(new RectTransform(new Vector2(0.5f, 1.0f), outpostHolder.RectTransform), elementCount: 6, listBoxScale: 2.0f)
            {
                ToolTip = TextManager.Get("gamemodesettings.outposttooltip"),
                AfterSelected = (component, obj) =>
                {
                    //don't register selecting the outpost until we've refreshed the available outposts,
                    //otherwise a client may request selecting "nothing" just because there's nothing in the list yet
                    if (outpostDropdownUpToDate && obj != null)
                    {
                        GameMain.Client?.ServerSettings.ClientAdminWrite(ServerSettings.NetFlags.Properties);
                    }
                    return true;
                }
            };
            outpostDropdown.ListBox.RectTransform.SetPosition(Anchor.BottomLeft, Pivot.TopLeft);
            //do this before adding the contents, otherwise they get disabled too (and we just want to disable the dropdown itself)
            clientDisabledElements.AddRange(outpostHolder.GetAllChildren());
            outpostDropdown.AddItem(TextManager.Get("random"), "Random".ToIdentifier());
            foreach (var submarineInfo in SubmarineInfo.SavedSubmarines.DistinctBy(s => s.Name))
            {
                outpostDropdown.AddItem(submarineInfo.DisplayName, userData: submarineInfo.Name.ToIdentifier(), toolTip: submarineInfo.Description);                
            }

            AssignComponentToServerSetting(outpostDropdown, nameof(ServerSettings.SelectedOutpostName));
            outpostHolder.RectTransform.MinSize = new Point(0, outpostDropdown.RectTransform.MinSize.Y);

            campaignHiddenElements.Add(outpostHolder);

            // biome -----------------------------------------------------------------------------
            GUILayoutGroup biomeHolder = new GUILayoutGroup(new RectTransform(new Vector2(1.0f, 0.05f), gameModeSettingsContent.RectTransform), isHorizontal: true)
            {
                Stretch = true
            };
            var biomeLabel = new GUITextBlock(new RectTransform(new Vector2(0.5f, 1.0f), biomeHolder.RectTransform), TextManager.Get("biome"), wrap: true);
            var biomeDropdown = new GUIDropDown(new RectTransform(new Vector2(0.5f, 1.0f), biomeHolder.RectTransform), elementCount: 6, listBoxScale: 2.0f)
            {
                AfterSelected = (component, obj) =>
                {
                    if (obj != null)
                    {
                        GameMain.Client?.ServerSettings.ClientAdminWrite(ServerSettings.NetFlags.Properties);
                    }
                    return true;
                }
            };
            biomeDropdown.ListBox.RectTransform.SetPosition(Anchor.BottomLeft, Pivot.TopLeft);
            //do this before adding the contents, otherwise they get disabled too (and we just want to disable the dropdown itself)
            clientDisabledElements.AddRange(biomeHolder.GetAllChildren());
            biomeDropdown.AddItem(TextManager.Get("random"), "Random".ToIdentifier());
            foreach (var biome in Biome.Prefabs.OrderBy(b => b.MinDifficulty))
            {
                if (biome.IsEndBiome) { continue; }
                biomeDropdown.AddItem(biome.DisplayName, biome.Identifier);
            }
            AssignComponentToServerSetting(biomeDropdown, nameof(ServerSettings.Biome));
            biomeHolder.RectTransform.MinSize = new Point(0, biomeDropdown.RectTransform.MinSize.Y);
            
            campaignHiddenElements.Add(biomeHolder);

            //seed ------------------------------------------------------------------

            var seedLabel = new GUITextBlock(new RectTransform(new Vector2(1.0f, 0.1f), gameModeSettingsContent.RectTransform), TextManager.Get("LevelSeed"))
            {
                CanBeFocused = false
            };
            LevelSeedBox = new GUITextBox(new RectTransform(new Vector2(0.5f, 1.0f), seedLabel.RectTransform, Anchor.CenterRight));
            LevelSeedBox.OnDeselected += (textBox, key) =>
            {
                GameMain.Client?.ServerSettings.ClientAdminWrite(ServerSettings.NetFlags.LevelSeed);
            };
            campaignDisabledElements.Add(LevelSeedBox);
            campaignDisabledElements.Add(seedLabel);
            clientDisabledElements.Add(LevelSeedBox);
            clientDisabledElements.Add(seedLabel);
            LevelSeed = ToolBox.RandomSeed(8);

            //level difficulty ------------------------------------------------------------------

            var levelDifficultyHolder = CreateLabeledSlider(gameModeSettingsContent, "LevelDifficulty", "", "LevelDifficultyExplanation", out levelDifficultySlider, out var difficultySliderLabel,
                step: 0.01f, range: new Vector2(0.0f, 100.0f));
            levelDifficultySlider.OnReleased = (scrollbar, value) =>
            {
                GameMain.Client?.ServerSettings.ClientAdminWrite(ServerSettings.NetFlags.Properties);
                return true;
            };
            levelDifficultySlider.OnMoved = (scrollbar, value) =>
            {
                if (!EventManagerSettings.Prefabs.Any()) { return true; }
                difficultySliderLabel.Text =
                    EventManagerSettings.GetByDifficultyPercentile(value).Name
                    + $" ({TextManager.GetWithVariable("percentageformat", "[value]", ((int)Math.Round(scrollbar.BarScrollValue)).ToString())})";
                difficultySliderLabel.TextColor = ToolBox.GradientLerp(scrollbar.BarScroll, GUIStyle.Green, GUIStyle.Orange, GUIStyle.Red);
                return true;
            };
            AssignComponentToServerSetting(levelDifficultySlider, nameof(ServerSettings.SelectedLevelDifficulty));
            campaignDisabledElements.AddRange(levelDifficultyHolder.GetAllChildren());
            clientDisabledElements.AddRange(levelDifficultyHolder.GetAllChildren());

            //bot count ------------------------------------------------------------------
            CreateSubHeader("BotSettings", gameModeSettingsContent);

            var botCountSettingHolder = new GUILayoutGroup(new RectTransform(new Vector2(1.0f, 0.1f), gameModeSettingsContent.RectTransform), isHorizontal: true, childAnchor: Anchor.CenterLeft) { Stretch = true };
            new GUITextBlock(new RectTransform(new Vector2(0.7f, 0.0f), botCountSettingHolder.RectTransform), TextManager.Get("BotCount"), wrap: true);
            var botCountSelection = new GUISelectionCarousel<int>(new RectTransform(new Vector2(0.5f, 1.0f), botCountSettingHolder.RectTransform));
            for (int i = 0; i <= NetConfig.MaxPlayers; i++)
            {
                botCountSelection.AddElement(i, i.ToString());
            }
            AssignComponentToServerSetting(botCountSelection, nameof(ServerSettings.BotCount));
            clientDisabledElements.AddRange(botCountSettingHolder.GetAllChildren());
            botSettingsElements.Add(botCountSelection);

            var botSpawnModeSettingHolder = new GUILayoutGroup(new RectTransform(new Vector2(1.0f, 0.1f), gameModeSettingsContent.RectTransform), isHorizontal: true, childAnchor: Anchor.CenterLeft) { Stretch = true };
            new GUITextBlock(new RectTransform(new Vector2(0.7f, 0.0f), botSpawnModeSettingHolder.RectTransform), TextManager.Get("BotSpawnMode"), wrap: true);
            var botSpawnModeSelection = new GUISelectionCarousel<BotSpawnMode>(new RectTransform(new Vector2(0.5f, 1.0f), botSpawnModeSettingHolder.RectTransform));
            foreach (var botSpawnMode in Enum.GetValues(typeof(BotSpawnMode)).Cast<BotSpawnMode>())
            {
                botSpawnModeSelection.AddElement(botSpawnMode, botSpawnMode.ToString(), TextManager.Get($"botspawnmode.{botSpawnMode}.tooltip"));
            }
            botSpawnModeSelection.OnValueChanged += (_) => GameMain.Client?.ServerSettings.ClientAdminWrite(ServerSettings.NetFlags.Properties);
            AssignComponentToServerSetting(botSpawnModeSelection, nameof(ServerSettings.BotSpawnMode));
            clientDisabledElements.AddRange(botSpawnModeSettingHolder.GetAllChildren());
            botSettingsElements.Add(botSpawnModeSelection);

            botCountSelection.OnValueChanged += (_) =>
            {
                botSpawnModeSelection.Enabled = GameMain.Client.ServerSettings.BotCount > 0;
                GameMain.Client?.ServerSettings.ClientAdminWrite(ServerSettings.NetFlags.Properties);
            };

            //traitor probability ------------------------------------------------------------------

            CreateSubHeader("TraitorSettings", gameModeSettingsContent);

            //spacing
            new GUIFrame(new RectTransform(new Point(1, GUI.IntScale(5)), gameModeSettingsContent.RectTransform), style: null);

            //the probability slider is a traitor element, but we don't add it to traitorElements
            //because we don't want to disable it when sliding it to 0 (need to be able to slide it back!)
            var traitorProbabilityHolder = CreateLabeledSlider(gameModeSettingsContent, "traitor.probability", "", "traitor.probability.tooltip",
                out traitorProbabilitySlider, out var traitorProbabilityText,
                step: 0.01f, range: new Vector2(0.0f, 1.0f));
            traitorProbabilitySlider.OnMoved = (scrollbar, value) =>
            {
                traitorProbabilityText.Text = TextManager.GetWithVariable("percentageformat", "[value]", ((int)Math.Round(scrollbar.BarScrollValue * 100)).ToString());
                traitorProbabilityText.TextColor =
                    value <= 0.0f ?
                        GUIStyle.Green :
                        ToolBox.GradientLerp(scrollbar.BarScroll, GUIStyle.Yellow, GUIStyle.Orange, GUIStyle.Red);
                RefreshEnabledElements();
                return true;
            };
            traitorProbabilitySlider.OnMoved(traitorProbabilitySlider, traitorProbabilitySlider.BarScroll);
            traitorProbabilitySlider.OnReleased += (scrollbar, value) => { GameMain.Client?.ServerSettings.ClientAdminWrite(ServerSettings.NetFlags.Properties); return true; };
            AssignComponentToServerSetting(traitorProbabilitySlider, nameof(ServerSettings.TraitorProbability));
            traitorElements.Clear();
            clientDisabledElements.AddRange(traitorProbabilityHolder.GetAllChildren());

            var traitorDangerHolder = new GUILayoutGroup(new RectTransform(new Vector2(1.0f, 0.1f), gameModeSettingsContent.RectTransform), isHorizontal: true, childAnchor: Anchor.CenterLeft)
            {
                Stretch = true
            };

            var dangerLevelLabel = new GUITextBlock(new RectTransform(new Vector2(0.7f, 1.0f), traitorDangerHolder.RectTransform), TextManager.Get("traitor.dangerlevelsetting"), wrap: true)
            {
                ToolTip = TextManager.Get("traitor.dangerlevelsetting.tooltip")
            };

            var traitorDangerContainer = new GUILayoutGroup(new RectTransform(new Vector2(0.5f, 1.0f), traitorDangerHolder.RectTransform), isHorizontal: true, childAnchor: Anchor.CenterLeft) { RelativeSpacing = 0.05f, Stretch = true };
            var traitorDangerButtons = new GUIButton[2];
            traitorDangerButtons[0] = new GUIButton(new RectTransform(new Vector2(0.15f, 1.0f), traitorDangerContainer.RectTransform), style: "GUIButtonToggleLeft")
            {
                OnClicked = (button, obj) =>
                {
                    GameMain.Client?.ServerSettings.ClientAdminWrite(ServerSettings.NetFlags.Misc, traitorDangerLevel: -1);
                    return true;
                }
            };

            traitorDangerGroup = new GUILayoutGroup(new RectTransform(new Vector2(0.7f, 1.0f), traitorDangerContainer.RectTransform), isHorizontal: true, childAnchor: Anchor.CenterLeft)
            {
                Stretch = true,
                AbsoluteSpacing = 1
            };
            for (int i = TraitorEventPrefab.MinDangerLevel; i <= TraitorEventPrefab.MaxDangerLevel; i++)
            {
                var difficultyColor = Mission.GetDifficultyColor(i);
                new GUIImage(new RectTransform(new Vector2(0.75f), traitorDangerGroup.RectTransform), "DifficultyIndicator", scaleToFit: true)
                {
                    ToolTip =
                        RichString.Rich(
                            $"‖color:{Color.White.ToStringHex()}‖{TextManager.Get($"traitor.dangerlevel.{i}")}‖color:end‖" + '\n' +
                            TextManager.Get($"traitor.dangerlevel.{i}.description")),
                    Color = difficultyColor,
                    DisabledColor = Color.Gray * 0.5f,
                };
            }

            traitorDangerButtons[1] = new GUIButton(new RectTransform(new Vector2(0.15f, 1.0f), traitorDangerContainer.RectTransform), style: "GUIButtonToggleRight")
            {
                OnClicked = (button, obj) =>
                {
                    GameMain.Client?.ServerSettings.ClientAdminWrite(ServerSettings.NetFlags.Misc, traitorDangerLevel: 1);
                    return true;
                }
            };

            traitorDangerContainer.InheritTotalChildrenMinHeight();
            SetTraitorDangerIndicators(GameMain.Client?.ServerSettings.TraitorDangerLevel ?? TraitorEventPrefab.MinDangerLevel);
            traitorElements.Add(dangerLevelLabel);
            traitorElements.AddRange(traitorDangerGroup.Children);
            traitorElements.AddRange(traitorDangerButtons);

            var traitorsMinPlayerCountHolder = new GUILayoutGroup(new RectTransform(new Vector2(1.0f, 0.1f), gameModeSettingsContent.RectTransform), isHorizontal: true, childAnchor: Anchor.CenterLeft) { Stretch = true };
            new GUITextBlock(new RectTransform(new Vector2(0.7f, 0.0f), traitorsMinPlayerCountHolder.RectTransform), TextManager.Get("ServerSettingsTraitorsMinPlayerCount"), wrap: true)
            {
                ToolTip = TextManager.Get("ServerSettingsTraitorsMinPlayerCountToolTip")
            };
            var traitorsMinPlayerCount = new GUISelectionCarousel<int>(new RectTransform(new Vector2(0.5f, 1.0f), traitorsMinPlayerCountHolder.RectTransform));
            for (int i = 1; i <= NetConfig.MaxPlayers; i++)
            {
                traitorsMinPlayerCount.AddElement(i, i.ToString());
            }
            traitorsMinPlayerCount.OnValueChanged += (_) => GameMain.Client?.ServerSettings.ClientAdminWrite(ServerSettings.NetFlags.Properties);
            AssignComponentToServerSetting(traitorsMinPlayerCount, nameof(ServerSettings.TraitorsMinPlayerCount));
            traitorElements.AddRange(traitorsMinPlayerCountHolder.Children);

            foreach (var traitorElement in traitorElements)
            {
                if (!clientDisabledElements.Contains(traitorElement))
                {
                    clientDisabledElements.Add(traitorElement);
                }
            }

            return gameModeSettingsContent;
        }

        private GUIButton upgradesTabButton,
                          respawnTabButton;

        private void SelectRespawnTab()
            => SelectTabShared(buttonToEnable: respawnTabButton,
                               buttonToDisable: upgradesTabButton,
                               elementsToEnable: respawnSettings,
                               elementsToDisable: disembarkPerkSettings);

        private void SelectUpgradesTab()
            => SelectTabShared(buttonToEnable: upgradesTabButton,
                               buttonToDisable: respawnTabButton,
                               elementsToEnable: disembarkPerkSettings,
                               elementsToDisable: respawnSettings);

        private void SelectTabShared(GUIButton buttonToEnable,
                                     GUIButton buttonToDisable,
                                     ICollection<GUIComponent> elementsToEnable,
                                     ICollection<GUIComponent> elementsToDisable)

        {
            if (buttonToEnable is null || buttonToDisable is null) { return; }

            buttonToDisable.Selected = false;
            buttonToEnable.Selected = true;
            foreach (var element in elementsToDisable) { element.Visible = element.Enabled = false; }
            foreach (var element in elementsToEnable) { element.Visible = element.Enabled = true; }
        }

        private GUIComponent CreateGeneralSettingsPanel(GUIComponent parent)
        {
            //------------------------------------------------------------------
            // settings panel
            //------------------------------------------------------------------

            GUILayoutGroup mainContainer = new GUILayoutGroup(new RectTransform(Vector2.One, parent.RectTransform));

            GUILayoutGroup tabContainer = new GUILayoutGroup(new RectTransform(new Vector2(1f, 0.066f), mainContainer.RectTransform), isHorizontal: true)
            {
                RelativeSpacing = 0.02f,
                Stretch = true
            };

            respawnTabButton = new GUIButton(new RectTransform(new Vector2(0.5f, 1.0f), tabContainer.RectTransform), TextManager.Get("respawnsettings"), style: "GUITabButton") { Selected = true };
            upgradesTabButton = new GUIButton(new RectTransform(new Vector2(0.5f, 1.0f), tabContainer.RectTransform), TextManager.Get("disembarkpointsettings"), style: "GUITabButton");

            respawnTabButton.OnClicked = (button, _) =>
            {
                SelectRespawnTab();
                return true;
            };

            upgradesTabButton.OnClicked = (button, _) =>
            {
                SelectUpgradesTab();
                return true;
            };


            GUIFrame mainFrame = new GUIFrame(new RectTransform(new Vector2(1f, 1.0f - tabContainer.RectTransform.RelativeSize.Y), mainContainer.RectTransform), style: null);

            GUILayoutGroup settingsLayout = new GUILayoutGroup(new RectTransform(Vector2.One, mainFrame.RectTransform));

            var settingsList = new GUIListBox(new RectTransform(Vector2.One, settingsLayout.RectTransform));
            respawnSettings.Add(settingsLayout);

            CreateDisembarkPointPanel(mainFrame);

            var settingsContent = settingsList.Content;

            // ------------------------------------------------------------------

            var respawnModeHolder = new GUILayoutGroup(new RectTransform(new Vector2(1.0f, 0.1f), settingsContent.RectTransform), isHorizontal: true, childAnchor: Anchor.CenterLeft) { Stretch = true };
            respawnModeLabel = new GUITextBlock(new RectTransform(new Vector2(0.4f, 0.0f), respawnModeHolder.RectTransform), TextManager.Get("RespawnMode"), wrap: true);
            respawnModeSelection = new GUISelectionCarousel<RespawnMode>(new RectTransform(new Vector2(0.6f, 1.0f), respawnModeHolder.RectTransform));
            foreach (var respawnMode in Enum.GetValues(typeof(RespawnMode)).Cast<RespawnMode>().Where(rm => rm != RespawnMode.None))
            {
                respawnModeSelection.AddElement(respawnMode, TextManager.Get($"respawnmode.{respawnMode}"), TextManager.Get($"respawnmode.{respawnMode}.tooltip"));
            }
            
            respawnModeSelection.ElementSelectionCondition += (value) => value != RespawnMode.Permadeath || SelectedMode == GameModePreset.MultiPlayerCampaign;
            respawnModeSelection.OnValueChanged += (_) => GameMain.Client?.ServerSettings.ClientAdminWrite(ServerSettings.NetFlags.Properties);
            AssignComponentToServerSetting(respawnModeSelection, nameof(ServerSettings.RespawnMode));

            GUILayoutGroup shuttleHolder = new GUILayoutGroup(new RectTransform(new Vector2(1.0f, 0.05f), settingsContent.RectTransform), isHorizontal: true)
            {
                Stretch = true
            };

            shuttleTickBox = new GUITickBox(new RectTransform(Vector2.One, shuttleHolder.RectTransform), TextManager.Get("RespawnShuttle"))
            {
                ToolTip = TextManager.Get("RespawnShuttleExplanation"),
                Selected = !PermadeathMode,
                OnSelected = (GUITickBox box) =>
                {
                    GameMain.Client?.ServerSettings.ClientAdminWrite(ServerSettings.NetFlags.Properties);
                    return true;
                }
            };
            AssignComponentToServerSetting(shuttleTickBox, nameof(ServerSettings.UseRespawnShuttle));
            midRoundRespawnSettings.Add(shuttleTickBox);

            shuttleTickBox.TextBlock.RectTransform.SizeChanged += () =>
            {
                shuttleTickBox.TextBlock.AutoScaleHorizontal = true;
                shuttleTickBox.TextBlock.TextScale = 1.0f;
                if (shuttleTickBox.TextBlock.TextScale < 0.75f)
                {
                    shuttleTickBox.TextBlock.Wrap = true;
                    shuttleTickBox.TextBlock.AutoScaleHorizontal = true;
                    shuttleTickBox.TextBlock.TextScale = 1.0f;
                }
            };
            ShuttleList = new GUIDropDown(new RectTransform(Vector2.One, shuttleHolder.RectTransform), elementCount: 10)
            {
                OnSelected = (component, obj) =>
                {
                    SelectShuttle((SubmarineInfo)obj);
                    return true;
                }
            };
            ShuttleList.ListBox.RectTransform.MinSize = new Point(250, 0);
            shuttleHolder.RectTransform.MinSize = new Point(0, ShuttleList.RectTransform.Children.Max(c => c.MinSize.Y));
            midRoundRespawnSettings.Add(ShuttleList);

            respawnIntervalElement = CreateLabeledSlider(settingsContent, "ServerSettingsRespawnInterval", "", "", out var respawnIntervalSlider, out var respawnIntervalSliderLabel,
                range: new Vector2(10.0f, 600.0f));
            LocalizedString intervalLabel = respawnIntervalSliderLabel.Text;
            respawnIntervalSlider.StepValue = 10.0f;
            respawnIntervalSlider.OnMoved = (GUIScrollBar scrollBar, float barScroll) =>
            {
                GUITextBlock text = scrollBar.UserData as GUITextBlock;
                text.Text = intervalLabel + " " + ToolBox.SecondsToReadableTime(scrollBar.BarScrollValue);
                return true;
            };
            respawnIntervalSlider.OnReleased = (GUIScrollBar scrollBar, float barScroll) =>
            {
                GameMain.Client?.ServerSettings.ClientAdminWrite(ServerSettings.NetFlags.Properties);
                return true;
            };
            respawnIntervalSlider.OnMoved(respawnIntervalSlider, respawnIntervalSlider.BarScroll);
            AssignComponentToServerSetting(respawnIntervalSlider, nameof(ServerSettings.RespawnInterval));

            var minRespawnElement = CreateLabeledSlider(settingsContent, "ServerSettingsMinRespawn", "", "ServerSettingsMinRespawnToolTip", out var minRespawnSlider, out var minRespawnSliderLabel,
                step: 0.1f, range: new Vector2(0.0f, 1.0f));
            minRespawnSlider.OnMoved = (GUIScrollBar scrollBar, float barScroll) =>
            {
                GUITextBlock text = scrollBar.UserData as GUITextBlock;
                text.Text = ToolBox.GetFormattedPercentage(scrollBar.BarScrollValue);
                return true;
            };
            minRespawnSlider.OnReleased = (GUIScrollBar scrollBar, float barScroll) =>
            {
                GameMain.Client?.ServerSettings.ClientAdminWrite(ServerSettings.NetFlags.Properties);
                return true;
            };
            minRespawnSlider.OnMoved(minRespawnSlider, minRespawnSlider.BarScroll);
            midRoundRespawnSettings.AddRange(minRespawnElement.GetAllChildren());
            AssignComponentToServerSetting(minRespawnSlider, nameof(ServerSettings.MinRespawnRatio));

            var respawnDurationElement = CreateLabeledSlider(settingsContent, "ServerSettingsRespawnDuration", "", "ServerSettingsRespawnDurationTooltip", out var respawnDurationSlider, out var respawnDurationSliderLabel,
                step: 0.1f, range: new Vector2(60.0f, 660.0f));
            respawnDurationSlider.OnMoved = (GUIScrollBar scrollBar, float barScroll) =>
            {
                GUITextBlock text = scrollBar.UserData as GUITextBlock;
                text.Text = scrollBar.BarScrollValue <= 0 ? TextManager.Get("Unlimited") : ToolBox.SecondsToReadableTime(scrollBar.BarScrollValue);
                return true;
            };
            respawnDurationSlider.OnReleased = (GUIScrollBar scrollBar, float barScroll) =>
            {
                GameMain.Client?.ServerSettings.ClientAdminWrite(ServerSettings.NetFlags.Properties);
                return true;
            };
            respawnDurationSlider.ScrollToValue = (GUIScrollBar scrollBar, float barScroll) =>
            {
                return barScroll >= 1.0f ? 0.0f : barScroll * (scrollBar.Range.Y - scrollBar.Range.X) + scrollBar.Range.X;
            };
            respawnDurationSlider.ValueToScroll = (GUIScrollBar scrollBar, float value) =>
            {
                return value <= 0.0f ? 1.0f : (value - scrollBar.Range.X) / (scrollBar.Range.Y - scrollBar.Range.X);
            };
            respawnDurationSlider.OnMoved(respawnDurationSlider, respawnDurationSlider.BarScroll);
            midRoundRespawnSettings.AddRange(respawnDurationElement.GetAllChildren());
            AssignComponentToServerSetting(respawnDurationSlider, nameof(ServerSettings.MaxTransportTime));

            var skillLossElement = CreateLabeledSlider(settingsContent, "ServerSettingsSkillLossPercentageOnDeath", "", "ServerSettingsSkillLossPercentageOnDeathToolTip", 
                out var skillLossSlider, out var skillLossSliderLabel, range: new Vector2(0, 100));
            skillLossSlider.StepValue = 1;
            skillLossSlider.OnMoved = (GUIScrollBar scrollBar, float barScroll) =>
            {
                GUITextBlock text = scrollBar.UserData as GUITextBlock;
                text.Text = TextManager.GetWithVariable("percentageformat", "[value]", ((int)Math.Round(scrollBar.BarScrollValue)).ToString());
                return true;
            };
            skillLossSlider.OnReleased = (GUIScrollBar scrollBar, float barScroll) =>
            {
                GameMain.Client?.ServerSettings.ClientAdminWrite(ServerSettings.NetFlags.Properties);
                return true;
            };
            permadeathDisabledRespawnSettings.AddRange(skillLossElement.GetAllChildren());
            clientDisabledElements.AddRange(skillLossElement.GetAllChildren());
            AssignComponentToServerSetting(skillLossSlider, nameof(ServerSettings.SkillLossPercentageOnDeath));
            skillLossSlider.OnMoved(skillLossSlider, skillLossSlider.BarScroll);

            var skillLossImmediateRespawnElement = CreateLabeledSlider(settingsContent, "ServerSettingsSkillLossPercentageOnImmediateRespawn", "", "ServerSettingsSkillLossPercentageOnImmediateRespawnToolTip", 
                out var skillLossImmediateRespawnSlider, out var skillLossImmediateRespawnSliderLabel, range: new Vector2(0, 100));
            skillLossImmediateRespawnSlider.StepValue = 1;
            skillLossImmediateRespawnSlider.OnMoved = (GUIScrollBar scrollBar, float barScroll) =>
            {
                GUITextBlock text = scrollBar.UserData as GUITextBlock;
                text.Text = TextManager.GetWithVariable("percentageformat", "[value]", ((int)Math.Round(scrollBar.BarScrollValue)).ToString());
                return true;
            };
            skillLossImmediateRespawnSlider.OnReleased = (GUIScrollBar scrollBar, float barScroll) =>
            {
                GameMain.Client?.ServerSettings.ClientAdminWrite(ServerSettings.NetFlags.Properties);
                return true;
            };
            midRoundRespawnSettings.AddRange(skillLossImmediateRespawnElement.GetAllChildren());
            permadeathDisabledRespawnSettings.AddRange(skillLossImmediateRespawnElement.GetAllChildren());
            AssignComponentToServerSetting(skillLossImmediateRespawnSlider, nameof(ServerSettings.SkillLossPercentageOnImmediateRespawn));
            skillLossImmediateRespawnSlider.OnMoved(skillLossImmediateRespawnSlider, skillLossImmediateRespawnSlider.BarScroll);

            var newCharacterCostSliderElement = CreateLabeledSlider(settingsContent,
                "ServerSettings.ReplaceCostPercentage", "", "ServerSettings.ReplaceCostPercentage.tooltip",
                out var newCharacterCostSlider, out var newCharacterCostSliderLabel,
                range: new Vector2(0, 200), step: 10f);
            newCharacterCostSlider.StepValue = 10f;
            newCharacterCostSlider.OnMoved = (GUIScrollBar scrollBar, float _) =>
            {
                GUITextBlock textBlock = scrollBar.UserData as GUITextBlock;
                int currentMultiplier = (int)Math.Round(scrollBar.BarScrollValue);
                if (currentMultiplier < 1)
                {
                    textBlock.Text = TextManager.Get("ServerSettings.ReplaceCostPercentage.Free");
                }
                else
                {
                    textBlock.Text = TextManager.GetWithVariable("percentageformat", "[value]", currentMultiplier.ToString());
                }
                return true;
            };
            newCharacterCostSlider.OnReleased = (GUIScrollBar scrollBar, float barScroll) =>
            {
                GameMain.Client?.ServerSettings.ClientAdminWrite(ServerSettings.NetFlags.Properties);
                return true;
            };
            clientDisabledElements.AddRange(newCharacterCostSliderElement.GetAllChildren());
            permadeathEnabledRespawnSettings.AddRange(newCharacterCostSliderElement.GetAllChildren());
            ironmanDisabledRespawnSettings.AddRange(newCharacterCostSliderElement.GetAllChildren());
            AssignComponentToServerSetting(newCharacterCostSlider, nameof(ServerSettings.ReplaceCostPercentage));
            newCharacterCostSlider.OnMoved(newCharacterCostSlider, newCharacterCostSlider.BarScroll); // initialize

            var allowBotTakeoverTickbox = new GUITickBox(new RectTransform(Vector2.One, settingsContent.RectTransform), TextManager.Get("AllowBotTakeover"))
            {
                ToolTip = TextManager.Get("AllowBotTakeover.Tooltip"),
                Selected = GameMain.Client != null && GameMain.Client.ServerSettings.AllowBotTakeoverOnPermadeath,
                OnSelected = (GUITickBox box) =>
                {
                    GameMain.Client?.ServerSettings.ClientAdminWrite(ServerSettings.NetFlags.Properties);
                    return true;
                }
            };
            AssignComponentToServerSetting(allowBotTakeoverTickbox, nameof(ServerSettings.AllowBotTakeoverOnPermadeath));
            permadeathEnabledRespawnSettings.Add(allowBotTakeoverTickbox);
            ironmanDisabledRespawnSettings.Add(allowBotTakeoverTickbox);
            clientDisabledElements.Add(allowBotTakeoverTickbox);
            
            var ironmanTickbox = new GUITickBox(new RectTransform(Vector2.One, settingsContent.RectTransform), TextManager.Get("IronmanMode").ToUpper())
            {
                ToolTip = TextManager.Get("IronmanMode.Tooltip"),
                Selected = GameMain.Client != null && GameMain.Client.ServerSettings.IronmanMode,
                OnSelected = (GUITickBox box) =>
                {
                    GameMain.Client?.ServerSettings.ClientAdminWrite(ServerSettings.NetFlags.Properties);
                    return true;
                }
            };
            AssignComponentToServerSetting(ironmanTickbox, nameof(ServerSettings.IronmanMode));
            permadeathEnabledRespawnSettings.Add(ironmanTickbox);
            clientDisabledElements.Add(ironmanTickbox);
            
            foreach (var respawnElement in midRoundRespawnSettings)
            {
                if (!clientDisabledElements.Contains(respawnElement))
                {
                    clientDisabledElements.Add(respawnElement);
                }
            }

            return settingsContent;
        }

        private GUIListBox disembarkPerkSettingList;
        private GUIComponent disembarkPerkDisabledDisclaimer;
        private GUIComponent noPerksAvailableDisclaimer;
        private GUITextBlock disembarkPerkFooterText;

        /// <summary>
        /// Used to prevent disembarkPerkSettingList.AfterSelected from firing when the server settings are updated.
        /// </summary>
        private bool isUpdatingPerks;

        public void CreateDisembarkPointPanel(GUIComponent parent)
        {
            GUILayoutGroup settingsLayout = new GUILayoutGroup(new RectTransform(Vector2.One, parent.RectTransform))
            {
                Stretch = true,
                Visible = false,
            };

            var settingsList = new GUIListBox(new RectTransform(Vector2.One, settingsLayout.RectTransform))
            {
                SelectMultiple = true,
                DisabledColor = Color.White * 0.1f
            };

            disembarkPerkSettingList = settingsList;

            noPerksAvailableDisclaimer = new GUIFrame(new RectTransform(Vector2.One, settingsLayout.RectTransform), style: "GUIBackgroundBlocker")
            {
                Visible = false,
                IgnoreLayoutGroups = true
            };

            new GUITextBlock(new RectTransform(Vector2.One, noPerksAvailableDisclaimer.RectTransform), TextManager.Get("noperksavailable"), textAlignment: Alignment.Center, font: GUIStyle.SubHeadingFont, wrap: true)
            {
                TextColor = GUIStyle.Red,
                Shadow = true,
            };

            disembarkPerkDisabledDisclaimer = new GUIFrame(new RectTransform(Vector2.One, settingsLayout.RectTransform), style: "GUIBackgroundBlocker")
            {
                IgnoreLayoutGroups = true,
            };
            var disclaimerLayout = new GUILayoutGroup(new RectTransform(Vector2.One, disembarkPerkDisabledDisclaimer.RectTransform));

            new GUITextBlock(new RectTransform(new Vector2(1f, 0.3f), disclaimerLayout.RectTransform), TextManager.Get("disembarkpointselectteam"), textAlignment: Alignment.BottomCenter, font: GUIStyle.LargeFont)
            {
                TextColor = GUIStyle.Red
            };

            var teamSelectLayout = new GUILayoutGroup(new RectTransform(new Vector2(1f, 0.7f), disclaimerLayout.RectTransform), isHorizontal: true);
            CreateTeamDisclaimerButtons(teamSelectLayout);

            disembarkPerkFooterText = new GUITextBlock(new RectTransform(new Vector2(1.0f, 0.055f), settingsLayout.RectTransform) { MinSize = new Point(0, GUI.IntScale(28)) },
                string.Empty, font: GUIStyle.SubHeadingFont, textAlignment: Alignment.CenterRight, textColor: GUIStyle.TextColorBright, color: Color.Black * 0.8f, style: null)
            {
                Padding = new Vector4(10, 0, 10, 0) * GUI.Scale
            };
            UpdatePerkFooterText(settingsList);

            settingsList.AfterSelected = (component, o) =>
            {
                if (GameMain.Client?.ServerSettings is not { } settings) { return false; }

                UpdatePerkFooterText(settingsList);

                if (isUpdatingPerks) { return false; }

                bool canChangePerks = ServerSettings.HasPermissionToChangePerks();

                if (!canChangePerks) { return false; }

                switch (MultiplayerPreferences.Instance.TeamPreference)
                {
                    case CharacterTeamType.Team2:
                    {
                        settings.SelectedSeparatistsPerks = PerksFromSelectedElements();
                        break;
                    }
                    default:
                    {
                        settings.SelectedCoalitionPerks = PerksFromSelectedElements();
                        break;
                    }

                    Identifier[] PerksFromSelectedElements()
                    {
                        var list = settingsList.AllSelected.Select(static c => ((DisembarkPerkPrefab)c.UserData)).ToList();

                        bool potentiallyHasOrphanedPerks = true;

                        do
                        {
                            potentiallyHasOrphanedPerks = false;
                            if (list.None()) { break; }

                            list.ForEachMod(perk =>
                            {
                                if (perk.Prerequisite.IsEmpty) { return; }

                                if (list.All(p => p.Identifier != perk.Prerequisite))
                                {
                                    list.Remove(perk);
                                    potentiallyHasOrphanedPerks = true;
                                }
                            });
                        } while (potentiallyHasOrphanedPerks);

                        return list.Select(static p => p.Identifier).ToArray();
                    }
                }

                settings.ClientAdminWritePerks();

                return true;
            };

            disembarkPerkSettings.Add(settingsLayout);

            Identifier disembarkPerkCategory = Identifier.Empty;

            foreach (var disembarkPerkPrefab in DisembarkPerkPrefab.Prefabs
                                                                   .OrderBy(static p => p.SortCategory)
                                                                   .ThenBy(static p => p.Cost)
                                                                   .ThenBy(static p => p.SortKey))
            {
                if (disembarkPerkCategory != disembarkPerkPrefab.SortCategory)
                {
                    disembarkPerkCategory = disembarkPerkPrefab.SortCategory;

                    if (!disembarkPerkCategory.IsEmpty)
                    {
                        GUIFrame categoryFrame = new GUIFrame(new RectTransform(new Vector2(1.0f, 0.15f), settingsList.Content.RectTransform), style: null)
                        {
                            CanBeFocused = false
                        };

                        new GUITextBlock(new RectTransform(Vector2.One, categoryFrame.RectTransform), TextManager.Get($"perkcategory.{disembarkPerkPrefab.SortCategory}"), font: GUIStyle.SubHeadingFont, textAlignment: Alignment.Center);
                    }
                }

                GUIFrame frame = new GUIFrame(new RectTransform(new Vector2(1.0f, 0.1f), settingsList.Content.RectTransform), style: "ListBoxElement")
                {
                    UserData = disembarkPerkPrefab,
                    ToolTip = disembarkPerkPrefab.Description
                };
                GUILayoutGroup prefabLayout = new GUILayoutGroup(new RectTransform(Vector2.One, frame.RectTransform), isHorizontal: true)
                {
                    Stretch = true
                };

                var perkLabel = new GUITextBlock(new RectTransform(new Vector2(0.8f, 1.0f), prefabLayout.RectTransform), disembarkPerkPrefab.Name, textAlignment: Alignment.CenterLeft)
                {
                    DisabledTextColor = Color.White * 0.1f,
                    DisabledColor = Color.White * 0.1f,
                    CanBeFocused = false,
                };

                perkLabel.Text = ToolBox.LimitString(perkLabel.Text, perkLabel.Font, perkLabel.Rect.Width);

                var costLabel = new GUITextBlock(new RectTransform(new Vector2(0.2f, 1.0f), prefabLayout.RectTransform), disembarkPerkPrefab.Cost.ToString(), textAlignment: Alignment.Right)
                {
                    DisabledTextColor = Color.White * 0.1f,
                    DisabledColor = Color.White * 0.1f,
                    CanBeFocused = false,
                };
            }

            GameMain.Client?.OnPermissionChanged?.RegisterOverwriteExisting(nameof(CreateDisembarkPointPanel).ToIdentifier(), _ =>
            {
                UpdateDisembarkPointListFromServerSettings();
            });

            void CreateTeamDisclaimerButtons(GUILayoutGroup buttonParent)
            {
                var team1Button = new GUIButton(new RectTransform(new Vector2(0.5f, 0.5f), buttonParent.RectTransform), style: "CoalitionButton")
                {
                    OnClicked = (button, obj) =>
                    {
                        TeamPreferenceListBox?.Select(CharacterTeamType.Team1);
                        return true;
                    }
                };

                var team2Button = new GUIButton(new RectTransform(new Vector2(0.5f, 0.5f), buttonParent.RectTransform), style: "SeparatistButton")
                {
                    OnClicked = (button, obj) =>
                    {
                        TeamPreferenceListBox?.Select(CharacterTeamType.Team2);
                        return true;
                    }
                };
            }
        }

        private void UpdatePerkFooterText(GUIListBox box)
        {
            int pointsLeft = GameMain.NetworkMember?.ServerSettings?.DisembarkPointAllowance ?? -1;
            bool ignorePerksThatCantApplyWithoutSub = GameSession.ShouldIgnorePerksThatCanNotApplyWithoutSubmarine(SelectedMode, MissionTypes);

            foreach (GUIComponent child in box.Content.Children)
            {
                if (box.AllSelected.Contains(child) && child.UserData is DisembarkPerkPrefab perkPrefab)
                {
                    if (ignorePerksThatCantApplyWithoutSub && perkPrefab.PerkBehaviors.Any(static b => !b.CanApplyWithoutSubmarine()))
                    {
                        continue;
                    }
                    pointsLeft -= perkPrefab.Cost;
                }
            }

            disembarkPerkFooterText.Text = TextManager.GetWithVariable("disembarkpointleft", "[amount]", pointsLeft.ToString());

            disembarkPerkFooterText.TextColor =
                pointsLeft < 0
                    ? GUIStyle.Red
                    : GUIStyle.TextColorBright;
        }

        public void UpdateDisembarkPointListFromServerSettings()
        {
            if (disembarkPerkSettingList is null || disembarkPerkDisabledDisclaimer is null || disembarkPerkFooterText is null) { return; }

            CharacterTeamType teamPreference = MultiplayerPreferences.Instance.TeamPreference;

            bool hasTeamPreference = teamPreference is (CharacterTeamType.Team1 or CharacterTeamType.Team2);

            if (SelectedMode != GameModePreset.PvP)
            {
                teamPreference = CharacterTeamType.Team1;
                hasTeamPreference = true;
            }

            disembarkPerkDisabledDisclaimer.Visible = !hasTeamPreference;
            disembarkPerkFooterText.Visible = hasTeamPreference;

            SetEnabled(hasTeamPreference);

            bool canManagePerks = ServerSettings.HasPermissionToChangePerks();

            if (!canManagePerks)
            {
                SetEnabled(false);
            }

            isUpdatingPerks = true;

            bool hasAvailablePerks = false;
            if (GameMain.Client?.ServerSettings is { } settings)
            {
                Identifier[] selectedPerks = teamPreference switch
                {
                    CharacterTeamType.Team1 => settings.SelectedCoalitionPerks,
                    CharacterTeamType.Team2 => settings.SelectedSeparatistsPerks,
                    _ => Array.Empty<Identifier>()
                };

                bool ignorePerksThatCantApplyWithoutSub = GameSession.ShouldIgnorePerksThatCanNotApplyWithoutSubmarine(SelectedMode, MissionTypes);
                disembarkPerkSettingList.Deselect();
                foreach (GUIComponent child in disembarkPerkSettingList.Content.Children)
                {
                    if (child.UserData is not DisembarkPerkPrefab perkPrefab) { continue; }
                    bool shouldSelect =  selectedPerks.Contains(perkPrefab.Identifier);

                    bool hasPrerequisite = !perkPrefab.Prerequisite.IsEmpty;
                    bool isMutuallyExclusivePerkSelected = selectedPerks.Any(p => perkPrefab.MutuallyExclusivePerks.Contains(p));
                    TogglePerkElement(enabled: true);

                    if (shouldSelect)
                    {
                        disembarkPerkSettingList.Select(child.UserData, force: GUIListBox.Force.Yes, GUIListBox.AutoScroll.Disabled);
                    }

                    if (hasPrerequisite)
                    {
                        bool enabled = selectedPerks.Contains(perkPrefab.Prerequisite);
                        TogglePerkElement(enabled);
                    }

                    if (isMutuallyExclusivePerkSelected)
                    {
                        TogglePerkElement(enabled: false);
                    }

                    if (ignorePerksThatCantApplyWithoutSub)
                    {
                        if (perkPrefab.PerkBehaviors.Any(static b => !b.CanApplyWithoutSubmarine()))
                        {
                            TogglePerkElement(enabled: false);
                        }
                    }

                    if (child.Enabled)
                    {
                        hasAvailablePerks = true;
                    }

                    void TogglePerkElement(bool enabled)
                    {
                        child.Enabled = enabled;
                        foreach (GUITextBlock text in child.GetAllChildren<GUITextBlock>())
                        {
                            text.Enabled = enabled;
                        }
                    }
                }
            }

            noPerksAvailableDisclaimer.Visible = !hasAvailablePerks;
            if (!hasAvailablePerks)
            {
                disembarkPerkDisabledDisclaimer.Visible = false;
            }

            UpdatePerkFooterText(disembarkPerkSettingList);
            isUpdatingPerks = false;

            void SetEnabled(bool enabled)
            {
                disembarkPerkSettingList.Enabled = enabled;
                foreach (GUIComponent child in disembarkPerkSettingList.Content.Children)
                {
                    //child.Enabled = enabled;
                    foreach (GUITextBlock block in child.GetAllChildren<GUITextBlock>())
                    {
                        block.Enabled = enabled;
                    }
                }
            }
        }

        public static void SelectShuttle(SubmarineInfo info)
        {
            GameMain.Client?.RequestSelectSub(info, SelectedSubType.Shuttle);
        }

        public static GUITextBlock CreateSubHeader(string textTag, GUIComponent parent, string toolTipTag = null)
        {
            var header = new GUITextBlock(new RectTransform(new Vector2(1.0f, 0.055f), parent.RectTransform) { MinSize = new Point(0, GUI.IntScale(28)) },
                TextManager.Get(textTag), font: GUIStyle.SubHeadingFont, textAlignment: Alignment.BottomLeft, textColor: GUIStyle.TextColorBright)
            {
                CanBeFocused = false
            };
            if (!toolTipTag.IsNullOrEmpty())
            {
                header.ToolTip = TextManager.Get(toolTipTag);
            }
            return header;
        }

        public static GUIComponent CreateLabeledSlider(GUIComponent parent, string headerTag, string valueLabelTag, string tooltipTag,
            out GUIScrollBar slider, out GUITextBlock label, float? step = null, Vector2? range = null)
        {
            return CreateLabeledSlider(parent, headerTag, valueLabelTag, tooltipTag, out slider, out label, out GUITextBlock _, step, range);
        }

        public static GUIComponent CreateLabeledSlider(GUIComponent parent, string headerTag, string valueLabelTag, string tooltipTag,
            out GUIScrollBar slider, out GUITextBlock label, out GUITextBlock header, float? step = null, Vector2? range = null)
        {
            GUILayoutGroup verticalLayout = null;
            header = null;
            if (!headerTag.IsNullOrEmpty())
            {
                verticalLayout = new GUILayoutGroup(new RectTransform(new Vector2(1.0f, 0.0f), parent.RectTransform), isHorizontal: false)
                {
                    Stretch = true
                };
                header = new GUITextBlock(new RectTransform(new Vector2(1.0f, 0.5f), verticalLayout.RectTransform),
                    TextManager.Get(headerTag), textAlignment: Alignment.CenterLeft)
                {
                    CanBeFocused = false
                };
                header.RectTransform.MinSize = new Point(0, (int)header.TextSize.Y);
            }

            var container = new GUILayoutGroup(new RectTransform(new Vector2(1.0f, headerTag == null ? 0.0f : 0.5f), (verticalLayout ?? parent).RectTransform), isHorizontal: true)
            {
                Stretch = true,
                RelativeSpacing = 0.05f
            };

            //spacing
            new GUIFrame(new RectTransform(new Point(GUI.IntScale(5), 0), container.RectTransform), style: null);

            slider = new GUIScrollBar(new RectTransform(new Vector2(0.5f, 1.0f), container.RectTransform), barSize: 0.1f, style: "GUISlider");
            if (step.HasValue) { slider.Step = step.Value; }
            if (range.HasValue) { slider.Range = range.Value; }

            container.RectTransform.MinSize = new Point(0, slider.RectTransform.MinSize.Y);
            container.RectTransform.MaxSize = new Point(int.MaxValue, slider.RectTransform.MaxSize.Y);
            verticalLayout?.InheritTotalChildrenMinHeight();            

            label = new GUITextBlock(new RectTransform(new Vector2(0.5f, 1.0f), container.RectTransform, Anchor.CenterRight),
                string.IsNullOrEmpty(valueLabelTag) ? "" : TextManager.Get(valueLabelTag), textAlignment: Alignment.CenterLeft, font: GUIStyle.SmallFont)
            {
                CanBeFocused = false
            };

            //slider has a reference to the label to change the text when it's used
            slider.UserData = label;

            slider.ToolTip = label.ToolTip = TextManager.Get(tooltipTag);
            return verticalLayout ?? container;
        }

        public static GUINumberInput CreateLabeledNumberInput(GUIComponent parent, string labelTag, int min, int max, string toolTipTag = null, GUIFont font = null)
        {
            var container = new GUILayoutGroup(new RectTransform(new Vector2(1.0f, 0.05f), parent.RectTransform), isHorizontal: true)
            {
                Stretch = true,
                RelativeSpacing = 0.05f,
                ToolTip = TextManager.Get(labelTag)
            };

            var label = new GUITextBlock(new RectTransform(new Vector2(0.7f, 1.0f), container.RectTransform),
                TextManager.Get(labelTag), textAlignment: Alignment.CenterLeft, font: font)
            {
                AutoScaleHorizontal = true
            };
            if (!string.IsNullOrEmpty(toolTipTag))
            {
                label.ToolTip = TextManager.Get(toolTipTag);
            }
            var input = new GUINumberInput(new RectTransform(new Vector2(0.3f, 1.0f), container.RectTransform), NumberType.Int)
            {
                MinValueInt = min,
                MaxValueInt = max
            };

            container.RectTransform.MinSize = new Point(0, input.RectTransform.MinSize.Y);
            container.RectTransform.MaxSize = new Point(int.MaxValue, input.RectTransform.MaxSize.Y);

            return input;
        }

        public static GUIDropDown CreateLabeledDropdown(GUIComponent parent, string labelTag, int numElements, string toolTipTag = null)
        {
            var container = new GUILayoutGroup(new RectTransform(new Vector2(1.0f, 0.1f), parent.RectTransform), isHorizontal: true)
            {
                Stretch = true,
                RelativeSpacing = 0.05f,
                ToolTip = TextManager.Get(labelTag)
            };

            var label = new GUITextBlock(new RectTransform(new Vector2(0.7f, 1.0f), container.RectTransform),
                TextManager.Get(labelTag), textAlignment: Alignment.CenterLeft)
            {
                AutoScaleHorizontal = true
            };
            if (!string.IsNullOrEmpty(toolTipTag))
            {
                label.ToolTip = TextManager.Get(toolTipTag);
            }
            var input = new GUIDropDown(new RectTransform(new Vector2(0.3f, 1.0f), container.RectTransform), elementCount: numElements);

            container.RectTransform.MinSize = new Point(0, input.RectTransform.MinSize.Y);
            container.RectTransform.MaxSize = new Point(int.MaxValue, input.RectTransform.MaxSize.Y);

            return input;
        }

        private void CreateSidePanelContents(GUIComponent rightPanel)
        {
            //player info panel ------------------------------------------------------------

            var myCharacterFrame = new GUIFrame(new RectTransform(new Vector2(1.0f, 0.55f), rightPanel.RectTransform), style: null);
            var myCharacterContent = new GUILayoutGroup(new RectTransform(new Vector2(1), myCharacterFrame.RectTransform, Anchor.Center))
            {
                Stretch = true
            };

            spectateBox = new GUITickBox(new RectTransform(new Vector2(0.4f, 0.06f), myCharacterContent.RectTransform),
                TextManager.Get("spectatebutton"))
            {
                Selected = false,
                OnSelected = ToggleSpectate,
                UserData = "spectate"
            };

            playerInfoContent = new GUILayoutGroup(new RectTransform(new Vector2(1.0f, 0.9f), myCharacterContent.RectTransform))
            {
                Stretch = true
            };

            // Social area

            GUIFrame logFrame = new GUIFrame(new RectTransform(new Vector2(1.0f, 0.45f), rightPanel.RectTransform), style: null);
            GUILayoutGroup logContents = new GUILayoutGroup(new RectTransform(Vector2.One, logFrame.RectTransform, Anchor.Center))
            {
                Stretch = true
            };

            GUILayoutGroup socialHolder = null; GUILayoutGroup serverLogHolder = null;

            LogButtons = new GUILayoutGroup(new RectTransform(new Vector2(1.0f, 0.05f), logContents.RectTransform), true)
            {
                Stretch = true,
                RelativeSpacing = 0.02f
            };

            clientHiddenElements.Add(LogButtons);

            // Show chat button
            chatPanelTabButtons.Add(new GUIButton(new RectTransform(new Vector2(0.5f, 1.25f), LogButtons.RectTransform),
               TextManager.Get("Chat"), style: "GUITabButton")
            {
                Selected = true,
                OnClicked = (GUIButton button, object userData) =>
                {
                    if (socialHolder != null) { socialHolder.Visible = true; }
                    if (serverLogHolder != null) { serverLogHolder.Visible = false; }
                    chatPanelTabButtons.ForEach(otherBtn => otherBtn.Selected = otherBtn == button);
                    return true;
                }
            });

            // Server log button
            chatPanelTabButtons.Add(new GUIButton(new RectTransform(new Vector2(0.5f, 1.25f), LogButtons.RectTransform),
                TextManager.Get("ServerLog"), style: "GUITabButton")
            {
                OnClicked = (GUIButton button, object userData) =>
                {
                    if (socialHolder != null) { socialHolder.Visible = false; }
                    if (serverLogHolder is { Visible: false })
                    {
                        if (GameMain.Client?.ServerSettings?.ServerLog == null) { return false; }
                        serverLogHolder.Visible = true;
                        GameMain.Client.ServerSettings.ServerLog.AssignLogFrame(serverLogReverseButton, serverLogBox, serverLogFilterTicks.Content, serverLogFilter);
                    }
                    chatPanelTabButtons.ForEach(otherBtn => otherBtn.Selected = otherBtn == button);
                    return true;
                }
            });

            GUITextBlock.AutoScaleAndNormalize(chatPanelTabButtons.Select(btn => btn.TextBlock));

            GUIFrame logHolderBottom = new GUIFrame(new RectTransform(Vector2.One, logContents.RectTransform), style: null)
            {
                CanBeFocused = false
            };

            socialHolder = new GUILayoutGroup(new RectTransform(Vector2.One, logHolderBottom.RectTransform, Anchor.Center))
            {
                Stretch = true
            };

            // Spacing
            new GUIFrame(new RectTransform(new Vector2(1.0f, 0.02f), socialHolder.RectTransform), style: null)
            {
                CanBeFocused = false
            };

            GUILayoutGroup socialHolderHorizontal = new GUILayoutGroup(new RectTransform(new Vector2(1.0f, 0.9f), socialHolder.RectTransform), isHorizontal: true)
            {
                Stretch = true
            };

            //chatbox ----------------------------------------------------------------------

            chatBox = new GUIListBox(new RectTransform(new Vector2(0.6f, 1.0f), socialHolderHorizontal.RectTransform));

            //player list ------------------------------------------------------------------

            PlayerList = new GUIListBox(new RectTransform(new Vector2(0.4f, 1.0f), socialHolderHorizontal.RectTransform))
            {
                PlaySoundOnSelect = true,
                OnSelected = (component, userdata) => { SelectPlayer(userdata as Client); return true; }
            };

            // Spacing
            new GUIFrame(new RectTransform(new Vector2(1.0f, 0.02f), socialHolder.RectTransform), style: null)
            {
                CanBeFocused = false
            };

            // Chat input
            chatRow = new GUILayoutGroup(new RectTransform(new Vector2(1.0f, 0.07f), socialHolder.RectTransform),
                isHorizontal: true, childAnchor: Anchor.CenterLeft)
            {
                Stretch = true
            };
            RefreshChatrow();

            serverLogHolder = new GUILayoutGroup(new RectTransform(Vector2.One, logHolderBottom.RectTransform, Anchor.Center))
            {
                Stretch = true,
                Visible = false
            };

            // Spacing
            new GUIFrame(new RectTransform(new Vector2(1.0f, 0.02f), serverLogHolder.RectTransform), style: null)
            {
                CanBeFocused = false
            };

            GUILayoutGroup serverLogHolderHorizontal = new GUILayoutGroup(new RectTransform(new Vector2(1.0f, 0.9f), serverLogHolder.RectTransform), isHorizontal: true)
            {
                Stretch = true
            };

            //server log ----------------------------------------------------------------------

            GUILayoutGroup serverLogListboxLayout = new GUILayoutGroup(new RectTransform(new Vector2(0.5f, 1.0f), serverLogHolderHorizontal.RectTransform))
            {
                Stretch = true
            };

            serverLogReverseButton = new GUIButton(new RectTransform(new Vector2(1.0f, 0.05f), serverLogListboxLayout.RectTransform), style: "UIToggleButtonVertical");
            serverLogBox = new GUIListBox(new RectTransform(new Vector2(1.0f, 0.95f), serverLogListboxLayout.RectTransform));

            //filter tickbox list ------------------------------------------------------------------

            serverLogFilterTicks = new GUIListBox(new RectTransform(new Vector2(0.5f, 1.0f), serverLogHolderHorizontal.RectTransform) { MinSize = new Point(150, 0) })
            {
                OnSelected = (component, userdata) => { return false; }
            };

            // Spacing
            new GUIFrame(new RectTransform(new Vector2(1.0f, 0.02f), serverLogHolder.RectTransform), style: null)
            {
                CanBeFocused = false
            };

            // Filter text input

            serverLogFilter = new GUITextBox(new RectTransform(new Vector2(1.0f, 0.07f), serverLogHolder.RectTransform))
            {
                MaxTextLength = ChatMessage.MaxLength,
                Font = GUIStyle.SmallFont
            };

        }

        private void CreateBottomPanelContents(GUIComponent bottomBar)
        {
            //bottom panel ------------------------------------------------------------

            GUILayoutGroup bottomBarLeft = new GUILayoutGroup(new RectTransform(new Vector2(0.3f, 1.0f), bottomBar.RectTransform), childAnchor: Anchor.CenterLeft)
            {
                Stretch = true,
                IsHorizontal = true,
                RelativeSpacing = PanelSpacing
            };
            GUILayoutGroup bottomBarMid = new GUILayoutGroup(new RectTransform(new Vector2(0.4f, 1.0f), bottomBar.RectTransform), childAnchor: Anchor.CenterLeft)
            {
                Stretch = true,
                IsHorizontal = true,
                RelativeSpacing = PanelSpacing
            };
            GUILayoutGroup bottomBarRight = new GUILayoutGroup(new RectTransform(new Vector2(0.3f, 1.0f), bottomBar.RectTransform), childAnchor: Anchor.CenterLeft)
            {
                Stretch = true,
                IsHorizontal = true,
                RelativeSpacing = PanelSpacing
            };

            var disconnectButton = new GUIButton(new RectTransform(new Vector2(0.5f, 1.0f), bottomBarLeft.RectTransform), TextManager.Get("disconnect"))
            {
                OnClicked = (bt, userdata) => { GameMain.QuitToMainMenu(save: false, showVerificationPrompt: true); return true; }
            };
            disconnectButton.TextBlock.AutoScaleHorizontal = true;


            // file transfers  ------------------------------------------------------------
            FileTransferFrame = new GUIFrame(new RectTransform(Vector2.One, bottomBarLeft.RectTransform), style: "TextFrame");
            var fileTransferContent = new GUILayoutGroup(new RectTransform(new Vector2(0.95f, 0.9f), FileTransferFrame.RectTransform, Anchor.Center))
            {
                Stretch = true,
                RelativeSpacing = 0.05f
            };
            FileTransferTitle = new GUITextBlock(new RectTransform(new Vector2(1.0f, 0.5f), fileTransferContent.RectTransform), "", font: GUIStyle.SmallFont);
            var fileTransferBottom = new GUILayoutGroup(new RectTransform(new Vector2(1.0f, 0.5f), fileTransferContent.RectTransform), isHorizontal: true, childAnchor: Anchor.CenterLeft)
            {
                Stretch = true
            };
            FileTransferProgressBar = new GUIProgressBar(new RectTransform(new Vector2(0.6f, 1.0f), fileTransferBottom.RectTransform), 0.0f, Color.DarkGreen);
            FileTransferProgressText = new GUITextBlock(new RectTransform(Vector2.One, FileTransferProgressBar.RectTransform), "",
                font: GUIStyle.SmallFont, textAlignment: Alignment.CenterLeft);
            new GUIButton(new RectTransform(new Vector2(0.4f, 1.0f), fileTransferBottom.RectTransform), TextManager.Get("cancel"), style: "GUIButtonSmall")
            {
                OnClicked = (btn, userdata) =>
                {
                    if (FileTransferFrame.UserData is not FileReceiver.FileTransferIn transfer) { return false; }
                    GameMain.Client?.CancelFileTransfer(transfer);
                    GameMain.Client?.FileReceiver.StopTransfer(transfer);
                    return true;
                }
            };


            roundControlsHolder = new GUILayoutGroup(new RectTransform(Vector2.One, bottomBarRight.RectTransform),
                isHorizontal: true, childAnchor: Anchor.CenterLeft)
            {
                Stretch = true
            };

            GUIFrame readyToStartContainer = new GUIFrame(new RectTransform(Vector2.One, roundControlsHolder.RectTransform), style: "TextFrame")
            {
                Visible = false
            };

            // Ready to start tickbox
            ReadyToStartBox = new GUITickBox(new RectTransform(new Vector2(0.95f, 0.75f), readyToStartContainer.RectTransform, anchor: Anchor.Center),
                TextManager.Get("ReadyToStartTickBox"));

            joinOnGoingRoundButton = new GUIButton(new RectTransform(Vector2.One, roundControlsHolder.RectTransform),
                TextManager.Get("ServerListJoin"));

            // Start button
            StartButton = new GUIButton(new RectTransform(Vector2.One, roundControlsHolder.RectTransform),
                TextManager.Get("StartGameButton"))
            {
                OnClicked = (btn, obj) =>
                {
                    if (GameMain.Client == null) { return true; }
                    if (CampaignSetupFrame.Visible && CampaignSetupUI != null)
                    {
                        CampaignSetupUI.StartGameClicked(btn, obj);
                    }
                    else
                    {
                        //if a campaign is active, and we're not setting one up atm, start button continues the existing campaign
                        GameMain.Client.RequestStartRound(continueCampaign: GameMain.GameSession?.GameMode is CampaignMode && CampaignSetupFrame is not { Visible: true });
                        CoroutineManager.StartCoroutine(WaitForStartRound(StartButton), "WaitForStartRound");
                    }
                    return true;
                }
            };
            clientHiddenElements.Add(StartButton);
            bottomBar.RectTransform.MinSize =
                new Point(0, (int)Math.Max(ReadyToStartBox.RectTransform.MinSize.Y / 0.75f, StartButton.RectTransform.MinSize.Y));

            //autorestart ------------------------------------------------------------------

            autoRestartText = new GUITextBlock(new RectTransform(Vector2.One, bottomBarMid.RectTransform), "", font: GUIStyle.SmallFont, style: "TextFrame", textAlignment: Alignment.Center);
            GUIFrame autoRestartBoxContainer = new GUIFrame(new RectTransform(Vector2.One, bottomBarMid.RectTransform), style: "TextFrame");
            autoRestartBox = new GUITickBox(new RectTransform(new Vector2(0.95f, 0.75f), autoRestartBoxContainer.RectTransform, Anchor.Center), TextManager.Get("AutoRestart"))
            {
                OnSelected = (tickBox) =>
                {
                    GameMain.Client?.ServerSettings.ClientAdminWrite(ServerSettings.NetFlags.Properties);
                    return true;
                }
            };
            clientDisabledElements.Add(autoRestartBox);
            AssignComponentToServerSetting(autoRestartBox, nameof(ServerSettings.AutoRestart));

        }

        public void StopWaitingForStartRound()
        {
            CoroutineManager.StopCoroutines("WaitForStartRound");
            if (StartButton != null)
            {
                StartButton.Enabled = true;
            }
            GUI.ClearCursorWait();
        }

        public const string PleaseWaitPopupUserData = "PleaseWaitPopup";

        public static IEnumerable<CoroutineStatus> WaitForStartRound(GUIButton startButton)
        {
            GUI.SetCursorWaiting();
            LocalizedString headerText = TextManager.Get("RoundStartingPleaseWait");
            var msgBox = new GUIMessageBox(headerText, TextManager.Get("RoundStarting"), Array.Empty<LocalizedString>())
            {
                UserData = PleaseWaitPopupUserData
            };

            if (startButton != null)
            {
                startButton.Enabled = false;
            }

            DateTime timeOut = DateTime.Now + new TimeSpan(0, 0, 10);
            while (Selected == GameMain.NetLobbyScreen && DateTime.Now < timeOut)
            {
                msgBox.Header.Text = headerText + new string('.', ((int)Timing.TotalTime % 3 + 1));
                yield return CoroutineStatus.Running;
            }

            msgBox.Close();
            if (startButton != null)
            {
                startButton.Enabled = true;
            }
            GUI.ClearCursorWait();
            yield return CoroutineStatus.Success;
        }

        public override void Deselect()
        {
            GameMain.Client?.OnPermissionChanged.TryDeregister(nameof(CreateDisembarkPointPanel).ToIdentifier());
            SaveAppearance();
            chatInput.Deselect();
            CampaignCharacterDiscarded = false;
            
            CharacterAppearanceCustomizationMenu?.Dispose();
            JobSelectionFrame = null;
        }

        public override void Select()
        {
            if (GameMain.NetworkMember == null) { return; }

            visibilityMenuOrder.Clear();
            
            CharacterAppearanceCustomizationMenu?.Dispose();
            JobSelectionFrame = null;

            Character.Controlled = null;
            GameMain.LightManager.LosEnabled = false;
            GUI.PreventPauseMenuToggle = false;
            CampaignCharacterDiscarded = false;

            changesPendingText?.Parent?.RemoveChild(changesPendingText);
            changesPendingText = null;
            
            RefreshChatrow();

            //disable/hide elements the clients are not supposed to use/see
            clientDisabledElements.ForEach(c => c.Enabled = false);
            clientHiddenElements.ForEach(c => c.Visible = false);

            RefreshEnabledElements();

            if (GameMain.Client != null)
            {
                joinOnGoingRoundButton.Visible = GameMain.Client.GameStarted;
                ReadyToStartBox.Selected = false;
                GameMain.Client.SetReadyToStart(ReadyToStartBox);
            }
            else
            {
                joinOnGoingRoundButton.Visible = false;
            }
            SetSpectate(spectateBox.Selected);

            if (GameMain.Client != null)
            {
                GameMain.Client.Voting.ResetVotes(GameMain.Client.ConnectedClients);
                joinOnGoingRoundButton.OnClicked = GameMain.Client.JoinOnGoingClicked;
                ReadyToStartBox.OnSelected = GameMain.Client.SetReadyToStart;
            }

            roundControlsHolder.Children.ForEach(c => c.IgnoreLayoutGroups = !c.Visible);
            roundControlsHolder.Recalculate();

            AssignComponentsToServerSettings();

            RefreshPlaystyleIcons();

            base.Select();
        }

        public void RefreshEnabledElements()
        {
            if (GameMain.Client == null) { return; }
            var client = GameMain.Client;
            var settings = client.ServerSettings;
            bool manageSettings = HasPermission(ClientPermissions.ManageSettings);
            bool campaignSelected = CampaignFrame.Visible || CampaignSetupFrame.Visible;
            bool campaignStarted = CampaignFrame.Visible;
            bool gameStarted = client != null && client.GameStarted;

            // First, enable or disable elements based on client permissions
            foreach (var element in clientDisabledElements)
            {
                element.Enabled = manageSettings;
            }
            
            // Then disable elements depending on other conditions
            traitorElements.ForEach(e => e.Enabled &= settings.TraitorProbability > 0);
            SetTraitorDangerIndicators(settings.TraitorDangerLevel);
            respawnModeSelection.Enabled = respawnModeLabel.Enabled = manageSettings && !gameStarted;
            midRoundRespawnSettings.ForEach(e => e.Enabled &= settings.RespawnMode == RespawnMode.MidRound);
            permadeathDisabledRespawnSettings.ForEach(e => e.Enabled &= settings.RespawnMode != RespawnMode.Permadeath);
            permadeathEnabledRespawnSettings.ForEach(e => e.Enabled &= settings.RespawnMode == RespawnMode.Permadeath && !gameStarted);
            ironmanDisabledRespawnSettings.ForEach(e => e.Enabled &= !settings.IronmanMode);

            // The respawn interval is used even if the shuttle is not
            respawnIntervalElement.GetAllChildren().ForEach(e => e.Enabled = settings.RespawnMode != RespawnMode.BetweenRounds && manageSettings);

            //go through the individual elements that are only enabled in a specific context
            shuttleTickBox.Enabled &= !gameStarted;
            if (ShuttleList != null)
            {
                // Shuttle list depends on shuttle tickbox
                ShuttleList.Enabled &= shuttleTickBox.Enabled && HasPermission(ClientPermissions.SelectSub);
                ShuttleList.ButtonEnabled = ShuttleList.Enabled;
            }
            if (SubList != null)
            {
                SubList.Enabled = !campaignStarted && (settings.AllowSubVoting || HasPermission(ClientPermissions.SelectSub));
            }
            if (ModeList != null)
            {
                ModeList.Enabled = !gameStarted && (settings.AllowModeVoting || HasPermission(ClientPermissions.SelectMode));
            }

            RefreshStartButtonVisibility();

            botSettingsElements.ForEach(b => b.Enabled = !campaignStarted && manageSettings);

            campaignDisabledElements.ForEach(e => e.Enabled = !campaignSelected && manageSettings);
            levelDifficultySlider.ToolTip = levelDifficultySlider.Enabled ? string.Empty : TextManager.Get("campaigndifficultydisabled");

            //hide elements the client shouldn't
            foreach (var element in clientHiddenElements)
            {
                element.Visible = manageSettings;
            }
            //go through the individual elements that are only visible in a specific context
            ReadyToStartBox.Parent.Visible = !gameStarted;
            LogButtons.Visible = HasPermission(ClientPermissions.ServerLog);

            client?.UpdateLogButtonPermissions();
            roundControlsHolder.Children.ForEach(c => c.IgnoreLayoutGroups = !c.Visible);
            roundControlsHolder.Children.ForEach(c => c.RectTransform.RelativeSize = Vector2.One);
            roundControlsHolder.Recalculate();

            SettingsButton.OnClicked = settings.ToggleSettingsFrame;

            RefreshGameModeContent();

            static bool HasPermission(ClientPermissions permissions)
            {
                if (GameMain.Client == null) { return false; }
                return GameMain.Client.HasPermission(permissions);
            }
        }

        public void ShowSpectateButton()
        {
            if (GameMain.Client == null) { return; }
            joinOnGoingRoundButton.Visible = true;
            joinOnGoingRoundButton.Enabled = true;
            StartButton.Visible = false;
        }

        public void SetCampaignCharacterInfo(CharacterInfo newCampaignCharacterInfo)
        {
            if (newCampaignCharacterInfo != null)
            {
                if (CampaignCharacterDiscarded) { return; }
                if (campaignCharacterInfo != newCampaignCharacterInfo)
                {
                    campaignCharacterInfo = newCampaignCharacterInfo;
                    UpdatePlayerFrame(campaignCharacterInfo, false);
                }
            }
            else if (campaignCharacterInfo != null)
            {
                campaignCharacterInfo = null;
                UpdatePlayerFrame(null, false);
            }
        }

        private void UpdatePlayerFrame(CharacterInfo characterInfo, bool allowEditing = true)
        {
            UpdatePlayerFrame(characterInfo, allowEditing, playerInfoContent);
        }

        public void CreatePlayerFrame(GUIComponent parent, bool createPendingText = true, bool alwaysAllowEditing = false)
        {
            if (GameMain.Client == null) { return; }
            UpdatePlayerFrame(
                Character.Controlled?.Info ?? playerInfoContent.UserData as CharacterInfo ?? GameMain.Client.CharacterInfo,
                allowEditing: alwaysAllowEditing || campaignCharacterInfo == null,
                parent: parent,
                createPendingText: createPendingText);
        }

        private void UpdatePlayerFrame(CharacterInfo characterInfo, bool allowEditing, GUIComponent parent, bool createPendingText = true)
        {
            if (GameMain.Client == null) { return; }
            
            // When permanently dead and still characterless, spectating is the only option
            spectateBox.Enabled = !PermanentlyDead;
            
            createPendingChangesText = createPendingText;
            if (characterInfo == null || CampaignCharacterDiscarded)
            {
                characterInfo = new CharacterInfo(CharacterPrefab.HumanSpeciesName, GameMain.Client.Name, null);
                characterInfo.RecreateHead(MultiplayerPreferences.Instance);
                GameMain.Client.CharacterInfo = characterInfo;
                characterInfo.OmitJobInMenus = true;
            }

            parent.ClearChildren();

            bool isGameRunning = GameMain.GameSession?.IsRunning ?? false;

            parent.ClearChildren();
            parent.UserData = characterInfo;

            bool nameChangePending = isGameRunning && GameMain.Client.PendingName != string.Empty && GameMain.Client?.Character?.Name != GameMain.Client.PendingName;
            changesPendingText?.Parent?.RemoveChild(changesPendingText);
            changesPendingText = null;

            if (TabMenu.PendingChanges)
            {
                CreateChangesPendingText();
            }

            CharacterNameBox = new GUITextBox(new RectTransform(new Vector2(1.0f, 0.065f), parent.RectTransform), !nameChangePending ? characterInfo.Name : GameMain.Client.PendingName, textAlignment: Alignment.Center)
            {
                MaxTextLength = Client.MaxNameLength,
                OverflowClip = true
            };
            
            if (!allowEditing ||
                (PermanentlyDead && !characterInfo.RenamingEnabled))
            {
                CharacterNameBox.Readonly = true;
                CharacterNameBox.Enabled = false;
            }
            else
            {
                CharacterNameBox.OnEnterPressed += (tb, text) =>
                {
                    CharacterNameBox.Deselect();
                    return true;
                };
                CharacterNameBox.OnDeselected += (tb, key) =>
                {
                    if (GameMain.Client == null)
                    {
                        return;
                    }
                    
                    string newName = Client.SanitizeName(tb.Text);
                    if (newName == GameMain.Client.Name) { return; }
                    if (string.IsNullOrWhiteSpace(newName))
                    {
                        tb.Text = GameMain.Client.Name;
                    }
                    else
                    {
                        if (isGameRunning)
                        {
                            GameMain.Client.PendingName = tb.Text;
                            TabMenu.PendingChanges = true;
                            if (createPendingText)
                            {
                                CreateChangesPendingText();
                            }
                        }
                        else
                        {
                            ReadyToStartBox.Selected = false;
                        }
                        
                        GameMain.Client.SetName(tb.Text);
                    }
                };
            }
            
            //spacing
            new GUIFrame(new RectTransform(new Vector2(1.0f, 0.006f), parent.RectTransform), style: null);
            
            if (allowEditing && (!PermadeathMode || !isGameRunning))
            {
                GUILayoutGroup characterInfoTabs = new GUILayoutGroup(new RectTransform(new Vector2(1.0f, 0.07f), parent.RectTransform), isHorizontal: true)
                {
                    Stretch = true,
                    RelativeSpacing = 0.02f
                };

                jobPreferencesButton = new GUIButton(new RectTransform(new Vector2(0.5f, 1f), characterInfoTabs.RectTransform),
                    TextManager.Get("JobPreferences"), style: "GUITabButton")
                {
                    Selected = true,
                    OnClicked = SelectJobPreferencesTab
                };
                appearanceButton = new GUIButton(new RectTransform(new Vector2(0.5f, 1f), characterInfoTabs.RectTransform),
                    TextManager.Get("CharacterAppearance"), style: "GUITabButton")
                {
                    OnClicked = SelectAppearanceTab
                };

                GUITextBlock.AutoScaleAndNormalize(jobPreferencesButton.TextBlock, appearanceButton.TextBlock);

                // Unsubscribe from previous events, not even sure if this matters here but it doesn't hurt so why not
                if (characterInfoFrame != null) { characterInfoFrame.RectTransform.SizeChanged -= RecalculateSubDescription; }
                characterInfoFrame = new GUIFrame(new RectTransform(Vector2.One, parent.RectTransform), style: null);
                characterInfoFrame.RectTransform.SizeChanged += RecalculateSubDescription;

                JobPreferenceContainer = new GUIFrame(new RectTransform(Vector2.One, characterInfoFrame.RectTransform),
                    style: "GUIFrameListBox");
                characterInfo.CreateIcon(new RectTransform(new Vector2(1.0f, 0.4f), JobPreferenceContainer.RectTransform, Anchor.TopCenter) { RelativeOffset = new Vector2(0f, 0.025f) });
                JobList = new GUIListBox(new RectTransform(new Vector2(1.0f, 0.6f), JobPreferenceContainer.RectTransform, Anchor.BottomCenter), true)
                {
                    Enabled = true,
                    PlaySoundOnSelect = true,
                    OnSelected = (child, obj) =>
                    {
                        if (child.IsParentOf(GUI.MouseOn)) { return false; }
                        return OpenJobSelection(child, obj);
                    }
                };

                for (int i = 0; i < 3; i++)
                {
                    JobVariant jobPrefab = null;
                    while (i < MultiplayerPreferences.Instance.JobPreferences.Count)
                    {
                        var jobPreference = MultiplayerPreferences.Instance.JobPreferences[i];
                        if (!JobPrefab.Prefabs.TryGet(jobPreference.JobIdentifier, out JobPrefab prefab) || prefab.HiddenJob)
                        {
                            MultiplayerPreferences.Instance.JobPreferences.RemoveAt(i);
                            continue;
                        }
                        // The old job variant system used one-based indexing
                        // so let's make sure no one get to pick a variant which doesn't exist
                        int variant = Math.Min(jobPreference.Variant, prefab.Variants - 1);
                        jobPrefab = new JobVariant(prefab, variant);
                        break;
                    }

                    var slot = new GUIFrame(new RectTransform(new Vector2(0.333f, 1.0f), JobList.Content.RectTransform), style: "ListBoxElementSquare")
                    {
                        CanBeFocused = true,
                        UserData = jobPrefab
                    };
                }

                UpdateJobPreferences(characterInfo);

                appearanceFrame = new GUIFrame(new RectTransform(Vector2.One, characterInfoFrame.RectTransform), style: "GUIFrameListBox")
                {
                    Visible = false,
                    Color = Color.White
                };
            }
            else
            {
                characterInfo.CreateIcon(new RectTransform(new Vector2(1.0f, 0.16f), parent.RectTransform, Anchor.TopCenter));

                if (PermanentlyDead)
                {
                    new GUITextBlock(
                        new RectTransform(new Vector2(1.0f, 0.0f), parent.RectTransform),
                        TextManager.Get("deceased"),
                        textAlignment: Alignment.Center, font: GUIStyle.LargeFont);
                    
                    if (GameMain.Client?.ServerSettings is { IronmanModeActive: true })
                    {
                        new GUITextBlock(
                            new RectTransform(new Vector2(1.0f, 0.0f), parent.RectTransform),
                            TextManager.Get("lobby.ironmaninfo"),
                            textAlignment: Alignment.Center, wrap: true);
                    }
                    else
                    {
                        new GUITextBlock(
                            new RectTransform(new Vector2(1.0f, 0.0f), parent.RectTransform),
                            TextManager.Get("lobby.permadeathinfo"),
                            textAlignment: Alignment.Center, wrap: true);
                        new GUITextBlock(
                            new RectTransform(new Vector2(1.0f, 0.0f), parent.RectTransform),
                            TextManager.Get("lobby.permadeathoptionsexplanation"),
                            textAlignment: Alignment.Center, wrap: true);
                    }
                }
                else
                {
                    new GUITextBlock(new RectTransform(new Vector2(1.0f, 0.0f), parent.RectTransform), characterInfo.Job.Name, textAlignment: Alignment.Center, font: GUIStyle.SubHeadingFont, wrap: true)
                    {
                        HoverColor = Color.Transparent,
                        SelectedColor = Color.Transparent
                    };
                    
                    new GUITextBlock(new RectTransform(new Vector2(1.0f, 0.0f), parent.RectTransform), TextManager.Get("Skills"), font: GUIStyle.SubHeadingFont);
                    foreach (Skill skill in characterInfo.Job.GetSkills())
                    {
                        Color textColor = Color.White * (0.5f + skill.Level / 200.0f);
                        var skillText = new GUITextBlock(new RectTransform(new Vector2(1.0f, 0.0f), parent.RectTransform),
                            "  - " + TextManager.AddPunctuation(':', TextManager.Get("SkillName." + skill.Identifier), ((int)skill.Level).ToString()),
                            textColor,
                            font: GUIStyle.SmallFont);
                    }
                }

                // Spacing
                new GUIFrame(new RectTransform(new Vector2(1.0f, 0.15f), parent.RectTransform), style: null);

                if (GameMain.Client?.ServerSettings?.RespawnMode != RespawnMode.Permadeath)
                {
                    // Button to create new character
                    new GUIButton(new RectTransform(new Vector2(0.8f, 0.1f), parent.RectTransform, Anchor.BottomCenter), TextManager.Get("CreateNew"))
                    {
                        IgnoreLayoutGroups = true,
                        OnClicked = (btn, userdata) =>
                        {
                            TryDiscardCampaignCharacter(() =>
                            {
                                UpdatePlayerFrame(null, true, parent);
                            });
                            return true;
                        }
                    };
                }
            }

            TeamPreferenceListBox = null;
            if (SelectedMode == GameModePreset.PvP)
            {
                TeamPreferenceListBox = new GUIListBox(new RectTransform(new Vector2(1.0f, 0.04f), parent.RectTransform, anchor: Anchor.TopLeft, pivot: Pivot.TopLeft), isHorizontal: true, style: null)
                {
                    Enabled = true,
                    KeepSpaceForScrollBar = false,
                    PlaySoundOnSelect = true,
                    ScrollBarEnabled = false,
                    ScrollBarVisible = false
                };
                TeamPreferenceListBox.RectTransform.MinSize = new Point(0, GUI.IntScale(30));
                TeamPreferenceListBox.UpdateDimensions();

                Color team1Color = new Color(0, 110, 150, 255);
                pvpTeamChoiceTeam1 = new GUITextBlock(new RectTransform(new Vector2(0.3f, 1.0f), TeamPreferenceListBox.Content.RectTransform), TextManager.Get("teampreference.team1"), textAlignment: Alignment.Center, style: null)
                {
                    UserData = CharacterTeamType.Team1,
                    CanBeFocused = true,
                    Padding = Vector4.One * 10.0f * GUI.Scale,
                    Color = Color.Lerp(team1Color, Color.Black, 0.7f) * 0.7f,
                    HoverColor = team1Color * 0.95f,
                    SelectedColor = team1Color * 0.8f,
                    OutlineColor = team1Color,
                    TextColor = Color.White,
                    HoverTextColor = Color.White,
                    SelectedTextColor = Color.White,
                    DisabledColor = team1Color * 0.25f,
                    DisabledTextColor = Color.Gray,
                };

                Color noPreferenceColor = new Color(100, 100, 100, 255);
                pvpTeamChoiceMiddleButton = new GUITextBlock(new RectTransform(new Vector2(0.4f, 1.0f), TeamPreferenceListBox.Content.RectTransform), "", textAlignment: Alignment.Center, style: null)
                {
                    UserData = CharacterTeamType.None,
                    CanBeFocused = true,
                    Padding = Vector4.One * 10.0f * GUI.Scale,
                    Color = Color.Lerp(noPreferenceColor, Color.Black, 0.7f) * 0.7f,
                    HoverColor = noPreferenceColor * 0.95f,
                    SelectedColor = noPreferenceColor * 0.8f,
                    OutlineColor = noPreferenceColor,
                    TextColor = Color.White,
                    HoverTextColor = Color.White,
                    SelectedTextColor = Color.White,
                };

                Color team2Color = new Color(150, 110, 0, 255);
                pvpTeamChoiceTeam2 = new GUITextBlock(new RectTransform(new Vector2(0.3f, 1.0f), TeamPreferenceListBox.Content.RectTransform), TextManager.Get("teampreference.team2"), textAlignment: Alignment.Center, style: null)
                {
                    UserData = CharacterTeamType.Team2,
                    CanBeFocused = true,
                    Padding = Vector4.One * 10.0f * GUI.Scale,
                    Color = Color.Lerp(team2Color, Color.Black, 0.7f) * 0.7f,
                    HoverColor = team2Color * 0.95f,
                    SelectedColor = team2Color * 0.8f,
                    OutlineColor = team2Color,
                    TextColor = Color.White,
                    HoverTextColor = Color.White,
                    SelectedTextColor = Color.White,
                    DisabledColor = team2Color * 0.25f,
                    DisabledTextColor = Color.Gray,
                };

                var prevTeamSelection = MultiplayerPreferences.Instance.TeamPreference;

                ResetPvpTeamSelection();

                // Handle special case: middle button in Player Choice mode should pick a random team, if possible
                TeamPreferenceListBox.OnSelected += (component, obj) =>
                {
                    CharacterTeamType newTeamPreference = (CharacterTeamType)obj;
                    if (newTeamPreference == CharacterTeamType.None
                        && GameMain.Client?.ServerSettings?.PvpTeamSelectionMode == PvpTeamSelectionMode.PlayerChoice)
                    {
                        TeamPreferenceListBox.Select(Rand.Value() < 0.5 ? CharacterTeamType.Team1 : CharacterTeamType.Team2);
                        var teamColor = (CharacterTeamType)TeamPreferenceListBox.SelectedData == CharacterTeamType.Team1 ? team1Color : team2Color;
                        TeamPreferenceListBox.SelectedComponent.Flash(teamColor, useRectangleFlash: true, flashDuration: 1.0f);
                        return true;
                    }
                    return false; // Allow the next delegate to handle other cases
                };
                
                // Handle everything else
                TeamPreferenceListBox.OnSelected += (component, obj) =>
                {
                    CharacterTeamType newTeamPreference = (CharacterTeamType)obj;
                    
                    if (newTeamPreference == CharacterTeamType.None
                        && GameMain.Client?.ServerSettings?.PvpTeamSelectionMode == PvpTeamSelectionMode.PlayerChoice) { return false; } // Already handled by delegate above 

                    var oldPreference = MultiplayerPreferences.Instance.TeamPreference;

                    MultiplayerPreferences.Instance.TeamPreference = newTeamPreference;
                    
                    UpdateSelectedSub(newTeamPreference);
                    if (newTeamPreference != oldPreference)
                    {
                        GameMain.Client?.ForceNameJobTeamUpdate();
                        GameSettings.SaveCurrentConfig();
                    }
                    RefreshPvpTeamSelectionButtons();
                    UpdateDisembarkPointListFromServerSettings();
                    //need to update job preferences and close the selection frame
                    //because the team selection might affect the uniform sprite and the loadouts
                    UpdateJobPreferences(GameMain.Client?.CharacterInfo ?? Character.Controlled?.Info);
                    JobSelectionFrame = null;
                    RefreshChatrow(); // to enable/disable team chat according to current selection

                    return true;
                };

                if (prevTeamSelection != CharacterTeamType.None)
                {
                    TeamPreferenceListBox.Select(prevTeamSelection);
                }
            }
        }

        public void UpdateSelectedSub(CharacterTeamType preference)
        {
            bool votingEnabled = GameMain.NetworkMember.ServerSettings.SubSelectionMode == SelectionMode.Vote;
            SubList.OnSelected -= VotableClicked;
            switch (preference)
            {
                case CharacterTeamType.Team1 or CharacterTeamType.None when SelectedSub is { } selectedSub:
                    TrySelectSub(selectedSub.Name, selectedSub.MD5Hash.StringRepresentation, SelectedSubType.Sub, SubList, showPreview: false);
                    if (!votingEnabled) { SubList.Select(selectedSub, autoScroll: GUIListBox.AutoScroll.Disabled); }
                    break;
                case CharacterTeamType.Team2 when SelectedEnemySub is { } selectedEnemySub:
                    TrySelectSub(selectedEnemySub.Name, selectedEnemySub.MD5Hash.StringRepresentation, SelectedSubType.EnemySub, SubList, showPreview: false);
                    if (!votingEnabled) { SubList.Select(selectedEnemySub, autoScroll: GUIListBox.AutoScroll.Disabled); }
                    break;
            }
            SubList.OnSelected += VotableClicked;
        }

        public void TryDiscardCampaignCharacter(Action onYes)
        {
            var confirmation = new GUIMessageBox(TextManager.Get("NewCampaignCharacterHeader"), TextManager.Get("NewCampaignCharacterText"),
                new[] { TextManager.Get("Yes"), TextManager.Get("No") });
            confirmation.Buttons[0].OnClicked += confirmation.Close;
            confirmation.Buttons[0].OnClicked += (btn2, userdata2) =>
            {
                CampaignCharacterDiscarded = true;
                campaignCharacterInfo = null;
                onYes();
                return true;
            };
            confirmation.Buttons[1].OnClicked += confirmation.Close;
        }

        private void CreateChangesPendingText()
        {
            if (!createPendingChangesText || changesPendingText != null || playerInfoContent == null) { return; }

            //remove the previous one
            changesPendingText?.Parent?.RemoveChild(changesPendingText);

            changesPendingText = new GUIFrame(new RectTransform(new Vector2(1.0f, 0.065f), playerInfoContent.RectTransform, Anchor.BottomCenter, Pivot.TopCenter) { RelativeOffset = new Vector2(0f, -0.03f) },
                style: "OuterGlow")
            {
                Color = Color.Black,
                IgnoreLayoutGroups = true
            };
            var text = new GUITextBlock(new RectTransform(Vector2.One, changesPendingText.RectTransform, Anchor.Center),
                TextManager.Get("tabmenu.characterchangespending"), textColor: GUIStyle.Orange, textAlignment: Alignment.Center, style: null);
            changesPendingText.RectTransform.MinSize = new Point((int)(text.TextSize.X * 1.2f), (int)(text.TextSize.Y * 2.0f));
        }

        public static void CreateChangesPendingFrame(GUIComponent parent)
        {
            parent.ClearChildren();
            GUIFrame changesPendingFrame = new GUIFrame(new RectTransform(Vector2.One, parent.RectTransform, Anchor.Center),
                style: "OuterGlow")
            {
                Color = Color.Black
            };
            new GUITextBlock(new RectTransform(Vector2.One, changesPendingFrame.RectTransform, Anchor.Center),
                TextManager.Get("tabmenu.characterchangespending"), textColor: GUIStyle.Orange, textAlignment: Alignment.Center, style: null)
            {
                AutoScaleHorizontal = true
            };
        }

        private void CreateJobVariantTooltip(JobPrefab jobPrefab, CharacterTeamType team, int variant, bool isPvPMode, GUIComponent parentSlot)
        {
            jobVariantTooltip = new GUIFrame(new RectTransform(new Point((int)(400 * GUI.Scale), (int)(180 * GUI.Scale)), GUI.Canvas, pivot: Pivot.BottomRight),
                style: "GUIToolTip")
            {
                UserData = new JobVariant(jobPrefab, variant)
            };
            jobVariantTooltip.RectTransform.AbsoluteOffset = new Point(parentSlot.Rect.Right, parentSlot.Rect.Y);

            var content = new GUILayoutGroup(new RectTransform(new Vector2(0.95f, 0.95f), jobVariantTooltip.RectTransform, Anchor.Center))
            {
                Stretch = true,
                AbsoluteSpacing = (int)(15 * GUI.Scale)
            };
            
            new GUITextBlock(new RectTransform(new Vector2(1.0f, 0.0f), content.RectTransform), TextManager.GetWithVariable("startingequipmentname", "[number]", (variant + 1).ToString()), font: GUIStyle.SubHeadingFont, textAlignment: Alignment.Center);

            var itemIdentifiers = jobPrefab.JobItems[variant]
                .Where(it => it.ShowPreview)
                .Select(it => it.GetItemIdentifier(team, isPvPMode))
                .Distinct();

            int itemsPerRow = 5;
            int rows = (int)Math.Max(Math.Ceiling(itemIdentifiers.Count() / (float)itemsPerRow), 1);

            new GUICustomComponent(new RectTransform(new Vector2(1.0f, 0.4f * rows), content.RectTransform, Anchor.BottomCenter),
                onDraw: (sb, component) => { DrawJobVariantItems(sb, component, new JobVariant(jobPrefab, variant), team, isPvPMode, itemsPerRow); });

            jobVariantTooltip.RectTransform.MinSize = new Point(0, content.RectTransform.Children.Sum(c => c.Rect.Height + content.AbsoluteSpacing));
        }

        private void SetTraitorDangerIndicators(int dangerLevel)
        {
            int i = 0;
            foreach (var child in traitorDangerGroup.Children)
            {
                child.Enabled = i < dangerLevel && GameMain.Client?.ServerSettings is { TraitorProbability: > 0 };
                i++;
            }
        }

        public bool ToggleSpectate(GUITickBox tickBox)
        {
            SetSpectate(tickBox.Selected);
            return false;
        }

        public void SetSpectate(bool spectate)
        {
            if (GameMain.Client == null) { return; }
            spectateBox.Selected = spectate;
            
            if (spectate)
            {
                GameMain.Client.CharacterInfo?.Remove();
                GameMain.Client.CharacterInfo = null;
                // TODO: The following lines are ancient, unexplained, and they cause a client spectating because of permadeath
                //       to get kicked from the server at round transition because the server expects to be in control of
                //       removing Characters and the client to still have one. Commenting these lines out for now, but
                //       if no side-effects occur, they can just be deleted.
                //GameMain.Client.Character?.Remove();
                //GameMain.Client.Character = null;

                playerInfoContent.ClearChildren();

                new GUITextBlock(new RectTransform(Vector2.One, playerInfoContent.RectTransform, Anchor.Center),
                    TextManager.Get("PlayingAsSpectator"),
                    textAlignment: Alignment.Center);
                
                if (SelectedMode == GameModePreset.PvP)
                {
                    // In PvP mode, becoming a spectator should reset any existing team selection
                    ResetPvpTeamSelection();
                }
            }
            else
            {
                UpdatePlayerFrame(campaignCharacterInfo, allowEditing: campaignCharacterInfo == null);
            }
        }
        
        public void RefreshPvpTeamSelectionButtons()
        {
            if (pvpTeamChoiceMiddleButton == null || pvpTeamChoiceTeam1 == null || pvpTeamChoiceTeam2 == null)
            {
                return;
            }
            
            ServerSettings serverSettings = GameMain.Client.ServerSettings;
            
            CharacterTeamType currentTeam = MultiplayerPreferences.Instance.TeamPreference;
            bool pvpPlayerChoiceMode = serverSettings.PvpTeamSelectionMode == PvpTeamSelectionMode.PlayerChoice;
            
            pvpTeamChoiceMiddleButton.Text = TextManager.Get(pvpPlayerChoiceMode ? "PvP.PickRandom" : "teampreference.nopreference");
            if (pvpPlayerChoiceMode && serverSettings.PvpAutoBalanceThreshold > 0)
            {
                pvpTeamChoiceTeam1.Enabled = currentTeam == CharacterTeamType.Team1 || CanJoinTeam1();
                pvpTeamChoiceTeam2.Enabled = currentTeam == CharacterTeamType.Team2 || CanJoinTeam2();
                pvpTeamChoiceTeam1.ToolTip = !pvpTeamChoiceTeam1.Enabled ? TextManager.Get("PvP.TeamDisabledBecauseBalance") : null;
                pvpTeamChoiceTeam2.ToolTip = !pvpTeamChoiceTeam2.Enabled ? TextManager.Get("PvP.TeamDisabledBecauseBalance") : null;
                pvpTeamChoiceMiddleButton.Enabled = CanJoinTeam1() && CanJoinTeam2();
            }
            else
            {
                pvpTeamChoiceTeam1.Enabled = true;
                pvpTeamChoiceTeam2.Enabled = true;
                pvpTeamChoiceTeam1.ToolTip = null;
                pvpTeamChoiceTeam2.ToolTip = null;
                pvpTeamChoiceMiddleButton.Enabled = true;                
            }
            
            bool CanJoinTeam1()
            {
                int newTeam1Count = Team1Count + (currentTeam == CharacterTeamType.Team1 ? 0 : 1);
                int newTeam2Count = Team2Count - (currentTeam == CharacterTeamType.Team2 ? 1 : 0);
                return newTeam1Count - newTeam2Count <= serverSettings.PvpAutoBalanceThreshold;
            }
            
            bool CanJoinTeam2()
            {
                int newTeam2Count = Team2Count + (currentTeam == CharacterTeamType.Team2 ? 0 : 1);
                int newTeam1Count = Team1Count - (currentTeam == CharacterTeamType.Team1 ? 1 : 0);
                return newTeam2Count - newTeam1Count <= serverSettings.PvpAutoBalanceThreshold;
            }
        }
        
        public void ResetPvpTeamSelection()
        {
            TeamPreferenceListBox?.Deselect();
            MultiplayerPreferences.Instance.TeamPreference = CharacterTeamType.None;
            RefreshPvpTeamSelectionButtons();
            RefreshChatrow();
            GameMain.Client.ForceNameJobTeamUpdate();
        }

        public void SetAllowSpectating(bool allowSpectating)
        {
            // Server owner is allowed to spectate regardless of the server settings
            if (GameMain.Client != null && GameMain.Client.IsServerOwner) { return; }

            // A client whose character has faced permadeath and hasn't chosen a new
            // character yet has no choice but to spectate
            if (campaignCharacterInfo != null && campaignCharacterInfo.PermanentlyDead) { return; }

            // Show the player config menu if spectating is not allowed
            if (spectateBox.Selected && !allowSpectating) { spectateBox.Selected = false; }

            // Hide spectate tickbox if spectating is not allowed
            spectateBox.Visible = allowSpectating;
        }

        public void SetAutoRestart(bool enabled, float timer = 0.0f)
        {
            autoRestartBox.Selected = enabled;
            autoRestartTimer = timer;
        }

        public void SetMissionTypes(IEnumerable<Identifier> missionTypes)
        {
            MissionTypes = missionTypes;
        }

        private void RefreshOutpostDropdown()
        {
            outpostDropdown.Parent.Visible = MissionTypeFrame.Visible;
            if (!outpostDropdown.Parent.Visible) { return; }

            outpostDropdownUpToDate = false;

            Identifier prevSelected = GameMain.NetworkMember?.ServerSettings.SelectedOutpostName ?? Identifier.Empty;

            outpostDropdown.ClearChildren();
            outpostDropdown.AddItem(TextManager.Get("Random"), "Random".ToIdentifier());
            HashSet<Identifier> validOutpostTagsForMissions = new HashSet<Identifier>();

            IEnumerable<Type> suitableMissionClasses = 
                SelectedMode == GameModePreset.PvP ?
                MissionPrefab.PvPMissionClasses.Values :
                MissionPrefab.CoOpMissionClasses.Values;
            foreach (var missionType in MissionTypes)
            {
                foreach (var missionPrefab in MissionPrefab.Prefabs)
                {
                    if (!suitableMissionClasses.Contains(missionPrefab.MissionClass)) { continue; }
                    if (missionPrefab.Type != missionType || missionPrefab.SingleplayerOnly) { continue; }
                    if (!missionPrefab.AllowOutpostSelectionFromTag.IsEmpty)
                    {
                        validOutpostTagsForMissions.Add(missionPrefab.AllowOutpostSelectionFromTag);
                    }
                }
            }
            if (validOutpostTagsForMissions.Any())
            {
                foreach (var submarineInfo in SubmarineInfo.SavedSubmarines.DistinctBy(s => s.Name))
                {
                    if (submarineInfo.Type == SubmarineType.Outpost && 
                        validOutpostTagsForMissions.Any(tag => submarineInfo.OutpostTags.Contains(tag)))
                    {
                        outpostDropdown.AddItem(submarineInfo.DisplayName, userData: submarineInfo.Name.ToIdentifier(), toolTip: submarineInfo.Description);
                    }
                }
                outpostDropdown.ListBox.Select(prevSelected);
                GameMain.Client.ServerSettings.AssignGUIComponent(nameof(ServerSettings.SelectedOutpostName), outpostDropdown);
            }
            else
            {
                outpostDropdown.Parent.Visible = false;
                //remove assignment, we shouldn't try selecting the outpost when there's none to select
                GameMain.Client.ServerSettings.AssignGUIComponent(nameof(ServerSettings.SelectedOutpostName), null);
            }
            outpostDropdownUpToDate = true;
        }

        public void UpdateSubList(GUIComponent subList, IEnumerable<SubmarineInfo> submarines)
        {
            if (subList == null) { return; }

            subList.ClearChildren();

            foreach (SubmarineInfo sub in submarines)
            {
                AddSubmarine(subList, sub);
            }
        }

        private void AddSubmarine(GUIComponent subList, SubmarineInfo sub)
        {
            if (subList is GUIListBox listBox)
            {
                subList = listBox.Content;
            }
            else if (subList is GUIDropDown dropDown)
            {
                subList = dropDown.ListBox.Content;
            }

            var frame = new GUIFrame(new RectTransform(new Vector2(1.0f, 0.1f), subList.RectTransform) 
            { 
                //enough space for 2 lines (price and class) + some padding
                MinSize = new Point(0, (int)(GUIStyle.SmallFont.LineHeight * 2.3f)) 
            },
                style: "ListBoxElement")
            {
                ToolTip = sub.Description,
                UserData = sub
            };

            var frameLayout = new GUILayoutGroup(new RectTransform(new Vector2(0.75f, 1f), frame.RectTransform), isHorizontal: true);

            var subTextBlock = new GUITextBlock(new RectTransform(new Vector2(0.7f, 1.0f), frameLayout.RectTransform, Anchor.CenterLeft),
                ToolBox.LimitString(sub.DisplayName.Value, GUIStyle.Font, subList.Rect.Width - 65), textAlignment: Alignment.CenterLeft)
            {
                UserData = "nametext",
                CanBeFocused = true
            };

            var pvpContainer = new GUIFrame(new RectTransform(new Vector2(0.3f, 1f), frameLayout.RectTransform, Anchor.CenterRight), style: null);
            var coalitionIcon = new GUIFrame(new RectTransform(new Vector2(0.5f, 1f), pvpContainer.RectTransform, Anchor.CenterLeft), style: "CoalitionIcon")
            {
                Visible = false,
                UserData = CoalitionIconUserData,
            };
            var separatistsIcon = new GUIFrame(new RectTransform(new Vector2(0.5f, 1f), pvpContainer.RectTransform, Anchor.CenterRight), style: "SeparatistIcon")
            {
                Visible = false,
                UserData = SeparatistsIconUserData,
            };

            var matchingSub = 
                SubmarineInfo.SavedSubmarines.FirstOrDefault(s => s.Name == sub.Name && s.MD5Hash?.StringRepresentation == sub.MD5Hash?.StringRepresentation) ??
                SubmarineInfo.SavedSubmarines.FirstOrDefault(s => s.Name == sub.Name);

            if (matchingSub == null)
            {
                subTextBlock.TextColor = new Color(subTextBlock.TextColor, 0.5f);
                frame.ToolTip = TextManager.Get("SubNotFound");
            }
            else if (matchingSub?.MD5Hash == null || matchingSub.MD5Hash?.StringRepresentation != sub.MD5Hash?.StringRepresentation)
            {
                subTextBlock.TextColor = new Color(subTextBlock.TextColor, 0.5f);
                frame.ToolTip = TextManager.Get("SubDoesntMatch");
            }
            else
            {
                if (subList == ShuttleList || subList == ShuttleList.ListBox || subList == ShuttleList.ListBox.Content)
                {
                    subTextBlock.TextColor = new Color(subTextBlock.TextColor, sub.HasTag(SubmarineTag.Shuttle) ? 1.0f : 0.6f);
                }
            }

            if (!sub.RequiredContentPackagesInstalled)
            {
                subTextBlock.TextColor = Color.Lerp(subTextBlock.TextColor, Color.DarkRed, 0.5f);
                frame.ToolTip = TextManager.Get("ContentPackageMismatch") + "\n\n" + frame.ToolTip.SanitizedString;
            }

            CreateSubmarineClassText(
                frame,
                sub,
                subTextBlock,
                subList);
        }

        private void CreateSubmarineClassText(
            GUIComponent parent,
            SubmarineInfo sub,
            GUITextBlock subTextBlock,
            GUIComponent subList)
        {
            if (sub.HasTag(SubmarineTag.Shuttle))
            {
                new GUITextBlock(new RectTransform(new Vector2(0.5f, 1.0f), parent.RectTransform, Anchor.CenterRight) { AbsoluteOffset = new Point(GUI.IntScale(20), 0) },
                    TextManager.Get("Shuttle", "RespawnShuttle"), textAlignment: Alignment.CenterRight, font: GUIStyle.SmallFont)
                {
                    TextColor = subTextBlock.TextColor * 0.8f,
                    ToolTip = subTextBlock.ToolTip?.SanitizedString,
                    CanBeFocused = false
                };
                //make shuttles more dim in the sub list (selecting a shuttle as the main sub is allowed but not recommended)
                if (subList == SubList.Content)
                {
                    subTextBlock.TextColor *= 0.8f;
                    foreach (GUIComponent child in parent.Children)
                    {
                        child.Color *= 0.8f;
                    }
                }
            }
            else
            {
                var infoContainer = new GUILayoutGroup(new RectTransform(new Vector2(0.25f, 1.0f), parent.RectTransform, Anchor.CenterRight) { AbsoluteOffset = new Point(GUI.IntScale(20), 0) }, isHorizontal: false);
                new GUITextBlock(new RectTransform(new Vector2(1.0f, 0.5f), infoContainer.RectTransform),
                    TextManager.GetWithVariable("currencyformat", "[credits]", string.Format(CultureInfo.InvariantCulture, "{0:N0}", sub.Price)), textAlignment: Alignment.BottomRight, font: GUIStyle.SmallFont)
                {
                    Padding = Vector4.Zero,
                    UserData = "pricetext",
                    TextColor = subTextBlock.TextColor * 0.8f,
                    CanBeFocused = false
                };
                new GUITextBlock(new RectTransform(new Vector2(1.0f, 0.5f), infoContainer.RectTransform),
                    sub.Class.Name, textAlignment: Alignment.TopRight, font: GUIStyle.SmallFont)
                {
                    Padding = Vector4.Zero,
                    UserData = "classtext",
                    TextColor = subTextBlock.TextColor * 0.8f,
                    ToolTip = subTextBlock.ToolTip,
                    CanBeFocused = false
                };
            }
        }
        
        public bool VotableClicked(GUIComponent component, object userData)
        {
            if (GameMain.Client == null) { return false; }

            VoteType voteType;
            if (component.Parent == GameMain.NetLobbyScreen.SubList.Content)
            {
                if (SelectedMode == GameModePreset.PvP && MultiplayerPreferences.Instance.TeamPreference is not (CharacterTeamType.Team1 or CharacterTeamType.Team2))
                {
                    // we are in PvP but don't have a team selected, so we can't select a sub
                    // and also highlight the team selection list

                    foreach (GUIComponent child in TeamPreferenceListBox.Content.Children)
                    {
                        if (child.UserData is CharacterTeamType.None) { continue; }
                        child.Flash(GUIStyle.Red, useRectangleFlash: true, flashDuration: 1f);
                    }

                    return false;
                }
                if (!GameMain.Client.ServerSettings.AllowSubVoting)
                {
                    var selectedSub = (SubmarineInfo)component.UserData;
                    var type = SelectedMode != GameModePreset.PvP
                                   ? SelectedSubType.Sub
                                   : MultiplayerPreferences.Instance.TeamPreference switch
                                   {
                                       CharacterTeamType.None or CharacterTeamType.Team1
                                           => SelectedSubType.Sub,
                                       CharacterTeamType.Team2
                                           => SelectedSubType.EnemySub,
                                       _ => throw new NotImplementedException()
                                   };

                    if (SelectedMode == GameModePreset.MultiPlayerCampaign && CampaignSetupUI != null)
                    {
                        if (selectedSub.Price > CampaignSettings.CurrentSettings.InitialMoney)
                        {
                            new GUIMessageBox(TextManager.Get("warning"), TextManager.Get("campaignsubtooexpensive"));
                        }
                        if (!selectedSub.IsCampaignCompatible)
                        {
                            new GUIMessageBox(TextManager.Get("warning"), TextManager.Get("campaignsubincompatible"));
                        }
                    }
                    if (!selectedSub.RequiredContentPackagesInstalled)
                    {
                        var msgBox = new GUIMessageBox(TextManager.Get("ContentPackageMismatch"),
                            selectedSub.RequiredContentPackages.Any() ?
                            TextManager.GetWithVariable("ContentPackageMismatchWarning", "[requiredcontentpackages]", string.Join(", ", selectedSub.RequiredContentPackages)) :
                            TextManager.Get("ContentPackageMismatchWarningGeneric"),
                            new LocalizedString[] { TextManager.Get("Yes"), TextManager.Get("No") });

                        msgBox.Buttons[0].OnClicked = msgBox.Close;
                        msgBox.Buttons[0].OnClicked += (button, obj) =>
                        {
                            GameMain.Client.RequestSelectSub(obj as SubmarineInfo, type);
                            return true;
                        };
                        msgBox.Buttons[1].OnClicked = msgBox.Close;
                        return false;
                    }
                    else if (GameMain.Client.HasPermission(ClientPermissions.SelectSub))
                    {
                        GameMain.Client.RequestSelectSub(selectedSub, type);
                        return true;
                    }
                    return false;
                }
                if (component.UserData is SubmarineInfo sub)
                {
                    CreateSubPreview(sub);
                }
                voteType = VoteType.Sub;
            }
            else if (component.Parent == GameMain.NetLobbyScreen.ModeList.Content)
            {
                if (!GameMain.Client.ServerSettings.AllowModeVoting)
                {
                    if (GameMain.Client.HasPermission(ClientPermissions.SelectMode))
                    {
                        Identifier presetName = ((GameModePreset)component.UserData).Identifier;

                        //display a verification prompt when switching away from the campaign
                        if (HighlightedModeIndex == SelectedModeIndex &&
                            (GameMain.NetLobbyScreen.ModeList.SelectedData as GameModePreset) == GameModePreset.MultiPlayerCampaign &&
                            presetName != GameModePreset.MultiPlayerCampaign.Identifier)
                        {
                            var verificationBox = new GUIMessageBox("", TextManager.Get("endcampaignverification"), new LocalizedString[] { TextManager.Get("yes"), TextManager.Get("no") });
                            verificationBox.Buttons[0].OnClicked += (btn, userdata) =>
                            {
                                GameMain.Client.RequestSelectMode(component.Parent.GetChildIndex(component));
                                HighlightMode(SelectedModeIndex);
                                verificationBox.Close(btn, userdata);
                                return true;
                            };
                            verificationBox.Buttons[1].OnClicked = verificationBox.Close;
                            return false;
                        }
                        GameMain.Client.RequestSelectMode(component.Parent.GetChildIndex(component));
                        HighlightMode(SelectedModeIndex);

                        if (presetName == "multiplayercampaign")
                        {
                            GUI.SetCursorWaiting(endCondition: () =>
                            {
                                return CampaignFrame.Visible || CampaignSetupFrame.Visible;
                            });
                        }

                        return presetName != "multiplayercampaign";
                    }
                    return false;
                }
                else if (!((GameModePreset)userData).Votable)
                {
                    return false;
                }

                voteType = VoteType.Mode;
            }
            else
            {
                return false;
            }

            GameMain.Client.Vote(voteType, userData);

            return true;
        }

        public void AddPlayer(Client client)
        {
            GUITextBlock textBlock = new GUITextBlock(new RectTransform(new Vector2(1.0f, 0.1f), PlayerList.Content.RectTransform) { MinSize = new Point(0, (int)(30 * GUI.Scale)) },
                client.Name, textAlignment: Alignment.CenterLeft, font: GUIStyle.SmallFont, style: null)
            {
                Padding = Vector4.One * 10.0f * GUI.Scale,
                Color = Color.White * 0.25f,
                HoverColor = Color.White * 0.5f,
                SelectedColor = Color.White * 0.85f,
                OutlineColor = Color.White * 0.5f,
                TextColor = Color.White,
                SelectedTextColor = Color.Black,
                UserData = client
            };
            var soundIcon = new GUIImage(new RectTransform(Vector2.One * 0.8f, textBlock.RectTransform, Anchor.CenterRight, scaleBasis: ScaleBasis.BothHeight) { AbsoluteOffset = new Point(5, 0) },
                sprite: GUIStyle.GetComponentStyle("GUISoundIcon").GetDefaultSprite(), scaleToFit: true)
            {
                UserData = new Pair<string, float>("soundicon", 0.0f),
                CanBeFocused = false,
                Visible = true,
                OverrideState = GUIComponent.ComponentState.None,
                HoverColor = Color.White
            };

            var soundIconDisabled = new GUIImage(new RectTransform(Vector2.One * 0.8f, textBlock.RectTransform, Anchor.CenterRight, scaleBasis: ScaleBasis.BothHeight) { AbsoluteOffset = new Point(5, 0) },
                "GUISoundIconDisabled")
            {
                UserData = "soundicondisabled",
                CanBeFocused = true,
                Visible = false,
                OverrideState = GUIComponent.ComponentState.None,
                HoverColor = Color.White
            };
            
            var readyTick = new GUIFrame(new RectTransform(new Vector2(0.6f, 0.6f), textBlock.RectTransform, Anchor.CenterRight, scaleBasis: ScaleBasis.BothHeight) { AbsoluteOffset = new Point(10 + soundIcon.Rect.Width, 0) }, style: "GUIReadyToStart")
            {
                Visible = false,
                CanBeFocused = false,
                ToolTip = TextManager.Get("ReadyToStartTickBox"),
                UserData = "clientready"
            };

            var downloadingThrobber = new GUICustomComponent(
                new RectTransform(Vector2.One, textBlock.RectTransform, scaleBasis: ScaleBasis.BothHeight),
                onUpdate: null,
                onDraw: DrawDownloadThrobber(client, soundIcon, soundIconDisabled, readyTick));
        }

        private Action<SpriteBatch, GUICustomComponent> DrawDownloadThrobber(Client client, params GUIComponent[] otherComponents)
            => (sb, c) => DrawDownloadThrobber(client, otherComponents, sb, c); //poor man's currying

        private static void DrawDownloadThrobber(Client client, GUIComponent[] otherComponents, SpriteBatch spriteBatch, GUICustomComponent component)
        {
            if (!client.IsDownloading)
            {
                component.ToolTip = "";
                return;
            }

            component.HideElementsOutsideFrame = false;
            int drawRectX = otherComponents.Where(c => c.Visible)
                .Select(c => c.Rect)
                .Concat(new Rectangle(component.Parent.Rect.Right, component.Parent.Rect.Y, 0, component.Parent.Rect.Height).ToEnumerable())
                .Min(r => r.X) - component.Parent.Rect.Height - 10;
            Rectangle drawRect
                = new Rectangle(drawRectX, component.Rect.Y, component.Parent.Rect.Height, component.Parent.Rect.Height);
            component.RectTransform.AbsoluteOffset = drawRect.Location - component.Parent.Rect.Location;
            component.RectTransform.NonScaledSize = drawRect.Size;
            var sheet = GUIStyle.GenericThrobber;
            sheet.Draw(
                spriteBatch,
                pos: drawRect.Location.ToVector2(),
                spriteIndex: (int)Math.Floor(Timing.TotalTime * 24.0f) % sheet.FrameCount,
                color: Color.White,
                origin: Vector2.Zero, rotate: 0.0f,
                scale: Vector2.One * component.Parent.Rect.Height / sheet.FrameSize.ToVector2());
            if (component.ToolTip.IsNullOrEmpty())
            {
                component.ToolTip = TextManager.Get("PlayerIsDownloadingFiles");
            }
        }
        
        public void SetPlayerNameAndJobPreference(Client client)
        {
            var playerFrame = (GUITextBlock)PlayerList.Content.FindChild(client);
            if (playerFrame == null) { return; }
            playerFrame.Text = client.Name;

            playerFrame.ToolTip = "";
            Color color = Color.White;
            if (SelectedMode == GameModePreset.PvP)
            {
                switch (client.PreferredTeam)
                {
                    case CharacterTeamType.Team1:
                        color = new Color(0, 110, 150, 255);
                        playerFrame.ToolTip = TextManager.GetWithVariable("teampreference", "[team]", TextManager.Get("teampreference.team1"));
                        break;
                    case CharacterTeamType.Team2:
                        color = new Color(150, 110, 0, 255);
                        playerFrame.ToolTip = TextManager.GetWithVariable("teampreference", "[team]", TextManager.Get("teampreference.team2"));
                        break;
                    default:
                        playerFrame.ToolTip = TextManager.GetWithVariable("teampreference", "[team]", TextManager.Get("none"));
                        break;
                }
            }
            else
            {
                if (JobPrefab.Prefabs.ContainsKey(client.PreferredJob))
                {
                    color = JobPrefab.Prefabs[client.PreferredJob].UIColor;
                    playerFrame.ToolTip = TextManager.GetWithVariable("jobpreference", "[job]", JobPrefab.Prefabs[client.PreferredJob].Name);
                }
                else
                {
                    playerFrame.ToolTip = TextManager.GetWithVariable("jobpreference", "[job]", TextManager.Get("none"));
                }
            }
            playerFrame.Color = color * 0.4f;
            playerFrame.HoverColor = color * 0.6f;
            playerFrame.SelectedColor = color * 0.8f;
            playerFrame.OutlineColor = color * 0.5f;
            playerFrame.TextColor = color;
        }

        public void SetPlayerVoiceIconState(Client client, bool muted, bool mutedLocally)
        {
            var PlayerFrame = PlayerList.Content.FindChild(client);
            if (PlayerFrame == null) { return; }
            var soundIcon = PlayerFrame.FindChild(c => c.UserData is Pair<string, float> pair && pair.First == "soundicon");
            var soundIconDisabled = PlayerFrame.FindChild("soundicondisabled");

            Pair<string, float> userdata = soundIcon.UserData as Pair<string, float>;

            if (!soundIcon.Visible)
            {
                userdata.Second = 0.0f;
            }
            soundIcon.Visible = !muted && !mutedLocally;
            soundIconDisabled.Visible = muted || mutedLocally;
            soundIconDisabled.ToolTip = TextManager.Get(mutedLocally ? "MutedLocally" : "MutedGlobally");
        }

        public void SetPlayerSpeaking(Client client)
        {
            var PlayerFrame = PlayerList.Content.FindChild(client);
            if (PlayerFrame == null) { return; }
            var soundIcon = PlayerFrame.FindChild(c => c.UserData is Pair<string, float> pair && pair.First == "soundicon");
            Pair<string, float> userdata = soundIcon.UserData as Pair<string, float>;
            userdata.Second = Math.Max(userdata.Second,   0.18f);
            soundIcon.Visible = true;
        }

        public void RemovePlayer(Client client)
        {
            GUIComponent child = PlayerList.Content.GetChildByUserData(client);
            if (child != null) { PlayerList.RemoveChild(child); }
        }

        public static Client ExtractClientFromClickableArea(GUITextBlock.ClickableArea area)
            => area.Data.ExtractClient();
        
        public void SelectPlayer(GUITextBlock component, GUITextBlock.ClickableArea area)
        {
            var client = ExtractClientFromClickableArea(area);
            if (client is null) { return; }
            GameMain.NetLobbyScreen.SelectPlayer(client);
        }

        public void ShowPlayerContextMenu(GUITextBlock component, GUITextBlock.ClickableArea area)
        {
            var client = ExtractClientFromClickableArea(area);
            if (client is null) { return; }
            CreateModerationContextMenu(client);
        }

        #region Context Menu
        public static void CreateModerationContextMenu(Client client)
        {
            if (GUIContextMenu.CurrentContextMenu != null) { return; }
            if (GameMain.IsSingleplayer || client == null) { return; }
            if (!(GameMain.Client is { PreviouslyConnectedClients: var previouslyConnectedClients })
                || !previouslyConnectedClients.Contains(client)) { return; }

            bool hasAccountId = client.AccountId.IsSome();
            bool canKick = GameMain.Client.HasPermission(ClientPermissions.Kick);
            bool canBan = GameMain.Client.HasPermission(ClientPermissions.Ban) && client.AllowKicking;
            bool canManagePermissions = GameMain.Client.HasPermission(ClientPermissions.ManagePermissions);

            // Disable options if we are targeting ourselves
            if (client.SessionId == GameMain.Client.SessionId)
            {
                canKick = canBan = canManagePermissions = false;
            }

            List<ContextMenuOption> options = new List<ContextMenuOption>();

            if (client.AccountId.TryUnwrap(out var accountId))
            {
                options.Add(new ContextMenuOption(accountId.ViewProfileLabel(), isEnabled: hasAccountId, onSelected: () =>
                {
                    accountId.OpenProfile();
                }));
            }
            
            options.Add(new ContextMenuOption("ModerationMenu.ManagePlayer", isEnabled: true, onSelected: () =>
                {
                    GameMain.NetLobbyScreen?.SelectPlayer(client);
                }));

            // Creates sub context menu options for all the ranks
            List<ContextMenuOption> rankOptions = new List<ContextMenuOption>();
            foreach (PermissionPreset rank in PermissionPreset.List)
            {
                rankOptions.Add(new ContextMenuOption(rank.DisplayName, isEnabled: true, onSelected: () =>
                {
                    LocalizedString label = TextManager.GetWithVariables(rank.Permissions == ClientPermissions.None ?  "clearrankprompt" : "giverankprompt", ("[user]", client.Name), ("[rank]", rank.DisplayName));
                    GUIMessageBox msgBox = new GUIMessageBox(string.Empty, label, new[] { TextManager.Get("Yes"), TextManager.Get("Cancel") });

                    msgBox.Buttons[0].OnClicked = delegate
                    {
                        client.SetPermissions(rank.Permissions, rank.PermittedCommands);
                        GameMain.Client.UpdateClientPermissions(client);
                        msgBox.Close();
                        return true;
                    };
                    msgBox.Buttons[1].OnClicked = delegate
                    {
                        msgBox.Close();
                        return true;
                    };
                }) { Tooltip = rank.Description });
            }

            options.Add(new ContextMenuOption("Rank", isEnabled: canManagePermissions, options: rankOptions.ToArray()));

            Color clientColor = client.Character?.Info?.Job.Prefab.UIColor ?? Color.White;

            if (GameMain.Client.ConnectedClients.Contains(client))
            {
                options.Add(new ContextMenuOption(client.MutedLocally ? "Unmute" : "Mute", isEnabled: client.SessionId != GameMain.Client.SessionId, onSelected: delegate
                {
                    client.MutedLocally = !client.MutedLocally;
                }));

                bool kickEnabled = client.SessionId != GameMain.Client.SessionId && client.AllowKicking;

                // if the user can kick create a kick option else create the votekick option
                ContextMenuOption kickOption;
                if (canKick)
                {
                    kickOption = new ContextMenuOption("Kick", isEnabled: kickEnabled, onSelected: delegate
                    {
                        GameMain.Client?.CreateKickReasonPrompt(client.Name, false);
                    });
                }
                else
                {
                    kickOption = new ContextMenuOption("VoteToKick", isEnabled: kickEnabled, onSelected: delegate
                    {
                        GameMain.Client?.VoteForKick(client);
                    });
                }

                options.Add(kickOption);
            }

            if (GameMain.Client?.ServerSettings?.BanList?.BannedPlayers?.Any(bp => bp.MatchesClient(client)) ?? false)
            {
                options.Add(new ContextMenuOption("clientpermission.unban", isEnabled: canBan, onSelected: delegate
                {
                    GameMain.Client?.UnbanPlayer(client.Name);
                }));
            }
            else
            {
                options.Add(new ContextMenuOption("Ban", isEnabled: canBan, onSelected: delegate
                {
                    GameMain.Client?.CreateKickReasonPrompt(client.Name, true);
                }));
            }

            GUIContextMenu.CreateContextMenu(null, client.Name, headerColor: clientColor, options.ToArray());
        }
        
        #endregion

        public bool SelectPlayer(Client selectedClient)
        {
            bool myClient = selectedClient.SessionId == GameMain.Client.SessionId;
            bool hasManagePermissions = GameMain.Client.HasPermission(ClientPermissions.ManagePermissions);

            PlayerFrame = new GUIButton(new RectTransform(Vector2.One, GUI.Canvas, Anchor.Center), style: null)
            {
                OnClicked = (btn, userdata) => 
                { 
                    if (GUI.MouseOn == btn || GUI.MouseOn == btn.TextBlock)
                    {
                        ClosePlayerFrame(btn, userdata);
                    }
                    return true; 
                }
            };

            new GUIFrame(new RectTransform(GUI.Canvas.RelativeSize, PlayerFrame.RectTransform, Anchor.Center), style: "GUIBackgroundBlocker");
            Vector2 frameSize = hasManagePermissions ? new Vector2(.28f, .5f) : new Vector2(.28f, .15f);

            var playerFrameInner = new GUIFrame(new RectTransform(frameSize, PlayerFrame.RectTransform, Anchor.Center) { MinSize = new Point(550, 0) });
            var paddedPlayerFrame = new GUILayoutGroup(new RectTransform(new Vector2(0.9f, 0.88f), playerFrameInner.RectTransform, Anchor.Center))
            {
                Stretch = true,
                RelativeSpacing = 0.03f
            };

            var headerContainer = new GUILayoutGroup(new RectTransform(new Vector2(1.0f, 0.25f), paddedPlayerFrame.RectTransform), isHorizontal: false);
            
            var headerTextContainer = new GUILayoutGroup(new RectTransform(new Vector2(1f, 0.5f), headerContainer.RectTransform), isHorizontal: true, childAnchor: Anchor.CenterLeft)
            {
                Stretch = true
            };

            var headerVolumeContainer = new GUILayoutGroup(new RectTransform(new Vector2(1f, 0.5f), headerContainer.RectTransform), isHorizontal: true, childAnchor: Anchor.CenterLeft)
            {
                Stretch = true
            };

            var nameText = new GUITextBlock(new RectTransform(new Vector2(0.5f, 1.0f), headerTextContainer.RectTransform),
                text: selectedClient.Name, font: GUIStyle.LargeFont);
            nameText.Text = ToolBox.LimitString(nameText.Text, nameText.Font, (int)(nameText.Rect.Width * 0.95f));

            if (hasManagePermissions && !selectedClient.IsOwner)
            {
                PlayerFrame.UserData = selectedClient;

                new GUITextBlock(new RectTransform(new Vector2(1.0f, 0.05f), paddedPlayerFrame.RectTransform),
                    TextManager.Get("Rank"), font: GUIStyle.SubHeadingFont);
                var rankDropDown = new GUIDropDown(new RectTransform(new Vector2(1.0f, 0.1f), paddedPlayerFrame.RectTransform),
                    TextManager.Get("Rank"))
                {
                    UserData = selectedClient,
                    Enabled = !myClient
                };
                foreach (PermissionPreset permissionPreset in PermissionPreset.List)
                {
                    rankDropDown.AddItem(permissionPreset.DisplayName, permissionPreset, permissionPreset.Description);
                }
                rankDropDown.AddItem(TextManager.Get("CustomRank"), null);

                PermissionPreset currentPreset = PermissionPreset.List.Find(p =>
                    p.Permissions == selectedClient.Permissions &&
                    p.PermittedCommands.Count == selectedClient.PermittedConsoleCommands.Count && !p.PermittedCommands.Except(selectedClient.PermittedConsoleCommands).Any());
                rankDropDown.SelectItem(currentPreset);

                rankDropDown.OnSelected += (c, userdata) =>
                {
                    PermissionPreset selectedPreset = (PermissionPreset)userdata;
                    if (selectedPreset != null)
                    {
                        var client = PlayerFrame.UserData as Client;
                        client.SetPermissions(selectedPreset.Permissions, selectedPreset.PermittedCommands);
                        GameMain.Client.UpdateClientPermissions(client);

                        PlayerFrame = null;
                        SelectPlayer(client);
                    }
                    return true;
                };

                var permissionLabels = new GUILayoutGroup(new RectTransform(new Vector2(1.0f, 0.1f), paddedPlayerFrame.RectTransform), isHorizontal: true)
                {
                    Stretch = true,
                    RelativeSpacing = 0.05f
                };
                var permissionLabel = new GUITextBlock(new RectTransform(new Vector2(0.5f, 1.0f), permissionLabels.RectTransform), TextManager.Get("Permissions"), font: GUIStyle.SubHeadingFont);
                var consoleCommandLabel = new GUITextBlock(new RectTransform(new Vector2(0.5f, 1.0f), permissionLabels.RectTransform),
                    TextManager.Get("PermittedConsoleCommands"), wrap: true, font: GUIStyle.SubHeadingFont);
                GUITextBlock.AutoScaleAndNormalize(permissionLabel, consoleCommandLabel);

                var permissionContainer = new GUILayoutGroup(new RectTransform(new Vector2(1.0f, 0.4f), paddedPlayerFrame.RectTransform), isHorizontal: true)
                {
                    Stretch = true,
                    RelativeSpacing = 0.05f
                };

                var listBoxContainerLeft = new GUILayoutGroup(new RectTransform(new Vector2(0.5f, 1.0f), permissionContainer.RectTransform))
                {
                    Stretch = true,
                    RelativeSpacing = 0.05f
                };

                new GUITickBox(new RectTransform(new Vector2(0.15f, 0.15f), listBoxContainerLeft.RectTransform), TextManager.Get("all", "clientpermission.all"))
                {
                    Enabled = !myClient,
                    OnSelected = (tickbox) =>
                    {
                        //reset rank to custom
                        rankDropDown.SelectItem(null);

                        if (PlayerFrame.UserData is not Client client) { return false; }

                        foreach (GUIComponent child in tickbox.Parent.GetChild<GUIListBox>().Content.Children)
                        {
                            var permissionTickBox = child as GUITickBox;
                            permissionTickBox.Enabled = false;
                            permissionTickBox.Selected = tickbox.Selected;
                            permissionTickBox.Enabled = true;
                        }
                        GameMain.Client.UpdateClientPermissions(client);
                        return true;
                    }
                };
                var permissionsBox = new GUIListBox(new RectTransform(Vector2.One, listBoxContainerLeft.RectTransform))
                {
                    UserData = selectedClient
                };

                foreach (ClientPermissions permission in Enum.GetValues(typeof(ClientPermissions)))
                {
                    if (permission == ClientPermissions.None || permission == ClientPermissions.All) { continue; }

                    var permissionTick = new GUITickBox(new RectTransform(new Vector2(0.15f, 0.15f), permissionsBox.Content.RectTransform),
                        TextManager.Get("ClientPermission." + permission), font: GUIStyle.SmallFont)
                    {
                        UserData = permission,
                        Selected = selectedClient.HasPermission(permission),
                        Enabled = !myClient,
                        OnSelected = (tickBox) =>
                        {
                            //reset rank to custom
                            rankDropDown.SelectItem(null);

                            if (PlayerFrame.UserData is not Client client) { return false; }

                            var thisPermission = (ClientPermissions)tickBox.UserData;
                            if (tickBox.Selected)
                            {
                                client.GivePermission(thisPermission);
                            }
                            else
                            {
                                client.RemovePermission(thisPermission);
                            }
                            if (tickBox.Enabled)
                            {
                                GameMain.Client.UpdateClientPermissions(client);
                            }
                            return true;
                        }
                    };
                    permissionTick.ToolTip = permissionTick.TextBlock.ToolTip = TextManager.Get("ClientPermission." + permission + ".description");
                }

                var listBoxContainerRight = new GUILayoutGroup(new RectTransform(new Vector2(0.5f, 1.0f), permissionContainer.RectTransform))
                {
                    Stretch = true,
                    RelativeSpacing = 0.05f
                };

                new GUITickBox(new RectTransform(new Vector2(0.15f, 0.15f), listBoxContainerRight.RectTransform), TextManager.Get("all", "clientpermission.all"))
                {
                    Enabled = !myClient,
                    OnSelected = (tickbox) =>
                    {
                        //reset rank to custom
                        rankDropDown.SelectItem(null);

                        if (PlayerFrame.UserData is not Client client) { return false; }

                        foreach (GUIComponent child in tickbox.Parent.GetChild<GUIListBox>().Content.Children)
                        {
                            var commandTickBox = child as GUITickBox;
                            commandTickBox.Enabled = false;
                            commandTickBox.Selected = tickbox.Selected;
                            commandTickBox.Enabled = true;
                        }
                        GameMain.Client.UpdateClientPermissions(client);
                        return true;
                    }
                };
                var commandList = new GUIListBox(new RectTransform(Vector2.One, listBoxContainerRight.RectTransform))
                {
                    UserData = selectedClient
                };
                foreach (DebugConsole.Command command in DebugConsole.Commands)
                {
                    var commandTickBox = new GUITickBox(new RectTransform(new Vector2(0.15f, 0.15f), commandList.Content.RectTransform),
                        command.Names[0].Value, font: GUIStyle.SmallFont)
                    {
                        Selected = selectedClient.PermittedConsoleCommands.Contains(command),
                        Enabled = !myClient,
                        ToolTip = command.Help,
                        UserData = command
                    };
                    commandTickBox.OnSelected += (GUITickBox tickBox) =>
                    {
                        //reset rank to custom
                        rankDropDown.SelectItem(null);

                        DebugConsole.Command selectedCommand = tickBox.UserData as DebugConsole.Command;
                        if (PlayerFrame.UserData is not Client client) { return false; }

                        if (!tickBox.Selected)
                        {
                            client.PermittedConsoleCommands.Remove(selectedCommand);
                        }
                        else if (!client.PermittedConsoleCommands.Contains(selectedCommand))
                        {
                            client.PermittedConsoleCommands.Add(selectedCommand);
                        }
                        if (tickBox.Enabled)
                        {
                            GameMain.Client.UpdateClientPermissions(client);
                        }
                        return true;
                    };
                }
            }

            var buttonAreaTop = myClient ? null : new GUILayoutGroup(new RectTransform(new Vector2(1.0f, 0.08f), paddedPlayerFrame.RectTransform), isHorizontal: true);
            var buttonAreaLower = new GUILayoutGroup(new RectTransform(new Vector2(1.0f, 0.08f), paddedPlayerFrame.RectTransform), isHorizontal: true, childAnchor: Anchor.CenterLeft);

            if (!myClient)
            {
                if (GameMain.Client.HasPermission(ClientPermissions.Ban))
                {
                    GUIButton banButton;
                    if (GameMain.Client?.ServerSettings?.BanList?.BannedPlayers?.Any(bp => bp.MatchesClient(selectedClient)) ?? false)
                    {
                        banButton = new GUIButton(new RectTransform(new Vector2(0.34f, 1.0f), buttonAreaTop.RectTransform),
                            TextManager.Get("clientpermission.unban"))
                        {
                            UserData = selectedClient,
                            OnClicked = (bt, userdata) => { GameMain.Client?.UnbanPlayer(selectedClient.Name); return true; }
                        };
                    }
                    else
                    {
                        banButton = new GUIButton(new RectTransform(new Vector2(0.34f, 1.0f), buttonAreaTop.RectTransform),
                            TextManager.Get("Ban"))
                        {
                            UserData = selectedClient,
                            OnClicked = (bt, userdata) => { BanPlayer(selectedClient); return true; }
                        };
                    }
                    banButton.OnClicked += ClosePlayerFrame;
                }

                if (GameMain.Client != null && GameMain.Client.ConnectedClients.Contains(selectedClient))
                {
                    if (GameMain.Client.ServerSettings.AllowVoteKick &&
                        selectedClient != null && selectedClient.AllowKicking)
                    {
                        var kickVoteButton = new GUIButton(new RectTransform(new Vector2(0.34f, 1.0f), buttonAreaLower.RectTransform),
                            TextManager.Get("VoteToKick"))
                        {
                            OnClicked = (btn, userdata) => { GameMain.Client.VoteForKick(selectedClient); btn.Enabled = false; return true; },
                            UserData = selectedClient
                        };
                    }

                    if (GameMain.Client.HasPermission(ClientPermissions.Kick) &&
                        selectedClient != null && selectedClient.AllowKicking)
                    {
                        var kickButton = new GUIButton(new RectTransform(new Vector2(0.34f, 1.0f), buttonAreaLower.RectTransform),
                            TextManager.Get("Kick"))
                        {
                            UserData = selectedClient,
                            OnClicked = (bt, userdata) => { KickPlayer(selectedClient); return true; }
                        };
                        kickButton.OnClicked += ClosePlayerFrame;
                    }

                    var volumeLayout = new GUILayoutGroup(new RectTransform(new Vector2(0.5f, 1f), headerVolumeContainer.RectTransform), isHorizontal: false);
                    var volumeTextLayout = new GUILayoutGroup(new RectTransform(new Vector2(1f, 0.5f), volumeLayout.RectTransform), isHorizontal: true, childAnchor: Anchor.CenterLeft);
                    new GUITextBlock(new RectTransform(new Vector2(0.6f, 1f), volumeTextLayout.RectTransform), TextManager.Get("VoiceChatVolume"));
                    var volumePercentageText = new GUITextBlock(new RectTransform(new Vector2(0.4f, 1f), volumeTextLayout.RectTransform), ToolBox.GetFormattedPercentage(selectedClient.VoiceVolume), textAlignment: Alignment.Right);
                    new GUIScrollBar(new RectTransform(new Vector2(1f, 0.5f), volumeLayout.RectTransform), barSize: 0.1f, style: "GUISlider")
                    {
                        Range = new Vector2(0f, 1f),
                        BarScroll = selectedClient.VoiceVolume / Client.MaxVoiceChatBoost,
                        OnMoved = (_, barScroll) =>
                        {
                            float newVolume = barScroll * Client.MaxVoiceChatBoost;

                            selectedClient.VoiceVolume = newVolume;
                            volumePercentageText.Text = ToolBox.GetFormattedPercentage(newVolume);
                            return true;
                        }
                    };

                    new GUITickBox(new RectTransform(new Vector2(0.175f, 1.0f), headerVolumeContainer.RectTransform, Anchor.TopRight),
                        TextManager.Get("Mute"))
                    {
                        Selected = selectedClient.MutedLocally,
                        OnSelected = (tickBox) => { selectedClient.MutedLocally = tickBox.Selected; return true; }
                    };
                }

                if (buttonAreaTop.CountChildren > 0)
                {
                    GUITextBlock.AutoScaleAndNormalize(buttonAreaTop.Children.Select(c => ((GUIButton)c).TextBlock).Concat(buttonAreaLower.Children.Select(c => ((GUIButton)c).TextBlock)));
                }
            }

            if (selectedClient.AccountId.TryUnwrap(out var accountId))
            {
                var viewSteamProfileButton = new GUIButton(new RectTransform(new Vector2(0.3f, 1.0f), headerTextContainer.RectTransform, Anchor.TopCenter) { MaxSize = new Point(int.MaxValue, (int)(40 * GUI.Scale)) },
                    accountId.ViewProfileLabel())
                {
                    UserData = selectedClient
                };
                viewSteamProfileButton.TextBlock.AutoScaleHorizontal = true;
                viewSteamProfileButton.OnClicked = (bt, userdata) =>
                {
                    accountId.OpenProfile();
                    return true;
                };
            }

            var closeButton = new GUIButton(new RectTransform(new Vector2(0f, 1.0f), buttonAreaLower.RectTransform, Anchor.CenterRight),
                TextManager.Get("Close"))
            {
                IgnoreLayoutGroups = true,
                OnClicked = ClosePlayerFrame
            };

            float xSize = 1f / buttonAreaLower.CountChildren;
            for (int i = 0; i < buttonAreaLower.CountChildren; i++)
            {
                buttonAreaLower.GetChild(i).RectTransform.RelativeSize = new Vector2(xSize, 1f);
            }

            buttonAreaLower.RectTransform.NonScaledSize = new Point(buttonAreaLower.Rect.Width, buttonAreaLower.RectTransform.Children.Max(c => c.NonScaledSize.Y));

            if (buttonAreaTop != null)
            {
                if (buttonAreaTop.CountChildren == 0)
                {
                    paddedPlayerFrame.RemoveChild(buttonAreaTop);
                }
                else
                {
                    for (int i = 0; i < buttonAreaTop.CountChildren; i++)
                    {
                        buttonAreaTop.GetChild(i).RectTransform.RelativeSize = new Vector2(1f / 3f, 1f);
                    }

                    buttonAreaTop.RectTransform.NonScaledSize =
                    buttonAreaLower.RectTransform.NonScaledSize =
                         new Point(buttonAreaLower.Rect.Width, Math.Max(buttonAreaLower.RectTransform.NonScaledSize.Y, buttonAreaTop.RectTransform.Children.Max(c => c.NonScaledSize.Y)));
                }
            }

            return false;
        }

        private bool ClosePlayerFrame(GUIButton button, object userData)
        {
            PlayerFrame = null;
            PlayerList.Deselect();
            return true;
        }

        public static void KickPlayer(Client client)
        {
            if (GameMain.NetworkMember == null || client == null) { return; }
            GameMain.Client.CreateKickReasonPrompt(client.Name, false);
        }

        public static void BanPlayer(Client client)
        {
            if (GameMain.NetworkMember == null || client == null) { return; }
            GameMain.Client.CreateKickReasonPrompt(client.Name, ban: true);
        }

        public override void AddToGUIUpdateList()
        {
            base.AddToGUIUpdateList();

            //CampaignSetupUI?.AddToGUIUpdateList();
            JobInfoFrame?.AddToGUIUpdateList();

            CharacterAppearanceCustomizationMenu?.AddToGUIUpdateList();
            JobSelectionFrame?.AddToGUIUpdateList();
        }

        public override void Update(double deltaTime)
        {
            if (GameMain.Client == null) { return; }

            UpdateMicIcon((float)deltaTime);

            foreach (GUIComponent child in PlayerList.Content.Children)
            {
                if (child.UserData is Client client)
                {
                    if (child.FindChild(c => c.UserData is Pair<string, float> pair && pair.First == "soundicon") is GUIImage soundIcon)
                    {
                        double voipAmplitude = 0.0f;
                        if (client.SessionId != GameMain.Client.SessionId)
                        {
                            voipAmplitude = client.VoipSound?.CurrentAmplitude ?? 0.0f;
                        }
                        else
                        {
                            var voip = VoipCapture.Instance;
                            if (voip == null)
                            {
                                voipAmplitude = 0;
                            }
                            else if (voip.LastEnqueueAudio > DateTime.Now - new TimeSpan(0, 0, 0, 0, milliseconds: 100))
                            {
                                voipAmplitude = voip.LastAmplitude;
                            }
                        }
                        VoipClient.UpdateVoiceIndicator(soundIcon, (float)voipAmplitude, (float)deltaTime);
                    }
                }
            }

            autoRestartText.Visible = autoRestartTimer > 0.0f && autoRestartBox.Selected;
            if (!MathUtils.NearlyEqual(autoRestartTimer, 0.0f) && autoRestartBox.Selected)
            {
                autoRestartTimer = Math.Max(autoRestartTimer - (float)deltaTime, 0.0f);
                if (autoRestartTimer > 0.0f)
                {
                    autoRestartText.Text = TextManager.Get("RestartingIn") + " " + ToolBox.SecondsToReadableTime(Math.Max(autoRestartTimer, 0));
                }
            }

            CharacterAppearanceCustomizationMenu?.Update();
            if (JobSelectionFrame != null && PlayerInput.PrimaryMouseButtonDown() && !GUI.IsMouseOn(JobSelectionFrame))
            {
                JobList.Deselect();
                JobSelectionFrame.Visible = false;
            }

            if (GUI.MouseOn?.UserData is JobVariant jobPrefab && 
                GUI.MouseOn.Style?.Name == "JobVariantButton" &&
                GUI.MouseOn.Parent != null)
            {
                if (jobVariantTooltip?.UserData is not JobVariant prevVisibleVariant || 
                    prevVisibleVariant.Prefab != jobPrefab.Prefab || 
                    prevVisibleVariant.Variant != jobPrefab.Variant)
                {
                    CreateJobVariantTooltip(jobPrefab.Prefab, TeamPreference, jobPrefab.Variant, isPvPMode: SelectedMode == GameModePreset.PvP, GUI.MouseOn.Parent);
                }
            }
            if (jobVariantTooltip != null)
            {
                jobVariantTooltip?.AddToGUIUpdateList();
                Rectangle mouseRect = jobVariantTooltip.MouseRect;
                mouseRect.Inflate(60 * GUI.Scale, 60 * GUI.Scale);
                if (!mouseRect.Contains(PlayerInput.MousePosition)) { jobVariantTooltip = null; }
            }
        }
        
        private void UpdateMicIcon(float deltaTime)
        {
            micCheckTimer -= deltaTime;
            if (micCheckTimer > 0.0f) { return; }

            Identifier newMicIconStyle = "GUIMicrophoneEnabled".ToIdentifier();
            if (GameSettings.CurrentConfig.Audio.VoiceSetting == VoiceMode.Disabled)
            {
                newMicIconStyle = "GUIMicrophoneDisabled".ToIdentifier();
            }
            else
            {
                var voipCaptureDeviceNames = VoipCapture.GetCaptureDeviceNames();
                if (voipCaptureDeviceNames.Count == 0)
                {
                    newMicIconStyle = "GUIMicrophoneUnavailable".ToIdentifier();
                }
            }

            if (newMicIconStyle != micIconStyle)
            {
                micIconStyle = newMicIconStyle;
                GUIStyle.Apply(micIcon, newMicIconStyle);
            }

            micCheckTimer = MicCheckInterval;
        }

        public override void Draw(double deltaTime, GraphicsDevice graphics, SpriteBatch spriteBatch)
        {
            if (backgroundSprite?.Texture == null) { return; }
            graphics.Clear(Color.Black);
            spriteBatch.Begin(SpriteSortMode.Deferred, samplerState: GUI.SamplerState, rasterizerState: GameMain.ScissorTestEnable);
            GUI.DrawBackgroundSprite(spriteBatch, backgroundSprite, Color.White);
            GUI.Draw(Cam, spriteBatch);
            spriteBatch.End();
        }

        private PlayStyle? prevPlayStyle = null;
        private bool? prevIsPublic = null;

        private void DrawServerBanner(SpriteBatch spriteBatch, GUICustomComponent component)
        {
            if (GameMain.NetworkMember?.ServerSettings == null) { return; }

            PlayStyle playStyle = GameMain.NetworkMember.ServerSettings.PlayStyle;

            Sprite sprite = GUIStyle
                .GetComponentStyle($"PlayStyleBanner.{playStyle}")?
                .GetSprite(GUIComponent.ComponentState.None);
            if (sprite is null) { return; }
            
            GUI.DrawBackgroundSprite(spriteBatch, sprite, Color.White, drawArea: component.Rect);

            if (!prevPlayStyle.HasValue || playStyle != prevPlayStyle.Value)
            {
                playstyleText.Text = TextManager.Get($"ServerTag.{playStyle}");
                playstyleText.Color = sprite.SourceElement.GetAttributeColor("BannerColor") ?? Color.White;
                playstyleText.RectTransform.NonScaledSize = (playstyleText.Font.MeasureString(playstyleText.Text) + new Vector2(25, 10) * GUI.Scale).ToPoint();
                prevPlayStyle = playStyle;
                (playstyleText.Parent as GUILayoutGroup)?.Recalculate();
                playstyleText.ToolTip = TextManager.Get($"ServerTagDescription.{playStyle}");
            }
            if (!prevIsPublic.HasValue || GameMain.NetworkMember.ServerSettings.IsPublic != prevIsPublic.Value)
            {
                publicOrPrivateText.Text = GameMain.NetworkMember.ServerSettings.IsPublic ? TextManager.Get("PublicLobbyTag") : TextManager.Get("PrivateLobbyTag");
                publicOrPrivateText.RectTransform.NonScaledSize = (publicOrPrivateText.Font.MeasureString(publicOrPrivateText.Text) + new Vector2(25, 10) * GUI.Scale).ToPoint();
                (publicOrPrivateText.Parent as GUILayoutGroup)?.Recalculate();
                prevIsPublic = GameMain.NetworkMember.ServerSettings.IsPublic;
            }
        }

        private static void DrawJobVariantItems(SpriteBatch spriteBatch, GUICustomComponent component, JobVariant jobPrefab, CharacterTeamType team, bool isPvPMode, int itemsPerRow)
        {
            var itemIdentifiers = jobPrefab.Prefab.JobItems[jobPrefab.Variant]
                .Where(it => it.ShowPreview)
                .Select(it => it.GetItemIdentifier(team, isPvPMode))
                .Distinct();

            Point slotSize = new Point(component.Rect.Height);
            int spacing = (int)(5 * GUI.Scale);
            int slotCount = itemIdentifiers.Count();
            int slotCountPerRow = Math.Min(slotCount, itemsPerRow);
            int rows = (int)Math.Max(Math.Ceiling(itemIdentifiers.Count() / (float)itemsPerRow), 1);

            float totalWidth = slotSize.X * slotCountPerRow + spacing * (slotCountPerRow - 1);
            float totalHeight = slotSize.Y * rows + spacing * (rows - 1);
            if (totalWidth > component.Rect.Width)
            {
                slotSize = new Point(
                    Math.Min((int)Math.Floor((slotSize.X - spacing) * (component.Rect.Width / totalWidth)),
                        (int)Math.Floor((slotSize.Y - spacing) * (component.Rect.Height / totalHeight))));
            }
            int i = 0;
            Rectangle tooltipRect = Rectangle.Empty;
            LocalizedString tooltip = null;
            foreach (Identifier itemIdentifier in itemIdentifiers)
            {
                if (MapEntityPrefab.FindByIdentifier(identifier: itemIdentifier) is not ItemPrefab itemPrefab) { continue; }

                int row = (int)Math.Floor(i / (float)slotCountPerRow);
                int slotsPerThisRow = Math.Min((slotCount - row * slotCountPerRow), slotCountPerRow);
                Vector2 slotPos = new Vector2(
                    component.Rect.Center.X + (slotSize.X + spacing) * (i % slotCountPerRow - slotsPerThisRow * 0.5f),
                    component.Rect.Bottom - (rows * (slotSize.Y + spacing)) + (slotSize.Y + spacing) * row);

                Rectangle slotRect = new Rectangle(slotPos.ToPoint(), slotSize);
                Inventory.SlotSpriteSmall.Draw(spriteBatch, slotPos,
                    scale: slotSize.X / (float)Inventory.SlotSpriteSmall.SourceRect.Width,
                    color: slotRect.Contains(PlayerInput.MousePosition) ? Color.White : Color.White * 0.6f);

                Sprite icon = itemPrefab.InventoryIcon ?? itemPrefab.Sprite;
                float iconScale = Math.Min(Math.Min(slotSize.X / icon.size.X, slotSize.Y / icon.size.Y), 2.0f) * 0.9f;
                icon.Draw(spriteBatch, slotPos + slotSize.ToVector2() * 0.5f, scale: iconScale);

                int count = jobPrefab.Prefab.JobItems[jobPrefab.Variant].Where(it => it.ShowPreview && it.ItemIdentifier == itemIdentifier).Sum(it => it.Amount);
                if (count > 1)
                {
                    string itemCountText = "x" + count;
                    GUIStyle.Font.DrawString(spriteBatch, itemCountText, slotPos + slotSize.ToVector2() - GUIStyle.Font.MeasureString(itemCountText) - Vector2.UnitX * 5, Color.White);
                }

                if (slotRect.Contains(PlayerInput.MousePosition))
                {
                    tooltipRect = slotRect;
                    tooltip = itemPrefab.Name + '\n' + itemPrefab.Description;
                }
                i++;
            }
            if (!tooltip.IsNullOrEmpty())
            {
                GUIComponent.DrawToolTip(spriteBatch, tooltip, tooltipRect);
            }
        }

        public void NewChatMessage(ChatMessage message)
        {
            float prevSize = chatBox.BarSize;

            while (chatBox.Content.CountChildren > 60)
            {
                chatBox.RemoveChild(chatBox.Content.Children.First());
            }
            
            LocalizedString displayedChatRow = ChatMessage.GetTimeStamp();
            if (message.Type == ChatMessageType.Private)
            {
                displayedChatRow += TextManager.Get("PrivateMessageTag") + " ";
            }
            else if (message.Type == ChatMessageType.Team)
            {
                displayedChatRow += TextManager.Get("PvP.ChatMode.Team.ChatPrefixTag") + " ";
            }
            displayedChatRow += message.TextWithSender;
            
            GUITextBlock msg = new GUITextBlock(new RectTransform(new Vector2(1.0f, 0.0f), chatBox.Content.RectTransform),
                text: RichString.Rich(displayedChatRow),
                textColor: message.Color,
                color: ((chatBox.CountChildren % 2) == 0) ? Color.Transparent : Color.Black * 0.1f,
                wrap: true, font: GUIStyle.SmallFont)
            {
                UserData = message,
                CanBeFocused = false
            };
            msg.CalculateHeightFromText();
            if (msg.RichTextData != null)
            {
                foreach (var data in msg.RichTextData)
                {
                    msg.ClickableAreas.Add(new GUITextBlock.ClickableArea()
                    {
                        Data = data,
                        OnClick = GameMain.NetLobbyScreen.SelectPlayer,
                        OnSecondaryClick = GameMain.NetLobbyScreen.ShowPlayerContextMenu
                    });
                }
            }
            msg.RectTransform.SizeChanged += Recalculate;
            void Recalculate()
            {
                msg.RectTransform.SizeChanged -= Recalculate;
                msg.CalculateHeightFromText();
                msg.RectTransform.SizeChanged += Recalculate;
            }

            if ((prevSize == 1.0f && chatBox.BarScroll == 0.0f) || (prevSize < 1.0f && chatBox.BarScroll == 1.0f))
            {
                chatBox.BarScroll = 1.0f;
            }
        }

        private bool SelectJobPreferencesTab(GUIButton button, object userData)
        {
            jobPreferencesButton.Selected = true;
            appearanceButton.Selected = false;

            JobPreferenceContainer.Visible = true;
            appearanceFrame.Visible = false;

            return false;
        }

        private bool SelectAppearanceTab(GUIButton button, object _)
        {
            jobPreferencesButton.Selected = false;
            appearanceButton.Selected = true;

            JobPreferenceContainer.Visible = false;
            appearanceFrame.Visible = true;

            appearanceFrame.ClearChildren();

            var info = GameMain.Client.CharacterInfo ?? Character.Controlled?.Info;
            CharacterAppearanceCustomizationMenu?.Dispose();
            CharacterAppearanceCustomizationMenu = new CharacterInfo.AppearanceCustomizationMenu(info, appearanceFrame)
            {
                OnHeadSwitch = menu =>
                {
                    UpdateJobPreferences(info);
                    SelectAppearanceTab(button, _);
                }
            };
            return false;
        }
        
        public bool SaveAppearance()
        {
            var info = GameMain.Client?.CharacterInfo;
            if (info?.Head == null) { return false; }

            var characterConfig = MultiplayerPreferences.Instance;
            
            characterConfig.TagSet.Clear();
            characterConfig.TagSet.UnionWith(info.Head.Preset.TagSet);
            characterConfig.HairIndex = info.Head.HairIndex;
            characterConfig.BeardIndex = info.Head.BeardIndex;
            characterConfig.MoustacheIndex = info.Head.MoustacheIndex;
            characterConfig.FaceAttachmentIndex = info.Head.FaceAttachmentIndex;
            characterConfig.HairColor = info.Head.HairColor;
            characterConfig.FacialHairColor = info.Head.FacialHairColor;
            characterConfig.SkinColor = info.Head.SkinColor;

            if (GameMain.GameSession?.IsRunning ?? false)
            {
                TabMenu.PendingChanges = true;
                CreateChangesPendingText();
            }
            GameSettings.SaveCurrentConfig();
            return true;
        }

        private bool SwitchJob(GUIButton _, object obj)
        {
            if (JobList == null || GameMain.Client == null) { return false; }

            int childIndex = JobList.SelectedIndex;
            var child = JobList.SelectedComponent;
            if (child == null) { return false; }

            bool moveToNext = obj != null;

            var jobPrefab = (obj as JobVariant)?.Prefab;
            object prevObj = child.UserData;

            var existingChild = JobList.Content.FindChild(d => (d.UserData is JobVariant prefab) && (prefab.Prefab == jobPrefab));
            if (existingChild != null && obj != null)
            {
                existingChild.UserData = prevObj;
            }
            child.UserData = obj;

            for (int i = 0; i < 2; i++)
            {
                if (i < 2 && JobList.Content.GetChild(i).UserData == null)
                {
                    JobList.Content.GetChild(i).UserData = JobList.Content.GetChild(i + 1).UserData;
                    JobList.Content.GetChild(i + 1).UserData = null;
                }
            }

            UpdateJobPreferences(GameMain.Client.CharacterInfo ?? Character.Controlled?.Info);

            if (moveToNext)
            {
                var emptyChild = JobList.Content.FindChild(c => c.UserData == null && c.CanBeFocused);
                if (emptyChild != null)
                {
                    JobList.Select(JobList.Content.GetChildIndex(emptyChild));
                }
                else
                {
                    JobList.Deselect();
                    if (JobSelectionFrame != null) { JobSelectionFrame.Visible = false; }
                }
            }
            else
            {
                OpenJobSelection(child, child.UserData);
            }

            return false;
        }

        private bool OpenJobSelection(GUIComponent _, object __)
        {
            if (JobSelectionFrame != null)
            {
                JobSelectionFrame.Visible = true;
                return true;
            }

            Point frameSize = new Point(characterInfoFrame.Rect.Width, (int)(characterInfoFrame.Rect.Height * 2 * 0.6f));
            JobSelectionFrame = new GUIFrame(new RectTransform(frameSize, GUI.Canvas, Anchor.TopLeft)
                { AbsoluteOffset = new Point(characterInfoFrame.Rect.Right - frameSize.X, characterInfoFrame.Rect.Bottom) }, style:"GUIFrameListBox");

            characterInfoFrame.RectTransform.SizeChanged += () =>
            {
                if (characterInfoFrame == null || JobSelectionFrame?.RectTransform == null) { return; }
                Point size = new Point(characterInfoFrame.Rect.Width, (int)(characterInfoFrame.Rect.Height * 2 * 0.6f));
                JobSelectionFrame.RectTransform.Resize(size);
                JobSelectionFrame.RectTransform.AbsoluteOffset = new Point(characterInfoFrame.Rect.Right - size.X, characterInfoFrame.Rect.Bottom);
            };

            new GUIFrame(new RectTransform(new Vector2(1.25f, 1.25f), JobSelectionFrame.RectTransform, anchor: Anchor.Center), style: "OuterGlow", color: Color.Black)
            {
                UserData = "outerglow",
                CanBeFocused = false
            };

            var rows = new GUILayoutGroup(new RectTransform(Vector2.One, JobSelectionFrame.RectTransform)) { Stretch = true };
            var row = new GUILayoutGroup(new RectTransform(Vector2.One, rows.RectTransform), true);

            GUIButton jobButton = null;

            var availableJobs = JobPrefab.Prefabs.Where(jobPrefab =>
                    !jobPrefab.HiddenJob && jobPrefab.MaxNumber > 0 && JobList.Content.Children.All(c => c.UserData is not JobVariant prefab || prefab.Prefab != jobPrefab)
            ).Select(j => new JobVariant(j, 0));

            availableJobs = availableJobs.Concat(
                JobPrefab.Prefabs.Where(jobPrefab =>
                    !jobPrefab.HiddenJob && jobPrefab.MaxNumber > 0 && JobList.Content.Children.Any(c => (c.UserData is JobVariant prefab) && prefab.Prefab == jobPrefab)
            ).Select(j => (JobVariant)JobList.Content.FindChild(c => (c.UserData is JobVariant prefab) && prefab.Prefab == j).UserData));

            availableJobs = availableJobs.ToList();

            int itemsInRow = 0;

            foreach (var jobPrefab in availableJobs)
            {
                if (itemsInRow >= 3)
                {
                    row = new GUILayoutGroup(new RectTransform(Vector2.One, rows.RectTransform), true);
                    itemsInRow = 0;
                }

                jobButton = new GUIButton(new RectTransform(new Vector2(1.0f / 3.0f, 1.0f), row.RectTransform), style: "ListBoxElementSquare")
                {
                    UserData = jobPrefab,
                    OnClicked = (btn, usdt) =>
                    {
                        if (btn.IsParentOf(GUI.MouseOn)) return false;
                        return SwitchJob(btn, usdt);
                    }
                };
                itemsInRow++;

                var images = AddJobSpritesToGUIComponent(jobButton, jobPrefab.Prefab,
                    team: TeamPreference,
                    isPvPMode: SelectedMode == GameModePreset.PvP,
                    selectedByPlayer: false);
                if (images != null && images.Length > 1)
                {
                    jobPrefab.Variant = Math.Min(jobPrefab.Variant, images.Length);
                    int currVisible = jobPrefab.Variant;
                    GUIButton currSelected = null;
                    for (int variantIndex = 0; variantIndex < images.Length; variantIndex++)
                    {
                        images[variantIndex].Visible = currVisible == variantIndex;                        

                        var variantButton = CreateJobVariantButton(jobPrefab, variantIndex, images.Length, jobButton);
                        variantButton.OnClicked = (btn, obj) =>
                        {
                            if (currSelected != null) { currSelected.Selected = false; }
                            int selectedVariantIndex = ((JobVariant)obj).Variant;
                            btn.Parent.UserData = obj;
                            for (int i = 0; i < images.Length; i++)
                            {
                                images[i].Visible = selectedVariantIndex == i;                                
                            }
                            currSelected = btn;
                            currSelected.Selected = true;
                            return false;
                        };

                        if (currVisible == variantIndex)
                        {
                            currSelected = variantButton;
                        }
                    }

                    if (currSelected != null)
                    {
                        currSelected.Selected = true;
                    }
                }
            }

            return true;
        }

        private static GUIImage[] AddJobSpritesToGUIComponent(GUIComponent parent, JobPrefab jobPrefab, CharacterTeamType team, bool isPvPMode, bool selectedByPlayer)
        {
            GUIFrame innerFrame = null;
            List<Sprite> outfitPreviews = jobPrefab.GetJobOutfitSprites(team, isPvPMode).ToList();

            innerFrame = new GUIFrame(new RectTransform(Vector2.One * 0.85f, parent.RectTransform, Anchor.Center), style: null)
            {
                CanBeFocused = false
            };

            GUIImage[] retVal = new GUIImage[outfitPreviews.Count];
            if (outfitPreviews != null && outfitPreviews.Any())
            {
                for (int i = 0; i < outfitPreviews.Count; i++)
                {
                    Sprite outfitPreview = outfitPreviews[i];        
                    float aspectRatio = outfitPreview.size.Y / outfitPreview.size.X;
                    retVal[i] = new GUIImage(new RectTransform(new Vector2(0.7f / aspectRatio, 0.7f), innerFrame.RectTransform, Anchor.Center), outfitPreview, scaleToFit: true)
                    {
                        PressedColor = Color.White,
                        CanBeFocused = false
                    };                    
                }
            }

            new GUIFrame(new RectTransform(new Vector2(1.0f, 0.35f), parent.RectTransform, Anchor.BottomCenter), style: "OuterGlow")
            {
                Color = Color.Black,
                HoverColor = Color.Black,
                PressedColor = Color.Black,
                SelectedColor = Color.Black,
                CanBeFocused = false
            };

            var textBlock = new GUITextBlock(
              innerFrame.CountChildren == 0 ?
                  new RectTransform(Vector2.One, parent.RectTransform, Anchor.Center) :
                  new RectTransform(new Vector2(selectedByPlayer ? 0.55f : 0.95f, 0.3f), parent.RectTransform, Anchor.BottomCenter),
              jobPrefab.Name, wrap: true, textAlignment: Alignment.BottomCenter)
            {
                Padding = Vector4.Zero,
                HoverColor = Color.Transparent,
                SelectedColor = Color.Transparent,
                TextColor = jobPrefab.UIColor,
                HoverTextColor = Color.Lerp(jobPrefab.UIColor, Color.White, 0.5f),
                CanBeFocused = false,
                AutoScaleHorizontal = true
            };
            textBlock.TextAlignment = textBlock.WrappedText.Contains('\n') ? Alignment.BottomCenter : Alignment.Center;
            textBlock.RectTransform.SizeChanged += () => { textBlock.TextScale = 1.0f; };

            return retVal;
        }

        public void SelectMode(int modeIndex)
        {
            if (modeIndex < 0 || modeIndex >= ModeList.Content.CountChildren) { return; }

            if ((GameModePreset)ModeList.Content.GetChild(modeIndex).UserData != GameModePreset.MultiPlayerCampaign)
            {
                ToggleCampaignMode(false);
            }

            var prevMode = ModeList.Content.GetChild(selectedModeIndex).UserData as GameModePreset;

            if ((HighlightedModeIndex == selectedModeIndex || HighlightedModeIndex < 0) && ModeList.SelectedIndex != modeIndex) { ModeList.Select(modeIndex, GUIListBox.Force.Yes); }
            selectedModeIndex = modeIndex;

            if ((prevMode == GameModePreset.PvP) != (SelectedMode == GameModePreset.PvP))
            {
                SaveAppearance();
                UpdatePlayerFrame(null);
                GameMain.Client.ConnectedClients.ForEach(SetPlayerNameAndJobPreference);
                ResetPvpTeamSelection();
            }

            if (SelectedMode != GameModePreset.MultiPlayerCampaign && GameMain.GameSession?.GameMode is CampaignMode && Selected == this)
            {
                GameMain.GameSession = null;
            }

            respawnModeSelection.Refresh(); // not all respawn modes are compatible with all game modes
            RefreshGameModeContent();
            RefreshEnabledElements();
            UpdateDisembarkPointListFromServerSettings();
        }

        public void HighlightMode(int modeIndex)
        {
            if (modeIndex < 0 || modeIndex >= ModeList.Content.CountChildren) { return; }

            HighlightedModeIndex = modeIndex;
            RefreshGameModeContent();
            RefreshEnabledElements();
        }

        private void RefreshMissionTypes()
        {
            IEnumerable<Type> suitableMissionClasses;
            if (SelectedMode == GameModePreset.Mission)
            {
                suitableMissionClasses = MissionPrefab.CoOpMissionClasses.Values;
            }
            else if (SelectedMode == GameModePreset.PvP)
            {
                suitableMissionClasses = MissionPrefab.PvPMissionClasses.Values;
            }
            else
            {
                return;
            }
            for (int i = 0; i < missionTypeTickBoxes.Length; i++)
            {
                Identifier missionType = (Identifier)missionTypeTickBoxes[i].UserData;
                missionTypeTickBoxes[i].Parent.Visible =
                    MissionPrefab.Prefabs.Any(p => p.Type == missionType && suitableMissionClasses.Contains(p.MissionClass));
            }
        }
        
        private void RefreshGameModeSettingsContent()
        {
            foreach (var element in campaignHiddenElements)
            {
                SetElementVisible(element, SelectedMode != GameModePreset.MultiPlayerCampaign && 
                                           SelectedMode != GameModePreset.SinglePlayerCampaign);
            }
            foreach (var element in pvpOnlyElements)
            {
                SetElementVisible(element, SelectedMode == GameModePreset.PvP);
            }

            if (respawnTabButton != null && upgradesTabButton != null)
            {
                if (SelectedMode == GameModePreset.MultiPlayerCampaign)
                {
                    SelectRespawnTab();
                    respawnTabButton.Enabled = upgradesTabButton.Enabled = false;
                }
                else
                {
                    respawnTabButton.Enabled = upgradesTabButton.Enabled = true;
                }
            }

            static void SetElementVisible(GUIComponent element, bool enabled)
            {
                element.Visible = enabled;
            }

            gameModeSettingsLayout.Recalculate();
        }

        private void RefreshGameModeContent()
        {
            if (GameMain.Client == null) { return; }

            foreach (var subElement in SubList.Content.Children)
            {
                subElement.CanBeFocused = true;
                foreach (var textBlock in subElement.GetAllChildren<GUITextBlock>())
                {
                    textBlock.Enabled = true;
                }                
            }

            SubList.Content.RectTransform.SortChildren((rt1, rt2) =>
            {
                SubmarineInfo s1 = rt1.GUIComponent.UserData as SubmarineInfo;
                SubmarineInfo s2 = rt2.GUIComponent.UserData as SubmarineInfo;
                return s1.Name.CompareTo(s2.Name);
            });

            autoRestartBox.Parent.Visible = true;

            UpdateDisembarkPointListFromServerSettings();

            bool isPvP = SelectedMode == GameModePreset.PvP;
            foreach (GUIComponent child in SubList.Content.Children)
            {
                var container = child.GetChild<GUILayoutGroup>();

                var imageFrame = container.GetChild<GUIFrame>();

                var coalIcon = imageFrame.GetChildByUserData(CoalitionIconUserData);
                var sepIcon = imageFrame.GetChildByUserData(SeparatistsIconUserData);
                coalIcon.Visible = isPvP;
                sepIcon.Visible = isPvP;

                if (GameMain.NetworkMember.ServerSettings.SubSelectionMode != SelectionMode.Vote)
                {
                    coalIcon.Enabled = sepIcon.Enabled = false;
                    if (child.UserData is not SubmarineInfo info) { continue; }
                    if (SelectedSub == info) { coalIcon.Enabled = true; }
                    if (SelectedEnemySub == info) { sepIcon.Enabled = true; }
                }
            }

            UpdateSelectedSub(isPvP ? MultiplayerPreferences.Instance.TeamPreference : CharacterTeamType.None);

            RefreshGameModeSettingsContent();
            if (SelectedMode == GameModePreset.Mission || SelectedMode == GameModePreset.PvP)
            {
                MissionTypeFrame.Visible = true;
                CampaignFrame.Visible = CampaignSetupFrame.Visible = false;
                RefreshMissionTypes();
            }
            else if (SelectedMode == GameModePreset.MultiPlayerCampaign)
            {
                MissionTypeFrame.Visible = autoRestartBox.Parent.Visible = false;

                if (GameMain.GameSession?.GameMode is CampaignMode campaign && campaign.Map != null)
                {
                    //campaign running
                    CampaignFrame.Visible = QuitCampaignButton.Enabled = CampaignMode.AllowedToManageCampaign(ClientPermissions.ManageRound);
                    CampaignSetupFrame.Visible = false;
                }
                else
                {
                    CampaignFrame.Visible = false;
                    CampaignSetupFrame.Visible = true;
                    if (!CampaignMode.AllowedToManageCampaign(ClientPermissions.ManageRound))
                    {
                        CampaignSetupFrame.ClearChildren();
                        new GUITextBlock(new RectTransform(new Vector2(0.8f, 0.5f), CampaignSetupFrame.RectTransform, Anchor.Center),
                            TextManager.Get("campaignstarting"), font: GUIStyle.SubHeadingFont, textAlignment: Alignment.Center, wrap: true);
                    }
                }

                if (CampaignSetupUI != null)
                {
                    foreach (var subElement in SubList.Content.Children)
                    {
                        var sub = subElement.UserData as SubmarineInfo;
                        bool tooExpensive = sub.Price > CampaignSettings.CurrentSettings.InitialMoney;
                        if (tooExpensive || !sub.IsCampaignCompatible)
                        {
                            foreach (var textBlock in subElement.GetAllChildren<GUITextBlock>())
                            {
                                textBlock.DisabledTextColor = (textBlock.UserData as string == "pricetext" && tooExpensive ? GUIStyle.Red : GUIStyle.TextColorNormal) * 0.7f;
                                textBlock.Enabled = false;
                            }
                        }
                    }
                    SubList.Content.RectTransform.SortChildren((rt1, rt2) =>
                    {
                        SubmarineInfo s1 = rt1.GUIComponent.UserData as SubmarineInfo;
                        SubmarineInfo s2 = rt2.GUIComponent.UserData as SubmarineInfo;
                        int p1 = s1.Price;
                        if (!s1.IsCampaignCompatible) { p1 += 100000; }
                        int p2 = s2.Price;
                        if (!s2.IsCampaignCompatible) { p2 += 100000; }
                        return p1.CompareTo(p2) * 100 + s1.Name.CompareTo(s2.Name);
                    });
                }
            }
            else
            {
                MissionTypeFrame.Visible = CampaignFrame.Visible = CampaignSetupFrame.Visible = false;
                CampaignFrame.Visible = CampaignSetupFrame.Visible = false;
            }

            ReadyToStartBox.Parent.Visible = !GameMain.Client.GameStarted;
            RefreshStartButtonVisibility();
            RefreshOutpostDropdown();            
        }

        public void RefreshStartButtonVisibility()
        {
            if (CampaignSetupUI != null && CampaignSetupFrame is { Visible: true })
            {
                //setting up a campaign -> start button only visible if we're in the "new game" tab (load game menu not visible) 
                StartButton.Visible =
                    !GameMain.Client.GameStarted &&
                    !CampaignSetupUI.LoadGameMenuVisible &&
                    (GameMain.Client.HasPermission(ClientPermissions.ManageRound) || GameMain.Client.HasPermission(ClientPermissions.ManageCampaign));
            }
            else
            {
                //if a campaign is currently running, we must show the start button to allow continuing
                bool campaignActive = GameMain.GameSession?.GameMode is CampaignMode;
                StartButton.Visible =
                    (SelectedMode != GameModePreset.MultiPlayerCampaign || campaignActive) &&
                    !GameMain.Client.GameStarted && GameMain.Client.HasPermission(ClientPermissions.ManageRound);
            }

            StartButton.Enabled = true;
            if (GameSession.ShouldApplyDisembarkPoints(SelectedMode))
            {
                StartButton.Enabled = GameSession.ValidatedDisembarkPoints(SelectedMode, MissionTypes);

                StartButton.ToolTip =
                    !StartButton.Enabled
                        ? TextManager.Get("DisembarkPointsNotValid")
                        : string.Empty;
            }
        }
        
        public void RefreshChatrow()
        {
            chatRow.ClearChildren();
            
            // Team chat only makes sense when in a team (in "player preference" team selection mode, team assignments only happen at round start)
            if (SelectedMode == GameModePreset.PvP && GameMain.Client?.ServerSettings?.PvpTeamSelectionMode == PvpTeamSelectionMode.PlayerChoice
                && MultiplayerPreferences.Instance.TeamPreference != CharacterTeamType.None)
            {
                var chatSelectorRT = new RectTransform(new Vector2(0.25f, 1.0f), chatRow.RectTransform, Anchor.CenterLeft);
                chatSelector = new GUIDropDown(chatSelectorRT, elementCount: 2)
                {
                    OnSelected = (_, userdata) =>
                    {
                        TeamChatSelected = (bool)userdata;
                        return true;
                    }
                };
                chatSelector.AddItem(TextManager.Get($"PvP.ChatMode.Team"), userData: true, color: ChatMessage.MessageColor[(int)ChatMessageType.Team]);
                chatSelector.AddItem(TextManager.Get($"PvP.ChatMode.All"), userData: false, color: ChatMessage.MessageColor[(int)ChatMessageType.Default]);
                chatSelector.SelectItem(TeamChatSelected);
            }
            else
            {
                TeamChatSelected = false;
            }

            if (chatInput != null)
            {
                chatInput.RectTransform.Parent = chatRow.RectTransform;
            }
            else
            {
                chatInput = new GUITextBox(new RectTransform(new Vector2(0.75f, 1.0f), chatRow.RectTransform, Anchor.CenterRight))
                {
                    MaxTextLength = ChatMessage.MaxLength,
                    Font = GUIStyle.SmallFont,
                    DeselectAfterMessage = false
                };

                micIcon = new GUIImage(new RectTransform(new Vector2(0.05f, 1.0f), chatRow.RectTransform), style: "GUIMicrophoneUnavailable");
                chatInput.Select();
            }

            //this needs to be done even if we're using the existing chatinput instance instead of creating a new one,
            //because the client might not have existed when the input box was first created
            if (GameMain.Client != null)
            {
                chatInput.ResetDelegates();
                chatInput.OnEnterPressed = GameMain.Client.EnterChatMessage;
                chatInput.OnTextChanged += GameMain.Client.TypingChatMessage;
                chatInput.OnDeselected += (sender, key) =>
                {
                    GameMain.Client?.ChatBox.ChatManager.Clear();
                };
                ChatManager.RegisterKeys(chatInput, GameMain.Client.ChatBox.ChatManager);    
            }
            
            chatRow.Recalculate();
        }

        public void ToggleCampaignMode(bool enabled)
        {
            if (!enabled)
            {
                //remove campaign character from the panel
                if (campaignCharacterInfo != null) 
                { 
                    campaignCharacterInfo = null;
                    UpdatePlayerFrame(null);
                    SetSpectate(spectateBox.Selected);
                }
                CampaignCharacterDiscarded = false;
            }
            RefreshEnabledElements();
            if (enabled && SelectedMode != GameModePreset.MultiPlayerCampaign)
            {
                ModeList.Select(GameModePreset.MultiPlayerCampaign, GUIListBox.Force.Yes);
            }
        }

        public void TryDisplayCampaignSubmarine(SubmarineInfo submarine)
        {
            string name = submarine?.Name;
            bool displayed = false;
            SubList.OnSelected -= VotableClicked;
            SubList.Deselect();
            subPreviewContainer.ClearChildren();
            foreach (GUIComponent child in SubList.Content.Children)
            {
                if (child.UserData is not SubmarineInfo sub) { continue; }
                //just check the name, even though the campaign sub may not be the exact same version
                //we're selecting the sub just for show, the selection is not actually used for anything
                if (sub.Name == name)
                {
                    SubList.Select(sub);
                    if (SubmarineInfo.SavedSubmarines.Contains(sub))
                    {
                        CreateSubPreview(sub);
                        displayed = true;
                    }
                    break;
                }
            }
            SubList.OnSelected += VotableClicked;
            if (!displayed)
            {
                CreateSubPreview(submarine);
            }
            UpdateSubVisibility();
        }

        private bool ViewJobInfo(GUIButton button, object obj)
        {
            if (button.UserData is not JobVariant jobPrefab) { return false; }

            JobInfoFrame = jobPrefab.Prefab.CreateInfoFrame(isPvP: SelectedMode == GameModePreset.PvP, out GUIComponent buttonContainer);
            GUIButton closeButton = new GUIButton(new RectTransform(new Vector2(0.25f, 0.05f), buttonContainer.RectTransform, Anchor.BottomRight),
                TextManager.Get("Close"))
            {
                OnClicked = CloseJobInfo
            };
            JobInfoFrame.OnClicked = (btn, userdata) => { if (GUI.MouseOn == btn || GUI.MouseOn == btn.TextBlock) CloseJobInfo(btn, userdata); return true; };

            return true;
        }

        private bool CloseJobInfo(GUIButton button, object obj)
        {
            JobInfoFrame = null;
            return true;
        }

        private void UpdateJobPreferences(CharacterInfo characterInfo)
        {
            if (characterInfo == null) { return; }

            GUICustomComponent characterIcon = JobPreferenceContainer.GetChild<GUICustomComponent>();
            JobPreferenceContainer.RemoveChild(characterIcon);
            characterInfo.CreateIcon(new RectTransform(new Vector2(1.0f, 0.4f), JobPreferenceContainer.RectTransform, Anchor.TopCenter) { RelativeOffset = new Vector2(0.0f, 0.025f) });

            GUIListBox listBox = JobPreferenceContainer.GetChild<GUIListBox>();
            /*foreach (Sprite sprite in jobPreferenceSprites) { sprite.Remove(); }
            jobPreferenceSprites.Clear();*/

            List<MultiplayerPreferences.JobPreference> jobPreferences = new List<MultiplayerPreferences.JobPreference>();

            bool disableNext = false;
            for (int i = 0; i < listBox.Content.CountChildren; i++)
            {
                GUIComponent slot = listBox.Content.GetChild(i);

                slot.ClearChildren();

                slot.CanBeFocused = !disableNext;
                if (slot.UserData is JobVariant jobPrefab)
                {
                    var images = AddJobSpritesToGUIComponent(slot, jobPrefab.Prefab,
                        team: TeamPreference,
                        isPvPMode: SelectedMode == GameModePreset.PvP,
                        selectedByPlayer: true);
                    for (int variantIndex = 0; variantIndex < images.Length; variantIndex++)
                    {
                        int selectedVariantIndex = Math.Min(jobPrefab.Variant, images.Length);
                        images[variantIndex].Visible = images.Length == 1 || selectedVariantIndex == variantIndex;
                        
                        if (images.Length > 1)
                        {
                            var variantButton = CreateJobVariantButton(jobPrefab, variantIndex, images.Length, slot);
                            variantButton.OnClicked = (btn, obj) =>
                            {
                                btn.Parent.UserData = obj;
                                UpdateJobPreferences(characterInfo);
                                return false;
                            };
                        }
                    }

                    // Info button
                    new GUIButton(new RectTransform(new Vector2(0.15f), slot.RectTransform, Anchor.BottomLeft, scaleBasis: ScaleBasis.BothWidth) { RelativeOffset = new Vector2(0.075f) },
                        style: "GUIButtonInfo")
                    {
                        UserData = jobPrefab,
                        OnClicked = ViewJobInfo
                    };

                    // Remove button
                    new GUIButton(new RectTransform(new Vector2(0.15f), slot.RectTransform, Anchor.BottomRight, scaleBasis: ScaleBasis.BothWidth) { RelativeOffset = new Vector2(0.075f) },
                        style: "GUICancelButton")
                    {
                        UserData = i,
                        OnClicked = (btn, obj) =>
                        {
                            JobList.Select((int)obj, GUIListBox.Force.Yes);
                            SwitchJob(btn, null);
                            if (JobSelectionFrame != null) { JobSelectionFrame.Visible = false; }
                            JobList.Deselect();

                            return false;
                        }
                    };

                    jobPreferences.Add(new MultiplayerPreferences.JobPreference(jobPrefab.Prefab.Identifier, jobPrefab.Variant));
                }
                else
                {
                    new GUITextBlock(new RectTransform(new Vector2(1.0f, 0.6f), slot.RectTransform), (i + 1).ToString(),
                        textColor: Color.White * (disableNext ? 0.15f : 0.5f),
                        textAlignment: Alignment.Center,
                        font: GUIStyle.LargeFont)
                    {
                        CanBeFocused = false
                    };

                    if (!disableNext)
                    {
                        new GUITextBlock(new RectTransform(new Vector2(1.0f, 0.4f), slot.RectTransform, Anchor.BottomCenter), TextManager.Get("clicktoselectjob"),
                            font: GUIStyle.SmallFont,
                            wrap: true,
                            textAlignment: Alignment.Center)
                        {
                            CanBeFocused = false
                        };
                    }

                    disableNext = true;
                }
            }
            GameMain.Client.ForceNameJobTeamUpdate();

            if (!MultiplayerPreferences.Instance.AreJobPreferencesEqual(jobPreferences))
            {
                if (GameMain.GameSession?.IsRunning ?? false)
                {
                    TabMenu.PendingChanges = true;
                    CreateChangesPendingText();
                }

                MultiplayerPreferences.Instance.JobPreferences.Clear();
                MultiplayerPreferences.Instance.JobPreferences.AddRange(jobPreferences);
                GameSettings.SaveCurrentConfig();
            }
        }

        private static GUIButton CreateJobVariantButton(JobVariant jobPrefab, int variantIndex, int variantCount, GUIComponent slot)
        {
            float relativeSize = 0.15f;

            var btn = new GUIButton(new RectTransform(new Vector2(relativeSize), slot.RectTransform, Anchor.TopCenter, scaleBasis: ScaleBasis.BothHeight)
                { RelativeOffset = new Vector2(relativeSize * 1.3f * (variantIndex - (variantCount - 1) / 2.0f), 0.02f) },
                (variantIndex + 1).ToString(), style: "JobVariantButton")
            {
                Selected = jobPrefab.Variant == variantIndex,
                UserData = new JobVariant(jobPrefab.Prefab, variantIndex),
            };

            return btn;
        }

        public readonly struct FailedSubInfo
        {
            public readonly string Name;
            public readonly string Hash;
            public FailedSubInfo(string name, string hash) { Name = name; Hash = hash; }
            public void Deconstruct(out string name, out string hash) { name = Name; hash = Hash; }

            private static bool StringsEqual(string a, string b)
                => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

            public static bool operator ==(FailedSubInfo a, FailedSubInfo b)
                => StringsEqual(a.Name, b.Name) && StringsEqual(a.Hash, b.Hash);

            public static bool operator !=(FailedSubInfo a, FailedSubInfo b)
                => !(a == b);

            public override int GetHashCode()
            {
                return HashCode.Combine(Name, Hash);
            }

            public override bool Equals(object obj)
            {
                return obj is FailedSubInfo info &&
                       Name == info.Name &&
                       Hash == info.Hash;
            }
        }

        public FailedSubInfo? FailedSelectedSub;
        public FailedSubInfo? FailedSelectedEnemySub;
        public FailedSubInfo? FailedSelectedShuttle;

        public List<FailedSubInfo> FailedCampaignSubs = new List<FailedSubInfo>();
        public List<FailedSubInfo> FailedOwnedSubs = new List<FailedSubInfo>();

        public bool TrySelectSub(string subName, string md5Hash, SelectedSubType type, GUIListBox subList, bool showPreview = true)
        {
            UpdateSubVisibility();
            if (GameMain.Client == null) { return false; }

            //already downloading the selected sub file
            if (GameMain.Client.FileReceiver.ActiveTransfers.Any(t => t.FileName == subName + ".sub"))
            {
                return false;
            }

            SubmarineInfo sub = subList.Content.Children
                .FirstOrDefault(c => c.UserData is SubmarineInfo s && s.Name == subName && s.MD5Hash?.StringRepresentation == md5Hash)?
                .UserData as SubmarineInfo;

            //matching sub found and already selected, all good
            if (sub != null)
            {
                if (subList == SubList && showPreview)
                {
                    if (type is not SelectedSubType.EnemySub || MultiplayerPreferences.Instance.TeamPreference == CharacterTeamType.Team2)
                    {
                        CreateSubPreview(sub);
                    }
                }

                SubmarineInfo selectedSub = type switch
                {
                    SelectedSubType.Sub => SelectedSub,
                    SelectedSubType.EnemySub => SelectedEnemySub,
                    SelectedSubType.Shuttle => SelectedShuttle,
                    _ => null
                };

                if (selectedSub != null && selectedSub.MD5Hash?.StringRepresentation == md5Hash && Barotrauma.IO.File.Exists(sub.FilePath))
                {
                    //ensure the selected sub matches the correct submarineInfo instance (which may have been just downloaded from the server)
                    switch (type)
                    {
                        case SelectedSubType.Sub:
                            SelectedSub = sub;
                            break;
                        case SelectedSubType.EnemySub:
                            SelectedEnemySub = sub;
                            break;
                    }
                    return true;
                }
            }

            //sub not found, see if we have a sub with the same name
            if (sub == null)
            {
                sub = subList.Content.Children
                    .FirstOrDefault(c => c.UserData is SubmarineInfo s && s.Name == subName)?
                    .UserData as SubmarineInfo;
            }

            //found a sub that at least has the same name, select it
            if (sub != null)
            {
                if (subList.Parent is GUIDropDown subDropDown)
                {
                    subDropDown.SelectItem(sub);
                }
                else
                {
                    subList.OnSelected -= VotableClicked;

                    var preference = MultiplayerPreferences.Instance.TeamPreference;
                    switch (type)
                    {
                        case SelectedSubType.Sub:
                            if (preference is CharacterTeamType.Team1 or CharacterTeamType.None)
                            {
                                subList.Select(sub);
                            }
                            SelectedSub = sub;
                            break;
                        case SelectedSubType.EnemySub:
                            if (preference is CharacterTeamType.Team2)
                            {
                                subList.Select(sub);
                            }
                            SelectedEnemySub = sub;
                            break;
                    }
                    subList.OnSelected += VotableClicked;
                }

                switch (type)
                {
                    case SelectedSubType.Sub:
                        FailedSelectedSub = null;
                        break;
                    case SelectedSubType.EnemySub:
                        FailedSelectedEnemySub = null;
                        break;
                    case SelectedSubType.Shuttle:
                        FailedSelectedShuttle = null;
                        break;
                }

                //hashes match, all good
                if (sub.MD5Hash?.StringRepresentation == md5Hash && SubmarineInfo.SavedSubmarines.Contains(sub))
                {
                    return true;
                }
            }

            //-------------------------------------------------------------------------------------
            //if we get to this point, a matching sub was not found or it has an incorrect MD5 hash

            switch (type)
            {
                case SelectedSubType.Sub:
                    FailedSelectedSub = new FailedSubInfo(subName, md5Hash);
                    break;
                case SelectedSubType.EnemySub:
                    FailedSelectedEnemySub = new FailedSubInfo(subName, md5Hash);
                    break;
                case SelectedSubType.Shuttle:
                    FailedSelectedShuttle = new FailedSubInfo(subName, md5Hash);
                    break;
            }

            LocalizedString errorMsg = "";
            if (sub == null || !SubmarineInfo.SavedSubmarines.Contains(sub))
            {
                errorMsg = TextManager.GetWithVariable("SubNotFoundError", "[subname]", subName) + " ";
            }
            else if (sub.MD5Hash?.StringRepresentation == null)
            {
                errorMsg = TextManager.GetWithVariable("SubLoadError", "[subname]", subName) + " ";
                GUITextBlock textBlock = subList.Content.GetChildByUserData(sub)?.GetChild<GUITextBlock>();
                if (textBlock != null) { textBlock.TextColor = GUIStyle.Red; }
            }
            else
            {
                errorMsg = TextManager.GetWithVariables("SubDoesntMatchError",
                    ("[subname]", sub.Name),
                    ("[myhash]", sub.MD5Hash.ShortRepresentation),
                    ("[serverhash]", Md5Hash.GetShortHash(md5Hash))) + " ";
            }

            if (GameMain.Client.ServerSettings.AllowFileTransfers)
            {
                GameMain.Client?.RequestFile(FileTransferType.Submarine, subName, md5Hash);
            }
            else
            {
                new GUIMessageBox(TextManager.Get("DownloadSubLabel"), errorMsg);
            }
            return false;
        }

        public enum SubmarineDeliveryData
        {
            Owned,
            Campaign
        }
        
        public bool CheckIfCampaignSubMatches(SubmarineInfo serverSubmarine, SubmarineDeliveryData deliveryData)
        {
            if (GameMain.Client == null) { return false; }

            //already downloading the selected sub file
            if (GameMain.Client.FileReceiver.ActiveTransfers.Any(t => t.FileName == serverSubmarine.Name + ".sub"))
            {
                return false;
            }

            SubmarineInfo purchasableSub = SubmarineInfo.SavedSubmarines.FirstOrDefault(s => s.Name == serverSubmarine.Name && s.MD5Hash?.StringRepresentation == serverSubmarine.MD5Hash?.StringRepresentation);
            if (purchasableSub != null)
            {
                return true;
            }

            FailedSubInfo fileInfo = new FailedSubInfo(serverSubmarine.Name, serverSubmarine.MD5Hash.StringRepresentation);

            switch (deliveryData)
            {
                case SubmarineDeliveryData.Owned:
                    FailedOwnedSubs.Add(fileInfo);
                    break;
                case SubmarineDeliveryData.Campaign:
                    FailedCampaignSubs.Add(fileInfo);
                    break;
            }

            GameMain.Client?.RequestFile(FileTransferType.Submarine, fileInfo.Name, fileInfo.Hash);

            return false;
        }

        private void CreateSubPreview(SubmarineInfo sub)
        {
            subPreviewContainer?.ClearChildren();
            sub.CreatePreviewWindow(subPreviewContainer);
            RecalculateSubDescription();
        }

        private void RecalculateSubDescription()
        {
            var descriptionBox = subPreviewContainer?.FindChild("descriptionbox", recursive: true);
            if (descriptionBox != null && characterInfoFrame != null)
            {
                //if description box and character info box are roughly the same size, scale them to the same size
                if (Math.Abs(descriptionBox.Rect.Height - characterInfoFrame.Rect.Height) < 80 * GUI.Scale)
                {
                    descriptionBox.RectTransform.MaxSize = new Point(descriptionBox.Rect.Width, characterInfoFrame.Rect.Height);
                }
            }
        }

        private readonly List<SubmarineInfo> visibilityMenuOrder = new List<SubmarineInfo>();
        public const string SeparatistsIconUserData = "separatistsIcon";
        public const string CoalitionIconUserData = "coalitionIcon";

        private void CreateSubmarineVisibilityMenu()
        {
            var messageBox = new GUIMessageBox(TextManager.Get("SubmarineVisibility"), "",
                buttons: Array.Empty<LocalizedString>(),
                relativeSize: new Vector2(0.75f, 0.75f));
            messageBox.Content.ChildAnchor = Anchor.TopCenter;
            var columns = new GUILayoutGroup(new RectTransform(new Vector2(1.0f, 0.9f), messageBox.Content.RectTransform), isHorizontal: true);

            GUILayoutGroup createColumn(float width)
                => new GUILayoutGroup(new RectTransform(new Vector2(width, 1.0f), columns.RectTransform))
                    { Stretch = true };
            
            GUIListBox createColumnListBox(string labelTag)
            {
                var column = createColumn(0.45f);
                var label = new GUITextBlock(new RectTransform(new Vector2(1.0f, 0.1f), column.RectTransform),
                    TextManager.Get(labelTag), textAlignment: Alignment.Center);
                return new GUIListBox(new RectTransform(new Vector2(1.0f, 0.9f), column.RectTransform))
                {
                    CurrentSelectMode = GUIListBox.SelectMode.RequireShiftToSelectMultiple,
                    CurrentDragMode = GUIListBox.DragMode.DragOutsideBox,
                    HideDraggedElement = true
                };
            }
            
            void handleDraggingAcrossLists(GUIListBox from, GUIListBox to)
            {
                //TODO: put this in a static class once modding-refactor gets merged
                
                if (to.Rect.Contains(PlayerInput.MousePosition) && from.DraggedElement != null)
                {
                    //move the dragged elements to the index determined previously
                    var draggedElement = from.DraggedElement;
                
                    var selected = from.AllSelected.ToList();
                    selected.Sort((a, b) => from.Content.GetChildIndex(a) - from.Content.GetChildIndex(b));
                
                    float oldCount = to.Content.CountChildren;
                    float newCount = oldCount + selected.Count;
                
                    var offset = draggedElement.RectTransform.AbsoluteOffset;
                    offset += from.Content.Rect.Location;
                    offset -= to.Content.Rect.Location;
                
                    for (int i = 0; i < selected.Count; i++)
                    {
                        var c = selected[i];
                        c.Parent.RemoveChild(c);
                        c.RectTransform.Parent = to.Content.RectTransform;
                        c.RectTransform.RepositionChildInHierarchy((int)oldCount+i);
                    }

                    from.DraggedElement = null;
                    from.Deselect();
                    from.RecalculateChildren();
                    from.RectTransform.RecalculateScale(true);
                    to.RecalculateChildren();
                    to.RectTransform.RecalculateScale(true);
                    to.Select(selected);
                
                    //recalculate the dragged element's offset so it doesn't jump around
                    draggedElement.RectTransform.AbsoluteOffset = offset;
                
                    to.DraggedElement = draggedElement;

                    to.BarScroll *= (oldCount / newCount);
                }
            }

            var visibleSubsList = createColumnListBox("VisibleSubmarines");
            var centerColumn = createColumn(0.1f);

            void centerSpacing()
            {
                new GUIFrame(new RectTransform(new Vector2(1.0f, 0.4f), centerColumn.RectTransform), style: null);
            }

            GUIButton centerButton(string style)
                => new GUIButton(
                    new RectTransform(new Vector2(1.0f, 0.1f), centerColumn.RectTransform),
                    style: style);

            var hiddenSubsList = createColumnListBox("HiddenSubmarines");

            void addSubToList(SubmarineInfo sub, GUIListBox list)
            {
                var modFrame = new GUIFrame(new RectTransform(new Vector2(1.0f, 0.08f), list.Content.RectTransform),
                    style: "ListBoxElement")
                {
                    UserData = sub
                };
                
                var frameContent = new GUILayoutGroup(new RectTransform(new Vector2(0.95f, 0.9f), modFrame.RectTransform, Anchor.Center), isHorizontal: true, childAnchor: Anchor.CenterLeft)
                {
                    Stretch = true,
                    RelativeSpacing = 0.02f
                };
                
                var dragIndicator = new GUIButton(new RectTransform(new Vector2(0.5f, 0.5f), frameContent.RectTransform, scaleBasis: ScaleBasis.BothHeight),
                    style: "GUIDragIndicator")
                {
                    CanBeFocused = false
                };

                var subName = new GUITextBlock(new RectTransform(new Vector2(0.6f, 1.0f), frameContent.RectTransform),
                    text: sub.DisplayName)
                {
                    UserData = "nametext",
                    CanBeFocused = false
                };
                
                CreateSubmarineClassText(
                    frameContent,
                    sub,
                    subName,
                    list.Content);
            }
            
            foreach (var sub in GameMain.Client.ServerSubmarines
                .OrderBy(s => visibilityMenuOrder.Contains(s))
                .ThenBy(s => visibilityMenuOrder.IndexOf(s)))
            {
                addSubToList(sub,
                    GameMain.Client.ServerSettings.HiddenSubs.Contains(sub.Name) ? hiddenSubsList : visibleSubsList);
            }

            void onRearranged(GUIListBox listBox, object userData)
            {
                visibilityMenuOrder.Clear();
                visibilityMenuOrder.AddRange(visibleSubsList.Content.Children.Select(c => c.UserData as SubmarineInfo));
                visibilityMenuOrder.AddRange(hiddenSubsList.Content.Children.Select(c => c.UserData as SubmarineInfo));
            }

            visibleSubsList.OnRearranged = onRearranged;
            hiddenSubsList.OnRearranged = onRearranged;

            void swapListItems(GUIListBox from, GUIListBox to)
            {
                to.Deselect();
                var selected = from.AllSelected.ToArray();
                int lastIndex = from.Content.GetChildIndex(selected.LastOrDefault());
                int nextIndex = lastIndex + 1;
                GUIComponent nextComponent = null;
                if (lastIndex >= 0 && nextIndex < from.Content.CountChildren)
                {
                    nextComponent = from.Content.GetChild(nextIndex);
                }
                foreach (var frame in selected)
                {
                    frame.Parent.RemoveChild(frame);
                    frame.RectTransform.Parent = to.Content.RectTransform;
                }
                from.RecalculateChildren();
                from.RectTransform.RecalculateScale(true);
                to.RecalculateChildren();
                to.RectTransform.RecalculateScale(true);
                to.Select(selected);
                if (nextComponent != null) { from.Select(nextComponent.ToEnumerable()); }
            }
            
            centerSpacing();
            var visibleToHidden = centerButton("GUIButtonToggleRight");
            visibleToHidden.OnClicked = (button, o) =>
            {
                swapListItems(visibleSubsList, hiddenSubsList);
                return false;
            };
            var hiddenToVisible = centerButton("GUIButtonToggleLeft");
            hiddenToVisible.OnClicked = (button, o) =>
            {
                swapListItems(hiddenSubsList, visibleSubsList);
                return false;
            };
            centerSpacing();

            var buttonLayout
                = new GUILayoutGroup(new RectTransform(new Vector2(0.7f, 0.1f), messageBox.Content.RectTransform),
                    isHorizontal: true)
                {
                    RelativeSpacing = 0.01f
                };
            var cancelButton = new GUIButton(new RectTransform(new Vector2(0.5f, 1.0f), buttonLayout.RectTransform),
                    TextManager.Get("Cancel"))
            {
                OnClicked = (button, o) =>
                {
                    messageBox.Close();
                    return false;
                }
            };
            var okButton = new GUIButton(new RectTransform(new Vector2(0.5f, 1.0f), buttonLayout.RectTransform),
                TextManager.Get("OK"))
            {
                OnClicked = (button, o) =>
                {
                    var hiddenSubs = GameMain.Client.ServerSettings.HiddenSubs;
                    hiddenSubs.Clear();
                    hiddenSubs.UnionWith(hiddenSubsList.Content.Children.Select(c => (c.UserData as SubmarineInfo).Name));
                    GameMain.Client.ServerSettings.ClientAdminWrite(ServerSettings.NetFlags.HiddenSubs);
                    messageBox.Close();
                    return false;
                }
            };
            
            new GUICustomComponent(new RectTransform(Vector2.Zero, messageBox.RectTransform),
                onUpdate: (f, component) =>
                {
                    handleDraggingAcrossLists(visibleSubsList, hiddenSubsList);
                    handleDraggingAcrossLists(hiddenSubsList, visibleSubsList);
                    if (PlayerInput.PrimaryMouseButtonClicked()
                        && !GUI.IsMouseOn(visibleToHidden)
                        && !GUI.IsMouseOn(hiddenToVisible))
                    {
                        if (!GUI.IsMouseOn(hiddenSubsList)
                            || !hiddenSubsList.Content.IsParentOf(GUI.MouseOn))
                        {
                            hiddenSubsList.Deselect();
                        }
                        
                        if (!GUI.IsMouseOn(visibleSubsList)
                            || !visibleSubsList.Content.IsParentOf(GUI.MouseOn))
                        {
                            visibleSubsList.Deselect();
                        }
                    }
                },
                onDraw: (spriteBatch, component) =>
                {
                    visibleSubsList.DraggedElement?.DrawManually(spriteBatch, true, true);
                    hiddenSubsList.DraggedElement?.DrawManually(spriteBatch, true, true);
                });
        }

        public void UpdateSubVisibility()
        {
            if (GameMain.Client == null) { return; }
            foreach (GUIComponent child in SubList.Content.Children)
            {
                if (child.UserData is not SubmarineInfo sub) { continue; }
                child.Visible =
                    (!GameMain.Client.ServerSettings.HiddenSubs.Contains(sub.Name)
                     || (GameMain.GameSession?.SubmarineInfo != null && GameMain.GameSession.SubmarineInfo.Name.Equals(sub.Name, StringComparison.OrdinalIgnoreCase)))
                    && (string.IsNullOrEmpty(subSearchBox.Text) || sub.DisplayName.Contains(subSearchBox.Text, StringComparison.OrdinalIgnoreCase));
            }
        }

        public void OnRoundEnded()
        {
            CampaignCharacterDiscarded = false;
        }

        private const string RoundStartWarningBoxUserData = "RoundStartWarningBox";

        public void ShowStartRoundWarning(SerializableDateTime waitUntilTime, string team1SubName, ImmutableArray<DisembarkPerkPrefab> team1IncompatiblePerks, string team2SubName, ImmutableArray<DisembarkPerkPrefab> team2IncompatiblePerks)
        {
            DateTime startTime = DateTime.UtcNow;
            TimeSpan differenceFromStart = waitUntilTime.ToUtcValue() - startTime;

            StopWaitingForStartRound();
            GUIMessageBox.MessageBoxes.OfType<GUIMessageBox>().ForEachMod(static mod =>
            {
                if (mod.UserData is PleaseWaitPopupUserData)
                {
                    mod.Close();
                }
            });

            var messageBox = new GUIMessageBox(TextManager.Get("warning"), TextManager.Get("startgamewarning"), Array.Empty<LocalizedString>(), relativeSize: new Vector2(0.3f / GUI.AspectRatioAdjustment, 0.4f), minSize: new Point(400, 300))
            {
                UserData = RoundStartWarningBoxUserData
            };

            GUILayoutGroup contentLayout = new GUILayoutGroup(new RectTransform(new Vector2(1f, 0.7f), messageBox.Content.RectTransform, Anchor.BottomCenter), isHorizontal: false);

            GUIListBox errorList = new GUIListBox(new RectTransform(new Vector2(1.0f, 0.7f), contentLayout.RectTransform));

            foreach (DisembarkPerkPrefab perk in team1IncompatiblePerks)
            {
                new GUITextBlock(new RectTransform(new Vector2(1.0f, 0.33f), errorList.Content.RectTransform), FormatWarning(perk, team1SubName));
            }

            foreach (DisembarkPerkPrefab perk in team2IncompatiblePerks)
            {
                new GUITextBlock(new RectTransform(new Vector2(1.0f, 0.33f), errorList.Content.RectTransform), FormatWarning(perk, team2SubName));
            }

            GUIProgressBar progress = new GUIProgressBar(new RectTransform(new Vector2(1f, 0.15f), contentLayout.RectTransform), 0.0f, GUIStyle.Orange);
            GUITextBlock progressText = new GUITextBlock(new RectTransform(Vector2.One, progress.RectTransform), TextManager.GetWithVariable("startggamewarningprogress", "[seconds]", ((int)differenceFromStart.TotalSeconds).ToString()), textAlignment: Alignment.Center)
            {
                Shadow = true,
                TextColor = Color.White
            };

            new GUICustomComponent(new RectTransform(Vector2.Zero, progress.RectTransform),
                                   onDraw: static (batch, component) =>  { },
                                   onUpdate: (f, component) =>
                                   {
                                       TimeSpan difference = waitUntilTime.ToUtcValue() - DateTime.UtcNow;
                                       float seconds = (float)difference.TotalSeconds;

                                       progress.BarSize = seconds / (float)differenceFromStart.TotalSeconds;

                                       progressText.Text = TextManager.GetWithVariable("startggamewarningprogress", "[seconds]", ((int)seconds).ToString());
                                   });

            GUILayoutGroup buttonLayout = new GUILayoutGroup(new RectTransform(new Vector2(1.0f, 0.25f), contentLayout.RectTransform), childAnchor: Anchor.BottomCenter);
            GUIButton cancelButton = new GUIButton(new RectTransform(new Vector2(0.5f, 1f), buttonLayout.RectTransform), TextManager.Get("Cancel"));


            cancelButton.OnClicked += (button, userData) =>
            {
                IWriteMessage msg = new WriteOnlyMessage().WithHeader(ClientPacketHeader.RESPONSE_CANCEL_STARTGAME);
                GameMain.Client?.ClientPeer?.Send(msg, DeliveryMethod.Reliable);
                messageBox.Close();
                return true;
            };

            static LocalizedString FormatWarning(DisembarkPerkPrefab prefab, string subName)
            {
                return TextManager.GetWithVariables("startgamewarningformat",
                    ("[category]", TextManager.Get($"perkcategory.{prefab.SortCategory}")),
                    ("[perk]", prefab.Name),
                    ("[submarine]", subName));
            }
        }

        public void CloseStartRoundWarning()
        {
            GUIMessageBox.MessageBoxes.OfType<GUIMessageBox>().ForEachMod(static mod =>
            {
                if (mod.UserData is RoundStartWarningBoxUserData)
                {
                    mod.Close();
                }
            });
        }
    }
}

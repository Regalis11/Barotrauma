using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using System.Linq;
using Barotrauma.Networking;
using System.Globalization;
using Barotrauma.Extensions;

namespace Barotrauma
{
    class TabMenu
    {
        public static bool PendingChanges = false;

        private static bool initialized = false;

        private static UISprite spectateIcon, disconnectedIcon;
        private static Sprite ownerIcon, moderatorIcon;

        public enum InfoFrameTab { Crew, Mission, Reputation, Submarine, Talents };
        public static InfoFrameTab SelectedTab { get; private set; }
        private GUIFrame infoFrame, contentFrame;

        private readonly List<GUIButton> tabButtons = new List<GUIButton>();
        private GUIFrame infoFrameHolder;
        private List<LinkedGUI> linkedGUIList;
        private GUIListBox logList;
        private GUIListBox[] crewListArray;
        private float sizeMultiplier = 1f;

        private IEnumerable<Character> crew;
        private List<CharacterTeamType> teamIDs;
        private const string inLobbyString = "\u2022 \u2022 \u2022";

        public static GUIFrame PendingChangesFrame = null;

        public static Color OwnCharacterBGColor = Color.Gold * 0.7f;
        private bool isTransferMenuOpen;
        private bool isSending;
        private GUIComponent transferMenu;
        private GUIButton transferMenuButton;
        private float transferMenuOpenState;
        private bool transferMenuStateCompleted;
        private readonly HashSet<Identifier> registeredEvents = new HashSet<Identifier>();
        private readonly TalentMenu talentMenu = new TalentMenu();

        private class LinkedGUI
        {
            private const ushort lowPingThreshold = 100;
            private const ushort mediumPingThreshold = 200;

            public readonly Client Client;

            private ushort currentPing;
            private readonly Character character;
            private readonly bool wasCharacterAlive;
            private readonly GUITextBlock textBlock;
            private readonly GUIFrame frame;

            private readonly GUIImage permissionIcon;

            public LinkedGUI(Client client, GUIFrame frame, GUITextBlock textBlock, GUIImage permissionIcon)
            {
                this.Client = client;
                this.textBlock = textBlock;
                this.frame = frame;
                this.permissionIcon = permissionIcon;
                character = client?.Character;
                wasCharacterAlive = client?.Character != null && !client.Character.IsDead;
            }

            public LinkedGUI(Character character, GUIFrame frame, GUITextBlock textBlock)
            {
                this.character = character;
                this.textBlock = textBlock;
                this.frame = frame;
                wasCharacterAlive = character != null && !character.IsDead;
            }

            public bool HasMultiplayerCharacterChanged()
            {
                if (Client == null) { return false; }

                if (GameSettings.CurrentConfig.VerboseLogging)
                {
                    if (Client.Character != character)
                    {
                        DebugConsole.Log($"Refreshing tab menu crew list (client \"{Client.Name}\"'s character changed from \"{character?.Name ?? "null"}\" to \"{Client.Character?.Name ?? "null"}\")");
                    }
                }
                return Client.Character != character;
            }

            public bool HasCharacterDied()
            {
                if (character == null) { return false; }
                bool isAlive = !(character?.IsDead ?? true);
                if (GameSettings.CurrentConfig.VerboseLogging)
                {
                    if (wasCharacterAlive && !isAlive)
                    {
                        DebugConsole.Log(Client == null ?
                            $"Refreshing tab menu crew list (character \"{character?.Name ?? "null"}\" died)" :
                            $"Refreshing tab menu crew list (client \"{Client.Name}\"'s character \"{character?.Name ?? "null"}\" died)");
                    }
                    else if (!wasCharacterAlive && isAlive)
                    {
                        DebugConsole.Log(Client == null ?

                            $"Refreshing tab menu crew list (character \"{character?.Name ?? "null"}\" came back to life)" :
                            $"Refreshing tab menu crew list (client \"{Client.Name}\"'s character \"{character?.Name ?? "null"}\" came back to life)");
                    }
                }
                return isAlive != wasCharacterAlive;
            }

            public void TryPingRefresh()
            {
                if (Client == null) { return; }
                if (currentPing == Client.Ping) { return; }
                currentPing = Client.Ping;
                textBlock.Text = currentPing.ToString();
                textBlock.TextColor = GetPingColor();
            }

            public void TryPermissionIconRefresh(Sprite icon)
            {
                if (Client == null || permissionIcon == null) { return; }
                permissionIcon.Sprite = icon;
            }

            private Color GetPingColor()
            {
                if (currentPing < lowPingThreshold)
                {
                    return GUIStyle.Green;
                }
                else if (currentPing < mediumPingThreshold)
                {
                    return GUIStyle.Yellow;
                }
                else
                {
                    return GUIStyle.Red;
                }
            }

            public void Remove(GUIFrame parent)
            {
                parent.RemoveChild(frame);
            }
        }

        public void Initialize()
        {
            spectateIcon = GUIStyle.GetComponentStyle("SpectateIcon").Sprites[GUIComponent.ComponentState.None][0];
            disconnectedIcon = GUIStyle.GetComponentStyle("DisconnectedIcon").Sprites[GUIComponent.ComponentState.None][0];
            ownerIcon = GUIStyle.GetComponentStyle("OwnerIcon").GetDefaultSprite();
            moderatorIcon = GUIStyle.GetComponentStyle("ModeratorIcon").GetDefaultSprite();
            initialized = true;
        }

        public TabMenu()
        {
            if (!initialized) { Initialize(); }
            if (Level.Loaded == null)
            {
                //make sure we're not trying to view e.g. mission or reputation info if the tab menu is opened in the test mode
                SelectedTab = InfoFrameTab.Crew;
            }
            CreateInfoFrame(SelectedTab);
            SelectInfoFrameTab(SelectedTab);
        }

        public void Update(float deltaTime)
        {
            float menuOpenSpeed = deltaTime * 10f;
            if (isTransferMenuOpen)
            {
                if (transferMenuStateCompleted)
                {
                    transferMenuOpenState = transferMenuOpenState < 0.25f ? Math.Min(0.25f, transferMenuOpenState + (menuOpenSpeed / 2f)) : 0.25f;
                }
                else
                {
                    if (transferMenuOpenState > 0.15f)
                    {
                        transferMenuStateCompleted = false;
                        transferMenuOpenState = Math.Max(0.15f, transferMenuOpenState - menuOpenSpeed);
                    }
                    else
                    {
                        transferMenuStateCompleted = true;
                    }
                }
            }
            else
            {
                transferMenuStateCompleted = false;
                if (transferMenuOpenState < 1f)
                {
                    transferMenuOpenState = Math.Min(1f, transferMenuOpenState + menuOpenSpeed);
                }
            }

            if (transferMenu != null && transferMenuButton != null)
            {
                int pos = (int)(transferMenuOpenState * -transferMenu.Rect.Height);
                transferMenu.RectTransform.AbsoluteOffset = new Point(0, pos);
                transferMenuButton.RectTransform.AbsoluteOffset = new Point(0, -pos - transferMenu.Rect.Height);
            }
            GameSession.UpdateTalentNotificationIndicator(talentPointNotification);

            talentMenu?.Update();

            if (SelectedTab != InfoFrameTab.Crew) { return; }
            if (linkedGUIList == null) { return; }

            if (GameMain.IsMultiplayer)
            {
                for (int i = 0; i < linkedGUIList.Count; i++)
                {
                    linkedGUIList[i].TryPingRefresh();
                    linkedGUIList[i].TryPermissionIconRefresh(GetPermissionIcon(linkedGUIList[i].Client));
                    if (linkedGUIList[i].HasMultiplayerCharacterChanged() || linkedGUIList[i].HasCharacterDied())
                    {
                        RemoveCurrentElements();
                        CreateMultiPlayerList(true);
                        return;
                    }
                }
            }
            else
            {
                for (int i = 0; i < linkedGUIList.Count; i++)
                {
                    if (linkedGUIList[i].HasCharacterDied())
                    {
                        RemoveCurrentElements();
                        CreateSinglePlayerList(true);
                        return;
                    }
                }
            }
        }

        public void AddToGUIUpdateList()
        {
            infoFrame?.AddToGUIUpdateList();
            NetLobbyScreen.JobInfoFrame?.AddToGUIUpdateList();
        }

        public static void OnRoundEnded()
        {
            storedMessages.Clear();
            PendingChanges = false;
        }

        private void CreateInfoFrame(InfoFrameTab selectedTab)
        {
            tabButtons.Clear();

            infoFrame = new GUIFrame(new RectTransform(Vector2.One, GUI.Canvas, Anchor.Center), style: null);
            new GUIFrame(new RectTransform(GUI.Canvas.RelativeSize, infoFrame.RectTransform, Anchor.Center), style: "GUIBackgroundBlocker");

            //this used to be a switch expression but i changed it because it killed enc :(
            //now it's not even a switch statement anymore :(
            Vector2 contentFrameSize = new Vector2(0.45f, 0.667f);
            contentFrame = new GUIFrame(new RectTransform(contentFrameSize, infoFrame.RectTransform, Anchor.TopCenter, Pivot.TopCenter) { RelativeOffset = new Vector2(0.0f, 0.12f) });

            var horizontalLayoutGroup = new GUILayoutGroup(new RectTransform(new Vector2(0.958f, 0.943f), contentFrame.RectTransform, Anchor.TopCenter, Pivot.TopCenter) { AbsoluteOffset = new Point(0, GUI.IntScale(25f)) }, isHorizontal: true)
            {
                RelativeSpacing = 0.01f
            };

            var buttonArea = new GUILayoutGroup(new RectTransform(new Vector2(0.07f, 1f), parent: horizontalLayoutGroup.RectTransform), isHorizontal: false)
            {
                AbsoluteSpacing = GUI.IntScale(5f)
            };
            var innerLayoutGroup = new GUILayoutGroup(new RectTransform(new Vector2(0.92f, 1f), horizontalLayoutGroup.RectTransform))
            {
                RelativeSpacing = 0.01f,
                Stretch = true
            };

            float absoluteSpacing = innerLayoutGroup.RelativeSpacing * innerLayoutGroup.Rect.Height;
            int multiplier = GameMain.GameSession?.GameMode is CampaignMode ? 2 : 1;
            int infoFrameHolderHeight = Math.Min((int)(0.97f * innerLayoutGroup.Rect.Height), (int)(innerLayoutGroup.Rect.Height - multiplier * (GUI.IntScale(15f) + absoluteSpacing)));
            infoFrameHolder = new GUIFrame(new RectTransform(new Point(innerLayoutGroup.Rect.Width, infoFrameHolderHeight), parent: innerLayoutGroup.RectTransform), style: null);

            GUIButton createTabButton(InfoFrameTab tab, string textTag)
            {
                var newButton = new GUIButton(new RectTransform(Vector2.One, buttonArea.RectTransform, scaleBasis: ScaleBasis.BothWidth), style: $"InfoFrameTabButton.{tab}")
                {
                    UserData = tab,
                    ToolTip = TextManager.Get(textTag),
                    OnClicked = (btn, userData) => { SelectInfoFrameTab((InfoFrameTab)userData); return true; }
                };
                tabButtons.Add(newButton);
                return newButton;
            }

            var crewButton = createTabButton(InfoFrameTab.Crew, "crew");

            if (GameMain.GameSession?.GameMode is not TestGameMode)
            {
                var missionBtn = createTabButton(InfoFrameTab.Mission, "mission");
                eventLogNotification = GameSession.CreateNotificationIcon(missionBtn);
                eventLogNotification.Visible = GameMain.GameSession?.EventManager?.EventLog?.UnreadEntries ?? false;
                if (eventLogNotification.Visible)
                {
                    eventLogNotification.Pulsate(Vector2.One, Vector2.One * 2, 1.0f);
                }
            }

            if (GameMain.GameSession?.GameMode is CampaignMode campaignMode)
            {
                var reputationButton = createTabButton(InfoFrameTab.Reputation, "reputation");

                var balanceFrame = new GUIFrame(new RectTransform(new Point(innerLayoutGroup.Rect.Width, innerLayoutGroup.Rect.Height - infoFrameHolderHeight), parent: innerLayoutGroup.RectTransform), style: "InnerFrame");
                GUILayoutGroup salaryFrame = new GUILayoutGroup(new RectTransform(new Vector2(0.66f, 1f), balanceFrame.RectTransform), isHorizontal: true, childAnchor: Anchor.CenterLeft);

                GUIScrollBar salaryScrollBar = null;
                GUITextBlock salaryPercentage = null;
                if (GameMain.GameSession?.GameMode is MultiPlayerCampaign)
                {
                    float value = campaignMode.Bank.RewardDistribution;
                    GUITextBlock salaryText = new GUITextBlock(new RectTransform(new Vector2(0.25f, 1f), salaryFrame.RectTransform), TextManager.Get("defaultsalary"), textAlignment: Alignment.Center)
                    {
                        AutoScaleHorizontal = true
                    };
                    salaryScrollBar = new GUIScrollBar(new RectTransform(new Vector2(0.4f, 1f), salaryFrame.RectTransform), barSize: 0.1f, style: "GUISlider")
                    {
                        Range = new Vector2(0, 1),
                        BarScrollValue = value / 100f,
                        Step = 0.01f,
                        BarSize = 0.1f,
                    };

                    salaryPercentage = new GUITextBlock(new RectTransform(new Vector2(0.15f, 1f), salaryFrame.RectTransform), "0", textAlignment: Alignment.Center)
                    {
                        Text = ValueToPercentage(RoundRewardDistribution(salaryScrollBar.BarScroll, salaryScrollBar.Step))
                    };

                    salaryScrollBar.OnMoved = (scrollBar, value) =>
                    {
                        salaryPercentage.Text = ValueToPercentage(RoundRewardDistribution(value, scrollBar.Step));
                        return true;
                    };
                    salaryScrollBar.OnReleased = (bar, scroll) =>
                    {
                        int newRewardDistribution = RoundRewardDistribution(scroll, bar.Step);
                        SetRewardDistribution(Option.None, newRewardDistribution);
                        return true;
                    };

                    var resetButton = new GUIButton(new RectTransform(new Vector2(0.2f, 1f), salaryFrame.RectTransform), TextManager.Get("ResetSalaries"), style: "GUIButtonSmall")
                    {
                        TextBlock = { AutoScaleHorizontal = true },
                        ToolTip = TextManager.Get("resetsalaries.tooltip"),
                        OnClicked = (button, userData) =>
                        {
                            GUI.AskForConfirmation(TextManager.Get("ResetSalaries"), TextManager.Get("ResetSalaries.Warning"), onConfirm: ResetRewardDistributions);
                            return true;
                        }
                    };

                    void UpdateSliderEnabled() 
                        => salaryScrollBar.Enabled = resetButton.Enabled = CampaignMode.AllowedToManageWallets();
                    UpdateSliderEnabled();

                    Identifier defaultSalaryEventIdentifier = "DefaultSalarySlider".ToIdentifier();
                    GameMain.Client?.OnPermissionChanged?.RegisterOverwriteExisting(defaultSalaryEventIdentifier, _ => UpdateSliderEnabled());
                }
                GUITextBlock balanceText = new GUITextBlock(new RectTransform(new Vector2(0.33f, 1f), balanceFrame.RectTransform, Anchor.TopRight), string.Empty, textAlignment: Alignment.Right);
                if (GameMain.IsMultiplayer)
                {
                    balanceText.ToolTip = TextManager.Get("bankdescription");
                }
                GUIFrame bottomDisclaimerFrame = new GUIFrame(new RectTransform(new Vector2(contentFrameSize.X, 0.1f), infoFrame.RectTransform)
                {
                    AbsoluteOffset = new Point(contentFrame.Rect.X, contentFrame.Rect.Bottom + GUI.IntScale(8))
                }, style: null);

                PendingChangesFrame = new GUIFrame(new RectTransform(Vector2.One, bottomDisclaimerFrame.RectTransform, Anchor.Center), style: null);

                if (GameMain.NetLobbyScreen?.CampaignCharacterDiscarded ?? false)
                {
                    NetLobbyScreen.CreateChangesPendingFrame(PendingChangesFrame);
                }

                SetBalanceText(balanceText, campaignMode.Bank.Balance);
                Identifier eventIdentifier = nameof(CreateInfoFrame).ToIdentifier();
                campaignMode.OnMoneyChanged.RegisterOverwriteExisting(eventIdentifier, e =>
                {
                    if (!e.Owner.IsNone()) { return; }
                    SetBalanceText(balanceText, e.Wallet.Balance);

                    if (salaryPercentage is not null && salaryScrollBar is not null)
                    {
                        float rewardDistribution = e.Wallet.RewardDistribution;
                        salaryScrollBar.BarScrollValue = rewardDistribution / 100f;
                        salaryPercentage.Text = ValueToPercentage(rewardDistribution);
                    }
                });
                registeredEvents.Add(eventIdentifier);

                static void SetBalanceText(GUITextBlock text, int balance)
                {
                    text.Text = TextManager.GetWithVariable("bankbalanceformat", "[money]", string.Format(CultureInfo.InvariantCulture, "{0:N0}", balance));
                }

                LocalizedString ValueToPercentage(float value)
                    => TextManager.GetWithVariable("percentageformat", "[value]", $"{(int)MathF.Round(value)}");
            }

            if (Submarine.MainSub != null)
            {
                createTabButton(InfoFrameTab.Submarine, "submarine");
            }

            var talentsButton = createTabButton(InfoFrameTab.Talents, "tabmenu.character");
            talentsButton.OnAddedToGUIUpdateList += (component) =>
            {
                talentsButton.Enabled = Character.Controlled?.Info != null || GameMain.Client?.CharacterInfo != null;
                if (!talentsButton.Enabled && selectedTab == InfoFrameTab.Talents)
                {
                    SelectInfoFrameTab(InfoFrameTab.Crew);
                }
            };

            talentPointNotification = GameSession.CreateNotificationIcon(talentsButton);
        }

        public void SelectInfoFrameTab(InfoFrameTab selectedTab)
        {
            SelectedTab = selectedTab;

            CreateInfoFrame(selectedTab);
            tabButtons.ForEach(tb => tb.Selected = (InfoFrameTab)tb.UserData == selectedTab);

            switch (selectedTab)
            {
                case InfoFrameTab.Crew:
                    CreateCrewListFrame(infoFrameHolder);
                    break;
                case InfoFrameTab.Mission:
                    CreateMissionInfo(infoFrameHolder);
                    break;
                case InfoFrameTab.Reputation:
                    if (GameMain.GameSession?.RoundSummary != null && GameMain.GameSession?.GameMode is CampaignMode campaignMode)
                    {
                        infoFrameHolder.ClearChildren();
                        GUIFrame reputationFrame = new GUIFrame(new RectTransform(Vector2.One, infoFrameHolder.RectTransform, Anchor.TopCenter), style: "GUIFrameListBox");
                        GameMain.GameSession.RoundSummary.CreateReputationInfoPanel(reputationFrame, campaignMode);
                    }
                    break;
                case InfoFrameTab.Submarine:
                    CreateSubmarineInfo(infoFrameHolder, Submarine.MainSub);
                    break;
                case InfoFrameTab.Talents:
                    talentMenu.CreateGUI(infoFrameHolder, Character.Controlled ?? GameMain.Client?.Character);
                    break;
            }
        }

        private const float JobColumnWidthPercentage = 0.138f,
                            CharacterColumnWidthPercentage = 0.45f,
                            KillColumnWidthPercentage = 0.1f,
                            DeathColumnWidthPercentage = 0.1f,
                            PingColumnWidthPercentage = 0.15f,
                            WalletColumnWidthPercentage = 0.206f;

        private int jobColumnWidth, characterColumnWidth, pingColumnWidth, walletColumnWidth, deathColumnWidth, killColumnWidth;

        private void CreateCrewListFrame(GUIFrame crewFrame)
        {
            crew = GameMain.GameSession?.CrewManager?.GetCharacters() ?? new List<Character>() { TestScreen.dummyCharacter};
            teamIDs = crew.Select(c => c.TeamID).Distinct().ToList();

            // Show own team first when there's more than one team
            if (teamIDs.Count > 1 && GameMain.Client?.Character != null)
            {
                CharacterTeamType ownTeam = GameMain.Client.Character.TeamID;
                teamIDs = teamIDs.OrderBy(i => i != ownTeam).ThenBy(i => i).ToList();
            }

            if (!teamIDs.Any()) { teamIDs.Add(CharacterTeamType.None); }

            var content = new GUILayoutGroup(new RectTransform(Vector2.One, crewFrame.RectTransform));

            crewListArray = new GUIListBox[teamIDs.Count];
            GUILayoutGroup[] headerFrames = new GUILayoutGroup[teamIDs.Count];

            float nameHeight = 0.075f;

            Vector2 crewListSize = new Vector2(1f, 1f / teamIDs.Count - (teamIDs.Count > 1 ? nameHeight * 1.1f : 0f));
            for (int i = 0; i < teamIDs.Count; i++)
            {
                if (teamIDs.Count > 1)
                {
                    var nameText = new GUITextBlock(new RectTransform(new Vector2(1.0f, nameHeight), content.RectTransform), CombatMission.GetTeamName(teamIDs[i]), textColor: CombatMission.GetTeamColor(teamIDs[i])) 
                    { 
                        ForceUpperCase = ForceUpperCase.Yes 
                    };
                    var teamIcon = new GUIImage(new RectTransform(Vector2.One, nameText.RectTransform, Anchor.CenterLeft, scaleBasis: ScaleBasis.BothHeight), 
                        style: teamIDs[i] == CharacterTeamType.Team2 ? "SeparatistIcon" : "CoalitionIcon")
                    {
                        Color = nameText.TextColor
                    };
                    nameText.Padding = new Vector4(teamIcon.Rect.Width + nameText.Padding.X, nameText.Padding.Y, nameText.Padding.Z, nameText.Padding.W);
                }

                headerFrames[i] = new GUILayoutGroup(new RectTransform(Vector2.Zero, content.RectTransform, Anchor.TopLeft, Pivot.BottomLeft) { AbsoluteOffset = new Point(2, -1) }, isHorizontal: true)
                {
                    Stretch = true,
                    AbsoluteSpacing = 2,
                    UserData = i
                };

                GUIListBox crewList = new GUIListBox(new RectTransform(crewListSize, content.RectTransform))
                {
                    Padding = new Vector4(2, 5, 0, 0),
                    AutoHideScrollBar = false,
                    PlaySoundOnSelect = true
                };
                crewList.UpdateDimensions();

                if (teamIDs.Count > 1)
                {
                    crewList.OnSelected = (component, obj) =>
                    {
                        for (int i = 0; i < crewListArray.Length; i++)
                        {
                            if (crewListArray[i] == crewList) continue;
                            crewListArray[i].Deselect();
                        }
                        SelectElement(component.UserData, crewList);
                        return true;
                    };
                }
                else
                {
                    crewList.OnSelected = (component, obj) =>
                    {
                        SelectElement(component.UserData, crewList);
                        return true;
                    };
                }

                crewListArray[i] = crewList;
            }

            for (int i = 0; i < teamIDs.Count; i++)
            {
                headerFrames[i].RectTransform.RelativeSize = new Vector2(1f - crewListArray[i].ScrollBar.Rect.Width / (float)crewListArray[i].Rect.Width, GUIStyle.HotkeyFont.Size / (float)crewFrame.RectTransform.Rect.Height * 1.5f);

                if (!GameMain.IsMultiplayer)
                {
                    CreateSinglePlayerListContentHolder(headerFrames[i]);
                }
                else
                {
                    CreateMultiPlayerListContentHolder(headerFrames[i]);
                }
            }

            crewFrame.RectTransform.AbsoluteOffset = new Point(0, (int)(headerFrames[0].Rect.Height * headerFrames.Length) - (teamIDs.Count > 1 ? GUI.IntScale(10f) : 0));

            float totalRelativeHeight = 0.0f;
            if (teamIDs.Count > 1) { totalRelativeHeight += teamIDs.Count * nameHeight; }
            headerFrames.ForEach(f => totalRelativeHeight += f.RectTransform.RelativeSize.Y);
            crewListArray.ForEach(f => totalRelativeHeight += f.RectTransform.RelativeSize.Y);
            if (totalRelativeHeight > 1.0f)
            {
                float heightOverflow = totalRelativeHeight - 1.0f;
                float heightToReduce = heightOverflow / crewListArray.Length;
                crewListArray.ForEach(l =>
                {
                    l.RectTransform.Resize(l.RectTransform.RelativeSize - new Vector2(0.0f, heightToReduce));
                    l.UpdateDimensions();
                });
            }

            if (GameMain.IsMultiplayer)
            {
                CreateMultiPlayerList(false);
                CreateMultiPlayerLogContent(crewFrame);
            }
            else
            {
                CreateSinglePlayerList(false);
            }
        }

        private void CreateSinglePlayerListContentHolder(GUILayoutGroup headerFrame)
        {
            GUIButton jobButton = new GUIButton(new RectTransform(new Vector2(0f, 1f), headerFrame.RectTransform), TextManager.Get("tabmenu.job"), style: "GUIButtonSmallFreeScale");
            GUIButton characterButton = new GUIButton(new RectTransform(new Vector2(0f, 1f), headerFrame.RectTransform), TextManager.Get("name"), style: "GUIButtonSmallFreeScale");

            sizeMultiplier = (headerFrame.Rect.Width - headerFrame.AbsoluteSpacing * (headerFrame.CountChildren - 1)) / (float)headerFrame.Rect.Width;

            jobButton.RectTransform.RelativeSize = new Vector2(JobColumnWidthPercentage * sizeMultiplier, 1f);
            characterButton.RectTransform.RelativeSize = new Vector2((1f - JobColumnWidthPercentage * sizeMultiplier) * sizeMultiplier, 1f);

            jobButton.TextBlock.Font = characterButton.TextBlock.Font = GUIStyle.HotkeyFont;
            jobButton.CanBeFocused = characterButton.CanBeFocused = false;
            jobButton.TextBlock.ForceUpperCase = characterButton.TextBlock.ForceUpperCase = ForceUpperCase.Yes;

            jobColumnWidth = jobButton.Rect.Width;
            characterColumnWidth = characterButton.Rect.Width;
        }

        private void CreateSinglePlayerList(bool refresh)
        {
            if (refresh)
            {
                crew = GameMain.GameSession.CrewManager.GetCharacters();
            }

            linkedGUIList = new List<LinkedGUI>();

            for (int i = 0; i < teamIDs.Count; i++)
            {
                foreach (Character character in crew.Where(c => c.TeamID == teamIDs[i]))
                {
                    CreateSinglePlayerCharacterElement(character, i);
                }
            }
        }

        private void CreateSinglePlayerCharacterElement(Character character, int i)
        {
            GUIFrame frame = new GUIFrame(new RectTransform(new Point(crewListArray[i].Content.Rect.Width, GUI.IntScale(33f)), crewListArray[i].Content.RectTransform), style: "ListBoxElement")
            {
                UserData = character,
                Color = (Character.Controlled == character) ? OwnCharacterBGColor : Color.Transparent
            };

            var paddedFrame = new GUILayoutGroup(new RectTransform(new Vector2(1.0f, 0.9f), frame.RectTransform, Anchor.Center), isHorizontal: true)
            {
                AbsoluteSpacing = 2,
                Stretch = true
            };

            new GUICustomComponent(new RectTransform(new Point(jobColumnWidth, paddedFrame.Rect.Height), paddedFrame.RectTransform, Anchor.Center), onDraw: (sb, component) => character.Info.DrawJobIcon(sb, component.Rect))
            {
                CanBeFocused = false,
                HoverColor = Color.White,
                SelectedColor = Color.White
            };

            GUITextBlock characterNameBlock = new GUITextBlock(new RectTransform(new Point(characterColumnWidth, paddedFrame.Rect.Height), paddedFrame.RectTransform),
                ToolBox.LimitString(character.Info.Name, GUIStyle.Font, characterColumnWidth), textAlignment: Alignment.Center, textColor: character.Info.Job.Prefab.UIColor);

            paddedFrame.Recalculate();

            linkedGUIList.Add(new LinkedGUI(character, frame, textBlock: null));
        }

        private void CreateMultiPlayerListContentHolder(GUILayoutGroup headerFrame)
        {
            bool isCampaign = GameMain.GameSession?.Campaign is MultiPlayerCampaign;
            GUIButton jobButton = new GUIButton(new RectTransform(new Vector2(JobColumnWidthPercentage, 1f), headerFrame.RectTransform), TextManager.Get("tabmenu.job"), style: "GUIButtonSmallFreeScale");
            GUIButton characterButton = new GUIButton(new RectTransform(new Vector2(CharacterColumnWidthPercentage, 1f), headerFrame.RectTransform), TextManager.Get("name"), style: "GUIButtonSmallFreeScale");

            if (GameMain.GameSession?.GameMode is PvPMode)
            {
                var killButton = new GUIButton(new RectTransform(new Vector2(KillColumnWidthPercentage, 1f), headerFrame.RectTransform), TextManager.Get("killcount"), style: "GUIButtonSmallFreeScale");
                killColumnWidth = killButton.Rect.Width;
                var deathButton = new GUIButton(new RectTransform(new Vector2(DeathColumnWidthPercentage, 1f), headerFrame.RectTransform), TextManager.Get("deathcount"), style: "GUIButtonSmallFreeScale");
                deathColumnWidth = deathButton.Rect.Width;
            }

            GUIButton pingButton = new GUIButton(new RectTransform(new Vector2(PingColumnWidthPercentage, 1f), headerFrame.RectTransform), TextManager.Get("serverlistping"), style: "GUIButtonSmallFreeScale");
            if (isCampaign)
            {
                GUIButton walletButton = new GUIButton(new RectTransform(new Vector2(WalletColumnWidthPercentage, 1f), headerFrame.RectTransform), TextManager.Get("crewwallet.wallet"), style: "GUIButtonSmallFreeScale")
                {
                    TextBlock = { Font = GUIStyle.HotkeyFont },
                    CanBeFocused = false,
                    ForceUpperCase = ForceUpperCase.Yes
                };
                walletColumnWidth = walletButton.Rect.Width;
            }

            foreach (var btn in headerFrame.GetAllChildren<GUIButton>())
            {
                btn.TextBlock.Font = GUIStyle.HotkeyFont;
                btn.CanBeFocused = false;
                btn.ForceUpperCase = ForceUpperCase.Yes;
            }

            jobColumnWidth = jobButton.Rect.Width;
            characterColumnWidth = characterButton.Rect.Width;
            pingColumnWidth = pingButton.Rect.Width;
        }

        private void CreateMultiPlayerList(bool refresh)
        {
            if (refresh)
            {
                crew = GameMain.GameSession.CrewManager.GetCharacters();
            }

            linkedGUIList = new List<LinkedGUI>();

            var connectedClients = GameMain.Client.ConnectedClients;

            for (int i = 0; i < teamIDs.Count; i++)
            {
                foreach (Character character in crew.Where(c => c.TeamID == teamIDs[i]))
                {
                    if (character is not AICharacter && connectedClients.Any(c => c.Character == null && c.Name == character.Name)) { continue; }
                    CreateMultiPlayerCharacterElement(character, GameMain.Client.PreviouslyConnectedClients.FirstOrDefault(c => c.Character == character), i);
                }
            }

            for (int j = 0; j < connectedClients.Count; j++)
            {
                Client client = connectedClients[j];
                if (!client.InGame || client.Character == null || client.Character.IsDead)
                {
                    CreateMultiPlayerClientElement(client);
                }
            }
        }

        private void CreateMultiPlayerCharacterElement(Character character, Client client, int i)
        {
            GUIFrame frame = new GUIFrame(new RectTransform(new Point(crewListArray[i].Content.Rect.Width, GUI.IntScale(33f)), crewListArray[i].Content.RectTransform), style: "ListBoxElement")
            {
                UserData = character,
                Color = (GameMain.NetworkMember != null && GameMain.Client.Character == character) ? OwnCharacterBGColor : Color.Transparent
            };

            frame.OnSecondaryClicked += (component, data) =>
            {
                NetLobbyScreen.CreateModerationContextMenu(client);
                return true;
            };

            var paddedFrame = new GUILayoutGroup(new RectTransform(new Vector2(1.0f, 0.9f), frame.RectTransform, Anchor.Center), isHorizontal: true)
            {
                AbsoluteSpacing = 2,
                Stretch = true
            };

            new GUICustomComponent(new RectTransform(new Point(jobColumnWidth, paddedFrame.Rect.Height), paddedFrame.RectTransform, Anchor.Center), onDraw: (sb, component) => character.Info.DrawJobIcon(sb, component.Rect))
            {
                CanBeFocused = false,
                HoverColor = Color.White,
                SelectedColor = Color.White
            };

            if (client != null)
            {
                CreateNameWithPermissionIcon(client, paddedFrame, out GUIImage permissionIcon);

                if (GameMain.GameSession?.GameMode is PvPMode)
                {
                    new GUITextBlock(new RectTransform(new Point(killColumnWidth, paddedFrame.Rect.Height), paddedFrame.RectTransform), string.Empty, textAlignment: Alignment.Center)
                    {
                        TextGetter = () => GameMain.GameSession.Missions.Sum(m => (m as CombatMission)?.GetClientKillCount(client) ?? 0).ToString()
                    };
                    new GUITextBlock(new RectTransform(new Point(deathColumnWidth, paddedFrame.Rect.Height), paddedFrame.RectTransform), string.Empty, textAlignment: Alignment.Center)
                    {
                        TextGetter = () => GameMain.GameSession.Missions.Sum(m => (m as CombatMission)?.GetClientDeathCount(client) ?? 0).ToString()                        
                    };
                }

                linkedGUIList.Add(new LinkedGUI(client, frame, 
                    new GUITextBlock(new RectTransform(new Point(pingColumnWidth, paddedFrame.Rect.Height), paddedFrame.RectTransform), client.Ping.ToString(), textAlignment: Alignment.Center),
                    permissionIcon));
            }
            else
            {
                GUITextBlock characterNameBlock = new GUITextBlock(new RectTransform(new Point(characterColumnWidth, paddedFrame.Rect.Height), paddedFrame.RectTransform),
                    ToolBox.LimitString(character.Info.Name, GUIStyle.Font, characterColumnWidth), textAlignment: Alignment.Center, textColor: character.Info.Job.Prefab.UIColor);

                if (GameMain.GameSession?.GameMode is PvPMode)
                {
                    new GUITextBlock(new RectTransform(new Point(killColumnWidth, paddedFrame.Rect.Height), paddedFrame.RectTransform), string.Empty, textAlignment: Alignment.Center)
                    {
                        TextGetter = () => GameMain.GameSession.Missions.Sum(m => (m as CombatMission)?.GetBotKillCount(character.Info) ?? 0).ToString()
                    };
                    new GUITextBlock(new RectTransform(new Point(deathColumnWidth, paddedFrame.Rect.Height), paddedFrame.RectTransform), string.Empty, textAlignment: Alignment.Center)
                    {
                        TextGetter = () => GameMain.GameSession.Missions.Sum(m => (m as CombatMission)?.GetBotDeathCount(character.Info) ?? 0).ToString()
                    };
                }

                if (character is AICharacter)
                {
                    linkedGUIList.Add(new LinkedGUI(character, frame, 
                        new GUITextBlock(new RectTransform(new Point(pingColumnWidth, paddedFrame.Rect.Height), paddedFrame.RectTransform), TextManager.Get("tabmenu.bot"), textAlignment: Alignment.Center) { ForceUpperCase = ForceUpperCase.Yes }));
                }
                else
                {
                    linkedGUIList.Add(new LinkedGUI(client: null, frame, textBlock: null, permissionIcon: null));

                    new GUICustomComponent(new RectTransform(new Point(pingColumnWidth, paddedFrame.Rect.Height), paddedFrame.RectTransform, Anchor.Center), onDraw: (sb, component) => DrawDisconnectedIcon(sb, component.Rect))
                    {
                        CanBeFocused = false,
                        HoverColor = Color.White,
                        SelectedColor = Color.White
                    };
                }
            }

            CreateWalletCrewFrame(character, paddedFrame);

            paddedFrame.Recalculate();
        }

        private void CreateMultiPlayerClientElement(Client client)
        {
            int teamIndex = GetTeamIndex(client);
            if (teamIndex == -1) teamIndex = 0;

            GUIFrame frame = new GUIFrame(new RectTransform(new Point(crewListArray[teamIndex].Content.Rect.Width, GUI.IntScale(33f)), crewListArray[teamIndex].Content.RectTransform), style: "ListBoxElement")
            {
                UserData = client,
                Color = Color.Transparent
            };

            frame.OnSecondaryClicked += (component, data) =>
            {
                NetLobbyScreen.CreateModerationContextMenu(client);
                return true;
            };

            var paddedFrame = new GUILayoutGroup(new RectTransform(new Vector2(1.0f, 0.9f), frame.RectTransform, Anchor.Center), isHorizontal: true)
            {
                AbsoluteSpacing = 2,
                Stretch = true
            };

            new GUICustomComponent(new RectTransform(new Point(jobColumnWidth, paddedFrame.Rect.Height), paddedFrame.RectTransform, Anchor.Center),
                onDraw: (sb, component) => DrawNotInGameIcon(sb, component.Rect, client))
            {
                CanBeFocused = false,
                HoverColor = Color.White,
                SelectedColor = Color.White
            };

            CreateNameWithPermissionIcon(client, paddedFrame, out GUIImage permissionIcon);

            if (GameMain.GameSession?.GameMode is PvPMode)
            {
                new GUITextBlock(new RectTransform(new Point(killColumnWidth, paddedFrame.Rect.Height), paddedFrame.RectTransform), string.Empty, textAlignment: Alignment.Center)
                {
                    TextGetter = () => GameMain.GameSession.Missions.Sum(m => (m as CombatMission)?.GetClientKillCount(client) ?? 0).ToString()
                };
                new GUITextBlock(new RectTransform(new Point(deathColumnWidth, paddedFrame.Rect.Height), paddedFrame.RectTransform), string.Empty, textAlignment: Alignment.Center)
                {
                    TextGetter = () => GameMain.GameSession.Missions.Sum(m => (m as CombatMission)?.GetClientDeathCount(client) ?? 0).ToString()
                };
            }

            linkedGUIList.Add(new LinkedGUI(client, frame,
                new GUITextBlock(new RectTransform(new Point(pingColumnWidth, paddedFrame.Rect.Height), paddedFrame.RectTransform), client.Ping.ToString(), textAlignment: Alignment.Center),
                permissionIcon));

            if (client.Character is { } character)
            {
                CreateWalletCrewFrame(character, paddedFrame);
            }

            paddedFrame.Recalculate();
        }

        private int GetTeamIndex(Client client)
        {
            if (teamIDs.Count <= 1) { return 0; }

            if (client.Character != null)
            {
                return teamIDs.IndexOf(client.Character.TeamID);
            }

            if (client.CharacterID != 0)
            {
                foreach (Character c in crew)
                {
                    if (client.CharacterID == c.ID)
                    {
                        return teamIDs.IndexOf(c.TeamID);
                    }
                }
            }
            else
            {
                foreach (Character c in crew)
                {
                    if (client.Name == c.Name)
                    {
                        return teamIDs.IndexOf(c.TeamID);
                    }
                }
            }

            return teamIDs.IndexOf(client.TeamID);
        }

        private void CreateWalletCrewFrame(Character character, GUILayoutGroup paddedFrame)
        {
            if (GameMain.GameSession?.Campaign is not MultiPlayerCampaign) { return; }

            GUILayoutGroup walletLayout = new GUILayoutGroup(new RectTransform(new Point(walletColumnWidth, paddedFrame.Rect.Height), paddedFrame.RectTransform, Anchor.Center), childAnchor: Anchor.Center)
            {
                CanBeFocused = false
            };

            GUILayoutGroup paddedLayoutGroup = new GUILayoutGroup(new RectTransform(new Vector2(0.9f, 1f), walletLayout.RectTransform, Anchor.Center), isHorizontal: true)
            {
                Stretch = true
            };

            new GUIFrame(new RectTransform(Vector2.One, paddedLayoutGroup.RectTransform), style: null)
            {
                IgnoreLayoutGroups = true,
                ToolTip = TextManager.Get("walletdescription")
            };

            if (character.IsBot) { return; }

            Sprite walletSprite = GUIStyle.CrewWalletIconSmall.Value.Sprite;

            GUIImage icon = new GUIImage(new RectTransform(Vector2.One, paddedLayoutGroup.RectTransform, scaleBasis: ScaleBasis.BothHeight), walletSprite, scaleToFit: true) { CanBeFocused =  false };
            GUITextBlock walletBlock = new GUITextBlock(new RectTransform(Vector2.One, paddedLayoutGroup.RectTransform), string.Empty, textAlignment: Alignment.Right, font: GUIStyle.Font)
            {
                AutoScaleHorizontal = true,
                Padding = Vector4.Zero,
                CanBeFocused = false
            };

            GUIImage largeIcon = new GUIImage(new RectTransform(Vector2.One, paddedLayoutGroup.RectTransform), walletSprite, scaleToFit: true)
            {
                CanBeFocused = false,
                IgnoreLayoutGroups = true,
                Visible = false
            };

            if (character.IsBot)
            {
                largeIcon.Visible = true;
                icon.Visible = false;
                walletBlock.Visible = false;
                largeIcon.Enabled = false;
                return;
            }

            walletLayout.Recalculate();
            paddedLayoutGroup.Recalculate();
            SetWalletText(walletBlock, character.Wallet, icon, largeIcon);

            if (GameMain.GameSession?.Campaign is MultiPlayerCampaign campaign)
            {
                Identifier eventIdentifier = new Identifier($"{nameof(CreateWalletCrewFrame)}.{character.ID}");
                campaign.OnMoneyChanged.RegisterOverwriteExisting(eventIdentifier, e =>
                {
                    if (!e.Owner.TryUnwrap(out var owner) || owner != character) { return; }
                    SetWalletText(walletBlock, e.Wallet, icon, largeIcon);
                });
                registeredEvents.Add(eventIdentifier);
            }

            static void SetWalletText(GUITextBlock block, Wallet wallet, GUIImage icon, GUIImage largeIcon)
            {
                const int million = 1000000,
                          tooSmallPixelTreshold = 50; // 50 pixels is just not enough to see any meaningful info

                block.Text = TextManager.FormatCurrency(wallet.Balance);
                block.ToolTip = string.Empty;

                if (wallet.Balance >= million)
                {
                    block.Text = TextManager.Get("crewwallet.balance.toomuchtoshow");
                    block.ToolTip = block.Text;
                }

                largeIcon.Visible = false;
                icon.Visible = true;
                block.Visible = true;

                if (tooSmallPixelTreshold > block.Rect.Width)
                {
                    largeIcon.Visible = true;
                    icon.Visible = false;
                    block.Visible = false;
                    largeIcon.ToolTip = block.Text;
                }
            }
        }

        private void CreateNameWithPermissionIcon(Client client, GUILayoutGroup paddedFrame, out GUIImage permissionIcon)
        {
            GUITextBlock characterNameBlock;
            Sprite permissionIconSprite = GetPermissionIcon(client);
            JobPrefab prefab = client.Character?.Info?.Job?.Prefab;
            Color nameColor = prefab != null ? prefab.UIColor : Color.White;

            Point iconSize = new Point((int)(paddedFrame.Rect.Height * 0.8f));
            float characterNameWidthAdjustment = (iconSize.X + paddedFrame.AbsoluteSpacing) / characterColumnWidth;

            characterNameBlock = new GUITextBlock(new RectTransform(new Point(characterColumnWidth, paddedFrame.Rect.Height), paddedFrame.RectTransform),
                ToolBox.LimitString(client.Name, GUIStyle.Font, (int)(characterColumnWidth - paddedFrame.Rect.Width * characterNameWidthAdjustment)), textAlignment: Alignment.Center, textColor: nameColor);

            float iconWidth = iconSize.X / (float)characterColumnWidth;
            int xOffset = (int)(jobColumnWidth + characterNameBlock.TextPos.X - GUIStyle.Font.MeasureString(characterNameBlock.Text).X / 2f - paddedFrame.AbsoluteSpacing - iconWidth * paddedFrame.Rect.Width);
            permissionIcon = new GUIImage(new RectTransform(new Vector2(iconWidth, 1f), paddedFrame.RectTransform) { AbsoluteOffset = new Point(xOffset + 2, 0) }, permissionIconSprite) { IgnoreLayoutGroups = true };

            if (client.Character != null && client.Character.IsDead)
            {
                characterNameBlock.Strikethrough = new GUITextBlock.StrikethroughSettings(null, GUI.IntScale(1f), GUI.IntScale(5f));
            }
        }

        private Sprite GetPermissionIcon(Client client)
        {
            if (GameMain.NetworkMember == null || client == null || !client.HasPermissions) { return null; }

            if (client.IsOwner) // Owner cannot be kicked
            {
                return ownerIcon;
            }
            else
            {
                return moderatorIcon;
            }
        }

        private void DrawNotInGameIcon(SpriteBatch spriteBatch, Rectangle area, Client client)
        {
            if (client.Spectating)
            {
                spectateIcon.Draw(spriteBatch, area, Color.White);
            }
            else if (client.Character != null && client.Character.IsDead)
            {
                client.Character.Info?.DrawJobIcon(spriteBatch, area);
            }
            else
            {
                Vector2 stringOffset = GUIStyle.Font.MeasureString(inLobbyString) / 2f;
                GUIStyle.Font.DrawString(spriteBatch, inLobbyString, area.Center.ToVector2() - stringOffset, Color.White);
            }
        }

        private void DrawDisconnectedIcon(SpriteBatch spriteBatch, Rectangle area)
        {
            disconnectedIcon.Draw(spriteBatch, area, GUIStyle.Red);
        }

        /// <summary>
        /// Select an element from CrewListFrame
        /// </summary>
        private bool SelectElement(object userData, GUIComponent crewList)
        {
            Character character = userData as Character;
            Client client = userData as Client;

            GUIComponent existingPreview = infoFrameHolder.FindChild("SelectedCharacter");
            if (existingPreview != null) { infoFrameHolder.RemoveChild(existingPreview); }

            GUIFrame background = new GUIFrame(new RectTransform(new Vector2(0.543f, 0.69f), infoFrameHolder.RectTransform, Anchor.TopRight, Pivot.TopLeft) { RelativeOffset = new Vector2(-0.061f, 0) })
            {
                UserData = "SelectedCharacter"
            };

            if (character != null)
            {
                if (GameMain.Client is null)
                {
                    GUIComponent preview = character.Info.CreateInfoFrame(background, false, null);
                }
                else
                {
                    GUIComponent preview = character.Info.CreateInfoFrame(background, false, GetPermissionIcon(GameMain.Client.ConnectedClients.Find(c => c.Character == character)));

                    GameMain.Client.SelectCrewCharacter(character, preview);
                    if (!character.IsBot && GameMain.GameSession?.Campaign is MultiPlayerCampaign mpCampaign) { CreateWalletFrame(background, character, mpCampaign); }
                }

                if (background.FindChild(TalentMenu.ManageBotTalentsButtonUserData, recursive: true) is GUIButton { Enabled: true } talentButton)
                {
                    talentButton.OnClicked = (button, o) =>
                    {
                        talentMenu.CreateGUI(infoFrameHolder, character);
                        return true;
                    };
                }
            }
            else if (client != null)
            {
                GUIComponent preview = CreateClientInfoFrame(background, client, GetPermissionIcon(client));
                GameMain.Client?.SelectCrewClient(client, preview);
                if (client.Character != null && GameMain.GameSession?.Campaign is MultiPlayerCampaign mpCampaign)
                {
                    CreateWalletFrame(background, client.Character, mpCampaign);
                }
            }

            return true;
        }

        private void CreateWalletFrame(GUIComponent parent, Character character, MultiPlayerCampaign campaign)
        {
            if (campaign is null) { throw new ArgumentNullException(nameof(campaign), "Tried to create a wallet frame when campaign was null"); }
            if (character is null) { throw new ArgumentNullException(nameof(character), "Tried to create a wallet frame for a null character");}
            isTransferMenuOpen = false;
            transferMenuOpenState = 1f;
            ImmutableHashSet<Character> salaryCrew = GameSession.GetSessionCrewCharacters(CharacterType.Player).Where(c => c != character).ToImmutableHashSet();

            Wallet targetWallet = character.Wallet;

            GUIFrame walletFrame = new GUIFrame(new RectTransform(new Vector2(1f, 0.35f), parent.RectTransform, anchor: Anchor.TopLeft)
            {
                RelativeOffset = new Vector2(0, 1.02f)
            });

            GUILayoutGroup walletLayout = new GUILayoutGroup(new RectTransform(ToolBox.PaddingSizeParentRelative(walletFrame.RectTransform, 0.9f), walletFrame.RectTransform, anchor: Anchor.Center));

            GUILayoutGroup headerLayout = new GUILayoutGroup(new RectTransform(new Vector2(1f, 0.33f), walletLayout.RectTransform), isHorizontal: true);
            GUIImage icon = new GUIImage(new RectTransform(Vector2.One, headerLayout.RectTransform, scaleBasis: ScaleBasis.BothHeight), style: "CrewWalletIconLarge", scaleToFit: true);
            float relativeX =  icon.RectTransform.NonScaledSize.X / (float)icon.Parent.RectTransform.NonScaledSize.X;
            GUILayoutGroup headerTextLayout = new GUILayoutGroup(new RectTransform(new Vector2(1.0f - relativeX, 1f), headerLayout.RectTransform), isHorizontal: true) { Stretch = true };
            new GUITextBlock(new RectTransform(new Vector2(0.5f, 1f), headerTextLayout.RectTransform), TextManager.Get("crewwallet.wallet"), font: GUIStyle.LargeFont);
            GUIFrame walletTooltipFrame = new GUIFrame(new RectTransform(Vector2.One, headerLayout.RectTransform), style: null)
            {
                IgnoreLayoutGroups = true,
                ToolTip = TextManager.Get("walletdescription")
            };
            GUITextBlock moneyBlock = new GUITextBlock(new RectTransform(new Vector2(0.5f, 1f), headerTextLayout.RectTransform), TextManager.FormatCurrency(targetWallet.Balance), font: GUIStyle.SubHeadingFont, textAlignment: Alignment.Right);

            GUILayoutGroup middleLayout = new GUILayoutGroup(new RectTransform(new Vector2(1f, 0.66f), walletLayout.RectTransform));
            GUILayoutGroup salaryTextLayout = new GUILayoutGroup(new RectTransform(new Vector2(1f, 0.5f), middleLayout.RectTransform), isHorizontal: true);
            GUITextBlock salaryTitle = new GUITextBlock(new RectTransform(new Vector2(0.5f, 1f), salaryTextLayout.RectTransform), TextManager.Get("crewwallet.salary"), font: GUIStyle.SubHeadingFont, textAlignment: Alignment.BottomLeft);
            GUITextBlock rewardBlock = new GUITextBlock(new RectTransform(new Vector2(0.5f, 1f), salaryTextLayout.RectTransform), string.Empty, textAlignment: Alignment.BottomRight);
            GUIFrame salaryTooltipFrame = new GUIFrame(new RectTransform(Vector2.One, middleLayout.RectTransform), style: null)
            {
                IgnoreLayoutGroups = true,
                ToolTip = TextManager.Get("crewwallet.salary.tooltip")
            };
            GUILayoutGroup sliderLayout = new GUILayoutGroup(new RectTransform(new Vector2(1f, 0.5f), middleLayout.RectTransform), isHorizontal: true, childAnchor: Anchor.Center);
            GUIScrollBar salarySlider = new GUIScrollBar(new RectTransform(new Vector2(0.9f, 1f), sliderLayout.RectTransform), style: "GUISlider", barSize: 0.03f)
            {
                Range = new Vector2(0, 1),
                BarScrollValue = targetWallet.RewardDistribution / 100f,
                Step = 0.01f,
                BarSize = 0.1f,
                OnMoved = (bar, scroll) =>
                {
                    int rewardDistribution = RoundRewardDistribution(scroll, bar.Step);
                    SetRewardText(rewardDistribution, rewardBlock);
                    return true;
                },
                OnReleased = (bar, scroll) =>
                {
                    int newRewardDistribution = RoundRewardDistribution(scroll, bar.Step);
                    if (newRewardDistribution == targetWallet.RewardDistribution) { return false; }
                    SetRewardDistribution(Option.Some(character), newRewardDistribution);
                    return true;
                }
            };

            SetRewardText(targetWallet.RewardDistribution, rewardBlock);

// @formatter:off
            GUIScissorComponent scissorComponent = new GUIScissorComponent(new RectTransform(new Vector2(0.85f, 1.25f), walletFrame.RectTransform, Anchor.BottomCenter, Pivot.TopCenter))
            {
                CanBeFocused = false
            };
            transferMenu = new GUIFrame(new RectTransform(Vector2.One, scissorComponent.Content.RectTransform));

            GUILayoutGroup transferMenuLayout = new GUILayoutGroup(new RectTransform(new Vector2(1f, 0.8f), transferMenu.RectTransform, Anchor.BottomLeft), childAnchor: Anchor.Center);
                GUILayoutGroup paddedTransferMenuLayout = new GUILayoutGroup(new RectTransform(ToolBox.PaddingSizeParentRelative(transferMenuLayout.RectTransform, 0.85f), transferMenuLayout.RectTransform));
                    GUILayoutGroup mainLayout = new GUILayoutGroup(new RectTransform(new Vector2(1f, 0.5f), paddedTransferMenuLayout.RectTransform), isHorizontal: true, childAnchor: Anchor.CenterLeft);
                        GUILayoutGroup leftLayout = new GUILayoutGroup(new RectTransform(new Vector2(0.5f, 1f), mainLayout.RectTransform));
                            GUITextBlock leftName = new GUITextBlock(new RectTransform(new Vector2(1f, 0.5f), leftLayout.RectTransform), character.Name, textAlignment: Alignment.CenterLeft, font: GUIStyle.SubHeadingFont);
                            GUITextBlock leftBalance = new GUITextBlock(new RectTransform(new Vector2(1f, 0.5f), leftLayout.RectTransform), TextManager.FormatCurrency(targetWallet.Balance), textAlignment: Alignment.Left) { TextColor = GUIStyle.Blue };
                        GUILayoutGroup rightLayout = new GUILayoutGroup(new RectTransform(new Vector2(0.5f, 1f), mainLayout.RectTransform), childAnchor: Anchor.TopRight);
                            GUITextBlock rightName = new GUITextBlock(new RectTransform(new Vector2(1f, 0.5f), rightLayout.RectTransform), string.Empty, font: GUIStyle.SubHeadingFont, textAlignment: Alignment.CenterRight);
                            GUITextBlock rightBalance = new GUITextBlock(new RectTransform(new Vector2(1f, 0.5f), rightLayout.RectTransform), string.Empty, textAlignment: Alignment.Right) { TextColor = GUIStyle.Red };
                        GUILayoutGroup centerLayout = new GUILayoutGroup(new RectTransform(new Vector2(1f, 0.9f), mainLayout.RectTransform, Anchor.Center), childAnchor: Anchor.Center) { IgnoreLayoutGroups = true };
                            new GUIFrame(new RectTransform(new Vector2(0f, 1f), centerLayout.RectTransform, Anchor.Center), style: "VerticalLine") { IgnoreLayoutGroups = true };
                            GUIButton centerButton = new GUIButton(new RectTransform(new Vector2(1f), centerLayout.RectTransform, scaleBasis: ScaleBasis.BothHeight, anchor: Anchor.Center), style: "GUIButtonTransferArrow");

                    GUILayoutGroup inputLayout = new GUILayoutGroup(new RectTransform(new Vector2(1f, 0.25f), paddedTransferMenuLayout.RectTransform), childAnchor: Anchor.Center);
                        GUINumberInput transferAmountInput = new GUINumberInput(new RectTransform(new Vector2(0.5f, 1f), inputLayout.RectTransform), NumberType.Int, buttonVisibility: GUINumberInput.ButtonVisibility.ForceHidden)
                        {
                            MinValueInt = 0
                        };

                    GUILayoutGroup buttonLayout = new GUILayoutGroup(new RectTransform(new Vector2(1f, 0.25f), paddedTransferMenuLayout.RectTransform), childAnchor: Anchor.Center);
                        GUILayoutGroup centerButtonLayout = new GUILayoutGroup(new RectTransform(new Vector2(0.75f, 1f), buttonLayout.RectTransform), isHorizontal: true);
                            GUIButton resetButton = new GUIButton(new RectTransform(new Vector2(0.5f, 1f), centerButtonLayout.RectTransform), TextManager.Get("reset"), style: "GUIButtonFreeScale") { Enabled = false };
                            GUIButton confirmButton = new GUIButton(new RectTransform(new Vector2(0.5f, 1f), centerButtonLayout.RectTransform), TextManager.Get("confirm"), style: "GUIButtonFreeScale") { Enabled = false };
// @formatter:on
            ImmutableArray<GUILayoutGroup> layoutGroups = ImmutableArray.Create(transferMenuLayout, paddedTransferMenuLayout, mainLayout, leftLayout, rightLayout);
            MedicalClinicUI.EnsureTextDoesntOverflow(character.Name, leftName, leftLayout.Rect, layoutGroups);
            transferMenuButton = new GUIButton(new RectTransform(new Vector2(0.5f, 0.2f), walletFrame.RectTransform, Anchor.BottomCenter, Pivot.TopCenter), style: "UIToggleButtonVertical")
            {
                ToolTip = TextManager.Get("crewwallet.transfer.tooltip"),
                OnClicked = (button, o) =>
                {
                    isTransferMenuOpen = !isTransferMenuOpen;
                    if (!isTransferMenuOpen)
                    {
                        transferAmountInput.IntValue = 0;
                    }
                    ToggleTransferMenuIcon(button, open: isTransferMenuOpen);
                    return true;
                }
            };

            Identifier eventIdentifier = nameof(CreateWalletFrame).ToIdentifier();

            ToggleTransferMenuIcon(transferMenuButton, open: isTransferMenuOpen);
            ToggleCenterButton(centerButton, isSending);

            Wallet otherWallet;
            GameMain.Client?.OnPermissionChanged.RegisterOverwriteExisting(eventIdentifier, e => UpdateWalletInterface(registerEvents: false));
            UpdateWalletInterface(registerEvents: true);

            void UpdateWalletInterface(bool registerEvents)
            {
                if (!(Character.Controlled is { } myCharacter))
                {
                    salarySlider.Enabled = false;
                    transferAmountInput.Enabled = false;
                    centerButton.Enabled = false;
                    confirmButton.Enabled = false;
                    return;
                }

                bool hasMoneyPermissions = CampaignMode.AllowedToManageWallets();
                salarySlider.Enabled = hasMoneyPermissions;

                switch (hasMoneyPermissions)
                {
                    case true:
                        rightName.Text = TextManager.Get("crewwallet.bank");
                        otherWallet = campaign.Bank;
                        break;
                    case false when character == myCharacter:
                        rightName.Text = TextManager.Get("crewwallet.bank");
                        otherWallet = campaign.Bank;
                        isSending = true;
                        ToggleCenterButton(centerButton, isSending);
                        break;
                    default:
                        rightName.Text = myCharacter.Name;
                        otherWallet = campaign.PersonalWallet;
                        break;
                }

                MedicalClinicUI.EnsureTextDoesntOverflow(rightName.Text.ToString(), rightName, rightLayout.Rect, layoutGroups);

                UpdatedConfirmButtonText();

                if (!hasMoneyPermissions)
                {
                    if (character != Character.Controlled)
                    {
                        centerButton.Enabled = centerButton.CanBeFocused = false;
                    }

                    salarySlider.Enabled = salarySlider.CanBeFocused = false;
                }

                leftBalance.Text = TextManager.FormatCurrency(otherWallet.Balance);

                UpdateAllInputs();

                if (!registerEvents) { return; }

                centerButton.OnClicked = (btn, o) =>
                {
                    isSending = !isSending;
                    UpdatedConfirmButtonText();
                    ToggleCenterButton(btn, isSending);
                    UpdateAllInputs();
                    return true;
                };

                transferAmountInput.OnValueChanged = input =>
                {
                    UpdateInputs();
                };

                transferAmountInput.OnValueEntered = input =>
                {
                    UpdateAllInputs();
                };

                resetButton.OnClicked = (button, o) =>
                {
                    transferAmountInput.IntValue = 0;
                    UpdateAllInputs();
                    return true;
                };

                confirmButton.OnClicked = (button, o) =>
                {
                    int amount = transferAmountInput.IntValue;
                    if (amount == 0) { return false; }

                    Option<Character> target1 = Option<Character>.Some(character),
                                      target2 = otherWallet == campaign.Bank ? Option<Character>.None() : Option<Character>.Some(myCharacter);
                    if (isSending) { (target1, target2) = (target2, target1); }

                    SendTransaction(target1, target2, amount);
                    isTransferMenuOpen = false;
                    ToggleTransferMenuIcon(transferMenuButton, isTransferMenuOpen);
                    return true;
                };

                campaign.OnMoneyChanged.RegisterOverwriteExisting(eventIdentifier, e =>
                {
                    if (e.Wallet == targetWallet)
                    {
                        moneyBlock.Text = TextManager.FormatCurrency(e.Info.Balance);
                        salarySlider.BarScrollValue = e.Info.RewardDistribution / 100f;
                        SetRewardText(e.Info.RewardDistribution, rewardBlock);
                    }

                    UpdateAllInputs();
                });

                registeredEvents.Add(eventIdentifier);

                void UpdatedConfirmButtonText()
                {
                    confirmButton.Text = TextManager.Get(hasMoneyPermissions || isSending ? "confirm" : "crewwallet.requestmoney");
                }

                void UpdateAllInputs()
                {
                    UpdateInputs();
                    UpdateMaxInput();
                }

                void UpdateInputs()
                {
                    confirmButton.Enabled = resetButton.Enabled = transferAmountInput.IntValue > 0;
                    if (transferAmountInput.IntValue == 0)
                    {
                        rightBalance.Text = TextManager.FormatCurrency(otherWallet.Balance);
                        rightBalance.TextColor = GUIStyle.TextColorNormal;
                        leftBalance.Text = TextManager.FormatCurrency(targetWallet.Balance);
                        leftBalance.TextColor = GUIStyle.TextColorNormal;
                    }
                    else if (isSending)
                    {
                        rightBalance.Text = TextManager.FormatCurrency(otherWallet.Balance + transferAmountInput.IntValue);
                        rightBalance.TextColor = GUIStyle.Blue;
                        leftBalance.Text = TextManager.FormatCurrency(targetWallet.Balance - transferAmountInput.IntValue);
                        leftBalance.TextColor = GUIStyle.Red;
                    }
                    else
                    {
                        rightBalance.Text = TextManager.FormatCurrency(otherWallet.Balance - transferAmountInput.IntValue);
                        rightBalance.TextColor = GUIStyle.Red;
                        leftBalance.Text = TextManager.FormatCurrency(targetWallet.Balance + transferAmountInput.IntValue);
                        leftBalance.TextColor = GUIStyle.Blue;
                    }
                }

                void UpdateMaxInput()
                {
                    int maxValue = isSending ? targetWallet.Balance : otherWallet.Balance;
                    transferAmountInput.MaxValueInt = maxValue;

                    transferAmountInput.Enabled = true;
                    transferAmountInput.ToolTip = string.Empty;

                    if (!hasMoneyPermissions && GameMain.Client?.ServerSettings is { } serverSettings)
                    {
                        transferAmountInput.MaxValueInt = Math.Min(maxValue, serverSettings.MaximumMoneyTransferRequest);
                        if (serverSettings.MaximumMoneyTransferRequest <= 0)
                        {
                            transferAmountInput.Enabled = false;
                            transferAmountInput.ToolTip = TextManager.Get("wallettransferrequestdisabled");
                        }
                    }
                }
            }

            void SetRewardText(int value, GUITextBlock block)
            {
                var (_, percentage, sum) = Mission.GetRewardShare(value, salaryCrew, Option<int>.None());
                LocalizedString tooltip = string.Empty;
                block.TextColor = GUIStyle.TextColorNormal;

                if (sum > 100)
                {
                    tooltip = TextManager.GetWithVariables("crewwallet.salary.over100toolitp", ("[sum]", $"{(int)sum}"), ("[newvalue]", $"{percentage}"));
                    block.TextColor = GUIStyle.Orange;
                }

                LocalizedString text = TextManager.GetWithVariable("percentageformat", "[value]", $"{value}");

                block.Text = text;
                block.ToolTip = RichString.Rich(tooltip);
            }

            static void ToggleTransferMenuIcon(GUIButton btn, bool open)
            {
                foreach (GUIComponent child in btn.Children)
                {
                    child.SpriteEffects = open ? SpriteEffects.None : SpriteEffects.FlipVertically;
                }
            }

            static void ToggleCenterButton(GUIButton btn, bool isSending)
            {
                foreach (GUIComponent child in btn.Children)
                {
                    child.SpriteEffects = isSending ? SpriteEffects.None : SpriteEffects.FlipHorizontally;
                }
            }

            static void SendTransaction(Option<Character> to, Option<Character> from, int amount)
            {
                INetSerializableStruct transfer = new NetWalletTransfer
                {
                    Sender = from.Select(option => option.ID),
                    Receiver = to.Select(option => option.ID),
                    Amount = amount
                };
                IWriteMessage msg = new WriteOnlyMessage().WithHeader(ClientPacketHeader.TRANSFER_MONEY);
                transfer.Write(msg);
                GameMain.Client?.ClientPeer?.Send(msg, DeliveryMethod.Reliable);
            }
        }

        static void SetRewardDistribution(Option<Character> character, int newValue)
        {
            INetSerializableStruct transfer = new NetWalletSetSalaryUpdate
            {
                Target = character.Select(c => c.ID),
                NewRewardDistribution = newValue
            };
            IWriteMessage msg = new WriteOnlyMessage().WithHeader(ClientPacketHeader.REWARD_DISTRIBUTION);
            transfer.Write(msg);
            GameMain.Client?.ClientPeer?.Send(msg, DeliveryMethod.Reliable);
        }

        static void ResetRewardDistributions()
        {
            IWriteMessage msg = new WriteOnlyMessage().WithHeader(ClientPacketHeader.RESET_REWARD_DISTRIBUTION);
            GameMain.Client?.ClientPeer?.Send(msg, DeliveryMethod.Reliable);
        }

        static int RoundRewardDistribution(float scroll, float step) 
            => (int)MathUtils.RoundTowardsClosest(scroll * 100, step * 100);

        private GUIComponent CreateClientInfoFrame(GUIFrame frame, Client client, Sprite permissionIcon = null)
        {
            GUIComponent paddedFrame;

            if (client.Character?.Info == null)
            {
                paddedFrame = new GUILayoutGroup(new RectTransform(new Vector2(0.874f, 0.58f), frame.RectTransform, Anchor.TopCenter) { RelativeOffset = new Vector2(0.0f, 0.05f) })
                {
                    RelativeSpacing = 0.05f
                    //Stretch = true
                };

                var headerArea = new GUILayoutGroup(new RectTransform(new Vector2(1.0f, 0.322f), paddedFrame.RectTransform), isHorizontal: true);

                new GUICustomComponent(new RectTransform(new Vector2(0.425f, 1.0f), headerArea.RectTransform),
                    onDraw: (sb, component) => DrawNotInGameIcon(sb, component.Rect, client));

                GUIFont font = paddedFrame.Rect.Width < 280 ? GUIStyle.SmallFont : GUIStyle.Font;

                var headerTextArea = new GUILayoutGroup(new RectTransform(new Vector2(0.575f, 1.0f), headerArea.RectTransform))
                {
                    RelativeSpacing = 0.02f,
                    Stretch = true
                };

                GUITextBlock clientNameBlock = new GUITextBlock(new RectTransform(new Vector2(1.0f, 0.0f), headerTextArea.RectTransform), ToolBox.LimitString(client.Name, GUIStyle.Font, headerTextArea.Rect.Width), textColor: Color.White, font: GUIStyle.Font)
                {
                    ForceUpperCase = ForceUpperCase.Yes,
                    Padding = Vector4.Zero
                };

                if (permissionIcon != null)
                {
                    Point iconSize = permissionIcon.SourceRect.Size;
                    int iconWidth = (int)((float)clientNameBlock.Rect.Height / iconSize.Y * iconSize.X);
                    new GUIImage(new RectTransform(new Point(iconWidth, clientNameBlock.Rect.Height), clientNameBlock.RectTransform) { AbsoluteOffset = new Point(-iconWidth - 2, 0) }, permissionIcon) { IgnoreLayoutGroups = true };
                }

                new GUITextBlock(new RectTransform(new Vector2(1.0f, 0.0f), headerTextArea.RectTransform), client.Spectating ? TextManager.Get("playingasspectator") : TextManager.Get("tabmenu.inlobby"), textColor: Color.White, font: font, wrap: true)
                {
                    Padding = Vector4.Zero
                };
            }
            else
            {
                paddedFrame = client.Character.Info.CreateInfoFrame(frame, false, permissionIcon);
            }

            return paddedFrame;
        }

        private void CreateMultiPlayerLogContent(GUIFrame crewFrame)
        {
            var logContainer = new GUIFrame(new RectTransform(new Vector2(0.543f, 0.717f), infoFrameHolder.RectTransform, Anchor.TopLeft, Pivot.TopRight) { RelativeOffset = new Vector2(-0.145f, 0) });
            var innerFrame = new GUIFrame(new RectTransform(new Vector2(0.900f, 0.900f), logContainer.RectTransform, Anchor.TopCenter, Pivot.TopCenter) { RelativeOffset = new Vector2(0f, 0.0475f) }, style: null);
            var content = new GUILayoutGroup(new RectTransform(Vector2.One, innerFrame.RectTransform))
            {
                Stretch = true
            };

            logList = new GUIListBox(new RectTransform(Vector2.One, content.RectTransform))
            {
                Padding = new Vector4(0, 10 * GUI.Scale, 0, 10 * GUI.Scale),
                UserData = crewFrame,
                AutoHideScrollBar = false,
                Spacing = (int)(5 * GUI.Scale)
            };

            foreach ((string message, PlayerConnectionChangeType type) in storedMessages)
            {
                AddLineToLog(message, type);
            }

            logList.BarScroll = 1f;
        }

        private static readonly List<(string message, PlayerConnectionChangeType type)> storedMessages = new List<(string message, PlayerConnectionChangeType type)>();

        public static void StorePlayerConnectionChangeMessage(ChatMessage message)
        {
            if (!GameMain.GameSession?.IsRunning ?? true) { return; }

            string msg = ChatMessage.GetTimeStamp() + message.TextWithSender;
            storedMessages.Add((msg, message.ChangeType));

            if (GameSession.IsTabMenuOpen && SelectedTab == InfoFrameTab.Crew)
            {
                TabMenu instance = GameSession.TabMenuInstance;
                instance.AddLineToLog(msg, message.ChangeType);
                instance.RemoveCurrentElements();
                instance.CreateMultiPlayerList(true);
            }
        }

        private void RemoveCurrentElements()
        {
            for (int i = 0; i < crewListArray.Length; i++)
            {
                for (int j = 0; j < linkedGUIList.Count; j++)
                {
                    linkedGUIList[j].Remove(crewListArray[i].Content);
                }
            }

            linkedGUIList.Clear();
        }

        private void AddLineToLog(string line, PlayerConnectionChangeType type)
        {
            Color textColor = Color.White;

            switch (type)
            {
                case PlayerConnectionChangeType.Joined:
                    textColor = GUIStyle.Green;
                    break;
                case PlayerConnectionChangeType.Kicked:
                    textColor = GUIStyle.Orange;
                    break;
                case PlayerConnectionChangeType.Disconnected:
                    textColor = GUIStyle.Yellow;
                    break;
                case PlayerConnectionChangeType.Banned:
                    textColor = GUIStyle.Red;
                    break;
            }

            if (logList != null)
            {
                var textBlock = new GUITextBlock(new RectTransform(new Vector2(1.0f, 0.0f), logList.Content.RectTransform), RichString.Rich(line), wrap: true, font: GUIStyle.SmallFont)
                {
                    TextColor = textColor,
                    CanBeFocused = false,
                    UserData = line
                };
                textBlock.CalculateHeightFromText();
                if (textBlock.HasColorHighlight)
                {
                    foreach (var data in textBlock.RichTextData)
                    {
                        textBlock.ClickableAreas.Add(new GUITextBlock.ClickableArea()
                        {
                            Data = data,
                            OnClick = GameMain.NetLobbyScreen.SelectPlayer,
                            OnSecondaryClick = GameMain.NetLobbyScreen.ShowPlayerContextMenu
                        });
                    }
                }
            }
        }

        private void CreateMissionInfo(GUIFrame infoFrame)
        {
            if (Level.Loaded?.LevelData == null)
            {
                DebugConsole.ThrowError("Failed to display mission info in the tab menu (no level loaded).\n" + Environment.StackTrace);
                return;
            }

            infoFrame.ClearChildren();
            GUIFrame missionFrame = new GUIFrame(new RectTransform(Vector2.One, infoFrame.RectTransform, Anchor.TopCenter), style: "GUIFrameListBox");
            int padding = (int)(0.0245f * missionFrame.Rect.Height);
            GUIFrame missionFrameContent = new GUIFrame(new RectTransform(new Point(missionFrame.Rect.Width - padding * 2, missionFrame.Rect.Height - padding * 2), infoFrame.RectTransform, Anchor.Center), style: null);
            Location location = GameMain.GameSession.StartLocation;
            if (Level.Loaded.Type == LevelData.LevelType.LocationConnection)
            {
                location ??= GameMain.GameSession.EndLocation;
            }

            GUILayoutGroup locationInfoContainer = new GUILayoutGroup(new RectTransform(new Vector2(1.0f, 0.3f), missionFrameContent.RectTransform))
            {
                AbsoluteSpacing = GUI.IntScale(10)
            };

            Sprite portrait = location.Type.GetPortrait(location.PortraitId);
            bool hasPortrait = portrait != null && portrait.SourceRect.Width > 0 && portrait.SourceRect.Height > 0;
            int contentWidth = missionFrameContent.Rect.Width;
            if (hasPortrait)
            {
                float portraitAspectRatio = portrait.SourceRect.Width / portrait.SourceRect.Height;
                GUIImage portraitImage = new GUIImage(new RectTransform(new Vector2(0.5f, 1f), locationInfoContainer.RectTransform, Anchor.CenterRight), portrait, scaleToFit: true)
                {
                    IgnoreLayoutGroups = true
                };
                locationInfoContainer.Recalculate();
                portraitImage.RectTransform.NonScaledSize = new Point(Math.Min((int)(portraitImage.Rect.Size.Y * portraitAspectRatio), portraitImage.Rect.Width), portraitImage.Rect.Size.Y);
            }

            new GUITextBlock(new RectTransform(new Vector2(1.0f, 0.0f), locationInfoContainer.RectTransform), location.DisplayName, font: GUIStyle.LargeFont);
            new GUITextBlock(new RectTransform(new Vector2(1.0f, 0.0f), locationInfoContainer.RectTransform), location.Type.Name, font: GUIStyle.SubHeadingFont);

            if (location.Faction?.Prefab != null)
            {
                var factionLabel = new GUITextBlock(new RectTransform(new Vector2(0.5f, 0.0f), locationInfoContainer.RectTransform),
                    TextManager.Get("Faction"), font: GUIStyle.SubHeadingFont, textAlignment: Alignment.CenterLeft);
                new GUITextBlock(new RectTransform(new Vector2(1.0f, 1.0f), factionLabel.RectTransform), location.Faction.Prefab.Name, textAlignment: Alignment.CenterRight);
            }
            var biomeLabel = new GUITextBlock(new RectTransform(new Vector2(0.5f, 0.0f), locationInfoContainer.RectTransform),
                TextManager.Get("Biome", "location"), font: GUIStyle.SubHeadingFont, textAlignment: Alignment.CenterLeft);
            new GUITextBlock(new RectTransform(new Vector2(1.0f, 1.0f), biomeLabel.RectTransform), Level.Loaded.LevelData.Biome.DisplayName, textAlignment: Alignment.CenterRight);
            var difficultyLabel = new GUITextBlock(new RectTransform(new Vector2(0.5f, 0.0f), locationInfoContainer.RectTransform),
                TextManager.Get("LevelDifficulty"), font: GUIStyle.SubHeadingFont, textAlignment: Alignment.CenterLeft);
            new GUITextBlock(new RectTransform(new Vector2(1.0f, 1.0f), difficultyLabel.RectTransform), TextManager.GetWithVariable("percentageformat", "[value]", ((int)Level.Loaded.LevelData.Difficulty).ToString()), textAlignment: Alignment.CenterRight);

            new GUIFrame(new RectTransform(new Vector2(1.0f, 0.01f), missionFrameContent.RectTransform) { AbsoluteOffset = new Point(0, locationInfoContainer.Rect.Height + padding) }, style: "HorizontalLine")
            {
                CanBeFocused = false
            };

            int locationInfoYOffset = locationInfoContainer.Rect.Height + padding * 2;

            GUIListBox missionList = new GUIListBox(new RectTransform(new Point(contentWidth, missionFrameContent.Rect.Height - locationInfoYOffset), missionFrameContent.RectTransform, Anchor.TopCenter) { AbsoluteOffset = new Point(0, locationInfoYOffset) });
            missionList.ContentBackground.Color = Color.Transparent;
            missionList.Spacing = GUI.IntScale(15);

            if (GameMain.GameSession?.Missions != null)
            {
                foreach (Mission mission in GameMain.GameSession.Missions)
                {
                    if (!mission.Prefab.ShowInMenus) { continue; }

                    var textContent = new List<LocalizedString>()
                    {
                        mission.GetMissionRewardText(Submarine.MainSub),
                        mission.GetReputationRewardText(),
                        mission.Description
                    };
                    textContent.AddRange(mission.ShownMessages);

                    RoundSummary.CreateMissionEntry(
                        missionList.Content, 
                        mission.Name, 
                        textContent, 
                        mission.Difficulty ?? 0, 
                        mission.Prefab.Icon, mission.Prefab.IconColor,
                        mission.GetDifficultyToolTipText(),
                        out GUIImage missionIcon);
                    if (missionIcon != null)
                    {
                        UpdateMissionStateIcon();
                        mission.OnMissionStateChanged += (mission) => UpdateMissionStateIcon();

                        void UpdateMissionStateIcon()
                        {
                            if (mission.DisplayAsCompleted || mission.DisplayAsFailed)
                            {
                                RoundSummary.UpdateMissionStateIcon(mission.DisplayAsCompleted, missionIcon);
                            }
                        }
                    }
                }
            }
            else
            {
                GUILayoutGroup missionTextGroup = new GUILayoutGroup(new RectTransform(new Vector2(1f, 0f), missionList.RectTransform, Anchor.CenterLeft), false, childAnchor: Anchor.TopLeft);
                new GUITextBlock(new RectTransform(new Vector2(1.0f, 0.0f), missionTextGroup.RectTransform), TextManager.Get("NoMission"), font: GUIStyle.LargeFont);
            }

            GameMain.GameSession?.EventManager?.EventLog?.CreateEventLogUI(missionList.Content);
            GameMain.GameSession.EnableEventLogNotificationIcon(enabled: false);

            RoundSummary.AddSeparators(missionList.Content);
        }

        private static void CreateSubmarineInfo(GUIFrame infoFrame, Submarine sub)
        {
            if (sub == null) { return; }

            GUIFrame subInfoFrame = new GUIFrame(new RectTransform(Vector2.One, infoFrame.RectTransform, Anchor.TopCenter), style: "GUIFrameListBox");
            GUIFrame paddedFrame = new GUIFrame(new RectTransform(Vector2.One * 0.97f, subInfoFrame.RectTransform, Anchor.Center), style: null);

            var previewButton = new GUIButton(new RectTransform(new Vector2(1.0f, 0.43f), paddedFrame.RectTransform), style: null)
            {
                OnClicked = (btn, obj) => { SubmarinePreview.Create(sub.Info); return false; },
            };

            var previewImage = sub.Info.PreviewImage ?? SubmarineInfo.SavedSubmarines.FirstOrDefault(s => s.Name.Equals(sub.Info.Name, StringComparison.OrdinalIgnoreCase))?.PreviewImage;
            if (previewImage == null)
            {
                new GUITextBlock(new RectTransform(Vector2.One, previewButton.RectTransform), TextManager.Get("SubPreviewImageNotFound"));
            }
            else
            {
                var submarinePreviewBackground = new GUIFrame(new RectTransform(Vector2.One, previewButton.RectTransform), style: null)
                {
                    Color = Color.Black,
                    HoverColor = Color.Black,
                    SelectedColor = Color.Black,
                    PressedColor = Color.Black,
                    CanBeFocused = false,
                };
                new GUIImage(new RectTransform(new Vector2(0.98f), submarinePreviewBackground.RectTransform, Anchor.Center), previewImage, scaleToFit: true) { CanBeFocused = false };
                new GUIFrame(new RectTransform(Vector2.One, submarinePreviewBackground.RectTransform), "InnerGlow", color: Color.Black) { CanBeFocused = false };
            }

            new GUIFrame(new RectTransform(Vector2.One * 0.12f, previewButton.RectTransform, anchor: Anchor.BottomRight, pivot: Pivot.BottomRight, scaleBasis: ScaleBasis.BothHeight)
            {
                AbsoluteOffset = new Point((int)(0.03f * previewButton.Rect.Height))
            },
                "ExpandButton", Color.White)
            {
                Color = Color.White,
                HoverColor = Color.White,
                PressedColor = Color.White
            };

            var subInfoTextLayout = new GUILayoutGroup(new RectTransform(Vector2.One, paddedFrame.RectTransform));

            LocalizedString className = !sub.Info.HasTag(SubmarineTag.Shuttle) ?
                TextManager.GetWithVariables("submarine.classandtier", 
                    ("[class]", sub.Info.Class.Name),
                    ("[tier]", TextManager.Get($"submarinetier.{sub.Info.Tier}"))) :
                TextManager.Get("shuttle");

            int nameHeight = (int)GUIStyle.LargeFont.MeasureString(sub.Info.DisplayName, true).Y;
            int classHeight = (int)GUIStyle.SubHeadingFont.MeasureString(className).Y;

            var submarineNameText = new GUITextBlock(new RectTransform(new Point(subInfoTextLayout.Rect.Width, nameHeight + HUDLayoutSettings.Padding / 2), subInfoTextLayout.RectTransform), sub.Info.DisplayName, textAlignment: Alignment.CenterLeft, font: GUIStyle.LargeFont) { CanBeFocused = false };
            submarineNameText.RectTransform.MinSize = new Point(0, (int)submarineNameText.TextSize.Y);
            var submarineClassText = new GUITextBlock(new RectTransform(new Point(subInfoTextLayout.Rect.Width, classHeight), subInfoTextLayout.RectTransform), className, textAlignment: Alignment.CenterLeft, font: GUIStyle.SubHeadingFont) { CanBeFocused = false };
            submarineClassText.RectTransform.MinSize = new Point(0, (int)submarineClassText.TextSize.Y);

            if (GameMain.GameSession?.GameMode is CampaignMode campaign)
            {
                GUILayoutGroup headerLayout = new GUILayoutGroup(new RectTransform(new Vector2(1f, 0.09f), paddedFrame.RectTransform) { RelativeOffset = new Vector2(0f, 0.43f) }, isHorizontal: true) { Stretch = true };
                GUIImage headerIcon = new GUIImage(new RectTransform(Vector2.One, headerLayout.RectTransform, scaleBasis: ScaleBasis.BothHeight), style: "SubmarineIcon");
                new GUITextBlock(new RectTransform(Vector2.One, headerLayout.RectTransform), TextManager.Get("uicategory.upgrades"), font: GUIStyle.LargeFont);

                var upgradeRootLayout = new GUILayoutGroup(new RectTransform(new Vector2(1f, 0.48f), paddedFrame.RectTransform, Anchor.BottomLeft, Pivot.BottomLeft), isHorizontal: true);

                var upgradeCategoryPanel = UpgradeStore.CreateUpgradeCategoryList(new RectTransform(new Vector2(0.4f, 1f), upgradeRootLayout.RectTransform));
                upgradeCategoryPanel.HideChildrenOutsideFrame = true;
                UpgradeStore.UpdateCategoryList(upgradeCategoryPanel, campaign, sub, UpgradeStore.GetApplicableCategories(sub).ToArray());
                GUIComponent[] toRemove = upgradeCategoryPanel.Content.FindChildren(c => !c.Enabled).ToArray();
                toRemove.ForEach(c => upgradeCategoryPanel.RemoveChild(c));

                var upgradePanel = new GUIListBox(new RectTransform(new Vector2(0.6f, 1f), upgradeRootLayout.RectTransform));
                upgradeCategoryPanel.OnSelected = (component, userData) =>
                {
                    upgradePanel.ClearChildren();
                    if (userData is UpgradeStore.CategoryData categoryData && Submarine.MainSub != null)
                    {
                        foreach (UpgradePrefab prefab in categoryData.Prefabs)
                        {
                            var frame = UpgradeStore.CreateUpgradeFrame(prefab, categoryData.Category, campaign, new RectTransform(new Vector2(1f, 0.3f), upgradePanel.Content.RectTransform), addBuyButton: false).Frame;
                            UpgradeStore.UpdateUpgradeEntry(frame, prefab, categoryData.Category, campaign);
                        }
                    }
                    return true;
                };
            }
            else
            {
                var specsListBox = new GUIListBox(new RectTransform(new Vector2(1f, 0.57f), paddedFrame.RectTransform, Anchor.BottomLeft, Pivot.BottomLeft))
                {
                    CurrentSelectMode = GUIListBox.SelectMode.None
                };
                sub.Info.CreateSpecsWindow(specsListBox, GUIStyle.Font,
                    includeTitle: false,
                    includeClass: false,
                    includeDescription: true);
            }
        }

        private GUIImage talentPointNotification, eventLogNotification;

        public static void CreateSkillList(Character character, CharacterInfo info, GUIListBox parent)
        {
            parent.Content.ClearChildren();
            List<GUITextBlock> skillNames = new List<GUITextBlock>();
            foreach (Skill skill in info.Job.GetSkills().OrderByDescending(static s => s.Level))
            {
                GUILayoutGroup skillContainer = new GUILayoutGroup(new RectTransform(new Vector2(1f, 0.0f), parent.Content.RectTransform), isHorizontal: true) { CanBeFocused = true };
                var skillName = new GUITextBlock(new RectTransform(new Vector2(0.7f, 0.0f), skillContainer.RectTransform), TextManager.Get($"skillname.{skill.Identifier}").Fallback(skill.Identifier.Value));
                skillNames.Add(skillName);
                skillName.RectTransform.MinSize = new Point(0, skillName.Rect.Height);
                skillContainer.RectTransform.MinSize = new Point(0, skillName.Rect.Height);

                new GUITextBlock(new RectTransform(new Vector2(0.15f, 1.0f), skillContainer.RectTransform), Math.Floor(skill.Level).ToString("F0"), textAlignment: Alignment.TopRight);

                float modifiedSkillLevel = MathF.Floor(character?.GetSkillLevel(skill.Identifier) ?? skill.Level);
                if (!MathUtils.NearlyEqual(MathF.Floor(modifiedSkillLevel), MathF.Floor(skill.Level)))
                {
                    int skillChange = (int)MathF.Floor(modifiedSkillLevel - MathF.Floor(skill.Level));
                    string stringColor = skillChange switch
                    {
                        > 0 => XMLExtensions.ToStringHex(GUIStyle.Green),
                        < 0 => XMLExtensions.ToStringHex(GUIStyle.Red),
                        _ => XMLExtensions.ToStringHex(GUIStyle.TextColorNormal)
                    };

                    RichString changeText = RichString.Rich($"(‖color:{stringColor}‖{(skillChange > 0 ? "+" : string.Empty) + skillChange}‖color:end‖)");
                    new GUITextBlock(new RectTransform(new Vector2(0.15f, 1.0f), skillContainer.RectTransform), changeText) { Padding = Vector4.Zero };
                }
                skillContainer.Recalculate();
            }

            parent.RecalculateChildren();
            GUITextBlock.AutoScaleAndNormalize(skillNames);
        }

        public void OnExperienceChanged(Character character)
        {
            if (character != Character.Controlled) { return; }
            talentMenu.UpdateTalentInfo();
        }

        public void OnClose()
        {
            if (!(GameMain.GameSession?.Campaign is { } campaign)) { return; }
            foreach (Identifier identifier in registeredEvents)
            {
                campaign.OnMoneyChanged.TryDeregister(identifier);
            }
        }
    }
}

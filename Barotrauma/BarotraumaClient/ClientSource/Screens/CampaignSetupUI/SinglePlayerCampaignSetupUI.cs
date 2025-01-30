using Barotrauma.Extensions;
using Barotrauma.IO;
using Microsoft.Xna.Framework;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Xml.Linq;

namespace Barotrauma
{
    sealed class SinglePlayerCampaignSetupUI : CampaignSetupUI
    {
        private GUIListBox subList;

        protected GUILayoutGroup subPreviewContainer;

        public CharacterInfo.AppearanceCustomizationMenu[] CharacterMenus { get; private set; }

        private GUIButton nextButton;
        private GUIListBox characterInfoColumns;
    
        public SinglePlayerCampaignSetupUI(GUIComponent newGameContainer, GUIComponent loadGameContainer)
            : base(newGameContainer, loadGameContainer)
        {
            CreateNewGameMenu();
        }

        private int currentPage = 0;
        private GUIListBox pageContainer;

        public void Update()
        {
            float targetScroll =
                (float)currentPage / ((float)pageContainer.Content.CountChildren - 1);

            pageContainer.BarScroll = MathHelper.Lerp(pageContainer.BarScroll, targetScroll, 0.2f);
            if (MathUtils.NearlyEqual(pageContainer.BarScroll, targetScroll, 0.001f))
            {
                pageContainer.BarScroll = targetScroll;
            }

            for (int i = 0; i < CharacterMenus.Length; i++)
            {
                CharacterMenus[i]?.Update();
            }

            pageContainer.HoverCursor = CursorState.Default;
            pageContainer.Content.HoverCursor = CursorState.Default;
        }

        public void SetPage(int pageIndex)
        {
            currentPage = pageIndex;
            for (int i = 0; i < pageContainer.Content.CountChildren; i++)
            {
                var child = pageContainer.Content.GetChild(i);
                child.CanBeFocused = (i == currentPage);
                child.GetAllChildren().ForEach(c =>
                {
                    if (c is GUIDropDown dd)
                    {
                        dd.Dropped = false;
                    }
                    c.CanBeFocused = (i == currentPage);
                });
            }
            var previewListBox = subPreviewContainer.GetAllChildren<GUIListBox>().FirstOrDefault();
            previewListBox?.GetAllChildren()?.ForEach(c =>
            {
                c.CanBeFocused = false;
            });
        }
        
        private void CreateNewGameMenu()
        {
            pageContainer =
                new GUIListBox(new RectTransform(Vector2.One, newGameContainer.RectTransform), style: null, isHorizontal: true)
                {
                    ScrollBarEnabled = false,
                    ScrollBarVisible = false,
                    AllowArrowKeyScroll = false,
                    HoverCursor = CursorState.Default
                };

            GUILayoutGroup createPageLayout()
            {
                var containerItem =
                    new GUIFrame(new RectTransform(Vector2.One, pageContainer.Content.RectTransform), style: null);
                return new GUILayoutGroup(new RectTransform(Vector2.One * 0.95f, containerItem.RectTransform,
                    Anchor.Center));
            }
            
            CreateFirstPage(createPageLayout());
            CreateSecondPage(createPageLayout());
            
            pageContainer.RecalculateChildren();
            pageContainer.GetAllChildren().ForEach(c =>
            {
                c.ClampMouseRectToParent = true;
            });
            pageContainer.GetAllChildren<GUIDropDown>().ForEach(dd =>
            {
                dd.ListBox.ClampMouseRectToParent = false;
                dd.ListBox.Content.ClampMouseRectToParent = false;
            });
            SetPage(0);
        }

        private void CreateFirstPage(GUILayoutGroup firstPageLayout)
        {
            firstPageLayout.RelativeSpacing = 0.02f;
            
            var columnContainer = new GUILayoutGroup(new RectTransform(new Vector2(1.0f, 0.9f), firstPageLayout.RectTransform), isHorizontal: true)
            {
                Stretch = true,
                RelativeSpacing = 0.02f
            };

            var leftColumn = new GUILayoutGroup(new RectTransform(Vector2.One, columnContainer.RectTransform))
            {
                Stretch = true,
                RelativeSpacing = 0.015f
            };

            var rightColumn = new GUILayoutGroup(new RectTransform(new Vector2(1.5f, 1.0f), columnContainer.RectTransform))
            {
                Stretch = true,
                RelativeSpacing = 0.015f
            };

            columnContainer.Recalculate();

            // New game left side
            new GUITextBlock(new RectTransform(new Vector2(1.0f, 0.02f), leftColumn.RectTransform) { MinSize = new Point(0, 20) }, TextManager.Get("SaveName"), font: GUIStyle.SubHeadingFont);
            saveNameBox = new GUITextBox(new RectTransform(new Vector2(1.0f, 0.05f), leftColumn.RectTransform) { MinSize = new Point(0, 20) }, string.Empty)
            {
                textFilterFunction = (string str) => { return ToolBox.RemoveInvalidFileNameChars(str); }
            };

            new GUITextBlock(new RectTransform(new Vector2(1.0f, 0.02f), leftColumn.RectTransform) { MinSize = new Point(0, 20) }, TextManager.Get("MapSeed"), font: GUIStyle.SubHeadingFont);
            seedBox = new GUITextBox(new RectTransform(new Vector2(1.0f, 0.05f), leftColumn.RectTransform) { MinSize = new Point(0, 20) }, ToolBox.RandomSeed(8));

            new GUITextBlock(new RectTransform(new Vector2(1.0f, 0.02f), leftColumn.RectTransform) { MinSize = new Point(0, 20) }, TextManager.Get("SelectedSub"), font: GUIStyle.SubHeadingFont);

            var moddedDropdown = new GUIDropDown(new RectTransform(new Vector2(1f, 0.02f), leftColumn.RectTransform), "", 3);
            moddedDropdown.AddItem(TextManager.Get("clientpermission.all"), CategoryFilter.All);
            moddedDropdown.AddItem(TextManager.Get("servertag.modded.false"), CategoryFilter.Vanilla);
            moddedDropdown.AddItem(TextManager.Get("customrank"), CategoryFilter.Custom);
            moddedDropdown.Select(0);

            var filterContainer = new GUILayoutGroup(new RectTransform(new Vector2(1.0f, 0.05f), leftColumn.RectTransform), isHorizontal: true)
            {
                Stretch = true
            };

            subList = new GUIListBox(new RectTransform(new Vector2(1.0f, 0.65f), leftColumn.RectTransform))
            {
                PlaySoundOnSelect = true,
                ScrollBarVisible = true
            };

            var searchTitle = new GUITextBlock(new RectTransform(new Vector2(0.001f, 1.0f), filterContainer.RectTransform), TextManager.Get("serverlog.filter"), textAlignment: Alignment.CenterLeft, font: GUIStyle.Font);
            var searchBox = new GUITextBox(new RectTransform(new Vector2(1.0f, 1.0f), filterContainer.RectTransform, Anchor.CenterRight), font: GUIStyle.Font, createClearButton: true);
            filterContainer.RectTransform.MinSize = searchBox.RectTransform.MinSize;
            searchBox.OnSelected += (sender, userdata) => { searchTitle.Visible = false; };
            searchBox.OnDeselected += (sender, userdata) => { searchTitle.Visible = true; };
            searchBox.OnTextChanged += (textBox, text) => { FilterSubs(subList, text); return true; };

            moddedDropdown.OnSelected = (component, data) =>
            {
                searchBox.Text = string.Empty;
                subFilter = (CategoryFilter)data;
                UpdateSubList(SubmarineInfo.SavedSubmarines);
                return true;
            };

            subList.OnSelected = OnSubSelected;

            // New game right side
            subPreviewContainer = new GUILayoutGroup(new RectTransform(new Vector2(1.0f, 1.0f), rightColumn.RectTransform))
            {
                Stretch = true
            };

            var firstPageButtonContainer = new GUILayoutGroup(new RectTransform(new Vector2(1f, 0.08f),
                firstPageLayout.RectTransform), childAnchor: Anchor.BottomLeft, isHorizontal: true)
            {
                RelativeSpacing = 0.025f
            };

            InitialMoneyText = new GUITextBlock(new RectTransform(new Vector2(0.3f, 1f), firstPageButtonContainer.RectTransform), "", font: GUIStyle.Font, textColor: GUIStyle.Green, textAlignment: Alignment.CenterLeft)
            {
                TextGetter = () =>
                {
                    int initialMoney = CampaignSettings.CurrentSettings.InitialMoney;
                    if (subList.SelectedData is SubmarineInfo subInfo)
                    {
                        initialMoney -= subInfo.Price;
                    }
                    initialMoney = Math.Max(initialMoney, 0);
                    return TextManager.GetWithVariable("campaignstartingmoney", "[money]", string.Format(CultureInfo.InvariantCulture, "{0:N0}", initialMoney));
                }
            };

            CampaignCustomizeButton = new GUIButton(new RectTransform(new Vector2(0.25f, 1f), firstPageButtonContainer.RectTransform, Anchor.CenterLeft), TextManager.Get("SettingsButton"))
            {
                OnClicked = (tb, userdata) =>
                {
                    CreateCustomizeWindow(CampaignSettings.CurrentSettings, settings =>
                    {
                        CampaignSettings prevSettings = CampaignSettings.CurrentSettings;
                        CampaignSettings.CurrentSettings = settings;
                        if (prevSettings.InitialMoney != settings.InitialMoney)
                        {
                            object selectedData = subList.SelectedData;
                            UpdateSubList(SubmarineInfo.SavedSubmarines);
                            if (selectedData is SubmarineInfo selectedSub && selectedSub.Price <= CampaignSettings.CurrentSettings.InitialMoney)
                            {
                                subList.Select(selectedData);
                            }
                        }
                    });
                    return true;
                }
            };

            nextButton = new GUIButton(new RectTransform(new Vector2(0.4f, 1f), firstPageButtonContainer.RectTransform, Anchor.BottomRight), TextManager.Get("Next"))
            {
                OnClicked = (GUIButton btn, object userData) =>
                {
                    SetPage(1);
                    return false;
                }
            };

            columnContainer.Recalculate();
            leftColumn.Recalculate();
            rightColumn.Recalculate();
        }
        
        private void CreateSecondPage(GUILayoutGroup secondPageLayout)
        {
            secondPageLayout.RelativeSpacing = 0.01f;
            
            new GUITextBlock(new RectTransform(new Vector2(1.0f, 0.04f), secondPageLayout.RectTransform),
                TextManager.Get("Crew"), font: GUIStyle.SubHeadingFont, textAlignment: Alignment.TopLeft);
            
            characterInfoColumns = new GUIListBox(new RectTransform(new Vector2(1.0f, 0.86f), secondPageLayout.RectTransform), isHorizontal: true);

            var secondPageButtonContainer = new GUILayoutGroup(new RectTransform(new Vector2(1f, 0.08f),
                secondPageLayout.RectTransform), childAnchor: Anchor.BottomLeft, isHorizontal: true)
            {
                RelativeSpacing = 0.2f
            };

            var backButton = new GUIButton(new RectTransform(new Vector2(0.4f, 1f), secondPageButtonContainer.RectTransform, Anchor.BottomRight), TextManager.Get("Back"))
            {
                OnClicked = (GUIButton btn, object userData) =>
                {
                    SetPage(0);
                    return false;
                }
            };
            
            StartButton = new GUIButton(new RectTransform(new Vector2(0.4f, 1f), secondPageButtonContainer.RectTransform, Anchor.BottomRight), TextManager.Get("StartCampaignButton"))
            {
                OnClicked = FinishSetup
            };
        }
        
        public void RandomizeCrew()
        {
            var characterInfos = new List<(CharacterInfo Info, JobPrefab Job)>();
            foreach (JobPrefab jobPrefab in JobPrefab.Prefabs)
            {
                for (int i = 0; i < jobPrefab.InitialCount; i++)
                {
                    var variant = Rand.Range(0, jobPrefab.Variants);
                    characterInfos.Add((new CharacterInfo(CharacterPrefab.HumanSpeciesName, jobOrJobPrefab: jobPrefab, variant: variant), jobPrefab));
                }
            }
            if (characterInfos.Count == 0)
            {
                DebugConsole.ThrowError($"No starting crew found! If you're using mods, it may be that the mods have overridden the vanilla jobs without specifying which types of characters the starting crew should consist of. If you're the developer of the mod, ensure that you've set the {nameof(JobPrefab.InitialCount)} properties for the custom jobs.");
                DebugConsole.AddWarning("Choosing the first available jobs as the starting crew...");
                foreach (JobPrefab jobPrefab in JobPrefab.Prefabs)
                {
                    var variant = Rand.Range(0, jobPrefab.Variants);
                    characterInfos.Add((new CharacterInfo(CharacterPrefab.HumanSpeciesName, jobOrJobPrefab: jobPrefab, variant: variant), jobPrefab));
                    if (characterInfos.Count >= 3) { break; }
                }
            }
            characterInfos.Sort((a, b) => Math.Sign(a.Job.CampaignSetupUIOrder - b.Job.CampaignSetupUIOrder));

            characterInfoColumns.ClearChildren();
            CharacterMenus?.ForEach(m => m.Dispose());
            CharacterMenus = new CharacterInfo.AppearanceCustomizationMenu[characterInfos.Count];
            
            for (int i = 0; i < characterInfos.Count; i++)
            {
                var subLayout = new GUILayoutGroup(new RectTransform(new Vector2(Math.Max(1.0f / characterInfos.Count, 0.33f), 1.0f),
                    characterInfoColumns.Content.RectTransform));

                var (characterInfo, job) = characterInfos[i];

                characterInfo.CreateIcon(new RectTransform(new Vector2(1.0f, 0.275f), subLayout.RectTransform));

                var jobTextContainer =
                    new GUIFrame(new RectTransform(new Vector2(1.0f, 0.05f), subLayout.RectTransform), style: null);
                var jobText = new GUITextBlock(new RectTransform(Vector2.One, jobTextContainer.RectTransform), job.Name, job.UIColor);

                var characterName = new GUITextBox(new RectTransform(new Vector2(1.0f, 0.1f), subLayout.RectTransform))
                {
                    Text = characterInfo.Name,
                    UserData = "random"
                };
                characterName.OnDeselected += (sender, key) =>
                {
                    if (string.IsNullOrWhiteSpace(sender.Text))
                    {
                        characterInfo.Name = characterInfo.GetRandomName(Rand.RandSync.Unsynced);
                        sender.Text = characterInfo.Name;
                        sender.UserData = "random";
                    }
                    else
                    {
                        characterInfo.Name = sender.Text;
                        sender.UserData = "user";
                    }
                };
                characterName.OnEnterPressed += (sender, text) =>
                {
                    sender.Deselect();
                    return false;
                };
                
                var customizationFrame =
                    new GUIFrame(new RectTransform(new Vector2(1.0f, 0.6f), subLayout.RectTransform), style: null);
                CharacterMenus[i] =
                    new CharacterInfo.AppearanceCustomizationMenu(characterInfo, customizationFrame, hasIcon: false)
                    {
                        OnHeadSwitch = menu =>
                        {
                            if (characterName.UserData is string ud && ud == "random")
                            {
                                characterInfo.Name = characterInfo.GetRandomName(Rand.RandSync.Unsynced);
                                characterName.Text = characterInfo.Name;
                                characterName.UserData = "random";
                            }

                            StealRandomizeButton(menu, jobTextContainer);
                        }
                    };
                StealRandomizeButton(CharacterMenus[i], jobTextContainer);
            }
        }

        private void CreateCustomizeWindow(CampaignSettings prevSettings, Action<CampaignSettings> onClosed = null)
        {
            CampaignCustomizeSettings = new GUIMessageBox("", "", new[] { TextManager.Get("OK") }, new Vector2(0.25f, 0.5f), minSize: new Point(450, 350));

            GUILayoutGroup campaignSettingContent = new GUILayoutGroup(new RectTransform(new Vector2(0.95f, 0.8f), CampaignCustomizeSettings.Content.RectTransform, Anchor.TopCenter));


            CampaignSettingElements elements = CreateCampaignSettingList(campaignSettingContent, prevSettings, true);
            CampaignCustomizeSettings.Buttons[0].OnClicked += (button, o) =>
            {

                onClosed?.Invoke(elements.CreateSettings());
                GameSettings.SaveCurrentConfig();
                return CampaignCustomizeSettings.Close(button, o);
            };
        }

        private static void StealRandomizeButton(CharacterInfo.AppearanceCustomizationMenu menu, GUIComponent parent)
        {
            //This is just stupid
            var randomizeButton = menu.RandomizeButton;
            var oldButton = parent.GetChild<GUIButton>();
            parent.RemoveChild(oldButton);
            randomizeButton.RectTransform.Parent = parent.RectTransform;
            randomizeButton.RectTransform.RelativeSize = Vector2.One * 1.3f;
        }

        private bool FinishSetup(GUIButton btn, object userdata)
        {
            if (string.IsNullOrWhiteSpace(saveNameBox.Text))
            {
                saveNameBox.Flash(GUIStyle.Red);
                return false;
            }

            SubmarineInfo selectedSub = null;

            if (subList.SelectedData is not SubmarineInfo) { return false; }
            selectedSub = subList.SelectedData as SubmarineInfo;
            
            if (selectedSub.Class == SubmarineClass.Undefined)
            {
                new GUIMessageBox(TextManager.Get("error"), TextManager.Get("undefinedsubmarineselected"));
                return false;
            }

            if (string.IsNullOrEmpty(selectedSub.MD5Hash.StringRepresentation))
            {
                ((GUITextBlock)subList.SelectedComponent).TextColor = Color.DarkRed * 0.8f;
                subList.SelectedComponent.CanBeFocused = false;
                subList.Deselect();
                return false;
            }

            string savePath = SaveUtil.CreateSavePath(SaveUtil.SaveType.Singleplayer, saveNameBox.Text);
            bool hasRequiredContentPackages = selectedSub.RequiredContentPackagesInstalled;

            CampaignSettings settings = CampaignSettings.CurrentSettings;

            if (selectedSub.HasTag(SubmarineTag.Shuttle) || !hasRequiredContentPackages)
            {
                if (!hasRequiredContentPackages)
                {
                    var msgBox = new GUIMessageBox(TextManager.Get("ContentPackageMismatch"),
                        TextManager.GetWithVariable("ContentPackageMismatchWarning", "[requiredcontentpackages]", string.Join(", ", selectedSub.RequiredContentPackages)),
                        new LocalizedString[] { TextManager.Get("Yes"), TextManager.Get("No") });
                    
                    msgBox.Buttons[0].OnClicked = msgBox.Close;
                    msgBox.Buttons[0].OnClicked += (button, obj) =>
                    {
                        if (GUIMessageBox.MessageBoxes.Count == 0)
                        {
                            StartNewGame?.Invoke(selectedSub, savePath, seedBox.Text, settings);
                        }
                        return true;
                    };

                    msgBox.Buttons[1].OnClicked = msgBox.Close;
                }

                if (selectedSub.HasTag(SubmarineTag.Shuttle))
                {
                    var msgBox = new GUIMessageBox(TextManager.Get("ShuttleSelected"),
                        TextManager.Get("ShuttleWarning"),
                        new LocalizedString[] { TextManager.Get("Yes"), TextManager.Get("No") });

                    msgBox.Buttons[0].OnClicked = (button, obj) => 
                    {
                        StartNewGame?.Invoke(selectedSub, savePath, seedBox.Text, settings);
                        return true;
                    };
                    msgBox.Buttons[0].OnClicked += msgBox.Close;

                    msgBox.Buttons[1].OnClicked = msgBox.Close;
                    return false;
                }
            }
            else
            {
                StartNewGame?.Invoke(selectedSub, savePath, seedBox.Text, settings);
            }

            return true;
        }

        public void RandomizeSeed()
        {
            seedBox.Text = ToolBox.RandomSeed(8);
        }

        private void FilterSubs(GUIListBox subList, string filter)
        {
            foreach (GUIComponent child in subList.Content.Children)
            {
                if (child.UserData is not SubmarineInfo sub) { return; }
                child.Visible = string.IsNullOrEmpty(filter) || sub.DisplayName.Contains(filter.ToLower(), StringComparison.OrdinalIgnoreCase);
            }
        }

        private bool OnSubSelected(GUIComponent component, object obj)
        {
            if (subPreviewContainer == null) { return false; }
            (subPreviewContainer.Parent as GUILayoutGroup)?.Recalculate();
            subPreviewContainer.ClearChildren();

            if (obj is not SubmarineInfo sub) { return true; }
#if !DEBUG
            if (sub.Price > CampaignSettings.CurrentSettings.InitialMoney && !GameMain.DebugDraw)
            {
                SetPage(0);
                nextButton.Enabled = false;
                return false; 
            }
#endif
            nextButton.Enabled = true;
            sub.CreatePreviewWindow(subPreviewContainer);
            return true;
        }
        
        public void CreateDefaultSaveName()
        {
            string savePath = SaveUtil.CreateSavePath(SaveUtil.SaveType.Singleplayer);
            saveNameBox.Text = Path.GetFileNameWithoutExtension(savePath);
        }

        public void UpdateSubList(IEnumerable<SubmarineInfo> submarines)
        {
            List<SubmarineInfo> subsToShow;
            if (subFilter != CategoryFilter.All)
            {
                subsToShow = submarines.Where(s => s.IsCampaignCompatibleIgnoreClass && s.IsVanillaSubmarine() == (subFilter == CategoryFilter.Vanilla)).ToList();
            }
            else
            {
                string downloadFolder = Path.GetFullPath(SaveUtil.SubmarineDownloadFolder);
                subsToShow = submarines.Where(s => s.IsCampaignCompatibleIgnoreClass && Path.GetDirectoryName(Path.GetFullPath(s.FilePath)) != downloadFolder).ToList();
            }

            subsToShow.Sort((s1, s2) =>
            {
                int p1 = s1.Price;
                if (!s1.IsCampaignCompatible) { p1 += 100000; }
                int p2 = s2.Price;
                if (!s2.IsCampaignCompatible) { p2 += 100000; }
                return p1.CompareTo(p2) * 100 + s1.Name.CompareTo(s2.Name);
            });

            subList.ClearChildren();

            foreach (SubmarineInfo sub in subsToShow)
            {
                var textBlock = new GUITextBlock(
                    new RectTransform(new Vector2(1, 0.15f), subList.Content.RectTransform) { MinSize = new Point(0, 30) },
                    ToolBox.LimitString(sub.DisplayName.Value, GUIStyle.Font, subList.Rect.Width - 65), style: "ListBoxElement")
                    {
                        ToolTip = sub.Description,
                        UserData = sub
                    };

                if (!sub.RequiredContentPackagesInstalled)
                {
                    textBlock.TextColor = Color.Lerp(textBlock.TextColor, Color.DarkRed, .5f);
                    textBlock.ToolTip = TextManager.Get("ContentPackageMismatch") + "\n\n" + textBlock.ToolTip.SanitizedString;
                }

                var infoContainer = new GUILayoutGroup(new RectTransform(new Vector2(0.5f, 1.0f), textBlock.RectTransform, Anchor.CenterRight), isHorizontal: false);
                new GUITextBlock(new RectTransform(new Vector2(1.0f, 0.5f), infoContainer.RectTransform),
                    TextManager.GetWithVariable("currencyformat", "[credits]", string.Format(CultureInfo.InvariantCulture, "{0:N0}", sub.Price)), textAlignment: Alignment.BottomRight, font: GUIStyle.SmallFont)
                {
                    TextColor = sub.Price > CampaignSettings.CurrentSettings.InitialMoney ? GUIStyle.Red : textBlock.TextColor * 0.8f,
                    ToolTip = textBlock.ToolTip
                };
                new GUITextBlock(new RectTransform(new Vector2(1.0f, 0.5f), infoContainer.RectTransform),
                    sub.Class.Name, textAlignment: Alignment.TopRight, font: GUIStyle.SmallFont)
                {
                    TextColor = textBlock.TextColor * 0.8f,
                    ToolTip = textBlock.ToolTip
                };
#if !DEBUG
                if (!GameMain.DebugDraw)
                {
                    if (sub.Price > CampaignSettings.CurrentSettings.InitialMoney || !sub.IsCampaignCompatible)
                    {
                        textBlock.CanBeFocused = false;
                        textBlock.TextColor *= 0.5f;
                    }
                }
#endif
            }
            if (SubmarineInfo.SavedSubmarines.Any())
            {
                var validSubs = subsToShow.Where(s => s.IsCampaignCompatible && s.Price <= CampaignSettings.CurrentSettings.InitialMoney).ToList();
                if (validSubs.Count > 0)
                {
                    subList.Select(validSubs[Rand.Int(validSubs.Count)]);
                }
            }
        }

        public override void CreateLoadMenu(IEnumerable<CampaignMode.SaveInfo> saveFiles = null)
        {
            prevSaveFiles?.Clear();
            prevSaveFiles = null;
            loadGameContainer.ClearChildren();

            if (saveFiles == null)
            {
                //we don't need to log errors at this point,
                //if any file fails to load the error will get logged when we try to extract the root from the game session doc later in the method
                saveFiles = SaveUtil.GetSaveFiles(SaveUtil.SaveType.Singleplayer, logLoadErrors: false);
            }

            var leftColumn = new GUILayoutGroup(new RectTransform(new Vector2(0.5f, 1.0f), loadGameContainer.RectTransform), childAnchor: Anchor.TopCenter)
            {
                Stretch = true,
                RelativeSpacing = 0.03f
            };

            saveList = new GUIListBox(new RectTransform(Vector2.One, leftColumn.RectTransform))
            {
                PlaySoundOnSelect = true,
                OnSelected = SelectSaveFile
            };

            new GUIButton(new RectTransform(new Vector2(0.6f, 0.08f), leftColumn.RectTransform), TextManager.Get("showinfolder"))
            {
                OnClicked = (btn, userdata) =>
                {
                    var saveFolder = SaveUtil.GetSaveFolder(SaveUtil.SaveType.Singleplayer);
                    try
                    {
                        ToolBox.OpenFileWithShell(saveFolder);
                    }
                    catch (Exception e)
                    {
                        new GUIMessageBox(
                            TextManager.Get("error"),
                            TextManager.GetWithVariables("showinfoldererror", ("[folder]", saveFolder), ("[errormessage]", e.Message)));
                    }
                    return true;
                }
            };

            foreach (var saveInfo in saveFiles)
            {
                var saveFrame = CreateSaveElement(saveInfo);
                if (saveFrame == null) { continue; }

                XElement docRoot = SaveUtil.ExtractGameSessionRootElementFromSaveFile(saveInfo.FilePath);

                if (docRoot == null)
                {
                    DebugConsole.ThrowError("Error loading save file \"" + saveInfo.FilePath + "\". The file may be corrupted.");
                    saveFrame.GetChild<GUITextBlock>().TextColor = GUIStyle.Red;
                    continue;
                }
                if (docRoot.GetChildElement("multiplayercampaign") != null)
                {
                    //multiplayer campaign save in the wrong folder -> don't show the save
                    saveList.Content.RemoveChild(saveFrame);
                    continue;
                }
                if (!SaveUtil.IsSaveFileCompatible(docRoot))
                {
                    saveFrame.GetChild<GUITextBlock>().TextColor = GUIStyle.Red;
                    saveFrame.ToolTip = TextManager.Get("campaignmode.incompatiblesave");
                }
            }

            SortSaveList();

            loadGameButton = new GUIButton(new RectTransform(new Vector2(0.45f, 0.12f), loadGameContainer.RectTransform, Anchor.BottomRight), TextManager.Get("LoadButton"))
            {
                OnClicked = (btn, obj) =>
                {
                    if (saveList.SelectedData is not CampaignMode.SaveInfo saveInfo) { return false; }
                    if (string.IsNullOrWhiteSpace(saveInfo.FilePath)) { return false; }
                    LoadGame?.Invoke(saveInfo.FilePath, backupIndex: Option.None);
                    return true;
                },
                Enabled = false
            };
        }

        private bool SelectSaveFile(GUIComponent component, object obj)
        {
            if (obj is not CampaignMode.SaveInfo saveInfo) { return true; }

            string fileName = saveInfo.FilePath;

            XElement docRoot = SaveUtil.ExtractGameSessionRootElementFromSaveFile(fileName);
            if (docRoot == null)
            {
                DebugConsole.ThrowError("Error loading save file \"" + fileName + "\". The file may be corrupted.");
                return false;
            }

            loadGameButton.Enabled = SaveUtil.IsSaveFileCompatible(docRoot);

            RemoveSaveFrame();

            string subName = saveInfo.SubmarineName;
            LocalizedString saveTime = saveInfo.SaveTime
                .Select(t => (LocalizedString)t.ToLocalUserString())
                .Fallback(TextManager.Get("Unknown"));

            string mapseed = docRoot.GetAttributeString("mapseed", "unknown");

            Identifier locationNameIdentifier = docRoot.GetAttributeIdentifier("currentlocation", Identifier.Empty);
            int locationNameFormatIndex = docRoot.GetAttributeInt("currentlocationnameformatindex", -1);
            Identifier locationType = docRoot.GetAttributeIdentifier("locationtype", Identifier.Empty);
            LevelData.LevelType levelType = docRoot.GetAttributeEnum("nextleveltype", LevelData.LevelType.LocationConnection);

            LocalizedString locationName = locationType.IsEmpty || locationNameIdentifier.IsEmpty ?
                LocalizedString.EmptyString :
                Location.GetName(locationType, locationNameFormatIndex, locationNameIdentifier);

            var saveFileFrame = new GUIFrame(
                new RectTransform(new Vector2(0.45f, 0.6f), loadGameContainer.RectTransform, Anchor.TopRight)
                {
                    RelativeOffset = new Vector2(0.0f, 0.1f)
                }, style: "InnerFrame")
            {
                UserData = "savefileframe"
            };

            var titleText = new GUITextBlock(
                new RectTransform(new Vector2(0.9f, 0.2f), saveFileFrame.RectTransform, Anchor.TopCenter)
                {
                    RelativeOffset = new Vector2(0, 0.05f)
                },
                Path.GetFileNameWithoutExtension(fileName), font: GUIStyle.LargeFont, textAlignment: Alignment.Center);
            titleText.Text = ToolBox.LimitString(titleText.Text, titleText.Font, titleText.Rect.Width);

            var layoutGroup = new GUILayoutGroup(
                new RectTransform(new Vector2(0.8f, 0.5f), saveFileFrame.RectTransform, Anchor.Center)
                {
                    RelativeOffset = new Vector2(0, 0.1f)
                });

            if (!locationName.IsNullOrEmpty())
            {
                new GUITextBlock(new RectTransform(new Vector2(1, 0), layoutGroup.RectTransform),
                    locationName, font: GUIStyle.SmallFont);
                new GUITextBlock(new RectTransform(new Vector2(1, 0), layoutGroup.RectTransform),
                    TextManager.Get($"savestate.{levelType}"), font: GUIStyle.SmallFont);
                //spacing
                new GUIFrame(new RectTransform(new Vector2(0.0f, 0.05f), layoutGroup.RectTransform), style: null);
            }
            new GUITextBlock(new RectTransform(new Vector2(1, 0), layoutGroup.RectTransform),
                $"{TextManager.Get("Submarine")} : {subName}", font: GUIStyle.SmallFont);
            new GUITextBlock(new RectTransform(new Vector2(1, 0), layoutGroup.RectTransform),
                $"{TextManager.Get("LastSaved")} : {saveTime}", font: GUIStyle.SmallFont);
            new GUITextBlock(new RectTransform(new Vector2(1, 0), layoutGroup.RectTransform),
                $"{TextManager.Get("MapSeed")} : {mapseed}", font: GUIStyle.SmallFont);

            GUILayoutGroup buttonContainer = new GUILayoutGroup(new RectTransform(new Vector2(0.85f, 0.15f), saveFileFrame.RectTransform, Anchor.BottomCenter)
            {
                RelativeOffset = new Vector2(0, 0.1f)
            }, isHorizontal: true)
            {
                RelativeSpacing = 0.05f,
                Stretch = true
            };

            new GUIButton(new RectTransform(new Vector2(0.5f, 1f), buttonContainer.RectTransform, Anchor.CenterLeft), TextManager.Get("Delete"), style: "GUIButtonSmall")
            {
                UserData = saveInfo,
                OnClicked = DeleteSave
            };

            new GUIButton(new RectTransform(new Vector2(0.5f, 1f), buttonContainer.RectTransform, Anchor.CenterLeft), TextManager.Get("rollbackbutton"), style: "GUIButtonSmall")
            {
                UserData = saveInfo,
                ToolTip = TextManager.Get("backuptooltip"),
                OnClicked = ViewBackupMenu
            };

            return true;
        }

        private bool ViewBackupMenu(GUIButton btn, object obj)
        {
            if (obj is not CampaignMode.SaveInfo saveInfo) { return false; }

            var indexData = SaveUtil.GetIndexData(saveInfo.FilePath);
            CreateBackupMenu(indexData, index =>
            {
                LoadGame(saveInfo.FilePath, Option.Some(index.Index));
            });
            return true;
        }

        private void RemoveSaveFrame()
        {
            GUIComponent prevFrame = null;
            foreach (GUIComponent child in loadGameContainer.Children)
            {
                if (child.UserData as string != "savefileframe") continue;

                prevFrame = child;
                break;
            }
            loadGameContainer.RemoveChild(prevFrame);
        }
    }
}

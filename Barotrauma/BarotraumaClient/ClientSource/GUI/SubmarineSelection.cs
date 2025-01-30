using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using System.Linq;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using PlayerBalanceElement = Barotrauma.CampaignUI.PlayerBalanceElement;

namespace Barotrauma
{
    class SubmarineSelection
    {
        private const int submarinesPerPage = 4;
        private int currentPage = 1;
        private int pageCount;
        private readonly bool transferService, purchaseService;
        private bool initialized;

        public GUIFrame GuiFrame;
        private GUIFrame pageIndicatorHolder;
        private GUICustomComponent selectedSubmarineIndicator;
        private GUILayoutGroup submarineHorizontalGroup, submarineControlsGroup;
        private GUIButton browseLeftButton, browseRightButton, confirmButton, confirmButtonAlt;
        private GUIListBox specsFrame;
        private GUIImage[] pageIndicators;
        private GUITextBlock descriptionTextBlock;
        private int selectionIndicatorThickness;
        private GUIImage listBackground;
        private GUITickBox transferItemsTickBox;
        private GUITextBlock itemTransferInfoBlock;

        private readonly List<SubmarineInfo> subsToShow;
        private readonly SubmarineDisplayContent[] submarineDisplays = new SubmarineDisplayContent[submarinesPerPage];
        private SubmarineInfo selectedSubmarine = null;
        private LocalizedString purchaseAndSwitchText, purchaseOnlyText, selectedSubText, switchText, missingPreviewText, currencyName;
        private readonly RectTransform parent;
        private readonly Action closeAction;
        private Sprite pageIndicator;

        private readonly LocalizedString[] messageBoxOptions;

        public static bool ContentRefreshRequired = false;

        private static readonly Color indicatorColor = new Color(112, 149, 129);
        private Point createdForResolution;

        private PlayerBalanceElement? playerBalanceElement;

        private struct SubmarineDisplayContent
        {
            public GUIFrame background;
            public GUIImage submarineImage;
            public SubmarineInfo displayedSubmarine;
            public GUITextBlock submarineName;
            public GUITextBlock submarineClass;
            public GUITextBlock submarineTier;
            public GUITextBlock submarineFee;
            public GUIButton selectSubmarineButton;
            public GUITextBlock middleTextBlock;
            public GUIButton previewButton;
        }

        private bool TransferItemsOnSwitch
        {
            get
            {
                return transferItemsOnSwitch;
            }
            set
            {
                transferItemsOnSwitch = value;
                if (transferItemsTickBox != null)
                {
                    transferItemsTickBox.Selected = value;
                }
            }
        }
        private bool transferItemsOnSwitch = true;

        public SubmarineSelection(bool transfer, Action closeAction, RectTransform parent)
        {
            if (GameMain.GameSession.Campaign == null) { return; }

            transferService = transfer;
            purchaseService = !transfer;
            this.parent = parent;
            this.closeAction = closeAction;

            subsToShow = new List<SubmarineInfo>();

            if (GameMain.Client == null)
            {
                messageBoxOptions = new LocalizedString[2] { TextManager.Get("Yes"), TextManager.Get("Cancel") };
            }
            else
            {
                messageBoxOptions = new LocalizedString[2] { TextManager.Get("Yes") + " " + TextManager.Get("initiatevoting"), TextManager.Get("Cancel") };
            }

            if (Submarine.MainSub?.Info == null) { return; }
            Initialize();
        }

        private void Initialize()
        {
            initialized = true;
            selectedSubText = TextManager.Get("selectedsub");
            switchText = TextManager.Get("switchtosubmarinebutton");
            purchaseAndSwitchText = TextManager.Get("purchaseandswitch");
            purchaseOnlyText = TextManager.Get("purchase");

            currencyName = TextManager.Get("credit").Value.ToLowerInvariant();

            UpdateSubmarines();
            missingPreviewText = TextManager.Get("SubPreviewImageNotFound");
            CreateGUI();
        }

        private void CreateGUI()
        {
            createdForResolution = new Point(GameMain.GraphicsWidth, GameMain.GraphicsHeight);

            GUILayoutGroup content;
            GuiFrame = new GUIFrame(new RectTransform(new Vector2(0.75f, 0.7f), parent, Anchor.TopCenter, Pivot.TopCenter) { RelativeOffset = new Vector2(0.0f, 0.02f) });
            selectionIndicatorThickness = HUDLayoutSettings.Padding / 2;

            GUIFrame background = new GUIFrame(new RectTransform(GuiFrame.Rect.Size - GUIStyle.ItemFrameMargin, GuiFrame.RectTransform, Anchor.Center), color: Color.Black * 0.9f)
            {
                CanBeFocused = false
            };

            content = new GUILayoutGroup(new RectTransform(new Point(background.Rect.Width - HUDLayoutSettings.Padding * 4, background.Rect.Height - HUDLayoutSettings.Padding * 4), background.RectTransform, Anchor.Center)) { AbsoluteSpacing = (int)(HUDLayoutSettings.Padding * 1.5f) };
            GUITextBlock header = new GUITextBlock(new RectTransform(new Vector2(1f, 0.0f), content.RectTransform), transferService ? TextManager.Get("switchsubmarineheader") : TextManager.GetWithVariable("outpostshipyard", "[location]", GameMain.GameSession.Map.CurrentLocation.DisplayName), font: GUIStyle.LargeFont);
            header.CalculateHeightFromText(0, true);
            playerBalanceElement = CampaignUI.AddBalanceElement(header, new Vector2(1.0f, 1.5f));

            new GUIFrame(new RectTransform(new Vector2(1.0f, 0.01f), content.RectTransform), style: "HorizontalLine");

            GUILayoutGroup submarineContentGroup = new GUILayoutGroup(new RectTransform(new Vector2(1f, 0.4f), content.RectTransform)) { AbsoluteSpacing = HUDLayoutSettings.Padding, Stretch = true };
            submarineHorizontalGroup = new GUILayoutGroup(new RectTransform(new Vector2(1f, 0.9f), submarineContentGroup.RectTransform)) { IsHorizontal = true, AbsoluteSpacing = HUDLayoutSettings.Padding, Stretch = true };

            submarineControlsGroup = new GUILayoutGroup(new RectTransform(new Vector2(1f, 0.1f), submarineContentGroup.RectTransform), true, Anchor.TopCenter);

            GUILayoutGroup infoFrame = new GUILayoutGroup(new RectTransform(new Vector2(1f, 0.4f), content.RectTransform)) { IsHorizontal = true, Stretch = true, AbsoluteSpacing = HUDLayoutSettings.Padding };
            new GUIFrame(new RectTransform(Vector2.One, infoFrame.RectTransform), style: null, new Color(8, 13, 19)) { IgnoreLayoutGroups = true };
            listBackground = new GUIImage(new RectTransform(new Vector2(0.59f, 1f), infoFrame.RectTransform, Anchor.CenterRight), style: null, true)
            {
                IgnoreLayoutGroups = true                
            };
            new GUIListBox(new RectTransform(Vector2.One, infoFrame.RectTransform)) { IgnoreLayoutGroups = true, CanBeFocused = false };
            specsFrame = new GUIListBox(new RectTransform(new Vector2(0.39f, 1f), infoFrame.RectTransform), style: null) 
            {
                CurrentSelectMode = GUIListBox.SelectMode.None,
                Spacing = GUI.IntScale(5), 
                Padding = new Vector4(HUDLayoutSettings.Padding / 2f, HUDLayoutSettings.Padding, 0, 0) 
            };
            new GUIFrame(new RectTransform(new Vector2(0.02f, 0.8f), infoFrame.RectTransform) { RelativeOffset = new Vector2(0.0f, 0.1f) }, style: "VerticalLine");
            GUIListBox descriptionFrame = new GUIListBox(new RectTransform(new Vector2(0.59f, 1f), infoFrame.RectTransform), style: null) { Padding = new Vector4(HUDLayoutSettings.Padding / 2f, HUDLayoutSettings.Padding * 1.5f, HUDLayoutSettings.Padding * 1.5f, HUDLayoutSettings.Padding / 2f) };
            descriptionTextBlock = new GUITextBlock(new RectTransform(new Vector2(1, 0), descriptionFrame.Content.RectTransform), string.Empty, font: GUIStyle.Font, wrap: true) { CanBeFocused = false };

            GUILayoutGroup bottomContainer = new GUILayoutGroup(new RectTransform(new Vector2(1.0f, 0.075f), content.RectTransform, Anchor.CenterRight), childAnchor: Anchor.CenterRight) { IsHorizontal = true, AbsoluteSpacing = HUDLayoutSettings.Padding };
            float transferInfoFrameWidth = 1.0f;

            if (closeAction != null)
            {
                GUIButton closeButton = new GUIButton(new RectTransform(new Vector2(0.2f, 1f), bottomContainer.RectTransform), TextManager.Get("Close"), style: "GUIButtonFreeScale")
                {
                    OnClicked = (button, userData) =>
                    {
                        closeAction();
                        return true;
                    }
                };
                transferInfoFrameWidth -= closeButton.RectTransform.RelativeSize.X;
            }

            if (purchaseService)
            {
                confirmButtonAlt = new GUIButton(new RectTransform(new Vector2(0.2f, 1f), bottomContainer.RectTransform), purchaseOnlyText, style: "GUIButtonFreeScale");
                transferInfoFrameWidth -= confirmButtonAlt.RectTransform.RelativeSize.X;
            } 
            confirmButton = new GUIButton(new RectTransform(new Vector2(0.2f, 1f), bottomContainer.RectTransform), purchaseService ? purchaseAndSwitchText : switchText, style: "GUIButtonFreeScale");
            SetConfirmButtonState(false);
            transferInfoFrameWidth -= confirmButton.RectTransform.RelativeSize.X;
            GUIFrame transferInfoFrame = new GUIFrame(new RectTransform(new Vector2(transferInfoFrameWidth, 1.0f), bottomContainer.RectTransform), style: null)
            {
                CanBeFocused = false
            };
            transferItemsTickBox = new GUITickBox(new RectTransform(new Vector2(0.2f, 1.0f), transferInfoFrame.RectTransform, Anchor.CenterRight), TextManager.Get("transferitems"), font: GUIStyle.SubHeadingFont)
            {
                Selected = TransferItemsOnSwitch,
                Visible = false,
                OnSelected = (tb) => transferItemsOnSwitch = tb.Selected
            };
            transferItemsTickBox.RectTransform.Resize(new Point(Math.Min((int)transferItemsTickBox.ContentWidth, transferInfoFrame.Rect.Width), transferItemsTickBox.Rect.Height));
            itemTransferInfoBlock = new GUITextBlock(new RectTransform(Vector2.One, transferInfoFrame.RectTransform, Anchor.CenterRight), null, wrap: true)
            {
                TextAlignment = Alignment.CenterRight,
                Visible = false
            };

            pageIndicatorHolder = new GUIFrame(new RectTransform(new Vector2(1f, 1.5f), submarineControlsGroup.RectTransform), style: null);
            pageIndicator = GUIStyle.GetComponentStyle("GUIPageIndicator").GetDefaultSprite();
            UpdatePaging();

            for (int i = 0; i < submarineDisplays.Length; i++)
            {
                SubmarineDisplayContent submarineDisplayElement = new SubmarineDisplayContent
                {
                    background = new GUIFrame(new RectTransform(new Vector2(1f / submarinesPerPage, 1f), submarineHorizontalGroup.RectTransform), style: null, new Color(8, 13, 19))
                };
                submarineDisplayElement.submarineImage = new GUIImage(new RectTransform(new Vector2(0.8f, 1f), submarineDisplayElement.background.RectTransform, Anchor.Center), null, true);
                submarineDisplayElement.middleTextBlock = new GUITextBlock(new RectTransform(new Vector2(0.8f, 1f), submarineDisplayElement.background.RectTransform, Anchor.Center), string.Empty, textAlignment: Alignment.Center);
                submarineDisplayElement.submarineName = new GUITextBlock(new RectTransform(new Vector2(1f, 0.1f), submarineDisplayElement.background.RectTransform, Anchor.TopCenter) { AbsoluteOffset = new Point(0, HUDLayoutSettings.Padding) }, string.Empty, textAlignment: Alignment.Center, font: GUIStyle.SubHeadingFont);
                submarineDisplayElement.submarineFee = new GUITextBlock(new RectTransform(new Vector2(1f, 0.1f), submarineDisplayElement.background.RectTransform, Anchor.BottomCenter) { AbsoluteOffset = new Point(0, HUDLayoutSettings.Padding) }, string.Empty, textAlignment: Alignment.Center, font: GUIStyle.SubHeadingFont);
                submarineDisplayElement.selectSubmarineButton = new GUIButton(new RectTransform(Vector2.One, submarineDisplayElement.background.RectTransform), style: null);
                submarineDisplayElement.previewButton = new GUIButton(new RectTransform(Vector2.One * 0.12f, submarineDisplayElement.background.RectTransform, anchor: Anchor.BottomRight, scaleBasis: ScaleBasis.BothHeight) { AbsoluteOffset = new Point((int)(0.03f * background.Rect.Height)) }, style: "ExpandButton")
                {
                    Color = Color.White,
                    HoverColor = Color.White,
                    PressedColor = Color.White
                };
                submarineDisplayElement.submarineClass = new GUITextBlock(new RectTransform(new Vector2(1f, 0.1f), submarineDisplayElement.background.RectTransform, Anchor.TopCenter) { AbsoluteOffset = new Point(0, HUDLayoutSettings.Padding + (int)GUIStyle.Font.MeasureString(submarineDisplayElement.submarineName.Text).Y) }, string.Empty, textAlignment: Alignment.Left);
                submarineDisplayElement.submarineTier = new GUITextBlock(new RectTransform(new Vector2(0.5f, 0.1f), submarineDisplayElement.background.RectTransform, Anchor.TopRight) { AbsoluteOffset = new Point(0, HUDLayoutSettings.Padding + (int)GUIStyle.Font.MeasureString(submarineDisplayElement.submarineName.Text).Y) }, string.Empty, textAlignment: Alignment.Right);

                submarineDisplays[i] = submarineDisplayElement;
            }

            selectedSubmarineIndicator = new GUICustomComponent(new RectTransform(Point.Zero, submarineHorizontalGroup.RectTransform), onDraw: (sb, component) => DrawSubmarineIndicator(sb, component.Rect)) { IgnoreLayoutGroups = true, CanBeFocused = false };
        }

        private void UpdatePaging()
        {
            if (pageIndicatorHolder == null) return;
            pageIndicatorHolder.ClearChildren();
            if (currentPage > pageCount) currentPage = pageCount;
            if (pageCount < 2) return;

            browseLeftButton = new GUIButton(new RectTransform(new Vector2(1.15f, 1.15f), pageIndicatorHolder.RectTransform, Anchor.CenterLeft, Pivot.CenterRight) { AbsoluteOffset = new Point(-HUDLayoutSettings.Padding * 3, 0) }, string.Empty, style: "GUIButtonToggleLeft")
            {
                IgnoreLayoutGroups = true,
                OnClicked = (button, userData) =>
                {
                    ChangePage(-1);
                    return true;
                }
            };

            Point indicatorSize = new Point(GUI.IntScale(pageIndicator.SourceRect.Width * 1.5f), GUI.IntScale(pageIndicator.SourceRect.Height * 1.5f));
            pageIndicatorHolder.RectTransform.NonScaledSize = new Point(pageCount * indicatorSize.X + HUDLayoutSettings.Padding * (pageCount - 1), pageIndicatorHolder.RectTransform.NonScaledSize.Y);

            int xPos = 0;
            int yPos = pageIndicatorHolder.Rect.Height / 2 - indicatorSize.Y / 2;

            pageIndicators = new GUIImage[pageCount];
            for (int i = 0; i < pageCount; i++)
            {
                pageIndicators[i] = new GUIImage(new RectTransform(indicatorSize, pageIndicatorHolder.RectTransform) { AbsoluteOffset = new Point(xPos, yPos) }, pageIndicator, null, true);
                xPos += indicatorSize.X + HUDLayoutSettings.Padding;
            }

            for (int i = 0; i < pageIndicators.Length; i++)
            {
                pageIndicators[i].Color = i == currentPage - 1 ? Color.White : Color.Gray;
            }

            browseRightButton = new GUIButton(new RectTransform(new Vector2(1.15f, 1.15f), pageIndicatorHolder.RectTransform, Anchor.CenterRight, Pivot.CenterLeft) { AbsoluteOffset = new Point(-HUDLayoutSettings.Padding * 3, 0) }, string.Empty, style: "GUIButtonToggleRight")
            {
                IgnoreLayoutGroups = true,
                OnClicked = (button, userData) =>
                {
                    ChangePage(1);
                    return true;
                }
            };

            browseLeftButton.Enabled = currentPage > 1;
            browseRightButton.Enabled = currentPage < pageCount;
        }

        private void DrawSubmarineIndicator(SpriteBatch spriteBatch, Rectangle area)
        {
            if (area == Rectangle.Empty) return;
            GUI.DrawRectangle(spriteBatch, area, indicatorColor, thickness: selectionIndicatorThickness);
        }

        public void Update()
        {
            if (ContentRefreshRequired)
            {
                RefreshSubmarineDisplay(true);
            }
            else
            {
                playerBalanceElement = CampaignUI.UpdateBalanceElement(playerBalanceElement);
            }

            // Input
            if (PlayerInput.KeyHit(Keys.Left))
            {
                SelectSubmarine(subsToShow.IndexOf(selectedSubmarine), -1);
            }
            else if (PlayerInput.KeyHit(Keys.Right))
            {
                SelectSubmarine(subsToShow.IndexOf(selectedSubmarine), 1);
            }
        }

        public void RefreshSubmarineDisplay(bool updateSubs, bool setTransferOptionToTrue = false)
        {
            if (!initialized)
            {
                Initialize();
            }
            if (GameMain.GraphicsWidth != createdForResolution.X || GameMain.GraphicsHeight != createdForResolution.Y)
            {
                CreateGUI();
            }
            else
            {
                playerBalanceElement = CampaignUI.UpdateBalanceElement(playerBalanceElement);
            }
            if (setTransferOptionToTrue)
            {
                TransferItemsOnSwitch = true;
            }
            if (updateSubs)
            {
                UpdateSubmarines();
            }

            if (pageIndicators != null)
            {
                for (int i = 0; i < pageIndicators.Length; i++)
                {
                    pageIndicators[i].Color = i == currentPage - 1 ? Color.White : Color.Gray;
                }
            }

            int submarineIndex = (currentPage - 1) * submarinesPerPage;

            for (int i = 0; i < submarineDisplays.Length; i++)
            {
                SubmarineInfo subToDisplay = GetSubToDisplay(submarineIndex);
                if (subToDisplay == null)
                {
                    submarineDisplays[i].submarineImage.Sprite = null;
                    submarineDisplays[i].submarineName.Text = string.Empty;
                    submarineDisplays[i].submarineFee.Text = string.Empty;
                    submarineDisplays[i].submarineClass.Text = string.Empty;
                    submarineDisplays[i].submarineTier.Text = string.Empty;
                    submarineDisplays[i].selectSubmarineButton.Enabled = false;
                    submarineDisplays[i].selectSubmarineButton.OnClicked = null;
                    submarineDisplays[i].displayedSubmarine = null;
                    submarineDisplays[i].middleTextBlock.AutoDraw = false;
                    submarineDisplays[i].previewButton.Visible = false;
                }
                else
                {
                    submarineDisplays[i].displayedSubmarine = subToDisplay;
                    Sprite previewImage = GetPreviewImage(subToDisplay);

                    if (previewImage != null)
                    {
                        submarineDisplays[i].submarineImage.Sprite = previewImage;
                        submarineDisplays[i].middleTextBlock.AutoDraw = false;
                    }
                    else
                    {
                        submarineDisplays[i].submarineImage.Sprite = null;
                        submarineDisplays[i].middleTextBlock.Text = missingPreviewText;
                        submarineDisplays[i].middleTextBlock.AutoDraw = true;
                    }

                    submarineDisplays[i].selectSubmarineButton.Enabled = true;

                    int index = i;
                    submarineDisplays[i].selectSubmarineButton.OnClicked = (button, userData) =>
                    {
                        SelectSubmarine(subToDisplay, submarineDisplays[index].background.Rect);
                        return true;
                    };

                    submarineDisplays[i].submarineName.Text = subToDisplay.DisplayName;                    
                    
                    submarineDisplays[i].submarineClass.Text = TextManager.GetWithVariable("submarineclass.classsuffixformat", "[type]", subToDisplay.Class.Name);
                    submarineDisplays[i].submarineClass.ToolTip = TextManager.Get("submarineclass.description") + "\n\n" + subToDisplay.Class.Description; 

                    submarineDisplays[i].submarineTier.Text = TextManager.Get($"submarinetier.{subToDisplay.Tier}");
                    submarineDisplays[i].submarineTier.ToolTip = TextManager.Get("submarinetier.description");

                    if (!GameMain.GameSession.IsSubmarineOwned(subToDisplay))
                    {
                        LocalizedString amountString = TextManager.FormatCurrency(subToDisplay.GetPrice());
                        submarineDisplays[i].submarineFee.Text = TextManager.GetWithVariable("price", "[amount]", amountString);
                    }
                    else
                    {
                        if (subToDisplay.Name != CurrentOrPendingSubmarine().Name)
                        {
                            submarineDisplays[i].submarineFee.Text = string.Empty;                            
                        }
                        else
                        {
                            submarineDisplays[i].submarineFee.Text = selectedSubText;
                        }
                    }

                    if (transferService && subToDisplay.Name == CurrentOrPendingSubmarine().Name && updateSubs)
                    {                        
                        if (selectedSubmarine == null)
                        {
                            CoroutineManager.StartCoroutine(SelectOwnSubmarineWithDelay(subToDisplay, submarineDisplays[i]));
                        }
                        else
                        {
                            SelectSubmarine(subToDisplay, submarineDisplays[i].background.Rect);
                        }
                    }
                    else if (!transferService && selectedSubmarine == null || !transferService && GameMain.GameSession.IsSubmarineOwned(selectedSubmarine) || subToDisplay == selectedSubmarine)
                    {
                        SelectSubmarine(subToDisplay, submarineDisplays[i].background.Rect);
                    }

                    submarineDisplays[i].previewButton.Visible = true;
                    submarineDisplays[i].previewButton.OnClicked = (btn, obj) =>
                    {
                        SubmarinePreview.Create(subToDisplay);
                        return false;
                    };
                }

                submarineIndex++;
            }

            if (subsToShow.Count == 0)
            {
                SelectSubmarine(null, Rectangle.Empty);
            }
            else
            {
                UpdateItemTransferInfoFrame();
            }
        }

        private void UpdateSubmarines()
        {
            subsToShow.Clear();
            if (transferService)
            {
                subsToShow.AddRange(GameMain.GameSession.OwnedSubmarines);
                subsToShow.Sort(ComparePrice);
                string currentSubName = CurrentOrPendingSubmarine().Name;
                int currentIndex = subsToShow.FindIndex(s => s.Name == currentSubName);
                if (currentIndex != -1)
                {
                    currentPage = (int)Math.Ceiling((currentIndex + 1) / (float)submarinesPerPage);
                }
            }
            else
            {
                subsToShow.AddRange((GameMain.Client is null ? SubmarineInfo.SavedSubmarines : MultiPlayerCampaign.GetCampaignSubs())
                    .Where(s => s.IsCampaignCompatible && !GameMain.GameSession.OwnedSubmarines.Any(os => os.Name == s.Name)));
                if (GameMain.GameSession.Campaign?.Map?.CurrentLocation is Location currentLocation)
                {
                    subsToShow.RemoveAll(sub => !currentLocation.IsSubmarineAvailable(sub));
                }
                subsToShow.Sort(ComparePrice);
            }

            if (transferService)
            {
                SetConfirmButtonState(selectedSubmarine != null && selectedSubmarine.Name != CurrentOrPendingSubmarine().Name);
            }

            pageCount = Math.Max(1, (int)Math.Ceiling(subsToShow.Count / (float)submarinesPerPage));
            UpdatePaging();
            ContentRefreshRequired = false;

            static int ComparePrice(SubmarineInfo x, SubmarineInfo y)
            {
                return x.Price.CompareTo(y.Price) * 100 + x.Name.CompareTo(y.Name);
            }
        }

        private SubmarineInfo GetSubToDisplay(int index)
        {
            if (subsToShow.Count <= index || index < 0) { return null; }
            return subsToShow[index];
        }

        private Sprite GetPreviewImage(SubmarineInfo info)
        {
            Sprite preview = info.PreviewImage;

            if (preview == null)
            {
                SubmarineInfo potentialMatch = SubmarineInfo.SavedSubmarines.FirstOrDefault(s => s.EqualityCheckVal == info.EqualityCheckVal);

                preview = potentialMatch?.PreviewImage;

                // Try name comparison as a backup
                if (preview == null)
                {
                    potentialMatch = SubmarineInfo.SavedSubmarines.FirstOrDefault(s => s.Name == info.Name);
                    preview = potentialMatch?.PreviewImage;
                }
            }

            return preview;
        }

        // Initial submarine selection needs a slight wait to allow the layoutgroups to place content properly
        private IEnumerable<CoroutineStatus> SelectOwnSubmarineWithDelay(SubmarineInfo info, SubmarineDisplayContent display)
        {
            yield return new WaitForSeconds(0.05f);
            SelectSubmarine(info, display.background.Rect);
        }

        // Selection based on key input
        private void SelectSubmarine(int index, int direction)
        {
            SubmarineInfo nextSub = GetSubToDisplay(index + direction);
            if (nextSub == null) return;

            for (int i = 0; i < submarineDisplays.Length; i++)
            {
                if (submarineDisplays[i].displayedSubmarine == nextSub)
                {
                    SelectSubmarine(nextSub, submarineDisplays[i].background.Rect);
                    return;
                }
            }

            ChangePage(direction);

            for (int i = 0; i < submarineDisplays.Length; i++)
            {
                if (submarineDisplays[i].displayedSubmarine == nextSub)
                {
                    SelectSubmarine(nextSub, submarineDisplays[i].background.Rect);
                    return;
                }
            }
        }

        private void SelectSubmarine(SubmarineInfo info, Rectangle backgroundRect)
        {
#if !DEBUG
            if (selectedSubmarine == info) return;
#endif
            specsFrame.Content.ClearChildren();
            selectedSubmarine = info;

            if (info != null)
            {
                bool owned = GameMain.GameSession.IsSubmarineOwned(info);

                if (owned)
                {
                    confirmButton.Text = switchText;
                    confirmButton.OnClicked = (button, userData) =>
                    {
                        ShowTransferPrompt();
                        return true;
                    };
                }
                else
                {
                    confirmButton.Text = purchaseAndSwitchText;
                    confirmButton.OnClicked = (button, userData) =>
                    {
                        ShowBuyPrompt(false);
                        return true;
                    };

                    confirmButtonAlt.Text = purchaseOnlyText;
                    confirmButtonAlt.OnClicked = (button, userData) =>
                    {
                        ShowBuyPrompt(true);
                        return true;
                    };
                }

                SetConfirmButtonState(selectedSubmarine.Name != CurrentOrPendingSubmarine().Name);

                selectedSubmarineIndicator.RectTransform.NonScaledSize = backgroundRect.Size;
                selectedSubmarineIndicator.RectTransform.AbsoluteOffset = new Point(backgroundRect.Left - submarineHorizontalGroup.Rect.Left, 0);

                Sprite previewImage = GetPreviewImage(info);
                listBackground.Sprite = previewImage;
                listBackground.SetCrop(true);

                GUIFont font = GUIStyle.Font;
                info.CreateSpecsWindow(specsFrame, font, includeCrushDepth: true);
                descriptionTextBlock.Text = info.Description;
                descriptionTextBlock.CalculateHeightFromText();
            }
            else
            {
                listBackground.Sprite = null;
                listBackground.SetCrop(false);
                descriptionTextBlock.Text = string.Empty;
                selectedSubmarineIndicator.RectTransform.NonScaledSize = Point.Zero;
                SetConfirmButtonState(false);
            }

            UpdateItemTransferInfoFrame();
        }

        private bool IsSelectedSubCurrentSub => Submarine.MainSub?.Info?.Name == selectedSubmarine?.Name;

        private void UpdateItemTransferInfoFrame()
        {
            if (selectedSubmarine != null)
            {
                if (IsSelectedSubCurrentSub)
                {
                    TransferItemsOnSwitch = false;
                    transferItemsTickBox.Visible = false;
                    itemTransferInfoBlock.Visible = confirmButton.Enabled;
                    itemTransferInfoBlock.Text = TextManager.Get("switchingbacktocurrentsub");
                }
                else if (GameMain.GameSession?.Campaign?.PendingSubmarineSwitch?.Name == selectedSubmarine.Name)
                {
                    transferItemsTickBox.Visible = false;
                    itemTransferInfoBlock.Visible = true;
                    itemTransferInfoBlock.Text = GameMain.GameSession.Campaign.TransferItemsOnSubSwitch ? TextManager.Get("itemtransferenabledreminder") : TextManager.Get("itemtransferdisabledreminder");
                }
                else
                {
                    transferItemsTickBox.Selected = TransferItemsOnSwitch;
                    transferItemsTickBox.Visible = true;
                    itemTransferInfoBlock.Visible = false;
                }
            }
            else
            {
                transferItemsTickBox.Visible = false;
                itemTransferInfoBlock.Visible = false;
            }
        }

        private void SetConfirmButtonState(bool state)
        {
            if (confirmButtonAlt != null)
            {
                confirmButtonAlt.Enabled = state;
            }

            if (confirmButton != null)
            {
                confirmButton.Enabled = state;
            }
        }

        public static SubmarineInfo CurrentOrPendingSubmarine()
        {
            if (GameMain.GameSession?.Campaign?.PendingSubmarineSwitch == null)
            {
                return Submarine.MainSub?.Info;
            }
            else
            {
                return GameMain.GameSession.Campaign.PendingSubmarineSwitch;
            }
        }

        private void ChangePage(int pageChangeDirection)
        {
            SelectSubmarine(null, Rectangle.Empty);
            if (pageChangeDirection < 0 && currentPage > 1) currentPage--;
            if (pageChangeDirection > 0 && currentPage < pageCount) currentPage++;
            browseLeftButton.Enabled = currentPage > 1;
            browseRightButton.Enabled = currentPage < pageCount;

            RefreshSubmarineDisplay(false);
        }

        private void ShowTransferPrompt()
        {
            var text = TextManager.GetWithVariables("switchsubmarinetext",
                ("[submarinename1]", CurrentOrPendingSubmarine().DisplayName),
                ("[submarinename2]", selectedSubmarine.DisplayName));
            text += GetItemTransferText();
            GUIMessageBox msgBox = new GUIMessageBox(TextManager.Get("switchsubmarineheader"), text, messageBoxOptions);


            msgBox.Buttons[0].OnClicked = (applyButton, obj) =>
            {
                if (!TransferItemsOnSwitch && !IsSelectedSubCurrentSub)
                {
                    if (selectedSubmarine.NoItems)
                    {
                        return ShowConfirmationPopup(TextManager.Get("noitemsheader"), TextManager.Get("noitemswarning"));
                    }
                    if (!GameMain.GameSession.IsSubmarineOwned(selectedSubmarine) && !selectedSubmarine.IsManuallyOutfitted)
                    {
                        var (header, body) = GetItemTransferWarningText();
                        return ShowConfirmationPopup(header, body);
                    }
                    if (selectedSubmarine.LowFuel)
                    {
                        return ShowConfirmationPopup(TextManager.Get("lowfuelheader"), TextManager.Get("lowfuelwarning"));
                    }
                }
                return Confirm();
            };
            msgBox.Buttons[0].OnClicked += msgBox.Close;
            msgBox.Buttons[1].OnClicked = msgBox.Close;

            bool ShowConfirmationPopup(LocalizedString header, LocalizedString textBody)
            {
                msgBox.Close();
                var extraConfirmationBox = new GUIMessageBox(header, textBody, new LocalizedString[2] { TextManager.Get("ok"), TextManager.Get("cancel") });
                extraConfirmationBox.Buttons[0].OnClicked = (b, o) => Confirm();
                extraConfirmationBox.Buttons[1].OnClicked = (b, o) =>
                {
                    selectedSubmarine = CurrentOrPendingSubmarine();
                    RefreshSubmarineDisplay(true);
                    return true;
                };
                extraConfirmationBox.Buttons[0].OnClicked += extraConfirmationBox.Close;
                extraConfirmationBox.Buttons[1].OnClicked += extraConfirmationBox.Close;
                return true;
            }

            bool Confirm()
            {
                if (GameMain.Client == null)
                {
                    GameMain.GameSession.SwitchSubmarine(selectedSubmarine, TransferItemsOnSwitch);
                    RefreshSubmarineDisplay(true);
                }
                else
                {
                    GameMain.Client.InitiateSubmarineChange(selectedSubmarine, TransferItemsOnSwitch, Networking.VoteType.SwitchSub);
                }
                return true;
            }
        }

        private (LocalizedString header, LocalizedString body) GetItemTransferWarningText()
        {
            var header = TextManager.Get("itemtransferheader").Fallback(TextManager.Get("lowfuelheader"), useDefaultLanguageIfFound: false);
            var body = TextManager.Get("itemtransferwarning").Fallback(TextManager.Get("lowfuelwarning"), useDefaultLanguageIfFound: false);
            return (header, body);
        }

        private void ShowBuyPrompt(bool purchaseOnly)
        {
            int price = selectedSubmarine.GetPrice();

            if (!GameMain.GameSession.Campaign.CanAfford(price))
            {
                new GUIMessageBox(TextManager.Get("purchasesubmarineheader"), TextManager.GetWithVariables("notenoughmoneyforpurchasetext",
                    ("[currencyname]", currencyName),
                    ("[submarinename]", selectedSubmarine.DisplayName)));
                return;
            }

            GUIMessageBox msgBox;
            if (!purchaseOnly)
            {
                var text = TextManager.GetWithVariables("purchaseandswitchsubmarinetext",
                    ("[submarinename1]", selectedSubmarine.DisplayName),
                    ("[amount]", price.ToString()),
                    ("[currencyname]", currencyName),
                    ("[submarinename2]", CurrentOrPendingSubmarine().DisplayName));
                text += GetItemTransferText();
                msgBox = new GUIMessageBox(TextManager.Get("purchaseandswitchsubmarineheader"), text, messageBoxOptions);

                msgBox.Buttons[0].OnClicked = (applyButton, obj) =>
                {
                    if (!TransferItemsOnSwitch && !IsSelectedSubCurrentSub)
                    {
                        if (selectedSubmarine.NoItems)
                        {
                            ShowConfirmationPopup(TextManager.Get("noitemsheader"), TextManager.Get("noitemswarning"));
                            return false;
                        }
                        if (!GameMain.GameSession.IsSubmarineOwned(selectedSubmarine) && !selectedSubmarine.IsManuallyOutfitted)
                        {
                            var (header, body) = GetItemTransferWarningText();
                            ShowConfirmationPopup(header, body);
                            return false;
                        }
                        if (selectedSubmarine.LowFuel)
                        {
                            ShowConfirmationPopup(TextManager.Get("lowfuelheader"), TextManager.Get("lowfuelwarning"));
                            return false;
                        }
                    }
                    return Confirm();
                };        

                void ShowConfirmationPopup(LocalizedString header, LocalizedString textBody)
                {
                    msgBox.Close();
                    var extraConfirmationBox = new GUIMessageBox(header, textBody, new LocalizedString[2] { TextManager.Get("ok"), TextManager.Get("cancel") });
                    extraConfirmationBox.Buttons[0].OnClicked = (b, o) => Confirm();
                    extraConfirmationBox.Buttons[0].OnClicked += extraConfirmationBox.Close;
                    extraConfirmationBox.Buttons[1].OnClicked += extraConfirmationBox.Close;
                }

                bool Confirm()
                {
                    if (GameMain.Client == null)
                    {
                        if (GameMain.GameSession.TryPurchaseSubmarine(selectedSubmarine))
                        {
                            GameMain.GameSession.SwitchSubmarine(selectedSubmarine, TransferItemsOnSwitch);
                        }
                        RefreshSubmarineDisplay(true);
                    }
                    else
                    {
                        GameMain.Client.InitiateSubmarineChange(selectedSubmarine, TransferItemsOnSwitch, Networking.VoteType.PurchaseAndSwitchSub);
                    }
                    return true;
                }
            }
            else
            {
                msgBox = new GUIMessageBox(TextManager.Get("purchasesubmarineheader"), TextManager.GetWithVariables("purchasesubmarinetext",
                    ("[submarinename]", selectedSubmarine.DisplayName),
                    ("[amount]", price.ToString()),
                    ("[currencyname]", currencyName)) + '\n' + TextManager.Get("submarineswitchinstruction"), messageBoxOptions);

                msgBox.Buttons[0].OnClicked = (applyButton, obj) =>
                {
                    if (GameMain.Client == null)
                    {
                        GameMain.GameSession.TryPurchaseSubmarine(selectedSubmarine);
                        RefreshSubmarineDisplay(true);
                    }
                    else
                    {
                        GameMain.Client.InitiateSubmarineChange(selectedSubmarine, false, Networking.VoteType.PurchaseSub);
                    }
                    return true;
                };
            }

            msgBox.Buttons[0].ClickSound = GUISoundType.ConfirmTransaction;
            msgBox.Buttons[0].OnClicked += msgBox.Close;
            msgBox.Buttons[1].OnClicked = msgBox.Close;
        }     
        
        private LocalizedString GetItemTransferText()
        {
            if (Submarine.MainSub?.Info?.Name == selectedSubmarine.Name)
            {
                return $"\n\n{TextManager.Get("switchingbacktocurrentsub")}";
            }
            var s = "\n\n" + TextManager.Get(TransferItemsOnSwitch ? "itemswillbetransferred" : "itemswontbetransferred");
            s += $" {TextManager.Get("toggleitemtransferprompt")}";
            return s;
        }
    }
}

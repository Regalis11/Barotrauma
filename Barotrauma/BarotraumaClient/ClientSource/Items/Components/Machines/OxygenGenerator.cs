using Barotrauma.Networking;
using Microsoft.Xna.Framework;

namespace Barotrauma.Items.Components
{
    internal partial class OxygenGenerator : IServerSerializable, IClientSerializable
    {
        private GUITickBox powerIndicator, autoControlIndicator;
        private GUIScrollBar rateSlider;

        [Serialize(0.5f, IsPropertySaveable.Yes)]
        public float RateWarningIndicatorLow { get; set; }

        [Serialize(0.25f, IsPropertySaveable.Yes)]
        public float RateWarningIndicatorExtremelyLow { get; set; }

        protected override void CreateGUI()
        {
            if (GuiFrame == null)
            {
                DebugConsole.AddWarning($"OxygenGenerator component of {Item.Name} ({Item.Prefab.Identifier}) has no GUIFrame defined.", Item.ContentPackage);
                return;
            }
            GUILayoutGroup paddedFrame = new(new RectTransform(GuiFrame.Rect.Size - GUIStyle.ItemFrameMargin, GuiFrame.RectTransform, Anchor.Center) { AbsoluteOffset = GUIStyle.ItemFrameOffset })
            {
                Stretch = true,
                RelativeSpacing = 0.1f
            };

            int indicatorHeight = MathUtils.RoundToInt(GUIStyle.SubHeadingFont.LineHeight * 2);
            GUILayoutGroup indicatorArea = new(new RectTransform(Vector2.One, paddedFrame.RectTransform), isHorizontal: true, Anchor.CenterLeft) { Stretch = true };
            powerIndicator = new GUITickBox(new RectTransform(Vector2.UnitX, indicatorArea.RectTransform, minSize: (0, indicatorHeight)), TextManager.Get("EnginePowered"), GUIStyle.SubHeadingFont, style: "IndicatorLightGreen")
            {
                CanBeFocused = false,
                OnAddedToGUIUpdateList = comp => comp.Selected = HasPower && IsActive,
                TextBlock = { Wrap = true }
            };
            powerIndicator.TextBlock.OverrideTextColor(GUIStyle.TextColorNormal);
            autoControlIndicator = new GUITickBox(new RectTransform(Vector2.UnitX, indicatorArea.RectTransform, minSize: (0, indicatorHeight)), TextManager.Get("PumpAutoControl", "ReactorAutoControl"), GUIStyle.SubHeadingFont, style: "IndicatorLightYellow")
            {
                Enabled = false,
                OnAddedToGUIUpdateList = comp => comp.Selected = controlLockTimer > 0f,
                ToolTip = TextManager.Get("AutoControlTip"),
                TextBlock = { Wrap = true, TextAlignment = Alignment.CenterRight }
            };
            autoControlIndicator.TextBlock.OverrideTextColor(GUIStyle.TextColorNormal);
            autoControlIndicator.TextBlock.SetAsFirstChild();
            GUITextBlock.AutoScaleAndNormalize(powerIndicator.TextBlock, autoControlIndicator.TextBlock);

            GUITextBlock rateName = new(new RectTransform(Vector2.UnitX, paddedFrame.RectTransform), TextManager.Get("OxygenGenerationRate"), GUIStyle.TextColorNormal, GUIStyle.SubHeadingFont, Alignment.CenterLeft);
            GUITextBlock rateText = new(new RectTransform(Vector2.One, rateName.RectTransform), "", GUIStyle.TextColorNormal, GUIStyle.Font, Alignment.CenterRight)
            {
                TextGetter = () => $"{MathUtils.RoundToInt(generatedAmount * generationRatio)}/s ({MathUtils.RoundToInt(generationRatio * 100f)}%)"
            };
            if (rateText.TextSize.X > rateText.Rect.Width) { rateText.Font = GUIStyle.SmallFont; }

            GUIFrame rateSliderContainer = new(new RectTransform(Vector2.UnitX, paddedFrame.RectTransform, minSize: (0, 50)));
            new GUICustomComponent(new RectTransform(new Vector2(0.95f, 0.9f), rateSliderContainer.RectTransform, Anchor.Center), (sb, comp) =>
            {
                if (RateWarningIndicatorLow > 0f)
                {
                    GUI.DrawRectangle(sb, comp.Rect.Location.ToVector2(), ( comp.Rect.Width * RateWarningIndicatorLow, comp.Rect.Height), GUIStyle.Orange, isFilled: true);
                }
                if (RateWarningIndicatorExtremelyLow > 0f)
                {
                    GUI.DrawRectangle(sb, comp.Rect.Location.ToVector2(), (comp.Rect.Width * RateWarningIndicatorExtremelyLow, comp.Rect.Height), GUIStyle.Red, isFilled: true);
                }
            });
            rateSlider = new GUIScrollBar(new RectTransform(Vector2.One, rateSliderContainer.RectTransform, Anchor.Center), barSize: 0.1f, isHorizontal: true, style: "DeviceSliderSeeThrough")
            {
                Step = GenerationRatioStep,
                OnMoved = (_, newRatio) =>
                {
                    GenerationRatio = newRatio;
                    if (GameMain.Client != null)
                    {
                        item.CreateClientEvent(this);
                        correctionTimer = CorrectionDelay;
                    }
                    return true;
                },
                OnAddedToGUIUpdateList = comp => comp.Enabled = controlLockTimer <= 0f
            };
            rateSlider.Bar.RectTransform.MaxSize = new Point(rateSlider.Bar.Rect.Height);
        }

        partial void InitProjSpecific() => CreateGUI();
        
        public override void OnItemLoaded()
        {
            base.OnItemLoaded();
            UpdateSlider();
        }

        #region Networking
        public void ClientEventWrite(IWriteMessage msg, NetEntityEvent.IData extraData) => WriteGenerationRatio(msg);
        public void ClientEventRead(IReadMessage msg, float sendingTime)
        {
            if (correctionTimer > 0f)
            {
                StartDelayedCorrection(msg.ExtractBits(4), sendingTime);
                return;
            }

            GenerationRatio = ReadGenerationRatio(msg);
        }
        #endregion

        private void UpdateSlider()
        {
            if (rateSlider != null)
            {
                rateSlider.BarScroll = generationRatio;
            }
        }
    }
}
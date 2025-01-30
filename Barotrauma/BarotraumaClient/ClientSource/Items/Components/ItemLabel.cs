using Barotrauma.Networking;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Xml.Linq;

namespace Barotrauma.Items.Components
{
    partial class ItemLabel : ItemComponent, IDrawableComponent, IHasExtraTextPickerEntries
    {
        private GUITextBlock textBlock;

        private float scrollAmount;
        private string scrollingText;
        private float scrollPadding;
        private int scrollIndex;
        private bool needsScrolling;

        private float[] charWidths;

        private float prevScale;
        private Rectangle prevRect;
        private StringBuilder sb;

        public LocalizedString DisplayText
        {
            get;
            private set;
        }

        private bool scrollable;
        [Serialize(false, IsPropertySaveable.Yes, description: "Should the text scroll horizontally across the item if it's too long to be displayed all at once.")]
        public bool Scrollable
        {
            get { return scrollable; }
            set
            {
                scrollable = value;
                IsActive = value || parseSpecialTextTagOnStart;
                TextBlock.Wrap = !scrollable;
            }
        }

        [Serialize(20.0f, IsPropertySaveable.Yes, description: "How fast the text scrolls across the item (only valid if Scrollable is set to true).")]
        public float ScrollSpeed
        {
            get;
            set;
        }

        private GUITextBlock TextBlock
        {
            get
            {
                if (textBlock == null)
                {
                    RecreateTextBlock();
                }
                return textBlock;
            }
        }

        public ItemLabel(Item item, ContentXElement element)
            : base(item, element)
        {            
        }

        public IEnumerable<string> GetExtraTextPickerEntries()
        {
            return SpecialTextTags;
        }

        private void SetScrollingText()
        {
            if (!scrollable) { return; }

            float totalWidth = textBlock.Font.MeasureString(DisplayText).X * TextBlock.TextScale;
            float textAreaWidth = Math.Max(textBlock.Rect.Width - textBlock.Padding.X - textBlock.Padding.Z, 0);
            if (totalWidth >= textAreaWidth)
            {
                //add enough spaces to fill the rect
                //(so the text can scroll entirely out of view before we reset it back to start)
                needsScrolling = true;
                float spaceWidth = textBlock.Font.MeasureChar(' ').X * TextBlock.TextScale;
                scrollingText = new string(' ', (int)Math.Ceiling(textAreaWidth / spaceWidth)) + DisplayText.Value;
                TextBlock.TextAlignment = Alignment.CenterLeft;
            }
            else
            {
                //whole text can fit in the textblock, no need to scroll
                needsScrolling = false;
                TextBlock.Text = scrollingText = DisplayText.Value;
                TextBlock.TextAlignment = alignment;
                scrollPadding = 0;
                scrollAmount = 0.0f;
                scrollIndex = 0;
                return;
            }

            //calculate character widths
            scrollPadding = 0;
            charWidths = new float[scrollingText.Length];
            for (int i = 0; i < scrollingText.Length; i++)
            {
                float charWidth = TextBlock.Font.MeasureChar(scrollingText[i]).X * TextBlock.TextScale;
                scrollPadding = Math.Max(charWidth, scrollPadding);
                charWidths[i] = charWidth;
            }

            scrollIndex = MathHelper.Clamp(scrollIndex, 0, DisplayText.Length);
        }

        private static readonly string[] SpecialTextTags = new string[] { "[CurrentLocationName]", "[CurrentBiomeName]", "[CurrentSubName]" };
        private bool parseSpecialTextTagOnStart;
        private void SetDisplayText(string value)
        {
            if (SpecialTextTags.Contains(value))
            {
                parseSpecialTextTagOnStart = true;
                IsActive = true;
            }

            DisplayText = IgnoreLocalization ? value : TextManager.Get(value).Fallback(value);

            TextBlock.Text = DisplayText;
            if (Screen.Selected == GameMain.SubEditorScreen && Scrollable)
            {
                TextBlock.Text = ToolBox.LimitString(DisplayText, TextBlock.Font, item.Rect.Width);
            }

            SetScrollingText();
        }

        private const float BaseTextSize = 12.0f;
        private float BaseToRealTextScaleFactor => BaseTextSize / GUIStyle.UnscaledSmallFont.Size;
        private void RecreateTextBlock()
        {
            textBlock = new GUITextBlock(new RectTransform(item.Rect.Size), "",
                textColor: textColor, font: font, textAlignment: needsScrolling ? Alignment.CenterLeft : alignment, wrap: !scrollable, style: null)
            {
                TextDepth = item.SpriteDepth - 0.00001f,
                RoundToNearestPixel = false,
                TextScale = TextScale * BaseToRealTextScaleFactor,
                Padding = padding * item.Scale
            };
        }

        private void ParseSpecialTextTag()
        {
            switch (text)
            {
                case "[CurrentLocationName]":
                    SetDisplayText(Level.Loaded?.StartLocation?.DisplayName.Value ?? string.Empty);
                    break;
                case "[CurrentBiomeName]":
                    SetDisplayText(Level.Loaded?.LevelData?.Biome?.DisplayName.Value ?? string.Empty);
                    break;
                case "[CurrentSubName]":
                    SetDisplayText(item.Submarine?.Info?.DisplayName.Value ?? string.Empty);
                    break;
                default:
                    break;
            }
        }

        public override void Update(float deltaTime, Camera cam)
        {
            if (parseSpecialTextTagOnStart)
            {
                ParseSpecialTextTag();
                parseSpecialTextTagOnStart = false;
            }

            if (!scrollable) 
            {
                IsActive = false;
                return; 
            }

            if (scrollingText == null)
            {
                SetScrollingText();
            }

            if (!needsScrolling) { return; }

            scrollAmount -= deltaTime * ScrollSpeed;
            UpdateScrollingText();
        }

        private void UpdateScrollingText()
        {
            if (!scrollable || !needsScrolling) { return; }

            float currLength = 0;
            sb ??= new StringBuilder();
            sb.Clear();
            float textAreaWidth = textBlock.Rect.Width - textBlock.Padding.X - textBlock.Padding.Z;
            for (int i = scrollIndex; i < scrollingText.Length; i++)
            {
                //first character is out of view -> skip to next character
                if (i == scrollIndex && scrollAmount < -charWidths[i])
                {
                    scrollIndex++;
                    scrollAmount = 0;
                    if (scrollIndex >= scrollingText.Length) //reached the last character, reset
                    {
                        scrollIndex = 0;
                        break;
                    }
                    continue;
                }

                //reached the right edge, stop adding more character
                if (scrollAmount + (currLength + charWidths[i] + scrollPadding) >= textAreaWidth)
                {
                    break;
                }
                else
                {
                    currLength += charWidths[i];
                    sb.Append(scrollingText[i]);
                }
            }

            TextBlock.Text = sb.ToString();
        }

        public override void OnScaleChanged()
        {
            RecreateTextBlock();
            SetDisplayText(Text);
            prevScale = item.Scale;
            prevRect = item.Rect;
        }
        
        public void Draw(SpriteBatch spriteBatch, bool editing = false, float itemDepth = -1, Color? overrideColor = null)
        {
            if (item.ParentInventory != null) { return; }
            if (editing)
            {
                if (!MathUtils.NearlyEqual(prevScale, item.Scale) || prevRect != item.Rect)
                {
                    RecreateTextBlock();
                    SetDisplayText(Text);
                    prevScale = item.Scale;
                    prevRect = item.Rect;
                }
            }

            var drawPos = new Vector2(
                item.DrawPosition.X - item.Rect.Width / 2.0f,
                -(item.DrawPosition.Y + item.Rect.Height / 2.0f));

            textBlock.TextDepth = item.SpriteDepth - 0.0001f;
            textBlock.TextOffset = drawPos - textBlock.Rect.Location.ToVector2() + (editing ? Vector2.Zero : new Vector2(scrollAmount + scrollPadding, 0.0f));
            textBlock.DrawManually(spriteBatch);
        }

        public void ClientEventRead(IReadMessage msg, float sendingTime)
        {
            Text = msg.ReadString();
        }

    }
}

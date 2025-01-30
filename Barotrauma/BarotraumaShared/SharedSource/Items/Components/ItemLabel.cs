using Barotrauma.Networking;
using Microsoft.Xna.Framework;

namespace Barotrauma.Items.Components
{
    partial class ItemLabel : ItemComponent, IDrawableComponent, IServerSerializable
    {
        public Vector2 DrawSize
        {
            //use the extents of the item as the draw size
            get { return Vector2.Zero; }
        }

        private bool ignoreLocalization;
        [Editable, Serialize(false, IsPropertySaveable.Yes, "Whether or not to skip localization and always display the raw value.")]
        public bool IgnoreLocalization
        {
            get => ignoreLocalization;
            set
            {
                ignoreLocalization = value;
#if CLIENT
                SetDisplayText(Text);
#endif
            }
        }

        private Vector4 padding;
        [Editable(DecimalCount = 0, VectorComponentLabels = new string[] { "inputtype.left", "inputtype.up", "inputtype.right", "inputtype.down" }), Serialize("0,0,0,0", IsPropertySaveable.Yes, description: "The amount of padding around the text in pixels.")]
        public Vector4 Padding
        {
            get => padding;
            set
            {
                padding = value;
#if CLIENT
                TextBlock.Padding = value * item.Scale;
#endif
            }
        }

        private Alignment alignment;
        [Editable, Serialize(Alignment.Center, IsPropertySaveable.Yes, description: "The alignment of the label's text.")]
        public Alignment TextAlignment
        {
            get => alignment;
            set
            {
                alignment = value;
#if CLIENT
                textBlock.TextAlignment = value;
#endif
            }
        }

        private string text;
        [Serialize("", IsPropertySaveable.Yes, translationTextTag: "Label.", description: "The text displayed in the label.", alwaysUseInstanceValues: true), Editable(MaxLength = 100)]
        public string Text
        {
            get => text;
            set
            {
                if (text == value || item.Rect.Width < 5) { return; }
                text = value;

#if CLIENT
                if (TextBlock.Rect.Width != item.Rect.Width || textBlock.Rect.Height != item.Rect.Height)
                {
                    textBlock = null;
                }

                SetDisplayText(value);
                UpdateScrollingText();
#endif
            }
        }

        private GUIFont font;
        [Editable, Serialize("UnscaledSmallFont", IsPropertySaveable.Yes, "The label's font.")]
        public GUIFont Font
        {
            get => font;
            set
            {
                font = value;
#if CLIENT
                textBlock.Font = value;
#endif
            }
        }

        private Color textColor;
        [Editable, Serialize("0,0,0,255", IsPropertySaveable.Yes, "The color of the text displayed on the label (R,G,B,A).", alwaysUseInstanceValues: true)]
        public Color TextColor
        {
            get => textColor;
            set
            {
                textColor = value;
#if CLIENT
                if (textBlock != null) { textBlock.TextColor = value; }
#endif
            }
        }

        private float textScale;
        [Editable(0f, 10f), Serialize(1f, IsPropertySaveable.Yes, "The scale of the text displayed on the label.", alwaysUseInstanceValues: true)]
        public float TextScale
        {
            get
            {
                return
#if CLIENT
                    textBlock == null ? textScale : textBlock.TextScale / BaseToRealTextScaleFactor;
#else
                    textScale;
#endif
            }

            set
            {
                textScale = value;
#if CLIENT
                if (textBlock != null)
                {
                    float prevScale = TextBlock.TextScale;
                    textBlock.TextScale = MathHelper.Clamp(value * BaseToRealTextScaleFactor, 0.1f, 10f);
                    if (!MathUtils.NearlyEqual(prevScale, TextBlock.TextScale))
                    {
                        SetScrollingText();
                    }
                }
#endif
            }
        }

        partial void OnStateChanged();

        private string prevColorSignal;

        public override void ReceiveSignal(Signal signal, Connection connection)
        {
            switch (connection.Name)
            {
                case "set_text":
                    if (Text == signal.value) { return; }
                    Text = signal.value;
                    OnStateChanged();
                    break;
                case "set_text_color":
                    if (signal.value != prevColorSignal)
                    {
                        TextColor = XMLExtensions.ParseColor(signal.value, false);
                        prevColorSignal = signal.value;
                    }
                    break;
            }
        }
    }
}
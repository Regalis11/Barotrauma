﻿using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace Barotrauma
{
    public class GUIProgressBar : GUIComponent
    {
        private bool isHorizontal;

        private GUIFrame frame, slider;
        private float barSize;
                
        public delegate float ProgressGetterHandler();
        public ProgressGetterHandler ProgressGetter;

        public bool IsHorizontal
        {
            get { return isHorizontal; }
            set { isHorizontal = value; }
        }

        public float BarSize
        {
            get { return barSize; }
            set
            {
                barSize = MathHelper.Clamp(value, 0.0f, 1.0f);
                //UpdateRect();
            }
        }

        [System.Obsolete("Use RectTransform instead of Rectangle")]
        public GUIProgressBar(Rectangle rect, Color color, float barSize, GUIComponent parent = null)
            : this(rect, color, barSize, (Alignment.Left | Alignment.Top), parent)
        {
        }

        [System.Obsolete("Use RectTransform instead of Rectangle")]
        public GUIProgressBar(Rectangle rect, Color color, float barSize, Alignment alignment, GUIComponent parent = null)
            : this(rect, color, null, barSize, alignment, parent)
        {

        }

        [System.Obsolete("Use RectTransform instead of Rectangle")]
        public GUIProgressBar(Rectangle rect, Color color, string style, float barSize, Alignment alignment, GUIComponent parent = null)
            : base(style)
        {
            this.rect = rect;
            this.color = color;
            isHorizontal = (rect.Width > rect.Height);

            this.alignment = alignment;
            
            if (parent != null)
                parent.AddChild(this);

            frame = new GUIFrame(new Rectangle(0, 0, 0, 0), null, this);
            GUI.Style.Apply(frame, "", this);

            slider = new GUIFrame(new Rectangle(0, 0, 0, 0), null);
            GUI.Style.Apply(slider, "Slider", this);

            this.barSize = barSize;
            //UpdateRect();
        }

        /// <summary>
        /// This is the new constructor.
        /// </summary>
        public GUIProgressBar(RectTransform rectT, float barSize, Color? color = null, string style = "") : base(style, rectT)
        {
            if (color.HasValue)
            {
                this.color = color.Value;
            }
            isHorizontal = (Rect.Width > Rect.Height);
            frame = new GUIFrame(new RectTransform(Vector2.One, rectT));
            GUI.Style.Apply(frame, "", this);
            slider = new GUIFrame(new RectTransform(Vector2.One, rectT));
            GUI.Style.Apply(slider, "Slider", this);
            this.barSize = barSize;
        }

        /*public override void ApplyStyle(GUIComponentStyle style)
        {
            if (frame == null) return;

            frame.Color = style.Color;
            frame.HoverColor = style.HoverColor;
            frame.SelectedColor = style.SelectedColor;

            Padding = style.Padding;

            frame.OutlineColor = style.OutlineColor;

            this.style = style;
        }*/

        /*private void UpdateRect()
        {
            if (RectTransform != null)
            {
                var newSize = isHorizontal ? new Vector2(barSize, 1) : new Vector2(1, barSize);
                slider.RectTransform.Resize(newSize);
            }
            else
            {
                slider.Rect = new Rectangle(
                    (int)(frame.Rect.X + padding.X),
                    (int)(frame.Rect.Y + padding.Y),
                    isHorizontal ? (int)((frame.Rect.Width - padding.X - padding.Z) * barSize) : frame.Rect.Width,
                    isHorizontal ? (int)(frame.Rect.Height - padding.Y - padding.W) : (int)(frame.Rect.Height * barSize));
            }
        }*/
        
        protected override void Draw(SpriteBatch spriteBatch)
        {
            if (!Visible) return;

            if (ProgressGetter != null) BarSize = ProgressGetter();   

            Rectangle sliderRect = new Rectangle(
                    frame.Rect.X,
                    (int)(frame.Rect.Y + (isHorizontal ? 0 : frame.Rect.Height * (1.0f - barSize))),
                    isHorizontal ? (int)((frame.Rect.Width) * barSize) : frame.Rect.Width,
                    isHorizontal ? (int)(frame.Rect.Height) : (int)(frame.Rect.Height * barSize));
            
            frame.Visible = true;
            slider.Visible = true;
            if (AutoDraw)
            {
                frame.DrawAuto(spriteBatch);
            }
            else
            {
                frame.DrawManually(spriteBatch);
            }

            Rectangle prevScissorRect = spriteBatch.GraphicsDevice.ScissorRectangle;
            spriteBatch.GraphicsDevice.ScissorRectangle = sliderRect;

            Color currColor = color;
            if (state == ComponentState.Selected) currColor = selectedColor;
            if (state == ComponentState.Hover) currColor = hoverColor;

            slider.Color = currColor;
            if (AutoDraw)
            {
                slider.DrawAuto(spriteBatch);
            }
            else
            {
                slider.DrawManually(spriteBatch);
            }
            //hide the slider, we've already drawn it manually
            frame.Visible = false;
            slider.Visible = false;
            spriteBatch.GraphicsDevice.ScissorRectangle = prevScissorRect;
        }
    }
}

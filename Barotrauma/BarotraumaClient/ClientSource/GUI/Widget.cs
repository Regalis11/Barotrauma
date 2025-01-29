using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Barotrauma.Extensions;

namespace Barotrauma
{
    public enum WidgetShape
    {
        Rectangle,
        Circle,
        Cross
    }

    internal class Widget
    {
        public WidgetShape Shape;
        public LocalizedString Tooltip;
        public bool ShowTooltip = true;
        public Rectangle DrawRect => new Rectangle((int)(DrawPos.X - (float)Size / 2), (int)(DrawPos.Y - (float)Size / 2), Size, Size);
        public Rectangle InputRect
        {
            get
            {
                var inputRect = DrawRect;
                inputRect.Inflate(InputAreaMargin, InputAreaMargin);
                return inputRect;
            }
        }

        public Vector2 DrawPos { get; set; }
        public int Size = 10;
        public float Thickness = 1f;
        /// <summary>
        /// Used only for circles.
        /// </summary>
        public int Sides = 40;
        /// <summary>
        /// Currently used only for rectangles.
        /// </summary>
        public bool IsFilled;
        public int InputAreaMargin;
        public Color Color = GUIStyle.Red;
        public Color? SecondaryColor;
        public Color TextColor = Color.White;
        public Color TextBackgroundColor = Color.Black * 0.5f;
        public readonly string Id;

        public event Action Selected;
        public event Action Deselected;
        public event Action Hovered;
        public event Action MouseUp;
        public event Action MouseDown;
        public event Action<float> MouseHeld;
        public event Action<float> PreUpdate;
        public event Action<float> PostUpdate;
        public event Action<SpriteBatch, float> PreDraw;
        public event Action<SpriteBatch, float> PostDraw;

        public bool RequireMouseOn = true;

        public Action Refresh;

        public object Data;

        public bool IsSelected => enabled && SelectedWidgets.Contains(this);
        public bool IsControlled => IsSelected && PlayerInput.PrimaryMouseButtonHeld();
        public bool IsMouseOver => GUI.MouseOn == null && InputRect.Contains(PlayerInput.MousePosition);
        private bool enabled = true;
        public bool Enabled
        {
            get { return enabled; }
            set
            {
                enabled = value;
                if (!enabled && SelectedWidgets.Contains(this))
                {
                    SelectedWidgets.Remove(this);
                }
            }
        }

        private static bool multiselect;
        public static bool EnableMultiSelect
        {
            get { return multiselect; }
            set
            {
                multiselect = value;
                if (!multiselect && SelectedWidgets.Multiple())
                {
                    SelectedWidgets = SelectedWidgets.Take(1).ToList();
                }
            }
        }
        public Vector2? TooltipOffset;

        public Widget LinkedWidget;

        public static List<Widget> SelectedWidgets = new List<Widget>();

        public Widget(string id, int size, WidgetShape shape)
        {
            Id = id;
            Size = size;
            Shape = shape;
        }

        public virtual void Update(float deltaTime)
        {
            PreUpdate?.Invoke(deltaTime);
            if (!enabled) { return; }
            if (IsMouseOver || (!RequireMouseOn && SelectedWidgets.Contains(this) && PlayerInput.PrimaryMouseButtonHeld()))
            {
                if (RequireMouseOn || PlayerInput.PrimaryMouseButtonDown())
                {
                    if ((multiselect && !SelectedWidgets.Contains(this)) || SelectedWidgets.None())
                    {
                        SelectedWidgets.Add(this);
                        Selected?.Invoke();
                    }
                }
            }
            else if (SelectedWidgets.Contains(this))
            {
                System.Diagnostics.Debug.WriteLine("selectedWidgets.Contains(this) -> remove");
                SelectedWidgets.Remove(this);
                Deselected?.Invoke();
            }
            if (IsSelected)
            {
                if (PlayerInput.PrimaryMouseButtonDown())
                {
                    MouseDown?.Invoke();
                }
                if (PlayerInput.PrimaryMouseButtonHeld())
                {
                    MouseHeld?.Invoke(deltaTime);
                }
                if (PlayerInput.PrimaryMouseButtonClicked())
                {
                    MouseUp?.Invoke();
                }
            }
            PostUpdate?.Invoke(deltaTime);
        }

        public virtual void Draw(SpriteBatch spriteBatch, float deltaTime)
        {
            PreDraw?.Invoke(spriteBatch, deltaTime);
            var drawRect = DrawRect;
            switch (Shape)
            {
                case WidgetShape.Rectangle:
                    if (SecondaryColor.HasValue)
                    {
                        GUI.DrawRectangle(spriteBatch, drawRect, SecondaryColor.Value, IsFilled, thickness: 2);
                    }
                    GUI.DrawRectangle(spriteBatch, drawRect, Color, IsFilled, thickness: IsSelected ? (int)(Thickness * 3) : (int)Thickness);
                    break;
                case WidgetShape.Circle:
                    if (SecondaryColor.HasValue)
                    {
                        ShapeExtensions.DrawCircle(spriteBatch, DrawPos, Size / 2, Sides, SecondaryColor.Value, thickness: 2);
                    }
                    ShapeExtensions.DrawCircle(spriteBatch, DrawPos, Size / 2, Sides, Color, thickness: IsSelected ? 3 : 1);
                    break;
                case WidgetShape.Cross:
                    float halfSize = Size / 2;
                    if (SecondaryColor.HasValue)
                    {
                        GUI.DrawLine(spriteBatch, DrawPos + Vector2.UnitY * halfSize, DrawPos - Vector2.UnitY * halfSize, SecondaryColor.Value, width: 2);
                        GUI.DrawLine(spriteBatch, DrawPos + Vector2.UnitX * halfSize, DrawPos - Vector2.UnitX * halfSize, SecondaryColor.Value, width: 2);
                    }
                    GUI.DrawLine(spriteBatch, DrawPos + Vector2.UnitY * halfSize, DrawPos - Vector2.UnitY * halfSize, Color, width: IsSelected ? 3 : 1);
                    GUI.DrawLine(spriteBatch, DrawPos + Vector2.UnitX * halfSize, DrawPos - Vector2.UnitX * halfSize, Color, width: IsSelected ? 3 : 1);
                    break;
                default: throw new NotImplementedException(Shape.ToString());
            }
            if (IsSelected)
            {
                if (ShowTooltip && !Tooltip.IsNullOrEmpty())
                {
                    var offset = TooltipOffset ?? new Vector2(Size, -Size / 2f);
                    GUIComponent.DrawToolTip(spriteBatch, Tooltip, DrawPos + offset, TextColor, TextBackgroundColor);
                }
            }
            PostDraw?.Invoke(spriteBatch, deltaTime);
        }
    }
}

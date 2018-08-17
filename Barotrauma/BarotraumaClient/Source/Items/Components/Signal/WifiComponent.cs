﻿using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace Barotrauma.Items.Components
{
    partial class WifiComponent : IDrawableComponent
    {
        public void Draw(SpriteBatch spriteBatch, bool editing)
        {
            if (!editing || !MapEntity.SelectedList.Contains(item)) return;

            Vector2 pos = new Vector2(item.DrawPosition.X, -item.DrawPosition.Y);
            ShapeExtensions.DrawLine(spriteBatch, pos + Vector2.UnitY * range, pos - Vector2.UnitY * range, Color.Cyan * 0.5f, 2);
            ShapeExtensions.DrawLine(spriteBatch, pos + Vector2.UnitX * range, pos - Vector2.UnitX * range, Color.Cyan * 0.5f, 2);
            ShapeExtensions.DrawCircle(spriteBatch, pos, range, 32, Color.Cyan * 0.5f, 3);
        }
    }
}

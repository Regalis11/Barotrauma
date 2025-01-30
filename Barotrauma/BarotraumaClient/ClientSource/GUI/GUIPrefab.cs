#nullable enable
using Barotrauma.Extensions;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.Linq;
using System.Xml.Linq;

namespace Barotrauma
{
    public abstract class GUIPrefab : Prefab
    {
        public GUIPrefab(ContentXElement element, UIStyleFile file) : base(file, element) { }

        protected override Identifier DetermineIdentifier(XElement element)
        {
            return element.NameAsIdentifier();
        }

        protected int ParseSize(XElement element, string attributeName)
        {
            string valueStr = element.GetAttributeString(attributeName, string.Empty);
            bool relativeToWidth = valueStr.EndsWith("vw");
            bool relativeToHeight = valueStr.EndsWith("vh");
            if (relativeToWidth || relativeToHeight)
            {
                string floatStr = valueStr.Substring(0, valueStr.Length - 2);
                if (!float.TryParse(floatStr, NumberStyles.Any, CultureInfo.InvariantCulture, out float relativeHeight))
                {
                    DebugConsole.ThrowError($"Error while parsing a {nameof(GUIComponentStyle)}: {valueStr} is not a valid size.");
                }
                return (int)(relativeHeight / 100.0f * (relativeToWidth ? GameMain.GraphicsWidth : GameMain.GraphicsHeight));
            }
            else
            {
                return element.GetAttributeInt(attributeName, 0);
            }
        }
    }

    public abstract partial class GUISelector<T> where T : GUIPrefab
    {
        public readonly PrefabSelector<T> Prefabs = new PrefabSelector<T>();
    }

    public partial class GUIFontPrefab : GUIPrefab
    {
        private readonly ContentXElement element;
        private ScalableFont? font;
        public ScalableFont? Font
        {
            get
            {
                if (Language != GameSettings.CurrentConfig.Language) { LoadFont(); }
                return font;
            }
        }

        private ImmutableDictionary<TextManager.SpeciallyHandledCharCategory, ScalableFont>? specialHandlingFonts;

        public ScalableFont? GetFontForCategory(TextManager.SpeciallyHandledCharCategory category)
        {
            if (Language != GameSettings.CurrentConfig.Language) { LoadFont(); }
            if (font is null) { return null; }
            if (specialHandlingFonts is null) { return font; }
            if (font.SpeciallyHandledCharCategory.HasFlag(category)) { return font; }
            return specialHandlingFonts.TryGetValue(category, out var resultFont)
                ? resultFont
                : specialHandlingFonts.TryGetValue(TextManager.SpeciallyHandledCharCategory.CJK, out resultFont)
                    ? resultFont
                    : font;
        }

        public LanguageIdentifier Language { get; private set; }

        public GUIFontPrefab(ContentXElement element, UIStyleFile file) : base(element, file)
        {
            this.element = element;
            LoadFont();
        }

        public void LoadFont()
        {
            string? fontPath = GetFontFilePath(element);
            uint size = GetFontSize(element);
            bool dynamicLoading = GetFontDynamicLoading(element);
            var shcc = GetShcc(element);
            font?.Dispose();
            specialHandlingFonts?.Values.ForEach(f => f.Dispose());
            font = new ScalableFont(
                fontPath,
                size,
                GameMain.Instance.GraphicsDevice,
                dynamicLoading,
                shcc)
            {
                ForceUpperCase = element.GetAttributeBool("forceuppercase", false)
            };

            var fallbackFonts = new Dictionary<TextManager.SpeciallyHandledCharCategory, ScalableFont>();
            foreach (var flag in TextManager.SpeciallyHandledCharCategories)
            {
                if (shcc.HasFlag(flag)) { continue; }

                var extractedFont = ExtractFont(flag, element);
                if (extractedFont is null) { continue; }
                fallbackFonts.Add(flag, extractedFont);
            }
            fallbackFonts.Values.ForEach(ff => ff.ForceUpperCase = font.ForceUpperCase);
            specialHandlingFonts = fallbackFonts.ToImmutableDictionary();
            Language = GameSettings.CurrentConfig.Language;
        }

        public override void Dispose()
        {
            font?.Dispose();
            font = null;
            specialHandlingFonts?.Values.ForEach(f => f.Dispose());
            specialHandlingFonts = null;
        }

        private ScalableFont? ExtractFont(TextManager.SpeciallyHandledCharCategory flag, ContentXElement element)
        {
            foreach (var subElement in element.Elements().Reverse())
            {
                if (subElement.NameAsIdentifier() != "override") { continue; }
                if (ScalableFont.ExtractShccFromXElement(subElement).HasFlag(flag))
                {
                    uint overrideFontSize = GetFontSize(subElement, defaultSize: font?.Size ?? 14);
                    return new ScalableFont(subElement, overrideFontSize, GameMain.Instance.GraphicsDevice);
                }
            }

            ScalableFont hardcodedFallback(string path)
                => new ScalableFont(
                    path,
                    font?.Size ?? 0,
                    GameMain.Instance.GraphicsDevice,
                    dynamicLoading: true,
                    speciallyHandledCharCategory: flag);
            
            return flag switch
            {
                TextManager.SpeciallyHandledCharCategory.CJK
                    => hardcodedFallback("Content/Fonts/NotoSans/NotoSansCJKsc-Bold.otf"),
                TextManager.SpeciallyHandledCharCategory.Cyrillic
                    => hardcodedFallback("Content/Fonts/Oswald-Bold.ttf"),
                _ => null
            };
        }
        
        private string? GetFontFilePath(ContentXElement element)
        {
            foreach (var subElement in element.Elements())
            {
                if (IsValidOverride(subElement))
                {
                    return subElement.GetAttributeContentPath("file")?.Value;
                }
            }
            return element.GetAttributeContentPath("file")?.Value;
        }

        private uint GetFontSize(XElement element, uint defaultSize = 14)
        {
            //check if any of the language override fonts want to override the font size as well
            foreach (var subElement in element.Elements())
            {
                if (IsValidOverride(subElement))
                {
                    uint overrideFontSize = GetFontSize(subElement, 0);
                    if (overrideFontSize > 0) { return (uint)Math.Round(overrideFontSize * GameSettings.CurrentConfig.Graphics.TextScale); }
                }
            }

            foreach (var subElement in element.Elements())
            {
                if (!subElement.Name.ToString().Equals("size", StringComparison.OrdinalIgnoreCase)) { continue; }
                Point maxResolution = subElement.GetAttributePoint("maxresolution", new Point(int.MaxValue, int.MaxValue));
                if (GameMain.GraphicsWidth <= maxResolution.X && GameMain.GraphicsHeight <= maxResolution.Y)
                {
                    int rawSize = ParseSize(subElement, "size");
                    return (uint)Math.Round(rawSize * GameSettings.CurrentConfig.Graphics.TextScale);
                }
            }
            return (uint)Math.Round(defaultSize * GameSettings.CurrentConfig.Graphics.TextScale);
        }

        private bool GetFontDynamicLoading(XElement element)
        {
            foreach (var subElement in element.Elements())
            {
                if (IsValidOverride(subElement))
                {
                    return subElement.GetAttributeBool("dynamicloading", false);
                }
            }
            return element.GetAttributeBool("dynamicloading", false);
        }

        private TextManager.SpeciallyHandledCharCategory GetShcc(XElement element)
        {
            foreach (var subElement in element.Elements())
            {
                if (IsValidOverride(subElement))
                {
                    return ScalableFont.ExtractShccFromXElement(subElement);
                }
            }
            return ScalableFont.ExtractShccFromXElement(element);
        }

        private bool IsValidOverride(XElement element)
        {
            if (!element.IsOverride()) { return false; }
            var languages = element.GetAttributeIdentifierArray("language", Array.Empty<Identifier>());
            return languages.Any(l => l.ToLanguageIdentifier() == GameSettings.CurrentConfig.Language);
        }
    }

    public partial class GUIFont : GUISelector<GUIFontPrefab>
    {
        public bool HasValue => Value is not null;

        public ScalableFont? Value => Prefabs.ActivePrefab?.Font;

        public static implicit operator ScalableFont?(GUIFont reference) => reference.Value;

        public bool ForceUpperCase => Prefabs.ActivePrefab?.Font is { ForceUpperCase: true };

        public uint Size => Value?.Size ?? 0;

        private ScalableFont? GetFontForStr(LocalizedString str) => GetFontForStr(str.Value);
        
        public ScalableFont? GetFontForStr(string str) =>
            Prefabs.ActivePrefab?.GetFontForCategory(TextManager.GetSpeciallyHandledCategories(str));
        
        public void DrawString(SpriteBatch sb, LocalizedString text, Vector2 position, Color color, float rotation, Vector2 origin, Vector2 scale, SpriteEffects spriteEffects, float layerDepth)
        {
            DrawString(sb, text.Value, position, color, rotation, origin, scale, spriteEffects, layerDepth);
        }

        public void DrawString(SpriteBatch sb, string text, Vector2 position, Color color, float rotation, Vector2 origin, Vector2 scale, SpriteEffects spriteEffects, float layerDepth)
        {
            GetFontForStr(text)?.DrawString(sb, text, position, color, rotation, origin, scale, spriteEffects, layerDepth);
        }

        public void DrawString(SpriteBatch sb, LocalizedString text, Vector2 position, Color color, float rotation, Vector2 origin, float scale, SpriteEffects spriteEffects, float layerDepth, Alignment alignment = Alignment.TopLeft)
        {
            DrawString(sb, text.Value, position, color, rotation, origin, scale, spriteEffects, layerDepth, alignment);
        }

        public void DrawString(SpriteBatch sb, string text, Vector2 position, Color color, float rotation, Vector2 origin, float scale, SpriteEffects spriteEffects, float layerDepth, Alignment alignment = Alignment.TopLeft, ForceUpperCase forceUpperCase = Barotrauma.ForceUpperCase.Inherit)
        {
            GetFontForStr(text)?.DrawString(sb, text, position, color, rotation, origin, scale, spriteEffects, layerDepth, alignment, forceUpperCase);
        }

        public void DrawString(SpriteBatch sb, LocalizedString text, Vector2 position, Color color, ForceUpperCase forceUpperCase = Barotrauma.ForceUpperCase.Inherit, bool italics = false)
        {
            DrawString(sb, text.Value, position, color, forceUpperCase, italics);
        }

        public void DrawString(SpriteBatch sb, string text, Vector2 position, Color color, ForceUpperCase forceUpperCase = Barotrauma.ForceUpperCase.Inherit, bool italics = false)
        {
            GetFontForStr(text)?.DrawString(sb, text, position, color, forceUpperCase, italics);
        }

        public void DrawStringWithColors(SpriteBatch sb, string text, Vector2 position, Color color, float rotation, Vector2 origin, float scale, SpriteEffects spriteEffects, float layerDepth, in ImmutableArray<RichTextData>? richTextData, int rtdOffset = 0, Alignment alignment = Alignment.TopLeft, ForceUpperCase forceUpperCase = Barotrauma.ForceUpperCase.Inherit)
        {
            GetFontForStr(text)?.DrawStringWithColors(sb, text, position, color, rotation, origin, scale, spriteEffects, layerDepth, richTextData, rtdOffset, alignment, forceUpperCase);
        }

        public Vector2 MeasureString(LocalizedString str, bool removeExtraSpacing = false)
        {
            return GetFontForStr(str)?.MeasureString(str, removeExtraSpacing) ?? Vector2.Zero;
        }

        public Vector2 MeasureChar(char c)
        {
            return GetFontForStr($"{c}")?.MeasureChar(c) ?? Vector2.Zero;
        }

        public string WrapText(string text, float width)
            => GetFontForStr(text)?.WrapText(text, width) ?? text;
        
        public string WrapText(string text, float width, int requestCharPos, out Vector2 requestedCharPos)
        {
            requestedCharPos = default;
            return GetFontForStr(text)?.WrapText(text, width, requestCharPos, out requestedCharPos) ?? text;
        }

        public string WrapText(string text, float width, out Vector2[] allCharPositions)
        {
            var scalableFont = GetFontForStr(text);
            if (scalableFont != null)
            {
                return scalableFont.WrapText(text, width, out allCharPositions);
            }

            allCharPositions = Enumerable.Range(0, text.Length + 1).Select(_ => Vector2.Zero).ToArray();
            return text;
        }

        public float LineHeight => Value?.LineHeight ?? 0;
    }

    public class GUIColorPrefab : GUIPrefab
    {
        public readonly Color Color;

        public GUIColorPrefab(ContentXElement element, UIStyleFile file) : base(element, file)
        {
            Color = element.GetAttributeColor("color", Color.White);
        }

        public override void Dispose() { }
    }

    public class GUIColor : GUISelector<GUIColorPrefab>
    {
        private readonly Color fallbackColor;

        public GUIColor(string identifier, Color fallbackColor) : base(identifier) 
        { 
            this.fallbackColor = fallbackColor;
        }

        public Color Value
        {
            get
            {
                return Prefabs?.ActivePrefab?.Color ?? fallbackColor;
            }
        }

        public static implicit operator Color(GUIColor reference) => reference.Value;

        public static Color operator*(GUIColor value, float scale)
        {
            return value.Value * scale;
        }
    }

    public class GUISpritePrefab : GUIPrefab
    {
        public readonly UISprite Sprite;

        public GUISpritePrefab(ContentXElement element, UIStyleFile file) : base(element, file)
        {
            Sprite = new UISprite(element);
        }

        public override void Dispose()
        {
            Sprite.Sprite.Remove();
        }
    }

    public class GUISprite : GUISelector<GUISpritePrefab>
    {
        public GUISprite(string identifier) : base(identifier) { }

        public UISprite? Value
        {
            get
            {
                return Prefabs.ActivePrefab?.Sprite;
            }
        }

        public static implicit operator UISprite?(GUISprite reference) => reference.Value;

        public void Draw(SpriteBatch spriteBatch, RectangleF rect, Color color, SpriteEffects spriteEffects = SpriteEffects.None)
        {
            Value?.Draw(spriteBatch, rect, color, spriteEffects);
        }

        public void Draw(SpriteBatch spriteBatch, Rectangle rect, Color color, SpriteEffects spriteEffects = SpriteEffects.None)
        {
            Value?.Draw(spriteBatch, rect, color, spriteEffects);
        }
    }

    public class GUISpriteSheetPrefab : GUIPrefab
    {
        public readonly SpriteSheet SpriteSheet;
        
        public GUISpriteSheetPrefab(ContentXElement element, UIStyleFile file) : base(element, file)
        {
            SpriteSheet = new SpriteSheet(element);
        }

        public override void Dispose()
        {
            SpriteSheet.Remove();
        }
    }

    public class GUISpriteSheet : GUISelector<GUISpriteSheetPrefab>
    {
        public GUISpriteSheet(string identifier) : base(identifier) { }

        public SpriteSheet? Value
        {
            get
            {
                return Prefabs.ActivePrefab?.SpriteSheet;
            }
        }

        public int FrameCount => Value?.FrameCount ?? 1;
        public Point FrameSize => Value?.FrameSize ?? Point.Zero;

        public void Draw(ISpriteBatch spriteBatch, Vector2 pos, float rotate = 0, float scale = 1, SpriteEffects spriteEffects = SpriteEffects.None)
        {
            Value?.Draw(spriteBatch, pos, rotate, scale, spriteEffects);
        }

        public void Draw(ISpriteBatch spriteBatch, Vector2 pos, Color color, Vector2 origin, float rotate = 0, float scale = 1, SpriteEffects spriteEffects = SpriteEffects.None, float? depth = null)
        {
            Value?.Draw(spriteBatch, pos, color, origin, rotate, scale, spriteEffects, depth);
        }

        public void Draw(ISpriteBatch spriteBatch, int spriteIndex, Vector2 pos, Color color, Vector2 origin, float rotate, Vector2 scale, SpriteEffects spriteEffects = SpriteEffects.None, float? depth = null)
        {
            Value?.Draw(spriteBatch, spriteIndex, pos, color, origin, rotate, scale, spriteEffects, depth);
        }

        public static implicit operator SpriteSheet?(GUISpriteSheet reference) => reference.Value;
    }

    public class GUICursorPrefab : GUIPrefab
    {
        public readonly Sprite[] Sprites;

        public GUICursorPrefab(ContentXElement element, UIStyleFile file) : base(element, file)
        {
            Sprites = new Sprite[Enum.GetValues(typeof(CursorState)).Length];
            foreach (var subElement in element.Elements())
            {
                CursorState state = subElement.GetAttributeEnum("state", CursorState.Default);
                Sprites[(int)state] = new Sprite(subElement);
            }
        }

        public override void Dispose()
        {
            foreach (var sprite in Sprites)
            {
                sprite?.Remove();
            }
        }
    }

    public class GUICursor : GUISelector<GUICursorPrefab>
    {
        public GUICursor(string identifier) : base(identifier) { }

        public Sprite? this[CursorState k] => Prefabs.ActivePrefab?.Sprites[(int)k];
    }
}

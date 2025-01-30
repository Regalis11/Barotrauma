using Barotrauma.Extensions;
using System.Collections.Immutable;
using System.Linq;
using System.Reflection;

namespace Barotrauma
{
    // Implemented in shared so clients can share fonts with eachother.
    // Required for font selection by `ItemLabel`s.

    public static partial class GUIStyle
    {
        public static readonly ImmutableDictionary<Identifier, GUIFont> Fonts;

        static GUIStyle()
        {
            FieldInfo[] guiClassProperties = typeof(GUIStyle).GetFields(BindingFlags.Public | BindingFlags.Static);

            ImmutableDictionary<Identifier, T> getPropertiesOfType<T>() where T : class
            {
                return guiClassProperties
                    .Where(p => p.FieldType == typeof(T))
                    .Select(p => (p.Name.ToIdentifier(), p.GetValue(null) as T))
                    .ToImmutableDictionary();
            }

            Fonts = getPropertiesOfType<GUIFont>();
#if CLIENT
            Sprites = getPropertiesOfType<GUISprite>();
            SpriteSheets = getPropertiesOfType<GUISpriteSheet>();
            Colors = getPropertiesOfType<GUIColor>();
#endif
        }

        public static readonly GUIFont Font = new GUIFont("Font");
        public static readonly GUIFont UnscaledSmallFont = new GUIFont("UnscaledSmallFont");
        public static readonly GUIFont SmallFont = new GUIFont("SmallFont");
        public static readonly GUIFont LargeFont = new GUIFont("LargeFont");
        public static readonly GUIFont SubHeadingFont = new GUIFont("SubHeadingFont");
        public static readonly GUIFont DigitalFont = new GUIFont("DigitalFont");
        public static readonly GUIFont HotkeyFont = new GUIFont("HotkeyFont");
        public static readonly GUIFont MonospacedFont = new GUIFont("MonospacedFont");
    }
}
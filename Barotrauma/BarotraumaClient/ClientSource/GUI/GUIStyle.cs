using Barotrauma.Extensions;
using Microsoft.Xna.Framework;
using System;
using System.Collections.Immutable;
using System.Linq;

namespace Barotrauma
{
    public static partial class GUIStyle
    {
        public readonly static ImmutableDictionary<Identifier, GUISprite> Sprites;
        public readonly static ImmutableDictionary<Identifier, GUISpriteSheet> SpriteSheets;
        public readonly static ImmutableDictionary<Identifier, GUIColor> Colors;

        public readonly static PrefabCollection<GUIComponentStyle> ComponentStyles = new PrefabCollection<GUIComponentStyle>();

        public readonly static GUICursor CursorSprite = new GUICursor("Cursor");

        public readonly static GUISprite SubmarineLocationIcon = new GUISprite("SubmarineLocationIcon");
        public readonly static GUISprite Arrow = new GUISprite("Arrow");
        public readonly static GUISprite SpeechBubbleIcon = new GUISprite("SpeechBubbleIcon");
        public readonly static GUISprite SpeechBubbleIconSliced = new GUISprite("SpeechBubbleIconSliced");
        public readonly static GUISprite InteractionLabelBackground = new GUISprite("InteractionLabelBackground");
        public readonly static GUISprite BrokenIcon = new GUISprite("BrokenIcon");
        public readonly static GUISprite YouAreHereCircle = new GUISprite("YouAreHereCircle");
        
        public readonly static GUISprite SubLocationIcon = new GUISprite("SubLocationIcon");
        public readonly static GUISprite ShuttleIcon = new GUISprite("ShuttleIcon");
        public readonly static GUISprite WreckIcon = new GUISprite("WreckIcon");
        public readonly static GUISprite CaveIcon = new GUISprite("CaveIcon");
        public readonly static GUISprite OutpostIcon = new GUISprite("OutpostIcon");
        public readonly static GUISprite RuinIcon = new GUISprite("RuinIcon");
        public readonly static GUISprite EnemyIcon = new GUISprite("EnemyIcon");
        public readonly static GUISprite CorpseIcon = new GUISprite("CorpseIcon");
        public readonly static GUISprite BeaconIcon = new GUISprite("BeaconIcon");

        public readonly static GUISprite Radiation = new GUISprite("Radiation");
        public readonly static GUISpriteSheet RadiationAnimSpriteSheet = new GUISpriteSheet("RadiationAnimSpriteSheet");

        public readonly static GUISpriteSheet SavingIndicator = new GUISpriteSheet("SavingIndicator");
        public readonly static GUISpriteSheet GenericThrobber = new GUISpriteSheet("GenericThrobber");

        public readonly static GUISprite UIGlow = new GUISprite("UIGlow");
        public readonly static GUISprite TalentGlow = new GUISprite("TalentGlow");
        public readonly static GUISprite PingCircle = new GUISprite("PingCircle");
        public readonly static GUISprite UIGlowCircular = new GUISprite("UIGlowCircular");
        public readonly static GUISprite UIGlowSolidCircular = new GUISprite("UIGlowSolidCircular");
        public readonly static GUISprite UIThermalGlow = new GUISprite("UIGlowSolidCircular");
        public readonly static GUISprite ButtonPulse = new GUISprite("ButtonPulse");
        public readonly static GUISprite WalletPortraitBG = new GUISprite("WalletPortraitBG");
        public readonly static GUISprite CrewWalletIconSmall = new GUISprite("CrewWalletIconSmall");

        public readonly static GUISprite EndRoundButtonPulse = new GUISprite("EndRoundButtonPulse");

        public readonly static GUISpriteSheet FocusIndicator = new GUISpriteSheet("FocusIndicator");

        public readonly static GUISprite IconOverflowIndicator = new GUISprite("IconOverflowIndicator");

        /// <summary>
        /// General green color used for elements whose colors are set from code
        /// </summary>
        public readonly static GUIColor Green = new GUIColor("Green", new Color(154, 213, 163, 255));

        /// <summary>
        /// General red color used for elements whose colors are set from code
        /// </summary>
        public readonly static GUIColor Orange = new GUIColor("Orange", new Color(243, 162, 50, 255));

        /// <summary>
        /// General red color used for elements whose colors are set from code
        /// </summary>
        public readonly static GUIColor Red = new GUIColor("Red", new Color(245, 105, 105, 255));

        /// <summary>
        /// General blue color used for elements whose colors are set from code
        /// </summary>
        public readonly static GUIColor Blue = new GUIColor("Blue", new Color(126, 211, 224, 255));

        /// <summary>
        /// General yellow color used for elements whose colors are set from code
        /// </summary>
        public readonly static GUIColor Yellow = new GUIColor("Yellow", new Color(255, 255, 0, 255));

        /// <summary>
        /// Color to display the name of modded servers in the server list.
        /// </summary>
        public readonly static GUIColor ModdedServerColor = new GUIColor("ModdedServerColor", new Color(154, 185, 160, 255));

        public readonly static GUIColor ColorInventoryEmpty = new GUIColor("ColorInventoryEmpty", new Color(245, 105, 105, 255));
        public readonly static GUIColor ColorInventoryHalf = new GUIColor("ColorInventoryHalf", new Color(243, 162, 50, 255));
        public readonly static GUIColor ColorInventoryFull = new GUIColor("ColorInventoryFull", new Color(96, 222, 146, 255));
        public readonly static GUIColor ColorInventoryBackground = new GUIColor("ColorInventoryBackground", new Color(56, 56, 56, 255));
        public readonly static GUIColor ColorInventoryEmptyOverlay = new GUIColor("ColorInventoryEmptyOverlay", new Color(125, 125, 125, 255));

        public readonly static GUIColor TextColorNormal = new GUIColor("TextColorNormal", new Color(228, 217, 167, 255));
        public readonly static GUIColor TextColorBright = new GUIColor("TextColorBright", new Color(255, 255, 255, 255));
        public readonly static GUIColor TextColorDark = new GUIColor("TextColorDark", new Color(0, 0, 0, 230));
        public readonly static GUIColor TextColorDim = new GUIColor("TextColorDim", new Color(153, 153, 153, 153));

        public readonly static GUIColor ItemQualityColorPoor = new GUIColor("ItemQualityColorPoor", new Color(128, 128, 128, 255));
        public readonly static GUIColor ItemQualityColorNormal = new GUIColor("ItemQualityColorNormal", new Color(255, 255, 255, 255));
        public readonly static GUIColor ItemQualityColorGood = new GUIColor("ItemQualityColorGood", new Color(144, 238, 144, 255));
        public readonly static GUIColor ItemQualityColorExcellent = new GUIColor("ItemQualityColorExcellent", new Color(173, 216, 230, 255));
        public readonly static GUIColor ItemQualityColorMasterwork = new GUIColor("ItemQualityColorMasterwork", new Color(147, 112, 219, 255));
            
        public readonly static GUIColor ColorReputationVeryLow = new GUIColor("ColorReputationVeryLow", new Color(192, 60, 60, 255));
        public readonly static GUIColor ColorReputationLow = new GUIColor("ColorReputationLow", new Color(203, 145, 23, 255));
        public readonly static GUIColor ColorReputationNeutral = new GUIColor("ColorReputationNeutral", new Color(228, 217, 167, 255));
        public readonly static GUIColor ColorReputationHigh = new GUIColor("ColorReputationHigh", new Color(51, 152, 64, 255));
        public readonly static GUIColor ColorReputationVeryHigh = new GUIColor("ColorReputationVeryHigh", new Color(71, 160, 164, 255));
        
        public readonly static GUIColor InteractionLabelColor = new GUIColor("InteractionLabelColor", new Color(255, 255, 255, 255));
        public readonly static GUIColor InteractionLabelHoverColor = new GUIColor("InteractionLabelHoverColor", new Color(0, 255, 255, 255));

        // Inventory
        public readonly static GUIColor EquipmentSlotIconColor = new GUIColor("EquipmentSlotIconColor", new Color(99, 70, 64, 255));

        // Health HUD
        public readonly static GUIColor BuffColorLow = new GUIColor("BuffColorLow", new Color(66, 170, 73, 255));
        public readonly static GUIColor BuffColorMedium = new GUIColor("BuffColorMedium", new Color(110, 168, 118, 255));
        public readonly static GUIColor BuffColorHigh = new GUIColor("BuffColorHigh", new Color(154, 213, 163, 255));

        public readonly static GUIColor DebuffColorLow = new GUIColor("DebuffColorLow", new Color(243, 162, 50, 255));
        public readonly static GUIColor DebuffColorMedium = new GUIColor("DebuffColorMedium", new Color(155, 55, 55, 255));
        public readonly static GUIColor DebuffColorHigh = new GUIColor("DebuffColorHigh", new Color(228, 27, 27, 255));

        public readonly static GUIColor HealthBarColorLow = new GUIColor("HealthBarColorLow", new Color(255, 0, 0, 255));
        public readonly static GUIColor HealthBarColorMedium = new GUIColor("HealthBarColorMedium", new Color(255, 165, 0, 255));
        public readonly static GUIColor HealthBarColorHigh = new GUIColor("HealthBarColorHigh", new Color(78, 114, 88));
        public readonly static GUIColor HealthBarColorPoisoned = new GUIColor("HealthBarColorPoisoned", new Color(100, 150, 0, 255));

        private readonly static Point defaultItemFrameMargin = new Point(50, 56);

        public static Point ItemFrameMargin 
        {
            get 
            { 
                Point size = defaultItemFrameMargin.Multiply(GUI.SlicedSpriteScale);

                var style = GetComponentStyle("ItemUI"); 
                var sprite = style?.Sprites[GUIComponent.ComponentState.None].First();
                if (sprite != null)
                {
                    size.X = Math.Min(sprite.Slices[0].Width + sprite.Slices[2].Width, size.X);
                    size.Y = Math.Min(sprite.Slices[0].Height + sprite.Slices[6].Height, size.Y);
                }
                return size;
            } 
        }

        public static int ItemFrameTopBarHeight
        {
            get
            {
                var style = GetComponentStyle("ItemUI");
                var sprite = style?.Sprites[GUIComponent.ComponentState.None].First();
                return (int)Math.Min(sprite?.Slices[0].Height ?? 0, defaultItemFrameMargin.Y / 2 * GUI.SlicedSpriteScale);   
            }
        }

        public static Point ItemFrameOffset => new Point(0, 3).Multiply(GUI.SlicedSpriteScale);

        public static GUIComponentStyle GetComponentStyle(string styleName)
        {
            return GetComponentStyle(styleName.ToIdentifier());
        }

        public static GUIComponentStyle GetComponentStyle(Identifier identifier)
            => ComponentStyles.TryGet(identifier, out var style) ? style : null;

        public static void Apply(GUIComponent targetComponent, string styleName = "", GUIComponent parent = null)
        {
            Apply(targetComponent, styleName.ToIdentifier(), parent);
        }

        public static void Apply(GUIComponent targetComponent, Identifier styleName, GUIComponent parent = null)
        {
            GUIComponentStyle componentStyle;
            if (parent != null)
            {
                GUIComponentStyle parentStyle = parent.Style;

                if (parentStyle == null)
                {
                    Identifier parentStyleName = ReflectionUtils.GetTypeNameWithoutGenericArity(parent.GetType());
                    if (!ComponentStyles.ContainsKey(parentStyleName))
                    {
                        DebugConsole.ThrowError($"Couldn't find a GUI style \"{parentStyleName}\"");
                        return;
                    }
                    parentStyle = ComponentStyles[parentStyleName];
                }
                Identifier childStyleName = styleName.IsEmpty ? ReflectionUtils.GetTypeNameWithoutGenericArity(targetComponent.GetType()) : styleName;
                parentStyle.ChildStyles.TryGetValue(childStyleName, out componentStyle);
            }
            else
            {
                Identifier styleIdentifier = styleName.ToIdentifier();
                if (styleIdentifier == Identifier.Empty)
                {
                    styleIdentifier = ReflectionUtils.GetTypeNameWithoutGenericArity(targetComponent.GetType());
                }
                if (!ComponentStyles.ContainsKey(styleIdentifier))
                {
                    DebugConsole.ThrowError($"Couldn't find a GUI style \"{styleIdentifier}\"");
                    return;
                }
                componentStyle = ComponentStyles[styleIdentifier];
            }

            targetComponent.ApplyStyle(componentStyle);
        }

        public static GUIColor GetQualityColor(int quality)
        {
            switch (quality)
            {
                case 1:
                    return ItemQualityColorGood;
                case 2:
                    return ItemQualityColorExcellent;
                case 3:
                    return ItemQualityColorMasterwork;
                case -1:
                    return ItemQualityColorPoor;
                default:
                    return ItemQualityColorNormal;
            }
        }

        public static void RecalculateFonts()
        {
            foreach (var font in Fonts.Values)
            {
                font.Prefabs.ForEach(p => p.LoadFont());
            }
        }

        public static void RecalculateSizeRestrictions()
        {
            foreach (var componentStyle in ComponentStyles)
            {
                componentStyle.RefreshSize();
            }
        }
    }
}

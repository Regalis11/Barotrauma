using Barotrauma.Networking;
using Microsoft.Xna.Framework;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Barotrauma.Items.Components;
using Barotrauma.Extensions;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;

namespace Barotrauma
{
    sealed class SerializableEntityEditor : GUIComponent
    {
        private readonly int elementHeight;
        private readonly GUILayoutGroup layoutGroup;
        private readonly float inputFieldWidth = 0.5f;
        private readonly float largeInputFieldWidth = 0.8f;
#if DEBUG
        public static List<string> MissingLocalizations = new List<string>();
#endif

        public static bool LockEditing;
        public static bool PropertyChangesActive;
        public static DateTime NextCommandPush;
        public static Tuple<SerializableProperty, PropertyCommand> CommandBuffer;

        private bool isReadonly;
        public bool Readonly
        {
            get => isReadonly;
            set
            {
                foreach (var component in Fields.SelectMany(f => f.Value))
                {
                    switch (component)
                    {
                        case GUINumberInput numInput:
                            numInput.Readonly = value;
                            break;
                        case GUITextBox textBox:
                            textBox.Readonly = value;
                            break;
                        default:
                            component.Enabled = !value;
                            break;
                    }
                }
                isReadonly = value;
            }
        }

        private Action refresh;

        public int ContentHeight
        {
            get
            {
                if (layoutGroup.NeedsToRecalculate) layoutGroup.Recalculate();

                int spacing = layoutGroup.CountChildren == 0 ? 0 : ((layoutGroup.CountChildren - 1) * layoutGroup.AbsoluteSpacing);
                return spacing + layoutGroup.Children.Sum(c => c.RectTransform.NonScaledSize.Y);
            }
        }

        public int ContentCount
        {
            get { return layoutGroup.CountChildren; }
        }

        /// <summary>
        /// Holds the references to the input fields.
        /// </summary>
        public Dictionary<Identifier, GUIComponent[]> Fields { get; private set; } = new Dictionary<Identifier, GUIComponent[]>();

        public void UpdateValue(SerializableProperty property, object newValue, bool flash = true)
        {
            if (!Fields.TryGetValue(property.Name.ToIdentifier(), out GUIComponent[] fields))
            {
                DebugConsole.ThrowError($"No field for {property.Name} found!");
                return;
            }
            if (newValue is float f)
            {
                foreach (var field in fields)
                {
                    if (field is GUINumberInput numInput)
                    {
                        if (numInput.InputType == NumberType.Float)
                        {
                            numInput.FloatValue = f;
                            if (flash)
                            {
                                numInput.Flash(GUIStyle.Green);
                            }
                        }
                    }
                }
            }
            else if (newValue is int integer)
            {
                foreach (var field in fields)
                {
                    if (field is GUINumberInput numInput)
                    {
                        if (numInput.InputType == NumberType.Int)
                        {
                            numInput.IntValue = integer;
                            if (flash)
                            {
                                numInput.Flash(GUIStyle.Green);
                            }
                        }
                    }
                }
            }
            else if (newValue is bool b)
            {
                if (fields[0] is GUITickBox tickBox)
                {
                    tickBox.Selected = b;
                    if (flash)
                    {
                        tickBox.Flash(GUIStyle.Green);
                    }
                }
            }
            else if (newValue is string s)
            {
                if (fields[0] is GUITextBox textBox)
                {
                    textBox.Text = s;
                    if (flash)
                    {
                        textBox.Flash(GUIStyle.Green);
                    }
                }
            }
            else if (newValue.GetType().IsEnum)
            {
                if (fields[0] is GUIDropDown dropDown)
                {
                    dropDown.Select((int)newValue);
                    if (flash)
                    {
                        dropDown.Flash(GUIStyle.Green);
                    }
                }
            }
            else if (newValue is Vector2 v2)
            {
                for (int i = 0; i < fields.Length; i++)
                {
                    var field = fields[i];
                    if (field is GUINumberInput numInput)
                    {
                        if (numInput.InputType == NumberType.Float)
                        {
                            numInput.FloatValue = i == 0 ? v2.X : v2.Y;
                            if (flash)
                            {
                                numInput.Flash(GUIStyle.Green);
                            }
                        }
                    }
                }
            }
            else if (newValue is Vector3 v3)
            {
                for (int i = 0; i < fields.Length; i++)
                {
                    var field = fields[i];
                    if (field is GUINumberInput numInput)
                    {
                        if (numInput.InputType == NumberType.Float)
                        {
                            switch (i)
                            {
                                case 0:
                                    numInput.FloatValue = v3.X;
                                    break;
                                case 1:
                                    numInput.FloatValue = v3.Y;
                                    break;
                                case 2:
                                    numInput.FloatValue = v3.Z;
                                    break;
                            }
                            if (flash)
                            {
                                numInput.Flash(GUIStyle.Green);
                            }
                        }
                    }
                }
            }
            else if (newValue is Vector4 v4)
            {
                for (int i = 0; i < fields.Length; i++)
                {
                    var field = fields[i];
                    if (field is GUINumberInput numInput)
                    {
                        if (numInput.InputType == NumberType.Float)
                        {
                            switch (i)
                            {
                                case 0:
                                    numInput.FloatValue = v4.X;
                                    break;
                                case 1:
                                    numInput.FloatValue = v4.Y;
                                    break;
                                case 2:
                                    numInput.FloatValue = v4.Z;
                                    break;
                                case 3:
                                    numInput.FloatValue = v4.W;
                                    break;
                            }
                            if (flash)
                            {
                                numInput.Flash(GUIStyle.Green);
                            }
                        }
                    }
                }
            }
            else if (newValue is Color c)
            {
                for (int i = 0; i < fields.Length; i++)
                {
                    var field = fields[i];
                    if (field is GUINumberInput numInput)
                    {
                        if (numInput.InputType == NumberType.Int)
                        {
                            switch (i)
                            {
                                case 0:
                                    numInput.IntValue = c.R;
                                    break;
                                case 1:
                                    numInput.IntValue = c.G;
                                    break;
                                case 2:
                                    numInput.IntValue = c.B;
                                    break;
                                case 3:
                                    numInput.IntValue = c.A;
                                    break;
                            }
                            if (flash)
                            {
                                numInput.Flash(GUIStyle.Green);
                            }
                        }
                    }
                }

                if (fields.FirstOrDefault() is { } comp && comp.Parent?.Parent?.Parent is { } parent)
                {
                    if (parent.FindChild("colorpreview", true) is GUIButton preview)
                    {
                        preview.Color = preview.HoverColor = preview.PressedColor = preview.SelectedTextColor = c;
                    }
                }
            }
            else if (newValue is Rectangle r)
            {
                for (int i = 0; i < fields.Length; i++)
                {
                    var field = fields[i];
                    if (field is GUINumberInput numInput)
                    {
                        if (numInput.InputType == NumberType.Int)
                        {
                            switch (i)
                            {
                                case 0:
                                    numInput.IntValue = r.X;
                                    break;
                                case 1:
                                    numInput.IntValue = r.Y;
                                    break;
                                case 2:
                                    numInput.IntValue = r.Width;
                                    break;
                                case 3:
                                    numInput.IntValue = r.Height;
                                    break;
                            }
                            if (flash)
                            {
                                numInput.Flash(GUIStyle.Green);
                            }
                        }
                    }
                }
            }
            else if (newValue is string[] a)
            {
                for (int i = 0; i < fields.Length; i++)
                {
                    if (i >= a.Length) { break; }
                    if (fields[i] is GUITextBox textBox)
                    {
                        textBox.Text = a[i];
                        if (flash)
                        {
                            textBox.Flash(GUIStyle.Green);
                        }
                    }
                }
            }
        }

        public SerializableEntityEditor(RectTransform parent, ISerializableEntity entity, bool inGame, bool showName, string style = "", int elementHeight = 24, GUIFont titleFont = null)
            : this(parent, entity, inGame ?
                SerializableProperty.GetProperties<InGameEditable>(entity).Union(SerializableProperty.GetProperties<ConditionallyEditable>(entity).Where(p => p.GetAttribute<ConditionallyEditable>()?.IsEditable(entity) ?? false))
                : SerializableProperty.GetProperties<Editable>(entity).Where(p => p.GetAttribute<ConditionallyEditable>()?.IsEditable(entity) ?? true), showName, style, elementHeight, titleFont)
        {
        }

        public SerializableEntityEditor(RectTransform parent, ISerializableEntity entity, IEnumerable<SerializableProperty> properties, bool showName, string style = "", int elementHeight = 24, GUIFont titleFont = null)
            : base(style, new RectTransform(Vector2.One, parent))
        {
            elementHeight =  (int)(elementHeight * GUI.Scale);
            var tickBoxStyle = GUIStyle.GetComponentStyle("GUITickBox");
            var textBoxStyle = GUIStyle.GetComponentStyle("GUITextBox");
            var numberInputStyle = GUIStyle.GetComponentStyle("GUINumberInput");
            if (tickBoxStyle.Height.HasValue) { this.elementHeight = Math.Max(tickBoxStyle.Height.Value, this.elementHeight); }
            if (textBoxStyle.Height.HasValue) { this.elementHeight = Math.Max(textBoxStyle.Height.Value, this.elementHeight); }
            if (numberInputStyle.Height.HasValue) { this.elementHeight = Math.Max(numberInputStyle.Height.Value, this.elementHeight); }

            layoutGroup = new GUILayoutGroup(new RectTransform(Vector2.One, RectTransform)) { AbsoluteSpacing = (int)(5 * GUI.Scale) };
            if (showName)
            {
                new GUITextBlock(new RectTransform(new Point(layoutGroup.Rect.Width, this.elementHeight), layoutGroup.RectTransform, isFixedSize: true), entity.Name, font: titleFont ?? GUIStyle.Font)
                {
                    TextColor = Color.White,
                    Color = Color.Black
                };
            }

            List<Header> headers = new List<Header>()
            { 
                //"no header" comes first = properties under no header are listed first
                null
            };
            //check which header each property is under
            Dictionary<SerializableProperty, Header> propertyHeaders = new Dictionary<SerializableProperty, Header>();
            Header prevHeader = null;
            foreach (var property in properties)
            {
                var header = property.GetAttribute<Header>();
                if (header != null)
                {
                    prevHeader = header;
                    //Attribute.Equals is based on the equality of the fields,
                    //so in practice we treat identical headers split into different files/classes as the same header
                    if (!headers.Contains(header))
                    {
                        //collect headers into a list in the order they're encountered in
                        //(to keep them in the same order as they're defined in the code, as the dictionary is not in any particular order)                        
                        headers.Add(header);
                    }
                }
                propertyHeaders[property] = prevHeader;
            }

            prevHeader = null;
            foreach (Header header in headers)
            {
                //go through all the properties that belong under this header
                foreach (var property in properties)
                {
                    if (!Equals(propertyHeaders[property], header)) { continue; }
                    //don't create a header if the previous header has the same text as this one (= if we already created this header before)
                    if (header != null && !Equals(header, prevHeader))
                    {
                        new GUITextBlock(new RectTransform(new Point(Rect.Width, Math.Max(elementHeight, 26)), layoutGroup.RectTransform, isFixedSize: true),
                            header.Text, textColor: GUIStyle.TextColorBright, font: GUIStyle.SubHeadingFont);
                        prevHeader = header;
                    }
                    CreateNewField(property, entity);
                }
            }

            //scale the size of this component and the layout group to fit the children
            Recalculate();
        }

        public void AddCustomContent(GUIComponent component, int childIndex)
        {
            component.RectTransform.Parent = layoutGroup.RectTransform;
            component.RectTransform.RepositionChildInHierarchy(Math.Min(childIndex, layoutGroup.CountChildren - 1));
            layoutGroup.Recalculate();
            Recalculate();
        }

        public void RefreshValues()
        {
            refresh?.Invoke();
        }

        public void Recalculate() => RectTransform.Resize(new Point(RectTransform.NonScaledSize.X, ContentHeight));

        public GUIComponent CreateNewField(SerializableProperty property, ISerializableEntity entity)
        {
            object value = property.GetValue(entity);
            if (property.PropertyType == typeof(string) && value == null)
            {
                value = "";
            }

            Identifier propertyTag = $"{property.PropertyInfo.DeclaringType.Name}.{property.PropertyInfo.Name}".ToIdentifier();
            Identifier fallbackTag = property.PropertyInfo.Name.ToIdentifier();
            LocalizedString displayName = TextManager.Get(propertyTag, $"sp.{propertyTag}.name".ToIdentifier());
            if (displayName.IsNullOrEmpty())
            {
                Editable editable = property.GetAttribute<Editable>();
                if (editable != null && !string.IsNullOrEmpty(editable.FallBackTextTag))
                {
                    displayName = TextManager.Get(editable.FallBackTextTag);
                }
                else
                {
                    displayName = TextManager.Get(fallbackTag, $"sp.{fallbackTag}.name".ToIdentifier());
                }
            }

            if (displayName.IsNullOrEmpty())
            {
                displayName = property.Name.FormatCamelCaseWithSpaces();
#if DEBUG
                InGameEditable editable = property.GetAttribute<InGameEditable>();
                if (editable != null)
                {
                    if (!MissingLocalizations.Contains($"sp.{propertyTag}.name|{displayName}"))
                    {
                        DebugConsole.NewMessage("Missing Localization for property: " + propertyTag);
                        MissingLocalizations.Add($"sp.{propertyTag}.name|{displayName}");
                        MissingLocalizations.Add($"sp.{propertyTag}.description|{property.GetAttribute<Serialize>().Description}");
                    }
                }
#endif
            }

            LocalizedString toolTip = TextManager.Get($"sp.{propertyTag}.description");
            if (entity.GetType() != property.PropertyInfo.DeclaringType)
            {
                Identifier propertyTagForDerivedClass = $"{entity.GetType().Name}.{property.PropertyInfo.Name}".ToIdentifier();
                var toolTipForDerivedClass = TextManager.Get($"{propertyTagForDerivedClass}.description", $"sp.{propertyTagForDerivedClass}.description");
                if (!toolTipForDerivedClass.IsNullOrEmpty())
                {
                    toolTip = toolTipForDerivedClass;
                }
            }
            if (toolTip.IsNullOrEmpty())
            {
                toolTip = TextManager.Get($"{propertyTag}.description", $"{fallbackTag}.description", $"sp.{fallbackTag}.description");
            }
            if (toolTip.IsNullOrEmpty())
            {
                toolTip = property.GetAttribute<Serialize>().Description;
            }

            GUIComponent propertyField = null;
            if (value is bool boolVal)
            {
                propertyField = CreateBoolField(entity, property, boolVal, displayName, toolTip);
            }
            else if (value.GetType().IsEnum)
            {
                if (value.GetType().IsDefined(typeof(FlagsAttribute), inherit: false))
                {
                    propertyField = CreateEnumFlagField(entity, property, value, displayName, toolTip);
                }
                else
                {
                    propertyField = CreateEnumField(entity, property, value, displayName, toolTip);
                }
            }
            else if (value is int i)
            {
                propertyField = CreateIntField(entity, property, i, displayName, toolTip);
            }
            else if (value is float f)
            {
                propertyField = CreateFloatField(entity, property, f, displayName, toolTip);
            }
            else if (value is Point p)
            {
                propertyField = CreatePointField(entity, property, p, displayName, toolTip);
            }
            else if (value is Vector2 v2)
            {
                propertyField = CreateVector2Field(entity, property, v2, displayName, toolTip);
            }
            else if (value is Vector3 v3)
            {
                propertyField = CreateVector3Field(entity, property, v3, displayName, toolTip);
            }
            else if (value is Vector4 v4)
            {
                propertyField = CreateVector4Field(entity, property, v4, displayName, toolTip);
            }
            else if (value is Color c)
            {
                propertyField = CreateColorField(entity, property, c, displayName, toolTip);
            }
            else if (value is Rectangle r)
            {
                propertyField = CreateRectangleField(entity, property, r, displayName, toolTip);
            }
            else if(value is string[] a)
            {
                propertyField = CreateStringArrayField(entity, property, a, displayName, toolTip);
            }
            else if (value is string or Identifier)
            {
                propertyField = CreateStringField(entity, property, value.ToString(), displayName, toolTip);
            }
            else if (value is GUIFont font)
            {
                propertyField = CreateFontField(entity, property, font, displayName, toolTip);
            }
            return propertyField;
        }

        public GUIComponent CreateBoolField(ISerializableEntity entity, SerializableProperty property, bool value, LocalizedString displayName, LocalizedString toolTip)
            {
                var editableAttribute = property.GetAttribute<Editable>();
                if (editableAttribute.ReadOnly)
                {
                    var frame = new GUIFrame(new RectTransform(new Point(Rect.Width, Math.Max(elementHeight, 26)), layoutGroup.RectTransform, isFixedSize: true), color: Color.Transparent);
                    var label = new GUITextBlock(new RectTransform(new Vector2(1.0f - inputFieldWidth, 1), frame.RectTransform), displayName, font: GUIStyle.SmallFont)
                    {
                        ToolTip = toolTip
                    };
                    var valueField = new GUITextBlock(new RectTransform(new Vector2(inputFieldWidth, 1), frame.RectTransform, Anchor.TopRight), value.ToString())
                    {
                        ToolTip = toolTip,
                        Font = GUIStyle.SmallFont
                    };
                    return valueField;
                }
                else
                {
                    GUITickBox propertyTickBox = new GUITickBox(new RectTransform(new Point(Rect.Width, elementHeight), layoutGroup.RectTransform, isFixedSize: true), displayName)
                    {
                        Font = GUIStyle.SmallFont,
                        Enabled = !Readonly,
                        Selected = value,
                        ToolTip = toolTip,
                        OnSelected = (tickBox) =>
                        {
                            if (SetPropertyValue(property, entity, tickBox.Selected))
                            {
                                TrySendNetworkUpdate(entity, property);
                            }
                            // Ensure that the values stay in sync (could be that we force the value in the property accessor).
                            bool propertyValue = (bool)property.GetValue(entity);
                            if (tickBox.Selected != propertyValue)
                            {
                                tickBox.Selected = propertyValue;
                                tickBox.Flash(Color.Red);
                            }
                            return true;
                        }
                    };
                    refresh += () =>
                    {
                        propertyTickBox.Selected = (bool)property.GetValue(entity);
                    };
                    if (!Fields.ContainsKey(property.Name)) { Fields.Add(property.Name.ToIdentifier(), new GUIComponent[] { propertyTickBox }); }
                    return propertyTickBox;
                }
            }

        public GUIComponent CreateIntField(ISerializableEntity entity, SerializableProperty property, int value, LocalizedString displayName, LocalizedString toolTip)
        {
            var frame = new GUIFrame(new RectTransform(new Point(Rect.Width, Math.Max(elementHeight, 26)), layoutGroup.RectTransform, isFixedSize: true), color: Color.Transparent);
            var label = new GUITextBlock(new RectTransform(new Vector2(1.0f - inputFieldWidth, 1), frame.RectTransform), displayName, font: GUIStyle.SmallFont)
            {
                ToolTip = toolTip
            };
            var editableAttribute = property.GetAttribute<Editable>();
            GUIComponent field;
            if (editableAttribute.ReadOnly)
            {
                var numberInput = new GUITextBlock(new RectTransform(new Vector2(inputFieldWidth, 1), frame.RectTransform, Anchor.TopRight), value.ToString())
                {
                    ToolTip = toolTip,
                    Font = GUIStyle.SmallFont
                };
                field = numberInput;
            }
            else
            {
                var numberInput = new GUINumberInput(new RectTransform(new Vector2(inputFieldWidth, 1), frame.RectTransform, Anchor.TopRight), NumberType.Int)
                {
                    ToolTip = toolTip,
                    Font = GUIStyle.SmallFont,
                    Readonly = Readonly
                };
                numberInput.MinValueInt = editableAttribute.MinValueInt;
                numberInput.MaxValueInt = editableAttribute.MaxValueInt;
                numberInput.IntValue = value;
                numberInput.OnValueChanged += (numInput) =>
                {
                    if (SetPropertyValue(property, entity, numInput.IntValue))
                    {
                        TrySendNetworkUpdate(entity, property);
                    }
                };
                refresh += () =>
                {
                    if (!numberInput.TextBox.Selected) { numberInput.IntValue = (int)property.GetValue(entity); }                   
                };
                field = numberInput;
            }
            if (!Fields.ContainsKey(property.Name)) { Fields.Add(property.Name.ToIdentifier(), new GUIComponent[] { field }); }
            return frame;
        }

        public GUIComponent CreateFloatField(ISerializableEntity entity, SerializableProperty property, float value, LocalizedString displayName, LocalizedString toolTip)
        {
            var frame = new GUIFrame(new RectTransform(new Point(Rect.Width, Math.Max(elementHeight, 26)), layoutGroup.RectTransform, isFixedSize: true), color: Color.Transparent)
            {
                CanBeFocused = false
            };
            var label = new GUITextBlock(new RectTransform(new Vector2(1.0f - inputFieldWidth, 1), frame.RectTransform), displayName, font: GUIStyle.SmallFont)
            {
                ToolTip = toolTip
            };
            
            GUINumberInput numberInput = new GUINumberInput(new RectTransform(new Vector2(inputFieldWidth, 1), frame.RectTransform,
                Anchor.TopRight), NumberType.Float)
            {
                ToolTip = toolTip,
                Font = GUIStyle.SmallFont
            };
            var editableAttribute = property.GetAttribute<Editable>();
            numberInput.MinValueFloat = editableAttribute.MinValueFloat;
            numberInput.MaxValueFloat = editableAttribute.MaxValueFloat;
            numberInput.DecimalsToDisplay = editableAttribute.DecimalCount;
            numberInput.ValueStep = editableAttribute.ValueStep;
            numberInput.PlusMinusButtonVisibility = editableAttribute
                .ForceShowPlusMinusButtons ? GUINumberInput.ButtonVisibility.ForceVisible : default;
            numberInput.FloatValue = value;

            numberInput.OnValueChanged += numInput =>
            {
                if (SetPropertyValue(property, entity, numInput.FloatValue))
                {
                    TrySendNetworkUpdate(entity, property);
                }
            };

            HandleSetterValueTampering(numberInput, () => property.GetFloatValue(entity));
            refresh += () =>
            {
                if (!numberInput.TextBox.Selected) { numberInput.FloatValue = (float)property.GetValue(entity); }
            };
            if (!Fields.ContainsKey(property.Name)) { Fields.Add(property.Name.ToIdentifier(), new GUIComponent[] { numberInput }); }
            return frame;
        }

        private static void HandleSetterValueTampering(GUINumberInput numberInput, Func<float> getter)
        {
            // Lots of UI boilerplate to handle all(?) cases where the property's setter may be called
            // and modify the input value (e.g. rotation value wrapping)
            void HandleSetterModifyingInput(GUINumberInput numInput)
            {
                var inputFloatValue = numInput.FloatValue;
                var resultingFloatValue = getter();
                if (!MathUtils.NearlyEqual(resultingFloatValue, inputFloatValue))
                {
                    numInput.FloatValue = resultingFloatValue;
                }
            }
            bool HandleSetterModifyingInputOnButtonPressed() { HandleSetterModifyingInput(numberInput); return true; }
            bool HandleSetterModifyingInputOnButtonClicked(GUIButton _, object __) { HandleSetterModifyingInput(numberInput); return true; }

            numberInput.OnValueEntered += HandleSetterModifyingInput;
            numberInput.PlusButton.OnPressed += HandleSetterModifyingInputOnButtonPressed;
            numberInput.PlusButton.OnClicked += HandleSetterModifyingInputOnButtonClicked;
            numberInput.MinusButton.OnPressed += HandleSetterModifyingInputOnButtonPressed;
            numberInput.MinusButton.OnClicked += HandleSetterModifyingInputOnButtonClicked;
        }

        public GUIComponent CreateEnumField(ISerializableEntity entity, SerializableProperty property, object value, LocalizedString displayName, LocalizedString toolTip)
        {
            var frame = new GUIFrame(new RectTransform(new Point(Rect.Width, elementHeight), layoutGroup.RectTransform, isFixedSize: true), color: Color.Transparent);
            var label = new GUITextBlock(new RectTransform(new Vector2(1.0f - inputFieldWidth, 1), frame.RectTransform), displayName, font: GUIStyle.SmallFont)
            {
                ToolTip = toolTip
            };
            GUIDropDown enumDropDown = new GUIDropDown(new RectTransform(new Vector2(inputFieldWidth, 1), frame.RectTransform, Anchor.TopRight),
                elementCount: Enum.GetValues(value.GetType()).Length)
            {
                ToolTip = toolTip
            };
            foreach (object enumValue in Enum.GetValues(value.GetType()))
            {
                enumDropDown.AddItem(enumValue.ToString(), enumValue);
            }
            enumDropDown.SelectItem(value);
            enumDropDown.OnSelected += (selected, val) =>
            {
                if (SetPropertyValue(property, entity, val))
                {
                    TrySendNetworkUpdate(entity, property);
                }
                return true;
            };
            refresh += () =>
            {
                if (!enumDropDown.Dropped) { enumDropDown.SelectItem(property.GetValue(entity)); }
            };
            if (!Fields.ContainsKey(property.Name)) { Fields.Add(property.Name.ToIdentifier(), new GUIComponent[] { enumDropDown }); }
            return frame;
        }

        public GUIComponent CreateEnumFlagField(ISerializableEntity entity, SerializableProperty property, object value, LocalizedString displayName, LocalizedString toolTip)
        {
            var frame = new GUIFrame(new RectTransform(new Point(Rect.Width, elementHeight), layoutGroup.RectTransform, isFixedSize: true), color: Color.Transparent);
            var label = new GUITextBlock(new RectTransform(new Vector2(1.0f - inputFieldWidth, 1), frame.RectTransform), displayName, font: GUIStyle.SmallFont)
            {
                ToolTip = toolTip
            };
            GUIDropDown enumDropDown = new GUIDropDown(new RectTransform(new Vector2(inputFieldWidth, 1), frame.RectTransform, Anchor.TopRight),
                elementCount: Enum.GetValues(value.GetType()).Length, selectMultiple: true)
            {
                ToolTip = toolTip
            };

            bool isFlagsAttribute = value.GetType().IsDefined(typeof(FlagsAttribute), false);

            bool hasNoneOption = false;
            foreach (object enumValue in Enum.GetValues(value.GetType()))
            {
                if (isFlagsAttribute && !MathHelper.IsPowerOfTwo((int)enumValue)) { continue; }
                hasNoneOption |= (int)enumValue == 0;
                enumDropDown.AddItem(enumValue.ToString(), enumValue);
                if (((int)enumValue != 0 || (int)value == 0) && ((Enum)value).HasFlag((Enum)enumValue))
                {
                    enumDropDown.SelectItem(enumValue);
                }
            }
            enumDropDown.MustSelectAtLeastOne = !hasNoneOption;
            enumDropDown.AfterSelected += (selected, val) =>
            {
                if (SetPropertyValue(property, entity, string.Join(", ", enumDropDown.SelectedDataMultiple.Select(d => d.ToString()))))
                {
                    TrySendNetworkUpdate(entity, property);
                }
                return true;
            };

            if (!Fields.ContainsKey(property.Name)) { Fields.Add(property.Name.ToIdentifier(), new GUIComponent[] { enumDropDown }); }
            return frame;
        }

        public GUIComponent CreateStringField(ISerializableEntity entity, SerializableProperty property, string value, LocalizedString displayName, LocalizedString toolTip)
        {
            bool isItemTagBox = IsItemTagBox(entity, property.Name, out Item it);
            var mainFrame = new GUILayoutGroup(new RectTransform(new Point(Rect.Width, isItemTagBox ? elementHeight * 2 : elementHeight), layoutGroup.RectTransform, isFixedSize: true));

            var frame = new GUILayoutGroup(new RectTransform(isItemTagBox ? new Vector2(1f, 0.5f) : Vector2.One, mainFrame.RectTransform), isHorizontal: true, childAnchor: Anchor.CenterLeft)
            {
                Stretch = true
            };

            var label = new GUITextBlock(new RectTransform(new Vector2(1.0f - inputFieldWidth, 1), frame.RectTransform), displayName, font: GUIStyle.SmallFont, textAlignment: Alignment.Left)
            {
                ToolTip = toolTip
            };

            Identifier translationTextTag = property.GetAttribute<Serialize>()?.TranslationTextTag ?? Identifier.Empty;
            const float browseButtonWidth = 0.1f;
            var editableAttribute = property.GetAttribute<Editable>();
            float textBoxWidth = inputFieldWidth;
            if (!translationTextTag.IsEmpty || isItemTagBox) { textBoxWidth -= browseButtonWidth; }
            GUITextBox propertyBox = new GUITextBox(new RectTransform(new Vector2(textBoxWidth, 1), frame.RectTransform))
            {
                Enabled = editableAttribute != null && !editableAttribute.ReadOnly,
                Readonly = Readonly,
                ToolTip = toolTip,
                Font = GUIStyle.SmallFont,
                Text = StripPrefabTags(value),
                OverflowClip = true,
            };
            if (editableAttribute != null && editableAttribute.MaxLength > 0)
            {
                propertyBox.MaxTextLength = editableAttribute.MaxLength;
            }

            HashSet<MapEntity> editedEntities = new HashSet<MapEntity>();
            propertyBox.OnTextChanged += (textBox, text) =>
            {
                foreach (var entity in MapEntity.SelectedList)
                {
                    editedEntities.Add(entity);
                }
                return true;
            };            
            propertyBox.OnDeselected += (textBox, keys) => OnApply(textBox);
            propertyBox.OnEnterPressed += (box, text) => OnApply(box);
            refresh += () =>
            {
                if (propertyBox.Selected) { return; }
                propertyBox.Text = StripPrefabTags(property.GetValue(entity)?.ToString());
            };

            bool OnApply(GUITextBox textBox)
            {
                List<MapEntity> prevSelected = MapEntity.SelectedList.ToList();
                //reselect the entities that were selected during editing
                //otherwise multi-editing won't work when we deselect the entities with unapplied changes in the textbox
                if (editedEntities.Count > 1)
                {
                    foreach (var entity in editedEntities)
                    { 
                        MapEntity.SelectedList.Add(entity);
                    }
                }
                if (SetPropertyValue(property, entity, textBox.Text))
                {
                    TrySendNetworkUpdate(entity, property);
                    textBox.Text = StripPrefabTags(property.GetValue(entity).ToString());
                    textBox.Flash(GUIStyle.Green, flashDuration: 1f);
                }
                //restore the entities that were selected before applying
                MapEntity.SelectedList.Clear();
                foreach (var entity in prevSelected)
                {
                    MapEntity.SelectedList.Add(entity);
                }
                return true;
            }
            
            if (!translationTextTag.IsEmpty)
            {
                new GUIButton(new RectTransform(new Vector2(browseButtonWidth, 1), frame.RectTransform, Anchor.TopRight), "...", style: "GUIButtonSmall")
                {
                    OnClicked = (bt, userData) => { CreateTextPicker(translationTextTag.Value, entity, property, propertyBox); return true; }
                };
                propertyBox.OnTextChanged += (tb, text) =>
                {
                    LocalizedString translatedText = TextManager.Get(text);
                    if (translatedText.IsNullOrEmpty())
                    {
                        propertyBox.TextColor = Color.Gray;
                        propertyBox.ToolTip = TextManager.GetWithVariable("StringPropertyCannotTranslate", "[tag]", text ?? string.Empty);
                    }
                    else
                    {
                        propertyBox.TextColor = GUIStyle.Green;
                        propertyBox.ToolTip = TextManager.GetWithVariable("StringPropertyTranslate", "[translation]", translatedText);
                    }
                    return true;
                };
                propertyBox.Text = value;
            }

            if (isItemTagBox)
            {
                // create prefab tag box
                var prefabFrame = new GUILayoutGroup(new RectTransform(new Vector2(1f, 0.5f), mainFrame.RectTransform), isHorizontal: true, childAnchor: Anchor.CenterLeft)
                {
                    Stretch = true
                };

                new GUITextBlock(new RectTransform(new Vector2(1.0f - inputFieldWidth, 1), prefabFrame.RectTransform), TextManager.Get("predefinedtags.name"), font: GUIStyle.SmallFont, textAlignment: Alignment.Left)
                {
                    ToolTip = TextManager.Get("predefinedtags.description")
                };

                new GUITextBox(new RectTransform(new Vector2(inputFieldWidth, 1), prefabFrame.RectTransform), createPenIcon: false)
                {
                    Readonly = true,
                    Font = GUIStyle.SmallFont,
                    Text = GetPrefabTags(it),
                    OverflowClip = true,
                    ToolTip = TextManager.Get("predefinedtags.description")
                };

                // add container tag popup button to the modifiable tag box
                new GUIButton(new RectTransform(new Vector2(browseButtonWidth, 1), frame.RectTransform, Anchor.TopRight), "...")
                {
                    OnClicked = (_, _) => { it.CreateContainerTagPicker(propertyBox); return true; }
                };  
            }

            frame.RectTransform.MinSize = new Point(0, frame.RectTransform.Children.Max(c => c.MinSize.Y));
            if (!Fields.ContainsKey(property.Name)) { Fields.Add(property.Name.ToIdentifier(), new GUIComponent[] { propertyBox }); }
            return frame;

            static bool IsItemTagBox(ISerializableEntity entity, string propertyName, [NotNullWhen(true)] out Item it)
            {
                if (entity is Item item && propertyName.Equals(nameof(Item.Tags), StringComparison.OrdinalIgnoreCase))
                {
                    it = item;
                    return true;
                }
                it = null;
                return false;
            }

            string StripPrefabTags(string text)
            {
                if (!isItemTagBox) { return text; }

                string prefabTags = GetPrefabTags(it);
                if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(prefabTags)) { return text; }

                string[] splitTags = text.Split(',');
                return string.Join(',', splitTags.Where(t => !it.Prefab.Tags.Contains(t)));
            }

            static string GetPrefabTags(Item it) => string.Join(',', it.Prefab.Tags);
        }

        public GUIComponent CreatePointField(ISerializableEntity entity, SerializableProperty property, Point value, LocalizedString displayName, LocalizedString toolTip)
        {
            var frame = new GUIFrame(new RectTransform(new Point(Rect.Width, Math.Max(elementHeight, 26)), layoutGroup.RectTransform, isFixedSize: true), color: Color.Transparent);
            var label = new GUITextBlock(new RectTransform(new Vector2(1.0f - inputFieldWidth, 1), frame.RectTransform), displayName, font: GUIStyle.SmallFont)
            {
                ToolTip = toolTip
            };
            var inputArea = new GUILayoutGroup(new RectTransform(new Vector2(inputFieldWidth, 1), frame.RectTransform, Anchor.TopRight), isHorizontal: true, childAnchor: Anchor.CenterRight)
            {
                Stretch = true,
                RelativeSpacing = 0.05f
            };
            var editableAttribute = property.GetAttribute<Editable>();
            var fields = new GUIComponent[2];
            for (int i = 1; i >= 0; i--)
            {
                var element = new GUIFrame(new RectTransform(new Vector2(0.45f, 1), inputArea.RectTransform), style: null);

                LocalizedString componentLabel = GUI.VectorComponentLabels[i];
                if (editableAttribute.VectorComponentLabels != null && i < editableAttribute.VectorComponentLabels.Length)
                {
                    componentLabel = TextManager.Get(editableAttribute.VectorComponentLabels[i]);
                }

                new GUITextBlock(new RectTransform(new Vector2(0.3f, 1), element.RectTransform, Anchor.CenterLeft), componentLabel, font: GUIStyle.SmallFont, textAlignment: Alignment.Center);
                GUINumberInput numberInput = new GUINumberInput(new RectTransform(new Vector2(0.7f, 1), element.RectTransform, Anchor.CenterRight),
                    NumberType.Int)
                {
                    Font = GUIStyle.SmallFont
                };

                if (i == 0)
                    numberInput.IntValue = value.X;
                else
                    numberInput.IntValue = value.Y;

                numberInput.MinValueInt = editableAttribute.MinValueInt;
                numberInput.MaxValueInt = editableAttribute.MaxValueInt;

                int comp = i;
                numberInput.OnValueChanged += (numInput) =>
                {
                    Point newVal = (Point)property.GetValue(entity);
                    if (comp == 0)
                        newVal.X = numInput.IntValue;
                    else
                        newVal.Y = numInput.IntValue;

                    if (SetPropertyValue(property, entity, newVal))
                    {
                        TrySendNetworkUpdate(entity, property);
                    }
                };
                fields[i] = numberInput;
            }
            refresh += () =>
            {
                if (!fields.Any(f => ((GUINumberInput)f).TextBox.Selected)) 
                { 
                    Point value = (Point)property.GetValue(entity);
                    ((GUINumberInput)fields[0]).IntValue = value.X;
                    ((GUINumberInput)fields[1]).IntValue = value.Y;
                }
            };
            frame.RectTransform.MinSize = new Point(0, frame.RectTransform.Children.Max(c => c.MinSize.Y));
            if (!Fields.ContainsKey(property.Name)) { Fields.Add(property.Name.ToIdentifier(), fields); }
            return frame;
        }

        public GUIComponent CreateVector2Field(ISerializableEntity entity, SerializableProperty property, Vector2 value, LocalizedString displayName, LocalizedString toolTip)
        {
            var frame = new GUIFrame(new RectTransform(new Point(Rect.Width, Math.Max(elementHeight, 26)), layoutGroup.RectTransform, isFixedSize: true), color: Color.Transparent);
            var label = new GUITextBlock(new RectTransform(new Vector2(1.0f - inputFieldWidth, 1), frame.RectTransform), displayName, font: GUIStyle.SmallFont)
            {
                ToolTip = toolTip
            };
            var inputArea = new GUILayoutGroup(new RectTransform(new Vector2(inputFieldWidth, 1), frame.RectTransform, Anchor.TopRight), isHorizontal: true, childAnchor: Anchor.CenterRight)
            {
                Stretch = true,
                RelativeSpacing = 0.05f
            };
            var editableAttribute = property.GetAttribute<Editable>();
            var fields = new GUIComponent[2];
            for (int i = 1; i >= 0; i--)
            {
                var element = new GUIFrame(new RectTransform(new Vector2(0.45f, 1), inputArea.RectTransform), style: null);

                LocalizedString componentLabel = GUI.VectorComponentLabels[i];
                if (editableAttribute.VectorComponentLabels != null && i < editableAttribute.VectorComponentLabels.Length)
                {
                    componentLabel = TextManager.Get(editableAttribute.VectorComponentLabels[i]);
                }
                new GUITextBlock(new RectTransform(new Vector2(0.3f, 1), element.RectTransform, Anchor.CenterLeft), componentLabel, font: GUIStyle.SmallFont, textAlignment: Alignment.Center);
                GUINumberInput numberInput = new GUINumberInput(new RectTransform(new Vector2(0.7f, 1), element.RectTransform, Anchor.CenterRight),
                    NumberType.Float)
                {
                    Font = GUIStyle.SmallFont
                };

                numberInput.MinValueFloat = editableAttribute.MinValueFloat;
                numberInput.MaxValueFloat = editableAttribute.MaxValueFloat;
                numberInput.DecimalsToDisplay = editableAttribute.DecimalCount;
                numberInput.ValueStep = editableAttribute.ValueStep;
                numberInput.PlusMinusButtonVisibility = editableAttribute
                    .ForceShowPlusMinusButtons ? GUINumberInput.ButtonVisibility.ForceVisible : default;

                numberInput.FloatValue = i == 0 ? value.X : value.Y;

                int comp = i;
                numberInput.OnValueChanged += (numInput) =>
                {
                    Vector2 newVal = (Vector2)property.GetValue(entity);
                    if (comp == 0)
                    {
                        newVal.X = numInput.FloatValue;
                    }
                    else
                    {
                        newVal.Y = numInput.FloatValue;
                    }

                    if (SetPropertyValue(property, entity, newVal))
                    {
                        TrySendNetworkUpdate(entity, property);
                    }
                };
                HandleSetterValueTampering(numberInput, () =>
                {
                    Vector2 currVal = (Vector2)property.GetValue(entity);
                    return comp == 0 ? currVal.X : currVal.Y;
                });
                fields[i] = numberInput;
            }
            refresh += () =>
            {
                if (!fields.Any(f => ((GUINumberInput)f).TextBox.Selected))
                {
                    Vector2 value = (Vector2)property.GetValue(entity);
                    ((GUINumberInput)fields[0]).FloatValue = value.X;
                    ((GUINumberInput)fields[1]).FloatValue = value.Y;
                }
            };
            frame.RectTransform.MinSize = new Point(0, frame.RectTransform.Children.Max(c => c.MinSize.Y));
            if (!Fields.ContainsKey(property.Name)) { Fields.Add(property.Name.ToIdentifier(), fields); }
            return frame;
        }

        public GUIComponent CreateVector3Field(ISerializableEntity entity, SerializableProperty property, Vector3 value, LocalizedString displayName, LocalizedString toolTip)
        {
            var frame = new GUIFrame(new RectTransform(new Point(Rect.Width, Math.Max(elementHeight, 26)), layoutGroup.RectTransform, isFixedSize: true), color: Color.Transparent);
            var label = new GUITextBlock(new RectTransform(new Vector2(1.0f - largeInputFieldWidth, 1), frame.RectTransform), displayName, font: GUIStyle.SmallFont)
            {
                ToolTip = toolTip
            };
            var inputArea = new GUILayoutGroup(new RectTransform(new Vector2(largeInputFieldWidth, 1), frame.RectTransform, Anchor.TopRight), isHorizontal: true, childAnchor: Anchor.CenterRight)
            {
                Stretch = true,
                RelativeSpacing = 0.03f
            };
            var editableAttribute = property.GetAttribute<Editable>();
            var fields = new GUIComponent[3];
            for (int i = 2; i >= 0; i--)
            {
                var element = new GUIFrame(new RectTransform(new Vector2(0.33f, 1), inputArea.RectTransform), style: null);

                LocalizedString componentLabel = GUI.VectorComponentLabels[i];
                if (editableAttribute.VectorComponentLabels != null && i < editableAttribute.VectorComponentLabels.Length)
                {
                    componentLabel = TextManager.Get(editableAttribute.VectorComponentLabels[i]);
                }

                new GUITextBlock(new RectTransform(new Vector2(0.3f, 1), element.RectTransform, Anchor.CenterLeft), componentLabel, font: GUIStyle.SmallFont, textAlignment: Alignment.Center);
                GUINumberInput numberInput = new GUINumberInput(new RectTransform(new Vector2(0.7f, 1), element.RectTransform, Anchor.CenterRight),
                    NumberType.Float)
                {
                    Font = GUIStyle.SmallFont
                };

                numberInput.MinValueFloat = editableAttribute.MinValueFloat;
                numberInput.MaxValueFloat = editableAttribute.MaxValueFloat;
                numberInput.DecimalsToDisplay = editableAttribute.DecimalCount;
                numberInput.ValueStep = editableAttribute.ValueStep;

                if (i == 0)
                    numberInput.FloatValue = value.X;
                else if (i == 1)
                    numberInput.FloatValue = value.Y;
                else if (i == 2)
                    numberInput.FloatValue = value.Z;

                int comp = i;
                numberInput.OnValueChanged += (numInput) =>
                {
                    Vector3 newVal = (Vector3)property.GetValue(entity);
                    if (comp == 0)
                        newVal.X = numInput.FloatValue;
                    else if (comp == 1)
                        newVal.Y = numInput.FloatValue;
                    else
                        newVal.Z = numInput.FloatValue;

                    if (SetPropertyValue(property, entity, newVal))
                    {
                        TrySendNetworkUpdate(entity, property);
                    }
                };
                fields[i] = numberInput;
            }
            refresh += () =>
            {
                if (!fields.Any(f => ((GUINumberInput)f).TextBox.Selected))
                {
                    Vector3 value = (Vector3)property.GetValue(entity);
                    ((GUINumberInput)fields[0]).FloatValue = value.X;
                    ((GUINumberInput)fields[1]).FloatValue = value.Y;
                    ((GUINumberInput)fields[2]).FloatValue = value.Z;
                }
            };
            frame.RectTransform.MinSize = new Point(0, frame.RectTransform.Children.Max(c => c.MinSize.Y));
            if (!Fields.ContainsKey(property.Name)) { Fields.Add(property.Name.ToIdentifier(), fields); }
            return frame;
        }

        public GUIComponent CreateVector4Field(ISerializableEntity entity, SerializableProperty property, Vector4 value, LocalizedString displayName, LocalizedString toolTip)
        {
            var frame = new GUIFrame(new RectTransform(new Point(Rect.Width, Math.Max(elementHeight, 26)), layoutGroup.RectTransform, isFixedSize: true), color: Color.Transparent);
            var label = new GUITextBlock(new RectTransform(new Vector2(1.0f - largeInputFieldWidth, 1), frame.RectTransform), displayName, font: GUIStyle.SmallFont)
            {
                ToolTip = toolTip
            };
            var editableAttribute = property.GetAttribute<Editable>();
            var fields = new GUIComponent[4];
            var inputArea = new GUILayoutGroup(new RectTransform(new Vector2(largeInputFieldWidth, 1), frame.RectTransform, Anchor.TopRight), isHorizontal: true, childAnchor: Anchor.CenterRight)
            {
                Stretch = true,
                RelativeSpacing = 0.01f
            };
            for (int i = 3; i >= 0; i--)
            {
                var element = new GUIFrame(new RectTransform(new Vector2(0.22f, 1), inputArea.RectTransform) { MinSize = new Point(50, 0), MaxSize = new Point(150, 50) }, style: null);

                LocalizedString componentLabel = GUI.VectorComponentLabels[i];
                if (editableAttribute.VectorComponentLabels != null && i < editableAttribute.VectorComponentLabels.Length)
                {
                    componentLabel = TextManager.Get(editableAttribute.VectorComponentLabels[i]);
                }

                new GUITextBlock(new RectTransform(new Vector2(0.3f, 1), element.RectTransform, Anchor.CenterLeft), componentLabel, font: GUIStyle.SmallFont, textAlignment: Alignment.Center);
                GUINumberInput numberInput = new GUINumberInput(new RectTransform(new Vector2(0.7f, 1), element.RectTransform, Anchor.CenterRight),
                    NumberType.Float)
                {
                    Font = GUIStyle.SmallFont
                };

                numberInput.MinValueFloat = editableAttribute.MinValueFloat;
                numberInput.MaxValueFloat = editableAttribute.MaxValueFloat;
                numberInput.DecimalsToDisplay = editableAttribute.DecimalCount;
                numberInput.ValueStep = editableAttribute.ValueStep;

                if (i == 0)
                    numberInput.FloatValue = value.X;
                else if (i == 1)
                    numberInput.FloatValue = value.Y;
                else if (i == 2)
                    numberInput.FloatValue = value.Z;
                else
                    numberInput.FloatValue = value.W;

                int comp = i;
                numberInput.OnValueChanged += (numInput) =>
                {
                    Vector4 newVal = (Vector4)property.GetValue(entity);
                    if (comp == 0)
                        newVal.X = numInput.FloatValue;
                    else if (comp == 1)
                        newVal.Y = numInput.FloatValue;
                    else if (comp == 2)
                        newVal.Z = numInput.FloatValue;
                    else
                        newVal.W = numInput.FloatValue;

                    if (SetPropertyValue(property, entity, newVal))
                    {
                        TrySendNetworkUpdate(entity, property);
                    }
                };
                fields[i] = numberInput;
            }
            refresh += () =>
            {
                if (!fields.Any(f => ((GUINumberInput)f).TextBox.Selected))
                {
                    Vector4 value = (Vector4)property.GetValue(entity);
                    ((GUINumberInput)fields[0]).FloatValue = value.X;
                    ((GUINumberInput)fields[1]).FloatValue = value.Y;
                    ((GUINumberInput)fields[2]).FloatValue = value.Z;
                    ((GUINumberInput)fields[3]).FloatValue = value.W;
                }
            };
            frame.RectTransform.MinSize = new Point(0, frame.RectTransform.Children.Max(c => c.MinSize.Y));
            if (!Fields.ContainsKey(property.Name)) { Fields.Add(property.Name.ToIdentifier(), fields); }
            return frame;
        }

        public GUIComponent CreateColorField(ISerializableEntity entity, SerializableProperty property, Color value, LocalizedString displayName, LocalizedString toolTip)
        {
            var frame = new GUIFrame(new RectTransform(new Point(Rect.Width, Math.Max(elementHeight, 26)), layoutGroup.RectTransform, isFixedSize: true), color: Color.Transparent);
            var label = new GUITextBlock(new RectTransform(new Vector2(1.0f - largeInputFieldWidth, 1), frame.RectTransform) { MinSize = new Point(80, 26) }, displayName, font: GUIStyle.SmallFont)
            {
                ToolTip = displayName + '\n' + toolTip
            };
            label.Text = ToolBox.LimitString(label.Text, label.Font, label.Rect.Width);
            var colorBoxBack = new GUIFrame(new RectTransform(new Vector2(0.04f, 1), frame.RectTransform)
            {
                AbsoluteOffset = new Point(label.Rect.Width, 0)
            }, color: Color.Black, style: null);
            var colorBox = new GUIButton(new RectTransform(new Vector2(largeInputFieldWidth, 0.9f), colorBoxBack.RectTransform, Anchor.Center), style: null)
            {
                UserData = "colorpreview",
                OnClicked = (component, data) =>
                {
                    if (!SubEditorScreen.IsSubEditor()) { return false; }
                    if (GUIMessageBox.MessageBoxes.Any(msgBox => msgBox is GUIMessageBox { Closed: false, UserData: "colorpicker" })) { return false; }

                    GUIMessageBox msgBox = SubEditorScreen.CreatePropertyColorPicker((Color) property.GetValue(entity), property, entity);
                    return true;
                }
            };
            var inputArea = new GUILayoutGroup(new RectTransform(new Vector2(Math.Max((frame.Rect.Width - label.Rect.Width - colorBoxBack.Rect.Width) / (float)frame.Rect.Width, 0.5f), 1), frame.RectTransform, Anchor.TopRight), isHorizontal: true, childAnchor: Anchor.CenterRight)
            {
                Stretch = true,
                RelativeSpacing = 0.001f
            };
            var fields = new GUIComponent[4];
            for (int i = 3; i >= 0; i--)
            {
                var element = new GUILayoutGroup(new RectTransform(new Vector2(0.18f, 1), inputArea.RectTransform), isHorizontal: true)
                {
                    Stretch = true
                };
                new GUITextBlock(new RectTransform(new Vector2(0.2f, 1), element.RectTransform, Anchor.CenterLeft) { MinSize = new Point(15, 0) }, GUI.ColorComponentLabels[i], font: GUIStyle.SmallFont, textAlignment: Alignment.Center);
                GUINumberInput numberInput = new GUINumberInput(new RectTransform(new Vector2(0.7f, 1), element.RectTransform, Anchor.CenterRight),
                    NumberType.Int)
                {
                    Font = GUIStyle.SmallFont
                };
                numberInput.MinValueInt = 0;
                numberInput.MaxValueInt = 255;

                if (i == 0)
                    numberInput.IntValue = value.R;
                else if (i == 1)
                    numberInput.IntValue = value.G;
                else if (i == 2)
                    numberInput.IntValue = value.B;
                else
                    numberInput.IntValue = value.A;

                numberInput.Font = GUIStyle.SmallFont;

                int comp = i;
                numberInput.OnValueChanged += (numInput) =>
                {
                    Color newVal = (Color)property.GetValue(entity);
                    if (comp == 0)
                        newVal.R = (byte)numInput.IntValue;
                    else if (comp == 1)
                        newVal.G = (byte)numInput.IntValue;
                    else if (comp == 2)
                        newVal.B = (byte)numInput.IntValue;
                    else
                        newVal.A = (byte)numInput.IntValue;

                    if (SetPropertyValue(property, entity, newVal))
                    {
                        TrySendNetworkUpdate(entity, property);
                        colorBox.Color = colorBox.HoverColor = colorBox.PressedColor = colorBox.SelectedTextColor = newVal;
                    }
                };
                colorBox.Color = colorBox.HoverColor = colorBox.PressedColor = colorBox.SelectedTextColor = (Color)property.GetValue(entity);
                fields[i] = numberInput;
            }
            refresh += () =>
            {
                if (!fields.Any(f => ((GUINumberInput)f).TextBox.Selected))
                {
                    Color value = (Color)property.GetValue(entity);
                    ((GUINumberInput)fields[0]).IntValue = value.R;
                    ((GUINumberInput)fields[1]).IntValue = value.G;
                    ((GUINumberInput)fields[2]).IntValue = value.B;
                    ((GUINumberInput)fields[3]).IntValue = value.A;
                }
            };
            frame.RectTransform.MinSize = new Point(0, frame.RectTransform.Children.Max(c => c.MinSize.Y));
            if (!Fields.ContainsKey(property.Name)) { Fields.Add(property.Name.ToIdentifier(), fields); }
            return frame;
        }

        public GUIComponent CreateRectangleField(ISerializableEntity entity, SerializableProperty property, Rectangle value, LocalizedString displayName, LocalizedString toolTip)
        {
            var frame = new GUIFrame(new RectTransform(new Point(Rect.Width, Math.Max(elementHeight, 26)), layoutGroup.RectTransform, isFixedSize: true), color: Color.Transparent);
            var label = new GUITextBlock(new RectTransform(new Vector2(0.25f, 1), frame.RectTransform), displayName, font: GUIStyle.SmallFont)
            {
                ToolTip = displayName + '\n' + toolTip
            };
            label.Text = ToolBox.LimitString(label.Text, label.Font, label.Rect.Width);
            var fields = new GUIComponent[4];
            var inputArea = new GUILayoutGroup(new RectTransform(new Vector2(0.8f, 1), frame.RectTransform, Anchor.TopRight), isHorizontal: true, childAnchor: Anchor.CenterRight)
            {
                Stretch = true,
                RelativeSpacing = 0.01f
            };
            for (int i = 3; i >= 0; i--)
            {
                var element = new GUIFrame(new RectTransform(new Vector2(0.22f, 1), inputArea.RectTransform) { MinSize = new Point(50, 0), MaxSize = new Point(150, 50) }, style: null);
                new GUITextBlock(new RectTransform(new Vector2(0.3f, 1), element.RectTransform, Anchor.CenterLeft), GUI.RectComponentLabels[i], font: GUIStyle.SmallFont, textAlignment: Alignment.Center);
                GUINumberInput numberInput = new GUINumberInput(new RectTransform(new Vector2(0.7f, 1), element.RectTransform, Anchor.CenterRight),
                    NumberType.Int)
                {
                    Font = GUIStyle.SmallFont
                };
                // Not sure if the min value could in any case be negative.
                numberInput.MinValueInt = 0;
                // Just something reasonable to keep the value in the input rect.
                numberInput.MaxValueInt = 9999;

                if (i == 0)
                    numberInput.IntValue = value.X;
                else if (i == 1)
                    numberInput.IntValue = value.Y;
                else if (i == 2)
                    numberInput.IntValue = value.Width;
                else
                    numberInput.IntValue = value.Height;

                int comp = i;
                numberInput.OnValueChanged += (numInput) =>
                {
                    Rectangle newVal = (Rectangle)property.GetValue(entity);
                    if (comp == 0)
                        newVal.X = numInput.IntValue;
                    else if (comp == 1)
                        newVal.Y = numInput.IntValue;
                    else if (comp == 2)
                        newVal.Width = numInput.IntValue;
                    else
                        newVal.Height = numInput.IntValue;

                    if (SetPropertyValue(property, entity, newVal))
                    {
                        TrySendNetworkUpdate(entity, property);
                    }
                };
                fields[i] = numberInput;
            }
            refresh += () =>
            {
                if (!fields.Any(f => ((GUINumberInput)f).TextBox.Selected))
                {
                    Rectangle value = (Rectangle)property.GetValue(entity);
                    ((GUINumberInput)fields[0]).IntValue = value.X;
                    ((GUINumberInput)fields[1]).IntValue = value.Y;
                    ((GUINumberInput)fields[2]).IntValue = value.Width;
                    ((GUINumberInput)fields[3]).IntValue = value.Height;
                }
            };
            if (!Fields.ContainsKey(property.Name)) { Fields.Add(property.Name.ToIdentifier(), fields); }
            return frame;
        }

        public GUIComponent CreateStringArrayField(ISerializableEntity entity, SerializableProperty property, string[] value, LocalizedString displayName, LocalizedString toolTip)
        {
            int elementCount = (value.Length + 1);
            var frame = new GUIFrame(new RectTransform(new Point(Rect.Width, elementCount * elementHeight), layoutGroup.RectTransform, isFixedSize: true), color: Color.Transparent);
            var label = new GUITextBlock(new RectTransform(new Vector2(1.0f, 1.0f / elementCount), frame.RectTransform), displayName, font: GUIStyle.SmallFont)
            {
                ToolTip = toolTip
            };
            var editableAttribute = property.GetAttribute<Editable>();
            var fields = new GUIComponent[value.Length];
            var inputArea = new GUILayoutGroup(new RectTransform(new Vector2(1.0f, (float)(elementCount - 1) / elementCount), frame.RectTransform, anchor: Anchor.BottomLeft))
            {
                RelativeSpacing = 0.01f
            };
            elementCount -= 1;

            for (int i = 0; i < value.Length; i++)
            {
                var element = new GUIFrame(new RectTransform(new Vector2(1.0f, 1.0f / elementCount), inputArea.RectTransform) { MinSize = new Point(50, 0), MaxSize = new Point((int)(0.9f * inputArea.Rect.Width), 50) }, style: null);
                var elementLayoutGroup = new GUILayoutGroup(new RectTransform(Vector2.One, element.RectTransform), isHorizontal: true, childAnchor: Anchor.CenterLeft);
                // Set the label to be (i + 1) so it's easier to understand for non-programmers
                string componentLabel = (i + 1).ToString();
                new GUITextBlock(new RectTransform(new Vector2(0.3f, 1), elementLayoutGroup.RectTransform) { MaxSize = new Point(25, elementLayoutGroup.Rect.Height) }, componentLabel, font: GUIStyle.SmallFont, textAlignment: Alignment.Center);
                GUITextBox textBox = new GUITextBox(new RectTransform(new Vector2(0.7f, 1), elementLayoutGroup.RectTransform), text: value[i])
                {
                    Font = GUIStyle.SmallFont,
                    Readonly = Readonly
                };
                int comp = i;
                textBox.OnEnterPressed += (textBox, text) => OnApply(textBox);
                textBox.OnDeselected += (textBox, keys) => OnApply(textBox);
                fields[i] = textBox;

                bool OnApply(GUITextBox textBox)
                {
                    // Reserve the semicolon for serializing the value
                    bool containsForbiddenCharacters = textBox.Text.Contains(';');
                    string[] newValue = (string[])property.GetValue(entity);
                    if (!containsForbiddenCharacters)
                    {
                        newValue[comp] = textBox.Text;
                        if (SetPropertyValue(property, entity, newValue))
                        {
                            TrySendNetworkUpdate(entity, property);
                            textBox.Flash(color: GUIStyle.Green, flashDuration: 1f);
                        }
                    }
                    else
                    {
                        textBox.Text = newValue[comp];
                        textBox.Flash(color: GUIStyle.Red, flashDuration: 1f);
                    }
                    return true;
                }
            }

            refresh += () =>
            {
                if (fields.None(f => ((GUITextBox)f).Selected))
                {
                    string[] value = (string[])property.GetValue(entity);
                    for (int i = 0; i < fields.Length; i++)
                    {
                        ((GUITextBox)fields[i]).Text = value[i];
                    }
                }
            };

            frame.RectTransform.MinSize =  new Point(0, frame.RectTransform.Children.Sum(c => c.MinSize.Y));
            if (!Fields.ContainsKey(property.Name)) { Fields.Add(property.Name.ToIdentifier(), fields); }
            return frame;
        }

        public void CreateTextPicker(string textTag, ISerializableEntity entity, SerializableProperty property, GUITextBox textBox)
        {
            var msgBox = new GUIMessageBox("", "", new LocalizedString[] { TextManager.Get("Ok") }, new Vector2(0.2f, 0.5f), new Point(300, 400));
            msgBox.Buttons[0].OnClicked = msgBox.Close;

            var textList = new GUIListBox(new RectTransform(new Vector2(1.0f, 0.8f), msgBox.Content.RectTransform, Anchor.TopCenter))
            {
                PlaySoundOnSelect = true,
                OnSelected = (component, userData) =>
                {
                    string text = userData as string ?? "";

                    if (SetPropertyValue(property, entity, text))
                    {
                        TrySendNetworkUpdate(entity, property);
                        textBox.Text = (string)property.GetValue(entity);
                        textBox.Deselect();
                    }
                    return true;
                }
            };

            var tagTextPairs = TextManager.GetAllTagTextPairs().ToList();
            tagTextPairs.Sort((t1, t2) => { return t1.Value.CompareTo(t2.Value); });
            foreach (KeyValuePair<Identifier, string> tagTextPair in tagTextPairs)
            {
                if (!tagTextPair.Key.StartsWith(textTag)) { continue; }
                new GUITextBlock(new RectTransform(new Vector2(1.0f, 0.05f), textList.Content.RectTransform) { MinSize = new Point(0, 20) },
                    ToolBox.LimitString(tagTextPair.Value, GUIStyle.Font, textList.Content.Rect.Width))
                {
                    UserData = tagTextPair.Key.ToString()
                };
            }

            if (entity is IHasExtraTextPickerEntries hasExtraTextPickerEntries)
            {
                foreach (string extraEntry in hasExtraTextPickerEntries.GetExtraTextPickerEntries())
                {
                    new GUITextBlock(new RectTransform(new Vector2(1.0f, 0.05f), textList.Content.RectTransform) { MinSize = new Point(0, 20) },
                        ToolBox.LimitString(extraEntry, GUIStyle.Font, textList.Content.Rect.Width), GUIStyle.Green)
                    {
                        UserData = extraEntry
                    };
                }
            }
        }

        public GUIComponent CreateFontField(ISerializableEntity entity, SerializableProperty property, GUIFont value, LocalizedString displayName, LocalizedString toolTip)
        {
            GUIFrame frame = new(new RectTransform(new Point(Rect.Width, elementHeight), layoutGroup.RectTransform, isFixedSize: true), color: Color.Transparent);
            GUITextBlock label = new(new RectTransform(new Vector2(1f - inputFieldWidth, 1f), frame.RectTransform), displayName, font: GUIStyle.SmallFont)
            {
                ToolTip = toolTip
            };
            GUIDropDown dropDown = new GUIDropDown(new RectTransform(new Vector2(inputFieldWidth, 1), frame.RectTransform, Anchor.TopRight), elementCount: GUIStyle.Fonts.Count)
            {
                ToolTip = toolTip
            };
            foreach ((Identifier fontIdentifier, GUIFont font) in GUIStyle.Fonts)
            {
                dropDown.AddItem(fontIdentifier.Value, font);
            }
            dropDown.SelectItem(value);
            dropDown.OnSelected += (selected, val) =>
            {
                if (SetPropertyValue(property, entity, val))
                {
                    TrySendNetworkUpdate(entity, property);
                }
                return true;
            };
            refresh += () =>
            {
                if (!dropDown.Dropped) { dropDown.SelectItem(property.GetValue(entity)); }
            };
            if (!Fields.ContainsKey(property.Name)) { Fields.Add(property.Name.ToIdentifier(), new GUIComponent[] { dropDown }); }
            return frame;
        }

        private static void TrySendNetworkUpdate(ISerializableEntity entity, SerializableProperty property)
        {
            if (IsEntityRemoved(entity)) { return; }

            if (GameMain.Client != null)
            {
                if (entity is Item item)
                {
                    GameMain.Client.CreateEntityEvent(item, new Item.ChangePropertyEventData(property, item));
                }
                else if (entity is ItemComponent ic)
                {
                    GameMain.Client.CreateEntityEvent(ic.Item, new Item.ChangePropertyEventData(property, ic));
                }
            }
        }

        private bool SetPropertyValue(SerializableProperty property, object entity, object value)
        {
            if (LockEditing || IsEntityRemoved(entity) || Readonly) { return false; }

            object oldData = property.GetValue(entity);
            // some properties have null as the default string value
            if (oldData == null && value is string) { oldData = ""; }
            if (entity is ISerializableEntity sEntity && Screen.Selected is SubEditorScreen && !Equals(oldData, value))
            {
                List<ISerializableEntity> entities = new List<ISerializableEntity> { sEntity };
                Dictionary<ISerializableEntity, object> affected = MultiSetProperties(property, entity, value);

                Dictionary<object, List<ISerializableEntity>> oldValues = new Dictionary<object, List<ISerializableEntity>> {{ oldData!, new List<ISerializableEntity> { sEntity }}};

                affected.ForEach(aEntity =>
                {
                    var (item, oldVal) = aEntity;
                    entities.Add(item);

                    if (!oldValues.ContainsKey(oldVal))
                    {
                        oldValues.Add(oldVal, new List<ISerializableEntity> { item });
                    }
                    else
                    {
                        oldValues[oldVal].Add(item);
                    }
                });

                PropertyCommand cmd = new PropertyCommand(entities, property.Name.ToIdentifier(), value, oldValues);
                if (CommandBuffer != null)
                {
                    if (CommandBuffer.Item1 == property && CommandBuffer.Item2.PropertyCount == cmd.PropertyCount)
                    {
                        if (!CommandBuffer.Item2.MergeInto(cmd))
                        {
                            CommitCommandBuffer();
                        }
                    }
                    else
                    {
                        CommitCommandBuffer();
                    }
                }

                NextCommandPush = DateTime.Now.AddSeconds(1);
                CommandBuffer = Tuple.Create(property, cmd);
                PropertyChangesActive = true;
            }

            return property.TrySetValue(entity, value);
        }

        public static bool IsEntityRemoved(object entity)
            => entity is Entity { Removed: true } or ItemComponent { Item.Removed: true };

        public static void CommitCommandBuffer()
        {
            if (CommandBuffer != null)
            {
                SubEditorScreen.StoreCommand(CommandBuffer.Item2);
            }
            CommandBuffer = null;
            PropertyChangesActive = false;
        }

        /// <summary>
        /// Sets common shared properties to all selected map entities in sub editor.
        /// Only works client side while in the sub editor and when parentObject is ItemComponent, Item or Structure.
        /// </summary>
        /// <param name="property"></param>
        /// <param name="parentObject"></param>
        /// <param name="value"></param>
        /// <remarks>The function has the same parameters as <see cref="SetValue"/></remarks>
        private Dictionary<ISerializableEntity, object> MultiSetProperties(SerializableProperty property, object parentObject, object value)
        {
            Dictionary<ISerializableEntity, object> affected = new Dictionary<ISerializableEntity, object>();

            if (!(Screen.Selected is SubEditorScreen) || MapEntity.SelectedList.Count <= 1) { return affected; }
            if (!(parentObject is ItemComponent || parentObject is Item || parentObject is Structure || parentObject is Hull)) { return affected; }
            
            foreach (var entity in MapEntity.SelectedList.Where(entity => entity != parentObject))
            {
                switch (parentObject)
                {
                    case Hull _:
                    case Structure _:
                    case Item _:
                        if (entity.GetType() == parentObject.GetType())
                        {
                            SafeAdd((ISerializableEntity) entity, property);
                            property.PropertyInfo.SetValue(entity, value);
                        } 
                        else if (entity is ISerializableEntity { SerializableProperties: { } } sEntity)
                        {
                            var props = sEntity.SerializableProperties;
                            if (props.TryGetValue(property.Name.ToIdentifier(), out SerializableProperty foundProp) && foundProp.Attributes.OfType<Editable>().Any())
                            {
                                SafeAdd(sEntity, foundProp);
                                foundProp.PropertyInfo.SetValue(entity, value);
                            }
                        }
                        break;
                    case ItemComponent parentComponent when entity is Item otherItem:
                        if (otherItem == parentComponent.Item) { continue; }                        
                        int componentIndex = parentComponent.Item.Components.FindAll(c => c.GetType() == parentComponent.GetType()).IndexOf(parentComponent);
                        //find the component of the same type and same index from the other item
                        var otherComponents = otherItem.Components.FindAll(c => c.GetType() == parentComponent.GetType());
                        if (componentIndex >= 0 && componentIndex < otherComponents.Count)
                        {
                            var component = otherComponents[componentIndex];
                            Debug.Assert(component.GetType() == parentObject.GetType());                            
                            SafeAdd(component, property);
                            if (value is string stringValue && 
                                property.PropertyType.IsEnum &&
                                Enum.TryParse(property.PropertyType, stringValue, out var enumValue))
                            {
                                property.PropertyInfo.SetValue(component, enumValue);
                            }
                            else
                            {
                                try
                                {
                                    property.PropertyInfo.SetValue(component, value);
                                }
                                catch (ArgumentException e)
                                {
                                    DebugConsole.ThrowError($"Failed to set the value of the property \"{property.Name}\" to {value?.ToString() ?? "null"}", e);
                                }
                            }                            
                        }
                        break;
                }
            }

            return affected;

            void SafeAdd(ISerializableEntity entity, SerializableProperty prop)
            {
                object obj = prop.GetValue(entity);
                if (prop.PropertyType == typeof(string) && obj == null) { obj = string.Empty; }
                affected.Add(entity, obj);
            }
        }
    }

    /// <summary>
    /// Implement this interface to insert extra entires to the text pickers created for the SerializableEntityEditors of the entity
    /// </summary>
    interface IHasExtraTextPickerEntries
    {
        public IEnumerable<string> GetExtraTextPickerEntries();
    }
}

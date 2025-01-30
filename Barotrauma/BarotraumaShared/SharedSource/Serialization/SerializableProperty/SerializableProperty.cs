using Barotrauma.Extensions;
using Barotrauma.Items.Components;
using Microsoft.Xna.Framework;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Xml.Linq;

namespace Barotrauma
{
    public enum IsPropertySaveable
    {
        Yes,
        No
    }

    [AttributeUsage(AttributeTargets.Property)]
    public sealed class Serialize : Attribute
    {
        public readonly object DefaultValue;
        public readonly IsPropertySaveable IsSaveable;
        public readonly Identifier TranslationTextTag;

        /// <summary>
        /// If set to true, the instance values saved in a submarine file will always override the prefab values, even if using a mod that normally overrides instance values.
        /// </summary>
        public bool AlwaysUseInstanceValues;

        public string Description;

        /// <summary>
        /// Makes the property serializable to/from XML
        /// </summary>
        /// <param name="defaultValue">The property is set to this value during deserialization if the value is not defined in XML.</param>
        /// <param name="isSaveable">Is the value saved to XML when serializing.</param>
        /// <param name="translationTextTag">If set to anything else than null, SerializableEntityEditors will show what the text gets translated to or warn if the text is not found in the language files.
        /// <param name="alwaysUseInstanceValues">If set to true, the instance values saved in a submarine file will always override the prefab values, even if using a mod that normally overrides instance values.
        /// Setting the value to a non-empty string will let the user select the text from one whose tag starts with the given string (e.g. RoomName. would show all texts with a RoomName.* tag)</param>
        public Serialize(object defaultValue, IsPropertySaveable isSaveable, string description = "", string translationTextTag = "", bool alwaysUseInstanceValues = false)
        {
            DefaultValue = defaultValue;
            IsSaveable = isSaveable;
            TranslationTextTag = translationTextTag.ToIdentifier();
            Description = description;
            AlwaysUseInstanceValues = alwaysUseInstanceValues;
        }
    }

    [AttributeUsage(AttributeTargets.Property)]
    public sealed class Header : Attribute
    {
        public readonly LocalizedString Text;

        public Header(string text = "", string localizedTextTag = null)
        {
            Text = localizedTextTag != null ? TextManager.Get(localizedTextTag) : text;
        }
    }

    public sealed class SerializableProperty
    {
        private static readonly ImmutableDictionary<Type, string> supportedTypes = new Dictionary<Type, string>
        {
            { typeof(bool), "bool" },
            { typeof(int), "int" },
            { typeof(float), "float" },
            { typeof(string), "string" },
            { typeof(Identifier), "identifier" },
            { typeof(LanguageIdentifier), "languageidentifier" },
            { typeof(LocalizedString), "localizedstring" },
            { typeof(Point), "point" },
            { typeof(Vector2), "vector2" },
            { typeof(Vector3), "vector3" },
            { typeof(Vector4), "vector4" },
            { typeof(Rectangle), "rectangle" },
            { typeof(Color), "color" },
            { typeof(string[]), "stringarray" },
            { typeof(Identifier[]), "identifierarray" },
            { typeof(GUIFont), "font" }
        }.ToImmutableDictionary();

        private static readonly Dictionary<Type, Dictionary<Identifier, SerializableProperty>> cachedProperties = 
            new Dictionary<Type, Dictionary<Identifier, SerializableProperty>>();
        public readonly string Name;
        public readonly AttributeCollection Attributes;
        public readonly Type PropertyType;

        public readonly bool OverridePrefabValues;

        public readonly PropertyInfo PropertyInfo;

        public SerializableProperty(PropertyDescriptor property)
        {
            Name = property.Name;
            PropertyInfo = property.ComponentType.GetProperty(property.Name);
            PropertyType = property.PropertyType;
            Attributes = property.Attributes;
            OverridePrefabValues = GetAttribute<Serialize>()?.AlwaysUseInstanceValues ?? false;
        }

        public T GetAttribute<T>() where T : Attribute
        {
            foreach (Attribute a in Attributes)
            {
                if (a is T) return (T)a;
            }

            return default;
        }

        public void SetValue(object parentObject, object val)
        {
            PropertyInfo.SetValue(parentObject, val);
        }

        public bool TrySetValue(object parentObject, string value)
        {
            if (value == null) { return false; }

            if (!supportedTypes.TryGetValue(PropertyType, out string typeName))
            {
                if (PropertyType.IsEnum)
                {
                    object enumVal;
                    try
                    {
                        enumVal = Enum.Parse(PropertyInfo.PropertyType, value, true);
                    }
                    catch (Exception e)
                    {
                        DebugConsole.ThrowError($"Failed to set the value of the property \"{Name}\" of \"{parentObject}\" to {value} (not a valid {PropertyInfo.PropertyType})", e);
                        return false;
                    }
                    try
                    {
                        PropertyInfo.SetValue(parentObject, enumVal);
                    }
                    catch (Exception e)
                    {
                        DebugConsole.ThrowError($"Failed to set the value of the property \"{Name}\" of \"{parentObject}\" to {value}", e);
                        return false;
                    }
                }
                else
                {
                    DebugConsole.ThrowError($"Failed to set the value of the property \"{Name}\" of \"{parentObject}\" to {value} (Type \"{PropertyType.Name}\" not supported)");

                    return false;
                }
            }

            try
            {
                switch (typeName)
                {
                    case "bool":
                        bool boolValue = value.ToIdentifier() == "true";
                        if (TrySetBoolValueWithoutReflection(parentObject, boolValue)) { return true; }
                        PropertyInfo.SetValue(parentObject, boolValue, null);
                        break;
                    case "int":
                        if (int.TryParse(value, out int intVal))
                        {
                            if (TrySetFloatValueWithoutReflection(parentObject, intVal)) { return true; }
                            PropertyInfo.SetValue(parentObject, intVal, null);
                        }
                        else
                        {
                            return false;
                        }
                        break;
                    case "float":
                        if (float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out float floatVal))
                        {
                            if (TrySetFloatValueWithoutReflection(parentObject, floatVal)) { return true; }
                            PropertyInfo.SetValue(parentObject, floatVal, null);
                        }
                        else
                        {
                            return false;
                        }
                        break;
                    case "string":
                        PropertyInfo.SetValue(parentObject, value, null);
                        break;
                    case "point":
                        PropertyInfo.SetValue(parentObject, XMLExtensions.ParsePoint(value));
                        break;
                    case "vector2":
                        PropertyInfo.SetValue(parentObject, XMLExtensions.ParseVector2(value));
                        break;
                    case "vector3":
                        PropertyInfo.SetValue(parentObject, XMLExtensions.ParseVector3(value));
                        break;
                    case "vector4":
                        PropertyInfo.SetValue(parentObject, XMLExtensions.ParseVector4(value));
                        break;
                    case "color":
                        PropertyInfo.SetValue(parentObject, XMLExtensions.ParseColor(value));
                        break;
                    case "rectangle":
                        PropertyInfo.SetValue(parentObject, XMLExtensions.ParseRect(value, true));
                        break;
                    case "identifier":
                        PropertyInfo.SetValue(parentObject, value.ToIdentifier());
                        break;
                    case "languageidentifier":
                        PropertyInfo.SetValue(parentObject, value.ToLanguageIdentifier());
                        break;
                    case "localizedstring":
                        PropertyInfo.SetValue(parentObject, new RawLString(value));
                        break;
                    case "stringarray":
                        PropertyInfo.SetValue(parentObject, ParseStringArray(value));
                        break;
                    case "identifierarray":
                        PropertyInfo.SetValue(parentObject, ParseIdentifierArray(value));
                        break;
                    case "font":
                        PropertyInfo.SetValue(parentObject, GUIStyle.Fonts.TryGetValue(new(value), out GUIFont font) ? font : null);
                        break;
                }
            }
            catch (Exception e)
            {
                DebugConsole.ThrowError($"Failed to set the value of the property \"{Name}\" of \"{parentObject}\" to {value}", e);
                return false;
            }
            return true;
        }


        private static string[] ParseStringArray(string stringArrayValues)
        {
            return string.IsNullOrEmpty(stringArrayValues) ? Array.Empty<string>() : stringArrayValues.Split(';');
        }

        private static Identifier[] ParseIdentifierArray(string stringArrayValues)
        {
            return ParseStringArray(stringArrayValues).ToIdentifiers();
        }

        public bool TrySetValue(object parentObject, object value)
        {
            if (value == null || parentObject == null || PropertyInfo == null) return false;

            try
            {
                if (!supportedTypes.TryGetValue(PropertyType, out string typeName))
                {
                    if (PropertyType.IsEnum)
                    {
                        object enumVal;
                        try
                        {
                            enumVal = Enum.Parse(PropertyInfo.PropertyType, value.ToString(), true);
                        }
                        catch (Exception e)
                        {
                            DebugConsole.ThrowError(
                                $"Failed to set the value of the property \"{Name}\" of \"{parentObject}\" to {value} (not a valid {PropertyInfo.PropertyType})", e);
                            return false;
                        }
                        PropertyInfo.SetValue(parentObject, enumVal);
                        return true;
                    }
                    else
                    {
                        DebugConsole.ThrowError($"Failed to set the value of the property \"{Name}\" of \"{parentObject}\" to {value} (Type \"{PropertyType.Name}\" not supported)");

                        return false;
                    }
                }

                try
                {
                    if (value.GetType() == typeof(string))
                    {
                        switch (typeName)
                        {
                            case "string":
                                PropertyInfo.SetValue(parentObject, value, null);
                                return true;
                            case "point":
                                PropertyInfo.SetValue(parentObject, XMLExtensions.ParsePoint((string)value));
                                return true;
                            case "vector2":
                                PropertyInfo.SetValue(parentObject, XMLExtensions.ParseVector2((string)value));
                                return true;
                            case "vector3":
                                PropertyInfo.SetValue(parentObject, XMLExtensions.ParseVector3((string)value));
                                return true;
                            case "vector4":
                                PropertyInfo.SetValue(parentObject, XMLExtensions.ParseVector4((string)value));
                                return true;
                            case "color":
                                PropertyInfo.SetValue(parentObject, XMLExtensions.ParseColor((string)value));
                                return true;
                            case "rectangle":
                                PropertyInfo.SetValue(parentObject, XMLExtensions.ParseRect((string)value, false));
                                return true;
                            case "identifier":
                                PropertyInfo.SetValue(parentObject, new Identifier((string)value));
                                return true;
                            case "languageidentifier":
                                PropertyInfo.SetValue(parentObject, ((string)value).ToLanguageIdentifier());
                                return true;
                            case "localizedstring":
                                PropertyInfo.SetValue(parentObject, new RawLString((string)value));
                                return true;
                            case "stringarray":
                                PropertyInfo.SetValue(parentObject, ParseStringArray((string)value));
                                return true;
                            case "identifierarray":
                                PropertyInfo.SetValue(parentObject, ParseIdentifierArray((string)value));
                                return true;
#if CLIENT
                            case "font":
                                PropertyInfo.SetValue(parentObject, GUIStyle.Fonts.TryGetValue(new((string)value), out GUIFont font) ? font : null);
                                return true;
#endif
                            default:
                                DebugConsole.ThrowError($"Failed to set the value of the property \"{Name}\" of \"{parentObject}\" to {value}");
                                DebugConsole.ThrowError($"(Cannot convert a string to a {PropertyType})");
                                return false;
                        }
                    }
                    else if (PropertyType != value.GetType())
                    {
                        DebugConsole.ThrowError($"Failed to set the value of the property \"{Name}\" of \"{parentObject}\" to {value}");
                        DebugConsole.ThrowError("(Non-matching type, should be " + PropertyType + " instead of " + value.GetType() + ")");
                        return false;
                    }

                    PropertyInfo.SetValue(parentObject, value, null);
                }

                catch (Exception e)
                {
                    DebugConsole.ThrowError($"Failed to set the value of the property \"{Name}\" of \"{parentObject}\" to {value}", e);
                    return false;
                }

                return true;
            }
            catch (Exception e)
            {
                DebugConsole.ThrowError($"Error in SerializableProperty.TrySetValue (Property: {PropertyInfo.Name})", e);
                return false;
            }
        }

        public bool TrySetValue(object parentObject, float value)
        {
            try
            {
                if (TrySetFloatValueWithoutReflection(parentObject, value)) { return true; }
                PropertyInfo.SetValue(parentObject, value, null);
            }
            catch (TargetInvocationException e)
            {
                DebugConsole.ThrowError("Exception thrown by the target of SerializableProperty.TrySetValue", e.InnerException);
                return false;
            }
            catch (Exception e)
            {
                DebugConsole.ThrowError($"Error in SerializableProperty.TrySetValue (Property: {PropertyInfo.Name})", e);
                return false;
            }

            return true;
        }

        public bool TrySetValue(object parentObject, bool value)
        {
            try
            {
                if (TrySetBoolValueWithoutReflection(parentObject, value)) { return true; }
                PropertyInfo.SetValue(parentObject, value, null);
            }
            catch (TargetInvocationException e)
            {
                DebugConsole.ThrowError("Exception thrown by the target of SerializableProperty.TrySetValue", e.InnerException);
                return false;
            }
            catch (Exception e)
            {
                DebugConsole.ThrowError($"Error in SerializableProperty.TrySetValue (Property: {PropertyInfo.Name})", e);
                return false;
            }
            return true;
        }

        public bool TrySetValue(object parentObject, int value)
        {
            try
            {
                if (TrySetFloatValueWithoutReflection(parentObject, value)) { return true; }
                PropertyInfo.SetValue(parentObject, value, null);
            }
            catch (TargetInvocationException e)
            {
                DebugConsole.ThrowError("Exception thrown by the target of SerializableProperty.TrySetValue", e.InnerException);
                return false;
            }
            catch (Exception e)
            {
                DebugConsole.ThrowError($"Error in SerializableProperty.TrySetValue (Property: {PropertyInfo.Name})", e);
                return false;
            }
            return true;
        }

        public object GetValue(object parentObject)
        {
            if (parentObject == null || PropertyInfo == null) { return false; }

            var value = TryGetValueWithoutReflection(parentObject);
            if (value != null) { return value; }
            
            try
            {
                return PropertyInfo.GetValue(parentObject, null);
            }
            catch (TargetInvocationException e)
            {
                DebugConsole.ThrowError("Exception thrown by the target of SerializableProperty.GetValue", e.InnerException);
                return false;
            }
            catch (Exception e)
            {
                DebugConsole.ThrowError("Error in SerializableProperty.GetValue", e);
                return false;
            }
        }

        public float GetFloatValue(object parentObject)
        {
            if (parentObject == null || PropertyInfo == null) { return 0.0f; }

            if (TryGetFloatValueWithoutReflection(parentObject, out float value))
            {
                return value;
            }

            try
            {
                if (PropertyType == typeof(int))
                {
                    return (int)PropertyInfo.GetValue(parentObject, null);
                }
                else
                {
                    return (float)PropertyInfo.GetValue(parentObject, null);
                }
            }
            catch (TargetInvocationException e)
            {
                DebugConsole.ThrowError("Exception thrown by the target of SerializableProperty.GetValue", e.InnerException);
                return 0.0f;
            }
            catch (Exception e)
            {
                DebugConsole.ThrowError("Error in SerializableProperty.GetValue", e);
                return 0.0f;
            }
        }

        public bool GetBoolValue(object parentObject)
        {
            if (parentObject == null || PropertyInfo == null) { return false; }

            if (TryGetBoolValueWithoutReflection(parentObject, out bool value))
            {
                return value;
            }

            try
            {
                return (bool)PropertyInfo.GetValue(parentObject, null);
            }
            catch (TargetInvocationException e)
            {
                DebugConsole.ThrowError("Exception thrown by the target of SerializableProperty.GetValue", e.InnerException);
                return false;
            }
            catch (Exception e)
            {
                DebugConsole.ThrowError("Error in SerializableProperty.GetValue", e);
                return false;
            }
        }

        public static string GetSupportedTypeName(Type type)
        {
            if (type.IsEnum) { return "Enum"; }
            if (!supportedTypes.TryGetValue(type, out string typeName))
            {
                return null;
            }
            return typeName;
        }

        private readonly ImmutableDictionary<Identifier, Func<object, object>> valueGetters =
            new Dictionary<Identifier, Func<object, object>>()
            {
                {"Voltage".ToIdentifier(), (obj) => obj is Powered p ? p.Voltage : (object) null},
                {"Charge".ToIdentifier(), (obj) => obj is PowerContainer p ? p.Charge : (object) null},
                {"Overload".ToIdentifier(), (obj) => obj is PowerTransfer p ? p.Overload : (object) null},
                {"AvailableFuel".ToIdentifier(), (obj) => obj is Reactor r ? r.AvailableFuel : (object) null},
                {"FissionRate".ToIdentifier(), (obj) => obj is Reactor r ? r.FissionRate : (object) null},
                {"OxygenFlow".ToIdentifier(), (obj) => obj is Vent v ? v.OxygenFlow : (object) null},
                {
                    "CurrFlow".ToIdentifier(),
                    (obj) => obj is Pump p ? (object) p.CurrFlow :
                                   obj is OxygenGenerator o ? (object)o.CurrFlow :
                                   null
                },
                {"CurrentVolume".ToIdentifier(), (obj) => obj is Engine e ? e.CurrentVolume : (object)null},
                {"MotionDetected".ToIdentifier(), (obj) => obj is MotionSensor m ? m.MotionDetected : (object)null},
                {"Oxygen".ToIdentifier(), (obj) => obj is Character c ? c.Oxygen : (object)null},
                {"Health".ToIdentifier(), (obj) => obj is Character c ? c.Health : (object)null},
                {"OxygenAvailable".ToIdentifier(), (obj) => obj is Character c ? c.OxygenAvailable : (object)null},
                {"PressureProtection".ToIdentifier(), (obj) => obj is Character c ? c.PressureProtection : (object)null},
                {"IsDead".ToIdentifier(), (obj) => obj is Character c ? c.IsDead : (object)null},
                {"IsHuman".ToIdentifier(), (obj) => obj is Character c ? c.IsHuman : (object)null},
                {"IsOn".ToIdentifier(), (obj) => obj is LightComponent l ? l.IsOn : (object)null},
                {"Condition".ToIdentifier(), (obj) => obj is Item i ? i.Condition : (object)null},
                {"ContainerIdentifier".ToIdentifier(), (obj) => obj is Item i ? i.ContainerIdentifier : (object)null},
                {"PhysicsBodyActive".ToIdentifier(), (obj) => obj is Item i ? i.PhysicsBodyActive : (object)null},
            }.ToImmutableDictionary();
        
        /// <summary>
        /// Try getting the values of some commonly used properties directly without reflection
        /// </summary>
        private object TryGetValueWithoutReflection(object parentObject)
        {
            if (PropertyType == typeof(float))
            {
                if (TryGetFloatValueWithoutReflection(parentObject, out float value)) { return value; }
            }
            else if (PropertyType == typeof(bool))
            {
                if (TryGetBoolValueWithoutReflection(parentObject, out bool value)) { return value; }
            }
            else if (PropertyType == typeof(string))
            {
                if (TryGetStringValueWithoutReflection(parentObject, out string value)) { return value; }
            }
            return null;
        }

        /// <summary>
        /// Try getting the values of some commonly used properties directly without reflection
        /// </summary>
        private bool TryGetFloatValueWithoutReflection(object parentObject, out float value)
        {
            value = 0.0f;
            switch (Name)
            {
                case nameof(Powered.Voltage):
                    {
                        if (parentObject is Powered powered) { value = powered.Voltage; return true; }
                    }
                    break;
                case nameof(Powered.RelativeVoltage):
                    {
                        if (parentObject is Powered powered) { value = powered.RelativeVoltage; return true; }
                    }
                    break;
                case nameof(Powered.CurrPowerConsumption):
                    {
                        if (parentObject is Powered powered) { value = powered.CurrPowerConsumption; return true; }
                    }
                    break;
                case nameof(PowerContainer.Charge):
                    {
                        if (parentObject is PowerContainer powerContainer) { value = powerContainer.Charge; return true; }
                    }
                    break;
                case nameof(Repairable.StressDeteriorationMultiplier):
                    {
                        if (parentObject is Repairable repairable) { value = repairable.StressDeteriorationMultiplier; return true; }
                    }
                    break;
                case nameof(PowerContainer.ChargePercentage):
                    {
                        if (parentObject is PowerContainer powerContainer) { value = powerContainer.ChargePercentage; return true; }
                    }
                    break;
                case nameof(PowerContainer.RechargeRatio):
                    {
                        if (parentObject is PowerContainer powerContainer) { value = powerContainer.RechargeRatio; return true; }
                    }
                    break;
                case nameof(ItemContainer.ContainedNonBrokenItemCount):
                    {
                        if (parentObject is ItemContainer itemContainer) { value = itemContainer.ContainedNonBrokenItemCount; return true; }
                    }
                    break;
                case nameof(Reactor.AvailableFuel):
                    { if (parentObject is Reactor reactor) { value = reactor.AvailableFuel; return true; } }
                    break;
                case nameof(Reactor.FissionRate):
                    { if (parentObject is Reactor reactor) { value = reactor.FissionRate; return true; } }
                    break;
                case nameof(Reactor.Temperature):
                    { if (parentObject is Reactor reactor) { value = reactor.Temperature; return true; } }
                    break;
                case nameof(Vent.OxygenFlow):
                    if (parentObject is Vent vent) { value = vent.OxygenFlow; return true; }
                    break;
                case nameof(Pump.CurrFlow):
                    { if (parentObject is Pump pump) { value = pump.CurrFlow; return true; } }
                    if (parentObject is OxygenGenerator oxygenGenerator) { value = oxygenGenerator.CurrFlow; return true; }
                    break;
                case nameof(Engine.CurrentBrokenVolume):
                    { if (parentObject is Engine engine) { value = engine.CurrentBrokenVolume; return true; } }
                    { if (parentObject is Pump pump) { value = pump.CurrentBrokenVolume; return true; } }
                    break;
                case nameof(Engine.CurrentVolume):
                    { if (parentObject is Engine engine) { value = engine.CurrentVolume; return true; } }
                    break;
                case nameof(Character.Oxygen):
                    { if (parentObject is Character character) { value = character.Oxygen; return true; } }
                    { if (parentObject is Hull hull) { value = hull.Oxygen; return true; } }
                    break;
                case nameof(Character.Health):
                    { if (parentObject is Character character) { value = character.Health; return true; } }
                    break;
                case nameof(Character.OxygenAvailable):
                    { if (parentObject is Character character) { value = character.OxygenAvailable; return true; } }
                    break;
                case nameof(Character.PressureProtection):
                    { if (parentObject is Character character) { value = character.PressureProtection; return true; } }
                    break;
                case nameof(Item.Condition):
                    { if (parentObject is Item item) { value = item.Condition; return true; } }
                    break;
                case nameof(Item.ConditionPercentage):
                    { if (parentObject is Item item) { value = item.ConditionPercentage; return true; } }
                    break;
                case nameof(Item.SightRange):
                    { if (parentObject is Item item) { value = item.SightRange; return true; } }
                    break;
                case nameof(Item.SoundRange):
                    { if (parentObject is Item item) { value = item.SoundRange; return true; } }
                    break;
                case nameof(Character.SpeedMultiplier):
                    { if (parentObject is Character character) { value = character.SpeedMultiplier; return true; } }
                    break;
                case nameof(Character.PropulsionSpeedMultiplier):
                    { if (parentObject is Character character) { value = character.PropulsionSpeedMultiplier; return true; } }
                    break;
                case nameof(Character.LowPassMultiplier):
                    { if (parentObject is Character character) { value = character.LowPassMultiplier; return true; } }
                    break;
                case nameof(Character.ObstructVisionAmount):
                    { if (parentObject is Character character) { value = character.ObstructVisionAmount; return true; } }
                    break;
                case nameof(Character.HullOxygenPercentage):
                    {
                        if (parentObject is Character character) 
                        { 
                            value = character.HullOxygenPercentage;
                            return true;
                        }
                        else if (parentObject is Item item)
                        {
                            value = item.HullOxygenPercentage;
                            return true;
                        }
                    }
                    break;
                case nameof(Door.Stuck):
                    { if (parentObject is Door door) { value = door.Stuck; return true; } }
                    break;
            }
            return false;
        }
        
        /// <summary>
         /// Try getting the values of some commonly used properties directly without reflection
         /// </summary>
        private bool TryGetBoolValueWithoutReflection(object parentObject, out bool value)
        {
            value = false;
            switch (Name)
            {
                case nameof(ItemComponent.IsActive):
                    if (parentObject is ItemComponent ic) { value = ic.IsActive; return true; }
                    break;
                case nameof(PowerTransfer.Overload):
                    if (parentObject is PowerTransfer powerTransfer) { value = powerTransfer.Overload; return true; }
                    break;
                case nameof(PowerContainer.OutputDisabled):
                    if (parentObject is PowerContainer powerContainer) { value = powerContainer.OutputDisabled; return true; }
                    break;
                case nameof(MotionSensor.MotionDetected):
                    if (parentObject is MotionSensor motionSensor) { value = motionSensor.MotionDetected; return true; }
                    break;
                case nameof(Character.IsDead):
                    { if (parentObject is Character character) { value = character.IsDead; return true; } }
                    break;
                case nameof(Character.NeedsAir):
                    { if (parentObject is Character character) { value = character.NeedsAir; return true; } }
                    break;
                case nameof(Character.NeedsOxygen):
                    { if (parentObject is Character character) { value = character.NeedsOxygen; return true; } }
                    break;
                case nameof(Character.IsHuman):
                    { if (parentObject is Character character) { value = character.IsHuman; return true; } }
                    break;
                case nameof(LightComponent.IsOn):
                    { if (parentObject is LightComponent lightComponent) { value = lightComponent.IsOn; return true; } }
                    break;
                case nameof(Item.PhysicsBodyActive):
                    {
                        if (parentObject is Item item) { value = item.PhysicsBodyActive; return true; }
                    }
                    break;
                case nameof(DockingPort.Docked):
                    if (parentObject is DockingPort dockingPort) { value = dockingPort.Docked; return true; }
                    break;
                case nameof(Reactor.TemperatureCritical):
                    if (parentObject is Reactor reactor) { value = reactor.TemperatureCritical; return true; }
                    break;
                case nameof(TriggerComponent.TriggerActive):
                    if (parentObject is TriggerComponent trigger) { value = trigger.TriggerActive; return true; }
                    break;
                case nameof(Controller.State):
                    if (parentObject is Controller controller) { value = controller.State; return true; }
                    break;
                case nameof(Holdable.Attached):
                    if (parentObject is Holdable holdable) { value = holdable.Attached; return true; }
                    break;
                case nameof(Character.InWater):
                    {
                        if (parentObject is Character character)
                        {
                            value = character.InWater;
                            return true;
                        }
                        else if (parentObject is Item item)
                        {
                            value = item.InWater;
                            return true;
                        }
                    }
                    break;
                case nameof(Rope.Snapped):
                    if (parentObject is Rope rope) { value = rope.Snapped; return true; }
                    break;
            }
            return false;
        }

        /// <summary>
        /// Try getting the values of some commonly used properties directly without reflection
        /// </summary>
        private bool TryGetStringValueWithoutReflection(object parentObject, out string value)
        {
            value = null;
            switch (Name)
            {
                case nameof(Item.ContainerIdentifier):
                    {
                        if (parentObject is Item item) { value = item.ContainerIdentifier.Value; return true; }
                    }
                    break;
            }
            return false;
        }

        /// <summary>
        /// Try setting the values of some commonly used properties directly without reflection
        /// </summary>
        private bool TrySetFloatValueWithoutReflection(object parentObject, float value)
        {
            switch (Name)
            {
                case nameof(Item.Condition):
                    { if (parentObject is Item item) { item.Condition = value; return true; } }
                    break;
                case nameof(Powered.Voltage):
                    if (parentObject is Powered powered) { powered.Voltage = value; return true; }
                    break;
                case nameof(PowerContainer.Charge):
                    if (parentObject is PowerContainer powerContainer) { powerContainer.Charge = value; return true; }
                    break;
                case nameof(Reactor.AvailableFuel):
                    if (parentObject is Reactor reactor) { reactor.AvailableFuel = value; return true; }
                    break;
                case nameof(Character.Oxygen):
                    { if (parentObject is Character character) { character.Oxygen = value; return true; } }
                    break;
                case nameof(Character.OxygenAvailable):
                    { if (parentObject is Character character) { character.OxygenAvailable = value; return true; } }
                    break;
                case nameof(Character.PressureProtection):
                    { if (parentObject is Character character) { character.PressureProtection = value; return true; } }
                    break;
                case nameof(Character.LowPassMultiplier):
                    { if (parentObject is Character character) { character.LowPassMultiplier = value; return true; } }
                    break;
                case nameof(Character.SpeedMultiplier):
                    { if (parentObject is Character character) { character.StackSpeedMultiplier(value); return true; } }
                    break;
                case nameof(Character.HealthMultiplier):
                    { if (parentObject is Character character) { character.StackHealthMultiplier(value); return true; } }
                    break;
                case nameof(Character.PropulsionSpeedMultiplier):
                    { if (parentObject is Character character) { character.PropulsionSpeedMultiplier = value; return true; } }
                    break;
                case nameof(Character.ObstructVisionAmount):
                    { if (parentObject is Character character) { character.ObstructVisionAmount = value; return true; } }
                    break;
                case nameof(Item.Scale):
                    { if (parentObject is Item item) { item.Scale = value; return true; } }
                    break;
                case nameof(Item.SightRange):
                    { if (parentObject is Item item) { item.SightRange = value; return true; } }
                    break;
                case nameof(Item.SoundRange):
                    { if (parentObject is Item item) { item.SoundRange = value; return true; } }
                    break;
            }
            return false;
        }
        /// <summary>
        /// Try setting the values of some commonly used properties directly without reflection
        /// </summary>
        private bool TrySetBoolValueWithoutReflection(object parentObject, bool value)
        {
            switch (Name)
            {
                case nameof(Character.ObstructVision):
                    { if (parentObject is Character character) { character.ObstructVision = value; return true; } }
                    break;
                case nameof(Character.HideFace):
                    { if (parentObject is Character character) { character.HideFace = value; return true; } }
                    break;
                case nameof(Character.UseHullOxygen):
                    { if (parentObject is Character character) { character.UseHullOxygen = value; return true; } }
                    break;
                case nameof(LightComponent.IsOn):
                    { if (parentObject is LightComponent lightComponent) { lightComponent.IsOn = value; return true; } }
                    break;
                case nameof(ItemComponent.IsActive):
                    { if (parentObject is ItemComponent ic) { ic.IsActive = value; return true; } }
                    break;
            }
            return false;
        }

        public static List<SerializableProperty> GetProperties<T>(ISerializableEntity obj)
        {
            List<SerializableProperty> editableProperties = new List<SerializableProperty>();
            foreach (var property in obj.SerializableProperties.Values)
            {
                if (property.Attributes.OfType<T>().Any()) editableProperties.Add(property);
            }

            return editableProperties;
        }

        public static Dictionary<Identifier, SerializableProperty> GetProperties(object obj)
        {
            Type objType = obj.GetType();
            if (cachedProperties.ContainsKey(objType))
            {
                return cachedProperties[objType];
            }

            var properties = TypeDescriptor.GetProperties(obj.GetType()).Cast<PropertyDescriptor>();
            Dictionary<Identifier, SerializableProperty> dictionary = new Dictionary<Identifier, SerializableProperty>();
            foreach (var property in properties)
            {
                var serializableProperty = new SerializableProperty(property);
                dictionary.Add(serializableProperty.Name.ToIdentifier(), serializableProperty);
            }

            cachedProperties[objType] = dictionary;

            return dictionary;
        }
        
        public static Dictionary<Identifier, SerializableProperty> DeserializeProperties(object obj, XElement element = null)
        {
            Dictionary<Identifier, SerializableProperty> dictionary = GetProperties(obj);

            foreach (var property in dictionary.Values)
            {
                //set the value of the property to the default value if there is one
                foreach (var ini in property.Attributes.OfType<Serialize>())
                {
                    property.TrySetValue(obj, ini.DefaultValue);
                    break;
                }
            }

            if (element != null)
            {
                //go through all the attributes in the xml element 
                //and set the value of the matching property if it is initializable
                foreach (XAttribute attribute in element.Attributes())
                {
                    if (!dictionary.TryGetValue(attribute.NameAsIdentifier(), out SerializableProperty property)) { continue; }
                    if (!property.Attributes.OfType<Serialize>().Any()) { continue; }
                    property.TrySetValue(obj, attribute.Value);
                }
            }

            return dictionary;
        }

        public static void SerializeProperties(ISerializableEntity obj, XElement element, bool saveIfDefault = false, bool ignoreEditable = false)
        {
            var saveProperties = GetProperties<Serialize>(obj);
            foreach (var property in saveProperties)
            {
                object value = property.GetValue(obj);
                if (value == null) continue;

                if (!saveIfDefault)
                {
                    //only save 
                    //  - if the attribute is saveable and it's different from the default value
                    //  - or can be changed in-game or in the editor
                    bool save = false;
                    foreach (var attribute in property.Attributes.OfType<Serialize>())
                    {
                        if ((attribute.IsSaveable == IsPropertySaveable.Yes && !attribute.DefaultValue.Equals(value)) ||
                            (!ignoreEditable && property.Attributes.OfType<Editable>().Any()))
                        {
                            save = true;
                            break;
                        }
                    }

                    if (!save) continue;
                }

                string stringValue;
                if (!supportedTypes.TryGetValue(value.GetType(), out string typeName))
                {
                    if (property.PropertyType.IsEnum)
                    {
                        stringValue = value.ToString();
                    }
                    else
                    {
                        DebugConsole.ThrowError("Failed to serialize the property \"" + property.Name + "\" of \"" + obj + "\" (type " + property.PropertyType + " not supported)");
                        continue;
                    }
                }
                else
                {
                    switch (typeName)
                    {
                        case "float":
                            //make sure the decimal point isn't converted to a comma or anything else
                            stringValue = ((float)value).ToString("G", CultureInfo.InvariantCulture);
                            break;
                        case "point":
                            stringValue = XMLExtensions.PointToString((Point)value);
                            break;
                        case "vector2":
                            stringValue = XMLExtensions.Vector2ToString((Vector2)value);
                            break;
                        case "vector3":
                            stringValue = XMLExtensions.Vector3ToString((Vector3)value);
                            break;
                        case "vector4":
                            stringValue = XMLExtensions.Vector4ToString((Vector4)value);
                            break;
                        case "color":
                            stringValue = XMLExtensions.ColorToString((Color)value);
                            break;
                        case "rectangle":
                            stringValue = XMLExtensions.RectToString((Rectangle)value);
                            break;
                        case "stringarray":
                            string[] stringArray = (string[])value;
                            stringValue =  stringArray != null ? string.Join(';', stringArray) : "";
                            break;
                        case "identifierarray":
                            Identifier[] identifierArray = (Identifier[])value;
                            stringValue =  identifierArray != null ? string.Join(';', identifierArray) : "";
                            break;
                        case "font":
                            stringValue = ((GUIFont)value).Identifier.Value;
                            break;
                        default:
                            stringValue = value.ToString();
                            break;
                    }
                }
                element.GetAttribute(property.Name)?.Remove();
                element.SetAttributeValue(property.Name, stringValue);
            }
        }

        /// <summary>
        /// Upgrade the properties of an entity saved with an older version of the game. Properties that should be upgraded are defined using "Upgrade" elements in the config file.
        /// for example, <Upgrade gameversion="0.9.2.0" scale="0.5"/> would force the scale of the entity to 0.5 if it was saved with a version prior to 0.9.2.0.
        /// </summary>
        /// <param name="entity">The entity to upgrade</param>
        /// <param name="configElement">The XML element to get the upgrade instructions from (e.g. the config of an item prefab)</param>
        /// <param name="savedVersion">The game version the entity was saved with</param>
        public static void UpgradeGameVersion(ISerializableEntity entity, ContentXElement configElement, Version savedVersion)
        {
            foreach (var subElement in configElement.Elements())
            {
                if (!subElement.Name.ToString().Equals("upgrade", StringComparison.OrdinalIgnoreCase)) { continue; }
                var upgradeVersion = new Version(subElement.GetAttributeString("gameversion", "0.0.0.0"));
                if (subElement.GetAttributeBool("campaignsaveonly", false))
                {
                    if ((GameMain.GameSession?.LastSaveVersion ?? GameMain.Version) >= upgradeVersion) { continue; }
                }
                else
                {
                    if (savedVersion >= upgradeVersion) { continue; }
                }
                foreach (XAttribute attribute in subElement.Attributes())
                {
                    var attributeName = attribute.NameAsIdentifier();
                    if (attributeName == "gameversion" || attributeName == "campaignsaveonly") { continue; }

                    if (attributeName == "refreshrect")
                    {
                        if (entity is Structure structure)
                        {
                            if (!structure.ResizeHorizontal)
                            {
                                structure.Rect = structure.DefaultRect = new Rectangle(structure.Rect.X, structure.Rect.Y,
                                    (int)structure.Prefab.ScaledSize.X,
                                    structure.Rect.Height);
                            }
                            if (!structure.ResizeVertical)
                            {
                                structure.Rect = structure.DefaultRect = new Rectangle(structure.Rect.X, structure.Rect.Y,
                                    structure.Rect.Width,
                                    (int)structure.Prefab.ScaledSize.Y);
                            }
                        }
                        else if (entity is Item item)
                        {
                            if (!item.ResizeHorizontal)
                            {
                                item.Rect = item.DefaultRect = new Rectangle(item.Rect.X, item.Rect.Y,
                                    (int)(item.Prefab.Size.X * item.Prefab.Scale),
                                    item.Rect.Height);
                            }
                            if (!item.ResizeVertical)
                            {
                                item.Rect = item.DefaultRect = new Rectangle(item.Rect.X, item.Rect.Y,
                                    item.Rect.Width,
                                    (int)(item.Prefab.Size.Y * item.Prefab.Scale));
                            }
                        }
                    }

                    if (entity.SerializableProperties.TryGetValue(attributeName, out SerializableProperty property))
                    {
                        FixValue(property, entity, attribute);
                        if (property.Name == nameof(ItemComponent.Msg) && entity is ItemComponent component)
                        {
                            component.ParseMsg();
                        }
                    }
                    else if (entity is Item item1)
                    {
                        foreach (ISerializableEntity component in item1.AllPropertyObjects)
                        {
                            if (component.SerializableProperties.TryGetValue(attributeName, out SerializableProperty componentProperty))
                            {
                                FixValue(componentProperty, component, attribute);
                                if (componentProperty.Name == nameof(ItemComponent.Msg))
                                {
                                    ((ItemComponent)component).ParseMsg();
                                }
                            }
                        }
                    }
                }

                static void FixValue(SerializableProperty property, object parentObject, XAttribute attribute)
                {
                    if (attribute.Value.Length > 0 && attribute.Value[0] == '*')
                    {
                        float.TryParse(attribute.Value.Substring(1), NumberStyles.Float, CultureInfo.InvariantCulture, out float multiplier);

                        if (property.PropertyType == typeof(int))
                        {
                            property.TrySetValue(parentObject, (int)(((int)property.GetValue(parentObject)) * multiplier));
                        }
                        else if (property.PropertyType == typeof(float))
                        {
                            property.TrySetValue(parentObject, (float)property.GetValue(parentObject) * multiplier);
                        }
                        else if (property.PropertyType == typeof(Vector2))
                        {
                            property.TrySetValue(parentObject, (Vector2)property.GetValue(parentObject) * multiplier);
                        }
                        else if (property.PropertyType == typeof(Point))
                        {
                            property.TrySetValue(parentObject, ((Point)property.GetValue(parentObject)).Multiply(multiplier));
                        }
                    }
                    else if (attribute.Value.Length > 0 && attribute.Value[0] == '+')
                    {
                        if (property.PropertyType == typeof(int))
                        {
                            float.TryParse(attribute.Value.Substring(1), NumberStyles.Float, CultureInfo.InvariantCulture, out float addition);
                            property.TrySetValue(parentObject, (int)(((int)property.GetValue(parentObject)) + addition));
                        }
                        else if (property.PropertyType == typeof(float))
                        {
                            float.TryParse(attribute.Value.Substring(1), NumberStyles.Float, CultureInfo.InvariantCulture, out float addition);
                            property.TrySetValue(parentObject, (float)property.GetValue(parentObject) + addition);
                        }
                        else if (property.PropertyType == typeof(Vector2))
                        {
                            var addition = XMLExtensions.ParseVector2(attribute.Value.Substring(1));
                            property.TrySetValue(parentObject, (Vector2)property.GetValue(parentObject) + addition);
                        }
                        else if (property.PropertyType == typeof(Point))
                        {
                            var addition = XMLExtensions.ParsePoint(attribute.Value.Substring(1));
                            property.TrySetValue(parentObject, ((Point)property.GetValue(parentObject)) + addition);
                        }
                    }
                    else
                    {
                        property.TrySetValue(parentObject, attribute.Value);
                    }
                }

                if (entity is Item item2)
                {
                    var componentElement = subElement.FirstElement();
                    if (componentElement == null) { continue; }
                    ItemComponent itemComponent = item2.Components.FirstOrDefault(c => c.Name == componentElement.Name.ToString());
                    if (itemComponent == null) { continue; }
                    foreach (XAttribute attribute in componentElement.Attributes())
                    {
                        var attributeName = attribute.NameAsIdentifier();
                        if (itemComponent.SerializableProperties.TryGetValue(attributeName, out SerializableProperty property))
                        {
                            FixValue(property, itemComponent, attribute);
                        }
                    }
                    foreach (var element in componentElement.Elements())
                    {
                        switch (element.Name.ToString().ToLowerInvariant())
                        {
                            case "requireditem":
                            case "requireditems":
                                itemComponent.RequiredItems.Clear();
                                itemComponent.DisabledRequiredItems.Clear();

                                itemComponent.SetRequiredItems(element, allowEmpty: true);
                                break;
                        }
                    }          
                    if (itemComponent is ItemContainer itemContainer &&
                        (componentElement.GetChildElement("containable") != null || componentElement.GetChildElement("subcontainer") != null))
                    {
                        itemContainer.ReloadContainableRestrictions(componentElement);
                    }
                }
            }
        }
    }
}

using Microsoft.Xna.Framework;
using System.Collections.Generic;
using System.Globalization;
using Barotrauma.IO;
using System;
using System.Diagnostics;
using System.Linq;
using System.Xml.Linq;
using Barotrauma.Extensions;

namespace Barotrauma
{
    public enum AnimationType
    {
        NotDefined = 0,
        Walk = 1,
        Run = 2,
        SwimSlow = 3,
        SwimFast = 4,
        Crouch = 5
    }

    abstract class GroundedMovementParams : AnimationParams
    {
        [Header("Legs")]
        [Serialize("1.0, 1.0", IsPropertySaveable.Yes, description: "How big steps the character takes."), Editable(DecimalCount = 2, ValueStep = 0.01f)]
        public Vector2 StepSize
        {
            get;
            set;
        }

        [Header("Standing")]
        [Serialize(0f, IsPropertySaveable.Yes, description: "How high above the ground the character's head is positioned."), Editable(DecimalCount = 2, ValueStep = 0.1f)]
        public float HeadPosition { get; set; }

        [Serialize(0f, IsPropertySaveable.Yes, description: "How high above the ground the character's torso is positioned."), Editable(DecimalCount = 2, ValueStep = 0.1f)]
        public float TorsoPosition { get; set; }

        [Header("Step lift")]
        [Serialize(1f, IsPropertySaveable.Yes, description: "Separate multiplier for the head lift"), Editable(MinValueFloat = 0, MaxValueFloat = 2, ValueStep = 0.1f)]
        public float StepLiftHeadMultiplier { get; set; }

        [Serialize(0f, IsPropertySaveable.Yes, description: "How much the body raises when taking a step."), Editable(MinValueFloat = 0, MaxValueFloat = 100, ValueStep = 0.1f)]
        public float StepLiftAmount { get; set; }

        [Serialize(0.5f, IsPropertySaveable.Yes, description: "When does the body raise when taking a step. The default (0.5) is in the middle of the step."), Editable(MinValueFloat = -1, MaxValueFloat = 1, DecimalCount = 2, ValueStep = 0.1f)]
        public float StepLiftOffset { get; set; }

        [Serialize(2f, IsPropertySaveable.Yes, description: "How frequently the body raises when taking a step. The default is 2 (after every step)."), Editable(MinValueFloat = 0, MaxValueFloat = 10, ValueStep = 0.1f)]
        public float StepLiftFrequency { get; set; }

        [Header("Movement")]
        [Serialize(0.75f, IsPropertySaveable.Yes, description: "The character's movement speed is multiplied with this value when moving backwards."), Editable(MinValueFloat = 0.1f, MaxValueFloat = 0.99f, DecimalCount = 2)]
        public float BackwardsMovementMultiplier { get; set; }
        
        [Serialize(1.0f, IsPropertySaveable.Yes, description: "Adjusts the maximum speed while climbing. The actual speed is affected by the MovementSpeed."), Editable(MinValueFloat = 0.1f, MaxValueFloat = 10f, DecimalCount = 2)]
        public float ClimbSpeed { get; set; }
        
        [Serialize(2.0f, IsPropertySaveable.Yes, description: "Used instead of ClimbSpeed when descending ladders while moving fast (running). Not used if lower than ClimbSpeed."), Editable(MinValueFloat = 0.1f, MaxValueFloat = 10f, DecimalCount = 2)]
        public float SlideSpeed { get; set; }
        
        [Serialize(10.5f, IsPropertySaveable.Yes, description: "Force applied to the main collider, torso and head, when climbing ladders."), Editable(MinValueFloat = 0.1f, MaxValueFloat = 100f, DecimalCount = 1)]
        public float ClimbBodyMoveForce { get; set; }
        
        [Serialize(5.2f, IsPropertySaveable.Yes, description: "Force applied to the hands when climbing ladders."), Editable(MinValueFloat = 0.1f, MaxValueFloat = 100f, DecimalCount = 1)]
        public float ClimbHandMoveForce { get; set; }
        
        [Serialize(10.0f, IsPropertySaveable.Yes, description: "Force applied to the feet when climbing ladders."), Editable(MinValueFloat = 0.1f, MaxValueFloat = 100f, DecimalCount = 1)]
        public float ClimbFootMoveForce { get; set; }
        
        [Serialize(30.0f, IsPropertySaveable.Yes), Editable(MinValueFloat = 0.1f, MaxValueFloat = 100f, DecimalCount = 1)]
        public float ClimbStepHeight { get; set; }
        
        protected override bool Deserialize(XElement element = null)
        {
            if (element.GetAttributeEnum(nameof(AnimationType), AnimationType.NotDefined) is AnimationType.Run)
            {
                // These values were previously hard-coded when running, so we need to set different default values for the run animations, when they are not defined.
                const string climbSpeedName = nameof(ClimbSpeed);
                if (element.GetAttribute(climbSpeedName) == null)
                {
                    element.SetAttribute(climbSpeedName, 2.0f);
                }
                const string climbStepName = nameof(ClimbStepHeight);
                if (element.GetAttribute(climbStepName) == null)
                {
                    element.SetAttribute(climbStepName, 60.0f);
                }
                const string slideSpeedName = nameof(SlideSpeed);
                if (element.GetAttribute(slideSpeedName) == null)
                {
                    element.SetAttribute(slideSpeedName, 4.0f);
                }
            }
            return base.Deserialize(element);
        }
    }

    abstract class SwimParams : AnimationParams
    {
        [Serialize(25.0f, IsPropertySaveable.Yes, description: "Turning speed (or rather a force applied on the main collider to make it turn). Note that you can set a limb-specific steering forces too (additional)."), Editable(MinValueFloat = 0, MaxValueFloat = 1000, ValueStep = 1)]
        public float SteerTorque { get; set; }

        [Serialize(25.0f, IsPropertySaveable.Yes, description: "How much torque is used to move the legs."), Editable(MinValueFloat = 0, MaxValueFloat = 1000, ValueStep = 1)]
        public float LegTorque { get; set; }
    }

    abstract class AnimationParams : EditableParams, IMemorizable<AnimationParams>
    {
        public Identifier SpeciesName { get; private set; }
        public bool IsGroundedAnimation => AnimationType is AnimationType.Walk or AnimationType.Run or AnimationType.Crouch;
        public bool IsSwimAnimation => AnimationType is AnimationType.SwimSlow or AnimationType.SwimFast;

        [Header("General")]
        [Serialize(AnimationType.NotDefined, IsPropertySaveable.Yes), Editable]
        public virtual AnimationType AnimationType { get; protected set; }
        /// <summary>
        /// The cached animations of all the characters that have been loaded.
        /// </summary>
        private static readonly Dictionary<Identifier, Dictionary<string, AnimationParams>> allAnimations = new Dictionary<Identifier, Dictionary<string, AnimationParams>>();

        [Header("Movement")]
        [Serialize(1.0f, IsPropertySaveable.Yes), Editable(DecimalCount = 2, MinValueFloat = 0, MaxValueFloat = Ragdoll.MAX_SPEED, ValueStep = 0.1f)]
        public float MovementSpeed { get; set; }
        
        [Serialize(1.0f, IsPropertySaveable.Yes, description: "The speed of the \"animation cycle\", i.e. how fast the character takes steps or moves the tail/legs/arms (the outcome depends what the clip is about)"), 
        Editable(MinValueFloat = 0, MaxValueFloat = 10, DecimalCount = 2, ValueStep = 0.01f)]
        public float CycleSpeed { get; set; }

        /// <summary>
        /// In degrees.
        /// </summary>
        [Header("Orientation")]
        [Serialize(float.NaN, IsPropertySaveable.Yes), Editable(-360f, 360f)]
        public float HeadAngle
        {
            get => float.IsNaN(HeadAngleInRadians) ? float.NaN : MathHelper.ToDegrees(HeadAngleInRadians);
            set
            {
                if (!float.IsNaN(value))
                {
                    HeadAngleInRadians = MathHelper.ToRadians(value);
                }
            }
        }
        public float HeadAngleInRadians { get; private set; } = float.NaN;

        /// <summary>
        /// In degrees.
        /// </summary>
        [Serialize(float.NaN, IsPropertySaveable.Yes), Editable(-360f, 360f)]
        public float TorsoAngle
        {
            get => float.IsNaN(TorsoAngleInRadians) ? float.NaN : MathHelper.ToDegrees(TorsoAngleInRadians);
            set
            {
                if (!float.IsNaN(value))
                {
                    TorsoAngleInRadians = MathHelper.ToRadians(value);
                }
            }
        }

        public float TorsoAngleInRadians { get; private set; } = float.NaN;

        [Serialize(50.0f, IsPropertySaveable.Yes, description: "How much torque is used to rotate the head to the correct orientation."), Editable(MinValueFloat = 0, MaxValueFloat = 1000, ValueStep = 1)]
        public float HeadTorque { get; set; }

        [Serialize(50.0f, IsPropertySaveable.Yes, description: "How much torque is used to rotate the torso to the correct orientation."), Editable(MinValueFloat = 0, MaxValueFloat = 1000, ValueStep = 1)]
        public float TorsoTorque { get; set; }

        public struct LimbConstantAngleData
        {
            public float ConstantAngle { get; set; }
            public float ConstantTorque { get; set; }

            public LimbConstantAngleData(float constantAngle, float constantTorque)
            {
                ConstantAngle = constantAngle;
                ConstantTorque = constantTorque;
            }
        }
        /// <summary>
        /// Key = limb id, value = LimbConstantAngleData struct with two properties: constant angle and constant torque
        /// </summary>
        public Dictionary<int, LimbConstantAngleData> LimbConstantAnglesData { get; set; } = new Dictionary<int, LimbConstantAngleData>();

        [Serialize(null, IsPropertySaveable.Yes), Editable]
        public string LimbConstantAngles
        {
            get => ParseLimbConstantAngles(LimbConstantAnglesData);
            set => SetLimbConstantAngles(LimbConstantAnglesData, value);
        }

        [Header("Legs")]
        [Serialize(25.0f, IsPropertySaveable.Yes, description: "How much torque is used to rotate the feet to the correct orientation."), Editable(MinValueFloat = 0, MaxValueFloat = 1000, ValueStep = 1)]
        public float FootTorque { get; set; }

        [Header("Arms")]
        [Serialize(1f, IsPropertySaveable.Yes, description: "How much force is used to rotate the arms to the IK position."), Editable(MinValueFloat = 0, MaxValueFloat = 10, DecimalCount = 2)]
        public float ArmIKStrength { get; set; }

        [Serialize(1f, IsPropertySaveable.Yes, description: "How much force is used to rotate the hands to the IK position."), Editable(MinValueFloat = 0, MaxValueFloat = 10, DecimalCount = 2)]
        public float HandIKStrength { get; set; }

        public static string GetDefaultFileName(Identifier speciesName, AnimationType animType) => $"{speciesName.Value.CapitaliseFirstInvariant()}{animType}";
        public static string GetDefaultFile(Identifier speciesName, AnimationType animType) => Barotrauma.IO.Path.Combine(GetFolder(speciesName), $"{GetDefaultFileName(speciesName, animType)}.xml");

        public static string GetFolder(Identifier speciesName)
        {
            CharacterPrefab prefab = CharacterPrefab.FindBySpeciesName(speciesName);
            if (prefab?.ConfigElement == null)
            {
                DebugConsole.ThrowError($"Failed to find config file for '{speciesName}'", contentPackage: prefab?.ContentPackage);
                return string.Empty;
            }
            return GetFolder(prefab.ConfigElement, prefab.FilePath.Value);
        }

        private static string GetFolder(ContentXElement root, string filePath)
        {
            Debug.Assert(filePath != null);
            Debug.Assert(root != null);
            string folder = root.GetChildElement("animations")?.GetAttributeContentPath("folder")?.Value;
            if (string.IsNullOrEmpty(folder) || folder.Equals("default", StringComparison.OrdinalIgnoreCase))
            {
                folder = IO.Path.Combine(IO.Path.GetDirectoryName(filePath), "Animations");
            }
            return folder.CleanUpPathCrossPlatform(correctFilenameCase: true);
        }

        /// <summary>
        /// Selects all file paths that match the specified animation type and filters them alphabetically.
        /// </summary>
        public static IEnumerable<string> FilterAndSortFiles(IEnumerable<string> filePaths, AnimationType type)
        {
            return filePaths.Where(f => AnimationPredicate(f, type)).OrderBy(f => f, StringComparer.OrdinalIgnoreCase);
            
            static bool AnimationPredicate(string filePath, AnimationType type)
            {
                XDocument doc = XMLExtensions.TryLoadXml(filePath);
                if (doc == null) { return false; }
                return doc.GetRootExcludingOverride().GetAttributeEnum("animationtype", AnimationType.NotDefined) == type;
            }
        }

        protected static T GetDefaultAnimParams<T>(Character character, AnimationType animType) where T : AnimationParams, new()
        {
            // Using a null file definition means we are taking a first matching file from the folder.
            return GetAnimParams<T>(character, animType, file: null, throwErrors: true);
        }
        
        protected static T GetAnimParams<T>(Character character, AnimationType animType, Either<string, ContentPath> file, bool throwErrors = true) where T : AnimationParams, new()
        {
            Identifier speciesName = character.SpeciesName;
            Identifier animSpecies = speciesName;
            if (!character.VariantOf.IsEmpty)
            {
                string folder = character.Params.VariantFile?.GetRootExcludingOverride().GetChildElement("animations")?.GetAttributeContentPath("folder", character.Prefab.ContentPackage)?.Value;
                if (folder.IsNullOrEmpty() || folder.Equals("default", StringComparison.OrdinalIgnoreCase))
                {
                    // Use the animations defined in the base definition file.
                    animSpecies = character.Prefab.GetBaseCharacterSpeciesName(speciesName);
                }
            }
            return GetAnimParams<T>(speciesName, animSpecies, fallbackSpecies: character.Prefab.GetBaseCharacterSpeciesName(speciesName), animType, file, throwErrors);
        }
        
        private static readonly List<string> errorMessages = new List<string>();

        private static T GetAnimParams<T>(Identifier speciesName, Identifier animSpecies, Identifier fallbackSpecies, AnimationType animType, Either<string, ContentPath> file, bool throwErrors = true) where T : AnimationParams, new()
        {
            Debug.Assert(!speciesName.IsEmpty);
            Debug.Assert(!animSpecies.IsEmpty);
            ContentPath contentPath = null;
            string fileName = null;
            if (file != null)
            {
                if (!file.TryGet(out fileName))
                {
                    file.TryGet(out contentPath);
                }
                Debug.Assert(!fileName.IsNullOrWhiteSpace() || !contentPath.IsNullOrWhiteSpace());
            }
            ContentPackage contentPackage = contentPath?.ContentPackage ?? CharacterPrefab.FindBySpeciesName(speciesName)?.ContentPackage;
            Debug.Assert(contentPackage != null);
            if (!allAnimations.TryGetValue(speciesName, out Dictionary<string, AnimationParams> animations))
            {
                animations = new Dictionary<string, AnimationParams>();
                allAnimations.Add(speciesName, animations);
            }
            string key = fileName ?? contentPath?.Value ?? GetDefaultFileName(animSpecies, animType);
            if (animations.TryGetValue(key, out AnimationParams anim) && anim.AnimationType == animType)
            {
                // Already cached.
                return (T)anim;
            }
            if (!contentPath.IsNullOrEmpty())
            {
                // Load the animation from path.
                T animInstance = new T();
                if (animInstance.Load(contentPath, speciesName))
                {
                    if (animInstance.AnimationType == animType)
                    {
                        animations.TryAdd(contentPath.Value, animInstance);
                        return animInstance;
                    }
                    else
                    {
                        errorMessages.Add($"[AnimationParams] Animation type mismatch. Expected: {animType}, Actual: {animInstance.AnimationType}. Using the default animation.");
                    }
                }
                else
                {
                    errorMessages.Add($"[AnimationParams] Failed to load an animation {animInstance} of type {animType} from {contentPath.Value} for the character {speciesName}. Using the default animation.");
                }
            }
            // Seek the correct animation from the character's animation folder.
            string selectedFile = null;
            string folder = GetFolder(animSpecies);
            if (Directory.Exists(folder))
            {
                string[] files = Directory.GetFiles(folder);
                if (files.None())
                {
                    errorMessages.Add($"[AnimationParams] Could not find any animation files from the folder: {folder}. Using the default animation.");
                }
                else
                {
                    var filteredFiles = FilterAndSortFiles(files, animType);
                    if (filteredFiles.None())
                    {
                        errorMessages.Add($"[AnimationParams] Could not find any animation files that match the animation type {animType} from the folder: {folder}. Using the default animation.");
                    }
                    else if (string.IsNullOrEmpty(fileName))
                    {
                        // Files found, but none specified -> Get a matching animation from the specified folder.
                        // First try to find a file that matches the default file name. If that fails, just take any file.
                        string defaultFileName = GetDefaultFileName(animSpecies, animType);
                        selectedFile = filteredFiles.FirstOrDefault(path => PathMatchesFile(path, defaultFileName)) ?? filteredFiles.First();
                    }
                    else
                    {
                        selectedFile = filteredFiles.FirstOrDefault(path => PathMatchesFile(path, fileName));
                        if (selectedFile == null)
                        {
                            errorMessages.Add($"[AnimationParams] Could not find an animation file that matches the name {fileName} and the animation type {animType}. Using the default animations.");
                        }
                    }   
                }
            }
            else
            {
                errorMessages.Add($"[AnimationParams] Invalid directory: {folder}. Using the default animation.");
            }
            selectedFile ??= GetDefaultFile(fallbackSpecies, animType);
            Debug.Assert(selectedFile != null);
            if (errorMessages.None())
            {
                DebugConsole.Log($"[AnimationParams] Loading animations from {selectedFile}.");
            }
            T animationInstance = new T();
            if (animationInstance.Load(ContentPath.FromRaw(contentPackage, selectedFile), speciesName))
            {
                animations.TryAdd(key, animationInstance);
            }
            else
            {
                errorMessages.Add($"[AnimationParams] Failed to load an animation {animationInstance} at {selectedFile} of type {animType} for the character {speciesName}");
            }
            foreach (string errorMsg in errorMessages)
            {
                if (throwErrors)
                {
                    DebugConsole.ThrowError(errorMsg, contentPackage: contentPackage);
                }
                else
                {
                    DebugConsole.Log("Logging a supressed (potential) error: " + errorMsg);
                }
            }
            errorMessages.Clear();
            return animationInstance;
            
            static bool PathMatchesFile(string p, string f) => IO.Path.GetFileNameWithoutExtension(p).Equals(f, StringComparison.OrdinalIgnoreCase);
        }

        public static void ClearCache() => allAnimations.Clear();

        public static AnimationParams Create(string fullPath, Identifier speciesName, AnimationType animationType, Type animationParamsType)
        {
            if (animationParamsType == typeof(HumanWalkParams))
            {
                return Create<HumanWalkParams>(fullPath, speciesName, animationType);
            }
            if (animationParamsType == typeof(HumanRunParams))
            {
                return Create<HumanRunParams>(fullPath, speciesName, animationType);
            }
            if (animationParamsType == typeof(HumanSwimSlowParams))
            {
                return Create<HumanSwimSlowParams>(fullPath, speciesName, animationType);
            }
            if (animationParamsType == typeof(HumanSwimFastParams))
            {
                return Create<HumanSwimFastParams>(fullPath, speciesName, animationType);
            }
            if (animationParamsType == typeof(HumanCrouchParams))
            {
                return Create<HumanCrouchParams>(fullPath, speciesName, animationType);
            }
            if (animationParamsType == typeof(FishWalkParams))
            {
                return Create<FishWalkParams>(fullPath, speciesName, animationType);
            }
            if (animationParamsType == typeof(FishRunParams))
            {
                return Create<FishRunParams>(fullPath, speciesName, animationType);
            }
            if (animationParamsType == typeof(FishSwimSlowParams))
            {
                return Create<FishSwimSlowParams>(fullPath, speciesName, animationType);
            }
            if (animationParamsType == typeof(FishSwimFastParams))
            {
                return Create<FishSwimFastParams>(fullPath, speciesName, animationType);
            }
            throw new NotImplementedException(animationParamsType.ToString());
        }

        /// <summary>
        /// Note: Overrides old animations, if found!
        /// </summary>
        public static T Create<T>(string fullPath, Identifier speciesName, AnimationType animationType) where T : AnimationParams, new()
        {
            if (animationType == AnimationType.NotDefined)
            {
                throw new Exception("Cannot create an animation file of type " + animationType);
            }
            if (!allAnimations.TryGetValue(speciesName, out Dictionary<string, AnimationParams> anims))
            {
                anims = new Dictionary<string, AnimationParams>();
                allAnimations.Add(speciesName, anims);
            }
            string fileName = IO.Path.GetFileNameWithoutExtension(fullPath);
            if (anims.ContainsKey(fileName))
            {
                DebugConsole.NewMessage($"[AnimationParams] Removing the old animation of type {animationType}.", Color.Red);
                anims.Remove(fileName);
            }
            var instance = new T();
            XElement animationElement = new XElement(GetDefaultFileName(speciesName, animationType), new XAttribute("animationtype", animationType.ToString()));
            instance.doc = new XDocument(animationElement);
            var characterPrefab = CharacterPrefab.FindBySpeciesName(speciesName);
            Debug.Assert(characterPrefab != null);
            var contentPath = ContentPath.FromRaw(characterPrefab.ContentPackage, fullPath);
            instance.UpdatePath(contentPath);
            instance.IsLoaded = instance.Deserialize(animationElement);
            instance.Save();
            instance.Load(contentPath, speciesName);
            anims.Add(fileName, instance);
            DebugConsole.NewMessage($"[AnimationParams] New animation file of type {animationType} created.", Color.GhostWhite);
            return instance;
        }

        public bool Serialize() => base.Serialize();
        public bool Deserialize() => base.Deserialize();

        protected bool Load(ContentPath file, Identifier speciesName)
        {
            if (Load(file))
            {
                SpeciesName = speciesName;
                return true;
            }
            return false;
        }

        protected override void UpdatePath(ContentPath newPath)
        {
            if (SpeciesName == null)
            {
                base.UpdatePath(newPath);
            }
            else
            {
                // Update the key by removing and re-adding the animation.
                string fileName = FileNameWithoutExtension;
                if (allAnimations.TryGetValue(SpeciesName, out Dictionary<string, AnimationParams> animations))
                {
                    animations.Remove(fileName);
                }
                base.UpdatePath(newPath);
                if (animations != null)
                {
                    if (!animations.ContainsKey(fileName))
                    {
                        animations.Add(fileName, this);
                    }
                }
            }
        }

        protected static string ParseFootAngles(Dictionary<int, float> footAngles)
        {
            //convert to the format "id1:angle,id2:angle,id3:angle"
            return string.Join(",", footAngles.Select(kv => kv.Key + ": " + kv.Value.ToString("G", CultureInfo.InvariantCulture)).ToArray());
        }

        protected static string ParseLimbConstantAngles(Dictionary<int, LimbConstantAngleData> limbAngles)
        {
            //convert to the format "id1:angle;torque,id2:angle;torque"
            return string.Join(",", limbAngles.Select(kv => $"{kv.Key}: {kv.Value.ConstantAngle.ToString("G", CultureInfo.InvariantCulture)};" + $"{kv.Value.ConstantTorque.ToString("G", CultureInfo.InvariantCulture)}"));
        }

        protected static void SetFootAngles(Dictionary<int, float> footAngles, string value)
        {
            footAngles.Clear();
            if (string.IsNullOrEmpty(value))
            {
                return;
            }

            string[] keyValuePairs = value.Split(',');
            foreach (string joinedKvp in keyValuePairs)
            {
                string[] keyValuePair = joinedKvp.Split(':');
                if (keyValuePair.Length != 2 ||
                    !int.TryParse(keyValuePair[0].Trim(), out int limbIndex) ||
                    !float.TryParse(keyValuePair[1].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out float angle))
                {
                    DebugConsole.ThrowError("Failed to parse foot angles (" + value + ")");
                    continue;
                }
                footAngles[limbIndex] = angle;
            }
        }

        protected static void SetLimbConstantAngles(Dictionary<int, LimbConstantAngleData> limbAngles, string value)
        {
            limbAngles.Clear();
            if (string.IsNullOrEmpty(value))
            {
                return;
            }

            string[] keyValuePairs = value.Split(',');
            foreach (string joinedKvp in keyValuePairs)
            {
                string[] keyValuePair = joinedKvp.Split(':');
                if (keyValuePair.Length != 2 ||
                    !int.TryParse(keyValuePair[0].Trim(), out int limbIndex))
                {
                    DebugConsole.ThrowError("Failed to parse limb constant angles (" + value + ")");
                    continue;
                }

                string[] dataValues = keyValuePair[1].Split(';');
                if (dataValues.Length != 2 ||
                    !float.TryParse(dataValues[0].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out float constantAngle) ||
                    !float.TryParse(dataValues[1].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out float constantTorque))
                {
                    DebugConsole.ThrowError($"Failed to parse angle or torque for limb {limbIndex} ({keyValuePair[1]})");
                    continue;
                }

                limbAngles[limbIndex] = new LimbConstantAngleData
                {
                    ConstantAngle = constantAngle,
                    ConstantTorque = constantTorque
                };
            }
        }

        public static Type GetParamTypeFromAnimType(AnimationType type, bool isHumanoid)
        {
            if (isHumanoid)
            {
                return type switch
                {
                    AnimationType.Walk => typeof(HumanWalkParams),
                    AnimationType.Run => typeof(HumanRunParams),
                    AnimationType.Crouch => typeof(HumanCrouchParams),
                    AnimationType.SwimSlow => typeof(HumanSwimSlowParams),
                    AnimationType.SwimFast => typeof(HumanSwimFastParams),
                    _ => throw new NotImplementedException(type.ToString())
                };
            }
            else
            {
                return type switch
                {
                    AnimationType.Walk => typeof(FishWalkParams),
                    AnimationType.Run => typeof(FishRunParams),
                    AnimationType.SwimSlow => typeof(FishSwimSlowParams),
                    AnimationType.SwimFast => typeof(FishSwimFastParams),
                    _ => throw new NotImplementedException(type.ToString())
                };
            }
        }

        #region Memento
        public Memento<AnimationParams> Memento { get; protected set; } = new Memento<AnimationParams>();
        public abstract void StoreSnapshot();
        protected void StoreSnapshot<T>() where T : AnimationParams, new()
        {
            if (doc == null)
            {
                DebugConsole.ThrowError("[AnimationParams] The source XML Document is null!", contentPackage: Path.ContentPackage);
                return;
            }
            Serialize();
            var copy = new T
            {
                IsLoaded = true,
                doc = new XDocument(doc),
                Path = Path
            };
            copy.Deserialize();
            copy.Serialize();
            Memento.Store(copy);
        }
        public void Undo() => Deserialize(Memento.Undo().MainElement);
        public void Redo() => Deserialize(Memento.Redo().MainElement);
        public void ClearHistory() => Memento.Clear();
        #endregion
    }
}

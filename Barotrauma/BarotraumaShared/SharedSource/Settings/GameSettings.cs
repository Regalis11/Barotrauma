#nullable enable
using Barotrauma.Extensions;
using Microsoft.Xna.Framework;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.Linq;
using System.Xml.Linq;
using Barotrauma.IO;
#if CLIENT
using Barotrauma.ClientSource.Settings;
using Barotrauma.Networking;
using Microsoft.Xna.Framework.Input;
#endif

namespace Barotrauma
{
    public enum WindowMode
    {
        Windowed, Fullscreen, BorderlessWindowed
    }

    public enum LosMode
    {
        None = 0,
        Transparent = 1,
        Opaque = 2
    }

    public enum VoiceMode
    {
        Disabled,
        PushToTalk,
        Activity
    }

    public enum EnemyHealthBarMode
    {
        ShowAll,
        BossHealthBarsOnly,
        HideAll
    }
    
    public enum InteractionLabelDisplayMode
    {
        Everything,
        InteractionAvailable,
        LooseItems
    }

    public static class GameSettings
    {
        public struct Config
        {
            public const float DefaultAimAssist = 0.05f;

            public static Config GetDefault()
            {
                Config config = new Config
                {
#if SERVER
                    //server defaults to English, clients get a prompt to select a language
                    Language = TextManager.DefaultLanguage,
#else
                    Language = LanguageIdentifier.None,
#endif
                    SubEditorUndoBuffer = 32,
                    MaxAutoSaves = 8,
                    AutoSaveIntervalSeconds = 300,
                    SubEditorBackground = new Color(13, 37, 69, 255),
                    EnableSplashScreen = true,
                    PauseOnFocusLost = true,
                    RemoteMainMenuContentUrl = "https://www.barotraumagame.com/gamedata/",
                    AimAssistAmount = DefaultAimAssist,
                    ShowEnemyHealthBars = EnemyHealthBarMode.ShowAll,
                    ChatSpeechBubbles = true,
                    InteractionLabelDisplayMode = InteractionLabelDisplayMode.Everything,
                    EnableMouseLook = true,
                    ChatOpen = true,
                    CrewMenuOpen = true,
                    ShowOffensiveServerPrompt = true,
                    TutorialSkipWarning = true,
                    CorpseDespawnDelay = 600,
                    CorpseDespawnDelayPvP = 60,
                    CorpsesPerSubDespawnThreshold = 5,
#if OSX
                    UseDualModeSockets = false,
#else
                    UseDualModeSockets = true,
#endif
                    DisableInGameHints = false,
                    EnableSubmarineAutoSave = true,
                    Graphics = GraphicsSettings.GetDefault(),
                    Audio = AudioSettings.GetDefault(),
#if CLIENT
                    CrossplayChoice = Eos.EosSteamPrimaryLogin.CrossplayChoice.Unknown,
                    DisableGlobalSpamList = false,
                    KeyMap = KeyMapping.GetDefault(),
                    InventoryKeyMap = InventoryKeyMapping.GetDefault()
#endif

                };
#if DEBUG
                config.QuickStartSub = "Humpback".ToIdentifier();
                config.AutomaticQuickStartEnabled = false;
                config.AutomaticCampaignLoadEnabled = false;
                config.TextManagerDebugModeEnabled = false;
                config.ModBreakerMode = false;
#endif
                return config;
            }

            public static Config FromElement(XElement element, in Config? fallback = null)
            {
                Config retVal = fallback ?? GetDefault();
                
                retVal.DeserializeElement(element);
#if SERVER
                //server defaults to English, clients get a prompt to select a language
                if (retVal.Language == LanguageIdentifier.None)
                {
                    retVal.Language = TextManager.DefaultLanguage;
                }
#endif
                //RemoteMainMenuContentUrl gets set to default it left empty - lets allow leaving it empty to make it possible to disable the remote content 
                if (element.GetAttribute("RemoteMainMenuContentUrl")?.Value == string.Empty)
                {
                    retVal.RemoteMainMenuContentUrl = string.Empty;
                }
                retVal.Graphics = GraphicsSettings.FromElements(element.GetChildElements("graphicsmode", "graphicssettings"), retVal.Graphics);
                retVal.Audio = AudioSettings.FromElements(element.GetChildElements("audio"), retVal.Audio);
#if CLIENT
                retVal.KeyMap = new KeyMapping(element.GetChildElements("keymapping"), retVal.KeyMap);
                retVal.InventoryKeyMap = new InventoryKeyMapping(element.GetChildElements("inventorykeymapping"), retVal.InventoryKeyMap);
                retVal.SavedCampaignSettings = element.GetChildElement("campaignsettings");
                LoadSubEditorImages(element);
#endif

                return retVal;
            }

            public LanguageIdentifier Language;
            public bool VerboseLogging;
            public bool SaveDebugConsoleLogs;
            public string SavePath;
            public int SubEditorUndoBuffer;
            public int MaxAutoSaves;
            public int AutoSaveIntervalSeconds;
            public Color SubEditorBackground;
            public bool EnableSplashScreen;
            public bool PauseOnFocusLost;
            public float AimAssistAmount;
            public bool EnableMouseLook;
            public EnemyHealthBarMode ShowEnemyHealthBars;
            public bool ChatSpeechBubbles;
            public InteractionLabelDisplayMode InteractionLabelDisplayMode;
            public bool ChatOpen;
            public bool CrewMenuOpen;
            public bool ShowOffensiveServerPrompt;
            public bool TutorialSkipWarning;
            public int CorpseDespawnDelay;
            public int CorpseDespawnDelayPvP;
            public int CorpsesPerSubDespawnThreshold;
            public bool UseDualModeSockets;
            public bool DisableInGameHints;
            public bool EnableSubmarineAutoSave;
            public Identifier QuickStartSub;
            public string RemoteMainMenuContentUrl;
#if CLIENT
            public Eos.EosSteamPrimaryLogin.CrossplayChoice CrossplayChoice;
            public XElement SavedCampaignSettings;
            public bool DisableGlobalSpamList;
#endif
#if DEBUG
            public bool AutomaticQuickStartEnabled;
            public bool AutomaticCampaignLoadEnabled;
            public bool TestScreenEnabled;
            public bool TextManagerDebugModeEnabled;
            public bool ModBreakerMode;
#endif

            public struct GraphicsSettings
            {
                public static readonly Point MinSupportedResolution = new Point(1024, 540);
                
                public static GraphicsSettings GetDefault()
                {
                    GraphicsSettings gfxSettings = new GraphicsSettings
                    {
                        Display = 0,
                        RadialDistortion = true,
                        InventoryScale = 1.0f,
                        LightMapScale = 1.0f,
                        VisibleLightLimit = 100,
                        TextScale = 1.0f,
                        HUDScale = 1.0f,
                        Specularity = true,
                        ChromaticAberration = true,
                        ParticleLimit = 1500,
                        LosMode = LosMode.Transparent,
                        ScreenShake = 1.0f
                    };
                    gfxSettings.RadialDistortion = true;
                    gfxSettings.CompressTextures = true;
                    gfxSettings.FrameLimit = 300;
                    gfxSettings.VSync = true;
#if DEBUG
                    gfxSettings.DisplayMode = WindowMode.Windowed;
#else
                    gfxSettings.DisplayMode = WindowMode.BorderlessWindowed;
#endif
                    return gfxSettings;
                }

                public static GraphicsSettings FromElements(IEnumerable<XElement> elements, in GraphicsSettings? fallback = null)
                {
                    GraphicsSettings retVal = fallback ?? GetDefault();
                    elements.ForEach(element => retVal.DeserializeElement(element));
                    return retVal;
                }

                public int Display;
                public int Width;
                public int Height;
                public bool VSync;
                public bool CompressTextures;
                public int FrameLimit;
                public WindowMode DisplayMode;
                public int ParticleLimit;
                public bool Specularity;
                public bool ChromaticAberration;
                public LosMode LosMode;
                public float HUDScale;
                public float InventoryScale;
                public float LightMapScale;
                public int VisibleLightLimit;
                public float TextScale;
                public bool RadialDistortion;
                public float ScreenShake;
            }

            [StructSerialization.Skip]
            public GraphicsSettings Graphics;

            public struct AudioSettings
            {
                public static class DeviceNameHandler
                {
                    public static string Read(string s)
                        => System.Xml.XmlConvert.DecodeName(s)!;

                    public static string Write(string s)
                        => System.Xml.XmlConvert.EncodeName(s)!;
                }
                
                public static AudioSettings GetDefault()
                {
                    AudioSettings audioSettings = new AudioSettings
                    {
                        MusicVolume = 0.3f,
                        SoundVolume = 0.5f,
                        UiVolume = 0.3f,
                        VoiceChatVolume = 0.5f,
                        VoiceChatCutoffPrevention = 200,
                        MicrophoneVolume = 5,
                        MuteOnFocusLost = false,
                        DynamicRangeCompressionEnabled = true,
                        UseDirectionalVoiceChat = true,
                        VoipAttenuationEnabled = true,
                        VoiceSetting = VoiceMode.PushToTalk,
                        DisableVoiceChatFilters = false
                    };
                    return audioSettings;
                }

                public static AudioSettings FromElements(IEnumerable<XElement> elements, in AudioSettings? fallback = null)
                {
                    AudioSettings retVal = fallback ?? GetDefault();
                    elements.ForEach(element => retVal.DeserializeElement(element));
                    return retVal;
                }

                public float MusicVolume;
                public float SoundVolume;
                public float UiVolume;
                public float VoiceChatVolume;
                public int VoiceChatCutoffPrevention;
                public float MicrophoneVolume;
                public bool MuteOnFocusLost;
                public bool DynamicRangeCompressionEnabled;
                public bool UseDirectionalVoiceChat;
                public bool VoipAttenuationEnabled;
                public VoiceMode VoiceSetting;
                
                [StructSerialization.Handler(typeof(DeviceNameHandler))]
                public string AudioOutputDevice;
                [StructSerialization.Handler(typeof(DeviceNameHandler))]
                public string VoiceCaptureDevice;
                
                public float NoiseGateThreshold;
                public bool DisableVoiceChatFilters;
            }

            [StructSerialization.Skip]
            public AudioSettings Audio;

#if CLIENT
            public struct KeyMapping
            {
                private readonly static ImmutableDictionary<InputType, KeyOrMouse> DefaultsQwerty =
                    new Dictionary<InputType, KeyOrMouse>()
                    {
                        { InputType.Run, Keys.LeftShift },
                        { InputType.ToggleRun, Keys.None },
                        { InputType.Attack, Keys.R },
                        { InputType.Crouch, Keys.LeftControl },
                        { InputType.Grab, Keys.G },
                        { InputType.Health, Keys.H },
                        { InputType.Ragdoll, Keys.Space },
                        { InputType.Aim, MouseButton.SecondaryMouse },
                        { InputType.DropItem, Keys.None },

                        { InputType.InfoTab, Keys.Tab },
                        { InputType.Chat, Keys.None },
                        { InputType.RadioChat, Keys.None },
                        { InputType.ActiveChat, Keys.T },
                        { InputType.CrewOrders, Keys.C },
                        { InputType.ChatBox, Keys.B }, 

                        { InputType.Voice, Keys.V },
                        { InputType.RadioVoice, Keys.None },
                        { InputType.LocalVoice, Keys.None },
                        { InputType.ToggleChatMode, Keys.R },
                        { InputType.Command, MouseButton.MiddleMouse },
                        { InputType.ContextualCommand, Keys.LeftShift },
                        { InputType.PreviousFireMode, MouseButton.MouseWheelDown },
                        { InputType.NextFireMode, MouseButton.MouseWheelUp },

                        { InputType.TakeHalfFromInventorySlot, Keys.LeftShift },
                        { InputType.TakeOneFromInventorySlot, Keys.LeftControl },

                        { InputType.Up, Keys.W },
                        { InputType.Down, Keys.S },
                        { InputType.Left, Keys.A },
                        { InputType.Right, Keys.D },
                        { InputType.ToggleInventory, Keys.Q },

                        { InputType.SelectNextCharacter, Keys.Z },
                        { InputType.SelectPreviousCharacter, Keys.X },

                        { InputType.Use, Keys.E },
                        { InputType.Select, MouseButton.PrimaryMouse },
                        { InputType.Deselect, MouseButton.SecondaryMouse },
                        { InputType.Shoot, MouseButton.PrimaryMouse },
                        { InputType.ShowInteractionLabels, Keys.LeftAlt }
                }.ToImmutableDictionary();

                public static KeyMapping GetDefault() => new KeyMapping
                {
                    Bindings = DefaultsQwerty
                        .Select(kvp =>
                            (kvp.Key, kvp.Value.MouseButton == MouseButton.None
                                ? (KeyOrMouse)Keyboard.QwertyToCurrentLayout(kvp.Value.Key)
                                : (KeyOrMouse)kvp.Value.MouseButton))
                        .ToImmutableDictionary()
                };

                public KeyMapping(IEnumerable<XElement> elements, in KeyMapping? fallback)
                {
                    var defaultBindings = GetDefault().Bindings;
                    Dictionary<InputType, KeyOrMouse> bindings = fallback?.Bindings?.ToMutable() ?? defaultBindings.ToMutable();
                    foreach (InputType inputType in (InputType[])Enum.GetValues(typeof(InputType)))
                    {
                        if (!bindings.ContainsKey(inputType))
                        {
                            bindings.Add(inputType, defaultBindings[inputType]);
                        }
                    }

                    Dictionary<InputType, KeyOrMouse> savedBindings = new Dictionary<InputType, KeyOrMouse>();
                    bool playerConfigContainsNewChatBinds = false;
                    bool playerConfigContainsRestoredVoipBinds = false;
                    foreach (XElement element in elements)
                    {
                        foreach (XAttribute attribute in element.Attributes())
                        {
                            if (Enum.TryParse(attribute.Name.LocalName, out InputType result))
                            {
                                playerConfigContainsNewChatBinds |= result == InputType.ActiveChat;
                                playerConfigContainsRestoredVoipBinds |= result == InputType.RadioVoice;
                                var keyOrMouse = element.GetAttributeKeyOrMouse(attribute.Name.LocalName, bindings[result]);
                                savedBindings.Add(result, keyOrMouse);
                                bindings[result] = keyOrMouse;
                            }
                        }
                    }

                    // Check for duplicate binds when introducing new binds
                    foreach (var defaultBinding in defaultBindings)
                    {
                        if (!IsSetToNone(defaultBinding.Value) && !savedBindings.ContainsKey(defaultBinding.Key))
                        {
                            foreach (var savedBinding in savedBindings)
                            {
                                if (savedBinding.Key is InputType.Run or InputType.TakeHalfFromInventorySlot &&
                                    defaultBinding.Key == InputType.ContextualCommand)
                                {
                                    //run and contextual commands have always defaulted to Shift, but the latter used to be hard-coded.
                                    //don't show a warning about those being bound to the same key
                                    continue;
                                }
                                if (savedBinding.Value == defaultBinding.Value)
                                {
                                    OnGameMainHasLoaded += () =>
                                    {
                                        (string, string)[] replacements =
                                        {
                                            ("[defaultbind]", $"\"{TextManager.Get($"inputtype.{defaultBinding.Key}")}\""),
                                            ("[savedbind]", $"\"{TextManager.Get($"inputtype.{savedBinding.Key}")}\""),
                                            ("[key]", $"\"{defaultBinding.Value.Name}\"")
                                        };
                                        new GUIMessageBox(TextManager.Get("warning"), TextManager.GetWithVariables("duplicatebindwarning", replacements));
                                    };
                                    break;
                                }
                            }
                        }

                        static bool IsSetToNone(KeyOrMouse keyOrMouse) => keyOrMouse == Keys.None && keyOrMouse == MouseButton.None;
                    }

                    // Clear the old chat binds for configs saved before the introduction of the new chat binds
                    if (!playerConfigContainsNewChatBinds)
                    {
                        bindings[InputType.Chat] = Keys.None;
                        bindings[InputType.RadioChat] = Keys.None;
                    }

                    // Clear old VOIP binds to make sure we have no overlapping binds
                    if (!playerConfigContainsRestoredVoipBinds)
                    {
                        bindings[InputType.LocalVoice] = Keys.None;
                        bindings[InputType.RadioVoice] = Keys.None;
                    }

                    Bindings = bindings.ToImmutableDictionary();
                }

                public KeyMapping WithBinding(InputType type, KeyOrMouse bind)
                {
                    KeyMapping newMapping = this;
                    newMapping.Bindings = newMapping.Bindings
                        .Select(kvp =>
                            kvp.Key == type
                                ? (type, bind)
                                : (kvp.Key, kvp.Value))
                        .ToImmutableDictionary();
                    return newMapping;
                }
                
                public ImmutableDictionary<InputType, KeyOrMouse> Bindings;

                public LocalizedString KeyBindText(InputType inputType) => Bindings[inputType].Name;
            }

            [StructSerialization.Skip]
            public KeyMapping KeyMap;

            public struct InventoryKeyMapping
            {
                public ImmutableArray<KeyOrMouse> Bindings;

                public static InventoryKeyMapping GetDefault() => new InventoryKeyMapping
                {
                    Bindings = new KeyOrMouse[]
                    {
                        Keys.D1,
                        Keys.D2,
                        Keys.D3,
                        Keys.D4,
                        Keys.D5,
                        Keys.D6,
                        Keys.D7,
                        Keys.D8,
                        Keys.D9,
                        Keys.D0,
                    }.ToImmutableArray()
                };

                public InventoryKeyMapping WithBinding(int index, KeyOrMouse keyOrMouse)
                {
                    var thisBindings = Bindings;
                    return new InventoryKeyMapping()
                    {
                        Bindings = Enumerable.Range(0, thisBindings.Length)
                            .Select(i => i == index ? keyOrMouse : thisBindings[i])
                            .ToImmutableArray()
                    };
                }

                public InventoryKeyMapping(IEnumerable<XElement> elements, InventoryKeyMapping? fallback)
                {
                    var bindings = (fallback?.Bindings ?? GetDefault().Bindings).ToArray();
                    foreach (XElement element in elements)
                    {
                        for (int i = 0; i < bindings.Length; i++)
                        {
                            bindings[i] = element.GetAttributeKeyOrMouse($"slot{i}", bindings[i]);
                        }
                    }
                    Bindings = bindings.ToImmutableArray();
                }
            }

            [StructSerialization.Skip]
            public InventoryKeyMapping InventoryKeyMap;
#endif
        }

        public const string PlayerConfigPath = "config_player.xml";

        private static Config currentConfig;
        public static ref readonly Config CurrentConfig => ref currentConfig;

#if CLIENT
        public static Action? OnGameMainHasLoaded;
#endif

        public static void Init()
        {
            XDocument? currentConfigDoc = null;

            if (File.Exists(PlayerConfigPath))
            {
                currentConfigDoc = XMLExtensions.TryLoadXml(PlayerConfigPath);
            }

            if (currentConfigDoc != null)
            {
                currentConfig = Config.FromElement(currentConfigDoc.Root ?? throw new NullReferenceException("Config XML element is invalid: document is null."));
#if CLIENT
                ServerListFilters.Init(currentConfigDoc.Root.GetChildElement("serverfilters"));
                MultiplayerPreferences.Init(
                    currentConfigDoc.Root.GetChildElement("player"),
                    currentConfigDoc.Root.GetChildElement("gameplay")?.GetChildElement("jobpreferences"));
                IgnoredHints.Init(currentConfigDoc.Root.GetChildElement("ignoredhints"));
                DebugConsoleMapping.Init(currentConfigDoc.Root.GetChildElement("debugconsolemapping"));
                CompletedTutorials.Init(currentConfigDoc.Root.GetChildElement("tutorials"));
#endif
            }
            else
            {
                currentConfig = Config.GetDefault();
                SaveCurrentConfig();
            }
        }

        public static void SetCurrentConfig(in Config newConfig)
        {
            bool resolutionChanged = 
                currentConfig.Graphics.Width != newConfig.Graphics.Width || 
                currentConfig.Graphics.Height != newConfig.Graphics.Height;
            bool languageChanged = currentConfig.Language != newConfig.Language;
            bool audioOutputChanged = currentConfig.Audio.AudioOutputDevice != newConfig.Audio.AudioOutputDevice;
            bool voiceCaptureChanged = currentConfig.Audio.VoiceCaptureDevice != newConfig.Audio.VoiceCaptureDevice;
            bool textScaleChanged = Math.Abs(currentConfig.Graphics.TextScale - newConfig.Graphics.TextScale) > MathF.Pow(2.0f, -7);

            bool hudScaleChanged = !MathUtils.NearlyEqual(currentConfig.Graphics.HUDScale, newConfig.Graphics.HUDScale);

            bool setGraphicsMode =
                resolutionChanged ||
                currentConfig.Graphics.VSync != newConfig.Graphics.VSync ||
                currentConfig.Graphics.DisplayMode != newConfig.Graphics.DisplayMode ||
                currentConfig.Graphics.Display != newConfig.Graphics.Display;

#if CLIENT
            bool keybindsChanged = false;
            foreach (var kvp in newConfig.KeyMap.Bindings)
            {
                if (!currentConfig.KeyMap.Bindings.TryGetValue(kvp.Key, out var existingBinding) ||
                    existingBinding != kvp.Value)
                {
                    keybindsChanged = true;
                    break;
                }
            }
#endif

            currentConfig = newConfig;

#if CLIENT
            if (setGraphicsMode)
            {
                GameMain.Instance.ApplyGraphicsSettings(recalculateFontsAndStyles: true);
            }
            else if (textScaleChanged)
            {
                GUIStyle.RecalculateFonts();
            }

            if (audioOutputChanged)
            {
                GameMain.SoundManager?.InitializeAlcDevice(currentConfig.Audio.AudioOutputDevice);
            }

            if (voiceCaptureChanged)
            {
                VoipCapture.ChangeCaptureDevice(currentConfig.Audio.VoiceCaptureDevice);
            }

            if (hudScaleChanged)
            {
                HUDLayoutSettings.CreateAreas();
                GameMain.GameSession?.HUDScaleChanged();
            }

            if (keybindsChanged)
            {
                foreach (var item in Item.ItemList)
                {
                    foreach (var ic in item.Components)
                    {
                        //parse messages because they may contain keybind texts
                        ic.ParseMsg();
                    }
                }
            }

            GameMain.SoundManager?.ApplySettings();
#endif
            if (languageChanged) { TextManager.ClearCache(); }
        }

        public static void SaveCurrentConfig()
        {
            XDocument configDoc = new XDocument();
            XElement root = new XElement("config"); configDoc.Add(root);
            currentConfig.SerializeElement(root);

            XElement graphicsElement = new XElement("graphicssettings"); root.Add(graphicsElement);
            currentConfig.Graphics.SerializeElement(graphicsElement);

            XElement audioElement = new XElement("audio"); root.Add(audioElement);
            currentConfig.Audio.SerializeElement(audioElement);

            XElement contentPackagesElement = new XElement("contentpackages"); root.Add(contentPackagesElement);
            XComment corePackageComment = new XComment(ContentPackageManager.EnabledPackages.Core?.Name ?? "Vanilla"); contentPackagesElement.Add(corePackageComment);
            XElement corePackageElement = new XElement(ContentPackageManager.CorePackageElementName); contentPackagesElement.Add(corePackageElement);
            corePackageElement.SetAttributeValue("path", ContentPackageManager.EnabledPackages.Core?.Path ?? ContentPackageManager.VanillaFileList);

            XElement regularPackagesElement = new XElement(ContentPackageManager.RegularPackagesElementName); contentPackagesElement.Add(regularPackagesElement);
            foreach (var regularPackage in ContentPackageManager.EnabledPackages.Regular)
            {
                XComment packageComment = new XComment(regularPackage.Name); regularPackagesElement.Add(packageComment);
                XElement packageElement = new XElement(ContentPackageManager.RegularPackagesSubElementName); regularPackagesElement.Add(packageElement);
                packageElement.SetAttributeValue("path", regularPackage.Path);
            }

#if CLIENT
            XElement serverFiltersElement = new XElement("serverfilters"); root.Add(serverFiltersElement);
            ServerListFilters.Instance.SaveTo(serverFiltersElement);

            XElement characterElement = new XElement("player"); root.Add(characterElement);
            MultiplayerPreferences.Instance.SaveTo(characterElement);

            XElement ignoredHintsElement = new XElement("ignoredhints"); root.Add(ignoredHintsElement);
            IgnoredHints.Instance.SaveTo(ignoredHintsElement);

            XElement debugConsoleMappingElement = new XElement("debugconsolemapping"); root.Add(debugConsoleMappingElement);
            DebugConsoleMapping.Instance.SaveTo(debugConsoleMappingElement);
            
            XElement tutorialsElement = new XElement("tutorials"); root.Add(tutorialsElement);
            CompletedTutorials.Instance.SaveTo(tutorialsElement);
            
            XElement keyMappingElement = new XElement("keymapping",
                currentConfig.KeyMap.Bindings.Select(kvp
                    => new XAttribute(kvp.Key.ToString(), kvp.Value.ToString())));
            root.Add(keyMappingElement);

            XElement inventoryKeyMappingElement = new XElement("inventorykeymapping",
                Enumerable.Range(0, currentConfig.InventoryKeyMap.Bindings.Length)
                    .Zip(currentConfig.InventoryKeyMap.Bindings)
                    .Cast<(int Index, KeyOrMouse Bind)>()
                    .Select(kvp
                        => new XAttribute($"slot{kvp.Index.ToString(CultureInfo.InvariantCulture)}", kvp.Bind.ToString())));
            root.Add(inventoryKeyMappingElement);

            SubEditorScreen.ImageManager.Save(root);

            root.Add(CampaignSettings.CurrentSettings.Save());
#endif

            configDoc.SaveSafe(PlayerConfigPath);
            
            System.Xml.XmlWriterSettings settings = new System.Xml.XmlWriterSettings
            {
                Indent = true,
                OmitXmlDeclaration = true,
                NewLineOnAttributes = true
            };

            try
            {
                using (var writer = XmlWriter.Create(PlayerConfigPath, settings))
                {
                    configDoc.WriteTo(writer);
                    writer.Flush();
                }
            }
            catch (Exception e)
            {
                DebugConsole.ThrowError("Saving game settings failed.", e);
                GameAnalyticsManager.AddErrorEventOnce("GameSettings.Save:SaveFailed", GameAnalyticsManager.ErrorSeverity.Error,
                    "Saving game settings failed.\n" + e.Message + "\n" + e.StackTrace.CleanupStackTrace());
            }
        }

#if CLIENT
        private static void LoadSubEditorImages(XElement configElement)
        {
            XElement? element = configElement?.Element("editorimages");
            if (element == null)
            {
                SubEditorScreen.ImageManager.Clear(alsoPending: true);
                return;
            }
            SubEditorScreen.ImageManager.Load(element);
        }
#endif
    }
}
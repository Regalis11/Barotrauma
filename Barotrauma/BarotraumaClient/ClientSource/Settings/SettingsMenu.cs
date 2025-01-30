#nullable enable
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.Linq;
using Barotrauma.Eos;
using Barotrauma.Extensions;
using Barotrauma.Networking;
using Barotrauma.Sounds;
using Barotrauma.Steam;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using OpenAL;

namespace Barotrauma
{
    sealed class SettingsMenu
    {
        public static SettingsMenu? Instance { get; private set; }
        
        public enum Tab
        {
            Graphics,
            AudioAndVC,
            Controls,
            Gameplay,
            Mods
        }

        public Tab CurrentTab { get; private set; }

        private GameSettings.Config unsavedConfig;

        private readonly GUIFrame mainFrame;
        
        private readonly GUILayoutGroup tabber;
        private readonly GUIFrame contentFrame;
        private readonly GUILayoutGroup bottom;

        public readonly WorkshopMenu WorkshopMenu;

        private static readonly ImmutableHashSet<InputType> LegacyInputTypes = new List<InputType>()
        {
            InputType.Chat,
            InputType.RadioChat,
            InputType.LocalVoice,
            InputType.RadioVoice,
        }.ToImmutableHashSet();

        public static SettingsMenu Create(RectTransform mainParent)
        {
            Instance?.Close();
            Instance = new SettingsMenu(mainParent);
            return Instance;
        }

        private SettingsMenu(RectTransform mainParent, GameSettings.Config setConfig = default)
        {
            unsavedConfig = GameSettings.CurrentConfig;
            
            mainFrame = new GUIFrame(new RectTransform(Vector2.One, mainParent));

            var mainLayout = new GUILayoutGroup(new RectTransform(Vector2.One * 0.95f, mainFrame.RectTransform, Anchor.Center, Pivot.Center),
                isHorizontal: false, childAnchor: Anchor.TopRight);

            new GUITextBlock(new RectTransform((1.0f, 0.07f), mainLayout.RectTransform), TextManager.Get("Settings"),
                font: GUIStyle.LargeFont);

            var tabberAndContentLayout = new GUILayoutGroup(new RectTransform((1.0f, 0.86f), mainLayout.RectTransform),
                isHorizontal: true);
            
            void tabberPadding()
                => new GUIFrame(new RectTransform((0.01f, 1.0f), tabberAndContentLayout.RectTransform), style: null);

            tabberPadding();
            tabber = new GUILayoutGroup(new RectTransform((0.06f, 1.0f), tabberAndContentLayout.RectTransform), isHorizontal: false) { AbsoluteSpacing = GUI.IntScale(5f) };
            tabberPadding();
            tabContents = new Dictionary<Tab, (GUIButton Button, GUIFrame Content)>();

            contentFrame = new GUIFrame(new RectTransform((0.92f, 1.0f), tabberAndContentLayout.RectTransform),
                style: "InnerFrame");

            bottom = new GUILayoutGroup(new RectTransform((contentFrame.RectTransform.RelativeSize.X, 0.04f), mainLayout.RectTransform), isHorizontal: true) { Stretch = true, RelativeSpacing = 0.01f };

            CreateGraphicsTab();
            CreateAudioAndVCTab();
            CreateControlsTab();
            CreateGameplayTab();
            CreateModsTab(out WorkshopMenu);

            CreateBottomButtons();
            
            SelectTab(Tab.Graphics);
            
            tabber.Recalculate();
        }

        private void SwitchContent(GUIFrame newContent)
        {
            contentFrame.Children.ForEach(c => c.Visible = false);
            newContent.Visible = true;
        }

        private readonly Dictionary<Tab, (GUIButton Button, GUIFrame Content)> tabContents;
        
        public void SelectTab(Tab tab)
        {
            if (tab == Tab.AudioAndVC && CurrentDeviceMismatchesDisplayed())
            {
                CreateAudioAndVCTab(refresh: true);
            }
            
            CurrentTab = tab;
            SwitchContent(tabContents[tab].Content);
            tabber.Children.ForEach(c =>
            {
                if (c is GUIButton btn) { btn.Selected = btn == tabContents[tab].Button; }
            });
        }

        private void AddButtonToTabber(Tab tab, GUIFrame content)
        {
            var button = new GUIButton(new RectTransform(Vector2.One, tabber.RectTransform, Anchor.TopLeft, Pivot.TopLeft, scaleBasis: ScaleBasis.Smallest), "", style: $"SettingsMenuTab.{tab}")
            {
                ToolTip = TextManager.Get($"SettingsTab.{tab}"),
                OnClicked = (b, _) =>
                {
                    SelectTab(tab);
                    return false;
                }
            };
            button.RectTransform.MaxSize = RectTransform.MaxPoint;
            button.Children.ForEach(c => c.RectTransform.MaxSize = RectTransform.MaxPoint);
            
            tabContents.Add(tab, (button, content));
        }

        private GUIFrame CreateNewContentFrame(Tab tab)
        {
            if (tabContents.TryGetValue(tab, out (GUIButton Button, GUIFrame Content) tabContent))
            {
                return tabContent.Content;
            }

            var content = new GUIFrame(new RectTransform(Vector2.One * 0.95f, contentFrame.RectTransform, Anchor.Center, Pivot.Center), style: null);
            AddButtonToTabber(tab, content);
            return content;
        }

        private static (GUILayoutGroup Left, GUILayoutGroup Right) CreateSidebars(GUIFrame parent, bool split = false)
        {
            GUILayoutGroup layout = new GUILayoutGroup(new RectTransform(Vector2.One, parent.RectTransform), isHorizontal: true);
            GUILayoutGroup left = new GUILayoutGroup(new RectTransform((0.4875f, 1.0f), layout.RectTransform), isHorizontal: false);
            var centerFrame = new GUIFrame(new RectTransform((0.025f, 1.0f), layout.RectTransform), style: null);
            if (split)
            {
                new GUICustomComponent(new RectTransform(Vector2.One, centerFrame.RectTransform),
                    onDraw: (sb, c) =>
                    {
                        sb.DrawLine((c.Rect.Center.X, c.Rect.Top),(c.Rect.Center.X, c.Rect.Bottom), GUIStyle.TextColorDim, 2f);
                    });
            }
            GUILayoutGroup right = new GUILayoutGroup(new RectTransform((0.4875f, 1.0f), layout.RectTransform), isHorizontal: false);
            return (left, right);
        }

        private static GUILayoutGroup CreateCenterLayout(GUIFrame parent)
        {
            return new GUILayoutGroup(new RectTransform((0.5f, 1.0f), parent.RectTransform, Anchor.TopCenter, Pivot.TopCenter)) { ChildAnchor = Anchor.TopCenter };
        }

        private static RectTransform NewItemRectT(GUILayoutGroup parent)
            => new RectTransform((1.0f, 0.06f), parent.RectTransform, Anchor.CenterLeft);

        private static void Spacer(GUILayoutGroup parent)
        {
            new GUIFrame(new RectTransform((1.0f, 0.03f), parent.RectTransform, Anchor.CenterLeft), style: null);
        }

        private static void Divider(GUILayoutGroup parent)
        {
            new GUICustomComponent(new RectTransform((1.0f, 0.03f), parent.RectTransform, Anchor.CenterLeft), onDraw: (sb, c) => sb.DrawLine((c.Rect.Left, c.Rect.Center.Y), (c.Rect.Right, c.Rect.Center.Y), GUIStyle.TextColorDim, 2f));
        }

        private static GUITextBlock Label(GUILayoutGroup parent, LocalizedString str, GUIFont font)
        {
            return new GUITextBlock(NewItemRectT(parent), str, font: font);
        }

        private static void DropdownEnum<T>(GUILayoutGroup parent, Func<T, LocalizedString> textFunc, Func<T, LocalizedString>? tooltipFunc, T currentValue,
            Action<T> setter) where T : Enum
            => Dropdown(parent, textFunc, tooltipFunc, (T[])Enum.GetValues(typeof(T)), currentValue, setter);
        
        private static GUIDropDown Dropdown<T>(GUILayoutGroup parent, Func<T, LocalizedString> textFunc, Func<T, LocalizedString>? tooltipFunc, IReadOnlyList<T> values, T currentValue, Action<T> setter)
        {
            var dropdown = new GUIDropDown(NewItemRectT(parent), elementCount: values.Count);
            values.ForEach(v => dropdown.AddItem(text: textFunc(v), userData: v, toolTip: tooltipFunc?.Invoke(v) ?? null));
            int childIndex = values.IndexOf(currentValue);
            dropdown.Select(childIndex);
            dropdown.ListBox.ForceLayoutRecalculation();
            dropdown.ListBox.ScrollToElement(dropdown.ListBox.Content.GetChild(childIndex));
            dropdown.OnSelected = (dd, obj) =>
            {
                setter((T)obj);
                return true;
            };
            return dropdown;
        }

        private static (GUIScrollBar slider, GUITextBlock label) Slider(GUILayoutGroup parent, Vector2 range, int steps, Func<float, string> labelFunc, float currentValue, Action<float> setter, LocalizedString? tooltip = null)
        {
            var layout = new GUILayoutGroup(NewItemRectT(parent), isHorizontal: true);
            var slider = new GUIScrollBar(new RectTransform((0.72f, 1.0f), layout.RectTransform), style: "GUISlider")
            {
                Range = range,
                BarScrollValue = currentValue,
                Step = 1.0f / (float)(steps - 1),
                BarSize = 1.0f / steps
            };
            if (tooltip != null)
            {
                slider.ToolTip = tooltip;
            }
            var label = new GUITextBlock(new RectTransform((0.28f, 1.0f), layout.RectTransform),
                labelFunc(currentValue), wrap: false, textAlignment: Alignment.Center);
            slider.OnMoved = (sb, val) =>
            {
                label.Text = labelFunc(sb.BarScrollValue);
                setter(sb.BarScrollValue);
                return true;
            };
            return (slider, label);
        }

        private static GUITickBox Tickbox(GUILayoutGroup parent, LocalizedString label, LocalizedString tooltip, bool currentValue, Action<bool> setter)
        {
            return new GUITickBox(NewItemRectT(parent), label)
            {
                Selected = currentValue,
                ToolTip = tooltip,
                OnSelected = (tb) =>
                {
                    setter(tb.Selected);
                    return true;
                }
            };
        }

        private string Percentage(float v) => ToolBox.GetFormattedPercentage(v);

        private static int Round(float v) => MathUtils.RoundToInt(v);
        
        private void CreateGraphicsTab()
        {
            GUIFrame content = CreateNewContentFrame(Tab.Graphics);

            var (left, right) = CreateSidebars(content);

            List<(int Width, int Height)> supportedResolutions =
                GameMain.GraphicsDeviceManager.GraphicsDevice.Adapter.SupportedDisplayModes
                    .Where(m => m.Format == SurfaceFormat.Color)
                    .Select(m => (m.Width, m.Height))
                    .Where(m => m.Width >= GameSettings.Config.GraphicsSettings.MinSupportedResolution.X
                        && m.Height >= GameSettings.Config.GraphicsSettings.MinSupportedResolution.Y)
                    .ToList();
            var currentResolution = (unsavedConfig.Graphics.Width, unsavedConfig.Graphics.Height);
            if (!supportedResolutions.Contains(currentResolution))
            {
                supportedResolutions.Add(currentResolution);
            }
            
            Label(left, TextManager.Get("Resolution"), GUIStyle.SubHeadingFont);
            Dropdown(left, (m) => $"{m.Width}x{m.Height}", null, supportedResolutions, currentResolution,
                (res) =>
                {
                    unsavedConfig.Graphics.Width = res.Width;
                    unsavedConfig.Graphics.Height = res.Height;
                });
            Spacer(left);

            Label(left, TextManager.Get("DisplayMode"), GUIStyle.SubHeadingFont);
            DropdownEnum(left, (m) => TextManager.Get($"{m}"), null, unsavedConfig.Graphics.DisplayMode, v => unsavedConfig.Graphics.DisplayMode = v);
            Spacer(left);

            var displayLabel = Label(left, TextManager.Get("TargetDisplay"), GUIStyle.SubHeadingFont);
            displayLabel.ToolTip = TextManager.Get("TargetDisplay.Tooltip");
            Dropdown(left, m => TextManager.GetWithVariables(m == 0 ? "PrimaryDisplayFormat" : "SecondaryDisplayFormat", ("[num]", m.ToString()), ("[name]", Display.GetDisplayName(m))), null, Enumerable.Range(0, Display.GetNumberOfDisplays()).ToArray(), unsavedConfig.Graphics.Display, v => unsavedConfig.Graphics.Display = v);
            Spacer(left);

            Tickbox(left, TextManager.Get("EnableVSync"), TextManager.Get("EnableVSyncTooltip"), unsavedConfig.Graphics.VSync, v => unsavedConfig.Graphics.VSync = v);
            Tickbox(left, TextManager.Get("EnableTextureCompression"), TextManager.Get("EnableTextureCompressionTooltip"), unsavedConfig.Graphics.CompressTextures, v => unsavedConfig.Graphics.CompressTextures = v);
            Divider(left);

            Label(left, TextManager.Get("ScreenShake"), GUIStyle.SubHeadingFont);
            Slider(left, (0.0f, 1.0f), 11, v => TextManager.GetWithVariable("percentageformat", "[value]", Round(v * 100).ToString()).Value, unsavedConfig.Graphics.ScreenShake, v => unsavedConfig.Graphics.ScreenShake = v);

            Label(right, TextManager.Get("LOSEffect"), GUIStyle.SubHeadingFont);
            DropdownEnum(right, (m) => TextManager.Get($"LosMode{m}"), null, unsavedConfig.Graphics.LosMode, v => unsavedConfig.Graphics.LosMode = v);
            Spacer(right);

            Label(right, TextManager.Get("LightMapScale"), GUIStyle.SubHeadingFont);
            Slider(right, (0.5f, 1.0f), 11, v => TextManager.GetWithVariable("percentageformat", "[value]", Round(v * 100).ToString()).Value, unsavedConfig.Graphics.LightMapScale, v => unsavedConfig.Graphics.LightMapScale = v, TextManager.Get("LightMapScaleTooltip"));
            Spacer(right);

            Label(right, TextManager.Get("VisibleLightLimit"), GUIStyle.SubHeadingFont);
            Slider(right, (10, 510), 21, v => v > 500 ? TextManager.Get("unlimited").Value : Round(v).ToString(), unsavedConfig.Graphics.VisibleLightLimit, 
                v =>  unsavedConfig.Graphics.VisibleLightLimit = v > 500 ? int.MaxValue : Round(v), TextManager.Get("VisibleLightLimitTooltip"));
            Spacer(right);

            Tickbox(right, TextManager.Get("RadialDistortion"), TextManager.Get("RadialDistortionTooltip"), unsavedConfig.Graphics.RadialDistortion, v => unsavedConfig.Graphics.RadialDistortion = v);
            Tickbox(right, TextManager.Get("ChromaticAberration"), TextManager.Get("ChromaticAberrationTooltip"), unsavedConfig.Graphics.ChromaticAberration, v => unsavedConfig.Graphics.ChromaticAberration = v);

            Label(right, TextManager.Get("ParticleLimit"), GUIStyle.SubHeadingFont);
            Slider(right, (100, 1500), 15, v => Round(v).ToString(), unsavedConfig.Graphics.ParticleLimit, v => unsavedConfig.Graphics.ParticleLimit = Round(v));
            Spacer(right);
        }

        private static string TrimAudioDeviceName(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) { return string.Empty; }
            string[] prefixes = { "OpenAL Soft on " };
            foreach (string prefix in prefixes)
            {
                if (name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    return name.Remove(0, prefix.Length);
                }
            }
            return name;
        }

        private static int HandleAlErrors(string message)
        {
            int alcError = Alc.GetError(IntPtr.Zero);
            if (alcError != Alc.NoError)
            {
                DebugConsole.ThrowError($"{message}: ALC error {Alc.GetErrorString(alcError)}");
                return alcError;
            }
            
            int alError = Al.GetError();
            if (alError != Al.NoError)
            {
                DebugConsole.ThrowError($"{message}: AL error {Al.GetErrorString(alError)}");
                return alError;
            }

            return Al.NoError;
        }
        
        private static void GetAudioDevices(int listSpecifier, int defaultSpecifier, out IReadOnlyList<string> list, ref string current)
        {
            list = Array.Empty<string>();

            var retVal = Alc.GetStringList(IntPtr.Zero, listSpecifier).ToList();
            if (HandleAlErrors("Alc.GetStringList failed") != Al.NoError) { return; }

            list = retVal;
            if (string.IsNullOrEmpty(current))
            {
                current = Alc.GetString(IntPtr.Zero, defaultSpecifier);
                if (HandleAlErrors("Alc.GetString failed") != Al.NoError) { return; }
            }

            string currentVal = current;
            if (list.Any() && !list.Any(n => n.Equals(currentVal, StringComparison.OrdinalIgnoreCase)))
            {
                current = list[0];
            }
        }
        
        private static bool IsCurrentDevice(string savedDeviceName, int deviceType)
        {
            try
            {
                string currentDevice = Alc.GetString(IntPtr.Zero, deviceType);
                if (string.IsNullOrEmpty(savedDeviceName) || string.IsNullOrEmpty(currentDevice))
                {
                    return false;
                }
                return currentDevice.Equals(savedDeviceName, StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error checking output device name: {ex.Message}");
                return false;
            }
        }
        
        private static bool CurrentDeviceMismatchesDisplayed()
        {
            return !IsCurrentDevice(GameSettings.CurrentConfig.Audio.VoiceCaptureDevice, Alc.CaptureDefaultDeviceSpecifier) ||
                   !IsCurrentDevice(GameSettings.CurrentConfig.Audio.AudioOutputDevice, Alc.DefaultDeviceSpecifier);
        }
        
        public void CreateAudioAndVCTab(bool refresh = false)
        {
            if (GameMain.Client == null
                && (refresh || VoipCapture.Instance == null))
            {
                string currDevice = unsavedConfig.Audio.VoiceCaptureDevice;
                GetAudioDevices(Alc.CaptureDeviceSpecifier, Alc.CaptureDefaultDeviceSpecifier, out var deviceList, ref currDevice);

                if (deviceList.Any())
                {
                    if (VoipCapture.Instance is VoipCapture currentCaptureInstance)
                    {
                        currentCaptureInstance.Dispose();
                    }
                    VoipCapture.Create(unsavedConfig.Audio.VoiceCaptureDevice);
                }
                if (VoipCapture.Instance == null)
                {
                    unsavedConfig.Audio.VoiceSetting = VoiceMode.Disabled;
                }
            }
            
            GUIFrame content = CreateNewContentFrame(Tab.AudioAndVC);
            if (refresh)
            {
                content.ClearChildren();
            }
            var (audio, voiceChat) = CreateSidebars(content, split: true);

            static void audioDeviceElement(
                GUILayoutGroup parent,
                Action<string> setter,
                int listSpecifier,
                int defaultSpecifier,
                ref string currentDevice)
            {
#if OSX
                //At the time of writing there are no OpenAL implementations
                //on macOS that return the list of available devices, or
                //allow selecting any other than the default one. I'm not
                //about to write my own OpenAL implementation to fix this
                //so here's a workaround instead, just a label that shows the
                //name of the current device.
                var deviceNameContainerElement = new GUIFrame(NewItemRectT(parent), style: "GUITextBoxNoIcon");
                var deviceNameElement = new GUITextBlock(new RectTransform(Vector2.One, deviceNameContainerElement.RectTransform), currentDevice, textAlignment: Alignment.CenterLeft);
                new GUICustomComponent(new RectTransform(Vector2.Zero, deviceNameElement.RectTransform), onUpdate:
                    (deltaTime, component) =>
                    {
                        deviceNameElement.Text = Alc.GetString(IntPtr.Zero, listSpecifier);
                    });
#else
                GetAudioDevices(listSpecifier, defaultSpecifier, out var devices, ref currentDevice);
                Dropdown(parent, v => TrimAudioDeviceName(v), null, devices, currentDevice, setter);
#endif
            }

            Label(audio, TextManager.Get("AudioOutputDevice"), GUIStyle.SubHeadingFont);
            
            string currentOutputDevice = unsavedConfig.Audio.AudioOutputDevice;
            audioDeviceElement(audio, v => unsavedConfig.Audio.AudioOutputDevice = v, Alc.OutputDevicesSpecifier, Alc.DefaultDeviceSpecifier, ref currentOutputDevice);
            new GUIButton(new RectTransform(new Vector2(1.0f, 1.0f), audio.RectTransform), text: TextManager.Get("RefreshAudioDevices"), style: "GUIButtonSmall")
            {
                ToolTip = TextManager.Get("RefreshAudioDevicesToolTip"),
                OnClicked = (btn, obj) =>
                {
                    CreateAudioAndVCTab(refresh: true);
                    return true;
                }
            };
            Spacer(audio);

            Label(audio, TextManager.Get("SoundVolume"), GUIStyle.SubHeadingFont);
            Slider(audio, (0, 1), 101, Percentage, unsavedConfig.Audio.SoundVolume, v =>
            {
                unsavedConfig.Audio.SoundVolume = v;
                GameMain.SoundManager.SetCategoryGainMultiplier(SoundManager.SoundCategoryDefault, v);
                GameMain.SoundManager.SetCategoryGainMultiplier(SoundManager.SoundCategoryWaterAmbience, v);
            });

            Label(audio, TextManager.Get("MusicVolume"), GUIStyle.SubHeadingFont);
            Slider(audio, (0, 1), 101, Percentage, unsavedConfig.Audio.MusicVolume, v =>
            {
                unsavedConfig.Audio.MusicVolume = v;
                GameMain.SoundManager.SetCategoryGainMultiplier(SoundManager.SoundCategoryMusic, v);
            });

            Label(audio, TextManager.Get("UiSoundVolume"), GUIStyle.SubHeadingFont);
            Slider(audio, (0, 1), 101, Percentage, unsavedConfig.Audio.UiVolume, v =>
            {
                unsavedConfig.Audio.UiVolume = v;
                GameMain.SoundManager.SetCategoryGainMultiplier(SoundManager.SoundCategoryUi, v);
            });

            Tickbox(audio, TextManager.Get("MuteOnFocusLost"), TextManager.Get("MuteOnFocusLostTooltip"), unsavedConfig.Audio.MuteOnFocusLost, v => unsavedConfig.Audio.MuteOnFocusLost = v);
            Tickbox(audio, TextManager.Get("DynamicRangeCompression"), TextManager.Get("DynamicRangeCompressionTooltip"), unsavedConfig.Audio.DynamicRangeCompressionEnabled, v => unsavedConfig.Audio.DynamicRangeCompressionEnabled = v);
            Spacer(audio);

            Label(audio, TextManager.Get("VoiceChatVolume"), GUIStyle.SubHeadingFont);
            Slider(audio, (0, 2), 201, Percentage, unsavedConfig.Audio.VoiceChatVolume, v =>
            {
                unsavedConfig.Audio.VoiceChatVolume = v;
                GameMain.SoundManager.SetCategoryGainMultiplier(SoundManager.SoundCategoryVoip, v);
            });

            Tickbox(audio, TextManager.Get("DirectionalVoiceChat"), TextManager.Get("DirectionalVoiceChatTooltip"), unsavedConfig.Audio.UseDirectionalVoiceChat, v => unsavedConfig.Audio.UseDirectionalVoiceChat = v);
            Tickbox(audio, TextManager.Get("VoipAttenuation"), TextManager.Get("VoipAttenuationTooltip"), unsavedConfig.Audio.VoipAttenuationEnabled, v => unsavedConfig.Audio.VoipAttenuationEnabled = v);

            Label(voiceChat, TextManager.Get("AudioInputDevice"), GUIStyle.SubHeadingFont);

            string currentInputDevice = unsavedConfig.Audio.VoiceCaptureDevice;
            audioDeviceElement(voiceChat, v => unsavedConfig.Audio.VoiceCaptureDevice = v, Alc.CaptureDeviceSpecifier, Alc.CaptureDefaultDeviceSpecifier, ref currentInputDevice);
            new GUIButton(new RectTransform(new Vector2(1.0f, 1.0f), voiceChat.RectTransform), text: TextManager.Get("RefreshAudioDevices"), style: "GUIButtonSmall")
            {
                ToolTip = TextManager.Get("RefreshAudioDevicesToolTip"),
                OnClicked = (btn, obj) =>
                {
                    CreateAudioAndVCTab(refresh: true);
                    return true;
                }
            };
            Spacer(voiceChat);
            
            Label(voiceChat, TextManager.Get("VCInputMode"), GUIStyle.SubHeadingFont);
            DropdownEnum(voiceChat, v => TextManager.Get($"VoiceMode.{v}"), v => TextManager.Get($"VoiceMode.{v}Tooltip"), unsavedConfig.Audio.VoiceSetting, v => unsavedConfig.Audio.VoiceSetting = v);
            Spacer(voiceChat);
            
            var noiseGateThresholdLabel = Label(voiceChat, TextManager.Get("NoiseGateThreshold"), GUIStyle.SubHeadingFont);
            var dbMeter = new GUIProgressBar(NewItemRectT(voiceChat), 0.0f, Color.Lime);
            dbMeter.ProgressGetter = () =>
            {
                if (VoipCapture.Instance == null) { return 0.0f; }

                dbMeter.Color = unsavedConfig.Audio.VoiceSetting switch
                {
                    VoiceMode.Activity => VoipCapture.Instance.LastdB > unsavedConfig.Audio.NoiseGateThreshold ? GUIStyle.Green : GUIStyle.Orange,
                    VoiceMode.PushToTalk => GUIStyle.Green,
                    VoiceMode.Disabled => Color.LightGray
                };
                
                float scrollVal = double.IsNegativeInfinity(VoipCapture.Instance.LastdB) ? 0.0f : ((float)VoipCapture.Instance.LastdB + 100.0f) / 100.0f;
                return scrollVal * scrollVal;
            };
            var noiseGateSlider = new GUIScrollBar(new RectTransform(Vector2.One, dbMeter.RectTransform, Anchor.Center), color: Color.White, 
                style: "GUISlider", barSize: 0.03f);
            noiseGateSlider.Frame.Visible = false;
            noiseGateSlider.Step = 0.01f;
            noiseGateSlider.Range = new Vector2(-100.0f, 0.0f);
            noiseGateSlider.BarScroll = MathUtils.InverseLerp(-100.0f, 0.0f, unsavedConfig.Audio.NoiseGateThreshold);
            noiseGateSlider.BarScroll *= noiseGateSlider.BarScroll;
            noiseGateSlider.OnMoved = (scrollBar, barScroll) =>
            {
                unsavedConfig.Audio.NoiseGateThreshold = MathHelper.Lerp(-100.0f, 0.0f, (float)Math.Sqrt(scrollBar.BarScroll));
                return true;
            };
            new GUICustomComponent(new RectTransform(Vector2.Zero, voiceChat.RectTransform), onUpdate:
                (deltaTime, component) =>
                {
                    noiseGateThresholdLabel.Visible = unsavedConfig.Audio.VoiceSetting == VoiceMode.Activity;
                    noiseGateSlider.Visible = unsavedConfig.Audio.VoiceSetting == VoiceMode.Activity;
                });
            Spacer(voiceChat);
            
            Label(voiceChat, TextManager.Get("MicrophoneVolume"), GUIStyle.SubHeadingFont);
            Slider(voiceChat, (0, 10), 101, Percentage, unsavedConfig.Audio.MicrophoneVolume, v => unsavedConfig.Audio.MicrophoneVolume = v);
            Spacer(voiceChat);
            
            Label(voiceChat, TextManager.Get("CutoffPrevention"), GUIStyle.SubHeadingFont);
            Slider(voiceChat, (0, 500), 26, v => $"{Round(v)} ms", unsavedConfig.Audio.VoiceChatCutoffPrevention, v => unsavedConfig.Audio.VoiceChatCutoffPrevention = Round(v), TextManager.Get("CutoffPreventionTooltip"));
        }

        private readonly Dictionary<GUIButton, Func<LocalizedString>> inputButtonValueNameGetters = new Dictionary<GUIButton, Func<LocalizedString>>();
        private bool inputBoxSelectedThisFrame = false;

        private void CreateControlsTab()
        {
            GUIFrame content = CreateNewContentFrame(Tab.Controls);

            GUILayoutGroup layout = CreateCenterLayout(content);
            
            Label(layout, TextManager.Get("AimAssist"), GUIStyle.SubHeadingFont);

            var aimAssistSlider = Slider(layout, (0, 1), 101, Percentage, unsavedConfig.AimAssistAmount, v => unsavedConfig.AimAssistAmount = v, TextManager.Get("AimAssistTooltip"));
            Tickbox(layout, TextManager.Get("EnableMouseLook"), TextManager.Get("EnableMouseLookTooltip"), unsavedConfig.EnableMouseLook, v => unsavedConfig.EnableMouseLook = v);

            Spacer(layout);

            GUIListBox keyMapList =
                new GUIListBox(new RectTransform((2.0f, 0.7f),
                    layout.RectTransform))
                {
                    CanBeFocused = false,
                    OnSelected = (_, __) => false
                };
            Spacer(layout);
            
            GUILayoutGroup createInputRowLayout()
                => new GUILayoutGroup(new RectTransform((1.0f, 0.1f), keyMapList.Content.RectTransform), isHorizontal: true);

            inputButtonValueNameGetters.Clear();
            Action<KeyOrMouse>? currentSetter = null;
            void addInputToRow(GUILayoutGroup currRow, LocalizedString labelText, Func<LocalizedString> valueNameGetter, Action<KeyOrMouse> valueSetter, bool isLegacyBind = false)
            {
                var inputFrame = new GUIFrame(new RectTransform((0.5f, 1.0f), currRow.RectTransform),
                    style: null);
                if (isLegacyBind)
                {
                    labelText = TextManager.GetWithVariable("legacyitemformat", "[name]", labelText);
                }
                var label = new GUITextBlock(new RectTransform((0.6f, 1.0f), inputFrame.RectTransform), labelText,
                    font: GUIStyle.SmallFont) {ForceUpperCase = ForceUpperCase.Yes};
                var inputBox = new GUIButton(
                    new RectTransform((0.4f, 1.0f), inputFrame.RectTransform, Anchor.TopRight, Pivot.TopRight),
                    valueNameGetter(), style: "GUITextBoxNoIcon")
                {
                    OnClicked = (btn, obj) =>
                    {
                        inputButtonValueNameGetters.Keys.ForEach(b =>
                        {
                            if (b != btn) { b.Selected = false; }
                        });
                        bool willBeSelected = !btn.Selected;
                        if (willBeSelected)
                        {
                            inputBoxSelectedThisFrame = true;
                            currentSetter = v =>
                            {
                                valueSetter(v);
                                btn.Text = valueNameGetter();
                            };
                        }

                        btn.Selected = willBeSelected;
                        return true;
                    }
                };
                if (isLegacyBind)
                {
                    label.TextColor = Color.Lerp(label.TextColor, label.DisabledTextColor, 0.5f);
                    inputBox.Color = Color.Lerp(inputBox.Color, inputBox.DisabledColor, 0.5f);
                    inputBox.TextColor = Color.Lerp(inputBox.TextColor, label.DisabledTextColor, 0.5f);
                }
                inputButtonValueNameGetters.Add(inputBox, valueNameGetter);
            }

            var inputListener = new GUICustomComponent(new RectTransform(Vector2.Zero, layout.RectTransform), onUpdate: (deltaTime, component) =>
            {
                if (currentSetter is null) { return; }

                if (PlayerInput.PrimaryMouseButtonClicked() && inputBoxSelectedThisFrame)
                {
                    inputBoxSelectedThisFrame = false;
                    return;
                }

                void clearSetter()
                {
                    currentSetter = null;
                    inputButtonValueNameGetters.Keys.ForEach(b => b.Selected = false);
                }
                
                void callSetter(KeyOrMouse v)
                {
                    currentSetter?.Invoke(v);
                    clearSetter();
                }
                
                var pressedKeys = PlayerInput.GetKeyboardState.GetPressedKeys();
                if (pressedKeys?.Any() ?? false)
                {
                    if (pressedKeys.Contains(Keys.Escape))
                    {
                        clearSetter();
                    }
                    else
                    {
                        callSetter(pressedKeys.First());
                    }
                }
                else if (PlayerInput.PrimaryMouseButtonClicked() &&
                        (GUI.MouseOn == null || !(GUI.MouseOn is GUIButton) || GUI.MouseOn.IsChildOf(keyMapList.Content)))
                {
                    callSetter(MouseButton.PrimaryMouse);
                }
                else if (PlayerInput.SecondaryMouseButtonClicked())
                {
                    callSetter(MouseButton.SecondaryMouse);
                }
                else if (PlayerInput.MidButtonClicked())
                {
                    callSetter(MouseButton.MiddleMouse);
                }
                else if (PlayerInput.Mouse4ButtonClicked())
                {
                    callSetter(MouseButton.MouseButton4);
                }
                else if (PlayerInput.Mouse5ButtonClicked())
                {
                    callSetter(MouseButton.MouseButton5);
                }
                else if (PlayerInput.MouseWheelUpClicked())
                {
                    callSetter(MouseButton.MouseWheelUp);
                }
                else if (PlayerInput.MouseWheelDownClicked())
                {
                    callSetter(MouseButton.MouseWheelDown);
                }
            });
            
            InputType[] inputTypes = (InputType[])Enum.GetValues(typeof(InputType));
            InputType[][] inputTypeColumns =
            {
                inputTypes.Take(inputTypes.Length - (inputTypes.Length / 2)).ToArray(),
                inputTypes.TakeLast(inputTypes.Length / 2).ToArray()
            };
            for (int i = 0; i < inputTypes.Length; i+=2)
            {
                var currRow = createInputRowLayout();
                for (int j = 0; j < 2; j++)
                {
                    var column = inputTypeColumns[j];
                    if (i / 2 >= column.Length) { break; }
                    var input = column[i / 2];
                    addInputToRow(
                        currRow,
                        TextManager.Get($"InputType.{input}"),
                        () => unsavedConfig.KeyMap.Bindings[input].Name,
                        v => unsavedConfig.KeyMap = unsavedConfig.KeyMap.WithBinding(input, v),
                        LegacyInputTypes.Contains(input));
                }
            }

            for (int i = 0; i < unsavedConfig.InventoryKeyMap.Bindings.Length; i += 2)
            {
                var currRow = createInputRowLayout();
                for (int j = 0; j < 2; j++)
                {
                    int currIndex = i + j;
                    if (currIndex >= unsavedConfig.InventoryKeyMap.Bindings.Length) { break; }

                    var input = unsavedConfig.InventoryKeyMap.Bindings[currIndex];
                    addInputToRow(
                        currRow,
                        TextManager.GetWithVariable("inventoryslotkeybind", "[slotnumber]", (currIndex + 1).ToString(CultureInfo.InvariantCulture)),
                        () => unsavedConfig.InventoryKeyMap.Bindings[currIndex].Name,
                        v => unsavedConfig.InventoryKeyMap = unsavedConfig.InventoryKeyMap.WithBinding(currIndex, v));
                }
            }

            GUILayoutGroup resetControlsHolder =
                new GUILayoutGroup(new RectTransform((1.75f, 0.1f), layout.RectTransform), isHorizontal: true, childAnchor: Anchor.Center)
                {
                    RelativeSpacing = 0.1f
                };

            var defaultBindingsButton =
                new GUIButton(new RectTransform(new Vector2(0.45f, 1.0f), resetControlsHolder.RectTransform),
                    TextManager.Get("Reset"), style: "GUIButtonSmall")
                {
                    ToolTip = TextManager.Get("SetDefaultBindingsTooltip"),
                    OnClicked = (_, userdata) => 
                    {
                        unsavedConfig.InventoryKeyMap = GameSettings.Config.InventoryKeyMapping.GetDefault();
                        unsavedConfig.KeyMap = GameSettings.Config.KeyMapping.GetDefault();
                        aimAssistSlider.slider.BarScrollValue = GameSettings.Config.DefaultAimAssist;
                        aimAssistSlider.label.Text = Percentage(GameSettings.Config.DefaultAimAssist);
                        foreach (var btn in inputButtonValueNameGetters.Keys)
                        {
                            btn.Text = inputButtonValueNameGetters[btn]();
                        }
                        Instance?.SelectTab(Tab.Controls);
                        return true; 
                    }
                };
        }

        private void CreateGameplayTab()
        {
            GUIFrame content = CreateNewContentFrame(Tab.Gameplay);
            
            var (leftColumn, rightColumn) = CreateSidebars(content, split: true);

            var languages = TextManager.AvailableLanguages
                .OrderBy(l => TextManager.GetTranslatedLanguageName(l).ToIdentifier())
                .ToArray();
            Label(leftColumn, TextManager.Get("Language"), GUIStyle.SubHeadingFont);
            Dropdown(leftColumn, v => TextManager.GetTranslatedLanguageName(v), null, languages, unsavedConfig.Language, v => unsavedConfig.Language = v);
            Spacer(leftColumn);
            
            Tickbox(leftColumn, TextManager.Get("PauseOnFocusLost"), TextManager.Get("PauseOnFocusLostTooltip"), unsavedConfig.PauseOnFocusLost, v => unsavedConfig.PauseOnFocusLost = v);
            Spacer(leftColumn);
            
            Tickbox(leftColumn, TextManager.Get("DisableInGameHints"), TextManager.Get("DisableInGameHintsTooltip"), unsavedConfig.DisableInGameHints, v => unsavedConfig.DisableInGameHints = v);
            var resetInGameHintsButton =
                new GUIButton(new RectTransform(new Vector2(1.0f, 1.0f), leftColumn.RectTransform),
                    TextManager.Get("ResetInGameHints"), style: "GUIButtonSmall")
                {
                    OnClicked = (button, o) =>
                    {
                        var msgBox = new GUIMessageBox(TextManager.Get("ResetInGameHints"),
                            TextManager.Get("ResetInGameHintsTooltip"),
                            buttons: new[] { TextManager.Get("Yes"), TextManager.Get("No") });
                        msgBox.Buttons[0].OnClicked = (guiButton, o1) =>
                        {
                            IgnoredHints.Instance.Clear();
                            msgBox.Close();
                            return false;
                        };
                        msgBox.Buttons[1].OnClicked = msgBox.Close;
                        return false;
                    }
                };

            Spacer(leftColumn);

            Tickbox(leftColumn, TextManager.Get("ChatSpeechBubbles"), TextManager.Get("ChatSpeechBubbles.Tooltip"), unsavedConfig.ChatSpeechBubbles, v => unsavedConfig.ChatSpeechBubbles = v);

            Label(leftColumn, TextManager.Get("ShowEnemyHealthBars"), GUIStyle.SubHeadingFont);
            DropdownEnum(leftColumn, v => TextManager.Get($"ShowEnemyHealthBars.{v}"), null, unsavedConfig.ShowEnemyHealthBars, v => unsavedConfig.ShowEnemyHealthBars = v);
            Spacer(leftColumn);
            Label(leftColumn, TextManager.Get("InteractionLabels"), GUIStyle.SubHeadingFont);
            DropdownEnum(leftColumn, v => TextManager.Get($"InteractionLabels.{v}"), null, unsavedConfig.InteractionLabelDisplayMode, v => unsavedConfig.InteractionLabelDisplayMode = v);

            Label(rightColumn, TextManager.Get("HUDScale"), GUIStyle.SubHeadingFont);
            Slider(rightColumn, (0.75f, 1.25f), 51, Percentage, unsavedConfig.Graphics.HUDScale, v => unsavedConfig.Graphics.HUDScale = v);
            Label(rightColumn, TextManager.Get("InventoryScale"), GUIStyle.SubHeadingFont);
            Slider(rightColumn, (0.75f, 1.25f), 51, Percentage, unsavedConfig.Graphics.InventoryScale, v => unsavedConfig.Graphics.InventoryScale = v);
            Label(rightColumn, TextManager.Get("TextScale"), GUIStyle.SubHeadingFont);
            Slider(rightColumn, (0.75f, 1.25f), 51, Percentage, unsavedConfig.Graphics.TextScale, v => unsavedConfig.Graphics.TextScale = v);
            Spacer(rightColumn);
            var resetSpamListFilter =
                new GUIButton(new RectTransform(new Vector2(1.0f, 1.0f), rightColumn.RectTransform),
                    TextManager.Get("clearserverlistfilters"), style: "GUIButtonSmall")
                {
                    OnClicked = static (_, _) =>
                    {
                        GUI.AskForConfirmation(
                            header: TextManager.Get("clearserverlistfilters"),
                            body: TextManager.Get("clearserverlistfiltersconfirmation"),
                            onConfirm: SpamServerFilters.ClearLocalSpamFilter);
                        return true;
                    }
                };
            Spacer(rightColumn);
#if !OSX
            Spacer(rightColumn);
            var statisticsTickBox = new GUITickBox(NewItemRectT(rightColumn), TextManager.Get("statisticsconsenttickbox"))
            {
                OnSelected = tickBox =>
                {
                    GUIMessageBox? loadingBox = null;
                    if (!tickBox.Selected)
                    {
                        loadingBox = GUIMessageBox.CreateLoadingBox(TextManager.Get("PleaseWait"));
                    }
                    GameAnalyticsManager.SetConsent(
                        tickBox.Selected
                            ? GameAnalyticsManager.Consent.Ask
                            : GameAnalyticsManager.Consent.No,
                        onAnswerSent: () => loadingBox?.Close());
                    return false;
                }
            };
#if DEBUG
            statisticsTickBox.Enabled = false;
#endif
            void updateGATickBoxToolTip()
                => statisticsTickBox.ToolTip = TextManager.Get($"GameAnalyticsStatus.{GameAnalyticsManager.UserConsented}");
            updateGATickBoxToolTip();

            var cachedConsent = GameAnalyticsManager.Consent.Unknown;
            var statisticsTickBoxUpdater = new GUICustomComponent(
                new RectTransform(Vector2.Zero, statisticsTickBox.RectTransform),
                onUpdate: (deltaTime, component) =>
            {
                bool shouldTickBoxBeSelected = GameAnalyticsManager.UserConsented == GameAnalyticsManager.Consent.Yes;
                
                bool shouldUpdateTickBoxState = cachedConsent != GameAnalyticsManager.UserConsented
                                                || statisticsTickBox.Selected != shouldTickBoxBeSelected;

                if (!shouldUpdateTickBoxState) { return; }

                updateGATickBoxToolTip();
                cachedConsent = GameAnalyticsManager.UserConsented;
                GUITickBox.OnSelectedHandler prevHandler = statisticsTickBox.OnSelected;
                statisticsTickBox.OnSelected = null;
                statisticsTickBox.Selected = shouldTickBoxBeSelected;
                statisticsTickBox.OnSelected = prevHandler;
                statisticsTickBox.Enabled &= GameAnalyticsManager.UserConsented != GameAnalyticsManager.Consent.Error;
            });
#endif
            //Steam version supports hosting/joining servers using EOS networking
            if (SteamManager.IsInitialized)
            {
                bool shouldCrossplayBeEnabled = unsavedConfig.CrossplayChoice is Eos.EosSteamPrimaryLogin.CrossplayChoice.Enabled;
                var crossplayTickBox = Tickbox(rightColumn, TextManager.Get("EosAllowCrossplay"), TextManager.Get("EosAllowCrossplayTooltip"), shouldCrossplayBeEnabled, v =>
                {
                    unsavedConfig.CrossplayChoice = v
                        ? Eos.EosSteamPrimaryLogin.CrossplayChoice.Enabled
                        : Eos.EosSteamPrimaryLogin.CrossplayChoice.Disabled;
                });
                if (GameMain.NetworkMember != null)
                {
                    crossplayTickBox.Enabled = false;
                    crossplayTickBox.ToolTip = TextManager.Get("CantAccessEOSSettingsInMP");
                }
            }
        }

        private void CreateModsTab(out WorkshopMenu workshopMenu)
        {
            GUIFrame content = CreateNewContentFrame(Tab.Mods);
            content.RectTransform.RelativeSize = Vector2.One;

            workshopMenu = Screen.Selected is MainMenuScreen
                ? (WorkshopMenu)new MutableWorkshopMenu(content)
                : (WorkshopMenu)new ImmutableWorkshopMenu(content);

            GameMain.MainMenuScreen.ResetModUpdateButton();
        }

        private void CreateBottomButtons()
        {
            new GUIButton(new RectTransform(new Vector2(1.0f, 1.0f), bottom.RectTransform), text: TextManager.Get("Cancel"))
            {
                OnClicked = (btn, obj) =>
                {
                    // reset any modified audio settings to current config
                    GameMain.SoundManager?.ApplySettings();
                    Close();
                    return false;
                }
            };
            new GUIButton(new RectTransform(new Vector2(1.0f, 1.0f), bottom.RectTransform), text: TextManager.Get("applysettingsbutton"))
            {
                OnClicked = (btn, obj) =>
                {
                    ApplyInstalledModChanges();
                    mainFrame.Flash(color: GUIStyle.Green);
                    return false;
                },
                OnAddedToGUIUpdateList = (GUIComponent component) =>
                {
                    component.Enabled = 
                        CurrentTab != Tab.Mods ||
                        (WorkshopMenu is MutableWorkshopMenu mutableWorkshopMenu && mutableWorkshopMenu.CurrentTab == MutableWorkshopMenu.Tab.InstalledMods && !mutableWorkshopMenu.ViewingItemDetails);
                }                
            };
        }

        public void ApplyInstalledModChanges()
        {
            EosSteamPrimaryLogin.HandleCrossplayChoiceChange(unsavedConfig.CrossplayChoice);
            GameSettings.SetCurrentConfig(unsavedConfig);
            if (WorkshopMenu is MutableWorkshopMenu { CurrentTab: MutableWorkshopMenu.Tab.InstalledMods } mutableWorkshopMenu)
            {
                mutableWorkshopMenu.Apply();
            }
            GameSettings.SaveCurrentConfig();
        }

        public void Close()
        {
            if (GameMain.Client is null || GameSettings.CurrentConfig.Audio.VoiceSetting == VoiceMode.Disabled)
            {
                VoipCapture.Instance?.Dispose();
            }
            mainFrame.Parent.RemoveChild(mainFrame);
            if (Instance == this) { Instance = null; }

            GUI.SettingsMenuOpen = false;
        }
    }
}
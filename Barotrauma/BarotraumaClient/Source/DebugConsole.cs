﻿using Barotrauma.Items.Components;
using Barotrauma.Networking;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Barotrauma
{
    static partial class DebugConsole
    {
        static bool isOpen;

        private static Queue<ColoredText> queuedMessages = new Queue<ColoredText>();

        private static GUITextBlock activeQuestionText;
        
        public static bool IsOpen
        {
            get
            {
                return isOpen;
            }
        }

        static GUIFrame frame;
        static GUIListBox listBox;
        static GUITextBox textBox;

        public static void Init()
        {
            frame = new GUIFrame(new RectTransform(new Vector2(0.5f, 0.45f), GUI.Canvas) { MinSize = new Point(400, 300), AbsoluteOffset = new Point(10, 10) }, 
                color: new Color(0.4f, 0.4f, 0.4f, 0.8f));
            var paddedFrame = new GUIFrame(new RectTransform(new Vector2(0.95f, 0.9f), frame.RectTransform, Anchor.Center), style: null);

            listBox = new GUIListBox(new RectTransform(new Point(paddedFrame.Rect.Width, paddedFrame.Rect.Height - 30), paddedFrame.RectTransform)
            {
                IsFixedSize = false
            }, color: Color.Black * 0.9f);

            textBox = new GUITextBox(new RectTransform(new Point(paddedFrame.Rect.Width, 20), paddedFrame.RectTransform, Anchor.BottomLeft)
            {
                IsFixedSize = false
            });
            textBox.OnTextChanged += (textBox, text) =>
            {
                ResetAutoComplete();
                return true;
            };

            NewMessage("Press F3 to open/close the debug console", Color.Cyan);
            NewMessage("Enter \"help\" for a list of available console commands", Color.Cyan);
        }

        public static void AddToGUIUpdateList()
        {
            if (isOpen)
            {
                frame.AddToGUIUpdateList();
            }
        }

        public static void Update(GameMain game, float deltaTime)
        {
            lock (queuedMessages)
            {
                while (queuedMessages.Count > 0)
                {
                    var newMsg = queuedMessages.Dequeue();
                    AddMessage(newMsg);

                    if (GameSettings.SaveDebugConsoleLogs)
                    {
                        unsavedMessages.Add(newMsg);
                        if (unsavedMessages.Count >= messagesPerFile)
                        {
                            SaveLogs();
                            unsavedMessages.Clear();
                        }
                    }
                }
            }

            activeQuestionText?.SetAsLastChild();

            if (PlayerInput.KeyHit(Keys.F3))
            {
                isOpen = !isOpen;
                if (isOpen)
                {
                    textBox.Select();
                    AddToGUIUpdateList();
                }
                else
                {
                    GUI.ForceMouseOn(null);
                    textBox.Deselect();
                }
            }

            if (isOpen)
            {
                frame.UpdateManually(deltaTime);

                Character.DisableControls = true;

                if (PlayerInput.KeyHit(Keys.Up))
                {
                    textBox.Text = SelectMessage(-1);
                }
                else if (PlayerInput.KeyHit(Keys.Down))
                {
                    textBox.Text = SelectMessage(1);
                }
                else if (PlayerInput.KeyHit(Keys.Tab))
                {
                    textBox.Text = AutoComplete(textBox.Text);
                }

                if (PlayerInput.KeyHit(Keys.Enter))
                {
                    ExecuteCommand(textBox.Text);
                    textBox.Text = "";
                }
            }
        }

        public static void Draw(SpriteBatch spriteBatch)
        {
            if (!isOpen) return;

            frame.DrawManually(spriteBatch);
        }

        private static bool IsCommandPermitted(string command, GameClient client)
        {
            switch (command)
            {
                case "kick":
                    return client.HasPermission(ClientPermissions.Kick);
                case "ban":
                case "banip":
                    return client.HasPermission(ClientPermissions.Ban);
                case "netstats":
                case "help":
                case "dumpids":
                case "admin":
                case "entitylist":
                    return true;
                default:
                    return client.HasConsoleCommandPermission(command);
            }
        }

        public static void DequeueMessages()
        {
            while (queuedMessages.Count > 0)
            {
                var newMsg = queuedMessages.Dequeue();
                AddMessage(newMsg);

                if (GameSettings.SaveDebugConsoleLogs) unsavedMessages.Add(newMsg);
            }
        }

        private static void AddMessage(ColoredText msg)
        {
            //listbox not created yet, don't attempt to add
            if (listBox == null) return;

            if (listBox.Content.CountChildren > MaxMessages)
            {
                listBox.RemoveChild(listBox.Content.Children.First());
            }

            Messages.Add(msg);
            if (Messages.Count > MaxMessages)
            {
                Messages.RemoveRange(0, Messages.Count - MaxMessages);
            }

            try
            {
                var textBlock = new GUITextBlock(new RectTransform(new Vector2(1.0f, 0.0f), listBox.Content.RectTransform),
                    msg.Text, font: GUI.SmallFont, wrap: true)
                {
                    CanBeFocused = false,
                    TextColor = msg.Color
                };
                listBox.UpdateScrollBarSize();
                listBox.BarScroll = 1.0f;
            }
            catch (Exception e)
            {
                ThrowError("Failed to add a message to the debug console.", e);
            }

            selectedIndex = Messages.Count;
        }

        private static void AddHelpMessage(Command command)
        {
            if (listBox.Content.CountChildren > MaxMessages)
            {
                listBox.RemoveChild(listBox.Content.Children.First());
            }

            var textContainer = new GUIFrame(new RectTransform(new Vector2(1.0f, 0.0f), listBox.Content.RectTransform),
                style: "InnerFrame", color: Color.White * 0.6f)
            {
                CanBeFocused = false
            };
            var textBlock = new GUITextBlock(new RectTransform(new Point(listBox.Content.Rect.Width - 170, 0), textContainer.RectTransform, Anchor.TopRight) { AbsoluteOffset = new Point(20, 0) },
                command.help, textAlignment: Alignment.TopLeft, font: GUI.SmallFont, wrap: true)
            {
                CanBeFocused = false,
                TextColor = Color.White
            };
            textContainer.RectTransform.NonScaledSize = new Point(textContainer.RectTransform.NonScaledSize.X, textBlock.RectTransform.NonScaledSize.Y + 5);
            textBlock.SetTextPos();
            var nameBlock = new GUITextBlock(new RectTransform(new Point(150, textContainer.Rect.Height), textContainer.RectTransform),
                command.names[0], textAlignment: Alignment.TopLeft);
            
            listBox.UpdateScrollBarSize();
            listBox.BarScroll = 1.0f;

            selectedIndex = Messages.Count;
        }

        private static void InitProjectSpecific()
        {
            commands.Add(new Command("autohull", "", (string[] args) =>
            {
                if (Screen.Selected != GameMain.SubEditorScreen) return;

                if (MapEntity.mapEntityList.Any(e => e is Hull || e is Gap))
                {
                    ShowQuestionPrompt("This submarine already has hulls and/or gaps. This command will delete them. Do you want to continue? Y/N",
                        (option) => {
                            if (option.ToLower() == "y") GameMain.SubEditorScreen.AutoHull();
                        });
                }
                else
                {
                    GameMain.SubEditorScreen.AutoHull();
                }
            }));

            commands.Add(new Command("startclient", "", (string[] args) =>
            {
                if (args.Length == 0) return;

                if (GameMain.Client == null)
                {
                    GameMain.NetworkMember = new GameClient("Name");
                    GameMain.Client.ConnectToServer(args[0]);
                }
            }));

            commands.Add(new Command("mainmenuscreen|mainmenu|menu", "mainmenu/menu: Go to the main menu.", (string[] args) =>
            {
                GameMain.GameSession = null;

                List<Character> characters = new List<Character>(Character.CharacterList);
                foreach (Character c in characters)
                {
                    c.Remove();
                }

                GameMain.MainMenuScreen.Select();
            }));

            commands.Add(new Command("gamescreen|game", "gamescreen/game: Go to the \"in-game\" view.", (string[] args) =>
            {
                GameMain.GameScreen.Select();
            }));

            commands.Add(new Command("editsubscreen|editsub|subeditor", "editsub/subeditor: Switch to the submarine editor.", (string[] args) =>
            {
                if (args.Length > 0)
                {
                    Submarine.Load(string.Join(" ", args), true);
                }
                GameMain.SubEditorScreen.Select();
            }));

            commands.Add(new Command("editcharacter", "", (string[] args) =>
            {
                GameMain.CharacterEditorScreen.Select();
            }));

            commands.Add(new Command("editparticles", "", (string[] args) =>
            {
                GameMain.ParticleEditorScreen.Select();
            }));

            commands.Add(new Command("editanimations|animedit|animationeditor|animeditor|animationedit", "animationeditor: Edit animations.", (string[] args) =>
            {
                GameMain.AnimationEditorScreen.Select();
            }));


            commands.Add(new Command("control|controlcharacter", "control [character name]: Start controlling the specified character.", (string[] args) =>
            {
                if (args.Length < 1) return;

                var character = FindMatchingCharacter(args, true);

                if (character != null)
                {
                    Character.Controlled = character;
                }
            },
            () =>
            {
                return new string[][]
                {
                    Character.CharacterList.Select(c => c.Name).Distinct().ToArray()
                };
            }, isCheat: true));

            commands.Add(new Command("shake", "", (string[] args) =>
            {
                GameMain.GameScreen.Cam.Shake = 10.0f;
            }));

            commands.Add(new Command("los", "los: Toggle the line of sight effect on/off.", (string[] args) =>
            {
                GameMain.LightManager.LosEnabled = !GameMain.LightManager.LosEnabled;
                NewMessage("Line of sight effect " + (GameMain.LightManager.LosEnabled ? "enabled" : "disabled"), Color.White);
            }, isCheat: true));

            commands.Add(new Command("lighting|lights", "Toggle lighting on/off.", (string[] args) =>
            {
                GameMain.LightManager.LightingEnabled = !GameMain.LightManager.LightingEnabled;
                NewMessage("Lighting " + (GameMain.LightManager.LightingEnabled ? "enabled" : "disabled"), Color.White);
            }, isCheat: true));

            commands.Add(new Command("multiplylights [color]", "Multiplies the colors of all the static lights in the sub with the given color value.", (string[] args) =>
            {
                if (Screen.Selected != GameMain.SubEditorScreen || args.Length < 1) return;

                Color color = XMLExtensions.ParseColor(args[0]);
                foreach (Item item in Item.ItemList)
                {
                    if (item.ParentInventory != null || item.body != null) continue;
                    var lightComponent = item.GetComponent<LightComponent>();
                    if (lightComponent != null) lightComponent.LightColor = 
                        new Color(
                            (lightComponent.LightColor.R / 255.0f) * (color.R / 255.0f), 
                            (lightComponent.LightColor.G / 255.0f) * (color.G / 255.0f),
                            (lightComponent.LightColor.B / 255.0f) * (color.B / 255.0f),
                            (lightComponent.LightColor.A / 255.0f) * (color.A / 255.0f));
                }
            }, isCheat: false));

            commands.Add(new Command("tutorial", "", (string[] args) =>
            {
                TutorialMode.StartTutorial(Tutorials.Tutorial.Tutorials[0]);
            }));

            commands.Add(new Command("lobby|lobbyscreen", "", (string[] args) =>
            {
                GameMain.LobbyScreen.Select();
            }));

            commands.Add(new Command("save|savesub", "save [submarine name]: Save the currently loaded submarine using the specified name.", (string[] args) =>
            {
                if (args.Length < 1) return;

                if (GameMain.SubEditorScreen.CharacterMode)
                {
                    GameMain.SubEditorScreen.SetCharacterMode(false);
                }

                string fileName = string.Join(" ", args);
                if (fileName.Contains("../"))
                {
                    ThrowError("Illegal symbols in filename (../)");
                    return;
                }

                if (Submarine.SaveCurrent(System.IO.Path.Combine(Submarine.SavePath, fileName + ".sub")))
                {
                    NewMessage("Sub saved", Color.Green);
                }
            }));

            commands.Add(new Command("load|loadsub", "load [submarine name]: Load a submarine.", (string[] args) =>
            {
                if (args.Length == 0) return;
                Submarine.Load(string.Join(" ", args), true);
            }));

            commands.Add(new Command("cleansub", "", (string[] args) =>
            {
                for (int i = MapEntity.mapEntityList.Count - 1; i >= 0; i--)
                {
                    MapEntity me = MapEntity.mapEntityList[i];

                    if (me.SimPosition.Length() > 2000.0f)
                    {
                        NewMessage("Removed " + me.Name + " (simposition " + me.SimPosition + ")", Color.Orange);
                        MapEntity.mapEntityList.RemoveAt(i);
                    }
                    else if (me.MoveWithLevel)
                    {
                        NewMessage("Removed " + me.Name + " (MoveWithLevel==true)", Color.Orange);
                        MapEntity.mapEntityList.RemoveAt(i);
                    }
                    else if (me is Item)
                    {
                        Item item = me as Item;
                        var wire = item.GetComponent<Wire>();
                        if (wire == null) continue;

                        if (wire.GetNodes().Count > 0 && !wire.Connections.Any(c => c != null))
                        {
                            wire.Item.Drop(null);
                            NewMessage("Dropped wire (ID: " + wire.Item.ID + ") - attached on wall but no connections found", Color.Orange);
                        }
                    }
                }
            }, isCheat: true));

            commands.Add(new Command("messagebox", "", (string[] args) =>
            {
                new GUIMessageBox("", string.Join(" ", args));
            }));

            commands.Add(new Command("debugdraw", "debugdraw: Toggle the debug drawing mode on/off.", (string[] args) =>
            {
                GameMain.DebugDraw = !GameMain.DebugDraw;
                NewMessage("Debug draw mode " + (GameMain.DebugDraw ? "enabled" : "disabled"), Color.White);
            }, isCheat: true));

            commands.Add(new Command("fpscounter", "fpscounter: Toggle the FPS counter.", (string[] args) =>
            {
                GameMain.ShowFPS = !GameMain.ShowFPS;
                NewMessage("FPS counter " + (GameMain.DebugDraw ? "enabled" : "disabled"), Color.White);
            }));
            commands.Add(new Command("showperf", "showperf: Toggle performance statistics on/off.", (string[] args) =>
            {
                GameMain.ShowPerf = !GameMain.ShowPerf;
                NewMessage("Performance statistics " + (GameMain.ShowPerf ? "enabled" : "disabled"), Color.White);
            }));

            commands.Add(new Command("hudlayoutdebugdraw", "hudlayoutdebugdraw: Toggle the debug drawing mode of HUD layout areas on/off.", (string[] args) =>
            {
                HUDLayoutSettings.DebugDraw = !HUDLayoutSettings.DebugDraw;
                NewMessage("HUD layout debug draw mode " + (HUDLayoutSettings.DebugDraw ? "enabled" : "disabled"), Color.White);

            }));

            commands.Add(new Command("togglehud|hud", "togglehud/hud: Toggle the character HUD (inventories, icons, buttons, etc) on/off.", (string[] args) =>
            {
                GUI.DisableHUD = !GUI.DisableHUD;
                GameMain.Instance.IsMouseVisible = !GameMain.Instance.IsMouseVisible;
                NewMessage(GUI.DisableHUD ? "Disabled HUD" : "Enabled HUD", Color.White);
            }));

            commands.Add(new Command("followsub", "followsub: Toggle whether the camera should follow the nearest submarine.", (string[] args) =>
            {
                Camera.FollowSub = !Camera.FollowSub;
                NewMessage(Camera.FollowSub ? "Set the camera to follow the closest submarine" : "Disabled submarine following.", Color.White);
            }));

            commands.Add(new Command("toggleaitargets|aitargets", "toggleaitargets/aitargets: Toggle the visibility of AI targets (= targets that enemies can detect and attack/escape from).", (string[] args) =>
            {
                AITarget.ShowAITargets = !AITarget.ShowAITargets;
                NewMessage(AITarget.ShowAITargets ? "Enabled AI target drawing" : "Disabled AI target drawing", Color.White);
            }, isCheat: true));
#if DEBUG
            commands.Add(new Command("spamchatmessages", "", (string[] args) =>
            {
                int msgCount = 1000;
                if (args.Length > 0) int.TryParse(args[0], out msgCount);
                int msgLength = 50;
                if (args.Length > 1) int.TryParse(args[1], out msgLength);

                for (int i = 0; i < msgCount; i++)
                {
                    if (GameMain.Server != null)
                    {
                        GameMain.Server.SendChatMessage(ToolBox.RandomSeed(msgLength), ChatMessageType.Default);
                    }
                    else if (GameMain.Client != null)
                    {
                        GameMain.Client.SendChatMessage(ToolBox.RandomSeed(msgLength));
                    }
                }
            }));
#endif
            commands.Add(new Command("cleanbuild", "", (string[] args) =>
            {
                GameMain.Config.MusicVolume = 0.5f;
                GameMain.Config.SoundVolume = 0.5f;
                NewMessage("Music and sound volume set to 0.5", Color.Green);

                GameMain.Config.GraphicsWidth = 0;
                GameMain.Config.GraphicsHeight = 0;
                GameMain.Config.WindowMode = WindowMode.Fullscreen;
                NewMessage("Resolution set to 0 x 0 (screen resolution will be used)", Color.Green);
                NewMessage("Fullscreen enabled", Color.Green);

                GameSettings.ShowUserStatisticsPrompt = true;

                GameSettings.VerboseLogging = false;

                if (GameMain.Config.MasterServerUrl != "http://www.undertowgames.com/baromaster")
                {
                    ThrowError("MasterServerUrl \"" + GameMain.Config.MasterServerUrl + "\"!");
                }

                GameMain.Config.Save();

                var saveFiles = System.IO.Directory.GetFiles(SaveUtil.SaveFolder);

                foreach (string saveFile in saveFiles)
                {
                    System.IO.File.Delete(saveFile);
                    NewMessage("Deleted " + saveFile, Color.Green);
                }

                if (System.IO.Directory.Exists(System.IO.Path.Combine(SaveUtil.SaveFolder, "temp")))
                {
                    System.IO.Directory.Delete(System.IO.Path.Combine(SaveUtil.SaveFolder, "temp"), true);
                    NewMessage("Deleted temp save folder", Color.Green);
                }

                if (System.IO.Directory.Exists(ServerLog.SavePath))
                {
                    var logFiles = System.IO.Directory.GetFiles(ServerLog.SavePath);

                    foreach (string logFile in logFiles)
                    {
                        System.IO.File.Delete(logFile);
                        NewMessage("Deleted " + logFile, Color.Green);
                    }
                }

                if (System.IO.File.Exists("filelist.xml"))
                {
                    System.IO.File.Delete("filelist.xml");
                    NewMessage("Deleted filelist", Color.Green);
                }

                if (System.IO.File.Exists("Data/bannedplayers.txt"))
                {
                    System.IO.File.Delete("Data/bannedplayers.txt");
                    NewMessage("Deleted bannedplayers.txt", Color.Green);
                }

                if (System.IO.File.Exists("Submarines/TutorialSub.sub"))
                {
                    System.IO.File.Delete("Submarines/TutorialSub.sub");

                    NewMessage("Deleted TutorialSub from the submarine folder", Color.Green);
                }

                if (System.IO.File.Exists(GameServer.SettingsFile))
                {
                    System.IO.File.Delete(GameServer.SettingsFile);
                    NewMessage("Deleted server settings", Color.Green);
                }

                if (System.IO.File.Exists(GameServer.ClientPermissionsFile))
                {
                    System.IO.File.Delete(GameServer.ClientPermissionsFile);
                    NewMessage("Deleted client permission file", Color.Green);
                }

                if (System.IO.File.Exists("crashreport.log"))
                {
                    System.IO.File.Delete("crashreport.log");
                    NewMessage("Deleted crashreport.log", Color.Green);
                }

                if (!System.IO.File.Exists("Content/Map/TutorialSub.sub"))
                {
                    ThrowError("TutorialSub.sub not found!");
                }
            }));

        }
    }
}

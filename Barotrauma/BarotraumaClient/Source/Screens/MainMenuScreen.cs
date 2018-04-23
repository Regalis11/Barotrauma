﻿using Barotrauma.Networking;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using System;
using System.IO;
using System.Collections.Generic;
using System.Linq;

namespace Barotrauma
{
    class MainMenuScreen : Screen
    {
        public enum Tab { NewGame = 1, LoadGame = 2, HostServer = 3, Settings = 4 }

        private GUIFrame buttonsTab;

        private GUIFrame[] menuTabs;

        private CampaignSetupUI campaignSetupUI;

        private GUITextBox serverNameBox, portBox, passwordBox, maxPlayersBox;
        private GUITickBox isPublicBox, useUpnpBox;

        private GameMain game;

        private Tab selectedTab;

        private GUIFrame outerElement;
        private List<GUIFrame> innerElements = new List<GUIFrame>();

        public MainMenuScreen(GameMain game)
        {
            // ui test, TODO: remove 
            int width = (int)(GameMain.GraphicsWidth * 0.9f);
            int height = (int)(GameMain.GraphicsHeight * 0.9f);
            outerElement = new GUIFrame(new RectTransform(new Point(width - 100, height - 100), anchor: Anchor.Center));

            menuTabs = new GUIFrame[Enum.GetValues(typeof(Tab)).Length + 1];

            buttonsTab = new GUIFrame(new Rectangle(0, 0, 0, 0), Color.Transparent, Alignment.Left | Alignment.CenterY);
            buttonsTab.Padding = new Vector4(20.0f, 20.0f, 20.0f, 20.0f);

            int y = (int)(GameMain.GraphicsHeight * 0.3f);

            Rectangle panelRect = new Rectangle(
                290, y,
                500, 360);

            GUIButton button = new GUIButton(new Rectangle(50, y, 200, 30), TextManager.Get("TutorialButton"), null, Alignment.TopLeft, Alignment.Left, "", buttonsTab);

            button.Color = button.Color * 0.8f;
            button.OnClicked = TutorialButtonClicked;

            button = new GUIButton(new Rectangle(50, y + 60, 200, 30), TextManager.Get("NewGameButton"), null, Alignment.TopLeft, Alignment.Left, "", buttonsTab);
            button.Color = button.Color * 0.8f;
            button.UserData = Tab.NewGame;
            button.OnClicked = SelectTab;

            button = new GUIButton(new Rectangle(50, y + 100, 200, 30), TextManager.Get("LoadGameButton"), null, Alignment.TopLeft, Alignment.Left, "", buttonsTab);
            button.Color = button.Color * 0.8f;
            button.UserData = Tab.LoadGame;
            button.OnClicked = SelectTab;

            button = new GUIButton(new Rectangle(50, y + 160, 200, 30), TextManager.Get("JoinServerButton"), null, Alignment.TopLeft, Alignment.Left, "", buttonsTab);
            button.Color = button.Color * 0.8f;
            //button.UserData = (int)Tabs.JoinServer;
            button.OnClicked = JoinServerClicked;

            button = new GUIButton(new Rectangle(50, y + 200, 200, 30), TextManager.Get("HostServerButton"), null, Alignment.TopLeft, Alignment.Left, "", buttonsTab);
            button.Color = button.Color * 0.8f;
            button.UserData = Tab.HostServer;
            button.OnClicked = SelectTab;

            button = new GUIButton(new Rectangle(50, y + 260, 200, 30), TextManager.Get("SubEditorButton"), null, Alignment.TopLeft, Alignment.Left, "", buttonsTab);
            button.Color = button.Color * 0.8f;
            button.OnClicked = (GUIButton btn, object userdata) => { GameMain.SubEditorScreen.Select(); return true; };

            button = new GUIButton(new Rectangle(50, y + 320, 200, 30), TextManager.Get("SettingsButton"), null, Alignment.TopLeft, Alignment.Left, "", buttonsTab);
            button.Color = button.Color * 0.8f;
            button.UserData = Tab.Settings;
            button.OnClicked = SelectTab;

            button = new GUIButton(new Rectangle(0, 0, 150, 30), TextManager.Get("QuitButton"), Alignment.BottomRight, "", buttonsTab);
            button.Color = button.Color * 0.8f;
            button.OnClicked = QuitClicked;

            panelRect.Y += 10;

            //----------------------------------------------------------------------

            menuTabs[(int)Tab.NewGame] = new GUIFrame(panelRect, "");
            menuTabs[(int)Tab.NewGame].Padding = new Vector4(20.0f, 20.0f, 20.0f, 20.0f);

            menuTabs[(int)Tab.LoadGame] = new GUIFrame(panelRect, "");

            campaignSetupUI = new CampaignSetupUI(false, menuTabs[(int)Tab.NewGame], menuTabs[(int)Tab.LoadGame]);
            campaignSetupUI.LoadGame = LoadGame;
            campaignSetupUI.StartNewGame = StartGame;

            //----------------------------------------------------------------------

            menuTabs[(int)Tab.HostServer] = new GUIFrame(panelRect, "");

            new GUITextBlock(new Rectangle(0, 0, 100, 30), TextManager.Get("ServerName"), "", Alignment.TopLeft, Alignment.Left, menuTabs[(int)Tab.HostServer]);
            serverNameBox = new GUITextBox(new Rectangle(160, 0, 200, 30), null, null, Alignment.TopLeft, Alignment.Left, "", menuTabs[(int)Tab.HostServer]);

            new GUITextBlock(new Rectangle(0, 50, 100, 30), TextManager.Get("ServerPort"), "", Alignment.TopLeft, Alignment.Left, menuTabs[(int)Tab.HostServer]);
            portBox = new GUITextBox(new Rectangle(160, 50, 200, 30), null, null, Alignment.TopLeft, Alignment.Left, "", menuTabs[(int)Tab.HostServer]);
            portBox.Text = NetConfig.DefaultPort.ToString();
            portBox.ToolTip = "Server port";

            new GUITextBlock(new Rectangle(0, 100, 100, 30), TextManager.Get("MaxPlayers"), "", Alignment.TopLeft, Alignment.Left, menuTabs[(int)Tab.HostServer]);
            maxPlayersBox = new GUITextBox(new Rectangle(195, 100, 30, 30), null, null, Alignment.TopLeft, Alignment.Center, "", menuTabs[(int)Tab.HostServer]);
            maxPlayersBox.Text = "8";
            maxPlayersBox.Enabled = false;

            var minusPlayersBox = new GUIButton(new Rectangle(160, 100, 30, 30), "-", "", menuTabs[(int)Tab.HostServer]);
            minusPlayersBox.UserData = -1;
            minusPlayersBox.OnClicked = ChangeMaxPlayers;

            var plusPlayersBox = new GUIButton(new Rectangle(230, 100, 30, 30), "+", "", menuTabs[(int)Tab.HostServer]);
            plusPlayersBox.UserData = 1;
            plusPlayersBox.OnClicked = ChangeMaxPlayers;

            new GUITextBlock(new Rectangle(0, 150, 100, 30), TextManager.Get("Password"), "", Alignment.TopLeft, Alignment.Left, menuTabs[(int)Tab.HostServer]);
            passwordBox = new GUITextBox(new Rectangle(160, 150, 200, 30), null, null, Alignment.TopLeft, Alignment.Left, "", menuTabs[(int)Tab.HostServer]);

            isPublicBox = new GUITickBox(new Rectangle(10, 200, 20, 20), TextManager.Get("PublicServer"), Alignment.TopLeft, menuTabs[(int)Tab.HostServer]);
            isPublicBox.ToolTip = TextManager.Get("PublicServerToolTip");

            useUpnpBox = new GUITickBox(new Rectangle(10, 250, 20, 20), TextManager.Get("AttemptUPnP"), Alignment.TopLeft, menuTabs[(int)Tab.HostServer]);
            useUpnpBox.ToolTip = TextManager.Get("AttemptUPnPToolTip");

            GUIButton hostButton = new GUIButton(new Rectangle(0, 0, 100, 30), TextManager.Get("StartServerButton"), Alignment.BottomRight, "", menuTabs[(int)Tab.HostServer]);
            hostButton.OnClicked = HostServerClicked;

            this.game = game;
        }

        public override void Select()
        {
            base.Select();

            if (GameMain.NetworkMember != null)
            {
                GameMain.NetworkMember.Disconnect();
                GameMain.NetworkMember = null;
            }

            Submarine.Unload();

            campaignSetupUI.UpdateSubList();

            SelectTab(null, 0);
        }

        public bool SelectTab(GUIButton button, object obj)
        {
            try
            {
                SelectTab((Tab)obj);
            }
            catch
            {
                selectedTab = 0;
            }

            if (button != null) button.Selected = true;

            foreach (GUIComponent child in buttonsTab.children)
            {
                GUIButton otherButton = child as GUIButton;
                if (otherButton == null || otherButton == button) continue;

                otherButton.Selected = false;
            }

            if (Selected != this) Select();

            return true;
        }

        public void SelectTab(Tab tab)
        {
            if (GameMain.Config.UnsavedSettings)
            {
                var applyBox = new GUIMessageBox(
                    TextManager.Get("ApplySettingsLabel"),
                    TextManager.Get("ApplySettingsQuestion"),
                    new string[] { TextManager.Get("ApplySettingsYes"), TextManager.Get("ApplySettingsNo") });
                applyBox.Buttons[0].OnClicked += applyBox.Close;
                applyBox.Buttons[0].OnClicked += ApplySettings;
                applyBox.Buttons[0].UserData = tab;
                applyBox.Buttons[1].OnClicked += applyBox.Close;
                applyBox.Buttons[1].OnClicked += DiscardSettings;
                applyBox.Buttons[1].UserData = tab;

                return;
            }

            selectedTab = tab;

            switch (selectedTab)
            {
                case Tab.NewGame:
                    campaignSetupUI.CreateDefaultSaveName();
                    break;
                case Tab.LoadGame:
                    campaignSetupUI.UpdateLoadMenu();
                    break;
                case Tab.Settings:
                    GameMain.Config.ResetSettingsFrame();
                    menuTabs[(int)Tab.Settings] = GameMain.Config.SettingsFrame;
                    break;
            }
        }

        private bool ApplySettings(GUIButton button, object userData)
        {
            GameMain.Config.Save("config.xml");

            if (userData is Tab) SelectTab((Tab)userData);

            if (GameMain.GraphicsWidth != GameMain.Config.GraphicsWidth || GameMain.GraphicsHeight != GameMain.Config.GraphicsHeight)
            {
                new GUIMessageBox(
                    TextManager.Get("RestartRequiredLabel"),
                    TextManager.Get("RestartRequiredText"));
            }

            return true;
        }

        private bool DiscardSettings(GUIButton button, object userData)
        {
            GameMain.Config.Load("config.xml");
            if (userData is Tab) SelectTab((Tab)userData);

            return true;
        }


        private bool TutorialButtonClicked(GUIButton button, object obj)
        {
            //!!!!!!!!!!!!!!!!!! placeholder
            TutorialMode.StartTutorial(Tutorials.TutorialType.TutorialTypes[0]);

            return true;
        }

        private bool JoinServerClicked(GUIButton button, object obj)
        {
            GameMain.ServerListScreen.Select();
            return true;
        }

        private bool ChangeMaxPlayers(GUIButton button, object obj)
        {
            int currMaxPlayers = 8;

            int.TryParse(maxPlayersBox.Text, out currMaxPlayers);
            currMaxPlayers = (int)MathHelper.Clamp(currMaxPlayers + (int)button.UserData, 1, NetConfig.MaxPlayers);

            maxPlayersBox.Text = currMaxPlayers.ToString();

            return true;
        }

        private bool HostServerClicked(GUIButton button, object obj)
        {
            string name = serverNameBox.Text;
            if (string.IsNullOrEmpty(name))
            {
                serverNameBox.Flash();
                return false;
            }

            int port;
            if (!int.TryParse(portBox.Text, out port) || port < 0 || port > 65535)
            {
                portBox.Text = NetConfig.DefaultPort.ToString();
                portBox.Flash();

                return false;
            }

            GameMain.NetLobbyScreen = new NetLobbyScreen();

            try
            {
                GameMain.NetworkMember = new GameServer(name, port, isPublicBox.Selected, passwordBox.Text, useUpnpBox.Selected, int.Parse(maxPlayersBox.Text));
            }

            catch (Exception e)
            {
                DebugConsole.ThrowError("Failed to start server", e);
            }

            GameMain.NetLobbyScreen.IsServer = true;
            //Game1.NetLobbyScreen.Select();
            return true;
        }


        private bool QuitClicked(GUIButton button, object obj)
        {
            game.Exit();
            return true;
        }


        public override void AddToGUIUpdateList()
        {
            buttonsTab.AddToGUIUpdateList();
            if (selectedTab > 0) menuTabs[(int)selectedTab].AddToGUIUpdateList();
        }

        public override void Update(double deltaTime)
        {
            buttonsTab.Update((float)deltaTime);

            if (selectedTab > 0) menuTabs[(int)selectedTab].Update((float)deltaTime);

            GameMain.TitleScreen.TitlePosition =
                Vector2.Lerp(GameMain.TitleScreen.TitlePosition, new Vector2(
                    GameMain.TitleScreen.TitleSize.X / 2.0f * GameMain.TitleScreen.Scale + 30.0f,
                    GameMain.TitleScreen.TitleSize.Y / 2.0f * GameMain.TitleScreen.Scale + 30.0f),
                    0.1f);

            // ui test, TODO: remove
            if (Keyboard.GetState().IsKeyDown(Keys.R))
            {
                outerElement.RectTransform.ResetScale();
                innerElements.Clear();
                for (int i = 0; i < 5; i++)
                {
                    //var parent = innerElements.LastOrDefault();
                    //if (parent == null)
                    //{
                    //    parent = outerElement;
                    //}
                    var parent = outerElement;
                    GUIFrame element;
                    switch (i)
                    {
                        case 0:
                            element = new GUIFrame(new RectTransform(new Vector2(0.4f, 0.4f), parent.RectTransform, anchor: Anchor.TopLeft), color: GetRandomColor());
                            // absolute element
                            //element = new GUIFrame(new RectTransform(new Point(200, 200), parent.RectTransform, anchor: Anchor.TopLeft), color: GetRandomColor());
                            break;
                        case 1:
                            element = new GUIFrame(new RectTransform(new Vector2(0.4f, 0.4f), parent.RectTransform, anchor: Anchor.TopRight), color: GetRandomColor());
                            break;
                        case 2:
                            element = new GUIFrame(new RectTransform(new Vector2(0.4f, 0.4f), parent.RectTransform, anchor: Anchor.BottomLeft), color: GetRandomColor());
                            break;
                        case 3:
                            element = new GUIFrame(new RectTransform(new Vector2(0.4f, 0.4f), parent.RectTransform, anchor: Anchor.BottomRight), color: GetRandomColor());
                            break;
                        case 4:
                            element = new GUIFrame(new RectTransform(new Vector2(0.25f, 0.25f), parent.RectTransform, anchor: Anchor.Center), color: GetRandomColor());
                            break;
                        default:
                            element = new GUIFrame(new RectTransform(new Vector2(0.1f, 0.1f), parent.RectTransform, anchor: Anchor.Center), color: GetRandomColor());
                            break;
                    }
                    if (i < 4)
                    {
                        element.RectTransform.RelativeOffset = new Point(50, 50);
                    }
                    innerElements.Add(element);
                }
            }
            UpdateRects();
        }

        public override void Draw(double deltaTime, GraphicsDevice graphics, SpriteBatch spriteBatch)
        {
            graphics.Clear(Color.CornflowerBlue);

            GameMain.TitleScreen.DrawLoadingText = false;
            GameMain.TitleScreen.Draw(spriteBatch, graphics, (float)deltaTime);

            //Game1.GameScreen.DrawMap(graphics, spriteBatch);

            spriteBatch.Begin(SpriteSortMode.Immediate, null, null, null, GameMain.ScissorTestEnable);

            buttonsTab.Draw(spriteBatch);
            if (selectedTab > 0) menuTabs[(int)selectedTab].Draw(spriteBatch);

            GUI.Draw((float)deltaTime, spriteBatch, null);

            // ui test, TODO: remove
            //outerElement.Draw(spriteBatch);
            //innerElements.ForEach(e => e.Draw(spriteBatch));

#if DEBUG
            GUI.Font.DrawString(spriteBatch, "Barotrauma v" + GameMain.Version + " (debug build)", new Vector2(10, GameMain.GraphicsHeight - 20), Color.White);
#else
            GUI.Font.DrawString(spriteBatch, "Barotrauma v" + GameMain.Version, new Vector2(10, GameMain.GraphicsHeight - 20), Color.White);
#endif

            spriteBatch.End();
        }

        private void StartGame(Submarine selectedSub, string saveName, string mapSeed)
        {
            if (string.IsNullOrEmpty(saveName)) return;

            string[] existingSaveFiles = SaveUtil.GetSaveFiles(SaveUtil.SaveType.Singleplayer);

            if (Array.Find(existingSaveFiles, s => s == saveName) != null)
            {
                new GUIMessageBox("Save name already in use", "Please choose another name for the save file");
                return;
            }

            if (selectedSub == null)
            {
                new GUIMessageBox(TextManager.Get("SubNotSelected"), TextManager.Get("SelectSubRequest"));
                return;
            }

            if (!Directory.Exists(SaveUtil.TempPath))
            {
                Directory.CreateDirectory(SaveUtil.TempPath);
            }

            File.Copy(selectedSub.FilePath, Path.Combine(SaveUtil.TempPath, selectedSub.Name + ".sub"), true);

            selectedSub = new Submarine(Path.Combine(SaveUtil.TempPath, selectedSub.Name + ".sub"), "");

            GameMain.GameSession = new GameSession(selectedSub, saveName, GameModePreset.list.Find(gm => gm.Name == "Single Player"));
            (GameMain.GameSession.GameMode as CampaignMode).GenerateMap(mapSeed);

            GameMain.LobbyScreen.Select();
        }

        private void LoadGame(string saveFile)
        {
            if (string.IsNullOrWhiteSpace(saveFile)) return;

            try
            {
                SaveUtil.LoadGame(saveFile);
            }
            catch (Exception e)
            {
                DebugConsole.ThrowError("Loading save \"" + saveFile + "\" failed", e);
                return;
            }


            GameMain.LobbyScreen.Select();
        }

        // ui test, TODO: remove
        #region Random
        private static Random _random;
        public static Random Random
        {
            get
            {
                if (_random == null)
                {
                    _random = new Random();
                }
                return _random;
            }
        }

        public static float RandomRange(int min, int max)
        {
            return Random.Next(min, max);
        }

        /// <summary>
        /// Returns a random value between 0 and 1
        /// </summary>
        public static float RandomValue()
        {
            float v = Random.Next(0, 100);
            return v / 100;
        }

        public static Color GetRandomColor()
        {
            return new Color(RandomValue(), RandomValue(), RandomValue());
        }
        #endregion

        // ui test, TODO: remove
        private void UpdateRects()
        {
            var element = Keyboard.GetState().IsKeyDown(Keys.LeftControl) ? innerElements.FirstOrDefault() : outerElement;
            if (element == null) { return; }
            // Scaling
            if (Keyboard.GetState().IsKeyDown(Keys.OemPlus))
            {
                element.RectTransform.ChangeScale(element.RectTransform.LocalScale *= 1.01f);
            }
            if (Keyboard.GetState().IsKeyDown(Keys.OemMinus))
            {
                element.RectTransform.ChangeScale(element.RectTransform.LocalScale *= 0.99f);
            }
            // Size
            if (Keyboard.GetState().IsKeyDown(Keys.Left))
            {
                element.RectTransform.Resize(element.RectTransform.NonScaledSize - new Point(1, 0));
            }
            if (Keyboard.GetState().IsKeyDown(Keys.Right))
            {
                element.RectTransform.Resize(element.RectTransform.NonScaledSize + new Point(1, 0));
            }
            if (Keyboard.GetState().IsKeyDown(Keys.Up))
            {
                element.RectTransform.Resize(element.RectTransform.NonScaledSize + new Point(0, 1));
            }
            if (Keyboard.GetState().IsKeyDown(Keys.Down))
            {
                element.RectTransform.Resize(element.RectTransform.NonScaledSize - new Point(0, 1));
            }
            // Translation (absolute offset)
            if (Keyboard.GetState().IsKeyDown(Keys.A))
            {
                element.RectTransform.Translate(new Point(-1, 0));
            }
            if (Keyboard.GetState().IsKeyDown(Keys.D))
            {
                element.RectTransform.Translate(new Point(1, 0));
            }
            if (Keyboard.GetState().IsKeyDown(Keys.W))
            {
                element.RectTransform.Translate(new Point(0, -1));
            }
            if (Keyboard.GetState().IsKeyDown(Keys.S))
            {
                element.RectTransform.Translate(new Point(0, 1));
            }
            // Positioning (with matching anchors and pivots)
            if (Keyboard.GetState().IsKeyDown(Keys.NumPad7))
            {
                element.RectTransform.SetPosition(Anchor.TopLeft);
            }
            if (Keyboard.GetState().IsKeyDown(Keys.NumPad8))
            {
                element.RectTransform.SetPosition(Anchor.TopCenter);
            }
            if (Keyboard.GetState().IsKeyDown(Keys.NumPad9))
            {
                element.RectTransform.SetPosition(Anchor.TopRight);
            }
            if (Keyboard.GetState().IsKeyDown(Keys.NumPad4))
            {
                element.RectTransform.SetPosition(Anchor.CenterLeft);
            }
            if (Keyboard.GetState().IsKeyDown(Keys.NumPad5))
            {
                element.RectTransform.SetPosition(Anchor.Center);
            }
            if (Keyboard.GetState().IsKeyDown(Keys.NumPad6))
            {
                element.RectTransform.SetPosition(Anchor.CenterRight);
            }
            if (Keyboard.GetState().IsKeyDown(Keys.NumPad1))
            {
                element.RectTransform.SetPosition(Anchor.BottomLeft);
            }
            if (Keyboard.GetState().IsKeyDown(Keys.NumPad2))
            {
                element.RectTransform.SetPosition(Anchor.BottomCenter);
            }
            if (Keyboard.GetState().IsKeyDown(Keys.NumPad3))
            {
                element.RectTransform.SetPosition(Anchor.BottomRight);
            }
        }
    }
}

using Barotrauma.Extensions;
using Barotrauma.IO;
using Barotrauma.Items.Components;
using Barotrauma.Steam;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Input;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using System.Xml.Linq;
using Barotrauma.PerkBehaviors;

namespace Barotrauma.Networking
{
    sealed class GameClient : NetworkMember
    {
        public static readonly TimeSpan CampaignSaveTransferTimeOut = new TimeSpan(0, 0, seconds: 100);
        //this should be longer than CampaignSaveTransferTimeOut - we shouldn't give up starting the round if we're still waiting for the save file
        public static readonly TimeSpan LevelTransitionTimeOut = new TimeSpan(0, 0, seconds: 150);

        public override bool IsClient => true;
        public override bool IsServer => false;
        
#if DEBUG
        public float DebugServerVoipAmplitude;
#endif

        public override Voting Voting { get; }

        private UInt16 nameId = 0;

        public string Name { get; private set; }

        public string PendingName = string.Empty;

        public void SetName(string value)
        {
            if (string.IsNullOrEmpty(value)) { return; }
            Name = value;
            nameId++;
        }

        public void ForceNameJobTeamUpdate()
        {
            // Triggers SendLobbyUpdate() which causes the server to call GameServer.ClientReadLobby()
            nameId++;
        }

        public ClientPeer ClientPeer { get; private set; }

        private GUIMessageBox reconnectBox, waitInServerQueueBox;

        //TODO: move these to NetLobbyScreen
        public LocalizedString endRoundVoteText;
        public GUITickBox EndVoteTickBox;
        private readonly GUIComponent buttonContainer;

        public readonly NetStats NetStats;

        protected GUITickBox cameraFollowsSub;
        public GUITickBox FollowSubTickBox => cameraFollowsSub;

        public bool IsFollowSubTickBoxVisible =>
            GameStarted && Screen.Selected == GameMain.GameScreen &&
            cameraFollowsSub != null && cameraFollowsSub.Visible;

        public CameraTransition EndCinematic;

        public bool LateCampaignJoin = false;

        private ClientPermissions permissions = ClientPermissions.None;
        private List<Identifier> permittedConsoleCommands = new List<Identifier>();

        private bool connected;

        private enum RoundInitStatus
        {
            NotStarted,
            Starting,
            WaitingForStartGameFinalize,
            Started,
            Error,
            Interrupted
        }

        private UInt16? debugStartGameCampaignSaveID;

        private RoundInitStatus roundInitStatus = RoundInitStatus.NotStarted;

        public bool RoundStarting => roundInitStatus == RoundInitStatus.Starting || roundInitStatus == RoundInitStatus.WaitingForStartGameFinalize;

        private readonly List<Client> otherClients;

        public readonly List<SubmarineInfo> ServerSubmarines = new List<SubmarineInfo>();

        public string ServerName => ServerSettings.ServerName;

        private bool canStart;

        private UInt16 lastSentChatMsgID = 0; //last message this client has successfully sent
        private UInt16 lastQueueChatMsgID = 0; //last message added to the queue
        private readonly List<ChatMessage> chatMsgQueue = new List<ChatMessage>();

        public UInt16 LastSentEntityEventID;

#if DEBUG
        public void PrintReceiverTransters()
        {
            foreach (var transfer in FileReceiver.ActiveTransfers)
            {
                DebugConsole.NewMessage(transfer.FileName + " " + transfer.Progress.ToString());
            }
        }
#endif

        //has the client been given a character to control this round
        public bool HasSpawned;

        public float EndRoundTimeRemaining { get; private set; }

        public LocalizedString TraitorFirstObjective;
        public TraitorEventPrefab TraitorMission = null;

        public byte SessionId { get; private set; }

        public VoipClient VoipClient
        {
            get;
            private set;
        }

        public override IReadOnlyList<Client> ConnectedClients
        {
            get
            {
                return otherClients;
            }
        }

        public Client MyClient => ConnectedClients.FirstOrDefault(c => c.SessionId == SessionId);

        public Option<int> Ping
        {
            get
            {
                if (MyClient is null || MyClient.Ping == 0) { return Option<int>.None(); }
                return Option<int>.Some(MyClient.Ping);
            }
        }

        private readonly List<Client> previouslyConnectedClients = new List<Client>();
        public IEnumerable<Client> PreviouslyConnectedClients
        {
            get { return previouslyConnectedClients; }
        }

        public readonly FileReceiver FileReceiver;

        public bool MidRoundSyncing
        {
            get { return EntityEventManager.MidRoundSyncing; }
        }

        public readonly ClientEntityEventManager EntityEventManager;

        public bool? WaitForNextRoundRespawn
        {
            get;
            set;
        }

        private readonly ImmutableArray<Endpoint> serverEndpoints;
        private readonly Option<int> ownerKey;

        public bool IsServerOwner => ownerKey.IsSome();

        internal readonly struct PermissionChangedEvent
        {
            public readonly ClientPermissions NewPermissions;
            public readonly ImmutableArray<Identifier> NewPermittedConsoleCommands;

            public PermissionChangedEvent(ClientPermissions newPermissions, IReadOnlyList<Identifier> newPermittedConsoleCommands)
            {
                NewPermissions = newPermissions;
                NewPermittedConsoleCommands = newPermittedConsoleCommands.ToImmutableArray();
            }
        }

        public readonly NamedEvent<PermissionChangedEvent> OnPermissionChanged = new NamedEvent<PermissionChangedEvent>();

        public GameClient(string newName, Endpoint endpoint, string serverName, Option<int> ownerKey)
            : this(newName, endpoint.ToEnumerable().ToImmutableArray(), serverName, ownerKey) { }

        public GameClient(string newName, ImmutableArray<Endpoint> endpoints, string serverName, Option<int> ownerKey)
        {
            //TODO: gui stuff should probably not be here?
            this.ownerKey = ownerKey;

            roundInitStatus = RoundInitStatus.NotStarted;

            NetStats = new NetStats();

            inGameHUD = new GUIFrame(new RectTransform(GUI.Canvas.RelativeSize, GUI.Canvas), style: null)
            {
                CanBeFocused = false
            };

            chatBox = new ChatBox(inGameHUD, isSinglePlayer: false);
            chatBox.OnEnterMessage += EnterChatMessage;
            chatBox.InputBox.OnTextChanged += TypingChatMessage;

            buttonContainer = new GUILayoutGroup(HUDLayoutSettings.ToRectTransform(HUDLayoutSettings.ButtonAreaTop, inGameHUD.RectTransform),
                isHorizontal: true, childAnchor: Anchor.CenterRight)
            {
                AbsoluteSpacing = 5,
                CanBeFocused = false
            };

            endRoundVoteText = TextManager.Get("EndRound");
            EndVoteTickBox = new GUITickBox(new RectTransform(new Vector2(0.1f, 0.4f), buttonContainer.RectTransform) { MinSize = new Point(150, 0) },
                endRoundVoteText)
            {
                OnSelected = ToggleEndRoundVote,
                Visible = false
            };
            EndVoteTickBox.TextBlock.Wrap = true;

            ShowLogButton = new GUIButton(new RectTransform(new Vector2(0.1f, 0.6f), buttonContainer.RectTransform) { MinSize = new Point(150, 0) },
                TextManager.Get("ServerLog"))
            {
                OnClicked = (GUIButton button, object userData) =>
                {
                    if (ServerSettings.ServerLog.LogFrame == null)
                    {
                        ServerSettings.ServerLog.CreateLogFrame();
                    }
                    else
                    {
                        ServerSettings.ServerLog.LogFrame = null;
                        GUI.KeyboardDispatcher.Subscriber = null;
                    }
                    return true;
                }
            };
            ShowLogButton.TextBlock.AutoScaleHorizontal = true;
            
            cameraFollowsSub = new GUITickBox(new RectTransform(new Vector2(0.1f, 0.4f), buttonContainer.RectTransform)
            {
                MinSize = new Point(150, 0)
            }, TextManager.Get("CamFollowSubmarine"))
            {
                Selected = Camera.FollowSub,
                OnSelected = (tbox) =>
                {
                    Camera.FollowSub = tbox.Selected;
                    return true;
                }
            };

            GameMain.DebugDraw = false;
            Hull.EditFire = false;
            Hull.EditWater = false;

            SetName(newName);

            EntityEventManager = new ClientEntityEventManager(this);

            FileReceiver = new FileReceiver();
            FileReceiver.OnFinished += OnFileReceived;
            FileReceiver.OnTransferFailed += OnTransferFailed;

            characterInfo = new CharacterInfo(CharacterPrefab.HumanSpeciesName, Name, originalName: null)
            {
                Job = null
            };

            otherClients = new List<Client>();

            ServerSettings = new ServerSettings(this, serverName, 0, 0, 0, false, false);
            Voting = new Voting();

            serverEndpoints = endpoints;
            InitiateServerJoin();

            //ServerLog = new ServerLog("");

            ChatMessage.LastID = 0;
            GameMain.ResetNetLobbyScreen();
        }

        public ServerInfo CreateServerInfoFromSettings()
        {
            var serverInfo = ServerInfo.FromServerEndpoints(ClientPeer.AllServerEndpoints, ServerSettings);
            GameMain.ServerListScreen.UpdateOrAddServerInfo(serverInfo);
            return serverInfo;
        }

        private void InitiateServerJoin()
        {
            LastClientListUpdateID = 0;

            foreach (var c in ConnectedClients)
            {
                GameMain.NetLobbyScreen.RemovePlayer(c);
                c.Dispose();
            }
            otherClients.Clear();

            chatBox.InputBox.Enabled = false;
            if (GameMain.NetLobbyScreen?.ChatInput != null)
            {
                GameMain.NetLobbyScreen.ChatInput.Enabled = false;
            }

            myCharacter = Character.Controlled;
            ChatMessage.LastID = 0;

            ClientPeer?.Close(PeerDisconnectPacket.WithReason(DisconnectReason.Disconnected));
            ClientPeer = CreateNetPeer();
            ClientPeer.Start();

            CoroutineManager.StartCoroutine(WaitForStartingInfo(), "WaitForStartingInfo");
        }

        public static void SetLobbyPublic(bool isPublic)
        {
            SteamManager.SetLobbyPublic(isPublic);
        }

        private ClientPeer CreateNetPeer()
        {
            Networking.ClientPeer.Callbacks callbacks = new ClientPeer.Callbacks(
                ReadDataMessage,
                OnClientPeerDisconnect,
                OnConnectionInitializationComplete);
            return serverEndpoints.First() switch
            {
                LidgrenEndpoint lidgrenEndpoint
                    => new LidgrenClientPeer(lidgrenEndpoint, callbacks, ownerKey),
                P2PEndpoint when ownerKey.TryUnwrap(out int key)
                    => new P2POwnerPeer(callbacks, key, serverEndpoints.Cast<P2PEndpoint>().ToImmutableArray()),
                P2PEndpoint when ownerKey.IsNone()
                    => new P2PClientPeer(serverEndpoints.Cast<P2PEndpoint>().ToImmutableArray(), callbacks),
                _ => throw new ArgumentOutOfRangeException()
            };
        }

        public void CreateServerCrashMessage()
        {
            // Close any message boxes that say "The server has crashed."
            var basicServerCrashMsg = TextManager.Get($"{nameof(DisconnectReason)}.{nameof(DisconnectReason.ServerCrashed)}");
            GUIMessageBox.MessageBoxes
                .OfType<GUIMessageBox>()
                .Where(mb => mb.Text?.Text == basicServerCrashMsg)
                .ToArray()
                .ForEach(mb => mb.Close());

            // Open a new message box with the crash report path
            if (GUIMessageBox.MessageBoxes.All(
                    mb => (mb as GUIMessageBox)?.Text?.Text != ChildServerRelay.CrashMessage))
            {
                var msgBox = new GUIMessageBox(TextManager.Get("ConnectionLost"), ChildServerRelay.CrashMessage);
                msgBox.Buttons[0].OnClicked += ReturnToPreviousMenu;
            }
        }

        private bool ReturnToPreviousMenu(GUIButton button, object obj)
        {
            Submarine.Unload();
            GameMain.Client = null;
            GameMain.GameSession = null;
            if (IsServerOwner)
            {
                GameMain.MainMenuScreen.Select();
            }
            else
            {
                GameMain.ServerListScreen.Select();
            }

            GUIMessageBox.MessageBoxes.Clear();

            return true;
        }

        private bool connectCancelled;
        private void CancelConnect()
        {
            Quit();
        }

        // Before main looping starts, we loop here and wait for approval message
        private IEnumerable<CoroutineStatus> WaitForStartingInfo()
        {
            GUI.SetCursorWaiting();

            connectCancelled = false;
            // When this is set to true, we are approved and ready to go
            canStart = false;

            DateTime timeOut = DateTime.Now + new TimeSpan(0, 0, 200);

            // Loop until we are approved
            LocalizedString connectingText = TextManager.Get("Connecting");
            while (!canStart && !connectCancelled)
            {
                if (reconnectBox == null && waitInServerQueueBox == null)
                {
                    string serverDisplayName = ServerName;
                    if (string.IsNullOrEmpty(serverDisplayName) && ClientPeer?.ServerConnection is SteamP2PConnection steamConnection)
                    {
                        if (SteamManager.IsInitialized && steamConnection.AccountInfo.AccountId.TryUnwrap(out var accountId) && accountId is SteamId steamId)
                        {
                            serverDisplayName = steamId.ToString();
                            string steamUserName = new Steamworks.Friend(steamId.Value).Name;
                            if (!string.IsNullOrEmpty(steamUserName) && steamUserName != "[unknown]")
                            {
                                serverDisplayName = steamUserName;
                            }
                        }
                    }
                    if (string.IsNullOrEmpty(serverDisplayName)) { serverDisplayName = TextManager.Get("Unknown").Value; }

                    CreateReconnectBox(
                        connectingText,
                        TextManager.GetWithVariable("ConnectingTo", "[serverip]", serverDisplayName));
                }

                if (reconnectBox != null)
                {
                    reconnectBox.Header.Text = connectingText + new string('.', ((int)Timing.TotalTime % 3 + 1));
                }

                yield return CoroutineStatus.Running;

                if (DateTime.Now > timeOut)
                {
                    ClientPeer?.Close(PeerDisconnectPacket.WithReason(DisconnectReason.Timeout));
                    var msgBox = new GUIMessageBox(TextManager.Get("ConnectionFailed"), TextManager.Get("CouldNotConnectToServer"))
                    {
                        DisplayInLoadingScreens = true
                    };
                    msgBox.Buttons[0].OnClicked += ReturnToPreviousMenu;
                    CloseReconnectBox();
                    break;
                }

                if (ClientPeer.WaitingForPassword && !canStart && !connectCancelled)
                {
                    GUI.ClearCursorWait();
                    CloseReconnectBox();

                    while (ClientPeer.WaitingForPassword)
                    {
                        yield return CoroutineStatus.Running;
                    }
                }
            }

            CloseReconnectBox();

            GUI.ClearCursorWait();
            if (connectCancelled) { yield return CoroutineStatus.Success; }

            yield return CoroutineStatus.Success;
        }

        public void Update(float deltaTime)
        {
#if DEBUG
            if (PlayerInput.GetKeyboardState.IsKeyDown(Keys.P)) return;

            if (PlayerInput.KeyHit(Keys.Home))
            {
                OnPermissionChanged.Invoke(new PermissionChangedEvent(permissions, permittedConsoleCommands));
            }
#endif

            foreach (Client c in ConnectedClients)
            {
                if (c.Character != null && c.Character.Removed) { c.Character = null; }
                c.UpdateVoipSound();
            }

            if (VoipCapture.Instance != null)
            {
                if (VoipCapture.Instance.LastEnqueueAudio > DateTime.Now - new TimeSpan(0, 0, 0, 0, milliseconds: 100))
                {
                    if (Screen.Selected == GameMain.NetLobbyScreen)
                    {
                        GameMain.NetLobbyScreen.SetPlayerSpeaking(MyClient);
                    }
                    else
                    {
                        GameMain.GameSession?.CrewManager?.SetClientSpeaking(MyClient);
                    }
                }
            }

            NetStats.Update(deltaTime);

            UpdateHUD(deltaTime);

            try
            {
                incomingMessagesToProcess.Clear();
                incomingMessagesToProcess.AddRange(pendingIncomingMessages);
                foreach (var inc in incomingMessagesToProcess)
                {
                    ReadDataMessage(inc);
                }
                pendingIncomingMessages.Clear();
                ClientPeer?.Update(deltaTime);
            }
            catch (Exception e)
            {
                string errorMsg = "Error while reading a message from server. ";
                if (GameMain.Client == null) { errorMsg += "Client disposed."; }
                AppendExceptionInfo(ref errorMsg, out Entity causingEntity, e);

                string targetSite = e.TargetSite?.ToString() ?? "unknown";
                GameAnalyticsManager.AddErrorEventOnce("GameClient.Update:CheckServerMessagesException" + targetSite, GameAnalyticsManager.ErrorSeverity.Error, errorMsg);
                DebugConsole.ThrowError(errorMsg, contentPackage: causingEntity?.ContentPackage);
                new GUIMessageBox(TextManager.Get("Error"), TextManager.GetWithVariables("MessageReadError", ("[message]", e.Message), ("[targetsite]", targetSite)))
                {
                    DisplayInLoadingScreens = true
                };
                Quit();
                GUI.DisableHUD = false;
                GameMain.ServerListScreen.Select();
                return;
            }

            if (!connected) { return; }

            CloseReconnectBox();

            if (GameStarted && Screen.Selected == GameMain.GameScreen)
            {
                EndVoteTickBox.Visible = ServerSettings.AllowEndVoting && HasSpawned;

                RespawnManager?.Update(deltaTime);

                if (updateTimer <= DateTime.Now)
                {
                    SendIngameUpdate();
                }
            }
            else
            {
                if (updateTimer <= DateTime.Now)
                {
                    SendLobbyUpdate();
                }
            }

            if (ServerSettings.VoiceChatEnabled)
            {
                VoipClient?.SendToServer();
            }

            if (IsServerOwner && connected && !connectCancelled)
            {
                if (GameMain.WindowActive)
                {
                    if (!ChildServerRelay.IsProcessAlive)
                    {
                        Quit();
                        CreateServerCrashMessage();
                    }
                }
            }

            if (updateTimer <= DateTime.Now)
            {
                // Update current time
                updateTimer = DateTime.Now + UpdateInterval;
            }
        }

        private readonly List<IReadMessage> pendingIncomingMessages = new List<IReadMessage>();
        private readonly List<IReadMessage> incomingMessagesToProcess = new List<IReadMessage>();

        private void ReadDataMessage(IReadMessage inc)
        {
            ServerPacketHeader header = (ServerPacketHeader)inc.ReadByte();

            if (roundInitStatus == RoundInitStatus.WaitingForStartGameFinalize
                && header is not (
                    ServerPacketHeader.STARTGAMEFINALIZE
                    or ServerPacketHeader.ENDGAME
                    or ServerPacketHeader.PING_REQUEST
                    or ServerPacketHeader.FILE_TRANSFER))
            {
                //rewind the header byte we just read
                inc.BitPosition -= 8;
                pendingIncomingMessages.Add(inc);
                return;
            }

            MultiPlayerCampaign campaign = GameMain.NetLobbyScreen.SelectedMode == GameMain.GameSession?.GameMode.Preset ?
                                                GameMain.GameSession?.GameMode as MultiPlayerCampaign : null;

            if (Screen.Selected is ModDownloadScreen)
            {
                switch (header)
                {
                    case ServerPacketHeader.UPDATE_LOBBY:
                    case ServerPacketHeader.PING_REQUEST:
                    case ServerPacketHeader.FILE_TRANSFER:
                    case ServerPacketHeader.PERMISSIONS:
                    case ServerPacketHeader.CHEATS_ENABLED:
                        //allow interpreting this packet
                        break;
                    case ServerPacketHeader.STARTGAME:
                        GameStarted = true;
                        return;
                    case ServerPacketHeader.ENDGAME:
                        GameStarted = false;
                        return;
                    default:
                        return; //ignore any other packets
                }
            }
            
            switch (header)
            {
                case ServerPacketHeader.PING_REQUEST:
                    IWriteMessage response = new WriteOnlyMessage();
                    response.WriteByte((byte)ClientPacketHeader.PING_RESPONSE);
                    byte requestLen = inc.ReadByte();
                    response.WriteByte(requestLen);
                    for (int i = 0; i < requestLen; i++)
                    {
                        byte b = inc.ReadByte();
                        response.WriteByte(b);
                    }
                    ClientPeer.Send(response, DeliveryMethod.Unreliable);
                    break;
                case ServerPacketHeader.CLIENT_PINGS:
                    byte clientCount = inc.ReadByte();
                    for (int i = 0; i < clientCount; i++)
                    {
                        byte clientId = inc.ReadByte();
                        UInt16 clientPing = inc.ReadUInt16();
                        Client client = ConnectedClients.Find(c => c.SessionId == clientId);
                        if (client != null)
                        {
                            client.Ping = clientPing;
                        }
                    }
                    break;
                case ServerPacketHeader.UPDATE_LOBBY:
                    ReadLobbyUpdate(inc);
                    break;
                case ServerPacketHeader.UPDATE_INGAME:
                    try
                    {
                        ReadIngameUpdate(inc);
                    }
                    catch (Exception e)
                    {
                        string errorMsg = "Error while reading an ingame update message from server.";
                        AppendExceptionInfo(ref errorMsg, out Entity causingEntity, e);
                        GameAnalyticsManager.AddErrorEventOnce("GameClient.ReadDataMessage:ReadIngameUpdate", GameAnalyticsManager.ErrorSeverity.Error, errorMsg);
                        throw;
                    }
                    break;
                case ServerPacketHeader.VOICE:
                    if (VoipClient == null)
                    {
                        string errorMsg = "Failed to read a voice packet from the server (VoipClient == null). ";
                        if (GameMain.Client == null) { errorMsg += "Client disposed. "; }
                        errorMsg += "\n" + Environment.StackTrace.CleanupStackTrace();
                        GameAnalyticsManager.AddErrorEventOnce(
                            "GameClient.ReadDataMessage:VoipClientNull",
                            GameMain.Client == null ? GameAnalyticsManager.ErrorSeverity.Error : GameAnalyticsManager.ErrorSeverity.Warning,
                            errorMsg);
                        return;
                    }

                    VoipClient.Read(inc);
                    break;
#if DEBUG
                case ServerPacketHeader.VOICE_AMPLITUDE_DEBUG:
                    GameMain.Client.DebugServerVoipAmplitude = inc.ReadRangedSingle(min: 0, max: 1, bitCount: 8);
                    break;
#endif
                case ServerPacketHeader.QUERY_STARTGAME:
                    DebugConsole.Log("Received QUERY_STARTGAME packet.");
                    string subName = inc.ReadString();
                    string subHash = inc.ReadString();

                    bool hasEnemySub = inc.ReadBoolean();

                    string enemySubName = subName;
                    string enemySubHash = subHash;
                    if (hasEnemySub)
                    {
                        enemySubName = inc.ReadString();
                        enemySubHash = inc.ReadString();
                    }

                    bool usingShuttle = inc.ReadBoolean();
                    string shuttleName = inc.ReadString();
                    string shuttleHash = inc.ReadString();

                    byte campaignID = inc.ReadByte();
                    UInt16 campaignSaveID = inc.ReadUInt16();
                    Dictionary<MultiPlayerCampaign.NetFlags, UInt16> campaignUpdateIDs = new Dictionary<MultiPlayerCampaign.NetFlags, ushort>();
                    foreach (MultiPlayerCampaign.NetFlags flag in Enum.GetValues(typeof(MultiPlayerCampaign.NetFlags)))
                    {
                        campaignUpdateIDs[flag] = inc.ReadUInt16();
                    }

                    IWriteMessage readyToStartMsg = new WriteOnlyMessage();
                    readyToStartMsg.WriteByte((byte)ClientPacketHeader.RESPONSE_STARTGAME);

                    if (campaign != null) { campaign.PendingSubmarineSwitch = null; }
                    GameMain.NetLobbyScreen.UsingShuttle = usingShuttle;
                    bool readyToStart;
                    if (campaign == null && campaignID == 0)
                    {
                        readyToStart = GameMain.NetLobbyScreen.TrySelectSub(subName, subHash, SelectedSubType.Sub, GameMain.NetLobbyScreen.SubList) &&
                                       GameMain.NetLobbyScreen.TrySelectSub(shuttleName, shuttleHash, SelectedSubType.Shuttle, GameMain.NetLobbyScreen.ShuttleList.ListBox);

                        if (hasEnemySub && !GameMain.NetLobbyScreen.TrySelectSub(enemySubName, enemySubHash, SelectedSubType.EnemySub, GameMain.NetLobbyScreen.SubList))
                        {
                            readyToStart = false;
                        }
                    }
                    else
                    {
                        readyToStart =
                            campaign != null &&
                            campaign.CampaignID == campaignID &&
                            campaign.LastSaveID == campaignSaveID &&
                            campaignUpdateIDs.All(kvp => campaign.GetLastUpdateIdForFlag(kvp.Key) == kvp.Value);
                    }
                    readyToStartMsg.WriteBoolean(readyToStart);

                    DebugConsole.Log(readyToStart ? "Ready to start." : "Not ready to start.");

                    WriteCharacterInfo(readyToStartMsg);

                    ClientPeer.Send(readyToStartMsg, DeliveryMethod.Reliable);

                    if (readyToStart && !CoroutineManager.IsCoroutineRunning("WaitForStartRound"))
                    {
                        CoroutineManager.StartCoroutine(NetLobbyScreen.WaitForStartRound(startButton: null), "WaitForStartRound");
                    }
                    break;
                case ServerPacketHeader.WARN_STARTGAME:
                    DebugConsole.Log("Received WARN_STARTGAME packet.");

                    RoundStartWarningData warningData = INetSerializableStruct.Read<RoundStartWarningData>(inc);
                    var team1IncompatiblePerks = ToolBox.UintIdentifierArrayToPrefabCollection(DisembarkPerkPrefab.Prefabs, warningData.Team1IncompatiblePerks);
                    var team2IncompatiblePerks = ToolBox.UintIdentifierArrayToPrefabCollection(DisembarkPerkPrefab.Prefabs, warningData.Team2IncompatiblePerks);

                    GameMain.NetLobbyScreen?.ShowStartRoundWarning(SerializableDateTime.UtcNow + TimeSpan.FromSeconds(warningData.RoundStartsAnywaysTimeInSeconds), warningData.Team1Sub, team1IncompatiblePerks, warningData.Team2Sub, team2IncompatiblePerks);
                    break;
                case ServerPacketHeader.CANCEL_STARTGAME:
                    DebugConsole.Log("Received CANCEL_STARTGAME packet.");
                    GameMain.NetLobbyScreen?.CloseStartRoundWarning();
                    if (GameMain.NetLobbyScreen?.ReadyToStartBox is { } readyToStartBox)
                    {
                        readyToStartBox.Selected = false;
                        SetReadyToStart(readyToStartBox);
                    }
                    break;
                case ServerPacketHeader.STARTGAME:
                    DebugConsole.Log("Received STARTGAME packet.");
                    if (Screen.Selected == GameMain.GameScreen && GameMain.GameSession?.GameMode is CampaignMode)
                    {
                        //start without a loading screen if playing a campaign round
                        CoroutineManager.StartCoroutine(StartGame(inc));
                    }
                    else
                    {
                        GUIMessageBox.CloseAll();
                        GameMain.Instance.ShowLoading(StartGame(inc), false);
                    }
                    break;
                case ServerPacketHeader.STARTGAMEFINALIZE:
                    DebugConsole.NewMessage("Received STARTGAMEFINALIZE packet. Round init status: " + roundInitStatus);
                    if (roundInitStatus == RoundInitStatus.WaitingForStartGameFinalize)
                    {
                        //waiting for a save file
                        if (campaign != null && 
                            NetIdUtils.IdMoreRecent(campaign.PendingSaveID, campaign.LastSaveID) &&
                            FileReceiver.ActiveTransfers.Any(t => t.FileType == FileTransferType.CampaignSave))
                        {
                            return;
                        }
                        ReadStartGameFinalize(inc);
                    }
                    break;
                case ServerPacketHeader.ENDGAME:
                    CampaignMode.TransitionType transitionType = (CampaignMode.TransitionType)inc.ReadByte();
                    bool save = inc.ReadBoolean();
                    string endMessage = string.Empty;

                    endMessage = inc.ReadString();
                    byte missionCount = inc.ReadByte();
                    for (int i = 0; i < missionCount; i++)
                    {
                        bool missionSuccessful = inc.ReadBoolean();
                        var mission = GameMain.GameSession?.GetMission(i);
                        if (mission != null)
                        {
                            mission.Completed = missionSuccessful;
                        }
                    }
                    CharacterTeamType winningTeam = (CharacterTeamType)inc.ReadByte();
                    if (winningTeam != CharacterTeamType.None)
                    {
                        GameMain.GameSession.WinningTeam = winningTeam;
                        var combatMission = GameMain.GameSession.Missions.FirstOrDefault(m => m is CombatMission);
                        if (combatMission != null)
                        {
                            combatMission.Completed = true;
                        }
                    }

                    bool includesTraitorInfo = inc.ReadBoolean();
                    TraitorManager.TraitorResults? traitorResults = null;
                    if (includesTraitorInfo)
                    {
                        traitorResults = INetSerializableStruct.Read<TraitorManager.TraitorResults>(inc);
                    }

                    roundInitStatus = RoundInitStatus.Interrupted;
                    CoroutineManager.StartCoroutine(EndGame(endMessage, transitionType, traitorResults), "EndGame");
                    GUI.SetSavingIndicatorState(save);
                    break;
                case ServerPacketHeader.CAMPAIGN_SETUP_INFO:
                    byte saveCount = inc.ReadByte();
                    List<CampaignMode.SaveInfo> saveInfos = new List<CampaignMode.SaveInfo>();
                    for (int i = 0; i < saveCount; i++)
                    {
                        saveInfos.Add(INetSerializableStruct.Read<CampaignMode.SaveInfo>(inc));
                    }
                    MultiPlayerCampaign.StartCampaignSetup(saveInfos);
                    break;
                case ServerPacketHeader.PERMISSIONS:
                    ReadPermissions(inc);
                    break;
                case ServerPacketHeader.ACHIEVEMENT:
                    ReadAchievement(inc);
                    break;
                case ServerPacketHeader.ACHIEVEMENT_STAT:
                    ReadAchievementStat(inc);
                    break;
                case ServerPacketHeader.CHEATS_ENABLED:
                    bool cheatsEnabled = inc.ReadBoolean();
                    inc.ReadPadBits();
                    if (cheatsEnabled == DebugConsole.CheatsEnabled)
                    {
                        return;
                    }
                    else
                    {
                        DebugConsole.CheatsEnabled = cheatsEnabled;
                        AchievementManager.CheatsEnabled = cheatsEnabled;
                        if (cheatsEnabled)
                        {
                            var cheatMessageBox = new GUIMessageBox(TextManager.Get("CheatsEnabledTitle"), TextManager.Get("CheatsEnabledDescription"));
                            cheatMessageBox.Buttons[0].OnClicked += (btn, userdata) =>
                            {
                                DebugConsole.TextBox.Select();
                                return true;
                            };
                        }
                    }
                    break;
                case ServerPacketHeader.CREW:
                    campaign?.ClientReadCrew(inc);
                    break;
                case ServerPacketHeader.MEDICAL:
                    campaign?.MedicalClinic?.ClientRead(inc);
                    break;
                case ServerPacketHeader.CIRCUITBOX:
                    ReadCircuitBoxMessage(inc);
                    break;
                case ServerPacketHeader.MONEY:
                    campaign?.ClientReadMoney(inc);
                    break;
                case ServerPacketHeader.READY_CHECK:
                    ReadyCheck.ClientRead(inc);
                    break;
                case ServerPacketHeader.TRAITOR_MESSAGE:
                    TraitorManager.ClientRead(inc);
                    break;
                case ServerPacketHeader.FILE_TRANSFER:
                    FileReceiver.ReadMessage(inc);
                    break;
                case ServerPacketHeader.MISSION:
                    {
                        int missionIndex = inc.ReadByte();
                        Mission mission = GameMain.GameSession?.GetMission(missionIndex);
                        mission?.ClientRead(inc);
                    }
                    break;
                case ServerPacketHeader.EVENTACTION:
                    GameMain.GameSession?.EventManager.ClientRead(inc);
                    break;
                case ServerPacketHeader.SEND_BACKUP_INDICES:
                    GameMain.NetLobbyScreen?.CampaignSetupUI?.OnBackupIndicesReceived(inc);
                    break;
            }
        }

        private void ReadStartGameFinalize(IReadMessage inc)
        {
            TaskPool.ListTasks(DebugConsole.Log);
            ushort contentToPreloadCount = inc.ReadUInt16();
            List<ContentFile> contentToPreload = new List<ContentFile>();
            for (int i = 0; i < contentToPreloadCount; i++)
            {
                string filePath = inc.ReadString();
                ContentFile file = ContentPackageManager.EnabledPackages.All
                    .Select(p =>
                        p.Files.FirstOrDefault(f => f.Path == filePath))
                    .FirstOrDefault(f => f is not null);
                contentToPreload.AddIfNotNull(file);
            }

            string campaignErrorInfo = string.Empty;
            if (GameMain.GameSession?.Campaign is MultiPlayerCampaign campaign)
            {
                campaignErrorInfo = $" Round start save ID: {debugStartGameCampaignSaveID}, last save id: {campaign.LastSaveID}, pending save id: {campaign.PendingSaveID}.";
            }            

            GameMain.GameSession.EventManager.PreloadContent(contentToPreload);

            int subEqualityCheckValue = inc.ReadInt32();
            if (subEqualityCheckValue != (Submarine.MainSub?.Info?.EqualityCheckVal ?? 0))
            {
                string errorMsg =
                    "Submarine equality check failed. The submarine loaded at your end doesn't match the one loaded by the server. " +
                    $"There may have been an error in receiving the up-to-date submarine file from the server. Round init status: {roundInitStatus}." + campaignErrorInfo;
                GameAnalyticsManager.AddErrorEventOnce("GameClient.StartGame:SubsDontMatch" + Level.Loaded.Seed, GameAnalyticsManager.ErrorSeverity.Error, errorMsg);
                throw new Exception(errorMsg);
            }

            byte missionCount = inc.ReadByte();
            List<Identifier> serverMissionIdentifiers = new List<Identifier>();
            for (int i = 0; i < missionCount; i++)
            {
                serverMissionIdentifiers.Add(inc.ReadIdentifier());
            }
            if (missionCount != GameMain.GameSession.GameMode.Missions.Count())
            {
                string errorMsg =
                    $"Mission equality check failed. Mission count doesn't match the server. " +
                    $"Server: {string.Join(", ", serverMissionIdentifiers)}, " +
                    $"client: {string.Join(", ", GameMain.GameSession.GameMode.Missions.Select(m => m.Prefab.Identifier))}, " +
                    $"game session: {string.Join(", ", GameMain.GameSession.Missions.Select(m => m.Prefab.Identifier))}). Round init status: {roundInitStatus}." + campaignErrorInfo;
                GameAnalyticsManager.AddErrorEventOnce("GameClient.StartGame:MissionsCountMismatch" + Level.Loaded.Seed, GameAnalyticsManager.ErrorSeverity.Error, errorMsg);
                throw new Exception(errorMsg);
            }

            if (missionCount > 0)
            {
                if (!GameMain.GameSession.GameMode.Missions.Select(m => m.Prefab.Identifier).OrderBy(id => id).SequenceEqual(serverMissionIdentifiers.OrderBy(id => id)))
                {
                    string errorMsg = 
                        $"Mission equality check failed. The mission selected at your end doesn't match the one loaded by the server " +
                        $"Server: {string.Join(", ", serverMissionIdentifiers)}, " +
                        $"client: {string.Join(", ", GameMain.GameSession.GameMode.Missions.Select(m => m.Prefab.Identifier))}, " +
                        $"game session: {string.Join(", ", GameMain.GameSession.Missions.Select(m => m.Prefab.Identifier))}). Round init status: {roundInitStatus}." + campaignErrorInfo;
                    GameAnalyticsManager.AddErrorEventOnce("GameClient.StartGame:MissionsDontMatch" + Level.Loaded.Seed, GameAnalyticsManager.ErrorSeverity.Error, errorMsg);
                    throw new Exception(errorMsg);
                }
                GameMain.GameSession.EnforceMissionOrder(serverMissionIdentifiers);
            }

            var levelEqualityCheckValues = new Dictionary<Level.LevelGenStage, int>();
            foreach (Level.LevelGenStage stage in Enum.GetValues(typeof(Level.LevelGenStage)).OfType<Level.LevelGenStage>().OrderBy(s => s))
            {
                levelEqualityCheckValues.Add(stage, inc.ReadInt32());
            }

            foreach (var stage in levelEqualityCheckValues.Keys)
            {
                if (Level.Loaded.EqualityCheckValues[stage] != levelEqualityCheckValues[stage])
                {
                    string errorMsg = "Level equality check failed. The level generated at your end doesn't match the level generated by the server, " +
                        $"(client value {stage}:{Level.Loaded.EqualityCheckValues[stage].ToString("X")}, " +
                        $"server value {stage}: {levelEqualityCheckValues[stage].ToString("X")}, "  +
                        $"level value count: {levelEqualityCheckValues.Count}, " +
                        $"seed: {Level.Loaded.Seed}, " +
                        $"missions: {string.Join(", ", GameMain.GameSession.GameMode.Missions.Select(m => m.Prefab.Identifier))}, " +
                        $"sub: {(Submarine.MainSub == null ? "null" : (Submarine.MainSub.Info.Name + " (" + Submarine.MainSub.Info.MD5Hash.ShortRepresentation))}, " +
                        $"mirrored: {Level.Loaded.Mirrored}). Round init status: {roundInitStatus}." + 
                        campaignErrorInfo;
                    GameAnalyticsManager.AddErrorEventOnce("GameClient.StartGame:LevelsDontMatch" + Level.Loaded.Seed, GameAnalyticsManager.ErrorSeverity.Error, errorMsg);
                    throw new Exception(errorMsg);
                }
            }

            foreach (Mission mission in GameMain.GameSession.Missions)
            {
                mission.ClientReadInitial(inc);
            }

            if (inc.ReadBoolean())
            {
                CrewManager.ClientReadActiveOrders(inc);
            }

            if (inc.ReadBoolean())
            {
                ApplyDisembarkPerk();
            }

            roundInitStatus = RoundInitStatus.Started;
        }

        private void ApplyDisembarkPerk()
        {
            var characters = GameSession.GetSessionCrewCharacters(CharacterType.Both);

            ImmutableArray<Character> team1Characters = characters.Where(static c => c.TeamID is CharacterTeamType.Team1).ToImmutableArray(), 
                                      team2Characters = characters.Where(static c => c.TeamID is CharacterTeamType.Team2).ToImmutableArray();

            GameSession.GetPerks().ApplyAll(team1Characters, team2Characters);
        }

        /// <summary>
        /// Fires when the ClientPeer gets disconnected from the server. Does not necessarily mean the client is shutting down, we may still be able to reconnect.
        /// </summary>
        private void OnClientPeerDisconnect(PeerDisconnectPacket disconnectPacket)
        {
            bool wasConnected = connected;
            connected = false;
            connectCancelled = true;
            
            CoroutineManager.StopCoroutines("WaitForStartingInfo");
            CloseReconnectBox();

            GUI.ClearCursorWait();

            if (disconnectPacket.ShouldCreateAnalyticsEvent)
            {
                GameAnalyticsManager.AddErrorEventOnce(
                    "GameClient.HandleDisconnectMessage",
                    GameAnalyticsManager.ErrorSeverity.Debug,
                    $"Client received a disconnect message. Reason: {disconnectPacket.DisconnectReason}");
            }
            
            if (disconnectPacket.DisconnectReason == DisconnectReason.ServerFull)
            {
                AskToWaitInQueue();
            }
            else if (disconnectPacket.ShouldAttemptReconnect && !IsServerOwner && wasConnected)
            {
                if (disconnectPacket.IsEventSyncError)
                {
                    GameMain.NetLobbyScreen.Select();
                    GameMain.GameSession?.EndRound("");
                    GameStarted = false;
                    myCharacter = null;
                }
                AttemptReconnect(disconnectPacket);
            }
            else
            {
                if (ClientPeer is P2PClientPeer or P2POwnerPeer)
                {
                    Eos.EosSessionManager.LeaveSession();
                    SteamManager.LeaveLobby();
                }

                GameMain.ModDownloadScreen.Reset();
                ContentPackageManager.EnabledPackages.Restore();

                GameMain.GameSession?.Campaign?.CancelStartRound();

                UpdatePresence("");
                foreach (var fileTransfer in FileReceiver.ActiveTransfers.ToArray())
                {
                    FileReceiver.StopTransfer(fileTransfer, deleteFile: true);
                }

                ChildServerRelay.AttemptGracefulShutDown();
                GUIMessageBox.MessageBoxes.RemoveAll(c => c?.UserData is RoundSummary);

                characterInfo?.Remove();

                VoipClient?.Dispose();
                VoipClient = null;
                GameMain.Client = null;
                GameMain.GameSession = null;

                ReturnToPreviousMenu(null, null);
                if (disconnectPacket.DisconnectReason != DisconnectReason.Disconnected)
                {
                    new GUIMessageBox(TextManager.Get(wasConnected ? "ConnectionLost" : "CouldNotConnectToServer"), disconnectPacket.PopupMessage)
                    {
                        DisplayInLoadingScreens = true
                    };
                }
            }
        }

        private void CreateReconnectBox(LocalizedString headerText, LocalizedString bodyText)
        {
            reconnectBox = new GUIMessageBox(
                headerText,
                bodyText,
                new LocalizedString[] { TextManager.Get("Cancel") })
            {
                DisplayInLoadingScreens = true
            };
            reconnectBox.Buttons[0].OnClicked += (btn, userdata) => { CancelConnect(); return true; };
            reconnectBox.Buttons[0].OnClicked += reconnectBox.Close;
        }
        
        private void CloseReconnectBox()
        {
            reconnectBox?.Close();
            reconnectBox = null;
        }
        
        private void AskToWaitInQueue()
        {
            CoroutineManager.StopCoroutines("WaitForStartingInfo");
            //already waiting for a slot to free up, stop waiting for starting info and 
            //let WaitInServerQueue reattempt connecting later
            if (CoroutineManager.IsCoroutineRunning("WaitInServerQueue"))
            {
                return;
            }

            var queueBox = new GUIMessageBox(
                TextManager.Get("DisconnectReason.ServerFull"),
                TextManager.Get("ServerFullQuestionPrompt"), new LocalizedString[] { TextManager.Get("Cancel"), TextManager.Get("ServerQueue") });

            queueBox.Buttons[0].OnClicked += queueBox.Close;
            queueBox.Buttons[1].OnClicked += queueBox.Close;
            queueBox.Buttons[1].OnClicked += (btn, userdata) =>
            {
                CloseReconnectBox();
                CoroutineManager.StartCoroutine(WaitInServerQueue(), "WaitInServerQueue");
                return true;
            };
        }
        
        private void AttemptReconnect(PeerDisconnectPacket peerDisconnectPacket)
        {
            connectCancelled = false;
            
            CreateReconnectBox(
                TextManager.Get("ConnectionLost"),
                peerDisconnectPacket.ReconnectMessage);

            var prevContentPackages = ClientPeer.ServerContentPackages;
            //decrement lobby update ID to make sure we update the lobby when we reconnect
            GameMain.NetLobbyScreen.LastUpdateID--;
            InitiateServerJoin();
            if (ClientPeer != null)
            {
                //restore the previous list of content packages so we can reconnect immediately without having to recheck that the packages match
                ClientPeer.ContentPackageOrderReceived = true;
                ClientPeer.ServerContentPackages = prevContentPackages;
            }
        }

        private void UpdatePresence(string connectCommand)
        {
            #warning TODO: use store localization functionality
            var desc = TextManager.GetWithVariable("FriendPlayingOnServer", "[servername]", ServerName);
            
            async Task updateEosPresence()
            {
                var epicIds = EosInterface.IdQueries.GetLoggedInEpicIds();
                if (!epicIds.FirstOrNone().TryUnwrap(out var epicAccountId)) { return; }

                var setPresenceResult = await EosInterface.Presence.SetJoinCommand(
                    epicAccountId: epicAccountId,
                    desc: desc.Value,
                    serverName: ServerName,
                    joinCommand: connectCommand);
                DebugConsole.NewMessage($"Set connect command: {connectCommand}, result: {setPresenceResult}");
            }

            TaskPool.Add(
                "UpdateEosPresence",
                updateEosPresence(),
                static _ => { });

            if (SteamManager.IsInitialized)
            {
                Steamworks.SteamFriends.ClearRichPresence();
                if (!connectCommand.IsNullOrWhiteSpace())
                {
                    Steamworks.SteamFriends.SetRichPresence("servername", ServerName);
                    Steamworks.SteamFriends.SetRichPresence("status",
                        desc.Value);
                    Steamworks.SteamFriends.SetRichPresence("connect",
                        connectCommand);
                }
            }
        }
        
        private void OnConnectionInitializationComplete()
        {
            UpdatePresence($"-connect \"{ToolBox.EscapeCharacters(ServerName)}\" {string.Join(",", serverEndpoints.Select(e => e.StringRepresentation))}");

            canStart = true;
            connected = true;

            VoipClient = new VoipClient(this, ClientPeer);

            //if we're still in the game, roundsummary or lobby screen, we don't need to redownload the mods
            if (Screen.Selected is GameScreen or RoundSummaryScreen or NetLobbyScreen)
            {
                EntityEventManager.ClearSelf();
                foreach (Character c in Character.CharacterList)
                {
                    c.ResetNetState();
                }
            }
            else
            {
                GameMain.ModDownloadScreen.Select();
            }

            chatBox.InputBox.Enabled = true;
            if (GameMain.NetLobbyScreen?.ChatInput != null)
            {
                GameMain.NetLobbyScreen.ChatInput.Enabled = true;
            }
        }
        
        private IEnumerable<CoroutineStatus> WaitInServerQueue()
        {
            waitInServerQueueBox = new GUIMessageBox(
                    TextManager.Get("ServerQueuePleaseWait"),
                    TextManager.Get("WaitingInServerQueue"), new LocalizedString[] { TextManager.Get("Cancel") });
            waitInServerQueueBox.Buttons[0].OnClicked += (btn, userdata) =>
            {
                CoroutineManager.StopCoroutines("WaitInServerQueue");
                waitInServerQueueBox?.Close();
                waitInServerQueueBox = null;
                return true;
            };

            while (!connected)
            {
                if (!CoroutineManager.IsCoroutineRunning("WaitForStartingInfo"))
                {
                    InitiateServerJoin();
                    yield return new WaitForSeconds(5.0f);
                }
                yield return new WaitForSeconds(0.5f);
            }

            waitInServerQueueBox?.Close();
            waitInServerQueueBox = null;

            yield return CoroutineStatus.Success;
        }


        private static void ReadAchievement(IReadMessage inc)
        {
            Identifier achievementIdentifier = inc.ReadIdentifier();
            AchievementManager.UnlockAchievement(achievementIdentifier);
        }

        private static void ReadAchievementStat(IReadMessage inc)
        {
            var netStat = INetSerializableStruct.Read<NetIncrementedStat>(inc);
            AchievementManager.IncrementStat(netStat.Stat, netStat.Amount);
        }

        private static void ReadCircuitBoxMessage(IReadMessage inc)
        {
            var header = INetSerializableStruct.Read<NetCircuitBoxHeader>(inc);

            INetSerializableStruct data = header.Opcode switch
            {
                CircuitBoxOpcode.Cursor => INetSerializableStruct.Read<NetCircuitBoxCursorInfo>(inc),
                CircuitBoxOpcode.Error => INetSerializableStruct.Read<CircuitBoxErrorEvent>(inc),
                _ => throw new ArgumentOutOfRangeException(nameof(header.Opcode), header.Opcode, "This data cannot be handled using direct network messages.")
            };

            if (header.FindTarget().TryUnwrap(out CircuitBox box))
            {
                box.ClientRead(data);
            }
        }

        private void ReadPermissions(IReadMessage inc)
        {
            List<string> permittedConsoleCommands = new List<string>();
            byte clientId = inc.ReadByte();

            ClientPermissions permissions = ClientPermissions.None;
            List<DebugConsole.Command> permittedCommands = new List<DebugConsole.Command>();
            Client.ReadPermissions(inc, out permissions, out permittedCommands);

            Client targetClient = ConnectedClients.Find(c => c.SessionId == clientId);
            targetClient?.SetPermissions(permissions, permittedCommands);
            if (clientId == SessionId)
            {
                SetMyPermissions(permissions, permittedCommands.Select(command => command.Names[0]));
            }
        }

        private void SetMyPermissions(ClientPermissions newPermissions, IEnumerable<Identifier> permittedConsoleCommands)
        {
            if (!(this.permittedConsoleCommands.Any(c => !permittedConsoleCommands.Contains(c)) ||
                  permittedConsoleCommands.Any(c => !this.permittedConsoleCommands.Contains(c))))
            {
                if (newPermissions == permissions) { return; }
            }

            bool refreshCampaignUI = permissions.HasFlag(ClientPermissions.ManageCampaign) != newPermissions.HasFlag(ClientPermissions.ManageCampaign) ||
                                     permissions.HasFlag(ClientPermissions.ManageRound) != newPermissions.HasFlag(ClientPermissions.ManageRound);

            permissions = newPermissions;
            this.permittedConsoleCommands = permittedConsoleCommands.ToList();
            //don't show the "permissions changed" popup if the client owns the server
            if (!IsServerOwner)
            {
                GUIMessageBox.MessageBoxes.RemoveAll(mb => mb.UserData as string == "permissions");
                GUIMessageBox msgBox = new GUIMessageBox("", "") { UserData = "permissions" };
                msgBox.Content.ClearChildren();
                msgBox.Content.RectTransform.RelativeSize = new Vector2(0.95f, 0.9f);

                var header = new GUITextBlock(new RectTransform(new Vector2(1.0f, 0.0f), msgBox.Content.RectTransform), TextManager.Get("PermissionsChanged"), textAlignment: Alignment.Center, font: GUIStyle.LargeFont);
                header.RectTransform.IsFixedSize = true;

                var permissionArea = new GUILayoutGroup(new RectTransform(new Vector2(1.0f, 1.0f), msgBox.Content.RectTransform), isHorizontal: true) { Stretch = true, RelativeSpacing = 0.05f };
                var leftColumn = new GUILayoutGroup(new RectTransform(new Vector2(0.5f, 1.0f), permissionArea.RectTransform)) { Stretch = true, RelativeSpacing = 0.05f };
                var rightColumn = new GUILayoutGroup(new RectTransform(new Vector2(0.5f, 1.0f), permissionArea.RectTransform)) { Stretch = true, RelativeSpacing = 0.05f };

                var permissionsLabel = new GUITextBlock(new RectTransform(new Vector2(newPermissions == ClientPermissions.None ? 2.0f : 1.0f, 0.0f), leftColumn.RectTransform),
                    TextManager.Get(newPermissions == ClientPermissions.None ? "PermissionsRemoved" : "CurrentPermissions"),
                    wrap: true, font: (newPermissions == ClientPermissions.None ? GUIStyle.Font : GUIStyle.SubHeadingFont));
                permissionsLabel.RectTransform.NonScaledSize = new Point(permissionsLabel.Rect.Width, permissionsLabel.Rect.Height);
                permissionsLabel.RectTransform.IsFixedSize = true;
                if (newPermissions != ClientPermissions.None)
                {
                    LocalizedString permissionList = "";
                    foreach (ClientPermissions permission in Enum.GetValues(typeof(ClientPermissions)))
                    {
                        if (!newPermissions.HasFlag(permission) || permission == ClientPermissions.None) { continue; }
                        permissionList += "   - " + TextManager.Get("ClientPermission." + permission) + "\n";
                    }
                    new GUITextBlock(new RectTransform(new Vector2(1.0f, 0.0f), leftColumn.RectTransform),
                        permissionList);
                }

                if (newPermissions.HasFlag(ClientPermissions.ConsoleCommands))
                {
                    var commandsLabel = new GUITextBlock(new RectTransform(new Vector2(1.0f, 0.0f), rightColumn.RectTransform),
                        TextManager.Get("PermittedConsoleCommands"), wrap: true, font: GUIStyle.SubHeadingFont);
                    var commandList = new GUIListBox(new RectTransform(new Vector2(1.0f, 1.0f), rightColumn.RectTransform));
                    foreach (Identifier permittedCommand in permittedConsoleCommands)
                    {
                        new GUITextBlock(new RectTransform(new Vector2(1.0f, 0.05f), commandList.Content.RectTransform, minSize: new Point(0, 15)),
                            permittedCommand.Value, font: GUIStyle.SmallFont)
                        {
                            CanBeFocused = false
                        };
                    }
                    permissionsLabel.RectTransform.NonScaledSize = commandsLabel.RectTransform.NonScaledSize =
                        new Point(permissionsLabel.Rect.Width, Math.Max(permissionsLabel.Rect.Height, commandsLabel.Rect.Height));
                    commandsLabel.RectTransform.IsFixedSize = true;
                }

                new GUIButton(new RectTransform(new Vector2(0.5f, 0.05f), msgBox.Content.RectTransform), TextManager.Get("ok"))
                {
                    OnClicked = msgBox.Close
                };

                permissionArea.RectTransform.MinSize = new Point(0, Math.Max(leftColumn.RectTransform.Children.Sum(c => c.Rect.Height), rightColumn.RectTransform.Children.Sum(c => c.Rect.Height)));
                permissionArea.RectTransform.IsFixedSize = true;
                int contentHeight = (int)(msgBox.Content.RectTransform.Children.Sum(c => c.Rect.Height + msgBox.Content.AbsoluteSpacing) * 1.05f);
                msgBox.Content.ChildAnchor = Anchor.TopCenter;
                msgBox.Content.Stretch = true;
                msgBox.Content.RectTransform.MinSize = new Point(0, contentHeight);
                msgBox.InnerFrame.RectTransform.MinSize = new Point(0, (int)(contentHeight / permissionArea.RectTransform.RelativeSize.Y / msgBox.Content.RectTransform.RelativeSize.Y));
            }

            if (refreshCampaignUI)
            {
                if (GameMain.GameSession?.GameMode is CampaignMode campaign)
                {
                    campaign.CampaignUI?.UpgradeStore?.RequestRefresh();
                    campaign.CampaignUI?.HRManagerUI?.RefreshUI();
                }
            }

            GameMain.NetLobbyScreen.RefreshEnabledElements();
            //close settings menu in case it was open
            ServerSettings.Close();
            OnPermissionChanged.Invoke(new PermissionChangedEvent(permissions, this.permittedConsoleCommands));
        }

        private IEnumerable<CoroutineStatus> StartGame(IReadMessage inc)
        {
            Character?.Remove();
            Character = null;
            HasSpawned = false;
            eventErrorWritten = false;
            GameMain.NetLobbyScreen.StopWaitingForStartRound();

            debugStartGameCampaignSaveID = null;

            while (CoroutineManager.IsCoroutineRunning("EndGame"))
            {
                EndCinematic?.Stop();
                yield return CoroutineStatus.Running;
            }

            //enable spectate button in case we fail to start the round now
            //(for example, due to a missing sub file or an error)
            GameMain.NetLobbyScreen.ShowSpectateButton();

            EntityEventManager.Clear();
            LastSentEntityEventID = 0;

            EndVoteTickBox.Selected = false;

            WaitForNextRoundRespawn = null;

            roundInitStatus = RoundInitStatus.Starting;

            int seed = inc.ReadInt32();
            string modeIdentifier = inc.ReadString();

            GameModePreset gameMode = GameModePreset.List.Find(gm => gm.Identifier == modeIdentifier);
            if (gameMode == null)
            {
                DebugConsole.ThrowError("Game mode \"" + modeIdentifier + "\" not found!");
                roundInitStatus = RoundInitStatus.Interrupted;
                yield return CoroutineStatus.Failure;
            }

            bool respawnAllowed = inc.ReadBoolean();
            ServerSettings.AllowDisguises = inc.ReadBoolean();
            ServerSettings.AllowRewiring = inc.ReadBoolean();
            ServerSettings.AllowImmediateItemDelivery = inc.ReadBoolean();
            ServerSettings.AllowFriendlyFire = inc.ReadBoolean();
            ServerSettings.AllowDragAndDropGive = inc.ReadBoolean();
            ServerSettings.LockAllDefaultWires = inc.ReadBoolean();
            ServerSettings.AllowLinkingWifiToChat = inc.ReadBoolean();
            ServerSettings.MaximumMoneyTransferRequest = inc.ReadInt32();
            ServerSettings.RespawnMode = (RespawnMode)inc.ReadByte();
            bool usingShuttle = GameMain.NetLobbyScreen.UsingShuttle = inc.ReadBoolean();
            GameMain.LightManager.LosMode = (LosMode)inc.ReadByte();
            ServerSettings.ShowEnemyHealthBars = (EnemyHealthBarMode)inc.ReadByte();
            bool includesFinalize = inc.ReadBoolean(); inc.ReadPadBits();
            GameMain.LightManager.LightingEnabled = true;

            ServerSettings.ReadMonsterEnabled(inc);

            Rand.SetSyncedSeed(seed);

            Task loadTask = null;
            var roundSummary = (GUIMessageBox.MessageBoxes.Find(c => c?.UserData is RoundSummary)?.UserData) as RoundSummary;

            bool isOutpost = false;

            if (gameMode != GameModePreset.MultiPlayerCampaign)
            {
                string levelSeed = inc.ReadString();
                float levelDifficulty = inc.ReadSingle();
                Identifier biomeId = inc.ReadIdentifier();
                string subName = inc.ReadString();
                string subHash = inc.ReadString();
                string shuttleName = inc.ReadString();
                string shuttleHash = inc.ReadString();

                bool hasEnemySub = inc.ReadBoolean();
                string enemySubName = subName;
                string enemySubHash = subHash;
                if (hasEnemySub)
                {
                    enemySubName = inc.ReadString();
                    enemySubHash = inc.ReadString();
                }

                List<UInt32> missionHashes = new List<UInt32>();
                int missionCount = inc.ReadByte();
                for (int i = 0; i < missionCount; i++)
                {
                    missionHashes.Add(inc.ReadUInt32());
                }
                if (!GameMain.NetLobbyScreen.TrySelectSub(subName, subHash, SelectedSubType.Sub, GameMain.NetLobbyScreen.SubList))
                {
                    roundInitStatus = RoundInitStatus.Interrupted;
                    yield return CoroutineStatus.Success;
                }

                if (hasEnemySub)
                {
                    if (!GameMain.NetLobbyScreen.TrySelectSub(enemySubName, enemySubHash, SelectedSubType.EnemySub, GameMain.NetLobbyScreen.SubList))
                    {
                        roundInitStatus = RoundInitStatus.Interrupted;
                        yield return CoroutineStatus.Success;
                    }
                }

                if (!GameMain.NetLobbyScreen.TrySelectSub(shuttleName, shuttleHash, SelectedSubType.Shuttle, GameMain.NetLobbyScreen.ShuttleList.ListBox))
                {
                    roundInitStatus = RoundInitStatus.Interrupted;
                    yield return CoroutineStatus.Success;
                }

                //this shouldn't happen, TrySelectSub should stop the coroutine if the correct sub/shuttle cannot be found
                if (GameMain.NetLobbyScreen.SelectedSub == null ||
                    GameMain.NetLobbyScreen.SelectedSub.Name != subName ||
                    GameMain.NetLobbyScreen.SelectedSub.MD5Hash?.StringRepresentation != subHash)
                {
                    string errorMsg = "Failed to select submarine \"" + subName + "\" (hash: " + subHash + ").";
                    if (GameMain.NetLobbyScreen.SelectedSub == null)
                    {
                        errorMsg += "\n" + "SelectedSub is null";
                    }
                    else
                    {
                        if (GameMain.NetLobbyScreen.SelectedSub.Name != subName)
                        {
                            errorMsg += "\n" + "Name mismatch: " + GameMain.NetLobbyScreen.SelectedSub.Name + " != " + subName;
                        }
                        if (GameMain.NetLobbyScreen.SelectedSub.MD5Hash?.StringRepresentation != subHash)
                        {
                            errorMsg += "\n" + "Hash mismatch: " + GameMain.NetLobbyScreen.SelectedSub.MD5Hash?.StringRepresentation + " != " + subHash;
                        }
                    }
                    GameStarted = true;
                    GameMain.NetLobbyScreen.Select();
                    DebugConsole.ThrowError(errorMsg);
                    GameAnalyticsManager.AddErrorEventOnce("GameClient.StartGame:FailedToSelectSub" + subName, GameAnalyticsManager.ErrorSeverity.Error, errorMsg);
                    roundInitStatus = RoundInitStatus.Interrupted;
                    yield return CoroutineStatus.Failure;
                }
                if (GameMain.NetLobbyScreen.SelectedShuttle == null ||
                    GameMain.NetLobbyScreen.SelectedShuttle.Name != shuttleName ||
                    GameMain.NetLobbyScreen.SelectedShuttle.MD5Hash?.StringRepresentation != shuttleHash)
                {
                    GameStarted = true;
                    GameMain.NetLobbyScreen.Select();
                    string errorMsg = "Failed to select shuttle \"" + shuttleName + "\" (hash: " + shuttleHash + ").";
                    DebugConsole.ThrowError(errorMsg);
                    GameAnalyticsManager.AddErrorEventOnce("GameClient.StartGame:FailedToSelectShuttle" + shuttleName, GameAnalyticsManager.ErrorSeverity.Error, errorMsg);
                    roundInitStatus = RoundInitStatus.Interrupted;
                    yield return CoroutineStatus.Failure;
                }

                var selectedMissions = missionHashes.Select(i => MissionPrefab.Prefabs.Find(p => p.UintIdentifier == i));

                var selectedEnemySub = hasEnemySub && GameMain.NetLobbyScreen.SelectedEnemySub is { } enemySub ? Option.Some(enemySub) : Option.None;

                GameMain.GameSession = new GameSession(GameMain.NetLobbyScreen.SelectedSub, selectedEnemySub, gameMode, missionPrefabs: selectedMissions);
                GameMain.GameSession.StartRound(levelSeed, levelDifficulty, levelGenerationParams: null, forceBiome: biomeId);
            }
            else
            {
                if (!(GameMain.GameSession?.GameMode is MultiPlayerCampaign campaign))
                {
                    throw new InvalidOperationException("Attempted to start a campaign round when a campaign was not active.");
                }

                if (GameMain.GameSession?.CrewManager != null) { GameMain.GameSession.CrewManager.Reset(); }

                byte campaignID = inc.ReadByte();
                UInt16 campaignSaveID = inc.ReadUInt16();
                int nextLocationIndex = inc.ReadInt32();
                int nextConnectionIndex = inc.ReadInt32();
                int selectedLocationIndex = inc.ReadInt32();
                bool mirrorLevel = inc.ReadBoolean();

                if (campaign.CampaignID != campaignID)
                {
                    GameStarted = true;
                    DebugConsole.ThrowError("Failed to start campaign round (campaign ID does not match).");
                    GameMain.NetLobbyScreen.Select();
                    roundInitStatus = RoundInitStatus.Interrupted;
                    yield return CoroutineStatus.Failure;
                }

                if (NetIdUtils.IdMoreRecent(campaign.PendingSaveID, campaign.LastSaveID) ||
                    NetIdUtils.IdMoreRecent(campaignSaveID, campaign.PendingSaveID))
                {
                    campaign.PendingSaveID = campaignSaveID;
                    DateTime saveFileTimeOut = DateTime.Now + CampaignSaveTransferTimeOut;
                    while (NetIdUtils.IdMoreRecent(campaignSaveID, campaign.LastSaveID))
                    {
                        if (DateTime.Now > saveFileTimeOut)
                        {
                            GameStarted = true;
                            new GUIMessageBox(TextManager.Get("error"), TextManager.Get("campaignsavetransfer.timeout"));
                            GameMain.NetLobbyScreen.Select();
                            roundInitStatus = RoundInitStatus.Interrupted;
                            //use success status, even though this is a failure (no need to show a console error because we show it in the message box)
                            yield return CoroutineStatus.Success;
                        }
                        yield return new WaitForSeconds(0.1f);
                    }
                }

                if (campaign.Map == null)
                {
                    GameStarted = true;
                    DebugConsole.ThrowError("Failed to start campaign round (campaign map not loaded yet).");
                    GameMain.NetLobbyScreen.Select();
                    roundInitStatus = RoundInitStatus.Interrupted;
                    yield return CoroutineStatus.Failure;
                }

                campaign.Map.SelectLocation(selectedLocationIndex);

                LevelData levelData = nextLocationIndex > -1 ?
                    campaign.Map.Locations[nextLocationIndex].LevelData :
                    campaign.Map.Connections[nextConnectionIndex].LevelData;

                debugStartGameCampaignSaveID = campaign.LastSaveID;

                if (roundSummary != null)
                {
                    loadTask = campaign.SelectSummaryScreen(roundSummary, levelData, mirrorLevel, null);
                    roundSummary.ContinueButton.Visible = false;
                }
                else
                {
                    GameMain.GameSession.StartRound(levelData, mirrorLevel, startOutpost: campaign?.GetPredefinedStartOutpost());
                }
                isOutpost = levelData.Type == LevelData.LevelType.Outpost;
            }

            Voting?.ResetVotes(GameMain.Client.ConnectedClients);            

            if (loadTask != null)
            {
                while (!loadTask.IsCompleted && !loadTask.IsFaulted && !loadTask.IsCanceled)
                {
                    yield return CoroutineStatus.Running;
                }
            }

            if (ClientPeer == null)
            {
                DebugConsole.ThrowError("There was an error initializing the round (disconnected during the StartGame coroutine.)");
                roundInitStatus = RoundInitStatus.Error;
                yield return CoroutineStatus.Failure;
            }

            roundInitStatus = RoundInitStatus.WaitingForStartGameFinalize;

            //wait for up to 30 seconds for the server to send the STARTGAMEFINALIZE message
            TimeSpan timeOutDuration = new TimeSpan(0, 0, seconds: 30);
            DateTime timeOut = DateTime.Now + timeOutDuration;
            DateTime requestFinalizeTime = DateTime.Now;
            TimeSpan requestFinalizeInterval = new TimeSpan(0, 0, 2);
            IWriteMessage msg = new WriteOnlyMessage();
            msg.WriteByte((byte)ClientPacketHeader.REQUEST_STARTGAMEFINALIZE);
            ClientPeer.Send(msg, DeliveryMethod.Unreliable);

            GUIMessageBox interruptPrompt = null;

            if (includesFinalize)
            {
                ReadStartGameFinalize(inc);
            }
            else
            {
                while (true)
                {
                    try
                    {
                        if (DateTime.Now > requestFinalizeTime)
                        {
                            msg = new WriteOnlyMessage();
                            msg.WriteByte((byte)ClientPacketHeader.REQUEST_STARTGAMEFINALIZE);
                            ClientPeer.Send(msg, DeliveryMethod.Unreliable);
                            requestFinalizeTime = DateTime.Now + requestFinalizeInterval;
                        }
                        if (DateTime.Now > timeOut && interruptPrompt == null)
                        {
                            interruptPrompt = new GUIMessageBox(string.Empty, TextManager.Get("WaitingForStartGameFinalizeTakingTooLong"),
                                new LocalizedString[] { TextManager.Get("Yes"), TextManager.Get("No") })
                            {
                                DisplayInLoadingScreens = true
                            };
                            interruptPrompt.Buttons[0].OnClicked += (btn, userData) =>
                            {
                                roundInitStatus = RoundInitStatus.Interrupted;
                                DebugConsole.ThrowError("Error while starting the round (did not receive STARTGAMEFINALIZE message from the server). Returning to the lobby...");
                                GameStarted = true;
                                GameMain.NetLobbyScreen.Select();
                                interruptPrompt.Close();
                                interruptPrompt = null;
                                return true;
                            };
                            interruptPrompt.Buttons[1].OnClicked += (btn, userData) =>
                            {
                                timeOut = DateTime.Now + timeOutDuration;
                                interruptPrompt.Close();
                                interruptPrompt = null;
                                return true;
                            };
                        }

                        if (!connected)
                        {
                            roundInitStatus = RoundInitStatus.Interrupted;
                            break;
                        }

                        if (roundInitStatus != RoundInitStatus.WaitingForStartGameFinalize) { break; }
                    }
                    catch (Exception e)
                    {
                        DebugConsole.ThrowError("There was an error initializing the round.", e, createMessageBox: true);
                        roundInitStatus = RoundInitStatus.Error;
                        break;
                    }

                    //waiting for a STARTGAMEFINALIZE message
                    yield return CoroutineStatus.Running;
                }
            }

            interruptPrompt?.Close();
            interruptPrompt = null;
            
            if (roundInitStatus != RoundInitStatus.Started)
            {
                if (roundInitStatus != RoundInitStatus.Interrupted)
                {
                    DebugConsole.ThrowError(roundInitStatus.ToString());
                    CoroutineManager.StartCoroutine(EndGame(""));
                    yield return CoroutineStatus.Failure;
                }
                else
                {
                    yield return CoroutineStatus.Success;
                }
            }

            if (GameMain.GameSession.Submarine != null && 
                GameMain.GameSession.Submarine.Info.IsFileCorrupted)
            {
                DebugConsole.ThrowError($"Failed to start a round. Could not load the submarine \"{GameMain.GameSession.Submarine.Info.Name}\".");
                yield return CoroutineStatus.Failure;
            }

            for (int i = 0; i < Submarine.MainSubs.Length; i++)
            {
                if (Submarine.MainSubs[i] == null) { break; }

                var teamID = i == 0 ? CharacterTeamType.Team1 : CharacterTeamType.Team2;
                Submarine.MainSubs[i].TeamID = teamID;
                foreach (Item item in Item.ItemList)
                {
                    if (item.Submarine == null) { continue; }
                    if (item.Submarine != Submarine.MainSubs[i] && !Submarine.MainSubs[i].DockedTo.Contains(item.Submarine)) { continue; }
                    foreach (WifiComponent wifiComponent in item.GetComponents<WifiComponent>())
                    {
                        wifiComponent.TeamID = Submarine.MainSubs[i].TeamID;
                    }
                }
                foreach (Submarine sub in Submarine.MainSubs[i].DockedTo)
                {
                    if (sub.Info.Type == SubmarineType.Outpost) { continue; }
                    sub.TeamID = teamID;
                }
            }

            if (respawnAllowed)
            {
                RespawnManager = new RespawnManager(this, usingShuttle && !isOutpost ? GameMain.NetLobbyScreen.SelectedShuttle : null);
            }

            GameStarted = true;
            ServerSettings.ServerDetailsChanged = true;

            if (roundSummary != null)
            {
                roundSummary.ContinueButton.Visible = true;
            }

            GameMain.GameScreen.Select();

            string message = "ServerMessage.HowToCommunicate" +
                $"~[chatbutton]={GameSettings.CurrentConfig.KeyMap.KeyBindText(InputType.ActiveChat)}" +
                $"~[pttbutton]={GameSettings.CurrentConfig.KeyMap.KeyBindText(InputType.Voice)}" +
                $"~[switchbutton]={GameSettings.CurrentConfig.KeyMap.KeyBindText(InputType.ToggleChatMode)}";
            AddChatMessage(message, ChatMessageType.Server);

            yield return CoroutineStatus.Success;
        }

        public IEnumerable<CoroutineStatus> EndGame(string endMessage, CampaignMode.TransitionType transitionType = CampaignMode.TransitionType.None, TraitorManager.TraitorResults? traitorResults = null)
        {
            //round starting up, wait for it to finish
            DateTime timeOut = DateTime.Now + new TimeSpan(0, 0, 60);
            while (TaskPool.IsTaskRunning("AsyncCampaignStartRound"))
            {
                if (DateTime.Now > timeOut)
                {
                    throw new Exception("Failed to end a round (async campaign round start timed out).");
                }
                yield return new WaitForSeconds(1.0f);
            }

            if (!GameStarted)
            {
                GameMain.NetLobbyScreen.Select();
                yield return CoroutineStatus.Success;
            }

            GameMain.GameSession?.EndRound(endMessage, transitionType, traitorResults);
            
            ServerSettings.ServerDetailsChanged = true;

            GameStarted = false;
            Character.Controlled = null;
            WaitForNextRoundRespawn = null;
            GameMain.GameScreen.Cam.TargetPos = Vector2.Zero;
            GameMain.LightManager.LosEnabled = false;
            RespawnManager = null;

            if (Screen.Selected == GameMain.GameScreen)
            {
                Submarine refSub = Submarine.MainSub;
                if (Submarine.MainSubs[1] != null &&
                    GameMain.GameSession.GameMode is PvPMode && 
                    GameMain.GameSession.WinningTeam.HasValue && GameMain.GameSession.WinningTeam == CharacterTeamType.Team1)
                {
                    refSub = Submarine.MainSubs[1];
                }

                if (refSub != null)
                {
                    // Enable characters near the main sub for the endCinematic
                    foreach (Character c in Character.CharacterList)
                    {
                        if (Vector2.DistanceSquared(refSub.WorldPosition, c.WorldPosition) < MathUtils.Pow2(c.Params.DisableDistance))
                        {
                            c.Enabled = true;
                        }
                    }
                }

                EndCinematic = new CameraTransition(refSub, GameMain.GameScreen.Cam, Alignment.CenterLeft, Alignment.CenterRight);
                while (EndCinematic.Running && Screen.Selected == GameMain.GameScreen)
                {
                    yield return CoroutineStatus.Running;
                }
                EndCinematic = null;
            }
            
            Submarine.Unload();
            if (transitionType == CampaignMode.TransitionType.None)
            {
                GameMain.NetLobbyScreen.Select();
            }
            myCharacter = null;
            foreach (Client c in otherClients)
            {
                c.InGame = false;
                c.Character = null;
            }

            yield return CoroutineStatus.Success;
        }

        private void ReadInitialUpdate(IReadMessage inc)
        {
            SessionId = inc.ReadByte();

            UInt16 subListCount = inc.ReadUInt16();
            ServerSubmarines.Clear();
            for (int i = 0; i < subListCount; i++)
            {
                string subName = inc.ReadString();
                string subHash = inc.ReadString();
                Identifier subClass = inc.ReadIdentifier();
                bool isShuttle = inc.ReadBoolean();
                bool requiredContentPackagesInstalled = inc.ReadBoolean();

                var matchingSub = SubmarineInfo.SavedSubmarines.FirstOrDefault(s => s.Name == subName && s.MD5Hash.StringRepresentation == subHash);
                if (matchingSub == null)
                {
                    matchingSub = new SubmarineInfo(Path.Combine(SaveUtil.SubmarineDownloadFolder, subName) + ".sub", subHash, tryLoad: false)
                    {
                        Class = SubmarineClass.Find(subClass)
                    };
                    if (isShuttle) { matchingSub.AddTag(SubmarineTag.Shuttle); }
                }
                matchingSub.RequiredContentPackagesInstalled = requiredContentPackagesInstalled;
                ServerSubmarines.Add(matchingSub);
            }

            GameMain.NetLobbyScreen.UpdateSubList(GameMain.NetLobbyScreen.SubList, ServerSubmarines);
            GameMain.NetLobbyScreen.UpdateSubList(GameMain.NetLobbyScreen.ShuttleList.ListBox, ServerSubmarines.Where(s => s.HasTag(SubmarineTag.Shuttle)));

            GameStarted = inc.ReadBoolean();
            bool allowSpectating = inc.ReadBoolean();
            bool permadeathMode = inc.ReadBoolean();
            bool ironmanMode = inc.ReadBoolean();

            ReadPermissions(inc);
            
            if (GameStarted)
            {
                if (Screen.Selected != GameMain.GameScreen)
                {
                    LocalizedString message;
                    if (permadeathMode)
                    {
                        message = TextManager.Get(ironmanMode ? "RoundRunningIronman" : "RoundRunningPermadeath");
                    }
                    else
                    {
                        message = TextManager.Get(allowSpectating ? "RoundRunningSpectateEnabled" : "RoundRunningSpectateDisabled");
                    }
                    new GUIMessageBox(TextManager.Get("PleaseWait"), message);
                    if (!(Screen.Selected is ModDownloadScreen)) { GameMain.NetLobbyScreen.Select(); }
                }
            }
        }

        private void ReadClientList(IReadMessage inc)
        {
            bool refreshCampaignUI = false;
            UInt16 listId = inc.ReadUInt16();
            GameMain.NetLobbyScreen.Team1Count = inc.ReadByte();
            GameMain.NetLobbyScreen.Team2Count = inc.ReadByte();
            List<TempClient> tempClients = new List<TempClient>();
            int clientCount = inc.ReadByte();
            for (int i = 0; i < clientCount; i++)
            {
                tempClients.Add(INetSerializableStruct.Read<TempClient>(inc));
                inc.ReadPadBits();
            }

            if (NetIdUtils.IdMoreRecent(listId, LastClientListUpdateID))
            {
                bool updateClientListId = true;
                List<Client> currentClients = new List<Client>();
                foreach (TempClient tc in tempClients)
                {
                    //see if the client already exists
                    var existingClient = ConnectedClients.Find(c => c.SessionId == tc.SessionId && c.Name == tc.Name);
                    if (existingClient == null) //if not, create it
                    {
                        existingClient = new Client(tc.Name, tc.SessionId)
                        {
                            AccountInfo = tc.AccountInfo,
                            Muted = tc.Muted,
                            InGame = tc.InGame,
                            IsOwner = tc.IsOwner
                        };
                        otherClients.Add(existingClient);
                        refreshCampaignUI = true;
                        GameMain.NetLobbyScreen.AddPlayer(existingClient);
                    }
                    existingClient.NameId = tc.NameId;
                    existingClient.PreferredJob = tc.PreferredJob;
                    existingClient.PreferredTeam = tc.PreferredTeam;
                    existingClient.TeamID = tc.TeamID;
                    existingClient.Character = null;
                    existingClient.Karma = tc.Karma;
                    existingClient.Muted = tc.Muted;
                    existingClient.InGame = tc.InGame;
                    existingClient.IsOwner = tc.IsOwner;
                    existingClient.IsDownloading = tc.IsDownloading;
                    GameMain.NetLobbyScreen.SetPlayerNameAndJobPreference(existingClient); // refresh lobby player list in the local UI
                    if (Screen.Selected != GameMain.NetLobbyScreen && tc.CharacterId > 0)
                    {
                        existingClient.CharacterID = tc.CharacterId;
                    }
                    if (existingClient.SessionId == SessionId)
                    {
                        MultiplayerPreferences.Instance.TeamPreference = existingClient.PreferredTeam;
                        // If a team is already selected, make sure the UI reflects it
                        if (MultiplayerPreferences.Instance.TeamPreference != CharacterTeamType.None)
                        {
                            GameMain.NetLobbyScreen.TeamPreferenceListBox?.Select(MultiplayerPreferences.Instance.TeamPreference);
                        }
                        else
                        {
                            GameMain.NetLobbyScreen.RefreshPvpTeamSelectionButtons();
                        }
                        existingClient.SetPermissions(permissions, permittedConsoleCommands);
                        if (!NetIdUtils.IdMoreRecent(nameId, tc.NameId))
                        {
                            Name = tc.Name;
                            nameId = tc.NameId;
                        }
                        if (GameMain.NetLobbyScreen.CharacterNameBox != null &&
                            !GameMain.NetLobbyScreen.CharacterNameBox.Selected)
                        {
                            GameMain.NetLobbyScreen.CharacterNameBox.Text = Name;
                        }
                    }
                    currentClients.Add(existingClient);
                }
                //remove clients that aren't present anymore
                for (int i = ConnectedClients.Count - 1; i >= 0; i--)
                {
                    if (!currentClients.Contains(ConnectedClients[i]))
                    {
                        GameMain.NetLobbyScreen.RemovePlayer(ConnectedClients[i]);
                        otherClients[i].Dispose();
                        otherClients.RemoveAt(i);
                        refreshCampaignUI = true;
                    }
                }
                foreach (Client client in ConnectedClients)
                {
                    int index = previouslyConnectedClients.FindIndex(c => c.SessionId == client.SessionId);
                    if (index < 0)
                    {
                        if (previouslyConnectedClients.Count > 100)
                        {
                            previouslyConnectedClients.RemoveRange(0, previouslyConnectedClients.Count - 100);
                        }
                    }
                    else
                    {
                        previouslyConnectedClients.RemoveAt(index);
                    }
                    previouslyConnectedClients.Add(client);
                }
                if (updateClientListId) { LastClientListUpdateID = listId; }

                if (ClientPeer is P2POwnerPeer)
                {
                    Eos.EosSessionManager.UpdateOwnedSession(ClientPeer.ServerConnection.Endpoint, ServerSettings);
                    TaskPool.Add("WaitForPingDataAsync (owner)",
                        Steamworks.SteamNetworkingUtils.WaitForPingDataAsync(), (task) =>
                    {
                        Steam.SteamManager.UpdateLobby(ServerSettings);
                    });

                    Steam.SteamManager.UpdateLobby(ServerSettings);
                }
                GameMain.NetLobbyScreen?.UpdateDisembarkPointListFromServerSettings();
            }

            if (refreshCampaignUI)
            {
                if (GameMain.GameSession?.GameMode is CampaignMode campaign)
                {
                    campaign.CampaignUI?.UpgradeStore?.RequestRefresh();
                    campaign.CampaignUI?.HRManagerUI?.RefreshUI();
                }
            }
            
        }

        private bool initialUpdateReceived;

        private void ReadLobbyUpdate(IReadMessage inc)
        {
            SegmentTableReader<ServerNetSegment>.Read(inc, (segment, inc) =>
            {
                switch (segment)
                {
                    case ServerNetSegment.SyncIds:
                        bool lobbyUpdated = inc.ReadBoolean();
                        inc.ReadPadBits();

                        if (lobbyUpdated)
                        {
                            var prevDispatcher = GUI.KeyboardDispatcher.Subscriber;

                            UInt16 updateID = inc.ReadUInt16();


                            UInt16 settingsLen = inc.ReadUInt16();
                            byte[] settingsData = inc.ReadBytes(settingsLen);

                            bool isInitialUpdate = inc.ReadBoolean();
                            DebugConsole.Log($"Received {(isInitialUpdate ? "initial" : string.Empty)} lobby update ID: {updateID}, last ID: {GameMain.NetLobbyScreen.LastUpdateID}.");

                            if (isInitialUpdate)
                            {                               
                                ReadInitialUpdate(inc);
                                initialUpdateReceived = true;
                            }

                            string selectSubName = inc.ReadString();
                            string selectSubHash = inc.ReadString();

                            bool usingEnemySub = inc.ReadBoolean();
                            string selectEnemySubName = selectSubName;
                            string selectEnemySubHash = selectSubHash;
                            if (usingEnemySub)
                            {
                                selectEnemySubName = inc.ReadString();
                                selectEnemySubHash = inc.ReadString();
                            }

                            bool usingShuttle = inc.ReadBoolean();
                            string selectShuttleName = inc.ReadString();
                            string selectShuttleHash = inc.ReadString();

                            bool allowSubVoting = inc.ReadBoolean();
                            bool allowModeVoting = inc.ReadBoolean();

                            bool voiceChatEnabled = inc.ReadBoolean();

                            bool allowSpectating = inc.ReadBoolean();

                            float traitorProbability = inc.ReadSingle();
                            int traitorDangerLevel = inc.ReadRangedInteger(TraitorEventPrefab.MinDangerLevel, TraitorEventPrefab.MaxDangerLevel);

                            List<Identifier> missionTypes = new List<Identifier>();
                            uint missionTypeCount = inc.ReadVariableUInt32();
                            for (int i = 0; i < missionTypeCount; i++)
                            {
                                missionTypes.Add(inc.ReadIdentifier());
                            }

                            int modeIndex = inc.ReadByte();

                            string levelSeed = inc.ReadString();
                            float levelDifficulty = inc.ReadSingle();

                            byte botCount = inc.ReadByte();
                            BotSpawnMode botSpawnMode = inc.ReadBoolean() ? BotSpawnMode.Fill : BotSpawnMode.Normal;

                            bool autoRestartEnabled = inc.ReadBoolean();
                            float autoRestartTimer = autoRestartEnabled ? inc.ReadSingle() : 0.0f;

                            //ignore the message if we already a more up-to-date one
                            //or if we're still waiting for the initial update
                            if (NetIdUtils.IdMoreRecent(updateID, GameMain.NetLobbyScreen.LastUpdateID) &&
                                (isInitialUpdate || initialUpdateReceived))
                            {
                                ReadWriteMessage settingsBuf = new ReadWriteMessage();
                                settingsBuf.WriteBytes(settingsData, 0, settingsLen); settingsBuf.BitPosition = 0;
                                ServerSettings.ClientRead(settingsBuf);
                                if (!IsServerOwner)
                                {
                                    ServerInfo info = CreateServerInfoFromSettings();
                                    GameMain.ServerListScreen.AddToRecentServers(info);
                                    GameMain.NetLobbyScreen.Favorite.Visible = true;
                                    GameMain.NetLobbyScreen.Favorite.Selected = GameMain.ServerListScreen.IsFavorite(info);
                                }
                                else
                                {
                                    GameMain.NetLobbyScreen.Favorite.Visible = false;
                                }

                                GameMain.NetLobbyScreen.LastUpdateID = updateID;

                                ServerSettings.ServerLog.ServerName = ServerSettings.ServerName;
                                GameMain.NetLobbyScreen.UsingShuttle = usingShuttle;

                                if (!allowSubVoting || GameMain.NetLobbyScreen.SelectedSub == null)
                                {
                                    GameMain.NetLobbyScreen.TrySelectSub(selectSubName, selectSubHash, SelectedSubType.Sub, GameMain.NetLobbyScreen.SubList);
                                    if (usingEnemySub)
                                    {
                                        GameMain.NetLobbyScreen.TrySelectSub(selectEnemySubName, selectEnemySubHash, SelectedSubType.EnemySub, GameMain.NetLobbyScreen.SubList);
                                    }
                                }
                                GameMain.NetLobbyScreen.TrySelectSub(selectShuttleName, selectShuttleHash, SelectedSubType.Shuttle, GameMain.NetLobbyScreen.ShuttleList.ListBox);

                                GameMain.NetLobbyScreen.SetTraitorProbability(traitorProbability);
                                GameMain.NetLobbyScreen.SetTraitorDangerLevel(traitorDangerLevel);
                                GameMain.NetLobbyScreen.SetMissionTypes(missionTypes);
                                GameMain.NetLobbyScreen.LevelSeed = levelSeed;

                                GameMain.NetLobbyScreen.SelectMode(modeIndex);
                                if (isInitialUpdate && GameMain.NetLobbyScreen.SelectedMode == GameModePreset.MultiPlayerCampaign)
                                {
                                    if (GameMain.Client.IsServerOwner) { RequestSelectMode(modeIndex); }
                                }

                                if (GameMain.NetLobbyScreen.SelectedMode == GameModePreset.MultiPlayerCampaign)
                                {
                                    foreach (SubmarineInfo sub in ServerSubmarines.Where(s => !ServerSettings.HiddenSubs.Contains(s.Name)))
                                    {
                                        GameMain.NetLobbyScreen.CheckIfCampaignSubMatches(sub, NetLobbyScreen.SubmarineDeliveryData.Campaign);
                                    }
                                }

                                GameMain.NetLobbyScreen.SetAllowSpectating(allowSpectating);
                                GameMain.NetLobbyScreen.SetLevelDifficulty(levelDifficulty);
                                GameMain.NetLobbyScreen.SetBotSpawnMode(botSpawnMode);
                                GameMain.NetLobbyScreen.SetBotCount(botCount);
                                GameMain.NetLobbyScreen.SetAutoRestart(autoRestartEnabled, autoRestartTimer);

                                ServerSettings.VoiceChatEnabled = voiceChatEnabled;
                                ServerSettings.AllowSubVoting = allowSubVoting;
                                ServerSettings.AllowModeVoting = allowModeVoting;

                                if (ClientPeer is P2POwnerPeer)
                                {
                                    Eos.EosSessionManager.UpdateOwnedSession(ClientPeer.ServerConnection.Endpoint, ServerSettings);
                                    Steam.SteamManager.UpdateLobby(ServerSettings);
                                }

                                GUI.KeyboardDispatcher.Subscriber = prevDispatcher;
                            }
                        }

                        bool campaignUpdated = inc.ReadBoolean();
                        inc.ReadPadBits();
                        if (campaignUpdated)
                        {
                            MultiPlayerCampaign.ClientRead(inc);
                        }
                        else if (GameMain.NetLobbyScreen.SelectedMode != GameModePreset.MultiPlayerCampaign)
                        {
                            GameMain.NetLobbyScreen.SetCampaignCharacterInfo(null);
                        }

                        lastSentChatMsgID = inc.ReadUInt16();
                        break;
                    case ServerNetSegment.ClientList:
                        ReadClientList(inc);
                        break;
                    case ServerNetSegment.ChatMessage:
                        ChatMessage.ClientRead(inc);
                        break;
                    case ServerNetSegment.Vote:
                        Voting.ClientRead(inc);
                        break;
                }

                return SegmentTableReader<ServerNetSegment>.BreakSegmentReading.No;
            });
        }

        readonly List<IServerSerializable> debugEntityList = new List<IServerSerializable>();
        private void ReadIngameUpdate(IReadMessage inc)
        {
            debugEntityList.Clear();
            
            float sendingTime = inc.ReadSingle() - 0.0f;//TODO: reimplement inc.SenderConnection.RemoteTimeOffset;

            EndRoundTimeRemaining = inc.ReadSingle();

            SegmentTableReader<ServerNetSegment>.Read(inc,
            segmentDataReader: (segment, inc) =>
            {
                switch (segment)
                {
                    case ServerNetSegment.SyncIds:
                        lastSentChatMsgID = inc.ReadUInt16();
                        LastSentEntityEventID = inc.ReadUInt16();

                        bool campaignUpdated = inc.ReadBoolean();
                        inc.ReadPadBits();
                        if (campaignUpdated)
                        {
                            MultiPlayerCampaign.ClientRead(inc);
                        }
                        else if (GameMain.NetLobbyScreen.SelectedMode != GameModePreset.MultiPlayerCampaign)
                        {
                            GameMain.NetLobbyScreen.SetCampaignCharacterInfo(null);
                        }
                        break;
                    case ServerNetSegment.EntityPosition:
                        inc.ReadPadBits(); //padding is required here to make sure any padding bits within tempBuffer are read correctly
                        
                        uint msgLength = inc.ReadVariableUInt32();
                        int msgEndPos = (int)(inc.BitPosition + msgLength * 8);
                        
                        var header = INetSerializableStruct.Read<EntityPositionHeader>(inc);
                        
                        var entity = Entity.FindEntityByID(header.EntityId) as IServerPositionSync;
                        if (msgEndPos > inc.LengthBits)
                        {
                            DebugConsole.ThrowError($"Error while reading a position update for the entity \"({entity?.ToString() ?? "null"})\". Message length exceeds the size of the buffer.");
                            return SegmentTableReader<ServerNetSegment>.BreakSegmentReading.Yes;
                        }

                        debugEntityList.Add(entity);
                        if (entity != null)
                        {
                            if (entity is Item != header.IsItem)
                            {
                                DebugConsole.AddWarning($"Received a potentially invalid ENTITY_POSITION message. Entity type does not match (server entity is {(header.IsItem ? "an item" : "not an item")}, client entity is {(entity?.GetType().ToString() ?? "null")}). Ignoring the message...");
                            }
                            else if (entity is MapEntity { Prefab.UintIdentifier: var uintIdentifier } me &&
                                     uintIdentifier != header.PrefabUintIdentifier)
                            {
                                DebugConsole.AddWarning($"Received a potentially invalid ENTITY_POSITION message."
                                                        +$"Entity identifier does not match (server entity is {MapEntityPrefab.List.FirstOrDefault(p => p.UintIdentifier == header.PrefabUintIdentifier)?.Identifier.Value ?? "[not found]"}, "
                                                        +$"client entity is {me.Prefab.Identifier}). Ignoring the message...");
                            }
                            else
                            {
                                entity.ClientReadPosition(inc, sendingTime);
                            }
                        }
                        //force to the correct position in case the entity doesn't exist
                        //or the message wasn't read correctly for whatever reason
                        inc.BitPosition = msgEndPos;
                        inc.ReadPadBits();
                        break;
                    case ServerNetSegment.ClientList:
                        ReadClientList(inc);
                        break;
                    case ServerNetSegment.EntityEvent:
                    case ServerNetSegment.EntityEventInitial:
                        if (!EntityEventManager.Read(segment, inc, sendingTime))
                        {
                            return SegmentTableReader<ServerNetSegment>.BreakSegmentReading.Yes;
                        }
                        break;
                    case ServerNetSegment.ChatMessage:
                        ChatMessage.ClientRead(inc);
                        break;
                    default:
                        throw new Exception($"Unknown segment \"{segment}\"!)");
                }

                return SegmentTableReader<ServerNetSegment>.BreakSegmentReading.No;
            },
            exceptionHandler: (segment, prevSegments, ex) =>
            {
                List<string> errorLines = new List<string>
                {
                    ex.Message,
                    "Message length: " + inc.LengthBits + " (" + inc.LengthBytes + " bytes)",
                    "Read position: " + inc.BitPosition,
                    $"Segment with error: {segment}"
                };
                if (prevSegments.Any())
                {
                    errorLines.Add("Prev segments: " + string.Join(", ", prevSegments));
                    errorLines.Add(" ");
                }
                errorLines.Add(ex.StackTrace.CleanupStackTrace());
                errorLines.Add(" ");
                if (prevSegments.Concat(segment.ToEnumerable()).Any(s => s.Identifier
                    is ServerNetSegment.EntityPosition
                    or ServerNetSegment.EntityEvent
                    or ServerNetSegment.EntityEventInitial))
                {
                    foreach (IServerSerializable ent in debugEntityList)
                    {
                        if (ent == null)
                        {
                            errorLines.Add(" - NULL");
                            continue;
                        }
                        Entity e = ent as Entity;
                        errorLines.Add(" - " + e.ToString());
                    }
                }

                errorLines.Add("Last console messages:");
                for (int i = DebugConsole.Messages.Count - 1; i > Math.Max(0, DebugConsole.Messages.Count - 20); i--)
                {
                    errorLines.Add("[" + DebugConsole.Messages[i].Time + "] " + DebugConsole.Messages[i].Text);
                }
                GameAnalyticsManager.AddErrorEventOnce("GameClient.ReadInGameUpdate", GameAnalyticsManager.ErrorSeverity.Critical, string.Join("\n", errorLines));
                
                throw new Exception(
                    $"Exception thrown while reading a message of the type \"{segment.Identifier}\" at position {segment.Pointer}." +
                    (prevSegments.Any() ? $" Previous segments: {string.Join(", ", prevSegments)}" : ""),
                    ex);
            });
        }

        private void SendLobbyUpdate()
        {
            IWriteMessage outmsg = new WriteOnlyMessage();
            outmsg.WriteByte((byte)ClientPacketHeader.UPDATE_LOBBY);

            using (var segmentTable = SegmentTableWriter<ClientNetSegment>.StartWriting(outmsg))
            {
                segmentTable.StartNewSegment(ClientNetSegment.SyncIds);
                outmsg.WriteUInt16(GameMain.NetLobbyScreen.LastUpdateID);
                outmsg.WriteUInt16(ChatMessage.LastID);
                outmsg.WriteUInt16(LastClientListUpdateID);
                outmsg.WriteUInt16(nameId);
                outmsg.WriteString(Name);
                var jobPreferences = GameMain.NetLobbyScreen.JobPreferences;
                if (jobPreferences.Count > 0)
                {
                    outmsg.WriteIdentifier(jobPreferences[0].Prefab.Identifier);
                }
                else
                {
                    outmsg.WriteIdentifier(Identifier.Empty);
                }
                outmsg.WriteByte((byte)MultiplayerPreferences.Instance.TeamPreference);

                if (GameMain.GameSession?.GameMode is not MultiPlayerCampaign campaign || campaign.LastSaveID == 0)
                {
                    outmsg.WriteUInt16((UInt16)0);
                }
                else
                {
                    outmsg.WriteUInt16(campaign.LastSaveID);
                    outmsg.WriteByte(campaign.CampaignID);
                    foreach (MultiPlayerCampaign.NetFlags netFlag in Enum.GetValues(typeof(MultiPlayerCampaign.NetFlags)))
                    {
                        outmsg.WriteUInt16(campaign.GetLastUpdateIdForFlag(netFlag));
                    }
                    outmsg.WriteBoolean(GameMain.NetLobbyScreen.CampaignCharacterDiscarded);
                }

                chatMsgQueue.RemoveAll(cMsg => !NetIdUtils.IdMoreRecent(cMsg.NetStateID, lastSentChatMsgID));
                for (int i = 0; i < chatMsgQueue.Count && i < ChatMessage.MaxMessagesPerPacket; i++)
                {
                    if (outmsg.LengthBytes + chatMsgQueue[i].EstimateLengthBytesClient() > MsgConstants.MTU - 5)
                    {
                        //no more room in this packet
                        break;
                    }
                    chatMsgQueue[i].ClientWrite(segmentTable, outmsg);
                }
            }
            if (outmsg.LengthBytes > MsgConstants.MTU)
            {
                DebugConsole.ThrowError($"Maximum packet size exceeded ({outmsg.LengthBytes} > {MsgConstants.MTU})");
            }

            ClientPeer.Send(outmsg, DeliveryMethod.Unreliable);
        }

        private void SendIngameUpdate()
        {
            IWriteMessage outmsg = new WriteOnlyMessage();
            outmsg.WriteByte((byte)ClientPacketHeader.UPDATE_INGAME);
            outmsg.WriteBoolean(EntityEventManager.MidRoundSyncingDone);
            outmsg.WritePadBits();

            using (var segmentTable = SegmentTableWriter<ClientNetSegment>.StartWriting(outmsg))
            {
                segmentTable.StartNewSegment(ClientNetSegment.SyncIds);
                //outmsg.Write(GameMain.NetLobbyScreen.LastUpdateID);
                outmsg.WriteUInt16(ChatMessage.LastID);
                outmsg.WriteUInt16(EntityEventManager.LastReceivedID);
                outmsg.WriteUInt16(LastClientListUpdateID);

                if (!(GameMain.GameSession?.GameMode is MultiPlayerCampaign campaign) || campaign.LastSaveID == 0)
                {
                    outmsg.WriteUInt16((UInt16)0);
                }
                else
                {
                    outmsg.WriteUInt16(campaign.LastSaveID);
                    outmsg.WriteByte(campaign.CampaignID);
                    foreach (MultiPlayerCampaign.NetFlags flag in Enum.GetValues(typeof(MultiPlayerCampaign.NetFlags)))
                    {
                        outmsg.WriteUInt16(campaign.GetLastUpdateIdForFlag(flag));
                    }

                    outmsg.WriteBoolean(GameMain.NetLobbyScreen.CampaignCharacterDiscarded);
                }

                Character.Controlled?.ClientWriteInput(segmentTable, outmsg);
                GameMain.GameScreen.Cam?.ClientWrite(segmentTable, outmsg);

                EntityEventManager.Write(segmentTable, outmsg, ClientPeer?.ServerConnection);

                chatMsgQueue.RemoveAll(cMsg => !NetIdUtils.IdMoreRecent(cMsg.NetStateID, lastSentChatMsgID));
                for (int i = 0; i < chatMsgQueue.Count && i < ChatMessage.MaxMessagesPerPacket; i++)
                {
                    if (outmsg.LengthBytes + chatMsgQueue[i].EstimateLengthBytesClient() > MsgConstants.MTU - 5)
                    {
                        //not enough room in this packet
                        break;
                    }

                    chatMsgQueue[i].ClientWrite(segmentTable, outmsg);
                }
            }

            if (outmsg.LengthBytes > MsgConstants.MTU)
            {
                DebugConsole.ThrowError($"Maximum packet size exceeded ({outmsg.LengthBytes} > {MsgConstants.MTU})");
            }

            ClientPeer.Send(outmsg, DeliveryMethod.Unreliable);
        }

        public void SendChatMessage(ChatMessage msg)
        {
            if (ClientPeer?.ServerConnection == null) { return; }
            lastQueueChatMsgID++;
            msg.NetStateID = lastQueueChatMsgID;
            chatMsgQueue.Add(msg);
        }

        public void SendChatMessage(string message, ChatMessageType type = ChatMessageType.Default)
        {
            if (ClientPeer?.ServerConnection == null) { return; }

            ChatMessage chatMessage = ChatMessage.Create(
                GameStarted && myCharacter != null ? myCharacter.Name : Name,
                message,
                type,
                GameStarted && myCharacter != null ? myCharacter : null);
            chatMessage.ChatMode = GameMain.ActiveChatMode;

            lastQueueChatMsgID++;
            chatMessage.NetStateID = lastQueueChatMsgID;

            chatMsgQueue.Add(chatMessage);
        }

        public void SendRespawnPromptResponse(bool waitForNextRoundRespawn)
        {
            WaitForNextRoundRespawn = waitForNextRoundRespawn;
            IWriteMessage msg = new WriteOnlyMessage();
            msg.WriteByte((byte)ClientPacketHeader.READY_TO_SPAWN);
            msg.WriteBoolean(GameMain.NetLobbyScreen.Spectating);
            msg.WriteBoolean(waitForNextRoundRespawn);
            ClientPeer?.Send(msg, DeliveryMethod.Reliable);
        }

        public void SendTakeOverBotRequest(CharacterInfo bot)
        {
            IWriteMessage msg = new WriteOnlyMessage();
            msg.WriteByte((byte)ClientPacketHeader.TAKEOVERBOT);
            msg.WriteUInt16(bot.ID);
            ClientPeer?.Send(msg, DeliveryMethod.Reliable);
        }

        public void RequestFile(FileTransferType fileType, string file, string fileHash)
        {
            DebugConsole.Log(
                fileType == FileTransferType.CampaignSave ?
                $"Sending a campaign file request to the server." :
                $"Sending a file request to the server (type: {fileType}, path: {file ?? "null"}");

            IWriteMessage msg = new WriteOnlyMessage();
            msg.WriteByte((byte)ClientPacketHeader.FILE_REQUEST);
            msg.WriteByte((byte)FileTransferMessageType.Initiate);
            msg.WriteByte((byte)fileType);
            if (fileType != FileTransferType.CampaignSave)
            {
                msg.WriteString(file ?? throw new ArgumentNullException(nameof(file)));
                msg.WriteString(fileHash ?? throw new ArgumentNullException(nameof(fileHash)));
            }
            ClientPeer.Send(msg, DeliveryMethod.Reliable);
        }

        public void CancelFileTransfer(FileReceiver.FileTransferIn transfer)
        {
            CancelFileTransfer(transfer.ID);
        }

        public void UpdateFileTransfer(FileReceiver.FileTransferIn transfer, int expecting, int lastSeen, bool reliable = false)
        {
            if (!reliable && (DateTime.Now - transfer.LastOffsetAckTime).TotalSeconds < 1)
            {
                return;
            }
            transfer.RecordOffsetAckTime();
            
            IWriteMessage msg = new WriteOnlyMessage();
            msg.WriteByte((byte)ClientPacketHeader.FILE_REQUEST);
            msg.WriteByte((byte)FileTransferMessageType.Data);
            msg.WriteByte((byte)transfer.ID);
            msg.WriteInt32(expecting);
            msg.WriteInt32(lastSeen);
            ClientPeer.Send(msg, reliable ? DeliveryMethod.Reliable : DeliveryMethod.Unreliable);
        }

        public void CancelFileTransfer(int id)
        {
            IWriteMessage msg = new WriteOnlyMessage();
            msg.WriteByte((byte)ClientPacketHeader.FILE_REQUEST);
            msg.WriteByte((byte)FileTransferMessageType.Cancel);
            msg.WriteByte((byte)id);
            ClientPeer.Send(msg, DeliveryMethod.Reliable);
        }

        private void OnFileReceived(FileReceiver.FileTransferIn transfer)
        {
            switch (transfer.FileType)
            {
                case FileTransferType.Submarine:
                    //new GUIMessageBox(TextManager.Get("ServerDownloadFinished"), TextManager.GetWithVariable("FileDownloadedNotification", "[filename]", transfer.FileName));
                    var newSub = new SubmarineInfo(transfer.FilePath);
                    if (newSub.IsFileCorrupted) { return; }

                    var existingSubs = SubmarineInfo.SavedSubmarines
                        .Where(s => s.Name == newSub.Name && s.MD5Hash == newSub.MD5Hash)
                        .ToList();
                    foreach (SubmarineInfo existingSub in existingSubs)
                    {
                        existingSub.Dispose();
                    }
                    SubmarineInfo.AddToSavedSubs(newSub);

                    for (int i = 0; i < 2; i++)
                    {
                        IEnumerable<GUIComponent> subListChildren = (i == 0) ?
                            GameMain.NetLobbyScreen.ShuttleList.ListBox.Content.Children :
                            GameMain.NetLobbyScreen.SubList.Content.Children;

                        var subElement = subListChildren.FirstOrDefault(c =>
                            ((SubmarineInfo)c.UserData).Name == newSub.Name &&
                            ((SubmarineInfo)c.UserData).MD5Hash.StringRepresentation == newSub.MD5Hash.StringRepresentation);
                        if (subElement == null) { continue; }

                        //set the dimmed out submarine info back to normal and update texts
                        if (subElement.FindChild("nametext", recursive: true) is GUITextBlock nameTextBlock)
                        {
                            nameTextBlock.TextColor = new Color(nameTextBlock.TextColor, 1.0f);
                        }
                        if (subElement.FindChild("classtext", recursive: true) is GUITextBlock classTextBlock)
                        {
                            classTextBlock.Text = newSub.Class.Name;
                            classTextBlock.TextColor = new Color(classTextBlock.TextColor, 0.8f);
                        }
                        if (subElement.FindChild("pricetext", recursive: true) is GUITextBlock priceTextBlock)
                        {
                            priceTextBlock.Text = TextManager.GetWithVariable("currencyformat", "[credits]", string.Format(CultureInfo.InvariantCulture, "{0:N0}", newSub.Price));
                            priceTextBlock.TextColor = new Color(priceTextBlock.TextColor, 0.8f);
                        }

                        subElement.UserData = newSub;
                        subElement.ToolTip = newSub.Description;
                    }

                    if (GameMain.NetLobbyScreen.FailedSelectedSub.HasValue &&
                        GameMain.NetLobbyScreen.FailedSelectedSub.Value.Name == newSub.Name &&
                        GameMain.NetLobbyScreen.FailedSelectedSub.Value.Hash == newSub.MD5Hash.StringRepresentation)
                    {
                        GameMain.NetLobbyScreen.TrySelectSub(newSub.Name, newSub.MD5Hash.StringRepresentation, SelectedSubType.Sub, GameMain.NetLobbyScreen.SubList);
                    }

                    if (GameMain.NetLobbyScreen.FailedSelectedShuttle.HasValue &&
                        GameMain.NetLobbyScreen.FailedSelectedShuttle.Value.Name == newSub.Name &&
                        GameMain.NetLobbyScreen.FailedSelectedShuttle.Value.Hash == newSub.MD5Hash.StringRepresentation)
                    {
                        GameMain.NetLobbyScreen.TrySelectSub(newSub.Name, newSub.MD5Hash.StringRepresentation, SelectedSubType.Shuttle, GameMain.NetLobbyScreen.ShuttleList.ListBox);
                    }

                    if (GameMain.NetLobbyScreen.SelectedMode == GameModePreset.PvP &&
                        GameMain.NetLobbyScreen.FailedSelectedEnemySub.HasValue &&
                        GameMain.NetLobbyScreen.FailedSelectedEnemySub.Value.Name == newSub.Name &&
                        GameMain.NetLobbyScreen.FailedSelectedEnemySub.Value.Hash == newSub.MD5Hash.StringRepresentation)
                    {
                        GameMain.NetLobbyScreen.TrySelectSub(newSub.Name, newSub.MD5Hash.StringRepresentation, SelectedSubType.EnemySub, GameMain.NetLobbyScreen.SubList);
                    }

                    NetLobbyScreen.FailedSubInfo failedCampaignSub = GameMain.NetLobbyScreen.FailedCampaignSubs.Find(s => s.Name == newSub.Name && s.Hash == newSub.MD5Hash.StringRepresentation);
                    if (failedCampaignSub != default)
                    {
                        GameMain.NetLobbyScreen.FailedCampaignSubs.Remove(failedCampaignSub);
                    }

                    NetLobbyScreen.FailedSubInfo failedOwnedSub = GameMain.NetLobbyScreen.FailedOwnedSubs.Find(s => s.Name == newSub.Name && s.Hash == newSub.MD5Hash.StringRepresentation);
                    if (failedOwnedSub != default)
                    {
                        GameMain.NetLobbyScreen.FailedOwnedSubs.Remove(failedOwnedSub);
                    }

                    // Replace a submarine dud with the downloaded version
                    SubmarineInfo existingServerSub = ServerSubmarines.Find(s =>
                        s.Name == newSub.Name
                        && s.MD5Hash == newSub.MD5Hash);
                    if (existingServerSub != null)
                    {
                        int existingIndex = ServerSubmarines.IndexOf(existingServerSub);
                        ServerSubmarines[existingIndex] = newSub;
                        existingServerSub.Dispose();
                    }

                    break;
                case FileTransferType.CampaignSave:
                    XElement gameSessionDocRoot = SaveUtil.DecompressSaveAndLoadGameSessionDoc(transfer.FilePath)?.Root;
                    byte campaignID = (byte)MathHelper.Clamp(gameSessionDocRoot.GetAttributeInt("campaignid", 0), 0, 255);
                    if (GameMain.GameSession?.GameMode is not MultiPlayerCampaign campaign || campaign.CampaignID != campaignID)
                    {
                        string savePath = transfer.FilePath;
                        GameMain.GameSession = new GameSession(null, Option.None, CampaignDataPath.CreateRegular(savePath), GameModePreset.MultiPlayerCampaign, CampaignSettings.Empty);
                        campaign = (MultiPlayerCampaign)GameMain.GameSession.GameMode;
                        campaign.CampaignID = campaignID;
                        GameMain.NetLobbyScreen.ToggleCampaignMode(true);
                    }

                    GameMain.GameSession.DataPath = CampaignDataPath.CreateRegular(transfer.FilePath);
                    if (GameMain.GameSession.SubmarineInfo == null || campaign.Map == null)
                    {
                        string subPath = Path.Combine(SaveUtil.TempPath, gameSessionDocRoot.GetAttributeString("submarine", "")) + ".sub";
                        GameMain.GameSession.SubmarineInfo = new SubmarineInfo(subPath, "");
                    }

                    campaign.LoadState(GameMain.GameSession.DataPath.LoadPath);
                    GameMain.GameSession?.SubmarineInfo?.Reload();
                    GameMain.GameSession?.SubmarineInfo?.CheckSubsLeftBehind();

                    if (GameMain.GameSession?.SubmarineInfo?.Name != null)
                    {
                        GameMain.NetLobbyScreen.TryDisplayCampaignSubmarine(GameMain.GameSession.SubmarineInfo);
                    }
                    campaign.LastSaveID = campaign.PendingSaveID;

                    if (Screen.Selected == GameMain.NetLobbyScreen)
                    {
                        //reselect to refresh the state of the lobby screen (enable spectate button, etc)
                        GameMain.NetLobbyScreen.SaveAppearance();
                        GameMain.NetLobbyScreen.Select();
                    }

                    DebugConsole.Log("Campaign save received (" + GameMain.GameSession.DataPath + "), save ID " + campaign.LastSaveID);
                    //decrement campaign update IDs so the server will send us the latest data
                    //(as there may have been campaign updates after the save file was created)
                    foreach (MultiPlayerCampaign.NetFlags flag in Enum.GetValues(typeof(MultiPlayerCampaign.NetFlags)))
                    {
                        campaign.SetLastUpdateIdForFlag(flag, (ushort)(campaign.GetLastUpdateIdForFlag(flag) - 1));
                    }
                    break;
                case FileTransferType.Mod:
                    if (!(Screen.Selected is ModDownloadScreen)) { return; }

                    GameMain.ModDownloadScreen.CurrentDownloadFinished(transfer);
                    break;
            }
        }

        private void OnTransferFailed(FileReceiver.FileTransferIn transfer)
        {
            if (transfer.FileType == FileTransferType.CampaignSave)
            {
                GameMain.Client.RequestFile(FileTransferType.CampaignSave, null, null);
            }
        }

        public override void CreateEntityEvent(INetSerializable entity, NetEntityEvent.IData extraData = null)
        {
            CreateEntityEvent(entity, extraData, requireControlledCharacter: true);
        }

        public void CreateEntityEvent(INetSerializable entity, NetEntityEvent.IData extraData, bool requireControlledCharacter)
        {
            if (entity is not IClientSerializable clientSerializable)
            {
                throw new InvalidCastException($"Entity is not {nameof(IClientSerializable)}");
            }
            EntityEventManager.CreateEvent(clientSerializable, extraData, requireControlledCharacter);
        }

        public bool HasPermission(ClientPermissions permission)
        {
            return permissions.HasFlag(permission);
        }

        public bool HasConsoleCommandPermission(Identifier commandName)
        {
            if (!permissions.HasFlag(ClientPermissions.ConsoleCommands)) { return false; }

            if (permittedConsoleCommands.Contains(commandName)) { return true; }

            //check aliases
            foreach (DebugConsole.Command command in DebugConsole.Commands)
            {
                if (command.Names.Contains(commandName))
                {
                    if (command.Names.Intersect(permittedConsoleCommands).Any()) { return true; }
                    break;
                }
            }

            return false;
        }

        public void Quit()
        {
            ClientPeer?.Close(PeerDisconnectPacket.WithReason(DisconnectReason.Disconnected));
            
            GUIMessageBox.MessageBoxes.RemoveAll(c => c?.UserData is RoundSummary);
        }

        public void SendCharacterInfo(string newName = null)
        {
            IWriteMessage msg = new WriteOnlyMessage();
            msg.WriteByte((byte)ClientPacketHeader.UPDATE_CHARACTERINFO);
            WriteCharacterInfo(msg, newName);
            ClientPeer?.Send(msg, DeliveryMethod.Reliable);
        }

        public void WriteCharacterInfo(IWriteMessage msg, string newName = null)
        {
            msg.WriteBoolean(GameMain.NetLobbyScreen.Spectating);
            msg.WritePadBits();
            if (characterInfo == null) { return; }

            var head = characterInfo.Head;

           var netInfo = new NetCharacterInfo(
               NewName: newName ?? string.Empty,
               Tags: head.Preset.TagSet.ToImmutableArray(),
               HairIndex: (byte)head.HairIndex,
               BeardIndex: (byte)head.BeardIndex,
               MoustacheIndex: (byte)head.MoustacheIndex,
               FaceAttachmentIndex: (byte)head.FaceAttachmentIndex,
               SkinColor: head.SkinColor,
               HairColor: head.HairColor,
               FacialHairColor: head.FacialHairColor,
               JobVariants: GameMain.NetLobbyScreen.JobPreferences.Select(NetJobVariant.FromJobVariant).ToImmutableArray());

            msg.WriteNetSerializableStruct(netInfo);
        }

        public void Vote(VoteType voteType, object data)
        {
            if (ClientPeer == null) { return; }

            IWriteMessage msg = new WriteOnlyMessage();
            msg.WriteByte((byte)ClientPacketHeader.UPDATE_LOBBY);
            using (var segmentTable = SegmentTableWriter<ClientNetSegment>.StartWriting(msg))
            {
                segmentTable.StartNewSegment(ClientNetSegment.Vote);
                bool succeeded = Voting.ClientWrite(msg, voteType, data);
                if (!succeeded)
                {
                    throw new Exception(
                        $"Failed to write vote of type {voteType}: " +
                        $"data was of invalid type {data?.GetType().Name ?? "NULL"}");
                }
            }

            ClientPeer.Send(msg, DeliveryMethod.Reliable);
        }

        public void VoteForKick(Client votedClient)
        {
            if (votedClient == null) { return; }
            Vote(VoteType.Kick, votedClient);
        }

        #region Submarine Change Voting
        public void InitiateSubmarineChange(SubmarineInfo sub, bool transferItems, VoteType voteType)
        {
            if (sub == null) { return; }
            Vote(voteType, (sub, transferItems));
        }

        public void ShowSubmarineChangeVoteInterface(Client starter, SubmarineInfo info, VoteType type, bool transferItems, float timeOut)
        {
            if (info == null) { return; }
            if (votingInterface != null && votingInterface.VoteRunning) { return; }
            votingInterface?.Remove();
            votingInterface = VotingInterface.CreateSubmarineVotingInterface(starter, info, type, transferItems, timeOut);
        }
        #endregion

        #region Money Transfer Voting
        public void ShowMoneyTransferVoteInterface(Client starter, Client from, int amount, Client to, float timeOut)
        {
            if (votingInterface != null && votingInterface.VoteRunning) { return; }
            if (from == null && to == null) 
            {
                DebugConsole.ThrowError("Tried to initiate a vote for transferring from null to null!");
                return; 
            }
            votingInterface?.Remove();
            votingInterface = VotingInterface.CreateMoneyTransferVotingInterface(starter, from, to, amount, timeOut);
        }
        #endregion

        public override void AddChatMessage(ChatMessage message)
        {
            if (string.IsNullOrEmpty(message.Text)) { return; }
            if (message.SenderCharacter is { IsDead: false } sender)
            {
                if (message.Text.IsNullOrEmpty())
                {
                    sender.ShowTextlessSpeechBubble(2.0f, message.Color);
                }
                else
                {
                    sender.ShowSpeechBubble(message.Color, message.Text);
                    if (!sender.IsBot)
                    {
                        sender.TextChatVolume = 1f;
                    }
                }
            }
            GameMain.NetLobbyScreen.NewChatMessage(message);
            chatBox.AddMessage(message);
        }

        public override void KickPlayer(string kickedName, string reason)
        {
            IWriteMessage msg = new WriteOnlyMessage();
            msg.WriteByte((byte)ClientPacketHeader.SERVER_COMMAND);
            msg.WriteUInt16((UInt16)ClientPermissions.Kick);
            msg.WriteString(kickedName);
            msg.WriteString(reason);

            ClientPeer.Send(msg, DeliveryMethod.Reliable);
        }

        public override void BanPlayer(string kickedName, string reason, TimeSpan? duration = null)
        {
            IWriteMessage msg = new WriteOnlyMessage();
            msg.WriteByte((byte)ClientPacketHeader.SERVER_COMMAND);
            msg.WriteUInt16((UInt16)ClientPermissions.Ban);
            msg.WriteString(kickedName);
            msg.WriteString(reason);
            msg.WriteDouble(duration.HasValue ? duration.Value.TotalSeconds : 0.0); //0 = permaban

            ClientPeer.Send(msg, DeliveryMethod.Reliable);
        }

        public override void UnbanPlayer(string playerName)
        {
            IWriteMessage msg = new WriteOnlyMessage();
            msg.WriteByte((byte)ClientPacketHeader.SERVER_COMMAND);
            msg.WriteUInt16((UInt16)ClientPermissions.Unban);
            msg.WriteBoolean(true); msg.WritePadBits();
            msg.WriteString(playerName);
            ClientPeer.Send(msg, DeliveryMethod.Reliable);
        }
        
        public override void UnbanPlayer(Endpoint endpoint)
        {
            IWriteMessage msg = new WriteOnlyMessage();
            msg.WriteByte((byte)ClientPacketHeader.SERVER_COMMAND);
            msg.WriteUInt16((UInt16)ClientPermissions.Unban);
            msg.WriteBoolean(false); msg.WritePadBits();
            msg.WriteString(endpoint.StringRepresentation);
            ClientPeer.Send(msg, DeliveryMethod.Reliable);
        }

        public void UpdateClientPermissions(Client targetClient)
        {
            IWriteMessage msg = new WriteOnlyMessage();
            msg.WriteByte((byte)ClientPacketHeader.SERVER_COMMAND);
            msg.WriteUInt16((UInt16)ClientPermissions.ManagePermissions);
            targetClient.WritePermissions(msg);
            ClientPeer.Send(msg, DeliveryMethod.Reliable);
        }

        public void SendCampaignState()
        {
            if (!(GameMain.GameSession.GameMode is MultiPlayerCampaign campaign))
            {
                DebugConsole.ThrowError("Failed send campaign state to the server (no campaign active).\n" + Environment.StackTrace.CleanupStackTrace());
                return;
            }
            IWriteMessage msg = new WriteOnlyMessage();
            msg.WriteByte((byte)ClientPacketHeader.SERVER_COMMAND);
            msg.WriteUInt16((UInt16)ClientPermissions.ManageCampaign);
            campaign.ClientWrite(msg);
            ClientPeer.Send(msg, DeliveryMethod.Reliable);
        }

        public void SendConsoleCommand(string command)
        {
            if (string.IsNullOrWhiteSpace(command))
            {
                DebugConsole.ThrowError("Cannot send an empty console command to the server!\n" + Environment.StackTrace.CleanupStackTrace());
                return;
            }

            IWriteMessage msg = new WriteOnlyMessage();
            msg.WriteByte((byte)ClientPacketHeader.SERVER_COMMAND);
            msg.WriteUInt16((UInt16)ClientPermissions.ConsoleCommands);
            msg.WriteString(command);
            Vector2 cursorWorldPos = GameMain.GameScreen.Cam.ScreenToWorld(PlayerInput.MousePosition);
            msg.WriteSingle(cursorWorldPos.X);
            msg.WriteSingle(cursorWorldPos.Y);

            ClientPeer.Send(msg, DeliveryMethod.Reliable);
        }

        /// <summary>
        /// Tell the server to start the round (permission required)
        /// </summary>
        public void RequestStartRound(bool continueCampaign = false)
        {
            IWriteMessage msg = new WriteOnlyMessage();
            msg.WriteByte((byte)ClientPacketHeader.SERVER_COMMAND);
            msg.WriteUInt16((UInt16)ClientPermissions.ManageRound);
            msg.WriteBoolean(false); //indicates round start
            msg.WriteBoolean(continueCampaign);

            ClientPeer.Send(msg, DeliveryMethod.Reliable);
        }

        /// <summary>
        /// Tell the server to select a submarine (permission required)
        /// </summary>
        public void RequestSelectSub(SubmarineInfo sub, SelectedSubType type)
        {
            if (!HasPermission(ClientPermissions.SelectSub) || sub == null) { return; }

            IWriteMessage msg = new WriteOnlyMessage();
            msg.WriteByte((byte)ClientPacketHeader.SERVER_COMMAND);
            msg.WriteUInt16((ushort)ClientPermissions.SelectSub);
            msg.WriteByte((byte)type);
            msg.WriteString(sub.MD5Hash.StringRepresentation);
            ClientPeer.Send(msg, DeliveryMethod.Reliable);
        }

        /// <summary>
        /// Tell the server to select a mode (permission required)
        /// </summary>
        public void RequestSelectMode(int modeIndex)
        {
            if (modeIndex < 0 || modeIndex >= GameMain.NetLobbyScreen.ModeList.Content.CountChildren)
            {
                DebugConsole.ThrowError("Gamemode index out of bounds (" + modeIndex + ")\n" + Environment.StackTrace.CleanupStackTrace());
                return;
            }

            IWriteMessage msg = new WriteOnlyMessage();
            msg.WriteByte((byte)ClientPacketHeader.SERVER_COMMAND);
            msg.WriteUInt16((UInt16)ClientPermissions.SelectMode);
            msg.WriteUInt16((UInt16)modeIndex);

            ClientPeer.Send(msg, DeliveryMethod.Reliable);
        }

        public void SetupNewCampaign(SubmarineInfo sub, string saveName, string mapSeed, CampaignSettings settings)
        {
            GameMain.NetLobbyScreen.CampaignSetupFrame.Visible = false;
            GameMain.NetLobbyScreen.CampaignFrame.Visible = false;

            saveName = Path.GetFileNameWithoutExtension(saveName);

            IWriteMessage msg = new WriteOnlyMessage();
            msg.WriteByte((byte)ClientPacketHeader.CAMPAIGN_SETUP_INFO);

            msg.WriteBoolean(true); msg.WritePadBits();
            msg.WriteString(saveName);
            msg.WriteString(mapSeed);
            msg.WriteString(sub.Name);
            msg.WriteString(sub.MD5Hash.StringRepresentation);
            msg.WriteNetSerializableStruct(settings);

            ClientPeer.Send(msg, DeliveryMethod.Reliable);
        }

        public void SetupLoadCampaign(string filePath, Option<uint> backupIndex)
        {
            if (ClientPeer == null) { return; }

            GameMain.NetLobbyScreen.CampaignSetupFrame.Visible = false;
            GameMain.NetLobbyScreen.CampaignFrame.Visible = false;

            IWriteMessage msg = new WriteOnlyMessage();
            msg.WriteByte((byte)ClientPacketHeader.CAMPAIGN_SETUP_INFO);

            msg.WriteBoolean(false); msg.WritePadBits();
            msg.WriteString(filePath);

            if (backupIndex.TryUnwrap(out uint index))
            {
                msg.WriteBoolean(true);
                msg.WritePadBits();
                msg.WriteUInt32(index);
            }
            else
            {
                msg.WriteBoolean(false);
                msg.WritePadBits();
            }

            ClientPeer.Send(msg, DeliveryMethod.Reliable);
        }

        /// <summary>
        /// Tell the server to end the round (permission required)
        /// </summary>
        public void RequestRoundEnd(bool save, bool quitCampaign = false)
        {
            IWriteMessage msg = new WriteOnlyMessage();
            msg.WriteByte((byte)ClientPacketHeader.SERVER_COMMAND);
            msg.WriteUInt16((UInt16)ClientPermissions.ManageRound);
            msg.WriteBoolean(true); //indicates round end
            msg.WriteBoolean(save);
            msg.WriteBoolean(quitCampaign);

            ClientPeer.Send(msg, DeliveryMethod.Reliable);
        }

        public bool JoinOnGoingClicked(GUIButton button, object _)
        {
            MultiPlayerCampaign campaign =
                GameMain.NetLobbyScreen.SelectedMode == GameMain.GameSession?.GameMode.Preset ?
                GameMain.GameSession?.GameMode as MultiPlayerCampaign : null;

            if (FileReceiver.ActiveTransfers.Any(t => t.FileType == FileTransferType.CampaignSave) ||
                (campaign != null && NetIdUtils.IdMoreRecent(campaign.PendingSaveID, campaign.LastSaveID)))
            {
                new GUIMessageBox("", TextManager.Get("campaignfiletransferinprogress"));
                return false;
            }
            if (button != null) { button.Enabled = false; }
            if (campaign != null) { LateCampaignJoin = true; }

            if (ClientPeer == null) { return false; }

            IWriteMessage readyToStartMsg = new WriteOnlyMessage();
            readyToStartMsg.WriteByte((byte)ClientPacketHeader.RESPONSE_STARTGAME);

            //assume we have the required sub files to start the round
            //(if not, we'll find out when the server sends the STARTGAME message and can initiate a file transfer)
            readyToStartMsg.WriteBoolean(true);

            WriteCharacterInfo(readyToStartMsg);

            ClientPeer.Send(readyToStartMsg, DeliveryMethod.Reliable);

            return false;
        }

        public bool SetReadyToStart(GUITickBox tickBox)
        {
            if (GameStarted)
            {
                tickBox.Parent.Visible = false;
                return false;
            }
            Vote(VoteType.StartRound, tickBox.Selected);
            return true;
        }

        public bool ToggleEndRoundVote(GUITickBox tickBox)
        {
            if (!GameStarted) return false;

            if (!ServerSettings.AllowEndVoting || !HasSpawned)
            {
                tickBox.Visible = false;
                return false;
            }

            Vote(VoteType.EndRound, tickBox.Selected);
            return false;
        }

        protected CharacterInfo characterInfo;
        protected Character myCharacter;

        public CharacterInfo CharacterInfo
        {
            get { return characterInfo; }
            set { characterInfo = value; }
        }

        public Character Character
        {
            get { return myCharacter; }
            set { myCharacter = value; }
        }

        protected GUIFrame inGameHUD;
        protected ChatBox chatBox;

        public GUIButton ShowLogButton; //TODO: move to NetLobbyScreen
        private bool hasPermissionToUseLogButton;

        public void UpdateLogButtonPermissions()
        {
            hasPermissionToUseLogButton = GameMain.Client.HasPermission(ClientPermissions.ServerLog);
            UpdateLogButtonVisibility();
        }

        private void UpdateLogButtonVisibility()
        {
            if (ShowLogButton != null)
            {
                if (Screen.Selected != GameMain.GameScreen)
                {
                    ShowLogButton.Visible = hasPermissionToUseLogButton;
                }
                else
                {
                    var campaign = GameMain.GameSession?.Campaign;
                    ShowLogButton.Visible = hasPermissionToUseLogButton && (campaign == null || !campaign.ShowCampaignUI);
                }
            }
        }
        
        public GUIFrame InGameHUD
        {
            get { return inGameHUD; }
        }

        public ChatBox ChatBox
        {
            get { return chatBox; }
        }

        public VotingInterface VotingInterface
        {
            get { return votingInterface; }
        }
        private VotingInterface votingInterface;

        public bool TypingChatMessage(GUITextBox textBox, string text)
        {
            return chatBox.TypingChatMessage(textBox, text);
        }

        public bool EnterChatMessage(GUITextBox textBox, string message)
        {
            var messageType = NetLobbyScreen.TeamChatSelected ? ChatMessageType.Team : ChatMessageType.Default;
            textBox.TextColor = ChatMessage.MessageColor[(int)messageType];

            if (string.IsNullOrWhiteSpace(message))
            {
                if (textBox == chatBox.InputBox) textBox.Deselect();
                return false;
            }
            chatBox.ChatManager.Store(message);
            SendChatMessage(message, type: messageType);

            if (textBox.DeselectAfterMessage)
            {
                textBox.Deselect();
            }
            textBox.Text = "";

            if (ChatBox.CloseAfterMessageSent)
            {
                ChatBox.ToggleOpen = false;
                ChatBox.CloseAfterMessageSent = false;
            }

            return true;
        }

        public void AddToGUIUpdateList()
        {
            if (GUI.DisableHUD || GUI.DisableUpperHUD) return;

            if (GameStarted &&
                Screen.Selected == GameMain.GameScreen)
            {
                inGameHUD.AddToGUIUpdateList();
                GameMain.NetLobbyScreen.FileTransferFrame?.AddToGUIUpdateList();
            }

            ServerSettings.AddToGUIUpdateList();
            if (ServerSettings.ServerLog.LogFrame != null) ServerSettings.ServerLog.LogFrame.AddToGUIUpdateList();

            GameMain.NetLobbyScreen?.PlayerFrame?.AddToGUIUpdateList();
        }

        public void UpdateHUD(float deltaTime)
        {
            GUITextBox msgBox = null;

            if (Screen.Selected == GameMain.GameScreen)
            {
                msgBox = chatBox.InputBox;
            }
            else if (Screen.Selected == GameMain.NetLobbyScreen)
            {
                msgBox = GameMain.NetLobbyScreen.ChatInput;
            }

            UpdateLogButtonVisibility();

            if (GameStarted && Screen.Selected == GameMain.GameScreen)
            {
                bool disableButtons = Character.Controlled?.SelectedItem?.GetComponent<Controller>() is Controller c1 && c1.HideHUD ||
                    Character.Controlled?.SelectedSecondaryItem?.GetComponent<Controller>() is Controller c2 && c2.HideHUD;
                buttonContainer.Visible = !disableButtons;
                
                if (!GUI.DisableHUD && !GUI.DisableUpperHUD)
                {
                    inGameHUD.UpdateManually(deltaTime);
                    chatBox.Update(deltaTime);

                    if (votingInterface != null)
                    {
                        votingInterface.Update(deltaTime);
                        if (!votingInterface.VoteRunning || votingInterface.TimedOut)
                        {
                            if (votingInterface.TimedOut)
                            {
                                DebugConsole.AddWarning($"Voting interface timed out.");
                            }
                            votingInterface.Remove();
                            votingInterface = null;
                        }
                    }

                    cameraFollowsSub.Visible = Character.Controlled == null;
                }
                /*if (Character.Controlled == null || Character.Controlled.IsDead)
                {
                    GameMain.GameScreen.Cam.TargetPos = Vector2.Zero;
                    GameMain.LightManager.LosEnabled = false;
                }*/
            }

            //tab doesn't autoselect the chatbox when debug console is open,
            //because tab is used for autocompleting console commands
            if (msgBox != null)
            {
                if (GUI.KeyboardDispatcher.Subscriber == null)                
                {
                    var chatKeyStates = ChatBox.ChatKeyStates.GetChatKeyStates();
                    if (chatKeyStates.AnyHit)
                    {
                        if (msgBox.Selected)
                        {
                            msgBox.Text = "";
                            msgBox.Deselect();
                        }
                        else
                        {
                            if (Screen.Selected == GameMain.GameScreen)
                            {
                                ChatBox.ApplySelectionInputs(msgBox, false, chatKeyStates);
                            }
                            msgBox.Select(msgBox.Text.Length);
                        }
                    }
                }
            }
        }

        public void Draw(Microsoft.Xna.Framework.Graphics.SpriteBatch spriteBatch)
        {
            if (GUI.DisableHUD || GUI.DisableUpperHUD) return;

            if (FileReceiver != null && FileReceiver.ActiveTransfers.Count > 0)
            {
                var transfer = FileReceiver.ActiveTransfers.First();
                GameMain.NetLobbyScreen.FileTransferFrame.Visible = true;
                GameMain.NetLobbyScreen.FileTransferFrame.UserData = transfer;
                GameMain.NetLobbyScreen.FileTransferTitle.Text =
                    ToolBox.LimitString(
                        TextManager.GetWithVariable("DownloadingFile", "[filename]", transfer.FileName).Value,
                        GameMain.NetLobbyScreen.FileTransferTitle.Font,
                        GameMain.NetLobbyScreen.FileTransferTitle.Rect.Width);
                GameMain.NetLobbyScreen.FileTransferProgressBar.BarSize = transfer.Progress;
                GameMain.NetLobbyScreen.FileTransferProgressText.Text =
                    MathUtils.GetBytesReadable((long)transfer.Received) + " / " + MathUtils.GetBytesReadable((long)transfer.FileSize);
            }
            else
            {
                GameMain.NetLobbyScreen.FileTransferFrame.Visible = false;
            }

            if (!GameStarted || Screen.Selected != GameMain.GameScreen) { return; }

            inGameHUD.DrawManually(spriteBatch);

            int endVoteCount = Voting.GetVoteCountYes(VoteType.EndRound);
            int endVoteMax = Voting.GetVoteCountMax(VoteType.EndRound);
            if (endVoteCount > 0)
            {
                if (EndVoteTickBox.Visible)
                {
                    EndVoteTickBox.Text = $"{endRoundVoteText} {endVoteCount}/{endVoteMax}";
                }
                else
                {
                    LocalizedString endVoteText = TextManager.GetWithVariables("EndRoundVotes", ("[votes]", endVoteCount.ToString()), ("[max]", endVoteMax.ToString()));
                    GUI.DrawString(spriteBatch, EndVoteTickBox.Rect.Center.ToVector2() - GUIStyle.SmallFont.MeasureString(endVoteText) / 2,
                        endVoteText.Value,
                        Color.White,
                        font: GUIStyle.SmallFont);
                }
            }
            else
            {
                EndVoteTickBox.Text = endRoundVoteText;
            }

            if (RespawnManager != null)
            {
                LocalizedString respawnText = string.Empty;
                Color textColor = Color.White;
                bool hideRespawnButtons = false; 

                if (EndRoundTimeRemaining > 0)
                {
                    respawnText = TextManager.GetWithVariable("endinground", "[time]", ToolBox.SecondsToReadableTime(EndRoundTimeRemaining))
                        .Fallback(ToolBox.SecondsToReadableTime(EndRoundTimeRemaining), useDefaultLanguageIfFound: false);
                }
                if (RespawnManager.CurrentState == RespawnManager.State.Waiting)
                {
                    if (RespawnManager.RespawnCountdownStarted)
                    {
                        float timeLeft = (float)(RespawnManager.RespawnTime - DateTime.Now).TotalSeconds;
                        respawnText = TextManager.GetWithVariable("RespawningIn", "[time]", ToolBox.SecondsToReadableTime(timeLeft));
                    }
                    else if (RespawnManager.PendingRespawnCount > 0)
                    {
                        respawnText = TextManager.GetWithVariables("RespawnWaitingForMoreDeadPlayers",
                            ("[deadplayers]", RespawnManager.PendingRespawnCount.ToString()),
                            ("[requireddeadplayers]", RespawnManager.RequiredRespawnCount.ToString()));
                    }
                }
                else if (RespawnManager.CurrentState == RespawnManager.State.Transporting && 
                    RespawnManager.ReturnCountdownStarted)
                {
                    float timeLeft = (float)(RespawnManager.ReturnTime - DateTime.Now).TotalSeconds;
                    respawnText = timeLeft <= 0.0f ?
                        "" :
                        TextManager.GetWithVariable("RespawnShuttleLeavingIn", "[time]", ToolBox.SecondsToReadableTime(timeLeft));
                    if (timeLeft < 20.0f)
                    {
                        //oscillate between 0-1
                        float phase = (float)(Math.Sin(timeLeft * MathHelper.Pi) + 1.0f) * 0.5f;
                        //textScale = 1.0f + phase * 0.5f;
                        textColor = Color.Lerp(GUIStyle.Red, Color.White, 1.0f - phase);
                    }
                    hideRespawnButtons = true;
                }

                GameMain.GameSession.SetRespawnInfo(
                    text: respawnText.Value, textColor: textColor, 
                    waitForNextRoundRespawn: (WaitForNextRoundRespawn ?? true), hideButtons: hideRespawnButtons);                
            }

            if (!ShowNetStats) { return; }

            NetStats.Draw(spriteBatch, new Rectangle(300, 10, 300, 150));

            /* TODO: reimplement
            int width = 200, height = 300;
            int x = GameMain.GraphicsWidth - width, y = (int)(GameMain.GraphicsHeight * 0.3f);

            GUI.DrawRectangle(spriteBatch, new Rectangle(x, y, width, height), Color.Black * 0.7f, true);
            GUIStyle.Font.DrawString(spriteBatch, "Network statistics:", new Vector2(x + 10, y + 10), Color.White);

            if (client.ServerConnection != null)
            {
                GUIStyle.Font.DrawString(spriteBatch, "Ping: " + (int)(client.ServerConnection.AverageRoundtripTime * 1000.0f) + " ms", new Vector2(x + 10, y + 25), Color.White);

                y += 15;

                GUIStyle.SmallFont.DrawString(spriteBatch, "Received bytes: " + client.Statistics.ReceivedBytes, new Vector2(x + 10, y + 45), Color.White);
                GUIStyle.SmallFont.DrawString(spriteBatch, "Received packets: " + client.Statistics.ReceivedPackets, new Vector2(x + 10, y + 60), Color.White);

                GUIStyle.SmallFont.DrawString(spriteBatch, "Sent bytes: " + client.Statistics.SentBytes, new Vector2(x + 10, y + 75), Color.White);
                GUIStyle.SmallFont.DrawString(spriteBatch, "Sent packets: " + client.Statistics.SentPackets, new Vector2(x + 10, y + 90), Color.White);
            }
            else
            {
                GUIStyle.Font.DrawString(spriteBatch, "Disconnected", new Vector2(x + 10, y + 25), Color.White);
            }*/
        }

        public bool SelectCrewCharacter(Character character, GUIComponent frame)
        {
            if (character == null) { return false; }

            if (character != myCharacter)
            {
                var client = previouslyConnectedClients.Find(c => c.Character == character);
                if (client == null) { return false; }

                CreateSelectionRelatedButtons(client, frame);
            }

            return true;
        }

        public bool SelectCrewClient(Client client, GUIComponent frame)
        {
            if (client == null || client.SessionId == SessionId) { return false; }
            CreateSelectionRelatedButtons(client, frame);
            return true;
        }

        private void CreateSelectionRelatedButtons(Client client, GUIComponent frame)
        {
            var content = new GUILayoutGroup(new RectTransform(new Vector2(1f, 1.0f - frame.RectTransform.RelativeSize.Y), frame.RectTransform, Anchor.BottomCenter, Pivot.TopCenter), childAnchor: Anchor.TopCenter);

            var mute = new GUITickBox(new RectTransform(new Vector2(1.0f, 0.2f), content.RectTransform, Anchor.TopCenter),
                TextManager.Get("Mute"))
            {
                Selected = client.MutedLocally,
                OnSelected = (tickBox) => { client.MutedLocally = tickBox.Selected; return true; }
            };
            
            var volumeLayout = new GUILayoutGroup(new RectTransform(new Vector2(1.0f, 0.45f), content.RectTransform, Anchor.TopCenter), isHorizontal: false);

            var volumeTextLayout = new GUILayoutGroup(new RectTransform(new Vector2(1f, 0.5f), volumeLayout.RectTransform), isHorizontal: true, childAnchor: Anchor.CenterLeft);
            var label = new GUITextBlock(new RectTransform(new Vector2(0.6f, 1f), volumeTextLayout.RectTransform), TextManager.Get("VoiceChatVolume"));
            var percentageText = new GUITextBlock(new RectTransform(new Vector2(0.4f, 1f), volumeTextLayout.RectTransform), ToolBox.GetFormattedPercentage(client.VoiceVolume), textAlignment: Alignment.Right);

            var volumeSlider = new GUIScrollBar(new RectTransform(new Vector2(1f, 0.5f), volumeLayout.RectTransform), barSize: 0.1f, style: "GUISlider")
            {
                Range = new Vector2(0f, 1f),
                BarScroll = client.VoiceVolume / Client.MaxVoiceChatBoost,
                OnMoved = (_, barScroll) =>
                {
                    float newVolume = barScroll * Client.MaxVoiceChatBoost;

                    client.VoiceVolume = newVolume;
                    percentageText.Text = ToolBox.GetFormattedPercentage(newVolume);
                    return true;
                }
            };

            var buttonContainer = new GUILayoutGroup(new RectTransform(new Vector2(1.0f, 0.35f), content.RectTransform), isHorizontal: true, childAnchor: Anchor.BottomLeft)
            {
                RelativeSpacing = 0.05f,
                Stretch = true
            };

            if (!GameMain.Client.GameStarted || (GameMain.Client.Character == null || GameMain.Client.Character.IsDead) && (client.Character == null || client.Character.IsDead))
            {
                var messageButton = new GUIButton(new RectTransform(new Vector2(1f, 0.2f), content.RectTransform, Anchor.BottomCenter) { RelativeOffset = new Vector2(0f, buttonContainer.RectTransform.RelativeSize.Y) },
                    TextManager.Get("message"), style: "GUIButtonSmall")
                {
                    UserData = client,
                    OnClicked = (btn, userdata) =>
                    {
                        chatBox.InputBox.Text = $"{client.Name}; ";
                        CoroutineManager.StartCoroutine(selectCoroutine());
                        return false;
                    }
                };
            }

            // Need a delayed selection due to the inputbox being deselected when a left click occurs outside of it
            IEnumerable<CoroutineStatus> selectCoroutine()
            {
                yield return new WaitForSeconds(0.01f, true);
                chatBox.InputBox.Select(chatBox.InputBox.Text.Length);
            }

            if (HasPermission(ClientPermissions.Ban) && client.AllowKicking)
            {
                var banButton = new GUIButton(new RectTransform(new Vector2(0.45f, 0.9f), buttonContainer.RectTransform),
                    TextManager.Get("Ban"), style: "GUIButtonSmall")
                {
                    UserData = client,
                    OnClicked = (btn, userdata) => { NetLobbyScreen.BanPlayer(client); return false; }
                };
            }
            if (HasPermission(ClientPermissions.Kick) && client.AllowKicking)
            {
                var kickButton = new GUIButton(new RectTransform(new Vector2(0.45f, 0.9f), buttonContainer.RectTransform),
                    TextManager.Get("Kick"), style: "GUIButtonSmall")
                {
                    UserData = client,
                    OnClicked = (btn, userdata) => { NetLobbyScreen.KickPlayer(client); return false; }
                };
            }
            else if (ServerSettings.AllowVoteKick && client.AllowKicking)
            {
                var kickVoteButton = new GUIButton(new RectTransform(new Vector2(0.45f, 0.9f), buttonContainer.RectTransform),
                    TextManager.Get("VoteToKick"), style: "GUIButtonSmall")
                {
                    UserData = client,
                    OnClicked = (btn, userdata) => { VoteForKick(client); btn.Enabled = false; return true; }
                };
            }
        }

        public void CreateKickReasonPrompt(string clientName, bool ban)
        {
            var banReasonPrompt = new GUIMessageBox(
                TextManager.Get(ban ? "BanReasonPrompt" : "KickReasonPrompt"),
                "", new LocalizedString[] { TextManager.Get("OK"), TextManager.Get("Cancel") }, new Vector2(0.25f, 0.25f), new Point(400, 260));

            var content = new GUILayoutGroup(new RectTransform(new Vector2(0.9f, 0.6f), banReasonPrompt.InnerFrame.RectTransform, Anchor.Center))
            {
                AbsoluteSpacing = GUI.IntScale(5)
            };
            var banReasonBox = new GUITextBox(new RectTransform(new Vector2(1.0f, 0.3f), content.RectTransform))
            {
                Wrap = true,
                MaxTextLength = 100
            };

            GUINumberInput durationInputDays = null, durationInputHours = null;
            GUITickBox permaBanTickBox = null;

            if (ban)
            {                
                var labelContainer = new GUILayoutGroup(new RectTransform(new Vector2(1f, 0.25f), content.RectTransform), isHorizontal: false);
                new GUITextBlock(new RectTransform(new Vector2(1f, 0.0f), labelContainer.RectTransform), TextManager.Get("BanDuration"), font: GUIStyle.SubHeadingFont) { Padding = Vector4.Zero };
                var buttonContent = new GUILayoutGroup(new RectTransform(new Vector2(1f, 0.5f), labelContainer.RectTransform), isHorizontal: true);
                permaBanTickBox = new GUITickBox(new RectTransform(new Vector2(0.4f, 0.15f), buttonContent.RectTransform), TextManager.Get("BanPermanent"))
                {
                    Selected = true
                };

                var durationContainer = new GUILayoutGroup(new RectTransform(new Vector2(0.8f, 1f), buttonContent.RectTransform), isHorizontal: true)
                {
                    Visible = false
                };

                permaBanTickBox.OnSelected += (tickBox) =>
                {
                    durationContainer.Visible = !tickBox.Selected;
                    return true;
                };

                durationInputDays = new GUINumberInput(new RectTransform(new Vector2(0.2f, 1.0f), durationContainer.RectTransform), NumberType.Int)
                {
                    MinValueInt = 0,
                    MaxValueFloat = 1000
                };
                new GUITextBlock(new RectTransform(new Vector2(0.2f, 1.0f), durationContainer.RectTransform), TextManager.Get("Days"));
                durationInputHours = new GUINumberInput(new RectTransform(new Vector2(0.2f, 1.0f), durationContainer.RectTransform), NumberType.Int)
                {
                    MinValueInt = 0,
                    MaxValueFloat = 24
                };
                new GUITextBlock(new RectTransform(new Vector2(0.2f, 1.0f), durationContainer.RectTransform), TextManager.Get("Hours"));
            }

            banReasonPrompt.Buttons[0].OnClicked += (btn, userData) =>
            {
                if (ban)
                {
                    if (!permaBanTickBox.Selected)
                    {
                        TimeSpan banDuration = new TimeSpan(durationInputDays.IntValue, durationInputHours.IntValue, 0, 0);
                        BanPlayer(clientName, banReasonBox.Text, banDuration);
                    }
                    else
                    {
                        BanPlayer(clientName, banReasonBox.Text);
                    }
                }
                else
                {
                    KickPlayer(clientName, banReasonBox.Text);
                }
                return true;
            };
            banReasonPrompt.Buttons[0].OnClicked += banReasonPrompt.Close;
            banReasonPrompt.Buttons[1].OnClicked += banReasonPrompt.Close;
        }

        public void ReportError(ClientNetError error, UInt16 expectedId = 0, UInt16 eventId = 0, UInt16 entityId = 0)
        {
            IWriteMessage outMsg = new WriteOnlyMessage();
            outMsg.WriteByte((byte)ClientPacketHeader.ERROR);
            outMsg.WriteByte((byte)error);
            switch (error)
            {
                case ClientNetError.MISSING_EVENT:
                    outMsg.WriteUInt16(expectedId);
                    outMsg.WriteUInt16(eventId);
                    break;
                case ClientNetError.MISSING_ENTITY:
                    outMsg.WriteUInt16(eventId);
                    outMsg.WriteUInt16(entityId);
                    outMsg.WriteByte((byte)Submarine.Loaded.Count);
                    foreach (Submarine sub in Submarine.Loaded)
                    {
                        outMsg.WriteString(sub.Info.Name);
                    }
                    break;
            }
            ClientPeer.Send(outMsg, DeliveryMethod.Reliable);

            WriteEventErrorData(error, expectedId, eventId, entityId);
        }

        private bool eventErrorWritten;
        private void WriteEventErrorData(ClientNetError error, UInt16 expectedID, UInt16 eventID, UInt16 entityID)
        {
            if (eventErrorWritten) { return; }
            List<string> errorLines = new List<string>
            {
                error.ToString(), ""
            };

            if (IsServerOwner)
            {
                errorLines.Add("SERVER OWNER");
            }

            if (error == ClientNetError.MISSING_EVENT)
            {
                errorLines.Add("Expected ID: " + expectedID + ", received " + eventID);
            }
            else if (error == ClientNetError.MISSING_ENTITY)
            {
                errorLines.Add("Event ID: " + eventID + ", entity ID " + entityID);
            }

            if (GameMain.GameSession?.GameMode != null)
            {
                errorLines.Add("Game mode: " + GameMain.GameSession.GameMode.Name.Value);
                if (GameMain.GameSession?.GameMode is MultiPlayerCampaign campaign)
                {
                    errorLines.Add("Campaign ID: " + campaign.CampaignID);
                    errorLines.Add("Campaign save ID: " + campaign.LastSaveID + "(pending: " + campaign.PendingSaveID + ")");
                }
                foreach (Mission mission in GameMain.GameSession.Missions)
                {
                    errorLines.Add("Mission: " + mission.Prefab.Identifier);
                }
            }
            if (GameMain.GameSession?.Submarine != null)
            {
                errorLines.Add("Submarine: " + GameMain.GameSession.Submarine.Info.Name);
            }
            if (GameMain.NetworkMember?.RespawnManager is { } respawnManager)
            {
                errorLines.Add("Respawn shuttles: " + string.Join(", ", respawnManager.RespawnShuttles.Select(s => s.Info.Name)));
            }
            if (Level.Loaded != null)
            {
                errorLines.Add("Level: " + Level.Loaded.Seed + ", "
                               + string.Join("; ", Level.Loaded.EqualityCheckValues.Select(cv
                                   => cv.Key + "=" + cv.Value.ToString("X"))));
                errorLines.Add("Entity count before generating level: " + Level.Loaded.EntityCountBeforeGenerate);
                errorLines.Add("Entities:");
                foreach (Entity e in Level.Loaded.EntitiesBeforeGenerate.OrderBy(e => e.CreationIndex))
                {
                    errorLines.Add(e.ErrorLine);
                }
                errorLines.Add("Entity count after generating level: " + Level.Loaded.EntityCountAfterGenerate);
            }

            errorLines.Add("Entity IDs:");
            Entity[] sortedEntities = Entity.GetEntities().OrderBy(e => e.CreationIndex).ToArray();
            foreach (Entity e in sortedEntities)
            {
                errorLines.Add(e.ErrorLine);
            }

            if (Entity.Spawner != null)
            {
                errorLines.Add("");
                errorLines.Add("EntitySpawner events:");
                foreach ((Entity entity, bool isRemoval) in Entity.Spawner.receivedEvents)
                {
                    errorLines.Add(
                        (isRemoval ? "Remove " : "Create ") +
                        entity.ToString() +
                        " (" + entity.ID + ")");
                }
            }

            errorLines.Add("");
            errorLines.Add("Last debug messages:");
            for (int i = DebugConsole.Messages.Count - 1; i > 0 && i > DebugConsole.Messages.Count - 15; i--)
            {
                errorLines.Add("   " + DebugConsole.Messages[i].Time + " - " + DebugConsole.Messages[i].Text);
            }

            string filePath = $"event_error_log_client_{Name}_{DateTime.UtcNow.ToShortTimeString()}.log";
            filePath = Path.Combine(ServerLog.SavePath, ToolBox.RemoveInvalidFileNameChars(filePath));

            if (!Directory.Exists(ServerLog.SavePath))
            {
                Directory.CreateDirectory(ServerLog.SavePath);
            }
            File.WriteAllLines(filePath, errorLines);

            eventErrorWritten = true;
        }

        private static void AppendExceptionInfo(ref string errorMsg, out Entity causingEntity, Exception e)
        {
            if (!errorMsg.EndsWith("\n")) { errorMsg += "\n"; }

            Exception innerMostException = e.GetInnermost();
            causingEntity = GetCausingEntity(e);

            if (causingEntity != null)
            {
                errorMsg += "Entity: " + causingEntity + "\n";
            }
            errorMsg += e.Message + "\n";

            if (innerMostException != e)
            {
                // If available, only append the stacktrace of the innermost exception,
                // because that's the most important one to fix
                errorMsg += "Inner exception: " + innerMostException.Message + "\n" + innerMostException.StackTrace.CleanupStackTrace();
            }
            else
            {
                errorMsg += e.StackTrace.CleanupStackTrace();
            }
        }

        /// <summary>
        /// Checks if the exception or any of its inner exceptions are EntityEventExceptions, and returns the entity that caused the innermost EntityEventException.
        /// </summary>
        private static Entity GetCausingEntity(Exception e)
        {
            Entity causingEntity = null;
            Exception currentException = e;
            while (currentException != null)
            {
                if (currentException is EntityEventException entityEventException)
                {
                    causingEntity = entityEventException.Entity;
                }
                currentException = currentException.InnerException;
            }
            return causingEntity;
        }

#if DEBUG
        public void ForceTimeOut()
        {
            ClientPeer?.ForceTimeOut();
        }
#endif
    }
}

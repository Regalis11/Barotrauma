using Barotrauma.Extensions;
using Barotrauma.IO;
using Barotrauma.Items.Components;
using Barotrauma.Steam;
using Lidgren.Network;
using Microsoft.Xna.Framework;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Xml.Linq;
using Barotrauma.PerkBehaviors;

namespace Barotrauma.Networking
{
    sealed class GameServer : NetworkMember
    {
        public override bool IsServer => true;
        public override bool IsClient => false;

        public override Voting Voting { get; }

        public string ServerName
        {
            get { return ServerSettings.ServerName; }
            set
            {
                if (string.IsNullOrEmpty(value)) { return; }
                ServerSettings.ServerName = value;
            }
        }

        public bool SubmarineSwitchLoad = false;

        private readonly List<Client> connectedClients = new List<Client>();

        /// <summary>
        /// For keeping track of disconnected clients in case they reconnect shortly after.
        /// </summary>
        private readonly List<Client> clientsAttemptingToReconnectSoon = new List<Client>();

        //keeps track of players who've previously been playing on the server
        //so kick votes persist during the session and the server can let the clients know what name this client used previously
        private readonly List<PreviousPlayer> previousPlayers = new List<PreviousPlayer>();

        private int roundStartSeed;

        //is the server running
        private bool started;

        private ServerPeer serverPeer;
        public ServerPeer ServerPeer { get { return serverPeer; } }

        private DateTime refreshMasterTimer;
        private readonly TimeSpan refreshMasterInterval = new TimeSpan(0, 0, 60);
        private bool registeredToSteamMaster;

        private DateTime roundStartTime;

        private bool wasReadyToStartAutomatically;
        private bool autoRestartTimerRunning;
        public float EndRoundTimer { get; private set; }
        public float EndRoundDelay { get; private set; }

        public float EndRoundTimeRemaining => EndRoundTimer > 0 ? EndRoundDelay - EndRoundTimer : 0;

        private const int PvpAutoBalanceCountdown = 10;
        private static float pvpAutoBalanceCountdownRemaining = -1;
        private int Team1Count => GetPlayingClients().Count(static c => c.TeamID == CharacterTeamType.Team1);
        private int Team2Count => GetPlayingClients().Count(static c => c.TeamID == CharacterTeamType.Team2);

        /// <summary>
        /// Chat messages that get sent to the owner of the server when the owner is determined
        /// </summary>
        private static readonly Queue<ChatMessage> pendingMessagesToOwner = new Queue<ChatMessage>();

        public VoipServer VoipServer
        {
            get;
            private set;
        }

        private bool initiatedStartGame;
        private CoroutineHandle startGameCoroutine;

        private readonly ServerEntityEventManager entityEventManager;

        public FileSender FileSender { get; private set; }

        public ModSender ModSender { get; private set; }

        private TraitorManager traitorManager;
        public TraitorManager TraitorManager
        {
            get 
            {
                traitorManager ??= new TraitorManager(this);
                return traitorManager; 
            }
        }
        
#if DEBUG
        public void PrintSenderTransters()
        {
            foreach (var transfer in FileSender.ActiveTransfers)
            {
                DebugConsole.NewMessage(transfer.FileName + " " + transfer.Progress.ToString());
            }
        }
#endif

        public override IReadOnlyList<Client> ConnectedClients
        {
            get
            {
                return connectedClients;
            }
        }


        public ServerEntityEventManager EntityEventManager
        {
            get { return entityEventManager; }
        }

        public int Port => ServerSettings?.Port ?? 0;

        //only used when connected to steam
        public int QueryPort => ServerSettings?.QueryPort ?? 0;

        public NetworkConnection OwnerConnection { get; private set; }
        private readonly Option<int> ownerKey;
        private readonly Option<P2PEndpoint> ownerEndpoint;

        public void ClearRecentlyDisconnectedClients()
        {
            lock (clientsAttemptingToReconnectSoon)
            {
                clientsAttemptingToReconnectSoon.Clear();
            }
        }

        public bool FindAndRemoveRecentlyDisconnectedConnection(NetworkConnection conn)
        {
            lock (clientsAttemptingToReconnectSoon)
            {
                Client found = null;
                foreach (var client in clientsAttemptingToReconnectSoon)
                {
                    if (conn.AddressMatches(client.Connection))
                    {
                        found = client;
                        break;
                    }
                }

                if (found is not null)
                {
                    clientsAttemptingToReconnectSoon.Remove(found);
                    return true;
                }
            }

            return false;
        }

        public GameServer(
            string name,
            int port,
            int queryPort,
            bool isPublic,
            string password,
            bool attemptUPnP,
            int maxPlayers,
            Option<int> ownerKey,
            Option<P2PEndpoint> ownerEndpoint)
        {
            if (name.Length > NetConfig.ServerNameMaxLength)
            {
                name = name.Substring(0, NetConfig.ServerNameMaxLength);
            }

            LastClientListUpdateID = 0;

            ServerSettings = new ServerSettings(this, name, port, queryPort, maxPlayers, isPublic, attemptUPnP);
            KarmaManager.SelectPreset(ServerSettings.KarmaPreset);
            ServerSettings.SetPassword(password);
            ServerSettings.SaveSettings();

            Voting = new Voting();

            this.ownerKey = ownerKey;

            this.ownerEndpoint = ownerEndpoint;

            entityEventManager = new ServerEntityEventManager(this);
        }

        public void StartServer(bool registerToServerList)
        {
            Log("Starting the server...", ServerLog.MessageType.ServerMessage);

            var callbacks = new ServerPeer.Callbacks(
                ReadDataMessage,
                OnClientDisconnect,
                OnInitializationComplete,
                GameMain.Instance.CloseServer,
                OnOwnerDetermined);

            if (ownerEndpoint.TryUnwrap(out var endpoint))
            {
                Log("Using P2P networking.", ServerLog.MessageType.ServerMessage);
                serverPeer = new P2PServerPeer(endpoint, ownerKey.Fallback(0), ServerSettings, callbacks);
            }
            else
            {
                Log("Using Lidgren networking. Manual port forwarding may be required. If players cannot connect to the server, you may want to use the in-game hosting menu (which uses Steamworks and EOS networking and does not require port forwarding).", ServerLog.MessageType.ServerMessage);
                serverPeer = new LidgrenServerPeer(ownerKey, ServerSettings, callbacks);
                if (registerToServerList)
                {
                    registeredToSteamMaster = SteamManager.CreateServer(this, ServerSettings.IsPublic);
                    Eos.EosSessionManager.UpdateOwnedSession(Option.None, ServerSettings);
                }
            }

            FileSender = new FileSender(serverPeer, MsgConstants.MTU);
            FileSender.OnEnded += FileTransferChanged;
            FileSender.OnStarted += FileTransferChanged;

            if (ServerSettings.AllowModDownloads) { ModSender = new ModSender(); }

            serverPeer.Start();

            VoipServer = new VoipServer(serverPeer);

            Log("Server started", ServerLog.MessageType.ServerMessage);

            GameMain.NetLobbyScreen.Select();
            GameMain.NetLobbyScreen.RandomizeSettings();
            if (!string.IsNullOrEmpty(ServerSettings.SelectedSubmarine))
            {
                SubmarineInfo sub = SubmarineInfo.SavedSubmarines.FirstOrDefault(s => s.Name == ServerSettings.SelectedSubmarine);
                if (sub != null) { GameMain.NetLobbyScreen.SelectedSub = sub; }
            }
            if (!string.IsNullOrEmpty(ServerSettings.SelectedShuttle))
            {
                SubmarineInfo shuttle = SubmarineInfo.SavedSubmarines.FirstOrDefault(s => s.Name == ServerSettings.SelectedShuttle);
                if (shuttle != null) { GameMain.NetLobbyScreen.SelectedShuttle = shuttle; }
            }

            started = true;

            GameAnalyticsManager.AddDesignEvent("GameServer:Start");
        }


        /// <summary>
        /// Creates a message that gets sent to the server owner once the connection is initialized. Can be used to for example notify the owner of problems during initialization
        /// </summary>
        public static void AddPendingMessageToOwner(string message, ChatMessageType messageType)
        {
            pendingMessagesToOwner.Enqueue(ChatMessage.Create(string.Empty, message, messageType, sender: null));
        }

        private void OnOwnerDetermined(NetworkConnection connection)
        {
            OwnerConnection = connection;

            var ownerClient = ConnectedClients.Find(c => c.Connection == connection);
            if (ownerClient == null)
            {
                DebugConsole.ThrowError("Owner client not found! Can't set permissions");
                return;
            }
            ownerClient.SetPermissions(ClientPermissions.All, DebugConsole.Commands);
            UpdateClientPermissions(ownerClient);
        }

        public void NotifyCrash()
        {
            var tempList = ConnectedClients.Where(c => c.Connection != OwnerConnection).ToList();
            foreach (var c in tempList)
            {
                DisconnectClient(c.Connection, PeerDisconnectPacket.WithReason(DisconnectReason.ServerCrashed));
            }
            if (OwnerConnection != null)
            {
                var conn = OwnerConnection; OwnerConnection = null;
                DisconnectClient(conn, PeerDisconnectPacket.WithReason(DisconnectReason.ServerCrashed));
            }
            Thread.Sleep(500);
        }

        private void OnInitializationComplete(NetworkConnection connection, string clientName)
        {
            clientName = Client.SanitizeName(clientName);
            Client newClient = new Client(clientName, GetNewClientSessionId());
            newClient.InitClientSync();
            newClient.Connection = connection;
            newClient.Connection.Status = NetworkConnectionStatus.Connected;
            newClient.AccountInfo = connection.AccountInfo;
            newClient.Language = connection.Language;
            connectedClients.Add(newClient);

            var previousPlayer = previousPlayers.Find(p => p.MatchesClient(newClient));
            if (previousPlayer != null)
            {
                newClient.Karma = previousPlayer.Karma;
                newClient.KarmaKickCount = previousPlayer.KarmaKickCount;
                foreach (Client c in previousPlayer.KickVoters)
                {
                    if (!connectedClients.Contains(c)) { continue; }
                    newClient.AddKickVote(c);
                }
            }

            LastClientListUpdateID++;

            if (newClient.Connection == OwnerConnection && OwnerConnection != null)
            {
                newClient.GivePermission(ClientPermissions.All);
                foreach (var command in DebugConsole.Commands)
                {
                    newClient.PermittedConsoleCommands.Add(command);
                }
                SendConsoleMessage("Granted all permissions to " + newClient.Name + ".", newClient);
            }

            SendChatMessage($"ServerMessage.JoinedServer~[client]={ClientLogName(newClient)}", ChatMessageType.Server, changeType: PlayerConnectionChangeType.Joined);
            ServerSettings.ServerDetailsChanged = true;

            if (previousPlayer != null && previousPlayer.Name != newClient.Name)
            {
                string prevNameSanitized = previousPlayer.Name.Replace("‖", "");
                SendChatMessage($"ServerMessage.PreviousClientName~[client]={ClientLogName(newClient)}~[previousname]={prevNameSanitized}", ChatMessageType.Server);
                previousPlayer.Name = newClient.Name;
            }
            if (!ServerSettings.ServerMessageText.IsNullOrEmpty())
            {
                SendDirectChatMessage((TextManager.Get("servermotd") + '\n' + ServerSettings.ServerMessageText).Value, newClient, ChatMessageType.Server);
            }

            var savedPermissions = ServerSettings.ClientPermissions.Find(scp =>
                scp.AddressOrAccountId.TryGet(out AccountId accountId)
                    ? newClient.AccountId.ValueEquals(accountId)
                    : newClient.Connection.Endpoint.Address == scp.AddressOrAccountId);

            if (savedPermissions != null)
            {
                newClient.SetPermissions(savedPermissions.Permissions, savedPermissions.PermittedCommands);
            }
            else
            {
                var defaultPerms = PermissionPreset.List.Find(p => p.Identifier == "None");
                if (defaultPerms != null)
                {
                    newClient.SetPermissions(defaultPerms.Permissions, defaultPerms.PermittedCommands);
                }
                else
                {
                    newClient.SetPermissions(ClientPermissions.None, Enumerable.Empty<DebugConsole.Command>());
                }
            }

            UpdateClientPermissions(newClient);
            //notify the client of everyone else's permissions
            foreach (Client otherClient in connectedClients)
            {
                if (otherClient == newClient) { continue; }
                CoroutineManager.StartCoroutine(SendClientPermissionsAfterClientListSynced(newClient, otherClient));
            }
        }

        private void OnClientDisconnect(NetworkConnection connection, PeerDisconnectPacket peerDisconnectPacket)
        {
            Client connectedClient = connectedClients.Find(c => c.Connection == connection);

            DisconnectClient(connectedClient, peerDisconnectPacket);
        }

        public void Update(float deltaTime)
        {
            dosProtection.Update(deltaTime);

            if (!started) { return; }

            if (ChildServerRelay.HasShutDown)
            {
                GameMain.Instance.CloseServer();
                return;
            }

            FileSender.Update(deltaTime);
            KarmaManager.UpdateClients(ConnectedClients, deltaTime);

            UpdatePing();

            if (ServerSettings.VoiceChatEnabled)
            {
                VoipServer.SendToClients(connectedClients);
                foreach (var c in connectedClients)
                {
                    c.VoipServerDecoder.DebugUpdate(deltaTime);
                }
            }

            if (GameStarted)
            {
                RespawnManager?.Update(deltaTime);

                entityEventManager.Update(connectedClients);
                bool permadeathMode = ServerSettings.RespawnMode == RespawnMode.Permadeath;

                //go through the characters backwards to give rejoining clients control of the latest created character
                for (int i = Character.CharacterList.Count - 1; i >= 0; i--)
                {
                    Character character = Character.CharacterList[i];
                    if (!character.ClientDisconnected) { continue; }

                    Client owner = connectedClients.Find(c => (c.Character == null || c.Character == character) && character.IsClientOwner(c));
                    bool canOwnerTakeControl =
                        owner != null && owner.InGame && !owner.NeedsMidRoundSync &&
                        (!ServerSettings.AllowSpectating || !owner.SpectateOnly ||
                         (permadeathMode && (!character.IsDead || character.CauseOfDeath?.Type == CauseOfDeathType.Disconnected)));
                    if (!character.IsDead)
                    {
                        character.KillDisconnectedTimer += deltaTime;
                        character.SetStun(1.0f);

                        if ((OwnerConnection == null || owner?.Connection != OwnerConnection) &&
                            character.KillDisconnectedTimer > (permadeathMode ? ServerSettings.DespawnDisconnectedPermadeathTime : ServerSettings.KillDisconnectedTime))
                        {
                            character.Kill(CauseOfDeathType.Disconnected, null);
                            continue;
                        }
                        if (canOwnerTakeControl)
                        {
                            SetClientCharacter(owner, character);
                        }
                    }
                    else if (canOwnerTakeControl &&
                        character.CauseOfDeath?.Type == CauseOfDeathType.Disconnected &&
                        character.CharacterHealth.VitalityDisregardingDeath > 0)
                    {
                        //create network event immediately to ensure the character is revived client-side
                        //before the client gains control of it (normally status events are created periodically)                        
                        character.Revive(removeAfflictions: false, createNetworkEvent: true);
                        SetClientCharacter(owner, character);
                    }
                }

                TraitorManager?.Update(deltaTime);

                Voting.Update(deltaTime);

                bool isCrewDown =
                    connectedClients.All(c => !c.UsingFreeCam && (c.Character == null || c.Character.IsDead || c.Character.IsIncapacitated));
                bool isSomeoneIncapacitatedNotDead =
                    connectedClients.Any(c => !c.UsingFreeCam && c.Character is { IsDead: false, IsIncapacitated: true });

                bool subAtLevelEnd = false;
                if (Submarine.MainSub != null && GameMain.GameSession.GameMode is not PvPMode)
                {
                    if (Level.Loaded?.EndOutpost != null)
                    {
                        int charactersInsideOutpost = connectedClients.Count(c =>
                            c.Character != null &&
                            !c.Character.IsDead && !c.Character.IsUnconscious &&
                            c.Character.Submarine == Level.Loaded.EndOutpost);
                        int charactersOutsideOutpost = connectedClients.Count(c =>
                            c.Character != null &&
                            !c.Character.IsDead && !c.Character.IsUnconscious &&
                            c.Character.Submarine != Level.Loaded.EndOutpost);

                        //level finished if the sub is docked to the outpost
                        //or very close and someone from the crew made it inside the outpost
                        subAtLevelEnd =
                            Submarine.MainSub.DockedTo.Contains(Level.Loaded.EndOutpost) ||
                            (Submarine.MainSub.AtEndExit && charactersInsideOutpost > 0) ||
                            (charactersInsideOutpost > charactersOutsideOutpost);
                    }
                    else
                    {
                        subAtLevelEnd = Submarine.MainSub.AtEndExit;
                    }
                }

                EndRoundDelay = 1.0f;
                if (permadeathMode && isCrewDown)
                {
                    if (EndRoundTimer <= 0.0f)
                    {
                        CreateEntityEvent(RespawnManager);
                    }
                    EndRoundDelay = 120.0f;
                    EndRoundTimer += deltaTime;
                }
                else if (ServerSettings.AutoRestart && isCrewDown)
                {
                    EndRoundDelay = isSomeoneIncapacitatedNotDead ? 120.0f : 5.0f;
                    EndRoundTimer += deltaTime;
                }
                else if (subAtLevelEnd && GameMain.GameSession?.GameMode is not CampaignMode)
                {
                    EndRoundDelay = 5.0f;
                    EndRoundTimer += deltaTime;
                }
                else if (isCrewDown && 
                    (RespawnManager == null || (!RespawnManager.CanRespawnAgain(CharacterTeamType.Team1) && !RespawnManager.CanRespawnAgain(CharacterTeamType.Team2))))
                {
#if !DEBUG
                    if (EndRoundTimer <= 0.0f)
                    {
                        SendChatMessage(TextManager.GetWithVariable("CrewDeadNoRespawns", "[time]", "120").Value, ChatMessageType.Server);
                    }
                    EndRoundDelay = 120.0f;
                    EndRoundTimer += deltaTime;
#endif
                }
                else if (isCrewDown && (GameMain.GameSession?.GameMode is CampaignMode))
                {
#if !DEBUG
                    EndRoundDelay = isSomeoneIncapacitatedNotDead ? 120.0f : 2.0f;
                    EndRoundTimer += deltaTime;
#endif
                }
                else
                {
                    EndRoundTimer = 0.0f;
                }

                if (EndRoundTimer >= EndRoundDelay)
                {
                    if (permadeathMode && isCrewDown)
                    {
                        Log("Ending round (entire crew dead or down and did not acquire new characters in time)", ServerLog.MessageType.ServerMessage);
                    }
                    else if (ServerSettings.AutoRestart && isCrewDown)
                    {
                        Log("Ending round (entire crew down)", ServerLog.MessageType.ServerMessage);
                    }
                    else if (subAtLevelEnd)
                    {
                        Log("Ending round (submarine reached the end of the level)", ServerLog.MessageType.ServerMessage);
                    }
                    else if (RespawnManager == null)
                    {
                        Log("Ending round (no players left standing and respawning is not enabled during this round)", ServerLog.MessageType.ServerMessage);
                    }
                    else
                    {
                        Log("Ending round (no players left standing)", ServerLog.MessageType.ServerMessage);
                    }
                    EndGame(wasSaved: false);
                    return;
                }
            }
            else if (initiatedStartGame)
            {
                //tried to start up the game and StartGame coroutine is not running anymore
                // -> something wen't wrong during startup, re-enable start button and reset AutoRestartTimer
                if (startGameCoroutine != null && !CoroutineManager.IsCoroutineRunning(startGameCoroutine))
                {
                    if (ServerSettings.AutoRestart) { ServerSettings.AutoRestartTimer = Math.Max(ServerSettings.AutoRestartInterval, 5.0f); }

                    if (startGameCoroutine.Exception != null && OwnerConnection != null)
                    {
                        SendConsoleMessage(
                            startGameCoroutine.Exception.Message + '\n' +
                            (startGameCoroutine.Exception.StackTrace?.CleanupStackTrace() ?? "null"),
                            connectedClients.Find(c => c.Connection == OwnerConnection),
                            Color.Red);
                    }

                    EndGame();
                    GameMain.NetLobbyScreen.LastUpdateID++;

                    startGameCoroutine = null;
                    initiatedStartGame = false;
                }
            }
            else if (Screen.Selected == GameMain.NetLobbyScreen && !GameStarted && !initiatedStartGame)
            {
                if (ServerSettings.AutoRestart)
                {
                    //autorestart if there are any non-spectators on the server (ignoring the server owner)
                    bool shouldAutoRestart = connectedClients.Any(c =>
                        c.Connection != OwnerConnection &&
                        (!c.SpectateOnly || !ServerSettings.AllowSpectating));

                    if (shouldAutoRestart != autoRestartTimerRunning)
                    {
                        autoRestartTimerRunning = shouldAutoRestart;
                        GameMain.NetLobbyScreen.LastUpdateID++;
                    }

                    if (autoRestartTimerRunning)
                    {
                        ServerSettings.AutoRestartTimer -= deltaTime;
                    }
                }

                bool readyToStartAutomatically = false;
                if (ServerSettings.AutoRestart && autoRestartTimerRunning && ServerSettings.AutoRestartTimer < 0.0f)
                {
                    readyToStartAutomatically = true;
                }
                else if (ServerSettings.StartWhenClientsReady)
                {
                    int clientsReady = connectedClients.Count(c => c.GetVote<bool>(VoteType.StartRound));
                    if (clientsReady / (float)connectedClients.Count >= ServerSettings.StartWhenClientsReadyRatio)
                    {
                        readyToStartAutomatically = true;
                    }
                }
                if (readyToStartAutomatically && !isRoundStartWarningActive)
                {
                    if (!wasReadyToStartAutomatically) { GameMain.NetLobbyScreen.LastUpdateID++; }
                    TryStartGame();
                }
                wasReadyToStartAutomatically = readyToStartAutomatically;
            }

            lock (clientsAttemptingToReconnectSoon)
            {
                foreach (var client in clientsAttemptingToReconnectSoon)
                {
                    client.DeleteDisconnectedTimer -= deltaTime;
                }

                clientsAttemptingToReconnectSoon.RemoveAll(static c => c.DeleteDisconnectedTimer < 0f);
            }

            foreach (Client c in connectedClients)
            {
                //slowly reset spam timers
                c.ChatSpamTimer = Math.Max(0.0f, c.ChatSpamTimer - deltaTime);
                c.ChatSpamSpeed = Math.Max(0.0f, c.ChatSpamSpeed - deltaTime);

                //constantly increase AFK timer if the client is controlling a character (gets reset to zero every time an input is received)
                if (GameStarted && c.Character != null && !c.Character.IsDead && !c.Character.IsIncapacitated)
                {
                    if (c.Connection != OwnerConnection && c.Permissions != ClientPermissions.All) { c.KickAFKTimer += deltaTime; }
                }
            }

            if (pvpAutoBalanceCountdownRemaining > 0)
            {
                if (GameStarted || initiatedStartGame || Screen.Selected != GameMain.NetLobbyScreen ||
                    ServerSettings.PvpTeamSelectionMode == PvpTeamSelectionMode.PlayerPreference || ServerSettings.PvpAutoBalanceThreshold == 0)
                {
                    StopAutoBalanceCountdown();
                }
                else
                {
                    float prevTimeRemaining = pvpAutoBalanceCountdownRemaining;
                    pvpAutoBalanceCountdownRemaining -= deltaTime;
                    if (pvpAutoBalanceCountdownRemaining <= 0)
                    {
                        pvpAutoBalanceCountdownRemaining = -1;
                        RefreshPvpTeamAssignments(autoBalanceNow: true);
                    }
                    else
                    {
                        // Send a chat message about the countdown every 5 seconds the countdown is running, but not when
                        // it (=its integer part, which gets printed out) is still at the starting value, or zero
                        int currentTimeRemainingInteger = (int)Math.Ceiling(pvpAutoBalanceCountdownRemaining);
                        if (Math.Ceiling(prevTimeRemaining) > currentTimeRemainingInteger && currentTimeRemainingInteger % 5 == 0)
                        {
                            SendChatMessage(
                                TextManager.GetWithVariable("AutoBalance.CountdownRemaining", "[number]", currentTimeRemainingInteger.ToString()).Value,
                                ChatMessageType.Server);
                        }
                    }    
                }
            }

            if (connectedClients.Any(c => c.KickAFKTimer >= ServerSettings.KickAFKTime))
            {
                IEnumerable<Client> kickAFK = connectedClients.FindAll(c =>
                    c.KickAFKTimer >= ServerSettings.KickAFKTime &&
                    (OwnerConnection == null || c.Connection != OwnerConnection));
                foreach (Client c in kickAFK)
                {
                    KickClient(c, "DisconnectMessage.AFK");
                }
            }

            serverPeer.Update(deltaTime);

            //don't run the rest of the method if something in serverPeer.Update causes the server to shutdown
            if (!started) { return; }

            // if update interval has passed
            if (updateTimer < DateTime.Now)
            {
                if (ConnectedClients.Count > 0)
                {
                    foreach (Client c in ConnectedClients)
                    {
                        try
                        {
                            ClientWrite(c);
                        }
                        catch (Exception e)
                        {
                            DebugConsole.ThrowError("Failed to write a network message for the client \"" + c.Name + "\"!", e);

                            string errorMsg = "Failed to write a network message for a client! (MidRoundSyncing: " + c.NeedsMidRoundSync + ")\n"
                                + e.Message + "\n" + e.StackTrace.CleanupStackTrace();
                            if (e.InnerException != null)
                            {
                                errorMsg += "\nInner exception: " + e.InnerException.Message + "\n" + e.InnerException.StackTrace.CleanupStackTrace();
                            }

                            GameAnalyticsManager.AddErrorEventOnce(
                                "GameServer.Update:ClientWriteFailed" + e.StackTrace.CleanupStackTrace(),
                                GameAnalyticsManager.ErrorSeverity.Error,
                                errorMsg);
                        }
                    }

                    foreach (Character character in Character.CharacterList)
                    {
                        if (character.healthUpdateTimer <= 0.0f)
                        {
                            if (!character.HealthUpdatePending)
                            {
                                character.healthUpdateTimer = character.HealthUpdateInterval;
                            }
                            character.HealthUpdatePending = true;
                        }
                        else
                        {
                            character.healthUpdateTimer -= (float)UpdateInterval.TotalSeconds;
                        }
                        character.HealthUpdateInterval += (float)UpdateInterval.TotalSeconds;
                    }
                }

                updateTimer = DateTime.Now + UpdateInterval;
            }

            if (DateTime.Now > refreshMasterTimer || ServerSettings.ServerDetailsChanged)
            {
                if (registeredToSteamMaster)
                {
                    bool refreshSuccessful = SteamManager.RefreshServerDetails(this);
                    if (GameSettings.CurrentConfig.VerboseLogging)
                    {
                        Log(refreshSuccessful ?
                            "Refreshed server info on the Steam server list." :
                            "Refreshing server info on the Steam server list failed.", ServerLog.MessageType.ServerMessage);
                    }
                }

                Eos.EosSessionManager.UpdateOwnedSession(Option.None, ServerSettings);

                ServerSettings.ServerDetailsChanged = false;
                refreshMasterTimer = DateTime.Now + refreshMasterInterval;
            }
        }


        private double lastPingTime;
        private byte[] lastPingData;
        private void UpdatePing()
        {
            if (Timing.TotalTime > lastPingTime + 1.0)
            {
                lastPingData ??= new byte[64];
                for (int i = 0; i < lastPingData.Length; i++)
                {
                    lastPingData[i] = (byte)Rand.Range(33, 126);
                }
                lastPingTime = Timing.TotalTime;

                ConnectedClients.ForEach(c =>
                {
                    IWriteMessage pingReq = new WriteOnlyMessage();
                    pingReq.WriteByte((byte)ServerPacketHeader.PING_REQUEST);
                    pingReq.WriteByte((byte)lastPingData.Length);
                    pingReq.WriteBytes(lastPingData, 0, lastPingData.Length);
                    serverPeer.Send(pingReq, c.Connection, DeliveryMethod.Unreliable);

                    IWriteMessage pingInf = new WriteOnlyMessage();
                    pingInf.WriteByte((byte)ServerPacketHeader.CLIENT_PINGS);
                    pingInf.WriteByte((byte)ConnectedClients.Count);
                    ConnectedClients.ForEach(c2 =>
                    {
                        pingInf.WriteByte(c2.SessionId);
                        pingInf.WriteUInt16(c2.Ping);
                    });
                    serverPeer.Send(pingInf, c.Connection, DeliveryMethod.Unreliable);
                });
            }
        }

        private readonly DoSProtection dosProtection = new();

        private void ReadDataMessage(NetworkConnection sender, IReadMessage inc)
        {
            var connectedClient = connectedClients.Find(c => c.Connection == sender);

            using var _ = dosProtection.Start(connectedClient);

            ClientPacketHeader header = (ClientPacketHeader)inc.ReadByte();
            switch (header)
            {
                case ClientPacketHeader.PING_RESPONSE:
                    byte responseLen = inc.ReadByte();
                    if (responseLen != lastPingData.Length) { return; }
                    for (int i = 0; i < responseLen; i++)
                    {
                        byte b = inc.ReadByte();
                        if (b != lastPingData[i]) { return; }
                    }
                    connectedClient.Ping = (UInt16)((Timing.TotalTime - lastPingTime) * 1000);
                    break;
                case ClientPacketHeader.RESPONSE_STARTGAME:
                    if (connectedClient != null)
                    {
                        connectedClient.ReadyToStart = inc.ReadBoolean();
                        UpdateCharacterInfo(inc, connectedClient);

                        //game already started -> send start message immediately
                        if (GameStarted)
                        {
                            SendStartMessage(roundStartSeed, GameMain.GameSession.Level.Seed, GameMain.GameSession, connectedClient, true);
                        }
                    }
                    break;
                case ClientPacketHeader.RESPONSE_CANCEL_STARTGAME:
                    if (isRoundStartWarningActive)
                    {
                        foreach (Client c in connectedClients)
                        {
                            IWriteMessage msg = new WriteOnlyMessage().WithHeader(ServerPacketHeader.CANCEL_STARTGAME);
                            serverPeer.Send(msg, c.Connection, DeliveryMethod.Reliable);
                        }
                        AbortStartGameIfWarningActive();
                    }
                    break;
                case ClientPacketHeader.REQUEST_STARTGAMEFINALIZE:
                    if (connectedClient == null)
                    {
                        DebugConsole.AddWarning("Received a REQUEST_STARTGAMEFINALIZE message. Client not connected, ignoring the message.");
                    }
                    else if (!GameStarted)
                    {
                        DebugConsole.AddWarning("Received a REQUEST_STARTGAMEFINALIZE message. Game not started, ignoring the message.");
                    }
                    else
                    {
                        SendRoundStartFinalize(connectedClient);
                    }
                    break;
                case ClientPacketHeader.UPDATE_LOBBY:
                    ClientReadLobby(inc);
                    break;
                case ClientPacketHeader.UPDATE_INGAME:
                    if (!GameStarted) { return; }
                    ClientReadIngame(inc);
                    break;
                case ClientPacketHeader.CAMPAIGN_SETUP_INFO:
                    bool isNew = inc.ReadBoolean(); inc.ReadPadBits();
                    if (isNew)
                    {
                        string saveName = inc.ReadString();
                        string seed = inc.ReadString();
                        string subName = inc.ReadString();
                        string subHash = inc.ReadString();
                        CampaignSettings settings = INetSerializableStruct.Read<CampaignSettings>(inc);

                        var matchingSub = SubmarineInfo.SavedSubmarines.FirstOrDefault(s => s.Name == subName && s.MD5Hash.StringRepresentation == subHash);

                        if (GameStarted)
                        {
                            SendDirectChatMessage(TextManager.Get("CampaignStartFailedRoundRunning").Value, connectedClient, ChatMessageType.MessageBox);
                            return;
                        }

                        if (matchingSub == null)
                        {
                            SendDirectChatMessage(
                                TextManager.GetWithVariable("CampaignStartFailedSubNotFound", "[subname]", subName).Value,
                                connectedClient, ChatMessageType.MessageBox);
                        }
                        else
                        {
                            string localSavePath = SaveUtil.CreateSavePath(SaveUtil.SaveType.Multiplayer, saveName);
                            if (CampaignMode.AllowedToManageCampaign(connectedClient, ClientPermissions.ManageRound))
                            {
                                using (dosProtection.Pause(connectedClient))
                                {
                                    ServerSettings.CampaignSettings = settings;
                                    ServerSettings.SaveSettings();
                                    MultiPlayerCampaign.StartNewCampaign(localSavePath, matchingSub.FilePath, seed, settings);
                                }
                            }
                        }
                    }
                    else
                    {
                        string savePath = inc.ReadString();
                        bool isBackup = inc.ReadBoolean();
                        inc.ReadPadBits();
                        uint backupIndex = isBackup ? inc.ReadUInt32() : uint.MinValue;

                        if (GameStarted)
                        {
                            SendDirectChatMessage(TextManager.Get("CampaignStartFailedRoundRunning").Value, connectedClient, ChatMessageType.MessageBox);
                            break;
                        }
                        if (CampaignMode.AllowedToManageCampaign(connectedClient, ClientPermissions.ManageRound)) 
                        {
                            using (dosProtection.Pause(connectedClient))
                            {
                                CampaignDataPath dataPath;
                                if (isBackup)
                                {
                                    string backupPath = SaveUtil.GetBackupPath(savePath, backupIndex);
                                    dataPath = new CampaignDataPath(loadPath: backupPath, savePath: savePath);
                                }
                                else
                                {
                                    dataPath = CampaignDataPath.CreateRegular(savePath);
                                }

                                MultiPlayerCampaign.LoadCampaign(dataPath, connectedClient);
                            }
                        }
                    }
                    break;
                case ClientPacketHeader.VOICE:
                    if (ServerSettings.VoiceChatEnabled && !connectedClient.Muted)
                    {
                        byte id = inc.ReadByte();
                        if (connectedClient.SessionId != id)
                        {
#if DEBUG
                            DebugConsole.ThrowError(
                                "Client \"" + connectedClient.Name + "\" sent a VOIP update that didn't match its ID (" + id.ToString() + "!=" + connectedClient.SessionId.ToString() + ")");
#endif
                            return;
                        }
                        VoipServer.Read(inc, connectedClient);
                    }
                    break;
                case ClientPacketHeader.SERVER_SETTINGS:
                    ServerSettings.ServerRead(inc, connectedClient);
                    break;
                case ClientPacketHeader.SERVER_SETTINGS_PERKS:
                    ServerSettings.ReadPerks(inc, connectedClient);
                    break;
                case ClientPacketHeader.SERVER_COMMAND:
                    ClientReadServerCommand(inc);
                    break;
                case ClientPacketHeader.CREW:
                    ReadCrewMessage(inc, connectedClient);
                    break;
                case ClientPacketHeader.TRANSFER_MONEY:
                    ReadMoneyMessage(inc, connectedClient);
                    break;
                case ClientPacketHeader.REWARD_DISTRIBUTION:
                    ReadRewardDistributionMessage(inc, connectedClient);
                    break;
                case ClientPacketHeader.RESET_REWARD_DISTRIBUTION:
                    ResetRewardDistribution(connectedClient);
                    break;
                case ClientPacketHeader.MEDICAL:
                    ReadMedicalMessage(inc, connectedClient);
                    break;
                case ClientPacketHeader.CIRCUITBOX:
                    ReadCircuitBoxMessage(inc, connectedClient);
                    break;
                case ClientPacketHeader.READY_CHECK:
                    ReadyCheck.ServerRead(inc, connectedClient);
                    break;
                case ClientPacketHeader.READY_TO_SPAWN:
                    ReadReadyToSpawnMessage(inc, connectedClient);
                    break;
                case ClientPacketHeader.TAKEOVERBOT:
                    ReadTakeOverBotMessage(inc, connectedClient);
                    break;
                case ClientPacketHeader.FILE_REQUEST:
                    if (ServerSettings.AllowFileTransfers)
                    {
                        FileSender.ReadFileRequest(inc, connectedClient);
                    }
                    break;
                case ClientPacketHeader.EVENTMANAGER_RESPONSE:
                    GameMain.GameSession?.EventManager.ServerRead(inc, connectedClient);
                    break;
                case ClientPacketHeader.UPDATE_CHARACTERINFO:
                    UpdateCharacterInfo(inc, connectedClient);
                    break;
                case ClientPacketHeader.REQUEST_BACKUP_INDICES:
                    SendBackupIndices(inc, connectedClient);
                    break;
                case ClientPacketHeader.ERROR:
                    HandleClientError(inc, connectedClient);
                    break;
            }
        }

        private void SendBackupIndices(IReadMessage inc, Client connectedClient)
        {
            string savePath = inc.ReadString();

            var indexData = SaveUtil.GetIndexData(savePath);

            IWriteMessage msg = new WriteOnlyMessage().WithHeader(ServerPacketHeader.SEND_BACKUP_INDICES);
            msg.WriteString(savePath);
            msg.WriteNetSerializableStruct(indexData.ToNetCollection());
            serverPeer?.Send(msg, connectedClient.Connection, DeliveryMethod.Reliable);
        }

        private void HandleClientError(IReadMessage inc, Client c)
        {
            string errorStr = "Unhandled error report";
            string errorStrNoName = errorStr;

            ClientNetError error = (ClientNetError)inc.ReadByte();
            switch (error)
            {
                case ClientNetError.MISSING_EVENT:
                    UInt16 expectedID = inc.ReadUInt16();
                    UInt16 receivedID = inc.ReadUInt16();
                    errorStr = errorStrNoName = "Expecting event id " + expectedID.ToString() + ", received " + receivedID.ToString();
                    break;
                case ClientNetError.MISSING_ENTITY:
                    UInt16 eventID = inc.ReadUInt16();
                    UInt16 entityID = inc.ReadUInt16();
                    byte subCount = inc.ReadByte();
                    List<string> subNames = new List<string>();
                    for (int i = 0; i < subCount; i++)
                    {
                        subNames.Add(inc.ReadString());
                    }
                    Entity entity = Entity.FindEntityByID(entityID);
                    if (entity == null)
                    {
                        errorStr = errorStrNoName = "Received an update for an entity that doesn't exist (event id " + eventID.ToString() + ", entity id " + entityID.ToString() + ").";
                    }
                    else if (entity is Character character)
                    {
                        errorStr = $"Missing character {character.Name} (event id {eventID}, entity id {entityID}).";
                        errorStrNoName = $"Missing character {character.SpeciesName}  (event id {eventID}, entity id {entityID}).";
                    }
                    else if (entity is Item item)
                    {
                        errorStr = errorStrNoName = $"Missing item {item.Name}, sub: {item.Submarine?.Info?.Name ?? "none"} (event id {eventID}, entity id {entityID}).";
                    }
                    else
                    {
                        errorStr = errorStrNoName = $"Missing entity {entity}, sub: {entity.Submarine?.Info?.Name ?? "none"} (event id {eventID}, entity id {entityID}).";
                    }
                    if (GameStarted)
                    {
                        var serverSubNames = Submarine.Loaded.Select(s => s.Info.Name);
                        if (subCount != Submarine.Loaded.Count || !subNames.SequenceEqual(serverSubNames))
                        {
                            string subErrorStr =  $" Loaded submarines don't match (client: {string.Join(", ", subNames)}, server: {string.Join(", ", serverSubNames)}).";
                            errorStr += subErrorStr;
                            errorStrNoName += subErrorStr;
                        }
                    }
                    break;
            }

            Log(ClientLogName(c) + " has reported an error: " + errorStr, ServerLog.MessageType.Error);
            GameAnalyticsManager.AddErrorEventOnce("GameServer.HandleClientError:" + errorStrNoName, GameAnalyticsManager.ErrorSeverity.Error, errorStr);

            try
            {
                WriteEventErrorData(c, errorStr);
            }
            catch (Exception e)
            {
                DebugConsole.ThrowError("Failed to write event error data", e);
            }

            if (c.Connection == OwnerConnection)
            {
                SendDirectChatMessage(errorStr, c, ChatMessageType.MessageBox);
                EndGame(wasSaved: false);
            }
            else
            {
                KickClient(c, errorStr);
            }

        }

        private void WriteEventErrorData(Client client, string errorStr)
        {
            if (!Directory.Exists(ServerLog.SavePath))
            {
                Directory.CreateDirectory(ServerLog.SavePath);
            }

            string filePath = $"event_error_log_server_{client.Name}_{DateTime.UtcNow.ToShortTimeString()}.log";
            filePath = Path.Combine(ServerLog.SavePath, ToolBox.RemoveInvalidFileNameChars(filePath));
            if (File.Exists(filePath)) { return; }

            List<string> errorLines = new List<string>
            {
                errorStr, ""
            };

            if (GameMain.GameSession?.GameMode != null)
            {
                errorLines.Add("Game mode: " + GameMain.GameSession.GameMode.Name.Value);
                if (GameMain.GameSession?.GameMode is MultiPlayerCampaign campaign)
                {
                    errorLines.Add("Campaign ID: " + campaign.CampaignID);
                    errorLines.Add("Campaign save ID: " + campaign.LastSaveID);
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

            errorLines.Add("");
            errorLines.Add("EntitySpawner events:");
            foreach (var entityEvent in entityEventManager.UniqueEvents)
            {
                if (entityEvent.Entity is EntitySpawner)
                {
                    var spawnData = entityEvent.Data as EntitySpawner.SpawnOrRemove;
                    errorLines.Add(
                        entityEvent.ID + ": " +
                        (spawnData is EntitySpawner.RemoveEntity ? "Remove " : "Create ") +
                        spawnData.Entity.ToString() +
                        " (" + spawnData.ID + ", " + spawnData.Entity.ID + ")");
                }
            }

            errorLines.Add("");
            errorLines.Add("Last debug messages:");
            for (int i = DebugConsole.Messages.Count - 1; i > 0 && i > DebugConsole.Messages.Count - 15; i--)
            {
                errorLines.Add("   " + DebugConsole.Messages[i].Time + " - " + DebugConsole.Messages[i].Text);
            }

            File.WriteAllLines(filePath, errorLines);
        }

        public override void CreateEntityEvent(INetSerializable entity, NetEntityEvent.IData extraData = null)
        {
            if (!(entity is IServerSerializable serverSerializable))
            {
                throw new InvalidCastException($"Entity is not {nameof(IServerSerializable)}");
            }
            entityEventManager.CreateEvent(serverSerializable, extraData);
        }

        private byte GetNewClientSessionId()
        {
            byte userId = 1;
            while (connectedClients.Any(c => c.SessionId == userId))
            {
                userId++;
            }
            return userId;
        }

        private void ClientReadLobby(IReadMessage inc)
        {
            Client c = ConnectedClients.Find(x => x.Connection == inc.Sender);
            if (c == null)
            {
                //TODO: remove?
                //inc.Sender.Disconnect("You're not a connected client.");
                return;
            }

            SegmentTableReader<ClientNetSegment>.Read(inc, (segment, inc) =>
            {
                switch (segment)
                {
                    case ClientNetSegment.SyncIds:
                        //TODO: might want to use a clever class for this
                        c.LastRecvLobbyUpdate = NetIdUtils.Clamp(inc.ReadUInt16(), c.LastRecvLobbyUpdate, GameMain.NetLobbyScreen.LastUpdateID);
                        if (c.HasPermission(ClientPermissions.ManageSettings) &&
                            NetIdUtils.IdMoreRecentOrMatches(c.LastRecvLobbyUpdate, c.LastSentServerSettingsUpdate))
                        {
                            c.LastRecvServerSettingsUpdate = c.LastSentServerSettingsUpdate;
                        }
                        c.LastRecvChatMsgID = NetIdUtils.Clamp(inc.ReadUInt16(), c.LastRecvChatMsgID, c.LastChatMsgQueueID);
                        c.LastRecvClientListUpdate = NetIdUtils.Clamp(inc.ReadUInt16(), c.LastRecvClientListUpdate, LastClientListUpdateID);

                        ReadClientNameChange(c, inc);

                        c.LastRecvCampaignSave = inc.ReadUInt16();
                        if (c.LastRecvCampaignSave > 0)
                        {
                            byte campaignID = inc.ReadByte();
                            foreach (MultiPlayerCampaign.NetFlags netFlag in Enum.GetValues(typeof(MultiPlayerCampaign.NetFlags)))
                            {
                                c.LastRecvCampaignUpdate[netFlag] = inc.ReadUInt16();
                            }
                            bool characterDiscarded = inc.ReadBoolean();
                            if (GameMain.GameSession?.GameMode is MultiPlayerCampaign campaign)
                            {
                                if (characterDiscarded) { campaign.DiscardClientCharacterData(c); }
                                //the client has a campaign save for another campaign
                                //(the server started a new campaign and the client isn't aware of it yet?)
                                if (campaign.CampaignID != campaignID)
                                {
                                    c.LastRecvCampaignSave = (ushort)(campaign.LastSaveID - 1);
                                    foreach (MultiPlayerCampaign.NetFlags netFlag in Enum.GetValues(typeof(MultiPlayerCampaign.NetFlags)))
                                    {
                                        c.LastRecvCampaignUpdate[netFlag] =
                                            (UInt16)(campaign.GetLastUpdateIdForFlag(netFlag) - 1);
                                    }
                                }
                            }
                        }
                        break;
                    case ClientNetSegment.ChatMessage:
                        ChatMessage.ServerRead(inc, c);
                        break;
                    case ClientNetSegment.Vote:
                        Voting.ServerRead(inc, c, dosProtection);
                        break;
                    default:
                        return SegmentTableReader<ClientNetSegment>.BreakSegmentReading.Yes;
                }

                //don't read further messages if the client has been disconnected (kicked due to spam for example)
                return connectedClients.Contains(c)
                    ? SegmentTableReader<ClientNetSegment>.BreakSegmentReading.No
                    : SegmentTableReader<ClientNetSegment>.BreakSegmentReading.Yes;
            });
        }

        private void ClientReadIngame(IReadMessage inc)
        {
            Client c = ConnectedClients.Find(x => x.Connection == inc.Sender);
            if (c == null)
            {
                //TODO: remove?
                //inc.SenderConnection.Disconnect("You're not a connected client.");
                return;
            }

            bool midroundSyncingDone = inc.ReadBoolean();
            inc.ReadPadBits();
            if (GameStarted)
            {
                if (!c.InGame)
                {
                    //check if midround syncing is needed due to missed unique events
                    if (!midroundSyncingDone) { entityEventManager.InitClientMidRoundSync(c); }
                    MissionAction.NotifyMissionsUnlockedThisRound(c);
                
                    if (GameMain.GameSession.GameMode is PvPMode)
                    {
                        if (c.TeamID == CharacterTeamType.None)
                        {
                            AssignClientToPvpTeamMidgame(c);
                        }
                    }
                    else
                    {
                        if (GameMain.GameSession.Campaign is MultiPlayerCampaign mpCampaign)
                        {
                            mpCampaign.SendCrewState();
                        }
                        //everyone's in team 1 in non-pvp game modes
                        c.TeamID = CharacterTeamType.Team1;
                    }
                    c.InGame = true;
                }
            }

            SegmentTableReader<ClientNetSegment>.Read(inc, (segment, inc) =>
            {
                switch (segment)
                {
                    case ClientNetSegment.SyncIds:
                        //TODO: switch this to INetSerializableStruct

                        UInt16 lastRecvChatMsgID = inc.ReadUInt16();
                        UInt16 lastRecvEntityEventID = inc.ReadUInt16();
                        UInt16 lastRecvClientListUpdate = inc.ReadUInt16();

                        //last msgs we've created/sent, the client IDs should never be higher than these
                        UInt16 lastEntityEventID = entityEventManager.Events.Count == 0 ? (UInt16)0 : entityEventManager.Events.Last().ID;

                        c.LastRecvCampaignSave = inc.ReadUInt16();
                        if (c.LastRecvCampaignSave > 0)
                        {
                            byte campaignID = inc.ReadByte();
                            foreach (MultiPlayerCampaign.NetFlags netFlag in Enum.GetValues(typeof(MultiPlayerCampaign.NetFlags)))
                            {
                                c.LastRecvCampaignUpdate[netFlag] = inc.ReadUInt16();
                            }
                            bool characterDiscarded = inc.ReadBoolean();
                            if (GameMain.GameSession?.GameMode is MultiPlayerCampaign campaign)
                            {
                                if (characterDiscarded) { campaign.DiscardClientCharacterData(c); }
                                //the client has a campaign save for another campaign
                                //(the server started a new campaign and the client isn't aware of it yet?)
                                if (campaign.CampaignID != campaignID)
                                {
                                    c.LastRecvCampaignSave = (ushort)(campaign.LastSaveID - 1);
                                    foreach (MultiPlayerCampaign.NetFlags netFlag in Enum.GetValues(typeof(MultiPlayerCampaign.NetFlags)))
                                    {
                                        c.LastRecvCampaignUpdate[netFlag] =
                                            (UInt16)(campaign.GetLastUpdateIdForFlag(netFlag) - 1);
                                    }
                                }
                            }
                        }

                        if (c.NeedsMidRoundSync)
                        {
                            //received all the old events -> client in sync, we can switch to normal behavior
                            if (lastRecvEntityEventID >= c.UnreceivedEntityEventCount - 1 ||
                                c.UnreceivedEntityEventCount == 0)
                            {
                                ushort prevID = lastRecvEntityEventID;
                                c.NeedsMidRoundSync = false;
                                lastRecvEntityEventID = (UInt16)(c.FirstNewEventID - 1);
                                c.LastRecvEntityEventID = lastRecvEntityEventID;
                                DebugConsole.Log("Finished midround syncing " + c.Name + " - switching from ID " + prevID + " to " + c.LastRecvEntityEventID);
                                //notify the client of the state of the respawn manager (so they show the respawn prompt if needed)
                                if (RespawnManager != null) { CreateEntityEvent(RespawnManager); }
                                if (GameMain.GameSession?.GameMode is MultiPlayerCampaign campaign)
                                {
                                    //notify the client of the current bank balance and purchased repairs
                                    campaign.Bank.ForceUpdate();
                                    campaign.IncrementLastUpdateIdForFlag(MultiPlayerCampaign.NetFlags.Misc);
                                }
                            }
                            else
                            {
                                lastEntityEventID = (UInt16)(c.UnreceivedEntityEventCount - 1);
                            }
                        }

                        if (NetIdUtils.IsValidId(lastRecvChatMsgID, c.LastRecvChatMsgID, c.LastChatMsgQueueID))
                        {
                            c.LastRecvChatMsgID = lastRecvChatMsgID;
                        }
                        else if (lastRecvChatMsgID != c.LastRecvChatMsgID && GameSettings.CurrentConfig.VerboseLogging)
                        {
                            DebugConsole.ThrowError(
                                "Invalid lastRecvChatMsgID  " + lastRecvChatMsgID +
                                " (previous: " + c.LastChatMsgQueueID + ", latest: " + c.LastChatMsgQueueID + ")");
                        }

                        if (NetIdUtils.IsValidId(lastRecvEntityEventID, c.LastRecvEntityEventID, lastEntityEventID))
                        {
                            if (c.NeedsMidRoundSync)
                            {
                                //give midround-joining clients a bit more time to get in sync if they keep receiving messages
                                int receivedEventCount = lastRecvEntityEventID - c.LastRecvEntityEventID;
                                if (receivedEventCount < 0) { receivedEventCount += ushort.MaxValue; }
                                c.MidRoundSyncTimeOut += receivedEventCount * 0.01f;
                                DebugConsole.Log("Midround sync timeout " + c.MidRoundSyncTimeOut.ToString("0.##") + "/" + Timing.TotalTime.ToString("0.##"));
                            }

                            c.LastRecvEntityEventID = lastRecvEntityEventID;
                        }
                        else if (lastRecvEntityEventID != c.LastRecvEntityEventID && GameSettings.CurrentConfig.VerboseLogging)
                        {
                            DebugConsole.ThrowError(
                                "Invalid lastRecvEntityEventID  " + lastRecvEntityEventID +
                                " (previous: " + c.LastRecvEntityEventID + ", latest: " + lastEntityEventID + ")");
                        }

                        if (NetIdUtils.IdMoreRecent(lastRecvClientListUpdate, c.LastRecvClientListUpdate))
                        {
                            c.LastRecvClientListUpdate = lastRecvClientListUpdate;
                        }

                        break;
                    case ClientNetSegment.ChatMessage:
                        ChatMessage.ServerRead(inc, c);
                        break;
                    case ClientNetSegment.CharacterInput:
                        if (c.Character != null)
                        {
                            c.Character.ServerReadInput(inc, c);
                        }
                        else
                        {
                            DebugConsole.AddWarning($"Received character inputs from a client who's not controlling a character ({c.Name}).");
                        }
                        break;
                    case ClientNetSegment.EntityState:
                        entityEventManager.Read(inc, c);
                        break;
                    case ClientNetSegment.Vote:
                        Voting.ServerRead(inc, c, dosProtection);
                        break;
                    case ClientNetSegment.SpectatingPos:
                        c.SpectatePos = new Vector2(inc.ReadSingle(), inc.ReadSingle());
                        break;
                    default:
                        return SegmentTableReader<ClientNetSegment>.BreakSegmentReading.Yes;
                }

                //don't read further messages if the client has been disconnected (kicked due to spam for example)
                return connectedClients.Contains(c)
                    ? SegmentTableReader<ClientNetSegment>.BreakSegmentReading.No
                    : SegmentTableReader<ClientNetSegment>.BreakSegmentReading.Yes;
            });
        }

        private void ReadCrewMessage(IReadMessage inc, Client sender)
        {
            if (GameMain.GameSession?.Campaign is MultiPlayerCampaign mpCampaign)
            {
                mpCampaign.ServerReadCrew(inc, sender);
            }
        }

        private void ReadMoneyMessage(IReadMessage inc, Client sender)
        {
            if (GameMain.GameSession?.Campaign is MultiPlayerCampaign mpCampaign)
            {
                mpCampaign.ServerReadMoney(inc, sender);
            }
        }

        private void ReadRewardDistributionMessage(IReadMessage inc, Client sender)
        {
            if (GameMain.GameSession?.Campaign is MultiPlayerCampaign mpCampaign)
            {
                mpCampaign.ServerReadRewardDistribution(inc, sender);
            }
        }
        
        private void ResetRewardDistribution(Client client)
        {
            if (GameMain.GameSession?.Campaign is MultiPlayerCampaign mpCampaign)
            {
                mpCampaign.ResetSalaries(client);
            }
        }

        private void ReadMedicalMessage(IReadMessage inc, Client sender)
        {
            if (GameMain.GameSession?.Campaign is MultiPlayerCampaign mpCampaign)
            {
                mpCampaign.MedicalClinic.ServerRead(inc, sender);
            }
        }

        private static void ReadCircuitBoxMessage(IReadMessage inc, Client sender)
        {
            var header = INetSerializableStruct.Read<NetCircuitBoxHeader>(inc);

            INetSerializableStruct data = header.Opcode switch
            {
                CircuitBoxOpcode.Cursor => INetSerializableStruct.Read<NetCircuitBoxCursorInfo>(inc),
                _ => throw new ArgumentOutOfRangeException(nameof(header.Opcode), header.Opcode, "This data cannot be handled using direct network messages.")
            };

            if (header.FindTarget().TryUnwrap(out var box))
            {
                box.ServerRead(data, sender);
            }
        }

        private void ReadReadyToSpawnMessage(IReadMessage inc, Client sender)
        {
            sender.SpectateOnly = inc.ReadBoolean() && (ServerSettings.AllowSpectating || sender.Connection == OwnerConnection);
            sender.WaitForNextRoundRespawn = inc.ReadBoolean();
            if (!(GameMain.GameSession?.GameMode is CampaignMode))
            {
                sender.WaitForNextRoundRespawn = null;
            }
        }

        private void ReadTakeOverBotMessage(IReadMessage inc, Client sender)
        {
            UInt16 botId = inc.ReadUInt16();
            if (GameMain.GameSession?.GameMode is not MultiPlayerCampaign campaign) { return; }
            
            if (ServerSettings.IronmanModeActive)
            {
                DebugConsole.ThrowError($"Client {sender.Name} has requested to take over a bot in Ironman mode!");
                return;
            }

            if (campaign.CurrentLocation.GetHireableCharacters().FirstOrDefault(c => c.ID == botId) is CharacterInfo hireableCharacter)
            {
                if (ServerSettings.ReplaceCostPercentage <= 0 || 
                    CampaignMode.AllowedToManageCampaign(sender, ClientPermissions.ManageMoney) || 
                    CampaignMode.AllowedToManageCampaign(sender, ClientPermissions.ManageHires))
                {
                    if (campaign.TryHireCharacter(campaign.CurrentLocation, hireableCharacter, takeMoney: true, sender, buyingNewCharacter: true))
                    {
                        campaign.CurrentLocation.RemoveHireableCharacter(hireableCharacter);
                        SpawnAndTakeOverBot(campaign, hireableCharacter, sender);
                        campaign.SendCrewState(createNotification: false);
                    }
                    else
                    {
                        SendConsoleMessage($"Could not hire the bot {hireableCharacter.Name}.", sender, Color.Red);
                        DebugConsole.ThrowError($"Client {sender.Name} failed to hire the bot {hireableCharacter.Name}.");                    
                    }
                }
                else
                {
                    SendConsoleMessage($"Could not hire the bot {hireableCharacter.Name}. No permission to manage money or hires.", sender, Color.Red);
                    DebugConsole.ThrowError($"Client {sender.Name} failed to hire the bot {hireableCharacter.Name}. No permission to manage money or hires.");
                }
            }
            else
            {
                CharacterInfo botInfo = GameMain.GameSession.CrewManager?.GetCharacterInfos()?.FirstOrDefault(i => i.ID == botId);

                if (botInfo is { IsNewHire: true, Character: null })
                {
                    SpawnAndTakeOverBot(campaign, botInfo, sender);
                }
                else if (botInfo?.Character == null || !botInfo.Character.IsBot)
                {
                    SendConsoleMessage($"Could not find a bot with the id {botId}.", sender, Color.Red);
                    DebugConsole.ThrowError($"Client {sender.Name} failed to take over a bot (Could not find a bot with the id {botId}).");
                    return;
                }
                else if (ServerSettings.AllowBotTakeoverOnPermadeath)
                {
                    sender.TryTakeOverBot(botInfo.Character);
                }
                else
                {
                    SendConsoleMessage($"Failed to take over a bot (taking control of bots is disallowed).", sender, Color.Red);
                    DebugConsole.ThrowError($"Client {sender.Name} failed to take over a bot (taking control of bots is disallowed).");
                }
            }
        }

        private static void SpawnAndTakeOverBot(CampaignMode campaign, CharacterInfo botInfo, Client client)
        {
            var mainSubSpawnpoint = WayPoint.SelectCrewSpawnPoints(botInfo.ToEnumerable().ToList(), Submarine.MainSub).FirstOrDefault();
            var spawnWaypoint =  campaign.CrewManager.GetOutpostSpawnpoints()?.FirstOrDefault() ?? mainSubSpawnpoint;
            if (spawnWaypoint == null)
            {
                DebugConsole.ThrowError("SpawnAndTakeOverBot: Unable to find any spawn waypoints inside the sub");
                return;
            }
            Entity.Spawner.AddCharacterToSpawnQueue(botInfo.SpeciesName, spawnWaypoint.WorldPosition, botInfo, onSpawn: newCharacter =>
            {
                if (newCharacter == null)
                {
                    DebugConsole.ThrowError("SpawnAndTakeOverBot: newCharacter is null somehow");
                    return;
                }
                // No longer show the hired character in the HR list of current hires
                campaign.CrewManager.RemoveCharacterInfo(botInfo);
                newCharacter.TeamID = CharacterTeamType.Team1;
                campaign.CrewManager.InitializeCharacter(newCharacter, mainSubSpawnpoint, spawnWaypoint);
                client.TryTakeOverBot(newCharacter);
            });
        }

        private void ClientReadServerCommand(IReadMessage inc)
        {
            Client sender = ConnectedClients.Find(x => x.Connection == inc.Sender);
            if (sender == null)
            {
                //TODO: remove?
                //inc.SenderConnection.Disconnect("You're not a connected client.");
                return;
            }

            ClientPermissions command = ClientPermissions.None;
            try
            {
                command = (ClientPermissions)inc.ReadUInt16();
            }
            catch
            {
                return;
            }

            var mpCampaign = GameMain.GameSession?.GameMode as MultiPlayerCampaign;
            if (command == ClientPermissions.ManageRound && mpCampaign != null)
            {
                //do nothing, ending campaign rounds is checked in more detail below
            }
            else if (command == ClientPermissions.ManageCampaign && mpCampaign != null)
            {
                //do nothing, campaign permissions are checked in more detail in MultiplayerCampaign.ServerRead
            }
            else if (!sender.HasPermission(command))
            {
                Log("Client \"" + GameServer.ClientLogName(sender) + "\" sent a server command \"" + command + "\". Permission denied.", ServerLog.MessageType.ServerMessage);
                return;
            }

            switch (command)
            {
                case ClientPermissions.Kick:
                    string kickedName = inc.ReadString().ToLowerInvariant();
                    string kickReason = inc.ReadString();
                    var kickedClient = connectedClients.Find(cl => cl != sender && cl.Name.Equals(kickedName, StringComparison.OrdinalIgnoreCase) && cl.Connection != OwnerConnection);
                    if (kickedClient != null)
                    {
                        Log("Client \"" + GameServer.ClientLogName(sender) + "\" kicked \"" + GameServer.ClientLogName(kickedClient) + "\".", ServerLog.MessageType.ServerMessage);
                        KickClient(kickedClient, string.IsNullOrEmpty(kickReason) ? $"ServerMessage.KickedBy~[initiator]={sender.Name}" : kickReason);
                    }
                    else
                    {
                        SendDirectChatMessage(TextManager.GetServerMessage($"ServerMessage.PlayerNotFound~[player]={kickedName}").Value, sender, ChatMessageType.Console);
                    }
                    break;
                case ClientPermissions.Ban:
                    string bannedName = inc.ReadString().ToLowerInvariant();
                    string banReason = inc.ReadString();
                    double durationSeconds = inc.ReadDouble();

                    TimeSpan? banDuration = null;
                    if (durationSeconds > 0) { banDuration = TimeSpan.FromSeconds(durationSeconds); }

                    var bannedClient = connectedClients.Find(cl => cl != sender && cl.Name.Equals(bannedName, StringComparison.OrdinalIgnoreCase) && cl.Connection != OwnerConnection);
                    if (bannedClient != null)
                    {
                        Log("Client \"" + ClientLogName(sender) + "\" banned \"" + ClientLogName(bannedClient) + "\".", ServerLog.MessageType.ServerMessage);
                        BanClient(bannedClient, string.IsNullOrEmpty(banReason) ? $"ServerMessage.BannedBy~[initiator]={sender.Name}" : banReason, banDuration);
                    }
                    else
                    {
                        var bannedPreviousClient = previousPlayers.Find(p => p.Name.Equals(bannedName, StringComparison.OrdinalIgnoreCase));
                        if (bannedPreviousClient != null)
                        {
                            Log("Client \"" + ClientLogName(sender) + "\" banned \"" + bannedPreviousClient.Name + "\".", ServerLog.MessageType.ServerMessage);
                            BanPreviousPlayer(bannedPreviousClient, string.IsNullOrEmpty(banReason) ? $"ServerMessage.BannedBy~[initiator]={sender.Name}" : banReason, banDuration);
                        }
                        else
                        {
                            SendDirectChatMessage(TextManager.GetServerMessage($"ServerMessage.PlayerNotFound~[player]={bannedName}").Value, sender, ChatMessageType.Console);
                        }
                    }
                    break;
                case ClientPermissions.Unban:
                    bool isPlayerName = inc.ReadBoolean(); inc.ReadPadBits();
                    string str = inc.ReadString();
                    if (isPlayerName)
                    {
                        UnbanPlayer(playerName: str);
                    }
                    else if (Endpoint.Parse(str).TryUnwrap(out var endpoint))
                    {
                        UnbanPlayer(endpoint);
                    }
                    break;
                case ClientPermissions.ManageRound:
                    bool end = inc.ReadBoolean();
                    if (end)
                    {
                        if (mpCampaign == null ||
                            CampaignMode.AllowedToManageCampaign(sender, ClientPermissions.ManageRound))
                        {
                            bool save = inc.ReadBoolean();
                            bool quitCampaign = inc.ReadBoolean();
                            if (GameStarted)
                            {
                                using (dosProtection.Pause(sender))
                                {
                                    Log($"Client \"{ClientLogName(sender)}\" ended the round.", ServerLog.MessageType.ServerMessage);
                                    if (mpCampaign != null && Level.IsLoadedFriendlyOutpost && save)
                                    {
                                        mpCampaign.SavePlayers();
                                        mpCampaign.HandleSaveAndQuit();
                                        GameMain.GameSession.SubmarineInfo = new SubmarineInfo(GameMain.GameSession.Submarine);
                                        SaveUtil.SaveGame(GameMain.GameSession.DataPath);
                                    }
                                    else
                                    {
                                        save = false;
                                    }
                                    EndGame(wasSaved: save);
                                }
                            }
                            else if (mpCampaign != null)
                            {
                                Log($"Client \"{ClientLogName(sender)}\" quit the currently active campaign.", ServerLog.MessageType.ServerMessage);
                                GameMain.GameSession = null;
                                GameMain.NetLobbyScreen.SelectedModeIdentifier = GameModePreset.Sandbox.Identifier;
                                GameMain.NetLobbyScreen.LastUpdateID++;

                            }
                        }
                    }
                    else
                    {
                        bool continueCampaign = inc.ReadBoolean();
                        if (mpCampaign != null && mpCampaign.GameOver || continueCampaign)
                        {
                            if (GameStarted)
                            {
                                SendDirectChatMessage("Cannot continue the campaign from the previous save (round already running).", sender, ChatMessageType.Error);
                                break;
                            }
                            else if (CampaignMode.AllowedToManageCampaign(sender, ClientPermissions.ManageCampaign) || CampaignMode.AllowedToManageCampaign(sender, ClientPermissions.ManageMap))
                            {
                                using (dosProtection.Pause(sender))
                                {
                                    MultiPlayerCampaign.LoadCampaign(GameMain.GameSession.DataPath, sender);
                                }
                            }
                        }
                        else if (!GameStarted && !initiatedStartGame)
                        {
                            using (dosProtection.Pause(sender))
                            {
                                Log("Client \"" + ClientLogName(sender) + "\" started the round.", ServerLog.MessageType.ServerMessage);
                                var result = TryStartGame();
                                if (result != TryStartGameResult.Success)
                                {
                                    SendDirectChatMessage(TextManager.Get($"TryStartGameError.{result}").Value, sender, ChatMessageType.Error);
                                }
                            }
                        }
                        else if (mpCampaign != null && (CampaignMode.AllowedToManageCampaign(sender, ClientPermissions.ManageCampaign) || CampaignMode.AllowedToManageCampaign(sender, ClientPermissions.ManageMap)))
                        {
                            using (dosProtection.Pause(sender))
                            {
                                var availableTransition = mpCampaign.GetAvailableTransition(out _, out _);
                                //don't force location if we've teleported
                                bool forceLocation = !mpCampaign.Map.AllowDebugTeleport || mpCampaign.Map.CurrentLocation == Level.Loaded.StartLocation;
                                switch (availableTransition)
                                {
                                    case CampaignMode.TransitionType.ReturnToPreviousEmptyLocation:
                                        if (forceLocation)
                                        {
                                            mpCampaign.Map.SelectLocation(
                                                mpCampaign.Map.CurrentLocation.Connections.Find(c => c.LevelData == Level.Loaded?.LevelData).OtherLocation(mpCampaign.Map.CurrentLocation));
                                        }
                                        mpCampaign.LoadNewLevel();
                                        break;
                                    case CampaignMode.TransitionType.ProgressToNextEmptyLocation:
                                        if (forceLocation)
                                        {
                                            mpCampaign.Map.SetLocation(mpCampaign.Map.Locations.IndexOf(Level.Loaded.EndLocation));
                                        }
                                        mpCampaign.LoadNewLevel();
                                        break;
                                    case CampaignMode.TransitionType.None:
    #if DEBUG || UNSTABLE
                                        DebugConsole.ThrowError($"Client \"{sender.Name}\" attempted to trigger a level transition. No transitions available.");
    #endif
                                        break;
                                    default:
                                        Log("Client \"" + ClientLogName(sender) + "\" ended the round.", ServerLog.MessageType.ServerMessage);
                                        mpCampaign.LoadNewLevel();
                                        break;
                                }
                            }
                        }
                    }
                    break;
                case ClientPermissions.SelectSub:
                    SelectedSubType subType = (SelectedSubType)inc.ReadByte();
                    string subHash = inc.ReadString();
                    var subList = GameMain.NetLobbyScreen.GetSubList();
                    var sub = subList.FirstOrDefault(s => s.MD5Hash.StringRepresentation == subHash);
                    if (sub == null)
                    {
                        DebugConsole.NewMessage($"Client \"{ClientLogName(sender)}\" attempted to select a sub, could not find a sub with the MD5 hash \"{subHash}\".", Color.Red);
                    }
                    else
                    {
                        switch (subType)
                        {
                            case SelectedSubType.Shuttle:
                                GameMain.NetLobbyScreen.SelectedShuttle = sub;
                                break;
                            case SelectedSubType.Sub:
                                GameMain.NetLobbyScreen.SelectedSub = sub;
                                break;
                            case SelectedSubType.EnemySub:
                                GameMain.NetLobbyScreen.SelectedEnemySub = sub;
                                break;
                        }
                    }
                    break;
                case ClientPermissions.SelectMode:
                    UInt16 modeIndex = inc.ReadUInt16();
                    GameMain.NetLobbyScreen.SelectedModeIndex = modeIndex;
                    Log("Gamemode changed to " + (GameMain.NetLobbyScreen.SelectedMode?.Name.Value ?? "none"), ServerLog.MessageType.ServerMessage);
                    if (GameMain.NetLobbyScreen.GameModes[modeIndex] == GameModePreset.MultiPlayerCampaign)
                    {
                        TrySendCampaignSetupInfo(sender);
                    }
                    break;
                case ClientPermissions.ManageCampaign:
                    mpCampaign?.ServerRead(inc, sender);
                    break;
                case ClientPermissions.ConsoleCommands:
                    DebugConsole.ServerRead(inc, sender);
                    break;
                case ClientPermissions.ManagePermissions:
                    byte targetClientID = inc.ReadByte();
                    Client targetClient = connectedClients.Find(c => c.SessionId == targetClientID);
                    if (targetClient == null || targetClient == sender || targetClient.Connection == OwnerConnection) { return; }

                    targetClient.ReadPermissions(inc);

                    List<string> permissionNames = new List<string>();
                    foreach (ClientPermissions permission in Enum.GetValues(typeof(ClientPermissions)))
                    {
                        if (permission == ClientPermissions.None || permission == ClientPermissions.All)
                        {
                            continue;
                        }
                        if (targetClient.Permissions.HasFlag(permission)) { permissionNames.Add(permission.ToString()); }
                    }

                    string logMsg;
                    if (permissionNames.Any())
                    {
                        logMsg = "Client \"" + GameServer.ClientLogName(sender) + "\" set the permissions of the client \"" + GameServer.ClientLogName(targetClient) + "\" to "
                            + string.Join(", ", permissionNames);
                    }
                    else
                    {
                        logMsg = "Client \"" + GameServer.ClientLogName(sender) + "\" removed all permissions from the client \"" + GameServer.ClientLogName(targetClient) + ".";
                    }
                    Log(logMsg, ServerLog.MessageType.ServerMessage);

                    UpdateClientPermissions(targetClient);

                    break;
            }

            inc.ReadPadBits();
        }

        private void ClientWrite(Client c)
        {
            if (GameStarted && c.InGame)
            {
                ClientWriteIngame(c);
            }
            else
            {
                //if 30 seconds have passed since the round started and the client isn't ingame yet,
                //consider the client's character disconnected (causing it to die if the client does not join soon)
                if (GameStarted && c.Character != null && (DateTime.Now - roundStartTime).Seconds > 30.0f)
                {
                    c.Character.ClientDisconnected = true;
                }

                ClientWriteLobby(c);

            }

            if (c.Connection == OwnerConnection)
            {
                while (pendingMessagesToOwner.Any())
                {
                    SendDirectChatMessage(pendingMessagesToOwner.Dequeue(), c);
                }
            }

            if (GameMain.GameSession?.GameMode is MultiPlayerCampaign campaign &&
                GameMain.NetLobbyScreen.SelectedMode == campaign.Preset &&
                NetIdUtils.IdMoreRecent(campaign.LastSaveID, c.LastRecvCampaignSave))
            {
                //already sent an up-to-date campaign save
                if (c.LastCampaignSaveSendTime != default && campaign.LastSaveID == c.LastCampaignSaveSendTime.saveId)
                {
                    //the save was sent less than 5 second ago, don't attempt to resend yet
                    //(the client may have received it but hasn't acked us yet)
                    if (c.LastCampaignSaveSendTime.time > NetTime.Now - 5.0f)
                    {
                        return;
                    }
                }

                if (!FileSender.ActiveTransfers.Any(t => t.Connection == c.Connection && t.FileType == FileTransferType.CampaignSave))
                {
                    FileSender.StartTransfer(c.Connection, FileTransferType.CampaignSave, GameMain.GameSession.DataPath.SavePath);
                    c.LastCampaignSaveSendTime = (campaign.LastSaveID, (float)NetTime.Now);
                }
            }
        }

        /// <summary>
        /// Write info that the client needs when joining the server
        /// </summary>
        private void ClientWriteInitial(Client c, IWriteMessage outmsg)
        {
            if (GameSettings.CurrentConfig.VerboseLogging)
            {
                DebugConsole.NewMessage($"Sending initial lobby update to {c.Name}", Color.Gray);
            }

            outmsg.WriteByte(c.SessionId);

            var subList = GameMain.NetLobbyScreen.GetSubList();
            outmsg.WriteUInt16((UInt16)subList.Count);
            for (int i = 0; i < subList.Count; i++)
            {
                var sub = subList[i];
                outmsg.WriteString(sub.Name);
                outmsg.WriteString(sub.MD5Hash.ToString());
                outmsg.WriteIdentifier(sub.Class.Identifier);
                outmsg.WriteBoolean(sub.HasTag(SubmarineTag.Shuttle));
                outmsg.WriteBoolean(sub.RequiredContentPackagesInstalled);
            }

            outmsg.WriteBoolean(GameStarted);
            outmsg.WriteBoolean(ServerSettings.AllowSpectating);
            outmsg.WriteBoolean(ServerSettings.RespawnMode == RespawnMode.Permadeath);
            outmsg.WriteBoolean(ServerSettings.IronmanMode);

            c.WritePermissions(outmsg);
        }

        private void ClientWriteIngame(Client c)
        {
            //don't send position updates to characters who are still midround syncing
            //characters or items spawned mid-round don't necessarily exist at the client's end yet
            if (!c.NeedsMidRoundSync)
            {
                Character clientCharacter = c.Character;
                foreach (Character otherCharacter in Character.CharacterList)
                {
                    if (!otherCharacter.Enabled) { continue; }
                    if (c.SpectatePos == null)
                    {
                        //not spectating ->
                        //  check if the client's character, or the entity they're viewing,
                        //  is close enough to the other character or the entity the other character is viewing
                        float distSqr = GetShortestDistance(clientCharacter.WorldPosition, otherCharacter);
                        if (clientCharacter.ViewTarget != null && clientCharacter.ViewTarget != clientCharacter)
                        {
                            distSqr = Math.Min(distSqr, GetShortestDistance(clientCharacter.ViewTarget.WorldPosition, otherCharacter));
                        }
                        if (distSqr >= MathUtils.Pow2(otherCharacter.Params.DisableDistance)) { continue; }
                    }
                    else if (otherCharacter != clientCharacter)
                    {
                        //spectating ->
                        //  check if the position the client is viewing
                        //  is close enough to the other character or the entity the other character is viewing
                        if (GetShortestDistance(c.SpectatePos.Value, otherCharacter) >= MathUtils.Pow2(otherCharacter.Params.DisableDistance)) { continue; }
                    }

                    static float GetShortestDistance(Vector2 viewPos, Character targetCharacter)
                    {
                        float distSqr = Vector2.DistanceSquared(viewPos, targetCharacter.WorldPosition);
                        if (targetCharacter.ViewTarget != null && targetCharacter.ViewTarget != targetCharacter)
                        {
                            //if the character is viewing something (far-away turret?),
                            //we might want to send updates about it to the spectating client even though they're far away from the actual character
                            distSqr = Math.Min(distSqr, Vector2.DistanceSquared(viewPos, targetCharacter.ViewTarget.WorldPosition));
                        }
                        return distSqr;
                    }

                    float updateInterval = otherCharacter.GetPositionUpdateInterval(c);
                    c.PositionUpdateLastSent.TryGetValue(otherCharacter, out float lastSent);
                    if (lastSent > NetTime.Now)
                    {
                        //sent in the future -> can't be right, remove
                        c.PositionUpdateLastSent.Remove(otherCharacter);
                    }
                    else
                    {
                        if (lastSent > NetTime.Now - updateInterval) { continue; }
                    }
                    if (!c.PendingPositionUpdates.Contains(otherCharacter)) { c.PendingPositionUpdates.Enqueue(otherCharacter); }
                }

                foreach (Submarine sub in Submarine.Loaded)
                {
                    //if docked to a sub with a smaller ID, don't send an update
                    //  (= update is only sent for the docked sub that has the smallest ID, doesn't matter if it's the main sub or a shuttle)
                    if (sub.Info.IsOutpost || sub.DockedTo.Any(s => s.ID < sub.ID)) { continue; }
                    if (sub.PhysicsBody == null || sub.PhysicsBody.BodyType == FarseerPhysics.BodyType.Static) { continue; }
                    if (!c.PendingPositionUpdates.Contains(sub)) { c.PendingPositionUpdates.Enqueue(sub); }
                }

                foreach (Item item in Item.ItemList)
                {
                    if (item.PositionUpdateInterval == float.PositiveInfinity) { continue; }
                    float updateInterval = item.GetPositionUpdateInterval(c);
                    c.PositionUpdateLastSent.TryGetValue(item, out float lastSent);
                    if (lastSent > NetTime.Now)
                    {
                        //sent in the future -> can't be right, remove
                        c.PositionUpdateLastSent.Remove(item);
                    }
                    else
                    {
                        if (lastSent > NetTime.Now - updateInterval) { continue; }
                    }
                    if (!c.PendingPositionUpdates.Contains(item)) { c.PendingPositionUpdates.Enqueue(item); }
                }
            }

            IWriteMessage outmsg = new WriteOnlyMessage();
            outmsg.WriteByte((byte)ServerPacketHeader.UPDATE_INGAME);
            outmsg.WriteSingle((float)NetTime.Now);
            outmsg.WriteSingle(EndRoundTimeRemaining);

            using (var segmentTable = SegmentTableWriter<ServerNetSegment>.StartWriting(outmsg))
            {
                segmentTable.StartNewSegment(ServerNetSegment.SyncIds);
                outmsg.WriteUInt16(c.LastSentChatMsgID); //send this to client so they know which chat messages weren't received by the server
                outmsg.WriteUInt16(c.LastSentEntityEventID);

                if (GameMain.GameSession?.GameMode is MultiPlayerCampaign campaign && campaign.Preset == GameMain.NetLobbyScreen.SelectedMode)
                {
                    outmsg.WriteBoolean(true);
                    outmsg.WritePadBits();
                    campaign.ServerWrite(outmsg, c);
                }
                else
                {
                    outmsg.WriteBoolean(false);
                    outmsg.WritePadBits();
                }

                int clientListBytes = outmsg.LengthBytes;
                WriteClientList(segmentTable, c, outmsg);
                clientListBytes = outmsg.LengthBytes - clientListBytes;

                int chatMessageBytes = outmsg.LengthBytes;
                WriteChatMessages(segmentTable, outmsg, c);
                chatMessageBytes = outmsg.LengthBytes - chatMessageBytes;

                //write as many position updates as the message can fit (only after midround syncing is done)
                int positionUpdateBytes = outmsg.LengthBytes;
                while (!c.NeedsMidRoundSync && c.PendingPositionUpdates.Count > 0)
                {
                    var entity = c.PendingPositionUpdates.Peek();
                    if (entity is not IServerPositionSync entityPositionSync ||
                        entity.Removed ||
                        (entity is Item item && float.IsInfinity(item.PositionUpdateInterval)))
                    {
                        c.PendingPositionUpdates.Dequeue();
                        continue;
                    }

                    var tempBuffer = new ReadWriteMessage();
                    var entityPositionHeader = EntityPositionHeader.FromEntity(entity);
                    tempBuffer.WriteNetSerializableStruct(entityPositionHeader);
                    entityPositionSync.ServerWritePosition(tempBuffer, c);

                    //no more room in this packet
                    if (outmsg.LengthBytes + tempBuffer.LengthBytes > MsgConstants.MTU - 100)
                    {
                        break;
                    }

                    segmentTable.StartNewSegment(ServerNetSegment.EntityPosition);
                    outmsg.WritePadBits(); //padding is required here to make sure any padding bits within tempBuffer are read correctly
                    outmsg.WriteVariableUInt32((uint)tempBuffer.LengthBytes);
                    outmsg.WriteBytes(tempBuffer.Buffer, 0, tempBuffer.LengthBytes);
                    outmsg.WritePadBits();

                    c.PositionUpdateLastSent[entity] = (float)NetTime.Now;
                    c.PendingPositionUpdates.Dequeue();
                }
                positionUpdateBytes = outmsg.LengthBytes - positionUpdateBytes;

                if (outmsg.LengthBytes > MsgConstants.MTU)
                {
                    string errorMsg = "Maximum packet size exceeded (" + outmsg.LengthBytes + " > " + MsgConstants.MTU + ")\n";
                    errorMsg +=
                        "  Client list size: " + clientListBytes + " bytes\n" +
                        "  Chat message size: " + chatMessageBytes + " bytes\n" +
                        "  Position update size: " + positionUpdateBytes + " bytes\n\n";
                    DebugConsole.ThrowError(errorMsg);
                    GameAnalyticsManager.AddErrorEventOnce("GameServer.ClientWriteIngame1:PacketSizeExceeded" + outmsg.LengthBytes, GameAnalyticsManager.ErrorSeverity.Error, errorMsg);
                }
            }

            serverPeer.Send(outmsg, c.Connection, DeliveryMethod.Unreliable);

            //---------------------------------------------------------------------------

            for (int i = 0; i < NetConfig.MaxEventPacketsPerUpdate; i++)
            {
                outmsg = new WriteOnlyMessage();
                outmsg.WriteByte((byte)ServerPacketHeader.UPDATE_INGAME);
                outmsg.WriteSingle((float)NetTime.Now);
                outmsg.WriteSingle(EndRoundTimeRemaining);

                using (var segmentTable = SegmentTableWriter<ServerNetSegment>.StartWriting(outmsg))
                {
                    int eventManagerBytes = outmsg.LengthBytes;
                    entityEventManager.Write(segmentTable, c, outmsg, out List<NetEntityEvent> sentEvents);
                    eventManagerBytes = outmsg.LengthBytes - eventManagerBytes;

                    if (sentEvents.Count == 0)
                    {
                        break;
                    }

                    if (outmsg.LengthBytes > MsgConstants.MTU)
                    {
                        string errorMsg = "Maximum packet size exceeded (" + outmsg.LengthBytes + " > " +
                                          MsgConstants.MTU + ")\n";
                        errorMsg +=
                            "  Event size: " + eventManagerBytes + " bytes\n";

                        if (sentEvents != null && sentEvents.Count > 0)
                        {
                            errorMsg += "Sent events: \n";
                            foreach (var entityEvent in sentEvents)
                            {
                                errorMsg += "  - " + (entityEvent.Entity?.ToString() ?? "null") + "\n";
                            }
                        }

                        DebugConsole.ThrowError(errorMsg);
                        GameAnalyticsManager.AddErrorEventOnce(
                            "GameServer.ClientWriteIngame2:PacketSizeExceeded" + outmsg.LengthBytes,
                            GameAnalyticsManager.ErrorSeverity.Error, errorMsg);
                    }
                }

                serverPeer.Send(outmsg, c.Connection, DeliveryMethod.Unreliable);
            }
        }

        private void WriteClientList(in SegmentTableWriter<ServerNetSegment> segmentTable, Client c, IWriteMessage outmsg)
        {
            bool hasChanged = NetIdUtils.IdMoreRecent(LastClientListUpdateID, c.LastRecvClientListUpdate);
            if (!hasChanged) { return; }

            segmentTable.StartNewSegment(ServerNetSegment.ClientList);
            outmsg.WriteUInt16(LastClientListUpdateID);

            outmsg.WriteByte((byte)Team1Count);
            outmsg.WriteByte((byte)Team2Count);

            outmsg.WriteByte((byte)connectedClients.Count);
            foreach (Client client in connectedClients)
            {
                var tempClientData = new TempClient
                {
                    SessionId = client.SessionId,
                    AccountInfo = client.AccountInfo,
                    NameId = client.NameId,
                    Name = client.Name,
                    PreferredJob = client.Character?.Info?.Job != null && GameStarted
                        ? client.Character.Info.Job.Prefab.Identifier
                        : client.PreferredJob,
                    PreferredTeam = client.PreferredTeam,
                    TeamID = client.TeamID,
                    CharacterId = client.Character == null || !GameStarted ? (ushort)0 : client.Character.ID,
                    Karma = c.HasPermission(ClientPermissions.ServerLog) ? client.Karma : 100.0f,
                    Muted = client.Muted,
                    InGame = client.InGame,
                    HasPermissions = client.Permissions != ClientPermissions.None,
                    IsOwner = client.Connection == OwnerConnection,
                    IsDownloading = FileSender.ActiveTransfers.Any(t => t.Connection == client.Connection)
                };
                
                outmsg.WriteNetSerializableStruct(tempClientData);
                outmsg.WritePadBits();
            }
        }

        private void ClientWriteLobby(Client c)
        {
            bool isInitialUpdate = false;

            IWriteMessage outmsg = new WriteOnlyMessage();
            outmsg.WriteByte((byte)ServerPacketHeader.UPDATE_LOBBY);

            bool messageTooLarge;
            using (var segmentTable = SegmentTableWriter<ServerNetSegment>.StartWriting(outmsg))
            {
                segmentTable.StartNewSegment(ServerNetSegment.SyncIds);

                int settingsBytes = outmsg.LengthBytes;
                int initialUpdateBytes = 0;

                if (ServerSettings.UnsentFlags() != ServerSettings.NetFlags.None)
                {
                    GameMain.NetLobbyScreen.LastUpdateID++;
                }

                IWriteMessage settingsBuf = null;
                if (NetIdUtils.IdMoreRecent(GameMain.NetLobbyScreen.LastUpdateID, c.LastRecvLobbyUpdate))
                {
                    outmsg.WriteBoolean(true);
                    outmsg.WritePadBits();

                    outmsg.WriteUInt16(GameMain.NetLobbyScreen.LastUpdateID);

                    settingsBuf = new ReadWriteMessage();
                    ServerSettings.ServerWrite(settingsBuf, c);
                    outmsg.WriteUInt16((UInt16)settingsBuf.LengthBytes);
                    outmsg.WriteBytes(settingsBuf.Buffer, 0, settingsBuf.LengthBytes);

                    outmsg.WriteBoolean(!c.InitialLobbyUpdateSent);
                    if (!c.InitialLobbyUpdateSent)
                    {
                        isInitialUpdate = true;
                        initialUpdateBytes = outmsg.LengthBytes;
                        ClientWriteInitial(c, outmsg);
                        c.InitialLobbyUpdateSent = true;
                        initialUpdateBytes = outmsg.LengthBytes - initialUpdateBytes;
                    }
                    outmsg.WriteString(GameMain.NetLobbyScreen.SelectedSub.Name);
                    outmsg.WriteString(GameMain.NetLobbyScreen.SelectedSub.MD5Hash.ToString());

                    if (GameMain.NetLobbyScreen.SelectedEnemySub is { } enemySub)
                    {
                        outmsg.WriteBoolean(true);
                        outmsg.WriteString(enemySub.Name);
                        outmsg.WriteString(enemySub.MD5Hash.ToString());
                    }
                    else
                    {
                        outmsg.WriteBoolean(false);
                    }

                    outmsg.WriteBoolean(IsUsingRespawnShuttle());
                    var selectedShuttle = GameStarted && RespawnManager != null && RespawnManager.UsingShuttle ? 
                        RespawnManager.RespawnShuttles.First().Info : 
                        GameMain.NetLobbyScreen.SelectedShuttle;
                    outmsg.WriteString(selectedShuttle.Name);
                    outmsg.WriteString(selectedShuttle.MD5Hash.ToString());

                    outmsg.WriteBoolean(ServerSettings.AllowSubVoting);
                    outmsg.WriteBoolean(ServerSettings.AllowModeVoting);

                    outmsg.WriteBoolean(ServerSettings.VoiceChatEnabled);

                    outmsg.WriteBoolean(ServerSettings.AllowSpectating);

                    outmsg.WriteSingle(ServerSettings.TraitorProbability);
                    outmsg.WriteRangedInteger(ServerSettings.TraitorDangerLevel, TraitorEventPrefab.MinDangerLevel, TraitorEventPrefab.MaxDangerLevel);

                    outmsg.WriteVariableUInt32((uint)GameMain.NetLobbyScreen.MissionTypes.Count());
                    foreach (var missionType in GameMain.NetLobbyScreen.MissionTypes)
                    {
                        outmsg.WriteIdentifier(missionType);
                    }

                    outmsg.WriteByte((byte)GameMain.NetLobbyScreen.SelectedModeIndex);
                    outmsg.WriteString(GameMain.NetLobbyScreen.LevelSeed);
                    outmsg.WriteSingle(ServerSettings.SelectedLevelDifficulty);

                    outmsg.WriteByte((byte)ServerSettings.BotCount);
                    outmsg.WriteBoolean(ServerSettings.BotSpawnMode == BotSpawnMode.Fill);

                    outmsg.WriteBoolean(ServerSettings.AutoRestart);
                    if (ServerSettings.AutoRestart)
                    {
                        outmsg.WriteSingle(autoRestartTimerRunning ? ServerSettings.AutoRestartTimer : 0.0f);
                    }

                    if (GameMain.NetLobbyScreen.SelectedMode == GameModePreset.MultiPlayerCampaign &&
                        connectedClients.None(c => c.Connection == OwnerConnection || c.HasPermission(ClientPermissions.ManageRound) || c.HasPermission(ClientPermissions.ManageCampaign)))
                    {
                        //if no-one has permissions to manage the campaign, show the setup UI to everyone
                        TrySendCampaignSetupInfo(c);
                    }
                }
                else
                {
                    outmsg.WriteBoolean(false);
                    outmsg.WritePadBits();
                }
                settingsBytes = outmsg.LengthBytes - settingsBytes;

                int campaignBytes = outmsg.LengthBytes;
                if (outmsg.LengthBytes < MsgConstants.MTU - 500 &&
                    GameMain.GameSession?.GameMode is MultiPlayerCampaign campaign && campaign.Preset == GameMain.NetLobbyScreen.SelectedMode)
                {
                    outmsg.WriteBoolean(true);
                    outmsg.WritePadBits();
                    campaign.ServerWrite(outmsg, c);
                }
                else
                {
                    outmsg.WriteBoolean(false);
                    outmsg.WritePadBits();
                }
                campaignBytes = outmsg.LengthBytes - campaignBytes;

                outmsg.WriteUInt16(c.LastSentChatMsgID); //send this to client so they know which chat messages weren't received by the server

                int clientListBytes = outmsg.LengthBytes;
                if (outmsg.LengthBytes < MsgConstants.MTU - 500)
                {
                    WriteClientList(segmentTable, c, outmsg);
                }
                clientListBytes = outmsg.LengthBytes - clientListBytes;

                int chatMessageBytes = outmsg.LengthBytes;
                WriteChatMessages(segmentTable, outmsg, c);
                chatMessageBytes = outmsg.LengthBytes - chatMessageBytes;

                messageTooLarge = outmsg.LengthBytes > MsgConstants.MTU;
                if (messageTooLarge && !isInitialUpdate)
                {
                    string warningMsg = "Maximum packet size exceeded, will send using reliable mode (" + outmsg.LengthBytes + " > " + MsgConstants.MTU + ")\n";
                    warningMsg +=
                        "  Client list size: " + clientListBytes + " bytes\n" +
                        "  Chat message size: " + chatMessageBytes + " bytes\n" +
                        "  Campaign size: " + campaignBytes + " bytes\n" +
                        "  Settings size: " + settingsBytes + " bytes\n";
                    if (initialUpdateBytes > 0)
                    {
                        warningMsg +=
                            "    Initial update size: " + initialUpdateBytes + " bytes\n";
                    }
                    if (settingsBuf != null)
                    {
                        warningMsg +=
                            "    Settings buffer size: " + settingsBuf.LengthBytes + " bytes\n";
                    }
#if DEBUG || UNSTABLE
                    DebugConsole.ThrowError(warningMsg);
#else
                    if (GameSettings.CurrentConfig.VerboseLogging) { DebugConsole.AddWarning(warningMsg); }                
#endif
                    GameAnalyticsManager.AddErrorEventOnce("GameServer.ClientWriteIngame1:ClientWriteLobby" + outmsg.LengthBytes, GameAnalyticsManager.ErrorSeverity.Warning, warningMsg);
                }
            }
            
            if (isInitialUpdate || messageTooLarge)
            {
                //the initial update may be very large if the host has a large number
                //of submarine files, so the message may have to be fragmented

                //unreliable messages don't play nicely with fragmenting, so we'll send the message reliably
                serverPeer.Send(outmsg, c.Connection, DeliveryMethod.Reliable);

                //and assume the message was received, so we don't have to keep resending
                //these large initial messages until the client acknowledges receiving them
                c.LastRecvLobbyUpdate = GameMain.NetLobbyScreen.LastUpdateID;

            }
            else
            {
                serverPeer.Send(outmsg, c.Connection, DeliveryMethod.Unreliable);
            }

            if (isInitialUpdate)
            {
                SendVoteStatus(new List<Client>() { c });
            }
        }

        private static void WriteChatMessages(in SegmentTableWriter<ServerNetSegment> segmentTable, IWriteMessage outmsg, Client c)
        {
            c.ChatMsgQueue.RemoveAll(cMsg => !NetIdUtils.IdMoreRecent(cMsg.NetStateID, c.LastRecvChatMsgID));
            for (int i = 0; i < c.ChatMsgQueue.Count && i < ChatMessage.MaxMessagesPerPacket; i++)
            {
                if (outmsg.LengthBytes + c.ChatMsgQueue[i].EstimateLengthBytesServer(c) > MsgConstants.MTU - 5 && i > 0)
                {
                    //not enough room in this packet
                    return;
                }
                c.ChatMsgQueue[i].ServerWrite(segmentTable, outmsg, c);
            }
        }

        public enum TryStartGameResult
        {
            Success,
            GameAlreadyStarted,
            PerksExceedAllowance,
            SubmarineNotFound,
            GameModeNotSelected,
            CannotStartMultiplayerCampaign,
        }

        public TryStartGameResult TryStartGame()
        {
            if (initiatedStartGame || GameStarted) { return TryStartGameResult.GameAlreadyStarted; }

            GameModePreset selectedMode =
                Voting.HighestVoted<GameModePreset>(VoteType.Mode, connectedClients) ?? GameMain.NetLobbyScreen.SelectedMode;
            if (selectedMode == null)
            {
                return TryStartGameResult.GameModeNotSelected;
            }
            if (selectedMode == GameModePreset.MultiPlayerCampaign && GameMain.GameSession?.GameMode is not MultiPlayerCampaign)
            {
                //DebugConsole.ThrowError($"{nameof(TryStartGame)} failed. Cannot start a multiplayer campaign via {nameof(TryStartGame)} - use {nameof(MultiPlayerCampaign.StartNewCampaign)} or {nameof(MultiPlayerCampaign.LoadCampaign)} instead.");
                if (GameMain.NetLobbyScreen.SelectedMode != GameModePreset.MultiPlayerCampaign)
                {
                    GameMain.NetLobbyScreen.SelectedModeIdentifier = GameModePreset.MultiPlayerCampaign.Identifier;
                }
                return TryStartGameResult.CannotStartMultiplayerCampaign;
            }

            bool applyPerks = GameSession.ShouldApplyDisembarkPoints(selectedMode);
            if (applyPerks)
            {
                if (!GameSession.ValidatedDisembarkPoints(selectedMode, GameMain.NetLobbyScreen.MissionTypes))
                {
                    return TryStartGameResult.PerksExceedAllowance;
                }
            }

            Log("Starting a new round...", ServerLog.MessageType.ServerMessage);
            SubmarineInfo selectedShuttle = GameMain.NetLobbyScreen.SelectedShuttle;

            SubmarineInfo selectedSub;
            Option<SubmarineInfo> selectedEnemySub = Option.None;

            if (ServerSettings.AllowSubVoting)
            {
                if (selectedMode == GameModePreset.PvP)
                {
                    var team1Voters = connectedClients.Where(static c => c.PreferredTeam == CharacterTeamType.Team1);
                    var team2Voters = connectedClients.Where(static c => c.PreferredTeam == CharacterTeamType.Team2);

                    SubmarineInfo team1Sub = Voting.HighestVoted<SubmarineInfo>(VoteType.Sub, team1Voters, out int team1VoteCount);
                    SubmarineInfo team2Sub = Voting.HighestVoted<SubmarineInfo>(VoteType.Sub, team2Voters, out int team2VoteCount);

                    // check if anyone on coalition voted for a sub
                    if (team1VoteCount > 0)
                    {
                        // use the most voted one
                        selectedSub = team1Sub;
                    }
                    else
                    {
                        selectedSub = team2VoteCount > 0
                                          ? team2Sub // only separatists voted for a sub, use theirs
                                          : GameMain.NetLobbyScreen.SelectedSub; // nobody voted for a sub so use the default one
                    }

                    // check if separatists voted for a sub
                    if (team2VoteCount > 0 && team2Sub != null)
                    {
                        selectedEnemySub = Option.Some(team2Sub);
                    }
                    // no reason to fall back to coalition sub,
                    // since not selecting an enemy submarine automatically selects the coalition sub
                    // deeper in the code
                }
                else
                {
                    selectedSub = Voting.HighestVoted<SubmarineInfo>(VoteType.Sub, connectedClients) ?? GameMain.NetLobbyScreen.SelectedSub;
                }
            }
            else
            {
                selectedSub = GameMain.NetLobbyScreen.SelectedSub;
                SubmarineInfo enemySub = GameMain.NetLobbyScreen.SelectedEnemySub ?? GameMain.NetLobbyScreen.SelectedSub;

                // Option throws an exception if the value is null, prevent that
                if (enemySub != null)
                {
                    selectedEnemySub = Option.Some(enemySub);
                }
            }

            if (selectedSub == null || selectedShuttle == null)
            {
                return TryStartGameResult.SubmarineNotFound;
            }

            if (applyPerks && CheckIfAnyPerksAreIncompatible(selectedSub, selectedEnemySub.Fallback(selectedSub), selectedMode, out var incompatiblePerks))
            {
                CoroutineManager.StartCoroutine(WarnAndDelayStartGame(incompatiblePerks, selectedSub, selectedEnemySub, selectedShuttle, selectedMode), nameof(WarnAndDelayStartGame));
                return TryStartGameResult.Success;
            }

            initiatedStartGame = true;
            startGameCoroutine = CoroutineManager.StartCoroutine(InitiateStartGame(selectedSub, selectedEnemySub, selectedShuttle, selectedMode), "InitiateStartGame");

            return TryStartGameResult.Success;
        }

        private bool CheckIfAnyPerksAreIncompatible(SubmarineInfo team1Sub, SubmarineInfo team2Sub, GameModePreset preset, out PerkCollection incompatiblePerks)
        {
            var incompatibleTeam1Perks = ImmutableArray.CreateBuilder<DisembarkPerkPrefab>();
            var incompatibleTeam2Perks = ImmutableArray.CreateBuilder<DisembarkPerkPrefab>();
            bool hasIncompatiblePerks = false;
            PerkCollection perks = GameSession.GetPerks();

            bool ignorePerksThatCanNotApplyWithoutSubmarine = GameSession.ShouldIgnorePerksThatCanNotApplyWithoutSubmarine(preset, GameMain.NetLobbyScreen.MissionTypes);

            foreach (DisembarkPerkPrefab perk in perks.Team1Perks)
            {
                if (ignorePerksThatCanNotApplyWithoutSubmarine && perk.PerkBehaviors.Any(static p => !p.CanApplyWithoutSubmarine())) { continue; }
                bool anyCanNotApply = perk.PerkBehaviors.Any(p => !p.CanApply(team1Sub));

                if (anyCanNotApply)
                {
                    incompatibleTeam1Perks.Add(perk);
                    hasIncompatiblePerks = true;
                }
            }

            if (preset == GameModePreset.PvP)
            {
                foreach (DisembarkPerkPrefab perk in perks.Team2Perks)
                {
                    if (ignorePerksThatCanNotApplyWithoutSubmarine && perk.PerkBehaviors.Any(static p => !p.CanApplyWithoutSubmarine())) { continue; }

                    bool anyCanNotApply = perk.PerkBehaviors.Any(p => !p.CanApply(team2Sub));

                    if (anyCanNotApply)
                    {
                        incompatibleTeam2Perks.Add(perk);
                        hasIncompatiblePerks = true;
                    }
                }
            }

            incompatiblePerks = new PerkCollection(incompatibleTeam1Perks.ToImmutable(), incompatibleTeam2Perks.ToImmutable());
            return hasIncompatiblePerks;
        }

        private bool isRoundStartWarningActive;

        private void AbortStartGameIfWarningActive()
        {
            isRoundStartWarningActive = false;
            //reset autorestart countdown to give the clients time to reselect perks
            if (ServerSettings.AutoRestart) 
            { 
                ServerSettings.AutoRestartTimer = Math.Max(ServerSettings.AutoRestartInterval, 5.0f); 
            }
            //reset start round votes so we don't immediately attempt to restart
            foreach (var client in connectedClients)
            {
                client.SetVote(VoteType.StartRound, false);
            }

            int clientsReady = connectedClients.Count(c => c.GetVote<bool>(VoteType.StartRound));

            GameMain.NetLobbyScreen.LastUpdateID++;

            CoroutineManager.StopCoroutines(nameof(WarnAndDelayStartGame));
        }

        private IEnumerable<CoroutineStatus> WarnAndDelayStartGame(PerkCollection incompatiblePerks, SubmarineInfo selectedSub, Option<SubmarineInfo> selectedEnemySub, SubmarineInfo selectedShuttle, GameModePreset selectedMode)
        {
            isRoundStartWarningActive = true;
            const float warningDuration = 15.0f;

            SerializableDateTime waitUntilTime = SerializableDateTime.UtcNow + TimeSpan.FromSeconds(warningDuration);
            if (connectedClients.Any())
            {
                IWriteMessage msg = new WriteOnlyMessage().WithHeader(ServerPacketHeader.WARN_STARTGAME);
                INetSerializableStruct warnData = new RoundStartWarningData(
                    RoundStartsAnywaysTimeInSeconds: warningDuration,
                    Team1Sub: selectedSub.Name,
                    Team1IncompatiblePerks: ToolBox.PrefabCollectionToUintIdentifierArray(incompatiblePerks.Team1Perks),
                    Team2Sub: selectedEnemySub.Fallback(selectedSub).Name,
                    Team2IncompatiblePerks: ToolBox.PrefabCollectionToUintIdentifierArray(incompatiblePerks.Team2Perks));
                msg.WriteNetSerializableStruct(warnData);

                foreach (Client c in connectedClients)
                {
                    serverPeer.Send(msg, c.Connection, DeliveryMethod.Reliable);
                }
            }

            while (waitUntilTime > SerializableDateTime.UtcNow)
            {
                yield return CoroutineStatus.Running;
            }

            CoroutineManager.StartCoroutine(InitiateStartGame(selectedSub, selectedEnemySub, selectedShuttle, selectedMode), "InitiateStartGame");
            yield return CoroutineStatus.Success;
        }

        private IEnumerable<CoroutineStatus> InitiateStartGame(SubmarineInfo selectedSub, Option<SubmarineInfo> selectedEnemySub, SubmarineInfo selectedShuttle, GameModePreset selectedMode)
        {
            isRoundStartWarningActive = false;
            initiatedStartGame = true;

            if (connectedClients.Any())
            {
                IWriteMessage msg = new WriteOnlyMessage();
                msg.WriteByte((byte)ServerPacketHeader.QUERY_STARTGAME);

                msg.WriteString(selectedSub.Name);
                msg.WriteString(selectedSub.MD5Hash.StringRepresentation);

                if (selectedEnemySub.TryUnwrap(out var enemySub))
                {
                    msg.WriteBoolean(true);
                    msg.WriteString(enemySub.Name);
                    msg.WriteString(enemySub.MD5Hash.StringRepresentation);
                }
                else
                {
                    msg.WriteBoolean(false);
                }

                msg.WriteBoolean(IsUsingRespawnShuttle());
                msg.WriteString(selectedShuttle.Name);
                msg.WriteString(selectedShuttle.MD5Hash.StringRepresentation);

                var campaign = GameMain.GameSession?.GameMode as MultiPlayerCampaign;
                msg.WriteByte(campaign == null ? (byte)0 : campaign.CampaignID);
                msg.WriteUInt16(campaign == null ? (UInt16)0 : campaign.LastSaveID);
                foreach (MultiPlayerCampaign.NetFlags flag in Enum.GetValues(typeof(MultiPlayerCampaign.NetFlags)))
                {
                    msg.WriteUInt16(campaign == null ? (UInt16)0 : campaign.GetLastUpdateIdForFlag(flag));
                }

                connectedClients.ForEach(c => c.ReadyToStart = false);

                foreach (NetworkConnection conn in connectedClients.Select(c => c.Connection))
                {
                    serverPeer.Send(msg, conn, DeliveryMethod.Reliable);
                }

                //give the clients a few seconds to request missing sub/shuttle files before starting the round
                float waitForResponseTimer = 5.0f;
                while (connectedClients.Any(c => !c.ReadyToStart) && waitForResponseTimer > 0.0f)
                {
                    waitForResponseTimer -= CoroutineManager.DeltaTime;
                    yield return CoroutineStatus.Running;
                }

                if (FileSender.ActiveTransfers.Count > 0)
                {
                    float waitForTransfersTimer = 20.0f;
                    while (FileSender.ActiveTransfers.Count > 0 && waitForTransfersTimer > 0.0f)
                    {
                        waitForTransfersTimer -= CoroutineManager.DeltaTime;
                        yield return CoroutineStatus.Running;
                    }
                }
            }

            startGameCoroutine = GameMain.Instance.ShowLoading(StartGame(selectedSub, selectedShuttle, selectedEnemySub, selectedMode, CampaignSettings.Empty), false);

            yield return CoroutineStatus.Success;
        }

        private IEnumerable<CoroutineStatus> StartGame(SubmarineInfo selectedSub, SubmarineInfo selectedShuttle, Option<SubmarineInfo> selectedEnemySub, GameModePreset selectedMode, CampaignSettings settings)
        {
            PerkCollection perkCollection = PerkCollection.Empty;

            if (GameSession.ShouldApplyDisembarkPoints(selectedMode))
            {
                perkCollection = GameSession.GetPerks();
            }

            entityEventManager.Clear();

            roundStartSeed = DateTime.Now.Millisecond;
            Rand.SetSyncedSeed(roundStartSeed);

            int teamCount = 1;
            bool isPvP = selectedMode == GameModePreset.PvP;
            MultiPlayerCampaign campaign = selectedMode == GameMain.GameSession?.GameMode.Preset ?
                GameMain.GameSession?.GameMode as MultiPlayerCampaign : null;

            if (campaign != null && campaign.Map == null)
            {
                initiatedStartGame = false;
                startGameCoroutine = null;
                string errorMsg = "Starting the round failed. Campaign was still active, but the map has been disposed. Try selecting another game mode.";
                DebugConsole.ThrowError(errorMsg);
                GameAnalyticsManager.AddErrorEventOnce("GameServer.StartGame:InvalidCampaignState", GameAnalyticsManager.ErrorSeverity.Error, errorMsg);
                if (OwnerConnection != null)
                {
                    SendDirectChatMessage(errorMsg, connectedClients.Find(c => c.Connection == OwnerConnection), ChatMessageType.Error);
                }
                yield return CoroutineStatus.Failure;
            }

            bool initialSuppliesSpawned = false;
            //don't instantiate a new gamesession if we're playing a campaign
            if (campaign == null || GameMain.GameSession == null)
            {
                traitorManager = new TraitorManager(this);
                GameMain.GameSession = new GameSession(selectedSub, selectedEnemySub, CampaignDataPath.Empty, selectedMode, settings, GameMain.NetLobbyScreen.LevelSeed, missionTypes: GameMain.NetLobbyScreen.MissionTypes);
            }
            else
            {
                initialSuppliesSpawned = GameMain.GameSession.SubmarineInfo is { InitialSuppliesSpawned: true };
            }

            if (GameMain.GameSession.GameMode is PvPMode pvpMode)
            {
                teamCount = 2;
                
                // In Player Preference mode, team assignments are handled only at this point, and in Player Choice mode,
                // everyone should already have chosen a team, ie. players can no longer make choices now and we should
                // finalize all the team assignments without further delay.
                RefreshPvpTeamAssignments(assignUnassignedNow: true, autoBalanceNow: true);
            }
            else
            {
                connectedClients.ForEach(c => c.TeamID = CharacterTeamType.Team1);
            }

            bool missionAllowRespawn = GameMain.GameSession.GameMode is not MissionMode missionMode || missionMode.Missions.All(m => m.AllowRespawning);
            foreach (var mission in GameMain.GameSession.GameMode.Missions)
            {
                if (mission.Prefab.ForceRespawnMode.HasValue)
                {
                    ServerSettings.RespawnMode = mission.Prefab.ForceRespawnMode.Value;
                }
            }


            List<Client> playingClients = GetPlayingClients();
            if (campaign != null)
            {
                if (campaign.Map == null)
                {
                    throw new Exception("Campaign map was null.");
                }
                if (campaign.NextLevel == null)
                {
                    string errorMsg = "Failed to start a campaign round (next level not set).";
                    DebugConsole.ThrowError(errorMsg);
                    GameAnalyticsManager.AddErrorEventOnce("GameServer.StartGame:InvalidCampaignState", GameAnalyticsManager.ErrorSeverity.Error, errorMsg);
                    if (OwnerConnection != null)
                    {
                        SendDirectChatMessage(errorMsg, connectedClients.Find(c => c.Connection == OwnerConnection), ChatMessageType.Error);
                    }
                    yield return CoroutineStatus.Failure;
                }

                SendStartMessage(roundStartSeed, campaign.NextLevel.Seed, GameMain.GameSession, connectedClients, includesFinalize: false);
                GameMain.GameSession.StartRound(campaign.NextLevel, startOutpost: campaign.GetPredefinedStartOutpost(), mirrorLevel: campaign.MirrorLevel);
                SubmarineSwitchLoad = false;
                campaign.AssignClientCharacterInfos(connectedClients);
                Log("Game mode: " + selectedMode.Name.Value, ServerLog.MessageType.ServerMessage);
                Log("Submarine: " + GameMain.GameSession.SubmarineInfo.Name, ServerLog.MessageType.ServerMessage);
                Log("Level seed: " + campaign.NextLevel.Seed, ServerLog.MessageType.ServerMessage);
            }
            else
            {
                SendStartMessage(roundStartSeed, GameMain.NetLobbyScreen.LevelSeed, GameMain.GameSession, connectedClients, false);
                GameMain.GameSession.StartRound(GameMain.NetLobbyScreen.LevelSeed, ServerSettings.SelectedLevelDifficulty, forceBiome: ServerSettings.Biome);
                Log("Game mode: " + selectedMode.Name.Value, ServerLog.MessageType.ServerMessage);
                Log("Submarine: " + selectedSub.Name, ServerLog.MessageType.ServerMessage);
                Log("Level seed: " + GameMain.NetLobbyScreen.LevelSeed, ServerLog.MessageType.ServerMessage);
            }

            foreach (Mission mission in GameMain.GameSession.Missions)
            {
                Log("Mission: " + mission.Prefab.Name.Value, ServerLog.MessageType.ServerMessage);
            }

            if (GameMain.GameSession.SubmarineInfo.IsFileCorrupted)
            {
                CoroutineManager.StopCoroutines(startGameCoroutine);
                initiatedStartGame = false;
                SendChatMessage(TextManager.FormatServerMessage($"SubLoadError~[subname]={GameMain.GameSession.SubmarineInfo.Name}"), ChatMessageType.Error);
                yield return CoroutineStatus.Failure;
            }

            bool isOutpost = campaign != null && campaign.NextLevel?.Type == LevelData.LevelType.Outpost;
            if (ServerSettings.RespawnMode != RespawnMode.BetweenRounds && missionAllowRespawn)
            {
                RespawnManager = new RespawnManager(this, ServerSettings.UseRespawnShuttle && !isOutpost ? selectedShuttle : null);
            }
            if (campaign != null)
            {
                campaign.CargoManager.CreatePurchasedItems();
                //midround-joining clients need to be informed of pending/new hires at outposts
                if (isOutpost) { campaign.SendCrewState(); }
            }

            if (GameMain.GameSession.Missions.None(m => !m.Prefab.AllowOutpostNPCs))
            {
                Level.Loaded?.SpawnNPCs();
            }
            Level.Loaded?.SpawnCorpses();
            Level.Loaded?.PrepareBeaconStation();
            AutoItemPlacer.SpawnItems(campaign?.Settings.StartItemSet);

            CrewManager crewManager = GameMain.GameSession.CrewManager;

            bool hadBots = true;

            List<Character> team1Characters = new(),
                            team2Characters = new();

            //assign jobs and spawnpoints separately for each team
            for (int n = 0; n < teamCount; n++)
            {
                var teamID = n == 0 ? CharacterTeamType.Team1 : CharacterTeamType.Team2;

                Submarine teamSub = Submarine.MainSubs[n];
                if (teamSub != null)
                {
                    teamSub.TeamID = teamID;
                    foreach (Item item in Item.ItemList)
                    {
                        if (item.Submarine == null) { continue; }
                        if (item.Submarine != teamSub && !teamSub.DockedTo.Contains(item.Submarine)) { continue; }
                        foreach (WifiComponent wifiComponent in item.GetComponents<WifiComponent>())
                        {
                            wifiComponent.TeamID = teamSub.TeamID;
                        }
                    }
                    foreach (Submarine sub in teamSub.DockedTo)
                    {
                        if (sub.Info.Type != SubmarineType.Player) { continue; }
                        sub.TeamID = teamID;
                    }
                }

                //find the clients in this team
                List<Client> teamClients = teamCount == 1 ? new List<Client>(playingClients) : playingClients.FindAll(c => c.TeamID == teamID);
                if (ServerSettings.AllowSpectating)
                {
                    teamClients.RemoveAll(c => c.SpectateOnly);
                }
                //always allow the server owner to spectate even if it's disallowed in server settings
                teamClients.RemoveAll(c => c.Connection == OwnerConnection && c.SpectateOnly);
                // Clients with last character permanently dead spectate regardless of server settings
                teamClients.RemoveAll(c => c.CharacterInfo != null && c.CharacterInfo.PermanentlyDead);

                //if (!teamClients.Any() && n > 0) { continue; }

                AssignJobs(teamClients);

                List<CharacterInfo> characterInfos = new List<CharacterInfo>();
                foreach (Client client in teamClients)
                {
                    client.NeedsMidRoundSync = false;

                    client.PendingPositionUpdates.Clear();
                    client.EntityEventLastSent.Clear();
                    client.LastSentEntityEventID = 0;
                    client.LastRecvEntityEventID = 0;
                    client.UnreceivedEntityEventCount = 0;

                    if (client.CharacterInfo == null)
                    {
                        client.CharacterInfo = new CharacterInfo(CharacterPrefab.HumanSpeciesName, client.Name);
                    }
                    characterInfos.Add(client.CharacterInfo);
                    if (client.CharacterInfo.Job == null || 
                        client.CharacterInfo.Job.Prefab != client.AssignedJob.Prefab ||
                        //always recreate the job to reset the skills in non-campaign modes
                        campaign == null)
                    {
                        client.CharacterInfo.Job = new Job(client.AssignedJob.Prefab, isPvP, Rand.RandSync.Unsynced, client.AssignedJob.Variant);
                    }
                }

                List<CharacterInfo> bots = new List<CharacterInfo>();
                // do not load new bots if we already have them
                if (!crewManager.HasBots || campaign == null)
                {
                    int botsToSpawn = ServerSettings.BotSpawnMode == BotSpawnMode.Fill ? ServerSettings.BotCount - characterInfos.Count : ServerSettings.BotCount;
                    for (int i = 0; i < botsToSpawn; i++)
                    {
                        var botInfo = new CharacterInfo(CharacterPrefab.HumanSpeciesName)
                        {
                            TeamID = teamID
                        };
                        characterInfos.Add(botInfo);
                        bots.Add(botInfo);
                    }

                    AssignBotJobs(bots, teamID, isPvP);
                    foreach (CharacterInfo bot in bots)
                    {
                        crewManager.AddCharacterInfo(bot);
                    }

                    crewManager.HasBots = true;
                    hadBots = false;                    
                }

                List<WayPoint> spawnWaypoints = null;
                List<WayPoint> mainSubWaypoints = teamSub != null ? WayPoint.SelectCrewSpawnPoints(characterInfos, Submarine.MainSubs[n]).ToList() : null;
                if (Level.Loaded != null && Level.Loaded.ShouldSpawnCrewInsideOutpost())
                {
                    spawnWaypoints = WayPoint.GetOutpostSpawnPoints(teamID);
                    while (spawnWaypoints.Count > characterInfos.Count)
                    {
                        spawnWaypoints.RemoveAt(Rand.Int(spawnWaypoints.Count));
                    }
                    while (spawnWaypoints.Any() && spawnWaypoints.Count < characterInfos.Count)
                    {
                        spawnWaypoints.Add(spawnWaypoints[Rand.Int(spawnWaypoints.Count)]);
                    }
                }
                if (teamSub != null)
                {
                    if (spawnWaypoints == null || !spawnWaypoints.Any())
                    {
                        spawnWaypoints = mainSubWaypoints;
                    }
                    Debug.Assert(spawnWaypoints.Count == mainSubWaypoints.Count);
                }

                for (int i = 0; i < teamClients.Count; i++)
                {
                    //if there's a main sub waypoint available (= the spawnpoint the character would've spawned at, if they'd spawned in the main sub instead of the outpost),
                    //give the job items based on that spawnpoint
                    WayPoint jobItemSpawnPoint = mainSubWaypoints != null ? mainSubWaypoints[i] : spawnWaypoints[i];

                    Character spawnedCharacter = Character.Create(teamClients[i].CharacterInfo, spawnWaypoints[i].WorldPosition, teamClients[i].CharacterInfo.Name, isRemotePlayer: true, hasAi: false);
                    spawnedCharacter.AnimController.Frozen = true;
                    spawnedCharacter.TeamID = teamID;
                    teamClients[i].Character = spawnedCharacter;
                    var characterData = campaign?.GetClientCharacterData(teamClients[i]);
                    if (characterData == null)
                    {
                        spawnedCharacter.GiveJobItems(GameMain.GameSession.GameMode is PvPMode, jobItemSpawnPoint);
                        if (campaign != null)
                        {
                            characterData = campaign.SetClientCharacterData(teamClients[i]);
                            characterData.HasSpawned = true;
                        }
                    }
                    else
                    {
                        if (!characterData.HasItemData && !characterData.CharacterInfo.StartItemsGiven)
                        {
                            //clients who've chosen to spawn with the respawn penalty can have CharacterData without inventory data
                            spawnedCharacter.GiveJobItems(GameMain.GameSession.GameMode is PvPMode, jobItemSpawnPoint);
                        }
                        else
                        {
                            characterData.SpawnInventoryItems(spawnedCharacter, spawnedCharacter.Inventory);
                        }
                        characterData.ApplyHealthData(spawnedCharacter);
                        characterData.ApplyOrderData(spawnedCharacter);
                        characterData.ApplyWalletData(spawnedCharacter);
                        spawnedCharacter.GiveIdCardTags(jobItemSpawnPoint);
                        spawnedCharacter.LoadTalents();
                        characterData.HasSpawned = true;
                    }
                    if (GameMain.GameSession?.GameMode is MultiPlayerCampaign mpCampaign && spawnedCharacter.Info != null)
                    {
                        spawnedCharacter.Info.SetExperience(Math.Max(spawnedCharacter.Info.ExperiencePoints, mpCampaign.GetSavedExperiencePoints(teamClients[i])));
                        mpCampaign.ClearSavedExperiencePoints(teamClients[i]);

                        if (spawnedCharacter.Info.LastRewardDistribution.TryUnwrap(out int salary))
                        {
                            spawnedCharacter.Wallet.SetRewardDistribution(salary);
                        }
                    }

                    spawnedCharacter.SetOwnerClient(teamClients[i]);
                    AddCharacterToList(teamID, spawnedCharacter);
                }

                for (int i = teamClients.Count; i < teamClients.Count + bots.Count; i++)
                {
                    WayPoint jobItemSpawnPoint = mainSubWaypoints != null ? mainSubWaypoints[i] : spawnWaypoints[i];
                    Character spawnedCharacter = Character.Create(characterInfos[i], spawnWaypoints[i].WorldPosition, characterInfos[i].Name, isRemotePlayer: false, hasAi: true);
                    spawnedCharacter.TeamID = teamID;
                    spawnedCharacter.GiveJobItems(GameMain.GameSession.GameMode is PvPMode, jobItemSpawnPoint);
                    spawnedCharacter.GiveIdCardTags(jobItemSpawnPoint);
                    spawnedCharacter.Info.InventoryData = new XElement("inventory");
                    spawnedCharacter.Info.StartItemsGiven = true;
                    spawnedCharacter.SaveInventory();
                    spawnedCharacter.LoadTalents();
                    AddCharacterToList(teamID, spawnedCharacter);
                }

                void AddCharacterToList(CharacterTeamType team, Character character)
                {
                    switch (team)
                    {
                        case CharacterTeamType.Team1:
                            team1Characters.Add(character);
                            break;
                        case CharacterTeamType.Team2:
                            team2Characters.Add(character);
                            break;
                    }
                }
            }

            if (campaign != null && crewManager.HasBots)
            {
                if (hadBots)
                {
                    //loaded existing bots -> init them
                    crewManager.InitRound();
                }
                else
                {
                    //created new bots -> save them
                    SaveUtil.SaveGame(GameMain.GameSession.DataPath);
                }
            }

            campaign?.LoadPets();
            campaign?.LoadActiveOrders();

            campaign?.CargoManager.InitPurchasedIDCards();

            if (campaign == null || !initialSuppliesSpawned)
            {
                foreach (Submarine sub in Submarine.MainSubs)
                {
                    if (sub == null) { continue; }
                    List<PurchasedItem> spawnList = new List<PurchasedItem>();
                    foreach (KeyValuePair<ItemPrefab, int> kvp in ServerSettings.ExtraCargo)
                    {
                        spawnList.Add(new PurchasedItem(kvp.Key, kvp.Value, buyer: null));
                    }
                    CargoManager.DeliverItemsToSub(spawnList, sub, cargoManager: null);
                }
            }

            TraitorManager.Initialize(GameMain.GameSession.EventManager, Level.Loaded);
            TraitorManager.Enabled = Rand.Range(0.0f, 1.0f) < ServerSettings.TraitorProbability;

            GameAnalyticsManager.AddDesignEvent("Traitors:" + (TraitorManager == null ? "Disabled" : "Enabled"));

            perkCollection.ApplyAll(team1Characters, team2Characters);

            yield return CoroutineStatus.Running;

            Voting.ResetVotes(GameMain.Server.ConnectedClients, resetKickVotes: false);

            GameMain.GameScreen.Select();

            Log("Round started.", ServerLog.MessageType.ServerMessage);

            GameStarted = true;
            initiatedStartGame = false;
            GameMain.ResetFrameTime();

            LastClientListUpdateID++;

            roundStartTime = DateTime.Now;

            startGameCoroutine = null;
            yield return CoroutineStatus.Success;
        }

        private void SendStartMessage(int seed, string levelSeed, GameSession gameSession, List<Client> clients, bool includesFinalize)
        {
            foreach (Client client in clients)
            {
                SendStartMessage(seed, levelSeed, gameSession, client, includesFinalize);
            }
        }

        private void SendStartMessage(int seed, string levelSeed, GameSession gameSession, Client client, bool includesFinalize)
        {
            IWriteMessage msg = new WriteOnlyMessage();
            msg.WriteByte((byte)ServerPacketHeader.STARTGAME);
            msg.WriteInt32(seed);
            msg.WriteIdentifier(gameSession.GameMode.Preset.Identifier);
            bool missionAllowRespawn = GameMain.GameSession.GameMode is not MissionMode missionMode || !missionMode.Missions.Any(m => !m.AllowRespawning);
            msg.WriteBoolean(ServerSettings.RespawnMode != RespawnMode.BetweenRounds && missionAllowRespawn);
            msg.WriteBoolean(ServerSettings.AllowDisguises);
            msg.WriteBoolean(ServerSettings.AllowRewiring);
            msg.WriteBoolean(ServerSettings.AllowImmediateItemDelivery);
            msg.WriteBoolean(ServerSettings.AllowFriendlyFire);
            msg.WriteBoolean(ServerSettings.AllowDragAndDropGive);
            msg.WriteBoolean(ServerSettings.LockAllDefaultWires);
            msg.WriteBoolean(ServerSettings.AllowLinkingWifiToChat);
            msg.WriteInt32(ServerSettings.MaximumMoneyTransferRequest);
            msg.WriteByte((byte)ServerSettings.RespawnMode);
            msg.WriteBoolean(IsUsingRespawnShuttle());
            msg.WriteByte((byte)ServerSettings.LosMode);
            msg.WriteByte((byte)ServerSettings.ShowEnemyHealthBars);
            msg.WriteBoolean(includesFinalize); msg.WritePadBits();

            ServerSettings.WriteMonsterEnabled(msg);

            if (GameMain.GameSession?.GameMode is not MultiPlayerCampaign campaign)
            {
                msg.WriteString(levelSeed);
                msg.WriteSingle(ServerSettings.SelectedLevelDifficulty);
                msg.WriteIdentifier(ServerSettings.Biome == "Random".ToIdentifier() ? Identifier.Empty : ServerSettings.Biome);
                msg.WriteString(gameSession.SubmarineInfo.Name);
                msg.WriteString(gameSession.SubmarineInfo.MD5Hash.StringRepresentation);
                var selectedShuttle = GameStarted && RespawnManager != null && RespawnManager.UsingShuttle ? 
                    RespawnManager.RespawnShuttles.First().Info : GameMain.NetLobbyScreen.SelectedShuttle;
                msg.WriteString(selectedShuttle.Name);
                msg.WriteString(selectedShuttle.MD5Hash.StringRepresentation);

                if (gameSession.EnemySubmarineInfo is { } enemySub)
                {
                    msg.WriteBoolean(true);
                    msg.WriteString(enemySub.Name);
                    msg.WriteString(enemySub.MD5Hash.StringRepresentation);
                }
                else
                {
                    msg.WriteBoolean(false);
                }

                msg.WriteByte((byte)GameMain.GameSession.GameMode.Missions.Count());
                foreach (Mission mission in GameMain.GameSession.GameMode.Missions)
                {
                    msg.WriteUInt32(mission.Prefab.UintIdentifier);
                }
            }
            else
            {
                int nextLocationIndex = campaign.Map.Locations.FindIndex(l => l.LevelData == campaign.NextLevel);
                int nextConnectionIndex = campaign.Map.Connections.FindIndex(c => c.LevelData == campaign.NextLevel);
                msg.WriteByte(campaign.CampaignID);
                msg.WriteUInt16(campaign.LastSaveID);
                msg.WriteInt32(nextLocationIndex);
                msg.WriteInt32(nextConnectionIndex);
                msg.WriteInt32(campaign.Map.SelectedLocationIndex);
                msg.WriteBoolean(campaign.MirrorLevel);
            }

            if (includesFinalize)
            {
                WriteRoundStartFinalize(msg, client);
            }

            serverPeer.Send(msg, client.Connection, DeliveryMethod.Reliable);
        }

        private bool TrySendCampaignSetupInfo(Client client)
        {
            if (!CampaignMode.AllowedToManageCampaign(client, ClientPermissions.ManageRound)) { return false; }

            using (dosProtection.Pause(client))
            {
                const int MaxSaves = 255;
                var saveInfos = SaveUtil.GetSaveFiles(SaveUtil.SaveType.Multiplayer, includeInCompatible: false);
                IWriteMessage msg = new WriteOnlyMessage();
                msg.WriteByte((byte)ServerPacketHeader.CAMPAIGN_SETUP_INFO);
                msg.WriteByte((byte)Math.Min(saveInfos.Count, MaxSaves));
                for (int i = 0; i < saveInfos.Count && i < MaxSaves; i++)
                {
                    msg.WriteNetSerializableStruct(saveInfos[i]);
                }
                serverPeer.Send(msg, client.Connection, DeliveryMethod.Reliable);
            }

            return true;
        }

        private bool IsUsingRespawnShuttle()
        {
           return ServerSettings.UseRespawnShuttle || (GameStarted && RespawnManager != null && RespawnManager.UsingShuttle);
        }

        private void SendRoundStartFinalize(Client client)
        {
            IWriteMessage msg = new WriteOnlyMessage();
            msg.WriteByte((byte)ServerPacketHeader.STARTGAMEFINALIZE);
            WriteRoundStartFinalize(msg, client);
            serverPeer.Send(msg, client.Connection, DeliveryMethod.Reliable);
        }

        private void WriteRoundStartFinalize(IWriteMessage msg, Client client)
        {
            //tell the client what content files they should preload
            var contentToPreload = GameMain.GameSession.EventManager.GetFilesToPreload();
            msg.WriteUInt16((ushort)contentToPreload.Count());
            foreach (ContentFile contentFile in contentToPreload)
            {
                msg.WriteString(contentFile.Path.Value);
            }
            msg.WriteInt32(Submarine.MainSub?.Info.EqualityCheckVal ?? 0);
            msg.WriteByte((byte)GameMain.GameSession.Missions.Count());
            foreach (Mission mission in GameMain.GameSession.Missions)
            {
                msg.WriteIdentifier(mission.Prefab.Identifier);
            }
            foreach (Level.LevelGenStage stage in Enum.GetValues(typeof(Level.LevelGenStage)).OfType<Level.LevelGenStage>().OrderBy(s => s))
            {
                msg.WriteInt32(GameMain.GameSession.Level.EqualityCheckValues[stage]);
            }
            foreach (Mission mission in GameMain.GameSession.Missions)
            {
                mission.ServerWriteInitial(msg, client);
            }
            msg.WriteBoolean(GameMain.GameSession.CrewManager != null);
            GameMain.GameSession.CrewManager?.ServerWriteActiveOrders(msg);

            msg.WriteBoolean(GameSession.ShouldApplyDisembarkPoints(GameMain.GameSession.GameMode?.Preset));
        }

        public void EndGame(CampaignMode.TransitionType transitionType = CampaignMode.TransitionType.None, bool wasSaved = false, IEnumerable<Mission> missions = null)
        {
            if (GameStarted)
            {
                if (GameSettings.CurrentConfig.VerboseLogging)
                {
                    Log("Ending the round...\n" + Environment.StackTrace.CleanupStackTrace(), ServerLog.MessageType.ServerMessage);

                }
                else
                {
                    Log("Ending the round...", ServerLog.MessageType.ServerMessage);
                }
            }

            string endMessage = TextManager.FormatServerMessage("RoundSummaryRoundHasEnded");
            missions ??= GameMain.GameSession.Missions.ToList();
            if (GameMain.GameSession is { IsRunning: true })
            {
                GameMain.GameSession.EndRound(endMessage);
            }
            TraitorManager.TraitorResults? traitorResults = traitorManager?.GetEndResults() ?? null;

            EndRoundTimer = 0.0f;

            if (ServerSettings.AutoRestart)
            {
                ServerSettings.AutoRestartTimer = ServerSettings.AutoRestartInterval;
                //send a netlobby update to get the clients' autorestart timers up to date
                GameMain.NetLobbyScreen.LastUpdateID++;
            }

            if (ServerSettings.SaveServerLogs) { ServerSettings.ServerLog.Save(); }

            GameMain.GameScreen.Cam.TargetPos = Vector2.Zero;

            entityEventManager.Clear();
            foreach (Client c in connectedClients)
            {
                c.EntityEventLastSent.Clear();
                c.PendingPositionUpdates.Clear();
                c.PositionUpdateLastSent.Clear();
            }

            if (GameStarted)
            {
                KarmaManager.OnRoundEnded();
            }

            RespawnManager = null;
            GameStarted = false;

            if (connectedClients.Count > 0)
            {
                IWriteMessage msg = new WriteOnlyMessage();
                msg.WriteByte((byte)ServerPacketHeader.ENDGAME);
                msg.WriteByte((byte)transitionType);
                msg.WriteBoolean(wasSaved);
                msg.WriteString(endMessage);
                msg.WriteByte((byte)missions.Count());
                foreach (Mission mission in missions)
                {
                    msg.WriteBoolean(mission.Completed);
                }
                msg.WriteByte(GameMain.GameSession?.WinningTeam == null ? (byte)0 : (byte)GameMain.GameSession.WinningTeam);

                msg.WriteBoolean(traitorResults.HasValue);
                if (traitorResults.HasValue)
                {
                    msg.WriteNetSerializableStruct(traitorResults.Value);
                }

                foreach (Client client in connectedClients)
                {
                    serverPeer.Send(msg, client.Connection, DeliveryMethod.Reliable);
                    client.Character?.Info?.ClearCurrentOrders();
                    client.Character = null;
                    client.HasSpawned = false;
                    client.InGame = false;
                    client.WaitForNextRoundRespawn = null;
                }
            }

            entityEventManager.Clear();
            Submarine.Unload();
            GameMain.NetLobbyScreen.Select();
            Log("Round ended.", ServerLog.MessageType.ServerMessage);

            GameMain.NetLobbyScreen.RandomizeSettings();
        }

        public override void AddChatMessage(ChatMessage message)
        {
            if (string.IsNullOrEmpty(message.Text)) { return; }
            string logMsg;
            if (message.SenderClient != null)
            {
                logMsg = GameServer.ClientLogName(message.SenderClient) + ": " + message.TranslatedText;
            }
            else
            {
                logMsg = message.TextWithSender;
            }

            if (message.Sender is Character sender)
            {
                sender.TextChatVolume = 1f;
            }

            Log(logMsg, ServerLog.MessageType.Chat);
        }

        private bool ReadClientNameChange(Client c, IReadMessage inc)
        {
            UInt16 nameId = inc.ReadUInt16();
            string newName = inc.ReadString();
            Identifier newJob = inc.ReadIdentifier();
            CharacterTeamType newTeam = (CharacterTeamType)inc.ReadByte();

            if (c == null || string.IsNullOrEmpty(newName) || !NetIdUtils.IdMoreRecent(nameId, c.NameId)) { return false; }

            if (!newJob.IsEmpty)
            {
                if (!JobPrefab.Prefabs.TryGet(newJob, out JobPrefab newJobPrefab) || newJobPrefab.HiddenJob)
                {
                    newJob = Identifier.Empty;
                }
            }

            if (newName == c.Name && newJob == c.PreferredJob && newTeam == c.PreferredTeam) { return false; }

            c.NameId = nameId;
            c.PreferredJob = newJob;
            if (newTeam != c.PreferredTeam)
            {
                c.PreferredTeam = newTeam;
                RefreshPvpTeamAssignments();
            }

            var timeSinceNameChange = DateTime.Now - c.LastNameChangeTime;
            if (timeSinceNameChange < Client.NameChangeCoolDown && newName != c.Name)
            {
                //only send once per second at most to prevent using this for spamming
                if (timeSinceNameChange.TotalSeconds > 1)
                {
                    var coolDownRemaining = Client.NameChangeCoolDown - timeSinceNameChange;
                    SendDirectChatMessage($"ServerMessage.NameChangeFailedCooldownActive~[seconds]={(int)coolDownRemaining.TotalSeconds}", c);
                    LastClientListUpdateID++;
                    //increment the ID to make sure the current server-side name is treated as the "latest",
                    //and the client correctly reverts back to the old name
                    c.NameId++;
                }
                c.RejectedName = newName;
                return false;
            }

            return TryChangeClientName(c, newName);
        }

        public bool TryChangeClientName(Client c, string newName)
        {
            newName = Client.SanitizeName(newName);
            if (newName != c.Name && !string.IsNullOrEmpty(newName) && IsNameValid(c, newName))
            {
                c.LastNameChangeTime = DateTime.Now;
                string oldName = c.Name;
                c.Name = newName;
                c.RejectedName = string.Empty;
                SendChatMessage($"ServerMessage.NameChangeSuccessful~[oldname]={oldName}~[newname]={newName}", ChatMessageType.Server);
                LastClientListUpdateID++;
                return true;
            }
            else
            {
                //update client list even if the name cannot be changed to the one sent by the client,
                //so the client will be informed what their actual name is
                LastClientListUpdateID++;
                return false;
            }
        }

        public bool IsNameValid(Client c, string newName)
        {
            if (c.Connection != OwnerConnection)
            {
                if (!Client.IsValidName(newName, ServerSettings))
                {
                    SendDirectChatMessage($"ServerMessage.NameChangeFailedSymbols~[newname]={newName}", c, ChatMessageType.ServerMessageBox);
                    return false;
                }
                if (Homoglyphs.Compare(newName.ToLower(), ServerName.ToLower()))
                {
                    SendDirectChatMessage($"ServerMessage.NameChangeFailedServerTooSimilar~[newname]={newName}", c, ChatMessageType.ServerMessageBox);
                    return false;
                }

                if (c.KickVoteCount > 0)
                {
                    SendDirectChatMessage($"ServerMessage.NameChangeFailedVoteKick~[newname]={newName}", c, ChatMessageType.ServerMessageBox);
                    return false;
                }
            }

            Client nameTakenByClient = ConnectedClients.Find(c2 => c != c2 && Homoglyphs.Compare(c2.Name.ToLower(), newName.ToLower()));
            if (nameTakenByClient != null)
            {
                SendDirectChatMessage($"ServerMessage.NameChangeFailedClientTooSimilar~[newname]={newName}~[takenname]={nameTakenByClient.Name}", c, ChatMessageType.ServerMessageBox);
                return false;
            }

            Character nameTakenByCharacter =
                GameSession.GetSessionCrewCharacters(CharacterType.Both).FirstOrDefault(c2 => c2 != c.Character && Homoglyphs.Compare(c2.Name.ToLower(), newName.ToLower()));
            if (nameTakenByCharacter != null)
            {
                SendDirectChatMessage($"ServerMessage.NameChangeFailedClientTooSimilar~[newname]={newName}~[takenname]={nameTakenByCharacter.Name}", c, ChatMessageType.ServerMessageBox);
                return false;
            }
            return true;
        }

        public override void KickPlayer(string playerName, string reason)
        {
            Client client = connectedClients.Find(c =>
                c.Name.Equals(playerName, StringComparison.OrdinalIgnoreCase) ||
                (c.Character != null && c.Character.Name.Equals(playerName, StringComparison.OrdinalIgnoreCase)));

            KickClient(client, reason);
        }

        public void KickClient(NetworkConnection conn, string reason)
        {
            if (conn == OwnerConnection) return;

            Client client = connectedClients.Find(c => c.Connection == conn);
            KickClient(client, reason);
        }

        public void KickClient(Client client, string reason, bool resetKarma = false)
        {
            if (client == null || client.Connection == OwnerConnection) { return; }

            if (resetKarma)
            {
                var previousPlayer = previousPlayers.Find(p => p.MatchesClient(client));
                if (previousPlayer != null)
                {
                    previousPlayer.Karma = Math.Max(previousPlayer.Karma, 50.0f);
                }
                client.Karma = Math.Max(client.Karma, 50.0f);
            }

            DisconnectClient(client, PeerDisconnectPacket.Kicked(reason));
        }

        public override void BanPlayer(string playerName, string reason, TimeSpan? duration = null)
        {
            Client client = connectedClients.Find(c =>
                c.Name.Equals(playerName, StringComparison.OrdinalIgnoreCase) ||
                (c.Character != null && c.Character.Name.Equals(playerName, StringComparison.OrdinalIgnoreCase)));

            if (client == null)
            {
                DebugConsole.ThrowError("Client \"" + playerName + "\" not found.");
                return;
            }

            BanClient(client, reason, duration);
        }

        public void BanClient(Client client, string reason, TimeSpan? duration = null)
        {
            if (client == null || client.Connection == OwnerConnection) { return; }

            var previousPlayer = previousPlayers.Find(p => p.MatchesClient(client));
            if (previousPlayer != null)
            {
                //reset karma to a neutral value, so if/when the ban is revoked the client wont get immediately punished by low karma again
                previousPlayer.Karma = Math.Max(previousPlayer.Karma, 50.0f);
            }
            client.Karma = Math.Max(client.Karma, 50.0f);

            DisconnectClient(client, PeerDisconnectPacket.Banned(reason));

            if (client.AccountInfo.AccountId.TryUnwrap(out var accountId))
            {
                ServerSettings.BanList.BanPlayer(client.Name, accountId, reason, duration);
            }
            else
            {
                ServerSettings.BanList.BanPlayer(client.Name, client.Connection.Endpoint, reason, duration);
            }
            foreach (var relatedId in client.AccountInfo.OtherMatchingIds)
            {
                ServerSettings.BanList.BanPlayer(client.Name, relatedId, reason, duration);
            }
        }

        public void BanPreviousPlayer(PreviousPlayer previousPlayer, string reason, TimeSpan? duration = null)
        {
            if (previousPlayer == null) { return; }

            //reset karma to a neutral value, so if/when the ban is revoked the client wont get immediately punished by low karma again
            previousPlayer.Karma = Math.Max(previousPlayer.Karma, 50.0f);

            ServerSettings.BanList.BanPlayer(previousPlayer.Name, previousPlayer.Address, reason, duration);
            if (previousPlayer.AccountInfo.AccountId.TryUnwrap(out var accountId))
            {
                ServerSettings.BanList.BanPlayer(previousPlayer.Name, accountId, reason, duration);
            }
            foreach (var relatedId in previousPlayer.AccountInfo.OtherMatchingIds)
            {
                ServerSettings.BanList.BanPlayer(previousPlayer.Name, relatedId, reason, duration);
            }

            string msg = $"ServerMessage.BannedFromServer~[client]={previousPlayer.Name}";
            if (!string.IsNullOrWhiteSpace(reason))
            {
                msg += $"/ /ServerMessage.Reason/: /{reason}";
            }
            SendChatMessage(msg, ChatMessageType.Server, changeType: PlayerConnectionChangeType.Banned);
        }

        public override void UnbanPlayer(string playerName)
        {
            BannedPlayer bannedPlayer
                = ServerSettings.BanList.BannedPlayers.FirstOrDefault(bp => bp.Name == playerName);
            if (bannedPlayer is null) { return; }
            ServerSettings.BanList.UnbanPlayer(bannedPlayer.AddressOrAccountId);
        }

        public override void UnbanPlayer(Endpoint endpoint)
        {
            ServerSettings.BanList.UnbanPlayer(endpoint);
        }

        public void DisconnectClient(NetworkConnection senderConnection, PeerDisconnectPacket peerDisconnectPacket)
        {
            Client client = connectedClients.Find(x => x.Connection == senderConnection);
            if (client == null) { return; }

            DisconnectClient(client, peerDisconnectPacket);
        }

        public void DisconnectClient(Client client, PeerDisconnectPacket peerDisconnectPacket)
        {
            if (client == null) return;

            if (client.Character != null)
            {
                client.Character.ClientDisconnected = true;
                client.Character.ClearInputs();
            }

            client.Character = null;
            client.HasSpawned = false;
            client.WaitForNextRoundRespawn = null;
            client.InGame = false;

            var previousPlayer = previousPlayers.Find(p => p.MatchesClient(client));
            if (previousPlayer == null)
            {
                previousPlayer = new PreviousPlayer(client);
                previousPlayers.Add(previousPlayer);
            }

            if (peerDisconnectPacket.ShouldAttemptReconnect)
            {
                lock (clientsAttemptingToReconnectSoon)
                {
                    client.DeleteDisconnectedTimer = ServerSettings.KillDisconnectedTime;
                    clientsAttemptingToReconnectSoon.Add(client);
                }
            }
            
            previousPlayer.Name = client.Name;
            previousPlayer.Karma = client.Karma;
            previousPlayer.KarmaKickCount = client.KarmaKickCount;
            previousPlayer.KickVoters.Clear();
            foreach (Client c in connectedClients)
            {
                if (client.HasKickVoteFrom(c)) { previousPlayer.KickVoters.Add(c); }
            }

            client.Dispose();
            connectedClients.Remove(client);
            serverPeer.Disconnect(client.Connection, peerDisconnectPacket);

            KarmaManager.OnClientDisconnected(client);
            
            // A player disconnecting might impact PvP team assignments if still in the lobby
            if (!GameStarted)
            {
                RefreshPvpTeamAssignments();
            }

            UpdateVoteStatus();

            SendChatMessage(peerDisconnectPacket.ChatMessage(client).Value, ChatMessageType.Server, changeType: peerDisconnectPacket.ConnectionChangeType);

            UpdateCrewFrame();

            ServerSettings.ServerDetailsChanged = true;
            refreshMasterTimer = DateTime.Now;
        }

        private void UpdateCrewFrame()
        {
            foreach (Client c in connectedClients)
            {
                if (c.Character == null || !c.InGame) continue;
            }
        }

        public void SendDirectChatMessage(string txt, Client recipient, ChatMessageType messageType = ChatMessageType.Server)
        {
            ChatMessage msg = ChatMessage.Create("", txt, messageType, null);
            SendDirectChatMessage(msg, recipient);
        }

        public void SendConsoleMessage(string txt, Client recipient, Color? color = null)
        {
            ChatMessage msg = ChatMessage.Create("", txt, ChatMessageType.Console, sender: null, textColor: color);
            SendDirectChatMessage(msg, recipient);
        }

        public void SendDirectChatMessage(ChatMessage msg, Client recipient)
        {
            if (recipient == null)
            {
                string errorMsg = "Attempted to send a chat message to a null client.\n" + Environment.StackTrace.CleanupStackTrace();
                DebugConsole.ThrowError(errorMsg);
                GameAnalyticsManager.AddErrorEventOnce("GameServer.SendDirectChatMessage:ClientNull", GameAnalyticsManager.ErrorSeverity.Error, errorMsg);
                return;
            }

            msg.NetStateID = recipient.ChatMsgQueue.Count > 0 ?
                (ushort)(recipient.ChatMsgQueue.Last().NetStateID + 1) :
                (ushort)(recipient.LastRecvChatMsgID + 1);

            recipient.ChatMsgQueue.Add(msg);
            recipient.LastChatMsgQueueID = msg.NetStateID;
        }

        /// <summary>
        /// Add the message to the chatbox and pass it to all clients who can receive it
        /// </summary>
        public void SendChatMessage(string message, ChatMessageType? type = null, Client senderClient = null, Character senderCharacter = null, PlayerConnectionChangeType changeType = PlayerConnectionChangeType.None, ChatMode chatMode = ChatMode.None)
        {
            string senderName = "";

            Client targetClient = null;

            if (type == null)
            {
                string command = ChatMessage.GetChatMessageCommand(message, out string tempStr);
                switch (command.ToLowerInvariant())
                {
                    case "r":
                    case "radio":
                        type = ChatMessageType.Radio;
                        break;
                    case "d":
                    case "dead":
                        type = ChatMessageType.Dead;
                        break;
                    default:
                        if (command != "")
                        {
                            if (command.ToLower() == ServerName.ToLower())
                            {
                                //a private message to the host
                                if (OwnerConnection != null)
                                {
                                    targetClient = connectedClients.Find(c => c.Connection == OwnerConnection);
                                }
                            }
                            else
                            {
                                targetClient = connectedClients.Find(c =>
                                    command.ToLower() == c.Name.ToLower() ||
                                    command.ToLower() == c.Character?.Name?.ToLower());

                                if (targetClient == null)
                                {
                                    if (senderClient != null)
                                    {
                                        var chatMsg = ChatMessage.Create(
                                            "", $"ServerMessage.PlayerNotFound~[player]={command}",
                                            ChatMessageType.Error, null);
                                        SendDirectChatMessage(chatMsg, senderClient);
                                    }
                                    else
                                    {
                                        AddChatMessage($"ServerMessage.PlayerNotFound~[player]={command}", ChatMessageType.Error);
                                    }

                                    return;
                                }
                            }

                            type = ChatMessageType.Private;
                        }
                        else if (chatMode == ChatMode.Radio)
                        {
                            type = ChatMessageType.Radio;
                        }
                        else
                        {
                            type = ChatMessageType.Default;
                        }
                        break;
                }

                message = tempStr;
            }

            if (GameStarted)
            {
                if (senderClient == null)
                {
                    //msg sent by the server
                    if (senderCharacter == null)
                    {
                        senderName = ServerName;
                    }
                    else //msg sent by an AI character
                    {
                        senderName = senderCharacter.DisplayName;
                    }
                }
                else //msg sent by a client
                {
                    senderCharacter = senderClient.Character;
                    senderName = senderCharacter == null ? senderClient.Name : senderCharacter.DisplayName;
                    if (type == ChatMessageType.Private)
                    {
                        if (senderCharacter != null && !senderCharacter.IsDead || targetClient.Character != null && !targetClient.Character.IsDead)
                        {
                            //sender or target has an alive character, sending private messages not allowed
                            SendDirectChatMessage(ChatMessage.Create("", $"ServerMessage.PrivateMessagesNotAllowed", ChatMessageType.Error, null), senderClient);
                            return;
                        }
                    }
                    //sender doesn't have a character or the character can't speak -> only ChatMessageType.Dead allowed
                    else if (senderCharacter == null || senderCharacter.IsDead || senderCharacter.SpeechImpediment >= 100.0f)
                    {
                        type = ChatMessageType.Dead;
                    }
                }
            }
            else
            {
                if (senderClient == null)
                {
                    //msg sent by the server
                    if (senderCharacter == null)
                    {
                        senderName = ServerName;
                    }
                    else //sent by an AI character, not allowed when the game is not running
                    {
                        return;
                    }
                }
                else //msg sent by a client
                {
                    //game not started -> clients can only send normal, private, and team chatmessages
                    if (type != ChatMessageType.Private && type != ChatMessageType.Team) type = ChatMessageType.Default;
                    senderName = senderClient.Name;
                }
            }

            //check if the client is allowed to send the message
            WifiComponent senderRadio = null;
            switch (type)
            {
                case ChatMessageType.Radio:
                case ChatMessageType.Order:
                    if (senderCharacter == null) { return; }
                    if (!ChatMessage.CanUseRadio(senderCharacter, out senderRadio)) { return; }
                    break;
                case ChatMessageType.Dead:
                    //character still alive and capable of speaking -> dead chat not allowed
                    if (senderClient != null && senderCharacter != null && !senderCharacter.IsDead && senderCharacter.SpeechImpediment < 100.0f)
                    {
                        return;
                    }
                    break;
            }

            if (type == ChatMessageType.Server || type == ChatMessageType.Error)
            {
                senderName = null;
                senderCharacter = null;
            }
            else if (type == ChatMessageType.Radio)
            {
                //send to chat-linked wifi components
                Signal s = new Signal(message, sender: senderCharacter, source: senderRadio.Item);
                senderRadio.TransmitSignal(s, sentFromChat: true);
            }

            //check which clients can receive the message and apply distance effects
            foreach (Client client in ConnectedClients)
            {
                string modifiedMessage = message;

                switch (type)
                {
                    case ChatMessageType.Default:
                    case ChatMessageType.Radio:
                    case ChatMessageType.Order:
                        if (senderCharacter != null &&
                            client.Character != null && !client.Character.IsDead)
                        {
                            if (senderCharacter != client.Character)
                            {
                                modifiedMessage = ChatMessage.ApplyDistanceEffect(message, (ChatMessageType)type, senderCharacter, client.Character);
                            }

                            //too far to hear the msg -> don't send
                            if (string.IsNullOrWhiteSpace(modifiedMessage)) { continue; }
                        }
                        break;
                    case ChatMessageType.Dead:
                        //character still alive -> don't send
                        if (client != senderClient && client.Character != null && !client.Character.IsDead) { continue; }
                        break;
                    case ChatMessageType.Private:
                        //private msg sent to someone else than this client -> don't send
                        if (client != targetClient && client != senderClient) { continue; }
                        break;
                    case ChatMessageType.Team:
                        // No need to relay team messages at all to clients in opposing teams (or without a team)
                        if (client.TeamID == CharacterTeamType.None || client.TeamID != senderClient.TeamID) { continue; }
                        break;
                }

                var chatMsg = ChatMessage.Create(
                    senderName,
                    modifiedMessage,
                    (ChatMessageType)type,
                    senderCharacter,
                    senderClient,
                    changeType);

                SendDirectChatMessage(chatMsg, client);
            }

            if (type.Value != ChatMessageType.MessageBox)
            {
                string myReceivedMessage = type == ChatMessageType.Server || type == ChatMessageType.Error ? TextManager.GetServerMessage(message).Value : message;
                if (!string.IsNullOrWhiteSpace(myReceivedMessage))
                {
                    AddChatMessage(myReceivedMessage, (ChatMessageType)type, senderName, senderClient, senderCharacter);
                }
            }
        }

        public void SendOrderChatMessage(OrderChatMessage message)
        {
            if (message.SenderCharacter == null || message.SenderCharacter.SpeechImpediment >= 100.0f) { return; }
            //check which clients can receive the message and apply distance effects
            foreach (Client client in ConnectedClients)
            {
                if (message.SenderCharacter != null && client.Character != null && !client.Character.IsDead)
                {
                    //too far to hear the msg -> don't send
                    if (!client.Character.CanHearCharacter(message.SenderCharacter)) { continue; }
                }
                SendDirectChatMessage(new OrderChatMessage(message.Order, message.Text, message.TargetCharacter, message.Sender, isNewOrder: message.IsNewOrder), client);
            }
            if (!string.IsNullOrWhiteSpace(message.Text))
            {
                AddChatMessage(new OrderChatMessage(message.Order, message.Text, message.TargetCharacter, message.Sender, isNewOrder: message.IsNewOrder));
                if (ChatMessage.CanUseRadio(message.SenderCharacter, out var senderRadio))
                {
                    //send to chat-linked wifi components
                    Signal s = new Signal(message.Text, sender: message.SenderCharacter, source: senderRadio.Item);
                    senderRadio.TransmitSignal(s, sentFromChat: true);
                }
            }
        }

        private void FileTransferChanged(FileSender.FileTransferOut transfer)
        {
            Client recipient = connectedClients.Find(c => c.Connection == transfer.Connection);
            if (transfer.FileType == FileTransferType.CampaignSave &&
                (transfer.Status == FileTransferStatus.Sending || transfer.Status == FileTransferStatus.Finished) &&
                recipient.LastCampaignSaveSendTime != default)
            {
                recipient.LastCampaignSaveSendTime.time = (float)NetTime.Now;
            }
        }

        public void SendCancelTransferMsg(FileSender.FileTransferOut transfer)
        {
            IWriteMessage msg = new WriteOnlyMessage();
            msg.WriteByte((byte)ServerPacketHeader.FILE_TRANSFER);
            msg.WriteByte((byte)FileTransferMessageType.Cancel);
            msg.WriteByte((byte)transfer.ID);
            serverPeer.Send(msg, transfer.Connection, DeliveryMethod.Reliable);
        }

        public void UpdateVoteStatus(bool checkActiveVote = true)
        {
            if (connectedClients.Count == 0) { return; }

            if (checkActiveVote && Voting.ActiveVote != null)
            {
#warning TODO: this is mostly the same as Voting.Update, deduplicate (if/when refactoring the Voting class?)
                var inGameClients = GameMain.Server.ConnectedClients.Where(c => c.InGame);
                if (inGameClients.Count() == 1 && inGameClients.First() == Voting.ActiveVote.VoteStarter)
                {
                    Voting.ActiveVote.Finish(Voting, passed: true);
                }
                else if (inGameClients.Any())
                {
                    var eligibleClients = inGameClients.Where(c => c != Voting.ActiveVote.VoteStarter);
                    int yes = eligibleClients.Count(c => c.GetVote<int>(Voting.ActiveVote.VoteType) == 2);
                    int no = eligibleClients.Count(c => c.GetVote<int>(Voting.ActiveVote.VoteType) == 1);
                    int max = eligibleClients.Count();
                    // Required ratio cannot be met
                    if (no / (float)max > 1f - ServerSettings.VoteRequiredRatio)
                    {
                        Voting.ActiveVote.Finish(Voting, passed: false);
                    }
                    else if (yes / (float)max >= ServerSettings.VoteRequiredRatio)
                    {
                        Voting.ActiveVote.Finish(Voting, passed: true);
                    }
                }
            }

            Client.UpdateKickVotes(connectedClients);

            var kickVoteEligibleClients = connectedClients.Where(c => (DateTime.Now - c.JoinTime).TotalSeconds > ServerSettings.DisallowKickVoteTime);
            float minimumKickVotes = Math.Max(2.0f, kickVoteEligibleClients.Count() * ServerSettings.KickVoteRequiredRatio);
            var clientsToKick = connectedClients.FindAll(c =>
                c.Connection != OwnerConnection &&
                !c.HasPermission(ClientPermissions.Kick) &&
                !c.HasPermission(ClientPermissions.Ban) &&
                !c.HasPermission(ClientPermissions.Unban) &&
                c.KickVoteCount >= minimumKickVotes);
            foreach (Client c in clientsToKick)
            {
                //reset the client's kick votes (they can rejoin after their ban expires)
                c.ResetVotes(resetKickVotes: true);
                previousPlayers.Where(p => p.MatchesClient(c)).ForEach(p => p.KickVoters.Clear());
                BanClient(c, "ServerMessage.KickedByVoteAutoBan", duration: TimeSpan.FromSeconds(ServerSettings.AutoBanTime));
            }

            //GameMain.NetLobbyScreen.LastUpdateID++;

            SendVoteStatus(connectedClients);

            int endVoteCount = ConnectedClients.Count(c => c.HasSpawned && c.GetVote<bool>(VoteType.EndRound));
            int endVoteMax = GameMain.Server.ConnectedClients.Count(c => c.HasSpawned);
            if (ServerSettings.AllowEndVoting && endVoteMax > 0 &&
                ((float)endVoteCount / (float)endVoteMax) >= ServerSettings.EndVoteRequiredRatio)
            {
                Log("Ending round by votes (" + endVoteCount + "/" + (endVoteMax - endVoteCount) + ")", ServerLog.MessageType.ServerMessage);
                EndGame(wasSaved: false);
            }
        }

        public void SendVoteStatus(List<Client> recipients)
        {
            if (!recipients.Any()) { return; }

            IWriteMessage msg = new WriteOnlyMessage();
            msg.WriteByte((byte)ServerPacketHeader.UPDATE_LOBBY);
            using (var segmentTable = SegmentTableWriter<ServerNetSegment>.StartWriting(msg))
            {
                segmentTable.StartNewSegment(ServerNetSegment.Vote);
                Voting.ServerWrite(msg);
            }

            foreach (var c in recipients)
            {
                serverPeer.Send(msg, c.Connection, DeliveryMethod.Reliable);
            }
        }

        public bool TrySwitchSubmarine()
        {
            if (Voting.ActiveVote is not Voting.SubmarineVote subVote) { return false; }

            SubmarineInfo targetSubmarine = subVote.Sub;
            VoteType voteType = Voting.ActiveVote.VoteType;
            Client starter = Voting.ActiveVote.VoteStarter;

            bool purchaseFailed = false;
            switch (voteType)
            {
                case VoteType.PurchaseAndSwitchSub:
                case VoteType.PurchaseSub:
                    // Pay for submarine
                    purchaseFailed = !GameMain.GameSession.TryPurchaseSubmarine(targetSubmarine, starter);
                    break;
                case VoteType.SwitchSub:
                    break;
                default:
                    return false;
            }

            if (voteType != VoteType.PurchaseSub && !purchaseFailed)
            {
                GameMain.GameSession.SwitchSubmarine(targetSubmarine, subVote.TransferItems, starter);
            }

            Voting.StopSubmarineVote(passed: !purchaseFailed);
            return !purchaseFailed;
        }

        public void UpdateClientPermissions(Client client)
        {
            if (client.AccountId.TryUnwrap(out var accountId))
            {
                ServerSettings.ClientPermissions.RemoveAll(scp => scp.AddressOrAccountId == accountId);
                if (client.Permissions != ClientPermissions.None)
                {
                    ServerSettings.ClientPermissions.Add(new ServerSettings.SavedClientPermission(
                        client.Name,
                        accountId,
                        client.Permissions,
                        client.PermittedConsoleCommands));
                }
            }
            else
            {
                ServerSettings.ClientPermissions.RemoveAll(scp => client.Connection.Endpoint.Address == scp.AddressOrAccountId);
                if (client.Permissions != ClientPermissions.None)
                {
                    ServerSettings.ClientPermissions.Add(new ServerSettings.SavedClientPermission(
                        client.Name,
                        client.Connection.Endpoint.Address,
                        client.Permissions,
                        client.PermittedConsoleCommands));
                }
            }

            foreach (Client recipient in connectedClients)
            {
                CoroutineManager.StartCoroutine(SendClientPermissionsAfterClientListSynced(recipient, client));
            }
            ServerSettings.SaveClientPermissions();
        }

        private IEnumerable<CoroutineStatus> SendClientPermissionsAfterClientListSynced(Client recipient, Client client)
        {
            DateTime timeOut = DateTime.Now + new TimeSpan(0, 0, 10);
            while (NetIdUtils.IdMoreRecent(LastClientListUpdateID, recipient.LastRecvClientListUpdate))
            {
                if (DateTime.Now > timeOut || GameMain.Server == null || !connectedClients.Contains(recipient))
                {
                    yield return CoroutineStatus.Success;
                }
                yield return null;
            }

            SendClientPermissions(recipient, client);
            yield return CoroutineStatus.Success;
        }

        private void SendClientPermissions(Client recipient, Client client)
        {
            if (recipient?.Connection == null) { return; }

            IWriteMessage msg = new WriteOnlyMessage();
            msg.WriteByte((byte)ServerPacketHeader.PERMISSIONS);
            client.WritePermissions(msg);
            serverPeer.Send(msg, recipient.Connection, DeliveryMethod.Reliable);
        }

        public void GiveAchievement(Character character, Identifier achievementIdentifier)
        {
            foreach (Client client in connectedClients)
            {
                if (client.Character == character)
                {
                    GiveAchievement(client, achievementIdentifier);
                    return;
                }
            }
        }

        public void IncrementStat(Character character, AchievementStat stat, int amount)
        {
            foreach (Client client in connectedClients)
            {
                if (client.Character == character)
                {
                    IncrementStat(client, stat, amount);
                    return;
                }
            }
        }

        public void GiveAchievement(Client client, Identifier achievementIdentifier)
        {
            if (client.GivenAchievements.Contains(achievementIdentifier)) { return; }
            client.GivenAchievements.Add(achievementIdentifier);

            IWriteMessage msg = new WriteOnlyMessage();
            msg.WriteByte((byte)ServerPacketHeader.ACHIEVEMENT);
            msg.WriteIdentifier(achievementIdentifier);

            serverPeer.Send(msg, client.Connection, DeliveryMethod.Reliable);
        }

        public void IncrementStat(Client client, AchievementStat stat, int amount)
        {
            IWriteMessage msg = new WriteOnlyMessage();
            msg.WriteByte((byte)ServerPacketHeader.ACHIEVEMENT_STAT);

            INetSerializableStruct incrementedStat = new NetIncrementedStat(stat, amount);
            incrementedStat.Write(msg);

            serverPeer.Send(msg, client.Connection, DeliveryMethod.Reliable);
        }

        public void SendTraitorMessage(WriteOnlyMessage msg, Client client)
        {
            if (client == null) { return; };
            serverPeer.Send(msg, client.Connection, DeliveryMethod.Reliable);
        }

        public void UpdateCheatsEnabled()
        {
            if (!connectedClients.Any()) { return; }

            IWriteMessage msg = new WriteOnlyMessage();
            msg.WriteByte((byte)ServerPacketHeader.CHEATS_ENABLED);
            msg.WriteBoolean(DebugConsole.CheatsEnabled);
            msg.WritePadBits();

            foreach (Client c in connectedClients)
            {
                serverPeer.Send(msg, c.Connection, DeliveryMethod.Reliable);
            }
        }

        public void SetClientCharacter(Client client, Character newCharacter)
        {
            if (client == null) return;

            //the client's previous character is no longer a remote player
            if (client.Character != null)
            {
                client.Character.SetOwnerClient(null);
            }

            if (newCharacter == null)
            {
                if (client.Character != null) //removing control of the current character
                {
                    CreateEntityEvent(client.Character, new Character.ControlEventData(null));
                    client.Character = null;
                }
            }
            else //taking control of a new character
            {
                newCharacter.ClientDisconnected = false;
                newCharacter.KillDisconnectedTimer = 0.0f;
                newCharacter.ResetNetState();
                if (client.Character != null)
                {
                    newCharacter.LastNetworkUpdateID = client.Character.LastNetworkUpdateID;
                }

                if (newCharacter.Info != null && newCharacter.Info.Character == null)
                {
                    newCharacter.Info.Character = newCharacter;
                }

                newCharacter.SetOwnerClient(client);
                newCharacter.Enabled = true;
                client.Character = newCharacter;
                client.CharacterInfo = newCharacter.Info;
                CreateEntityEvent(newCharacter, new Character.ControlEventData(client));
            }
        }

        private readonly RateLimiter charInfoRateLimiter = new(
            maxRequests: 5,
            expiryInSeconds: 10,
            punishmentRules: new[]
            {
                (RateLimitAction.OnLimitReached, RateLimitPunishment.Announce),
                (RateLimitAction.OnLimitDoubled, RateLimitPunishment.Kick)
            });

        private void UpdateCharacterInfo(IReadMessage message, Client sender)
        {
            bool spectateOnly = message.ReadBoolean();
            message.ReadPadBits();

            sender.SpectateOnly = spectateOnly && (ServerSettings.AllowSpectating || sender.Connection == OwnerConnection);
            if (sender.SpectateOnly) { return; }

            var netInfo = INetSerializableStruct.Read<NetCharacterInfo>(message);

            if (charInfoRateLimiter.IsLimitReached(sender)) { return; }

            string newName = netInfo.NewName;
            if (string.IsNullOrEmpty(newName))
            {
                newName = sender.Name;
            }
            else
            {
                newName = Client.SanitizeName(newName);
                if (!IsNameValid(sender, newName))
                {
                    newName = sender.Name;
                }
                else
                {
                    sender.PendingName = newName;
                }
            }

            // If a CharacterInfo for this Client already exists on the server, make sure it is used, and prevent the Client from replacing it
            var existingCampaignData = (GameMain.GameSession?.Campaign as MultiPlayerCampaign)?.GetClientCharacterData(sender);
            if (existingCampaignData != null)
            {
                sender.CharacterInfo = existingCampaignData.CharacterInfo;
                return; 
            }

            sender.CharacterInfo = new CharacterInfo(CharacterPrefab.HumanSpeciesName, newName);

            sender.CharacterInfo.RecreateHead(
                tags: netInfo.Tags.ToImmutableHashSet(),
                hairIndex: netInfo.HairIndex,
                beardIndex: netInfo.BeardIndex,
                moustacheIndex: netInfo.MoustacheIndex,
                faceAttachmentIndex: netInfo.FaceAttachmentIndex);

            sender.CharacterInfo.Head.SkinColor = netInfo.SkinColor;
            sender.CharacterInfo.Head.HairColor = netInfo.HairColor;
            sender.CharacterInfo.Head.FacialHairColor = netInfo.FacialHairColor;

            if (netInfo.JobVariants.Length > 0)
            {
                List<JobVariant> variants = new List<JobVariant>();
                foreach (NetJobVariant jv in netInfo.JobVariants)
                {
                    if (jv.ToJobVariant() is { } variant)
                    {
                        variants.Add(variant);
                    }
                }

                sender.JobPreferences = variants;
            }
        }

        public readonly List<string> JobAssignmentDebugLog = new List<string>();

        public void AssignJobs(List<Client> unassigned)
        {
            JobAssignmentDebugLog.Clear();

            var jobList = JobPrefab.Prefabs.ToList();
            unassigned = new List<Client>(unassigned);
            unassigned = unassigned.OrderBy(sp => Rand.Int(int.MaxValue)).ToList();

            Dictionary<JobPrefab, int> assignedClientCount = new Dictionary<JobPrefab, int>();
            foreach (JobPrefab jp in jobList)
            {
                assignedClientCount.Add(jp, 0);
            }

            CharacterTeamType teamID = CharacterTeamType.None;
            if (unassigned.Count > 0) { teamID = unassigned[0].TeamID; }

            //if we're playing a multiplayer campaign, check which clients already have a character and a job
            //(characters are persistent in campaigns)
            if (GameMain.GameSession.GameMode is MultiPlayerCampaign multiplayerCampaign)
            {
                var campaignAssigned = multiplayerCampaign.GetAssignedJobs(connectedClients);
                //remove already assigned clients from unassigned
                unassigned.RemoveAll(u => campaignAssigned.ContainsKey(u));
                //add up to assigned client count
                foreach ((Client client, Job job) in campaignAssigned)
                {
                    assignedClientCount[job.Prefab]++;
                    client.AssignedJob = new JobVariant(job.Prefab, job.Variant);
                    JobAssignmentDebugLog.Add($"Client {client.Name} has an existing campaign character, keeping the job {job.Name}.");
                }
            }

            //count the clients who already have characters with an assigned job
            foreach (Client c in connectedClients)
            {
                if (c.TeamID != teamID || unassigned.Contains(c)) { continue; }
                if (c.Character?.Info?.Job != null && !c.Character.IsDead)
                {
                    assignedClientCount[c.Character.Info.Job.Prefab]++;
                }
            }

            //if any of the players has chosen a job that is Always Allowed, give them that job
            for (int i = unassigned.Count - 1; i >= 0; i--)
            {
                if (unassigned[i].JobPreferences.Count == 0) { continue; }
                if (!unassigned[i].JobPreferences.Any() || !unassigned[i].JobPreferences[0].Prefab.AllowAlways) { continue; }
                JobAssignmentDebugLog.Add($"Client {unassigned[i].Name} has {unassigned[i].JobPreferences[0].Prefab.Name} as their first preference, assigning it because the job is always allowed.");
                unassigned[i].AssignedJob = unassigned[i].JobPreferences[0];
                unassigned.RemoveAt(i);
            }

            // Assign the necessary jobs that are always required at least one, in vanilla this means in practice the captain
            bool unassignedJobsFound = true;
            while (unassignedJobsFound && unassigned.Any())
            {
                unassignedJobsFound = false;

                foreach (JobPrefab jobPrefab in jobList)
                {
                    if (unassigned.Count == 0) { break; }
                    if (jobPrefab.MinNumber < 1 || assignedClientCount[jobPrefab] >= jobPrefab.MinNumber) { continue; }
                    // Find the client that wants the job the most, don't force any jobs yet, because it might be that we can meet the preference for other jobs.
                    Client client = FindClientWithJobPreference(unassigned, jobPrefab, forceAssign: false);
                    if (client != null)
                    {
                        JobAssignmentDebugLog.Add($"At least {jobPrefab.MinNumber} {jobPrefab.Name} required. Assigning {client.Name} as a {jobPrefab.Name} (has the job in their preferences).");
                        AssignJob(client, jobPrefab);
                    }
                }

                if (unassigned.Any())
                {
                    // Another pass, force required jobs that are not yet filled.
                    foreach (JobPrefab jobPrefab in jobList)
                    {
                        if (unassigned.Count == 0) { break; }
                        if (jobPrefab.MinNumber < 1 || assignedClientCount[jobPrefab] >= jobPrefab.MinNumber) { continue; }
                        var client = FindClientWithJobPreference(unassigned, jobPrefab, forceAssign: true);
                        JobAssignmentDebugLog.Add(
                            $"At least {jobPrefab.MinNumber} {jobPrefab.Name} required. "+
                            $"A random client needs to be assigned because no one has the job in their preferences. Assigning {client.Name} as a {jobPrefab.Name}.");
                        AssignJob(client, jobPrefab);
                    }
                }

                void AssignJob(Client client, JobPrefab jobPrefab)
                {
                    client.AssignedJob =
                        client.JobPreferences.FirstOrDefault(jp => jp.Prefab == jobPrefab) ??
                        new JobVariant(jobPrefab, Rand.Int(jobPrefab.Variants));

                    assignedClientCount[jobPrefab]++;
                    unassigned.Remove(client);

                    //the job still needs more crew members, set unassignedJobsFound to true to keep the while loop running
                    if (assignedClientCount[jobPrefab] < jobPrefab.MinNumber) { unassignedJobsFound = true; }
                }
            }

            // Attempt to give the clients a job they have in their job preferences.
            // First evaluate all the primary preferences, then all the secondary etc.
            for (int preferenceIndex = 0; preferenceIndex < 3; preferenceIndex++)
            {
                for (int i = unassigned.Count - 1; i >= 0; i--)
                {
                    Client client = unassigned[i];
                    if (preferenceIndex >= client.JobPreferences.Count) { continue; }
                    var preferredJob = client.JobPreferences[preferenceIndex];
                    JobPrefab jobPrefab = preferredJob.Prefab;
                    if (assignedClientCount[jobPrefab] >= jobPrefab.MaxNumber)
                    {
                        JobAssignmentDebugLog.Add($"{client.Name} has {jobPrefab.Name} as their {preferenceIndex + 1}. preference. Cannot assign, maximum number of the job has been reached.");
                        continue;
                    }
                    if (client.Karma < jobPrefab.MinKarma)
                    {
                        JobAssignmentDebugLog.Add($"{client.Name} has {jobPrefab.Name} as their {preferenceIndex + 1}. preference. Cannot assign, karma too low ({client.Karma} < {jobPrefab.MinKarma}).");
                        continue;
                    }
                    JobAssignmentDebugLog.Add($"{client.Name} has {jobPrefab.Name} as their {preferenceIndex + 1}. preference. Assigning {client.Name} as a {jobPrefab.Name}.");
                    client.AssignedJob = preferredJob;
                    assignedClientCount[jobPrefab]++;
                    unassigned.RemoveAt(i);
                }
            }

            //give random jobs to rest of the clients
            foreach (Client c in unassigned)
            {
                //find all jobs that are still available
                var remainingJobs = jobList.FindAll(jp => !jp.HiddenJob && assignedClientCount[jp] < jp.MaxNumber && c.Karma >= jp.MinKarma);

                //all jobs taken, give a random job
                if (remainingJobs.Count == 0)
                {
                    string errorMsg = $"Failed to assign a suitable job for \"{c.Name}\" (all jobs already have the maximum numbers of players). Assigning a random job...";
                    DebugConsole.ThrowError(errorMsg);
                    JobAssignmentDebugLog.Add(errorMsg);
                    int jobIndex = Rand.Range(0, jobList.Count);
                    int skips = 0;
                    while (c.Karma < jobList[jobIndex].MinKarma)
                    {
                        jobIndex++;
                        skips++;
                        if (jobIndex >= jobList.Count) { jobIndex -= jobList.Count; }
                        if (skips >= jobList.Count) { break; }
                    }
                    c.AssignedJob =
                        c.JobPreferences.FirstOrDefault(jp => jp.Prefab == jobList[jobIndex]) ??
                        new JobVariant(jobList[jobIndex], 0);
                    assignedClientCount[c.AssignedJob.Prefab]++;
                }
                //if one of the client's preferences is still available, give them that job
                else if (c.JobPreferences.FirstOrDefault(jp => remainingJobs.Contains(jp.Prefab)) is { } remainingJob)
                {
                    JobAssignmentDebugLog.Add(
                        $"{c.Name} has {remainingJob.Prefab.Name} as their {c.JobPreferences.IndexOf(remainingJob) + 1}. preference, and it is still available."+
                        $" Assigning {c.Name} as a {remainingJob.Prefab.Name}.");                    
                    c.AssignedJob = remainingJob;
                    assignedClientCount[remainingJob.Prefab]++;
                }
                else //none of the client's preferred jobs available, choose a random job
                {
                    c.AssignedJob = new JobVariant(remainingJobs[Rand.Range(0, remainingJobs.Count)], 0);
                    assignedClientCount[c.AssignedJob.Prefab]++;
                    JobAssignmentDebugLog.Add(
                        $"No suitable jobs available for {c.Name} (karma {c.Karma}). Assigning a random job: {c.AssignedJob.Prefab.Name}.");
                }
            }
        }

        public void AssignBotJobs(List<CharacterInfo> bots, CharacterTeamType teamID, bool isPvP)
        {
            //shuffle first so the parts where we go through the prefabs
            //and find ones there's too few of don't always pick the same job
            List<JobPrefab> shuffledPrefabs = JobPrefab.Prefabs.Where(static jp => !jp.HiddenJob).ToList();
            shuffledPrefabs.Shuffle(Rand.RandSync.Unsynced);

            Dictionary<JobPrefab, int> assignedPlayerCount = new Dictionary<JobPrefab, int>();
            foreach (JobPrefab jp in shuffledPrefabs)
            {
                if (jp.HiddenJob) { continue; }
                assignedPlayerCount.Add(jp, 0);
            }

            //count the clients who already have characters with an assigned job
            foreach (Client c in connectedClients)
            {
                if (c.TeamID != teamID) continue;
                if (c.Character?.Info?.Job != null && !c.Character.IsDead)
                {
                    assignedPlayerCount[c.Character.Info.Job.Prefab]++;
                }
                else if (c.CharacterInfo?.Job != null)
                {
                    assignedPlayerCount[c.CharacterInfo?.Job.Prefab]++;
                }
            }

            List<CharacterInfo> unassignedBots = new List<CharacterInfo>(bots);
            while (unassignedBots.Count > 0)
            {
                //if there's any jobs left that must be included in the crew, assign those
                var jobsBelowMinNumber = shuffledPrefabs.Where(jp => assignedPlayerCount[jp] < jp.MinNumber);
                if (jobsBelowMinNumber.Any())
                {
                    AssignJob(unassignedBots[0], jobsBelowMinNumber.GetRandomUnsynced());
                }
                else
                {
                    //if there's any jobs left that are below the normal number of bots initially in the crew, assign those
                    var jobsBelowInitialCount = shuffledPrefabs.Where(jp => assignedPlayerCount[jp] < jp.InitialCount);
                    if (jobsBelowInitialCount.Any())
                    {
                        AssignJob(unassignedBots[0], jobsBelowInitialCount.GetRandomUnsynced());
                    }
                    else
                    {
                        //no "must-have-jobs" left, break and start assigning randomly
                        break;
                    }
                }
            }

            foreach (CharacterInfo c in unassignedBots.ToList())
            {
                //find all jobs that are still available
                var remainingJobs = shuffledPrefabs.Where(jp => assignedPlayerCount[jp] < jp.MaxNumber);
                //all jobs taken, give a random job
                if (remainingJobs.None())
                {
                    DebugConsole.ThrowError("Failed to assign a suitable job for bot \"" + c.Name + "\" (all jobs already have the maximum numbers of players). Assigning a random job...");
                    AssignJob(c, shuffledPrefabs.GetRandomUnsynced());
                }
                else
                { 
                    //some jobs still left, choose one of them by random (preferring ones there's the least of in the crew)
                    var selectedJob = remainingJobs.GetRandomByWeight(jp => 1.0f / Math.Max(assignedPlayerCount[jp], 0.01f), Rand.RandSync.Unsynced);
                    AssignJob(c, selectedJob);
                }
            }

            void AssignJob(CharacterInfo bot, JobPrefab job)
            {
                int variant = Rand.Range(0, job.Variants);
                bot.Job = new Job(job, isPvP, Rand.RandSync.Unsynced, variant);
                assignedPlayerCount[bot.Job.Prefab]++;
                unassignedBots.Remove(bot);
            }
        }

        private Client FindClientWithJobPreference(List<Client> clients, JobPrefab job, bool forceAssign = false)
        {
            int bestPreference = int.MaxValue;
            Client preferredClient = null;
            foreach (Client c in clients)
            {
                if (ServerSettings.KarmaEnabled && c.Karma < job.MinKarma) { continue; }
                int index = c.JobPreferences.IndexOf(c.JobPreferences.Find(j => j.Prefab == job));
                if (index > -1 && index < bestPreference)
                {
                    bestPreference = index;
                    preferredClient = c;
                }
            }

            //none of the clients wants the job, assign it to random client
            if (forceAssign && preferredClient == null)
            {
                preferredClient = clients[Rand.Int(clients.Count)];
            }

            return preferredClient;
        }

        public void UpdateMissionState(Mission mission)
        {
            foreach (var client in connectedClients)
            {
                IWriteMessage msg = new WriteOnlyMessage();
                msg.WriteByte((byte)ServerPacketHeader.MISSION);
                int missionIndex = GameMain.GameSession.GetMissionIndex(mission);
                msg.WriteByte((byte)(missionIndex == -1 ? 255: missionIndex));
                mission?.ServerWrite(msg);
                serverPeer.Send(msg, client.Connection, DeliveryMethod.Reliable);
            }
        }

        public static string CharacterLogName(Character character)
        {
            if (character == null) { return "[NULL]"; }
            Client client = GameMain.Server.ConnectedClients.Find(c => c.Character == character);
            return ClientLogName(client, character.LogName);
        }

        public static void Log(string line, ServerLog.MessageType messageType)
        {
            if (GameMain.Server == null || !GameMain.Server.ServerSettings.SaveServerLogs) { return; }

            GameMain.Server.ServerSettings.ServerLog.WriteLine(line, messageType);

            foreach (Client client in GameMain.Server.ConnectedClients)
            {
                if (!client.HasPermission(ClientPermissions.ServerLog)) continue;
                //use sendername as the message type
                GameMain.Server.SendDirectChatMessage(
                    ChatMessage.Create(messageType.ToString(), line, ChatMessageType.ServerLog, null),
                    client);
            }
        }

        public void Quit()
        {            
            if (started)
            {
                started = false;

                ServerSettings.BanList.Save();

                if (GameMain.NetLobbyScreen.SelectedSub != null) { ServerSettings.SelectedSubmarine = GameMain.NetLobbyScreen.SelectedSub.Name; }
                if (GameMain.NetLobbyScreen.SelectedShuttle != null) { ServerSettings.SelectedShuttle = GameMain.NetLobbyScreen.SelectedShuttle.Name; }

                ServerSettings.SaveSettings();

                ModSender?.Dispose();
                
                if (ServerSettings.SaveServerLogs)
                {
                    Log("Shutting down the server...", ServerLog.MessageType.ServerMessage);
                    ServerSettings.ServerLog.Save();
                }

                GameAnalyticsManager.AddDesignEvent("GameServer:ShutDown");
                serverPeer?.Close();

                SteamManager.CloseServer();
            }
        }

        private void UpdateClientLobbies()
        {
            // Triggers a call to WriteClientList(), which causes clients to call GameClient.ReadClientList()
            LastClientListUpdateID++;
        }

        private List<Client> GetPlayingClients()
        {
            List<Client> playingClients = new List<Client>(connectedClients);
            if (ServerSettings.AllowSpectating)
            {
                playingClients.RemoveAll(static c => c.SpectateOnly);
            }
            // Always allow the server owner to spectate even if it's disallowed in server settings
            playingClients.RemoveAll(c => c.Connection == OwnerConnection && c.SpectateOnly);
            return playingClients;
        }
        
        /// <summary>
        /// Assigns currently playing clients into PvP teams according to current server settings.
        /// </summary>
        /// <param name="assignUnassignedNow">Should players without team preference be randomized into teams or given time to choose?</param>
        /// <param name="autoBalanceNow">Should auto-balance be applied immediately? Otherwise, only the auto-balance countdown is started (in case of imbalance).</param>
        public void RefreshPvpTeamAssignments(bool assignUnassignedNow = false, bool autoBalanceNow = false)
        {
            List<Client> team1 = new List<Client>();
            List<Client> team2 = new List<Client>();
            List<Client> playingClients = GetPlayingClients();

            // First assign clients with a team preference/choice into the teams they want (applies in both team selection modes)
            List<Client> unassignedClients = new List<Client>(playingClients);
            for (int i = 0; i < unassignedClients.Count; i++)
            {
                if (unassignedClients[i].PreferredTeam == CharacterTeamType.Team1 ||
                    unassignedClients[i].PreferredTeam == CharacterTeamType.Team2)
                {
                    assignTeam(unassignedClients[i], unassignedClients[i].PreferredTeam);
                    i--;
                }
            }

            // Should unassigned players be forced into teams now? (eg. at round start when the time to make choices is over)
            if (assignUnassignedNow)
            {
                if (unassignedClients.Any())
                {
                    SendChatMessage(TextManager.Get("PvP.WithoutTeamWillBeRandomlyAssigned").Value, ChatMessageType.Server);
                }
                
                // Assign to the team that has the least players
                while (unassignedClients.Any())
                {
                    var randomClient = unassignedClients.GetRandom(Rand.RandSync.Unsynced);
                    assignTeam(randomClient, team1.Count < team2.Count ? CharacterTeamType.Team1 : CharacterTeamType.Team2);
                }
            }
            
            if (ServerSettings.PvpAutoBalanceThreshold > 0)
            {
                // Deal with team size balance as necessary
                int sizeDifference = Math.Abs(team1.Count - team2.Count);
                if (sizeDifference > ServerSettings.PvpAutoBalanceThreshold)
                {
                    if (autoBalanceNow)
                    {
                        SendChatMessage(TextManager.Get("AutoBalance.Activating").Value, ChatMessageType.Server);
                        
                        // Assign a random player from the bigger team into the smaller team until the teams are no longer too imbalanced
                        while (Math.Abs(team1.Count - team2.Count) > ServerSettings.PvpAutoBalanceThreshold)
                        {
                            // Note: team size difference never 0 at this point
                            var biggerTeam = GetPlayingClients().Where(
                                    c => team1.Count > team2.Count ?
                                        c.TeamID == CharacterTeamType.Team1 :
                                        c.TeamID == CharacterTeamType.Team2)
                                .ToList();
                            switchTeam(biggerTeam.GetRandom(Rand.RandSync.Unsynced), team1.Count < team2.Count ? CharacterTeamType.Team1 : CharacterTeamType.Team2);
                        }
                    }
                    else if (ServerSettings.PvpTeamSelectionMode != PvpTeamSelectionMode.PlayerPreference)
                    {
                        // Start a countdown (if not already running) to auto-balancing, so players have a chance to manually rebalance the team before that
                        if (pvpAutoBalanceCountdownRemaining == -1)
                        {
                            SendChatMessage(TextManager.GetWithVariables(
                                "AutoBalance.CountdownStarted",
                                ("[teamname]", TextManager.Get(team1.Count > team2.Count ? "teampreference.team1" : "teampreference.team2")),
                                ("[numberplayers]", (sizeDifference - ServerSettings.PvpAutoBalanceThreshold).ToString()),
                                ("[numberseconds]", PvpAutoBalanceCountdown.ToString())
                            ).Value, ChatMessageType.Server);
                            pvpAutoBalanceCountdownRemaining = PvpAutoBalanceCountdown;
                        }
                    }
                }
                else
                {
                    // Stop countdown if there was one
                    StopAutoBalanceCountdown();
                }    
            }
            else
            {
                // Stop countdown if there was one (eg. if the settings were changed during countdown)
                StopAutoBalanceCountdown();
            }

            // Finally, push the assignments to the clients
            UpdateClientLobbies();

            void assignTeam(Client client, CharacterTeamType newTeam)
            {
                client.TeamID = newTeam;
                unassignedClients.Remove(client);
                if (newTeam == CharacterTeamType.Team1)
                {
                    team1.Add(client);
                }
                else if (newTeam == CharacterTeamType.Team2)
                {
                    team2.Add(client);
                }
            }
            
            void switchTeam(Client client, CharacterTeamType newTeam)
            {
                string teamNameVariable = "";
                if (newTeam == CharacterTeamType.Team1)
                {
                    team2.Remove(client);
                    team1.Add(client);
                    teamNameVariable = "teampreference.team1";
                }
                else if (newTeam == CharacterTeamType.Team2)
                {
                    team1.Remove(client);
                    team2.Add(client);
                    teamNameVariable = "teampreference.team2";
                }
                SendChatMessage(TextManager.GetWithVariables(
                    "AutoBalance.PlayerMoved",
                    ("[clientname]", client.Name),
                    ("[teamname]", TextManager.Get(teamNameVariable))
                ).Value, ChatMessageType.Server);
                client.TeamID = newTeam;
                client.PreferredTeam = newTeam;
            }
        }
        
        /// <summary>
        /// Assign a team for single clients who join the server when a round is already running.
        /// </summary>
        public void AssignClientToPvpTeamMidgame(Client client)
        {
            if (client.PreferredTeam == CharacterTeamType.None)
            {
                // If teams are currently even, assign the preference-less new player into a random team 
                if (Team1Count == Team2Count)
                {
                    client.TeamID = Rand.Value() > 0.5f ? CharacterTeamType.Team1 : CharacterTeamType.Team2;
                }
                else // Otherwise, just assign them to the smaller team
                {
                    client.TeamID = Team1Count < Team2Count ? CharacterTeamType.Team1 : CharacterTeamType.Team2;
                }
            }
            else if (ServerSettings.PvpAutoBalanceThreshold > 0) // Check if the player can be put into their preferred team
            {
                int newTeam1Count = Team1Count + (client.PreferredTeam == CharacterTeamType.Team1 ? 1 : 0);
                int newTeam2Count = Team2Count + (client.PreferredTeam == CharacterTeamType.Team2 ? 1 : 0);
                
                // Threshold won't be crossed by assigning the player to their preferred team, so do it
                if (Math.Abs(newTeam1Count - newTeam2Count) <= ServerSettings.PvpAutoBalanceThreshold)
                {
                    client.TeamID = client.PreferredTeam;
                }
                else // Preferred team would go against balance threshold, assing the player to the smaller team
                {
                    client.TeamID = Team1Count < Team2Count ? CharacterTeamType.Team1 : CharacterTeamType.Team2;
                }
            }
            else // Nothing stopping us from assigning the player into their preferred team
            {
                client.TeamID = client.PreferredTeam;
            }
        }
        
        private void StopAutoBalanceCountdown()
        {
            if (pvpAutoBalanceCountdownRemaining != -1)
            {
                SendChatMessage(TextManager.Get("AutoBalance.CountdownCancelled").Value, ChatMessageType.Server);
            }
            pvpAutoBalanceCountdownRemaining = -1;
        }
    }

    class PreviousPlayer
    {
        public string Name;
        public Address Address;
        public AccountInfo AccountInfo;
        public float Karma;
        public int KarmaKickCount;
        public readonly List<Client> KickVoters = new List<Client>();

        public PreviousPlayer(Client c)
        {
            Name = c.Name;
            Address = c.Connection.Endpoint.Address;
            AccountInfo = c.AccountInfo;
        }

        public bool MatchesClient(Client c)
        {
            if (c.AccountInfo.AccountId.IsSome() && AccountInfo.AccountId.IsSome()) { return c.AccountInfo.AccountId == AccountInfo.AccountId; }
            return c.AddressMatches(Address);
        }
    }
}

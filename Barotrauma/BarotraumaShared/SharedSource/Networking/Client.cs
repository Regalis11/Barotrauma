using Lidgren.Network;
using Microsoft.Xna.Framework;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Barotrauma.Networking
{
    partial class Client : IDisposable
    {
        public const int MaxNameLength = 32;

        public string Name; public UInt16 NameID;
        public byte ID;
        public UInt64 SteamID;
        public UInt64 OwnerSteamID;

        public string Language;

        public UInt16 Ping;

        public string PreferredJob;

        public CharacterTeamType TeamID;

        public CharacterTeamType PreferredTeam;

        private Character character;
        public Character Deadcharacter;
        public Character Character
        {
            get
            {
                if (GameMain.NetworkMember != null && GameMain.NetworkMember.IsClient && (character?.ID ?? 0) != CharacterID)
                {
                    Character = Entity.FindEntityByID(CharacterID) as Character;
                }
                return character;
            }
            set
            {
                if (GameMain.NetworkMember != null && GameMain.NetworkMember.IsServer)
                {
                    GameMain.NetworkMember.LastClientListUpdateID++;
                    if (value != null)
                    {
                        CharacterID = value.ID;
                    }
                }
                else
                {
                    if (value != null)
                    {
                        DebugConsole.NewMessage(value.Name, Microsoft.Xna.Framework.Color.Yellow);
                    }
                }
                character = value;
                if (character != null)
                {
                    HasSpawned = true;
#if CLIENT
                    GameMain.GameSession?.CrewManager?.SetPlayerVoiceIconState(this, muted, mutedLocally);

                    if (character == GameMain.Client.Character && GameMain.Client.SpawnAsTraitor)
                    {
                        character.IsTraitor = true;
                        character.TraitorCurrentObjective = GameMain.Client.TraitorFirstObjective;
                    }
#endif
                }
            }
        }

        public UInt16 CharacterID;

        private Vector2 spectate_position;
        public Vector2? SpectatePos
        {
            get
            {
                if (character == null || character.IsDead)
                {
                    Deadcharacter = character;
                    return spectate_position;
                }
                else
                {
                    return null;
                }
            }

            set
            {
                spectate_position = value.Value;
            }
        }

        public bool Spectating
        {
            get
            {
                return inGame && character == null;
            }
        }

        private bool muted;
        public bool Muted
        {
            get { return muted; }
            set
            {
                if (muted == value) { return; }
                muted = value;
#if CLIENT
                GameMain.NetLobbyScreen.SetPlayerVoiceIconState(this, muted, mutedLocally);
                GameMain.GameSession?.CrewManager?.SetPlayerVoiceIconState(this, muted, mutedLocally);
#endif
                if (GameMain.NetworkMember != null && GameMain.NetworkMember.IsServer)
                {
                    GameMain.NetworkMember.LastClientListUpdateID++;
                }
            }
        }

        public bool HasPermissions = false;

        public VoipQueue VoipQueue
        {
            get;
            private set;
        }

        private bool inGame;
        public bool InGame
        {
            get
            {
                return inGame;
            }
            set
            {
                if (GameMain.NetworkMember != null && GameMain.NetworkMember.IsServer)
                {
                    GameMain.NetworkMember.LastClientListUpdateID++;
                }
                inGame = value;
            }
        }
        public bool HasSpawned; //has the client spawned as a character during the current round
        
        private List<Client> kickVoters;

        public HashSet<string> GivenAchievements = new HashSet<string>();

        public ClientPermissions Permissions = ClientPermissions.None;
        public List<DebugConsole.Command> PermittedConsoleCommands
        {
            get;
            private set;
        }

        private object[] votes;

        public int KickVoteCount
        {
            get { return kickVoters.Count; }
        }
        
        /*public Client(NetPeer server, string name, byte ID)
            : this(name, ID)
        {
            
        }*/

        partial void InitProjSpecific();
        partial void DisposeProjSpecific();
        public Client(string name, byte ID)
        {
            this.Name = name;
            this.ID = ID;

            PermittedConsoleCommands = new List<DebugConsole.Command>();
            kickVoters = new List<Client>();

            votes = new object[Enum.GetNames(typeof(VoteType)).Length];

            InitProjSpecific();
        }

        public T GetVote<T>(VoteType voteType)
        {
            return (votes[(int)voteType] is T) ? (T)votes[(int)voteType] : default(T);
        }

        public void SetVote(VoteType voteType, object value)
        {
            votes[(int)voteType] = value;
        }

        public void ResetVotes()
        {
            for (int i = 0; i < votes.Length; i++)
            {
                votes[i] = null;
            }

            kickVoters.Clear();
        }

        public void AddKickVote(Client voter)
        {
            if (voter != null && !kickVoters.Contains(voter)) { kickVoters.Add(voter); }
        }


        public void RemoveKickVote(Client voter)
        {
            kickVoters.Remove(voter);
        }
        
        public bool HasKickVoteFrom(Client voter)
        {
            return kickVoters.Contains(voter);
        }

        public bool HasKickVoteFromID(int id)
        {
            return kickVoters.Any(k => k.ID == id);
        }


        public static void UpdateKickVotes(List<Client> connectedClients)
        {
            foreach (Client client in connectedClients)
            {
                client.kickVoters.RemoveAll(voter => !connectedClients.Contains(voter));
            }
        }

        public void WritePermissions(IWriteMessage msg)
        {
            msg.Write(ID);
            msg.Write((UInt16)Permissions);
            if (HasPermission(ClientPermissions.ConsoleCommands))
            {
                msg.Write((UInt16)PermittedConsoleCommands.Count);
                foreach (DebugConsole.Command command in PermittedConsoleCommands)
                {
                    msg.Write(command.names[0]);
                }
            }
        }
        public static void ReadPermissions(IReadMessage inc, out ClientPermissions permissions, out List<DebugConsole.Command> permittedCommands)
        {
            UInt16 permissionsInt = inc.ReadUInt16();

            permissions = ClientPermissions.None;
            permittedCommands = new List<DebugConsole.Command>();
            try
            {
                permissions = (ClientPermissions)permissionsInt;
            }
            catch (InvalidCastException)
            {
                return;
            }
            if (permissions.HasFlag(ClientPermissions.ConsoleCommands))
            {
                UInt16 commandCount = inc.ReadUInt16();
                for (int i = 0; i < commandCount; i++)
                {
                    string commandName = inc.ReadString();
                    var consoleCommand = DebugConsole.Commands.Find(c => c.names.Contains(commandName));
                    if (consoleCommand != null)
                    {
                        permittedCommands.Add(consoleCommand);
                    }
                }
            }
        }

        public void ReadPermissions(IReadMessage inc)
        {
            ClientPermissions permissions = ClientPermissions.None;
            List<DebugConsole.Command> permittedCommands = new List<DebugConsole.Command>();
            ReadPermissions(inc, out permissions, out permittedCommands);
            SetPermissions(permissions, permittedCommands);
        }

        public static string SanitizeName(string name)
        {
            name = name.Trim();
            if (name.Length > MaxNameLength)
            {
                name = name.Substring(0, MaxNameLength);
            }
            string rName = "";
            for (int i = 0; i < name.Length; i++)
            {
                rName += name[i] < 32 ? '?' : name[i];
            }
            return rName;
        }

        public void Dispose()
        {
            DisposeProjSpecific();
        }
    }
}

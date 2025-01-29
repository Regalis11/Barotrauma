using Barotrauma.Networking;
using Microsoft.Xna.Framework;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Xml.Linq;
#if CLIENT
using Microsoft.Xna.Framework.Input;
#endif

namespace Barotrauma.Items.Components
{
    partial class Wire : ItemComponent, IDrawableComponent, IServerSerializable, IClientSerializable
    {
        public partial class WireSection
        {
            private Vector2 start;
            private Vector2 end;

            private readonly float angle;
            public readonly float Length;

            public Vector2 Start
            {
                get { return start; }
            }
            public Vector2 End
            {
                get { return end; }
            }

            public WireSection(Vector2 start, Vector2 end)
            {
                this.start = start;
                this.end = end;

                angle = MathUtils.VectorToAngle(end - start);
                Length = Vector2.Distance(start, end);
            }
        }

        private bool shouldClearConnections = true;

        const float MaxAttachDistance = 150.0f;

        const float MinNodeDistance = 7.0f;

        const int MaxNodeCount = 255;
        const int MaxNodesPerNetworkEvent = 30;

        private List<Vector2> nodes;
        private readonly List<WireSection> sections;

        private readonly Connection[] connections;

        private bool canPlaceNode;
        private Vector2 newNodePos;

        private Vector2 sectionExtents;

        private float currLength;

        public bool Hidden;

        private float editNodeDelay;

        private bool locked;

        public bool Locked
        {
            get
            {
                if (GameMain.NetworkMember?.ServerSettings != null && !GameMain.NetworkMember.ServerSettings.AllowRewiring) { return false; }
                return locked || connections.Any(c => c != null && (c.ConnectionPanel.Locked || c.ConnectionPanel.TemporarilyLocked));
            }
            set { locked = value; }
        }

        public Connection[] Connections
        {
            get { return connections; }
        }

        public float Length { get; private set; }

        [Serialize(5000.0f, IsPropertySaveable.No, description: "The maximum distance the wire can extend (in pixels).")]
        public float MaxLength
        {
            get;
            set;
        }

        [Serialize(false, IsPropertySaveable.No, description: "If enabled, the wire will not be visible in connection panels outside the submarine editor.")]
        public bool HiddenInGame
        {
            get;
            set;
        }

        [Editable, Serialize(false, IsPropertySaveable.Yes, "If enabled, this wire will be ignored by the \"Lock all default wires\" setting.", alwaysUseInstanceValues: true)]
        public bool NoAutoLock
        {
            get;
            set;
        }

        [Editable, Serialize(false, IsPropertySaveable.Yes, "If enabled, this wire will use the sprite depth instead of a constant depth.")]
        public bool UseSpriteDepth
        {
            get;
            set;
        }

        [Serialize(true, IsPropertySaveable.Yes, "If disabled, the wire will not be dropped when connecting. Used in circuit box to store the wires inside the box.")]
        public bool DropOnConnect
        {
            get;
            set;
        }

        public Wire(Item item, ContentXElement element)
            : base(item, element)
        {
            nodes = new List<Vector2>();
            sections = new List<WireSection>();
            connections = new Connection[2];            
            IsActive = false;
            item.IsShootable = true;

            InitProjSpecific(element);
        }

        partial void InitProjSpecific(ContentXElement element);

        public Connection OtherConnection(Connection connection)
        {
            if (connection == connections[0]) { return connections[1]; }
            if (connection == connections[1]) { return connections[0]; }

            return null;
        }

        public bool IsConnectedTo(Item item)
        {
            if (connections[0] != null && connections[0].Item == item) { return true; }
            return connections[1] != null && connections[1].Item == item;
        }

        public void RemoveConnection(Item item)
        {
            for (int i = 0; i < 2; i++)
            {
                if (connections[i] == null || connections[i].Item != item) { continue; }

                if (connections[i].Wires.Contains(this))
                {
                    SetConnectedDirty();

                    connections[i].DisconnectWire(this);
                }

                connections[i] = null;
            }
        }

        public void RemoveConnection(Connection connection)
        {
            if (connection == connections[0]) { connections[0] = null; }
            if (connection == connections[1]) { connections[1] = null; }

            SetConnectedDirty();
        }

        /// <summary>
        /// Tries to add the given connection to this wire. Note that this only affects the wire - 
        /// adding the wire to the connection is done in <see cref="Connection.ConnectWire(Wire)"/>
        /// </summary>

        public bool TryConnect(Connection newConnection, bool addNode = true, bool sendNetworkEvent = false)
        {
            if (connections[0] == null) 
            { 
                return Connect(newConnection, 0, addNode, sendNetworkEvent); 
            }
            else if (connections[1] == null) 
            { 
                return Connect(newConnection, 1, addNode, sendNetworkEvent); 
            }
            return false;
        }


        /// <summary>
        /// Tries to add the given connection to this wire. Note that this only affects the wire - 
        /// adding the wire to the connection is done in <see cref="Connection.ConnectWire(Wire)"/>
        /// </summary>
        /// <param name="connectionIndex">Which end of the wire to add the connection to? 0 or 1. 
        /// Normally doesn't make a difference, but matters if we're copying/loading a wire,
        /// in which case the 1st node should be located at the same item as the 1st connection.</param>
        /// <returns></returns>
        public bool Connect(Connection newConnection, int connectionIndex, bool addNode = true, bool sendNetworkEvent = false)
        {
            for (int i = 0; i < 2; i++)
            {
                if (connections[i] == newConnection) { return false; }
            }

            if (connectionIndex < 0 || connectionIndex > 1)
            {
                DebugConsole.ThrowError($"Error while connecting a wire to {newConnection.Item}: {connectionIndex} is not a valid index.");
                return false;
            }
            if (connections[connectionIndex] != null)
            {
                DebugConsole.ThrowError($"Error while connecting a wire to {newConnection.Item}: a wire is already connected to the index {connectionIndex}.");
                return false;
            }

            for (int i = 0; i < 2; i++)
            {
                if (connections[i] != null && connections[i].Item == newConnection.Item)
                {
                    addNode = false;
                    break;
                }
            }
            if (item.body != null) { item.Submarine = newConnection.Item.Submarine; }

            newConnection.ConnectionPanel.DisconnectedWires.Remove(this);

            connections[connectionIndex] = newConnection;
            FixNodeEnds();

            if (addNode)
            {
                AddNode(newConnection, connectionIndex);
            }

            SetConnectedDirty();

            if (DropOnConnect)
            {
                if (connections[0] != null && connections[1] != null)
                {
                    foreach (ItemComponent ic in item.Components)
                    {
                        if (ic == this) { continue; }

                        ic.Drop(null);
                    }

                    item.Container?.RemoveContained(item);
                    if (item.body != null) { item.body.Enabled = false; }

                    IsActive = false;

                    CleanNodes();
                }
            }

            if (item.body != null) { item.Submarine = newConnection.Item.Submarine; }

            if (sendNetworkEvent)
            {
#if SERVER
                if (GameMain.Server != null)
                {
                    CreateNetworkEvent();
                }
#endif
                //the wire is active if it's currently being wired to something (in character inventory and connected from one end)
                IsActive = 
                    item.ParentInventory is CharacterInventory &&
                    connections[0] == null ^ connections[1] == null;
            }

            Drawable = IsActive || nodes.Any();

            UpdateSections();
            return true;
        }

        private void AddNode(Connection newConnection, int selectedIndex)
        {
            Submarine refSub = newConnection.Item.Submarine;
            if (refSub == null)
            {
                Structure attachTarget = Structure.GetAttachTarget(newConnection.Item.WorldPosition);
                if (attachTarget == null && !(newConnection.Item.GetComponent<Holdable>()?.Attached ?? false))
                {
                    connections[selectedIndex] = null;
                    return;
                }
                refSub = attachTarget?.Submarine;
            }

            Vector2 nodePos = RoundNode(newConnection.Item.Position);
            if (refSub != null) { nodePos -= refSub.HiddenSubPosition; }

            if (nodes.Count > 0 && nodes[0] == nodePos) { return; }
            if (nodes.Count > 1 && nodes[nodes.Count - 1] == nodePos) { return; }

            //make sure we place the node at the correct end of the wire (the end that's closest to the new node pos)
            int newNodeIndex = 0;
            if (nodes.Count > 1)
            {
                if (connections[0] != null && connections[0] != newConnection)
                {
                    if (Vector2.DistanceSquared(nodes[0], connections[0].Item.Position - (refSub?.HiddenSubPosition ?? Vector2.Zero)) <
                        Vector2.DistanceSquared(nodes[nodes.Count - 1], connections[0].Item.Position - (refSub?.HiddenSubPosition ?? Vector2.Zero)))
                    {
                        newNodeIndex = nodes.Count;
                    }
                }
                else if (connections[1] != null && connections[1] != newConnection)
                {
                    if (Vector2.DistanceSquared(nodes[0], connections[1].Item.Position - (refSub?.HiddenSubPosition ?? Vector2.Zero)) <
                        Vector2.DistanceSquared(nodes[nodes.Count - 1], connections[1].Item.Position - (refSub?.HiddenSubPosition ?? Vector2.Zero)))
                    {
                        newNodeIndex = nodes.Count;
                    }
                }
                else if (Vector2.DistanceSquared(nodes[nodes.Count - 1], nodePos) < Vector2.DistanceSquared(nodes[0], nodePos))
                {
                    newNodeIndex = nodes.Count;
                }
            }

            if (newNodeIndex == 0 && nodes.Count > 1)
            {
                nodes.Insert(0, nodePos);
            }
            else
            {
                nodes.Add(nodePos);
            }
        }

        public override void Equip(Character character)
        {
            if (shouldClearConnections) { ClearConnections(character); }
            IsActive = true;
        }

        public override void Unequip(Character character)
        {
            ClearConnections(character);
            IsActive = false;
        }

        public override void Drop(Character dropper, bool setTransform = true)
        {
            if (shouldClearConnections) { ClearConnections(dropper); }
            IsActive = false;
        }

        public override void Update(float deltaTime, Camera cam)
        {
            if (nodes.Count == 0) { return; }

            Character user = item.ParentInventory?.Owner as Character;
            editNodeDelay = (user?.SelectedItem == null) ? editNodeDelay - deltaTime : 0.5f;

            Submarine sub = item.Submarine;
            if (connections[0] != null && connections[0].Item.Submarine != null) { sub = connections[0].Item.Submarine; }
            if (connections[1] != null && connections[1].Item.Submarine != null) { sub = connections[1].Item.Submarine; }

            if (Screen.Selected != GameMain.SubEditorScreen)
            {
                if (user != null) { NoAutoLock = true; }

                //cannot run wires from sub to another
                if (item.Submarine != sub && sub != null && item.Submarine != null)
                {
                    ClearConnections();
                    return;
                }

                if (item.CurrentHull == null)
                {
                    Structure attachTarget = Structure.GetAttachTarget(item.WorldPosition);
                    canPlaceNode = attachTarget != null;

                    sub ??= attachTarget?.Submarine;
                    Vector2 attachPos = GetAttachPosition(user);
                    newNodePos = sub == null ?
                        attachPos :
                        attachPos - sub.Position - sub.HiddenSubPosition;
                }
                else
                {
                    newNodePos = GetAttachPosition(user);
                    if (sub != null) { newNodePos -= sub.HiddenSubPosition; }
                    canPlaceNode = true;
                }

                //prevent the wire from extending too far when rewiring
                if (nodes.Count > 0)
                {
                    if (user == null) { return; }

                    Vector2 prevNodePos = nodes[nodes.Count - 1];
                    if (sub != null) { prevNodePos += sub.HiddenSubPosition; }

                    currLength = 0.0f;
                    for (int i = 0; i < nodes.Count - 1; i++)
                    {
                        currLength += Vector2.Distance(nodes[i], nodes[i + 1]);
                    }
                    Vector2 itemPos = item.Position;
                    if (sub != null && user.Submarine == null) { prevNodePos += sub.Position; }
                    currLength += Vector2.Distance(prevNodePos, itemPos);
                    if (currLength > MaxLength)
                    {
                        Vector2 diff = prevNodePos - user.Position;
                        Vector2 pullBackDir = diff == Vector2.Zero ? Vector2.Zero : Vector2.Normalize(diff);
                        Vector2 forceDir = pullBackDir;
                        if (!user.AnimController.InWater) { forceDir.Y = 0.0f; }
                        user.AnimController.Collider.ApplyForce(forceDir * user.Mass * 50.0f, maxVelocity: NetConfig.MaxPhysicsBodyVelocity * 0.5f);
                        if (diff.LengthSquared() > 50.0f * 50.0f)
                        {
                            user.AnimController.UpdateUseItem(!user.IsClimbing, user.WorldPosition + pullBackDir * Math.Min(150.0f, diff.Length()));
                        }

                        if (GameMain.NetworkMember == null || GameMain.NetworkMember.IsServer)
                        {
                            if (currLength > MaxLength * 1.5f)
                            {
                                ClearConnections();
#if SERVER
                                CreateNetworkEvent();
#endif
                                return;
                            }
                        }
                    }
                }
            }
            else
            {
#if CLIENT
                bool disableGrid = SubEditorScreen.IsSubEditor() && PlayerInput.IsShiftDown();
                newNodePos = disableGrid ? item.Position : RoundNode(item.Position);
#else
                newNodePos = RoundNode(item.Position);
#endif
                if (sub != null) { newNodePos -= sub.HiddenSubPosition; }
                canPlaceNode = true;
            }

            if (item != null)
            {
                Vector2 relativeNodePos = newNodePos - item.Position;

                if (sub != null)
                {
                    relativeNodePos += sub.HiddenSubPosition;
                }

                sectionExtents = new Vector2(
                    Math.Max(Math.Abs(relativeNodePos.X), sectionExtents.X),
                    Math.Max(Math.Abs(relativeNodePos.Y), sectionExtents.Y));
            }
        }

        private Vector2 GetAttachPosition(Character user)
        {
            if (user == null) { return item.Position; }

            Vector2 mouseDiff = user.CursorWorldPosition - user.WorldPosition;
            mouseDiff = mouseDiff.ClampLength(MaxAttachDistance);

            return RoundNode(user.Position + mouseDiff);
        }

        public override bool Use(float deltaTime, Character character = null)
        {
            if (character == null || character != Character.Controlled) { return false; }
            if (character.HasSelectedAnyItem) { return false; }
#if CLIENT
            if (Screen.Selected == GameMain.SubEditorScreen && !PlayerInput.PrimaryMouseButtonClicked())
            {
                return false;
            }
#endif
            //clients communicate node addition/removal with network events
            if (GameMain.NetworkMember != null && GameMain.NetworkMember.IsServer) { return false; }

            if (newNodePos != Vector2.Zero && canPlaceNode && editNodeDelay <= 0.0f && nodes.Count > 0 && 
                Vector2.DistanceSquared(newNodePos, nodes[nodes.Count - 1]) > MinNodeDistance * MinNodeDistance)
            {
                if (nodes.Count >= MaxNodeCount)
                {
                    nodes.RemoveAt(nodes.Count - 1);
                }

                nodes.Add(newNodePos);
                CleanNodes();
                UpdateSections();
                Drawable = true;
                newNodePos = Vector2.Zero;
#if CLIENT
                if (GameMain.NetworkMember != null)
                {
                    item.CreateClientEvent(this, new ClientEventData(nodes.Count));
                }
#endif
            }
            editNodeDelay = 0.1f;
            return true;
        }

        public override bool SecondaryUse(float deltaTime, Character character = null)
        {
            if (character == null || character != Character.Controlled) { return false; }

            //clients communicate node addition/removal with network events
            if (GameMain.NetworkMember != null && GameMain.NetworkMember.IsServer) { return false; }

            if (nodes.Count > 1 && editNodeDelay <= 0.0f)
            {
                nodes.RemoveAt(nodes.Count - 1);
                UpdateSections();
#if CLIENT
                if (GameMain.NetworkMember != null)
                {
                    item.CreateClientEvent(this, new ClientEventData(nodes.Count));
                }
#endif
            }
            editNodeDelay = 0.1f;

            Drawable = IsActive || sections.Count > 0;
            return true;
        }

        public override bool Pick(Character picker)
        {
            ClearConnections(picker);
            return true;
        }

        public List<Vector2> GetNodes()
        {
            return new List<Vector2>(nodes);
        }

        public void SetNodes(IEnumerable<Vector2> nodes)
        {
            this.nodes = nodes.ToList();
            UpdateSections();
        }

        public void MoveNode(int index, Vector2 amount)
        {
            if (index < 0 || index >= nodes.Count) return;
            nodes[index] += amount;            
            UpdateSections();
        }

        public void MoveNodes(Vector2 amount)
        {
            for (int i = 0; i < nodes.Count; i++)
            {
                nodes[i] += amount;
            }
            UpdateSections();
        }

        public void UpdateSections()
        {
            sections.Clear();

            for (int i = 0; i < nodes.Count - 1; i++)
            {
                sections.Add(new WireSection(nodes[i], nodes[i + 1]));
            }
            Drawable = IsActive || sections.Count > 0;
            Length = sections.Count > 0 ? sections.Sum(s => s.Length) : 0;
            CalculateExtents();
        }

        private void CalculateExtents()
        {
            sectionExtents = Vector2.Zero;
            if (sections.Count > 0)
            {
                for (int i = 0; i < nodes.Count; i++)
                {
                    sectionExtents.X = Math.Max(Math.Abs(nodes[i].X - item.Position.X), sectionExtents.X);
                    sectionExtents.Y = Math.Max(Math.Abs(nodes[i].Y - item.Position.Y), sectionExtents.Y);
                }
            }
#if CLIENT
            item.ResetCachedVisibleSize();
#endif
        }

        public void ClearConnections(Character user = null)
        {
            nodes.Clear();
            sections.Clear();

            foreach (Item item in Item.ItemList)
            {
                var connectionPanel = item.GetComponent<ConnectionPanel>();
                if (connectionPanel != null && connectionPanel.DisconnectedWires.Contains(this) && !item.Removed)
                {
#if SERVER
                    item.CreateServerEvent(connectionPanel);
#endif
                    connectionPanel.DisconnectedWires.Remove(this);
                }
            }

#if SERVER
            if (user != null)
            {
                if (connections[0] != null || connections[1] != null)
                {
                    GameMain.Server.KarmaManager.OnWireDisconnected(user, this);
                }

                if (connections[0] != null && connections[1] != null)
                {
                    GameServer.Log(GameServer.CharacterLogName(user) + " disconnected a wire from " + 
                        connections[0].Item.Name + " (" + connections[0].Name + ") to "+
                        connections[1].Item.Name + " (" + connections[1].Name + ")", ServerLog.MessageType.ItemInteraction);
                }
                else if (connections[0] != null)
                {
                    GameServer.Log(GameServer.CharacterLogName(user) + " disconnected a wire from " +
                        connections[0].Item.Name + " (" + connections[0].Name + ")", ServerLog.MessageType.ItemInteraction);
                }
                else if (connections[1] != null)
                {
                    GameServer.Log(GameServer.CharacterLogName(user) + " disconnected a wire from " +
                        connections[1].Item.Name + " (" + connections[1].Name + ")", ServerLog.MessageType.ItemInteraction);
                }
            }
#endif

            SetConnectedDirty();

            for (int i = 0; i < 2; i++)
            {
                if (connections[i] == null) { continue; }

                var wire = connections[i].FindWireByItem(item);
                if (wire is null) { continue; }
#if SERVER
                if (!connections[i].Item.Removed && (!connections[i].Item.Submarine?.Loading ?? true) && (!Level.Loaded?.Generating ?? true))
                {
                    connections[i].Item.CreateServerEvent(connections[i].Item.GetComponent<ConnectionPanel>());
                }
#endif
                connections[i].DisconnectWire(wire);
                connections[i] = null;
            }

            Drawable = sections.Count > 0;
        }

        private static Vector2 RoundNode(Vector2 position)
        {
            Vector2 halfGrid = Submarine.GridSize / 2;

            position += halfGrid;
            position.X = MathUtils.RoundTowardsClosest(position.X, Submarine.GridSize.X / 2.0f);
            position.Y = MathUtils.RoundTowardsClosest(position.Y, Submarine.GridSize.Y / 2.0f);
            return position - halfGrid;
        }

        public void SetConnectedDirty()
        {
            for (int i = 0; i < 2; i++)
            {
                if (connections[i]?.Item != null)
                {
                    connections[i].Item.GetComponent<PowerTransfer>()?.SetConnectionDirty(connections[i]);
                    connections[i].SetRecipientsDirty();
                }
            }
        }

        private void CleanNodes()
        {
            bool removed;
            do
            {
                removed = false;
                for (int i = nodes.Count - 2; i > 0; i--)
                {
                    if (Math.Abs(nodes[i - 1].X - nodes[i].X) < 1.0f && Math.Abs(nodes[i + 1].X - nodes[i].X) < 1.0f &&
                        Math.Sign(nodes[i - 1].Y - nodes[i].Y) != Math.Sign(nodes[i + 1].Y - nodes[i].Y))
                    {
                        nodes.RemoveAt(i);
                        removed = true;
                    }
                    else if (Math.Abs(nodes[i - 1].Y - nodes[i].Y) < 1.0f && Math.Abs(nodes[i + 1].Y - nodes[i].Y) < 1.0f &&
                            Math.Sign(nodes[i - 1].X - nodes[i].X) != Math.Sign(nodes[i + 1].X - nodes[i].X))
                    {
                        nodes.RemoveAt(i);
                        removed = true;
                    }
                }

            } while (removed);
        }

        public void FixNodeEnds()
        {
            Item item0 = connections[0]?.Item;
            Item item1 = connections[1]?.Item;

            if (item0 == null && item1 != null)
            {
                item0 = Item.ItemList.Find(it => it.GetComponent<ConnectionPanel>()?.DisconnectedWires.Contains(this) ?? false);
            }
            else if (item0 != null && item1 == null)
            {
                item1 = Item.ItemList.Find(it => it.GetComponent<ConnectionPanel>()?.DisconnectedWires.Contains(this) ?? false);
            }

            if (item0 == null || item1 == null || nodes.Count == 0) { return; }

            Vector2 nodePos = nodes[0];

            Submarine refSub = item0.Submarine ?? item1.Submarine;
            if (refSub != null) { nodePos += refSub.HiddenSubPosition; }

            float dist1 = Vector2.DistanceSquared(item0.Position, nodePos);
            float dist2 = Vector2.DistanceSquared(item1.Position, nodePos);

            //first node is closer to the second item
            //= the nodes are "backwards", need to reverse them
            if (dist1 > dist2)
            {
                nodes.Reverse();
                UpdateSections();
            }
        }

        private int GetClosestNodeIndex(Vector2 pos, float maxDist, out float closestDist)
        {
            closestDist = 0.0f;
            int closestIndex = -1;

            for (int i = 0; i < nodes.Count; i++)
            {
                float dist = Vector2.Distance(nodes[i], pos);
                if (dist > maxDist) continue;

                if (closestIndex == -1 || dist < closestDist)
                {
                    closestIndex = i;
                    closestDist = dist;
                }
            }

            return closestIndex;
        }

        private int GetClosestSectionIndex(Vector2 mousePos, float maxDist, out float closestDist)
        {
            closestDist = 0.0f;
            int closestIndex = -1;

            maxDist *= maxDist;
            for (int i = 0; i < nodes.Count-1; i++)
            {
                if ((Math.Abs(nodes[i].X - nodes[i + 1].X)<5 || Math.Sign(mousePos.X - nodes[i].X) != Math.Sign(mousePos.X - nodes[i + 1].X)) &&
                     (Math.Abs(nodes[i].Y - nodes[i + 1].Y)<5 || Math.Sign(mousePos.Y - nodes[i].Y) != Math.Sign(mousePos.Y - nodes[i + 1].Y)))
                {
                    float dist = MathUtils.LineToPointDistanceSquared(nodes[i], nodes[i + 1], mousePos);
                    if (dist > maxDist) continue;

                    if (closestIndex == -1 || dist < closestDist)
                    {
                        closestIndex = i;
                        closestDist = dist;
                    }
                }
            }
            closestDist = (float)Math.Sqrt(closestDist);

            return closestIndex;
        }

        public override void FlipX(bool relativeToSub)
        {
            if (item.ParentInventory != null) { return; }
#if CLIENT
            if (!relativeToSub)
            {
                if (Screen.Selected != GameMain.SubEditorScreen || (item.Submarine?.Loading ?? false)) { return; }
            }
#else
            if (!relativeToSub) { return; }
#endif

            Vector2 refPos = item.Submarine == null ?
                Vector2.Zero :
                item.Position - item.Submarine.HiddenSubPosition;

            for (int i = 0; i < nodes.Count; i++)
            {
                nodes[i] = relativeToSub ?
                    new Vector2(-nodes[i].X, nodes[i].Y) :
                    new Vector2(refPos.X - (nodes[i].X - refPos.X), nodes[i].Y);
            }
            UpdateSections();
        }

        public override void FlipY(bool relativeToSub)
        {
            Vector2 refPos = item.Submarine == null ?
                Vector2.Zero :
               item.Position - item.Submarine.HiddenSubPosition;

            for (int i = 0; i < nodes.Count; i++)
            {
                nodes[i] = relativeToSub ?
                    new Vector2(nodes[i].X, -nodes[i].Y) :
                    new Vector2(nodes[i].X, refPos.Y - (nodes[i].Y - refPos.Y));
            }
            UpdateSections();
        }

        public static IEnumerable<Vector2> ExtractNodes(XElement element)
        {
            string nodeString = element.GetAttributeString("nodes", "");
            if (nodeString.IsNullOrWhiteSpace()) { yield break; }

            string[] nodeCoords = nodeString.Split(';');
            for (int i = 0; i < nodeCoords.Length / 2; i++)
            {
                float.TryParse(nodeCoords[i * 2].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out float x);
                float.TryParse(nodeCoords[i * 2 + 1].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out float y);
                yield return new Vector2(x, y);
            }
        }
        
        public override void Load(ContentXElement componentElement, bool usePrefabValues, IdRemap idRemap, bool isItemSwap)
        {
            base.Load(componentElement, usePrefabValues, idRemap, isItemSwap);

            nodes.AddRange(ExtractNodes(componentElement));

            Drawable = nodes.Any();
        }

        public override XElement Save(XElement parentElement)
        {
            XElement componentElement = base.Save(parentElement);

            if (nodes == null || nodes.Count == 0) return componentElement;

            string[] nodeCoords = new string[nodes.Count * 2];
            for (int i = 0; i < nodes.Count; i++)
            {
                nodeCoords[i * 2] = nodes[i].X.ToString(CultureInfo.InvariantCulture);
                nodeCoords[i * 2 + 1] = nodes[i].Y.ToString(CultureInfo.InvariantCulture);
            }

            componentElement.Add(new XAttribute("nodes", string.Join(";", nodeCoords)));

            return componentElement;
        }

        protected override void ShallowRemoveComponentSpecific()
        {
            /*for (int i = 0; i < 2; i++)
            {
                if (connections[i] == null) continue;
                int wireIndex = connections[i].FindWireIndex(item);

                if (wireIndex > -1)
                {
                    connections[i].AddLink(wireIndex, null);
                }
            }*/
        }

        protected override void RemoveComponentSpecific()
        {
            ClearConnections();
            base.RemoveComponentSpecific();
#if CLIENT
            if (DraggingWire == this) { draggingWire = null; }
            overrideSprite?.Remove();
            overrideSprite = null;
            wireSprite = null;
#endif
        }
    }
}

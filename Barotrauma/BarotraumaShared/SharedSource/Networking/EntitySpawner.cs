using Barotrauma.Extensions;
using Barotrauma.Items.Components;
using Barotrauma.Networking;
using Microsoft.Xna.Framework;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Barotrauma
{
    partial class EntitySpawner : Entity, IServerSerializable
    {
        private enum SpawnableType { Item, Character };
        
        public interface IEntitySpawnInfo
        {
            Entity Spawn();
            void OnSpawned(Entity entity);
        }

        public class ItemSpawnInfo : IEntitySpawnInfo
        {
            public readonly ItemPrefab Prefab;

            public readonly Vector2 Position;
            public readonly Inventory Inventory;
            public readonly Submarine Submarine;
            public readonly Option<float> Condition;
            public readonly Option<int> Quality;

            public bool SpawnIfInventoryFull = true;
            public bool IgnoreLimbSlots = false;
            public InvSlotType Slot = InvSlotType.None;

            private readonly Action<Item> onSpawned;

            public ItemSpawnInfo(ItemPrefab prefab, Vector2 worldPosition, Action<Item> onSpawned, float? condition = null, int? quality = null)
                : this(prefab, onSpawned, condition, quality)
            {
                Position = worldPosition;
            }

            public ItemSpawnInfo(ItemPrefab prefab, Vector2 position, Submarine sub, Action<Item> onSpawned, float? condition = null, int? quality = null)
                : this(prefab, onSpawned, condition, quality)
            {
                Position = position;
                Submarine = sub;
            }
            
            public ItemSpawnInfo(ItemPrefab prefab, Inventory inventory, Action<Item> onSpawned, float? condition = null, int? quality = null)
                : this(prefab, onSpawned, condition, quality)
            {
                Inventory = inventory;
            }

            private ItemSpawnInfo(ItemPrefab prefab, Action<Item> onSpawned, float? condition = null, int? quality = null)
            {
                Prefab = prefab ?? throw new ArgumentException("ItemSpawnInfo prefab cannot be null.");
                Condition = condition.HasValue ? Option<float>.Some(condition.Value) : Option<float>.None();
                Quality = quality.HasValue ? Option<int>.Some(quality.Value) : Option<int>.None();
                this.onSpawned = onSpawned;
            }

            public Entity Spawn()
            {                
                if (Prefab == null)
                {
                    return null;
                }
                Item spawnedItem;
                if (Inventory?.Owner != null)
                {
                    if (!SpawnIfInventoryFull && !Inventory.CanProbablyBePut(Prefab))
                    {
                        return null;
                    }
                    spawnedItem = new Item(Prefab, Vector2.Zero, null);
                    //this needs to be done before attempting to put the item in the inventory,
                    //because the quality and condition may affect whether it can go in the inventory (into an existing stack)
                    SetItemProperties(spawnedItem);

                    var slot = Slot != InvSlotType.None ? Slot.ToEnumerable() : spawnedItem.AllowedSlots;
                    if (!Inventory.Owner.Removed && !Inventory.TryPutItem(spawnedItem, null, slot))
                    {
                        if (IgnoreLimbSlots)
                        {
                            for (int i = 0; i < Inventory.Capacity; i++)
                            {
                                if (Inventory.GetItemAt(i) == null)
                                {
                                    Inventory.ForceToSlot(spawnedItem, i);
                                    break;
                                }
                            }
                        }
                        spawnedItem.SetTransform(FarseerPhysics.ConvertUnits.ToSimUnits(Inventory.Owner?.WorldPosition ?? Vector2.Zero), spawnedItem.body?.Rotation ?? 0.0f, findNewHull: false);
                    }
                }
                else
                {
                    spawnedItem = new Item(Prefab, Position, Submarine);
                    SetItemProperties(spawnedItem);
                }
                return spawnedItem;

                void SetItemProperties(Item spawnedItem)
                {
                    if (Condition.TryUnwrap(out float condition))
                    {
                        spawnedItem.Condition = condition;
                    }
                    if (Quality.TryUnwrap(out int quality))
                    {
                        spawnedItem.Quality = quality;
                    }
                }
            }

            public void OnSpawned(Entity spawnedItem)
            {
                if (spawnedItem is not Item item) { throw new ArgumentException($"The entity passed to ItemSpawnInfo.OnSpawned must be an Item (value was {spawnedItem?.ToString() ?? "null"})."); }
                onSpawned?.Invoke(item);
            }
        }

        class CharacterSpawnInfo : IEntitySpawnInfo
        {
            public readonly Identifier Identifier;
            public readonly CharacterInfo CharacterInfo;

            public readonly Vector2 Position;
            public readonly Submarine Submarine;

            private readonly Action<Character> onSpawned;

            public CharacterSpawnInfo(Identifier identifier, Vector2 worldPosition, Action<Character> onSpawn = null)
            {
                this.Identifier = identifier;
                if (identifier.IsEmpty) { throw new ArgumentException($"{nameof(CharacterSpawnInfo)} identifier cannot be null."); }
                Position = worldPosition;
                this.onSpawned = onSpawn;
            }

            public CharacterSpawnInfo(Identifier identifier, Vector2 position, Submarine sub, Action<Character> onSpawn = null)
            {
                this.Identifier = identifier;
                if (identifier.IsEmpty) { throw new ArgumentException($"{nameof(CharacterSpawnInfo)} identifier cannot be null."); }
                Position = position;
                Submarine = sub;
                this.onSpawned = onSpawn;
            }

            public CharacterSpawnInfo(Identifier identifier, Vector2 position, CharacterInfo characterInfo, Action<Character> onSpawn = null) : this (identifier, position, onSpawn)
            {
                CharacterInfo = characterInfo;
            }

            public Entity Spawn()
            {
                var character = Identifier.IsEmpty ? null :
                    Character.Create(Identifier,
                    Submarine == null ? Position : Submarine.Position + Position,
                    ToolBox.RandomSeed(8), CharacterInfo, createNetworkEvent: false);
                return character;
            }

            public void OnSpawned(Entity spawnedCharacter)
            {
                if (!(spawnedCharacter is Character character)) { throw new ArgumentException($"The entity passed to CharacterSpawnInfo.OnSpawned must be a Character (value was {spawnedCharacter?.ToString() ?? "null"})."); }
                onSpawned?.Invoke(character);
            }
        }

        class SubmarineSpawnInfo : IEntitySpawnInfo
        {
            public readonly string Name;

            public readonly Vector2 Position;

            private readonly Action<Character> onSpawned;

            public SubmarineSpawnInfo(string name, Vector2 worldPosition, Action<Character> onSpawn = null)
            {
                this.Name = name ?? throw new ArgumentException("ItemSpawnInfo prefab cannot be null.");
                Position = worldPosition;
                this.onSpawned = onSpawn;
            }


            public Entity Spawn()
            {
                var submarine = string.IsNullOrEmpty(Name) ? null :
                    new Submarine(SubmarineInfo.SavedSubmarines.First(s => s.Name.Equals(Name, StringComparison.OrdinalIgnoreCase)));
                return submarine;
            }

            public void OnSpawned(Entity spawnedCharacter)
            {
                if (!(spawnedCharacter is Character character)) { throw new ArgumentException($"The entity passed to CharacterSpawnInfo.OnSpawned must be a Character (value was {spawnedCharacter?.ToString() ?? "null"})."); }
                onSpawned?.Invoke(character);
            }
        }

        private readonly Queue<Either<IEntitySpawnInfo, Entity>> spawnOrRemoveQueue;

        public abstract class SpawnOrRemove : NetEntityEvent.IData
        {
            public readonly Entity Entity;
            public UInt16 ID => Entity.ID;
            
            public readonly UInt16 InventoryID;

            public readonly byte ItemContainerIndex;
            public readonly int SlotIndex;

            public override string ToString()
            {
                return
                    "(" +
                    ((Entity as MapEntity)?.Name ?? "[NULL]") +
                    $", {ID}, {InventoryID}, {SlotIndex})";
            }

            protected SpawnOrRemove(Entity entity)
            {
                Entity = entity;
                if (!(entity is Item { ParentInventory: { Owner: { } } } item)) { return; }

                InventoryID = item.ParentInventory.Owner.ID;
                SlotIndex = item.ParentInventory.FindIndex(item);
                //find the index of the ItemContainer this item is inside to get the item to
                //spawn in the correct inventory in multi-inventory items like fabricators
                if (item.Container == null) { return; }

                foreach (ItemComponent component in item.Container.Components)
                {
                    if (component is ItemContainer container &&
                        container.Inventory == item.ParentInventory)
                    {
                        ItemContainerIndex = (byte)item.Container.GetComponentIndex(component);
                        break;
                    }
                }
            }
        }

        public sealed class SpawnEntity : SpawnOrRemove
        {
            public SpawnEntity(Entity entity) : base(entity) { }
            public override string ToString()
                => $"Spawn {base.ToString()}";
        }
        
        public sealed class RemoveEntity : SpawnOrRemove
        {
            public RemoveEntity(Entity entity) : base(entity) { }
            public override string ToString()
                => $"Remove {base.ToString()}";
        }
        
        public EntitySpawner()
            : base(null, Entity.EntitySpawnerID)
        {
            spawnOrRemoveQueue = new Queue<Either<IEntitySpawnInfo, Entity>>();
        }

        public override string ToString()
        {
            return "EntitySpawner";
        }

        public void AddItemToSpawnQueue(ItemPrefab itemPrefab, Vector2 worldPosition, float? condition = null, int? quality = null, Action<Item> onSpawned = null)
        {
            if (GameMain.NetworkMember != null && GameMain.NetworkMember.IsClient) { return; }
            if (itemPrefab == null)
            {
                string errorMsg = "Attempted to add a null item to entity spawn queue.\n" + Environment.StackTrace.CleanupStackTrace();
                DebugConsole.ThrowError(errorMsg);
                GameAnalyticsManager.AddErrorEventOnce("EntitySpawner.AddToSpawnQueue1:ItemPrefabNull", GameAnalyticsManager.ErrorSeverity.Error, errorMsg);
                return;
            }
            spawnOrRemoveQueue.Enqueue(new ItemSpawnInfo(itemPrefab, worldPosition, onSpawned, condition, quality));
        }

        public void AddItemToSpawnQueue(ItemPrefab itemPrefab, Vector2 position, Submarine sub, float? condition = null, int? quality = null, Action<Item> onSpawned = null)
        {
            if (GameMain.NetworkMember != null && GameMain.NetworkMember.IsClient) { return; }
            if (itemPrefab == null)
            {
                string errorMsg = "Attempted to add a null item to entity spawn queue.\n" + Environment.StackTrace.CleanupStackTrace();
                DebugConsole.ThrowError(errorMsg);
                GameAnalyticsManager.AddErrorEventOnce("EntitySpawner.AddToSpawnQueue2:ItemPrefabNull", GameAnalyticsManager.ErrorSeverity.Error, errorMsg);
                return;
            }
            spawnOrRemoveQueue.Enqueue(new ItemSpawnInfo(itemPrefab, position, sub, onSpawned, condition, quality));
        }

        public void AddItemToSpawnQueue(ItemPrefab itemPrefab, Inventory inventory, float? condition = null, int? quality = null, Action<Item> onSpawned = null, bool spawnIfInventoryFull = true, bool ignoreLimbSlots = false, InvSlotType slot = InvSlotType.None)
        {
            if (GameMain.NetworkMember != null && GameMain.NetworkMember.IsClient) { return; }
            if (itemPrefab == null)
            {
                string errorMsg = "Attempted to add a null item to entity spawn queue.\n" + Environment.StackTrace.CleanupStackTrace();
                DebugConsole.ThrowError(errorMsg);
                GameAnalyticsManager.AddErrorEventOnce("EntitySpawner.AddToSpawnQueue3:ItemPrefabNull", GameAnalyticsManager.ErrorSeverity.Error, errorMsg);
                return;
            }
            spawnOrRemoveQueue.Enqueue(new ItemSpawnInfo(itemPrefab, inventory, onSpawned, condition, quality) 
            { 
                SpawnIfInventoryFull = spawnIfInventoryFull, 
                IgnoreLimbSlots = ignoreLimbSlots,
                Slot = slot
            });
        }

        public void AddCharacterToSpawnQueue(Identifier speciesName, Vector2 worldPosition, Action<Character> onSpawn = null)
        {
            if (GameMain.NetworkMember != null && GameMain.NetworkMember.IsClient) { return; }
            if (speciesName.IsEmpty)
            {
                string errorMsg = "Attempted to add an empty/null species name to entity spawn queue.\n" + Environment.StackTrace.CleanupStackTrace();
                DebugConsole.ThrowError(errorMsg);
                GameAnalyticsManager.AddErrorEventOnce("EntitySpawner.AddToSpawnQueue4:SpeciesNameNullOrEmpty", GameAnalyticsManager.ErrorSeverity.Error, errorMsg);
                return;
            }
            spawnOrRemoveQueue.Enqueue(new CharacterSpawnInfo(speciesName, worldPosition, onSpawn));
        }

        public void AddCharacterToSpawnQueue(Identifier speciesName, Vector2 position, Submarine sub, Action<Character> onSpawn = null)
        {
            if (GameMain.NetworkMember != null && GameMain.NetworkMember.IsClient) { return; }
            if (speciesName.IsEmpty)
            {
                string errorMsg = "Attempted to add an empty/null species name to entity spawn queue.\n" + Environment.StackTrace.CleanupStackTrace();
                DebugConsole.ThrowError(errorMsg);
                GameAnalyticsManager.AddErrorEventOnce("EntitySpawner.AddToSpawnQueue5:SpeciesNameNullOrEmpty", GameAnalyticsManager.ErrorSeverity.Error, errorMsg);
                return;
            }
            spawnOrRemoveQueue.Enqueue(new CharacterSpawnInfo(speciesName, position, sub, onSpawn));
        }

        public void AddCharacterToSpawnQueue(Identifier speciesName, Vector2 worldPosition, CharacterInfo characterInfo, Action<Character> onSpawn = null)
        {
            if (GameMain.NetworkMember != null && GameMain.NetworkMember.IsClient) { return; }
            if (speciesName.IsEmpty)
            {
                string errorMsg = "Attempted to add an empty/null species name to entity spawn queue.\n" + Environment.StackTrace.CleanupStackTrace();
                DebugConsole.ThrowError(errorMsg);
                GameAnalyticsManager.AddErrorEventOnce("EntitySpawner.AddToSpawnQueue4:SpeciesNameNullOrEmpty", GameAnalyticsManager.ErrorSeverity.Error, errorMsg);
                return;
            }
            spawnOrRemoveQueue.Enqueue(new CharacterSpawnInfo(speciesName, worldPosition, characterInfo, onSpawn));
        }

        public void AddEntityToRemoveQueue(Entity entity)
        {
            if (GameMain.NetworkMember != null && GameMain.NetworkMember.IsClient) { return; }
            if (entity == null || IsInRemoveQueue(entity) || entity.Removed || entity.IdFreed) { return; }
            if (entity is Item item) { AddItemToRemoveQueue(item); return; }
            if (entity is Character)
            {
                Character character = entity as Character;
#if SERVER
                if (GameMain.Server != null)
                {
                    Client client = GameMain.Server.ConnectedClients.Find(c => c.Character == character);
                    if (client != null) GameMain.Server.SetClientCharacter(client, null);
                }
#endif
#if CLIENT
                if (GameMain.IsSingleplayer && character != null && Character.Controlled == character && character.IsOnPlayerTeam)
                {
                    GameMain.GameSession.CrewManager.SelectNextCharacter();
                }
#endif
            }

            spawnOrRemoveQueue.Enqueue(entity);
        }

        public void AddItemToRemoveQueue(Item item)
        {
            if (GameMain.NetworkMember != null && GameMain.NetworkMember.IsClient) { return; }
            if (IsInRemoveQueue(item) || item.Removed) { return; }

            spawnOrRemoveQueue.Enqueue(item);

            foreach (var containedItem in item.ContainedItems)
            {
                if (containedItem != null)
                {
                    AddItemToRemoveQueue(containedItem);
                }
            }
        }

        /// <summary>
        /// Are there any entities in the spawn queue that match the given predicate
        /// </summary>
        public bool IsInSpawnQueue(Predicate<IEntitySpawnInfo> predicate)
        {
            foreach (var spawnOrRemove in spawnOrRemoveQueue)
            {
                if (spawnOrRemove.TryGet(out IEntitySpawnInfo spawnInfo) && predicate(spawnInfo)) { return true; }
            }
            return false;
        }

        /// <summary>
        /// How many entities in the spawn queue match the given predicate
        /// </summary>
        public int CountSpawnQueue(Predicate<IEntitySpawnInfo> predicate)
        {
            int count = 0;
            foreach (var spawnOrRemove in spawnOrRemoveQueue)
            {
                if (spawnOrRemove.TryGet(out IEntitySpawnInfo spawnInfo) && predicate(spawnInfo)) { count++; }
            }
            return count;
        }

        public bool IsInRemoveQueue(Entity entity)
        {
            foreach (var spawnOrRemove in spawnOrRemoveQueue)
            {
                if (spawnOrRemove.TryGet(out Entity entityToRemove) && entityToRemove == entity) { return true; }
            }
            return false;
        }

        public void Update(bool createNetworkEvents = true)
        {
            if (GameMain.NetworkMember is { IsClient: true }) { return; }
            while (spawnOrRemoveQueue.Count > 0)
            {
                var spawnOrRemove = spawnOrRemoveQueue.Dequeue();
                if (spawnOrRemove.TryGet(out Entity entityToRemove))
                {
                    if (entityToRemove is Item item)
                    {
                        item.SendPendingNetworkUpdates();
                    }
                    if (createNetworkEvents)
                    {
                        CreateNetworkEventProjSpecific(new RemoveEntity(entityToRemove));
                    }
                    entityToRemove.Remove();
                }
                else if (spawnOrRemove.TryGet(out IEntitySpawnInfo spawnInfo))
                { 
                    var spawnedEntity = spawnInfo.Spawn();
                    if (spawnedEntity == null) { continue; }
                    if (createNetworkEvents) 
                    { 
                        CreateNetworkEventProjSpecific(new SpawnEntity(spawnedEntity)); 
                    }
                    spawnInfo.OnSpawned(spawnedEntity);
                    GameMain.GameSession?.EventManager?.EntitySpawned(spawnedEntity);
                }
            }
        }

        partial void CreateNetworkEventProjSpecific(SpawnOrRemove spawnOrRemove);

        public void Reset()
        {
            spawnOrRemoveQueue.Clear();
#if CLIENT
            receivedEvents.Clear();
#endif
        }
    }
}

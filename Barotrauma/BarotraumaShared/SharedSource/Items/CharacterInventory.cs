using Barotrauma.Items.Components;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Xml.Linq;

namespace Barotrauma
{
    [Flags]
    public enum InvSlotType
    {
        None = 0, Any = 1, RightHand = 2, LeftHand = 4, Head = 8, InnerClothes = 16, OuterClothes = 32, Headset = 64, Card = 128, Bag = 256
    };

    partial class CharacterInventory : Inventory
    {
        private readonly Character character;

        public InvSlotType[] SlotTypes
        {
            get;
            private set;
        }


        public static readonly List<InvSlotType> anySlot = new List<InvSlotType>() { InvSlotType.Any };

        protected bool[] IsEquipped;

        public bool AccessibleWhenAlive
        {
            get;
            private set;
        }

        public bool AccessibleByOwner
        {
            get;
            private set;
        }

        private static string[] ParseSlotTypes(XElement element)
        {
            string slotString = element.GetAttributeString("slots", null);
            return slotString == null ? new string[0] : slotString.Split(',');
        }

        public CharacterInventory(XElement element, Character character)
            : base(character, ParseSlotTypes(element).Length)
        {
            this.character = character;
            IsEquipped = new bool[capacity];
            SlotTypes = new InvSlotType[capacity];

            AccessibleWhenAlive = element.GetAttributeBool("accessiblewhenalive", true);
            AccessibleByOwner = element.GetAttributeBool("accessiblebyowner", false);

            string[] slotTypeNames = ParseSlotTypes(element);
            System.Diagnostics.Debug.Assert(slotTypeNames.Length == capacity);

            for (int i = 0; i < capacity; i++)
            {
                InvSlotType parsedSlotType = InvSlotType.Any;
                slotTypeNames[i] = slotTypeNames[i].Trim();
                if (!Enum.TryParse(slotTypeNames[i], out parsedSlotType))
                {
                    DebugConsole.ThrowError("Error in the inventory config of \"" + character.SpeciesName + "\" - " + slotTypeNames[i] + " is not a valid inventory slot type.");
                }
                SlotTypes[i] = parsedSlotType;
                switch (SlotTypes[i])
                {
                    case InvSlotType.LeftHand:
                    case InvSlotType.RightHand:
                        slots[i].HideIfEmpty = true;
                        break;
                }               
            }
            
            InitProjSpecific(element);

#if CLIENT
            //clients don't create items until the server says so
            if (GameMain.Client != null) { return; }
#endif

            foreach (XElement subElement in element.Elements())
            {
                if (!subElement.Name.ToString().Equals("item", StringComparison.OrdinalIgnoreCase)) { continue; }
                
                string itemIdentifier = subElement.GetAttributeString("identifier", "");
                if (!(MapEntityPrefab.Find(null, itemIdentifier) is ItemPrefab itemPrefab))
                {
                    DebugConsole.ThrowError("Error in character inventory \"" + character.SpeciesName + "\" - item \"" + itemIdentifier + "\" not found.");
                    continue;
                }

                Entity.Spawner?.AddToSpawnQueue(itemPrefab, this, ignoreLimbSlots: subElement.GetAttributeBool("forcetoslot", false));
            }
        }

        partial void InitProjSpecific(XElement element);

        public int FindLimbSlot(InvSlotType limbSlot)
        {
            for (int i = 0; i < slots.Length; i++)
            {
                if (SlotTypes[i] == limbSlot) { return i; }
            }
            return -1;
        }

        public Item GetItemInLimbSlot(InvSlotType limbSlot)
        {
            for (int i = 0; i < slots.Length; i++)
            {
                if (SlotTypes[i] == limbSlot) { return slots[i].FirstOrDefault(); }
            }
            return null;
        }


        public bool IsInLimbSlot(Item item, InvSlotType limbSlot)
        {
            if (limbSlot == (InvSlotType.LeftHand | InvSlotType.RightHand))
            {
                int rightHandSlot = FindLimbSlot(InvSlotType.RightHand);
                int leftHandSlot = FindLimbSlot(InvSlotType.LeftHand);
                if (rightHandSlot > -1 && slots[rightHandSlot].Contains(item) &&
                    leftHandSlot > -1 && slots[leftHandSlot].Contains(item))
                {
                    return true;
                }
            }

            for (int i = 0; i < slots.Length; i++)
            {
                if (SlotTypes[i] == limbSlot && slots[i].Contains(item)) { return true; }
            }
            return false;
        }

        public override bool CanBePutInSlot(Item item, int i, bool ignoreCondition = false)
        {
            return 
                base.CanBePutInSlot(item, i, ignoreCondition) && item.AllowedSlots.Any(s => s.HasFlag(SlotTypes[i])) && 
                (SlotTypes[i] == InvSlotType.Any || slots[i].ItemCount < 1);
        }

        public override bool CanBePutInSlot(ItemPrefab itemPrefab, int i, float? condition)
        {
            return 
                base.CanBePutInSlot(itemPrefab, i, condition) &&
                (SlotTypes[i] == InvSlotType.Any || slots[i].ItemCount < 1);
        }

        public bool CanBeAutoMovedToCorrectSlots(Item item)
        {
            if (item == null) { return false; }
            foreach (var allowedSlot in item.AllowedSlots)
            {
                InvSlotType slotsFree = InvSlotType.None;
                for (int i = 0; i < slots.Length; i++)
                {
                    if (allowedSlot.HasFlag(SlotTypes[i]) && slots[i].Empty()) { slotsFree |= SlotTypes[i]; }
                }
                if (allowedSlot == slotsFree) { return true; }
            }
            return false;
        }

        public override void RemoveItem(Item item)
        {
            RemoveItem(item, tryEquipFromSameStack: false);
        }

        public void RemoveItem(Item item, bool tryEquipFromSameStack)
        {
            if (!Contains(item)) { return; }

            bool wasEquipped = character.HasEquippedItem(item);
            var indices = FindIndices(item);

            base.RemoveItem(item);
#if CLIENT
            CreateSlots();
#endif
            //if the item was equipped and there are more items in the same stack, equip one of those items
            if (tryEquipFromSameStack && wasEquipped)
            {
                int limbSlot = indices.Find(j => SlotTypes[j] != InvSlotType.Any);
                foreach (int i in indices)
                {
                    var itemInSameSlot = GetItemAt(i);
                    if (itemInSameSlot != null)
                    {
                        if (TryPutItem(itemInSameSlot, limbSlot, allowSwapping: false, allowCombine: false, character))
                        {
#if CLIENT
                            visualSlots[i].ShowBorderHighlight(GUI.Style.Green, 0.1f, 0.412f);
#endif
                        }
                        break;
                    }
                }
            }
        }


        /// <summary>
        /// If there is no room in the generic inventory (InvSlotType.Any), check if the item can be auto-equipped into its respective limbslot
        /// </summary>
        public bool TryPutItemWithAutoEquipCheck(Item item, Character user, IEnumerable<InvSlotType> allowedSlots = null, bool createNetworkEvent = true)
        {
            // Does not auto-equip the item if specified and no suitable any slot found (for example handcuffs are not auto-equipped)
            if (item.AllowedSlots.Contains(InvSlotType.Any))
            {
                var wearable = item.GetComponent<Wearable>();
                if (wearable != null && !wearable.AutoEquipWhenFull && CheckIfAnySlotAvailable(item, false) == -1)
                {
                    return false;
                }
            }

            if (allowedSlots != null && allowedSlots.Any() && !allowedSlots.Contains(InvSlotType.Any))
            {
                bool allSlotsTaken = true;
                foreach (var allowedSlot in allowedSlots)
                {
                    if (allowedSlot == (InvSlotType.RightHand | InvSlotType.LeftHand))
                    {
                        int rightHandSlot = FindLimbSlot(InvSlotType.RightHand);
                        int leftHandSlot = FindLimbSlot(InvSlotType.LeftHand);
                        if (rightHandSlot > -1 && slots[rightHandSlot].CanBePut(item) &&
                            leftHandSlot > -1 && slots[leftHandSlot].CanBePut(item))
                        {
                            allSlotsTaken = false;
                            break;
                        }
                    }
                    else
                    {
                        int slot = FindLimbSlot(allowedSlot);
                        if (slot > -1 && slots[slot].CanBePut(item))
                        {
                            allSlotsTaken = false;
                            break;
                        }
                    }

                }
                if (allSlotsTaken)
                {
                    int slot = FindLimbSlot(allowedSlots.First());
                    if (slot > -1 && slots[slot].Items.Any(it => it != item) && slots[slot].First().AllowDroppingOnSwapWith(item))
                    {
                        foreach (Item existingItem in slots[slot].Items.ToList())
                        {
                            existingItem.Drop(user);
                            if (existingItem.ParentInventory != null) { existingItem.ParentInventory.RemoveItem(existingItem); }
                        }
                    }
                }
            }

            return TryPutItem(item, user, allowedSlots, createNetworkEvent);
        }

        /// <summary>
        /// If there is room, puts the item in the inventory and returns true, otherwise returns false
        /// </summary>
        public override bool TryPutItem(Item item, Character user, IEnumerable<InvSlotType> allowedSlots = null, bool createNetworkEvent = true, bool ignoreCondition = false)
        {
            if (allowedSlots == null || !allowedSlots.Any()) { return false; }
            if (item == null)
            {
#if DEBUG
                throw new Exception("item null");
#else
                return false;
#endif
            }
            if (item.Removed)
            {
#if DEBUG
                throw new Exception("Tried to put a removed item (" + item.Name + ") in an inventory");
#else
                DebugConsole.ThrowError("Tried to put a removed item (" + item.Name + ") in an inventory.\n" + Environment.StackTrace.CleanupStackTrace());
                return false;
#endif
            }

            bool inSuitableSlot = false;
            bool inWrongSlot = false;
            int currentSlot = -1;
            for (int i = 0; i < capacity; i++)
            {
                if (slots[i].Contains(item))
                {
                    currentSlot = i;
                    if (allowedSlots.Any(a => a.HasFlag(SlotTypes[i])))
                        inSuitableSlot = true;
                    else if (!allowedSlots.Any(a => a.HasFlag(SlotTypes[i])))
                        inWrongSlot = true;
                }
            }
            //all good
            if (inSuitableSlot && !inWrongSlot) { return true; }

            //try to place the item in a LimbSlot.Any slot if that's allowed
            if (allowedSlots.Contains(InvSlotType.Any) && item.AllowedSlots.Contains(InvSlotType.Any))
            {
                int freeIndex = CheckIfAnySlotAvailable(item, inWrongSlot);
                if (freeIndex > -1)
                {
                    PutItem(item, freeIndex, user, true, createNetworkEvent);
                    item.Unequip(character);
                    return true;
                }
            }

            int placedInSlot = -1;
            foreach (InvSlotType allowedSlot in allowedSlots)
            {
                if (allowedSlot.HasFlag(InvSlotType.RightHand) && character.AnimController.GetLimb(LimbType.RightHand) == null) { continue; }
                if (allowedSlot.HasFlag(InvSlotType.LeftHand) && character.AnimController.GetLimb(LimbType.LeftHand) == null) { continue; }

                //check if all the required slots are free
                bool free = true;
                for (int i = 0; i < capacity; i++)
                {
                    if (allowedSlot.HasFlag(SlotTypes[i]) && item.AllowedSlots.Any(s => s.HasFlag(SlotTypes[i])) && slots[i].Items.Any(it => it != item))
                    {
#if CLIENT
                        if (PersonalSlots.HasFlag(SlotTypes[i])) { hidePersonalSlots = false; }
#endif
                        if (!slots[i].First().AllowedSlots.Contains(InvSlotType.Any) || !TryPutItem(slots[i].FirstOrDefault(), character, new List<InvSlotType> { InvSlotType.Any }, true, ignoreCondition))
                        {
                            free = false;
#if CLIENT
                            for (int j = 0; j < capacity; j++)
                            {
                                if (visualSlots != null && slots[j] == slots[i]) { visualSlots[j].ShowBorderHighlight(GUI.Style.Red, 0.1f, 0.9f); }
                            }
#endif
                        }
                    }
                }

                if (!free) { continue; }

                for (int i = 0; i < capacity; i++)
                {
                    if (allowedSlot.HasFlag(SlotTypes[i]) && item.GetComponents<Pickable>().Any(p => p.AllowedSlots.Any(s => s.HasFlag(SlotTypes[i]))) && slots[i].Empty())
                    {
#if CLIENT
                        if (PersonalSlots.HasFlag(SlotTypes[i])) { hidePersonalSlots = false; }
#endif
                        bool removeFromOtherSlots = item.ParentInventory != this;
                        if (placedInSlot == -1 && inWrongSlot)
                        {
                            if (!slots[i].HideIfEmpty || SlotTypes[currentSlot] != InvSlotType.Any) { removeFromOtherSlots = true; }
                        }

                        PutItem(item, i, user, removeFromOtherSlots, createNetworkEvent);
                        item.Equip(character);
                        placedInSlot = i;
                    }
                }
                if (placedInSlot > -1) { break; }
            }

            return placedInSlot > -1;
        }

        public int CheckIfAnySlotAvailable(Item item, bool inWrongSlot)
        {
            //attempt to stack first
            for (int i = 0; i < capacity; i++)
            {
                if (SlotTypes[i] != InvSlotType.Any) { continue; }
                if (!slots[i].Empty() && CanBePutInSlot(item, i))
                {
                    return i;
                }
            }
            for (int i = 0; i < capacity; i++)
            {
                if (SlotTypes[i] != InvSlotType.Any) { continue; }
                if (slots[i].Contains(item))
                {
                    return i;
                }
            }
            for (int i = 0; i < capacity; i++)
            {
                if (SlotTypes[i] != InvSlotType.Any) { continue; }
                if (CanBePutInSlot(item, i))
                {
                    return i;
                }
            }
            for (int i = 0; i < capacity; i++)
            {
                if (SlotTypes[i] != InvSlotType.Any) { continue; }
                if (inWrongSlot)
                {
                    //another item already in the slot
                    if (slots[i].Any() && slots[i].Items.Any(it => it != item)) { continue; }
                }
                else
                {
                    if (!CanBePutInSlot(item, i)) { continue; }
                }
                return i;
            }
            return -1;
        }

        public override bool TryPutItem(Item item, int index, bool allowSwapping, bool allowCombine, Character user, bool createNetworkEvent = true, bool ignoreCondition = false)
        {
            if (index < 0 || index >= slots.Length)
            {
                string errorMsg = "CharacterInventory.TryPutItem failed: index was out of range(" + index + ").\n" + Environment.StackTrace.CleanupStackTrace();
                GameAnalyticsManager.AddErrorEventOnce("CharacterInventory.TryPutItem:IndexOutOfRange", GameAnalyticsSDK.Net.EGAErrorSeverity.Error, errorMsg);
                return false;
            }
#if CLIENT
            if (PersonalSlots.HasFlag(SlotTypes[index])) { hidePersonalSlots = false; }
#endif
            //there's already an item in the slot
            if (slots[index].Any())
            {
                if (slots[index].Contains(item)) { return false; }
                return base.TryPutItem(item, index, allowSwapping, allowCombine, user, createNetworkEvent, ignoreCondition);
            }

            if (SlotTypes[index] == InvSlotType.Any)
            {
                if (!item.GetComponents<Pickable>().Any(p => p.AllowedSlots.Contains(InvSlotType.Any))) { return false; }
                if (slots[index].Any()) { return slots[index].Contains(item); }
                PutItem(item, index, user, true, createNetworkEvent);
                return true;
            }

            InvSlotType placeToSlots = InvSlotType.None;

            bool slotsFree = true;
            foreach (Pickable pickable in item.GetComponents<Pickable>())
            {
                foreach (InvSlotType allowedSlot in pickable.AllowedSlots)
                {
                    if (!allowedSlot.HasFlag(SlotTypes[index])) { continue; }
    #if CLIENT
                    if (PersonalSlots.HasFlag(allowedSlot)) { hidePersonalSlots = false; }
    #endif
                    for (int i = 0; i < capacity; i++)
                    {
                        if (allowedSlot.HasFlag(SlotTypes[i]) && slots[i].Any() && !slots[i].Contains(item))
                        {
                            slotsFree = false;
                            break;
                        }
                        placeToSlots = allowedSlot;
                    }
                }
            }

            if (!slotsFree) { return false; }

            return TryPutItem(item, user, new List<InvSlotType>() { placeToSlots }, createNetworkEvent, ignoreCondition);
        }
    }
}

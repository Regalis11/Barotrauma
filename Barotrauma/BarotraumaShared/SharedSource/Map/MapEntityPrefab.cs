using Microsoft.Xna.Framework;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Xml.Linq;
using Barotrauma.Extensions;

namespace Barotrauma
{
    [Flags]
    enum MapEntityCategory
    {
        None = 0,
        Structure = 1, 
        Decorative = 2,
        Machine = 4,
        Medical = 8,
        Weapon = 16,
        Diving = 32,
        Equipment = 64,
        Fuel = 128,
        Electrical = 256,
        Material = 1024,
        Alien = 2048,
        Wrecked = 4096,
        ItemAssembly = 8192,
        Legacy = 16384,
        Misc = 32768
    }

    abstract partial class MapEntityPrefab : PrefabWithUintIdentifier
    {
        public static IEnumerable<MapEntityPrefab> List
        {
            get
            {
                foreach (var ep in CoreEntityPrefab.Prefabs)
                {
                    yield return ep;
                }

                foreach (var ep in StructurePrefab.Prefabs)
                {
                    yield return ep;
                }

                foreach (var ep in ItemPrefab.Prefabs)
                {
                    yield return ep;
                }

                foreach (var ep in ItemAssemblyPrefab.Prefabs)
                {
                    yield return ep;
                }
            }
        }

        //which prefab has been selected for placing
        public static MapEntityPrefab Selected { get; set; }

        //the position where the structure is being placed (needed when stretching the structure)
        protected static Vector2 placePosition;

        public static bool SelectPrefab(object selection)
        {
            if ((Selected = selection as MapEntityPrefab) != null)
            {
                placePosition = Vector2.Zero;
                return true;
            }
            else
            {
                return false;
            }
        }

        //a method that allows the GUIListBoxes to check through a delegate if the entityprefab is still selected
        public static object GetSelected()
        {
            return (object)Selected;
        }

        /// <summary>
        /// Find a matching map entity prefab
        /// </summary>
        /// <param name="name">The name of the item (can be omitted when searching based on identifier)</param>
        /// <param name="identifier">The identifier of the item (if null, the identifier is ignored and the search is done only based on the name)</param>
        [Obsolete("Prefer MapEntityPrefab.FindByIdentifier or MapEntityPrefab.FindByName")]
        public static MapEntityPrefab Find(string name, string identifier = null, bool showErrorMessages = true)
        {
            return Find(name, (identifier ?? "").ToIdentifier(), showErrorMessages);
        }

        [Obsolete("Prefer MapEntityPrefab.FindByIdentifier or MapEntityPrefab.FindByName")]
        public static MapEntityPrefab Find(string name, Identifier identifier, bool showErrorMessages = true)
        {
            //try to search based on identifier first
            if (string.IsNullOrEmpty(name) && !identifier.IsEmpty)
            {
                if (CoreEntityPrefab.Prefabs.ContainsKey(identifier)) { return CoreEntityPrefab.Prefabs[identifier]; }
                if (StructurePrefab.Prefabs.ContainsKey(identifier)) { return StructurePrefab.Prefabs[identifier]; }
                if (ItemPrefab.Prefabs.ContainsKey(identifier)) { return ItemPrefab.Prefabs[identifier]; }
                if (ItemAssemblyPrefab.Prefabs.ContainsKey(identifier)) { return ItemAssemblyPrefab.Prefabs[identifier]; }
            }

            foreach (MapEntityPrefab prefab in List)
            {
                if (!identifier.IsEmpty)
                {
                    if (prefab.Identifier != identifier)
                    {
                        if (prefab.Aliases != null && prefab.Aliases.Any(a => a == identifier))
                        {
                            return prefab;
                        }
                        continue;
                    }
                    else
                    {
                        if (string.IsNullOrEmpty(name)) { return prefab; }
                    }
                }
                if (!string.IsNullOrEmpty(name))
                {
                    if (prefab.Name.Equals(name, StringComparison.OrdinalIgnoreCase) ||
                        prefab.OriginalName.Equals(name, StringComparison.OrdinalIgnoreCase) ||
                        (prefab.Aliases != null && prefab.Aliases.Any(a => a.Equals(name, StringComparison.OrdinalIgnoreCase))))
                    {
                        return prefab;
                    }
                }
            }

            if (showErrorMessages)
            {
                DebugConsole.ThrowError("Failed to find a matching MapEntityPrefab (name: \"" + name + "\", identifier: \"" + identifier + "\").\n" + Environment.StackTrace.CleanupStackTrace());
            }
            return null;
        }

        public static MapEntityPrefab GetRandom(Predicate<MapEntityPrefab> predicate, Rand.RandSync sync)
        {
            return List.GetRandom(p => predicate(p), sync);
        }

        /// <summary>
        /// Find a matching map entity prefab
        /// </summary>
        /// <param name="predicate">A predicate that returns true on the desired prefab.</param>
        public static MapEntityPrefab Find(Predicate<MapEntityPrefab> predicate)
        {
            return List.FirstOrDefault(p => predicate(p));
        }


        public static MapEntityPrefab FindByName(string name)
        {
            if (name.IsNullOrEmpty()) { throw new ArgumentException($"{nameof(name)} must not be null or empty"); }

            var matches = 
                List.Where(prefab =>
                    prefab.Name.Equals(name, StringComparison.OrdinalIgnoreCase) ||
                    prefab.OriginalName.Equals(name, StringComparison.OrdinalIgnoreCase) ||
                    (prefab.Aliases != null &&
                     prefab.Aliases.Any(a => a.Equals(name, StringComparison.OrdinalIgnoreCase))));
            //if there's multiple matches, prefer ones that aren't hidden in menus and base items
            //(hidden ones and variants are often e.g. some kind of special ones used in an event or versions unlockable with talents)
            if (matches.Count() > 1)
            {
                var bestMatch = 
                    matches.FirstOrDefault(prefab => !prefab.HideInMenus) ??
                    matches.FirstOrDefault(prefab => prefab is ItemPrefab ip && ip.VariantOf.IsEmpty);
                if (bestMatch != null) { return bestMatch; }
            }
            return matches.FirstOrDefault();
        }
        
        public static MapEntityPrefab FindByIdentifier(Identifier identifier)
            => CoreEntityPrefab.Prefabs.TryGet(identifier, out var corePrefab) ? corePrefab
                : ItemPrefab.Prefabs.TryGet(identifier, out var itemPrefab) ? itemPrefab
                : StructurePrefab.Prefabs.TryGet(identifier, out var structurePrefab) ? structurePrefab
                : ItemAssemblyPrefab.Prefabs.TryGet(identifier, out var itemAssemblyPrefab) ? itemAssemblyPrefab
                : (MapEntityPrefab)null;
        
        public abstract Sprite Sprite { get; }

        public virtual bool CanSpriteFlipX { get; } = false;
        public virtual bool CanSpriteFlipY { get; } = false;

        public abstract string OriginalName { get; }

        public abstract LocalizedString Name { get; }

        public abstract ImmutableHashSet<Identifier> Tags { get; }

        /// <summary>
        /// Links defined to identifiers.
        /// </summary>
        public abstract ImmutableHashSet<Identifier> AllowedLinks { get; }

        public abstract MapEntityCategory Category { get; }

        //If a matching prefab is not found when loading a sub, the game will attempt to find a prefab with a matching alias.
        //(allows changing names while keeping backwards compatibility with older sub files)
        public abstract ImmutableHashSet<string> Aliases { get; }

        //is it possible to stretch the entity horizontally/vertically
        [Serialize(false, IsPropertySaveable.No)]
        public bool ResizeHorizontal { get; protected set; }

        [Serialize(false, IsPropertySaveable.No)]
        public bool ResizeVertical { get; protected set; }

        [Serialize("", IsPropertySaveable.No)]
        public LocalizedString Description { get; protected set; }
        
        [Serialize("", IsPropertySaveable.No)]
        public string AllowedUpgrades { get; protected set; }

        [Serialize(false, IsPropertySaveable.No)]
        public bool HideInMenus { get; protected set; }

        [Serialize(false, IsPropertySaveable.No)]
        public bool HideInEditors { get; protected set; }

        [Serialize("", IsPropertySaveable.No)]
        public string Subcategory { get; protected set; }

        [Serialize(false, IsPropertySaveable.No)]
        public bool Linkable { get; protected set; }

        [Serialize("1.0,1.0,1.0,1.0", IsPropertySaveable.No)]
        public Color SpriteColor { get; protected set; }

        [Serialize(1f, IsPropertySaveable.Yes), Editable(0.1f, 10f, DecimalCount = 3)]
        public float Scale { get; protected set; }

        protected MapEntityPrefab(Identifier identifier) : base(ContentFile.Empty, identifier) { }

        public MapEntityPrefab(ContentXElement element, ContentFile file) : base(file, element) { }

        public string GetItemNameTextId()
        {
            var textId = $"entityname.{Identifier}";
            return TextManager.ContainsTag(textId) ? textId : null;
        }

        public string GetHullNameTextId()
        {
            var textId = $"roomname.{Identifier}";
            return TextManager.ContainsTag(textId) ? textId : null;
        }

        private string cachedAllowedUpgrades = "";
        private ImmutableHashSet<Identifier> allowedUpgradeSet;
        public IEnumerable<Identifier> GetAllowedUpgrades()
        {
            if (string.IsNullOrWhiteSpace(AllowedUpgrades)) { return Enumerable.Empty<Identifier>(); }
            if (allowedUpgradeSet is null || cachedAllowedUpgrades != AllowedUpgrades)
            {
                allowedUpgradeSet = AllowedUpgrades.ToIdentifiers().ToImmutableHashSet();
                cachedAllowedUpgrades = AllowedUpgrades;
            }

            return allowedUpgradeSet;
        }

        public bool HasSubCategory(string subcategory)
        {
            return subcategory?.Equals(this.Subcategory, StringComparison.OrdinalIgnoreCase) ?? false;
        }

        protected abstract void CreateInstance(Rectangle rect);

#if DEBUG
        public void DebugCreateInstance()
        {
            Rectangle rect = new Rectangle(new Point((int)Screen.Selected.Cam.WorldViewCenter.X, (int)Screen.Selected.Cam.WorldViewCenter.Y), new Point((int)Submarine.GridSize.X, (int)Submarine.GridSize.Y));
            CreateInstance(rect);
        }
#endif

        /// <summary>
        /// Check if the name or any of the aliases of this prefab match the given name.
        /// </summary>
        public bool NameMatches(string name, StringComparison comparisonType) => OriginalName.Equals(name, comparisonType) || (Aliases != null && Aliases.Any(a => a.Equals(name, comparisonType)));

        public bool NameMatches(IEnumerable<string> allowedNames, StringComparison comparisonType) => allowedNames.Any(n => NameMatches(n, comparisonType));

        public bool IsLinkAllowed(MapEntityPrefab target)
        {
            if (target == null) { return false; }
            if (target is StructurePrefab && AllowedLinks.Contains("structure".ToIdentifier())) { return true; }
            if (target is ItemPrefab && AllowedLinks.Contains("item".ToIdentifier())) { return true; }
            if (target is LinkedSubmarinePrefab && Tags.Contains("dock".ToIdentifier())) { return true; }
            if (this is LinkedSubmarinePrefab && target.Tags.Contains("dock".ToIdentifier())) { return true; }
            return AllowedLinks.Contains(target.Identifier) || target.AllowedLinks.Contains(Identifier)
                   || target.Tags.Any(t => AllowedLinks.Contains(t)) || Tags.Any(t => target.AllowedLinks.Contains(t));
        }

        protected void LoadDescription(ContentXElement element)
        {
            Identifier nameIdentifier = element.GetAttributeIdentifier("nameidentifier", Identifier.Empty);
            string originalDescription = Description.Value;
            const string descriptionIdentifierAttributeName = "descriptionidentifier";
            XAttribute descriptionIdenfifierAttribute = element.GetAttribute(descriptionIdentifierAttributeName);
            if (descriptionIdenfifierAttribute != null)
            {
                Identifier descriptionIdentifier = element.GetAttributeIdentifier(descriptionIdentifierAttributeName, Identifier.Empty);
                Description = TextManager.Get($"EntityDescription.{descriptionIdentifier}");
            }
            else if (nameIdentifier == Identifier.Empty)
            {
                Description = TextManager.Get($"EntityDescription.{Identifier}");
            }
            else
            {
                Description = TextManager.Get($"EntityDescription.{nameIdentifier}");
            }
            if (!originalDescription.IsNullOrEmpty())
            {
                Description = Description.Fallback(originalDescription);
            }
        }
    }
}

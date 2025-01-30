using Microsoft.Xna.Framework;
using System.Xml.Linq;
using System.Linq;
using Barotrauma.Extensions;
using System;
using SpriteParams = Barotrauma.RagdollParams.SpriteParams;
#if CLIENT
using Microsoft.Xna.Framework.Graphics;
#endif

namespace Barotrauma
{
    public partial class Sprite
    {
        /// <summary>
        /// Reference to the xml element from where the sprite was created. Can be null if the sprite was not defined in xml!
        /// </summary>
        public ContentXElement SourceElement { get; private set; }

        //the area in the texture that is supposed to be drawn
        private Rectangle sourceRect;

        //the offset used when drawing the sprite
        protected Vector2 offset;

        public bool LazyLoad
        {
            get;
            private set;
        }

        protected Vector2 origin;

        //the size of the drawn sprite, if larger than the source,
        //the sprite is tiled to fill the target size
        public Vector2 size = Vector2.One;

        public float rotation;

#if CLIENT
        public SpriteEffects effects = SpriteEffects.None;
#endif

        protected float depth;

        public Rectangle SourceRect
        {
            get { return sourceRect; }
            set { sourceRect = value; }
        }

        public float Depth
        {
            get { return depth; }
            set { depth = MathHelper.Clamp(value, 0.001f, 0.999f); }
        }

        /// <summary>
        /// In pixels
        /// </summary>
        public Vector2 Origin
        {
            get { return origin; }
            set
            {
                origin = value;
                _relativeOrigin = new Vector2(origin.X / sourceRect.Width, origin.Y / sourceRect.Height);
            }
        }

        private Vector2 _relativeOrigin;
        /// <summary>
        /// 0 - 1
        /// </summary>
        public Vector2 RelativeOrigin
        {
            get => _relativeOrigin;
            set
            {
                _relativeOrigin = value;
                origin = new Vector2(_relativeOrigin.X * sourceRect.Width, _relativeOrigin.Y * sourceRect.Height);
            }
        }

        public Vector2 RelativeSize { get; private set; }

        public ContentPath FilePath { get; private set; }

        public string FullPath => FilePath.FullPath;

        public bool Compress { get; private set; }

        public override string ToString()
        {
            return FilePath + ": " + sourceRect;
        }

        /// <summary>
        /// Identifier of the Map Entity so that we can link the sprite to its owner.
        /// </summary>
        public Identifier EntityIdentifier { get; set; }
        public string Name { get; set; }

        public Color Color { get; set; }
        
        partial void LoadTexture(ref Vector4 sourceVector, ref bool shouldReturn);

        partial void CalculateSourceRect();

        static partial void AddToList(Sprite sprite);

        public Sprite(ContentXElement element, string path = "", string file = "", bool lazyLoad = false, float sourceRectScale = 1)
        {
            if (element is null)
            {
                DebugConsole.ThrowError($"Sprite: xml element null in {file}. Failed to create the sprite!");
                return;
            }
            this.LazyLoad = lazyLoad;
            SourceElement = element;
            if (!ParseTexturePath(path, file)) { return; }
            Name = SourceElement.GetAttributeString("name", null);
            Vector4 sourceVector = SourceElement.GetAttributeVector4("sourcerect", Vector4.Zero);
            var overrideElement = GetLocalizationOverrideElement();
            if (overrideElement != null && overrideElement.Attribute("sourcerect") != null)
            {
                sourceVector = overrideElement.GetAttributeVector4("sourcerect", Vector4.Zero);
            }
            if ((overrideElement ?? SourceElement).Attribute("sheetindex") != null)
            {
                Point sheetElementSize = (overrideElement ?? SourceElement).GetAttributePoint("sheetelementsize", Point.Zero);
                Point sheetIndex = (overrideElement ?? SourceElement).GetAttributePoint("sheetindex", Point.Zero);
                sourceVector = new Vector4(sheetIndex.X * sheetElementSize.X, sheetIndex.Y * sheetElementSize.Y, sheetElementSize.X, sheetElementSize.Y);
            }
            Compress = SourceElement.GetAttributeBool("compress", true);
            bool shouldReturn = false;
            if (!lazyLoad)
            {
                LoadTexture(ref sourceVector, ref shouldReturn);
            }
            if (shouldReturn) { return; }
            sourceRect = new Rectangle((int)(sourceVector.X * sourceRectScale), (int)(sourceVector.Y * sourceRectScale), (int)(sourceVector.Z * sourceRectScale), (int)(sourceVector.W * sourceRectScale));
            size = SourceElement.GetAttributeVector2("size", Vector2.One);
            RelativeSize = size;
            size.X *= sourceRect.Width;
            size.Y *= sourceRect.Height;
            RelativeOrigin = SourceElement.GetAttributeVector2("origin", new Vector2(0.5f, 0.5f));
            Depth = SourceElement.GetAttributeFloat("depth", 0.001f);
            Color = SourceElement.GetAttributeColor("color", Color.White);
#if CLIENT
            AddToList(this);
#endif
        }

        internal void LoadParams(SpriteParams spriteParams, bool isFlipped)
        {
            SourceElement = spriteParams.Element;
            sourceRect = spriteParams.SourceRect;
            RelativeOrigin = spriteParams.Origin;
            if (isFlipped)
            {
                Origin = new Vector2(sourceRect.Width - origin.X, origin.Y);
            }
            depth = spriteParams.Depth;
        }

        public Sprite(string newFile, Vector2 newOrigin)
        {
            Init(newFile, newOrigin: newOrigin);
            AddToList(this);
        }
        
        public Sprite(string newFile, Rectangle? sourceRectangle, Vector2? origin = null, float rotation = 0)
        {
            Init(newFile, sourceRectangle: sourceRectangle, newOrigin: origin, newRotation: rotation);
            AddToList(this);
        }
        
        private void Init(string newFile, Rectangle? sourceRectangle = null, Vector2? newOrigin = null, Vector2? newOffset = null, float newRotation = 0)
        {
            FilePath = ContentPath.FromRaw(newFile);
            Vector4 sourceVector = Vector4.Zero;
            bool shouldReturn = false;
            LoadTexture(ref sourceVector, ref shouldReturn);
            if (shouldReturn) return;
            if (sourceRectangle.HasValue)
            {
                sourceRect = sourceRectangle.Value;
            }
            else
            {
                CalculateSourceRect();
            }
            offset = newOffset ?? Vector2.Zero;
            if (newOrigin.HasValue)
            {
                RelativeOrigin = newOrigin.Value;
            }
            size = new Vector2(sourceRect.Width, sourceRect.Height);
            rotation = newRotation;
        }

        /// <summary>
        /// Creates a supposedly unique identifier from the parent element. If the parent element is not found, uses the sprite element.
        /// TODO: If there are multiple elements with exactly the same data, the ids will fail. -> Is there a better way to identify the sprites?
        /// ALSO TODO: delete :)
        /// </summary>
        public static Identifier GetIdentifier(XElement sourceElement)
        {
            if (sourceElement == null) { return "".ToIdentifier(); }
            var parentElement = sourceElement.Parent;
            return $"{sourceElement}{parentElement?.ToString() ?? ""}".ToIdentifier();
        }

        static partial void RemoveFromList(Sprite sprite);
        
        public void Remove()
        {
            RemoveFromList(this);
            DisposeTexture();
        }

        ~Sprite()
        {
            Remove();
        }

        partial void DisposeTexture();

        /// <summary>
        /// Works only if there is a name attribute defined for the sprite. For items and structures, the entity id or name is used if the sprite's name attribute is not defined.
        /// </summary>
        public void ReloadXML()
        {
            if (SourceElement == null) { return; }
            string path = SourceElement.ParseContentPathFromUri();
            if (string.IsNullOrWhiteSpace(path))
            {
                DebugConsole.NewMessage($"[Sprite] Could not parse the content path from the source element ({SourceElement}) uri: {SourceElement.BaseUri}", Color.Yellow);
                return;
            }
            var doc = XMLExtensions.TryLoadXml(path);
            if (doc == null) { return; }
            if (string.IsNullOrWhiteSpace(Name) && string.IsNullOrWhiteSpace(EntityIdentifier.Value)) { return; }
            var spriteElements = doc.Descendants("sprite").Concat(doc.Descendants("Sprite"));
            var sourceElements = spriteElements.Where(e => e.GetAttributeString("name", null) == Name);
            if (sourceElements.None())
            {
                // Try parents by first comparing the entity id and then the name, if no match was found.
                sourceElements = spriteElements.Where(e => e.Parent?.GetAttributeString("identifier", null) == EntityIdentifier);
                if (sourceElements.None())
                {
                    sourceElements = spriteElements.Where(e => e.Parent?.GetAttributeString("name", null) == Name);
                }
            }
            if (sourceElements.Multiple())
            {
                DebugConsole.NewMessage($"[Sprite] Multiple matching elements found by name ({Name}) or identifier ({EntityIdentifier})!: {SourceElement}", Color.Yellow);
            }
            else if (sourceElements.None())
            {
                DebugConsole.NewMessage($"[Sprite] Cannot find matching source element by comparing the name attribute ({Name}) or identifier ({EntityIdentifier})! Cannot reload the xml for sprite element \"{SourceElement.ToString()}\"!", Color.Yellow);
            }
            else
            {
                SourceElement = sourceElements.Single().FromPackage(SourceElement.ContentPackage);
            }
            if (SourceElement != null)
            {
                sourceRect = SourceElement.GetAttributeRect("sourcerect", Rectangle.Empty);
                var overrideElement = GetLocalizationOverrideElement();
                if (overrideElement != null && overrideElement.Attribute("sourcerect") != null)
                {
                    sourceRect = overrideElement.GetAttributeRect("sourcerect", Rectangle.Empty);
                }
                if ((overrideElement ?? SourceElement).Attribute("sheetindex") != null)
                {
                    Point sheetElementSize = (overrideElement ?? SourceElement).GetAttributePoint("sheetelementsize", Point.Zero);
                    Point sheetIndex = (overrideElement ?? SourceElement).GetAttributePoint("sheetindex", Point.Zero);
                    sourceRect = new Rectangle(sheetIndex.X * sheetElementSize.X, sheetIndex.Y * sheetElementSize.Y, sheetElementSize.X, sheetElementSize.Y);
                }
                size = SourceElement.GetAttributeVector2("size", Vector2.One);
                size.X *= sourceRect.Width;
                size.Y *= sourceRect.Height;
                RelativeOrigin = SourceElement.GetAttributeVector2("origin", new Vector2(0.5f, 0.5f));
                Depth = SourceElement.GetAttributeFloat("depth", 0.001f);
            }
        }

        public bool ParseTexturePath(string path = "", string file = "")
        {
#if SERVER
            // Server doesn't care about texture paths at all
            return true;
#endif
            if (file == "")
            {
                file = SourceElement.GetAttributeStringUnrestricted("texture", "");
                var overrideElement = GetLocalizationOverrideElement();
                if (overrideElement != null)
                {
                    string overrideFile = overrideElement.GetAttributeStringUnrestricted("texture", "");
                    if (!string.IsNullOrEmpty(overrideFile)) { file = overrideFile; }
                }
            }
            if (file == "")
            {
                DebugConsole.ThrowError("Sprite " + SourceElement.Element + " doesn't have a texture specified!",
                    contentPackage: SourceElement.ContentPackage);
                return false;
            }
            if (!string.IsNullOrEmpty(path))
            {
                if (!path.EndsWith("/")) path += "/";
            }
            FilePath = ContentPath.FromRaw(SourceElement.ContentPackage, (path + file).CleanUpPathCrossPlatform(correctFilenameCase: true));
            return true;
        }

        private XElement GetLocalizationOverrideElement()
        {
            foreach (var subElement in SourceElement.Elements())
            {
                if (subElement.Name.ToString().Equals("override", StringComparison.OrdinalIgnoreCase))
                {
                    LanguageIdentifier language = subElement.GetAttributeIdentifier("language", "").ToLanguageIdentifier();
                    if (GameSettings.CurrentConfig.Language == language)
                    {
                        return subElement;
                    }
                }
            }
            return null;
        }
    }
}


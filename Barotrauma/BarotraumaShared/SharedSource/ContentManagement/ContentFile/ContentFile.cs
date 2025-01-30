#nullable enable
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using Barotrauma.IO;
using System.Linq;
using System.Reflection;
using System.Xml.Linq;

namespace Barotrauma
{
    [AttributeUsage(AttributeTargets.Class, Inherited = true)]
    public class NotSyncedInMultiplayer : Attribute { }
    
    /// <summary>
    /// Base class for content file types, which are loaded
    /// from filelist.xml via reflection.
    /// PLEASE AVOID INHERITING FROM THIS CLASS DIRECTLY.
    /// Inheriting from GenericPrefabFile&lt;T&gt; is likely what
    /// you want.
    /// </summary>
    public abstract class ContentFile
    {
        public class TypeInfo
        {
            public readonly Type Type;
            public readonly bool RequiredByCorePackage;
            public readonly bool NotSyncedInMultiplayer;
            public readonly ImmutableHashSet<Type>? AlternativeTypes;
            public readonly ImmutableHashSet<Identifier> Names;
            private readonly MethodInfo? contentPathMutator;

            public TypeInfo(Type type)
            {
                Type = type;
                
                var reqByCoreAttribute = type.GetCustomAttribute<RequiredByCorePackage>();
                RequiredByCorePackage = reqByCoreAttribute != null;
                var notSyncedInMultiplayerAttribute = type.GetCustomAttribute<NotSyncedInMultiplayer>();
                NotSyncedInMultiplayer = notSyncedInMultiplayerAttribute != null;
                AlternativeTypes = reqByCoreAttribute?.AlternativeTypes;
                contentPathMutator
                    = Type.GetMethod(nameof(MutateContentPath), BindingFlags.Static | BindingFlags.Public);

                HashSet<Identifier> names = new HashSet<Identifier> { type.Name.RemoveFromEnd("File").ToIdentifier() };
                if (type.GetCustomAttribute<AlternativeContentTypeNames>(inherit: false)?.Names is { } altNames)
                {
                    names.UnionWith(altNames);
                }

                Names = names.ToImmutableHashSet();
            }

            public ContentPath MutateContentPath(ContentPath path)
                => (ContentPath?)contentPathMutator?.Invoke(null, new object[] { path })
                   ?? path;
            
            public ContentFile? CreateInstance(ContentPackage contentPackage, ContentPath path) =>
                (ContentFile?)Activator.CreateInstance(Type, contentPackage, path);
        }
        
        public readonly static ImmutableHashSet<TypeInfo> Types;


        private ContentFile(ContentPackage contentPackage, ContentPath path, Md5Hash hash)
        {
            ContentPackage = contentPackage;
            Path = path;
            Hash = hash;
        }
        private class EmptyContentFile : ContentFile
        {
            public EmptyContentFile() : base(ContentPackage.Empty, ContentPath.Empty, Md5Hash.Blank)
            { }

            public override void LoadFile()
            {
                throw new NotImplementedException();
            }

            public override void Sort()
            {
                throw new NotImplementedException();
            }

            public override void UnloadFile()
            {
                throw new NotImplementedException();
            }
        }

        public readonly static ContentFile Empty = new EmptyContentFile();
        static ContentFile()
        {
            Types = ReflectionUtils.GetDerivedNonAbstract<ContentFile>()
                .Select(t => new TypeInfo(t))
                .ToImmutableHashSet();
        }

        public static bool IsLegacyContentType(XElement contentFileElement, ContentPackage package, bool logWarning)
        {
            Identifier elemName = contentFileElement.NameAsIdentifier();
            if (elemName == "TraitorMissions")
            {
                if (logWarning)
                {
                    DebugConsole.AddWarning(
                      $"The content type \"TraitorMission\" in content package \"{package.Name}\" is no longer supported." +
                      $" Traitor missions should be implemented using the scripted event system and the content type TraitorEvents.",
                      package);
                }
                return true;
            }
            return false;
        }

        public static Result<ContentFile, ContentPackage.LoadError> CreateFromXElement(ContentPackage contentPackage, XElement element)
        {
            static Result<ContentFile, ContentPackage.LoadError> fail(string error, Exception? exception = null)
                => Result<ContentFile, ContentPackage.LoadError>.Failure(new ContentPackage.LoadError(error, exception));
            
            Identifier elemName = element.NameAsIdentifier();
            var type = Types.FirstOrDefault(t => t.Names.Contains(elemName));
            var filePath = element.GetAttributeContentPath("file", contentPackage);
            if (type is null)
            {
                return fail($"Invalid content type \"{elemName}\"");
            }
            if (filePath is null)
            {
                return fail($"No content path defined for file of type \"{elemName}\"");
            }

            using var errorCatcher = DebugConsole.ErrorCatcher.Create();
            try
            {
                filePath = type.MutateContentPath(filePath);
                if (!File.Exists(filePath.FullPath))
                {
                    return fail($"Failed to load file \"{filePath}\" of type \"{elemName}\": file not found.");
                }

                var file = type.CreateInstance(contentPackage, filePath);
                if (file is null) { return fail($"Content type {type.Type.Name} is not implemented correctly"); }

                if (errorCatcher.Errors.Any())
                {
                    return fail(
                        $"Errors were issued to the debug console when loading \"{filePath}\" of type \"{elemName}\"");
                }
                return Result<ContentFile, ContentPackage.LoadError>.Success(file);
            }
            catch (Exception e)
            {
                return fail($"Failed to load file \"{filePath}\" of type \"{elemName}\": {e.Message}", e);
            }
        }

        protected ContentFile(ContentPackage contentPackage, ContentPath path)
        {
            ContentPackage = contentPackage;
            Path = path;
            Hash = CalculateHash();
        }

        public readonly ContentPackage ContentPackage;
        public readonly ContentPath Path;
        public readonly Md5Hash Hash;
        public abstract void LoadFile();
        public abstract void UnloadFile();
        public abstract void Sort();

        public virtual void Preload(Action<Sprite> addPreloadedSprite) { }

        public virtual Md5Hash CalculateHash()
        {
            return Md5Hash.CalculateForFile(Path.Value, Md5Hash.StringHashOptions.IgnoreWhitespace);
        }

        public bool NotSyncedInMultiplayer => Types.Any(t => t.Type == GetType() && t.NotSyncedInMultiplayer);
    }
}
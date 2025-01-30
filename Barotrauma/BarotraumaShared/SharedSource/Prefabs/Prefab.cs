#nullable enable

using System;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Xml.Linq;

namespace Barotrauma
{
    public abstract class Prefab
    {
        public readonly static ImmutableHashSet<Type> Types;
        static Prefab()
        {
            Types = ReflectionUtils.GetDerivedNonAbstract<Prefab>().ToImmutableHashSet();
        }

        private static bool potentialCallFromConstructor = false;
        public static void DisallowCallFromConstructor()
        {
            if (!potentialCallFromConstructor) { return; }
            StackTrace st = new StackTrace(skipFrames: 2, fNeedFileInfo: false);
            for (int i = st.FrameCount - 1; i >= 0; i--)
            {
                if (st.GetFrame(i)?.GetMethod() is { IsConstructor: true, DeclaringType: { } declaringType }
                    && Types.Contains(declaringType))
                {
                    throw new Exception("Called disallowed method from within a prefab's constructor!");
                }
            }
            potentialCallFromConstructor = false;
        }
        
        public readonly Identifier Identifier;
        public readonly ContentFile ContentFile;

        public ContentPackage ContentPackage => ContentFile.ContentPackage;
        public ContentPath FilePath => ContentFile.Path;

        public Prefab(ContentFile file, Identifier identifier)
        {
            potentialCallFromConstructor = true;
            ContentFile = file;
            Identifier = identifier;
            if (Identifier.IsEmpty) { throw new ArgumentException($"Error creating {GetType().Name}: Identifier cannot be empty"); }
        }

        public Prefab(ContentFile file, ContentXElement element)
        {
            potentialCallFromConstructor = true;
            ContentFile = file;
            Identifier = DetermineIdentifier(element!);
            if (Identifier.IsEmpty) { throw new ArgumentException($"Error creating {GetType().Name}: Identifier cannot be empty"); }
        }

        protected virtual Identifier DetermineIdentifier(XElement element)
        {
            return element.GetAttributeIdentifier("identifier", Identifier.Empty);
        }

        public abstract void Dispose();
    }
}

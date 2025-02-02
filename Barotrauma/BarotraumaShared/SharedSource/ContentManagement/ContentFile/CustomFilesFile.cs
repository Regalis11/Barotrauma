using System;
using Barotrauma.IO;

namespace Barotrauma
{
    sealed class CustomFilesFile : OtherFile
    {
        public CustomFilesFile(ContentPackage contentPackage, ContentPath path) : base(contentPackage, path) { }

        public static ContentPath MutateContentPath(ContentPath path)
        {
            if (File.Exists(path.FullPath)) { return path; }

            string rawValueWithoutExtension()
                => Barotrauma.IO.Path.Combine(
                    Barotrauma.IO.Path.GetDirectoryName(path.RawValue ?? ""),
                    Barotrauma.IO.Path.GetFileNameWithoutExtension(path.RawValue ?? "")).CleanUpPath();
            
            path = ContentPath.FromRaw(path.ContentPackage, rawValueWithoutExtension());
            if (File.Exists(path.FullPath)) { return path; }
            
            path = ContentPath.FromRaw(path.ContentPackage,
                rawValueWithoutExtension() + ".zip");
            return path;
        }
    }
}

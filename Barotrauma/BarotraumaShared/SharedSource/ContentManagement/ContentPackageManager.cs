#nullable enable
using Barotrauma.Extensions;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Xml.Linq;
using Barotrauma.IO;
using Barotrauma.Steam;
using Microsoft.Xna.Framework;

namespace Barotrauma
{
    public static partial class ContentPackageManager
    {
        public const string CopyIndicatorFileName = ".copying";
        public const string VanillaFileList = "Content/ContentPackages/Vanilla.xml";

        public const string CorePackageElementName = "corepackage";
        public const string RegularPackagesElementName = "regularpackages";
        public const string RegularPackagesSubElementName = "package";

        public static bool ModsEnabled => GameMain.VanillaContent == null || EnabledPackages.All.Any(p => p.HasMultiplayerSyncedContent && p != GameMain.VanillaContent);

        public static class EnabledPackages
        {
            public static CorePackage? Core { get; private set; } = null;

            private static readonly List<RegularPackage> regular = new List<RegularPackage>();
            public static IReadOnlyList<RegularPackage> Regular => regular;

            /// <summary>
            /// Combined hash of the currently enabled packages. Can be used to check if the enabled mods or their load order has changed.
            /// </summary>
            public static Md5Hash MergedHash { get; private set; } = Md5Hash.Blank;

            public static IEnumerable<ContentPackage> All =>
                Core != null
                    ? (Core as ContentPackage).ToEnumerable().CollectionConcat(Regular)
                    : Enumerable.Empty<ContentPackage>();

            private static class BackupPackages
            {
                public static CorePackage? Core;
                public static ImmutableArray<RegularPackage>? Regular;
            }

            public static void SetCore(CorePackage newCore)
            {
                newCore.TryToInstallCustomFiles();

                SetCoreEnumerable(newCore).Consume();
            }
            
            public static IEnumerable<LoadProgress> SetCoreEnumerable(CorePackage newCore)
            {
                var oldCore = Core;
                if (newCore == oldCore) { yield break; }
                if (newCore.FatalLoadErrors.Any()) { yield break; }
                Core?.UnloadContent();
                Core = newCore;
                foreach (var p in newCore.LoadContentEnumerable()) { yield return p; }
                SortContent();
                yield return LoadProgress.Progress(1.0f);
            }

            public static void ReloadCore()
            {
                if (Core == null) { return; }
                ReloadPackage(Core);
            }

            public static void ReloadPackage(ContentPackage p)
            {
                p.UnloadContent();
                p.LoadContent();
                SortContent();
            }

            public static void EnableRegular(RegularPackage p)
            {
                if (regular.Contains(p)) { return; }

                var newRegular = regular.ToList();
                newRegular.Add(p);
                SetRegular(newRegular);
            }

            public static void SetRegular(IReadOnlyList<RegularPackage> newRegular)
                => SetRegularEnumerable(newRegular).Consume();
            
            public static IEnumerable<LoadProgress> SetRegularEnumerable(IReadOnlyList<RegularPackage> inNewRegular)
            {
                if (ReferenceEquals(inNewRegular, regular)) { yield break; }
                if (inNewRegular.SequenceEqual(regular)) { yield break; }
                ThrowIfDuplicates(inNewRegular);
                var newRegular = inNewRegular
                    // Refuse to enable packages with load errors
                    // so people are forced away from broken mods
                    .Where(r => !r.FatalLoadErrors.Any())
                    .ToList();
                IEnumerable<RegularPackage> toUnload = regular.Where(r => !newRegular.Contains(r));
                RegularPackage[] toLoad = newRegular.Where(r => !regular.Contains(r)).ToArray();
                toUnload.ForEach(r => r.UnloadContent());

                Range<float> loadingRange = new Range<float>(0.0f, 1.0f);
                
                for (int i = 0; i < toLoad.Length; i++)
                {
                    var package = toLoad[i];
                    loadingRange = new Range<float>(i / (float)toLoad.Length, (i + 1) / (float)toLoad.Length);
                    foreach (var progress in package.LoadContentEnumerable())
                    {
                        if (progress.Result.IsFailure)
                        {
                            //If an exception was thrown while loading this package, refuse to add it to the list of enabled packages
                            newRegular.Remove(package);
                            break;
                        }
                        yield return progress.Transform(loadingRange);
                    }
                }
                regular.Clear(); regular.AddRange(newRegular);
                SortContent();
                yield return LoadProgress.Progress(1.0f);
            }

            public static void ThrowIfDuplicates(IEnumerable<ContentPackage> pkgs)
            {
                var contentPackages = pkgs as IList<ContentPackage> ?? pkgs.ToArray();
                foreach (ContentPackage cp in contentPackages)
                {
                    if (contentPackages.AtLeast(2, cp2 => cp == cp2))
                    {
                        throw new InvalidOperationException($"There are duplicates in the list of selected content packages (\"{cp.Name}\", hash: {cp.Hash?.ShortRepresentation ?? "none"})");
                    }
                }
            }

            private class TypeComparer<T> : IEqualityComparer<T>
            {
                public bool Equals([AllowNull] T x, [AllowNull] T y)
                {
                    if (x is null || y is null)
                    {
                        return x is null == y is null;
                    }
                    return x.GetType() == y.GetType();
                }

                public int GetHashCode([DisallowNull] T obj)
                {
                    return obj.GetType().GetHashCode();
                }
            }

            private static void SortContent()
            {
                ThrowIfDuplicates(All);
                All
                    .SelectMany(r => r.Files)
                    .Distinct(new TypeComparer<ContentFile>())
                    .ForEach(f => f.Sort());
                MergedHash = Md5Hash.MergeHashes(All.Select(cp => cp.Hash));
                TextManager.IncrementLanguageVersion();
            }

            public static int IndexOf(ContentPackage contentPackage)
            {
                if (contentPackage is CorePackage core)
                {
                    if (core == Core) { return 0; }
                    return -1;
                }
                else if (contentPackage is RegularPackage reg)
                {
                    return Regular.IndexOf(reg) + 1;
                }
                return -1;
            }

            public static void DisableMods(IReadOnlyCollection<ContentPackage> mods)
            {
                if (Core != null && mods.Contains(Core))
                {
                    var newCore = ContentPackageManager.CorePackages.FirstOrDefault(p => !mods.Contains(p));
                    if (newCore != null)
                    {
                        SetCore(newCore);
                    }
                }
                SetRegular(Regular.Where(p => !mods.Contains(p)).ToArray());
            }
            
            public static void DisableRemovedMods()
            {
                if (Core != null && !ContentPackageManager.CorePackages.Contains(Core))
                {
                    SetCore(ContentPackageManager.CorePackages.First());
                }
                SetRegular(Regular.Where(p => ContentPackageManager.RegularPackages.Contains(p)).ToArray());
            }

            public static void RefreshUpdatedMods()
            {
                if (Core != null && !ContentPackageManager.CorePackages.Contains(Core))
                {
                    SetCore(ContentPackageManager.WorkshopPackages.Core.FirstOrDefault(p => p.UgcId == Core.UgcId) ??
                        ContentPackageManager.CorePackages.First());
                }

                List<RegularPackage> newRegular = new List<RegularPackage>();
                foreach (var p in Regular)
                {
                    if (ContentPackageManager.RegularPackages.Contains(p))
                    {
                        newRegular.Add(p);
                    }
                    else if (ContentPackageManager.WorkshopPackages.Regular.FirstOrDefault(p2
                                 => p2.UgcId == p.UgcId) is { } newP)
                    {
                        newRegular.Add(newP);
                    }
                }
                SetRegular(newRegular);
            }

            public static void BackUp()
            {
                if (BackupPackages.Core != null || BackupPackages.Regular != null)
                {
                    throw new InvalidOperationException("Tried to back up enabled packages multiple times");
                }

                BackupPackages.Core = Core;
                BackupPackages.Regular = Regular.ToImmutableArray();
            }

            public static void Restore()
            {
                if (BackupPackages.Core == null || BackupPackages.Regular == null)
                {
                    DebugConsole.AddWarning("Tried to restore enabled packages multiple times/without performing a backup");
                    return;
                }

                SetCore(BackupPackages.Core);
                SetRegular(BackupPackages.Regular);
                
                BackupPackages.Core = null;
                BackupPackages.Regular = null;
            }
        }

        public sealed partial class PackageSource : ICollection<ContentPackage>
        {
            private readonly Predicate<string>? skipPredicate;
            private readonly Action<string, Exception>? onLoadFail;
            
            public PackageSource(string dir, Predicate<string>? skipPredicate, Action<string, Exception>? onLoadFail)
            {
                this.skipPredicate = skipPredicate;
                this.onLoadFail = onLoadFail;
                directory = dir;
                Directory.CreateDirectory(directory);
            }

            public void SwapPackage(ContentPackage oldPackage, ContentPackage newPackage)
            {
                bool contains = false;
                if (oldPackage is CorePackage oldCore && corePackages.Contains(oldCore))
                {
                    corePackages.Remove(oldCore);
                    contains = true;
                }
                else if (oldPackage is RegularPackage oldRegular && regularPackages.Contains(oldRegular))
                {
                    regularPackages.Remove(oldRegular);
                    contains = true;
                }

                if (contains)
                {
                    if (newPackage is CorePackage newCore)
                    {
                        corePackages.Add(newCore);
                    }
                    else if (newPackage is RegularPackage newRegular)
                    {
                        regularPackages.Add(newRegular);
                    }
                }
            }
            
            public void Refresh()
            {
                //remove packages that have been deleted from the directory
                corePackages.RemoveWhere(p => !File.Exists(p.Path));
                regularPackages.RemoveWhere(p => !File.Exists(p.Path));

                //load packages that have been added to the directory
                var subDirs = Directory.GetDirectories(directory);
                foreach (string subDir in subDirs)
                {
                    var fileListPath = Path.Combine(subDir, ContentPackage.FileListFileName).CleanUpPathCrossPlatform();
                    if (this.Any(p => p.Path.Equals(fileListPath, StringComparison.OrdinalIgnoreCase))) { continue; }

                    if (!File.Exists(fileListPath)) { continue; }
                    if (skipPredicate?.Invoke(fileListPath) is true) { continue; }

                    var result = ContentPackage.TryLoad(fileListPath);
                    if (!result.TryUnwrapSuccess(out var newPackage))
                    {
                        onLoadFail?.Invoke(
                            fileListPath,
                            result.TryUnwrapFailure(out var exception) ? exception : throw new UnreachableCodeException());
                        continue;
                    }
                    
                    switch (newPackage)
                    {
                        case CorePackage corePackage:
                            corePackages.Add(corePackage);
                            break;
                        case RegularPackage regularPackage:
                            regularPackages.Add(regularPackage);
                            break;
                    }

                    Debug.WriteLine($"Loaded \"{newPackage.Name}\"");
                }
            }

            private readonly string directory;
            private readonly HashSet<RegularPackage> regularPackages = new HashSet<RegularPackage>();
            public IEnumerable<RegularPackage> Regular => regularPackages;
            
            private readonly HashSet<CorePackage> corePackages = new HashSet<CorePackage>();
            public IEnumerable<CorePackage> Core => corePackages;

            public IEnumerator<ContentPackage> GetEnumerator()
            {
                foreach (var core in Core) { yield return core; }
                foreach (var regular in Regular) { yield return regular; }
            }

            IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
            
            void ICollection<ContentPackage>.Add(ContentPackage item) => throw new InvalidOperationException();

            void ICollection<ContentPackage>.Clear() => throw new InvalidOperationException();

            public bool Contains(ContentPackage item)
                => item switch
                {
                    CorePackage core => corePackages.Contains(core),
                    RegularPackage regular => this.regularPackages.Contains(regular),
                    _ => throw new ArgumentException($"Expected regular or core package, got {item.GetType().Name}")
                };

            void ICollection<ContentPackage>.CopyTo(ContentPackage[] array, int arrayIndex)
            {
                foreach (var package in corePackages)
                {
                    array[arrayIndex] = package;
                    arrayIndex++;
                }

                foreach (var package in regularPackages)
                {
                    array[arrayIndex] = package;
                    arrayIndex++;
                }
            }

            bool ICollection<ContentPackage>.Remove(ContentPackage item) => throw new InvalidOperationException();

            public int Count => corePackages.Count + regularPackages.Count;
            public bool IsReadOnly => true;
        }

        public static readonly PackageSource LocalPackages
            = new PackageSource(
                ContentPackage.LocalModsDir,
                skipPredicate: null,
                onLoadFail: null);
        public static readonly PackageSource WorkshopPackages = new PackageSource(
            ContentPackage.WorkshopModsDir,
            skipPredicate: SteamManager.Workshop.IsInstallingToPath,
            onLoadFail: (fileListPath, exception) =>
            {
                // Delete Workshop mods that fail to load to
                // force a reinstall on next launch if necessary
                Directory.TryDelete(Path.GetDirectoryName(fileListPath)!);
            });

        public static CorePackage? VanillaCorePackage { get; private set; } = null;
        
        public static IEnumerable<CorePackage> CorePackages
            => (VanillaCorePackage is null
                ? Enumerable.Empty<CorePackage>()
                : VanillaCorePackage.ToEnumerable())
                    .CollectionConcat(LocalPackages.Core.CollectionConcat(WorkshopPackages.Core));

        public static IEnumerable<RegularPackage> RegularPackages
            => LocalPackages.Regular.CollectionConcat(WorkshopPackages.Regular);

        public static IEnumerable<ContentPackage> AllPackages
            => VanillaCorePackage.ToEnumerable().CollectionConcat(LocalPackages).CollectionConcat(WorkshopPackages)
                .OfType<ContentPackage>();

        public static void UpdateContentPackageList()
        {
            LocalPackages.Refresh();
            WorkshopPackages.Refresh();
            EnabledPackages.DisableRemovedMods();
        }

        public static Result<ContentPackage, Exception> ReloadContentPackage(ContentPackage p)
        {
            var result = ContentPackage.TryLoad(p.Path);

            if (result.TryUnwrapSuccess(out var newPackage))
            {
                switch (newPackage)
                {
                    case CorePackage core:
                    {
                        if (EnabledPackages.Core == p) { EnabledPackages.SetCore(core); }

                        break;
                    }
                    case RegularPackage regular:
                    {
                        int index = EnabledPackages.Regular.IndexOf(p);
                        if (index >= 0)
                        {
                            var newRegular = EnabledPackages.Regular.ToArray();
                            newRegular[index] = regular;
                            EnabledPackages.SetRegular(newRegular);
                        }

                        break;
                    }
                }

                LocalPackages.SwapPackage(p, newPackage);
                WorkshopPackages.SwapPackage(p, newPackage);
            }
            EnabledPackages.DisableRemovedMods();
            return result;
        }

        public readonly record struct LoadProgress(Result<float, LoadProgress.Error> Result)
        {
            public readonly record struct Error(
                Either<ImmutableArray<string>, Exception> ErrorsOrException)
            {
                public Error(IEnumerable<string> errorMessages) : this(ErrorsOrException: errorMessages.ToImmutableArray()) { }
                public Error(Exception exception) : this(ErrorsOrException: exception) { }
            }

            public static LoadProgress Failure(Exception exception)
                => new LoadProgress(
                    Result<float, Error>.Failure(new Error(exception)));

            public static LoadProgress Failure(IEnumerable<string> errorMessages)
                => new LoadProgress(
                    Result<float, Error>.Failure(new Error(errorMessages)));

            public static LoadProgress Progress(float value)
                => new LoadProgress(
                    Result<float, Error>.Success(value));

            public LoadProgress Transform(Range<float> range)
                => Result.TryUnwrapSuccess(out var value)
                    ? new LoadProgress(
                        Result<float, Error>.Success(
                            MathHelper.Lerp(range.Start, range.End, value)))
                    : this;
        }

        public static void LoadVanillaFileList()
        {
            VanillaCorePackage = new CorePackage(XDocument.Load(VanillaFileList), VanillaFileList);
            foreach (ContentPackage.LoadError error in VanillaCorePackage.FatalLoadErrors)
            {
                DebugConsole.ThrowError(error.ToString());
            }
        }

        public static IEnumerable<LoadProgress> Init()
        {
            Range<float> loadingRange = new Range<float>(0.0f, 1.0f);
            
            SteamManager.Workshop.DeleteFailedCopies();
            UpdateContentPackageList();

            if (VanillaCorePackage is null) { LoadVanillaFileList(); }

            SteamManager.Workshop.DeleteUnsubscribedMods();

            CorePackage enabledCorePackage = VanillaCorePackage!;
            List<RegularPackage> enabledRegularPackages = new List<RegularPackage>();

#if CLIENT
            TaskPool.AddWithResult("EnqueueWorkshopUpdates", EnqueueWorkshopUpdates(), t => { });
#else
            #warning TODO: implement Workshop updates for servers at some point
#endif

            var contentPackagesElement = XMLExtensions.TryLoadXml(GameSettings.PlayerConfigPath)?.Root
                ?.GetChildElement("ContentPackages");
            if (contentPackagesElement != null)
            {
                T? findPackage<T>(IEnumerable<T> packages, XElement? elem) where T : ContentPackage
                {
                    if (elem is null) { return null; }
                    string name = elem.GetAttributeString("name", "");
                    string path = elem.GetAttributeStringUnrestricted("path", "").CleanUpPathCrossPlatform(correctFilenameCase: false);
                    return
                        packages.FirstOrDefault(p => p.Path.Equals(path, StringComparison.OrdinalIgnoreCase))
                        ?? packages.FirstOrDefault(p => p.NameMatches(name));
                }
                
                var corePackageElement = contentPackagesElement.GetChildElement(CorePackageElementName);
                if (corePackageElement == null)
                {
                    DebugConsole.AddWarning($"No core package selected. Switching to the \"{enabledCorePackage.Name}\" package.");
                }
                else
                {
                    var configEnabledCorePackage = findPackage(CorePackages, corePackageElement);
                    if (configEnabledCorePackage == null)
                    {
                        string packageStr = corePackageElement.GetAttributeString("name", null) ?? corePackageElement.GetAttributeStringUnrestricted("path", "UNKNOWN");
                        DebugConsole.ThrowError($"Could not find the selected core package \"{packageStr}\". Switching to the \"{enabledCorePackage.Name}\" package.");
                    }
                    else
                    {
                        enabledCorePackage = configEnabledCorePackage;
                    }
                }
                
                var regularPackagesElement = contentPackagesElement.GetChildElement(RegularPackagesElementName);
                if (regularPackagesElement != null)
                {
                    XElement[] regularPackageElements = regularPackagesElement.GetChildElements(RegularPackagesSubElementName).ToArray();
                    for (int i = 0; i < regularPackageElements.Length; i++)
                    {
                        var regularPackage = findPackage(RegularPackages, regularPackageElements[i]);
                        if (regularPackage != null) { enabledRegularPackages.Add(regularPackage); }
                    }
                }
            }

            int pkgCount = 1 + enabledRegularPackages.Count; //core + regular

            loadingRange = new Range<float>(0.01f, 0.01f + (0.99f / pkgCount));
            foreach (var p in EnabledPackages.SetCoreEnumerable(enabledCorePackage))
            {
                yield return p.Transform(loadingRange);
            }

            loadingRange = new Range<float>(0.01f + (0.99f / pkgCount), 1.0f);
            foreach (var p in EnabledPackages.SetRegularEnumerable(enabledRegularPackages))
            {
                yield return p.Transform(loadingRange);
            }

            yield return LoadProgress.Progress(1.0f);
        }

        public static void LogEnabledRegularPackageErrors()
        {
            foreach (var p in EnabledPackages.Regular)
            {
                p.LogErrors();
            }
        }
    }
}
using Barotrauma.Extensions;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Reflection;
using System.Xml.Linq;
using System.IO.Compression;

namespace Barotrauma
{
    [AttributeUsage(AttributeTargets.Class, Inherited = false)]
    public class RequiredByCorePackage : Attribute
    {
        public readonly ImmutableHashSet<Type> AlternativeTypes;
        public RequiredByCorePackage(params Type[] alternativeTypes)
        {
            AlternativeTypes = alternativeTypes.ToImmutableHashSet();
        }
    }

    [AttributeUsage(AttributeTargets.Class, Inherited = false)]
    public class AlternativeContentTypeNames : Attribute
    {
        public readonly ImmutableHashSet<Identifier> Names;
        public AlternativeContentTypeNames(params string[] names)
        {
            Names = names.ToIdentifiers().ToImmutableHashSet();
        }
    }

    public class CorePackage : ContentPackage
    {
        public CorePackage(XDocument doc, string path) : base(doc, path)
        {
            AssertCondition(doc.Root.GetAttributeBool("corepackage", false), 
                "Expected a core package, got a regular package");

            var missingFileTypes = ContentFile.Types.Where(
                t => t.RequiredByCorePackage
                     && !Files.Any(f => t.Type == f.GetType()
                                       || t.AlternativeTypes.Contains(f.GetType())));
            AssertCondition(!missingFileTypes.Any(),
                    "Core package requires at least one of the following content types: " +
                            string.Join(", ", missingFileTypes.Select(t => t.Type.Name)));
        }

        public void TryToInstallCustomFiles()
        {
#if CLIENT
            string customFilesZip = Files.OfType<CustomFilesFile>().FirstOrDefault()?.Path?.FullPath;
            if (!customFilesZip.IsNullOrEmpty())
            {
                // Convert these LocalizedStrings to normal strings so if the corepackage
                // that's being switched over to changed one of these texts or one of them are missing it wont't sync.

                var warningBox = new GUIMessageBox(headerText: TextManager.Get("Warning").ToString(),
                    text: TextManager.Get("ModCustomClientFilesAtYourOwnRisk").ToString(),
                    new LocalizedString[] { TextManager.Get("Yes").ToString(), TextManager.Get("No").ToString() });
                warningBox.Buttons[0].OnClicked = (_, __) =>
                {
                    warningBox.Close();
                    try
                    {
                        ZipFile.ExtractToDirectory(customFilesZip, AppDomain.CurrentDomain.BaseDirectory, true);
                    }
                    catch (Exception e) 
                    {
                        DebugConsole.ThrowError($"An error occured while trying to install files: {e.Message}\n{e.StackTrace}");
                        return false;
                    }

                    var restartBox = new GUIMessageBox(TextManager.Get("restartrequiredlabel"), TextManager.Get("restartrequiredgeneric"));
                    restartBox.Buttons[0].OnClicked += (btn, userdata) =>
                    {
                        restartBox.Close();
                        return true;
                    };

                    return false;
                };
                warningBox.Buttons[1].OnClicked = (_, __) =>
                {
                    warningBox.Close();
                    return false;
                };
            }
#endif
        }
    }
}
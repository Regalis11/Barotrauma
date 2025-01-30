using System.Xml.Linq;

namespace Barotrauma
{
    [RequiredByCorePackage]
    sealed class UpgradeModulesFile : GenericPrefabFile<UpgradeContentPrefab>
    {
        public UpgradeModulesFile(ContentPackage contentPackage, ContentPath path) : base(contentPackage, path) { }

        protected override bool MatchesSingular(Identifier identifier) =>
            identifier == "upgrademodule" ||
            identifier == "submarineclass" ||
            identifier == "upgradecategory";

        protected override bool MatchesPlural(Identifier identifier) =>
            identifier == "upgrademodules";

        protected override PrefabCollection<UpgradeContentPrefab> Prefabs => UpgradeContentPrefab.AllPrefabs;
        protected override UpgradeContentPrefab CreatePrefab(ContentXElement element)
        {
            Identifier elemName = element.NameAsIdentifier();
            if (elemName == "upgradecategory")
            {
                return new UpgradeCategory(element, this);
            }
            else if (elemName == "submarineclass")
            {
                return new SubmarineClass(element, this);
            }
            else
            {
                return new UpgradePrefab(element, this);
            }
        }
    }
}

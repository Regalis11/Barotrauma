using System.Collections.Immutable;

namespace Barotrauma
{
    internal partial class SubmarineClass
    {
        public readonly Sprite LocationIndicator, AvailabilityIcon;
    }
    
    sealed partial class UpgradePrefab
    {
        public readonly ImmutableArray<DecorativeSprite> DecorativeSprites = new ImmutableArray<DecorativeSprite>();
        public Sprite Sprite { get; private set; }
    }
}
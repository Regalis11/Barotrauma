using Barotrauma.Sounds;
using Microsoft.Xna.Framework;
using System.Collections.Immutable;

namespace Barotrauma
{
    class CharacterSound
    {
        public enum SoundType
        {
            Idle, Attack, Die, Damage, Happy, Unhappy
        }

        private readonly RoundSound roundSound;
        public readonly CharacterParams.SoundParams Params;

        public SoundType Type => Params.State;
        public ImmutableHashSet<Identifier> TagSet => Params.TagSet;
        public float Volume => roundSound == null ? 0.0f : roundSound.Volume;

        public Vector2 FrequencyMultiplierRange => roundSound == null ? new Vector2(1.0f) : roundSound.FrequencyMultiplierRange;
        public float Range => roundSound == null ? 0.0f : roundSound.Range;
        public Sound Sound => roundSound?.Sound;

        public bool IgnoreMuffling => roundSound?.IgnoreMuffling ?? false;

        public CharacterSound(CharacterParams.SoundParams soundParams)
        {
            Params = soundParams;
            roundSound = RoundSound.Load(soundParams.Element);
        }

        public float GetRandomFrequencyMultiplier()
        {
            return Rand.Range(FrequencyMultiplierRange.X, FrequencyMultiplierRange.Y);
        }
    }
}

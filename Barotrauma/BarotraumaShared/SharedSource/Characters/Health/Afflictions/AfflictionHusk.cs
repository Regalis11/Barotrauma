using System.Collections.Generic;
using System.Linq;
using System.Xml.Linq;
using System;
using Barotrauma.Extensions;
using Microsoft.Xna.Framework;

namespace Barotrauma
{
    /// <summary>
    /// A special affliction type that gradually makes the character turn into another type of character. 
    /// See <see cref="AfflictionPrefabHusk"/> for more details.
    /// </summary>
    partial class AfflictionHusk : Affliction
    {
        public enum InfectionState
        {
            Initial, Dormant, Transition, Active, Final
        }

        private bool subscribedToDeathEvent;

        private InfectionState state;

        private List<Limb> huskAppendage;

        private Character character;

        private bool stun = false;

        [Serialize(0f, IsPropertySaveable.Yes), Editable]
        public override float Strength
        {
            get { return _strength; }
            set
            {
                // Don't allow to set the strength too high (from outside) to avoid rapid transformation into husk when taking lots of damage from husks.
                float previousValue = _strength;
                float threshold = _strength > ActiveThreshold ? ActiveThreshold + 1 : DormantThreshold - 1;
                float max = Math.Max(threshold, previousValue);
                _strength = Math.Clamp(value, 0, max);
                stun = GameMain.GameSession?.IsRunning ?? true;
                if (previousValue > 0.0f && value <= 0.0f)
                {
                    DeactivateHusk();
                    highestStrength = 0;
                }
                activeEffectDirty = true;
            }
        }
        private float highestStrength;

        public InfectionState State
        {
            get { return state; }
            private set
            {
                if (state == value) { return; }
                state = value;
            }
        }

        public readonly AfflictionPrefabHusk HuskPrefab;

        private float DormantThreshold => HuskPrefab.DormantThreshold;
        private float ActiveThreshold => HuskPrefab.ActiveThreshold;
        private float TransitionThreshold => HuskPrefab.TransitionThreshold;

        private float TransformThresholdOnDeath => HuskPrefab.TransformThresholdOnDeath;

        public AfflictionHusk(AfflictionPrefab prefab, float strength) : base(prefab, strength)
        {
            HuskPrefab = prefab as AfflictionPrefabHusk;
            if (HuskPrefab == null)
            {
                DebugConsole.ThrowError("Error in husk affliction definition: the prefab is of wrong type!",
                    contentPackage: prefab.ContentPackage);
            }
        }

        public override void Update(CharacterHealth characterHealth, Limb targetLimb, float deltaTime)
        {
            if (HuskPrefab == null) { return; }
            base.Update(characterHealth, targetLimb, deltaTime);
            highestStrength = Math.Max(_strength, highestStrength);
            character = characterHealth.Character;
            if (character == null) { return; }

            UpdateMessages();

            if (!subscribedToDeathEvent)
            {
                character.OnDeath += CharacterDead;
                subscribedToDeathEvent = true;
            }
            if (Strength < DormantThreshold)
            {
                DeactivateHusk();
                if (Strength > Math.Min(1.0f, DormantThreshold))
                {
                    State = InfectionState.Dormant;
                }
            }
            else if (Strength < ActiveThreshold)
            {
                DeactivateHusk();
                if (Prefab is AfflictionPrefabHusk { CauseSpeechImpediment: true })
                {
                    character.SpeechImpediment = 30;
                }
                State = InfectionState.Transition;
            }
            else if (Strength < TransitionThreshold)
            {
                if (State != InfectionState.Active && stun)
                {
                    character.SetStun(Rand.Range(2f, 3f));
                }
                if (Prefab is AfflictionPrefabHusk { CauseSpeechImpediment: true })
                {
                    character.SpeechImpediment = 100;
                }
                State = InfectionState.Active;
                ActivateHusk();
            }
            else
            {
                State = InfectionState.Final;
                ActivateHusk();
                ApplyDamage(deltaTime);
                character.SetStun(5);
            }
        }

        private InfectionState? prevDisplayedMessage;
        private void UpdateMessages()
        {
            if (Prefab is AfflictionPrefabHusk { SendMessages: false }) { return; }
            if (prevDisplayedMessage.HasValue && prevDisplayedMessage.Value == State) { return; }
            if (highestStrength > Strength) { return; }

            // Show initial husk warning by default, and disable it only if campaign difficulty settings explicitly disable it
            bool showHuskWarning = GameMain.GameSession?.Campaign?.Settings.ShowHuskWarning ?? true;

            switch (State)
            {
                case InfectionState.Dormant:
                    if (Strength < DormantThreshold * 0.5f)
                    {
                        return;
                    }
                    if (showHuskWarning)
                    {
                        if (character == Character.Controlled)
                        {
#if CLIENT
                            GUI.AddMessage(TextManager.Get("HuskDormant"), GUIStyle.Red);
#endif
                        }
                        else if (character.IsBot)
                        {
                            character.Speak(TextManager.Get("dialoghuskdormant").Value, delay: Rand.Range(0.5f, 5.0f), identifier: "huskdormant".ToIdentifier());
                        }
                    }
                    break;
                case InfectionState.Transition:
                    if (character == Character.Controlled)
                    {
#if CLIENT
                        GUI.AddMessage(TextManager.Get("HuskCantSpeak"), GUIStyle.Red);
#endif
                    }
                    else if (character.IsBot)
                    {
                        character.Speak(TextManager.Get("dialoghuskcantspeak").Value, delay: Rand.Range(0.5f, 5.0f), identifier: "huskcantspeak".ToIdentifier());
                    }
                    break;
                case InfectionState.Active:
#if CLIENT
                    if (character == Character.Controlled && character.Params.UseHuskAppendage)
                    {
                        GUI.AddMessage(TextManager.GetWithVariable("HuskActivate", "[Attack]", GameSettings.CurrentConfig.KeyMap.KeyBindText(InputType.Attack)), GUIStyle.Red);
                    }
#endif
                    break;
                case InfectionState.Final:
                default:
                    break;
            }
            prevDisplayedMessage = State;
        }

        private const float DamageCooldown = 0.1f;
        private float damageCooldownTimer;
        private void ApplyDamage(float deltaTime)
        {
            if (damageCooldownTimer > 0)
            {
                damageCooldownTimer -= deltaTime;
                return;
            }
            damageCooldownTimer = DamageCooldown;
            int limbCount = character.AnimController.Limbs.Count(IsValidLimb);
            foreach (Limb limb in character.AnimController.Limbs)
            {
                if (!IsValidLimb(limb)) { continue; }
                float random = Rand.Value();
                if (random == 0) { continue; }
                const float damageRate = 2;
                float dmg = random / limbCount * damageRate;
                character.LastDamageSource = null;
                var afflictions = AfflictionPrefab.InternalDamage.Instantiate(dmg).ToEnumerable();
                const float forceMultiplier = 5;
                float force = dmg * limb.Mass * forceMultiplier;
                character.DamageLimb(limb.WorldPosition, limb, afflictions, stun: 0, playSound: false, Rand.Vector(force), ignoreDamageOverlay: true, recalculateVitality: false);
            }
            character.CharacterHealth.RecalculateVitality();

            static bool IsValidLimb(Limb limb) => !limb.IgnoreCollisions && !limb.IsSevered && !limb.Hidden;
        }

        public void ActivateHusk()
        {
            if (huskAppendage == null && character.Params.UseHuskAppendage)
            {
                var huskAffliction = Prefab as AfflictionPrefabHusk;
                huskAppendage = AttachHuskAppendage(character, huskAffliction, GetHuskedSpeciesName(character.Params, huskAffliction));
            }

            if (Prefab is AfflictionPrefabHusk { NeedsAir: false })
            {
                character.NeedsAir = false;
            }

            if (Prefab is AfflictionPrefabHusk { CauseSpeechImpediment: true })
            {
                character.SpeechImpediment = 100;
            }
        }

        private void DeactivateHusk()
        {
            if (character?.AnimController == null || character.Removed) { return; }
            if (Prefab is AfflictionPrefabHusk { NeedsAir: false } && 
                !character.CharacterHealth.GetAllAfflictions().Any(a => a != this && a.Prefab is AfflictionPrefabHusk { NeedsAir: false }))
            {
                character.NeedsAir = character.Params.MainElement.GetAttributeBool("needsair", false);
            }

            if (huskAppendage != null)
            {
                huskAppendage.ForEach(l => character.AnimController.RemoveLimb(l));
                huskAppendage = null;
            }
        }

        public void UnsubscribeFromDeathEvent()
        {
            if (character == null || !subscribedToDeathEvent) { return; }
            character.OnDeath -= CharacterDead;
            subscribedToDeathEvent = false;
        }

        private void CharacterDead(Character character, CauseOfDeath causeOfDeath)
        {
            if (GameMain.NetworkMember != null && GameMain.NetworkMember.IsClient) { return; }
            if (Strength < TransformThresholdOnDeath || character.Removed || 
                character.CharacterHealth.GetAllAfflictions().Any(a => a.GetActiveEffect()?.BlockTransformation.Contains(Prefab.Identifier) ?? false)) 
            {
                UnsubscribeFromDeathEvent();
                return; 
            }

            //don't turn the character into a husk if any of its limbs are severed
            if (character.AnimController?.LimbJoints != null)
            {
                foreach (var limbJoint in character.AnimController.LimbJoints)
                {
                    if (limbJoint.IsSevered) { return; }
                }
            }

            //create the AI husk in a coroutine to ensure that we don't modify the character list while enumerating it
            CoroutineManager.StartCoroutine(CreateAIHusk());
        }

        private IEnumerable<CoroutineStatus> CreateAIHusk()
        {
            //character already in remove queue (being removed by something else, for example a modded affliction that uses AfflictionHusk as the base)
            // -> don't spawn the AI husk
            if (Entity.Spawner.IsInRemoveQueue(character))
            {
                yield return CoroutineStatus.Success;
            }
#if SERVER
            var client = GameMain.Server?.ConnectedClients.FirstOrDefault(c => c.Character == character);
#endif
            character.Enabled = false;
            UnsubscribeFromDeathEvent();

            Identifier huskedSpeciesName = GetHuskedSpeciesName(character.Params, Prefab as AfflictionPrefabHusk);
            CharacterPrefab prefab = CharacterPrefab.FindBySpeciesName(huskedSpeciesName);

            if (prefab == null)
            {
                DebugConsole.ThrowError("Failed to turn character \"" + character.Name + "\" into a husk - husk config file not found.",
                    contentPackage: Prefab.ContentPackage);
                yield return CoroutineStatus.Success;
            }

            XElement parentElement = new XElement("CharacterInfo");
            XElement infoElement = character.Info?.Save(parentElement);
            CharacterInfo huskCharacterInfo = infoElement == null ? null : new CharacterInfo(new ContentXElement(Prefab.ContentPackage, infoElement));

            if (huskCharacterInfo != null)
            {
                var bodyTint = GetBodyTint();
                huskCharacterInfo.Head.SkinColor =
                        Color.Lerp(huskCharacterInfo.Head.SkinColor, bodyTint.Opaque(), bodyTint.A / 255.0f);
            }

            var husk = Character.Create(huskedSpeciesName, character.WorldPosition, ToolBox.RandomSeed(8), huskCharacterInfo, isRemotePlayer: false, hasAi: true);
            if (husk.Info != null)
            {
                husk.Info.Character = husk;
                husk.Info.TeamID = CharacterTeamType.None;
            }
            husk.AllowPlayDead = character.AllowPlayDead;

            if (Prefab is AfflictionPrefabHusk huskPrefab)
            {
                if (huskPrefab.ControlHusk)
                {
#if SERVER
                    if (client != null)
                    {
                        GameMain.Server.SetClientCharacter(client, husk);
                    }
#else
                    if (!character.IsRemotelyControlled && character == Character.Controlled)
                    {
                        Character.Controlled = husk; 
                    }
#endif
                }
            }

            foreach (Limb limb in husk.AnimController.Limbs)
            {
                if (limb.type == LimbType.None)
                {
                    limb.body.SetTransform(character.SimPosition, 0.0f);
                    continue;
                }

                var matchingLimb = character.AnimController.GetLimb(limb.type);
                if (matchingLimb?.body != null)
                {
                    limb.body.SetTransform(matchingLimb.SimPosition, matchingLimb.Rotation);
                    limb.body.LinearVelocity = matchingLimb.LinearVelocity;
                    limb.body.AngularVelocity = matchingLimb.body.AngularVelocity;
                }
            }

            if ((Prefab as AfflictionPrefabHusk)?.TransferBuffs ?? false)
            {
                foreach (Affliction affliction in character.CharacterHealth.GetAllAfflictions())
                {
                    if (affliction.Prefab.IsBuff)
                    {
                        husk.CharacterHealth.ApplyAffliction(
                            character.CharacterHealth.GetAfflictionLimb(affliction), 
                            affliction.Prefab.Instantiate(affliction.Strength));
                    }
                }
            }

            if (character.Inventory != null && husk.Inventory != null)
            {
                for (int i = 0; i < character.Inventory.Capacity && i < husk.Inventory.Capacity; i++)
                {
                    character.Inventory.GetItemsAt(i).ForEachMod(item => husk.Inventory.TryPutItem(item, i, true, false, null));
                }
            }

            husk.SetStun(5);
            Entity.Spawner.AddEntityToRemoveQueue(character);
            yield return new WaitForSeconds(5, false);
#if CLIENT
            husk?.PlaySound(CharacterSound.SoundType.Idle);
#endif
            yield return CoroutineStatus.Success;
        }

        public static List<Limb> AttachHuskAppendage(Character character, AfflictionPrefabHusk matchingAffliction, Identifier huskedSpeciesName, ContentXElement appendageDefinition = null, Ragdoll ragdoll = null)
        {
            var appendage = new List<Limb>();
            CharacterPrefab huskPrefab = CharacterPrefab.FindBySpeciesName(huskedSpeciesName);
            if (huskPrefab?.ConfigElement == null)
            {
                DebugConsole.ThrowError($"Failed to find the config file for the husk infected species with the species name '{huskedSpeciesName}'!",
                    contentPackage: matchingAffliction.ContentPackage);
                return appendage;
            }
            var mainElement = huskPrefab.ConfigElement;
            var element = appendageDefinition;
            if (element == null)
            {
                element = mainElement.GetChildElements("huskappendage").FirstOrDefault(e => e.GetAttributeIdentifier("affliction", Identifier.Empty) == matchingAffliction.Identifier);
            }
            if (element == null)
            {
                DebugConsole.ThrowError($"Error in '{huskPrefab.FilePath}': Failed to find a huskappendage that matches the affliction with an identifier '{matchingAffliction.Identifier}'!",
                    contentPackage: matchingAffliction.ContentPackage);
                return appendage;
            }
            ContentPath pathToAppendage = element.GetAttributeContentPath("path") ?? ContentPath.Empty;
            XDocument doc = XMLExtensions.TryLoadXml(pathToAppendage);
            if (doc == null) { return appendage; }
            ragdoll ??= character.AnimController;
            if (ragdoll.Dir < 1.0f)
            {
                ragdoll.Flip();
            }

            var root = doc.Root.FromPackage(pathToAppendage.ContentPackage);
            var limbElements = root.GetChildElements("limb").ToDictionary(e => e.GetAttributeString("id", null), e => e);
            //the IDs may need to be offset if the character has other extra appendages (e.g. from gene splicing)
            //that take up the IDs of this appendage
            int idOffset = 0;
            foreach (var jointElement in root.GetChildElements("joint"))
            {
                if (!limbElements.TryGetValue(jointElement.GetAttributeString("limb2", null), out ContentXElement limbElement)) { continue; }
                
                var jointParams = new RagdollParams.JointParams(jointElement, ragdoll.RagdollParams);
                Limb attachLimb = null;
                if (matchingAffliction.AttachLimbId > -1)
                {
                    attachLimb = ragdoll.Limbs.FirstOrDefault(l => !l.IsSevered && l.Params.ID == matchingAffliction.AttachLimbId);
                }
                else if (matchingAffliction.AttachLimbName != null)
                {
                    attachLimb = ragdoll.Limbs.FirstOrDefault(l => !l.IsSevered && l.Name == matchingAffliction.AttachLimbName);
                }
                else if (matchingAffliction.AttachLimbType != LimbType.None)
                {
                    attachLimb = ragdoll.Limbs.FirstOrDefault(l => !l.IsSevered && l.type == matchingAffliction.AttachLimbType);
                }
                if (attachLimb == null)
                {
                    attachLimb = ragdoll.Limbs.FirstOrDefault(l => !l.IsSevered && l.Params.ID == jointParams.Limb1);
                }
                if (attachLimb != null)
                {
                    jointParams.Limb1 = attachLimb.Params.ID;
                    //the joint attaches to a limb outside the character's normal limb count = to another part of the appendage
                    // -> if the appendage's IDs have been offset, we need to take that into account to attach to the correct limb
                    if (jointParams.Limb1 >= ragdoll.RagdollParams.Limbs.Count)
                    {
                        jointParams.Limb1 += idOffset;
                    }
                    var appendageLimbParams = new RagdollParams.LimbParams(limbElement, ragdoll.RagdollParams);
                    if (idOffset == 0)
                    {
                        idOffset = ragdoll.Limbs.Length - appendageLimbParams.ID;
                    }
                    jointParams.Limb2 = appendageLimbParams.ID = ragdoll.Limbs.Length;
                    Limb huskAppendage = new Limb(ragdoll, character, appendageLimbParams);
                    huskAppendage.body.Submarine = character.Submarine;
                    huskAppendage.body.SetTransform(attachLimb.SimPosition, attachLimb.Rotation);
                    ragdoll.AddLimb(huskAppendage);
                    ragdoll.AddJoint(jointParams);
                    appendage.Add(huskAppendage);
                }
            }
            return appendage;
        }

        public static Identifier GetHuskedSpeciesName(CharacterParams character, AfflictionPrefabHusk prefab)
        {
            Identifier huskedSpecies = character.HuskedSpecies;
            if (huskedSpecies.IsEmpty)
            {
                // Default pattern: Crawler -> Crawlerhusk, Human -> Humanhusk
                return new Identifier(character.SpeciesName.Value + prefab.HuskedSpeciesName.Value);
            }
            return huskedSpecies;
        }

        public static Identifier GetNonHuskedSpeciesName(CharacterParams character, AfflictionPrefabHusk prefab)
        {
            Identifier nonHuskedSpecies = character.NonHuskedSpecies;
            if (nonHuskedSpecies.IsEmpty)
            {
                // Default pattern: Crawlerhusk -> Crawler, Humanhusk -> Human
                return character.SpeciesName.Remove(prefab.HuskedSpeciesName);
            }
            return nonHuskedSpecies;
        }
    }
}

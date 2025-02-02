using Barotrauma.Networking;
using System.Linq;
using System.Xml.Linq;
using Barotrauma.Items.Components;

namespace Barotrauma
{
    partial class Character
    {
        public static Character Controlled => null;

        partial void OnAttackedProjSpecific(Character attacker, AttackResult attackResult, float stun)
        {
            GameMain.Server.KarmaManager.OnCharacterHealthChanged(this, attacker, attackResult.Damage, stun, attackResult.Afflictions);
        }

        partial void KillProjSpecific(CauseOfDeathType causeOfDeath, Affliction causeOfDeathAffliction, bool log, bool triggerDeathEffects)
        {
            if (log)
            {
                if (causeOfDeath == CauseOfDeathType.Affliction)
                {
                    GameServer.Log(GameServer.CharacterLogName(this) + " has died (Cause of death: " + causeOfDeathAffliction.Prefab.Name.Value + ")", ServerLog.MessageType.Attack);
                }
                else
                {
                    GameServer.Log(GameServer.CharacterLogName(this) + " has died (Cause of death: " + causeOfDeath + ")", ServerLog.MessageType.Attack);
                }
            }

            if (GameMain.Server is { ServerSettings.RespawnMode: RespawnMode.Permadeath } && 
                GameMain.GameSession?.Campaign is MultiPlayerCampaign mpCampaign &&
                causeOfDeath != CauseOfDeathType.Disconnected)
            {
                Client ownerClient = GameMain.Server.ConnectedClients.FirstOrDefault(c => c.Character == this);
                if (ownerClient != null)
                {
                    ownerClient.SpectateOnly = true;
                    CharacterCampaignData matchingData = mpCampaign.GetClientCharacterData(ownerClient);
                    if (matchingData != null)
                    {
                        matchingData.ApplyPermadeath();
                        
                        if (GameMain.Server?.ServerSettings is { IronmanModeActive: true })
                        {
                            mpCampaign.SaveSingleCharacter(matchingData);
                        }
                    }
                }
            }

            if (HasAbilityFlag(AbilityFlags.RetainExperienceForNewCharacter))
            {
                var ownerClient = GameMain.Server.ConnectedClients.Find(c => c.Character == this);
                if (ownerClient != null)
                {
                    (GameMain.GameSession?.GameMode as MultiPlayerCampaign)?.SaveExperiencePoints(ownerClient);
                }
            }

            healthUpdateTimer = 0.0f;

            if (CauseOfDeath.Killer != null && CauseOfDeath.Killer.IsTraitor && CauseOfDeath.Killer != this)
            {
                var owner = GameMain.Server.ConnectedClients.Find(c => c.Character == this);
                if (owner != null)
                {
                    GameMain.Server.SendDirectChatMessage(TextManager.FormatServerMessage("KilledByTraitorNotification"), owner, ChatMessageType.ServerMessageBoxInGame);
                }
            }
            foreach (Client client in GameMain.Server.ConnectedClients)
            {
                if (client.InGame)
                {
                    client.PendingPositionUpdates.Enqueue(this);
                }
            }
        }

        partial void OnMoneyChanged(int prevAmount, int newAmount)
        {
            GameMain.NetworkMember.CreateEntityEvent(this, new UpdateMoneyEventData());
        }

        partial void OnTalentGiven(TalentPrefab talentPrefab)
        {
            GameServer.Log($"{GameServer.CharacterLogName(this)} has gained the talent '{talentPrefab.DisplayName}'", ServerLog.MessageType.Talent);
        }
        
        private void SyncInGameEditables(Item item)
        {
            foreach (ItemComponent itemComponent in item.Components)
            {
                foreach (var serializableProperty in SerializableProperty.GetProperties<InGameEditable>(itemComponent))
                {
                    GameMain.Server.CreateEntityEvent(item, new Item.ChangePropertyEventData(serializableProperty, itemComponent));
                }
            }
        }
    }
}

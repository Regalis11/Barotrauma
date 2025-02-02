using Barotrauma.Networking;
using Microsoft.Xna.Framework;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Barotrauma
{
    partial class Character
    {
        private Address ownerClientAddress;
        private Option<AccountId> ownerClientAccountId;

        public bool ClientDisconnected;
        public float KillDisconnectedTimer;

        private bool networkUpdateSent;

        private double LastInputTime;

        public bool HealthUpdatePending;

        public void SetOwnerClient(Client client)
        {
            if (client == null)
            {
                ownerClientAddress = null;
                ownerClientAccountId = Option<AccountId>.None();
                IsRemotePlayer = false;
            }
            else
            {
                ownerClientAddress = client.Connection.Endpoint.Address;
                ownerClientAccountId = client.AccountId;
                IsRemotePlayer = true;
            }
        }

        public bool IsClientOwner(Client client)
        {
            if (ownerClientAccountId.TryUnwrap(out var accountId)
                && client.AccountId.TryUnwrap(out var clientId))
            {
                return accountId == clientId;
            }
            else
            {
                return ownerClientAddress == client.Connection.Endpoint.Address;
            }            
        }

        public float GetPositionUpdateInterval(Client recipient)
        {
            if (!Enabled) { return 1000.0f; }

            Vector2 comparePosition = recipient.SpectatePos == null ? recipient.Character.WorldPosition : recipient.SpectatePos.Value;

            float distance = Vector2.Distance(comparePosition, WorldPosition);
            if (recipient.Character?.ViewTarget != null)
            {
                distance = Math.Min(distance, Vector2.Distance(recipient.Character.ViewTarget.WorldPosition, WorldPosition));
            }
            if (ViewTarget != null && ViewTarget != this)
            {
                distance = Math.Min(distance, Vector2.Distance(comparePosition, ViewTarget.WorldPosition));
            }

            float priority = 1.0f - MathUtils.InverseLerp(
                NetConfig.HighPrioCharacterPositionUpdateDistance, 
                NetConfig.LowPrioCharacterPositionUpdateDistance,
                distance);

            float interval = MathHelper.Lerp(
                NetConfig.LowPrioCharacterPositionUpdateInterval, 
                NetConfig.HighPrioCharacterPositionUpdateInterval,
                priority);

            if (IsDead && !AnimController.IsDraggedWithRope)
            {
                interval = Math.Max(interval * 2, 0.1f);
            }

            return interval;
        }

        partial void UpdateNetInput()
        {
            if (!(this is AICharacter) || IsRemotePlayer)
            {
                if (!CanMove)
                {
                    AnimController.Frozen = false;
                    if (memInput.Count > 0)
                    {
                        prevDequeuedInput = dequeuedInput;
                        dequeuedInput = memInput[memInput.Count - 1].states & InputNetFlags.Ragdoll;
                        memInput.RemoveAt(memInput.Count - 1);
                    }
                }
                else if (memInput.Count == 0)
                {
                    AnimController.Frozen = true;
                    if (Timing.TotalTime > LastInputTime + 0.5)
                    {
                        //no inputs have been received in 0.5 seconds, reset input
                        //(if there's a temporary network hiccup that prevents us from receiving inputs, we assume the inputs haven't changed,
                        //but if it takes too long, for example due to a client crashing/disconnecting, we don't want to keep the character
                        //firing a welding tool or whatever else they were doing until the kill disconnect timer kicks in)
                        prevDequeuedInput = dequeuedInput =
                            dequeuedInput.HasFlag(InputNetFlags.FacingLeft) ? InputNetFlags.FacingLeft : InputNetFlags.None;
                    }
                }
                else
                {
                    AnimController.Frozen = false;
                    prevDequeuedInput = dequeuedInput;

                    LastProcessedID = memInput[memInput.Count - 1].networkUpdateID;
                    dequeuedInput = memInput[memInput.Count - 1].states;

                    double aimAngle = ((double)memInput[memInput.Count - 1].intAim / 65535.0) * 2.0 * Math.PI;
                    cursorPosition = AimRefPosition + new Vector2((float)Math.Cos(aimAngle), (float)Math.Sin(aimAngle)) * 500.0f;

                    //reset focus when attempting to use/select something
                    if (memInput[memInput.Count - 1].states.HasFlag(InputNetFlags.Use) ||
                        memInput[memInput.Count - 1].states.HasFlag(InputNetFlags.Select) ||
                        memInput[memInput.Count - 1].states.HasFlag(InputNetFlags.Deselect) ||
                        memInput[memInput.Count - 1].states.HasFlag(InputNetFlags.Health) ||
                        memInput[memInput.Count - 1].states.HasFlag(InputNetFlags.Grab))
                    {
                        focusedItem = null;
                        FocusedCharacter = null;
                    }
                    var closestEntity = FindEntityByID(memInput[memInput.Count - 1].interact);
                    if (closestEntity is Item item)
                    {
                        if (CanInteractWith(item))
                        {
                            focusedItem = item;
                            FocusedCharacter = null;
                        }
                        else
                        {
                            //failed to interact with the item 
                            // -> correct the position and the state of the Holdable component (in case the item was deattached client-side)
                            item.PositionUpdateInterval = 0.0f;
                            var holdable = item.GetComponent<Items.Components.Holdable>();
                            holdable?.Item?.CreateServerEvent(holdable);
                        }
                    }
                    else if (closestEntity is Character character)
                    {
                        if (CanInteractWith(character, maxDist: 250.0f))
                        {
                            FocusedCharacter = character;
                            focusedItem = null;
                        }
                    }

                    memInput.RemoveAt(memInput.Count - 1);

                    if ((dequeuedInput == InputNetFlags.None || dequeuedInput == InputNetFlags.FacingLeft) && Math.Abs(AnimController.Collider.LinearVelocity.X) < 0.005f && Math.Abs(AnimController.Collider.LinearVelocity.Y) < 0.2f)
                    {
                        while (memInput.Count > 5 && memInput[memInput.Count - 1].states == dequeuedInput)
                        {
                            //remove inputs where the player is not moving at all
                            //helps the server catch up, shouldn't affect final position
                            LastProcessedID = memInput[memInput.Count - 1].networkUpdateID;
                            memInput.RemoveAt(memInput.Count - 1);
                        }
                    }
                }
            }
            AnimController.Frozen = false;

            if (networkUpdateSent)
            {
                foreach (Key key in keys)
                {
                    key.DequeueHit();
                    key.DequeueHeld();
                }

                networkUpdateSent = false;
            }
        }

        public void ServerReadInput(IReadMessage msg, Client c)
        {
            if (c.Character != this)
            {
#if DEBUG
                DebugConsole.Log("Received a character update message from a client who's not controlling the character");
#endif
                return;
            }

            UInt16 networkUpdateID = msg.ReadUInt16();
            byte inputCount = msg.ReadByte();

            if (AllowInput) { Enabled = true; }

            for (int i = 0; i < inputCount; i++)
            {
                InputNetFlags newInput = (InputNetFlags)msg.ReadRangedInteger(0, (int)InputNetFlags.MaxVal);
                UInt16 newAim = 0;
                UInt16 newInteract = 0;

                if (newInput != InputNetFlags.None && newInput != InputNetFlags.FacingLeft)
                {
                    c.KickAFKTimer = 0.0f;
                }
                else if (AnimController.Dir < 0.0f != newInput.HasFlag(InputNetFlags.FacingLeft))
                {
                    //character changed the direction they're facing
                    c.KickAFKTimer = 0.0f;
                }

                newAim = msg.ReadUInt16();
                if (newInput.HasFlag(InputNetFlags.Select) ||
                    newInput.HasFlag(InputNetFlags.Deselect) ||
                    newInput.HasFlag(InputNetFlags.Use) ||
                    newInput.HasFlag(InputNetFlags.Health) ||
                    newInput.HasFlag(InputNetFlags.Grab))
                {
                    newInteract = msg.ReadUInt16();
                }

                if (NetIdUtils.IdMoreRecent((ushort)(networkUpdateID - i), LastNetworkUpdateID) && (i < 60))
                {
                    if ((i > 0 && memInput[i - 1].intAim != newAim))
                    {
                        c.KickAFKTimer = 0.0f;
                    }
                    NetInputMem newMem = new NetInputMem
                    {
                        states = newInput,
                        intAim = newAim,
                        interact = newInteract,
                        networkUpdateID = (ushort)(networkUpdateID - i)
                    };
                    memInput.Insert(i, newMem);
                    LastInputTime = Timing.TotalTime;
                }
            }

            if (NetIdUtils.IdMoreRecent(networkUpdateID, LastNetworkUpdateID))
            {
                LastNetworkUpdateID = networkUpdateID;
            }
            else if (NetIdUtils.Difference(networkUpdateID, LastNetworkUpdateID) > 500)
            {
#if DEBUG || UNSTABLE
                DebugConsole.AddWarning($"Large discrepancy between a client character's network update ID server-side and client-side (client: {networkUpdateID}, server: {LastNetworkUpdateID}). Resetting the ID.");
#endif
                LastNetworkUpdateID = LastProcessedID = networkUpdateID;
            }
            if (memInput.Count > 60)
            {
                //deleting inputs from the queue here means the server is way behind and data needs to be dropped
                //we'll make the server drop down to 30 inputs for good measure
                memInput.RemoveRange(30, memInput.Count - 30);
            }
        }

        public virtual void ServerEventRead(IReadMessage msg, Client c)
        {
            EventType eventType = (EventType)msg.ReadRangedInteger((int)EventType.MinValue, (int)EventType.MaxValue);
            switch (eventType)
            {
                case EventType.InventoryState:
                    Inventory.ServerEventRead(msg, c);
                    break;
                case EventType.Treatment:
                    bool doingCPR = msg.ReadBoolean();
                    if (c.Character != this)
                    {
#if DEBUG
                        DebugConsole.Log("Received a character update message from a client who's not controlling the character");
#endif
                        return;
                    }

                    AnimController.Anim = doingCPR ? AnimController.Animation.CPR : AnimController.Animation.None;
                    break;
                case EventType.Status:
                    if (c.Character != this)
                    {
#if DEBUG
                        DebugConsole.Log("Received a character update message from a client who's not controlling the character");
#endif
                        return;
                    }

                    if (IsIncapacitated)
                    {
                        var causeOfDeath = CharacterHealth.GetCauseOfDeath();
                        Kill(causeOfDeath.type, causeOfDeath.affliction);
                    }
                    break;
                case EventType.ConfirmTalentRefund:
                    if (!CanManageTalents(c)) { return; }
                    Info?.RefundTalents();
                    break;
                case EventType.UpdateTalents:
                    if (!CanManageTalents(c)) { return; }

                    // get the full list of talents from the player, only give the ones
                    // that are not already given (or otherwise not viable)
                    ushort talentCount = msg.ReadUInt16();
                    List<Identifier> talentSelection = new List<Identifier>();
                    for (int i = 0; i < talentCount; i++)
                    {
                        UInt32 talentIdentifier = msg.ReadUInt32();
                        var prefab = TalentPrefab.TalentPrefabs.Find(p => p.UintIdentifier == talentIdentifier);
                        if (prefab == null) { continue; }

                        if (TalentTree.IsViableTalentForCharacter(this, prefab.Identifier, talentSelection))
                        {
                            GiveTalent(prefab.Identifier);
                            talentSelection.Add(prefab.Identifier);
                        }
                    }
                    if (talentSelection.Count != talentCount)
                    {
                        DebugConsole.AddWarning($"Failed to unlock talents: the amount of unlocked talents doesn't match (client: {talentCount}, server: {talentSelection.Count})");
                    }
                    break;
            }

            bool CanManageTalents(Client client)
            {
                if (client.Character != this)
                {
                    if (client.TeamID != TeamID || !IsBot || !client.HasPermission(ClientPermissions.ManageBotTalents) || client.Spectating)
                    {
#if DEBUG
                        DebugConsole.Log("A client tried to manage talents of a character they don't control or have permission to manage");
#endif
                        return false;
                    }
                }

                return true;
            }
        }

        public void ServerWritePosition(ReadWriteMessage tempBuffer, Client c)
        {
            if (this == c.Character)
            {
                tempBuffer.WriteBoolean(true);
                if (LastNetworkUpdateID < memInput.Count + 1)
                {
                    tempBuffer.WriteUInt16((UInt16)0);
                }
                else
                {
                    tempBuffer.WriteUInt16((UInt16)(LastNetworkUpdateID - memInput.Count - 1));
                }
            }
            else
            {
                tempBuffer.WriteBoolean(false);

                bool aiming = false;
                bool use = false;
                bool attack = false;
                bool shoot = false;

                if (IsRemotePlayer)
                {
                    aiming  = dequeuedInput.HasFlag(InputNetFlags.Aim);
                    use     = dequeuedInput.HasFlag(InputNetFlags.Use);
                    attack  = dequeuedInput.HasFlag(InputNetFlags.Attack);
                    shoot   = dequeuedInput.HasFlag(InputNetFlags.Shoot);
                }
                else if (keys != null)
                {
                    aiming  = keys[(int)InputType.Aim].GetHeldQueue;
                    use     = keys[(int)InputType.Use].GetHeldQueue;
                    attack  = keys[(int)InputType.Attack].GetHeldQueue;
                    shoot   = keys[(int)InputType.Shoot].GetHeldQueue;
                    networkUpdateSent = true;
                }

                tempBuffer.WriteBoolean(aiming);
                tempBuffer.WriteBoolean(shoot);
                tempBuffer.WriteBoolean(use);

                if (AnimController is HumanoidAnimController humanAnim)
                {
                    tempBuffer.WriteBoolean(humanAnim.Crouching);
                }
                else if (AnimController is FishAnimController fishAnim)
                {
                    tempBuffer.WriteBoolean(fishAnim.Reverse);
                }

                tempBuffer.WriteBoolean(attack);

                Vector2 relativeCursorPos = cursorPosition - AimRefPosition;
                tempBuffer.WriteUInt16((UInt16)(65535.0 * Math.Atan2(relativeCursorPos.Y, relativeCursorPos.X) / (2.0 * Math.PI)));

                tempBuffer.WriteBoolean(IsRagdolled || Stun > 0.0f || IsDead || IsIncapacitated);

                tempBuffer.WriteBoolean(AnimController.Dir > 0.0f);
            }

            if (SelectedCharacter != null || HasSelectedAnyItem)
            {
                tempBuffer.WriteBoolean(true);
                tempBuffer.WriteUInt16(SelectedCharacter != null ? SelectedCharacter.ID : NullEntityID);
                tempBuffer.WriteUInt16(SelectedItem != null ? SelectedItem.ID : NullEntityID);
                tempBuffer.WriteUInt16(SelectedSecondaryItem != null ? SelectedSecondaryItem.ID : NullEntityID);
                if (SelectedCharacter != null)
                {
                    tempBuffer.WriteBoolean(AnimController.Anim == AnimController.Animation.CPR);
                }
            }
            else
            {
                tempBuffer.WriteBoolean(false);
            }

            tempBuffer.WriteSingle(SimPosition.X);
            tempBuffer.WriteSingle(SimPosition.Y);
            float MaxVel = NetConfig.MaxPhysicsBodyVelocity;
            AnimController.Collider.LinearVelocity = new Vector2(
                NetConfig.Quantize(AnimController.Collider.LinearVelocity.X, -MaxVel, MaxVel, 12),
                NetConfig.Quantize(AnimController.Collider.LinearVelocity.Y, -MaxVel, MaxVel, 12));
            tempBuffer.WriteRangedSingle(AnimController.Collider.LinearVelocity.X, -MaxVel, MaxVel, 12);
            tempBuffer.WriteRangedSingle(AnimController.Collider.LinearVelocity.Y, -MaxVel, MaxVel, 12);

            AnimController.TargetMovement = new Vector2(
                NetConfig.Quantize(AnimController.TargetMovement.X, -Ragdoll.MAX_SPEED, Ragdoll.MAX_SPEED, 12),
                NetConfig.Quantize(AnimController.TargetMovement.Y, -Ragdoll.MAX_SPEED, Ragdoll.MAX_SPEED, 12));
            tempBuffer.WriteRangedSingle(AnimController.TargetMovement.X, -Ragdoll.MAX_SPEED, Ragdoll.MAX_SPEED, 12);
            tempBuffer.WriteRangedSingle(AnimController.TargetMovement.Y, -Ragdoll.MAX_SPEED, Ragdoll.MAX_SPEED, 12);

            bool fixedRotation = AnimController.Collider.FarseerBody.FixedRotation;
            tempBuffer.WriteBoolean(fixedRotation);
            if (!fixedRotation)
            {
                tempBuffer.WriteSingle(AnimController.Collider.Rotation);
                float MaxAngularVel = NetConfig.MaxPhysicsBodyAngularVelocity;
                AnimController.Collider.AngularVelocity =
                    AnimController.Collider.PhysEnabled ?
                    0.0f :
                    NetConfig.Quantize(AnimController.Collider.AngularVelocity, -MaxAngularVel, MaxAngularVel, 8);
                tempBuffer.WriteRangedSingle(MathHelper.Clamp(AnimController.Collider.AngularVelocity, -MaxAngularVel, MaxAngularVel), -MaxAngularVel, MaxAngularVel, 8);
            }


            tempBuffer.WriteBoolean(AnimController.IgnorePlatforms);

            bool writeStatus = healthUpdateTimer <= 0.0f;
            tempBuffer.WriteBoolean(writeStatus);
            if (writeStatus)
            {
                WriteStatus(tempBuffer);
                tempBuffer.WriteBoolean(AIController is EnemyAIController);
                if (AIController is EnemyAIController enemyAi)
                {
                    tempBuffer.WriteByte((byte)enemyAi.State);
                    tempBuffer.WriteBoolean(enemyAi.PetBehavior is PetBehavior);
                    if (enemyAi.PetBehavior is PetBehavior petBehavior)
                    {
                        tempBuffer.WriteByte((byte)((petBehavior.Happiness / petBehavior.MaxHappiness) * byte.MaxValue));
                        tempBuffer.WriteByte((byte)((petBehavior.Hunger / petBehavior.MaxHunger) * byte.MaxValue));
                    }
                }
                HealthUpdatePending = false;
            }
        }

        public virtual void ServerEventWrite(IWriteMessage msg, Client c, NetEntityEvent.IData extraData = null)
        {
            if (extraData is not IEventData eventData) { throw new Exception($"Malformed character event: expected {nameof(Character)}.{nameof(IEventData)}, got {extraData?.GetType().Name ?? "[NULL]"}"); }

            msg.WriteRangedInteger((int)eventData.EventType, (int)EventType.MinValue, (int)EventType.MaxValue);
            switch (eventData)
            {
                case InventoryStateEventData inventoryData:
                    msg.WriteUInt16(GameMain.Server.EntityEventManager.Events.Last()?.ID ?? (ushort)0);
                    Inventory.ServerEventWrite(msg, c, inventoryData);
                    break;
                case ControlEventData controlEventData:
                    Client owner = controlEventData.Owner;
                    msg.WriteBoolean(owner == c && owner.Character == this);
                    msg.WriteByte(owner != null && owner.Character == this && GameMain.Server.ConnectedClients.Contains(owner) ? owner.SessionId : (byte)0);
                    msg.WriteBoolean(info is { RenamingEnabled: true });
                    break;
                case CharacterStatusEventData statusEventData:
                    WriteStatus(msg, statusEventData.ForceAfflictionData);
                    break;
                case UpdateSkillsEventData updateSkillsData:
                    if (Info?.Job is { } job)
                    {
                        msg.WriteIdentifier(updateSkillsData.SkillIdentifier);
                        msg.WriteBoolean(updateSkillsData.ForceNotification);
                        //don't use Character.GetSkillLevel here, because it applies all the temporary boosts from items and afflictions on the skill level
                        msg.WriteSingle(job.GetSkillLevel(updateSkillsData.SkillIdentifier));
                    }
                    else
                    {
                        msg.WriteIdentifier(Identifier.Empty);
                    }
                    break;
                case IAttackEventData attackEventData:
                    {
                        int attackLimbIndex = Removed ? -1 : Array.IndexOf(AnimController.Limbs, attackEventData.AttackLimb);
                        ushort targetEntityId = NullEntityID;
                        int targetLimbIndex = -1;
                        if (attackEventData.TargetEntity is Entity { Removed: false } targetEntity)
                        {
                            targetEntityId = targetEntity.ID;
                            if (targetEntity is Character { AnimController.Limbs: var targetLimbsArray })
                            {
                                targetLimbIndex = targetLimbsArray.IndexOf(attackEventData.TargetLimb);
                            }
                        }
                        msg.WriteByte((byte)(attackLimbIndex < 0 ? 255 : attackLimbIndex));
                        msg.WriteUInt16((ushort)targetEntityId);
                        msg.WriteByte((byte)(targetLimbIndex < 0 ? 255 : targetLimbIndex));
                        msg.WriteSingle(attackEventData.TargetSimPos.X);
                        msg.WriteSingle(attackEventData.TargetSimPos.Y);
                    }
                    break;
                case AssignCampaignInteractionEventData _:
                    msg.WriteByte((byte)CampaignInteractionType);
                    msg.WriteBoolean(RequireConsciousnessForCustomInteract);
                    break;
                case ObjectiveManagerStateEventData objectiveManagerStateEventData:
                    AIObjectiveManager.ObjectiveType type = objectiveManagerStateEventData.ObjectiveType;
                    msg.WriteRangedInteger((int)type, (int)AIObjectiveManager.ObjectiveType.MinValue, (int)AIObjectiveManager.ObjectiveType.MaxValue);
                    if (!(AIController is HumanAIController controller))
                    {
                        msg.WriteBoolean(false);
                        break;
                    }
                    if (type == AIObjectiveManager.ObjectiveType.Order)
                    {
                        var currentOrderInfo = controller.ObjectiveManager.GetCurrentOrderInfo();
                        bool validOrder = currentOrderInfo != null;
                        msg.WriteBoolean(validOrder);
                        if (!validOrder) { break; }
                        var orderPrefab = currentOrderInfo.Prefab;
                        msg.WriteUInt32(orderPrefab.UintIdentifier);
                        if (!orderPrefab.HasOptions) { break; }
                        int optionIndex = orderPrefab.AllOptions.IndexOf(currentOrderInfo.Option);
                        if (optionIndex == -1)
                        {
                            DebugConsole.AddWarning($"Error while writing order data. Order option \"{currentOrderInfo.Option}\" not found in the order prefab \"{orderPrefab.Name}\".");
                        }
                        msg.WriteRangedInteger(optionIndex, -1, orderPrefab.AllOptions.Length);
                    }
                    else if (type == AIObjectiveManager.ObjectiveType.Objective)
                    {
                        var objective = controller.ObjectiveManager.CurrentObjective;
                        bool validObjective = objective?.Identifier is { IsEmpty: false };
                        msg.WriteBoolean(validObjective);
                        if (!validObjective) { break; }
                        msg.WriteIdentifier(objective.Identifier);
                        msg.WriteIdentifier(objective.Option);
                        UInt16 targetEntityId = 0;
                        if (objective is AIObjectiveOperateItem operateObjective && operateObjective.OperateTarget != null)
                        {
                            targetEntityId = operateObjective.OperateTarget.ID;
                        }
                        msg.WriteUInt16(targetEntityId);
                    }
                    break;
                case TeamChangeEventData _:
                    msg.WriteByte((byte)TeamID);
                    break;
                case AddToCrewEventData addToCrewEventData:
                    msg.WriteNetSerializableStruct(addToCrewEventData.ItemTeamChange);
                    break;
                case RemoveFromCrewEventData removeFromCrewEventData:
                    msg.WriteNetSerializableStruct(removeFromCrewEventData.ItemTeamChange);
                    break;
                case UpdateExperienceEventData _:
                    msg.WriteInt32(Info.ExperiencePoints);
                    msg.WriteInt32(info.AdditionalTalentPoints);
                    break;
                case UpdateTalentsEventData _:
                    msg.WriteUInt16((ushort)characterTalents.Count);
                    foreach (var unlockedTalent in characterTalents)
                    {
                        msg.WriteBoolean(unlockedTalent.AddedThisRound);
                        msg.WriteUInt32(unlockedTalent.Prefab.UintIdentifier);
                    }
                    break;
                case UpdateMoneyEventData _:
                    msg.WriteInt32(Wallet?.Balance ?? 0);
                    break;
                case UpdateRefundPointsEventData when Info is { } i:
                    msg.WriteInt32(i.TalentRefundPoints);
                    break;
                case ConfirmRefundEventData:
                    // No data
                    break;
                case UpdatePermanentStatsEventData updatePermanentStatsEventData:

                    StatTypes statType = updatePermanentStatsEventData.StatType;
                    if (Info == null)
                    {
                        msg.WriteByte((byte)0);
                        msg.WriteByte((byte)0);
                    }
                    else if (!Info.SavedStatValues.ContainsKey(statType))
                    {
                        msg.WriteByte((byte)0);
                        msg.WriteByte((byte)statType);
                    }
                    else
                    {
                        msg.WriteByte((byte)Info.SavedStatValues[statType].Count);
                        msg.WriteByte((byte)statType);
                        foreach (var savedStatValue in Info.SavedStatValues[statType])
                        {
                            msg.WriteIdentifier(savedStatValue.StatIdentifier);
                            msg.WriteSingle(savedStatValue.StatValue);
                            msg.WriteBoolean(savedStatValue.RemoveOnDeath);
                        }
                    }
                    break;
                case LatchedOntoTargetEventData latchedOntoTargetEventData:
                    msg.WriteBoolean(latchedOntoTargetEventData.IsLatched);
                    if (latchedOntoTargetEventData.IsLatched)
                    {
                        msg.WriteSingle(SimPosition.X);
                        msg.WriteSingle(SimPosition.Y);
                        msg.WriteSingle(latchedOntoTargetEventData.AttachSurfaceNormal.X);
                        msg.WriteSingle(latchedOntoTargetEventData.AttachSurfaceNormal.Y);
                        msg.WriteSingle(latchedOntoTargetEventData.AttachPos.X);
                        msg.WriteSingle(latchedOntoTargetEventData.AttachPos.Y);
                        msg.WriteInt32(latchedOntoTargetEventData.TargetLevelWallIndex);
                        if (latchedOntoTargetEventData.TargetStructureID != NullEntityID)
                        {
                            msg.WriteUInt16(latchedOntoTargetEventData.TargetStructureID);
                        }
                        else if (latchedOntoTargetEventData.TargetCharacterID != NullEntityID)
                        {
                            msg.WriteUInt16(latchedOntoTargetEventData.TargetCharacterID);
                        }
                        else
                        {
                            msg.WriteUInt16(NullEntityID);
                        }
                    }
                    break;
                default:
                    throw new Exception($"Malformed character event: did not expect {eventData.GetType().Name}");
            }
        }

        private readonly List<int> severedJointIndices = new List<int>();

        /// <param name="forceAfflictionData">Normally full affliction data is not written for dead characters, this can be used to force them to be written</param>
        private void WriteStatus(IWriteMessage msg, bool forceAfflictionData = false)
        {
            msg.WriteBoolean(TriggerDeathEffects);
            msg.WriteBoolean(IsDead);
            if (IsDead)
            {
                msg.WriteRangedInteger((int)CauseOfDeath.Type, 0, Enum.GetValues(typeof(CauseOfDeathType)).Length - 1);
                if (CauseOfDeath.Type == CauseOfDeathType.Affliction)
                {
                    msg.WriteUInt32(CauseOfDeath.Affliction.UintIdentifier);
                }
                msg.WriteBoolean(forceAfflictionData);
                if (forceAfflictionData)
                {
                    CharacterHealth.ServerWrite(msg);
                }
            }
            else
            {
                CharacterHealth.ServerWrite(msg);
            }
            if (AnimController?.LimbJoints == null)
            {
                //0 limbs severed
                msg.WriteByte((byte)0);
            }
            else
            {
                severedJointIndices.Clear();
                for (int i = 0; i < AnimController.LimbJoints.Length; i++)
                {
                    if (AnimController.LimbJoints[i] != null && AnimController.LimbJoints[i].IsSevered)
                    {
                        severedJointIndices.Add(i);
                    }
                }
                msg.WriteByte((byte)severedJointIndices.Count);
                foreach (int jointIndex in severedJointIndices)
                {
                    msg.WriteByte((byte)jointIndex);
                }
            }
        }

        public void WriteSpawnData(IWriteMessage msg, UInt16 entityId, bool restrictMessageSize)
        {
            if (GameMain.Server == null) { return; }
            
            int initialMsgLength = msg.LengthBytes;

            msg.WriteBoolean(Info == null);
            msg.WriteUInt16(entityId);
            msg.WriteIdentifier(SpeciesName);
            msg.WriteString(Seed);

            if (Removed)
            {
                msg.WriteSingle(0.0f);
                msg.WriteSingle(0.0f);
            }
            else
            {
                msg.WriteSingle(WorldPosition.X);
                msg.WriteSingle(WorldPosition.Y);
            }

            msg.WriteBoolean(Enabled);
            msg.WriteBoolean(DisabledByEvent);

            //character with no characterinfo (e.g. some monster)
            if (Info == null)
            {
                TryWriteStatus(msg);
                return;
            }

            Client ownerClient = GameMain.Server.ConnectedClients.Find(c => c.Character == this);
            if (ownerClient != null)
            {
                msg.WriteBoolean(true);
                msg.WriteByte(ownerClient.SessionId);
            }
            else if (GameMain.Server.Character == this)
            {
                msg.WriteBoolean(true);
                msg.WriteByte((byte)0);
            }
            else
            {
                msg.WriteBoolean(false);
            }
            msg.WriteSingle(HumanPrefabHealthMultiplier);
            msg.WriteInt32(Wallet.Balance);
            msg.WriteRangedInteger(Wallet.RewardDistribution, 0, 100);
            msg.WriteByte((byte)TeamID);
            msg.WriteBoolean(this is AICharacter);
            msg.WriteIdentifier(info.SpeciesName);
            int msgLengthBeforeInfo = msg.LengthBytes;
            info.ServerWrite(msg);
            int infoLength = msg.LengthBytes - msgLengthBeforeInfo;

            msg.WriteByte((byte)CampaignInteractionType);
            if (CampaignInteractionType == CampaignMode.InteractionType.Store)
            {
                msg.WriteIdentifier(MerchantIdentifier);
            }
            msg.WriteIdentifier(Faction);

            int msgLengthBeforeOrders = msg.LengthBytes;
            // Current orders
            msg.WriteByte((byte)info.CurrentOrders.Count(o => o != null));
            foreach (var orderInfo in info.CurrentOrders)
            {
                if (orderInfo == null) { continue; }
                msg.WriteUInt32(orderInfo.Prefab.UintIdentifier);
                msg.WriteUInt16(orderInfo.TargetEntity == null ? (UInt16)0 : orderInfo.TargetEntity.ID);
                var hasOrderGiver = orderInfo.OrderGiver != null;
                msg.WriteBoolean(hasOrderGiver);
                if (hasOrderGiver) { msg.WriteUInt16(orderInfo.OrderGiver.ID); }
                msg.WriteByte((byte)(orderInfo.Option == Identifier.Empty ? 0 : orderInfo.Prefab.Options.IndexOf(orderInfo.Option)));
                msg.WriteByte((byte)orderInfo.ManualPriority);
                var hasTargetPosition = orderInfo.TargetPosition != null;
                msg.WriteBoolean(hasTargetPosition);
                if (hasTargetPosition)
                {
                    msg.WriteSingle(orderInfo.TargetPosition.Position.X);
                    msg.WriteSingle(orderInfo.TargetPosition.Position.Y);
                    msg.WriteUInt16(orderInfo.TargetPosition.Hull == null ? (UInt16)0 : orderInfo.TargetPosition.Hull.ID);
                }
            }
            int ordersLength = msg.LengthBytes - msgLengthBeforeOrders;

            if (msg.LengthBytes - initialMsgLength >= 255 && restrictMessageSize)
            {
                string errorMsg = $"Error when writing character spawn data for  \"{Name}\": data exceeded 255 bytes (info: {infoLength}, orders: {ordersLength}, total: {msg.LengthBytes - initialMsgLength})";
                DebugConsole.ThrowError(errorMsg);
                GameAnalyticsManager.AddErrorEventOnce("Character.WriteSpawnData:TooMuchData", GameAnalyticsManager.ErrorSeverity.Error, errorMsg);
            }

            TryWriteStatus(msg);

            void TryWriteStatus(IWriteMessage msg)
            {
                int msgLengthBeforeStatus = msg.LengthBytes - initialMsgLength;

                var tempBuffer = new ReadWriteMessage();
                WriteStatus(tempBuffer, forceAfflictionData: true);
                if (msgLengthBeforeStatus + tempBuffer.LengthBytes >= 255 && restrictMessageSize)
                { 
                    msg.WriteBoolean(false);
                    if (msgLengthBeforeStatus < 255)
                    {
                        string errorMsg = $"Error when writing character spawn data for \"{Name}\": status data caused the length of the message to exceed 255 bytes ({msgLengthBeforeStatus} + {tempBuffer.LengthBytes})";
                        DebugConsole.ThrowError(errorMsg);
                        GameAnalyticsManager.AddErrorEventOnce("Character.WriteSpawnData:TooMuchDataForStatus", GameAnalyticsManager.ErrorSeverity.Error, errorMsg);
                    }
                }
                else
                {
                    msg.WriteBoolean(true);
                    WriteStatus(msg, forceAfflictionData: true);
                }
            }

            DebugConsole.Log("Character spawn message length: " + (msg.LengthBytes - initialMsgLength));
        }
    }
}

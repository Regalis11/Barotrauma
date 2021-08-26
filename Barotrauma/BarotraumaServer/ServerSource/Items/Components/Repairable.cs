using Barotrauma.Networking;

namespace Barotrauma.Items.Components
{
    partial class Repairable : ItemComponent, IServerSerializable, IClientSerializable
    {
        void InitProjSpecific()
        {
            //let the clients know the initial deterioration delay
            item.CreateServerEvent(this);
        }

        public void ServerRead(ClientNetObject type, IReadMessage msg, Client c)
        {
            if (c.Character == null) { return; }
            var requestedFixAction = (FixActions)msg.ReadRangedInteger(0, 2);
            var QTESuccess = msg.ReadBoolean();
            if (requestedFixAction != FixActions.None)
            {
                if (!c.Character.IsTraitor && requestedFixAction == FixActions.Sabotage)
                {
                    if (GameSettings.VerboseLogging)
                    {
                        DebugConsole.Log($"Non traitor \"{c.Character.Name}\" attempted to sabotage item.");
                    }
                    requestedFixAction = FixActions.Repair;
                }

                if (CurrentFixer == null || CurrentFixer == c.Character && requestedFixAction != currentFixerAction)
                {
                    StartRepairing(c.Character, requestedFixAction);
                    item.CreateServerEvent(this);
                }
            }
            else
            {
                RepairBoost(QTESuccess);
                item.CreateServerEvent(this);
            }
        }

        public void ServerWrite(IWriteMessage msg, Client c, object[] extraData = null)
        {
            msg.Write(deteriorationTimer);
            msg.Write(deteriorateAlwaysResetTimer);
            msg.Write(DeteriorateAlways);
            msg.Write(CurrentFixer == null ? (ushort)0 : CurrentFixer.ID);
            msg.WriteRangedInteger((int)currentFixerAction, 0, 2);
            msg.Write(repairBoost);
        }
    }
}

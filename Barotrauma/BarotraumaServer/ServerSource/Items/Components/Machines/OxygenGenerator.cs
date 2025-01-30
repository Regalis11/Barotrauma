using Barotrauma.Networking;

namespace Barotrauma.Items.Components
{
    internal partial class OxygenGenerator : IServerSerializable, IClientSerializable
    {
        #region Networking
        public void ServerEventWrite(IWriteMessage msg, Client c, NetEntityEvent.IData extraData = null) => WriteGenerationRatio(msg);
        public void ServerEventRead(IReadMessage msg, Client c)
        {
            float newGenerationRatio = ReadGenerationRatio(msg);

            if (item.CanClientAccess(c))
            {
                GenerationRatio = newGenerationRatio;
                GameServer.Log($"{GameServer.CharacterLogName(c.Character)} set the oxygen generation amount of {item.Name} to {MathUtils.RoundToInt(generationRatio * 100f)}%", ServerLog.MessageType.ItemInteraction);
            }

            item.CreateServerEvent(this);
        }
        #endregion
    }
}
using System.Xml.Linq;

namespace Barotrauma.Items.Components
{
    class DeltaComponent : ItemComponent
    {
        private int prevValueHash;

        public DeltaComponent(Item item, XElement element)
            : base (item, element)
        {
        }

        public override void ReceiveSignal(Signal signal, Connection connection)
        {
            if (connection.Name != "signal_in") { return; }

            int valueHash = signal.value.GetHashCode();

            if (valueHash == prevValueHash) {return;}
            prevValueHash = valueHash;
            item.SendSignal(signal, "signal_out");
        }
    }
}

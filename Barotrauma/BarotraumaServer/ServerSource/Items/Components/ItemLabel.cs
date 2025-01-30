using Barotrauma.Networking;
using Microsoft.Xna.Framework;
using System.Collections.Generic;
using System.Xml.Linq;

namespace Barotrauma.Items.Components
{
    partial class ItemLabel : ItemComponent, IDrawableComponent
    {
        private CoroutineHandle sendStateCoroutine;
        private string lastSentText;
        private float sendStateTimer;

        public override void Move(Vector2 amount, bool ignoreContacts = false)
        {
            //do nothing
        }

        public ItemLabel(Item item, ContentXElement element)
            : base(item, element)
        {
        }

        partial void OnStateChanged()
        {
            sendStateTimer = 0.1f;
            if (sendStateCoroutine == null)
            {
                sendStateCoroutine = CoroutineManager.StartCoroutine(SendStateAfterDelay());
            }
        }

        private IEnumerable<CoroutineStatus> SendStateAfterDelay()
        {
            while (sendStateTimer > 0.0f)
            {
                sendStateTimer -= CoroutineManager.DeltaTime;
                yield return CoroutineStatus.Running;
            }

            if (item.Removed || GameMain.NetworkMember == null)
            {
                yield return CoroutineStatus.Success;
            }

            sendStateCoroutine = null;
            if (lastSentText != Text) { item.CreateServerEvent(this); }
            yield return CoroutineStatus.Success;
        }

        public void ServerEventWrite(IWriteMessage msg, Client c, NetEntityEvent.IData extraData = null)
        {
            msg.WriteString(Text);
            lastSentText = Text;
        }
    }
}

using System;
using System.Collections.Generic;
using System.Xml.Linq;
using Microsoft.Xna.Framework;
namespace Barotrauma.Items.Components
{
    class DelayComponent : ItemComponent
    {
        class DelayedSignal
        {
            public readonly Signal Signal;
            //in number of frames
            public int SendTimer;
            //in number of frames
            public int SendDuration;

            public DelayedSignal(Signal signal, int sendTimer)
            {
                Signal = signal;
                SendTimer = sendTimer;
            }
        }

        private int signalQueueSize;
        private int delayTicks;

        private readonly Queue<DelayedSignal> signalQueue;

        private DelayedSignal prevQueuedSignal;
        
        private float delay;
        [InGameEditable(MinValueFloat = 0.0f, MaxValueFloat = 60.0f, DecimalCount = 2), Serialize(1.0f, true, description: "How long the item delays the signals (in seconds).", alwaysUseInstanceValues: true)]
        public float Delay
        {
            get { return delay; }
            set
            {
                if (value == delay) { return; }
                delay = value;
                delayTicks = (int)(delay / Timing.Step);
                signalQueueSize = Math.Max(delayTicks, 1) * 2;
            }
        }

        [InGameEditable, Serialize(false, true, description: "Should the component discard previously received signals when a new one is received.", alwaysUseInstanceValues: true)]
        public bool ResetWhenSignalReceived
        {
            get;
            set;
        }

        [InGameEditable, Serialize(false, true, description: "Should the component discard previously received signals when the incoming signal changes.", alwaysUseInstanceValues: true)]
        public bool ResetWhenDifferentSignalReceived
        {
            get;
            set;
        }

        public DelayComponent(Item item, XElement element)
            : base (item, element)
        {
            signalQueue = new Queue<DelayedSignal>();
            IsActive = true;
        }

        public override void Update(float deltaTime, Camera cam)
        {
            foreach (var val in signalQueue)
            {
                val.SendTimer -= 1;
            }

            while (signalQueue.Count > 0 && signalQueue.Peek().SendTimer <= 0)
            {
                var signalOut = signalQueue.Peek();
                signalOut.SendDuration -= 1;
                item.SendSignal(new Signal(signalOut.Signal.value, strength: signalOut.Signal.strength), "signal_out");
                if (signalOut.SendDuration <= 0) 
                { 
                    signalQueue.Dequeue(); 
                } 
                else 
                { 
                    break; 
                }
            }
        }

        public override void ReceiveSignal(Signal signal, Connection connection)
        {
            switch (connection.Name)
            {
                case "signal_in":
                    if (signalQueue.Count >= signalQueueSize) { return; }
                    if (ResetWhenSignalReceived) { prevQueuedSignal = null; signalQueue.Clear(); }
                    if (ResetWhenDifferentSignalReceived && signalQueue.Count > 0 && signalQueue.Peek().Signal.value != signal.value)
                    {
                        prevQueuedSignal = null;
                        signalQueue.Clear();
                    }

                    if (prevQueuedSignal != null && 
                        prevQueuedSignal.Signal.value == signal.value && 
                        MathUtils.NearlyEqual(prevQueuedSignal.Signal.strength, signal.strength) &&
                        ((prevQueuedSignal.SendTimer + prevQueuedSignal.SendDuration == delayTicks) || (prevQueuedSignal.SendTimer <= 0 && prevQueuedSignal.SendDuration > 0)))
                    {
                        prevQueuedSignal.SendDuration += 1;
                        return;
                    }

                    prevQueuedSignal = new DelayedSignal(signal, delayTicks)
                    {
                        SendDuration = 1
                    };
                    signalQueue.Enqueue(prevQueuedSignal);
                    break;
                case "set_delay":
                    float val;
                    if (float.TryParse(signal.value, out val))
                    {
						float clampval = MathHelper.Clamp(val, 0, 60);
                        if (signalQueue.Count > 0 && clampval != Delay)
                        {
                            prevQueuedSignal = null; //might not be the best but hey it works???
                            signalQueue.Clear();
                        }
                        Delay = clampval;
                    }
                    break;
            }
        }
    }
}

﻿using System;
using System.Xml.Linq;

namespace Barotrauma.Items.Components
{
    class RelayComponent : ItemComponent
    {
        private float maxPower;

        [Editable, HasDefaultValue(1000.0f, true)]
        public float MaxPower
        {
            get { return maxPower; }
            set
            {
                maxPower = Math.Max(0.0f, value);
            }
        }

        [Editable, HasDefaultValue(false, true)]
        public bool IsOn
        {
            get { return IsActive; }
            set
            {
                IsActive = value;
            }
        }

        public RelayComponent(Item item, XElement element)
            : base (item, element)
        {
            IsActive = true;
        }

        public override void ReceiveSignal(string signal, Connection connection, Item sender, float power=0.0f)
        {
            if (item.Condition <= 0.0f) return;

            if (power > maxPower) item.Condition = 0.0f;
            
            if (connection.Name.Contains("_in"))
            {
                if (!IsActive) return;

                string outConnection = connection.Name.Contains("power_in") ? "power_out" : "signal_out";

                int connectionNumber = -1;
                int.TryParse(connection.Name.Substring(connection.Name.Length - 1, 1), out connectionNumber);

                if (connectionNumber > 0) outConnection += connectionNumber;

                item.SendSignal(signal, outConnection, power);
            }
            else if (connection.Name == "toggle")
            {
                IsActive = !IsActive;
            }
            else if (connection.Name == "set_state")
            {
                IsActive = signal == "1";
            }

        }
    }
}

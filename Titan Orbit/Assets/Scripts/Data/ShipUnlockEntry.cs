using System;
using UnityEngine;

namespace TitanOrbit.Data
{
    /// <summary>One chassis unlock row for orbit store / ships tab.</summary>
    [Serializable]
    public class ShipUnlockEntry
    {
        public ShipChassisDefinition chassis;
        public int minHomePlanetLevel = 1;
        public float gemCost = 20f;
    }
}

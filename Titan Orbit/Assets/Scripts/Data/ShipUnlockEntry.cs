using System;
using UnityEngine;

namespace TitanOrbit.Data
{
    /// <summary>
    /// One unlockable chassis row for the orbit station ships tab — chassis metadata, minimum home-planet
    /// level, and gem cost. Built at runtime by <see cref="PlanetShipFamilyConfig.GetUnlockedEntriesForPlanet"/>.
    /// </summary>
    [Serializable]
    public class ShipUnlockEntry
    {
        public ShipChassisDefinition chassis;
        public int minHomePlanetLevel = 1;
        public float gemCost = 20f;
    }
}

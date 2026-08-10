using System;
using UnityEngine;

namespace TitanOrbit.Data
{
    // --- Type members ---
    /// <summary>
    /// One unlockable chassis row for the orbit station ships tab — chassis metadata, minimum
    /// homeworld level, and gem cost. Built at runtime by
    /// <see cref="PlanetShipFamilyConfig.GetUnlockedEntriesForPlanet"/>. Designer data lives in
    /// <see cref="PlanetShipFamilyConfig"/> ScriptableObject; this is a transient DTO (data transfer
    /// object) for UI lists, not saved to disk or replicated over the network.
    /// </summary>
    [Serializable]
    public class ShipUnlockEntry
    {
        /// <summary>
        /// Chassis definition (mesh, focus type, base stats reference). [UNITY] ScriptableObject link.
        /// </summary>
        public ShipChassisDefinition chassis;

        /// <summary>
        /// [TITAN-ORBIT] Minimum homeworld planet level before this row appears in the store.
        /// </summary>
        public int minHomePlanetLevel = 1;

        /// <summary>[TITAN-ORBIT] Gem price to unlock this chassis at the orbit station.</summary>
        public float gemCost = 20f;
    }
}

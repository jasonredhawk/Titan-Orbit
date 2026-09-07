using Unity.Entities;
using Unity.Mathematics;

namespace TitanOrbit.ECS
{
    /// <summary>
    /// [ECS/DOTS] Server-only last-damager bookkeeping for kill credit. Not ghost-serialized —
    /// clients never need who last hit a ship; only the server credits <see cref="ShipMatchStats.Kills"/>
    /// when <see cref="ShipDeathRecordingSystem"/> sees a new death.
    /// <para>
    /// [TITAN-ORBIT] Written by bullet and ramming damage paths. Cleared on respawn.
    /// Match stats themselves are left intact across deaths.
    /// Last impulse is packed into <see cref="ShipDeathVfxState"/> on death for the cosmetic breakup.
    /// </para>
    /// </summary>
    public struct ShipCombatAttribution : IComponentData
    {
        /// <summary>
        /// [NETCODE] <see cref="Unity.NetCode.GhostOwner.NetworkId"/> of the last ship that damaged
        /// this hull (bullet OwnerNetworkId or ramming GhostOwner). 0 = unknown / environment.
        /// </summary>
        public int LastDamagerNetworkId;

        /// <summary>
        /// [UNITY] Server world ElapsedTime when the last damaging hit was applied.
        /// Useful for debugging stale attribution; not currently used as a timeout gate.
        /// </summary>
        public float LastDamageServerTime;

        /// <summary>
        /// [TITAN-ORBIT] Unit XZ direction of the last damaging hit (bullet velocity, asteroid
        /// contact, mine/splash offset). Zero when the hit had no clear direction (burn DoT).
        /// </summary>
        public float2 LastImpulseXZ;

        /// <summary>
        /// [TITAN-ORBIT] Raw damage / force of that hit. Quantized into
        /// <see cref="ShipDeathVfxState.Packed"/> on death.
        /// </summary>
        public float LastImpulsePower;
    }
}

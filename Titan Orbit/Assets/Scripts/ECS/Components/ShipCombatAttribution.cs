using Unity.Entities;

namespace TitanOrbit.ECS
{
    /// <summary>
    /// [ECS/DOTS] Server-only last-damager bookkeeping for kill credit. Not ghost-serialized —
    /// clients never need who last hit a ship; only the server credits <see cref="ShipMatchStats.Kills"/>
    /// when <see cref="ShipDeathRecordingSystem"/> sees a new death.
    /// <para>
    /// [TITAN-ORBIT] Written by bullet and ramming damage paths. Cleared on respawn.
    /// Match stats themselves are left intact across deaths.
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
    }
}

using Unity.Entities;

namespace TitanOrbit.ECS
{
    /// <summary>
    /// Gem motion / tractor lock state. Server writes locks; clients apply
    /// <see cref="GemTractorLockRpc"/> and present beams from these fields.
    /// Not ghost-replicated.
    /// </summary>
    public struct GemMotionState : IComponentData
    {
        /// <summary>Coast (burst / nudge), Tractor (server pull active), or Idle (stopped).</summary>
        public const byte PhaseCoast = 0;

        /// <summary>Server is actively overwriting velocity toward a wing tip.</summary>
        public const byte PhaseTractor = 1;

        /// <summary>Below stop-speed and not under tractor — coast finished.</summary>
        public const byte PhaseIdle = 2;

        /// <summary>Motion phase (coast / tractor / idle).</summary>
        public byte Phase;

        /// <summary>
        /// Index within an asteroid destroy burst (0..N-1). Mining nuggets use 0.
        /// </summary>
        public byte BurstIndex;

        /// <summary>
        /// <see cref="Unity.NetCode.GhostOwner.NetworkId"/> of the ship locking this gem, or 0.
        /// </summary>
        public int TractorShipId;

        /// <summary>Wing index on that ship (0-based). Ignored when <see cref="TractorShipId"/> is 0.</summary>
        public byte TractorWingIndex;

        /// <summary>
        /// ServerTick index when the ship–gem deploy lock started (0 = unlocked).
        /// </summary>
        public uint TractorLockTick;

        /// <summary>
        /// Beam extend duration (seconds) computed at lock from toroidal distance.
        /// </summary>
        public float TractorExtendDuration;
    }
}

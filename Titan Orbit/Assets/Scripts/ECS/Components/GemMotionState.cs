using Unity.Entities;
using Unity.NetCode;

namespace TitanOrbit.ECS
{
    /// <summary>
    /// [ECS/DOTS] Ghosted gem motion / tractor lock state for deterministic client presentation.
    /// <para>
    /// Server is the only writer: spawn sets <see cref="BurstIndex"/> + Coast phase;
    /// <see cref="GemTractorBeamSystem"/> sets tractor lock fields; <see cref="GemMotionSystem"/>
    /// advances Coast→Idle when the gem stops. Clients follow interpolated LocalTransform /
    /// <see cref="GemKinematics"/> and use this lock for beam timing — they must not invent
    /// their own wing assignment for GO velocity.
    /// </para>
    /// </summary>
    public struct GemMotionState : IComponentData
    {
        /// <summary>Coast (burst / nudge), Tractor (server pull active), or Idle (stopped).</summary>
        public const byte PhaseCoast = 0;

        /// <summary>Server is actively overwriting velocity toward a wing tip.</summary>
        public const byte PhaseTractor = 1;

        /// <summary>Below stop-speed and not under tractor — coast finished.</summary>
        public const byte PhaseIdle = 2;

        /// <summary>
        /// [NETCODE] Motion phase. Clients use this to choose presentation (extrapolate coast vs
        /// follow tractor lock) without guessing from sparse Velocity snapshots alone.
        /// </summary>
        [GhostField] public byte Phase;

        /// <summary>
        /// [TITAN-ORBIT] Index within an asteroid destroy burst (0..N-1). Mining nuggets use 0.
        /// Client local-burst handoff matches this index so the wrong GO is not claimed.
        /// </summary>
        [GhostField] public byte BurstIndex;

        /// <summary>
        /// [NETCODE] <see cref="GhostOwner.NetworkId"/> of the ship locking this gem, or 0 if none.
        /// </summary>
        [GhostField] public int TractorShipId;

        /// <summary>Wing index on that ship (0-based). Ignored when <see cref="TractorShipId"/> is 0.</summary>
        [GhostField] public byte TractorWingIndex;

        /// <summary>
        /// [NETCODE] ServerTick index when the ship–gem deploy lock started (0 = unlocked).
        /// Client converts with the shared tick rate to match server deploy timing.
        /// </summary>
        [GhostField] public uint TractorLockTick;

        /// <summary>
        /// [TITAN-ORBIT] Beam extend duration (seconds) computed at lock from toroidal distance.
        /// Ghosted so client deploy clocks match without re-deriving from a lagged pose.
        /// </summary>
        [GhostField] public float TractorExtendDuration;
    }
}

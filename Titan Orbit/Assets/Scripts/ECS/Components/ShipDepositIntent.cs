using Unity.Entities;
using Unity.NetCode;

namespace TitanOrbit.ECS
{
    /// <summary>
    /// Server-authoritative gem deposit toggle that survives NetCode input prediction rollback.
    /// [NETCODE] <see cref="ShipInput.WantDepositGems"/> can be lost during prediction resync;
    /// this component is set by <see cref="SetWantDepositGemsCommand"/> RPC and read by gem deposit
    /// systems each server tick. [NETCODE] Ghost-serialized so orbit UI shows deposit state on all clients.
    /// Paired with <see cref="ShipMoonDockState"/> (must be landed) and moon orbit store RPCs.
    /// <para>
    /// Deposits are <b>discrete metronome chunks</b> (one <c>ShipLevel</c> of gems every
    /// <see cref="GemEconomyConstants.GemDepositBeatIntervalSeconds"/>), not a smooth per-frame drip.
    /// Presentation (SFX / Orbit Menu) follows ghosted <see cref="ShipDepositFeedback"/> beats.
    /// </para>
    /// <para>
    /// [TITAN-ORBIT] Server metronome timing lives on <see cref="ShipDepositBeatTimer"/> — a separate
    /// non-ghost component. Feedback beats are a separate ghost component so the intent bool stays simple.
    /// </para>
    /// </summary>
    public struct ShipDepositIntent : IComponentData
    {
        // --- Type members ---
        /// <summary>
        /// [TITAN-ORBIT] When true and the ship is fully docked at a moon, gems transfer from ship
        /// cargo to the planet pool on each deposit beat. Toggled by orbit UI via RPC, not raw input alone.
        /// </summary>
        [GhostField] public bool WantDepositGems;
    }

    /// <summary>
    /// [TITAN-ORBIT] Server-only metronome accumulator for discrete gem deposits.
    /// Not ghosted and not part of the StarshipGhost serializer — clients present SFX/UI from
    /// <see cref="ShipDepositFeedback"/> beats instead. Added at runtime by
    /// <see cref="ShipEnsureComponentsSystem"/>.
    /// </summary>
    public struct ShipDepositBeatTimer : IComponentData
    {
        /// <summary>
        /// Seconds accumulated toward the next deposit beat.
        /// Primed to one full interval on first eligible tick so the first chunk fires immediately.
        /// Reset to 0 when deposit stops.
        /// </summary>
        public float Accum;
    }

    /// <summary>
    /// [NETCODE] Ghosted deposit metronome feedback. Server increments <see cref="BeatSequence"/>
    /// and writes <see cref="LastChunkAmount"/> each time <see cref="GemDepositSystem"/> transfers
    /// one chunk. Clients play SFX and bump Orbit Menu Ship/Bank only when the sequence advances —
    /// presentation stays locked to real server deposits (not a free-running wall clock).
    /// </summary>
    public struct ShipDepositFeedback : IComponentData
    {
        /// <summary>
        /// Monotonic beat counter. Clients detect increases to fire one presentation tick.
        /// </summary>
        [GhostField] public uint BeatSequence;

        /// <summary>
        /// Gems moved on the most recent server beat (ship level, or leftover cargo).
        /// Drives deposit SFX pitch and optimistic Bank/Ship UI deltas.
        /// </summary>
        [GhostField] public float LastChunkAmount;
    }
}

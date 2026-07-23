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
    /// <see cref="GemEconomyConstants.GemDepositBeatIntervalSeconds"/>), not a smooth per-frame drip —
    /// so cargo HUD, Bank UI, and deposit SFX share the same cadence.
    /// </para>
    /// <para>
    /// [TITAN-ORBIT] Server metronome timing lives on <see cref="ShipDepositBeatTimer"/> — a separate
    /// non-ghost component — so adding a timer field never changes the <c>StarshipGhost</c> type hash
    /// (mismatched client/server builds disconnect on Join Team).
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
    /// Not ghosted and not part of the StarshipGhost serializer — clients run their own wall-clock
    /// deposit SFX metronome. Added at runtime by <see cref="ShipEnsureComponentsSystem"/>.
    /// </summary>
    public struct ShipDepositBeatTimer : IComponentData
    {
        /// <summary>
        /// Seconds accumulated toward the next deposit beat.
        /// Primed to one full interval on first eligible tick so the first chunk fires immediately
        /// (matches client SFX). Reset to 0 when deposit stops.
        /// </summary>
        public float Accum;
    }
}

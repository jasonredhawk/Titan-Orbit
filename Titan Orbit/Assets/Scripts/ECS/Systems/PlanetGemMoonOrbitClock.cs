using Unity.Entities;
using Unity.Mathematics;
using Unity.NetCode;
using UnityEngine;

namespace TitanOrbit.ECS
{
    /// <summary>
    /// Shared clock for analytic gem-moon orbit phase.
    /// <para>
    /// [TITAN-ORBIT] Moons are not ghosted transforms. Client and server recompute pose from
    /// <c>θ = phase(planetId) − ω · elapsed</c> (<see cref="Simulation.PlanetOrbitMath"/>).
    /// That only stays aligned if every consumer uses the same elapsed seconds.
    /// </para>
    /// <para>
    /// [NETCODE] Do not use <c>World.Time.ElapsedTime</c> for moon orbit. ClientWorld elapsed
    /// starts when the client process creates its world (late-join → large phase offset vs the
    /// server). Presentation code that preferred ServerWorld elapsed while client physics used
    /// ClientWorld elapsed caused “invisible moon” hits along the ring.
    /// </para>
    /// <para>
    /// Source of truth: <see cref="NetworkTime.ServerTick"/> converted with the sim tick rate.
    /// ServerTick is the same integer timeline on server and predicted client (including rollback
    /// resim inside <see cref="PredictedSimulationSystemGroup"/>).
    /// </para>
    /// <para>
    /// [TITAN-ORBIT] Presentation (<see cref="TryGetElapsedSeconds"/>) falls back when ClientWorld
    /// ServerTick stalls (Join Team hang / quiet snapshots) so moons keep orbiting while axial spin
    /// (deltaTime) still runs. Physics / combat keep calling <see cref="GetElapsedSeconds"/> on
    /// their world tick — no presentation fallback there.
    /// </para>
    /// </summary>
    public static class PlanetGemMoonOrbitClock
    {
        /// <summary>
        /// Default Hz when <see cref="ClientServerTickRate"/> is not present yet.
        /// Must stay equal to TitanOrbit.NetCode.TitanOrbitServerTickRateSystem.SimulationHz (60).
        /// Duplicated here because TitanOrbit.ECS cannot reference TitanOrbit.NetCode (cycle).
        /// </summary>
        public const int FallbackSimulationHz = 60;

        /// <summary>Last ClientWorld ServerTick index seen by presentation (stall detection).</summary>
        static uint s_LastPresentationTick;

        /// <summary>Realtime when <see cref="s_LastPresentationTick"/> last changed.</summary>
        static float s_LastPresentationTickRealtime = -1f;

        /// <summary>
        /// Last valid ServerTick elapsed. Dedicated clients have no ServerWorld fallback —
        /// holding this beats <see cref="Time.timeAsDouble"/> (that snaps the ring every stall).
        /// </summary>
        static double s_LastGoodElapsed;

        /// <summary>Realtime seconds with unchanged ClientWorld tick before presentation fallback.</summary>
        const float PresentationTickStallSeconds = 0.35f;

        /// <summary>
        /// Converts a NetCode <see cref="NetworkTime"/> sample into orbit elapsed seconds.
        /// Prefer this from ISystem.OnUpdate after <c>SystemAPI.GetSingleton&lt;NetworkTime&gt;()</c>
        /// (no EntityQuery allocations on the hot path).
        /// </summary>
        /// <param name="networkTime">Current world NetworkTime singleton.</param>
        /// <param name="simulationHz">
        /// Authoritative sim rate. Pass <see cref="ClientServerTickRate.SimulationTickRate"/> when
        /// available; otherwise <see cref="FallbackSimulationHz"/>.
        /// </param>
        /// <param name="includeTickFraction">
        /// True for presentation (LateUpdate / minimap) so the moon slides between fixed ticks.
        /// False inside predicted fixed-step systems so client and server share an exact tick phase.
        /// </param>
        /// <returns>Seconds on the ServerTick timeline (0 when tick is invalid).</returns>
        public static double ToElapsedSeconds(
            in NetworkTime networkTime,
            int simulationHz,
            bool includeTickFraction)
        {
            // --- Validate tick + rate ---
            // [NETCODE] ServerTick starts at 1; 0 / Invalid means networking is not ready yet.
            if (!networkTime.ServerTick.IsValid || simulationHz <= 0)
                return 0d;

            uint tickIndex = networkTime.ServerTick.TickIndexForValidTick;

            // --- Whole-tick phase (physics / prediction / server combat) ---
            // [TITAN-ORBIT] Same tick index ⇒ same θ on every peer. No World.Time involved.
            if (!includeTickFraction)
                return tickIndex / (double)simulationHz;

            // --- Fractional phase (render / UI) ---
            // [NETCODE] ServerTickFraction is in (0, 1] on variable-rate clients; always 1 on server.
            float fraction = math.clamp(networkTime.ServerTickFraction, 0f, 1f);
            return (tickIndex - 1u + fraction) / (double)simulationHz;
        }

        /// <summary>
        /// Convenience for ISystem callers that already hold NetworkTime (and optional tick rate).
        /// </summary>
        /// <param name="networkTime">Current world NetworkTime.</param>
        /// <param name="simulationTickRate">
        /// <see cref="ClientServerTickRate.SimulationTickRate"/>, or 0 to use
        /// <see cref="FallbackSimulationHz"/>.
        /// </param>
        /// <param name="includeTickFraction">See <see cref="ToElapsedSeconds"/>.</param>
        public static double GetElapsedSeconds(
            in NetworkTime networkTime,
            int simulationTickRate = 0,
            bool includeTickFraction = false)
        {
            int hz = simulationTickRate > 0 ? simulationTickRate : FallbackSimulationHz;
            return ToElapsedSeconds(networkTime, hz, includeTickFraction);
        }

        /// <summary>
        /// ISystem helper: ServerTick seconds from this world's EntityManager, or <paramref name="fallbackElapsed"/>.
        /// Prefer over World.Time for gem lifetime stamps and anything that must match late-join clients.
        /// </summary>
        /// <param name="em">World EntityManager (server or client).</param>
        /// <param name="fallbackElapsed">Used when NetworkTime is not ready (usually World.Time.ElapsedTime).</param>
        /// <param name="includeTickFraction">False for sim stamps; true for presentation smoothing.</param>
        /// <returns>Seconds on the ServerTick timeline, or the fallback.</returns>
        public static float GetElapsedSecondsOrFallback(
            EntityManager em,
            double fallbackElapsed,
            bool includeTickFraction = false)
        {
            // --- NetworkTime singleton ---
            // [NETCODE] Allocates a short-lived query — call once per system OnUpdate, not per entity.
            using var timeQuery = em.CreateEntityQuery(ComponentType.ReadOnly<NetworkTime>());
            if (!timeQuery.TryGetSingleton<NetworkTime>(out var networkTime) || !networkTime.ServerTick.IsValid)
                return (float)fallbackElapsed;

            // --- Sim Hz (usually 60) ---
            int hz = FallbackSimulationHz;
            using var rateQuery = em.CreateEntityQuery(ComponentType.ReadOnly<ClientServerTickRate>());
            if (rateQuery.TryGetSingleton<ClientServerTickRate>(out var tickRate)
                && tickRate.SimulationTickRate > 0)
            {
                hz = tickRate.SimulationTickRate;
            }

            return (float)ToElapsedSeconds(networkTime, hz, includeTickFraction);
        }

        /// <summary>
        /// MonoBehaviour / hybrid helper for presentation moons.
        /// Prefers ClientWorld tick; if that tick stalls while real time advances, falls back to
        /// ServerWorld (Local Host) then <see cref="Time.timeAsDouble"/> so orbit keeps moving.
        /// </summary>
        /// <param name="elapsedSeconds">Orbit elapsed when true.</param>
        /// <param name="includeTickFraction">True for mesh/minimap smoothing between ticks.</param>
        /// <returns>False only when no usable timeline exists at all.</returns>
        public static bool TryGetElapsedSeconds(out double elapsedSeconds, bool includeTickFraction = true)
        {
            elapsedSeconds = 0d;

            // --- ClientWorld first (matches predicted colliders when snapshots advance) ---
            if (TryReadWorldElapsed(
                    ClientServerBootstrap.ClientWorld,
                    includeTickFraction,
                    out elapsedSeconds,
                    out uint clientTick,
                    out bool clientValid))
            {
                // --- Stall detection ---
                // [TITAN-ORBIT] Debug 1af271: after map load / Join Team, ClientWorld ServerTick can
                // freeze while axial spin (deltaTime) keeps going — moons look glued to the ring.
                float now = Time.realtimeSinceStartup;
                if (clientValid)
                {
                    if (clientTick != s_LastPresentationTick)
                    {
                        s_LastPresentationTick = clientTick;
                        s_LastPresentationTickRealtime = now;
                        s_LastGoodElapsed = elapsedSeconds;
                        return true;
                    }

                    if (s_LastPresentationTickRealtime < 0f)
                        s_LastPresentationTickRealtime = now;

                    if (now - s_LastPresentationTickRealtime < PresentationTickStallSeconds)
                    {
                        s_LastGoodElapsed = elapsedSeconds;
                        return true;
                    }
                }
            }

            // --- Local Host: ServerWorld tick still advances during client quiet ---
            if (TryReadWorldElapsed(
                    ClientServerBootstrap.ServerWorld,
                    includeTickFraction,
                    out elapsedSeconds,
                    out _,
                    out bool serverValid) &&
                serverValid)
            {
                s_LastGoodElapsed = elapsedSeconds;
                return true;
            }

            // --- Dedicated: hold last ServerTick phase (do not jump to wall clock) ---
            // Time.timeAsDouble is a different epoch than ServerTick. Alternating them
            // snaps the entire moon ring every time snapshots hitch — the dedicated-only
            // "orbit path snap-back" that Local Host never hits (ServerWorld fallback).
            if (s_LastGoodElapsed > 0d)
            {
                elapsedSeconds = s_LastGoodElapsed;
                return true;
            }

            // --- First frames only: no tick yet ---
            elapsedSeconds = Time.timeAsDouble;
            return true;
        }

        /// <summary>
        /// Reads ServerTick elapsed from one world. Returns false when the world/singleton is missing.
        /// </summary>
        static bool TryReadWorldElapsed(
            World world,
            bool includeTickFraction,
            out double elapsedSeconds,
            out uint tickIndex,
            out bool tickValid)
        {
            elapsedSeconds = 0d;
            tickIndex = 0;
            tickValid = false;

            if (world == null || !world.IsCreated)
                return false;

            var em = world.EntityManager;
            using var timeQuery = em.CreateEntityQuery(ComponentType.ReadOnly<NetworkTime>());
            if (!timeQuery.TryGetSingleton<NetworkTime>(out var networkTime) || !networkTime.ServerTick.IsValid)
                return false;

            int hz = FallbackSimulationHz;
            using var rateQuery = em.CreateEntityQuery(ComponentType.ReadOnly<ClientServerTickRate>());
            if (rateQuery.TryGetSingleton<ClientServerTickRate>(out var tickRate)
                && tickRate.SimulationTickRate > 0)
            {
                hz = tickRate.SimulationTickRate;
            }

            tickIndex = networkTime.ServerTick.TickIndexForValidTick;
            tickValid = true;
            elapsedSeconds = ToElapsedSeconds(networkTime, hz, includeTickFraction);
            return true;
        }
    }
}

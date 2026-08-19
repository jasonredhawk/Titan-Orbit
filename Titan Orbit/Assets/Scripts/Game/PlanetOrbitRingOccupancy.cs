using System.Collections.Generic;
using TitanOrbit.Core;
using TitanOrbit.ECS;
using Unity.Entities;
using Unity.NetCode;
using UnityEngine;

namespace TitanOrbit.Game
{
    /// <summary>
    /// Per-planet orbit-ring occupancy published each client presentation tick.
    /// World rings and the minimap share this so every positively locked team's color is
    /// visible — cycling when more than one team is captured in the ring.
    /// </summary>
    public static class PlanetOrbitRingOccupancy
    {
        /// <summary>One planet's locked-in team mask for the current frame.</summary>
        public struct Snapshot
        {
            /// <summary>TeamA=bit0 … TeamE=bit4. Zero means nobody is locked in.</summary>
            public byte TeamMask;
        }

        /// <summary>
        /// Idle people-transfer ring RGB — world fill, minimap stroke, and turret pads share this.
        /// Occupied colors come from <see cref="TeamIdExtensions.ToColor"/>.
        /// </summary>
        public static readonly Color IdleTint = Color.white;

        /// <summary>How long each occupying team holds the ring tint before the next.</summary>
        public const float SecondsPerOccupyingTeam = 1f;

        /// <summary>Cross-fade into the next team near the end of each hold.</summary>
        const float TeamBlendSeconds = 0.2f;

        static readonly Dictionary<int, Snapshot> Published = new Dictionary<int, Snapshot>(32);
        static readonly Dictionary<int, Snapshot> Building = new Dictionary<int, Snapshot>(32);

        /// <summary>Starts a new occupancy publish (clears the write buffer).</summary>
        public static void BeginPublish() => Building.Clear();

        /// <summary>Records one locked-in ship team for <paramref name="planetId"/>.</summary>
        public static void AddLockedShip(int planetId, TeamId team)
        {
            if (planetId == 0 || team == TeamId.None)
                return;

            byte bit = (byte)team.ToMaskBit();
            if (Building.TryGetValue(planetId, out var snap))
            {
                snap.TeamMask |= bit;
                Building[planetId] = snap;
                return;
            }

            Building[planetId] = new Snapshot { TeamMask = bit };
        }

        /// <summary>Swaps the write buffer into the published map for draw / minimap readers.</summary>
        public static void EndPublish()
        {
            Published.Clear();
            foreach (var kv in Building)
                Published[kv.Key] = kv.Value;
        }

        /// <summary>Clears published occupancy (session teardown / world destroy).</summary>
        public static void Clear()
        {
            Published.Clear();
            Building.Clear();
        }

        /// <summary>True when at least one team is locked in this planet's ring.</summary>
        public static bool TryGet(int planetId, out Snapshot snapshot)
        {
            if (planetId != 0 && Published.TryGetValue(planetId, out snapshot) && snapshot.TeamMask != 0)
                return true;
            snapshot = default;
            return false;
        }

        /// <summary>
        /// World / minimap ring RGB: <see cref="IdleTint"/> when empty, otherwise the occupying
        /// team color (cycled when several teams share the ring). Same result for both views.
        /// </summary>
        public static Color ResolveRingTint(int planetId)
        {
            if (!TryGet(planetId, out var snap))
                return IdleTint;
            return ResolveCycledTeamColor(snap.TeamMask, IdleTint);
        }

        /// <summary>
        /// Fill tint + peak alpha for the world people-transfer ring.
        /// Occupied rings read a bit hotter so the team color is obvious.
        /// </summary>
        public static void ResolveFill(int planetId, float idlePeakAlpha,
            out Color tint, out float peakAlpha)
        {
            tint = ResolveRingTint(planetId);
            peakAlpha = TryGet(planetId, out _)
                ? Mathf.Clamp01(idlePeakAlpha * 1.45f)
                : idlePeakAlpha;
        }

        /// <summary>
        /// Picks the current team color from <paramref name="teamMask"/>.
        /// One team = solid. Several teams = 1s hold each, with a short blend.
        /// Uses unscaled time so world rings and the minimap stay in lockstep.
        /// </summary>
        public static Color ResolveCycledTeamColor(byte teamMask, Color idleTint)
        {
            int count = CountTeams(teamMask);
            if (count <= 0)
                return idleTint;
            if (count == 1)
                return TeamAtIndex(teamMask, 0).ToColor();

            float t = Time.unscaledTime / SecondsPerOccupyingTeam;
            if (t < 0f)
                t = 0f;
            int slot = (int)t;
            float frac = t - slot;
            Color current = TeamAtIndex(teamMask, Mod(slot, count)).ToColor();
            float blendStart = 1f - TeamBlendSeconds / SecondsPerOccupyingTeam;
            if (frac < blendStart)
                return current;

            Color next = TeamAtIndex(teamMask, Mod(slot + 1, count)).ToColor();
            float u = Mathf.InverseLerp(blendStart, 1f, frac);
            return Color.Lerp(current, next, u);
        }

        static int CountTeams(byte teamMask)
        {
            int count = 0;
            for (int i = 0; i < 5; i++)
            {
                if ((teamMask & (1 << i)) != 0)
                    count++;
            }

            return count;
        }

        static TeamId TeamAtIndex(byte teamMask, int index)
        {
            int seen = 0;
            for (int i = 0; i < 5; i++)
            {
                if ((teamMask & (1 << i)) == 0)
                    continue;
                if (seen == index)
                    return (TeamId)(i + 1);
                seen++;
            }

            return TeamId.None;
        }

        static int Mod(int value, int modulus)
        {
            if (modulus <= 0)
                return 0;
            int r = value % modulus;
            return r < 0 ? r + modulus : r;
        }
    }

    /// <summary>
    /// Client: walks ghosted ship orbit state and publishes
    /// <see cref="PlanetOrbitRingOccupancy"/> for planet ring fills and minimap rings.
    /// Skips ship gathers during join Instantiates.
    /// </summary>
    [WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation)]
    [UpdateInGroup(typeof(PresentationSystemGroup))]
    public partial class PlanetOrbitRingOccupancySystem : SystemBase
    {
        /// <summary>Drop occupancy when the client world is torn down.</summary>
        protected override void OnDestroy()
        {
            PlanetOrbitRingOccupancy.Clear();
        }

        /// <summary>
        /// [HYBRID] Presentation-only gather of locked-in hulls. Not sim.
        /// </summary>
        protected override void OnUpdate()
        {
            // [TITAN-ORBIT] Ship archetype gather — never during join Instantiates.
            if (ClientJoinSettleCache.ShouldSkipShipEntityQueries)
                return;

            PlanetOrbitRingOccupancy.BeginPublish();

            foreach (var (orbit, ship) in SystemAPI
                         .Query<RefRO<ShipOrbitState>, RefRO<ShipState>>()
                         .WithAll<ShipTag>())
            {
                if (ship.ValueRO.IsDead || ship.ValueRO.AwaitingTeamSelection)
                    continue;

                var o = orbit.ValueRO;
                // Positive lock only — ignore ships still flying into or blending onto the rail.
                if (!o.OrbitLocked || o.OrbitPlanetId == 0)
                    continue;

                PlanetOrbitRingOccupancy.AddLockedShip(o.OrbitPlanetId, ship.ValueRO.Team);
            }

            PlanetOrbitRingOccupancy.EndPublish();
        }
    }
}

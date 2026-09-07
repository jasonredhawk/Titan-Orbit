using System.Collections.Generic;
using TitanOrbit.Data;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.NetCode;

namespace TitanOrbit.ECS
{
    /// <summary>
    /// Match-start roll: every planet gets three MEGA hulls drawn from the armed catalog
    /// (firepower &gt; 0). Unarmed hulls stay in <see cref="MegaShipCatalog.entries"/> so
    /// designers can add weapons later; they are never assigned to planet slots.
    /// Draws without replacement across the match so the same hull is not assigned twice
    /// until the armed pool is exhausted. Called from <see cref="GameBootstrapSystem"/>.
    /// </summary>
    public static class MegaShipMatchRoll
    {
        /// <summary>
        /// Writes a 3-slot MEGA buffer onto every planet. Safe no-op when the catalog is
        /// empty or every hull is unarmed — slots stay unassigned (no 0-FP fallback).
        /// </summary>
        public static void AssignAllPlanets(EntityManager em, ref Random rng)
        {
            if (em.World != null && em.World.IsClient() && ClientJoinSettleCache.ShouldSkipMapBodyQueries)
                return;

            var catalog = MegaShipCatalog.Load();
            if (catalog == null || catalog.entries == null || catalog.entries.Count == 0)
            {
                UnityEngine.Debug.LogWarning(
                    "[MegaShipMatchRoll] MegaShipCatalog missing or empty — L7 MEGA slots stay unassigned.");
                return;
            }

            // --- Armed pool only ---
            // [TITAN-ORBIT] CollectAllIndices is the full designer list (previews / refresh).
            // Match slots must never receive a firepower-0 hull.
            var match = new List<ushort>(catalog.entries.Count);
            catalog.CollectMatchIndices(match);
            if (match.Count == 0)
            {
                UnityEngine.Debug.LogWarning(
                    "[MegaShipMatchRoll] No armed MEGA hulls (firepower > 0) — L7 slots stay unassigned. " +
                    "Unarmed hulls remain in MegaShipCatalog for designers.");
                return;
            }

            var pool = ToNative(match);
            var used = new NativeList<int>(pool.Length, Allocator.Temp);
            FillZeros(used, pool.Length);

            using var query = em.CreateEntityQuery(typeof(PlanetTag), typeof(PlanetState));
            using var planets = query.ToEntityArray(Allocator.Temp);
            int assigned = 0;
            for (int i = 0; i < planets.Length; i++)
            {
                var buffer = MegaShipPlanetLogic.EnsureSlots(em, planets[i]);
                ushort a = MegaShipPlanetLogic.DrawFromPool(pool, ref used, ref rng);
                ushort b = MegaShipPlanetLogic.DrawFromPool(pool, ref used, ref rng);
                ushort c = MegaShipPlanetLogic.DrawFromPool(pool, ref used, ref rng);
                MegaShipPlanetLogic.AssignRolledTrio(buffer, a, b, c);
                assigned++;
            }

            pool.Dispose();
            used.Dispose();

            UnityEngine.Debug.Log($"[MegaShipMatchRoll] Assigned MEGA trios to {assigned} planets.");
        }

        static NativeList<ushort> ToNative(List<ushort> source)
        {
            var list = new NativeList<ushort>(math.max(1, source.Count), Allocator.Temp);
            for (int i = 0; i < source.Count; i++)
                list.Add(source[i]);
            return list;
        }

        static void FillZeros(NativeList<int> list, int count)
        {
            list.Clear();
            for (int i = 0; i < count; i++)
                list.Add(0);
        }
    }
}

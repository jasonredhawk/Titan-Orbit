using System.Collections.Generic;
using Unity.Entities;

namespace TitanOrbit.Game
{
    /// <summary>
    /// [HYBRID] World-space gem diameters from live presentation proxies (updated each frame by EcsWorldVisualizer).
    /// Gem tractor beam systems read this for beam endpoint sizing.
    /// </summary>
    public static class GemVisualDiameterRegistry
    {
        static readonly Dictionary<Entity, float> DiameterByEntity = new Dictionary<Entity, float>(128);

        /// <summary>Records or removes diameter when gem proxy scale changes or entity despawns.</summary>
        public static void SetDiameter(Entity entity, float worldDiameter)
        {
            // --- Add/update or remove when diameter invalid ---
            if (worldDiameter <= 0.001f)
                DiameterByEntity.Remove(entity);
            else
                DiameterByEntity[entity] = worldDiameter;
        }

        /// <summary>Lookup world diameter for gem entity — false when proxy not yet created.</summary>
        public static bool TryGetDiameter(Entity entity, out float worldDiameter) =>
            DiameterByEntity.TryGetValue(entity, out worldDiameter);

        /// <summary>Removes entries for gems that despawned — called from EcsWorldVisualizer each LateUpdate.</summary>
        public static void RemoveStale(HashSet<Entity> alive)
        {
            // --- Prune despawned gems each LateUpdate ---
            if (DiameterByEntity.Count == 0)
                return;

            var stale = new List<Entity>(8);
            foreach (var kv in DiameterByEntity)
            {
                if (!alive.Contains(kv.Key))
                    stale.Add(kv.Key);
            }

            for (int i = 0; i < stale.Count; i++)
                DiameterByEntity.Remove(stale[i]);
        }

        /// <summary>Clears all entries — scene teardown or disconnect.</summary>
        public static void Clear() => DiameterByEntity.Clear();
    }
}

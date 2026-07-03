using System.Collections.Generic;
using Unity.Entities;

namespace TitanOrbit.Game
{
    /// <summary>World-space gem diameters from live presentation proxies (updated each frame).</summary>
    public static class GemVisualDiameterRegistry
    {
        static readonly Dictionary<Entity, float> DiameterByEntity = new Dictionary<Entity, float>(128);

        public static void SetDiameter(Entity entity, float worldDiameter)
        {
            if (worldDiameter <= 0.001f)
                DiameterByEntity.Remove(entity);
            else
                DiameterByEntity[entity] = worldDiameter;
        }

        public static bool TryGetDiameter(Entity entity, out float worldDiameter) =>
            DiameterByEntity.TryGetValue(entity, out worldDiameter);

        public static void RemoveStale(HashSet<Entity> alive)
        {
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

        public static void Clear() => DiameterByEntity.Clear();
    }
}

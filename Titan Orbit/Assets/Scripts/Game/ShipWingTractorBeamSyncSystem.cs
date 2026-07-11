using TitanOrbit.ECS;
using TitanOrbit.ECS.Authoring;
using Unity.Entities;
using Unity.Mathematics;
using Unity.NetCode;
using Unity.Transforms;
using UnityEngine;

namespace TitanOrbit.Game
{
    // --- Type members ---
    /// <summary>
    /// Copies wing tractor-beam authoring from ship visual hull proxies into ShipWingTractorBeamElement
    /// on ship ghosts. Runs on server and client so both worlds have wing local poses and stats.
    /// GemTractorBeamSystem (server) consumes the buffer for gem pull assignment. Hull lookup
    /// uses ShipWeaponProxyRegistry keyed by GhostOwner.NetworkId.
    /// </summary>
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    [UpdateAfter(typeof(ShipWeaponMountSyncSystem))]
    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation | WorldSystemFilterFlags.ClientSimulation)]
    public partial class ShipWingTractorBeamSyncSystem : SystemBase
    {
        protected override void OnUpdate()
        {
            foreach (var (owner, entity) in SystemAPI.Query<RefRO<GhostOwner>>()
                         .WithAll<ShipTag>()
                         .WithEntityAccess())
            {
                if (!ShipWeaponProxyRegistry.TryGetHull(owner.ValueRO.NetworkId, out var hullRoot))
                    continue;

                var wingAuthorings = hullRoot.GetComponentsInChildren<ShipWingTractorBeamAuthoring>(true);
                if (wingAuthorings == null || wingAuthorings.Length == 0)
                    continue;

                if (!EntityManager.HasBuffer<ShipWingTractorBeamElement>(entity))
                    EntityManager.AddBuffer<ShipWingTractorBeamElement>(entity);

                var buffer = EntityManager.GetBuffer<ShipWingTractorBeamElement>(entity);
                buffer.Clear();

                for (int i = 0; i < wingAuthorings.Length; i++)
                {
                    var wingAuth = wingAuthorings[i];
                    if (wingAuth == null || wingAuth.transform == hullRoot)
                        continue;

                    var wt = wingAuth.transform;
                    buffer.Add(new ShipWingTractorBeamElement
                    {
                        LocalPosition = wt.localPosition,
                        TractorBeamDistance = wingAuth.tractorBeamDistance,
                        TractorBeamDistancePerLevel = wingAuth.tractorBeamDistancePerLevel,
                        TractorBeamPower = wingAuth.tractorBeamPower,
                        TractorBeamPowerPerLevel = wingAuth.tractorBeamPowerPerLevel,
                        MaxGems = wingAuth.maxGems,
                        MaxGemsPerLevel = wingAuth.maxGemsPerLevel,
                    });
                }
            }
        }
    }
}

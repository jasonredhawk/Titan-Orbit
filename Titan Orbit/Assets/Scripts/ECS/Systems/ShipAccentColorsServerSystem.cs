using Unity.Collections;
using Unity.Entities;
using Unity.NetCode;

namespace TitanOrbit.ECS
{
    /// <summary>
    /// Server: accepts <see cref="SetShipAccentColorsCommand"/> and writes
    /// <see cref="ShipAccentColors"/> on the sender's ship ghost. Remotes see the
    /// palette through GhostField replication — no extra announce RPC.
    /// World: ServerSimulation. Group: SimulationSystemGroup.
    /// </summary>
    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    public partial struct ShipAccentColorsServerSystem : ISystem
    {
        /// <summary>Needs an in-game match singleton so we do not drain RPCs in an empty world.</summary>
        public void OnCreate(ref SystemState state)
        {
            state.RequireForUpdate<TeamStateSingleton>();
        }

        /// <summary>Drains accent RPCs and stamps the owned ship.</summary>
        public void OnUpdate(ref SystemState state)
        {
            var em = state.EntityManager;
            var ecb = new EntityCommandBuffer(Allocator.Temp);

            foreach (var (cmd, req, rpcEntity) in SystemAPI
                         .Query<RefRO<SetShipAccentColorsCommand>, RefRO<ReceiveRpcCommandRequest>>()
                         .WithEntityAccess())
            {
                ecb.DestroyEntity(rpcEntity);

                Entity connection = req.ValueRO.SourceConnection;
                if (!em.HasComponent<NetworkId>(connection))
                    continue;

                int networkId = em.GetComponentData<NetworkId>(connection).Value;
                if (networkId <= 0)
                    continue;

                if (!TryGetOwnedShip(em, networkId, out Entity ship))
                    continue;

                var accents = new ShipAccentColors
                {
                    HasCustom = cmd.ValueRO.HasCustom,
                    Color2Packed = cmd.ValueRO.Color2Packed,
                    Color3Packed = cmd.ValueRO.Color3Packed,
                    EmissionPacked = cmd.ValueRO.EmissionPacked,
                    Emission2Packed = cmd.ValueRO.Emission2Packed,
                    Emission3Packed = cmd.ValueRO.Emission3Packed,
                    ThrusterCustom = cmd.ValueRO.ThrusterCustom,
                    ThrusterStyle = cmd.ValueRO.ThrusterStyle,
                    ThrusterColorPacked = cmd.ValueRO.ThrusterColorPacked,
                    ThrusterFollowTeam = cmd.ValueRO.ThrusterFollowTeam,
                    ThrusterLifeCustom = cmd.ValueRO.ThrusterLifeCustom,
                    ThrusterLife0Packed = cmd.ValueRO.ThrusterLife0Packed,
                    ThrusterLife1Packed = cmd.ValueRO.ThrusterLife1Packed,
                    ThrusterLife2Packed = cmd.ValueRO.ThrusterLife2Packed,
                    ThrusterLife3Packed = cmd.ValueRO.ThrusterLife3Packed,
                };

                if (em.HasComponent<ShipAccentColors>(ship))
                    em.SetComponentData(ship, accents);
                else
                    em.AddComponentData(ship, accents);
            }

            ecb.Playback(em);
            ecb.Dispose();
        }

        /// <summary>Finds the ship ghost owned by this connection's NetworkId.</summary>
        static bool TryGetOwnedShip(EntityManager em, int networkId, out Entity shipEntity)
        {
            shipEntity = Entity.Null;
            if (networkId <= 0)
                return false;

            using var query = em.CreateEntityQuery(typeof(ShipTag), typeof(GhostOwner));
            using var owners = query.ToComponentDataArray<GhostOwner>(Allocator.Temp);
            using var entities = query.ToEntityArray(Allocator.Temp);
            for (int i = 0; i < owners.Length; i++)
            {
                if (owners[i].NetworkId != networkId)
                    continue;
                shipEntity = entities[i];
                return true;
            }

            return false;
        }
    }
}

using TitanOrbit.Core;
using Unity.Collections;
using Unity.Entities;
using Unity.NetCode;

namespace TitanOrbit.ECS
{
    /// <summary>
    /// Client: applies <see cref="SessionShipOfferRpc"/> so the continue / start-fresh
    /// screen can open before a hull ghost exists. World: ClientSimulation.
    /// </summary>
    [WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation)]
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    public partial struct SessionShipOfferClientSystem : ISystem
    {
        public void OnUpdate(ref SystemState state)
        {
            var ecb = new EntityCommandBuffer(Allocator.Temp);
            foreach (var (offer, entity) in SystemAPI.Query<RefRO<SessionShipOfferRpc>>().WithEntityAccess())
            {
                Apply(offer.ValueRO);
                ecb.DestroyEntity(entity);
            }

            ecb.Playback(state.EntityManager);
            ecb.Dispose();
        }

        [Unity.Burst.BurstDiscard]
        static void Apply(SessionShipOfferRpc rpc)
        {
            SessionShipOfferCache.Set(rpc.Ship);
            ClientTeamFlowState.TryNotifyRejoinableShip(true);
            UnityEngine.Debug.Log(
                "[SessionShipOffer] Saved ship available team=" + rpc.Ship.Team +
                " level=" + rpc.Ship.ShipLevel + ".");
        }
    }
}

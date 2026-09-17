using Unity.Collections;
using Unity.Entities;
using Unity.NetCode;
using Unity.Transforms;

namespace TitanOrbit.ECS
{
    /// <summary>
    /// Client: drains <see cref="ShipCommsRpc"/> into <see cref="ShipCommsInbox"/>.
    /// The GameObject presenter (<c>ShipCommsBubblePresenter</c>) paints chips above the
    /// speaker's hull — this system never Instantiates UI.
    /// <para>
    /// World: ClientSimulation. Group: SimulationSystemGroup. Paired with
    /// <see cref="ShipCommsServerSystem"/>. Team-only rows only arrive when this
    /// connection is on the speaker's team (the server already filtered). Incoming
    /// rows are dropped while the local hull is jammed in enemy territory so a
    /// late RPC cannot paint chips the player should not hear.
    /// </para>
    /// </summary>
    [WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation)]
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    public partial struct ShipCommsRpcClientSystem : ISystem
    {
        /// <summary>
        /// Copies each inbound callout into the process-wide inbox, then destroys the RPC entity.
        /// Jammed local hulls still consume the RPC so it cannot linger, but they do not enqueue.
        /// </summary>
        public void OnUpdate(ref SystemState state)
        {
            var ecb = new EntityCommandBuffer(Allocator.Temp);

            // --- Local jam snapshot ---
            // One pose read for this tick. Server already skipped jammed listeners;
            // this drop covers Local Host races and a hull that entered fill after send.
            // [ECS/DOTS] SystemAPI.Query stays in OnUpdate so the source generator can
            // rewrite it (static helpers do not get that rewrite).
            bool localJammed = false;
            // [NETCODE] GhostOwnerIsLocal — enableable tag on the connection-owned ghost.
            foreach (var (transform, ship) in SystemAPI
                         .Query<RefRO<LocalTransform>, RefRO<ShipState>>()
                         .WithAll<ShipTag, GhostOwnerIsLocal>())
            {
                if (ship.ValueRO.IsDead || ship.ValueRO.AwaitingTeamSelection)
                    break;

                localJammed = ShipCommsJam.IsPositionJammed(
                    transform.ValueRO.Position,
                    ship.ValueRO.Team,
                    PlanetConnectionGraphSide.Client);
                break;
            }

            // --- Drain inbound RPCs ---
            // [NETCODE] ReceiveRpcCommandRequest marks inbound RPC entities from the network.
            foreach (var (rpc, entity) in SystemAPI
                         .Query<RefRO<ShipCommsRpc>>()
                         .WithAll<ReceiveRpcCommandRequest>()
                         .WithEntityAccess())
            {
                // Always consume — a jammed drop must not leave the RPC entity forever.
                ecb.DestroyEntity(entity);
                if (localJammed)
                    continue;

                ShipCommsRpc row = rpc.ValueRO;
                ShipCommsInbox.Enqueue(new ShipCommsInbox.Callout
                {
                    NetworkId = row.NetworkId,
                    Count = row.Count,
                    K0 = row.K0,
                    K1 = row.K1,
                    K2 = row.K2,
                    K3 = row.K3,
                    K4 = row.K4,
                    TeamOnly = row.TeamOnly,
                    HasWaypoint = row.HasWaypoint,
                    WaypointX = row.WaypointX,
                    WaypointZ = row.WaypointZ,
                    FocusKind = row.FocusKind,
                    YouNetworkId = row.YouNetworkId,
                    PlanetId = row.PlanetId,
                    Everyone = row.Everyone,
                    Us0 = row.Us0,
                    Us1 = row.Us1,
                    Us2 = row.Us2,
                    Us3 = row.Us3,
                    MeX = row.MeX,
                    MeZ = row.MeZ,
                    YouX = row.YouX,
                    YouZ = row.YouZ,
                    GroupCount = row.GroupCount,
                    G0X = row.G0X, G0Z = row.G0Z,
                    G1X = row.G1X, G1Z = row.G1Z,
                    G2X = row.G2X, G2Z = row.G2Z,
                    G3X = row.G3X, G3Z = row.G3Z,
                    G4X = row.G4X, G4Z = row.G4Z,
                    G5X = row.G5X, G5Z = row.G5Z,
                    G6X = row.G6X, G6Z = row.G6Z,
                    G7X = row.G7X, G7Z = row.G7Z,
                });
            }

            ecb.Playback(state.EntityManager);
            ecb.Dispose();
        }
    }
}

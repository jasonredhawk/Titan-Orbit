using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.NetCode;

namespace TitanOrbit.ECS
{
    /// <summary>
    /// [NETCODE] Applies <see cref="ViewInterestCommand"/> onto each server connection, then
    /// publishes <see cref="ViewInterestLookup"/> for relevancy and combat fan-out.
    /// Runs first in the server sim tick so later systems see this frame's rectangles.
    /// Until the client reports a camera, the rectangle is a modest box around
    /// <see cref="GhostConnectionPosition"/> (the owned ship).
    /// Map width and height come from <see cref="MapStateSingleton"/>.
    /// </summary>
    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
    [UpdateInGroup(typeof(SimulationSystemGroup), OrderFirst = true)]
    public partial struct ViewInterestServerSystem : ISystem
    {
        /// <summary>In-game connections that do not yet have a view component.</summary>
        EntityQuery _missingViewQuery;

        /// <summary>Camera RPCs waiting on the server. Empty most ticks, so we skip the lists.</summary>
        EntityQuery _reportQuery;

        /// <summary>Caches the queries for camera RPCs and connections that still need a default rectangle.</summary>
        public void OnCreate(ref SystemState state)
        {
            _missingViewQuery = state.GetEntityQuery(
                ComponentType.ReadOnly<NetworkStreamInGame>(),
                ComponentType.ReadOnly<NetworkId>(),
                ComponentType.Exclude<ConnectionViewInterest>());
            _reportQuery = state.GetEntityQuery(
                ComponentType.ReadOnly<ViewInterestCommand>(),
                ComponentType.ReadOnly<ReceiveRpcCommandRequest>());
        }

        /// <summary>
        /// Writes camera reports, fills defaults, then copies every view into the lookup.
        /// </summary>
        public void OnUpdate(ref SystemState state)
        {
            var em = state.EntityManager;

            // --- Client camera reports ---
            // Lists exist only on ticks that actually received a report (camera moved or heartbeat).
            if (!_reportQuery.IsEmptyIgnoreFilter)
            {
                var reports = new NativeList<PendingReport>(4, Allocator.Temp);
                var destroy = new NativeList<Entity>(4, Allocator.Temp);
                foreach (var (cmd, req, rpcEntity) in SystemAPI
                             .Query<RefRO<ViewInterestCommand>, RefRO<ReceiveRpcCommandRequest>>()
                             .WithEntityAccess())
                {
                    destroy.Add(rpcEntity);
                    Entity connection = req.ValueRO.SourceConnection;
                    if (!em.Exists(connection) || !em.HasComponent<NetworkId>(connection))
                        continue;

                    reports.Add(new PendingReport
                    {
                        Connection = connection,
                        Command = cmd.ValueRO,
                    });
                }

                for (int i = 0; i < destroy.Length; i++)
                {
                    if (em.Exists(destroy[i]))
                        em.DestroyEntity(destroy[i]);
                }

                for (int i = 0; i < reports.Length; i++)
                    ApplyReport(em, reports[i].Connection, reports[i].Command);

                reports.Dispose();
                destroy.Dispose();
            }

            // --- First-tick default around the ship ---
            if (!_missingViewQuery.IsEmptyIgnoreFilter)
            {
                var defaultEcb = new EntityCommandBuffer(Allocator.Temp);
                using var connections = _missingViewQuery.ToEntityArray(Allocator.Temp);
                for (int i = 0; i < connections.Length; i++)
                    defaultEcb.AddComponent(connections[i], MakeDefault(em, connections[i]));
                defaultEcb.Playback(em);
                defaultEcb.Dispose();
            }

            // --- Publish the cache combat systems read ---
            float mapW = 0f;
            float mapH = 0f;
            if (SystemAPI.TryGetSingleton<MapStateSingleton>(out var map))
            {
                mapW = map.MapWidth;
                mapH = map.MapHeight;
            }

            ViewInterestLookup.BeginFrame(mapW, mapH);
            foreach (var (view, id, entity) in SystemAPI
                         .Query<RefRO<ConnectionViewInterest>, RefRO<NetworkId>>()
                         .WithAll<NetworkStreamInGame>()
                         .WithEntityAccess())
            {
                ConnectionViewInterest v = view.ValueRO;
                ViewInterestLookup.Add(new ViewInterestSlot
                {
                    Connection = entity,
                    NetworkId = id.ValueRO.Value,
                    CenterX = v.CenterX,
                    CenterZ = v.CenterZ,
                    HalfW = v.HalfW,
                    HalfH = v.HalfH,
                    FullMap = v.FullMap,
                });
            }
        }

        /// <summary>One camera RPC pulled out of the query before we touch connection components.</summary>
        struct PendingReport
        {
            /// <summary>Server connection that sent the camera rectangle.</summary>
            public Entity Connection;

            /// <summary>The rectangle and full-map flag.</summary>
            public ViewInterestCommand Command;
        }

        /// <summary>
        /// Stores a client rectangle. Marks <see cref="ConnectionViewInterest.ViewMoved"/> when the
        /// rect changed enough that gems entering the new area need a catch-up.
        /// </summary>
        static void ApplyReport(EntityManager em, Entity connection, in ViewInterestCommand cmd)
        {
            bool existed = em.HasComponent<ConnectionViewInterest>(connection);
            ConnectionViewInterest next = existed
                ? em.GetComponentData<ConnectionViewInterest>(connection)
                : MakeDefault(em, connection);

            float halfW = math.max(8f, cmd.HalfWidth);
            float halfH = math.max(8f, cmd.HalfHeight);
            bool moved = next.HasReport == 0
                || math.abs(next.CenterX - cmd.Center.x) > 4f
                || math.abs(next.CenterZ - cmd.Center.z) > 4f
                || math.abs(next.HalfW - halfW) > 1f
                || math.abs(next.HalfH - halfH) > 1f;

            if (next.HasReport != 0 && moved)
            {
                next.PrevCenterX = next.CenterX;
                next.PrevCenterZ = next.CenterZ;
                next.PrevHalfW = next.HalfW;
                next.PrevHalfH = next.HalfH;
                next.HasPrev = 1;
                next.ViewMoved = 1;
            }
            else if (next.HasReport == 0)
            {
                // First real camera: previous rect is the spawn fallback, so catch-up can fill the gap.
                next.HasPrev = 1;
                next.ViewMoved = 1;
            }

            next.CenterX = cmd.Center.x;
            next.CenterZ = cmd.Center.z;
            next.HalfW = halfW;
            next.HalfH = halfH;
            next.FullMap = cmd.FullMap;
            next.HasReport = 1;

            if (existed)
                em.SetComponentData(connection, next);
            else
                em.AddComponentData(connection, next);
        }

        /// <summary>Box around the owned ship until a camera report exists.</summary>
        static ConnectionViewInterest MakeDefault(EntityManager em, Entity connection)
        {
            float3 pos = float3.zero;
            if (em.HasComponent<GhostConnectionPosition>(connection))
                pos = em.GetComponentData<GhostConnectionPosition>(connection).Position;

            return new ConnectionViewInterest
            {
                CenterX = pos.x,
                CenterZ = pos.z,
                HalfW = ViewInterestTuning.DefaultHalfExtent,
                HalfH = ViewInterestTuning.DefaultHalfExtent,
                PrevCenterX = pos.x,
                PrevCenterZ = pos.z,
                PrevHalfW = ViewInterestTuning.DefaultHalfExtent,
                PrevHalfH = ViewInterestTuning.DefaultHalfExtent,
                HasPrev = 1,
            };
        }
    }
}

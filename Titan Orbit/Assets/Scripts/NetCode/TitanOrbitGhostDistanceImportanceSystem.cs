using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.NetCode;
using Unity.Transforms;

namespace TitanOrbit.NetCode
{
    /// <summary>
    /// [NETCODE] Enables distance-based ghost importance scaling on the server so late-join
    /// snapshots prefer nearby / high-importance ghosts (ships) over far static map chunks.
    /// <para>
    /// Without this, <see cref="TitanOrbitGhostSendTuneSystem"/> can still deliver one dense
    /// asteroid archetype chunk per tick (dozens–100+ Instantiates on the client). Partitioning
    /// also fragments large asteroid archetypes into smaller spatial chunks, which pairs with
    /// MaxSendChunks=1 to stream the map gradually.
    /// </para>
    /// World: ServerSimulation. Group: InitializationSystemGroup (once at startup).
    /// </summary>
    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
    [UpdateInGroup(typeof(InitializationSystemGroup))]
    [UpdateAfter(typeof(TitanOrbitGhostSendTuneSystem))]
    public partial struct TitanOrbitGhostDistanceImportanceBootstrapSystem : ISystem
    {
        /// <summary>
        /// Tile size in world units for <see cref="GhostDistanceData"/>.
        /// Large enough for space flight; small enough to split dense asteroid fields.
        /// </summary>
        public const int TileSizeWorld = 512;

        /// <summary>True after GhostImportance + GhostDistanceData singletons exist.</summary>
        bool _bootstrapped;

        /// <summary>Requires GhostSendSystemData so NetCode send pipeline is ready.</summary>
        public void OnCreate(ref SystemState state)
        {
            // --- Dependencies ---
            // [NETCODE] GhostSendSystemData is created by NetCode's GhostSend bootstrap.
            state.RequireForUpdate<GhostSendSystemData>();
        }

        /// <summary>
        /// Creates GhostDistanceData + GhostImportance once. GhostDistancePartitioningSystem
        /// then auto-tags server ghosts with GhostDistancePartitionShared.
        /// </summary>
        public void OnUpdate(ref SystemState state)
        {
            if (_bootstrapped)
                return;

            // --- Already configured? ---
            // [STANDARD] Avoid duplicate singletons if a previous play mode left state (editor).
            if (SystemAPI.HasSingleton<GhostImportance>() && SystemAPI.HasSingleton<GhostDistanceData>())
            {
                _bootstrapped = true;
                return;
            }

            // --- Spatial grid config ---
            // [NETCODE] GhostDistanceData drives GhostDistancePartitioningSystem tile assignment.
            var gridEntity = state.EntityManager.CreateSingleton(new GhostDistanceData
            {
                TileSize = new int3(TileSizeWorld, TileSizeWorld, TileSizeWorld),
                TileCenter = new int3(0, 0, 0),
                TileBorderWidth = new float3(8f, 8f, 8f),
            });

            // --- Importance scale function ---
            // [NETCODE] BatchScaleFunctionPointer downscales far chunks per connection position.
            state.EntityManager.AddComponentData(gridEntity, new GhostImportance
            {
                BatchScaleImportanceFunction = GhostDistanceImportance.BatchScaleFunctionPointer,
                GhostConnectionComponentType = ComponentType.ReadOnly<GhostConnectionPosition>(),
                GhostImportanceDataType = ComponentType.ReadOnly<GhostDistanceData>(),
                GhostImportancePerChunkDataType = ComponentType.ReadOnly<GhostDistancePartitionShared>(),
            });

            _bootstrapped = true;
            UnityEngine.Debug.Log(
                "[TitanOrbitGhostSend] Distance importance enabled: TileSize=" + TileSizeWorld +
                " (streams far map ghosts after nearby / high-importance chunks).");
        }
    }

    /// <summary>
    /// [NETCODE] Keeps each in-game connection's <see cref="GhostConnectionPosition"/> aligned
    /// with that player's ship so distance importance knows what "near" means for that client.
    /// Also adds the component when missing (GoInGame does not add it by default).
    /// World: ServerSimulation. Group: SimulationSystemGroup.
    /// </summary>
    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    public partial struct TitanOrbitGhostConnectionPositionSystem : ISystem
    {
        /// <summary>Requires send pipeline + distance config before updating positions.</summary>
        public void OnCreate(ref SystemState state)
        {
            state.RequireForUpdate<GhostDistanceData>();
            state.RequireForUpdate<GhostImportance>();
        }

        /// <summary>
        /// Ensures GhostConnectionPosition exists on in-game connections and copies ship position
        /// from CommandTarget when available.
        /// </summary>
        public void OnUpdate(ref SystemState state)
        {
            var em = state.EntityManager;
            var ecb = new EntityCommandBuffer(Allocator.Temp);

            // --- Ensure component on in-game connections ---
            // [NETCODE] GhostSend only runs distance scaling when the connection has this component.
            foreach (var (_, entity) in SystemAPI.Query<RefRO<NetworkStreamInGame>>()
                         .WithAll<NetworkId>()
                         .WithNone<GhostConnectionPosition>()
                         .WithEntityAccess())
            {
                ecb.AddComponent(entity, new GhostConnectionPosition
                {
                    Position = float3.zero,
                    Rotation = quaternion.identity,
                });
            }

            ecb.Playback(em);
            ecb.Dispose();

            // --- Follow CommandTarget ship ---
            // [NETCODE] CommandTarget on the connection points at the player's ship ghost.
            foreach (var (conPos, cmd) in SystemAPI
                         .Query<RefRW<GhostConnectionPosition>, RefRO<CommandTarget>>()
                         .WithAll<NetworkStreamInGame>())
            {
                Entity ship = cmd.ValueRO.targetEntity;
                if (ship == Entity.Null || !em.Exists(ship) || !em.HasComponent<LocalTransform>(ship))
                    continue;

                conPos.ValueRW.Position = em.GetComponentData<LocalTransform>(ship).Position;
            }
        }
    }
}

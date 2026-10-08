using TitanOrbit.ECS;
using TitanOrbit.Generation;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.NetCode;
using Unity.Transforms;

namespace TitanOrbit.NetCode
{
    /// <summary>
    /// Rebuilds <see cref="GhostRelevancy.GhostRelevancySet"/> each tick under
    /// <see cref="GhostRelevancyMode.SetIsRelevant"/>.
    /// <para>
    /// Planets stay always-relevant through <see cref="TitanOrbitDynamicGhostRelevancySystem"/>'s
    /// default query (there are few of them). Ships are relevant only for their owner, or when
    /// the hull overlaps that connection's camera rectangle on the torus. A ship already on screen
    /// uses a wider margin than a ship coming into view, so hulls do not pop at the edge.
    /// </para>
    /// Map width and height come from <see cref="MapStateSingleton"/>. When the map size is not
    /// ready yet, every ship stays relevant so join is not a blank world.
    /// World: ServerSimulation. Group: SimulationSystemGroup, before <see cref="GhostSendSystem"/>.
    /// </summary>
    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    [UpdateBefore(typeof(GhostSendSystem))]
    public partial struct TitanOrbitGemGhostRelevancySystem : ISystem
    {
        struct ShipRow
        {
            public int GhostId;
            public int OwnerNetworkId;
            public float3 Position;
        }

        NativeList<ShipRow> _ships;
        NativeParallelHashMap<RelevantGhostForConnection, byte> _relevantNow;
        NativeParallelHashMap<RelevantGhostForConnection, byte> _relevantPrev;

        /// <summary>Allocates the scratch ship list and the two hysteresis maps.</summary>
        public void OnCreate(ref SystemState state)
        {
            state.RequireForUpdate<GhostRelevancy>();
            _ships = new NativeList<ShipRow>(64, Allocator.Persistent);
            _relevantNow = new NativeParallelHashMap<RelevantGhostForConnection, byte>(256, Allocator.Persistent);
            _relevantPrev = new NativeParallelHashMap<RelevantGhostForConnection, byte>(256, Allocator.Persistent);
        }

        /// <summary>Releases the persistent containers.</summary>
        public void OnDestroy(ref SystemState state)
        {
            if (_ships.IsCreated)
                _ships.Dispose();
            if (_relevantNow.IsCreated)
                _relevantNow.Dispose();
            if (_relevantPrev.IsCreated)
                _relevantPrev.Dispose();
        }

        /// <summary>
        /// Writes owner ships and in-view ships into the relevancy set. Planets are not listed
        /// here; the default query already replicates them to every connection.
        /// </summary>
        public void OnUpdate(ref SystemState state)
        {
            ref var relevancy = ref SystemAPI.GetSingletonRW<GhostRelevancy>().ValueRW;
            var set = relevancy.GhostRelevancySet;
            set.Clear();

            float mapW = 0f;
            float mapH = 0f;
            bool mapOk = false;
            if (SystemAPI.TryGetSingleton<MapStateSingleton>(out var map))
            {
                mapW = map.MapWidth;
                mapH = map.MapHeight;
                mapOk = ToroidalMapEcs.IsValidMapSize(mapW, mapH);
            }

            // --- One pass over ships into a reused list (no managed array copy) ---
            _ships.Clear();
            foreach (var (ghost, owner, transform) in SystemAPI
                         .Query<RefRO<GhostInstance>, RefRO<GhostOwner>, RefRO<LocalTransform>>()
                         .WithAll<ShipTag>())
            {
                int ghostId = ghost.ValueRO.ghostId;
                if (ghostId == 0)
                    continue;

                _ships.Add(new ShipRow
                {
                    GhostId = ghostId,
                    OwnerNetworkId = owner.ValueRO.NetworkId,
                    Position = transform.ValueRO.Position,
                });
            }

            _relevantNow.Clear();
            // ViewInterestServerSystem (OrderFirst) already put a rectangle on every in-game
            // connection, so this loop does not touch EntityManager mid-iteration.
            foreach (var (id, view) in SystemAPI
                         .Query<RefRO<NetworkId>, RefRO<ConnectionViewInterest>>()
                         .WithAll<NetworkStreamInGame>())
            {
                int connectionId = id.ValueRO.Value;
                if (connectionId <= 0)
                    continue;

                ConnectionViewInterest cam = view.ValueRO;
                for (int i = 0; i < _ships.Length; i++)
                {
                    ShipRow ship = _ships[i];
                    var key = new RelevantGhostForConnection(connectionId, ship.GhostId);
                    bool was = _relevantPrev.ContainsKey(key);
                    bool owner = ship.OwnerNetworkId == connectionId;
                    bool inside = owner;
                    if (!inside)
                    {
                        // Map size not rolled yet: everyone still sees every ship so join is not blank.
                        if (!mapOk)
                        {
                            inside = true;
                        }
                        else
                        {
                            float margin = was ? ViewInterestTuning.ExitMargin : ViewInterestTuning.EnterMargin;
                            inside = ViewInterestMath.PointInView(
                                cam.CenterX,
                                cam.CenterZ,
                                cam.HalfW,
                                cam.HalfH,
                                ship.Position,
                                mapW,
                                mapH,
                                margin);
                        }
                    }

                    if (!inside)
                        continue;

                    set.TryAdd(key, 1);
                    _relevantNow.TryAdd(key, 1);
                }
            }

            // Previous frame becomes "was relevant" for the next tick's wider exit margin.
            var swap = _relevantPrev;
            _relevantPrev = _relevantNow;
            _relevantNow = swap;
        }
    }
}

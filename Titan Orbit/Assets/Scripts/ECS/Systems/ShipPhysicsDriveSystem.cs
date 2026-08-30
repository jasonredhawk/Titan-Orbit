using TitanOrbit.Core;
using TitanOrbit.Data;
using TitanOrbit.Diagnostics;
using TitanOrbit.Generation;
using TitanOrbit.Simulation;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.NetCode;
using Unity.Physics;
using Unity.Transforms;

namespace TitanOrbit.ECS
{
    /// <summary>
    /// Shared predicted ship motor — one system, both worlds. Schedules
    /// <see cref="ShipPhysicsDriveJob"/> before Unity Physics so thrust/turn/orbit write
    /// <see cref="PhysicsVelocity"/>, then the solver integrates pose and resolves hull contacts.
    /// <para>
    /// [NETCODE] Official NCE practice: write the motor once and run it inside
    /// <see cref="PredictedFixedStepSimulationSystemGroup"/> on Server + Client. Ships are
    /// Predicted by default (not OwnerPredicted). The server steps every hull; each client
    /// keeps only the local ship Predicted (`ShipClientPredictionSwitchSystem`) so remotes
    /// do not run this motor. Queries still use <see cref="PredictedGhost"/> + <see cref="Simulate"/>.
    /// </para>
    /// Client join-settle skip stays (<see cref="ClientJoinSettleCache.ShouldSkipShipSimulation"/>).
    /// Under TransformQuarantine the client collects planets from the Instantiates registry
    /// (no ToEntityArray Crash!!!). Server always uses the full archetype collect.
    /// Pipeline: Input → MassSync → Drive (this) → Physics → Planar → Wrap → KinematicsSync.
    /// </summary>
    [UpdateInGroup(typeof(PredictedFixedStepSimulationSystemGroup), OrderFirst = true)]
    [UpdateAfter(typeof(ShipPhysicsMassSyncSystem))]
    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation | WorldSystemFilterFlags.ClientSimulation)]
    public partial struct ShipPhysicsDriveSystem : ISystem
    {
        /// <summary>Require at least one ship motor config before scheduling the drive job.</summary>
        static double s_NextAgentLogRealtime;

        public void OnCreate(ref SystemState state)
        {
            state.RequireForUpdate<ShipMotorConfig>();
        }

        /// <summary>
        /// Collects shared orbit/territory context, then schedules the Burst drive job
        /// for every predicted simulated ship.
        /// </summary>
        public void OnUpdate(ref SystemState state)
        {
            bool client = state.World.IsClient();

            // --- Join-crash gate (client only) ---
            // [TITAN-ORBIT] ScheduleParallel over fresh ship archetypes Crash!!!'d during
            // TeamChoice Instantiates. ShouldSkipShipSimulation covers that window.
            // Do NOT gate on ShouldSkipShipEntityQueries — map Instantiates keep
            // GhostSpawnBacklog true after proxy-ready Join Team and froze thrust.
            if (client && ClientJoinSettleCache.ShouldSkipShipSimulation)
                return;

            // --- Map size (same source both worlds) ---
            // [TITAN-ORBIT] Prefer ToroidalMapEcs (MapSessionMeta on dedicated clients —
            // MapStateSingleton often missing). Missing size → skip this tick (never invent 1000).
            float preferredW = 0f;
            float preferredH = 0f;
            if (SystemAPI.TryGetSingleton(out MapStateSingleton mapState) &&
                ToroidalMapEcs.IsValidMapSize(mapState.MapWidth, mapState.MapHeight))
            {
                preferredW = mapState.MapWidth;
                preferredH = mapState.MapHeight;
            }

            if (!ToroidalMapEcs.ResolveMapSize(preferredW, preferredH, out float mapW, out float mapH))
                return;
            if (ToroidalMapEcs.IsValidMapSize(preferredW, preferredH))
                ToroidalMapEcs.SetMapSize(mapW, mapH);

            // --- Planet snapshots ---
            // [TITAN-ORBIT] Client TransformQuarantine is session-long after join. Full Collect
            // (ToEntityArray) Crash!!! on Windows — Instantiates registry Collect is safe.
            NativeList<PlanetMotorSnapshot> planets;
            if (client && (ClientJoinSettleCache.TransformQuarantine || ClientJoinSettleCache.Settling))
                planets = PlanetMotorSnapshotCollection.CollectFromClientRegistry(ref state, Allocator.TempJob);
            else
                planets = PlanetMotorSnapshotCollection.Collect(ref state, Allocator.TempJob);

            // --- Moon orbit clock (must match collider sync — not World.ElapsedTime) ---
            int hz = 0;
            if (SystemAPI.TryGetSingleton<ClientServerTickRate>(out var tickRate))
                hz = tickRate.SimulationTickRate;
            double moonElapsed = SystemAPI.TryGetSingleton<NetworkTime>(out var networkTime)
                ? PlanetGemMoonOrbitClock.GetElapsedSeconds(networkTime, hz, includeTickFraction: false)
                : SystemAPI.Time.ElapsedTime;

            // --- Territory triangles (Persistent native — do not Dispose) ---
            var side = client ? PlanetConnectionGraphSide.Client : PlanetConnectionGraphSide.Server;
            var territory = PlanetConnectionGraphCache.GetRuntimeTrianglesNative(
                side,
                planets.AsArray(),
                moonElapsed);
            var homeLevels = new NativeArray<int>(6, Allocator.TempJob);
            PlanetConnectionGraphCache.CopyHomeLevels(side, ref homeLevels);

            // --- Client presentation thruster / HUD mult (first predicting tick only) ---
            // [NETCODE] Rollback/resim re-runs this system; writing LocalOwnerTerritoryMult every
            // resim tick made engine/thruster meshes blink while inside a triangle.
            // SystemAPI.Query / TryGetSingleton must stay in OnUpdate (not a static helper).
            if (client)
            {
                bool publishMult = !SystemAPI.TryGetSingleton<NetworkTime>(out var nt) ||
                                   nt.IsFirstTimeFullyPredictingTick;
                if (publishMult)
                {
                    float localMult = 1f;
                    bool foundLocalShip = false;
                    foreach (var (lt, ship) in SystemAPI
                                 .Query<RefRO<LocalTransform>, RefRO<ShipState>>()
                                 .WithAll<ShipTag, GhostOwnerIsLocal, Simulate>())
                    {
                        localMult = PlanetConnectionGraphLogic.FriendlyTerritoryMovementMultiplier(
                            lt.ValueRO.Position,
                            ship.ValueRO.Team,
                            territory,
                            homeLevels,
                            mapW,
                            mapH);
                        foundLocalShip = true;
                        break;
                    }

                    if (foundLocalShip)
                        PlanetConnectionGraphCache.UpdateLocalOwnerTerritoryMult(localMult, moonElapsed);

                    // #region agent log
                    if (foundLocalShip &&
                        UnityEngine.Time.realtimeSinceStartupAsDouble >= s_NextAgentLogRealtime)
                    {
                        s_NextAgentLogRealtime = UnityEngine.Time.realtimeSinceStartupAsDouble + 2.0;
                        float speed = 0f;
                        bool thrust = false;
                        bool predicted = false;
                        foreach (var (pv, input, entity) in SystemAPI
                                     .Query<RefRO<PhysicsVelocity>, RefRO<ShipInput>>()
                                     .WithAll<ShipTag, GhostOwnerIsLocal, Simulate>()
                                     .WithEntityAccess())
                        {
                            speed = math.length(pv.ValueRO.Linear);
                            thrust = input.ValueRO.Thrust;
                            predicted = state.EntityManager.HasComponent<PredictedGhost>(entity);
                            break;
                        }

                        AgentDebugNdjson.Write(
                            "D",
                            "ShipPhysicsDriveSystem.cs:OnUpdate",
                            "local predicted motor",
                            "{\"speed\":" + speed.ToString("F2") +
                            ",\"thrust\":" + (thrust ? "true" : "false") +
                            ",\"predictedGhost\":" + (predicted ? "true" : "false") +
                            ",\"dt\":" + SystemAPI.Time.DeltaTime.ToString("F4") + "}");
                    }
                    // #endregion
                }
            }

            ShipCargoMobilitySettings mobility = ShipCargoMobilitySettingsCache.ResolveOrDefault();

            // [NETCODE] Fixed-step dt from PredictedFixedStepSimulationSystemGroup — not frame delta.
            var job = new ShipPhysicsDriveJob
            {
                Dt = SystemAPI.Time.DeltaTime,
                Elapsed = moonElapsed,
                MapW = mapW,
                MapH = mapH,
                Planets = planets.AsArray(),
                TerritoryTriangles = territory,
                HomeLevelByTeam = homeLevels,
                MassPerGem = mobility.massPerGem,
                MassPerPerson = mobility.massPerPerson,
                MassPerComponentSize = mobility.massPerComponentSize,
                SpeedWeightPerMass = mobility.speedWeightPerMass,
                AccelWeightPerMass = mobility.accelWeightPerMass,
                TurnWeightPerMass = mobility.turnWeightPerMass,
                MinSpeed = mobility.minSpeed,
                MinAccel = mobility.minAccel,
                MinTurn = mobility.minTurn,
            };
            state.Dependency = job.ScheduleParallel(state.Dependency);
            state.Dependency = planets.Dispose(state.Dependency);
            state.Dependency = homeLevels.Dispose(state.Dependency);
        }
    }
}

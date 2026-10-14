#if UNITY_WEBGL && !UNITY_EDITOR
using TitanOrbit.Diagnostics;
using Unity.Entities;
using Unity.NetCode;
using Unity.Physics.Systems;

namespace TitanOrbit.ECS
{
    /// <summary>
    /// Samples native heap growth at simulation-group boundaries. WebGL player only.
    /// One profiler read per boundary, no entity scans.
    /// </summary>
    [WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation)]
    [UpdateInGroup(typeof(SimulationSystemGroup), OrderFirst = true)]
    [UpdateBefore(typeof(GhostSimulationSystemGroup))]
    public partial struct WebGlSimProbeStartSystem : ISystem
    {
        public void OnUpdate(ref SystemState state)
        {
            WebGlAllocBuckets.SimCursorReset();
        }
    }

    [WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation)]
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    [UpdateAfter(typeof(GhostSimulationSystemGroup))]
    [UpdateBefore(typeof(PredictedSimulationSystemGroup))]
    public partial struct WebGlSimProbeAfterGhostSystem : ISystem
    {
        public void OnUpdate(ref SystemState state)
        {
            WebGlAllocBuckets.SimCursorAdd(ref WebGlAllocBuckets.SimGhostAlloc);
        }
    }

    [WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation)]
    [UpdateInGroup(typeof(PhysicsSystemGroup), OrderFirst = true)]
    public partial struct WebGlPhysProbeStartSystem : ISystem
    {
        public void OnUpdate(ref SystemState state)
        {
            WebGlAllocBuckets.SimCursorAdd(ref WebGlAllocBuckets.SimPredAlloc);
        }
    }

    [WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation)]
    [UpdateInGroup(typeof(PhysicsSystemGroup))]
    [UpdateAfter(typeof(PhysicsInitializeGroup))]
    [UpdateBefore(typeof(PhysicsSimulationGroup))]
    public partial struct WebGlPhysProbeAfterInitSystem : ISystem
    {
        public void OnUpdate(ref SystemState state)
        {
            WebGlAllocBuckets.SimCursorAdd(ref WebGlAllocBuckets.SimPhysInitAlloc);
        }
    }

    [WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation)]
    [UpdateInGroup(typeof(PhysicsSimulationGroup))]
    [UpdateAfter(typeof(PhysicsCreateBodyPairsGroup))]
    [UpdateBefore(typeof(PhysicsCreateContactsGroup))]
    public partial struct WebGlPhysProbeAfterPairsSystem : ISystem
    {
        public void OnUpdate(ref SystemState state)
        {
            WebGlAllocBuckets.SimCursorAdd(ref WebGlAllocBuckets.SimPhysPairsAlloc);
        }
    }

    [WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation)]
    [UpdateInGroup(typeof(PhysicsSimulationGroup))]
    [UpdateAfter(typeof(PhysicsCreateContactsGroup))]
    [UpdateBefore(typeof(PhysicsCreateJacobiansGroup))]
    public partial struct WebGlPhysProbeAfterContactsSystem : ISystem
    {
        public void OnUpdate(ref SystemState state)
        {
            WebGlAllocBuckets.SimCursorAdd(ref WebGlAllocBuckets.SimPhysContactsAlloc);
        }
    }

    [WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation)]
    [UpdateInGroup(typeof(PhysicsSimulationGroup))]
    [UpdateAfter(typeof(PhysicsCreateJacobiansGroup))]
    [UpdateBefore(typeof(PhysicsSolveAndIntegrateGroup))]
    public partial struct WebGlPhysProbeBeforeSolveSystem : ISystem
    {
        public void OnUpdate(ref SystemState state)
        {
            WebGlAllocBuckets.SimCursorAdd(ref WebGlAllocBuckets.SimPhysJacAlloc);
        }
    }

    [WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation)]
    [UpdateInGroup(typeof(PhysicsSimulationGroup))]
    [UpdateAfter(typeof(PhysicsSolveAndIntegrateGroup))]
    public partial struct WebGlPhysProbeAfterSolveSystem : ISystem
    {
        public void OnUpdate(ref SystemState state)
        {
            WebGlAllocBuckets.SimCursorAdd(ref WebGlAllocBuckets.SimPhysSolveOnlyAlloc);
        }
    }

    [WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation)]
    [UpdateInGroup(typeof(PhysicsSystemGroup), OrderLast = true)]
    public partial struct WebGlPhysProbeEndSystem : ISystem
    {
        public void OnUpdate(ref SystemState state)
        {
            WebGlAllocBuckets.SimCursorAdd(ref WebGlAllocBuckets.SimPhysAfterAlloc);
            WebGlAllocBuckets.SimPhysSteps++;
        }
    }

    [WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation)]
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    [UpdateAfter(typeof(PredictedSimulationSystemGroup))]
    public partial struct WebGlSimProbeAfterPredictedSystem : ISystem
    {
        public void OnUpdate(ref SystemState state)
        {
            WebGlAllocBuckets.SimCursorAdd(ref WebGlAllocBuckets.SimPredAlloc);
        }
    }

    [WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation)]
    [UpdateInGroup(typeof(SimulationSystemGroup), OrderLast = true)]
    public partial struct WebGlSimProbeEndSystem : ISystem
    {
        public void OnUpdate(ref SystemState state)
        {
            WebGlAllocBuckets.SimCursorAdd(ref WebGlAllocBuckets.SimRestAlloc);
        }
    }
}
#endif

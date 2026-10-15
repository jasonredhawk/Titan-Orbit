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
    [UpdateInGroup(typeof(PhysicsBuildWorldGroup))]
    [UpdateBefore(typeof(BuildPhysicsWorld))]
    public partial struct WebGlPhysProbeBeforeBuildSystem : ISystem
    {
        public void OnUpdate(ref SystemState state)
        {
            WebGlAllocBuckets.SimCursorAdd(ref WebGlAllocBuckets.SimPhysPreBuildAlloc);
        }
    }

    [WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation)]
    [UpdateInGroup(typeof(PhysicsBuildWorldGroup))]
    [UpdateAfter(typeof(BuildPhysicsWorld))]
    public partial struct WebGlPhysProbeAfterBuildSystem : ISystem
    {
        long _reset;
        long _mid;
        long _stream;
        long _copy;
        long _tree;
        long _head;
        long _data;
        long _count;
        long _cohere;
        long _cohereDyn;
        long _cohereSta;
        long _cohereCombine;
        long _dispose;
        long _epilogue;
        long _listDispose;
        long _singleton;

        public void OnUpdate(ref SystemState state)
        {
            WebGlAllocBuckets.SimCursorAdd(ref WebGlAllocBuckets.SimPhysWorldAlloc);
            long reset = PhysicsWorldBuilder.WebGlResetBytes;
            long mid = PhysicsWorldBuilder.WebGlMidBytes;
            long stream = PhysicsWorldBuilder.WebGlStreamBytes;
            long copy = PhysicsWorldBuilder.WebGlCopyBytes;
            long tree = PhysicsWorldBuilder.WebGlTreeBytes;
            long head = PhysicsWorldBuilder.WebGlHeadBytes;
            long data = PhysicsWorldBuilder.WebGlDataBytes;
            long count = PhysicsWorldBuilder.WebGlCountBytes;
            long cohere = PhysicsWorldBuilder.WebGlCohereBytes;
            long cohereDyn = PhysicsWorldBuilder.WebGlCohereDynBytes;
            long cohereSta = PhysicsWorldBuilder.WebGlCohereStaBytes;
            long cohereCombine = PhysicsWorldBuilder.WebGlCohereCombineBytes;
            long dispose = PhysicsWorldBuilder.WebGlDisposeBytes;
            long epilogue = PhysicsWorldBuilder.WebGlEpilogueBytes;
            long listDispose = PhysicsWorldBuilder.WebGlListDisposeBytes;
            long singleton = PhysicsWorldBuilder.WebGlSingletonBytes;
            WebGlAllocBuckets.PhysResetAlloc += reset - _reset;
            WebGlAllocBuckets.PhysMidAlloc += mid - _mid;
            WebGlAllocBuckets.PhysStreamAlloc += stream - _stream;
            WebGlAllocBuckets.PhysCopyAlloc += copy - _copy;
            WebGlAllocBuckets.PhysTreeAlloc += tree - _tree;
            WebGlAllocBuckets.PhysHeadAlloc += head - _head;
            WebGlAllocBuckets.PhysDataAlloc += data - _data;
            WebGlAllocBuckets.PhysCountAlloc += count - _count;
            WebGlAllocBuckets.PhysCohereAlloc += cohere - _cohere;
            WebGlAllocBuckets.PhysCohereDynAlloc += cohereDyn - _cohereDyn;
            WebGlAllocBuckets.PhysCohereStaAlloc += cohereSta - _cohereSta;
            WebGlAllocBuckets.PhysCohereCombineAlloc += cohereCombine - _cohereCombine;
            WebGlAllocBuckets.PhysDisposeAlloc += dispose - _dispose;
            WebGlAllocBuckets.PhysEpilogueAlloc += epilogue - _epilogue;
            WebGlAllocBuckets.PhysListDisposeAlloc += listDispose - _listDispose;
            WebGlAllocBuckets.PhysSingletonAlloc += singleton - _singleton;
            _reset = reset;
            _mid = mid;
            _stream = stream;
            _copy = copy;
            _tree = tree;
            _head = head;
            _data = data;
            _count = count;
            _cohere = cohere;
            _cohereDyn = cohereDyn;
            _cohereSta = cohereSta;
            _cohereCombine = cohereCombine;
            _dispose = dispose;
            _epilogue = epilogue;
            _listDispose = listDispose;
            _singleton = singleton;
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

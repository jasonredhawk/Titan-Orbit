using Unity.Entities;
using Unity.Mathematics;
using Unity.NetCode;

namespace TitanOrbit.NetCode
{
    /// <summary>
    /// Client clock repair: interpolated ghosts must stay behind the predict target.
    /// <para>
    /// e2d7d2 switch-v5: both players' remotes yaw-fought. Logs showed
    /// <c>ServerTick.TicksSince(InterpolationTick)</c> at -80 (host) to -380 (P2).
    /// Package docs require InterpolationTick &lt; ServerTick. When interp runs ahead,
    /// NCE extrapolates rotation from stale angular velocity — heading flips every frame.
    /// Join can advance interp while <c>RequirePredictedGhost</c> stalls ServerTick; NCE
    /// only jumps interp <b>forward</b>, so a runaway never recovers.
    /// </para>
    /// World: ClientSimulation. After <see cref="NetworkTimeSystem"/> and IPC tandem.
    /// Lives in TitanOrbit.NetCode so it can <c>UpdateAfter</c> the IPC system.
    /// </summary>
    [WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation)]
    [UpdateInGroup(typeof(InitializationSystemGroup))]
    [UpdateAfter(typeof(NetworkTimeSystem))]
    [UpdateAfter(typeof(TitanOrbitIpcLocalHostTimeSyncSystem))]
    public partial struct TitanOrbitInterpolationClockClampSystem : ISystem
    {
        /// <summary>Need an in-game connection and the NetCode time singleton.</summary>
        public void OnCreate(ref SystemState state)
        {
            state.RequireForUpdate<NetworkStreamInGame>();
            state.RequireForUpdate<NetworkTimeSystemData>();
        }

        /// <summary>Snaps interpolateTarget back when it is ahead of predictTarget or the latest snapshot.</summary>
        public void OnUpdate(ref SystemState state)
        {
            if (!SystemAPI.TryGetSingletonRW<NetworkTimeSystemData>(out var netTimeRw))
                return;

            ref var netTimeData = ref netTimeRw.ValueRW;
            if (!netTimeData.predictTargetTick.IsValid || !netTimeData.interpolateTargetTick.IsValid)
                return;

            uint interpDelay = 2;
            if (SystemAPI.TryGetSingleton<ClientTickRate>(out var clientTickRate) &&
                clientTickRate.InterpolationTimeNetTicks > 0)
                interpDelay = clientTickRate.InterpolationTimeNetTicks;

            int interpAheadOfPredict = netTimeData.interpolateTargetTick.TicksSince(netTimeData.predictTargetTick);
            int interpAheadOfSnap = 0;
            bool haveSnap = netTimeData.latestSnapshot.IsValid;
            if (haveSnap)
                interpAheadOfSnap = netTimeData.interpolateTargetTick.TicksSince(netTimeData.latestSnapshot);

            if (interpAheadOfPredict > 0 || interpAheadOfSnap > 0)
            {
                NetworkTick idealInterp = haveSnap ? netTimeData.latestSnapshot : netTimeData.predictTargetTick;
                if (interpDelay > 0)
                    idealInterp.Subtract(interpDelay);
                netTimeData.interpolateTargetTick = idealInterp;
                netTimeData.subInterpolateTargetTick = 0f;
                netTimeData.currentInterpolationFrames = math.max(
                    netTimeData.currentInterpolationFrames, interpDelay);
                if (haveSnap &&
                    netTimeData.latestSnapshotEstimate.IsValid &&
                    netTimeData.latestSnapshotEstimate.TicksSince(netTimeData.latestSnapshot) > 2)
                {
                    netTimeData.latestSnapshotEstimate = netTimeData.latestSnapshot;
                    netTimeData.latestSnapshotAge = 0;
                }

            }
        }
    }
}

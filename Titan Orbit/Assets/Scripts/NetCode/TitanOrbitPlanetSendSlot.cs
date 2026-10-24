using System;
using AOT;
using TitanOrbit.ECS;
using Unity.Burst;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Entities;
using Unity.Mathematics;
using Unity.NetCode;
using Unity.NetCode.LowLevel.Unsafe;

namespace TitanOrbit.NetCode
{
    /// <summary>
    /// [NETCODE] Distance-scales ghost chunks, then reserves one send slot for the planet tile
    /// nearest the player. AI ships are importance 100 and always relevant, so they used to fill
    /// all <see cref="TitanOrbitGhostSendTuneSystem.MaxSendChunksPerSnapshot"/> slots and the
    /// planet buffer (turret levels) never left the server. Combat does not read this.
    /// </summary>
    [BurstCompile]
    public struct TitanOrbitPlanetSendSlot
    {
        /// <summary>Installed on <see cref="GhostImportance.BatchScaleImportanceFunction"/>.</summary>
        public static readonly PortableFunctionPointer<GhostImportance.BatchScaleImportanceDelegate>
            BatchScaleFunctionPointer =
                new PortableFunctionPointer<GhostImportance.BatchScaleImportanceDelegate>(BatchScale);

        [BurstCompile(DisableDirectCall = true)]
        [MonoPInvokeCallback(typeof(GhostImportance.BatchScaleImportanceDelegate))]
        static unsafe void BatchScale(
            IntPtr connectionDataPtr,
            IntPtr distanceDataPtr,
            IntPtr sharedComponentTypeHandlePtr,
            ref UnsafeList<PrioChunk> chunks)
        {
            var distanceData = GhostComponentSerializer.TypeCast<GhostDistanceData>(distanceDataPtr);
            var centerTile = (int3)((GhostComponentSerializer.TypeCast<GhostConnectionPosition>(connectionDataPtr).Position
                                     - distanceData.TileCenter) / distanceData.TileSize);
            var sharedType = GhostComponentSerializer.TypeCast<DynamicSharedComponentTypeHandle>(sharedComponentTypeHandlePtr);

            int bestPlanet = -1;
            int bestDist = int.MaxValue;
            int bestPriority = int.MinValue;

            for (int i = 0; i < chunks.Length; ++i)
            {
                ref var data = ref chunks.ElementAt(i);
                int distSq = int.MaxValue;
                if (data.chunk.Has(ref sharedType))
                {
                    var chunkTile = (GhostDistancePartitionShared*)data.chunk.GetDynamicSharedComponentDataAddress(ref sharedType);
                    var delta = chunkTile->Index - centerTile;
                    distSq = math.dot(delta, delta);
                    data.priority = GhostDistanceImportance.CalculateDefaultScaledPriority(
                        data.priority, in *chunkTile, centerTile);
                }

                // PrioChunk.ghostType is internal. PlanetTag is on the planet ghost only.
                if (!data.chunk.Has<PlanetTag>())
                    continue;

                // Nearest tile wins the reserved slot. Same tile: the chunk that has waited longest.
                bool nearer = distSq < bestDist;
                bool tie = distSq == bestDist && data.priority > bestPriority;
                if (bestPlanet < 0 || nearer || tie)
                {
                    bestPlanet = i;
                    bestDist = distSq;
                    bestPriority = data.priority;
                }
            }

            if (bestPlanet < 0)
                return;

            int topOther = 0;
            for (int i = 0; i < chunks.Length; ++i)
            {
                if (i == bestPlanet)
                    continue;
                int priority = chunks.ElementAt(i).priority;
                if (priority > topOther)
                    topOther = priority;
            }

            // Sorts this planet chunk first. The other three MaxSendChunks slots stay
            // with ships, gems, and transports.
            ref var reserved = ref chunks.ElementAt(bestPlanet);
            if (topOther < int.MaxValue && reserved.priority <= topOther)
                reserved.priority = topOther + 1;
        }
    }
}

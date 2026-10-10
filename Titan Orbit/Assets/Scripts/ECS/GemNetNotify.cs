using TitanOrbit.Simulation;
using Unity.Entities;
using Unity.Mathematics;
using Unity.NetCode;

namespace TitanOrbit.ECS
{
    /// <summary>
    /// Server → client notify helpers for event-hydrated gems (spawn / burst / consume / value /
    /// tractor / catch-up). Spawn, burst, consume, value, and tractor go only to connections
    /// whose camera can see the crystal. Catch-up stays targeted at one connection.
    /// Clients drop crystals that leave the view; the view-enter catch-up puts live ones back.
    /// </summary>
    public static class GemNetNotify
    {
        /// <summary>Sends one <see cref="GemSpawnRpc"/> to cameras that can see the crystal.</summary>
        public static void SendSpawn(ref EntityCommandBuffer ecb, in GemSpawnRecipe recipe)
        {
            float3 pos = recipe.Position;
            pos.y = 0f;
            ViewInterestFanout.EmitPoint(ref ecb, ToSpawnRpc(recipe, pos), pos, ViewInterestTuning.GemKeepMargin, 0);
        }

        /// <summary>Sends one asteroid-destroy burst to cameras that can see the origin.</summary>
        public static void SendBurst(
            ref EntityCommandBuffer ecb,
            float3 origin,
            float remainingValue,
            uint seed,
            float spawnServerTime,
            GemVisualTint tint)
        {
            origin.y = 0f;
            var rpc = new GemBurstRpc
            {
                Origin = origin,
                RemainingValue = remainingValue,
                Seed = seed,
                SpawnServerTime = spawnServerTime,
                IsBonus = (byte)tint,
            };
            ViewInterestFanout.EmitPoint(ref ecb, rpc, origin, ViewInterestTuning.GemKeepMargin, 0);
        }

        /// <summary>Tells viewers to hide a scooped or expired crystal.</summary>
        public static void SendConsumed(ref EntityCommandBuffer ecb, int spawnId, float3 position)
        {
            if (spawnId == 0)
                return;

            position.y = 0f;
            ViewInterestFanout.EmitPoint(ref ecb, new GemConsumedRpc { SpawnId = spawnId }, position, ViewInterestTuning.GemKeepMargin, 0);
        }

        /// <summary>Tells viewers the leftover value after a partial scoop.</summary>
        public static void SendValueChanged(ref EntityCommandBuffer ecb, int spawnId, float remainingValue, float3 position)
        {
            if (spawnId == 0)
                return;

            position.y = 0f;
            ViewInterestFanout.EmitPoint(
                ref ecb,
                new GemValueChangedRpc { SpawnId = spawnId, RemainingValue = remainingValue },
                position,
                ViewInterestTuning.GemKeepMargin,
                0);
        }

        /// <summary>Sends a tractor lock or unlock to cameras that can see the crystal.</summary>
        public static void SendTractorLock(ref EntityCommandBuffer ecb, in GemTractorLockRpc rpc, float3 position)
        {
            if (rpc.SpawnId == 0)
                return;

            position.y = 0f;
            ViewInterestFanout.EmitPoint(ref ecb, rpc, position, ViewInterestTuning.GemKeepMargin, rpc.TractorShipId);
        }

        /// <summary>Sends one live-gem snapshot to a joining connection.</summary>
        public static void SendCatchUp(
            ref EntityCommandBuffer ecb,
            Entity connection,
            in GemCatchUpRpc rpc)
        {
            if (connection == Entity.Null || rpc.SpawnId == 0)
                return;

            Entity rpcEntity = ecb.CreateEntity();
            ecb.AddComponent(rpcEntity, rpc);
            ecb.AddComponent(rpcEntity, new SendRpcCommandRequest { TargetConnection = connection });
        }

        /// <summary>Maps a recipe to the spawn RPC (shared by broadcast and tests).</summary>
        public static GemSpawnRpc ToSpawnRpc(in GemSpawnRecipe recipe, float3 positionY0)
        {
            return new GemSpawnRpc
            {
                SpawnId = GemSpawnMath.ComputeSpawnId(recipe),
                Position = positionY0,
                Value = recipe.Value,
                Salt = recipe.Salt,
                SpawnServerTime = recipe.SpawnServerTime,
                Flags = recipe.Flags,
                BurstIndex = recipe.BurstIndex,
                BurstIntensity = recipe.BurstIntensity,
                ExcludePickupNetworkId = recipe.ExcludePickupNetworkId,
                ExcludePickupUntilServerTime = recipe.ExcludePickupUntilServerTime,
                LaunchDir = recipe.LaunchDir,
                AddVelocity = recipe.AddVelocity,
                LaunchSpeedMul = recipe.LaunchSpeedMul <= 0f ? 1f : recipe.LaunchSpeedMul,
            };
        }

        /// <summary>Rebuilds a recipe from an inbound spawn RPC.</summary>
        public static GemSpawnRecipe ToRecipe(in GemSpawnRpc rpc)
        {
            return new GemSpawnRecipe
            {
                Position = rpc.Position,
                Value = rpc.Value,
                SpawnIdOverride = rpc.SpawnId,
                Salt = rpc.Salt,
                SpawnServerTime = rpc.SpawnServerTime,
                BurstIndex = rpc.BurstIndex,
                Flags = rpc.Flags,
                BurstIntensity = rpc.BurstIntensity,
                ExcludePickupNetworkId = rpc.ExcludePickupNetworkId,
                ExcludePickupUntilServerTime = rpc.ExcludePickupUntilServerTime,
                LaunchDir = rpc.LaunchDir,
                AddVelocity = rpc.AddVelocity,
                LaunchSpeedMul = rpc.LaunchSpeedMul <= 0f ? 1f : rpc.LaunchSpeedMul,
            };
        }
    }
}

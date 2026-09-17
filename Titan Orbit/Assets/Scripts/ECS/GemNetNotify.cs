using TitanOrbit.Simulation;
using Unity.Entities;
using Unity.Mathematics;
using Unity.NetCode;

namespace TitanOrbit.ECS
{
    /// <summary>
    /// Server → client notify helpers for event-hydrated gems (spawn / burst / consume / value /
    /// tractor / catch-up). Mirrors <see cref="BulletNetNotify"/>: broadcast RPC with
    /// <see cref="SendRpcCommandRequest.TargetConnection"/> null, or a targeted connection.
    /// </summary>
    public static class GemNetNotify
    {
        /// <summary>Broadcasts one <see cref="GemSpawnRpc"/> from a resolved recipe.</summary>
        public static void SendSpawn(ref EntityCommandBuffer ecb, in GemSpawnRecipe recipe)
        {
            float3 pos = recipe.Position;
            pos.y = 0f;
            Entity rpcEntity = ecb.CreateEntity();
            ecb.AddComponent(rpcEntity, ToSpawnRpc(recipe, pos));
            ecb.AddComponent(rpcEntity, new SendRpcCommandRequest { TargetConnection = Entity.Null });
        }

        /// <summary>Broadcasts one asteroid-destroy burst (client expands chord recipes locally).</summary>
        public static void SendBurst(
            ref EntityCommandBuffer ecb,
            float3 origin,
            float remainingValue,
            uint seed,
            float spawnServerTime,
            GemVisualTint tint)
        {
            origin.y = 0f;
            Entity rpcEntity = ecb.CreateEntity();
            ecb.AddComponent(rpcEntity, new GemBurstRpc
            {
                Origin = origin,
                RemainingValue = remainingValue,
                Seed = seed,
                SpawnServerTime = spawnServerTime,
                IsBonus = (byte)tint,
            });
            ecb.AddComponent(rpcEntity, new SendRpcCommandRequest { TargetConnection = Entity.Null });
        }

        /// <summary>Broadcasts a full scoop so clients hide and destroy the local crystal.</summary>
        public static void SendConsumed(ref EntityCommandBuffer ecb, int spawnId)
        {
            if (spawnId == 0)
                return;

            Entity rpcEntity = ecb.CreateEntity();
            ecb.AddComponent(rpcEntity, new GemConsumedRpc { SpawnId = spawnId });
            ecb.AddComponent(rpcEntity, new SendRpcCommandRequest { TargetConnection = Entity.Null });
        }

        /// <summary>Broadcasts a leftover value after a partial scoop.</summary>
        public static void SendValueChanged(ref EntityCommandBuffer ecb, int spawnId, float remainingValue)
        {
            if (spawnId == 0)
                return;

            Entity rpcEntity = ecb.CreateEntity();
            ecb.AddComponent(rpcEntity, new GemValueChangedRpc
            {
                SpawnId = spawnId,
                RemainingValue = remainingValue,
            });
            ecb.AddComponent(rpcEntity, new SendRpcCommandRequest { TargetConnection = Entity.Null });
        }

        /// <summary>Broadcasts a tractor lock or unlock (TractorShipId 0 = unlock).</summary>
        public static void SendTractorLock(ref EntityCommandBuffer ecb, in GemTractorLockRpc rpc)
        {
            if (rpc.SpawnId == 0)
                return;

            Entity rpcEntity = ecb.CreateEntity();
            ecb.AddComponent(rpcEntity, rpc);
            ecb.AddComponent(rpcEntity, new SendRpcCommandRequest { TargetConnection = Entity.Null });
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

using Unity.Entities;
using Unity.Mathematics;
using Unity.NetCode;

namespace TitanOrbit.ECS
{
    /// <summary>
    /// Server → client notify helpers for bullet VFX (spawn + hit).
    /// Mirrors <see cref="PeopleTransportNetNotify"/>: in-process bridge for host + broadcast RPC.
    /// </summary>
    public static class BulletNetNotify
    {
        /// <summary>
        /// Broadcasts a spawn and mirrors into the host VFX bridge when ClientWorld exists.
        /// </summary>
        /// <param name="ecb">Server ECB for the broadcast RPC entity.</param>
        /// <param name="bullet">Authoritative bullet just spawned (or same-frame hit).</param>
        /// <param name="mountIndex">Weapon mount index for local muzzle reproject (volley-aware).</param>
        public static void SendSpawn(
            ref EntityCommandBuffer ecb,
            in BulletElement bullet,
            int mountIndex = 0)
        {
            if (bullet.Sequence == 0)
                return;

            // Keep muzzle Y from the weapon mount; flatten flight to XZ (top-down).
            float3 spawnPos = bullet.Position;
            float3 velocity = bullet.Velocity;
            velocity.y = 0f;
            int safeMount = mountIndex; // Keep -1 for drone / world spawns (no barrel reproject).

            var req = new BulletVfxBridge.SpawnRequest
            {
                Sequence = bullet.Sequence,
                SpawnPosition = spawnPos,
                Velocity = velocity,
                Lifetime = bullet.Lifetime,
                MaxDistance = bullet.MaxDistance,
                Damage = bullet.Damage,
                OwnerTeam = bullet.OwnerTeam,
                OwnerNetworkId = bullet.OwnerNetworkId,
                BankIndex = bullet.BankIndex,
                ScaleMultiplier = bullet.ScaleMultiplier > 0f ? bullet.ScaleMultiplier : 1f,
                MountIndex = safeMount,
                IsAnticipation = false,
                IsDisplaySpace = false,
                DamageFilter = (byte)bullet.DamageFilter,
            };

            // --- Host in-process (Editor / listen-server) ---
            if (ClientServerBootstrap.ClientWorld != null && ClientServerBootstrap.ClientWorld.IsCreated)
                BulletVfxBridge.TryEnqueueSpawn(req);

            // --- All remote clients (+ host client connection) ---
            Entity rpcEntity = ecb.CreateEntity();
            ecb.AddComponent(rpcEntity, new BulletSpawnRpc
            {
                Sequence = bullet.Sequence,
                SpawnPosition = spawnPos,
                Velocity = velocity,
                Lifetime = bullet.Lifetime,
                MaxDistance = bullet.MaxDistance,
                Damage = bullet.Damage,
                OwnerTeam = bullet.OwnerTeam,
                OwnerNetworkId = bullet.OwnerNetworkId,
                BankIndex = bullet.BankIndex,
                ScaleMultiplier = bullet.ScaleMultiplier > 0f ? bullet.ScaleMultiplier : 1f,
                MountIndex = safeMount,
                DamageFilter = (byte)bullet.DamageFilter,
            });
            ecb.AddComponent(rpcEntity, new SendRpcCommandRequest { TargetConnection = Entity.Null });
        }

        /// <summary>
        /// Broadcasts an impact and mirrors into the host VFX bridge when ClientWorld exists.
        /// </summary>
        /// <param name="ecb">Server ECB for the broadcast RPC entity.</param>
        /// <param name="bullet">Authoritative bullet that scored the hit.</param>
        /// <param name="hitPosition">World impact point (logical XZ).</param>
        /// <param name="asteroidHealthAfter">
        /// Asteroid Health after damage, or &lt; 0 when the hit was not an asteroid.
        /// </param>
        public static void SendHit(
            ref EntityCommandBuffer ecb,
            in BulletElement bullet,
            float3 hitPosition,
            float asteroidHealthAfter = -1f)
        {
            if (bullet.Sequence == 0)
                return;

            hitPosition.y = 0f;
            var req = new BulletVfxBridge.HitRequest
            {
                Sequence = bullet.Sequence,
                HitPosition = hitPosition,
                Damage = bullet.Damage,
                OwnerTeam = bullet.OwnerTeam,
                BankIndex = bullet.BankIndex,
                ScaleMultiplier = bullet.ScaleMultiplier > 0f ? bullet.ScaleMultiplier : 1f,
                AsteroidHealthAfter = asteroidHealthAfter,
            };

            if (ClientServerBootstrap.ClientWorld != null && ClientServerBootstrap.ClientWorld.IsCreated)
                BulletVfxBridge.EnqueueHit(req);

            Entity rpcEntity = ecb.CreateEntity();
            ecb.AddComponent(rpcEntity, new BulletHitRpc
            {
                Sequence = bullet.Sequence,
                HitPosition = hitPosition,
                Damage = bullet.Damage,
                OwnerTeam = bullet.OwnerTeam,
                BankIndex = bullet.BankIndex,
                ScaleMultiplier = bullet.ScaleMultiplier > 0f ? bullet.ScaleMultiplier : 1f,
                AsteroidHealthAfter = asteroidHealthAfter,
            });
            ecb.AddComponent(rpcEntity, new SendRpcCommandRequest { TargetConnection = Entity.Null });
        }
    }
}

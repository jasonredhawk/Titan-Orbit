using Unity.Entities;
using Unity.Mathematics;
using Unity.NetCode;

namespace TitanOrbit.ECS
{
    /// <summary>
    /// Server → client notify helpers for bullet VFX (spawn + hit) and ram/grind explosions
    /// (Sequence 0 <see cref="BulletHitRpc"/>).
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
                Homing = bullet.Homing,
                TurnSpeedDeg = bullet.TurnSpeedDeg,
                AcquireRange = bullet.AcquireRange,
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
                Homing = bullet.Homing,
                TurnSpeedDeg = bullet.TurnSpeedDeg,
                AcquireRange = bullet.AcquireRange,
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
        /// <param name="planetaryDefensePlanetId">
        /// Stable planet id when a PD turret was damaged; 0 otherwise.
        /// </param>
        /// <param name="planetaryDefenseSlotIndex">Slot index when PlanetId &gt; 0.</param>
        /// <param name="planetaryDefenseHealthAfter">
        /// Turret Health after damage (0 = destroyed); ignored when PlanetId is 0.
        /// </param>
        public static void SendHit(
            ref EntityCommandBuffer ecb,
            in BulletElement bullet,
            float3 hitPosition,
            float asteroidHealthAfter = -1f,
            int planetaryDefensePlanetId = 0,
            byte planetaryDefenseSlotIndex = 0,
            float planetaryDefenseHealthAfter = -1f)
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
                PlanetaryDefensePlanetId = planetaryDefensePlanetId,
                PlanetaryDefenseSlotIndex = planetaryDefenseSlotIndex,
                PlanetaryDefenseHealthAfter = planetaryDefenseHealthAfter,
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
                PlanetaryDefensePlanetId = planetaryDefensePlanetId,
                PlanetaryDefenseSlotIndex = planetaryDefenseSlotIndex,
                PlanetaryDefenseHealthAfter = planetaryDefenseHealthAfter,
            });
            ecb.AddComponent(rpcEntity, new SendRpcCommandRequest { TargetConnection = Entity.Null });
        }

        /// <summary>
        /// [TITAN-ORBIT] Broadcasts a ram / grind asteroid impact using the existing
        /// <see cref="BulletHitRpc"/> wire layout (no new RPC type — Linux headless stays compatible).
        /// <c>Sequence = 0</c> means there is no tracer to adopt; clients play the ship's bullet
        /// explosion and apply <paramref name="asteroidHealthAfter"/> onto the seed-hydrated rock.
        /// </summary>
        /// <param name="ecb">Server ECB for the broadcast RPC entity.</param>
        /// <param name="hitPosition">Contact point on the asteroid surface (logical XZ).</param>
        /// <param name="damage">Asteroid damage this impact or grind pulse (VFX intensity).</param>
        /// <param name="ownerTeam">Ramming ship's team as a byte.</param>
        /// <param name="bankIndex">
        /// <c>ShipLoadoutState.RuntimeBulletIndex</c> — the explosion prefab that ship currently fires.
        /// </param>
        /// <param name="scaleMultiplier">Visual size from ram damage (kill frames are larger).</param>
        /// <param name="asteroidHealthAfter">Health after this pulse; 0 means the rock died.</param>
        public static void SendRamAsteroidHit(
            ref EntityCommandBuffer ecb,
            float3 hitPosition,
            float damage,
            byte ownerTeam,
            int bankIndex,
            float scaleMultiplier,
            float asteroidHealthAfter)
        {
            // --- Flatten to the play plane ---
            // [TITAN-ORBIT] Combat and display are XZ; Y is unused on the torus.
            hitPosition.y = 0f;
            float safeScale = scaleMultiplier > 0f ? scaleMultiplier : 1f;
            int safeBank = math.max(0, bankIndex);

            var req = new BulletVfxBridge.HitRequest
            {
                Sequence = 0,
                HitPosition = hitPosition,
                Damage = damage,
                OwnerTeam = ownerTeam,
                BankIndex = safeBank,
                ScaleMultiplier = safeScale,
                AsteroidHealthAfter = asteroidHealthAfter,
                PlanetaryDefensePlanetId = 0,
                PlanetaryDefenseSlotIndex = 0,
                PlanetaryDefenseHealthAfter = -1f,
            };

            // --- Host in-process (Editor / listen-server) ---
            // Sequence 0 is allowed here; BulletVfxBridge dedupes host+RPC by pose this frame.
            if (ClientServerBootstrap.ClientWorld != null && ClientServerBootstrap.ClientWorld.IsCreated)
                BulletVfxBridge.EnqueueHit(req);

            // --- All clients (including the host connection) ---
            // [NETCODE] TargetConnection Null = broadcast. Wire fields match BulletHitRpc exactly.
            Entity rpcEntity = ecb.CreateEntity();
            ecb.AddComponent(rpcEntity, new BulletHitRpc
            {
                Sequence = 0,
                HitPosition = hitPosition,
                Damage = damage,
                OwnerTeam = ownerTeam,
                BankIndex = safeBank,
                ScaleMultiplier = safeScale,
                AsteroidHealthAfter = asteroidHealthAfter,
                PlanetaryDefensePlanetId = 0,
                PlanetaryDefenseSlotIndex = 0,
                PlanetaryDefenseHealthAfter = -1f,
            });
            ecb.AddComponent(rpcEntity, new SendRpcCommandRequest { TargetConnection = Entity.Null });
        }
    }
}

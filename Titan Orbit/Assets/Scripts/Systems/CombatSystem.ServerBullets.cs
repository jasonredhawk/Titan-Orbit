using System.Collections.Generic;
using UnityEngine;
using Unity.Netcode;
using TitanOrbit.Audio;
using TitanOrbit.Core;
using TitanOrbit.Data;
using TitanOrbit.Entities;
using TitanOrbit.Generation;
using TitanOrbit.Networking;

namespace TitanOrbit.Systems
{
    /// <summary>
    /// Compact spawn payload broadcast to all clients in batches. Carries everything a client needs
    /// to render a parametric tracer that matches the server simulation: start position, velocity,
    /// visual selectors, and lifetime. No per-frame replication is used.
    /// </summary>
    public struct BulletSpawnPayload : INetworkSerializable
    {
        public Vector3 SpawnPosition;
        public Vector3 Velocity;
        public float MaxDistance;
        public float Lifetime;
        public ulong OwnerShipNetworkId;
        public float Damage;
        public int VisualPrefabBankIndex;
        public uint Sequence;
        /// <summary>
        /// Synced NGO server time (seconds) at which the bullet was spawned. Clients use it to
        /// position the tracer at <c>spawnPosition + velocity * (currentServerTime - ServerSpawnTime)</c>
        /// so the bullet pops in where the server has already simulated it to, instead of at the
        /// firing ship's RTT-stale origin (which made shots look like they fired behind the ship).
        /// </summary>
        public float ServerSpawnTime;
        public byte OwnerTeamByte;
        public byte ShapeIndex;
        public byte NoTrailFlag;
        public float ScaleMultiplier;
        /// <summary>Bit flags — <see cref="BulletSpawnFlagDrone"/> always spawns a client tracer on the firing ship owner.</summary>
        public byte SpawnFlags;

        public const byte BulletSpawnFlagDrone = 1;

        public void NetworkSerialize<T>(BufferSerializer<T> serializer) where T : IReaderWriter
        {
            serializer.SerializeValue(ref SpawnPosition);
            serializer.SerializeValue(ref Velocity);
            serializer.SerializeValue(ref MaxDistance);
            serializer.SerializeValue(ref Lifetime);
            serializer.SerializeValue(ref OwnerShipNetworkId);
            serializer.SerializeValue(ref Damage);
            serializer.SerializeValue(ref VisualPrefabBankIndex);
            serializer.SerializeValue(ref Sequence);
            serializer.SerializeValue(ref ServerSpawnTime);
            serializer.SerializeValue(ref OwnerTeamByte);
            serializer.SerializeValue(ref ShapeIndex);
            serializer.SerializeValue(ref NoTrailFlag);
            serializer.SerializeValue(ref ScaleMultiplier);
            serializer.SerializeValue(ref SpawnFlags);
        }
    }

    /// <summary>
    /// Lightweight read-only view of an in-flight server bullet, used by gameplay systems (e.g.
    /// shield drones) that need to query active threats without holding NetworkObject references.
    /// </summary>
    public readonly struct ServerBulletSnapshot
    {
        public readonly Vector3 Position;
        public readonly Vector3 Velocity;
        public readonly TeamManager.Team OwnerTeam;
        public readonly ulong OwnerShipNetworkId;
        public readonly float Damage;

        public ServerBulletSnapshot(Vector3 position, Vector3 velocity, TeamManager.Team ownerTeam, ulong ownerShipNetworkId, float damage)
        {
            Position = position;
            Velocity = velocity;
            OwnerTeam = ownerTeam;
            OwnerShipNetworkId = ownerShipNetworkId;
            Damage = damage;
        }
    }

    /// <summary>
    /// Server-authoritative bullet simulation. Bullets live as plain structs in a pool and travel
    /// in a straight line; collisions are resolved with one SphereCast plus a toroidal asteroid
    /// sweep per bullet per FixedUpdate, and clients render parametric tracers (no NGO traffic
    /// per bullet). Replaces the legacy per-bullet NetworkObject + NetworkTransform model so a
    /// 60-player match can sustain thousands of in-flight projectiles.
    /// </summary>
    public partial class CombatSystem
    {
        // Tunables baked here so we don't depend on the legacy Bullet prefab at runtime.
        private const float BulletRadius = 0.3f;
        private const float BulletOverlapPadding = 0.85f;
        private const float DefaultMaxDistance = 30f;
        private const float DefaultLifetime = 2f;
        private const float DefaultMinTravelBeforeHit = 0.5f;
        private const int DefaultPoolCapacity = 1024;
        private const int MaxSpawnBatchSize = 32;

        private struct ServerBullet
        {
            public bool Active;
            public Vector3 SpawnPosition;
            public Vector3 LastPosition;
            public Vector3 Position;
            public Vector3 Velocity;
            public float Damage;
            public float SpawnTime;
            public float MaxDistance;
            public float Lifetime;
            public float MinTravelBeforeHit;
            public ulong OwnerShipNetworkId;
            public TeamManager.Team OwnerTeam;
            public int VisualPrefabBankIndex;
            public byte ShapeIndex;
            public byte NoTrailFlag;
            public float ScaleMultiplier;
            public uint Sequence;
            public byte SpawnFlags;
        }

        private struct PendingImpact
        {
            public Vector3 Position;
            public int BankIndex;
            public byte TeamByte;
            public float Pitch;
            public uint Sequence;
            public ulong OwnerShipNetworkId;
            public float Damage;
            public int DamageChannelId;
            public bool ShowDamagePopup;
            public float AsteroidRemainingHealth;
            public float AsteroidRemainingGems;
            public bool IsAsteroidHit;
            public float LingeringFireDuration;
            public ulong AttachBurnNetworkObjectId;
            public bool IsDroneShot;
            public float VisualScaleMultiplier;
        }

        private ServerBullet[] serverBullets;
        private int activeServerBulletCount;
        private uint nextBulletSequence = 1;
        private static readonly RaycastHit[] s_sphereCastHits = new RaycastHit[32];
        private static readonly Collider[] s_overlapHits = new Collider[32];
        private readonly List<BulletSpawnPayload> pendingSpawnBatch = new List<BulletSpawnPayload>(MaxSpawnBatchSize);
        private readonly List<PendingImpact> pendingImpacts = new List<PendingImpact>(MaxSpawnBatchSize);

        /// <summary>Public count for diagnostics and the max-bullet cap below.</summary>
        public int ActiveServerBulletCount => activeServerBulletCount;

        private void EnsureSimulationInitialized()
        {
            if (serverBullets != null) return;
            int capacity = Mathf.Max(64, Mathf.Max(maxBullets, DefaultPoolCapacity));
            serverBullets = new ServerBullet[capacity];
        }

        private static float GetServerTimeNowSeconds()
        {
            var nm = NetworkManager.Singleton;
            if (nm != null && nm.IsListening)
                return (float)nm.ServerTime.Time;
            return Time.time;
        }

        /// <summary>
        /// Player/ship shots spawn at the weapon muzzle; firing ship colliders are ignored server-side.
        /// Drone shots keep a small forward offset so muzzle-in-hull overlap is avoided.
        /// </summary>
        private Vector3 ResolveBulletSpawnPosition(Vector3 muzzlePosition, Vector3 normalizedDirection, bool isDroneShot)
        {
            Vector3 spawnPos = muzzlePosition;
            if (isDroneShot)
            {
                Vector3 dir = normalizedDirection;
                dir.y = 0f;
                if (dir.sqrMagnitude < 0.01f) dir = Vector3.forward;
                else dir.Normalize();
                spawnPos = muzzlePosition + dir * spawnOffset;
            }

            spawnPos.y = 0f;
            return spawnPos;
        }

        /// <summary>
        /// Server-authoritative bullet spawn. Pushes a struct into the pool and queues a batched
        /// spawn payload for clients. Returns false when the bullet cap is reached or required
        /// inputs are invalid (so callers can skip energy / recoil). Still returns true when the
        /// bullet immediately overlaps a target (e.g. ramming an asteroid) and is despawned in
        /// <see cref="CheckImmediateOverlap"/> — callers must not treat that as a failed shot.
        /// </summary>
        public bool TrySpawnServerBullet(
            Vector3 position,
            Vector3 direction,
            float speed,
            float damage,
            TeamManager.Team ownerTeam,
            ulong ownerShipNetworkId,
            float visualScaleMultiplier,
            byte bulletShapeIndex,
            Vector3 shipVelocity,
            int bulletPrefabIndex,
            byte spawnFlags = 0)
        {
            if (!IsServer) return false;
            EnsureSimulationInitialized();

            if (activeServerBulletCount >= maxBullets) return false;

            Vector3 dir = direction;
            dir.y = 0f;
            if (dir.sqrMagnitude < 0.01f) dir = Vector3.forward;
            else dir.Normalize();

            float finalSpeed = Mathf.Max(0.01f, speed * bulletSpeedMultiplier);
            bool isDroneShot = (spawnFlags & BulletSpawnPayload.BulletSpawnFlagDrone) != 0;
            Vector3 spawnPos = ResolveBulletSpawnPosition(position, dir, isDroneShot);

            Vector3 flatShipVel = new Vector3(shipVelocity.x, 0f, shipVelocity.z);
            Vector3 totalVelocity = dir * finalSpeed + flatShipVel;

            int bankCount = BulletPrefabBankCount;
            int requestedBankIndex = (bankCount > 0)
                ? (bulletPrefabIndex >= 0 && bulletPrefabIndex < bankCount ? bulletPrefabIndex : 0)
                : -1;

            int slot = AcquireSlot();
            if (slot < 0) return false;

            uint sequence = nextBulletSequence++;
            if (nextBulletSequence == 0) nextBulletSequence = 1;

            ref ServerBullet b = ref serverBullets[slot];
            b.Active = true;
            b.SpawnPosition = spawnPos;
            b.LastPosition = spawnPos;
            b.Position = spawnPos;
            b.Velocity = totalVelocity;
            b.Damage = damage;
            b.SpawnTime = GetServerTimeNowSeconds();
            b.MaxDistance = DefaultMaxDistance;
            b.Lifetime = DefaultLifetime;
            BulletBankProfileUtility.ApplyBulletFlightModifiers(requestedBankIndex, ref b.Lifetime, ref b.MaxDistance);
            b.MinTravelBeforeHit = DefaultMinTravelBeforeHit;
            b.OwnerShipNetworkId = ownerShipNetworkId;
            b.OwnerTeam = ownerTeam;
            b.VisualPrefabBankIndex = requestedBankIndex;
            b.ShapeIndex = bulletShapeIndex;
            b.NoTrailFlag = 0;
            b.ScaleMultiplier = Mathf.Max(0.1f, visualScaleMultiplier);
            b.Sequence = sequence;
            b.SpawnFlags = spawnFlags;

            activeServerBulletCount++;

            float serverSpawnTime = NetworkManager.Singleton != null
                ? (float)NetworkManager.Singleton.ServerTime.Time
                : 0f;

            pendingSpawnBatch.Add(new BulletSpawnPayload
            {
                SpawnPosition = spawnPos,
                Velocity = totalVelocity,
                MaxDistance = b.MaxDistance,
                Lifetime = b.Lifetime,
                OwnerShipNetworkId = ownerShipNetworkId,
                Damage = damage,
                VisualPrefabBankIndex = requestedBankIndex,
                Sequence = sequence,
                ServerSpawnTime = serverSpawnTime,
                OwnerTeamByte = (byte)ownerTeam,
                ShapeIndex = bulletShapeIndex,
                NoTrailFlag = 0,
                ScaleMultiplier = b.ScaleMultiplier,
                SpawnFlags = spawnFlags,
            });

            CheckImmediateOverlap(slot);
            // Slot was acquired and spawn was queued; do not use b.Active (overlap despawn is a valid shot).
            return true;
        }

        /// <summary>
        /// Same geometry as <see cref="TrySpawnServerBullet"/> for cosmetic tracers (no pool slot, no cap check).
        /// Used by the owning client to show bullets immediately before the server spawn batch arrives.
        /// </summary>
        public BulletSpawnPayload BuildBulletTracerPayloadForClientPreview(
            Vector3 position,
            Vector3 direction,
            float speed,
            float damage,
            TeamManager.Team ownerTeam,
            ulong ownerShipNetworkId,
            float visualScaleMultiplier,
            byte bulletShapeIndex,
            Vector3 shipVelocity,
            int bulletPrefabIndex)
        {
            Vector3 dir = direction;
            dir.y = 0f;
            if (dir.sqrMagnitude < 0.01f) dir = Vector3.forward;
            else dir.Normalize();

            float finalSpeed = Mathf.Max(0.01f, speed * bulletSpeedMultiplier);
            Vector3 spawnPos = ResolveBulletSpawnPosition(position, dir, isDroneShot: false);

            Vector3 flatShipVel = new Vector3(shipVelocity.x, 0f, shipVelocity.z);
            Vector3 totalVelocity = dir * finalSpeed + flatShipVel;

            int bankCount = BulletPrefabBankCount;
            int requestedBankIndex = (bankCount > 0)
                ? (bulletPrefabIndex >= 0 && bulletPrefabIndex < bankCount ? bulletPrefabIndex : 0)
                : -1;

            float scaleMul = Mathf.Max(0.1f, visualScaleMultiplier);
            float previewLifetime = DefaultLifetime;
            float previewMaxDistance = DefaultMaxDistance;
            BulletBankProfileUtility.ApplyBulletFlightModifiers(requestedBankIndex, ref previewLifetime, ref previewMaxDistance);

            float previewServerTime = GetServerTimeNowSeconds();

            return new BulletSpawnPayload
            {
                SpawnPosition = spawnPos,
                Velocity = totalVelocity,
                MaxDistance = previewMaxDistance,
                Lifetime = previewLifetime,
                OwnerShipNetworkId = ownerShipNetworkId,
                Damage = damage,
                VisualPrefabBankIndex = requestedBankIndex,
                Sequence = 0,
                ServerSpawnTime = previewServerTime,
                OwnerTeamByte = (byte)ownerTeam,
                ShapeIndex = bulletShapeIndex,
                NoTrailFlag = 0,
                ScaleMultiplier = scaleMul,
            };
        }

        private int AcquireSlot()
        {
            if (serverBullets == null) return -1;
            for (int i = 0; i < serverBullets.Length; i++)
            {
                if (!serverBullets[i].Active) return i;
            }
            // Pool is full, but we already check maxBullets above; leave the pool size sticky.
            return -1;
        }

        private void ReleaseSlot(int slot)
        {
            if (slot < 0 || serverBullets == null || slot >= serverBullets.Length) return;
            if (!serverBullets[slot].Active) return;
            serverBullets[slot].Active = false;
            activeServerBulletCount = Mathf.Max(0, activeServerBulletCount - 1);
        }

        private void StepBullet(int slot, float dt, float now)
        {
            ref ServerBullet b = ref serverBullets[slot];

            Vector3 next = b.Position + b.Velocity * dt;
            next.y = 0f;
            b.LastPosition = b.Position;
            b.Position = next;

            if (now - b.SpawnTime > b.Lifetime
                || ToroidalMap.ToroidalDistance(b.Position, b.SpawnPosition) > b.MaxDistance)
            {
                ReleaseSlot(slot);
                return;
            }

            Vector3 from = b.LastPosition;
            Vector3 to = b.Position;
            float pathLen = Vector3.Distance(from, to);
            if (pathLen < 0.001f) return;

            Vector3 dir = (to - from) / pathLen;
            int hitCount = Physics.SphereCastNonAlloc(from, BulletRadius, dir, s_sphereCastHits, pathLen, ~0, QueryTriggerInteraction.Ignore);

            if (hitCount > 1) SortHitsByDistance(hitCount);

            // Pass 1: nearest valid gameplay target (asteroid, ship, moon, etc.).
            for (int i = 0; i < hitCount; i++)
            {
                RaycastHit hit = s_sphereCastHits[i];
                if (hit.collider == null) continue;
                if (BulletHitResolver.IsColliderOnFiringShipNetworkObject(hit.collider, b.OwnerShipNetworkId)) continue;

                if (BulletHitResolver.TryHit(hit.collider, b.Damage, b.OwnerTeam, b.OwnerShipNetworkId, hit.point, out Vector3 impactPos, out BulletHitResolver.BulletHitPopupInfo popup, b.VisualPrefabBankIndex))
                {
                    DespawnWithImpact(slot, impactPos, popup);
                    return;
                }
            }

            // Overlap fallback: sphere-cast can miss thin colliders against large kinematic hulls.
            if (TryOverlapFallbackHit(slot)) return;

            if (DroneSwarmHitScan.TrySegmentHit(from, to, BulletRadius, b.OwnerTeam, out DroneBody droneBody, out Vector3 droneImpact)
                && BulletHitResolver.TryHitDroneSphere(droneBody, b.Damage, b.OwnerTeam, b.OwnerShipNetworkId, droneImpact, out BulletHitResolver.BulletHitPopupInfo dronePopup, b.VisualPrefabBankIndex))
            {
                DespawnWithImpact(slot, droneImpact, dronePopup);
                return;
            }

            if (BulletHitResolver.TryToroidalShipSegmentHit(from, to, BulletRadius, b.Damage, b.OwnerTeam, b.OwnerShipNetworkId, out Vector3 shipImpact, out BulletHitResolver.BulletHitPopupInfo shipPopup, b.VisualPrefabBankIndex))
            {
                DespawnWithImpact(slot, shipImpact, shipPopup);
                return;
            }

            // Toroidal sweeps before geometry despawn: world physics can miss tiled asteroids/moons
            // while still hitting a nearer planet shell on the same cast.
            if (BulletHitResolver.TryToroidalAsteroidSegmentHit(from, to, BulletRadius, b.Damage, b.OwnerTeam, b.OwnerShipNetworkId, out Vector3 toroidalImpact, out BulletHitResolver.BulletHitPopupInfo asteroidPopup, b.VisualPrefabBankIndex))
            {
                DespawnWithImpact(slot, toroidalImpact, asteroidPopup);
                return;
            }

            if (BulletHitResolver.TryToroidalGemMoonSegmentHit(from, to, BulletRadius, b.Damage, b.OwnerTeam, b.OwnerShipNetworkId, out Vector3 moonImpact, out BulletHitResolver.BulletHitPopupInfo moonPopup, b.VisualPrefabBankIndex))
            {
                DespawnWithImpact(slot, moonImpact, moonPopup);
                return;
            }

            // Pass 2: blocking geometry only after valid targets and toroidal sweeps.
            float travelled = ToroidalMap.ToroidalDistance(b.Position, b.SpawnPosition);
            if (travelled >= b.MinTravelBeforeHit)
            {
                for (int i = 0; i < hitCount; i++)
                {
                    RaycastHit hit = s_sphereCastHits[i];
                    if (hit.collider == null) continue;
                    if (BulletHitResolver.IsColliderOnFiringShipNetworkObject(hit.collider, b.OwnerShipNetworkId)) continue;
                    DespawnWithImpact(slot, hit.point);
                    return;
                }
            }
        }

        private bool TryOverlapFallbackHit(int slot)
        {
            ref ServerBullet b = ref serverBullets[slot];
            int m = Physics.OverlapSphereNonAlloc(b.Position, BulletRadius + BulletOverlapPadding, s_overlapHits, ~0, QueryTriggerInteraction.Ignore);
            if (m == 0) return false;

            int bestIdx = -1;
            float bestDistSq = float.MaxValue;
            for (int i = 0; i < m; i++)
            {
                Collider c = s_overlapHits[i];
                if (c == null) continue;
                if (BulletHitResolver.IsColliderOnFiringShipNetworkObject(c, b.OwnerShipNetworkId)) continue;
                // Asteroids use SphereCast / toroidal segment tests with tight radii; overlap padding causes near-miss hits.
                if (c.GetComponentInParent<Asteroid>() != null) continue;
                Vector3 cp = c.ClosestPoint(b.Position);
                float dSq = (cp - b.Position).sqrMagnitude;
                if (dSq < bestDistSq)
                {
                    bestDistSq = dSq;
                    bestIdx = i;
                }
            }

            if (bestIdx >= 0)
            {
                Collider chosen = s_overlapHits[bestIdx];
                Vector3 impact = chosen.ClosestPoint(b.Position);
                if (BulletHitResolver.TryHit(chosen, b.Damage, b.OwnerTeam, b.OwnerShipNetworkId, impact, out Vector3 finalImpact, out BulletHitResolver.BulletHitPopupInfo popup, b.VisualPrefabBankIndex))
                {
                    DespawnWithImpact(slot, finalImpact, popup);
                    return true;
                }
            }

            if (DroneSwarmHitScan.TryOverlapHit(b.Position, BulletRadius, b.OwnerTeam, out DroneBody droneBody, out Vector3 droneImpact)
                && BulletHitResolver.TryHitDroneSphere(droneBody, b.Damage, b.OwnerTeam, b.OwnerShipNetworkId, droneImpact, out BulletHitResolver.BulletHitPopupInfo dronePopup, b.VisualPrefabBankIndex))
            {
                DespawnWithImpact(slot, droneImpact, dronePopup);
                return true;
            }

            if (BulletHitResolver.TryToroidalShipOverlapHit(b.Position, BulletRadius, b.Damage, b.OwnerTeam, b.OwnerShipNetworkId, out Vector3 shipOverlapImpact, out BulletHitResolver.BulletHitPopupInfo shipOverlapPopup, b.VisualPrefabBankIndex))
            {
                DespawnWithImpact(slot, shipOverlapImpact, shipOverlapPopup);
                return true;
            }

            return false;
        }

        /// <summary>
        /// Muzzle can sit inside an asteroid while ramming/grinding; resolve the nearest valid
        /// gameplay target once (same radius as overlap fallback) without treating it as a failed spawn.
        /// </summary>
        private void CheckImmediateOverlap(int slot)
        {
            ref ServerBullet b = ref serverBullets[slot];
            Vector3 aim = b.Velocity;
            aim.y = 0f;
            bool hasAim = aim.sqrMagnitude > 0.01f;
            if (hasAim) aim.Normalize();

            float radius = BulletRadius + BulletOverlapPadding;
            int m = Physics.OverlapSphereNonAlloc(b.Position, radius, s_overlapHits, ~0, QueryTriggerInteraction.Ignore);
            if (m == 0) return;

            Collider bestCollider = null;
            Vector3 bestImpact = b.Position;
            float bestDistSq = float.MaxValue;
            for (int i = 0; i < m; i++)
            {
                Collider c = s_overlapHits[i];
                if (c == null) continue;
                if (BulletHitResolver.IsColliderOnFiringShipNetworkObject(c, b.OwnerShipNetworkId)) continue;
                if (!BulletHitResolver.TryGetBulletDamageChannel(c, b.OwnerTeam, out _)) continue;

                Asteroid asteroid = c.GetComponentInParent<Asteroid>();
                if (asteroid != null)
                {
                    // Wide overlap padding catches neighbors beside the aim line; require forward alignment.
                    if (hasAim)
                    {
                        Vector3 toTarget = c.ClosestPoint(b.Position) - b.Position;
                        toTarget.y = 0f;
                        if (toTarget.sqrMagnitude > 1e-6f && Vector3.Dot(aim, toTarget.normalized) < 0.5f)
                            continue;
                    }

                    float maxDist = asteroid.GetBulletHitRadiusWorld() + BulletRadius;
                    Vector3 impact = c.ClosestPoint(b.Position);
                    float dSq = (impact - b.Position).sqrMagnitude;
                    if (dSq > maxDist * maxDist)
                        continue;

                    if (dSq < bestDistSq)
                    {
                        bestDistSq = dSq;
                        bestCollider = c;
                        bestImpact = impact;
                    }
                    continue;
                }

                Vector3 broadImpact = c.ClosestPoint(b.Position);
                float broadDistSq = (broadImpact - b.Position).sqrMagnitude;
                if (broadDistSq < bestDistSq)
                {
                    bestDistSq = broadDistSq;
                    bestCollider = c;
                    bestImpact = broadImpact;
                }
            }

            if (bestCollider != null
                && BulletHitResolver.TryHit(bestCollider, b.Damage, b.OwnerTeam, b.OwnerShipNetworkId, bestImpact, out Vector3 finalImpact, out BulletHitResolver.BulletHitPopupInfo popup, b.VisualPrefabBankIndex))
            {
                DespawnWithImpact(slot, finalImpact, popup);
            }
        }

        private static void SortHitsByDistance(int n)
        {
            for (int i = 1; i < n; i++)
            {
                RaycastHit key = s_sphereCastHits[i];
                float kd = key.distance;
                int j = i - 1;
                while (j >= 0 && s_sphereCastHits[j].distance > kd)
                {
                    s_sphereCastHits[j + 1] = s_sphereCastHits[j];
                    j--;
                }
                s_sphereCastHits[j + 1] = key;
            }
        }

        private void DespawnWithImpact(int slot, Vector3 impactPos, BulletHitResolver.BulletHitPopupInfo popupInfo = default)
        {
            ref ServerBullet b = ref serverBullets[slot];
            Vector3 fixedImpact = impactPos;
            fixedImpact.y = 0f;
            // World-space looping fire for burn bullets (ship burns use Starship burn VFX parented to hull).
            float lingerFire = 0f;
            ulong attachBurnId = 0;
            if (b.VisualPrefabBankIndex >= 0
                && BulletBankProfileUtility.TryGetProfile(b.VisualPrefabBankIndex, out BulletBankProfile burnProfile)
                && burnProfile != null
                && burnProfile.HasBurn
                && (!popupInfo.HasPopup || popupInfo.Channel == FloatingCountChannel.DamageAsteroid))
            {
                lingerFire = burnProfile.GetBurnDuration();
            }

            pendingImpacts.Add(new PendingImpact
            {
                Position = fixedImpact,
                BankIndex = b.VisualPrefabBankIndex,
                TeamByte = b.OwnerTeam == TeamManager.Team.None ? (byte)0 : (byte)b.OwnerTeam,
                Pitch = BulletHitResolver.GetImpactSoundPitch(b.Damage),
                Sequence = b.Sequence,
                OwnerShipNetworkId = b.OwnerShipNetworkId,
                Damage = popupInfo.Damage,
                DamageChannelId = (int)popupInfo.Channel,
                ShowDamagePopup = popupInfo.HasPopup || popupInfo.HasAsteroidFeedback,
                AsteroidRemainingHealth = popupInfo.AsteroidRemainingHealth,
                AsteroidRemainingGems = popupInfo.AsteroidRemainingGems,
                IsAsteroidHit = popupInfo.IsAsteroidHit,
                LingeringFireDuration = lingerFire,
                AttachBurnNetworkObjectId = attachBurnId,
                IsDroneShot = (b.SpawnFlags & BulletSpawnPayload.BulletSpawnFlagDrone) != 0,
                VisualScaleMultiplier = b.ScaleMultiplier,
            });
            ReleaseSlot(slot);
        }

        private void FlushPendingSpawnBatch()
        {
            if (pendingSpawnBatch.Count == 0) return;
            int total = pendingSpawnBatch.Count;
            for (int start = 0; start < total; start += MaxSpawnBatchSize)
            {
                int chunk = Mathf.Min(MaxSpawnBatchSize, total - start);
                BulletSpawnPayload[] arr = new BulletSpawnPayload[chunk];
                pendingSpawnBatch.CopyTo(start, arr, 0, chunk);
                SpawnBulletBatchClientRpc(arr);
            }
            pendingSpawnBatch.Clear();
        }

        private void FlushPendingImpacts()
        {
            if (pendingImpacts.Count == 0) return;
            for (int i = 0; i < pendingImpacts.Count; i++)
            {
                PendingImpact p = pendingImpacts[i];
                SpawnBulletImpactClientRpc(
                    p.Position,
                    p.BankIndex,
                    p.TeamByte,
                    p.Pitch,
                    p.Sequence,
                    p.OwnerShipNetworkId,
                    p.Damage,
                    p.DamageChannelId,
                    p.ShowDamagePopup,
                    p.AsteroidRemainingHealth,
                    p.AsteroidRemainingGems,
                    p.IsAsteroidHit,
                    p.LingeringFireDuration,
                    p.AttachBurnNetworkObjectId,
                    p.IsDroneShot,
                    p.VisualScaleMultiplier);
            }
            pendingImpacts.Clear();
        }

        [ClientRpc]
        private void SpawnBulletBatchClientRpc(BulletSpawnPayload[] payloads)
        {
            if (payloads == null) return;
            ulong localShipId = ClientBulletTracer.GetLocalPlayerOwnedShipNetworkObjectId();
            for (int i = 0; i < payloads.Length; i++)
            {
                BulletSpawnPayload p = payloads[i];
                // Owner already has lag-free ClientBulletTracer from HandleInput; do not spawn a second tracer from the server batch.
                // Drone shots never use owner prediction — always show the batched tracer.
                bool isDroneShot = (p.SpawnFlags & BulletSpawnPayload.BulletSpawnFlagDrone) != 0;
                if (!isDroneShot && localShipId != 0 && p.OwnerShipNetworkId == localShipId)
                {
                    ClientBulletTracer.HandleOwnerServerBulletSpawn(p);
                    continue;
                }
                ClientBulletTracer.Spawn(p);
            }
        }

        [ClientRpc]
        private void SpawnBulletImpactClientRpc(
            Vector3 position,
            int impactPrefabBankIndex,
            byte teamByte,
            float pitch,
            uint sequence,
            ulong bulletOwnerShipNetworkId,
            float damage,
            int damageChannelId,
            bool showDamagePopup,
            float asteroidRemainingHealth,
            float asteroidRemainingGems,
            bool isAsteroidHit,
            float lingeringFireDuration,
            ulong attachBurnNetworkObjectId,
            bool isDroneShot,
            float visualScaleMultiplier)
        {
            ulong localShipId = ClientBulletTracer.GetLocalPlayerOwnedShipNetworkObjectId();
            TeamManager.Team team = (TeamManager.Team)teamByte;
            var popup = showDamagePopup
                ? new BulletHitResolver.BulletHitPopupInfo(
                    damage > 0.0001f,
                    (FloatingCountChannel)Mathf.Clamp(damageChannelId, 0, FloatingCountFeedbackSettings.MaxChannelIndex),
                    damage,
                    isAsteroidHit: isAsteroidHit,
                    asteroidRemainingHealth: asteroidRemainingHealth,
                    asteroidRemainingGems: asteroidRemainingGems)
                : BulletHitResolver.BulletHitPopupInfo.None;

            // Firing owner normally uses owner-predicted impact VFX; fall back to server RPC when prediction missed.
            if (!isDroneShot && localShipId != 0 && bulletOwnerShipNetworkId == localShipId)
            {
                ClientBulletTracer.DespawnBySequence(sequence);
                if (popup.HasAsteroidFeedback)
                {
                    BulletHitResolver.SpawnBulletHitFeedbackLocal(
                        BulletVisualFactory.ResolveClientImpactWorldPosition(position),
                        popup,
                        team);
                }

                if (ClientBulletTracer.ShouldSkipOwnerServerImpactVfx(sequence))
                    return;
            }
            else
            {
                ClientBulletTracer.DespawnBySequence(sequence);
            }

            float impactScale = BulletVisualFactory.GetImpactScale(
                Mathf.Max(0.1f, visualScaleMultiplier),
                isAsteroidHit);
            GameObject prefab = impactPrefabBankIndex >= 0
                ? GetImpactPrefabFromBank(impactPrefabBankIndex, team)
                : null;

            Transform attach = null;
            Vector3 localOffset = Vector3.zero;
            if (lingeringFireDuration > 0.05f
                && attachBurnNetworkObjectId != 0
                && NetworkManager.Singleton != null
                && NetworkManager.Singleton.SpawnManager.SpawnedObjects.TryGetValue(
                    attachBurnNetworkObjectId, out NetworkObject attachNo)
                && attachNo != null)
            {
                attach = attachNo.transform;
                Vector3 displayImpact = BulletVisualFactory.ResolveClientImpactWorldPosition(position);
                localOffset = attach.InverseTransformPoint(displayImpact);
            }

            BulletVisualFactory.SpawnBulletImpactVfx(
                position,
                prefab,
                team,
                pitch,
                impactScale,
                BulletVisualFactory.DefaultImpactDuration,
                attach,
                localOffset,
                lingeringFireDuration);

            if (AudioManager.Instance != null)
                AudioManager.Instance.PlayImpactSound(pitch);

            BulletHitResolver.SpawnBulletHitFeedbackLocal(
                BulletVisualFactory.ResolveClientImpactWorldPosition(position),
                popup,
                team);
        }

        /// <summary>
        /// Snapshot active server bullets into <paramref name="dest"/> so server-side AI (e.g.
        /// shield drones via <see cref="DroneSwarmLogic"/>) can reason about incoming threats without iterating every
        /// frame across all NetworkObjects.
        /// </summary>
        public int CopyActiveBulletSnapshots(ServerBulletSnapshot[] dest)
        {
            if (!IsServer || serverBullets == null || dest == null || dest.Length == 0) return 0;
            int written = 0;
            for (int i = 0; i < serverBullets.Length && written < dest.Length; i++)
            {
                if (!serverBullets[i].Active) continue;
                ref ServerBullet b = ref serverBullets[i];
                dest[written++] = new ServerBulletSnapshot(b.Position, b.Velocity, b.OwnerTeam, b.OwnerShipNetworkId, b.Damage);
            }
            return written;
        }
    }
}

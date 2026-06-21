using UnityEngine;
using Unity.Netcode;
using TitanOrbit.Core;
using TitanOrbit.Entities;
using TitanOrbit.Generation;

namespace TitanOrbit.Systems
{
    public struct RocketSpawnPayload : INetworkSerializable
    {
        public Vector3 SpawnPosition;
        public Vector3 Velocity;
        public float MaxDistance;
        public float Lifetime;
        public float Damage;
        public ulong OwnerShipNetworkId;
        public uint Sequence;
        public float ServerSpawnTime;
        public byte OwnerTeamByte;
        public byte IsLargeFlag;

        public void NetworkSerialize<T>(BufferSerializer<T> serializer) where T : IReaderWriter
        {
            serializer.SerializeValue(ref SpawnPosition);
            serializer.SerializeValue(ref Velocity);
            serializer.SerializeValue(ref MaxDistance);
            serializer.SerializeValue(ref Lifetime);
            serializer.SerializeValue(ref Damage);
            serializer.SerializeValue(ref OwnerShipNetworkId);
            serializer.SerializeValue(ref Sequence);
            serializer.SerializeValue(ref ServerSpawnTime);
            serializer.SerializeValue(ref OwnerTeamByte);
            serializer.SerializeValue(ref IsLargeFlag);
        }
    }

    public partial class CombatSystem
    {
        private const float RocketRadius = 0.35f;
        private const int MaxRocketBatchSize = 16;
        private const int DefaultRocketPool = 64;

        private struct ServerRocket
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
            public ulong OwnerShipNetworkId;
            public TeamManager.Team OwnerTeam;
            public uint Sequence;
        }

        private ServerRocket[] serverRockets;
        private int activeServerRocketCount;
        private uint nextRocketSequence = 1;
        private readonly System.Collections.Generic.List<RocketSpawnPayload> pendingRocketBatch =
            new System.Collections.Generic.List<RocketSpawnPayload>(MaxRocketBatchSize);

        public bool TrySpawnServerRocket(
            Vector3 position,
            Vector3 direction,
            bool isLarge,
            TeamManager.Team ownerTeam,
            ulong ownerShipNetworkId)
        {
            if (!IsServer) return false;
            EnsureRocketPoolInitialized();

            float speed = isLarge ? 20f : 24f;
            float damage = isLarge ? 55f : 25f;
            float maxDist = 150f;
            float lifetime = 8f;

            Vector3 dir = direction;
            dir.y = 0f;
            if (dir.sqrMagnitude < 0.01f) dir = Vector3.forward;
            else dir.Normalize();

            Vector3 spawnPos = position;
            spawnPos.y = 0f;
            Vector3 velocity = dir * speed;

            int slot = AcquireRocketSlot();
            if (slot < 0) return false;

            uint seq = nextRocketSequence++;
            if (nextRocketSequence == 0) nextRocketSequence = 1;
            float spawnTime = GetServerTimeNowSeconds();

            ref ServerRocket r = ref serverRockets[slot];
            r.Active = true;
            r.SpawnPosition = spawnPos;
            r.LastPosition = spawnPos;
            r.Position = spawnPos;
            r.Velocity = velocity;
            r.Damage = damage;
            r.SpawnTime = spawnTime;
            r.MaxDistance = maxDist;
            r.Lifetime = lifetime;
            r.OwnerShipNetworkId = ownerShipNetworkId;
            r.OwnerTeam = ownerTeam;
            r.Sequence = seq;
            activeServerRocketCount++;

            pendingRocketBatch.Add(new RocketSpawnPayload
            {
                SpawnPosition = spawnPos,
                Velocity = velocity,
                MaxDistance = maxDist,
                Lifetime = lifetime,
                Damage = damage,
                OwnerShipNetworkId = ownerShipNetworkId,
                Sequence = seq,
                ServerSpawnTime = spawnTime,
                OwnerTeamByte = (byte)ownerTeam,
                IsLargeFlag = (byte)(isLarge ? 1 : 0),
            });
            return true;
        }

        private void EnsureRocketPoolInitialized()
        {
            if (serverRockets != null) return;
            serverRockets = new ServerRocket[DefaultRocketPool];
        }

        private int AcquireRocketSlot()
        {
            if (serverRockets == null) return -1;
            for (int i = 0; i < serverRockets.Length; i++)
                if (!serverRockets[i].Active) return i;
            return -1;
        }

        private void ReleaseRocketSlot(int slot)
        {
            if (slot < 0 || serverRockets == null || !serverRockets[slot].Active) return;
            serverRockets[slot].Active = false;
            activeServerRocketCount = Mathf.Max(0, activeServerRocketCount - 1);
        }

        private void TickServerRockets(float dt, float now)
        {
            if (serverRockets == null || activeServerRocketCount == 0) return;
            for (int i = 0; i < serverRockets.Length; i++)
            {
                if (!serverRockets[i].Active) continue;
                StepRocket(i, dt, now);
            }
        }

        private void StepRocket(int slot, float dt, float now)
        {
            ref ServerRocket r = ref serverRockets[slot];
            r.LastPosition = r.Position;
            r.Position += r.Velocity * dt;
            r.Position.y = 0f;

            if (now - r.SpawnTime > r.Lifetime
                || ToroidalMap.ToroidalDistance(r.Position, r.SpawnPosition) > r.MaxDistance)
            {
                ReleaseRocketSlot(slot);
                return;
            }

            Vector3 from = r.LastPosition;
            Vector3 to = r.Position;
            float pathLen = Vector3.Distance(from, to);
            if (pathLen < 0.001f) return;

            int hitCount = Physics.SphereCastNonAlloc(from, RocketRadius, (to - from).normalized, s_sphereCastHits, pathLen, ~0, QueryTriggerInteraction.Collide);
            for (int h = 0; h < hitCount; h++)
            {
                if (s_sphereCastHits[h].collider == null) continue;
                if (TryRocketHit(s_sphereCastHits[h].collider, ref r))
                {
                    ReleaseRocketSlot(slot);
                    return;
                }
            }
        }

        private bool TryRocketHit(Collider col, ref ServerRocket r)
        {
            if (col == null) return false;
            if (BulletHitResolver.IsColliderOnFiringShipNetworkObject(col, r.OwnerShipNetworkId)) return false;

            Starship ship = col.GetComponent<Starship>() ?? col.GetComponentInParent<Starship>();
            if (ship != null && !ship.IsDead && ship.ShipTeam != r.OwnerTeam)
            {
                ship.TakeDamageServerRpc(r.Damage, r.OwnerTeam, r.OwnerShipNetworkId);
                return true;
            }

            DroneBody drone = col.GetComponentInParent<DroneBody>();
            if (drone != null && !drone.IsDestroyed && drone.IsEnemyTeam(r.OwnerTeam))
            {
                drone.Swarm?.ApplyDamageFromBullet(drone.EquipmentSlotIndex, r.Damage, r.OwnerTeam, r.OwnerShipNetworkId, r.Position);
                return true;
            }

            Asteroid ast = col.GetComponent<Asteroid>() ?? col.GetComponentInParent<Asteroid>();
            if (ast != null && !ast.IsDestroyed)
            {
                ast.TakeDamageServerRpc(r.Damage, r.OwnerShipNetworkId);
                return true;
            }

            PlanetGemMoon moon = col.GetComponentInParent<PlanetGemMoon>();
            if (moon != null && !moon.IsTeamFriendlyToThisMoon(r.OwnerTeam))
            {
                moon.TakeDamageServer(r.Damage, r.OwnerTeam);
                return true;
            }

            return false;
        }

        private void FlushPendingRocketBatch()
        {
            if (pendingRocketBatch.Count == 0) return;
            int total = pendingRocketBatch.Count;
            for (int start = 0; start < total; start += MaxRocketBatchSize)
            {
                int chunk = Mathf.Min(MaxRocketBatchSize, total - start);
                RocketSpawnPayload[] arr = new RocketSpawnPayload[chunk];
                pendingRocketBatch.CopyTo(start, arr, 0, chunk);
                SpawnRocketBatchClientRpc(arr);
            }
            pendingRocketBatch.Clear();
        }

        [ClientRpc]
        private void SpawnRocketBatchClientRpc(RocketSpawnPayload[] payloads)
        {
            if (payloads == null) return;
            for (int i = 0; i < payloads.Length; i++)
                ClientRocketTracer.Spawn(payloads[i]);
        }
    }
}

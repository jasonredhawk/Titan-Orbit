using UnityEngine;
using Unity.Netcode;
using TitanOrbit.Core;
using TitanOrbit.Entities;
using TitanOrbit.Generation;

namespace TitanOrbit.Systems
{
    public struct MineSpawnPayload : INetworkSerializable
    {
        public Vector3 Position;
        public float TriggerRadius;
        public float ExplosionRadius;
        public float Damage;
        public float ArmTime;
        public ulong OwnerShipNetworkId;
        public uint Sequence;
        public float ServerSpawnTime;
        public byte OwnerTeamByte;
        public byte IsLargeFlag;

        public void NetworkSerialize<T>(BufferSerializer<T> serializer) where T : IReaderWriter
        {
            serializer.SerializeValue(ref Position);
            serializer.SerializeValue(ref TriggerRadius);
            serializer.SerializeValue(ref ExplosionRadius);
            serializer.SerializeValue(ref Damage);
            serializer.SerializeValue(ref ArmTime);
            serializer.SerializeValue(ref OwnerShipNetworkId);
            serializer.SerializeValue(ref Sequence);
            serializer.SerializeValue(ref ServerSpawnTime);
            serializer.SerializeValue(ref OwnerTeamByte);
            serializer.SerializeValue(ref IsLargeFlag);
        }
    }

    public partial class CombatSystem
    {
        private const int MaxMineBatchSize = 16;
        private const int DefaultMinePool = 64;
        private static readonly Collider[] s_mineOverlapHits = new Collider[32];

        private struct ServerMine
        {
            public bool Active;
            public Vector3 Position;
            public float TriggerRadius;
            public float ExplosionRadius;
            public float Damage;
            public float SpawnTime;
            public float ArmTime;
            public ulong OwnerShipNetworkId;
            public TeamManager.Team OwnerTeam;
            public uint Sequence;
        }

        private ServerMine[] serverMines;
        private int activeServerMineCount;
        private uint nextMineSequence = 1;
        private readonly System.Collections.Generic.List<MineSpawnPayload> pendingMineBatch =
            new System.Collections.Generic.List<MineSpawnPayload>(MaxMineBatchSize);
        private readonly System.Collections.Generic.List<uint> pendingMineDespawn =
            new System.Collections.Generic.List<uint>(MaxMineBatchSize);

        public bool TrySpawnServerMine(Vector3 position, bool isLarge, TeamManager.Team ownerTeam, ulong ownerShipNetworkId)
        {
            if (!IsServer) return false;
            EnsureMinePoolInitialized();

            float damage = isLarge ? 70f : 35f;
            float explosionRadius = isLarge ? 7f : 4f;
            float triggerRadius = Mathf.Max(explosionRadius, isLarge ? 7f : 5f);
            position.y = 0f;

            int slot = AcquireMineSlot();
            if (slot < 0) return false;

            uint seq = nextMineSequence++;
            if (nextMineSequence == 0) nextMineSequence = 1;
            float spawnTime = GetServerTimeNowSeconds();

            ref ServerMine m = ref serverMines[slot];
            m.Active = true;
            m.Position = position;
            m.TriggerRadius = triggerRadius;
            m.ExplosionRadius = explosionRadius;
            m.Damage = damage;
            m.SpawnTime = spawnTime;
            m.ArmTime = 0.5f;
            m.OwnerShipNetworkId = ownerShipNetworkId;
            m.OwnerTeam = ownerTeam;
            m.Sequence = seq;
            activeServerMineCount++;

            pendingMineBatch.Add(new MineSpawnPayload
            {
                Position = position,
                TriggerRadius = triggerRadius,
                ExplosionRadius = explosionRadius,
                Damage = damage,
                ArmTime = m.ArmTime,
                OwnerShipNetworkId = ownerShipNetworkId,
                Sequence = seq,
                ServerSpawnTime = spawnTime,
                OwnerTeamByte = (byte)ownerTeam,
                IsLargeFlag = (byte)(isLarge ? 1 : 0),
            });
            return true;
        }

        private void EnsureMinePoolInitialized()
        {
            if (serverMines != null) return;
            serverMines = new ServerMine[DefaultMinePool];
        }

        private int AcquireMineSlot()
        {
            if (serverMines == null) return -1;
            for (int i = 0; i < serverMines.Length; i++)
                if (!serverMines[i].Active) return i;
            return -1;
        }

        private void ReleaseMineSlot(int slot, uint sequence)
        {
            if (slot < 0 || serverMines == null || !serverMines[slot].Active) return;
            pendingMineDespawn.Add(sequence);
            serverMines[slot].Active = false;
            activeServerMineCount = Mathf.Max(0, activeServerMineCount - 1);
        }

        private void TickServerMines(float now)
        {
            if (serverMines == null || activeServerMineCount == 0) return;
            for (int i = 0; i < serverMines.Length; i++)
            {
                if (!serverMines[i].Active) continue;
                ref ServerMine m = ref serverMines[i];
                if (now - m.SpawnTime < m.ArmTime) continue;

                int count = Physics.OverlapSphereNonAlloc(m.Position, m.TriggerRadius, s_mineOverlapHits, ~0, QueryTriggerInteraction.Ignore);
                for (int c = 0; c < count; c++)
                {
                    Starship ship = s_mineOverlapHits[c].GetComponent<Starship>();
                    if (ship != null && !ship.IsDead && ship.ShipTeam != m.OwnerTeam)
                    {
                        ExplodeMine(i, ref m);
                        break;
                    }
                }
            }
        }

        private void ExplodeMine(int slot, ref ServerMine m)
        {
            Vector3 pos = m.Position;
            int hits = Physics.OverlapSphereNonAlloc(pos, m.ExplosionRadius, s_mineOverlapHits, ~0, QueryTriggerInteraction.Ignore);
            for (int i = 0; i < hits; i++)
            {
                Collider c = s_mineOverlapHits[i];
                if (c == null) continue;
                Vector3 hitPoint = c.ClosestPoint(pos);
                hitPoint.y = 0f;
                float dist = Vector3.Distance(hitPoint, pos);
                float falloff = 1f - (dist / m.ExplosionRadius) * 0.5f;
                float dmg = m.Damage * Mathf.Clamp01(falloff);

                Starship ship = c.GetComponent<Starship>();
                if (ship != null && !ship.IsDead && ship.ShipTeam != m.OwnerTeam)
                    ship.TakeDamageServerRpc(dmg, m.OwnerTeam, m.OwnerShipNetworkId);

                DroneBody drone = c.GetComponent<DroneBody>();
                if (drone != null && !drone.IsDestroyed && drone.IsEnemyTeam(m.OwnerTeam))
                    drone.Swarm?.ApplyDamageFromBullet(drone.EquipmentSlotIndex, dmg, m.OwnerTeam, m.OwnerShipNetworkId, hitPoint);

                Asteroid ast = c.GetComponent<Asteroid>();
                if (ast != null && !ast.IsDestroyed)
                    ast.TakeDamageServerRpc(dmg);
            }

            ReleaseMineSlot(slot, m.Sequence);
        }

        private void FlushPendingMineBatch()
        {
            if (pendingMineBatch.Count > 0)
            {
                int total = pendingMineBatch.Count;
                for (int start = 0; start < total; start += MaxMineBatchSize)
                {
                    int chunk = Mathf.Min(MaxMineBatchSize, total - start);
                    MineSpawnPayload[] arr = new MineSpawnPayload[chunk];
                    pendingMineBatch.CopyTo(start, arr, 0, chunk);
                    SpawnMineBatchClientRpc(arr);
                }
                pendingMineBatch.Clear();
            }

            if (pendingMineDespawn.Count > 0)
            {
                for (int i = 0; i < pendingMineDespawn.Count; i++)
                    DespawnMineClientRpc(pendingMineDespawn[i]);
                pendingMineDespawn.Clear();
            }
        }

        [ClientRpc]
        private void SpawnMineBatchClientRpc(MineSpawnPayload[] payloads)
        {
            if (payloads == null) return;
            for (int i = 0; i < payloads.Length; i++)
                ClientMineVisual.Spawn(payloads[i]);
        }

        [ClientRpc]
        private void DespawnMineClientRpc(uint sequence)
        {
            ClientMineVisual.DespawnBySequence(sequence);
        }
    }
}

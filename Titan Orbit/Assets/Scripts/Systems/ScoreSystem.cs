using System;
using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;
using TitanOrbit.Core;
using TitanOrbit.Entities;
using TitanOrbit.AI;

namespace TitanOrbit.Systems
{
    public struct ScoreEntry : INetworkSerializable, IEquatable<ScoreEntry>
    {
        public ulong ShipNetworkId;
        public ulong OwnerClientId;
        public TeamManager.Team Team;
        public int Score;
        public int Kills;
        public float MinedGems;
        public float DepositedGems;
        public float HealedPeople;
        public float TransportedPeople;
        public bool IsAI;

        public void NetworkSerialize<T>(BufferSerializer<T> serializer) where T : IReaderWriter
        {
            serializer.SerializeValue(ref ShipNetworkId);
            serializer.SerializeValue(ref OwnerClientId);
            serializer.SerializeValue(ref Team);
            serializer.SerializeValue(ref Score);
            serializer.SerializeValue(ref Kills);
            serializer.SerializeValue(ref MinedGems);
            serializer.SerializeValue(ref DepositedGems);
            serializer.SerializeValue(ref HealedPeople);
            serializer.SerializeValue(ref TransportedPeople);
            serializer.SerializeValue(ref IsAI);
        }

        public bool Equals(ScoreEntry other)
        {
            return ShipNetworkId == other.ShipNetworkId
                && OwnerClientId == other.OwnerClientId
                && Team == other.Team
                && Score == other.Score
                && Kills == other.Kills
                && Mathf.Approximately(MinedGems, other.MinedGems)
                && Mathf.Approximately(DepositedGems, other.DepositedGems)
                && Mathf.Approximately(HealedPeople, other.HealedPeople)
                && Mathf.Approximately(TransportedPeople, other.TransportedPeople)
                && IsAI == other.IsAI;
        }
    }

    /// <summary>
    /// Server-authoritative player scoring with synced entries for leaderboard UI.
    /// </summary>
    public class ScoreSystem : NetworkBehaviour
    {
        public static ScoreSystem Instance { get; private set; }

        [Header("Scoring (tunable)")]
        [SerializeField] private int pointsPerMinedGem = 1;
        [SerializeField] private int pointsPerDepositedGem = 2;
        [SerializeField] private int pointsPerHostileUnloadPerson = 5;
        [SerializeField] private int pointsPerEnemyKill = 100;

        [Header("Sync")]
        [SerializeField] private float shipSyncInterval = 1f;

        private NetworkList<ScoreEntry> scoreEntries;
        private readonly Dictionary<ulong, float> miningCarry = new Dictionary<ulong, float>();
        private readonly Dictionary<ulong, float> depositCarry = new Dictionary<ulong, float>();
        private readonly Dictionary<ulong, float> unloadCarry = new Dictionary<ulong, float>();
        private float nextShipSyncTime;

        public NetworkList<ScoreEntry> Entries => scoreEntries;

        private void Awake()
        {
            BootTrace.Mark("ScoreSystem.Awake - enter");
            if (Instance == null)
            {
                Instance = this;
                BootTrace.Mark("ScoreSystem.Awake - instance set");
            }
            else
            {
                BootTrace.Mark("ScoreSystem.Awake - duplicate instance, destroying");
                Destroy(gameObject);
                return;
            }

            scoreEntries = new NetworkList<ScoreEntry>();
        }

        private void OnDestroy()
        {
            if (scoreEntries != null)
                scoreEntries.Dispose();

            if (Instance == this)
                Instance = null;
        }

        public override void OnNetworkSpawn()
        {
            BootTrace.Mark("ScoreSystem.OnNetworkSpawn - enter (IsServer=" + IsServer + ")");
            if (IsServer)
            {
                BootTrace.Mark("ScoreSystem.OnNetworkSpawn - initial SyncTrackedShips");
                SyncTrackedShips();
            }
        }

        private void Update()
        {
            if (!IsServer || !IsSpawned) return;
            if (Time.time < nextShipSyncTime) return;
            nextShipSyncTime = Time.time + Mathf.Max(0.25f, shipSyncInterval);
            BootTrace.Mark("ScoreSystem.Update - SyncTrackedShips");
            SyncTrackedShips();
        }

        public void AwardMining(Starship ship, float gemsMined)
        {
            AwardByAmount(ship, gemsMined, Mathf.Max(0, pointsPerMinedGem), miningCarry);
            AddMetric(ship, gemsMined, MetricType.Mined);
        }

        public void AwardDeposit(Starship ship, float gemsDeposited)
        {
            AwardByAmount(ship, gemsDeposited, Mathf.Max(0, pointsPerDepositedGem), depositCarry);
            AddMetric(ship, gemsDeposited, MetricType.Deposited);
        }

        public void AwardHostileUnload(Starship ship, float peopleUnloaded)
        {
            AwardByAmount(ship, peopleUnloaded, Mathf.Max(0, pointsPerHostileUnloadPerson), unloadCarry);
            AddMetric(ship, peopleUnloaded, MetricType.Transported);
        }

        public void AwardFriendlyLoad(Starship ship, float peopleLoaded)
        {
            AddMetric(ship, peopleLoaded, MetricType.Healed);
        }

        public void AwardEnemyKill(Starship killerShip)
        {
            if (!IsServer || killerShip == null) return;
            AddScore(killerShip, Mathf.Max(0, pointsPerEnemyKill), true);
        }

        private void AwardByAmount(Starship ship, float amount, int pointsPerUnit, Dictionary<ulong, float> carryMap)
        {
            if (!IsServer || ship == null || pointsPerUnit <= 0 || amount <= 0f) return;
            if (ship.ShipTeam == TeamManager.Team.None) return;

            ulong shipId = ship.NetworkObjectId;
            float carry = 0f;
            carryMap.TryGetValue(shipId, out carry);
            float total = carry + amount * pointsPerUnit;
            int points = Mathf.FloorToInt(total);
            carryMap[shipId] = total - points;

            if (points > 0)
                AddScore(ship, points, false);
        }

        private void AddScore(Starship ship, int points, bool isKill)
        {
            if (!IsServer || ship == null || points <= 0) return;

            int index = EnsureEntryForShip(ship);
            if (index < 0) return;

            ScoreEntry entry = scoreEntries[index];
            entry.Score += points;
            if (isKill) entry.Kills += 1;
            scoreEntries[index] = entry;
        }

        private enum MetricType
        {
            Mined,
            Deposited,
            Healed,
            Transported
        }

        private void AddMetric(Starship ship, float amount, MetricType metricType)
        {
            if (!IsServer || ship == null || amount <= 0f) return;
            int index = EnsureEntryForShip(ship);
            if (index < 0) return;

            ScoreEntry entry = scoreEntries[index];
            switch (metricType)
            {
                case MetricType.Mined:
                    entry.MinedGems += amount;
                    break;
                case MetricType.Deposited:
                    entry.DepositedGems += amount;
                    break;
                case MetricType.Healed:
                    entry.HealedPeople += amount;
                    break;
                case MetricType.Transported:
                    entry.TransportedPeople += amount;
                    break;
            }
            scoreEntries[index] = entry;
        }

        private int EnsureEntryForShip(Starship ship)
        {
            if (ship == null) return -1;
            ulong shipId = ship.NetworkObjectId;

            for (int i = 0; i < scoreEntries.Count; i++)
            {
                if (scoreEntries[i].ShipNetworkId != shipId) continue;
                ScoreEntry existing = scoreEntries[i];
                bool changed = false;
                if (existing.Team != ship.ShipTeam) { existing.Team = ship.ShipTeam; changed = true; }
                if (existing.OwnerClientId != ship.OwnerClientId) { existing.OwnerClientId = ship.OwnerClientId; changed = true; }
                bool isAi = ship.GetComponent<AIShipMarker>() != null;
                if (existing.IsAI != isAi) { existing.IsAI = isAi; changed = true; }
                if (changed) scoreEntries[i] = existing;
                return i;
            }

            ScoreEntry newEntry = new ScoreEntry
            {
                ShipNetworkId = shipId,
                OwnerClientId = ship.OwnerClientId,
                Team = ship.ShipTeam,
                Score = 0,
                Kills = 0,
                MinedGems = 0f,
                DepositedGems = 0f,
                HealedPeople = 0f,
                TransportedPeople = 0f,
                IsAI = ship.GetComponent<AIShipMarker>() != null
            };
            scoreEntries.Add(newEntry);
            return scoreEntries.Count - 1;
        }

        private void SyncTrackedShips()
        {
            var ships = FindObjectsByType<Starship>(FindObjectsSortMode.None);
            HashSet<ulong> activeShipIds = new HashSet<ulong>();
            foreach (var ship in ships)
            {
                if (ship == null || !ship.IsSpawned) continue;
                activeShipIds.Add(ship.NetworkObjectId);
                EnsureEntryForShip(ship);
            }

            for (int i = scoreEntries.Count - 1; i >= 0; i--)
            {
                if (!activeShipIds.Contains(scoreEntries[i].ShipNetworkId))
                {
                    miningCarry.Remove(scoreEntries[i].ShipNetworkId);
                    depositCarry.Remove(scoreEntries[i].ShipNetworkId);
                    unloadCarry.Remove(scoreEntries[i].ShipNetworkId);
                    scoreEntries.RemoveAt(i);
                }
            }
        }
    }
}

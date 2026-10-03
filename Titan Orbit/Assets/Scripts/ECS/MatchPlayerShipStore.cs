using System;
using System.Collections.Generic;
using TitanOrbit.Core;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.NetCode;
using UnityEngine;

namespace TitanOrbit.ECS
{
    /// <summary>
    /// One player's ship, gear, cargo, and upgrades for the current match.
    /// Written once when their hull is removed. Not a ghost and not a per-tick copy.
    /// </summary>
    public sealed class MatchPlayerShipSnapshot
    {
        /// <summary>How many card or equipment rows a snapshot will keep.</summary>
        public const int MaxRows = 32;

        public ShipState Ship;
        public bool HasLoadout;
        public ShipLoadoutState Loadout;
        public bool HasAttributes;
        public ShipAttributeUpgradeState Attributes;
        public bool HasMatchStats;
        public ShipMatchStats MatchStats;
        public bool HasAccents;
        public ShipAccentColors Accents;
        public bool HasMega;
        public MegaShipState Mega;
        public EquippedCardElement[] Cards = Array.Empty<EquippedCardElement>();
        public EquippedEquipmentElement[] Equipment = Array.Empty<EquippedEquipmentElement>();
    }

    /// <summary>
    /// Server-only match memory of who owns which saved ship.
    /// Keyed by the stable player id from <see cref="SetSessionPlayerIdCommand"/>, not
    /// <see cref="NetworkId"/> (that id is recycled). Cleared when the match ends.
    /// Disconnect-time writes only — never scanned from a per-tick hot path.
    /// </summary>
    public static class MatchPlayerShipStore
    {
        static readonly Dictionary<string, MatchPlayerShipSnapshot> Snapshots =
            new Dictionary<string, MatchPlayerShipSnapshot>(8, StringComparer.Ordinal);

        static readonly Dictionary<int, string> NetworkToPlayer = new Dictionary<int, string>(8);

        /// <summary>True after a win until the next new game opens the store again.</summary>
        static bool _closed;

        /// <summary>
        /// [UNITY] Domain Reload off: the dictionary would otherwise outlive Play Mode.
        /// Menu return stays inside one Play, so this does not run then.
        /// </summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStaticsForPlayMode() => Clear();

        public static int SnapshotCount => Snapshots.Count;

        /// <summary>New game: drop every saved ship and accept saves again.</summary>
        public static void Clear()
        {
            int count = Snapshots.Count;
            Snapshots.Clear();
            NetworkToPlayer.Clear();
            _closed = false;
            if (count > 0)
                Debug.Log("[MatchPlayerShipStore] Cleared " + count + " saved ship(s) for a new game.");
        }

        /// <summary>
        /// Match won. Drops saved ships and ignores later binds so a disconnect during
        /// the win screen cannot write a new snapshot.
        /// </summary>
        public static void CloseMatch()
        {
            int count = Snapshots.Count;
            Snapshots.Clear();
            NetworkToPlayer.Clear();
            _closed = true;
            if (count > 0)
                Debug.Log("[MatchPlayerShipStore] Closed match — dropped " + count + " saved ship(s).");
        }

        /// <summary>Drops NetworkId bindings after the hulls are gone. Snapshots stay.</summary>
        public static void ClearBindings() => NetworkToPlayer.Clear();

        /// <summary>Remember which stable id owns this live connection.</summary>
        public static bool Bind(int networkId, in FixedString128Bytes playerId)
        {
            if (_closed || networkId <= 0 || playerId.Length <= 0)
                return false;

            string key = playerId.ToString();
            if (string.IsNullOrEmpty(key))
                return false;

            NetworkToPlayer[networkId] = key;
            return true;
        }

        public static bool TryGetPlayerId(int networkId, out string playerId) =>
            NetworkToPlayer.TryGetValue(networkId, out playerId);

        public static void Unbind(int networkId)
        {
            if (networkId > 0)
                NetworkToPlayer.Remove(networkId);
        }

        public static bool HasSavedShip(int networkId) =>
            TryGetPlayerId(networkId, out string playerId) && Snapshots.ContainsKey(playerId);

        /// <summary>
        /// True when a different live connection is still flying this same player id.
        /// A reconnect that overlaps the old connection must not spawn a second hull.
        /// </summary>
        public static bool OtherConnectionFlying(int networkId, NativeHashSet<int> liveOwners)
        {
            if (!TryGetPlayerId(networkId, out string playerId))
                return false;

            foreach (var pair in NetworkToPlayer)
            {
                if (pair.Key == networkId || pair.Value != playerId)
                    continue;
                if (liveOwners.Contains(pair.Key))
                    return true;
            }

            return false;
        }

        /// <summary>
        /// Copies the hull into the store when it has a team and a bound player id.
        /// Eliminated hulls are skipped — resume cannot put them back in the match.
        /// </summary>
        public static bool TryCapture(EntityManager em, Entity ship)
        {
            if (_closed || !em.Exists(ship) || !em.HasComponent<GhostOwner>(ship))
                return false;

            int networkId = em.GetComponentData<GhostOwner>(ship).NetworkId;
            if (networkId <= 0 || !TryGetPlayerId(networkId, out string playerId))
                return false;
            if (!TryRead(em, ship, out MatchPlayerShipSnapshot snapshot))
                return false;

            Snapshots[playerId] = snapshot;
            Debug.Log(
                "[MatchPlayerShipStore] Saved ship for player " + playerId +
                " networkId=" + networkId +
                " team=" + snapshot.Ship.Team +
                " level=" + snapshot.Ship.ShipLevel + ".");
            return true;
        }

        /// <summary>Saves every remaining hull that still has a player-id binding.</summary>
        public static void CaptureRemainingShips(EntityManager em)
        {
            using var query = em.CreateEntityQuery(
                ComponentType.ReadOnly<ShipTag>(),
                ComponentType.ReadOnly<GhostOwner>());
            using var entities = query.ToEntityArray(Allocator.Temp);
            for (int i = 0; i < entities.Length; i++)
            {
                if (em.Exists(entities[i]))
                    TryCapture(em, entities[i]);
            }
        }

        /// <summary>Removes and returns the snapshot for this connection's player id.</summary>
        public static bool TryTake(int networkId, out MatchPlayerShipSnapshot snapshot, out string playerId)
        {
            snapshot = null;
            playerId = null;
            if (!TryGetPlayerId(networkId, out playerId))
                return false;
            if (!Snapshots.TryGetValue(playerId, out snapshot))
                return false;

            Snapshots.Remove(playerId);
            return snapshot != null;
        }

        public static void ReturnSnapshot(string playerId, MatchPlayerShipSnapshot snapshot)
        {
            if (_closed || string.IsNullOrEmpty(playerId) || snapshot == null)
                return;
            Snapshots[playerId] = snapshot;
        }

        /// <summary>Drops a saved ship without spawning it (start fresh).</summary>
        public static bool RemoveForNetworkId(int networkId)
        {
            if (!TryGetPlayerId(networkId, out string playerId))
                return false;
            return Snapshots.Remove(playerId);
        }

        /// <summary>
        /// Writes the snapshot onto a hull that <see cref="PlayerShipSpawn"/> just created,
        /// then recomputes chassis stats. Cargo and gear are stamped again after that
        /// recompute so caps update without wiping rockets, cards, or gem hold.
        /// </summary>
        public static void ApplyToSpawnedShip(
            EntityManager em,
            Entity ship,
            MatchPlayerShipSnapshot snapshot,
            int networkId)
        {
            if (!em.Exists(ship) || snapshot == null)
                return;

            var saved = snapshot.Ship;
            saved.AwaitingTeamSelection = false;
            saved.OverdriveLockout = false;
            em.SetComponentData(ship, saved);
            WriteLoadoutGearAndStats(em, ship, snapshot);

            bool restoredPreviousHull = false;
            if (snapshot.HasMega && snapshot.Mega.IsMega && em.HasComponent<MegaShipState>(ship))
            {
                em.SetComponentData(ship, snapshot.Mega);
                bool megaKept = MegaShipPlanetLogic.TryOccupySlot(
                    em,
                    snapshot.Mega.StorePlanetId,
                    snapshot.Mega.MegaSlotIndex,
                    networkId);
                if (!megaKept)
                {
                    // Bay was taken while they were gone. Death-restore path writes the old L6.
                    MegaShipStatApplyLogic.RestorePreviousHull(em, ship);
                    restoredPreviousHull = true;
                }
            }

            if (!restoredPreviousHull)
            {
                ShipStatApplyLogic.ApplyToShip(
                    em,
                    ship,
                    saved.Team,
                    saved.ShipLevel,
                    saved.BranchIndex);
            }

            RestampAfterStatApply(em, ship, snapshot, keepSavedIdentity: !restoredPreviousHull);
            WriteLoadoutGearAndStats(em, ship, snapshot);
        }

        /// <summary>Puts the returning player back on their team even when that team filled up.</summary>
        public static void IncrementTeamCount(EntityManager em, TeamId teamId)
        {
            if (teamId == TeamId.None)
                return;

            using var teamQuery = em.CreateEntityQuery(ComponentType.ReadWrite<TeamStateSingleton>());
            if (teamQuery.CalculateEntityCount() != 1)
                return;

            Entity teamEntity = teamQuery.GetSingletonEntity();
            var team = em.GetComponentData<TeamStateSingleton>(teamEntity);
            switch (teamId)
            {
                case TeamId.TeamA: team.TeamACount += 1; break;
                case TeamId.TeamB: team.TeamBCount += 1; break;
                case TeamId.TeamC: team.TeamCCount += 1; break;
                case TeamId.TeamD: team.TeamDCount += 1; break;
                case TeamId.TeamE: team.TeamECount += 1; break;
            }

            em.SetComponentData(teamEntity, team);
        }

        static bool TryRead(EntityManager em, Entity ship, out MatchPlayerShipSnapshot snapshot)
        {
            snapshot = null;
            if (!em.HasComponent<ShipState>(ship))
                return false;

            ShipState shipState = em.GetComponentData<ShipState>(ship);
            if (shipState.Team == TeamId.None || shipState.AwaitingTeamSelection || shipState.IsEliminated)
                return false;

            snapshot = new MatchPlayerShipSnapshot { Ship = shipState };
            if (em.HasComponent<ShipLoadoutState>(ship))
            {
                snapshot.HasLoadout = true;
                snapshot.Loadout = em.GetComponentData<ShipLoadoutState>(ship);
            }

            if (em.HasComponent<ShipAttributeUpgradeState>(ship))
            {
                snapshot.HasAttributes = true;
                snapshot.Attributes = em.GetComponentData<ShipAttributeUpgradeState>(ship);
            }

            if (em.HasComponent<ShipMatchStats>(ship))
            {
                snapshot.HasMatchStats = true;
                snapshot.MatchStats = em.GetComponentData<ShipMatchStats>(ship);
            }

            if (em.HasComponent<ShipAccentColors>(ship))
            {
                snapshot.HasAccents = true;
                snapshot.Accents = em.GetComponentData<ShipAccentColors>(ship);
            }

            if (em.HasComponent<MegaShipState>(ship))
            {
                snapshot.HasMega = true;
                snapshot.Mega = em.GetComponentData<MegaShipState>(ship);
            }

            snapshot.Cards = CopyBuffer<EquippedCardElement>(em, ship);
            snapshot.Equipment = CopyBuffer<EquippedEquipmentElement>(em, ship);
            return true;
        }

        static void WriteLoadoutGearAndStats(EntityManager em, Entity ship, MatchPlayerShipSnapshot snapshot)
        {
            if (snapshot.HasLoadout)
                SetOrAdd(em, ship, snapshot.Loadout);
            if (snapshot.HasAttributes)
                SetOrAdd(em, ship, snapshot.Attributes);
            if (snapshot.HasMatchStats)
                SetOrAdd(em, ship, snapshot.MatchStats);
            if (snapshot.HasAccents)
                SetOrAdd(em, ship, snapshot.Accents);

            WriteBuffer(em, ship, snapshot.Cards);
            WriteBuffer(em, ship, snapshot.Equipment);
        }

        static void RestampAfterStatApply(
            EntityManager em,
            Entity ship,
            MatchPlayerShipSnapshot snapshot,
            bool keepSavedIdentity)
        {
            if (!em.HasComponent<ShipState>(ship))
                return;

            var shipState = em.GetComponentData<ShipState>(ship);
            ShipState saved = snapshot.Ship;
            shipState.Team = saved.Team;
            if (keepSavedIdentity)
            {
                shipState.ShipLevel = saved.ShipLevel;
                shipState.BranchIndex = saved.BranchIndex;
                shipState.ShipFamilyConfigIndex = saved.ShipFamilyConfigIndex;
                shipState.HullBulletBankIndex = saved.HullBulletBankIndex;
            }
            shipState.CurrentGems = math.min(saved.CurrentGems, math.max(0f, shipState.GemCapacity));
            shipState.CurrentPeople = math.min(saved.CurrentPeople, math.max(0, shipState.PeopleCapacity));
            shipState.CurrentEnergy = math.min(saved.CurrentEnergy, math.max(0f, shipState.MaxEnergy));
            if (saved.IsDead)
            {
                shipState.Health = 0f;
                shipState.IsDead = true;
            }
            else
            {
                shipState.Health = math.clamp(saved.Health, 0f, math.max(1f, shipState.MaxHealth));
                shipState.IsDead = false;
            }

            shipState.IsEliminated = false;
            shipState.AwaitingTeamSelection = false;
            shipState.OverdriveLockout = false;
            em.SetComponentData(ship, shipState);
        }

        static TElement[] CopyBuffer<TElement>(EntityManager em, Entity ship)
            where TElement : unmanaged, IBufferElementData
        {
            if (!em.HasBuffer<TElement>(ship))
                return Array.Empty<TElement>();

            DynamicBuffer<TElement> buffer = em.GetBuffer<TElement>(ship);
            int count = math.min(buffer.Length, MatchPlayerShipSnapshot.MaxRows);
            if (count <= 0)
                return Array.Empty<TElement>();

            var copy = new TElement[count];
            for (int i = 0; i < count; i++)
                copy[i] = buffer[i];
            return copy;
        }

        static void WriteBuffer<TElement>(EntityManager em, Entity ship, TElement[] rows)
            where TElement : unmanaged, IBufferElementData
        {
            DynamicBuffer<TElement> buffer = em.HasBuffer<TElement>(ship)
                ? em.GetBuffer<TElement>(ship)
                : em.AddBuffer<TElement>(ship);
            buffer.Clear();
            if (rows == null)
                return;

            for (int i = 0; i < rows.Length && i < MatchPlayerShipSnapshot.MaxRows; i++)
                buffer.Add(rows[i]);
        }

        static void SetOrAdd<T>(EntityManager em, Entity ship, T value)
            where T : unmanaged, IComponentData
        {
            if (em.HasComponent<T>(ship))
                em.SetComponentData(ship, value);
            else
                em.AddComponentData(ship, value);
        }
    }
}

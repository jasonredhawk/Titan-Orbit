using System.Collections.Generic;
using TitanOrbit.Core;

namespace TitanOrbit.Systems
{
    /// <summary>
    /// Server-only in-memory ship progress for the current map instance (boot epoch + blueprint seed).
    /// Cleared when the map instance identity changes; not persisted across server processes or recycled instances.
    /// </summary>
    public static class MapInstanceShipProgressStore
    {
        private static long _boundBootEpochUtc;
        private static int _boundBlueprintSeed;
        private static bool _bound;

        private static readonly Dictionary<string, PlayerShipProgressSnapshot> SnapshotsByAuthPlayerId =
            new Dictionary<string, PlayerShipProgressSnapshot>();

        private static readonly Dictionary<ulong, string> AuthPlayerIdByClientId = new Dictionary<ulong, string>();

        public static long BoundBootEpochUtc => _boundBootEpochUtc;
        public static int BoundBlueprintSeed => _boundBlueprintSeed;

        /// <summary>
        /// Binds the store to a map instance. Clears all saved ships when the instance identity changes.
        /// </summary>
        public static void BindMapInstance(long bootEpochUtc, int blueprintSeed)
        {
            if (_bound && _boundBootEpochUtc == bootEpochUtc && _boundBlueprintSeed == blueprintSeed)
                return;

            _boundBootEpochUtc = bootEpochUtc;
            _boundBlueprintSeed = blueprintSeed;
            _bound = true;
            SnapshotsByAuthPlayerId.Clear();
            AuthPlayerIdByClientId.Clear();
        }

        public static void RegisterClientAuthId(ulong clientId, string authPlayerId)
        {
            if (string.IsNullOrWhiteSpace(authPlayerId))
                authPlayerId = FallbackAuthKey(clientId);
            AuthPlayerIdByClientId[clientId] = authPlayerId;
        }

        public static void UnregisterClient(ulong clientId)
        {
            AuthPlayerIdByClientId.Remove(clientId);
        }

        public static string ResolveAuthPlayerId(ulong clientId)
        {
            if (AuthPlayerIdByClientId.TryGetValue(clientId, out string id) && !string.IsNullOrEmpty(id))
                return id;
            return FallbackAuthKey(clientId);
        }

        public static string NormalizeAuthPlayerId(string authPlayerId, ulong clientId)
        {
            return string.IsNullOrWhiteSpace(authPlayerId) ? FallbackAuthKey(clientId) : authPlayerId.Trim();
        }

        public static void SaveSnapshot(string authPlayerId, in PlayerShipProgressSnapshot snapshot)
        {
            if (!_bound || string.IsNullOrEmpty(authPlayerId))
                return;
            SnapshotsByAuthPlayerId[authPlayerId] = snapshot;
        }

        public static bool TryGetSnapshot(string authPlayerId, out PlayerShipProgressSnapshot snapshot)
        {
            snapshot = default;
            if (!_bound || string.IsNullOrEmpty(authPlayerId))
                return false;
            return SnapshotsByAuthPlayerId.TryGetValue(authPlayerId, out snapshot);
        }

        public static void ClearSnapshot(string authPlayerId)
        {
            if (!_bound || string.IsNullOrEmpty(authPlayerId))
                return;
            SnapshotsByAuthPlayerId.Remove(authPlayerId);
        }

        private static string FallbackAuthKey(ulong clientId) => "client:" + clientId.ToString();
    }

    /// <summary>Serializable ship loadout for one player within a single map instance.</summary>
    public readonly struct PlayerShipProgressSnapshot
    {
        public readonly int ShipLevel;
        public readonly int BranchIndex;
        public readonly int ChassisIndex;
        public readonly string ChassisId;
        public readonly TeamManager.Team Team;
        public readonly int AttrFirePower;
        public readonly int AttrBulletSpeed;
        public readonly int AttrMaxHealth;
        public readonly int AttrHealthRegen;
        public readonly int AttrEnergyCapacity;
        public readonly int AttrEnergyRegen;
        public readonly int AttrMovementSpeed;
        public readonly int AttrRotationSpeed;
        public readonly int AttrGemCapacity;
        public readonly int AttrPeopleCapacity;
        public readonly string[] CardIds;
        public readonly int SmallRockets;
        public readonly int LargeRockets;
        public readonly int SmallMines;
        public readonly int LargeMines;
        public readonly float CurrentHealth;
        public readonly float CurrentGems;
        public readonly float CurrentPeople;
        public readonly float CurrentEnergy;

        public PlayerShipProgressSnapshot(
            int shipLevel,
            int branchIndex,
            int chassisIndex,
            string chassisId,
            TeamManager.Team team,
            int attrFirePower,
            int attrBulletSpeed,
            int attrMaxHealth,
            int attrHealthRegen,
            int attrEnergyCapacity,
            int attrEnergyRegen,
            int attrMovementSpeed,
            int attrRotationSpeed,
            int attrGemCapacity,
            int attrPeopleCapacity,
            string[] cardIds,
            int smallRockets,
            int largeRockets,
            int smallMines,
            int largeMines,
            float currentHealth,
            float currentGems,
            float currentPeople,
            float currentEnergy)
        {
            ShipLevel = shipLevel;
            BranchIndex = branchIndex;
            ChassisIndex = chassisIndex;
            ChassisId = chassisId ?? string.Empty;
            Team = team;
            AttrFirePower = attrFirePower;
            AttrBulletSpeed = attrBulletSpeed;
            AttrMaxHealth = attrMaxHealth;
            AttrHealthRegen = attrHealthRegen;
            AttrEnergyCapacity = attrEnergyCapacity;
            AttrEnergyRegen = attrEnergyRegen;
            AttrMovementSpeed = attrMovementSpeed;
            AttrRotationSpeed = attrRotationSpeed;
            AttrGemCapacity = attrGemCapacity;
            AttrPeopleCapacity = attrPeopleCapacity;
            CardIds = cardIds ?? System.Array.Empty<string>();
            SmallRockets = smallRockets;
            LargeRockets = largeRockets;
            SmallMines = smallMines;
            LargeMines = largeMines;
            CurrentHealth = currentHealth;
            CurrentGems = currentGems;
            CurrentPeople = currentPeople;
            CurrentEnergy = currentEnergy;
        }
    }
}

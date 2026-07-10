using System.Collections.Generic;
using TitanOrbit.Core;
using TitanOrbit.Data;
using TitanOrbit.ECS;
using TitanOrbit.Entities;
using TitanOrbit.Game;
using TitanOrbit.UI;
using UnityEngine;

namespace TitanOrbit.Entities
{
    /// <summary>
    /// Lightweight MonoBehaviour adapter that mirrors local ship ECS state into the legacy <c>Starship</c> API
    /// expected by <see cref="OrbitStationUI"/>. Lives in a DontDestroyOnLoad "OrbitStationShipView" object —
    /// not a networked ghost. Refreshed via <see cref="SyncFromEcs"/> when the orbit station opens.
    /// </summary>
    public class Starship : MonoBehaviour
    {
        const ulong FakeNetworkObjectId = 1;

        public TeamManager.Team ShipTeam { get; set; } = TeamManager.Team.None;
        public int ShipLevel { get; set; } = 1;
        public int BranchIndex { get; set; }
        public string CurrentChassisId { get; set; }
        public bool GemMoonDocked { get; set; }
        public bool WantToDepositGems { get; private set; }
        public ulong NetworkObjectId => FakeNetworkObjectId;
        public ShipData CurrentShipData { get; set; }

        public int SlotCount => Mathf.Max(1, ShipLevel);
        public int EquipmentSlotCount => SlotCount;
        public bool HasEmptySlot => EquippedCards == null || EquippedCards.Count < SlotCount;
        public bool HasEmptyEquipmentSlot => EquippedEquipment == null || EquippedEquipment.Count < EquipmentSlotCount;

        public List<CardData> EquippedCards { get; } = new List<CardData>();
        public List<EquippedEquipmentEntry> EquippedEquipment { get; } = new List<EquippedEquipmentEntry>();

        /// <summary>Singleton accessor — finds existing view or creates the DontDestroyOnLoad adapter.</summary>
        public static Starship GetOrCreate()
        {
            var existing = FindFirstObjectByType<Starship>();
            if (existing != null)
                return existing;

            var go = new GameObject("OrbitStationShipView");
            DontDestroyOnLoad(go);
            return go.AddComponent<Starship>();
        }

        /// <summary>
        /// Pulls team, level, branch, moon-dock, deposit intent, chassis id, and equipment buffers from ECS.
        /// Called by orbit station UI each refresh frame.
        /// </summary>
        public void SyncFromEcs(int storePlanetId)
        {
            if (!EcsGameBridge.TryGetLocalShipState(out var ship))
                return;

            ShipTeam = TeamManager.FromTeamId(ship.Team);
            ShipLevel = Mathf.Max(1, ship.ShipLevel);
            BranchIndex = 0;

            if (EcsGameBridge.TryGetLocalShipLoadout(out var loadout))
                BranchIndex = loadout.BranchIndex;

            if (EcsGameBridge.TryGetLocalShipMoonDockState(out var moonDock))
                GemMoonDocked = moonDock.MoonPlanetId != 0
                    && moonDock.LandingProgress >= GemEconomyConstants.MoonLandingCompleteThreshold;

            if (EcsGameBridge.TryGetLocalShipInput(out var input))
                WantToDepositGems = input.WantDepositGems;

            if (EcsGameBridge.TryGetLocalShipDepositIntent(out bool depositIntent))
                WantToDepositGems = depositIntent;

            ResolveChassisId(storePlanetId);
            SyncLoadoutBuffers();
        }

        /// <summary>Resolves <see cref="CurrentChassisId"/> from planet family config and upgrade-tree ladder slot.</summary>
        void ResolveChassisId(int storePlanetId)
        {
            CurrentChassisId = null;
            var config = Resources.Load<PlanetShipFamilyConfig>("PlanetShipFamilyConfig");
            if (config == null)
                config = Resources.Load<PlanetShipFamilyConfig>("Data/PlanetShipFamilyConfig");

            if (config != null && storePlanetId > 0)
            {
                bool isHomePlanet = false;
                int configIndex = -1;
                if (EcsGameBridge.TryGetPlanetStateByPlanetId(storePlanetId, out var planetState))
                {
                    isHomePlanet = planetState.IsHomePlanet;
                    if (planetState.IsHomePlanet)
                        configIndex = PlanetShipFamilyAssignment.HomeFamilyConfigIndex;
                    else if (planetState.ShipFamilyConfigIndex > 0)
                        configIndex = planetState.ShipFamilyConfigIndex;
                }

                CurrentChassisId = config.GetChassisIdForLadderSlot(
                    storePlanetId, ShipLevel, BranchIndex, isHomePlanet, configIndex);
            }
        }

        /// <summary>Copies equipped equipment buffer from visualization ECS world into legacy lists.</summary>
        void SyncLoadoutBuffers()
        {
            EquippedCards.Clear();
            EquippedEquipment.Clear();

            var world = EcsGameBridge.GetVisualizationWorld();
            if (world == null || !world.IsCreated)
                return;

            if (!EcsGameBridge.TryGetLocalShipEntityOnWorld(world, out var shipEntity))
                return;

            var em = world.EntityManager;
            if (em.HasBuffer<EquippedEquipmentElement>(shipEntity))
            {
                var buffer = em.GetBuffer<EquippedEquipmentElement>(shipEntity);
                for (int i = 0; i < buffer.Length; i++)
                {
                    var e = buffer[i];
                    EquippedEquipment.Add(new EquippedEquipmentEntry
                    {
                        itemType = e.ItemType,
                        componentId = e.ComponentId.ToString(),
                        remainingCharges = e.RemainingCharges,
                    });
                }
            }
        }

        /// <summary>Returns true when a ship component id is already in the equipped equipment buffer.</summary>
        public bool HasComponentEquipped(string componentId)
        {
            if (string.IsNullOrWhiteSpace(componentId))
                return false;

            for (int i = 0; i < EquippedEquipment.Count; i++)
            {
                var e = EquippedEquipment[i];
                if (e.IsShipComponent && string.Equals(e.ComponentId, componentId, System.StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            return false;
        }

        /// <summary>Forwards gem-deposit intent to server via <see cref="MoonOrbitRpcClient"/>.</summary>
        public void SetWantToDepositGemsServerRpc(bool wantDeposit)
        {
            WantToDepositGems = wantDeposit;
            MoonOrbitRpcClient.SetWantDepositGems(wantDeposit);
        }

        public void RemoveCardServerRpc(int slotIndex) { }

        public void RemoveEquipmentServerRpc(int slotIndex) { }

        public void UpdateEquippedComponentPlacementServerRpc(
            int slotIndex,
            float localPosX,
            float localPosY,
            float localPosZ,
            float localRotX,
            float localRotY,
            float localRotZ) { }
    }
}

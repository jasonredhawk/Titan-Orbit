using System.Collections.Generic;
using TitanOrbit.Core;
using TitanOrbit.Data;
using TitanOrbit.ECS;
using TitanOrbit.Entities;
using TitanOrbit.Game;
using TitanOrbit.Systems;
using TitanOrbit.UI;
using Unity.Entities;
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

        /// <summary>Last loadout fingerprint — skip buffer rebuild when equipment/cards unchanged.</summary>
        int _lastLoadoutFingerprint = int.MinValue;

        /// <summary>Singleton accessor — finds existing view or creates the DontDestroyOnLoad adapter.</summary>
        public static Starship GetOrCreate()
        {
            // --- Compute value ---
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
            // --- SyncFromEcs ---
            if (!EcsGameBridge.TryGetLocalShipState(out var ship))
                return;

            ShipTeam = TeamManager.FromTeamId(ship.Team);
            ShipLevel = Mathf.Max(1, ship.ShipLevel);
            // [NETCODE] ShipState.BranchIndex is the authoritative ghosted upgrade-tree branch.
            // Loadout.BranchIndex alone used to leave the tree highlighting branch 0 while the
            // correct hull (ShipState branch) was already on screen after a purchase.
            BranchIndex = Mathf.Max(0, ship.BranchIndex);

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

        /// <summary>
        /// Resolves <see cref="CurrentChassisId"/> from the ship's ghosted family + ladder slot.
        /// Uses <see cref="ShipState.ShipFamilyConfigIndex"/> (not the store planet) so the "current hull"
        /// identity stays correct while browsing another family's tree at a captured moon.
        /// </summary>
        void ResolveChassisId(int storePlanetId)
        {
            // --- Resolve value ---
            CurrentChassisId = null;
            var config = Resources.Load<PlanetShipFamilyConfig>("PlanetShipFamilyConfig");
            if (config == null)
                return;

            // Ship's owned family (0 = AstroEagle until a captured-planet purchase).
            int familyIndex = PlanetShipFamilyAssignment.HomeFamilyConfigIndex;
            if (EcsGameBridge.TryGetLocalShipState(out var shipState))
                familyIndex = shipState.ShipFamilyConfigIndex;

            bool isHomeFamily = familyIndex == PlanetShipFamilyAssignment.HomeFamilyConfigIndex;
            int planetIdHint = isHomeFamily ? 0 : Mathf.Max(1, storePlanetId);

            CurrentChassisId = config.GetChassisIdForLadderSlot(
                planetIdHint, ShipLevel, BranchIndex, isHomeFamily, familyIndex);
        }

        /// <summary>
        /// Copies equipped equipment + card buffers from ECS into legacy lists for orbit UI.
        /// Local Host reads ServerWorld so purchases applied directly there show immediately
        /// (ghost buffer replication can lag one snapshot behind the menu refresh).
        /// Skips rebuild when the equipment fingerprint is unchanged (avoids lag while menu is open).
        /// </summary>
        void SyncLoadoutBuffers()
        {
            // --- SyncLoadoutBuffers ---
            // [TITAN-ORBIT] Prefer authoritative ServerWorld on Local Host for store inventory.
            World world = null;
            if (EcsGameBridge.IsLocalHost() &&
                EcsGameBridge.ServerWorld != null &&
                EcsGameBridge.ServerWorld.IsCreated)
            {
                world = EcsGameBridge.ServerWorld;
            }
            else
            {
                world = EcsGameBridge.GetVisualizationWorld();
            }

            if (world == null || !world.IsCreated)
                return;

            if (!EcsGameBridge.TryGetLocalShipEntityOnWorld(world, out var shipEntity))
                return;

            var em = world.EntityManager;
            int fingerprint = ShipStatApplyLogic.ComputeEquippedLoadoutFingerprint(em, shipEntity);
            if (fingerprint == _lastLoadoutFingerprint)
                return;

            _lastLoadoutFingerprint = fingerprint;
            EquippedCards.Clear();
            EquippedEquipment.Clear();

            // --- Equipment (drones / rockets / mines / ship components) ---
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
                        itemLevel = e.ItemLevel,
                    });
                }
            }

            // --- Upgrade cards ---
            if (em.HasBuffer<EquippedCardElement>(shipEntity))
            {
                var cards = em.GetBuffer<EquippedCardElement>(shipEntity);
                for (int i = 0; i < cards.Length; i++)
                {
                    string cardId = cards[i].CardId.ToString();
                    if (string.IsNullOrWhiteSpace(cardId))
                        continue;

                    CardData card = null;
                    if (CardShopSystem.Instance != null)
                        card = CardShopSystem.Instance.GetCardByIdForShip(this, cardId);
                    if (card == null)
                        card = ShipStatApplyLogic.FindCardAnywhere(cardId);
                    if (card != null)
                        EquippedCards.Add(card);
                }
            }
        }

        /// <summary>Forces next SyncLoadoutBuffers to rebuild lists (after purchase / remove).</summary>
        public void InvalidateLoadoutCache() => _lastLoadoutFingerprint = int.MinValue;

        /// <summary>Returns true when a ship component id is already in the equipped equipment buffer.</summary>
        public bool HasComponentEquipped(string componentId)
        {
            // --- HasComponentEquipped ---
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

        /// <summary>Requests server removal of an equipped upgrade card (free discard).</summary>
        public void RemoveCardServerRpc(int slotIndex)
        {
            MoonOrbitRpcClient.RemoveEquippedCard(slotIndex);
        }

        /// <summary>Requests server removal of an equipped store item / component (free discard).</summary>
        public void RemoveEquipmentServerRpc(int slotIndex)
        {
            MoonOrbitRpcClient.RemoveEquippedEquipment(slotIndex);
        }

        /// <summary>
        /// [LEGACY] Component placement editor — not wired to ECS yet (buy uses default placement).
        /// </summary>
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

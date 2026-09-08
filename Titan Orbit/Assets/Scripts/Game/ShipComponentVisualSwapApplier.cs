using TitanOrbit.Core;
using TitanOrbit.Data;
using TitanOrbit.ECS;
using Unity.Entities;
using UnityEngine;

namespace TitanOrbit.Game
{
    /// <summary>
    /// Hybrid-proxy restyle: swaps cockpit / engine / wing meshes when moon-store extras
    /// come from another family, and swaps all weapon mounts when B cycles
    /// <see cref="ShipLoadoutState.RuntimeBulletIndex"/> to that family's bank.
    /// Host slot pose is kept; source authored scale is used; attribute + store grow
    /// still run afterward via <see cref="ShipComponentAttributeScaleApplier"/>.
    /// <para>
    /// [HYBRID] Presentation only. Covering colliders remesh the same way in
    /// <see cref="ShipHullColliderLogic"/>. MEGA hulls are skipped.
    /// </para>
    /// </summary>
    [DefaultExecutionOrder(90)]
    public class ShipComponentVisualSwapApplier : MonoBehaviour
    {
        const string StashChildName = ShipFamilyPartMatch.OriginalStashName;

        Entity _shipEntity;
        string _familyPrefix = "AstroEagle";
        ShipFamilyDefinition _hostFamily;
        TeamId _team;
        int _networkId;
        float _muzzleOffset = 1f;
        string _propulsionPrefix;
        ShipFamilyDefinition _propulsionFamily;
        ShipPropulsionVisualApplier.Settings _propulsionSettings;

        int _lastEquipmentKey = -1;
        int _lastBankIndex = int.MinValue;
        bool _lastHealing;
        bool _bound;

        Transform _stash;
        ShipComponentAttributeScaleApplier _scaleApplier;
        ShipPropulsionVisualApplier _propulsion;

        /// <summary>
        /// Links to the ship ghost and applies the current equipment + bank remaps
        /// before weapon collectors and attribute Bind (called from CreateShipProxy).
        /// </summary>
        public void Bind(
            Entity shipEntity,
            string familyPrefix,
            ShipFamilyDefinition hostFamily,
            TeamId team,
            int networkId,
            float muzzleOffset)
        {
            _shipEntity = shipEntity;
            if (!string.IsNullOrWhiteSpace(familyPrefix))
                _familyPrefix = familyPrefix.Trim();
            _hostFamily = hostFamily;
            _team = team;
            _networkId = networkId;
            _muzzleOffset = muzzleOffset;
            _lastEquipmentKey = -1;
            _lastBankIndex = int.MinValue;
            _lastHealing = false;
            _bound = true;
            EnsureStash();
            // [TITAN-ORBIT] Snapshot authored meshes now, before attribute grow Bind
            // (order 95) and before any store remap. Orbit-menu discard reads this stash
            // so the purchased mesh is replaced with the host part, not deleted.
            // Stashing on the first B-key copied already-grown guns; restore then
            // treated that size as the new base and the next Apply ballooned them.
            ShipComponentVisualSwapLogic.StashCurrentSlots(
                transform, _familyPrefix, _stash);
            TryApply(force: true);
        }

        /// <summary>
        /// Remembers propulsion Bind args so we can rebuild jets after a part remap.
        /// RebuildVfx then picks the purchased family's flame from <see cref="ShipPartVisualSource"/>.
        /// </summary>
        public void CapturePropulsionBind(
            string familyPrefix,
            ShipPropulsionVisualApplier.Settings settings,
            ShipFamilyDefinition family)
        {
            _propulsionPrefix = familyPrefix;
            _propulsionSettings = settings;
            _propulsionFamily = family;
        }

        void LateUpdate()
        {
            if (_bound)
                TryApply(force: false);
        }

        void TryApply(bool force)
        {
            if (_shipEntity == Entity.Null)
                return;

            var world = EcsGameBridge.GetVisualizationWorld();
            if (world == null || !world.IsCreated)
                return;

            var em = world.EntityManager;
            if (!em.Exists(_shipEntity))
                return;
            if (em.HasComponent<MegaShipState>(_shipEntity)
                && em.GetComponentData<MegaShipState>(_shipEntity).IsMega)
                return;

            int equipmentKey = ShipComponentStoreVisualScaleLogic.ComputeEquipmentScaleKey(em, _shipEntity);
            int bankIndex = 0;
            bool healing = false;
            if (em.HasComponent<ShipLoadoutState>(_shipEntity))
            {
                var loadout = em.GetComponentData<ShipLoadoutState>(_shipEntity);
                bankIndex = loadout.RuntimeBulletIndex;
                healing = loadout.HealingBulletsActive;
            }

            bool equipmentChanged = force || equipmentKey != _lastEquipmentKey;
            bool bankChanged = bankIndex != _lastBankIndex || healing != _lastHealing;
            if (!force && !equipmentChanged && !bankChanged)
                return;

            _lastEquipmentKey = equipmentKey;
            _lastBankIndex = bankIndex;
            _lastHealing = healing;

            // B-key only swaps guns. Restoring every stashed engine/cockpit and
            // rebinding jets was recapturing their already-grown scale each cycle.
            bool remapNonWeapons = equipmentChanged;
            bool changed = ShipComponentVisualSwapLogic.ApplyFromShip(
                transform,
                em,
                _shipEntity,
                _familyPrefix,
                _hostFamily,
                _team,
                _stash,
                stripColliders: true,
                remapNonWeapons);

            // Discard restores the host mesh (stash / host prefab) then rebuilds
            // jets so leftover purchased flames do not stay on the original mount.
            if (!changed && !force && !equipmentChanged)
                return;

            RefreshDependents(weaponOnly: !remapNonWeapons);
        }

        void RefreshDependents(bool weaponOnly)
        {
            ShipWeaponMountCollector.EnsureWeaponMountsOnHierarchy(transform, _muzzleOffset);
            if (!weaponOnly)
                ShipWingTractorBeamCollector.EnsureWingTractorBeamsOnHierarchy(transform);

            if (_scaleApplier == null)
                _scaleApplier = GetComponent<ShipComponentAttributeScaleApplier>();
            if (_scaleApplier != null)
                _scaleApplier.ForceRebuildAfterHierarchyChange();

            if (!weaponOnly)
            {
                if (_propulsion == null)
                    _propulsion = GetComponent<ShipPropulsionVisualApplier>();
                if (_propulsion != null && !string.IsNullOrEmpty(_propulsionPrefix))
                    _propulsion.Bind(_shipEntity, _propulsionPrefix, _propulsionSettings, _propulsionFamily);
            }

            if (_networkId > 0)
                ShipWeaponProxyRegistry.Register(_networkId, transform);
        }

        void EnsureStash()
        {
            if (_stash != null)
                return;

            Transform existing = transform.Find(StashChildName);
            if (existing != null)
            {
                _stash = existing;
                return;
            }

            var go = new GameObject(StashChildName);
            go.transform.SetParent(transform, false);
            go.SetActive(false);
            _stash = go.transform;
        }
    }
}

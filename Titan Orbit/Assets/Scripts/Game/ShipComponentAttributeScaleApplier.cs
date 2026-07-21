using TitanOrbit.Data;
using TitanOrbit.ECS;
using Unity.Entities;
using UnityEngine;

namespace TitanOrbit.Game
{
    /// <summary>
    /// Client-side component mesh scaling on ship proxies when bottom-bar attribute upgrades change.
    /// Watches ShipAttributeUpgradeState on the linked ship entity and applies scale factors via
    /// ShipComponentAttributeScaleLogic. Attached by EcsWorldVisualizer; <b>cosmetic only</b> —
    /// does not change firePower, fireRate, or any sim stats (those use authored prefab scale +
    /// numeric attribute multipliers in <c>ShipWeaponMountCombatLogic</c> / ShipStatApplyLogic).
    /// </summary>
    [DefaultExecutionOrder(95)]
    public class ShipComponentAttributeScaleApplier : MonoBehaviour
    {
        Entity _shipEntity;
        string _familyPrefix = "AstroEagle";
        bool _hasWeaponComponentEnergy;
        bool _initialized;

        ShipComponentAttributeScaleLogic.ScaleGroup _cockpit;
        ShipComponentAttributeScaleLogic.ScaleGroup _wing;
        ShipComponentAttributeScaleLogic.ScaleGroup _weapon;
        ShipComponentAttributeScaleLogic.ScaleGroup _engine;
        ShipComponentAttributeScaleLogic.ScaleGroup _thruster;
        ShipComponentAttributeScaleLogic.ScaleGroup _part;

        ShipAttributeUpgradeState _lastApplied;

        /// <summary>Links to ship entity, caches chassis transform groups, applies initial scale.</summary>
        public void Bind(Entity shipEntity, string familyPrefix, ShipFamilyDefinition family)
        {
            // --- Bind ---
            _shipEntity = shipEntity;
            if (!string.IsNullOrWhiteSpace(familyPrefix))
                _familyPrefix = familyPrefix.Trim();
            _hasWeaponComponentEnergy = ShipComponentAttributeScaleLogic.FamilyHasWeaponComponentEnergy(family);
            _lastApplied = default;
            RebuildCache();
        }

        /// <summary>Scans hull hierarchy for component transforms and stores base scales/positions.</summary>
        void RebuildCache()
        {
            // --- Rebuild cache ---
            var stats = ChassisComponentStats.FromTransform(transform, _familyPrefix);

            _cockpit = ShipComponentAttributeScaleLogic.BuildGroup(stats.cockpitTransforms);
            _wing = ShipComponentAttributeScaleLogic.BuildGroup(stats.wingTransforms);
            _weapon = ShipComponentAttributeScaleLogic.BuildGroup(stats.weaponTransforms);
            _engine = ShipComponentAttributeScaleLogic.BuildGroup(stats.engineTransforms);
            _thruster = ShipComponentAttributeScaleLogic.BuildGroup(stats.thrusterTransforms);
            _part = ShipComponentAttributeScaleLogic.BuildGroup(stats.partTransforms);

            Transform hull = transform.Find("Hull");
            if (hull != null)
            {
                _cockpit.Transforms.Add(hull);
                _cockpit.BaseScales.Add(hull.localScale);
                _cockpit.BasePositions.Add(hull.localPosition);
            }

            _initialized = _cockpit.Transforms.Count > 0
                || _wing.Transforms.Count > 0
                || _weapon.Transforms.Count > 0
                || _engine.Transforms.Count > 0
                || _thruster.Transforms.Count > 0
                || _part.Transforms.Count > 0;

            TryApplyAttributeScale(force: true);
        }

        void TryApplyAttributeScale(bool force = false)
        {
            // --- Attempt resolution ---
            if (!_initialized || _shipEntity == Entity.Null)
                return;

            var world = EcsGameBridge.GetVisualizationWorld();
            if (world == null || !world.IsCreated)
                return;

            var em = world.EntityManager;
            if (!em.Exists(_shipEntity))
                return;

            if (em.HasComponent<ShipState>(_shipEntity))
            {
                var ship = em.GetComponentData<ShipState>(_shipEntity);
                if (ship.IsDead)
                    return;
            }

            if (!em.HasComponent<ShipAttributeUpgradeState>(_shipEntity))
                return;

            var attrs = em.GetComponentData<ShipAttributeUpgradeState>(_shipEntity);
            // [STANDARD] Skip work when ghost state unchanged since last frame.
            if (!force && attrs.Equals(_lastApplied))
                return;

            _lastApplied = attrs;
            ShipComponentAttributeScaleLogic.Apply(
                attrs,
                _cockpit,
                _wing,
                _weapon,
                _engine,
                _thruster,
                _part,
                _hasWeaponComponentEnergy);
        }

        void LateUpdate() => TryApplyAttributeScale();
    }
}

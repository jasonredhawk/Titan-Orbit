using System.Collections.Generic;
using TitanOrbit.Data;
using TitanOrbit.ECS;
using Unity.Entities;
using Unity.Mathematics;
using Unity.NetCode;
using UnityEngine;

namespace TitanOrbit.Game
{
    /// <summary>
    /// Client-side component mesh scaling on ship proxies when bottom-bar attribute upgrades change,
    /// when moon-dock store extras of the same part type raise visual scale, when the ship is
    /// inside a friendly territory triangle (Engine/Thruster mounts grow), and when OVERDRIVE is
    /// active (Thruster mounts bloom with the baked overdrive speed mul).
    /// Attached by EcsWorldVisualizer.
    /// <para>
    /// Growth rates come from <c>ShipFamilyPartCalcProfileSet.asset</c> Part Profiles
    /// (<c>perLevel / base</c> via <see cref="ShipComponentAttributeScaleLogic.BuildRatesFromProfileSet"/>).
    /// Collider rebuilds via <see cref="ShipHullColliderLogic"/> use attribute size only
    /// (territory / overdrive are presentation-only).
    /// </para>
    /// <para>
    /// [TITAN-ORBIT] Territory boosts are <b>smoothed</b>; OVERDRIVE thruster bloom <b>snaps</b>
    /// with <see cref="ShipOverdriveTuning.IsBurstActive"/> (same rule as the motor — pending
    /// Shift/Thrust + ghosted energy/lockout). Thruster VFX is <b>not</b> ForceRefresh'd on boost
    /// changes — that restart was the blink; <see cref="ShipPropulsionVisualApplier"/> self-heals.
    /// </para>
    /// </summary>
    [DefaultExecutionOrder(95)]
    public class ShipComponentAttributeScaleApplier : MonoBehaviour
    {
        /// <summary>How fast territory display muls approach their targets (mult units per second).</summary>
        const float TerritoryScaleTransitionPerSecond = 2.5f;

        /// <summary>Treat display and target as equal within this (avoid endless Apply writes).</summary>
        const float BoostDisplayEpsilon = 0.002f;

        /// <summary>Linked ship ghost entity — source of ShipAttributeUpgradeState.</summary>
        Entity _shipEntity;
        /// <summary>USC family prefix for legacy token filter (e.g. AstroEagle).</summary>
        string _familyPrefix = "AstroEagle";
        bool _initialized;

        /// <summary>
        /// Cached ProfileSet <c>perLevel/base</c> fractions per part group (version 1).
        /// Rebuilt on Bind / RebuildCache — not every LateUpdate.
        /// </summary>
        ShipComponentAttributeScaleLogic.ProfileScaleRates _rates;

        ShipComponentAttributeScaleLogic.ScaleGroup _cockpit;
        ShipComponentAttributeScaleLogic.ScaleGroup _wing;
        ShipComponentAttributeScaleLogic.ScaleGroup _weapon;
        ShipComponentAttributeScaleLogic.ScaleGroup _engine;
        ShipComponentAttributeScaleLogic.ScaleGroup _thruster;
        ShipComponentAttributeScaleLogic.ScaleGroup _tail;
        ShipComponentAttributeScaleLogic.ScaleGroup _part;

        ShipAttributeUpgradeState _lastApplied;

        /// <summary>
        /// Last equipment-buffer hash from <see cref="ShipComponentStoreVisualScaleLogic"/>.
        /// -1 means "never applied" so Bind always writes once.
        /// </summary>
        int _lastStoreKey = -1;

        /// <summary>Instant target from ECS / graph cache (may jump).</summary>
        float _targetTerritoryMult = 1f;
        /// <summary>Instant OVERDRIVE target (1 or baked speed mul).</summary>
        float _targetOverdriveMult = 1f;

        /// <summary>Smoothed territory mul actually applied to meshes this frame.</summary>
        float _displayTerritoryMult = 1f;
        /// <summary>Smoothed overdrive mul actually applied to thruster meshes.</summary>
        float _displayOverdriveMult = 1f;

        /// <summary>Last display muls written to Transform — skip Apply when unchanged.</summary>
        float _lastAppliedTerritoryMult = -1f;
        float _lastAppliedOverdriveMult = -1f;

        /// <summary>
        /// Cached propulsion applier on the same hull proxy. Null until first upgrade apply
        /// that needs a VFX refresh after mount scale (attribute grow only — not boost lerp).
        /// </summary>
        ShipPropulsionVisualApplier _propulsionVisual;

        /// <summary>Links to ship entity, caches chassis transform groups + ProfileSet rates, applies initial scale.</summary>
        public void Bind(Entity shipEntity, string familyPrefix, ShipFamilyDefinition family)
        {
            _shipEntity = shipEntity;
            if (!string.IsNullOrWhiteSpace(familyPrefix))
                _familyPrefix = familyPrefix.Trim();
            // Family is unused for rates — ProfileSet Part Profiles are the shared source of truth.
            _ = family;
            _lastApplied = default;
            _lastStoreKey = -1;
            _targetTerritoryMult = 1f;
            _targetOverdriveMult = 1f;
            _displayTerritoryMult = 1f;
            _displayOverdriveMult = 1f;
            _lastAppliedTerritoryMult = -1f;
            _lastAppliedOverdriveMult = -1f;
            RebuildCache();
        }

        /// <summary>
        /// Re-scans part groups after a mesh swap. Transforms that still exist keep their
        /// authored bind-time base scale/position so B-key weapon swaps cannot compound
        /// grow onto engines / wings. New instances (the swapped mesh) use the scale they
        /// were just given (source authored size).
        /// </summary>
        public void ForceRebuildAfterHierarchyChange()
        {
            _lastApplied = default;
            _lastStoreKey = -1;
            RebuildCache(preserveLiveAuthoredBases: true);
        }

        /// <summary>
        /// Scans hull hierarchy via shared <see cref="ShipComponentAttributeScaleLogic.BuildGroupsFromHierarchy"/>
        /// (same grouping as PhysicsCollider bake), loads ProfileSet rates, stores base scales/positions.
        /// </summary>
        /// <param name="preserveLiveAuthoredBases">
        /// When true, reuse BaseScale/BasePosition for Transform instances that survived the
        /// swap. Without this, RebuildCache would snapshot already-grown localScale and the
        /// next Apply would multiply again (engines ballooning on every B press).
        /// </param>
        void RebuildCache(bool preserveLiveAuthoredBases = false)
        {
            // --- Remember authored bases on live instances before the rescan ---
            Dictionary<int, Vector3> keptScales = null;
            Dictionary<int, Vector3> keptPositions = null;
            if (preserveLiveAuthoredBases)
            {
                keptScales = new Dictionary<int, Vector3>(32);
                keptPositions = new Dictionary<int, Vector3>(32);
                SnapshotAuthoredBases(_cockpit, keptScales, keptPositions);
                SnapshotAuthoredBases(_wing, keptScales, keptPositions);
                SnapshotAuthoredBases(_weapon, keptScales, keptPositions);
                SnapshotAuthoredBases(_engine, keptScales, keptPositions);
                SnapshotAuthoredBases(_thruster, keptScales, keptPositions);
                SnapshotAuthoredBases(_tail, keptScales, keptPositions);
                SnapshotAuthoredBases(_part, keptScales, keptPositions);
            }

            // --- ProfileSet percent-of-base rates (version 1) ---
            var profileSet = ShipFamilyPartCalcProfileSet.LoadShared();
            _rates = ShipComponentAttributeScaleLogic.BuildRatesFromProfileSet(profileSet);

            // --- Same USC groups as ShipHullColliderLogic bake ---
            ShipComponentAttributeScaleLogic.BuildGroupsFromHierarchy(
                transform,
                _familyPrefix,
                out _cockpit,
                out _wing,
                out _weapon,
                out _engine,
                out _thruster,
                out _tail,
                out _part);

            if (preserveLiveAuthoredBases && keptScales != null)
            {
                RestoreAuthoredBases(ref _cockpit, keptScales, keptPositions);
                RestoreAuthoredBases(ref _wing, keptScales, keptPositions);
                RestoreAuthoredBases(ref _weapon, keptScales, keptPositions);
                RestoreAuthoredBases(ref _engine, keptScales, keptPositions);
                RestoreAuthoredBases(ref _thruster, keptScales, keptPositions);
                RestoreAuthoredBases(ref _tail, keptScales, keptPositions);
                RestoreAuthoredBases(ref _part, keptScales, keptPositions);
            }

            _initialized = (_cockpit.Transforms != null && _cockpit.Transforms.Count > 0)
                || (_wing.Transforms != null && _wing.Transforms.Count > 0)
                || (_weapon.Transforms != null && _weapon.Transforms.Count > 0)
                || (_engine.Transforms != null && _engine.Transforms.Count > 0)
                || (_thruster.Transforms != null && _thruster.Transforms.Count > 0)
                || (_tail.Transforms != null && _tail.Transforms.Count > 0)
                || (_part.Transforms != null && _part.Transforms.Count > 0);

            TryApplyAttributeScale(force: true);
        }

        /// <summary>Copies bind-time bases keyed by Transform instance id.</summary>
        static void SnapshotAuthoredBases(
            in ShipComponentAttributeScaleLogic.ScaleGroup group,
            Dictionary<int, Vector3> scales,
            Dictionary<int, Vector3> positions)
        {
            if (group.Transforms == null || scales == null)
                return;

            for (int i = 0; i < group.Transforms.Count; i++)
            {
                Transform t = group.Transforms[i];
                if (t == null)
                    continue;
                int id = t.GetInstanceID();
                if (i < group.BaseScales.Count)
                    scales[id] = group.BaseScales[i];
                if (positions != null && i < group.BasePositions.Count)
                    positions[id] = group.BasePositions[i];
            }
        }

        /// <summary>
        /// Puts saved authored bases back on surviving instances. New swapped meshes
        /// keep the localScale captured from the template (not in the snapshot).
        /// </summary>
        static void RestoreAuthoredBases(
            ref ShipComponentAttributeScaleLogic.ScaleGroup group,
            Dictionary<int, Vector3> scales,
            Dictionary<int, Vector3> positions)
        {
            if (group.Transforms == null || scales == null)
                return;

            for (int i = 0; i < group.Transforms.Count; i++)
            {
                Transform t = group.Transforms[i];
                if (t == null)
                    continue;
                int id = t.GetInstanceID();
                if (scales.TryGetValue(id, out Vector3 baseScale) && i < group.BaseScales.Count)
                    group.BaseScales[i] = baseScale;
                if (positions != null
                    && positions.TryGetValue(id, out Vector3 basePos)
                    && i < group.BasePositions.Count)
                    group.BasePositions[i] = basePos;
            }
        }

        /// <summary>
        /// Resolves boost targets, smoothly approaches them, and writes mesh scales.
        /// Remotes use 1× territory / overdrive (only the local owner cache is meaningful).
        /// </summary>
        /// <param name="force">Snap display to targets and rebuild (Bind / cache rebuild).</param>
        void TryApplyAttributeScale(bool force = false)
        {
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

            // [NETCODE] Equipment buffer is ghosted — purchase / discard replicates here.
            int storeKey = ShipComponentStoreVisualScaleLogic.ComputeEquipmentScaleKey(em, _shipEntity);

            // --- Resolve instant targets (local owner only) ---
            float targetTerritory = 1f;
            float targetOverdrive = 1f;
            bool isLocalOwner =
                (em.HasComponent<GhostOwnerIsLocal>(_shipEntity) &&
                 em.IsComponentEnabled<GhostOwnerIsLocal>(_shipEntity)) ||
                em.HasComponent<LocalPlayerShipTag>(_shipEntity);
            if (isLocalOwner)
            {
                // Sticky graph cache — still can step when sticky expires; we smooth below.
                targetTerritory = Mathf.Max(1f, PlanetConnectionGraphCache.LocalOwnerTerritoryMult);

                // OVERDRIVE bloom: same IsBurstActive rule as the motor (pending Shift/Thrust +
                // ghosted energy / OverdriveLockout). Snapped below — not eased.
                if (em.HasComponent<ShipState>(_shipEntity))
                {
                    var ship = em.GetComponentData<ShipState>(_shipEntity);
                    bool thrustHeld;
                    bool shiftHeld;
                    if (ShipPendingInput.HasValue)
                    {
                        thrustHeld = ShipPendingInput.Latest.Thrust;
                        shiftHeld = ShipPendingInput.Latest.Overdrive;
                    }
                    else if (em.HasComponent<ShipInput>(_shipEntity))
                    {
                        var input = em.GetComponentData<ShipInput>(_shipEntity);
                        thrustHeld = input.Thrust;
                        shiftHeld = input.Overdrive;
                    }
                    else
                    {
                        thrustHeld = false;
                        shiftHeld = false;
                    }

                    // Presentation never runs the orbit motor — useOrbit false for bloom.
                    // MEGAs have no overdrive (Shift is heading-lock); never bloom them.
                    bool isMega = em.HasComponent<MegaShipState>(_shipEntity)
                        && em.GetComponentData<MegaShipState>(_shipEntity).IsMega;
                    if (!isMega && ShipOverdriveTuning.IsBurstActive(
                            shiftHeld,
                            thrustHeld,
                            useOrbit: false,
                            ship.CurrentEnergy,
                            ship.OverdriveLockout))
                    {
                        if (em.HasComponent<ShipMotorConfig>(_shipEntity))
                        {
                            var motor = em.GetComponentData<ShipMotorConfig>(_shipEntity);
                            targetOverdrive = ShipOverdriveTuning.ResolveSpeedMultiplier(motor);
                        }
                        else
                            targetOverdrive = ShipOverdriveTuning.SpeedMultiplier;
                    }
                }
            }

            _targetTerritoryMult = targetTerritory;
            _targetOverdriveMult = targetOverdrive;

            // --- Territory: smooth; OVERDRIVE: snap (synced with speed hard-cap on OD exit) ---
            if (force)
            {
                _displayTerritoryMult = _targetTerritoryMult;
                _displayOverdriveMult = _targetOverdriveMult;
            }
            else
            {
                float step = TerritoryScaleTransitionPerSecond * Time.deltaTime;
                _displayTerritoryMult = Mathf.MoveTowards(
                    _displayTerritoryMult, _targetTerritoryMult, step);
                _displayOverdriveMult = _targetOverdriveMult;
            }

            bool attrsSame = attrs.Equals(_lastApplied);
            bool storeSame = storeKey == _lastStoreKey;
            bool displaySettled =
                math.abs(_displayTerritoryMult - _targetTerritoryMult) < BoostDisplayEpsilon &&
                math.abs(_displayOverdriveMult - _targetOverdriveMult) < BoostDisplayEpsilon;
            bool displayUnchanged =
                math.abs(_displayTerritoryMult - _lastAppliedTerritoryMult) < BoostDisplayEpsilon &&
                math.abs(_displayOverdriveMult - _lastAppliedOverdriveMult) < BoostDisplayEpsilon;

            // Skip Transform writes when upgrades / store extras idle and display already matches.
            if (!force && attrsSame && storeSame && displaySettled && displayUnchanged)
                return;

            bool attrsChanged = !attrsSame;
            bool storeChanged = !storeSame;
            _lastApplied = attrs;
            _lastStoreKey = storeKey;
            _lastAppliedTerritoryMult = _displayTerritoryMult;
            _lastAppliedOverdriveMult = _displayOverdriveMult;

            var storeFactors = ShipComponentStoreVisualScaleLogic.ComputeForShip(em, _shipEntity);
            ShipComponentAttributeScaleLogic.Apply(
                attrs,
                _rates,
                _cockpit,
                _wing,
                _weapon,
                _engine,
                _thruster,
                _tail,
                _part,
                _displayTerritoryMult,
                _displayOverdriveMult,
                storeFactors);

            // --- VFX: never ForceRefresh on boost lerp ---
            // [TITAN-ORBIT] ForceRefreshEmission Stop+Clear+Play caused thruster blink whenever
            // territory/overdrive stepped. Propulsion LateUpdate already restarts stopped particles
            // while thrust is held. Only nudge after attribute / store mesh grow (upgrade tick / bind).
            if (attrsChanged || storeChanged || force)
                NotifyPropulsionAfterMountScale();
        }

        /// <summary>
        /// Asks the sibling propulsion applier to re-apply emission after attribute mount grow.
        /// Not used for territory/overdrive smooth transitions.
        /// </summary>
        void NotifyPropulsionAfterMountScale()
        {
            if (_propulsionVisual == null)
                _propulsionVisual = GetComponent<ShipPropulsionVisualApplier>();

            if (_propulsionVisual != null)
                _propulsionVisual.ForceRefreshEmission();
        }

        void LateUpdate() => TryApplyAttributeScale();
    }
}

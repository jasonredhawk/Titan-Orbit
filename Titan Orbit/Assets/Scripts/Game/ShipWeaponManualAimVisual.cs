using System.Collections.Generic;
using TitanOrbit.Core;
using TitanOrbit.Data;
using TitanOrbit.ECS;
using TitanOrbit.Generation;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;
using UnityEngine;

namespace TitanOrbit.Game
{
    /// <summary>
    /// Yaws regular-ship weapon meshes onto the owner's aim point when Game Manager
    /// → Regular Ships — Guns is Point, the move button is released, and that point
    /// is in front of the nose. A gliding hull still tracks. Holding the move button,
    /// a cursor behind the ship, and <see cref="GameManager.ParkLandedWeapons"/> while
    /// fully landed all restore the authored forward pose. Boosters stay parented to
    /// the hull. MEGA hulls keep <see cref="MegaShipWeaponVisualSync"/>.
    /// </summary>
    public static class ShipWeaponManualAimVisual
    {
        /// <summary>
        /// True when this regular hull's weapon meshes should point at the aim point.
        /// Local owners read <see cref="ShipInput"/> and only while the move button
        /// is released, including while the hull is still gliding. Remotes read ghosted
        /// <see cref="ShipState.ManualAimActive"/>, which the server sets on that same
        /// release. Forward mode, thrust, and a cursor behind the nose return false so
        /// tracers leave along the ship.
        /// </summary>
        public static bool IsMouseTracking(EntityManager em, Entity shipEntity)
        {
            return TryGetTrackedAimPoint(em, shipEntity, out _);
        }

        /// <summary>
        /// Aims each live weapon assembly at the cursor. Restores authored local
        /// rotation when the hull is a MEGA, guns are Forward, the move button is
        /// held, the cursor is behind the nose, or the aim sample is missing.
        /// </summary>
        public static void Apply(EntityManager em, Entity shipEntity, GameObject proxy)
        {
            if (proxy == null || shipEntity == Entity.Null || !em.Exists(shipEntity))
                return;

            var binding = proxy.GetComponent<ShipWeaponManualAimBinding>();
            bool mega = em.HasComponent<MegaShipState>(shipEntity)
                        && em.GetComponentData<MegaShipState>(shipEntity).IsMega;
            // Parked on the moon (toggle on): authored forward, not the cursor.
            // The orbit menu films this same proxy, so a tracking barrel spins in that view.
            if (mega || ShouldParkLandedWeapons(em, shipEntity)
                     || !TryGetTrackedAimPoint(em, shipEntity, out float3 aimPoint))
            {
                if (binding != null)
                    binding.RestoreRest();
                return;
            }

            if (binding == null)
                binding = proxy.AddComponent<ShipWeaponManualAimBinding>();

            binding.AimAt(aimPoint);
        }

        /// <summary>
        /// True when Regular Ships — Guns is Point. A missing Game Manager uses the
        /// published flag, which defaults off (Forward).
        /// </summary>
        static bool GunsPointAtCursor()
        {
            GameManager manager = GameManager.Instance;
            if (manager != null)
                return manager.RegularShipGunsPoint;
            return TitanOrbitDebugFlags.RegularShipGunsPoint;
        }

        /// <summary>
        /// Owner or ghosted aim point, and only when guns are Point, the move
        /// button is released, and that point is in front of the nose.
        /// </summary>
        static bool TryGetTrackedAimPoint(EntityManager em, Entity shipEntity, out float3 aimPoint)
        {
            aimPoint = default;
            if (!GunsPointAtCursor())
                return false;
            if (!TryGetAimPoint(em, shipEntity, out aimPoint))
                return false;
            if (!em.HasComponent<LocalTransform>(shipEntity))
                return false;

            // The local owner reads the live cursor every frame. Holding the move
            // button keeps the meshes on the nose even if the hull is still gliding.
            // Remotes follow ManualAimActive, already cleared by the server while
            // thrust is held.
            if (em.HasComponent<LocalPlayerShipTag>(shipEntity))
            {
                if (!em.HasComponent<ShipInput>(shipEntity))
                    return false;
                var input = em.GetComponentData<ShipInput>(shipEntity);
                if (!MegaShipWeaponAim.AllowsPointWeaponAim(input.Thrust))
                    return false;
            }

            var hull = em.GetComponentData<LocalTransform>(shipEntity);
            ToroidalMapEcs.TryGetMapSize(out float mapW, out float mapH);
            if (!MegaShipWeaponAim.IsInFrontOfHull(in hull, aimPoint, mapW, mapH))
                return false;
            return true;
        }

        /// <summary>Owner mouse point, or the ghosted point on a remote regular hull.</summary>
        static bool TryGetAimPoint(EntityManager em, Entity shipEntity, out float3 aimPoint)
        {
            aimPoint = default;
            if (shipEntity == Entity.Null || !em.Exists(shipEntity))
                return false;
            if (em.HasComponent<MegaShipState>(shipEntity)
                && em.GetComponentData<MegaShipState>(shipEntity).IsMega)
                return false;
            if (ShouldParkLandedWeapons(em, shipEntity))
                return false;

            if (em.HasComponent<LocalPlayerShipTag>(shipEntity)
                && em.HasComponent<ShipInput>(shipEntity)
                && em.HasComponent<LocalTransform>(shipEntity))
            {
                var hull = em.GetComponentData<LocalTransform>(shipEntity);
                var input = em.GetComponentData<ShipInput>(shipEntity);
                return MegaShipWeaponAim.TryGetOwnerMouseAimPoint(in hull, in input, out aimPoint);
            }

            if (!em.HasComponent<ShipState>(shipEntity))
                return false;

            var state = em.GetComponentData<ShipState>(shipEntity);
            if (!state.ManualAimActive)
                return false;

            aimPoint = new float3(state.ManualAimWorldX, 0f, state.ManualAimWorldZ);
            return math.isfinite(aimPoint.x) && math.isfinite(aimPoint.z);
        }

        /// <summary>
        /// True when this hull is fully landed and Game Manager → Park Landed Weapons is on.
        /// Missing Game Manager keeps the park, matching the checkbox default.
        /// </summary>
        public static bool ShouldParkLandedWeapons(EntityManager em, Entity shipEntity)
        {
            if (!ShipMoonDockState.IsFullyLandedOnMoon(em, shipEntity))
                return false;
            GameManager manager = GameManager.Instance;
            return manager == null || manager.ParkLandedWeapons;
        }
    }

    /// <summary>
    /// Cached weapon assemblies and their authored local rotations on one hull proxy.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class ShipWeaponManualAimBinding : MonoBehaviour
    {
        const string BankPivotName = "BankPivot";

        static readonly List<Transform> Scratch = new List<Transform>(8);

        Transform[] _weapons;
        Quaternion[] _restLocal;
        int _visualRevision = int.MinValue;

        /// <summary>Puts every cached weapon back on its authored local rotation.</summary>
        public void RestoreRest()
        {
            if (_weapons == null || _restLocal == null)
                return;

            int count = _weapons.Length < _restLocal.Length ? _weapons.Length : _restLocal.Length;
            for (int i = 0; i < count; i++)
            {
                if (_weapons[i] != null)
                    _weapons[i].localRotation = _restLocal[i];
            }
        }

        /// <summary>Points each weapon's unbanked forward at <paramref name="aimPoint"/>.</summary>
        public void AimAt(float3 aimPoint)
        {
            EnsureCache();
            if (_weapons == null || _weapons.Length == 0)
                return;

            bool haveMap = ToroidalMapEcs.TryGetMapSize(out float mapW, out float mapH);
            Transform bank = FindBankPivot(transform);
            Quaternion hullRot = transform.rotation;

            for (int i = 0; i < _weapons.Length; i++)
            {
                Transform weapon = _weapons[i];
                if (weapon == null)
                    continue;

                _weapons[i].localRotation = _restLocal[i];
                float3 from = weapon.position;
                float3 offset = haveMap
                    ? ToroidalMapEcs.ShortestOffsetXZ(from, aimPoint, mapW, mapH)
                    : aimPoint - from;
                offset.y = 0f;
                if (math.lengthsq(offset) < 0.0025f)
                    continue;

                Vector3 desiredWorldFwd = ((Vector3)offset).normalized;
                Vector3 hullFwd = transform.forward;
                hullFwd.y = 0f;
                Vector3 aimFlat = desiredWorldFwd;
                aimFlat.y = 0f;
                // A cursor in front of the hull can still sit behind a nose muzzle.
                // Leave that barrel on its authored forward so it matches the shot.
                if (hullFwd.sqrMagnitude > 1e-8f && aimFlat.sqrMagnitude > 1e-8f
                    && Vector3.Dot(hullFwd.normalized, aimFlat.normalized) < 0f)
                    continue;

                if (bank != null)
                    desiredWorldFwd = bank.rotation * (Quaternion.Inverse(hullRot) * desiredWorldFwd);

                Transform parent = weapon.parent;
                if (parent == null)
                {
                    weapon.rotation = Quaternion.LookRotation(desiredWorldFwd, Vector3.up);
                    continue;
                }

                Vector3 localFwd = parent.InverseTransformDirection(desiredWorldFwd);
                if (localFwd.sqrMagnitude < 1e-8f)
                    continue;

                Vector3 localUp = parent.InverseTransformDirection(Vector3.up);
                if (localUp.sqrMagnitude < 1e-8f)
                    localUp = Vector3.up;
                weapon.localRotation = Quaternion.LookRotation(localFwd, localUp);
            }
        }

        void EnsureCache()
        {
            var swap = GetComponent<ShipComponentVisualSwapApplier>();
            int revision = swap != null ? swap.VisualRevision : 0;
            if (_weapons != null && revision == _visualRevision && AllAlive())
                return;

            // Capture authored pose before this frame's aim overwrite. A part swap
            // already restored host rotations earlier this LateUpdate.
            MegaShipPartClassifier.CollectWeaponAssemblies(transform, Scratch);
            int write = 0;
            for (int i = 0; i < Scratch.Count; i++)
            {
                Transform t = Scratch[i];
                if (!IsLiveWeapon(t))
                    continue;
                Scratch[write++] = t;
            }

            _weapons = new Transform[write];
            _restLocal = new Quaternion[write];
            for (int i = 0; i < write; i++)
            {
                _weapons[i] = Scratch[i];
                _restLocal[i] = Scratch[i].localRotation;
            }

            _visualRevision = revision;
        }

        bool AllAlive()
        {
            for (int i = 0; i < _weapons.Length; i++)
            {
                if (_weapons[i] == null)
                    return false;
            }

            return true;
        }

        static bool IsLiveWeapon(Transform t)
        {
            if (t == null || !t.gameObject.activeInHierarchy)
                return false;

            Transform walk = t;
            while (walk != null)
            {
                if (walk.name == ShipFamilyPartMatch.OriginalStashName)
                    return false;
                walk = walk.parent;
            }

            return true;
        }

        static Transform FindBankPivot(Transform hull)
        {
            return hull != null ? hull.Find(BankPivotName) : null;
        }
    }
}

using TitanOrbit.Data;
using TitanOrbit.ECS.Authoring;
using UnityEngine;

namespace TitanOrbit.Game
{
    /// <summary>
    /// Tags weapon-slot transforms on a hull prefab with <see cref="ECS.Authoring.ShipWeaponMountAuthoring"/>
    /// so ECS baking can build <c>ShipWeaponMountElement</c> buffers for muzzle pose and VFX.
    /// Creates a default forward "Weapon" child when no weapon transforms exist. Presentation/bake helper only.
    /// </summary>
    public static class ShipWeaponMountCollector
    {
        /// <summary>
        /// Scans hull children for weapon-named transforms and ensures each has mount authoring.
        /// Falls back to <see cref="EnsureDefaultWeaponMount"/> when none are found.
        /// </summary>
        public static void EnsureWeaponMountsOnHierarchy(Transform hullRoot, float muzzleOffset)
        {
            if (hullRoot == null)
                return;

            bool taggedAny = false;
            foreach (var t in hullRoot.GetComponentsInChildren<Transform>(true))
            {
                if (t == hullRoot)
                    continue;
                if (!LooksLikeWeaponTransform(t))
                    continue;

                if (t.GetComponent<ShipWeaponMountAuthoring>() == null)
                    t.gameObject.AddComponent<ShipWeaponMountAuthoring>();
                taggedAny = true;
            }

            if (!taggedAny)
                EnsureDefaultWeaponMount(hullRoot, muzzleOffset);
        }

        /// <summary>
        /// Adds a single forward weapon mount at <paramref name="muzzleOffset"/> when the prefab has no weapon children.
        /// [TITAN-ORBIT] Guarantees at least one muzzle origin for shooting VFX and pose math.
        /// </summary>
        public static void EnsureDefaultWeaponMount(Transform hullRoot, float muzzleOffset)
        {
            if (hullRoot == null)
                return;

            if (hullRoot.GetComponentInChildren<ShipWeaponMountAuthoring>(true) != null)
                return;

            var weaponGo = new GameObject("Weapon");
            weaponGo.transform.SetParent(hullRoot, false);
            weaponGo.transform.localPosition = new Vector3(0f, 0f, Mathf.Max(0.5f, muzzleOffset));
            weaponGo.transform.localRotation = Quaternion.identity;
            weaponGo.AddComponent<ShipWeaponMountAuthoring>();
        }

        /// <summary>
        /// True when this transform is an offensive weapon barrel for mounts / live muzzle resolve.
        /// Matches family component ids via <see cref="ShipComponentAbilityStatsMath.IsWeaponComponent"/>
        /// and legacy child names containing "Weapon".
        /// </summary>
        public static bool LooksLikeWeaponTransform(Transform t)
        {
            if (t == null)
                return false;

            string name = t.name;
            if (string.IsNullOrEmpty(name))
                return false;

            // --- Direct authoring already present ---
            if (t.GetComponent<ShipWeaponMountAuthoring>() != null)
                return true;

            // --- Family / legacy: "Weapon…", "…_Weapon…", isolated "weapon" ---
            if (name.IndexOf("Weapon", System.StringComparison.OrdinalIgnoreCase) >= 0)
                return true;

            // Strip common "FamilyId_" prefix for IsWeaponComponent (ShipFamilyStatsCalculator style).
            string id = name;
            int underscore = name.IndexOf('_');
            if (underscore > 0 && underscore < name.Length - 1)
                id = name.Substring(underscore + 1);

            return ShipComponentAbilityStatsMath.IsWeaponComponent(id)
                   || ShipComponentAbilityStatsMath.IsWeaponComponent(name);
        }
    }
}

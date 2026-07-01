using TitanOrbit.ECS.Authoring;
using UnityEngine;

namespace TitanOrbit.Game
{
    public static class ShipWeaponMountCollector
    {
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

        static bool LooksLikeWeaponTransform(Transform t)
        {
            string name = t.name;
            if (string.IsNullOrEmpty(name))
                return false;

            if (name.Contains("Weapon"))
                return true;

            // Legacy chassis slots use a plain "Weapon" child name from ShipFamilyDefinition.
            return false;
        }
    }
}

using System.Collections.Generic;
using TitanOrbit.Simulation;
using Unity.Mathematics;
using UnityEngine;

namespace TitanOrbit.ECS
{
    public static class ShipHullColliderBakeUtility
    {
        public static List<ShipHullColliderElement> CollectFromHierarchy(Transform hullRoot)
        {
            var results = new List<ShipHullColliderElement>();
            if (hullRoot == null)
                return results;

            var boxes = hullRoot.GetComponentsInChildren<BoxCollider>(true);
            for (int i = 0; i < boxes.Length; i++)
            {
                var box = boxes[i];
                if (box == null || !box.enabled || box.isTrigger)
                    continue;

                if (!TryConvertBoxCollider(box, hullRoot, out var element))
                    continue;

                results.Add(element);
            }

            if (results.Count == 0)
                results.Add(CreateFallbackFuselage());

            return results;
        }

        static bool TryConvertBoxCollider(BoxCollider box, Transform hullRoot, out ShipHullColliderElement element)
        {
            element = default;
            Transform boxTransform = box.transform;

            Vector3 worldCenter = boxTransform.TransformPoint(box.center);
            Vector3 localCenter = hullRoot.InverseTransformPoint(worldCenter);
            Quaternion localRotation = Quaternion.Inverse(hullRoot.rotation) * boxTransform.rotation;

            Vector3 lossy = boxTransform.lossyScale;
            Vector3 halfExtents = new Vector3(
                box.size.x * 0.5f * math.abs(lossy.x),
                box.size.y * 0.5f * math.abs(lossy.y),
                box.size.z * 0.5f * math.abs(lossy.z));

            if (halfExtents.x < 0.001f && halfExtents.z < 0.001f)
                return false;

            element = new ShipHullColliderElement
            {
                LocalCenter = new float3(localCenter.x, localCenter.y, localCenter.z),
                LocalRotation = new quaternion(localRotation.x, localRotation.y, localRotation.z, localRotation.w),
                HalfExtents = new float3(halfExtents.x, halfExtents.y, halfExtents.z),
            };
            return true;
        }

        static ShipHullColliderElement CreateFallbackFuselage()
        {
            float radius = BodyCollisionMath.GetShipHullRadiusWorld(1f);
            return new ShipHullColliderElement
            {
                LocalCenter = float3.zero,
                LocalRotation = quaternion.identity,
                HalfExtents = new float3(radius, 0.08f, radius * 0.85f),
            };
        }
    }
}

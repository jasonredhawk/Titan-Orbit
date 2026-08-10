using System.Collections.Generic;
using TitanOrbit.Data;
using UnityEngine;

namespace TitanOrbit.Entities
{
    /// <summary>
    /// Computes default local placement for store-bought ship components based on part type
    /// and existing chassis / equipped transforms.
    /// </summary>
    public static class EquippedComponentPlacementUtility
    {
        public const float DefaultStackSpacing = 0.14f;
        public const float DefaultHorizontalSpacing = 0.18f;
        public const float DefaultCockpitSpacing = 0.24f;
        public const float DefaultRearOffset = 0.35f;

        public struct PlacementReference
        {
            public List<Vector3> positions;
            public List<Quaternion> rotations;
        }

        public static string ResolvePartType(string componentId) =>
            ShipComponentAbilityStats.ResolvePartTypeForSuggestedStats(componentId);

        public static Vector3 ComputeDefaultPlacement(
            string partType,
            int ordinalAmongType,
            int totalOfType,
            in PlacementReference reference)
        {
            return ComputeDefaultPosition(partType, ordinalAmongType, totalOfType, in reference);
        }

        public static Quaternion ComputeDefaultRotation(
            string partType,
            int ordinalAmongType,
            int totalOfType,
            in PlacementReference reference)
        {
            if (reference.rotations != null && reference.rotations.Count > 0)
            {
                Quaternion sum = Quaternion.identity;
                for (int i = 0; i < reference.rotations.Count; i++)
                    sum = i == 0 ? reference.rotations[i] : Quaternion.Slerp(sum, reference.rotations[i], 1f / (i + 1));
                return sum;
            }

            return Quaternion.identity;
        }

        public static Vector3 ComputeDefaultPosition(
            string partType,
            int ordinalAmongType,
            int totalOfType,
            in PlacementReference reference)
        {
            int count = Mathf.Max(1, totalOfType);
            int index = Mathf.Clamp(ordinalAmongType, 0, count - 1);
            Vector3 avg = AveragePosition(reference.positions);
            float spacing = InferSpacing(reference.positions, DefaultHorizontalSpacing);

            if (IsWing(partType))
                return ComputeStackedCentered(index, count, avg, DefaultStackSpacing);

            if (IsCockpit(partType))
                return ComputeCockpitHorizontal(index, count, avg, spacing);

            if (IsWeapon(partType))
                return ComputeHorizontalCentered(index, count, avg, spacing);

            if (IsTailOrFin(partType))
                return ComputeRearCentered(index, count, avg, spacing);

            return ComputeStackedCentered(index, count, avg, DefaultStackSpacing * 0.85f);
        }

        public static void ComputeAllPlacementsForType(
            string partType,
            int totalCount,
            in PlacementReference reference,
            List<Vector3> outPositions,
            List<Quaternion> outRotations)
        {
            outPositions ??= new List<Vector3>();
            outRotations ??= new List<Quaternion>();
            outPositions.Clear();
            outRotations.Clear();

            int count = Mathf.Max(1, totalCount);
            Quaternion rot = ComputeDefaultRotation(partType, 0, count, in reference);
            for (int i = 0; i < count; i++)
            {
                outPositions.Add(ComputeDefaultPosition(partType, i, count, in reference));
                outRotations.Add(rot);
            }
        }

        public static void ApplyPlacementToEntry(ref EquippedEquipmentEntry entry, Vector3 localPosition, Quaternion localRotation)
        {
            // --- Apply changes ---
            Vector3 euler = localRotation.eulerAngles;
            entry.localPosX = localPosition.x;
            entry.localPosY = localPosition.y;
            entry.localPosZ = localPosition.z;
            entry.localRotX = euler.x;
            entry.localRotY = euler.y;
            entry.localRotZ = euler.z;
        }

        public static Vector3 GetLocalPosition(in EquippedEquipmentEntry entry) =>
            new Vector3(entry.localPosX, entry.localPosY, entry.localPosZ);

        public static Quaternion GetLocalRotation(in EquippedEquipmentEntry entry) =>
            Quaternion.Euler(entry.localRotX, entry.localRotY, entry.localRotZ);

        public static bool HasPlacement(in EquippedEquipmentEntry entry) =>
            entry.localPosX != 0f || entry.localPosY != 0f || entry.localPosZ != 0f ||
            entry.localRotX != 0f || entry.localRotY != 0f || entry.localRotZ != 0f;

        public const float RotationSnapDegrees = 45f;

        public static Vector3 SnapEulerAngles(Vector3 euler) =>
            new Vector3(
                Mathf.Round(euler.x / RotationSnapDegrees) * RotationSnapDegrees,
                Mathf.Round(euler.y / RotationSnapDegrees) * RotationSnapDegrees,
                Mathf.Round(euler.z / RotationSnapDegrees) * RotationSnapDegrees);

        private static bool IsWing(string partType) =>
            string.Equals(partType, "Wing", System.StringComparison.OrdinalIgnoreCase);

        private static bool IsWeapon(string partType) =>
            string.Equals(partType, "Weapon", System.StringComparison.OrdinalIgnoreCase);

        private static bool IsCockpit(string partType) =>
            string.Equals(partType, "Cockpit", System.StringComparison.OrdinalIgnoreCase);

        private static bool IsTailOrFin(string partType) =>
            string.Equals(partType, "Tail", System.StringComparison.OrdinalIgnoreCase) ||
            string.Equals(partType, "Fin", System.StringComparison.OrdinalIgnoreCase);

        private static Vector3 AveragePosition(List<Vector3> positions)
        {
            // --- AveragePosition ---
            if (positions == null || positions.Count == 0)
                return Vector3.zero;

            Vector3 sum = Vector3.zero;
            for (int i = 0; i < positions.Count; i++)
                sum += positions[i];
            return sum / positions.Count;
        }

        private static float InferSpacing(List<Vector3> positions, float fallback)
        {
            // --- InferSpacing ---
            if (positions == null || positions.Count < 2)
                return fallback;

            float minX = positions[0].x;
            float maxX = positions[0].x;
            for (int i = 1; i < positions.Count; i++)
            {
                minX = Mathf.Min(minX, positions[i].x);
                maxX = Mathf.Max(maxX, positions[i].x);
            }

            float span = maxX - minX;
            if (span > 0.01f && positions.Count > 1)
                return span / (positions.Count - 1);
            return fallback;
        }

        private static Vector3 ComputeStackedCentered(int index, int count, Vector3 referenceAvg, float stackSpacing)
        {
            // --- Compute value ---
            float centerOffset = (count - 1) * 0.5f;
            float y = referenceAvg.y + (index - centerOffset) * stackSpacing;
            return new Vector3(0f, y, referenceAvg.z);
        }

        private static Vector3 ComputeHorizontalCentered(int index, int count, Vector3 referenceAvg, float spacing)
        {
            // --- Compute value ---
            float centerOffset = (count - 1) * 0.5f;
            float x = (index - centerOffset) * spacing;
            return new Vector3(x, referenceAvg.y, referenceAvg.z);
        }

        private static Vector3 ComputeCockpitHorizontal(int index, int count, Vector3 referenceAvg, float spacing)
        {
            // --- Compute value ---
            float useSpacing = Mathf.Max(spacing, DefaultCockpitSpacing);
            float centerOffset = (count - 1) * 0.5f;
            float x = (index - centerOffset) * useSpacing;
            return new Vector3(x, referenceAvg.y, referenceAvg.z);
        }

        private static Vector3 ComputeRearCentered(int index, int count, Vector3 referenceAvg, float spacing)
        {
            // --- Compute value ---
            float rearZ = referenceAvg.z - DefaultRearOffset;
            if (referenceAvg.z < -0.01f)
                rearZ = referenceAvg.z - DefaultRearOffset * 0.5f;

            float centerOffset = (count - 1) * 0.5f;
            float x = (index - centerOffset) * spacing * 0.75f;
            return new Vector3(x, referenceAvg.y, rearZ);
        }
    }
}

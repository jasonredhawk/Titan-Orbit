using System;
using System.Collections.Generic;
using Unity.Mathematics;
using UnityEngine;

namespace TitanOrbit.Data
{
    /// <summary>
    /// One render mesh/material slot baked from a USC chassis prefab for Entities Graphics.
    /// </summary>
    [Serializable]
    public struct ShipChassisRenderPart
    {
        public Mesh Mesh;
        public Material Material;
        public float3 LocalPosition;
        public quaternion LocalRotation;
        public float3 LocalScale;
    }

    /// <summary>
    /// Baked visual + gameplay attachment data for one chassis tier (e.g. AstroEagle_Hawk).
    /// Populated by editor bake menu; read at runtime by the client Entities Graphics ship presentation system.
    /// </summary>
    [Serializable]
    public class ShipChassisVisualEntry
    {
        public string ChassisId;
        public List<ShipChassisRenderPart> RenderParts = new List<ShipChassisRenderPart>();
        public List<ShipWeaponMountBakeData> WeaponMounts = new List<ShipWeaponMountBakeData>();
        public List<ShipWingTractorBeamBakeData> WingTractorBeams = new List<ShipWingTractorBeamBakeData>();
    }

    /// <summary>Weapon mount pose relative to ship root — mirrors baked ship weapon mount buffer fields.</summary>
    [Serializable]
    public struct ShipWeaponMountBakeData
    {
        public float3 LocalPosition;
        public quaternion LocalRotation;
        public float DirectionAngleDeg;
        public int CannonIndex;
    }

    /// <summary>Wing tractor beam slot relative to ship root.</summary>
    [Serializable]
    public struct ShipWingTractorBeamBakeData
    {
        public float3 LocalPosition;
        public float TractorBeamDistance;
        public float TractorBeamDistancePerLevel;
        public float TractorBeamPower;
        public float TractorBeamPowerPerLevel;
        public float MaxGems;
        public float MaxGemsPerLevel;
    }

    /// <summary>
    /// ScriptableObject catalog of baked chassis visuals and attachment points for pure ECS presentation.
    /// </summary>
    [CreateAssetMenu(fileName = "ShipChassisVisualCatalog", menuName = "Titan Orbit/Ship Chassis Visual Catalog")]
    public class ShipChassisVisualCatalog : ScriptableObject
    {
        static ShipChassisVisualCatalog s_Instance;

        [SerializeField] List<ShipChassisVisualEntry> entries = new List<ShipChassisVisualEntry>();

        /// <summary>Loads <c>Resources/ShipChassisVisualCatalog</c> once per session.</summary>
        public static ShipChassisVisualCatalog Instance
        {
            get
            {
                if (s_Instance == null)
                    s_Instance = Resources.Load<ShipChassisVisualCatalog>("ShipChassisVisualCatalog");
                return s_Instance;
            }
        }

        /// <summary>Finds baked data for a chassis id string from the upgrade tree.</summary>
        public bool TryGetEntry(string chassisId, out ShipChassisVisualEntry entry)
        {
            entry = null;
            if (string.IsNullOrEmpty(chassisId) || entries == null)
                return false;

            for (int i = 0; i < entries.Count; i++)
            {
                var candidate = entries[i];
                if (candidate != null && string.Equals(candidate.ChassisId, chassisId, StringComparison.OrdinalIgnoreCase))
                {
                    entry = candidate;
                    return true;
                }
            }

            return false;
        }

        /// <summary>Replaces or appends a baked chassis entry (editor bake menu).</summary>
        public void UpsertEntry(ShipChassisVisualEntry entry)
        {
            if (entry == null || string.IsNullOrEmpty(entry.ChassisId))
                return;

            for (int i = 0; i < entries.Count; i++)
            {
                if (entries[i] != null &&
                    string.Equals(entries[i].ChassisId, entry.ChassisId, StringComparison.OrdinalIgnoreCase))
                {
                    entries[i] = entry;
                    return;
                }
            }

            entries.Add(entry);
        }

#if UNITY_EDITOR
        public IReadOnlyList<ShipChassisVisualEntry> EditorEntries => entries;
#endif
    }
}

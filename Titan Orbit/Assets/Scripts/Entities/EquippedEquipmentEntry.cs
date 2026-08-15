using TitanOrbit.Data;
using UnityEngine;

namespace TitanOrbit.Entities
{
    /// <summary>
    /// Serialized equipped slot state for store components and drones. Stored on ship loadout
    /// and mirrored to visual placement. [TITAN-ORBIT] Flat floats for JSON/ghost-friendly layout.
    /// </summary>
    public struct EquippedEquipmentEntry
    {
        // --- Identity ---
        public int itemType;
        public string componentId;
        public int remainingCharges;
        /// <summary>
        /// Store purchase level at buy time (<c>min(ship, docked planet)</c>) for drones and
        /// extra components. 0 for rockets/mines and legacy rows.
        /// </summary>
        public int itemLevel;

        // --- Local transform (ship hull space) ---
        public float localPosX;
        public float localPosY;
        public float localPosZ;
        public float localRotX;
        public float localRotY;
        public float localRotZ;

        public StoreItemType ItemType => (StoreItemType)itemType;
        public bool IsShipComponent => ItemType == StoreItemType.ShipComponent;
        public string ComponentId => componentId ?? string.Empty;

        public Vector3 LocalPosition
        {
            get => new Vector3(localPosX, localPosY, localPosZ);
            set
            {
                // --- Flatten Vector3 into serialized floats ---
                localPosX = value.x;
                localPosY = value.y;
                localPosZ = value.z;
            }
        }

        public Vector3 LocalEulerAngles
        {
            get => new Vector3(localRotX, localRotY, localRotZ);
            set
            {
                // --- Flatten euler into serialized floats ---
                localRotX = value.x;
                localRotY = value.y;
                localRotZ = value.z;
            }
        }
    }
}

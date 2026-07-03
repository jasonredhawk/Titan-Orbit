using TitanOrbit.Data;
using UnityEngine;

namespace TitanOrbit.Entities
{
    /// <summary>Serialized equipped slot state for store components and drones.</summary>
    public struct EquippedEquipmentEntry
    {
        public int itemType;
        public string componentId;
        public int remainingCharges;
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
                localRotX = value.x;
                localRotY = value.y;
                localRotZ = value.z;
            }
        }
    }
}

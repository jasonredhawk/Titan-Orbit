namespace TitanOrbit.Entities
{
    /// <summary>Serialized equipped slot state for store components and drones.</summary>
    public struct EquippedEquipmentEntry
    {
        public string componentId;
        public int remainingCharges;
        public float localPosX;
        public float localPosY;
        public float localPosZ;
        public float localRotX;
        public float localRotY;
        public float localRotZ;
    }
}

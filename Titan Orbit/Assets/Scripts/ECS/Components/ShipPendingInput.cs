namespace TitanOrbit.ECS
{
    /// <summary>Main-thread input snapshot consumed by GhostInput / server apply systems.</summary>
    public static class ShipPendingInput
    {
        public static ShipInput Latest;
        public static bool HasValue;
        public static bool LocalHostMode;

        public static void Set(ShipInput input, bool localHostMode)
        {
            Latest = input;
            HasValue = true;
            LocalHostMode = localHostMode;
        }
    }
}

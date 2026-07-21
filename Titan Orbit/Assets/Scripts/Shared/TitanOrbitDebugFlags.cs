namespace TitanOrbit
{
    /// <summary>
    /// Process-wide debug flags published by <c>GameManager</c> (TitanOrbit.Core) for assemblies that
    /// cannot reference Core — notably TitanOrbit.ECS server systems.
    /// [TITAN-ORBIT] Not a cheat channel for remote clients: the dedicated server process normally
    /// never sets these. Local Editor host sets them from the Inspector toggle on NceGameRoot.
    /// </summary>
    public static class TitanOrbitDebugFlags
    {
        /// <summary>
        /// When true, moon-orbit ship upgrade purchases may jump to any tree node for free.
        /// Written by GameManager; read by MoonOrbitStoreSystem and UI helpers.
        /// </summary>
        public static bool FreeShipUpgradeTree;

        /// <summary>
        /// When true, asteroid-destroy paths log millisecond timings (local gem burst, urgent gem
        /// proxies). Use this to find hitch frames — Console filter: <c>[AsteroidDestroy]</c>.
        /// </summary>
        public static bool LogAsteroidDestroyPerf;
    }
}

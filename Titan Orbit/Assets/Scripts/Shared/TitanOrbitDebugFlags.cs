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

        /// <summary>
        /// When true, <c>ClientStutterIsolator</c> shows the on-screen panel and accepts Shift+F1–F7.
        /// Off by default — enable from GameManager Inspector when bisecting destroy stutter again.
        /// </summary>
        public static bool StutterIsolatorEnabled;

        /// <summary>
        /// When true, <c>InstructionReferenceCaptureSession</c> shows its status banner and accepts
        /// F8/F9 (and Esc / Shift+F8 cancel) to gather InstructionScreens reference plates.
        /// Off by default — enable from GameManager Inspector only when rebuilding instruction art.
        /// </summary>
        public static bool InstructionImageCaptureEnabled;

        // --- Stutter / destroy isolation (Shift+F1–F5 while isolator enabled) ---

        /// <summary>Skip bullet impact one-shot VFX (HitRpc + predicted impact).</summary>
        public static bool IsolateDisableImpactVfx;

        /// <summary>Skip mining float / HP Left popups on asteroid hits.</summary>
        public static bool IsolateDisableFloatingCounts;

        /// <summary>
        /// Skip asteroid obstacles in <c>ShipToroidalWorldCollisionSystem</c> (planets/moons remain).
        /// </summary>
        public static bool IsolateDisableAsteroidShipCollision;

        /// <summary>
        /// Skip soft-track / cruise-correct on local ship presentation — raw NetCode pose only.
        /// </summary>
        public static bool IsolateDisableShipSoftTrack;

        /// <summary>Skip local gem burst presentation on asteroid kill.</summary>
        public static bool IsolateDisableGemBurst;
    }
}

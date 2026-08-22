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
        /// When true, B-key cycles every <c>BulletVfxBank</c> category including heal.
        /// Written by GameManager; dedicated server stays false.
        /// </summary>
        public static bool CycleAllBulletBanks;

        /// <summary>
        /// When true, ALT rocket fire does not consume equipment charges and works with an
        /// empty loadout. The 5s reload still applies. Written by GameManager; dedicated
        /// server stays false (not a remote cheat channel).
        /// </summary>
        public static bool InfiniteRockets;

        /// <summary>
        /// When true, E mine place does not consume equipment charges and works with an
        /// empty loadout. The deploy cooldown still applies. Written by GameManager;
        /// dedicated server stays false (not a remote cheat channel).
        /// </summary>
        public static bool InfiniteMines;

        /// <summary>
        /// When true, store rockets and mines treat the owner (and same team) as an enemy
        /// after <see cref="SelfHarmArmDelaySeconds"/>. Local Editor / MPPM host only.
        /// </summary>
        public static bool SelfHarmRocketsAndMines;

        /// <summary>Seconds after fire / place before self-harm debug arms.</summary>
        public const float SelfHarmArmDelaySeconds = 2f;

        /// <summary>True when rocket <paramref name="ageSeconds"/> has passed the self-harm arm delay.</summary>
        public static bool IsSelfHarmArmed(float ageSeconds) =>
            SelfHarmRocketsAndMines && ageSeconds >= SelfHarmArmDelaySeconds;

        /// <summary>
        /// True when a homing rocket may lock and collide with its owner / team (debug self-harm).
        /// Straight guns never use this path.
        /// </summary>
        public static bool IsHomingSelfHarmArmed(byte homing, float ageSeconds) =>
            homing != 0 && IsSelfHarmArmed(ageSeconds);

        /// <summary>True when a mine placed at <paramref name="placeTime"/> is armed against its owner.</summary>
        public static bool IsSelfHarmArmed(double placeTime, double now) =>
            SelfHarmRocketsAndMines && now >= placeTime + SelfHarmArmDelaySeconds;

        /// <summary>
        /// When true, unoccupied MEGA mounts auto-aim and fire on living asteroids (damage mode
        /// only). Local Editor / MPPM host only; dedicated server stays false.
        /// </summary>
        public static bool MegaShipsAutoFireAsteroids;

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

        /// <summary>
        /// Temporary wrap-test overlay: cyan rectangle on the world map and minimap at
        /// <c>±MapWidth/2</c> / <c>±MapHeight/2</c>. Default on for seam playtest; turn off
        /// from GameManager when done.
        /// </summary>
        public static bool ShowMapSeamLines = true;
    }
}

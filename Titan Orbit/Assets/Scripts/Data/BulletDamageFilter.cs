namespace TitanOrbit.Data
{
    /// <summary>
    /// Which obstacle classes an authoritative bullet may collide with / damage.
    /// [TITAN-ORBIT] Ship guns use <see cref="Everything"/>. Combat drones use Starblast-style
    /// filters so mining bolts ignore ships and fighter bolts ignore asteroids.
    /// </summary>
    public enum BulletDamageFilter : byte
    {
        /// <summary>Normal ship weapons — planets, moons, ships, rocks, transports, drones.</summary>
        Everything = 0,

        /// <summary>
        /// Mining drones — damage asteroids only. Pass through ships, drones, transports, moons.
        /// Planets still block as solid world geometry (no HP write).
        /// </summary>
        AsteroidsOnly = 1,

        /// <summary>
        /// Fighter / attack drones — damage enemy ships (and their drones). Pass through asteroids,
        /// transports, moons. Planets still block as solid world geometry.
        /// </summary>
        ShipsOnly = 2,
    }
}

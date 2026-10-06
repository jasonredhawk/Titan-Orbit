using Unity.Mathematics;

namespace TitanOrbit.Data
{
    // --- Type members ---
    /// <summary>
    /// Constants and spawn rolls for procedural planet identity: which
    /// <see cref="PlanetShipFamilyConfig"/> list index each planet receives, and which
    /// <see cref="BulletVfxBank"/> gun that planet stamps onto purchased hulls.
    /// <para>
    /// Home planets always get family index 0 (AstroEagle starter) and Laserbolt guns.
    /// Neutral and captured planets roll a non-home family <b>and</b> a damage bullet type
    /// so the same family can fire Fireballs one match and Rift the next.
    /// Family assets themselves default to Laserbolt; the neutral-planet override is the live gun.
    /// </para>
    /// Written at planet spawn by <see cref="ECS.Systems.GameBootstrapSystem"/> and
    /// <see cref="ECS.MapLayoutBlueprint"/> into <c>PlanetState.ShipFamilyConfigIndex</c> and
    /// <c>PlanetState.BulletBankIndex</c>. Shared client/server — indices must stay stable
    /// across builds. Bullet rolls hash <c>matchSeed + planetId</c> so they do not consume
    /// the placement RNG stream (homes → neutrals → claims → family rolls → asteroids).
    /// </summary>
    public static class PlanetShipFamilyAssignment
    {
        /// <summary>[TITAN-ORBIT] PlanetShipFamilyConfig list index for team home planets (AstroEagle).</summary>
        public const byte HomeFamilyConfigIndex = 0;

        /// <summary>[TITAN-ORBIT] Count of non-home families in PlanetShipFamilyConfig (Cosmic Shark through Strider Ox).</summary>
        public const int NonHomeFamilySlotCount = 11;

        /// <summary>
        /// [TITAN-ORBIT] Laserbolt — first <see cref="BulletVfxBank"/> category, the
        /// authored family fallback, and the locked gun on every home world.
        /// Neutral planets overwrite this at spawn.
        /// </summary>
        public const byte DefaultBulletBankIndex = 0;

        /// <summary>
        /// Salt mixed into the per-planet bullet RNG so a seed that already drives
        /// placement does not accidentally reuse the same stream.
        /// </summary>
        const uint BulletRollStreamSalt = 0xB011E7u;

        /// <summary>Cached selectable (non-heal, non-rocket) bank indices. Built once at first roll.</summary>
        static int[] s_SelectableDamageBanks;

        /// <summary>
        /// Gun stamped onto a planet at spawn. Homes always return Laserbolt so starters
        /// stay familiar. Neutrals hash <paramref name="matchSeed"/> + <paramref name="planetId"/>.
        /// </summary>
        public static byte ResolveSpawnBulletBankIndex(bool isHomePlanet, uint matchSeed, int planetId)
        {
            if (isHomePlanet)
                return DefaultBulletBankIndex;
            return RollPlanetBulletBankIndex(matchSeed, planetId);
        }

        /// <summary>
        /// Picks a damage bullet bank for a <b>neutral</b> planet from the match seed and planet id.
        /// Same inputs always yield the same bank on server and client — no extra
        /// <c>Random.NextInt</c> on the placement stream, so asteroid poses stay stable.
        /// Skips heal (EnergySpheres) and store-reserved Rockets. Homes must use
        /// <see cref="ResolveSpawnBulletBankIndex"/> (always Laserbolt).
        /// </summary>
        /// <param name="matchSeed">Map generation seed from <c>MapGenerationLogic.RolledParameters</c>.</param>
        /// <param name="planetId">Stable <c>PlanetState.PlanetId</c> (neutrals ≥ 100).</param>
        /// <returns>Zero-based <see cref="BulletVfxBank"/> category, or Laserbolt when the bank is empty.</returns>
        public static byte RollPlanetBulletBankIndex(uint matchSeed, int planetId)
        {
            // --- One-shot catalog walk (not a sim tick) ---
            int[] valid = GetSelectableDamageBankIndices();
            if (valid == null || valid.Length == 0)
                return DefaultBulletBankIndex;

            // --- Deterministic per-planet stream ---
            // [TITAN-ORBIT] Knuth multiplicative hash on planetId so nearby ids (team 1/2,
            // neutrals 100/101) do not cluster on the same few banks.
            uint salt = unchecked((uint)planetId * 2654435761u);
            var rng = Random.CreateFromIndex(matchSeed ^ salt ^ BulletRollStreamSalt);
            return (byte)valid[rng.NextInt(0, valid.Length)];
        }

        /// <summary>
        /// Clamps a ghosted / authored bank onto a fireable damage category.
        /// Heal and Rockets fall back to Laserbolt so B-key and planetary defense never
        /// inherit those reserved rows as a hull default.
        /// </summary>
        /// <param name="bankIndex">Raw index from a planet, ship, or family asset.</param>
        public static int SanitizeSelectableDamageBank(int bankIndex)
        {
            if (bankIndex < 0 ||
                BulletBankProfileUtility.IsHealBankIndex(bankIndex) ||
                BulletBankProfileUtility.IsStoreReservedBankIndex(bankIndex))
                return DefaultBulletBankIndex;
            return bankIndex;
        }

        /// <summary>
        /// Damage-bank catalog used by planet rolls. Built once from
        /// <see cref="BulletVfxBank.LoadDefault"/> (Resources — fine at map gen, not per tick).
        /// </summary>
        static int[] GetSelectableDamageBankIndices()
        {
            if (s_SelectableDamageBanks != null)
                return s_SelectableDamageBanks;

            var bank = BulletVfxBank.LoadDefault();
            int categoryCount = bank != null ? bank.CategoryCount : 0;
            if (categoryCount <= 0)
            {
                s_SelectableDamageBanks = new[] { (int)DefaultBulletBankIndex };
                return s_SelectableDamageBanks;
            }

            // --- Filter reserved rows ---
            // Laserbolt (0), Plasma, Fireballs, Lightning, … stay. EnergySpheres and Rockets out.
            var scratch = new int[categoryCount];
            int written = 0;
            for (int i = 0; i < categoryCount; i++)
            {
                if (BulletBankProfileUtility.IsHealBankIndex(i) ||
                    BulletBankProfileUtility.IsStoreReservedBankIndex(i))
                    continue;
                scratch[written++] = i;
            }

            if (written <= 0)
            {
                s_SelectableDamageBanks = new[] { (int)DefaultBulletBankIndex };
                return s_SelectableDamageBanks;
            }

            s_SelectableDamageBanks = new int[written];
            for (int i = 0; i < written; i++)
                s_SelectableDamageBanks[i] = scratch[i];
            return s_SelectableDamageBanks;
        }
    }
}

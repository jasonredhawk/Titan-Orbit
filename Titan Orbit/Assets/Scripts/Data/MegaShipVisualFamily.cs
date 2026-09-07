namespace TitanOrbit.Data
{
    /// <summary>
    /// Visual MEGA hull line — the three StarSparrow folders under
    /// <c>Assets/Prefabs/MEGA_Ships/</c>. Used to tag catalog entries; match roll draws
    /// any three armed hulls from the playable pool (not one per folder).
    /// [TITAN-ORBIT] This is not a gameplay ship family (AstroEagle, CosmicShark, …).
    /// Gameplay family still owns bullet bank, team tint, and the L1–L6 ladder.
    /// </summary>
    public enum MegaShipVisualFamily : byte
    {
        /// <summary><c>CraizanStar (Mega)</c> folder (20 hulls).</summary>
        CraizanStar = 0,

        /// <summary><c>GalacticLeopard (Mega)</c> folder (40 hulls).</summary>
        GalacticLeopard = 1,

        /// <summary><c>GalacticOkamoto (Mega)</c> folder (30 hulls).</summary>
        GalacticOkamoto = 2,
    }
}

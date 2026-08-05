namespace TitanOrbit.Game
{
    /// <summary>
    /// Full-version "bling" badge lookup for ship nameplates.
    /// When a player owns the full version, every client shows a badge next to their ship name —
    /// free users see the bling on paid players, but free players themselves have no badge.
    /// <para>
    /// [TITAN-ORBIT] Stub for now: always returns false. Later this will read a replicated
    /// entitlement (ghost field / match roster flag) so remote full-version owners stay visible
    /// to free clients. Do not use local-only <c>TitanOrbitEntitlements</c> here — that would only
    /// paint the local player's own badge correctly.
    /// </para>
    /// </summary>
    public static class ShipFullVersionBadge
    {
        /// <summary>
        /// Whether <paramref name="networkId"/>'s ship should show the full-version badge
        /// to all clients. Stub: always false until entitlement replication exists.
        /// </summary>
        /// <param name="networkId">[NETCODE] Owner <c>GhostOwner.NetworkId</c> (0 = unknown).</param>
        /// <returns>True when this owner should display bling on their nameplate.</returns>
        public static bool IsFullVersionUser(int networkId)
        {
            // --- Stub: no replicated full-version entitlement yet ---
            // [TITAN-ORBIT] Keep the nameplate badge slot wired; hide until network truth exists.
            _ = networkId;
            return false;
        }
    }
}

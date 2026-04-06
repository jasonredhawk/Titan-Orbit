using Unity.Netcode.Components;

namespace TitanOrbit.Networking
{
    /// <summary>
    /// Player ships simulate movement on the owning client (<see cref="Entities.Starship"/> uses <c>IsOwner</c>).
    /// Default <see cref="NetworkTransform"/> is server-authoritative, so the server never applied thrust/orbit updates
    /// and remote peers only had spawn-time <see cref="Unity.Netcode.NetworkObject.SynchronizeTransform"/> — wrong/stale poses.
    /// Owner authority replicates the owner&apos;s transform each tick; <see cref="NetworkRigidbody"/> keeps proxies kinematic.
    /// </summary>
    public class OwnerAuthoritativeNetworkTransform : NetworkTransform
    {
        protected override bool OnIsServerAuthoritative() => false;

        public override void OnNetworkSpawn()
        {
            base.OnNetworkSpawn();
            #region agent log e695ff
            NetworkGameManager.DebugSessionE695ffLog("H7", "OwnerAuthoritativeNetworkTransform.OnNetworkSpawn", "spawn",
                "{\"isOwner\":" + (IsOwner ? "true" : "false") + ",\"isServer\":" + (IsServer ? "true" : "false") + ",\"ownerAuthoritative\":true}");
            #endregion
        }
    }
}

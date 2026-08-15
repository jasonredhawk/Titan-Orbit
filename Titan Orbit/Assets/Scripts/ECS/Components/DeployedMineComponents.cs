using Unity.Entities;
using Unity.Mathematics;
using Unity.NetCode;

namespace TitanOrbit.ECS
{
    /// <summary>
    /// One live mine sitting in space. Ghosted on the owner ship so late joiners see it.
    /// Position is world-absolute (unbounded XZ). Clients pose the Bomb_4 mesh with
    /// <c>ToroidalMapEcs.GetDisplayPosition</c> — they never wrap the local ship.
    /// <para>
    /// [NETCODE] Must be baked on the starship ghost (<see cref="Authoring.StarshipGhostAuthoring"/>).
    /// Adding this buffer only at runtime does <b>not</b> replicate GhostFields.
    /// </para>
    /// Paired with <see cref="ShipMineDeploySystem"/> (append) and <see cref="MineSimulationSystem"/> (explode).
    /// </summary>
    public struct DeployedMineElement : IBufferElementData
    {
        /// <summary>Logical world position (flight-plane Y). Display unwraps per local ship.</summary>
        [GhostField(Quantization = 100)] public float3 Position;

        /// <summary>Owner team as byte (cast to <see cref="Core.TeamId"/>).</summary>
        [GhostField] public byte OwnerTeam;

        /// <summary>Shooter network id — own ship never triggers this mine.</summary>
        [GhostField] public int OwnerNetworkId;

        /// <summary>Stamped store purchase level (1-based).</summary>
        [GhostField] public int ItemLevel;

        /// <summary>Monotonic id for VFX dedupe (visual driver + <see cref="MineExplodeRpc"/>).</summary>
        [GhostField] public uint Sequence;

        /// <summary>Server <c>ElapsedTime</c> when the mine self-destructs.</summary>
        [GhostField] public double ExpireTime;

        /// <summary>Center damage on the contact target and at the blast origin.</summary>
        [GhostField(Quantization = 100)] public float Damage;

        /// <summary>Contact trigger radius (added to the other body's hull).</summary>
        [GhostField(Quantization = 100)] public float HitRadius;

        /// <summary>Concussive AoE radius. Linear falloff to 0 at the edge.</summary>
        [GhostField(Quantization = 100)] public float BlastRadius;

        /// <summary>Knockback impulse applied to enemy ships in the blast.</summary>
        [GhostField(Quantization = 100)] public float BlastForce;

        /// <summary>Bomb_4 mesh size vs a 1× mine.</summary>
        [GhostField(Quantization = 100)] public float VisualScale;

        /// <summary>Burst size for a 1× mine. Final VFX scale is VisualScale × this.</summary>
        [GhostField(Quantization = 100)] public float ExplosionVfxScale;
    }

    /// <summary>
    /// [NETCODE] Server → all clients: play the mine explosion VFX at <see cref="Position"/>.
    /// Mines are ghosted on the ship buffer; this RPC covers the burst after the element is removed
    /// (and host in-process via <see cref="MineExplosionBridge"/>).
    /// </summary>
    public struct MineExplodeRpc : IRpcCommand
    {
        /// <summary>Same id as <see cref="DeployedMineElement.Sequence"/>.</summary>
        public uint Sequence;

        /// <summary>World explode position (logical / unbounded XZ).</summary>
        public float3 Position;

        /// <summary>Owner team as byte — picks the FireballsV2 team impact when the catalog prefab is null.</summary>
        public byte OwnerTeam;

        /// <summary>Stamped mine level — catalog row for VFX prefab fallback.</summary>
        public int ItemLevel;

        /// <summary>Bomb_4 mesh scale. Impact VFX is this times <see cref="ExplosionVfxScale"/>.</summary>
        public float VisualScale;

        /// <summary>Burst size for a 1× mine (not the bullet-bank 0.25 global mul).</summary>
        public float ExplosionVfxScale;

        /// <summary>Center damage — impact sound pitch only (server owns real damage).</summary>
        public float Damage;
    }
}

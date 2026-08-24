using System.Collections.Generic;
using Unity.Entities;
using Unity.Mathematics;
using Unity.NetCode;
using Unity.Physics;

namespace TitanOrbit.ECS
{
    /// <summary>
    /// Predicted ships (local and remotes) keep <see cref="PhysicsVelocity"/>. Interpolated
    /// map ghosts must not — Unity's default variant puts it on <see cref="GhostPrefabType.All"/>,
    /// so <c>ExportPhysicsWorld</c> overwrites GhostUpdate pose every rollback tick.
    /// </summary>
    [GhostComponentVariation(typeof(PhysicsVelocity), "Predicted ships only")]
    [GhostComponent(PrefabType = GhostPrefabType.AllPredicted, SendTypeOptimization = GhostSendType.OnlyPredictedClients)]
    public struct PhysicsVelocityPredictedOnlyVariant
    {
        /// <summary>World-space linear velocity (m/s), same quantization as NetCode's default.</summary>
        [GhostField(Quantization = 1000)] public float3 Linear;

        /// <summary>World-space angular velocity (rad/s).</summary>
        [GhostField(Quantization = 1000)] public float3 Angular;
    }

    /// <summary>
    /// Registers <see cref="PhysicsVelocityPredictedOnlyVariant"/> as the default so
    /// interpolated map ghosts never bake <see cref="PhysicsVelocity"/>. Takes precedence
    /// over NetCode's <c>PhysicsDefaultVariantSystem</c> (that one uses TrySet).
    /// </summary>
    public sealed partial class TitanOrbitPhysicsVelocityVariantSystem : DefaultVariantSystemBase
    {
        /// <summary>Maps PhysicsVelocity → predicted-only variant for parent ship ghosts.</summary>
        protected override void RegisterDefaultVariants(Dictionary<ComponentType, Rule> defaultVariants)
        {
            defaultVariants.Add(
                ComponentType.ReadWrite<PhysicsVelocity>(),
                Rule.OnlyParents(typeof(PhysicsVelocityPredictedOnlyVariant)));
        }
    }
}

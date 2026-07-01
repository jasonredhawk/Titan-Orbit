using TitanOrbit.ECS;
using Unity.Entities;
using Unity.NetCode;
using UnityEngine;

namespace TitanOrbit.ECS.Authoring
{
    public class StarshipGhostAuthoring : MonoBehaviour
    {
        public float EngineThrust = 40f;
        public float MaxSpeed = 35f;
        public float RotationSpeed = 180f;
        public float BrakeDeceleration = 25f;
        public float Mass = 5f;

        class Baker : Baker<StarshipGhostAuthoring>
        {
            public override void Bake(StarshipGhostAuthoring authoring)
            {
                var entity = GetEntity(TransformUsageFlags.Dynamic);
                AddComponent(entity, new ShipTag());
                AddComponent(entity, new ShipState
                {
                    Health = 100f,
                    MaxHealth = 100f,
                    ShipLevel = 1,
                    GemCapacity = 50f,
                    AwaitingTeamSelection = true,
                });
                AddComponent(entity, new ShipMotorConfig
                {
                    EngineThrust = authoring.EngineThrust,
                    MaxSpeed = authoring.MaxSpeed,
                    RotationSpeed = authoring.RotationSpeed,
                    BrakeDeceleration = authoring.BrakeDeceleration,
                    Mass = authoring.Mass,
                });
                AddComponent(entity, new ShipInput());
                AddComponent(entity, new ShipKinematics());
            }
        }
    }
}

using Unity.Entities;
using Unity.NetCode;

namespace TitanOrbit.Game
{
    /// <summary>
    /// Disabled legacy path. People-transport GameObjects are owned by
    /// <see cref="PeopleTransportVfxDriver"/> (bridge → Instantiates → magnet), not ECS presentation
    /// entities. Kept disabled so it cannot despawn or fight the driver.
    /// </summary>
    [WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation)]
    [UpdateInGroup(typeof(PresentationSystemGroup), OrderLast = true)]
    public partial class PeopleTransportVisualSyncSystem : SystemBase
    {
        /// <summary>Permanently disable — see class summary.</summary>
        protected override void OnCreate()
        {
            Enabled = false;
        }

        /// <summary>No-op.</summary>
        protected override void OnUpdate() { }
    }
}

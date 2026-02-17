using UnityEngine;

namespace TitanOrbit.AI
{
    /// <summary>
    /// Marker component added to AI ships before Spawn so Starship skips repositioning in OnNetworkSpawn.
    /// </summary>
    public class AIShipMarker : MonoBehaviour { }
}

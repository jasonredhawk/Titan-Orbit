using UnityEngine;

namespace TitanOrbit.Entities
{
    /// <summary>
    /// Trigger zone around a home planet (orbit/loading zone). When a starship enters and is not
    /// moving with right mouse, it auto-orbits and can interact (deposit gems, load/unload people, future store).
    /// </summary>
    [RequireComponent(typeof(SphereCollider))]
    public class HomePlanetOrbitZone : MonoBehaviour
    {
        [SerializeField] private HomePlanet homePlanet;
        private SphereCollider zoneCollider;

        private void Awake()
        {
            if (homePlanet == null)
                homePlanet = GetComponentInParent<HomePlanet>();
            ResolveZoneCollider();
        }

        private void ResolveZoneCollider()
        {
            zoneCollider = null;
            foreach (var c in GetComponents<SphereCollider>())
            {
                if (c.isTrigger)
                {
                    zoneCollider = c;
                    break;
                }
            }
        }

        private void OnValidate()
        {
            ResolveZoneCollider();
        }

        public HomePlanet HomePlanet => homePlanet;

        public void SetHomePlanet(HomePlanet planet)
        {
            homePlanet = planet;
        }

        private void OnTriggerEnter(Collider other)
        {
            Starship ship = other.GetComponent<Starship>();
            if (ship != null)
                ship.EnterOrbitZone(homePlanet);
        }

        private void OnTriggerExit(Collider other)
        {
            Starship ship = other.GetComponent<Starship>();
            if (ship != null)
                ship.ExitOrbitZone(homePlanet);
        }
    }
}

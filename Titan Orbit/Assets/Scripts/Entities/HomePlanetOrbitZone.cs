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
            zoneCollider = GetComponent<SphereCollider>();
            if (zoneCollider != null)
                zoneCollider.isTrigger = true;
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

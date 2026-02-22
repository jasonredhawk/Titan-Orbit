using UnityEngine;
using Unity.Netcode;
using UnityEngine.UI;
using TMPro;
using TitanOrbit.Entities;

namespace TitanOrbit.UI
{
    /// <summary>
    /// Top-left ship stats HUD: horizontal progress bars (Health, Energy, Gems, People)
    /// with icons (CleanFlatIcon) and bar styling (Shift Sci-Fi UI). Same width as minimap.
    /// Uses Unity UI: icon Image, Slider (or fill Image), value Text per row.
    /// </summary>
    public class ShipStatsFpsStyleHUD : MonoBehaviour
    {
        [Header("References (assigned by GameSetup or inspector)")]
        [SerializeField] private Image iconHealth;
        [SerializeField] private Image iconEnergy;
        [SerializeField] private Image iconGems;
        [SerializeField] private Image iconPeople;
        [SerializeField] private Slider barHealth;
        [SerializeField] private Slider barEnergy;
        [SerializeField] private Slider barGems;
        [SerializeField] private Slider barPeople;
        [SerializeField] private TextMeshProUGUI valueHealth;
        [SerializeField] private TextMeshProUGUI valueEnergy;
        [SerializeField] private TextMeshProUGUI valueGems;
        [SerializeField] private TextMeshProUGUI valuePeople;

        private Starship _playerShip;

        private Starship GetPlayerShip()
        {
            if (_playerShip != null && !_playerShip.IsDead) return _playerShip;
            _playerShip = null;
            if (NetworkManager.Singleton == null || NetworkManager.Singleton.SpawnManager == null)
                return null;
            NetworkObject localPlayer = NetworkManager.Singleton.SpawnManager.GetLocalPlayerObject();
            if (localPlayer == null) return null;
            var ship = localPlayer.GetComponent<Starship>();
            if (ship != null && !ship.IsDead) _playerShip = ship;
            return _playerShip;
        }

        private void LateUpdate()
        {
            Starship ship = GetPlayerShip();
            // Always keep the panel visible; when no ship, show zeros so the HUD is visible before spawn
            if (ship == null)
            {
                if (barHealth != null) barHealth.value = 0f;
                if (barEnergy != null) barEnergy.value = 0f;
                if (barGems != null) barGems.value = 0f;
                if (barPeople != null) barPeople.value = 0f;
                if (valueHealth != null) valueHealth.text = "0";
                if (valueEnergy != null) valueEnergy.text = "0";
                if (valueGems != null) valueGems.text = "0";
                if (valuePeople != null) valuePeople.text = "0";
                return;
            }

            if (barHealth != null)
            {
                float healthMax = ship.MaxHealth > 0 ? ship.MaxHealth : 1f;
                barHealth.value = ship.CurrentHealth / healthMax;
                if (valueHealth != null) valueHealth.text = Mathf.FloorToInt(ship.CurrentHealth).ToString();
            }
            if (barEnergy != null)
            {
                float cap = ship.EnergyCapacity;
                barEnergy.value = cap > 0 ? ship.CurrentEnergy / cap : 0f;
                if (valueEnergy != null) valueEnergy.text = Mathf.FloorToInt(ship.CurrentEnergy).ToString();
            }
            if (barGems != null)
            {
                float cap = ship.GemCapacity;
                barGems.value = cap > 0 ? ship.CurrentGems / cap : 0f;
                if (valueGems != null) valueGems.text = Mathf.FloorToInt(ship.CurrentGems).ToString();
            }
            if (barPeople != null)
            {
                float cap = ship.PeopleCapacity;
                barPeople.value = cap > 0 ? ship.CurrentPeople / cap : 0f;
                if (valuePeople != null) valuePeople.text = Mathf.FloorToInt(ship.CurrentPeople).ToString();
            }
        }
    }
}

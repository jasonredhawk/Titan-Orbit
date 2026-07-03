using TitanOrbit.Core;
using TitanOrbit.Simulation;
using UnityEngine;

namespace TitanOrbit.Game
{
    /// <summary>Team-colored matrix shield VFX around the gem moon (legacy PlanetGemMoon matrix shield).</summary>
    public class GemMoonMatrixShieldVisual : MonoBehaviour
    {
        const string ShieldRootName = "GemMoonMatrixShield";

        [SerializeField] float matrixShieldScaleMultiplier = 1f;

        PlanetGemMoonVisualProxy _moon;
        TeamId _team = TeamId.None;
        GameObject _shieldInstance;
        Quaternion _baseLocalRotation = Quaternion.identity;
        float _baseXScale = 1f;
        float _baseYScale = 1f;
        float _lastDockLocalRadius = -1f;
        ParticleSystem[] _particles;

        public void Configure(PlanetGemMoonVisualProxy moon, TeamId team)
        {
            _moon = moon;
            if (_team != team)
            {
                _team = team;
                DestroyShieldInstance();
            }
        }

        public static GemMoonMatrixShieldVisual EnsureOnMoonRoot(Transform moonRoot, PlanetGemMoonVisualProxy moon, TeamId team)
        {
            Transform existing = moonRoot.Find(ShieldRootName);
            GameObject shieldGo;
            if (existing != null)
                shieldGo = existing.gameObject;
            else
            {
                shieldGo = new GameObject(ShieldRootName);
                shieldGo.transform.SetParent(moonRoot, false);
            }

            var visual = shieldGo.GetComponent<GemMoonMatrixShieldVisual>();
            if (visual == null)
                visual = shieldGo.AddComponent<GemMoonMatrixShieldVisual>();
            visual.Configure(moon, team);
            return visual;
        }

        void OnDestroy()
        {
            DestroyShieldInstance();
        }

        void LateUpdate()
        {
            if (_moon == null)
                return;

            UpdateShieldVisual(_moon.CurrentShieldRatio);
        }

        void EnsureShieldInstance()
        {
            if (_shieldInstance != null)
                return;

            GameObject prefab = GemMoonShieldPrefabLibrary.GetPrefab(_team);
            if (prefab == null)
                return;

            _shieldInstance = Instantiate(prefab, transform);
            _shieldInstance.transform.localPosition = Vector3.zero;
            _baseLocalRotation = _shieldInstance.transform.localRotation;

            Vector3 baseScale = _shieldInstance.transform.localScale;
            _baseXScale = Mathf.Max(0.0001f, Mathf.Abs(baseScale.x));
            _baseYScale = Mathf.Max(0.0001f, Mathf.Abs(baseScale.y));
            _lastDockLocalRadius = -1f;
            _particles = _shieldInstance.GetComponentsInChildren<ParticleSystem>(true);
        }

        void UpdateShieldVisual(float shieldRatio)
        {
            EnsureShieldInstance();
            if (_shieldInstance == null)
                return;

            Vector3 axisLocal = transform.InverseTransformDirection(_moon.SpinAxisWorld);
            if (axisLocal.sqrMagnitude < 0.0001f)
                axisLocal = Vector3.up;
            axisLocal.Normalize();
            Quaternion alignToMoonAxis = Quaternion.FromToRotation(Vector3.up, axisLocal);
            _shieldInstance.transform.localRotation = alignToMoonAxis * _baseLocalRotation;

            float shellOuterLocal = _moon.MoonVisualShellOuterRadiusLocal;
            if (_lastDockLocalRadius < 0f || Mathf.Abs(shellOuterLocal - _lastDockLocalRadius) > 0.001f)
            {
                _lastDockLocalRadius = shellOuterLocal;
                float denom = Mathf.Max(0.001f, PlanetGemMoonMath.MatrixShieldRadiusReference);
                float scaleMultiplier = (shellOuterLocal * PlanetGemMoonMath.MatrixShieldOrbitZoneEdgeExpandMultiplier / denom)
                    * matrixShieldScaleMultiplier;
                float scaleX = _baseXScale * scaleMultiplier;
                float scaleY = _baseYScale * scaleMultiplier;
                _shieldInstance.transform.localScale = new Vector3(scaleX, scaleY, scaleX);
            }

            bool shouldBeActive = shieldRatio > 0.001f;
            if (_shieldInstance.activeSelf != shouldBeActive)
                _shieldInstance.SetActive(shouldBeActive);

            if (!shouldBeActive || _particles == null)
                return;

            float ratio = Mathf.Clamp01(shieldRatio);
            for (int i = 0; i < _particles.Length; i++)
            {
                if (_particles[i] == null)
                    continue;
                var emission = _particles[i].emission;
                emission.rateOverTimeMultiplier = ratio;
            }
        }

        void DestroyShieldInstance()
        {
            if (_shieldInstance != null)
            {
                if (Application.isPlaying)
                    Destroy(_shieldInstance);
                else
                    DestroyImmediate(_shieldInstance);
                _shieldInstance = null;
            }

            _particles = null;
            _lastDockLocalRadius = -1f;
        }
    }
}

using TitanOrbit.ECS;
using Unity.Entities;
using UnityEngine;

namespace TitanOrbit.Game
{
    /// <summary>
    /// Local-MEGA aim markers: one icon per unoccupied auto-aimed mount.
    /// Sits at the ghosted target distance along the <b>current</b> barrel heading, so a
    /// turret that has not finished traversing shows the icon at the right range but the
    /// wrong angle. Client presentation only — no sim writes, no ship wrap.
    /// </summary>
    [DefaultExecutionOrder(67025)]
    public sealed class MegaShipAimReticleVisual : MonoBehaviour
    {
        const float IconWorldSize = 2.4f;
        const float MinDistance = 0.75f;

        static Sprite s_iconSprite;

        Transform[] _icons;

        /// <summary>Creates or updates reticles for the local player's MEGA only.</summary>
        public static void Sync(MegaShipWeaponVisualBinding binding, EntityManager em)
        {
            if (binding == null || binding.gameObject == null)
                return;

            var visual = binding.GetComponent<MegaShipAimReticleVisual>();
            if (visual == null)
                visual = binding.gameObject.AddComponent<MegaShipAimReticleVisual>();

            visual.Apply(binding, em);
        }

        void Apply(MegaShipWeaponVisualBinding binding, EntityManager em)
        {
            if (ClientJoinSettleCache.ShouldSkipShipEntityQueries)
            {
                SetAllActive(false);
                return;
            }

            Entity ship = binding.ShipEntity;
            if (ship == Entity.Null || !em.Exists(ship))
            {
                SetAllActive(false);
                return;
            }

            var world = em.World;
            if (world == null
                || !EcsGameBridge.TryGetLocalShipEntityOnWorld(world, out Entity local)
                || local != ship)
            {
                SetAllActive(false);
                return;
            }

            if (!em.HasBuffer<MegaShipGunnerSlotElement>(ship))
            {
                SetAllActive(false);
                return;
            }

            var gunners = em.GetBuffer<MegaShipGunnerSlotElement>(ship);
            Transform[] barrels = binding.Barrels;
            if (barrels == null)
            {
                SetAllActive(false);
                return;
            }

            EnsureIcons(barrels.Length);
            for (int i = 0; i < _icons.Length; i++)
            {
                Transform icon = _icons[i];
                if (icon == null)
                    continue;

                if (i >= barrels.Length || barrels[i] == null || i >= gunners.Length)
                {
                    icon.gameObject.SetActive(false);
                    continue;
                }

                var slot = gunners[i];
                if (slot.OccupiedByNetworkId != 0 || slot.TargetDistance < MinDistance)
                {
                    icon.gameObject.SetActive(false);
                    continue;
                }

                Vector3 muzzle = barrels[i].position;
                Vector3 fwd = MegaShipWeaponVisualSync.Flatten(barrels[i].forward);
                Vector3 pos = muzzle + fwd * slot.TargetDistance;
                pos.y = muzzle.y;
                icon.position = pos;
                icon.rotation = Quaternion.LookRotation(Vector3.down, fwd);
                icon.gameObject.SetActive(true);
            }
        }

        void EnsureIcons(int count)
        {
            if (_icons != null && _icons.Length == count)
                return;

            if (_icons != null)
            {
                for (int i = 0; i < _icons.Length; i++)
                {
                    if (_icons[i] != null)
                        Destroy(_icons[i].gameObject);
                }
            }

            _icons = new Transform[count];
            Sprite sprite = GetOrCreateSprite();
            for (int i = 0; i < count; i++)
            {
                var go = new GameObject("MegaAimReticle");
                go.transform.SetParent(null, false);
                go.transform.localScale = Vector3.one * IconWorldSize;
                var sr = go.AddComponent<SpriteRenderer>();
                sr.sprite = sprite;
                sr.color = new Color(1f, 0.45f, 0.2f, 0.92f);
                sr.sortingOrder = 40;
                go.SetActive(false);
                _icons[i] = go.transform;
            }
        }

        void SetAllActive(bool on)
        {
            if (_icons == null)
                return;
            for (int i = 0; i < _icons.Length; i++)
            {
                if (_icons[i] != null)
                    _icons[i].gameObject.SetActive(on);
            }
        }

        void OnDisable() => SetAllActive(false);

        void OnDestroy()
        {
            if (_icons == null)
                return;
            for (int i = 0; i < _icons.Length; i++)
            {
                if (_icons[i] != null)
                    Destroy(_icons[i].gameObject);
            }

            _icons = null;
        }

        static Sprite GetOrCreateSprite()
        {
            if (s_iconSprite != null)
                return s_iconSprite;

            const int size = 64;
            var tex = new Texture2D(size, size, TextureFormat.ARGB32, false)
            {
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp,
                name = "MegaAimReticleTex",
            };

            float cx = (size - 1) * 0.5f;
            float outer = 28f;
            float inner = 20f;
            float arm = 3f;
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float dx = x - cx;
                    float dy = y - cx;
                    float r = Mathf.Sqrt(dx * dx + dy * dy);
                    bool ring = r <= outer && r >= inner;
                    bool cross = (Mathf.Abs(dx) <= arm && Mathf.Abs(dy) <= outer)
                                 || (Mathf.Abs(dy) <= arm && Mathf.Abs(dx) <= outer);
                    bool hole = r < 8f;
                    tex.SetPixel(x, y, ring || (cross && !hole)
                        ? Color.white
                        : Color.clear);
                }
            }

            tex.Apply(false, true);
            s_iconSprite = Sprite.Create(tex, new Rect(0f, 0f, size, size), new Vector2(0.5f, 0.5f), 32f);
            s_iconSprite.name = "MegaAimReticle";
            return s_iconSprite;
        }
    }
}

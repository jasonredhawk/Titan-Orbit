using System;
using TitanOrbit.Simulation;
using TMPro;
using UnityEngine;
using UnityEngine.Rendering;

namespace TitanOrbit.Game
{
    /// <summary>
    /// [HYBRID] Runtime-created world-space popup: parks on the target, billboards to camera,
    /// pops on each accumulate, and fades only after the streak window expires.
    /// Layout and motion come from <see cref="FloatingText"/>. Cosmetic only.
    /// <para>
    /// Execution order 67050: after <see cref="EcsWorldVisualizer"/> (66000) and
    /// <see cref="CameraFollowEcs"/> (67001) so ship-follow popups (gem pickup) stay locked
    /// to the same presentation pose the camera used this frame.
    /// </para>
    /// </summary>
    [DefaultExecutionOrder(67050)]
    public class FloatingCountPopup : MonoBehaviour
    {
        const float MinPopupWorldY = 4f;
        const int TextSortingOrder = 5001;
        const int IconSortingOrder = 5000;
        static readonly int RenderQueueOverlay = (int)RenderQueue.Overlay;

        const float PopDuration = 0.15f;
        const float PopPeakScale = 1.35f;
        const float FadeInDuration = 0.08f;
        const float FadeRiseSpeed = 1.15f;

        enum Phase
        {
            Hot = 0,
            Fading = 1,
        }

        TMP_Text tmpText;
        SpriteRenderer iconRenderer;

        Color baseColor = Color.white;
        float elapsed;
        float fadeDuration;
        float lockedY;
        Transform followAnchor;
        Vector3 followWorldOffset;
        int stackLane;
        float stackSpacing;
        Vector3 worldMotionOffset;
        bool _materialReady;
        Camera _cachedCamera;
        Phase _phase;
        float _popElapsed = 99f;
        float _iconScale = 2f;
        float _iconLeftPadding = 8f;
        float _baseWorldScale = 0.155f;
        float _extraHeight = 8f;
        Vector3 _worldOffset;
        float _hotAge;
        float _fontSize = 32f;
        float _textLeft;
        float _textRight;
        float _bodyRadius;
        float _cachedTargetHeight;
        Renderer _targetRenderer;
        Vector3 _lockedWorldPos;
        bool _hasLockedWorldPos;

        public Action<FloatingCountPopup> OnFinished;

        void EnsureTextAndIcon()
        {
            if (tmpText == null)
            {
                Transform textT = transform.Find("Text");
                GameObject textGo = textT != null ? textT.gameObject : new GameObject("Text");
                if (textT == null)
                    textGo.transform.SetParent(transform, false);

                var text3d = textGo.GetComponent<TextMeshPro>();
                if (text3d == null)
                    text3d = textGo.AddComponent<TextMeshPro>();
                tmpText = text3d;
            }

            if (iconRenderer == null)
            {
                Transform iconT = transform.Find("Icon");
                GameObject iconGo = iconT != null ? iconT.gameObject : new GameObject("Icon");
                if (iconT == null)
                    iconGo.transform.SetParent(transform, false);

                iconRenderer = iconGo.GetComponent<SpriteRenderer>();
                if (iconRenderer == null)
                    iconRenderer = iconGo.AddComponent<SpriteRenderer>();
            }
        }

        public void Initialize(
            string message,
            Sprite iconSprite,
            Color color,
            TMP_FontAsset font,
            FloatingText settings,
            Transform followAnchor,
            Vector3 followWorldOffset,
            int stackLane,
            float stackSpacing,
            float bodyRadius = 0f)
        {
            ApplySettings(settings);
            _hasLockedWorldPos = false;
            _cachedTargetHeight = 0f;
            SetFollow(followAnchor, followWorldOffset, stackLane, stackSpacing, bodyRadius);
            worldMotionOffset = Vector3.zero;
            EnsureTextAndIcon();

            if (tmpText == null)
            {
                Debug.LogWarning("FloatingCountPopup: TMP_Text missing; cannot initialize popup text.");
                Finish();
                return;
            }

            if (_cachedCamera == null)
                _cachedCamera = Camera.main;

            elapsed = 0f;
            fadeDuration = 0f;
            _hotAge = 0f;
            _phase = Phase.Hot;
            PlayPop();

            if (followAnchor == null)
            {
                Vector3 initPos = transform.position + _worldOffset;
                lockedY = Mathf.Max(initPos.y + ResolveLiftY(), MinPopupWorldY);
                initPos.y = lockedY;
                transform.position = initPos;
            }
            else
            {
                ApplyFollowPosition();
                RememberFollowPose();
            }

            baseColor = color;
            ApplyMessage(message, font, _fontSize);

            if (!_materialReady)
            {
                ApplyReadableTextMaterial(tmpText);
                _materialReady = true;
            }
            else
            {
                ApplyNoOutlineStyle(tmpText);
            }

            ApplyIcon(iconSprite, 0f);
            ApplyZoomScale();
            ApplyPopScale();
            ApplyAlpha(0f);
        }

        public void Refresh(
            string message,
            Color color,
            Transform followAnchor,
            Vector3 followWorldOffset,
            int stackLane,
            float stackSpacing,
            Sprite iconSprite = null,
            float bodyRadius = -1f)
        {
            PullLiveSettings();
            SetFollow(followAnchor, followWorldOffset, stackLane, stackSpacing, bodyRadius);
            worldMotionOffset = Vector3.zero;
            _phase = Phase.Hot;
            elapsed = 0f;
            fadeDuration = 0f;
            _hotAge = 0f;
            baseColor = color;
            PlayPop();

            if (tmpText != null)
            {
                tmpText.text = message ?? string.Empty;
                tmpText.alignment = TextAlignmentOptions.Left;
                tmpText.ForceMeshUpdate();
                CacheTextLeft();
            }

            if (iconSprite != null)
                ApplyIcon(iconSprite, 1f);
            else
                LayoutCenteredGroup();

            if (followAnchor == null)
            {
                Vector3 pos = transform.position;
                lockedY = Mathf.Max(pos.y, MinPopupWorldY);
                pos.y = lockedY;
                transform.position = pos;
            }
            else
            {
                ApplyFollowPosition();
                RememberFollowPose();
            }

            ApplyAlpha(1f);
        }

        void ApplySettings(FloatingText settings)
        {
            if (settings == null)
                return;

            _fontSize = settings.FontSize;
            _iconScale = settings.IconScale;
            _iconLeftPadding = settings.IconLeftPadding;
            _extraHeight = settings.ExtraHeight;
            _worldOffset = settings.worldOffset;
            _baseWorldScale = BodyCollisionMath.ShipPresentationScale;
        }

        void PullLiveSettings()
        {
            var manager = WorldFloatingCountManager.Instance;
            if (manager != null)
                ApplySettings(manager.Settings);
        }

        void SetFollow(Transform anchor, Vector3 worldOffset, int lane, float spacing, float bodyRadius)
        {
            followWorldOffset = worldOffset;
            stackLane = Mathf.Max(0, lane);
            stackSpacing = Mathf.Max(0f, spacing);
            if (bodyRadius >= 0f)
                _bodyRadius = Mathf.Max(0f, bodyRadius);

            if (IsUsableAnchor(anchor))
            {
                followAnchor = anchor;
                CacheTargetHeight(anchor);
                return;
            }

            // Dead / hidden target (asteroid kill): keep the last parked pose.
            CacheTargetHeight(null);
            if (followAnchor != null && !IsUsableAnchor(followAnchor))
                LockAtCurrentPose();
            else
                followAnchor = null;
        }

        static bool IsUsableAnchor(Transform anchor) =>
            anchor != null && anchor.gameObject.activeInHierarchy;

        void CacheTargetHeight(Transform anchor)
        {
            _targetRenderer = null;
            float height = _bodyRadius;
            if (IsUsableAnchor(anchor))
            {
                _targetRenderer = anchor.GetComponent<Renderer>();
                if (_targetRenderer == null)
                    _targetRenderer = anchor.GetComponentInChildren<Renderer>();

                if (_targetRenderer != null && _targetRenderer.enabled)
                    height = Mathf.Max(height, _targetRenderer.bounds.extents.y);
                _cachedTargetHeight = height;
                return;
            }

            // Don't replace a good height with 0 after the mesh is gone.
            if (_cachedTargetHeight <= 0.0001f)
                _cachedTargetHeight = height;
        }

        void LockAtCurrentPose()
        {
            if (!_hasLockedWorldPos)
            {
                Vector3 pos = transform.position;
                pos.y = pos.y + ResolveLiftY() + _worldOffset.y;
                _lockedWorldPos = pos;
                _hasLockedWorldPos = true;
            }

            lockedY = _lockedWorldPos.y;
            followAnchor = null;
        }

        void RememberFollowPose()
        {
            _lockedWorldPos = transform.position;
            lockedY = _lockedWorldPos.y;
            _hasLockedWorldPos = true;
        }

        float ResolveLiftY() => _cachedTargetHeight + Mathf.Max(0f, _extraHeight);

        public void RelocateWorld(Vector3 worldPosition, float bodyRadius = 0f)
        {
            PullLiveSettings();
            followAnchor = null;
            if (bodyRadius >= 0f)
                _bodyRadius = Mathf.Max(0f, bodyRadius);
            CacheTargetHeight(null);
            worldMotionOffset = Vector3.zero;
            worldPosition += _worldOffset;
            lockedY = Mathf.Max(worldPosition.y + ResolveLiftY(), MinPopupWorldY);
            worldPosition.y = lockedY;
            transform.position = worldPosition;
            _lockedWorldPos = worldPosition;
            _hasLockedWorldPos = true;
        }

        public void BeginFadeOut(float duration)
        {
            if (!IsUsableAnchor(followAnchor))
                LockAtCurrentPose();
            _phase = Phase.Fading;
            elapsed = 0f;
            fadeDuration = Mathf.Max(0.08f, duration);
        }

        void ApplyMessage(string message, TMP_FontAsset font, float fontSize)
        {
            tmpText.text = message ?? string.Empty;
            if (font != null)
                tmpText.font = font;
            _fontSize = Mathf.Max(1f, fontSize);
            tmpText.fontSize = _fontSize;
            tmpText.fontStyle = FontStyles.Bold;
            tmpText.transform.localScale = Vector3.one;
            tmpText.alignment = TextAlignmentOptions.Left;
            tmpText.enableWordWrapping = false;
            tmpText.richText = false;
            tmpText.ForceMeshUpdate();
            CacheTextLeft();
        }

        void PlayPop()
        {
            _popElapsed = 0f;
        }

        void ApplyIcon(Sprite iconSprite, float alpha)
        {
            if (iconRenderer == null)
                return;

            if (iconSprite == null)
            {
                iconRenderer.enabled = false;
                return;
            }

            iconRenderer.sprite = iconSprite;
            iconRenderer.enabled = true;
            iconRenderer.sortingOrder = IconSortingOrder;
            LayoutCenteredGroup();
            iconRenderer.color = WithAlpha(baseColor, alpha);

            Material iconMat = iconRenderer.material;
            if (iconMat != null)
            {
                iconMat.renderQueue = RenderQueueOverlay;
                iconMat.SetInt("_ZTest", (int)CompareFunction.Always);
            }
        }

        void CacheTextLeft()
        {
            _textLeft = 0f;
            _textRight = 0f;
            if (tmpText == null)
                return;
            tmpText.alignment = TextAlignmentOptions.Left;
            tmpText.transform.localPosition = Vector3.zero;
            _textLeft = tmpText.textBounds.min.x;
            _textRight = tmpText.textBounds.max.x;
        }

        /// <summary>
        /// Icon stays left of the digits; the whole icon+number group is centered on the target.
        /// </summary>
        void LayoutCenteredGroup()
        {
            PullLiveSettings();
            _iconScale = Mathf.Max(0.05f, _iconScale);

            if (tmpText != null)
                tmpText.transform.localPosition = Vector3.zero;

            float groupLeft = _textLeft;
            float groupRight = _textRight;
            float iconX = 0f;
            float iconHalfW = 0f;
            bool hasIcon = iconRenderer != null && iconRenderer.enabled && iconRenderer.sprite != null;
            if (hasIcon)
            {
                iconHalfW = iconRenderer.sprite.bounds.extents.x * _iconScale;
                iconX = _textLeft - _iconLeftPadding - iconHalfW;
                groupLeft = iconX - iconHalfW;
            }

            float mid = (groupLeft + groupRight) * 0.5f;
            if (tmpText != null)
                tmpText.transform.localPosition = new Vector3(-mid, 0f, 0f);
            if (hasIcon)
                iconRenderer.transform.localPosition = new Vector3(iconX - mid, 0f, 0f);
        }

        static void ApplyReadableTextMaterial(TMP_Text text)
        {
            if (text == null)
                return;

            Material mat = text.fontMaterial;
            if (mat == null)
                return;

            ApplyNoOutlineStyle(text);
            mat.renderQueue = RenderQueueOverlay;
            if (mat.HasProperty("_ZTestMode"))
                mat.SetFloat("_ZTestMode", 8f);

            var renderer = text.GetComponent<Renderer>();
            if (renderer != null)
                renderer.sortingOrder = TextSortingOrder;
        }

        static void ApplyNoOutlineStyle(TMP_Text text)
        {
            if (text == null)
                return;

            Material mat = text.fontMaterial;
            if (mat == null)
                return;

            mat.DisableKeyword("OUTLINE_ON");
            if (mat.HasProperty("_OutlineWidth"))
                mat.SetFloat("_OutlineWidth", 0f);
            if (mat.HasProperty("_OutlineSoftness"))
                mat.SetFloat("_OutlineSoftness", 0f);

            text.fontStyle = FontStyles.Bold;
        }

        static Vector3 GetRiseDirectionOnPlayPlane(Camera cam)
        {
            if (cam == null)
                return Vector3.forward;

            Vector3 dir = cam.transform.up;
            dir.y = 0f;
            if (dir.sqrMagnitude < 1e-8f)
                dir = Vector3.ProjectOnPlane(-cam.transform.forward, Vector3.up);
            if (dir.sqrMagnitude < 1e-8f)
                dir = Vector3.forward;
            return dir.normalized;
        }

        void Update()
        {
            _popElapsed += Time.deltaTime;

            if (_phase == Phase.Hot)
            {
                _hotAge += Time.deltaTime;
                float fadeIn = FadeInDuration <= 0.001f ? 1f : Mathf.Clamp01(_hotAge / FadeInDuration);
                ApplyAlpha(fadeIn);
            }
            else
            {
                elapsed += Time.deltaTime;
                float t = fadeDuration <= 0.001f ? 1f : Mathf.Clamp01(elapsed / fadeDuration);
                ApplyAlpha(1f - t);

                if (elapsed >= fadeDuration)
                {
                    Finish();
                    return;
                }
            }
        }

        void LateUpdate()
        {
            if (_cachedCamera == null)
                _cachedCamera = Camera.main;
            var cam = _cachedCamera;
            if (cam != null)
                transform.rotation = Quaternion.LookRotation(cam.transform.forward, cam.transform.up);

            if (_phase == Phase.Fading)
                worldMotionOffset += GetRiseDirectionOnPlayPlane(cam) * FadeRiseSpeed * Time.deltaTime;

            ApplyZoomScale();

            if (IsUsableAnchor(followAnchor))
            {
                ApplyFollowPosition();
                RememberFollowPose();
            }
            else
            {
                if (followAnchor != null)
                    LockAtCurrentPose();

                Vector3 pos = _hasLockedWorldPos ? _lockedWorldPos : transform.position;
                if (_phase == Phase.Fading)
                    pos += GetRiseDirectionOnPlayPlane(cam) * FadeRiseSpeed * Time.deltaTime;
                pos.y = _hasLockedWorldPos ? lockedY : pos.y;
                transform.position = pos;
                if (_phase == Phase.Fading)
                {
                    _lockedWorldPos = pos;
                    _lockedWorldPos.y = lockedY;
                }
            }

            LayoutCenteredGroup();
            ApplyPopScale();
        }

        void ApplyFollowPosition()
        {
            if (!IsUsableAnchor(followAnchor))
            {
                LockAtCurrentPose();
                return;
            }

            if (_cachedCamera == null)
                _cachedCamera = Camera.main;

            Vector3 pos = followAnchor.position + followWorldOffset + _worldOffset;
            if (stackLane > 0 && stackSpacing > 0.001f)
            {
                float zoom = WorldFloatingCountManager.ResolveCameraZoomScale();
                pos += GetRiseDirectionOnPlayPlane(_cachedCamera) * (stackLane * stackSpacing * zoom);
            }

            pos += worldMotionOffset;
            pos.y = followAnchor.position.y + ResolveLiftY() + _worldOffset.y;
            transform.position = pos;
        }

        void ApplyZoomScale()
        {
            float zoom = WorldFloatingCountManager.ResolveCameraZoomScale();
            transform.localScale = Vector3.one * (_baseWorldScale * zoom);
        }

        void ApplyPopScale()
        {
            float pop = 1f;
            if (_popElapsed < PopDuration)
            {
                float t = Mathf.Clamp01(_popElapsed / PopDuration);
                float eased = 1f - (1f - t) * (1f - t);
                pop = Mathf.Lerp(PopPeakScale, 1f, eased);
            }

            if (tmpText != null)
                tmpText.transform.localScale = Vector3.one * pop;
            if (iconRenderer != null && iconRenderer.enabled)
                iconRenderer.transform.localScale = Vector3.one * (_iconScale * pop);
        }

        void ApplyAlpha(float alpha)
        {
            alpha = Mathf.Clamp01(alpha);
            Color c = WithAlpha(baseColor, alpha);
            if (tmpText != null)
                tmpText.color = c;
            if (iconRenderer != null && iconRenderer.enabled)
                iconRenderer.color = WithAlpha(baseColor, alpha);
        }

        static Color WithAlpha(Color color, float alpha)
        {
            color.a = alpha;
            return color;
        }

        void Finish()
        {
            fadeDuration = 0f;
            followAnchor = null;
            _hasLockedWorldPos = false;
            _phase = Phase.Fading;
            if (OnFinished != null)
            {
                OnFinished.Invoke(this);
                return;
            }

            Destroy(gameObject);
        }
    }
}

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
        float _shipExtraHeight = 2f;
        Vector3 _worldOffset;
        float _hotAge;
        float _fontSize = 32f;
        float _textLeft;
        float _textRight;
        float _bodyRadius;
        float _cachedTargetHeight;
        Vector3 _lockedWorldPos;
        bool _hasLockedWorldPos;
        bool _clearShipHull;

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
            float bodyRadius = 0f,
            bool clearShipHull = false)
        {
            ApplySettings(settings);
            _hasLockedWorldPos = false;
            _cachedTargetHeight = 0f;
            _clearShipHull = clearShipHull;
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
                lockedY = initPos.y;
                initPos.y = LiftAboveLocalShipIfOverlapping(initPos);
                transform.position = initPos;
                _lockedWorldPos = initPos;
                _hasLockedWorldPos = true;
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
            float bodyRadius = -1f,
            bool clearShipHull = false)
        {
            PullLiveSettings();
            _clearShipHull = clearShipHull;
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
                if (!_hasLockedWorldPos)
                    lockedY = pos.y;
                pos.y = LiftAboveLocalShipIfOverlapping(new Vector3(pos.x, lockedY, pos.z));
                transform.position = pos;
                _lockedWorldPos = pos;
                _hasLockedWorldPos = true;
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
            _shipExtraHeight = settings.ShipExtraHeight;
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
            if (!IsUsableAnchor(anchor))
            {
                if (_cachedTargetHeight <= 0.0001f)
                    _cachedTargetHeight = _bodyRadius;
                return;
            }

            _cachedTargetHeight = _bodyRadius;
            if (_clearShipHull &&
                ShipWeaponProxyRegistry.TryGetCachedHullClearance(anchor, out float liftFromPivot, out _))
                _cachedTargetHeight = Mathf.Max(_cachedTargetHeight, liftFromPivot);
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

        float ResolveLiftY()
        {
            float height = Mathf.Max(_bodyRadius, _cachedTargetHeight);
            if (_clearShipHull &&
                IsUsableAnchor(followAnchor) &&
                ShipWeaponProxyRegistry.TryGetCachedHullClearance(followAnchor, out float liftFromPivot, out _))
                height = Mathf.Max(height, liftFromPivot);

            return height + Mathf.Max(0f, _extraHeight) +
                   (_clearShipHull ? Mathf.Max(0f, _shipExtraHeight) : 0f);
        }

        float ResolveShipOverlapGap() =>
            Mathf.Max(0f, _extraHeight) + Mathf.Max(0f, _shipExtraHeight);

        public void RelocateWorld(Vector3 worldPosition, float bodyRadius = 0f)
        {
            PullLiveSettings();
            followAnchor = null;
            if (bodyRadius >= 0f)
                _bodyRadius = Mathf.Max(0f, bodyRadius);
            CacheTargetHeight(null);
            worldMotionOffset = Vector3.zero;
            worldPosition += _worldOffset;
            lockedY = worldPosition.y;
            worldPosition.y = LiftAboveLocalShipIfOverlapping(worldPosition);
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
                pos.y = LiftAboveLocalShipIfOverlapping(pos);
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
            pos.y = LiftAboveLocalShipIfOverlapping(pos);
            transform.position = pos;
        }

        float LiftAboveLocalShipIfOverlapping(Vector3 worldPos)
        {
            var manager = WorldFloatingCountManager.Instance;
            if (manager == null ||
                !manager.TryGetLocalShipVisualClearance(out Vector3 shipPos, out float shipTopY, out float shipRadius))
                return worldPos.y;

            float dx = worldPos.x - shipPos.x;
            float dz = worldPos.z - shipPos.z;
            float reach = shipRadius + Mathf.Max(1.25f, _shipExtraHeight);
            if (dx * dx + dz * dz > reach * reach)
                return worldPos.y;

            return Mathf.Max(worldPos.y, shipTopY + ResolveShipOverlapGap() + _worldOffset.y);
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

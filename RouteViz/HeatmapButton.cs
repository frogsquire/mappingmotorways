using System;
using System.Reflection;
using Motorways;
using Motorways.Audio;
using Motorways.Views;
using Server;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace RouteViz
{
    /// <summary>
    /// On-screen heatmap toggle, right of the clear (broom) button. Same floating/transparent ring style;
    /// glyph is a flaming car. Dims while the heatmap is off.
    /// </summary>
    public partial class RouteVizPlugin
    {
        private const string HeatButtonName = "RouteViz_HeatButton";
        private const string HeatAnchorName = "RouteViz_HeatButton (Active)";
        private const string HeatInactiveAnchorName = "RouteViz_HeatButton (Inactive)";

        private GameObject _heatButton;
        private TouchButton _heatBackButton;
        private RectTransform _heatButtonRect, _heatCollider;
        private Canvas _heatCanvas;
        private GameObject _heatAnchor, _heatInactiveAnchor, _heatBackAnchor;
        private Transform _heatBackInactiveAnchor;
        private Image _heatSymbol, _heatRing;
        private CanvasGroup _heatGlyphGroup, _heatRingGroup;
        private float _heatScale = 1f;
        private bool _heatPressed;
        private Texture2D _heatIconTexture;
        private Sprite _heatIconSprite;
        private float _nextHeatButtonCheck;
        private bool _heatButtonFailed;

        private bool PointerOverHeatButton()
        {
            if (_heatButton == null || !_heatButton.activeInHierarchy)
                return false;
            RectTransform hit = _heatCollider != null ? _heatCollider : _heatButtonRect;
            Camera cam = _heatCanvas != null && _heatCanvas.renderMode != RenderMode.ScreenSpaceOverlay
                ? _heatCanvas.worldCamera
                : null;
            return hit != null && RectTransformUtility.RectangleContainsScreenPoint(hit, Input.mousePosition, cam);
        }

        private void UpdateHeatButton()
        {
            if (_heatButtonFailed)
                return;

            if (_heatButton != null)
            {
                SyncAnchorPair(_heatBackAnchor, _heatBackInactiveAnchor, _heatAnchor, _heatInactiveAnchor, 3f * RoutesButtonSpacing);
                if (_heatBackAnchor != null && _heatAnchor != null && _heatAnchor.activeSelf != _heatBackAnchor.activeSelf)
                    _heatAnchor.SetActive(_heatBackAnchor.activeSelf);
                if (_heatBackButton != null && _heatButton.activeSelf != _heatBackButton.gameObject.activeSelf)
                    _heatButton.SetActive(_heatBackButton.gameObject.activeSelf);
                AnimateHeatButton();
                return;
            }

            if (Time.unscaledTime < _nextHeatButtonCheck)
                return;
            _nextHeatButtonCheck = Time.unscaledTime + 1f;

            GameUIScreen screen = null;
            City city = Get.City;
            if (city != null && city.Scope != null)
                screen = city.Scope.Get<GameUIScreen>();
            if (screen == null)
                screen = Object.FindObjectOfType<GameUIScreen>();
            if (screen == null || screen.backButton == null)
                return;
            if (!BackButtonReady(screen.backButton))
                return;

            try
            {
                CreateHeatButton(screen.backButton);
            }
            catch (Exception e)
            {
                _heatButtonFailed = true;
                Logger.LogError($"Couldn't create the heatmap button: {e}");
                DestroyHeatButton(false);
            }
        }

        private void AnimateHeatButton()
        {
            bool over = PointerOverHeatButton();
            if (Input.GetMouseButtonDown(0))
                _heatPressed = over;
            else if (!Input.GetMouseButton(0))
                _heatPressed = false;

            float target = _heatPressed && over ? PressScale : over ? HoverScale : 1f;
            float k = 1f - (float)Math.Exp(-ScaleEaseRate * Time.unscaledDeltaTime);
            _heatScale += (target - _heatScale) * k;
            if (Math.Abs(target - _heatScale) < 0.001f)
                _heatScale = target;
            var scale = new Vector3(_heatScale, _heatScale, 1f);
            if (_heatSymbol != null && _heatSymbol.transform.localScale != scale)
                _heatSymbol.transform.localScale = scale;
            if (_heatRing != null && _heatRing.transform.localScale != scale)
                _heatRing.transform.localScale = scale;
            if (_heatSymbol != null && _heatRing != null && _heatRing.color != _heatSymbol.color)
                _heatRing.color = _heatSymbol.color;

            float alpha = _heatmapVisible ? 1f : HiddenGlyphAlpha;
            if (_heatGlyphGroup != null && _heatGlyphGroup.alpha != alpha)
                _heatGlyphGroup.alpha = alpha;
            if (_heatRingGroup != null && _heatRingGroup.alpha != alpha)
                _heatRingGroup.alpha = alpha;
        }

        private void CreateHeatButton(TouchButton back)
        {
            Transform parent = back.transform.parent;
            DestroyLeftover(parent, HeatButtonName);
            var offset = new Vector2(3f * RoutesButtonSpacing, 0f);

            var holder = new GameObject("RouteViz_HeatButtonHolder");
            holder.SetActive(false);
            GameObject go;
            try
            {
                go = Object.Instantiate(back.gameObject, holder.transform, false);
                foreach (Component c in go.GetComponents<Component>())
                {
                    if (c == null)
                        continue;
                    string typeName = c.GetType().Name;
                    if (typeName == "Animator")
                        ((Behaviour)c).enabled = false;
                    else if (typeName == "FloatingElement")
                        RetargetHeatFloatingElement(c, offset);
                }
                go.transform.SetParent(parent, false);
            }
            finally
            {
                Destroy(holder);
            }
            go.name = HeatButtonName;
            _heatButton = go;
            _heatBackButton = back;
            _heatCanvas = parent.GetComponentInParent<Canvas>();
            if (_heatCanvas != null)
                _heatCanvas = _heatCanvas.rootCanvas;

            var rect = (RectTransform)go.transform;
            rect.anchoredPosition = ((RectTransform)back.transform).anchoredPosition + offset;
            _heatButtonRect = rect;

            var button = go.GetComponent<TouchButton>();
            FieldInfo onClick = FindField(typeof(TouchButton), "_onClick");
            if (onClick == null)
                throw new MissingFieldException("TouchButton._onClick");
            onClick.SetValue(button, Activator.CreateInstance(onClick.FieldType));
            button.AddOnClickedEvent(new UnityAction(OnHeatButtonClicked));
            ApplyGameButtonSound(button, back);

            Sprite icon = GetHeatIcon();
            foreach (Transform child in go.transform)
            {
                switch (child.name)
                {
                    case "Symbol":
                        _heatSymbol = child.GetComponent<Image>();
                        _heatSymbol.sprite = icon;
                        _heatSymbol.raycastTarget = false;
                        Color c = _heatSymbol.color;
                        c.a = 1f;
                        _heatSymbol.color = c;
                        ((RectTransform)child).sizeDelta = new Vector2(IconDisplaySize, IconDisplaySize);
                        child.localScale = Vector3.one;
                        child.gameObject.SetActive(true);
                        _heatGlyphGroup = AddGroup(child);
                        break;
                    case "Collider":
                        _heatCollider = (RectTransform)child;
                        _heatCollider.sizeDelta = new Vector2(ColliderSize, ColliderSize);
                        child.gameObject.SetActive(true);
                        break;
                    default:
                        child.gameObject.SetActive(false);
                        break;
                }
            }

            if (_heatSymbol != null)
            {
                var ringGo = new GameObject("RouteViz_Ring", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
                ringGo.transform.SetParent(go.transform, false);
                ringGo.transform.SetSiblingIndex(_heatSymbol.transform.GetSiblingIndex());
                var ringRect = (RectTransform)ringGo.transform;
                ringRect.anchorMin = ringRect.anchorMax = new Vector2(0.5f, 0.5f);
                ringRect.pivot = new Vector2(0.5f, 0.5f);
                ringRect.anchoredPosition = ((RectTransform)_heatSymbol.transform).anchoredPosition;
                ringRect.sizeDelta = new Vector2(RingDiameter, RingDiameter);
                _heatRing = ringGo.GetComponent<Image>();
                _heatRing.sprite = GetRingSprite();
                _heatRing.color = _heatSymbol.color;
                _heatRing.raycastTarget = false;
                _heatRingGroup = AddGroup(ringGo.transform);
            }
            _heatScale = 1f;
            _heatPressed = false;

            if (_heatAnchor != null)
                _heatAnchor.SetActive(false);

            if (_verboseLogging.Value)
                Logger.LogInfo($"Heatmap button created at {rect.anchoredPosition}.");
        }

        private void RetargetHeatFloatingElement(Component floating, Vector2 offset)
        {
            FieldInfo baseField = FindField(floating.GetType(), "baseElement");
            FieldInfo inactiveField = FindField(floating.GetType(), "_inactiveAnchor");
            var backBase = baseField?.GetValue(floating) as GameObject;
            var backInactive = inactiveField?.GetValue(floating) as Transform;
            if (backBase == null || backInactive == null)
            {
                Logger.LogWarning("Back button's FloatingElement anchors not found; placing the heatmap button directly.");
                Object.DestroyImmediate(floating);
                return;
            }
            _heatBackAnchor = backBase;
            _heatBackInactiveAnchor = backInactive;
            _heatAnchor = CloneAnchor(backBase, HeatAnchorName, offset);
            _heatInactiveAnchor = CloneAnchor(backInactive.gameObject, HeatInactiveAnchorName, offset);
            baseField.SetValue(floating, _heatAnchor);
            inactiveField.SetValue(floating, _heatInactiveAnchor.transform);
        }

        private void OnHeatButtonClicked()
        {
            try
            {
                ToggleHeatmap(playSound: false);
            }
            catch (Exception e)
            {
                Logger.LogError($"Heatmap button click failed: {e}");
            }
        }

        private void AbandonHeatButton()
        {
            if (_heatSymbol != null)
                _heatSymbol.sprite = null;
            if (_heatRing != null)
                _heatRing.sprite = null;
            _heatButton = null;
            _heatAnchor = null;
            _heatInactiveAnchor = null;
            _heatBackAnchor = null;
            _heatBackInactiveAnchor = null;
            _heatButtonRect = null;
            _heatCollider = null;
            _heatCanvas = null;
            _heatSymbol = null;
            _heatRing = null;
            _heatGlyphGroup = null;
            _heatRingGroup = null;
            _heatIconSprite = null;
            _heatIconTexture = null;
        }

        private void DestroyHeatButton(bool includeSprites = true)
        {
            if (_heatSymbol != null)
                _heatSymbol.sprite = null;
            if (_heatRing != null)
                _heatRing.sprite = null;
            if (_heatButton != null)
                Destroy(_heatButton);
            if (_heatAnchor != null)
                Destroy(_heatAnchor);
            if (_heatInactiveAnchor != null)
                Destroy(_heatInactiveAnchor);
            _heatButton = null;
            _heatAnchor = null;
            _heatInactiveAnchor = null;
            _heatBackAnchor = null;
            _heatBackInactiveAnchor = null;
            _heatButtonRect = null;
            _heatCollider = null;
            _heatCanvas = null;
            _heatSymbol = null;
            _heatRing = null;
            _heatGlyphGroup = null;
            _heatRingGroup = null;
            if (!includeSprites)
                return;
            if (_heatIconSprite != null)
                Destroy(_heatIconSprite);
            if (_heatIconTexture != null)
                Destroy(_heatIconTexture);
            _heatIconSprite = null;
            _heatIconTexture = null;
        }

        private Sprite GetHeatIcon()
        {
            if (_heatIconSprite == null)
                _heatIconSprite = MakeSprite(IconTextureSize, IconDesignSize, EdgeSoftness * IconDesignSize / IconDisplaySize,
                    FlameCarIconSdf, "RouteViz_HeatIcon", out _heatIconTexture);
            return _heatIconSprite;
        }

        /// <summary>
        /// Rear-view car with three flame tongues rising from the roof, clipped to the ring circle.
        /// Design space 128x128, y down (same as the other button glyphs).
        /// </summary>
        private static float FlameCarIconSdf(float x, float y)
        {
            float car = CarSdf(x, y);
            float cut = Math.Min(RoundRect(x, y, 64f, 77f, 14f, 5f, 3f),
                Math.Min(RoundRect(x, y, 47f, 96f, 6f, 3.5f, 2f), RoundRect(x, y, 81f, 96f, 6f, 3.5f, 2f)));
            float carShape = Math.Max(car, -cut);

            // Flames: elongated teardrops (ellipse + tip) rising above the cabin. y decreases upward in design space.
            float f1 = Flame(x, y, 64f, 52f, 9f, 22f);
            float f2 = Flame(x, y, 50f, 56f, 7f, 16f);
            float f3 = Flame(x, y, 78f, 56f, 7f, 16f);
            float flames = Math.Min(f1, Math.Min(f2, f3));

            float dx = x - 64f, dy = y - 64f;
            float circle = (float)Math.Sqrt(dx * dx + dy * dy) - 62f;
            return Math.Max(Math.Min(carShape, flames), circle);
        }

        /// <summary>Flame tongue centred at (cx, cy), half-width hw, height h upward (toward smaller y).</summary>
        private static float Flame(float x, float y, float cx, float cy, float hw, float h)
        {
            // Body: rounded oval; tip: taper to a point above.
            float body = Ellipse(x, y, cx, cy + h * 0.15f, hw, h * 0.55f);
            float tip = Taper(x, y, cx - hw * 0.35f, cy + h * 0.2f, hw * 0.45f, cx, cy - h * 0.45f, 1.2f);
            float tip2 = Taper(x, y, cx + hw * 0.35f, cy + h * 0.2f, hw * 0.45f, cx, cy - h * 0.45f, 1.2f);
            return Math.Min(body, Math.Min(tip, tip2));
        }

        private static float Ellipse(float x, float y, float cx, float cy, float rx, float ry)
        {
            float nx = (x - cx) / Math.Max(1e-4f, rx);
            float ny = (y - cy) / Math.Max(1e-4f, ry);
            return (float)Math.Sqrt(nx * nx + ny * ny) - 1f;
        }
    }
}

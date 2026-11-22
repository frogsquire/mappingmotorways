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
    /// A second on-screen button, right of the routes button, that clears every route (like the C key).
    /// Built the same way as the routes button: a clone of the back button with a dark ring and a generated glyph
    /// (a broom sweeping routes away).
    /// </summary>
    public partial class RouteVizPlugin
    {
        private const string ClearButtonName = "RouteViz_ClearButton";
        private const string ClearAnchorName = "RouteViz_ClearButton (Active)";
        private const string ClearInactiveAnchorName = "RouteViz_ClearButton (Inactive)";

        private GameObject _clearButton;
        private TouchButton _clearBackButton;
        private RectTransform _clearButtonRect, _clearCollider;
        private Canvas _clearCanvas;
        private GameObject _clearAnchor, _clearInactiveAnchor, _clearBackAnchor;
        private Transform _clearBackInactiveAnchor;
        private Image _clearSymbol, _clearRing;
        private float _clearScale = 1f;
        private bool _clearPressed;
        private Texture2D _clearIconTexture;
        private Sprite _clearIconSprite;
        private float _nextClearButtonCheck;
        private bool _clearButtonFailed;

        /// <summary>The C key and the clear button: drop every route. The button also buzzes when there was nothing.</summary>
        private void ClearAllRoutes(bool fromButton, bool playSound = true)
        {
            int count = ClearSelections();
            if (playSound && count > 0)
                PlayClick(Sounds.Kind.Dong);
            else if (playSound && fromButton)
                PlayClick(Sounds.Kind.Error);
            _routesVisible = true; // nothing left to hide; make the next click show its route
            Logger.LogInfo(count > 0
                ? $"All routes cleared ({count}) via {(fromButton ? "the clear button" : "the " + _toggleKey + " key")}."
                : $"Clear requested via {(fromButton ? "the clear button" : "the " + _toggleKey + " key")}: no routes to clear.");
        }

        private bool PointerOverClearButton()
        {
            if (_clearButton == null || !_clearButton.activeInHierarchy)
                return false;
            RectTransform hit = _clearCollider != null ? _clearCollider : _clearButtonRect;
            Camera cam = _clearCanvas != null && _clearCanvas.renderMode != RenderMode.ScreenSpaceOverlay
                ? _clearCanvas.worldCamera
                : null;
            return hit != null && RectTransformUtility.RectangleContainsScreenPoint(hit, Input.mousePosition, cam);
        }

        /// <summary>Called from Update: (re)creates the button whenever the game UI exists without it.</summary>
        private void UpdateClearButton()
        {
            if (_clearButtonFailed)
                return;

            if (_clearButton != null)
            {
                SyncAnchorPair(_clearBackAnchor, _clearBackInactiveAnchor, _clearAnchor, _clearInactiveAnchor, 2f * RoutesButtonSpacing);
                if (_clearBackAnchor != null && _clearAnchor != null && _clearAnchor.activeSelf != _clearBackAnchor.activeSelf)
                    _clearAnchor.SetActive(_clearBackAnchor.activeSelf);
                if (_clearBackButton != null && _clearButton.activeSelf != _clearBackButton.gameObject.activeSelf)
                    _clearButton.SetActive(_clearBackButton.gameObject.activeSelf);
                AnimateClearButton();
                return;
            }

            if (Time.unscaledTime < _nextClearButtonCheck)
                return;
            _nextClearButtonCheck = Time.unscaledTime + 1f;

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
                CreateClearButton(screen.backButton);
            }
            catch (Exception e)
            {
                _clearButtonFailed = true;
                Logger.LogError($"Couldn't create the clear button: {e}");
                DestroyClearButton(false);
            }
        }

        private void AnimateClearButton()
        {
            bool over = PointerOverClearButton();
            if (Input.GetMouseButtonDown(0))
                _clearPressed = over;
            else if (!Input.GetMouseButton(0))
                _clearPressed = false;

            float target = _clearPressed && over ? PressScale : over ? HoverScale : 1f;
            float k = 1f - (float)Math.Exp(-ScaleEaseRate * Time.unscaledDeltaTime);
            _clearScale += (target - _clearScale) * k;
            if (Math.Abs(target - _clearScale) < 0.001f)
                _clearScale = target;
            var scale = new Vector3(_clearScale, _clearScale, 1f);
            if (_clearSymbol != null && _clearSymbol.transform.localScale != scale)
                _clearSymbol.transform.localScale = scale;
            if (_clearRing != null && _clearRing.transform.localScale != scale)
                _clearRing.transform.localScale = scale;
            if (_clearSymbol != null && _clearRing != null && _clearRing.color != _clearSymbol.color)
                _clearRing.color = _clearSymbol.color;
        }

        private void CreateClearButton(TouchButton back)
        {
            Transform parent = back.transform.parent;
            DestroyLeftover(parent, ClearButtonName);
            var offset = new Vector2(2f * RoutesButtonSpacing, 0f);

            var holder = new GameObject("RouteViz_ClearButtonHolder");
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
                        RetargetClearFloatingElement(c, offset);
                }
                go.transform.SetParent(parent, false);
            }
            finally
            {
                Destroy(holder);
            }
            go.name = ClearButtonName;
            _clearButton = go;
            _clearBackButton = back;
            _clearCanvas = parent.GetComponentInParent<Canvas>();
            if (_clearCanvas != null)
                _clearCanvas = _clearCanvas.rootCanvas;

            var rect = (RectTransform)go.transform;
            rect.anchoredPosition = ((RectTransform)back.transform).anchoredPosition + offset;
            _clearButtonRect = rect;

            var button = go.GetComponent<TouchButton>();
            FieldInfo onClick = FindField(typeof(TouchButton), "_onClick");
            if (onClick == null)
                throw new MissingFieldException("TouchButton._onClick");
            onClick.SetValue(button, Activator.CreateInstance(onClick.FieldType));
            button.AddOnClickedEvent(new UnityAction(OnClearButtonClicked));
            ApplyGameButtonSound(button, back); // the game's ordinary button sound instead of the back sound

            Sprite icon = GetClearIcon();
            foreach (Transform child in go.transform)
            {
                switch (child.name)
                {
                    case "Symbol":
                        _clearSymbol = child.GetComponent<Image>();
                        _clearSymbol.sprite = icon;
                        _clearSymbol.raycastTarget = false;
                        Color c = _clearSymbol.color;
                        c.a = 1f;
                        _clearSymbol.color = c;
                        ((RectTransform)child).sizeDelta = new Vector2(IconDisplaySize, IconDisplaySize);
                        child.localScale = Vector3.one;
                        child.gameObject.SetActive(true);
                        break;
                    case "Collider":
                        _clearCollider = (RectTransform)child;
                        _clearCollider.sizeDelta = new Vector2(ColliderSize, ColliderSize);
                        child.gameObject.SetActive(true);
                        break;
                    default:
                        // Transparent outlined icon: hide the back button's filled cream disc and chrome.
                        child.gameObject.SetActive(false);
                        break;
                }
            }

            if (_clearSymbol != null)
            {
                var ringGo = new GameObject("RouteViz_Ring", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
                ringGo.transform.SetParent(go.transform, false);
                ringGo.transform.SetSiblingIndex(_clearSymbol.transform.GetSiblingIndex());
                var ringRect = (RectTransform)ringGo.transform;
                ringRect.anchorMin = ringRect.anchorMax = new Vector2(0.5f, 0.5f);
                ringRect.pivot = new Vector2(0.5f, 0.5f);
                ringRect.anchoredPosition = ((RectTransform)_clearSymbol.transform).anchoredPosition;
                ringRect.sizeDelta = new Vector2(RingDiameter, RingDiameter);
                _clearRing = ringGo.GetComponent<Image>();
                _clearRing.sprite = GetRingSprite(); // shared with the routes button
                _clearRing.color = _clearSymbol.color;
                _clearRing.raycastTarget = false;
            }
            _clearScale = 1f;
            _clearPressed = false;

            // Drop in with FloatingElement like the routes button / back button.
            if (_clearAnchor != null)
                _clearAnchor.SetActive(false);

            if (_verboseLogging.Value)
                Logger.LogInfo($"Clear button created at {rect.anchoredPosition}.");
        }

        /// <summary>Same as the routes button's: own float in/out anchors, offset from the back button's.</summary>
        private void RetargetClearFloatingElement(Component floating, Vector2 offset)
        {
            FieldInfo baseField = FindField(floating.GetType(), "baseElement");
            FieldInfo inactiveField = FindField(floating.GetType(), "_inactiveAnchor");
            var backBase = baseField?.GetValue(floating) as GameObject;
            var backInactive = inactiveField?.GetValue(floating) as Transform;
            if (backBase == null || backInactive == null)
            {
                Logger.LogWarning("Back button's FloatingElement anchors not found; placing the clear button directly.");
                Object.DestroyImmediate(floating);
                return;
            }
            _clearBackAnchor = backBase;
            _clearBackInactiveAnchor = backInactive;
            _clearAnchor = CloneAnchor(backBase, ClearAnchorName, offset);
            _clearInactiveAnchor = CloneAnchor(backInactive.gameObject, ClearInactiveAnchorName, offset);
            baseField.SetValue(floating, _clearAnchor);
            inactiveField.SetValue(floating, _clearInactiveAnchor.transform);
        }

        private void OnClearButtonClicked()
        {
            try
            {
                ClearAllRoutes(true, playSound: false); // the button plays the game's click sound itself
            }
            catch (Exception e)
            {
                Logger.LogError($"Clear button click failed: {e}");
            }
        }

        /// <summary>Drop references without Destroy — see AbandonRoutesButton.</summary>
        private void AbandonClearButton()
        {
            if (_clearSymbol != null)
                _clearSymbol.sprite = null;
            if (_clearRing != null)
                _clearRing.sprite = null;
            _clearButton = null;
            _clearAnchor = null;
            _clearInactiveAnchor = null;
            _clearBackAnchor = null;
            _clearBackInactiveAnchor = null;
            _clearButtonRect = null;
            _clearCollider = null;
            _clearCanvas = null;
            _clearSymbol = null;
            _clearRing = null;
            _clearIconSprite = null;
            _clearIconTexture = null;
        }

        /// <summary>Create failure path: destroy the half-built button GOs (game is running; safe to Destroy).</summary>
        private void DestroyClearButton(bool includeSprites = true)
        {
            if (_clearSymbol != null)
                _clearSymbol.sprite = null;
            if (_clearRing != null)
                _clearRing.sprite = null;
            if (_clearButton != null)
                Destroy(_clearButton);
            if (_clearAnchor != null)
                Destroy(_clearAnchor);
            if (_clearInactiveAnchor != null)
                Destroy(_clearInactiveAnchor);
            _clearButton = null;
            _clearAnchor = null;
            _clearInactiveAnchor = null;
            _clearBackAnchor = null;
            _clearBackInactiveAnchor = null;
            _clearButtonRect = null;
            _clearCollider = null;
            _clearCanvas = null;
            _clearSymbol = null;
            _clearRing = null;
            if (!includeSprites)
                return;
            if (_clearIconSprite != null)
                Destroy(_clearIconSprite);
            if (_clearIconTexture != null)
                Destroy(_clearIconTexture);
            _clearIconSprite = null;
            _clearIconTexture = null;
        }

        // ---------------------------------------------------------------- broom icon

        private Sprite GetClearIcon()
        {
            if (_clearIconSprite == null)
                _clearIconSprite = MakeSprite(IconTextureSize, IconDesignSize, EdgeSoftness * IconDesignSize / IconDisplaySize,
                    BroomIconSdf, "RouteViz_ClearIcon", out _clearIconTexture);
            return _clearIconSprite;
        }

        // Broom axis: from the top of the handle (upper right) toward the bristles (lower left), 40 degrees from vertical.
        private const float BroomAx = 104f, BroomAy = 18f;
        private const float BroomUx = -0.6428f, BroomUy = 0.7660f;

        /// <summary>
        /// A broom sweeping route dashes off to the lower left, in a 128x128 design space with y down.
        /// Everything is built in broom coordinates (s along the handle, t across it); the rotation keeps distances.
        /// </summary>
        private static float BroomIconSdf(float x, float y)
        {
            float px = x - BroomAx, py = y - BroomAy;
            float s = px * BroomUx + py * BroomUy;
            float t = px * -BroomUy + py * BroomUx; // positive toward the upper left

            float handle = Taper(s, t, 7f, 0f, 6.5f, 46f, 0f, 6.5f);
            // Binding band where the bristles meet the handle.
            float band = RoundRect(s, t, 51f, 0f, 4.5f, 12.5f, 2.5f);

            // Fan-shaped bristle head, widening from 13 to 27 across between s = 59 and 94, with two slits.
            const float s0 = 59f, s1 = 94f, w0 = 13f, w1 = 27f;
            float slope = (w1 - w0) / (s1 - s0);
            float side = (Math.Abs(t) - (w0 + (s - s0) * slope)) / (float)Math.Sqrt(1f + slope * slope);
            float head = Math.Max(Math.Max(s0 - s, s - s1), side);
            float slits = Math.Min(Taper(s, t, 70f, -6f, 2.2f, 100f, -12f, 3.2f), Taper(s, t, 70f, 6f, 2.2f, 100f, 12f, 3.2f));
            head = Math.Max(head, -slits);

            float broom = Math.Min(Math.Min(handle, band), head);

            // The swept-away route: two staggered dashes ahead of the bristles, the outer one further left.
            // Everything stays within ~62 of the centre so it clears the ring.
            float dash1 = Taper(s, t, 104f, -12f, 5f, 104f, 3f, 5f);
            float dash2 = Taper(s, t, 118f, 5f, 4.5f, 118f, 14f, 4.5f);
            float debris = Math.Min(dash1, dash2);
            return Math.Min(broom, debris);
        }

        private static float Circle(float x, float y, float cx, float cy, float r)
        {
            float dx = x - cx, dy = y - cy;
            return (float)Math.Sqrt(dx * dx + dy * dy) - r;
        }

    }
}

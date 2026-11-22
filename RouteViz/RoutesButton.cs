using System;
using System.Reflection;
using Motorways;
using Motorways.Audio;
using Motorways.Views;
using Server;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace RouteViz
{
    /// <summary>
    /// An in-game button next to the top-left back arrow that shows/hides all routes (like Alt+C).
    /// It's a clone of the back button (so it floats in/out with it), drawn as a dark outlined round icon,
    /// with its own hover/press feedback instead of the back button's animator.
    /// </summary>
    public partial class RouteVizPlugin
    {
        private const string RoutesButtonName = "RouteViz_RoutesButton";
        private const string RoutesAnchorName = "RouteViz_RoutesButton (Active)";
        private const string RoutesInactiveAnchorName = "RouteViz_RoutesButton (Inactive)";
        /// <summary>Distance from the back button's centre to ours, in the parent's units (button is 100 wide, scale 1.1).</summary>
        private const float RoutesButtonSpacing = 160f;
        /// <summary>The icon is designed in a 128x128 space (y down) and rendered at 256 px, with mipmaps.</summary>
        private const float IconDesignSize = 128f;
        private const int IconTextureSize = 256;
        /// <summary>Width of the anti-aliased edge ramp in button units (~1.4 screen px at 1080p-ish scale, like the game's sprites).</summary>
        private const float EdgeSoftness = 1.2f;
        /// <summary>Glyph box size inside the ring.</summary>
        private const float IconDisplaySize = 64f;
        /// <summary>Ring outer diameter and stroke, in the button's units.</summary>
        private const float RingDiameter = 86f;
        private const float RingStroke = 7f;
        private const int RingTextureSize = 256;
        /// <summary>Hit area (the back button's is 163x160, which would overlap the back button at this spacing).</summary>
        private const float ColliderSize = 110f;
        private const float HiddenGlyphAlpha = 0.35f;
        private const float HoverScale = 1.08f;
        private const float PressScale = 0.94f;
        /// <summary>Exponential easing rate: ~90% of the way in 0.1 s.</summary>
        private const float ScaleEaseRate = 23f;

        private GameObject _routesButton;
        private TouchButton _routesBackButton;
        private RectTransform _routesButtonRect;
        private RectTransform _routesCollider;
        private Canvas _routesCanvas;
        private GameObject _routesAnchor, _routesInactiveAnchor, _backAnchor;
        private Transform _backInactiveAnchor;
        private Image _routesSymbol, _routesRing;
        /// <summary>Dim the glyph and ring while routes are hidden.</summary>
        private CanvasGroup _routesGlyphGroup, _routesRingGroup;
        private float _routesScale = 1f;
        private bool _routesPressed;
        private Texture2D _routesIconTexture, _routesRingTexture;
        private Sprite _routesIconSprite, _routesRingSprite;
        private float _nextRoutesButtonCheck;
        private bool _routesButtonFailed;

        /// <summary>Alt+C and the on-screen button: hide or show every route without dropping the selections.</summary>
        private void ToggleRoutesVisible(bool playSound = true)
        {
            _routesVisible = !_routesVisible;
            if (playSound)
                PlayClick(_routesVisible ? Sounds.Kind.Ding : Sounds.Kind.Error);
            Logger.LogInfo(_routesVisible
                ? $"Routes shown ({_selections.Count})."
                : $"Routes hidden ({_selections.Count} kept; press Alt+{_toggleKey} to show).");
        }

        /// <summary>True while the mouse is over game UI (or our button), so map clicks and hover should be skipped.</summary>
        private bool PointerOverUI()
        {
            try
            {
                EventSystem es = EventSystem.current;
                if (es != null && es.IsPointerOverGameObject())
                    return true;
            }
            catch (Exception)
            {
                // fall through to the rect check
            }
            return PointerOverRoutesButton();
        }

        private bool PointerOverRoutesButton()
        {
            if (_routesButton == null || !_routesButton.activeInHierarchy)
                return false;
            RectTransform hit = _routesCollider != null ? _routesCollider : _routesButtonRect;
            Camera cam = _routesCanvas != null && _routesCanvas.renderMode != RenderMode.ScreenSpaceOverlay
                ? _routesCanvas.worldCamera
                : null;
            return hit != null && RectTransformUtility.RectangleContainsScreenPoint(hit, Input.mousePosition, cam);
        }

        /// <summary>Called from Update: (re)creates the button whenever the game UI exists without it.</summary>
        private void UpdateRoutesButton()
        {
            if (_routesButtonFailed)
                return;

            if (_routesButton != null)
            {
                // Positions first, then activeSelf — so the drop-in starts from the right place.
                SyncAnchorPair(_backAnchor, _backInactiveAnchor, _routesAnchor, _routesInactiveAnchor, RoutesButtonSpacing);
                // Shown/hidden together with the back button: the game hides it by switching off its anchor
                // (FloatingElement then floats it in/out) or the button itself.
                if (_backAnchor != null && _routesAnchor != null && _routesAnchor.activeSelf != _backAnchor.activeSelf)
                    _routesAnchor.SetActive(_backAnchor.activeSelf);
                if (_routesBackButton != null && _routesButton.activeSelf != _routesBackButton.gameObject.activeSelf)
                    _routesButton.SetActive(_routesBackButton.gameObject.activeSelf);
                AnimateRoutesButton();
                return;
            }

            if (Time.unscaledTime < _nextRoutesButtonCheck)
                return;
            _nextRoutesButtonCheck = Time.unscaledTime + 1f;

            GameUIScreen screen = null;
            City city = Get.City;
            if (city != null && city.Scope != null)
                screen = city.Scope.Get<GameUIScreen>();
            if (screen == null)
                screen = Object.FindObjectOfType<GameUIScreen>();
            if (screen == null || screen.backButton == null)
                return;
            // Need the back button's FloatingElement anchors; SyncAnchorPair tracks later layout moves.
            if (!BackButtonReady(screen.backButton))
                return;

            try
            {
                CreateRoutesButton(screen.backButton);
            }
            catch (Exception e)
            {
                _routesButtonFailed = true;
                Logger.LogError($"Couldn't create the routes button: {e}");
                DestroyRoutesButton(false);
            }
        }

        /// <summary>Hover grows the icon and ring a little, pressing shrinks them; hidden routes dim them.</summary>
        private void AnimateRoutesButton()
        {
            bool over = PointerOverRoutesButton();
            if (Input.GetMouseButtonDown(0))
                _routesPressed = over;
            else if (!Input.GetMouseButton(0))
                _routesPressed = false;

            float target = _routesPressed && over ? PressScale : over ? HoverScale : 1f;
            float k = 1f - (float)Math.Exp(-ScaleEaseRate * Time.unscaledDeltaTime);
            _routesScale += (target - _routesScale) * k;
            if (Math.Abs(target - _routesScale) < 0.001f)
                _routesScale = target;
            var scale = new Vector3(_routesScale, _routesScale, 1f);
            if (_routesSymbol != null && _routesSymbol.transform.localScale != scale)
                _routesSymbol.transform.localScale = scale;
            if (_routesRing != null && _routesRing.transform.localScale != scale)
                _routesRing.transform.localScale = scale;

            // The ring follows the glyph's (themed) colour.
            if (_routesSymbol != null && _routesRing != null && _routesRing.color != _routesSymbol.color)
                _routesRing.color = _routesSymbol.color;

            float alpha = _routesVisible ? 1f : HiddenGlyphAlpha;
            if (_routesGlyphGroup != null && _routesGlyphGroup.alpha != alpha)
                _routesGlyphGroup.alpha = alpha;
            if (_routesRingGroup != null && _routesRingGroup.alpha != alpha)
                _routesRingGroup.alpha = alpha;
        }

        private void CreateRoutesButton(TouchButton back)
        {
            Transform parent = back.transform.parent;
            DestroyLeftover(parent, RoutesButtonName);
            var offset = new Vector2(RoutesButtonSpacing, 0f);

            // Clone under an inactive holder so none of the clone's components wake up before it's adjusted.
            var holder = new GameObject("RouteViz_ButtonHolder");
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
                    // The back button's Animator produces the gold hover circle; we do our own feedback instead.
                    // (It doesn't move the button: FloatingElement does the float in/out.)
                    if (typeName == "Animator")
                        ((Behaviour)c).enabled = false;
                    else if (typeName == "FloatingElement")
                        RetargetFloatingElement(c, offset);
                }
                go.transform.SetParent(parent, false);
            }
            finally
            {
                Destroy(holder);
            }
            go.name = RoutesButtonName;
            _routesButton = go;
            _routesBackButton = back;
            _routesCanvas = parent.GetComponentInParent<Canvas>();
            if (_routesCanvas != null)
                _routesCanvas = _routesCanvas.rootCanvas;

            var rect = (RectTransform)go.transform;
            rect.anchoredPosition = ((RectTransform)back.transform).anchoredPosition + offset;
            _routesButtonRect = rect;

            var button = go.GetComponent<TouchButton>();
            // The clone copied the back button's serialized click listeners (pause menu); start from an empty event.
            FieldInfo onClick = FindField(typeof(TouchButton), "_onClick");
            if (onClick == null)
                throw new MissingFieldException("TouchButton._onClick");
            onClick.SetValue(button, Activator.CreateInstance(onClick.FieldType));
            button.AddOnClickedEvent(new UnityAction(OnRoutesButtonClicked));
            // The game's ordinary button sound instead of the back sound.
            ApplyGameButtonSound(button, back);

            // With the Animator off, the children keep whatever it last wrote; set the resting look explicitly.
            Sprite icon = GetRoutesIcon();
            foreach (Transform child in go.transform)
            {
                switch (child.name)
                {
                    case "Symbol":
                        _routesSymbol = child.GetComponent<Image>();
                        _routesSymbol.sprite = icon;
                        _routesSymbol.raycastTarget = false;
                        Color c = _routesSymbol.color;
                        c.a = 1f;
                        _routesSymbol.color = c;
                        ((RectTransform)child).sizeDelta = new Vector2(IconDisplaySize, IconDisplaySize);
                        child.localScale = Vector3.one;
                        child.gameObject.SetActive(true);
                        _routesGlyphGroup = AddGroup(child);
                        break;
                    case "Collider":
                        _routesCollider = (RectTransform)child;
                        _routesCollider.sizeDelta = new Vector2(ColliderSize, ColliderSize);
                        child.gameObject.SetActive(true);
                        break;
                    default:
                        // Gold hover circle, cream BG disc, white highlight glyph, arrow shadow — we want a
                        // transparent outlined icon, not the back button's filled chrome.
                        child.gameObject.SetActive(false);
                        break;
                }
            }

            if (_routesSymbol != null)
            {
                var ringGo = new GameObject("RouteViz_Ring", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
                ringGo.transform.SetParent(go.transform, false);
                ringGo.transform.SetSiblingIndex(_routesSymbol.transform.GetSiblingIndex());
                var ringRect = (RectTransform)ringGo.transform;
                ringRect.anchorMin = ringRect.anchorMax = new Vector2(0.5f, 0.5f);
                ringRect.pivot = new Vector2(0.5f, 0.5f);
                ringRect.anchoredPosition = ((RectTransform)_routesSymbol.transform).anchoredPosition;
                ringRect.sizeDelta = new Vector2(RingDiameter, RingDiameter);
                _routesRing = ringGo.GetComponent<Image>();
                _routesRing.sprite = GetRingSprite();
                _routesRing.color = _routesSymbol.color;
                _routesRing.raycastTarget = false;
                _routesRingGroup = AddGroup(ringGo.transform);
            }
            _routesScale = 1f;
            _routesPressed = false;

            // Start with the active anchor off so FloatingElement plays its drop-in when Update syncs it
            // to the back button's anchor (same appear animation the game's own buttons use).
            if (_routesAnchor != null)
                _routesAnchor.SetActive(false);

            if (_verboseLogging.Value)
                Logger.LogInfo($"Routes button created at {rect.anchoredPosition} next to the back button.");
        }

        /// <summary>
        /// FloatingElement places the button at its anchor (and floats it to the inactive anchor when the game hides it).
        /// Give ours its own anchors, offset from the back button's.
        /// </summary>
        private void RetargetFloatingElement(Component floating, Vector2 offset)
        {
            FieldInfo baseField = FindField(floating.GetType(), "baseElement");
            FieldInfo inactiveField = FindField(floating.GetType(), "_inactiveAnchor");
            var backBase = baseField?.GetValue(floating) as GameObject;
            var backInactive = inactiveField?.GetValue(floating) as Transform;
            if (backBase == null || backInactive == null)
            {
                Logger.LogWarning("Back button's FloatingElement anchors not found; placing the routes button directly.");
                Object.DestroyImmediate(floating);
                return;
            }
            _backAnchor = backBase;
            _backInactiveAnchor = backInactive;
            _routesAnchor = CloneAnchor(backBase, RoutesAnchorName, offset);
            _routesInactiveAnchor = CloneAnchor(backInactive.gameObject, RoutesInactiveAnchorName, offset);
            baseField.SetValue(floating, _routesAnchor);
            inactiveField.SetValue(floating, _routesInactiveAnchor.transform);
        }

        /// <summary>True once the back button has a FloatingElement with anchors we can clone and track.</summary>
        private static bool BackButtonReady(TouchButton back)
        {
            if (back == null || !back.isActiveAndEnabled)
                return false;
            Component fe = null;
            foreach (Component c in back.GetComponents<Component>())
            {
                if (c != null && c.GetType().Name == "FloatingElement")
                {
                    fe = c;
                    break;
                }
            }
            if (fe == null)
                return true;
            var baseEl = FindField(fe.GetType(), "baseElement")?.GetValue(fe) as GameObject;
            var inactive = FindField(fe.GetType(), "_inactiveAnchor")?.GetValue(fe) as Transform;
            return baseEl != null && inactive != null;
        }

        /// <summary>Keep our float anchors locked to the back button's, so late layout moves don't leave us stranded.</summary>
        private static void SyncAnchorPair(GameObject backActive, Transform backInactive, GameObject ourActive, GameObject ourInactive, float spacing)
        {
            var offset = new Vector2(spacing, 0f);
            if (backActive != null && ourActive != null)
            {
                var want = ((RectTransform)backActive.transform).anchoredPosition + offset;
                var r = (RectTransform)ourActive.transform;
                if (r.anchoredPosition != want)
                    r.anchoredPosition = want;
            }
            if (backInactive != null && ourInactive != null)
            {
                var want = ((RectTransform)backInactive).anchoredPosition + offset;
                var r = (RectTransform)ourInactive.transform;
                if (r.anchoredPosition != want)
                    r.anchoredPosition = want;
            }
        }

        private static CanvasGroup AddGroup(Transform t)
        {
            var group = t.gameObject.AddComponent<CanvasGroup>();
            group.interactable = false;
            group.blocksRaycasts = false;
            return group;
        }

        private static GameObject CloneAnchor(GameObject source, string name, Vector2 offset)
        {
            DestroyLeftover(source.transform.parent, name);
            GameObject anchor = Object.Instantiate(source, source.transform.parent, false);
            anchor.name = name;
            var r = (RectTransform)anchor.transform;
            r.anchoredPosition = ((RectTransform)source.transform).anchoredPosition + offset;
            return anchor;
        }

        /// <summary>Removes an object left over from an earlier load (hot reload).</summary>
        private static void DestroyLeftover(Transform parent, string name)
        {
            if (parent == null)
                return;
            Transform existing = parent.Find(name);
            if (existing != null)
                Destroy(existing.gameObject);
        }

        private void OnRoutesButtonClicked()
        {
            try
            {
                ToggleRoutesVisible(playSound: false); // the button plays the game's click sound itself
            }
            catch (Exception e)
            {
                Logger.LogError($"Routes button click failed: {e}");
            }
        }

        /// <summary>
        /// Drop references without Destroy. Safe during quit (avoids UI Camera targetTexture errors) and
        /// during hot reload (next CreateRoutesButton DestroyLeftover removes any leftover GOs by name).
        /// </summary>
        private void AbandonRoutesButton()
        {
            if (_routesSymbol != null)
                _routesSymbol.sprite = null;
            if (_routesRing != null)
                _routesRing.sprite = null;
            _routesButton = null;
            _routesAnchor = null;
            _routesInactiveAnchor = null;
            _backAnchor = null;
            _backInactiveAnchor = null;
            _routesButtonRect = null;
            _routesCollider = null;
            _routesCanvas = null;
            _routesSymbol = null;
            _routesRing = null;
            _routesGlyphGroup = null;
            _routesRingGroup = null;
            _routesIconSprite = null;
            _routesIconTexture = null;
            _routesRingSprite = null;
            _routesRingTexture = null;
        }

        /// <summary>Create failure path: destroy the half-built button GOs (game is running; safe to Destroy).</summary>
        private void DestroyRoutesButton(bool includeSprites = true)
        {
            if (_routesSymbol != null)
                _routesSymbol.sprite = null;
            if (_routesRing != null)
                _routesRing.sprite = null;
            if (_routesButton != null)
                Destroy(_routesButton);
            if (_routesAnchor != null)
                Destroy(_routesAnchor);
            if (_routesInactiveAnchor != null)
                Destroy(_routesInactiveAnchor);
            _routesButton = null;
            _routesAnchor = null;
            _routesInactiveAnchor = null;
            _backAnchor = null;
            _backInactiveAnchor = null;
            _routesButtonRect = null;
            _routesCollider = null;
            _routesCanvas = null;
            _routesSymbol = null;
            _routesRing = null;
            _routesGlyphGroup = null;
            _routesRingGroup = null;
            if (!includeSprites)
                return;
            if (_routesIconSprite != null)
                Destroy(_routesIconSprite);
            if (_routesIconTexture != null)
                Destroy(_routesIconTexture);
            if (_routesRingSprite != null)
                Destroy(_routesRingSprite);
            if (_routesRingTexture != null)
                Destroy(_routesRingTexture);
            _routesIconSprite = null;
            _routesIconTexture = null;
            _routesRingSprite = null;
            _routesRingTexture = null;
        }

        private static FieldInfo FindField(Type type, string name)
        {
            for (Type t = type; t != null; t = t.BaseType)
            {
                FieldInfo f = t.GetField(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                if (f != null)
                    return f;
            }
            return null;
        }

        // ---------------------------------------------------------------- sprites

        private Sprite GetRoutesIcon()
        {
            if (_routesIconSprite == null)
                _routesIconSprite = MakeSprite(IconTextureSize, IconDesignSize, EdgeSoftness * IconDesignSize / IconDisplaySize,
                    IconSdf, "RouteViz_RoutesIcon", out _routesIconTexture);
            return _routesIconSprite;
        }

        private Sprite GetRingSprite()
        {
            if (_routesRingSprite == null)
                _routesRingSprite = MakeSprite(RingTextureSize, RingDiameter, EdgeSoftness, RingSdf, "RouteViz_RoutesRing", out _routesRingTexture);
            return _routesRingSprite;
        }

        /// <summary>
        /// White sprite whose alpha is the analytic coverage of a signed-distance shape (negative inside), sampled at
        /// texel centres: alpha = clamp(0.5 - d / edge), with the edge ramp in design units (at least one texel).
        /// Mipmapped and trilinear so it stays smooth when drawn smaller. Mip levels are built on the CPU —
        /// this game's stripped Unity uploads blank textures for runtime Apply(updateMipmaps: true).
        /// The Image colour tints it.
        /// </summary>
        private static Sprite MakeSprite(int size, float designSize, float edge, Func<float, float, float> sdf, string name, out Texture2D tex)
        {
            float texel = designSize / size;
            edge = Math.Max(edge, texel);
            var level0 = new Color32[size * size];
            for (int py = 0; py < size; py++)
            {
                for (int px = 0; px < size; px++)
                {
                    float x = (px + 0.5f) * texel;
                    float y = (size - (py + 0.5f)) * texel; // design space has y pointing down
                    float a = 0.5f - sdf(x, y) / edge;
                    a = Math.Max(0f, Math.Min(1f, a));
                    level0[py * size + px] = new Color32(255, 255, 255, (byte)(a * 255f + 0.5f));
                }
            }

            tex = new Texture2D(size, size, TextureFormat.RGBA32, true)
            {
                name = name,
                filterMode = FilterMode.Trilinear,
                anisoLevel = 4,
                wrapMode = TextureWrapMode.Clamp,
            };
            tex.SetPixels32(level0, 0);
            Color32[] prev = level0;
            int prevSize = size;
            for (int mip = 1; mip < tex.mipmapCount; mip++)
            {
                int s = Math.Max(1, prevSize / 2);
                var next = new Color32[s * s];
                for (int y = 0; y < s; y++)
                {
                    for (int x = 0; x < s; x++)
                    {
                        int x0 = x * 2, y0 = y * 2;
                        int x1 = Math.Min(x0 + 1, prevSize - 1);
                        int y1 = Math.Min(y0 + 1, prevSize - 1);
                        // Box-filter alpha (RGB stays white); average in float so thin strokes don't disappear.
                        int a = prev[y0 * prevSize + x0].a + prev[y0 * prevSize + x1].a
                              + prev[y1 * prevSize + x0].a + prev[y1 * prevSize + x1].a;
                        next[y * s + x] = new Color32(255, 255, 255, (byte)((a + 2) / 4));
                    }
                }
                tex.SetPixels32(next, mip);
                prev = next;
                prevSize = s;
            }
            tex.Apply(false, false); // upload our CPU mips; do not regenerate
            Sprite sprite = Sprite.Create(tex, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f), 100f);
            sprite.name = name;
            return sprite;
        }

        /// <summary>Ring in a RingDiameter design square, its outer edge just inside the border.</summary>
        private static float RingSdf(float x, float y)
        {
            float half = RingDiameter / 2f;
            float outer = half - 0.75f;
            float dx = x - half, dy = y - half;
            float d = (float)Math.Sqrt(dx * dx + dy * dy);
            return Math.Abs(d - (outer - RingStroke / 2f)) - RingStroke / 2f;
        }

        /// <summary>
        /// Rear view of a small car driving up a road, in a 128x128 design space with y down: bold road edges converging
        /// to the horizon and two route chevrons ahead of the car, all clipped to a circle so they stop just inside the ring.
        /// </summary>
        private static float IconSdf(float x, float y)
        {
            float car = CarSdf(x, y);
            // Cut-outs: rear window and two tail lights.
            float cut = Math.Min(RoundRect(x, y, 64f, 77f, 14f, 5f, 3f),
                Math.Min(RoundRect(x, y, 47f, 96f, 6f, 3.5f, 2f), RoundRect(x, y, 81f, 96f, 6f, 3.5f, 2f)));
            float carShape = Math.Max(car, -cut);

            float road = Math.Min(Taper(x, y, 10f, 112f, 8.5f, 52f, 14f, 3.5f), Taper(x, y, 118f, 112f, 8.5f, 76f, 14f, 3.5f));
            float chevrons = Math.Min(Chevron(x, y, 64f, 43f, 12.5f, 10f, 4.4f), Chevron(x, y, 64f, 24f, 7.5f, 6.5f, 3.3f));
            float route = Math.Max(Math.Min(road, chevrons), 6f - car); // kept 6 units clear of the car

            float dx = x - 64f, dy = y - 64f;
            float circle = (float)Math.Sqrt(dx * dx + dy * dy) - 62f;
            return Math.Max(Math.Min(carShape, route), circle);
        }

        private static float CarSdf(float x, float y)
        {
            float body = RoundRect(x, y, 64f, 97f, 28f, 13f, 7f);
            float cabin = RoundRect(x, y, 64f, 78f, 20f, 11f, 7f);
            float wheelL = RoundRect(x, y, 47f, 112f, 6.5f, 6f, 2f);
            float wheelR = RoundRect(x, y, 81f, 112f, 6.5f, 6f, 2f);
            return Math.Min(Math.Min(body, cabin), Math.Min(wheelL, wheelR));
        }

        /// <summary>A "^" stroke with its tip at (cx, cy), arms w wide and h tall, half-width r.</summary>
        private static float Chevron(float x, float y, float cx, float cy, float w, float h, float r)
        {
            return Math.Min(Taper(x, y, cx - w, cy + h, r, cx, cy, r), Taper(x, y, cx, cy, r, cx + w, cy + h, r));
        }

        /// <summary>Signed distance to a stroke from (ax, ay) to (bx, by) whose half-width goes from ra to rb.</summary>
        private static float Taper(float x, float y, float ax, float ay, float ra, float bx, float by, float rb)
        {
            float dx = bx - ax, dy = by - ay;
            float t = ((x - ax) * dx + (y - ay) * dy) / (dx * dx + dy * dy);
            t = Math.Max(0f, Math.Min(1f, t));
            float ex = x - (ax + t * dx), ey = y - (ay + t * dy);
            return (float)Math.Sqrt(ex * ex + ey * ey) - (ra + (rb - ra) * t);
        }

        /// <summary>Signed distance to a rounded rectangle centred at (cx, cy) with half-size (hw, hh).</summary>
        private static float RoundRect(float x, float y, float cx, float cy, float hw, float hh, float r)
        {
            float qx = Math.Abs(x - cx) - (hw - r);
            float qy = Math.Abs(y - cy) - (hh - r);
            float ox = Math.Max(qx, 0f), oy = Math.Max(qy, 0f);
            return (float)Math.Sqrt(ox * ox + oy * oy) + Math.Min(Math.Max(qx, qy), 0f) - r;
        }
    }
}

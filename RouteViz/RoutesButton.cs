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
    /// It's a clone of the back button, so it looks, animates, floats in/out and sounds like the game's own buttons.
    /// </summary>
    public partial class RouteVizPlugin
    {
        private const string RoutesButtonName = "RouteViz_RoutesButton";
        private const string RoutesAnchorName = "RouteViz_RoutesButton (Active)";
        private const string RoutesInactiveAnchorName = "RouteViz_RoutesButton (Inactive)";
        /// <summary>Distance from the back button's centre to ours, in the parent's units (button is 100 wide, scale 1.1).</summary>
        private const float RoutesButtonSpacing = 200f;
        private const int IconSize = 128;
        /// <summary>Glyph box size (the back arrow's is 75; our icon fills its box more).</summary>
        private const float IconDisplaySize = 66f;
        private const float HiddenGlyphAlpha = 0.35f;

        private GameObject _routesButton;
        private TouchButton _routesBackButton;
        private RectTransform _routesButtonRect;
        private GameObject _routesAnchor, _routesInactiveAnchor, _backAnchor;
        /// <summary>Dim the glyph and its shadow; CanvasGroups the Animator has no curves for, so it never fights them.</summary>
        private CanvasGroup _routesGlyphGroup, _routesShadowGroup;
        private Texture2D _routesIconTexture;
        private Sprite _routesIconSprite;
        private float _nextRoutesButtonCheck;
        private bool _routesButtonFailed;

        /// <summary>Alt+C and the on-screen button: hide or show every route without dropping the selections.</summary>
        private void ToggleRoutesVisible()
        {
            _routesVisible = !_routesVisible;
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
            return _routesButtonRect != null && _routesButton != null && _routesButton.activeInHierarchy &&
                   RectTransformUtility.RectangleContainsScreenPoint(_routesButtonRect, Input.mousePosition, null);
        }

        /// <summary>Called from Update: (re)creates the button whenever the game UI exists without it.</summary>
        private void UpdateRoutesButton()
        {
            if (_routesButtonFailed)
                return;

            if (_routesButton != null)
            {
                // Shown/hidden together with the back button: the game hides it by switching off its anchor
                // (FloatingElement then floats it away) or the button itself.
                if (_backAnchor != null && _routesAnchor != null && _routesAnchor.activeSelf != _backAnchor.activeSelf)
                    _routesAnchor.SetActive(_backAnchor.activeSelf);
                if (_routesBackButton != null && _routesButton.activeSelf != _routesBackButton.gameObject.activeSelf)
                    _routesButton.SetActive(_routesBackButton.gameObject.activeSelf);

                float alpha = _routesVisible ? 1f : HiddenGlyphAlpha;
                if (_routesGlyphGroup != null && _routesGlyphGroup.alpha != alpha)
                    _routesGlyphGroup.alpha = alpha;
                if (_routesShadowGroup != null && _routesShadowGroup.alpha != alpha)
                    _routesShadowGroup.alpha = alpha;
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

                // FloatingElement places the button at its anchor (and floats it to the inactive anchor when the
                // game hides it). Give ours its own anchors, offset from the back button's.
                Component floating = null;
                foreach (Component c in go.GetComponents<Component>())
                    if (c != null && c.GetType().Name == "FloatingElement")
                        floating = c;
                if (floating != null)
                {
                    FieldInfo baseField = FindField(floating.GetType(), "baseElement");
                    FieldInfo inactiveField = FindField(floating.GetType(), "_inactiveAnchor");
                    var backBase = baseField?.GetValue(floating) as GameObject;
                    var backInactive = inactiveField?.GetValue(floating) as Transform;
                    if (backBase != null && backInactive != null)
                    {
                        _backAnchor = backBase;
                        _routesAnchor = CloneAnchor(backBase, RoutesAnchorName, offset);
                        _routesInactiveAnchor = CloneAnchor(backInactive.gameObject, RoutesInactiveAnchorName, offset);
                        baseField.SetValue(floating, _routesAnchor);
                        inactiveField.SetValue(floating, _routesInactiveAnchor.transform);
                    }
                    else
                    {
                        Logger.LogWarning("Back button's FloatingElement anchors not found; placing the routes button directly.");
                        Object.DestroyImmediate(floating);
                    }
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
            // No "back" sound: our own ding/buzz plays on click.
            FieldInfo audioProfile = FindField(typeof(TouchButton), "_audioProfile");
            if (audioProfile != null && audioProfile.FieldType.IsEnum && Enum.IsDefined(audioProfile.FieldType, "None"))
                audioProfile.SetValue(button, Enum.Parse(audioProfile.FieldType, "None"));

            Sprite icon = GetRoutesIcon();
            foreach (Transform child in go.transform)
            {
                switch (child.name)
                {
                    case "Symbol":
                    case "Symbol-Highlight":
                    case "ArrowDropShadow":
                        child.GetComponent<Image>().sprite = icon;
                        ((RectTransform)child).sizeDelta = new Vector2(IconDisplaySize, IconDisplaySize);
                        if (child.name == "Symbol")
                            _routesGlyphGroup = AddGroup(child);
                        else if (child.name == "ArrowDropShadow")
                            _routesShadowGroup = AddGroup(child);
                        break;
                }
            }

            if (_verboseLogging.Value)
                Logger.LogInfo($"Routes button created at {rect.anchoredPosition} next to the back button.");
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
                ToggleRoutesVisible();
            }
            catch (Exception e)
            {
                Logger.LogError($"Routes button click failed: {e}");
            }
        }

        /// <summary>OnDestroy: remove the button and its anchors, and (when unloading) the icon too.</summary>
        private void DestroyRoutesButton(bool includeIcon = true)
        {
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
            _routesButtonRect = null;
            _routesGlyphGroup = null;
            _routesShadowGroup = null;
            if (!includeIcon)
                return;
            if (_routesIconSprite != null)
                Destroy(_routesIconSprite);
            if (_routesIconTexture != null)
                Destroy(_routesIconTexture);
            _routesIconSprite = null;
            _routesIconTexture = null;
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

        // ---------------------------------------------------------------- icon

        private Sprite GetRoutesIcon()
        {
            if (_routesIconSprite != null)
                return _routesIconSprite;

            const int ss = 4; // 4x4 supersampling
            var pixels = new Color32[IconSize * IconSize];
            for (int py = 0; py < IconSize; py++)
            {
                for (int px = 0; px < IconSize; px++)
                {
                    int hits = 0;
                    for (int sy = 0; sy < ss; sy++)
                        for (int sx = 0; sx < ss; sx++)
                        {
                            float x = px + (sx + 0.5f) / ss;
                            float y = IconSize - (py + (sy + 0.5f) / ss); // design space has y pointing down
                            if (IconCovers(x, y))
                                hits++;
                        }
                    byte a = (byte)(hits * 255 / (ss * ss));
                    pixels[py * IconSize + px] = new Color32(255, 255, 255, a);
                }
            }

            var tex = new Texture2D(IconSize, IconSize, TextureFormat.RGBA32, false)
            {
                name = "RouteViz_RoutesIcon",
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp,
            };
            tex.SetPixels32(pixels);
            tex.Apply();
            _routesIconTexture = tex;
            _routesIconSprite = Sprite.Create(tex, new Rect(0, 0, IconSize, IconSize), new Vector2(0.5f, 0.5f), 100f);
            _routesIconSprite.name = "RouteViz_RoutesIcon";
            return _routesIconSprite;
        }

        /// <summary>Rear view of a small car on a highway, in a 128x128 design space with y down.</summary>
        private static bool IconCovers(float x, float y)
        {
            float car = CarSdf(x, y);
            if (car <= 0f)
            {
                // Cut-outs: rear window and two tail lights.
                if (RoundRect(x, y, 64f, 61f, 20f, 8f, 4f) <= 0f)
                    return false;
                if (RoundRect(x, y, 41f, 86f, 8f, 5f, 3f) <= 0f)
                    return false;
                if (RoundRect(x, y, 87f, 86f, 8f, 5f, 3f) <= 0f)
                    return false;
                return true;
            }
            if (car < 6f)
                return false; // clear gap around the car

            // Road edges converging toward the horizon (thicker up close), and a centre-lane dash ahead of the car.
            if (Taper(x, y, 4f, 120f, 8f, 42f, 14f, 4.5f) <= 0f)
                return true;
            if (Taper(x, y, 124f, 120f, 8f, 86f, 14f, 4.5f) <= 0f)
                return true;
            if (Taper(x, y, 64f, 34f, 5.5f, 64f, 14f, 4f) <= 0f)
                return true;
            return false;
        }

        private static float CarSdf(float x, float y)
        {
            float body = RoundRect(x, y, 64f, 88f, 38f, 18f, 10f);
            float cabin = RoundRect(x, y, 64f, 62f, 28f, 16f, 11f);
            float wheelL = RoundRect(x, y, 41f, 110f, 9f, 9f, 3f);
            float wheelR = RoundRect(x, y, 87f, 110f, 9f, 9f, 3f);
            return Math.Min(Math.Min(body, cabin), Math.Min(wheelL, wheelR));
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

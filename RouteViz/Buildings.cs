using System.Collections.Generic;
using System.Reflection;
using Motorways;
using Motorways.Models;
using Motorways.Processes;
using Motorways.Views;
using Server;
using UnityEngine;

namespace RouteViz
{
    /// <summary>
    /// Clicking a destination's pin selects the car coming for it; clicking a house or destination selects every car
    /// driving to or from it (or, if any are shown, hides them). Hovering either outlines it.
    /// </summary>
    public partial class RouteVizPlugin
    {
        private static readonly FieldInfo PinAnimatorField =
            typeof(DestinationView).GetField("_pinAnimatorToUse", BindingFlags.Instance | BindingFlags.NonPublic);

        private readonly List<VehicleModel> _groupCars = new List<VehicleModel>();

        private enum TargetKind { None, Pin, Destination, House }

        /// <summary>A pin or building under the mouse (what a click there would act on).</summary>
        private struct Target
        {
            public TargetKind Kind;
            public DestinationView Destination;
            public HouseView House;
            public DestinationPinView Pin;
            public int PinIndex;
            public int VisiblePins;
            public Bounds Bounds; // world-space footprint of the building (without its pins)
        }

        private Target _hoverTarget;
        private TargetKind _loggedHoverKind;
        private bool _loggedOutline;
        private LineRenderer _hoverOutline;
        private readonly Dictionary<GameObject, Renderer[]> _rendererCache = new Dictionary<GameObject, Renderer[]>();
        private float _rendererCacheTime;

        /// <summary>Finds the pin, or else the building, under the point. Pins win: they float above buildings.</summary>
        private bool HitTest(Vector2 screenPos, ViewIndex viewIndex, GameCamera cam, out Target target)
        {
            target = default;
            if (Time.unscaledTime - _rendererCacheTime > 1f)
            {
                _rendererCache.Clear(); // buildings upgrade and change their meshes; refresh now and then
                _rendererCacheTime = Time.unscaledTime;
            }

            foreach (DestinationView dv in viewIndex.DestinationViews)
            {
                if (dv == null || dv.Model == null || !dv.gameObject.activeInHierarchy)
                    continue;
                int pinIndex = PinAt(dv, cam, screenPos, out int visiblePins, out DestinationPinView pin);
                if (pinIndex < 0)
                    continue;
                target = new Target { Kind = TargetKind.Pin, Destination = dv, Pin = pin, PinIndex = pinIndex, VisiblePins = visiblePins };
                return true;
            }
            foreach (DestinationView dv in viewIndex.DestinationViews)
            {
                if (dv == null || dv.Model == null || !dv.gameObject.activeInHierarchy)
                    continue;
                Transform pins = PinAnimatorField?.GetValue(dv) is DestinationPinAnimatorView a && a != null ? a.transform : null;
                if (BuildingBounds(dv.gameObject, pins, out Bounds b) && ScreenRectContains(b, cam, screenPos))
                {
                    target = new Target { Kind = TargetKind.Destination, Destination = dv, Bounds = b };
                    return true;
                }
            }
            foreach (HouseView hv in viewIndex.HouseViews)
            {
                if (hv == null || hv.Model == null || !hv.gameObject.activeInHierarchy)
                    continue;
                if (BuildingBounds(hv.gameObject, null, out Bounds b) && ScreenRectContains(b, cam, screenPos))
                {
                    target = new Target { Kind = TargetKind.House, House = hv, Bounds = b };
                    return true;
                }
            }
            return false;
        }

        /// <summary>Hover for pins and buildings. They take precedence over cars. Returns true if one is hovered.</summary>
        private bool UpdateBuildingHover(Vector2 screenPos)
        {
            _hoverTarget = default;
            if (!_routesVisible)
                return false;
            if (!TryGetWorld(out ViewIndex viewIndex, out ISimulation simulation))
                return false;
            GameCamera cam = AnyGameCamera(viewIndex, simulation);
            if (cam != null && HitTest(screenPos, viewIndex, cam, out Target t))
                _hoverTarget = t;
            if (_verboseLogging.Value && _hoverTarget.Kind != _loggedHoverKind)
            {
                _loggedHoverKind = _hoverTarget.Kind;
                Logger.LogInfo($"[hover] {_hoverTarget.Kind} bounds {_hoverTarget.Bounds.center}/{_hoverTarget.Bounds.size}");
            }
            return _hoverTarget.Kind != TargetKind.None;
        }

        /// <summary>Handles a click on a pin or building (checked before cars). Returns true if it hit one.</summary>
        private bool ClickPinOrBuilding(Vector2 screenPos)
        {
            if (!TryGetWorld(out ViewIndex viewIndex, out ISimulation simulation))
                return false;
            GameCamera cam = AnyGameCamera(viewIndex, simulation);
            if (cam == null || !HitTest(screenPos, viewIndex, cam, out Target t))
                return false;

            switch (t.Kind)
            {
                case TargetKind.Pin:
                {
                    // Arriving cars remove the highest visible pin, so the top claimed pin belongs to the car arriving
                    // soonest. Pins below the claimed ones have no car yet.
                    DestinationModel d = t.Destination.Model;
                    CarsTo(d, simulation, _groupCars);
                    _groupCars.Sort((a, b) => ((float)a.pathLength).CompareTo((float)b.pathLength));
                    int claimed = Mathf.Min(d.waitingDemand.Count, _groupCars.Count);
                    int fromTop = t.VisiblePins - 1 - t.PinIndex;
                    if (_verboseLogging.Value)
                        Logger.LogInfo($"Clicked pin {t.PinIndex + 1}/{t.VisiblePins} of a destination: {claimed} claimed by cars on the way.");
                    VehicleView view = fromTop >= 0 && fromTop < claimed ? viewIndex.GetVehicleView(_groupCars[fromTop]) : null;
                    if (view == null)
                        PlayClick(Sounds.Kind.Error); // nobody's coming for this pin yet
                    else
                        ToggleCar(view);
                    return true;
                }
                case TargetKind.Destination:
                    CarsTo(t.Destination.Model, simulation, _groupCars);
                    CarsFrom(t.Destination.Model, simulation, _groupCars);
                    ToggleGroup(viewIndex, "destination");
                    return true;
                case TargetKind.House:
                    _groupCars.Clear();
                    foreach (VehicleModel m in simulation.GetModels<VehicleModel>())
                        if (m.house == t.House.Model && IsDriving(m.behaviorState))
                            _groupCars.Add(m);
                    ToggleGroup(viewIndex, "house");
                    return true;
            }
            return false;
        }

        /// <summary>Outlines the hovered pin (a ring) or building (a rounded rectangle), like the car ring.</summary>
        private void DrawBuildingOutline()
        {
            Target t = _hoverTarget;
            Component owner = t.Kind == TargetKind.Pin ? (t.Pin != null && t.Pin.IsVisible ? t.Destination : null)
                : t.Kind == TargetKind.Destination ? t.Destination
                : t.Kind == TargetKind.House ? (Component)t.House : null;
            GameCamera cam = null;
            float carPx = 0f;
            // Car views are root objects (no shared parent), so the line hangs off the hovered building instead; it's
            // in world space, so the parent only keeps it alive (the game destroys foreign root objects).
            if (owner != null && TryGetWorld(out ViewIndex viewIndex, out ISimulation simulation))
            {
                cam = AnyGameCamera(viewIndex, simulation);
                if (cam != null)
                    carPx = CarLengthPixels(viewIndex, cam);
            }
            Vector3 centre = t.Kind == TargetKind.Pin && owner != null ? t.Pin.PinCenterPosition : t.Bounds.center;
            float ppu = cam != null ? (cam.GetScreenFromWorld(centre + Vector3.right) - cam.GetScreenFromWorld(centre)).magnitude : 0f;
            if (owner == null || ppu < 1e-3f || carPx <= 0f)
            {
                if (_hoverOutline != null)
                    _hoverOutline.enabled = false;
                return;
            }

            _hoverOutline = EnsureLine(_hoverOutline, owner.transform); // never returned to the pool: it's in world space
            LineRenderer line = _hoverOutline;
            line.useWorldSpace = true;
            line.numCapVertices = 0; // closed shape: round caps would overlap at the seam and draw darker
            // Sizes in car lengths, converted to world units here, so they scale with the map like the car ring.
            float worldPerCar = carPx / ppu;
            float width = OutlineCarLengths * worldPerCar;
            line.startWidth = width;
            line.endWidth = width;
            line.startColor = RingColor;
            line.endColor = RingColor;
            line.sortingLayerID = TopSortingLayer();
            line.sortingOrder = 100;

            _points.Clear();
            const float z = LineZ; // in front of buildings and pins (z -1..0); the layer does the rest
            if (t.Kind == TargetKind.Pin)
            {
                float r = _clickRadius.Value * PinRadiusFraction * worldPerCar;
                if (_pointBuffer.Length < RingPointCount)
                    _pointBuffer = new Vector3[Mathf.NextPowerOfTwo(RingPointCount)];
                int n = FillRing(_pointBuffer, centre.x, centre.y, r, z);
                for (int i = 0; i < n; i++)
                    _points.Add(_pointBuffer[i]);
            }
            else
            {
                float pad = PadCarLengths * worldPerCar;
                RoundedRect(t.Bounds.min.x - pad, t.Bounds.min.y - pad, t.Bounds.max.x + pad, t.Bounds.max.y + pad, 0.25f * worldPerCar, z);
            }

            if (_pointBuffer.Length < _points.Count)
                _pointBuffer = new Vector3[Mathf.NextPowerOfTwo(_points.Count)];
            _points.CopyTo(_pointBuffer);
            line.positionCount = _points.Count;
            line.SetPositions(_pointBuffer);
            line.enabled = true;
            if (_verboseLogging.Value && !_loggedOutline)
            {
                _loggedOutline = true;
                Logger.LogInfo($"[outline] {t.Kind}: {_points.Count} pts from {_points[0]}, width {width}, layer {SortingLayer.IDToName(line.sortingLayerID)}, " +
                               $"parent {owner.name}, ppu {ppu}, carPx {carPx}");
            }
            _points.Clear();
        }

        /// <summary>Points FillRing writes.</summary>
        private const int RingPointCount = RingSegments + 3;

        /// <summary>
        /// A closed circle for a capless LineRenderer (this build has no LineRenderer.loop): the first and last
        /// segments are tiny and nearly tangent at the seam, so their flat ends meet flush with no overlap or gap.
        /// Returns the number of points written (RingPointCount).
        /// </summary>
        private static int FillRing(Vector3[] buffer, float cx, float cy, float r, float z)
        {
            const float seamGap = 0.01f; // radians
            float step = 2f * (float)System.Math.PI / RingSegments;
            int n = 0;
            buffer[n++] = new Vector3(cx + r, cy, z);
            buffer[n++] = new Vector3(cx + Mathf.Cos(seamGap) * r, cy + Mathf.Sin(seamGap) * r, z);
            for (int i = 1; i < RingSegments; i++)
                buffer[n++] = new Vector3(cx + Mathf.Cos(i * step) * r, cy + Mathf.Sin(i * step) * r, z);
            buffer[n++] = new Vector3(cx + Mathf.Cos(-seamGap) * r, cy + Mathf.Sin(-seamGap) * r, z);
            buffer[n++] = new Vector3(cx + r, cy, z);
            return n;
        }

        private const float OutlineCarLengths = 0.22f; // outline thickness, a little bolder than the car ring
        private const float PadCarLengths = 0.12f; // gap between building and outline

        private void RoundedRect(float x0, float y0, float x1, float y1, float radius, float z)
        {
            radius = Mathf.Min(radius, Mathf.Min((x1 - x0) * 0.5f, (y1 - y0) * 0.5f));
            const int perCorner = 6;
            // Start and end mid-way up the right edge: both ends then run straight up it, so their flat (capless) ends
            // meet flush, with no overlap to draw darker.
            var seam = new Vector3(x1, (y0 + y1) * 0.5f, z);
            _points.Add(seam);
            float[] cx = { x1 - radius, x0 + radius, x0 + radius, x1 - radius };
            float[] cy = { y1 - radius, y1 - radius, y0 + radius, y0 + radius };
            for (int c = 0; c < 4; c++)
                for (int i = 0; i <= perCorner; i++)
                {
                    float a = (c * 90f + i * (90f / perCorner)) * ((float)System.Math.PI / 180f);
                    _points.Add(new Vector3(cx[c] + Mathf.Cos(a) * radius, cy[c] + Mathf.Sin(a) * radius, z));
                }
            _points.Add(seam);
        }

        private static int _topSortingLayer = int.MinValue;

        /// <summary>
        /// The game's "InteractionCircle" layer: above buildings, demand pins and cars, below alerts and UI. Falls back
        /// to the frontmost layer.
        /// </summary>
        private static int TopSortingLayer()
        {
            if (_topSortingLayer == int.MinValue)
            {
                int id = SortingLayer.NameToID("InteractionCircle");
                SortingLayer[] layers = SortingLayer.layers;
                _topSortingLayer = SortingLayer.IDToName(id) == "InteractionCircle" ? id
                    : layers != null && layers.Length > 0 ? layers[layers.Length - 1].id : MotorwayCarLayerId;
            }
            return _topSortingLayer;
        }

        /// <summary>Drop shadows: on a "...Shadow" sorting layer (HouseShadow, SquareDestinationShadow...).</summary>
        private static bool IsShadow(Renderer r)
        {
            return SortingLayer.IDToName(r.sortingLayerID).EndsWith("Shadow", System.StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// World bounds of the building's body, leaving out drop shadows and anything under `exclude` (its pins).
        /// Houses are drawn by a shared combined mesh, so their own body renderers sit inactive (only the shadow is
        /// live); when no active body renderer is found, the inactive ones still give the right footprint.
        /// </summary>
        private bool BuildingBounds(GameObject root, Transform exclude, out Bounds bounds)
        {
            if (!_rendererCache.TryGetValue(root, out Renderer[] renderers))
            {
                renderers = root.GetComponentsInChildren<Renderer>(true);
                _rendererCache[root] = renderers;
            }
            return BodyBounds(renderers, exclude, true, out bounds) || BodyBounds(renderers, exclude, false, out bounds);
        }

        private static bool BodyBounds(Renderer[] renderers, Transform exclude, bool activeOnly, out Bounds bounds)
        {
            bounds = default;
            bool any = false;
            foreach (Renderer r in renderers)
            {
                if (r == null || r is LineRenderer || IsShadow(r))
                    continue;
                if (activeOnly && (!r.enabled || !r.gameObject.activeInHierarchy))
                    continue;
                if (exclude != null && r.transform.IsChildOf(exclude))
                    continue;
                Bounds b = r.bounds;
                if (b.extents.sqrMagnitude < 1e-6f || b.extents.magnitude > 6f)
                    continue;
                if (!any) { bounds = b; any = true; }
                else bounds.Encapsulate(b);
            }
            return any;
        }

        private const float PinRadiusFraction = 0.7f; // pin circles are a bit smaller than car circles

        /// <summary>On-screen length of a car, in pixels (from any car in view), or 0 if there are none.</summary>
        private static float CarLengthPixels(ViewIndex viewIndex, GameCamera cam)
        {
            if (viewIndex == null && !TryGetWorld(out viewIndex, out _))
                return 0f;
            if (!TryGetWorld(out _, out ISimulation simulation))
                return 0f;
            foreach (VehicleModel m in simulation.GetModels<VehicleModel>())
            {
                VehicleView v = viewIndex.GetVehicleView(m);
                if (v != null && v.gameObject.activeInHierarchy && TryGetPixelsPerUnit(v, out float ppu))
                    return ppu * (float)VehicleMovementProcess.VehicleLength;
            }
            return 0f;
        }

        /// <summary>Cars heading to the destination (including parking there). Clears the list first.</summary>
        private static void CarsTo(DestinationModel d, ISimulation simulation, List<VehicleModel> into)
        {
            into.Clear();
            foreach (VehicleModel m in simulation.GetModels<VehicleModel>())
                if (m.destination == d && TripLeg(m.behaviorState) == 1)
                    into.Add(m);
        }

        /// <summary>Adds cars driving home from the destination.</summary>
        private static void CarsFrom(DestinationModel d, ISimulation simulation, List<VehicleModel> into)
        {
            foreach (VehicleModel m in simulation.GetModels<VehicleModel>())
                if (m.lastVisitedDestination == d && TripLeg(m.behaviorState) == 2)
                    into.Add(m);
        }

        /// <summary>
        /// Shows all the group's cars, or, if any of them is already shown, hides them all. "Any" rather than "all", so
        /// a newly dispatched car (a new pin) doesn't turn the next click into an add.
        /// </summary>
        private void ToggleGroup(ViewIndex viewIndex, string what)
        {
            var views = new List<VehicleView>(_groupCars.Count);
            foreach (VehicleModel m in _groupCars)
            {
                VehicleView v = viewIndex.GetVehicleView(m);
                if (v != null && v.Model == m && v.gameObject.activeInHierarchy)
                    views.Add(v);
            }
            if (views.Count == 0)
            {
                if (_verboseLogging.Value)
                    Logger.LogInfo($"Clicked a {what}: no cars driving to or from it.");
                PlayClick(Sounds.Kind.Error);
                return;
            }

            bool anyShown = views.Exists(v => FindSelection(v) >= 0);
            int changed = 0;
            foreach (VehicleView v in views)
            {
                int existing = FindSelection(v);
                if (anyShown)
                {
                    if (existing < 0)
                        continue;
                    RemoveSelectionAt(existing);
                    StartFlash(v);
                    changed++;
                }
                else if (existing < 0)
                {
                    AddSelection(v);
                    StartFlash(v);
                    changed++;
                }
            }
            Logger.LogInfo($"Clicked a {what}: {(anyShown ? "hid" : "showing")} {changed} of its {views.Count} car(s). {_selections.Count} shown.");
            PlayClick(anyShown ? Sounds.Kind.Dong : Sounds.Kind.Ding);
        }

        /// <summary>Index of the visible pin under the point (in the animator's pin list), or -1.</summary>
        private int PinAt(DestinationView dv, GameCamera cam, Vector2 screenPos, out int visiblePins, out DestinationPinView hit)
        {
            visiblePins = 0;
            hit = null;
            if (!(PinAnimatorField?.GetValue(dv) is DestinationPinAnimatorView animator) || animator == null || animator.pins == null)
                return -1;
            visiblePins = animator.VisiblePinCount;
            int best = -1;
            float carPx = CarLengthPixels(null, cam);
            float bestDistance = carPx * _clickRadius.Value * PinRadiusFraction;
            for (int i = 0; i < animator.pins.Count; i++)
            {
                DestinationPinView pin = animator.pins[i];
                if (pin == null || !pin.IsVisible || !pin.gameObject.activeInHierarchy)
                    continue;
                float d = (cam.GetScreenFromWorld(pin.PinCenterPosition) - screenPos).magnitude;
                if (d < bestDistance)
                {
                    bestDistance = d;
                    best = i;
                    hit = pin;
                }
            }
            return best;
        }

        /// <summary>Whether the point falls inside the screen rectangle around the world bounds.</summary>
        private static bool ScreenRectContains(Bounds b, GameCamera cam, Vector2 screenPos)
        {
            Vector2 min = Vector2.zero, max = Vector2.zero;
            for (int c = 0; c < 8; c++)
            {
                Vector3 corner = new Vector3(
                    (c & 1) == 0 ? b.min.x : b.max.x,
                    (c & 2) == 0 ? b.min.y : b.max.y,
                    (c & 4) == 0 ? b.min.z : b.max.z);
                Vector2 sp = cam.GetScreenFromWorld(corner);
                if (c == 0) { min = max = sp; }
                else { min = Vector2.Min(min, sp); max = Vector2.Max(max, sp); }
            }
            return screenPos.x >= min.x && screenPos.x <= max.x && screenPos.y >= min.y && screenPos.y <= max.y;
        }

        private static GameCamera AnyGameCamera(ViewIndex viewIndex, ISimulation simulation)
        {
            foreach (VehicleModel m in simulation.GetModels<VehicleModel>())
            {
                VehicleView v = viewIndex.GetVehicleView(m);
                if (v != null && GameCameraField?.GetValue(v) is GameCamera cam)
                    return cam;
            }
            return null;
        }
    }
}

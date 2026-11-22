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

        /// <summary>Hover for pins and buildings, only when no car is under the mouse.</summary>
        private void UpdateBuildingHover(Vector2 screenPos)
        {
            _hoverTarget = default;
            if (!_routesVisible || _hover != null)
                return;
            if (!TryGetWorld(out ViewIndex viewIndex, out ISimulation simulation))
                return;
            GameCamera cam = AnyGameCamera(viewIndex, simulation);
            if (cam != null && HitTest(screenPos, viewIndex, cam, out Target t))
                _hoverTarget = t;
            if (_verboseLogging.Value && _hoverTarget.Kind != _loggedHoverKind)
            {
                _loggedHoverKind = _hoverTarget.Kind;
                Logger.LogInfo($"[hover] {_hoverTarget.Kind} bounds {_hoverTarget.Bounds.center}/{_hoverTarget.Bounds.size}");
            }
        }

        /// <summary>Handles a click that didn't hit a car. Returns true if it hit a pin or building.</summary>
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
            bool valid = t.Kind == TargetKind.Pin ? t.Pin != null && t.Pin.IsVisible
                : t.Kind == TargetKind.Destination ? t.Destination != null
                : t.Kind == TargetKind.House && t.House != null;
            GameCamera cam = null;
            Transform parent = null;
            if (valid && TryGetWorld(out ViewIndex viewIndex, out ISimulation simulation))
            {
                cam = AnyGameCamera(viewIndex, simulation);
                parent = AnyCarParent(viewIndex, simulation);
            }
            if (!valid || cam == null || parent == null)
            {
                if (_hoverOutline != null)
                    _hoverOutline.enabled = false;
                return;
            }

            Vector3 centre = t.Kind == TargetKind.Pin ? t.Pin.PinCenterPosition : t.Bounds.center;
            float ppu = (cam.GetScreenFromWorld(centre + Vector3.right) - cam.GetScreenFromWorld(centre)).magnitude;
            if (ppu < 1e-3f)
                return;

            if (_hoverOutline == null)
            {
                _hoverOutline = EnsureLine(null, parent); // never returned to the pool: it's in world space
                _hoverOutline.useWorldSpace = true;
            }
            LineRenderer line = _hoverOutline;
            line.numCapVertices = 3;
            // Sizes in car lengths, converted to world units here, so they scale with the map like the car ring.
            float carPx = CarLengthPixels(viewIndex2(), cam);
            if (carPx <= 0f)
                return;
            float worldPerCar = carPx / ppu;
            float width = 0.08f * worldPerCar;
            line.startWidth = width;
            line.endWidth = width;
            line.startColor = RingColor;
            line.endColor = RingColor;
            line.sortingLayerID = TopSortingLayer();
            line.sortingOrder = 100;

            _points.Clear();
            float z = t.Kind == TargetKind.Pin ? centre.z - 0.05f : t.Bounds.min.z - 0.05f;
            if (t.Kind == TargetKind.Pin)
            {
                float r = _clickRadius.Value * PinRadiusFraction * worldPerCar;
                for (int i = 0; i <= RingSegments; i++)
                {
                    float a = i * (2f * (float)System.Math.PI / RingSegments);
                    _points.Add(new Vector3(centre.x + Mathf.Cos(a) * r, centre.y + Mathf.Sin(a) * r, z));
                }
            }
            else
            {
                float pad = 0.06f * worldPerCar;
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
                               $"parent {(line.transform.parent != null ? line.transform.parent.name : "-")}, ppu {ppu}");
            }
            _points.Clear();
        }

        private void RoundedRect(float x0, float y0, float x1, float y1, float radius, float z)
        {
            radius = Mathf.Min(radius, Mathf.Min((x1 - x0) * 0.5f, (y1 - y0) * 0.5f));
            const int perCorner = 6;
            float[] cx = { x1 - radius, x0 + radius, x0 + radius, x1 - radius };
            float[] cy = { y1 - radius, y1 - radius, y0 + radius, y0 + radius };
            for (int c = 0; c < 4; c++)
                for (int i = 0; i <= perCorner; i++)
                {
                    float a = (c * 90f + i * (90f / perCorner)) * ((float)System.Math.PI / 180f);
                    _points.Add(new Vector3(cx[c] + Mathf.Cos(a) * radius, cy[c] + Mathf.Sin(a) * radius, z));
                }
            _points.Add(_points[_points.Count - 4 * (perCorner + 1)]); // close the loop
        }

        private static int _topSortingLayer = int.MinValue;

        /// <summary>The frontmost sorting layer, so outlines draw over buildings and their pins.</summary>
        private static int TopSortingLayer()
        {
            if (_topSortingLayer == int.MinValue)
            {
                SortingLayer[] layers = SortingLayer.layers;
                _topSortingLayer = layers != null && layers.Length > 0 ? layers[layers.Length - 1].id : MotorwayCarLayerId;
            }
            return _topSortingLayer;
        }

        /// <summary>World bounds of the object's visible renderers, leaving out anything under `exclude` (its pins).</summary>
        private bool BuildingBounds(GameObject root, Transform exclude, out Bounds bounds)
        {
            bounds = default;
            if (!_rendererCache.TryGetValue(root, out Renderer[] renderers))
            {
                renderers = root.GetComponentsInChildren<Renderer>(false);
                _rendererCache[root] = renderers;
            }
            bool any = false;
            foreach (Renderer r in renderers)
            {
                if (r == null || !r.enabled || !r.gameObject.activeInHierarchy || r is LineRenderer)
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

        private ViewIndex viewIndex2() => TryGetWorld(out ViewIndex vi, out _) ? vi : null;

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

        private static Transform AnyCarParent(ViewIndex viewIndex, ISimulation simulation)
        {
            foreach (VehicleModel m in simulation.GetModels<VehicleModel>())
            {
                VehicleView v = viewIndex.GetVehicleView(m);
                if (v != null && v.transform.parent != null)
                    return v.transform.parent;
            }
            return null;
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

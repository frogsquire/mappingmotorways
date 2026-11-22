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
        private Vector2 _hoverScreenPos;
        private TargetKind _loggedHoverKind;
        private DestinationPinView _loggedHoverPin;
        private bool _loggedOutline;
        private LineRenderer _hoverOutline;
        private readonly Dictionary<GameObject, Renderer[]> _rendererCache = new Dictionary<GameObject, Renderer[]>();
        private float _rendererCacheTime;

        /// <summary>
        /// Finds the pin, or else the building, under the point. Pins win: they float above buildings. `stickyPins` is
        /// the destination whose pin was hovered last: its pins get a wider catch area, so sliding the mouse along its
        /// row of pins never drops through a gap between two pins onto the building.
        /// </summary>
        private bool HitTest(Vector2 screenPos, ViewIndex viewIndex, GameCamera cam, DestinationView stickyPins, out Target target)
        {
            target = default;
            if (Time.unscaledTime - _rendererCacheTime > 1f)
            {
                _rendererCache.Clear(); // buildings upgrade and change their meshes; refresh now and then
                _pinShapeCache.Clear();
                _rendererCacheTime = Time.unscaledTime;
            }

            float worldPerCar = WorldPerCar(cam, screenPos);
            if (worldPerCar > 0f)
            {
                Vector3 world = cam.GetWorldFromScreen(screenPos);
                if (stickyPins != null && stickyPins.gameObject.activeInHierarchy
                    && PinTarget(stickyPins, world, StickyPinPadCarLengths * worldPerCar, out target))
                    return true;
                foreach (DestinationView dv in viewIndex.DestinationViews)
                {
                    if (dv == null || dv.Model == null || !dv.gameObject.activeInHierarchy)
                        continue;
                    if (PinTarget(dv, world, PinHitPadCarLengths * worldPerCar, out target))
                        return true;
                }
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
            DestinationView sticky = StickyPins();
            _hoverTarget = default;
            _hoverScreenPos = screenPos;
            if (!_routesVisible)
                return false;
            if (!TryGetWorld(out ViewIndex viewIndex, out ISimulation simulation))
                return false;
            GameCamera cam = AnyGameCamera(viewIndex, simulation);
            if (cam != null && HitTest(screenPos, viewIndex, cam, sticky, out Target t))
                _hoverTarget = t;
            if (cam != null) TempPinTest(viewIndex, cam); // TEMP
            if (_verboseLogging.Value && (_hoverTarget.Kind != _loggedHoverKind || _hoverTarget.Pin != _loggedHoverPin))
            {
                _loggedHoverKind = _hoverTarget.Kind;
                _loggedHoverPin = _hoverTarget.Pin;
                Logger.LogInfo(_hoverTarget.Kind == TargetKind.Pin
                    ? $"[hover] Pin {_hoverTarget.PinIndex + 1}/{_hoverTarget.VisiblePins}{(sticky == _hoverTarget.Destination ? " (sticky row)" : "")}"
                    : $"[hover] {_hoverTarget.Kind} bounds {_hoverTarget.Bounds.center}/{_hoverTarget.Bounds.size}");
            }
            return _hoverTarget.Kind != TargetKind.None;
        }

        // TEMP test harness.
        private bool _tempSwept;
        private LineRenderer _tempLine;
        private float _tempStart;
        private bool _tempCleaned;
        private Material _tempMat;
        private void TempPinTest(ViewIndex viewIndex, GameCamera cam)
        {
            DestinationView best = null;
            DestinationPinAnimatorView bestA = null;
            foreach (DestinationView dv in viewIndex.DestinationViews)
                if (dv != null && dv.gameObject.activeInHierarchy && PinAnimatorField?.GetValue(dv) is DestinationPinAnimatorView a && a != null
                    && (bestA == null || a.VisiblePinCount > bestA.VisiblePinCount))
                { best = dv; bestA = a; }
            if (best == null || bestA.VisiblePinCount < 2)
                return;
            var centres = new List<Vector2>();
            int forced = -1;
            for (int i = 0; i < bestA.pins.Count; i++)
                if (bestA.pins[i] != null && bestA.pins[i].IsVisible)
                {
                    centres.Add(cam.GetScreenFromWorld(bestA.pins[i].PinCenterPosition));
                    if (forced < 0 || centres.Count == 2) forced = i;
                }
            if (!_tempCleaned)
            {
                _tempCleaned = true;
                int killed = 0;
                foreach (DestinationView dv in viewIndex.DestinationViews)
                    if (dv != null)
                        for (int c = dv.transform.childCount - 1; c >= 0; c--)
                        {
                            Transform ch = dv.transform.GetChild(c);
                            if (ch.name == "RouteViz_Line" && (_hoverOutline == null || ch != _hoverOutline.transform)) { Destroy(ch.gameObject); killed++; }
                        }
                Logger.LogInfo($"[pintest] removed {killed} stray lines");
            }
            if (_tempStart == 0f) _tempStart = Time.unscaledTime;
            if (Time.unscaledTime - _tempStart > 25f)
            {
                if (_tempLine != null) _tempLine.enabled = false;
            }
            else if (forced >= 0 && TryGetPinShape(bestA.pins[forced], out PinShape ts))
            {
                float wpc = CarLengthPixels(viewIndex, cam) / (cam.GetScreenFromWorld(Vector3.right) - cam.GetScreenFromWorld(Vector3.zero)).magnitude;
                _tempLine = EnsureLine(_tempLine, best.transform.parent != null ? best.transform.parent : best.transform);
                _tempLine.useWorldSpace = true;
                _tempLine.numCapVertices = 0;
                _tempLine.startWidth = _tempLine.endWidth = OutlineCarLengths * wpc;
                _tempLine.startColor = _tempLine.endColor = RingColor;
                _tempLine.sortingLayerID = SortingLayer.NameToID("UI"); if (_tempMat == null) { _tempMat = new Material(_tempLine.sharedMaterial); _tempMat.renderQueue = 4000; } _tempLine.sharedMaterial = _tempMat;
                _tempLine.sortingOrder = 32000;
                _points.Clear();
                PinOutline(ts, PadCarLengths * wpc, LineZ);
                var arr = _points.ToArray();
                { float cx = bestA.pins[forced].PinCenterPosition.x; for (int k = 0; k < arr.Length; k++) arr[k].x = 2f * cx - arr[k].x; }
                _points.Clear();
                _tempLine.positionCount = arr.Length;
                _tempLine.SetPositions(arr);
                _tempLine.enabled = true;
                if (Time.frameCount % 120 == 0)
                    Logger.LogInfo($"[pintest] temp line mirrored at pin {forced} scr {cam.GetScreenFromWorld(bestA.pins[forced].PinCenterPosition)} parent {_tempLine.transform.parent.name}");
            }
            if (_tempSwept)
                return;
            _tempSwept = true;
            Logger.LogInfo("[pintest] layers: " + string.Join(", ", System.Array.ConvertAll(SortingLayer.layers, l => SortingLayer.IDToName(l.id) + "=" + SortingLayer.GetLayerValueFromID(l.id))));
            float carPx = CarLengthPixels(viewIndex, cam);
            foreach (float off in new[] { 0f, -0.5f, 0.5f, -0.8f, 0.8f })
            {
                var plain = new System.Text.StringBuilder();
                var sticky = new System.Text.StringBuilder();
                DestinationView prev = null;
                for (int seg = 0; seg + 1 < centres.Count; seg++)
                {
                    Vector2 a = centres[seg], b = centres[seg + 1];
                    Vector2 n = new Vector2(-(b - a).y, (b - a).x).normalized * off * carPx;
                    for (int k = 0; k < 20; k++)
                    {
                        Vector2 sp = Vector2.Lerp(a, b, k / 20f) + n;
                        HitTest(sp, viewIndex, cam, null, out Target t0);
                        plain.Append(TempChar(t0));
                        HitTest(sp, viewIndex, cam, prev, out Target t1);
                        sticky.Append(TempChar(t1));
                        prev = t1.Kind == TargetKind.Pin ? t1.Destination : null;
                    }
                }
                Logger.LogInfo($"[pintest] offset {off} car: plain  {plain}\n[pintest] offset {off} car: sticky {sticky}");
            }
        }
        private static char TempChar(Target t) =>
            t.Kind == TargetKind.Pin ? (char)('1' + t.PinIndex) : t.Kind == TargetKind.Destination ? 'D' : t.Kind == TargetKind.House ? 'H' : '-';

        /// <summary>Handles a click on a pin or building (checked before cars). Returns true if it hit one.</summary>
        private bool ClickPinOrBuilding(Vector2 screenPos)
        {
            if (!TryGetWorld(out ViewIndex viewIndex, out ISimulation simulation))
                return false;
            // Act on exactly what's outlined: hover ran this frame for the same mouse position.
            Target t = _hoverTarget;
            if (!(_routesVisible && screenPos == _hoverScreenPos))
            {
                GameCamera cam = AnyGameCamera(viewIndex, simulation);
                if (cam == null || !HitTest(screenPos, viewIndex, cam, StickyPins(), out t))
                    return false;
            }
            if (t.Kind == TargetKind.Pin && (t.Pin == null || !t.Pin.IsVisible))
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

        /// <summary>Outlines the hovered pin (pin-shaped) or building (a rounded rectangle), like the car ring.</summary>
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
                // Rebuilt every frame from the pin sprite's current transform, so it follows the pop-in animation.
                if (!TryGetPinShape(t.Pin, out PinShape shape))
                {
                    line.enabled = false;
                    return;
                }
                PinOutline(shape, PadCarLengths * worldPerCar, z);
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
            if (Time.frameCount % 120 == 0) // TEMP
                Logger.LogInfo($"[pintest] draw {t.Kind} pin {t.PinIndex} first {_points[0]:F3} n {_points.Count} active {line.gameObject.activeInHierarchy} en {line.enabled} order {line.sortingOrder} layer {SortingLayer.IDToName(line.sortingLayerID)} mat {(line.sharedMaterial != null ? line.sharedMaterial.shader.name : "null")}");
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

        // Pin geometry, measured in game (sprite SPR_Pin_Small_BakedCol, 256 px at 128 px/unit, so -1..1 in sprite
        // units, drawn at world scale 0.5): a teardrop, a round head with a sharp point straight down, white rim
        // included. In units of the sprite's half-height, relative to the sprite's centre:
        private const float PinHeadY = 0.18f;   // head circle centre (also the coloured dot's centre)
        private const float PinHeadRadius = 0.80f;
        private const float PinTipY = -0.99f;   // the point

        private const float PinHitPadCarLengths = 0.25f;   // pins catch the mouse a little outside their outline
        private const float StickyPinPadCarLengths = 0.6f; // ...and further out while one of the same row is hovered

        /// <summary>A pin's teardrop, in the local space of its sprite renderer.</summary>
        private struct PinShape
        {
            public Transform Transform;
            public Vector2 Head;
            public float Radius;
            public Vector2 Tip;
            public float Scale; // world units per local unit
            public int SortingOrder;
        }

        private readonly Dictionary<DestinationPinView, SpriteRenderer> _pinShapeCache = new Dictionary<DestinationPinView, SpriteRenderer>();

        /// <summary>The pin's body sprite (the white-rimmed teardrop), or else its biggest visible sprite.</summary>
        private bool TryGetPinShape(DestinationPinView pin, out PinShape shape)
        {
            shape = default;
            if (pin == null)
                return false;
            if (!_pinShapeCache.TryGetValue(pin, out SpriteRenderer body) || body == null)
            {
                body = null;
                float bestSize = 0f;
                foreach (SpriteRenderer sr in pin.GetComponentsInChildren<SpriteRenderer>(true))
                {
                    if (sr.sprite == null)
                        continue;
                    if (sr.sprite.name.Contains("BakedCol")) { body = sr; break; }
                    float size = sr.sprite.bounds.size.y * sr.transform.lossyScale.y;
                    if (sr.color.a > 0.01f && size > bestSize) { bestSize = size; body = sr; }
                }
                if (body == null)
                    return false;
                _pinShapeCache[pin] = body;
            }
            if (body.sprite == null || !body.gameObject.activeInHierarchy)
                return false;
            Bounds sb = body.sprite.bounds;
            float h = sb.extents.y;
            Transform tr = body.transform;
            float scale = Mathf.Abs(tr.lossyScale.y);
            if (h < 1e-5f || scale < 1e-5f)
                return false;
            shape = new PinShape
            {
                Transform = tr,
                Head = new Vector2(sb.center.x, sb.center.y + PinHeadY * h),
                Radius = PinHeadRadius * h,
                Tip = new Vector2(sb.center.x, sb.center.y + PinTipY * h),
                Scale = scale,
                SortingOrder = body.sortingOrder,
            };
            return true;
        }

        /// <summary>
        /// Distance in world units from the point to the pin's teardrop (negative inside, roughly). The teardrop is
        /// the convex hull of the head circle and the tip.
        /// </summary>
        private static float PinDistance(PinShape s, Vector3 world)
        {
            Vector3 l = s.Transform.InverseTransformPoint(world);
            var p = new Vector2(s.Head.x + Mathf.Abs(l.x - s.Head.x), l.y); // symmetric: fold onto the right half
            float dHead = (p - s.Head).magnitude - s.Radius;
            TipTangent(s.Head, s.Radius, s.Tip, out Vector2 tangent, out _);
            // Right flank, from the tip up to where it touches the head.
            Vector2 e = tangent - s.Tip;
            float u = Mathf.Clamp01(Vector2.Dot(p - s.Tip, e) / e.sqrMagnitude);
            float dFlank = (p - (s.Tip + u * e)).magnitude;
            // On the axis side of the flank, between the tip and the head: inside the pin.
            float cross = e.x * (p.y - s.Tip.y) - e.y * (p.x - s.Tip.x);
            if (cross > 0f && p.y >= s.Tip.y && p.y <= tangent.y)
                dFlank = -dFlank;
            return Mathf.Min(dHead, dFlank) * s.Scale;
        }

        /// <summary>Where the right flank from the tip touches the head circle, and the sine of the tip's half-angle.</summary>
        private static void TipTangent(Vector2 head, float radius, Vector2 tip, out Vector2 tangent, out float sinHalfAngle)
        {
            float d = Mathf.Max((head - tip).magnitude, radius * 1.001f);
            sinHalfAngle = radius / d;
            float cosA = radius / d; // angle at the head between "towards the tip" and the tangent point
            float sinA = Mathf.Sqrt(Mathf.Max(0f, 1f - cosA * cosA));
            Vector2 down = (tip - head) / d;
            var right = new Vector2(-down.y, down.x); // "down" turned a quarter anticlockwise: +x for a pin pointing down
            tangent = head + radius * (cosA * down + sinA * right);
        }

        /// <summary>
        /// The pin's teardrop grown by `pad` world units (a bigger head and a lower point, flanks parallel), as a closed
        /// line for a capless LineRenderer: it starts and ends mid-way along the right flank so the ends meet flush.
        /// </summary>
        private void PinOutline(PinShape s, float pad, float z)
        {
            float padLocal = pad / s.Scale;
            TipTangent(s.Head, s.Radius, s.Tip, out _, out float sinHalf);
            Vector2 down = (s.Tip - s.Head).normalized;
            float radius = s.Radius + padLocal;
            Vector2 tip = s.Tip + down * (padLocal / Mathf.Max(sinHalf, 0.1f));
            TipTangent(s.Head, radius, tip, out Vector2 tangentRight, out _);
            Vector2 tangentLeft = new Vector2(2f * s.Head.x - tangentRight.x, tangentRight.y);

            Vector2 seam = (tip + tangentRight) * 0.5f;
            AddPinPoint(s, seam, z);
            // Head arc, anticlockwise from the right tangent point over the top to the left one.
            float a0 = Mathf.Atan2(tangentRight.y - s.Head.y, tangentRight.x - s.Head.x);
            float a1 = Mathf.Atan2(tangentLeft.y - s.Head.y, tangentLeft.x - s.Head.x);
            if (a1 < a0)
                a1 += 2f * (float)System.Math.PI;
            const int arcSegments = 32;
            for (int i = 0; i <= arcSegments; i++)
            {
                float a = a0 + (a1 - a0) * i / arcSegments;
                AddPinPoint(s, s.Head + radius * new Vector2(Mathf.Cos(a), Mathf.Sin(a)), z);
            }
            AddPinPoint(s, tip, z);
            AddPinPoint(s, seam, z);
        }

        private void AddPinPoint(PinShape s, Vector2 local, float z)
        {
            Vector3 w = s.Transform.TransformPoint(new Vector3(local.x, local.y, 0f));
            _points.Add(new Vector3(w.x, w.y, z));
        }

        /// <summary>The destination whose pin is hovered now, if any (its pins are sticky; see HitTest).</summary>
        private DestinationView StickyPins()
        {
            return _hoverTarget.Kind == TargetKind.Pin && _hoverTarget.Destination != null ? _hoverTarget.Destination : null;
        }

        /// <summary>
        /// The destination's visible pin under the world point, allowing `pad` world units around each pin's outline.
        /// Where pins overlap, the one drawn on top wins if the point is really on it; otherwise the nearest.
        /// </summary>
        private bool PinTarget(DestinationView dv, Vector3 world, float pad, out Target target)
        {
            target = default;
            if (!(PinAnimatorField?.GetValue(dv) is DestinationPinAnimatorView animator) || animator == null || animator.pins == null)
                return false;
            int best = -1;
            float bestDistance = pad;
            int bestOrder = int.MinValue;
            for (int i = 0; i < animator.pins.Count; i++)
            {
                DestinationPinView pin = animator.pins[i];
                if (pin == null || !pin.IsVisible || !pin.gameObject.activeInHierarchy || !TryGetPinShape(pin, out PinShape shape))
                    continue;
                float d = PinDistance(shape, world);
                if (d > pad)
                    continue;
                bool better = d <= 0f && bestDistance <= 0f ? shape.SortingOrder > bestOrder : d < bestDistance;
                if (best < 0 || better)
                {
                    best = i;
                    bestDistance = d;
                    bestOrder = shape.SortingOrder;
                }
            }
            if (best < 0)
                return false;
            target = new Target
            {
                Kind = TargetKind.Pin, Destination = dv, Pin = animator.pins[best], PinIndex = best, VisiblePins = animator.VisiblePinCount,
            };
            return true;
        }

        /// <summary>World units per car length at the screen point (car lengths are measured on screen), or 0.</summary>
        private static float WorldPerCar(GameCamera cam, Vector2 screenPos)
        {
            float carPx = CarLengthPixels(null, cam);
            Vector3 w = cam.GetWorldFromScreen(screenPos);
            float ppu = (cam.GetScreenFromWorld(w + Vector3.right) - cam.GetScreenFromWorld(w)).magnitude;
            return carPx > 0f && ppu > 1e-3f ? carPx / ppu : 0f;
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

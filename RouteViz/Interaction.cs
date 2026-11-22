using System.Collections.Generic;
using Motorways.Models;
using Motorways.Processes;
using Motorways.Views;
using UnityEngine;

namespace RouteViz
{
    /// <summary>Hover ring around the car under the mouse, click-to-toggle with sounds, and a flash on the toggled car.</summary>
    public partial class RouteVizPlugin
    {
        private const int OverlaySortingOrder = 7; // above the car body (6)
        private const int RingSegments = 40;
        private const float FlashSeconds = 0.75f;
        private const float FlashPeak = 1f;
        private static readonly Color RingColor = new Color(0f, 0f, 0f, 0.55f);

        private sealed class Flash
        {
            public VehicleView View;
            public VehicleModel Model;
            public float Start;
            public LineRenderer Disc;
        }

        private VehicleView _hover;
        private LineRenderer _hoverRing;
        private readonly List<Flash> _flashes = new List<Flash>();
        private Sounds _sounds;

        /// <summary>The car whose click circle contains the screen point (the nearest one if circles overlap).</summary>
        /// <summary>Click-circle radius in the car parent's local units (sized to the car, not the screen).</summary>
        private float RingRadius => (float)VehicleMovementProcess.VehicleLength * _clickRadius.Value;

        /// <summary>Outline thickness in local units, as a fraction of a car length.</summary>
        private static float OutlineWidth => (float)VehicleMovementProcess.VehicleLength * 0.08f;

        private VehicleView FindCarAt(Vector2 screenPos, out float distance, out int viewCount)
        {
            VehicleView best = null;
            distance = float.MaxValue;
            float bestRatio = 1f; // distance / that car's circle radius, on screen
            viewCount = 0;
            foreach (VehicleView view in EnumerateVehicleViews())
            {
                viewCount++;
                if (view.Model == null || !view.gameObject.activeInHierarchy)
                    continue;
                if (!(GameCameraField?.GetValue(view) is GameCamera cam))
                    continue;
                if (!TryGetPixelsPerUnit(view, out float ppu))
                    continue;
                float d = (cam.GetScreenFromWorld(view.transform.position) - screenPos).magnitude;
                float ratio = d / (RingRadius * ppu);
                if (ratio < bestRatio)
                {
                    bestRatio = ratio;
                    distance = d;
                    best = view;
                }
            }
            return best;
        }

        private void UpdateHover()
        {
            _hover = _routesVisible ? FindCarAt(Input.mousePosition, out _, out _) : null;
            UpdateBuildingHover(Input.mousePosition);
        }

        private void ToggleAt(Vector2 screenPos)
        {
            VehicleView best = FindCarAt(screenPos, out float distance, out int viewCount);
            if (_verboseLogging.Value)
                Logger.LogInfo($"Click at {screenPos}: {viewCount} vehicles, " +
                               (best == null ? "none in range." : $"hit vehicle {best.Model.id} ({distance:F1}px)."));

            // Not a car: maybe a pin or a building. Clicking empty space does nothing, so routes aren't cleared by accident.
            if (best == null)
            {
                ClickPinOrBuilding(screenPos);
                return;
            }
            ToggleCar(best);
        }

        private int FindSelection(VehicleView view)
        {
            return _selections.FindIndex(s => s.View == view && s.Model == view.Model && s.Id == view.Model.id);
        }

        private void AddSelection(VehicleView view)
        {
            int group = view.groupIndex;
            _selections.Add(new Selection { View = view, Model = view.Model, Id = view.Model.id, GroupIndex = group, Variant = NextVariant(group) });
        }

        /// <summary>Clicking a car: hide its route if shown, else show it (or buzz if it has no route).</summary>
        private void ToggleCar(VehicleView best)
        {
            int existing = FindSelection(best);
            if (existing >= 0)
            {
                Logger.LogInfo($"Hid route for vehicle {best.Model.id}.");
                RemoveSelectionAt(existing);
                PlayClick(Sounds.Kind.Dong);
                StartFlash(best);
                return;
            }

            if (!IsDriving(best.Model.behaviorState))
            {
                if (_verboseLogging.Value)
                    Logger.LogInfo($"Vehicle {best.Model.id} is {best.Model.behaviorState}: no route to show.");
                PlayClick(Sounds.Kind.Error);
                return;
            }

            _histories.TryGetValue(best.Model, out TripHistory h);
            AddSelection(best);
            Logger.LogInfo($"Showing route for vehicle {best.Model.id} ({best.Model.behaviorState}): " +
                           $"{best.Model.path.Count} lanes ahead, {(h != null ? h.Lanes.Count : 0)} driven. {_selections.Count} shown.");
            PlayClick(Sounds.Kind.Ding);
            StartFlash(best);
        }

        private void StartFlash(VehicleView view)
        {
            _flashes.Add(new Flash { View = view, Model = view.Model, Start = Time.unscaledTime });
        }

        /// <summary>Positions the hover ring and flashes on their cars. Runs in LateUpdate, after cars have moved.</summary>
        private void DrawOverlays()
        {
            // Hover ring.
            VehicleView hover = _hover;
            if (hover != null && _routesVisible && hover.Model != null && hover.gameObject.activeInHierarchy
                && TryGetPixelsPerUnit(hover, out float ppu))
            {
                Transform parent = hover.transform.parent;
                _hoverRing = EnsureLine(_hoverRing, parent);
                _hoverRing.numCapVertices = 3;
                float radius = RingRadius;
                float width = OutlineWidth; // bold, like the game's own outlines
                _hoverRing.startWidth = width;
                _hoverRing.endWidth = width;
                _hoverRing.startColor = RingColor;
                _hoverRing.endColor = RingColor;
                _hoverRing.sortingLayerID = CarSortingLayer(hover);
                _hoverRing.sortingOrder = OverlaySortingOrder;
                Vector3 c = hover.transform.localPosition;
                if (_pointBuffer.Length < RingSegments + 1)
                    _pointBuffer = new Vector3[Mathf.NextPowerOfTwo(RingSegments + 1)];
                for (int i = 0; i <= RingSegments; i++)
                {
                    float a = i * (2f * (float)System.Math.PI / RingSegments);
                    _pointBuffer[i] = new Vector3(c.x + Mathf.Cos(a) * radius, c.y + Mathf.Sin(a) * radius, LineZ);
                }
                _hoverRing.positionCount = RingSegments + 1;
                _hoverRing.SetPositions(_pointBuffer);
                _hoverRing.enabled = true;
            }
            else if (_hoverRing != null)
            {
                _hoverRing.enabled = false;
            }

            DrawBuildingOutline();

            // Flashes: a white disc over the car that pops in and fades out.
            for (int i = _flashes.Count - 1; i >= 0; i--)
            {
                Flash f = _flashes[i];
                float t = Time.unscaledTime - f.Start;
                if (t >= FlashSeconds || f.View == null || f.View.Model != f.Model || !f.View.gameObject.activeInHierarchy)
                {
                    ReleaseLine(ref f.Disc);
                    _flashes.RemoveAt(i);
                    continue;
                }

                // Pop in, hold, then fade: easy to spot even with several cars moving.
                const float popIn = 0.05f, hold = 0.2f;
                float alpha = t < popIn ? t / popIn : t < popIn + hold ? 1f : 1f - (t - popIn - hold) / (FlashSeconds - popIn - hold);
                alpha = Mathf.Clamp01(alpha) * FlashPeak;
                float diameter = (float)VehicleMovementProcess.VehicleLength * (1.3f + 0.5f * Mathf.Clamp01(t / FlashSeconds));
                Vector3 c = f.View.transform.localPosition;

                f.Disc = EnsureLine(f.Disc, f.View.transform.parent);
                f.Disc.numCapVertices = 12; // two nearly coincident points + round caps = a filled circle
                f.Disc.startWidth = diameter;
                f.Disc.endWidth = diameter;
                var color = new Color(1f, 1f, 1f, alpha);
                f.Disc.startColor = color;
                f.Disc.endColor = color;
                f.Disc.sortingLayerID = CarSortingLayer(f.View);
                f.Disc.sortingOrder = OverlaySortingOrder;
                _pointBuffer[0] = new Vector3(c.x - 0.001f, c.y, LineZ);
                _pointBuffer[1] = new Vector3(c.x + 0.001f, c.y, LineZ);
                f.Disc.positionCount = 2;
                f.Disc.SetPositions(_pointBuffer);
                f.Disc.enabled = true;
            }
        }

        /// <summary>Screen pixels per local unit of the car's parent space, at the car.</summary>
        private static bool TryGetPixelsPerUnit(VehicleView view, out float ppu)
        {
            ppu = 0f;
            if (!(GameCameraField?.GetValue(view) is GameCamera cam))
                return false;
            Transform t = view.transform;
            Vector3 local = t.localPosition;
            Vector3 worldA = t.position;
            Vector3 worldB = t.parent != null ? t.parent.TransformPoint(local + Vector3.right) : worldA + Vector3.right;
            ppu = (cam.GetScreenFromWorld(worldB) - cam.GetScreenFromWorld(worldA)).magnitude;
            return ppu > 1e-3f;
        }

        /// <summary>The sorting layer the car is drawn on right now ("Car" on the ground, "MotorwayCar" on bridges).</summary>
        private static int CarSortingLayer(VehicleView view)
        {
            if (TrailField?.GetValue(view) is VehicleTrailRenderer trail && trail != null && trail.Renderer != null)
                return trail.Renderer.sortingLayerID;
            return CarLayerId;
        }

        private void DestroyOverlays()
        {
            if (_hoverOutline != null)
                Destroy(_hoverOutline.gameObject);
            _hoverOutline = null;
            if (_hoverRing != null)
                Destroy(_hoverRing.gameObject);
            _hoverRing = null;
            foreach (Flash f in _flashes)
                if (f.Disc != null)
                    Destroy(f.Disc.gameObject);
            _flashes.Clear();
        }
    }
}

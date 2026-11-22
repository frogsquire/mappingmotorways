using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using Motorways;
using Motorways.Audio;
using Motorways.Models;
using Motorways.Processes;
using Motorways.Views;
using Server;
using UnityEngine;

namespace RouteViz
{
    /// <summary>
    /// Edge-based traffic heat overlay: each directed LaneModel is coloured by a slow EMA of
    /// (1 − speed/SpeedLimit) gated by occupancy. Cars waiting at intersections are ignored for speed;
    /// empty edges stay a faded cool green.
    /// </summary>
    public partial class RouteVizPlugin
    {
        private const int HeatSortingOrder = 1; // under routes (2–4) and cars
        /// <summary>EMA half-life in real seconds — slow so colours don't flicker as cars cross edges.</summary>
        private const float HeatEmaHalfLife = 2.2f;
        private const float HeatEmptyAlpha = 0.28f;
        private const float HeatFullAlpha = 0.82f;
        private static readonly Color HeatGreen = new Color(0.25f, 0.85f, 0.35f, 1f);
        private static readonly Color HeatYellow = new Color(0.95f, 0.85f, 0.15f, 1f);
        private static readonly Color HeatOrange = new Color(0.98f, 0.48f, 0.08f, 1f);
        private static readonly Color HeatRed = new Color(0.92f, 0.12f, 0.1f, 1f);

        private ConfigEntry<string> _heatmapKeyName;
        private KeyCode _heatmapKey = KeyCode.H;

        private bool _heatmapVisible;
        private readonly Dictionary<LaneModel, HeatEdge> _heatEdges = new Dictionary<LaneModel, HeatEdge>();
        private readonly List<LaneModel> _heatStale = new List<LaneModel>();
        private readonly List<RouteMesh> _heatMeshes = new List<RouteMesh>();
        private int _heatMeshesUsed;
        private readonly Stack<RouteMesh> _spareHeatMeshes = new Stack<RouteMesh>();
        private bool _loggedHeatError;
        private Transform _heatParent;
        private TilemapView _heatTilemap;

        private sealed class HeatEdge
        {
            public float Ema;
            public bool Seen;
        }

        private void ToggleHeatmap(bool playSound = true)
        {
            _heatmapVisible = !_heatmapVisible;
            if (!_heatmapVisible)
                HideHeatmap();
            if (playSound)
                PlayClick(_heatmapVisible ? Sounds.Kind.Ding : Sounds.Kind.Error);
            Logger.LogInfo(_heatmapVisible ? "Heatmap shown." : "Heatmap hidden.");
        }

        private void UpdateHeatmap()
        {
            if (!_heatmapVisible)
                return;
            if (!TryGetWorld(out _, out ISimulation simulation))
            {
                HideHeatmap();
                return;
            }

            EnsureHeatParent();
            if (_heatParent == null || _heatTilemap == null)
            {
                HideHeatmap();
                return;
            }

            foreach (HeatEdge edge in _heatEdges.Values)
                edge.Seen = false;

            float carLength = (float)VehicleMovementProcess.VehicleLength;
            float minGap = (float)VehicleMovementProcess.MinimumGap;
            float slot = Mathf.Max(1e-4f, carLength + minGap);
            float width = carLength * _widthInCarLengths.Value;
            float k = 1f - (float)Math.Exp(-Math.Log(2.0) * Time.unscaledDeltaTime / HeatEmaHalfLife);

            _heatMeshesUsed = 0;
            foreach (LaneModel lane in simulation.GetModels<LaneModel>())
            {
                if (lane == null || !IncludeHeatLane(lane))
                    continue;

                if (!_heatEdges.TryGetValue(lane, out HeatEdge edge))
                {
                    edge = new HeatEdge();
                    _heatEdges.Add(lane, edge);
                }
                edge.Seen = true;

                float instant = SampleLaneHeat(lane, slot);
                edge.Ema += (instant - edge.Ema) * k;

                DrawHeatLane(lane, HeatColor(edge.Ema), width);
            }

            _heatStale.Clear();
            foreach (KeyValuePair<LaneModel, HeatEdge> kv in _heatEdges)
                if (!kv.Value.Seen)
                    _heatStale.Add(kv.Key);
            foreach (LaneModel lane in _heatStale)
                _heatEdges.Remove(lane);

            ReleaseUnusedHeatMeshes();
        }

        private static bool IncludeHeatLane(LaneModel lane)
        {
            if (lane.isTemporary)
                return false;
            RoadType type = lane.connection.input.type;
            if (type == RoadType.Driveway || type == RoadType.Carpark || type == RoadType.ParkingSpace)
                return false;
            float length = (float)lane.Length;
            return length > 1e-3f;
        }

        /// <summary>
        /// Occupancy-gated congestion in [0, 1]. Speed samples skip cars waiting on a blocking intersection
        /// so a lone car at a red light does not light the edge red.
        /// </summary>
        private static float SampleLaneHeat(LaneModel lane, float slotLength)
        {
            List<VehicleModel> vehicles = lane.Vehicles;
            int count = vehicles != null ? vehicles.Count : 0;
            float length = (float)lane.Length;
            float capacity = Mathf.Max(1f, Mathf.Floor(length / slotLength));
            float occupancy = Mathf.Clamp01(count / capacity);
            if (count == 0 || occupancy <= 0f)
                return 0f;

            float speedLimit = (float)lane.SpeedLimit;
            if (speedLimit < 1e-4f)
                speedLimit = (float)VehicleMovementProcess.DefaultDesiredSpeed;

            float speedSum = 0f;
            int samples = 0;
            for (int i = 0; i < count; i++)
            {
                VehicleModel v = vehicles[i];
                if (v == null)
                    continue;
                VehicleModel.Frame frame = v.CurrentFrame;
                if (frame.nearestObstacle == VehicleModel.ObstacleType.BlockingIntersection)
                    continue;
                float speed = (float)frame.speed;
                speedSum += Mathf.Clamp01(speed / speedLimit);
                samples++;
            }

            // All cars waiting at the light → treat as free-flow for heat (occupancy gate still applies if we
            // had a speed signal; with none, stay cool).
            if (samples == 0)
                return 0f;

            float congestion = 1f - speedSum / samples;
            return Mathf.Clamp01(congestion * occupancy);
        }

        private static Color HeatColor(float heat)
        {
            heat = Mathf.Clamp01(heat);
            Color rgb;
            if (heat <= 0.33f)
                rgb = Color.Lerp(HeatGreen, HeatYellow, heat / 0.33f);
            else if (heat <= 0.66f)
                rgb = Color.Lerp(HeatYellow, HeatOrange, (heat - 0.33f) / 0.33f);
            else
                rgb = Color.Lerp(HeatOrange, HeatRed, (heat - 0.66f) / 0.34f);
            float a = Mathf.Lerp(HeatEmptyAlpha, HeatFullAlpha, heat);
            return new Color(rgb.r, rgb.g, rgb.b, a);
        }

        private void DrawHeatLane(LaneModel lane, Color color, float width)
        {
            _points.Clear();
            if (!GetLanePolyline(lane, _heatTilemap) || _lanePolyline.Count < 2)
                return;

            for (int i = 0; i < _lanePolyline.Count; i++)
            {
                Vector2 p = _lanePolyline[i];
                if (_points.Count > 0)
                {
                    Vector3 last = _points[_points.Count - 1];
                    if ((new Vector2(last.x, last.y) - p).sqrMagnitude < 1e-6f)
                        continue;
                }
                _points.Add(new Vector3(p.x, p.y, LineZ));
            }
            if (_points.Count < 2)
                return;

            bool onMotorway = _heatTilemap.TryGetMotorwayViewForLane(lane) != null;
            RouteMesh mesh = EnsureHeatMesh();
            mesh.BuildSolid(_points, color, width);
            mesh.Renderer.sortingLayerID = onMotorway ? MotorwayCarLayerId : CarLayerId;
            mesh.Renderer.sortingOrder = HeatSortingOrder;
            mesh.Object.SetActive(true);
        }

        private RouteMesh EnsureHeatMesh()
        {
            RouteMesh mesh = null;
            if (_heatMeshesUsed < _heatMeshes.Count)
                mesh = _heatMeshes[_heatMeshesUsed];
            while (mesh == null && _spareHeatMeshes.Count > 0)
            {
                mesh = _spareHeatMeshes.Pop();
                if (mesh.Object == null)
                    mesh = null;
            }
            if (mesh == null)
                mesh = new RouteMesh(GetMaterial(_heatParent));

            Transform t = mesh.Object.transform;
            if (t.parent != _heatParent)
            {
                t.SetParent(_heatParent, false);
                t.localPosition = Vector3.zero;
                t.localRotation = Quaternion.identity;
                t.localScale = Vector3.one;
            }

            if (_heatMeshesUsed < _heatMeshes.Count)
                _heatMeshes[_heatMeshesUsed] = mesh;
            else
                _heatMeshes.Add(mesh);
            _heatMeshesUsed++;
            return mesh;
        }

        private void ReleaseUnusedHeatMeshes()
        {
            for (int i = _heatMeshes.Count - 1; i >= _heatMeshesUsed; i--)
            {
                RouteMesh mesh = _heatMeshes[i];
                if (mesh != null && mesh.Object != null)
                {
                    mesh.Object.SetActive(false);
                    _spareHeatMeshes.Push(mesh);
                }
                _heatMeshes.RemoveAt(i);
            }
        }

        private void HideHeatmap()
        {
            _heatMeshesUsed = 0;
            ReleaseUnusedHeatMeshes();
        }

        private void EnsureHeatParent()
        {
            if (_heatParent != null && _heatTilemap != null)
                return;

            City city = Get.City;
            if (city != null && city.Scope != null)
            {
                try { _heatTilemap = city.Scope.Get<TilemapView>(); }
                catch { /* not registered */ }
            }

            foreach (VehicleView view in EnumerateVehicleViews())
            {
                if (_heatTilemap == null)
                    _heatTilemap = TilemapViewField?.GetValue(view) as TilemapView;
                if (view.transform.parent != null)
                {
                    _heatParent = view.transform.parent;
                    break;
                }
            }

            if (_heatParent == null && _heatTilemap != null)
                _heatParent = _heatTilemap.transform;
        }

        private void DestroyHeatmap()
        {
            HideHeatmap();
            while (_spareHeatMeshes.Count > 0)
                _spareHeatMeshes.Pop().Destroy();
            _heatEdges.Clear();
            _heatParent = null;
            _heatTilemap = null;
        }
    }
}

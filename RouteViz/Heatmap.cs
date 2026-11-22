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
    /// Traffic heat overlay on turn-to-turn road stretches: chains of straight LaneModels that share a
    /// travel heading. Each stretch is coloured by a slow EMA of speed-congestion gated by peak local
    /// packing along the stretch. Empty stretches stay a faded cool green.
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
        private readonly Dictionary<LaneModel, HeatStretch> _heatStretches = new Dictionary<LaneModel, HeatStretch>();
        private readonly List<LaneModel> _heatStale = new List<LaneModel>();
        private readonly List<LaneModel> _heatLaneScratch = new List<LaneModel>(256);
        private readonly HashSet<LaneModel> _heatVisited = new HashSet<LaneModel>();
        private readonly List<LaneModel> _heatSection = new List<LaneModel>(32);
        private readonly HashSet<VehicleModel> _heatVehicles = new HashSet<VehicleModel>();
        private readonly List<RouteMesh> _heatMeshes = new List<RouteMesh>();
        private int _heatMeshesUsed;
        private readonly Stack<RouteMesh> _spareHeatMeshes = new Stack<RouteMesh>();
        private bool _loggedHeatError;
        private Transform _heatParent;
        private TilemapView _heatTilemap;

        private sealed class HeatStretch
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

            foreach (HeatStretch stretch in _heatStretches.Values)
                stretch.Seen = false;

            float carLength = (float)VehicleMovementProcess.VehicleLength;
            float minGap = (float)VehicleMovementProcess.MinimumGap;
            float slot = Mathf.Max(1e-4f, carLength + minGap);
            float width = carLength * _widthInCarLengths.Value;
            float k = 1f - (float)Math.Exp(-Math.Log(2.0) * Time.unscaledDeltaTime / HeatEmaHalfLife);

            _heatLaneScratch.Clear();
            foreach (LaneModel lane in simulation.GetModels<LaneModel>())
            {
                if (lane != null && IncludeHeatLane(lane))
                    _heatLaneScratch.Add(lane);
            }

            _heatVisited.Clear();
            _heatMeshesUsed = 0;

            foreach (LaneModel seed in _heatLaneScratch)
            {
                if (_heatVisited.Contains(seed))
                    continue;

                BuildHeatSection(seed);
                if (_heatSection.Count == 0)
                    continue;

                LaneModel key = _heatSection[0];
                if (!_heatStretches.TryGetValue(key, out HeatStretch stretch))
                {
                    stretch = new HeatStretch();
                    _heatStretches.Add(key, stretch);
                }
                stretch.Seen = true;

                float instant = SampleSectionHeat(_heatSection, slot);
                stretch.Ema += (instant - stretch.Ema) * k;
                Color color = HeatColor(stretch.Ema);

                for (int i = 0; i < _heatSection.Count; i++)
                    DrawHeatLane(_heatSection[i], color, width);
            }

            _heatStale.Clear();
            foreach (KeyValuePair<LaneModel, HeatStretch> kv in _heatStretches)
                if (!kv.Value.Seen)
                    _heatStale.Add(kv.Key);
            foreach (LaneModel lane in _heatStale)
                _heatStretches.Remove(lane);

            ReleaseUnusedHeatMeshes();
        }

        /// <summary>
        /// Fills _heatSection with the turn-to-turn stretch containing seed: walk back to the unique
        /// straight predecessor, then forward while heading stays the same. Marked in _heatVisited.
        /// Non-straight lanes (turns, roundabout arcs) are singleton sections.
        /// </summary>
        private void BuildHeatSection(LaneModel seed)
        {
            _heatSection.Clear();
            if (!_heatVisited.Add(seed))
                return;

            if (!IsStraightHeatLane(seed))
            {
                _heatSection.Add(seed);
                return;
            }

            LaneModel start = seed;
            for (;;)
            {
                LaneModel pred = UniqueStraightNeighbour(start, inbound: true);
                if (pred == null || _heatVisited.Contains(pred))
                    break;
                start = pred;
            }

            LaneModel cur = start;
            while (cur != null)
            {
                if (cur != seed)
                    _heatVisited.Add(cur);
                _heatSection.Add(cur);
                LaneModel next = UniqueStraightNeighbour(cur, inbound: false);
                if (next == null || _heatVisited.Contains(next))
                    break;
                cur = next;
            }
        }

        private static bool IncludeHeatLane(LaneModel lane)
        {
            if (lane.isTemporary)
                return false;
            RoadType type = lane.connection.input.type;
            if (type == RoadType.Driveway || type == RoadType.Carpark || type == RoadType.ParkingSpace)
                return false;
            return (float)lane.Length > 1e-3f;
        }

        private static bool IsStraightHeatLane(LaneModel lane)
        {
            TileDirection a = lane.connection.input.direction;
            TileDirection b = lane.connection.output.direction;
            return a != TileDirection.None && b != TileDirection.None && Opposite(a, b);
        }

        private static bool Opposite(TileDirection a, TileDirection b)
        {
            int ia = (int)a, ib = (int)b;
            return ia >= 0 && ib >= 0 && ((ia + 4) & 7) == ib;
        }

        /// <summary>
        /// The unique straight neighbour that continues the same travel heading, or null if none / branching.
        /// </summary>
        private static LaneModel UniqueStraightNeighbour(LaneModel lane, bool inbound)
        {
            TileDirection heading = lane.connection.output.direction;
            List<LaneModel> neighbours = inbound ? lane.InboundLanes : lane.OutboundLanes;
            if (neighbours == null || neighbours.Count == 0)
                return null;

            LaneModel match = null;
            for (int i = 0; i < neighbours.Count; i++)
            {
                LaneModel n = neighbours[i];
                if (n == null || !IncludeHeatLane(n) || !IsStraightHeatLane(n))
                    continue;
                if (n.connection.output.direction != heading)
                    continue;
                if (match != null)
                    return null; // branch: end of stretch
                match = n;
            }
            return match;
        }

        /// <summary>
        /// Heat over a stretch: speed congestion (sensitive curve) gated by the peak local packing
        /// along any lane, not average occupancy over the whole length — so a queue on a long
        /// arterial still reads hot. A lone car at a light stays cool via the gate.
        /// </summary>
        private float SampleSectionHeat(List<LaneModel> section, float slotLength)
        {
            _heatVehicles.Clear();
            float peakOccupancy = 0f;
            for (int i = 0; i < section.Count; i++)
            {
                LaneModel lane = section[i];
                List<VehicleModel> vehicles = lane.Vehicles;
                int laneCount = 0;
                if (vehicles != null)
                {
                    for (int v = 0; v < vehicles.Count; v++)
                    {
                        VehicleModel car = vehicles[v];
                        if (car == null)
                            continue;
                        _heatVehicles.Add(car);
                        laneCount++;
                    }
                }
                float laneCap = Mathf.Max(1f, Mathf.Floor((float)lane.Length / slotLength));
                peakOccupancy = Mathf.Max(peakOccupancy, Mathf.Clamp01(laneCount / laneCap));
            }

            int count = _heatVehicles.Count;
            if (count == 0)
                return 0f;

            float speedSum = 0f;
            int samples = 0;
            foreach (VehicleModel v in _heatVehicles)
            {
                VehicleModel.Frame frame = v.CurrentFrame;
                float speedLimit = frame.lane != null ? (float)frame.lane.SpeedLimit : 0f;
                if (speedLimit < 1e-4f)
                    speedLimit = (float)VehicleMovementProcess.DefaultDesiredSpeed;

                // Waiting at a light = stopped. Peak packing keeps a lone waiter from going red.
                float speed = frame.nearestObstacle == VehicleModel.ObstacleType.BlockingIntersection
                    ? 0f
                    : (float)frame.speed;
                float ratio = Mathf.Clamp01(speed / speedLimit);
                speedSum += ratio;
                samples++;
            }

            if (samples == 0)
                return 0f;

            float avgRatio = speedSum / samples;
            // 1 - r^2: half speed ≈ 0.75 congestion (stricter than linear 1 - r).
            float congestion = 1f - avgRatio * avgRatio;
            // Full gate by ~40% peak packing; still pass ~50% of congestion when lightly occupied.
            float gate = Mathf.Clamp01(peakOccupancy / 0.4f);
            float heat = congestion * (0.5f + 0.5f * gate);
            return Mathf.Clamp01(heat);
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
            _heatStretches.Clear();
            _heatParent = null;
            _heatTilemap = null;
        }
    }
}

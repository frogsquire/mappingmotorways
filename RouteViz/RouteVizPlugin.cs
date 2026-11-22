using System;
using System.Collections.Generic;
using System.Reflection;
using BepInEx;
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
    /// Click cars to see their routes in the car's own colour: ahead of the car (its stored VehicleModel.path) and,
    /// grayed out, behind it (the lanes it has driven this trip, observed by this plugin). Stretches of lane shared by
    /// several shown routes pulse through each of those routes' colours. Click a selected car again to hide it.
    /// C clears every route; Alt+C hides/shows them all.
    /// Read-only: never calls the pathfinder and never writes simulation state.
    /// </summary>
    [BepInPlugin(Guid, Name, Version)]
    public partial class RouteVizPlugin : BaseUnityPlugin
    {
        public const string Guid = "motorways.routeviz";
        public const string Name = "RouteViz";
        public const string Version = "1.0.0";

        private const float LineZ = -6.05f; // cars sit at z = -6.1
        // Same sorting layer a car on that lane uses ("Car" on the ground, "MotorwayCar" on bridges), just below the
        // car's shadow (5) and body (6), so routes run under cars and ground routes pass under bridges like cars do.
        private const int BehindSortingOrder = 2;
        private const int AheadSortingOrder = 3;
        private const int OverlapSortingOrder = 4;
        private static readonly int CarLayerId = SortingLayer.NameToID("Car");
        private static readonly int MotorwayCarLayerId = SortingLayer.NameToID("MotorwayCar");
        private const float AheadAlpha = 0.95f;
        private const float BehindAlpha = 0.8f;
        private const float LaneEpsilon = 1e-3f;
        private static readonly Color MaskGray = new Color(0.62f, 0.62f, 0.62f, 1f);
        private static readonly Color FallbackCarColor = new Color(1f, 0.82f, 0.1f, 1f);
        private const int LookaheadLanes = 6;

        private static readonly FieldInfo TilemapViewField =
            typeof(VehicleView).GetField("_tilemapView", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo GameCameraField =
            typeof(VehicleView).GetField("_gameCamera", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo TrailField =
            typeof(VehicleView).GetField("_trail", BindingFlags.Instance | BindingFlags.NonPublic);

        private ConfigEntry<bool> _showBehind;
        private ConfigEntry<float> _behindGrayAmount;
        private ConfigEntry<float> _pulseSecondsPerColor;
        private ConfigEntry<float> _widthInCarLengths;
        private ConfigEntry<float> _arrowSpeed;
        private ConfigEntry<float> _clickRadius;
        private ConfigEntry<float> _dragTolerancePixels;
        private ConfigEntry<string> _toggleKeyName;
        private ConfigEntry<bool> _verboseLogging;
        private ConfigEntry<float> _soundVolume;
        private ConfigEntry<bool> _useGameSounds;
        private ConfigEntry<string> _dingSampleName;
        private ConfigEntry<string> _errorSampleName;

        private KeyCode _toggleKey;

        /// <summary>False while the overlay is toggled off; selections are kept and reappear when toggled back on.</summary>
        private bool _routesVisible = true;

        /// <summary>True after quit starts; skip Destroy — this build strips Application.isQuitting, so we watch Application.quitting.</summary>
        private bool _quitting;
        private Delegate _quittingHandler;

        /// <summary>One selected car and the lines showing its route.</summary>
        private sealed class Selection
        {
            public VehicleView View;
            public VehicleModel Model;
            public int Id;
            public int GroupIndex;
            /// <summary>Shade of the car's colour, so two selected cars of the same colour still differ.</summary>
            public int Variant;
            public readonly List<RouteMesh> Lines = new List<RouteMesh>();
            public int LinesUsed;
            public bool LoggedFirstDraw;

            // Rebuilt every frame by BuildPieces.
            public bool Active;
            public readonly List<Piece> Pieces = new List<Piece>(64);
            public Color AheadColor;
            public Color BehindColor;
            public Transform Parent;
            public TilemapView Tilemap;
            public Vector3 CarPoint;
        }

        /// <summary>Part of one lane on a selected car's route, in lane-model distance units.</summary>
        private struct Piece
        {
            public LaneModel Lane;
            public float From;
            public float To;
            public bool Behind;
            public bool CarAtStart;
            public bool CarAtEnd;
        }

        /// <summary>A shown route's claim on part of a lane, for finding where routes overlap.</summary>
        private struct Cover
        {
            public int Sel;
            public float From;
            public float To;
            public bool Behind;
        }

        private enum RunStyle { None, Ahead, Behind, Skip, Overlap }

        /// <summary>Lanes a car has driven on its current trip, as observed frame by frame.</summary>
        private sealed class TripHistory
        {
            public int Id;
            public VehicleModel.BehaviorState State;
            public readonly List<LaneModel> Lanes = new List<LaneModel>(32);
            public float FirstLaneStart;
            public LaneModel LastLane;
            /// <summary>True if this plugin saw the trip begin; otherwise its start is filled in by FillInTripStart.</summary>
            public bool ObservedStart;
            public bool PrefixTried;
            public readonly LaneModel[] Lookahead = new LaneModel[LookaheadLanes];
            public int LookaheadCount;
            public int LastSeenFrame;
        }

        private readonly List<Selection> _selections = new List<Selection>();
        private readonly Stack<LineRenderer> _spareLines = new Stack<LineRenderer>();
        private readonly Stack<RouteMesh> _spareMeshes = new Stack<RouteMesh>();
        private readonly Dictionary<VehicleModel, TripHistory> _histories = new Dictionary<VehicleModel, TripHistory>();
        private readonly List<VehicleModel> _staleHistories = new List<VehicleModel>();

        private Material _material;
        private readonly List<Vector3> _points = new List<Vector3>(256);
        private readonly List<float> _pointU = new List<float>(256); // arrow coordinate of each point (see AppendLane)
        private LaneModel _uLane;
        private int _uLaneArrows;
        private float _uOffset;
        private float _arrowSpacing;
        private readonly List<Vector2> _lanePolyline = new List<Vector2>(64);
        private readonly Dictionary<LaneModel, List<Cover>> _covers = new Dictionary<LaneModel, List<Cover>>();
        private readonly Stack<List<Cover>> _spareCoverLists = new Stack<List<Cover>>();
        private readonly List<float> _breaks = new List<float>(16);
        private readonly List<int> _runCodes = new List<int>(8);
        private readonly List<int> _subCodes = new List<int>(8);
        private readonly List<Color> _pulseColors = new List<Color>(8);
        private Vector3[] _pointBuffer = new Vector3[256];

        private Vector3 _mouseDownPosition;
        private bool _mouseDownTracked;

        private bool _loggedInputError;
        private bool _loggedDrawError;
        private bool _loggedHistoryError;
        private bool _loggedFirstUpdate;

        private void Awake()
        {
            _showBehind = Config.Bind("Display", "ShowBehind", true,
                "Also draw the route the car has driven so far this trip, as a grayed-out version of the car's colour.");
            _behindGrayAmount = Config.Bind("Display", "BehindGrayAmount", 0.6f,
                "How strongly the gray mask covers the car's colour on the driven part of a route (0 = none, 1 = plain gray).");
            _pulseSecondsPerColor = Config.Bind("Display", "PulseSecondsPerColor", 0.6f,
                "Where routes overlap, how long each car's colour is shown before blending into the next.");
            // Lanes sit 0.2 tiles either side of a road's middle; a car is about 0.3 tiles (0.46 car lengths) wide.
            _widthInCarLengths = Config.Bind("Display", "RouteWidth", 0.46f,
                "Route width as a fraction of one car length (0.46 = about as wide as a car).");
            _arrowSpeed = Config.Bind("Display", "ArrowSpeed", 1.2f,
                "How fast the arrows on a route move, in car lengths per second (real time). 0 = still.");
            _clickRadius = Config.Bind("Input", "ClickRadius", 0.8f,
                "How close (in screen pixels) a click must be to a car to select it.");
            _dragTolerancePixels = Config.Bind("Input", "DragTolerancePixels", 6f,
                "Mouse movement allowed between press and release for it to count as a click.");
            _toggleKeyName = Config.Bind("Input", "ToggleKey", "C",
                "Key that clears every route (a UnityEngine.KeyCode name). Alt + this key hides/shows all routes. None disables both.");
            _soundVolume = Config.Bind("Input", "SoundVolume", 0.5f,
                "Volume of the click sounds (ding on, dong off, buzz for a car with no route), 0 to 1. 0 mutes.");
            _useGameSounds = Config.Bind("Input", "UseGameSounds", true,
                "Play click sounds through the game's audio (follows its volume), using its own samples. Off = built-in tones.");
            _dingSampleName = Config.Bind("Input", "OnOffSample", "",
                "Game sample for selecting (pitched up) and deselecting (pitched down). Empty = automatic. VerboseLogging lists the names.");
            _errorSampleName = Config.Bind("Input", "ErrorSample", "",
                "Game sample for clicking a car with no route. Empty = automatic.");
            _verboseLogging = Config.Bind("Debug", "VerboseLogging", false, "Log selection details.");

            ParseSettings();
            _toggleKeyName.SettingChanged += (_, __) => ParseSettings();

            try
            {
                _sounds = new Sounds();
                _sounds.SetVolume(_soundVolume.Value);
                _soundVolume.SettingChanged += (_, __) => _sounds.SetVolume(_soundVolume.Value);
            }
            catch (Exception e)
            {
                Logger.LogWarning($"Click sounds unavailable: {e.Message}");
                _sounds = null;
            }

            RestoreStateAfterReload();
            WatchForQuit();

            if (TilemapViewField == null || GameCameraField == null || TrailField == null)
                Logger.LogError("Could not find VehicleView private fields; the game may have updated.");

            Logger.LogInfo($"{Name} {Version} loaded.");
        }

        /// <summary>
        /// Application.isQuitting is stripped; the quitting event is not. It fires before teardown, so we can
        /// skip Destroy and avoid "releasing ... targetTexture" on the UI Camera.
        /// </summary>
        private void WatchForQuit()
        {
            try
            {
                EventInfo ev = typeof(Application).GetEvent("quitting", BindingFlags.Static | BindingFlags.Public);
                if (ev == null)
                    return;
                _quittingHandler = Delegate.CreateDelegate(ev.EventHandlerType, this, nameof(HandleQuitting));
                ev.AddEventHandler(null, _quittingHandler);
            }
            catch (Exception e)
            {
                Logger.LogWarning($"Couldn't watch Application.quitting: {e.Message}");
            }
        }

        private void HandleQuitting()
        {
            _quitting = true;
            try
            {
                if (_routesButton != null)
                    _routesButton.SetActive(false);
                if (_clearButton != null)
                    _clearButton.SetActive(false);
            }
            catch { /* UI may already be gone */ }
        }

        private void OnApplicationQuit()
        {
            HandleQuitting();
        }

        /// <summary>Removes every line this plugin made, so unloading or hot-reloading leaves nothing behind.</summary>
        private void OnDestroy()
        {
            if (_quittingHandler != null)
            {
                try
                {
                    EventInfo ev = typeof(Application).GetEvent("quitting", BindingFlags.Static | BindingFlags.Public);
                    ev?.RemoveEventHandler(null, _quittingHandler);
                }
                catch { /* ignore */ }
                _quittingHandler = null;
            }

            try { _sounds?.Dispose(); }
            catch { /* ignore */ }

            // UI clones: never Destroy here (quit races the UI Camera). Hot reload uses DestroyLeftover on recreate.
            AbandonClearButton();
            AbandonRoutesButton();

            // Quitting: do not Destroy anything Unity-owned (UI Camera targetTexture errors). Hot reload only.
            if (_quitting)
                return;

            try
            {
                SaveStateForReload();
            }
            catch (Exception e)
            {
                Logger.LogWarning($"Couldn't save state for the next reload: {e.Message}");
            }

            try
            {
                DestroyOverlays();
                foreach (Selection s in _selections)
                    foreach (RouteMesh mesh in s.Lines)
                        mesh?.Destroy();
                _selections.Clear();
                while (_spareMeshes.Count > 0)
                    _spareMeshes.Pop().Destroy();
                while (_spareLines.Count > 0)
                {
                    LineRenderer line = _spareLines.Pop();
                    if (line != null)
                        Destroy(line.gameObject);
                }
                if (_material != null)
                    Destroy(_material);
            }
            catch (Exception e)
            {
                Logger.LogWarning($"Unload cleanup hit a Unity teardown race: {e.Message}");
            }
            Logger.LogInfo($"{Name} {Version} unloaded.");
        }

        private void ParseSettings()
        {
            try
            {
                _toggleKey = (KeyCode)Enum.Parse(typeof(KeyCode), _toggleKeyName.Value.Trim(), true);
            }
            catch (Exception)
            {
                Logger.LogWarning($"Unknown ToggleKey '{_toggleKeyName.Value}'; using C.");
                _toggleKey = KeyCode.C;
            }
        }

        private void Update()
        {
            if (!_loggedFirstUpdate)
            {
                _loggedFirstUpdate = true;
                Logger.LogInfo("Update loop running.");
            }

            try
            {
                UpdateRoutesButton();
            }
            catch (Exception e)
            {
                if (!_routesButtonFailed) { _routesButtonFailed = true; Logger.LogError($"Routes button failed: {e}"); }
            }
            try
            {
                UpdateClearButton();
            }
            catch (Exception e)
            {
                if (!_clearButtonFailed) { _clearButtonFailed = true; Logger.LogError($"Clear button failed: {e}"); }
            }

            try
            {
                HandleInput();
            }
            catch (Exception e)
            {
                if (!_loggedInputError)
                {
                    _loggedInputError = true;
                    Logger.LogError($"Input handling failed: {e}");
                }
            }
        }

        // LateUpdate so the simulation has stepped and car views have been positioned for this frame.
        private void LateUpdate()
        {
            try
            {
                RecordHistories();
            }
            catch (Exception e)
            {
                if (!_loggedHistoryError)
                {
                    _loggedHistoryError = true;
                    Logger.LogError($"Recording trip history failed: {e}");
                }
            }

            for (int i = _selections.Count - 1; i >= 0; i--)
            {
                Selection s = _selections[i];
                if (!SelectionStillValid(s))
                {
                    if (_verboseLogging.Value)
                        Logger.LogInfo($"Vehicle {s.Id} no longer valid (despawned or recycled); removing its route.");
                    RemoveSelectionAt(i);
                }
            }

            try
            {
                DrawOverlays();
            }
            catch (Exception e)
            {
                if (!_loggedDrawError)
                {
                    _loggedDrawError = true;
                    Logger.LogError($"Drawing hover/flash failed: {e}");
                }
            }

            if (!_routesVisible)
            {
                foreach (Selection s in _selections)
                    HideLines(s);
                return;
            }

            try
            {
                DrawAllRoutes();
            }
            catch (Exception e)
            {
                if (!_loggedDrawError)
                {
                    _loggedDrawError = true;
                    Logger.LogError($"Drawing route failed: {e}");
                }
            }
        }

        // ---------------------------------------------------------------- input

        private void HandleInput()
        {
            if (_toggleKey != KeyCode.None && Input.GetKeyDown(_toggleKey))
            {
                bool alt = Input.GetKey(KeyCode.LeftAlt) || Input.GetKey(KeyCode.RightAlt) || Input.GetKey(KeyCode.AltGr);
                if (!alt)
                {
                    ClearAllRoutes(false);
                }
                else
                {
                    ToggleRoutesVisible();
                }
            }

            // Over game UI (e.g. the routes button): not a map click, and no hover rings.
            if (PointerOverUI() || PointerOverClearButton())
            {
                _mouseDownTracked = false;
                _hover = null;
                return;
            }

            // While hidden (Alt+C), clicks don't select: you couldn't see what you'd toggled.
            if (!_routesVisible)
            {
                _mouseDownTracked = false;
                _hover = null;
                return;
            }

            UpdateHover();

            if (Input.GetMouseButtonDown(0))
            {
                _mouseDownPosition = Input.mousePosition;
                _mouseDownTracked = true;
            }

            if (Input.GetMouseButtonUp(0) && _mouseDownTracked)
            {
                _mouseDownTracked = false;
                if ((Input.mousePosition - _mouseDownPosition).magnitude <= _dragTolerancePixels.Value)
                    ToggleAt(Input.mousePosition);
            }
        }

        /// <summary>Lowest shade not used by another selected car of the same colour.</summary>
        private int NextVariant(int group)
        {
            for (int candidate = 0; ; candidate++)
            {
                bool used = false;
                foreach (Selection s in _selections)
                    if (s.GroupIndex == group && s.Variant == candidate) { used = true; break; }
                if (!used)
                    return candidate;
            }
        }

        /// <summary>
        /// Live vehicle views, via the game's own model→view registry (the same lookup its audio code uses).
        /// Object.FindObjectsOfType finds nothing in this build, so the scene can't be searched directly.
        /// </summary>
        private static IEnumerable<VehicleView> EnumerateVehicleViews()
        {
            if (!TryGetWorld(out ViewIndex viewIndex, out ISimulation simulation))
                yield break;

            foreach (VehicleModel model in simulation.GetModels<VehicleModel>())
            {
                VehicleView view = viewIndex.GetVehicleView(model);
                if (view != null)
                    yield return view;
            }
        }

        private static bool TryGetWorld(out ViewIndex viewIndex, out ISimulation simulation)
        {
            viewIndex = null;
            simulation = null;
            City city = Get.City;
            if (city == null || city.Scope == null)
                return false;
            viewIndex = city.Scope.Get<ViewIndex>();
            simulation = city.Scope.Get<ISimulation>();
            return viewIndex != null && simulation != null;
        }

        private static bool SelectionStillValid(Selection s)
        {
            // Views and models are pooled, so make sure this is still the same car.
            return s.View != null
                   && s.View.gameObject.activeInHierarchy
                   && s.View.Model != null
                   && s.View.Model == s.Model
                   && s.Model.id == s.Id;
        }

        private void RemoveSelectionAt(int index)
        {
            Selection s = _selections[index];
            _selections.RemoveAt(index);
            s.LinesUsed = 0;
            ReleaseUnusedLines(s);
        }

        /// <summary>Removes every route; returns how many there were.</summary>
        private int ClearSelections()
        {
            int count = _selections.Count;
            if (count > 0)
                Logger.LogInfo($"Cleared {count} route(s).");
            for (int i = _selections.Count - 1; i >= 0; i--)
                RemoveSelectionAt(i);
            return count;
        }

        // ---------------------------------------------------------------- trip history

        /// <summary>
        /// Notes, for every car, each lane it enters during the current trip. The game discards lanes once driven,
        /// so this is the only way to know the route behind a car without recomputing anything.
        /// </summary>
        private void RecordHistories()
        {
            if (!_showBehind.Value)
            {
                _histories.Clear();
                return;
            }
            if (!TryGetWorld(out _, out ISimulation simulation))
            {
                _histories.Clear();
                return;
            }

            int frame = Time.frameCount;
            foreach (VehicleModel m in simulation.GetModels<VehicleModel>())
            {
                if (!_histories.TryGetValue(m, out TripHistory h))
                {
                    h = new TripHistory { Id = m.id, State = m.behaviorState };
                    _histories.Add(m, h);
                }
                h.LastSeenFrame = frame;
                RecordHistory(m, h);
            }

            // Drop histories of cars that have left the simulation.
            if (frame % 120 == 0)
            {
                _staleHistories.Clear();
                foreach (KeyValuePair<VehicleModel, TripHistory> kv in _histories)
                    if (kv.Value.LastSeenFrame != frame)
                        _staleHistories.Add(kv.Key);
                foreach (VehicleModel m in _staleHistories)
                    _histories.Remove(m);
            }
        }

        private void RecordHistory(VehicleModel m, TripHistory h)
        {
            bool driving = IsDriving(m.behaviorState);

            // A new trip starts whenever the car enters a different leg (out or home), or the pooled model is reused.
            // Turning in to park continues the outbound leg, so the driven route survives it.
            bool recycled = h.Id != m.id;
            if (recycled || (driving && TripLeg(h.State) != TripLeg(m.behaviorState)))
            {
                // Seen begin only if we already knew this car before the change (not a recycled model).
                h.ObservedStart = !recycled;
                h.PrefixTried = false;
                h.Id = m.id;
                h.Lanes.Clear();
                h.LastLane = null;
                h.LookaheadCount = 0;
            }
            h.State = m.behaviorState;
            if (!driving)
                return;

            LaneModel current = m.CurrentFrame.lane;
            if (current == null)
                return;

            if (h.LastLane == null)
            {
                h.Lanes.Add(current);
                h.FirstLaneStart = (float)m.CurrentFrame.distanceAlongLane;
            }
            else if (current != h.LastLane)
            {
                // At high game speed a car can pass through short lanes (junctions) between two frames.
                // Fill those in from the path it had stored last frame.
                int found = -1;
                for (int i = 0; i < h.LookaheadCount; i++)
                    if (h.Lookahead[i] == current) { found = i; break; }
                for (int i = 0; i < found; i++)
                    if (h.Lookahead[i] != null && h.Lookahead[i] != h.LastLane)
                        h.Lanes.Add(h.Lookahead[i]);
                h.Lanes.Add(current);
            }
            h.LastLane = current;

            List<LaneModel> path = m.path;
            h.LookaheadCount = Math.Min(path.Count, LookaheadLanes);
            for (int i = 0; i < h.LookaheadCount; i++)
                h.Lookahead[i] = path[i];
        }

        /// <summary>
        /// Moving along lanes. ParkingAtDestination counts: the game switches to it once the path is down to its last
        /// lane or two, while the car is still driving in to its parking space.
        /// </summary>
        private static bool IsDriving(VehicleModel.BehaviorState state)
        {
            return TripLeg(state) != 0;
        }

        /// <summary>1 = heading to a destination (including parking there), 2 = heading home, 0 = not driving.</summary>
        private static int TripLeg(VehicleModel.BehaviorState state)
        {
            switch (state)
            {
                case VehicleModel.BehaviorState.DrivingToDestination:
                case VehicleModel.BehaviorState.ParkingAtDestination:
                    return 1;
                case VehicleModel.BehaviorState.DrivingHome:
                    return 2;
                default:
                    return 0;
            }
        }

        // ---------------------------------------------------------------- drawing

        private void DrawAllRoutes()
        {
            _arrowSpacing = (float)VehicleMovementProcess.VehicleLength * _widthInCarLengths.Value * 2.2f;
            foreach (Selection s in _selections)
                BuildPieces(s);

            IndexCovers();

            for (int k = 0; k < _selections.Count; k++)
            {
                Selection s = _selections[k];
                s.LinesUsed = 0;
                if (s.Active)
                    RenderSelection(s, k);
                ReleaseUnusedLines(s);

                if (_verboseLogging.Value && s.Active && !s.LoggedFirstDraw)
                {
                    s.LoggedFirstDraw = true;
                    Logger.LogInfo($"First draw for vehicle {s.Id}: {s.Pieces.Count} lane pieces in {s.LinesUsed} line(s), " +
                                   $"colour {s.AheadColor} (group {s.GroupIndex}, shade {s.Variant}).");
                }
            }
        }

        /// <summary>Works out which parts of which lanes a selected car's route covers this frame.</summary>
        private void BuildPieces(Selection s)
        {
            s.Pieces.Clear();
            VehicleModel m = s.Model;
            LaneModel currentLane = m.CurrentFrame.lane;

            // Parked or waiting: keep the selection but show nothing until the car drives again.
            s.Active = IsDriving(m.behaviorState) && currentLane != null;
            if (!s.Active)
                return;

            s.Tilemap = TilemapViewField?.GetValue(s.View) as TilemapView;
            s.Parent = s.View.transform.parent;
            Vector3 carLocal = s.View.transform.localPosition;
            s.CarPoint = new Vector3(carLocal.x, carLocal.y, LineZ);

            Color baseColor = Shade(GetCarColor(s.View), s.Variant);
            s.AheadColor = new Color(baseColor.r, baseColor.g, baseColor.b, AheadAlpha);
            Color masked = Color.Lerp(baseColor, MaskGray, Mathf.Clamp01(_behindGrayAmount.Value));
            s.BehindColor = new Color(masked.r, masked.g, masked.b, BehindAlpha);

            // Use the further-ahead frame to avoid a backwards kink at the car.
            float distOnLane = (float)m.CurrentFrame.distanceAlongLane;
            if (m.NextFrame.lane == currentLane)
                distOnLane = Mathf.Max(distOnLane, (float)m.NextFrame.distanceAlongLane);

            // Behind: lanes driven this trip, ending at the car.
            if (_showBehind.Value && _histories.TryGetValue(m, out TripHistory h) && h.Lanes.Count > 0)
            {
                if (!h.PrefixTried)
                {
                    try
                    {
                        FillInTripStart(m, h);
                    }
                    catch (Exception e)
                    {
                        Logger.LogWarning($"Couldn't fill in the start of vehicle {m.id}'s trip: {e.Message}");
                    }
                }
                List<LaneModel> lanes = h.Lanes;
                for (int i = 0; i < lanes.Count; i++)
                {
                    bool last = i == lanes.Count - 1;
                    bool isCurrent = last && lanes[i] == currentLane;
                    AddPiece(s, lanes[i], i == 0 ? h.FirstLaneStart : 0f, isCurrent ? distOnLane : float.MaxValue,
                             behind: true, carAtStart: false, carAtEnd: last);
                }
            }

            // Ahead: rest of the current lane, then every lane stored in path (path[0] is the next lane to enter).
            List<LaneModel> path = m.path;
            float endOnCurrent = path.Count == 0 ? (float)m.targetDistanceAlongLastLane : float.MaxValue;
            AddPiece(s, currentLane, distOnLane, endOnCurrent, behind: false, carAtStart: true, carAtEnd: false);
            for (int i = 0; i < path.Count; i++)
            {
                LaneModel lane = path[i];
                if (lane == null || lane == currentLane)
                    continue;
                float end = i == path.Count - 1 ? (float)m.targetDistanceAlongLastLane : float.MaxValue;
                AddPiece(s, lane, 0f, end, behind: false, carAtStart: false, carAtEnd: false);
            }
        }

        private static void AddPiece(Selection s, LaneModel lane, float from, float to, bool behind, bool carAtStart, bool carAtEnd)
        {
            float length = (float)lane.Length;
            from = Mathf.Max(0f, from);
            to = to >= float.MaxValue || length <= 0f ? Mathf.Max(length, from) : Mathf.Min(to, length);
            if (to < from)
                to = from;
            s.Pieces.Add(new Piece { Lane = lane, From = from, To = to, Behind = behind, CarAtStart = carAtStart, CarAtEnd = carAtEnd });
        }

        /// <summary>Indexes every shown route's pieces by lane, so overlaps can be found.</summary>
        private void IndexCovers()
        {
            foreach (List<Cover> list in _covers.Values)
            {
                list.Clear();
                _spareCoverLists.Push(list);
            }
            _covers.Clear();

            for (int k = 0; k < _selections.Count; k++)
            {
                Selection s = _selections[k];
                if (!s.Active)
                    continue;
                foreach (Piece p in s.Pieces)
                {
                    if (p.To - p.From <= LaneEpsilon)
                        continue;
                    if (!_covers.TryGetValue(p.Lane, out List<Cover> list))
                    {
                        list = _spareCoverLists.Count > 0 ? _spareCoverLists.Pop() : new List<Cover>(4);
                        _covers.Add(p.Lane, list);
                    }
                    list.Add(new Cover { Sel = k, From = p.From, To = p.To, Behind = p.Behind });
                }
            }
        }

        /// <summary>
        /// Draws one selected car's route as a series of lines. Each stretch is drawn in the car's own colour; or skipped
        /// when it overlaps an earlier-selected car's route (that car draws it); or, when this car is the first of several
        /// covering it, drawn pulsing through all of their colours.
        /// </summary>
        private void RenderSelection(Selection s, int k)
        {
            RunStyle runStyle = RunStyle.None;
            bool runOnMotorway = false;
            _runCodes.Clear();
            _points.Clear();
            _pointU.Clear();
            _uLane = null;
            _uOffset = 0f;

            foreach (Piece p in s.Pieces)
            {
                if (p.To - p.From <= LaneEpsilon)
                {
                    continue; // car right at a lane end
                }

                _covers.TryGetValue(p.Lane, out List<Cover> covers);
                bool onMotorway = s.Tilemap != null && s.Tilemap.TryGetMotorwayViewForLane(p.Lane) != null;

                // Split the piece wherever another route's claim on this lane starts or ends.
                _breaks.Clear();
                _breaks.Add(p.From);
                _breaks.Add(p.To);
                if (covers != null)
                    foreach (Cover c in covers)
                    {
                        if (c.Sel == k) continue;
                        if (c.From > p.From + LaneEpsilon && c.From < p.To - LaneEpsilon) _breaks.Add(c.From);
                        if (c.To > p.From + LaneEpsilon && c.To < p.To - LaneEpsilon) _breaks.Add(c.To);
                    }
                _breaks.Sort();

                for (int b = 0; b + 1 < _breaks.Count; b++)
                {
                    float a = _breaks[b], z = _breaks[b + 1];
                    if (z - a <= LaneEpsilon)
                        continue;

                    RunStyle style = ClassifyStretch(covers, k, p.Behind, (a + z) * 0.5f, _subCodes);
                    if (style != runStyle || (style != RunStyle.Skip && onMotorway != runOnMotorway)
                        || (style == RunStyle.Overlap && !SameCodes(_runCodes, _subCodes)))
                    {
                        FlushRun(s, runStyle, runOnMotorway);
                        runStyle = style;
                        runOnMotorway = onMotorway;
                        _runCodes.Clear();
                        _runCodes.AddRange(_subCodes);
                    }
                    if (style == RunStyle.Skip)
                        continue; // drawn by another route; arrows are pinned to lanes so they match

                    bool atStart = a <= p.From + LaneEpsilon;
                    bool atEnd = z >= p.To - LaneEpsilon;
                    AppendLane(p.Lane, s.Tilemap, a, atEnd && p.To >= (float)p.Lane.Length ? float.MaxValue : z);
                }
            }
            FlushRun(s, runStyle, runOnMotorway);
        }

        /// <summary>
        /// Who covers the point at distance `at` on this lane. For an overlap this route draws, fills `codes` with
        /// (selection * 2 + behind) for each covering route, in selection order.
        /// </summary>
        private static RunStyle ClassifyStretch(List<Cover> covers, int k, bool behind, float at, List<int> codes)
        {
            codes.Clear();
            int first = k;
            int others = 0;
            if (covers != null)
                foreach (Cover c in covers)
                    if (c.Sel != k && c.From <= at && c.To >= at)
                    {
                        others++;
                        if (c.Sel < first) first = c.Sel;
                    }

            if (others == 0)
                return behind ? RunStyle.Behind : RunStyle.Ahead;
            if (first != k)
                return RunStyle.Skip;

            // This route is the first of several here: list each covering route once, preferring its ahead part.
            codes.Add(k * 2 + (behind ? 1 : 0));
            foreach (Cover c in covers)
            {
                if (c.Sel == k || c.From > at || c.To < at)
                    continue;
                int existing = -1;
                for (int i = 0; i < codes.Count; i++)
                    if (codes[i] / 2 == c.Sel) { existing = i; break; }
                if (existing < 0)
                    codes.Add(c.Sel * 2 + (c.Behind ? 1 : 0));
                else if (!c.Behind)
                    codes[existing] = c.Sel * 2;
            }
            codes.Sort();
            return RunStyle.Overlap;
        }

        private static bool SameCodes(List<int> a, List<int> b)
        {
            if (a.Count != b.Count)
                return false;
            for (int i = 0; i < a.Count; i++)
                if (a[i] != b[i])
                    return false;
            return true;
        }

        /// <summary>Turns the points gathered for the current run into a line, then starts a new run.</summary>
        private void FlushRun(Selection s, RunStyle style, bool onMotorway)
        {
            if (style == RunStyle.None || style == RunStyle.Skip || _points.Count < 2)
            {
                // Keep a lone point so the next run joins on where this one ended.
                if (style == RunStyle.Skip || _points.Count > 1)
                {
                    _points.Clear();
                    _pointU.Clear();
                }
                return;
            }

            Color color;
            int order;
            switch (style)
            {
                case RunStyle.Behind:
                    color = s.BehindColor;
                    order = BehindSortingOrder;
                    break;
                case RunStyle.Overlap:
                    color = PulseColor(_runCodes);
                    order = OverlapSortingOrder;
                    break;
                default:
                    color = s.AheadColor;
                    order = AheadSortingOrder;
                    break;
            }

            if (s.LinesUsed >= s.Lines.Count)
                s.Lines.Add(null);
            RouteMesh mesh = s.Lines[s.LinesUsed];
            ShowPoints(ref mesh, s, color, onMotorway ? MotorwayCarLayerId : CarLayerId, order);
            s.Lines[s.LinesUsed] = mesh;
            s.LinesUsed++;

            // The next run starts where this one ended, so lines join up.
            Vector3 last = _points[_points.Count - 1];
            float lastU = _pointU[_pointU.Count - 1];
            _points.Clear();
            _pointU.Clear();
            _points.Add(last);
            _pointU.Add(lastU);
        }

        /// <summary>Cycles through the covering routes' colours, holding each and then blending into the next.</summary>
        private Color PulseColor(List<int> codes)
        {
            _pulseColors.Clear();
            foreach (int code in codes)
            {
                Selection o = _selections[code / 2];
                _pulseColors.Add((code & 1) == 1 ? o.BehindColor : o.AheadColor);
            }
            if (_pulseColors.Count == 1)
                return _pulseColors[0];

            float period = Mathf.Max(0.05f, _pulseSecondsPerColor.Value);
            float t = Time.unscaledTime / period; // unscaled: keeps pulsing while the game is paused
            float whole = Mathf.Floor(t);
            int i = (int)(whole % _pulseColors.Count);
            float blend = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(0.6f, 1f, t - whole));
            return Color.Lerp(_pulseColors[i], _pulseColors[(i + 1) % _pulseColors.Count], blend);
        }

        /// <summary>The car's own themed colour, read from its trail (which the game keeps in the house colour).</summary>
        private static Color GetCarColor(VehicleView view)
        {
            if (TrailField?.GetValue(view) is VehicleTrailRenderer trail && trail != null)
            {
                Color c = trail.Color;
                return new Color(c.r, c.g, c.b, 1f);
            }
            return FallbackCarColor;
        }

        /// <summary>Shade 0 is the car's colour; later shades alternate lighter and darker, further each time.</summary>
        private static Color Shade(Color c, int variant)
        {
            if (variant <= 0)
                return c;
            int step = (variant + 1) / 2;
            float amount = Mathf.Min(0.3f + 0.2f * (step - 1), 0.7f);
            return variant % 2 == 1 ? Color.Lerp(c, Color.white, amount) : Color.Lerp(c, Color.black, amount);
        }

        /// <summary>Builds the current _points into a route mesh (taking one from the pool if needed).</summary>
        private void ShowPoints(ref RouteMesh mesh, Selection s, Color color, int sortingLayerId, int sortingOrder)
        {
            mesh = EnsureMesh(mesh, s.Parent);
            float carLength = (float)VehicleMovementProcess.VehicleLength;
            float width = carLength * _widthInCarLengths.Value;
            // Arrows slide forward one spacing per cycle; every lane steps in sync.
            float cycle = Mathf.Repeat(Time.unscaledTime * _arrowSpeed.Value * carLength / Mathf.Max(1e-4f, _arrowSpacing), 1f);
            mesh.Build(_points, _pointU, color, width, cycle);
            mesh.Renderer.sortingLayerID = sortingLayerId;
            mesh.Renderer.sortingOrder = sortingOrder;
            mesh.Object.SetActive(true);
        }

        private RouteMesh EnsureMesh(RouteMesh mesh, Transform parent)
        {
            while (mesh == null && _spareMeshes.Count > 0)
            {
                mesh = _spareMeshes.Pop();
                if (mesh.Object == null) // destroyed along with a former parent
                    mesh = null;
            }
            if (mesh == null)
                mesh = new RouteMesh(GetMaterial(parent));

            Transform t = mesh.Object.transform;
            if (t.parent != parent)
            {
                t.SetParent(parent, false);
                t.localPosition = Vector3.zero;
                t.localRotation = Quaternion.identity;
                t.localScale = Vector3.one;
            }
            return mesh;
        }

        private static void HideLines(Selection s)
        {
            foreach (RouteMesh mesh in s.Lines)
                if (mesh != null && mesh.Object != null)
                    mesh.Object.SetActive(false);
        }

        /// <summary>Returns a selection's meshes beyond those used this frame to the spare pool.</summary>
        private void ReleaseUnusedLines(Selection s)
        {
            for (int i = s.Lines.Count - 1; i >= s.LinesUsed; i--)
            {
                RouteMesh mesh = s.Lines[i];
                if (mesh != null && mesh.Object != null)
                {
                    mesh.Object.SetActive(false);
                    _spareMeshes.Push(mesh);
                }
                s.Lines.RemoveAt(i);
            }
        }

        /// <summary>Appends the part of a lane between two distances (in lane-model units).</summary>
        private void AppendLane(LaneModel lane, TilemapView tilemap, float fromDist, float toDist)
        {
            if (!GetLanePolyline(lane, tilemap) || _lanePolyline.Count < 2)
                return;

            float polyLength = 0f;
            for (int i = 1; i < _lanePolyline.Count; i++)
                polyLength += Vector2.Distance(_lanePolyline[i - 1], _lanePolyline[i]);
            if (polyLength <= 0f)
                return;

            // Arrows are pinned to lanes: a whole number of them per lane, so any route over this lane draws the same
            // arrows and they run on seamlessly into the next lane. u counts arrows along the run; lane ends are whole.
            int arrowsHere = Mathf.Max(1, Mathf.RoundToInt(polyLength / Mathf.Max(1e-4f, _arrowSpacing)));
            float perArrow = polyLength / arrowsHere;
            if (lane != _uLane)
            {
                if (_uLane != null)
                    _uOffset += _uLaneArrows;
                _uLane = lane;
                _uLaneArrows = arrowsHere;
            }

            // Lane-model length and drawn-geometry length can differ slightly (motorways especially).
            float laneLength = (float)lane.Length;
            float scale = laneLength > 0f ? polyLength / laneLength : 1f;
            float from = Mathf.Clamp(fromDist * scale, 0f, polyLength);
            float to = toDist >= float.MaxValue ? polyLength : Mathf.Clamp(toDist * scale, 0f, polyLength);
            if (to <= from)
                return;

            float walked = 0f;
            bool started = false;
            for (int i = 1; i < _lanePolyline.Count; i++)
            {
                Vector2 a = _lanePolyline[i - 1];
                Vector2 b = _lanePolyline[i];
                float segLen = Vector2.Distance(a, b);
                float segStart = walked;
                float segEnd = walked + segLen;
                walked = segEnd;
                if (segLen <= 0f || segEnd < from)
                    continue;

                if (!started)
                {
                    AddPoint(Vector2.Lerp(a, b, (from - segStart) / segLen), _uOffset + from / perArrow);
                    started = true;
                }

                if (segEnd >= to)
                {
                    AddPoint(Vector2.Lerp(a, b, (to - segStart) / segLen), _uOffset + to / perArrow);
                    return;
                }

                AddPoint(b, _uOffset + segEnd / perArrow);
            }
        }

        private bool GetLanePolyline(LaneModel lane, TilemapView tilemap)
        {
            _lanePolyline.Clear();

            // Motorway (bridge) lanes are drawn from view-side geometry, as LaneCursor does.
            MotorwayView motorwayView = tilemap != null ? tilemap.TryGetMotorwayViewForLane(lane) : null;
            if (motorwayView != null)
            {
                List<Vector2> pts = motorwayView.GetLanePoints(lane);
                if (pts != null)
                    _lanePolyline.AddRange(pts);
                return _lanePolyline.Count > 0;
            }

            List<LineSegment> segments = lane.GetLineSegments();
            if (segments == null || segments.Count == 0)
                return false;
            _lanePolyline.Add(segments[0].Start);
            for (int i = 0; i < segments.Count; i++)
                _lanePolyline.Add(segments[i].End);
            return true;
        }

        private void AddPoint(Vector2 p, float u)
        {
            if (_points.Count > 0)
            {
                Vector3 last = _points[_points.Count - 1];
                if ((new Vector2(last.x, last.y) - p).sqrMagnitude < 1e-6f)
                    return;
            }
            _points.Add(new Vector3(p.x, p.y, LineZ));
            _pointU.Add(u);
        }

        // ---------------------------------------------------------------- line objects

        private LineRenderer EnsureLine(LineRenderer line, Transform parent)
        {
            while (line == null && _spareLines.Count > 0)
                line = _spareLines.Pop(); // skips any destroyed along with their parent
            if (line == null)
                line = CreateLine(parent);

            // Lane points live in the same local space as the car views' parent.
            Transform t = line.transform;
            if (t.parent != parent)
            {
                t.SetParent(parent, false);
                t.localPosition = Vector3.zero;
                t.localRotation = Quaternion.identity;
                t.localScale = Vector3.one;
            }
            return line;
        }

        private LineRenderer CreateLine(Transform parent)
        {
            var go = new GameObject("RouteViz_Line");
            var line = go.AddComponent<LineRenderer>();
            line.useWorldSpace = false;
            line.numCapVertices = 3;
            line.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            line.receiveShadows = false;
            line.sharedMaterial = GetMaterial(parent);
            return line;
        }

        private void ReleaseLine(ref LineRenderer line)
        {
            if (line == null)
                return;
            line.enabled = false;
            _spareLines.Push(line);
            line = null;
        }

        private Material GetMaterial(Transform carParent)
        {
            if (_material != null)
                return _material;

            string[] candidates =
            {
                "Universal Render Pipeline/2D/Sprite-Unlit-Default",
                "Sprites/Default",
                "Universal Render Pipeline/Unlit",
                "Unlit/Color",
            };
            foreach (string name in candidates)
            {
                Shader shader = Shader.Find(name);
                if (shader == null)
                    continue;
                _material = new Material(shader) { name = "RouteViz_Material" };
                Logger.LogInfo($"Using shader '{name}' for route lines.");
                return _material;
            }

            // Borrow from one of the game's own LineRenderers (road-drawing preview, car trails).
            foreach (LineRenderer existing in Resources.FindObjectsOfTypeAll<LineRenderer>())
            {
                if (existing == null || existing.sharedMaterial == null || existing.name == "RouteViz_Line")
                    continue;
                _material = new Material(existing.sharedMaterial) { name = "RouteViz_Material" };
                Logger.LogInfo($"Using material copied from game LineRenderer '{existing.name}' ({existing.sharedMaterial.shader.name}).");
                return _material;
            }

            Logger.LogWarning("No known shader found; falling back to a car's own material.");
            Renderer carRenderer = carParent != null ? carParent.GetComponentInChildren<Renderer>() : null;
            _material = carRenderer != null
                ? new Material(carRenderer.sharedMaterial) { name = "RouteViz_Material" }
                : new Material(Shader.Find("Hidden/Internal-Colored"));
            return _material;
        }
    }
}

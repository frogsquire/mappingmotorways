using System;
using System.Collections.Generic;
using Motorways;
using Motorways.Models;
using Motorways.Views;
using Server;
using UnityEngine;

namespace RouteViz
{
    /// <summary>
    /// Filling in the start of trips this plugin didn't watch begin, and keeping selections and trip histories across
    /// hot reloads.
    /// </summary>
    public partial class RouteVizPlugin
    {
        private const string SavedStateKey = "RouteViz.SavedState.v1";

        /// <summary>
        /// For a trip already under way when this plugin first saw the car (game just loaded, plugin just reloaded),
        /// prepends the lanes from the trip's origin to the first lane we saw it on. The game doesn't keep driven lanes,
        /// so this asks its pathfinder for the route from the origin (read-only queries) and keeps the part before the
        /// first observed lane. Matches what the car drove unless the roads changed since it set off.
        /// </summary>
        private void FillInTripStart(VehicleModel m, TripHistory h)
        {
            h.PrefixTried = true;
            if (h.ObservedStart || h.Lanes.Count == 0 || m.house == null)
                return;
            if (!TryGetWorld(out _, out ISimulation simulation))
                return;
            Pathfinder pathfinder = simulation.Scope.Get<Pathfinder>();
            if (pathfinder == null)
                return;

            LaneModel firstSeen = h.Lanes[0];
            LaneModel originLane = null;
            float originDist = 0f;
            List<LaneModel> route = null;

            if (TripLeg(m.behaviorState) == 1)
            {
                // Outbound: from the front of the house's driveway, where a car waits before it's dispatched.
                DestinationModel destination = m.destination;
                LaneModel driveway = m.house.DrivewayLane;
                if (destination == null || destination.Carpark == null || driveway == null)
                    return;
                originLane = driveway;
                originDist = (float)m.house.GetLaneDistanceAtFrontOfDriveway(driveway);
                route = firstSeen == driveway ? new List<LaneModel>() : pathfinder.CreatePath(driveway, destination.Carpark.entranceLanes, false);
            }
            else
            {
                // Home: from the car park it visited. Its parking space isn't recorded, so start at the car park entrance.
                DestinationModel visited = m.lastVisitedDestination;
                LaneModel driveway = m.house.DrivewayLane;
                if (visited == null || visited.Carpark == null || driveway == null)
                    return;
                foreach (LaneModel entrance in visited.Carpark.entranceLanes)
                {
                    if (entrance == firstSeen)
                    {
                        originLane = entrance;
                        route = new List<LaneModel>();
                        break;
                    }
                    List<LaneModel> candidate = pathfinder.CreatePath(entrance, driveway, false);
                    if (candidate != null && candidate.Contains(firstSeen))
                    {
                        originLane = entrance;
                        route = candidate;
                        break;
                    }
                }
            }

            if (originLane == null || route == null)
                return;

            // route excludes the origin lane; route[i] are the lanes after it.
            var prefix = new List<LaneModel>();
            if (firstSeen == originLane)
            {
                h.FirstLaneStart = Mathf.Min(h.FirstLaneStart, originDist);
            }
            else
            {
                int index = route.IndexOf(firstSeen);
                if (index < 0)
                {
                    if (_verboseLogging.Value)
                        Logger.LogInfo($"Vehicle {m.id}: couldn't match its trip start to a route from its origin; showing only what was seen.");
                    return;
                }
                prefix.Add(originLane);
                for (int i = 0; i < index; i++)
                    prefix.Add(route[i]);
                h.Lanes.InsertRange(0, prefix);
                h.FirstLaneStart = originDist;
            }

            if (_verboseLogging.Value)
                Logger.LogInfo($"Vehicle {m.id}: filled in {prefix.Count} lane(s) at the start of its trip.");
        }

        // ---------------------------------------------------------------- hot reload

        /// <summary>
        /// Stashes selections and trip histories in the AppDomain so the next copy of this plugin (after a hot reload)
        /// carries on. Only game types and primitives are stored: our own types differ between reloaded copies.
        /// </summary>
        private void SaveStateForReload()
        {
            var histories = new List<object[]>(_histories.Count);
            foreach (KeyValuePair<VehicleModel, TripHistory> kv in _histories)
            {
                TripHistory h = kv.Value;
                var lookahead = new LaneModel[h.LookaheadCount];
                Array.Copy(h.Lookahead, lookahead, h.LookaheadCount);
                histories.Add(new object[]
                {
                    kv.Key, h.Id, (int)h.State, h.Lanes.ToArray(), h.FirstLaneStart, h.LastLane, lookahead,
                    h.ObservedStart, h.PrefixTried, h.LastSeenFrame,
                });
            }

            var selections = new List<object[]>(_selections.Count);
            foreach (Selection s in _selections)
                selections.Add(new object[] { s.Model, s.Id, s.GroupIndex, s.Variant });

            AppDomain.CurrentDomain.SetData(SavedStateKey, new object[] { histories, selections, _routesVisible });
        }

        private void RestoreStateAfterReload()
        {
            if (!(AppDomain.CurrentDomain.GetData(SavedStateKey) is object[] saved))
                return;
            AppDomain.CurrentDomain.SetData(SavedStateKey, null);

            try
            {
                foreach (object[] e in (List<object[]>)saved[0])
                {
                    var h = new TripHistory
                    {
                        Id = (int)e[1],
                        State = (VehicleModel.BehaviorState)(int)e[2],
                        FirstLaneStart = (float)e[4],
                        LastLane = (LaneModel)e[5],
                        ObservedStart = (bool)e[7],
                        PrefixTried = (bool)e[8],
                        LastSeenFrame = (int)e[9],
                    };
                    h.Lanes.AddRange((LaneModel[])e[3]);
                    var lookahead = (LaneModel[])e[6];
                    h.LookaheadCount = Math.Min(lookahead.Length, LookaheadLanes);
                    Array.Copy(lookahead, h.Lookahead, h.LookaheadCount);
                    _histories[(VehicleModel)e[0]] = h;
                }

                int restored = 0;
                if (TryGetWorld(out ViewIndex viewIndex, out _))
                    foreach (object[] e in (List<object[]>)saved[1])
                    {
                        var model = (VehicleModel)e[0];
                        VehicleView view = viewIndex.GetVehicleView(model);
                        var s = new Selection { View = view, Model = model, Id = (int)e[1], GroupIndex = (int)e[2], Variant = (int)e[3] };
                        if (view != null && SelectionStillValid(s))
                        {
                            _selections.Add(s);
                            restored++;
                        }
                    }

                _routesVisible = (bool)saved[2];
                Logger.LogInfo($"Restored {restored} route(s) and {_histories.Count} trip histories from before the reload.");
            }
            catch (Exception e)
            {
                Logger.LogWarning($"Couldn't restore state from before the reload: {e.Message}");
                _histories.Clear();
                _selections.Clear();
            }
        }
    }
}

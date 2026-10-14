using System;
using System.Collections.Generic;
using System.Linq;

namespace MSFS2024AIPilot.Core;

/// <summary>A single routing constraint on a taxiway path.</summary>
public sealed class TaxiConstraints
{
    /// <summary>Runway the taxi route is for, used to allow crossing that runway.</summary>
    public string? RunwayName { get; set; }

    /// <summary>Hard hold-short: routing must stop before crossing this runway.</summary>
    public string? HoldShortBefore { get; set; }

    /// <summary>
    /// Weight of the aircraft being routed, used to avoid taxiway paths that publish
    /// a weight rating below it. Null disables weight checking entirely.
    /// </summary>
    public uint? AircraftWeightLbs { get; set; }

    /// <summary>Wingspan in meters; paths narrower than this are avoided.</summary>
    public double MinWidthMeters { get; set; }

    /// <summary>Runways that may be crossed when a hold-short applies.</summary>
    public HashSet<string> CrossableRunways { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>One leg of a verified taxi route.</summary>
public sealed class TaxiRouteLeg
{
    public int FromNode { get; set; }
    public int ToNode { get; set; }
    public double LengthMeters { get; set; }
    public TaxiPathType PathType { get; set; }
    public string? TaxiwayName { get; set; }
    public double BearingDegrees { get; set; }
    public bool CrossesRunway { get; set; }
}

public sealed class TaxiRoute
{
    private readonly List<(double lat, double lon)> _path = new();

    public List<TaxiRouteLeg> Legs { get; } = new();
    public bool Success { get; set; }
    public string FailureReason { get; set; } = "";

    public double TotalLengthMeters => Legs.Sum(l => l.LengthMeters);

    /// <summary>
    /// Every position the route passes through, start node first and goal node last. Only
    /// populated on success; the leg list alone carries node indices, which are meaningless
    /// without the airport they came from.
    /// </summary>
    public IReadOnlyList<(double lat, double lon)> Path => _path;

    internal void SetPath(IEnumerable<(double lat, double lon)> path) => _path.AddRange(path);

    public string Describe()
    {
        if (!Success) return $"Taxi route unavailable: {FailureReason}";
        return string.Join(" -> ", Legs.Select(l => l.TaxiwayName ?? $"N{l.ToNode}"));
    }
}

/// <summary>
/// Dijkstra shortest-path routing over the airport taxiway graph, plus an A* variant
/// that uses the straight-line distance as a heuristic for large airports.
/// </summary>
public static class TaxiRouter
{
    public static TaxiRoute FindRoute(AirportData airport, int startNode, int endNode, TaxiConstraints? constraints = null)
    {
        constraints ??= new TaxiConstraints();
        var route = new TaxiRoute();

        if (!airport.Nodes.TryGetValue(startNode, out var start) || !airport.Nodes.TryGetValue(endNode, out var goal))
        {
            route.Success = false;
            route.FailureReason = "start or end node is not in the airport network";
            return route;
        }

        if (startNode == endNode)
        {
            route.Success = true;
            route.SetPath(new[] { (start.Latitude, start.Longitude) });
            return route;
        }

        // A* with an admissible straight-line heuristic.
        var distances = new Dictionary<int, double> { [startNode] = 0 };
        var previous = new Dictionary<int, (int from, TaxiRouteLeg leg)>();
        var visited = new HashSet<int>();
        var frontier = new PriorityQueue<int, double>();
        frontier.Enqueue(startNode, 0);

        while (frontier.Count > 0)
        {
            if (!frontier.TryDequeue(out var current, out _)) break;
            if (!visited.Add(current)) continue;

            if (current == endNode) break;

            foreach (var edge in airport.Nodes[current].Edges)
            {
                if (visited.Contains(edge.To)) continue;
                if (!IsAllowed(edge, airport, constraints)) continue;

                var candidate = distances[current] + edge.LengthMeters;
                if (candidate >= distances.GetValueOrDefault(edge.To, double.MaxValue)) continue;

                distances[edge.To] = candidate;

                var bearing = Geo.BearingDegrees(
                    airport.Nodes[current].Latitude, airport.Nodes[current].Longitude,
                    airport.Nodes[edge.To].Latitude, airport.Nodes[edge.To].Longitude);

                previous[edge.To] = (current, new TaxiRouteLeg
                {
                    FromNode = current,
                    ToNode = edge.To,
                    LengthMeters = edge.LengthMeters,
                    PathType = edge.PathType,
                    TaxiwayName = airport.TaxiNames.Count > edge.NameIndex ? airport.TaxiNames[edge.NameIndex] : null,
                    BearingDegrees = bearing,
                    CrossesRunway = edge.PathType == TaxiPathType.Runway
                });

                var goalNode = airport.Nodes[endNode];
                var heuristic = Geo.DistanceMeters(
                    airport.Nodes[edge.To].Latitude, airport.Nodes[edge.To].Longitude,
                    goalNode.Latitude, goalNode.Longitude);
                frontier.Enqueue(edge.To, candidate + heuristic);
            }
        }

        if (!distances.ContainsKey(endNode))
        {
            route.Success = false;
            route.FailureReason = "no taxi route connects the start and end nodes";
            return route;
        }

        // Rebuild the path.
        var node = endNode;
        var reversed = new List<TaxiRouteLeg>();
        while (previous.TryGetValue(node, out var step))
        {
            reversed.Add(step.leg);
            node = step.from;
        }
        reversed.Reverse();
        route.Legs.AddRange(reversed);
        route.Success = true;

        // Positions in travel order: the start node, then every node the legs arrive at.
        var positions = new List<(double lat, double lon)> { (start.Latitude, start.Longitude) };
        foreach (var leg in reversed)
        {
            var legNode = airport.Nodes[leg.ToNode];
            positions.Add((legNode.Latitude, legNode.Longitude));
        }
        route.SetPath(positions);
        return route;
    }

    private static bool IsAllowed(GroundEdge edge, AirportData airport, TaxiConstraints constraints)
    {
        if (edge.PathType == TaxiPathType.Closed) return false;
        if (edge.WidthMeters > 0 && edge.WidthMeters < constraints.MinWidthMeters) return false;

        // A published weight rating only excludes aircraft heavier than the rating.
        uint aircraftWeight = constraints.AircraftWeightLbs ?? 0;
        if (aircraftWeight > 0)
        {
            uint edgeLimit = edge.WeightLimitLbs;
            if (edgeLimit > 0 && edgeLimit < aircraftWeight) return false;
        }

        // Runway edges are taxied along, so restrict them to the assigned runway...
        if (edge.PathType == TaxiPathType.Runway)
        {
            var edgeRunway = RunwayInfo.RunwayName(edge.RunwayNumber, edge.RunwayDesignator);

            // ...and nowhere near a runway the route must hold short of: crossing it
            // would take the aircraft onto a strip it has no clearance for. Only an
            // explicitly crossable runway is allowed through.
            if (!string.IsNullOrEmpty(constraints.HoldShortBefore) &&
                string.Equals(edgeRunway, constraints.HoldShortBefore, StringComparison.OrdinalIgnoreCase) &&
                !constraints.CrossableRunways.Contains(edgeRunway))
            {
                return false;
            }

            if (string.IsNullOrEmpty(constraints.RunwayName) ||
                !string.Equals(edgeRunway, constraints.RunwayName, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// A route that passes through a named chain of taxiways in order, which is what a
    /// controller issues and what the shortest-path route does not give.
    /// Returns a failed route when any leg of the chain cannot be joined.
    /// </summary>
    public static TaxiRoute FindRouteViaChain(
        AirportData airport, int startNode, int endNode, List<int> chain, TaxiConstraints? constraints = null)
    {
        var route = new TaxiRoute();
        var waypoints = new List<int> { startNode };
        waypoints.AddRange(chain.Where(index => index != startNode && airport.Nodes.ContainsKey(index)));
        if (endNode != startNode && endNode != waypoints[^1]) waypoints.Add(endNode);

        if (waypoints.Count < 2)
        {
            route.Success = false;
            route.FailureReason = "taxiway chain does not reach the destination";
            return route;
        }

        for (var i = 0; i < waypoints.Count - 1; i++)
        {
            var leg = FindRoute(airport, waypoints[i], waypoints[i + 1], constraints);
            if (!leg.Success)
            {
                route.Success = false;
                route.FailureReason = $"no taxi route through node {waypoints[i + 1]}: {leg.FailureReason}";
                return route;
            }

            // Each leg already starts at the previous waypoint, so only its own legs
            // are appended; otherwise the shared node would be crossed twice.
            route.Legs.AddRange(leg.Legs);
        }

        route.Success = true;
        route.SetPath(RebuildPositions(airport, route.Legs, waypoints[0]));
        return route;
    }

    /// <summary>Positions the route passes through, start node first.</summary>
    private static IEnumerable<(double lat, double lon)> RebuildPositions(
        AirportData airport, List<TaxiRouteLeg> legs, int startNode)
    {
        if (airport.Nodes.TryGetValue(startNode, out var start))
            yield return (start.Latitude, start.Longitude);

        foreach (var leg in legs)
        {
            if (airport.Nodes.TryGetValue(leg.ToNode, out var node))
                yield return (node.Latitude, node.Longitude);
        }
    }

    /// <summary>
    /// Resolves an ATC taxi route string such as "ALPHA BRAVO HOLD SHORT 25L" into an
    /// ordered chain of graph node indices that passes through each named taxiway.
    /// Returns null when the chain cannot be resolved.
    /// </summary>
    /// <param name="fromNode">
    /// The node the aircraft is standing on, when it is known. A taxiway name covers many
    /// nodes, so the first name is resolved from this one rather than from an arbitrary
    /// node of that taxiway.
    /// </param>
    public static List<int> ResolveTaxiwayChain(
        AirportData airport, List<string> taxiwayNames, int? fromNode = null)
    {
        var nameToNodes = new Dictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase);
        foreach (var node in airport.Nodes.Values)
        {
            foreach (var edge in node.Edges)
            {
                if (edge.NameIndex <= 0 || edge.NameIndex >= airport.TaxiNames.Count) continue;
                var name = airport.TaxiNames[edge.NameIndex];
                if (string.IsNullOrEmpty(name)) continue;

                if (!nameToNodes.TryGetValue(name, out var list))
                {
                    list = new List<int>();
                    nameToNodes[name] = list;
                }
                if (!list.Contains(node.Index)) list.Add(node.Index);
                if (!list.Contains(edge.To)) list.Add(edge.To);
            }
        }

        var chain = new List<int>();
        int? current = null;

        foreach (var requested in taxiwayNames)
        {
            if (!nameToNodes.TryGetValue(requested, out var candidates) || candidates.Count == 0)
                return new List<int>();

            // The next node is the one on that taxiway nearest where the chain currently
            // is. Facility item indices are ordering numbers with no geographic meaning, so
            // picking the candidate with the closest index number chose whichever BRAVO
            // happened to publish a low index - which at a large airport can be kilometres
            // away, and the caller then silently discards the whole named route.
            GroundNode? reference = current is not null
                ? airport.Nodes[current.Value]
                : fromNode is int from && airport.Nodes.TryGetValue(from, out var startNode)
                    ? startNode
                    : null;

            var next = reference is null
                ? candidates[0]
                : candidates.OrderBy(c => Geo.DistanceMeters(
                        reference.Latitude, reference.Longitude,
                        airport.Nodes[c].Latitude, airport.Nodes[c].Longitude))
                    .First();

            if (current is null || next != current)
            {
                if (!chain.Contains(next)) chain.Add(next);
            }
            current = next;
        }

        return chain;
    }

    /// <summary>Finds the network node closest to a geographic position.</summary>
    public static GroundNode? FindNearestNode(AirportData airport, double lat, double lon)
    {
        GroundNode? best = null;
        var bestDistance = double.MaxValue;

        foreach (var node in airport.Nodes.Values)
        {
            var d = Geo.DistanceMeters(lat, lon, node.Latitude, node.Longitude);
            if (d >= bestDistance) continue;
            bestDistance = d;
            best = node;
        }

        return best;
    }

    /// <summary>Finds the nearest parking spot / gate to a geographic position.</summary>
    public static GroundNode? FindNearestParking(AirportData airport, double lat, double lon)
    {
        GroundNode? best = null;
        var bestDistance = double.MaxValue;

        foreach (var node in airport.Nodes.Values)
        {
            if (node.Type != GroundNodeType.Parking) continue;
            var d = Geo.DistanceMeters(lat, lon, node.Latitude, node.Longitude);
            if (d >= bestDistance) continue;
            bestDistance = d;
            best = node;
        }

        return best;
    }

    /// <summary>
    /// Whether a taxiway path touching <paramref name="node"/> belongs to
    /// <paramref name="runwayName"/>. A taxi point payload carries no runway of its own -
    /// the runway association lives on the path - so the edge is the only evidence.
    /// </summary>
    public static bool HoldShortServesRunway(AirportData airport, GroundNode node, string runwayName)
    {
        if (string.IsNullOrWhiteSpace(runwayName)) return false;

        foreach (var edge in node.Edges)
        {
            if (edge.PathType != TaxiPathType.Runway) continue;
            var edgeRunway = RunwayInfo.RunwayName(edge.RunwayNumber, edge.RunwayDesignator);
            if (string.Equals(edgeRunway, runwayName, StringComparison.OrdinalIgnoreCase)) return true;
        }

        return false;
    }

    /// <summary>
    /// Every hold-short node that guards <paramref name="runwayName"/>, in index order.
    /// Preferring these over "the nearest hold-short to the runway centre" is what keeps a
    /// parallel runway's hold-short from being chosen at an airport with two close-spaced
    /// strips: the runway a hold-short guards is written on the taxiway paths attached to
    /// it, not on the point itself.
    /// </summary>
    public static List<GroundNode> HoldShortsFor(AirportData airport, string runwayName)
    {
        var result = new List<GroundNode>();
        var seen = new HashSet<int>();

        void Add(GroundNode node)
        {
            if (seen.Add(node.Index)) result.Add(node);
        }

        foreach (var node in airport.Nodes.Values
                     .Where(n => n.Type is GroundNodeType.HoldShort or GroundNodeType.IlsHoldShort
                                 && n.RunwayNumber.HasValue && n.RunwayNumber > 0))
        {
            if (RunwayInfo.RunwayName(node.RunwayNumber!.Value, node.RunwayDesignator)
                    .Equals(runwayName, StringComparison.OrdinalIgnoreCase))
            {
                Add(node);
            }
        }

        foreach (var node in airport.Nodes.Values
                     .Where(n => n.Type is GroundNodeType.HoldShort or GroundNodeType.IlsHoldShort))
        {
            if (HoldShortServesRunway(airport, node, runwayName)) Add(node);
        }

        return result;
    }

    /// <summary>
    /// Finds a hold-short for a runway at an airport - the node an aircraft must stop at
    /// before entering it - when the caller has no start position to route from, so the
    /// choice is by proximity to the runway. Only hold-shorts that actually guard this
    /// runway are considered: "the nearest hold-short to the runway centre" across all
    /// hold-shorts picks the other strip's hold-short at a parallel-runway airport,
    /// because a taxi point carries no runway of its own (see <see cref="HoldShortsFor"/>).
    /// A caller that has a position should use <see cref="FindReachableHoldShort"/>, which
    /// chooses by route length rather than proximity.
    /// </summary>
    public static GroundNode? FindRunwayHoldShort(AirportData airport, string runwayName)
    {
        var runway = airport.FindRunway(runwayName);
        if (runway is null) return null;

        return HoldShortsFor(airport, runwayName)
            .OrderBy(n => Geo.DistanceMeters(n.Latitude, n.Longitude, runway.Latitude, runway.Longitude))
            .FirstOrDefault();
    }

    /// <summary>
    /// The hold-short the aircraft can actually reach for a runway, together with the
    /// route that gets there: the shortest successful route to any hold-short guarding
    /// that runway. Returns null when none is reachable, which is the signal to fall back
    /// to a distance-based hold rather than to drive onto the runway.
    /// </summary>
    public static GroundNode? FindReachableHoldShort(
        AirportData airport, int startNode, string runwayName, TaxiConstraints? constraints = null)
    {
        constraints ??= new TaxiConstraints();
        constraints.RunwayName = runwayName;
        constraints.HoldShortBefore = runwayName;

        GroundNode? best = null;
        var bestLength = double.MaxValue;

        foreach (var candidate in HoldShortsFor(airport, runwayName))
        {
            if (candidate.Index == startNode) return candidate;

            var route = FindRoute(airport, startNode, candidate.Index, constraints);
            if (!route.Success) continue;
            if (route.TotalLengthMeters >= bestLength) continue;

            bestLength = route.TotalLengthMeters;
            best = candidate;
        }

        return best;
    }
}
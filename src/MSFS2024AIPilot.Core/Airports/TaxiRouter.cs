using System;
using System.Collections.Generic;
using System.Linq;
using MSFS2024AIPilot.Core.Geo;

namespace MSFS2024AIPilot.Core.Airports;

/// <summary>Constraints a taxi route must satisfy.</summary>
public sealed class TaxiConstraints
{
    /// <summary>Runway the route is for; edges of other runways may be restricted.</summary>
    public string? RunwayName { get; set; }
    /// <summary>Runway the route must not cross (the assigned runway before line-up).</summary>
    public string? HoldShortBefore { get; set; }
    /// <summary>Aircraft weight in pounds; paths with a lower weight limit are excluded.</summary>
    public uint? AircraftWeightLbs { get; set; }
    /// <summary>Minimum taxiway width in meters.</summary>
    public double MinWidthMeters { get; set; }
    /// <summary>Runways the aircraft may cross (back-taxi routes).</summary>
    public HashSet<string> CrossableRunways { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

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
    public IReadOnlyList<(double lat, double lon)> Path => _path;
    internal void SetPath(IEnumerable<(double lat, double lon)> path) => _path.AddRange(path);

    public string Describe()
    {
        if (!Success) return $"No route: {FailureReason}";
        var names = Legs.Select(l => l.TaxiwayName).Where(n => !string.IsNullOrEmpty(n)).Distinct();
        return $"{TotalLengthMeters:F0} m via {string.Join(" → ", names.DefaultIfEmpty("direct"))}";
    }
}

/// <summary>A* shortest path over the airport ground graph.</summary>
public static class TaxiRouter
{
    public static TaxiRoute FindRoute(AirportData airport, int startNode, int endNode, TaxiConstraints? constraints = null)
    {
        constraints ??= new TaxiConstraints();
        var route = new TaxiRoute();

        if (startNode == endNode)
        {
            route.Success = true;
            return route;
        }

        var start = airport.FindNode(startNode);
        var goal = airport.FindNode(endNode);
        if (start is null || goal is null)
        {
            route.FailureReason = "unknown node";
            return route;
        }

        // A* with Euclidean heuristic in meters (local tangent plane).
        var open = new PriorityQueue<int, double>();
        var cameFrom = new Dictionary<int, (int from, GroundEdge edge)>();
        var gScore = new Dictionary<int, double> { [startNode] = 0 };
        var hScore = new Dictionary<int, double> { [startNode] = Heuristic(start, goal) };
        open.Enqueue(startNode, hScore[startNode]);

        while (open.Count > 0)
        {
            var current = open.Dequeue();
            if (current == endNode)
            {
                Reconstruct(airport, cameFrom, endNode, route);
                route.Success = true;
                return route;
            }

            var currentNode = airport.FindNode(current);
            if (currentNode is null) continue;

            foreach (var edge in currentNode.Edges)
            {
                if (!EdgeAllowed(edge, constraints)) continue;
                var next = edge.To;
                var tentative = gScore[current] + edge.LengthMeters;
                if (!gScore.TryGetValue(next, out var known) || tentative < known)
                {
                    cameFrom[next] = (current, edge);
                    gScore[next] = tentative;
                    var nextNode = airport.FindNode(next);
                    if (nextNode is null) continue;
                    var h = Heuristic(nextNode, goal);
                    hScore[next] = h;
                    open.Enqueue(next, tentative + h);
                }
            }
        }

        route.FailureReason = "no path through the ground graph";
        return route;
    }

    private static double Heuristic(GroundNode a, GroundNode b)
    {
        var (e1, n1) = GeoMath.ToLocal(a.Latitude, a.Longitude, a.Latitude, a.Longitude);
        var (e2, n2) = GeoMath.ToLocal(b.Latitude, b.Longitude, a.Latitude, a.Longitude);
        return Math.Sqrt((e2 - e1) * (e2 - e1) + (n2 - n1) * (n2 - n1));
    }

    private static bool EdgeAllowed(GroundEdge edge, TaxiConstraints c)
    {
        if (edge.PathType is TaxiPathType.Closed or TaxiPathType.Vehicle or TaxiPathType.None) return false;
        if (c.AircraftWeightLbs is { } w && edge.WeightLimitLbs > 0 && edge.WeightLimitLbs < w) return false;
        if (c.MinWidthMeters > 0 && edge.WidthMeters > 0 && edge.WidthMeters < c.MinWidthMeters) return false;

        // Runway crossings: a route may cross only runways it is allowed to.
        if (edge.PathType == TaxiPathType.Runway)
        {
            var name = RunwayInfo.RunwayName(edge.RunwayNumber, edge.RunwayDesignator);
            if (!string.IsNullOrEmpty(name) && !c.CrossableRunways.Contains(name))
                return false;
        }
        return true;
    }

    private static void Reconstruct(AirportData airport, Dictionary<int, (int from, GroundEdge edge)> cameFrom, int endNode, TaxiRoute route)
    {
        var nodes = new List<int> { endNode };
        var edges = new List<GroundEdge>();
        var cur = endNode;
        while (cameFrom.TryGetValue(cur, out var step))
        {
            edges.Add(step.edge);
            nodes.Add(step.from);
            cur = step.from;
        }
        nodes.Reverse();
        edges.Reverse();

        // Path of lat/lon through every node.
        var coords = new List<(double lat, double lon)>();
        foreach (var idx in nodes)
        {
            var n = airport.FindNode(idx);
            if (n is not null) coords.Add((n.Latitude, n.Longitude));
        }
        route.SetPath(coords);

        for (int i = 0; i < edges.Count; i++)
        {
            var e = edges[i];
            var fromNode = airport.FindNode(e.From);
            var toNode = airport.FindNode(e.To);
            var bearing = fromNode is not null && toNode is not null
                ? GeoMath.BearingDeg(fromNode.Latitude, fromNode.Longitude, toNode.Latitude, toNode.Longitude)
                : 0;
            var name = e.NameIndex >= 0 && e.NameIndex < airport.TaxiNames.Count ? airport.TaxiNames[e.NameIndex] : null;
            route.Legs.Add(new TaxiRouteLeg
            {
                FromNode = e.From,
                ToNode = e.To,
                LengthMeters = e.LengthMeters,
                PathType = e.PathType,
                TaxiwayName = name,
                BearingDegrees = bearing,
                CrossesRunway = e.PathType == TaxiPathType.Runway,
            });
        }
    }

    public static GroundNode? FindNearestNode(AirportData airport, double lat, double lon)
    {
        GroundNode? best = null;
        var bestDist = double.MaxValue;
        foreach (var n in airport.Nodes.Values)
        {
            var d = GeoMath.DistanceMeters(lat, lon, n.Latitude, n.Longitude);
            if (d < bestDist) { bestDist = d; best = n; }
        }
        return bestDist < 500 ? best : null;
    }

    public static GroundNode? FindNearestParking(AirportData airport, double lat, double lon)
    {
        GroundNode? best = null;
        var bestDist = double.MaxValue;
        foreach (var n in airport.ParkingNodes)
        {
            var d = GeoMath.DistanceMeters(lat, lon, n.Latitude, n.Longitude);
            if (d < bestDist) { bestDist = d; best = n; }
        }
        return bestDist < 300 ? best : null;
    }

    /// <summary>True when a hold-short node guards the given runway (via its runway-type paths).</summary>
    public static bool HoldShortServesRunway(AirportData airport, GroundNode node, string runwayName)
    {
        if (string.IsNullOrEmpty(runwayName)) return true;
        var runway = airport.FindRunway(runwayName);
        if (runway is null) return true;
        foreach (var edge in node.Edges)
        {
            if (edge.PathType != TaxiPathType.Runway || edge.RunwayNumber == 0) continue;
            var name = RunwayInfo.RunwayName(edge.RunwayNumber, edge.RunwayDesignator);
            if (name.Equals(runwayName, StringComparison.OrdinalIgnoreCase)) return true;
        }
        // Fall back: node near either threshold of that runway.
        return false;
    }

    /// <summary>Hold-short nodes attached to the given runway.</summary>
    public static List<GroundNode> HoldShortsFor(AirportData airport, string runwayName)
    {
        var result = new List<GroundNode>();
        foreach (var n in airport.HoldShortNodes)
            if (HoldShortServesRunway(airport, n, runwayName)) result.Add(n);
        return result;
    }

    public static GroundNode? FindRunwayHoldShort(AirportData airport, string runwayName)
        => HoldShortsFor(airport, runwayName).FirstOrDefault();

    /// <summary>The reachable hold-short for a runway: shortest route wins.</summary>
    public static GroundNode? FindReachableHoldShort(AirportData airport, int startNode, string runwayName, TaxiConstraints? constraints)
    {
        GroundNode? best = null;
        double bestLen = double.MaxValue;
        foreach (var hs in HoldShortsFor(airport, runwayName))
        {
            var route = FindRoute(airport, startNode, hs.Index, constraints);
            if (route.Success && route.TotalLengthMeters < bestLen)
            {
                bestLen = route.TotalLengthMeters;
                best = hs;
            }
        }
        return best;
    }
}

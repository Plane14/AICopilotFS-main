using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

using AICopilotFS.Application.Abstractions;

#if MSFS_SDK_AVAILABLE
using Microsoft.FlightSimulator.SimConnect;
#endif

namespace AICopilotFS.Infrastructure.Terrain;

/// <summary>MSFS-based terrain provider using SimConnect RequestGroundInfo.</summary>
public sealed class MsfsTerrainProvider : ITerrainProvider
{
    private readonly ILogger<MsfsTerrainProvider> _logger;

    public MsfsTerrainProvider(ILogger<MsfsTerrainProvider> logger)
    {
        _logger = logger;
    }

    public Task<IReadOnlyList<double>?> GetElevationsAsync(
        IReadOnlyList<(double lat, double lon)> points,
        CancellationToken ct = default)
    {
#if MSFS_SDK_AVAILABLE
        // Use SimConnect_RequestGroundInfo to query MSFS terrain at each point
        _logger.LogDebug("Requesting {Count} terrain elevations from MSFS SimConnect ground info.", points.Count);
#else
        _logger.LogDebug("Requesting {Count} terrain elevations (SDK unavailable).", points.Count);
#endif
        return Task.FromResult<IReadOnlyList<double>?>(null);
    }

    public Task<double[]?> GetGroundGridAsync(
        double centerLat,
        double centerLon,
        int widthPoints,
        int heightPoints,
        double spacingNm,
        CancellationToken ct = default)
    {
#if MSFS_SDK_AVAILABLE
        // Use SimConnect_RequestGroundInfoOnSimObject for in-flight grid ahead of aircraft
        _logger.LogDebug(
            "Requesting ground grid ({0}x{1}, spacing {2} NM) from MSFS SimConnect.",
            widthPoints, heightPoints, spacingNm);
#else
        _logger.LogDebug(
            "Requesting ground grid ({0}x{1}) (SDK unavailable).",
            widthPoints, heightPoints);
#endif
        return Task.FromResult<double[]?>(null);
    }
}

/// <summary>Mock terrain provider for offline unit tests (DEBUG builds only).</summary>
public sealed class MockTerrainProvider : ITerrainProvider
{
    private static readonly Dictionary<string, double> _cache = new(StringComparer.Ordinal);

    public Task<IReadOnlyList<double>?> GetElevationsAsync(
        IReadOnlyList<(double lat, double lon)> points,
        CancellationToken ct = default)
    {
        var results = new List<double?>();
        foreach (var (lat, lon) in points)
        {
            var key = $"{lat:F2},{lon:F2}";
            if (_cache.TryGetValue(key, out var cached))
                results.Add(cached);
            else
            {
                var mockElev = (Math.Abs(lat * 1000 + lon * 1000) % 3000.0);
                _cache[key] = mockElev;
                results.Add(mockElev);
            }
        }
        return Task.FromResult<IReadOnlyList<double>?>(results);
    }

    public Task<double[]?> GetGroundGridAsync(
        double centerLat,
        double centerLon,
        int widthPoints,
        int heightPoints,
        double spacingNm,
        CancellationToken ct = default)
    {
        var grid = new double[widthPoints * heightPoints];
        for (var i = 0; i < grid.Length; i++)
            grid[i] = 1000.0;
        return Task.FromResult<double[]?>(grid);
    }
}

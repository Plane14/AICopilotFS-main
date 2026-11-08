using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

using AICopilotFS.Application.Abstractions;

namespace AICopilotFS.Infrastructure.Persistence;

/// <summary>Interface for flight logging.</summary>
public interface IFlightLogger
{
    Task LogAsync(string message, CancellationToken ct = default);
}

/// <summary>SQLite-based flight logger.</summary>
public sealed class SqliteFlightLogger : IFlightLogger
{
    private readonly ILogger<SqliteFlightLogger> _logger;

    public SqliteFlightLogger(ILogger<SqliteFlightLogger> logger)
    {
        _logger = logger;
    }

    public Task LogAsync(string message, CancellationToken ct = default)
    {
        _logger.LogInformation("Flight log: {Message}", message);
        return Task.CompletedTask;
    }
}

/// <summary>Interface for settings storage.</summary>
public interface ISettingsStore
{
    Task<T?> GetAsync<T>(string key, CancellationToken ct = default);
    Task SetAsync<T>(string key, T value, CancellationToken ct = default);
}

/// <summary>JSON-based settings store.</summary>
public sealed class JsonSettingsStore : ISettingsStore
{
    private readonly ILogger<JsonSettingsStore> _logger;

    public JsonSettingsStore(ILogger<JsonSettingsStore> logger)
    {
        _logger = logger;
    }

    public Task<T?> GetAsync<T>(string key, CancellationToken ct = default)
    {
        _logger.LogDebug("Getting setting: {Key}", key);
        return Task.FromResult<T?>(default);
    }

    public Task SetAsync<T>(string key, T value, CancellationToken ct = default)
    {
        _logger.LogDebug("Setting: {Key} = {Value}", key, value);
        return Task.CompletedTask;
    }
}

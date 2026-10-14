using System;
using System.Collections.Generic;

namespace MSFS2024AIPilot.Core;

/// <summary>
/// Bridges <see cref="ChecklistExecutor"/> to live SimConnect: fires the
/// checkpoint events and streams the simvars a checkpoint's tests read.
/// Each distinct simvar is requested once at visual frame rate and shared by
/// every checkpoint that reads it, so a long checklist does not accumulate one
/// SimConnect request per item. The executor only ever has one checkpoint
/// subscribed at a time, so single-ownership per feed is safe.
/// </summary>
public sealed class ChecklistSimHost : IChecklistHost, IDisposable
{
    private readonly SimConnectManager _sc;
    private readonly Dictionary<string, SimVarFeed> _feeds = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<uint, SimVarFeed> _byRequest = new();
    private readonly Dictionary<uint, Subscription> _subscriptions = new();
    private uint _nextHandle = 1;
    private bool _disposed;

    private sealed class SimVarFeed
    {
        public string Name = "";
        public string Units = "number";
        public uint RequestId;
        public Subscription? Owner;
        public double Value;
    }

    private sealed class Subscription
    {
        public uint Handle;
        public string[] Names = Array.Empty<string>();
        public double[] Values = Array.Empty<double>();
        public Action<double[]>? Callback;
        public readonly List<SimVarFeed> Feeds = new();
        public bool Active = true;
    }

    public ChecklistSimHost(SimConnectManager simConnect)
    {
        _sc = simConnect;
        _sc.OnConnected += OnSimConnected;
        if (_sc.IsConnected) Attach();
    }

    private void OnSimConnected() => Attach();

    private void Attach()
    {
        _sc.OnDataReceived -= OnDataReceived;
        _sc.OnDataReceived += OnDataReceived;
        // Requests created before the connection existed can only be started now.
        foreach (var feed in _feeds.Values)
        {
            if (feed.RequestId != 0) continue;
            StartFeed(feed);
        }
    }

    /// <summary>
    /// Registers a one-datum definition for the simvar and starts its request. The
    /// datum must be added to a definition that already exists: SimConnect assembles
    /// a definition from repeated SimConnect_AddToDataDefinition calls sharing one ID.
    /// </summary>
    private void StartFeed(SimVarFeed feed)
    {
        var defineId = _sc.CreateDataDefinition();
        _sc.AddToDataDefinition(defineId, feed.Name, feed.Units);
        feed.RequestId = _sc.RequestData(defineId);
        _byRequest[feed.RequestId] = feed;
    }

    public void FireEvent(string eventName, double data)
    {
        if (!_sc.IsConnected) return;
        // Event payloads are unsigned 32-bit; lever commands arrive on the
        // 0..16383 percent scale and must survive the round-trip unchanged.
        _sc.FireEvent(eventName, (uint)Math.Clamp(data, 0, uint.MaxValue));
    }

    public uint Subscribe(string[] names, string[] units, Action<double[]> onData)
    {
        var sub = new Subscription
        {
            Handle = _nextHandle,
            Names = names,
            Values = new double[names.Length],
            Callback = onData
        };
        _subscriptions[sub.Handle] = sub;

        for (int i = 0; i < names.Length; i++)
        {
            var key = names[i];
            if (!_feeds.TryGetValue(key, out var feed))
            {
                feed = new SimVarFeed { Name = names[i], Units = units[i] };
                if (_sc.IsConnected) StartFeed(feed);
                _feeds[key] = feed;
            }
            feed.Owner = sub;
            sub.Feeds.Add(feed);
        }
        return _nextHandle++;
    }

    /// <summary>
    /// Releases a subscription's feeds. The handle was advisory before and the subscription
    /// was ignored, so a feed stayed live for the session: SimConnect allows only a limited
    /// number of outstanding requests per client, and the flight would eventually be
    /// refused its own simvar requests.
    /// </summary>
    public void Unsubscribe(uint handle)
    {
        if (!_subscriptions.Remove(handle, out var sub)) return;

        sub.Active = false;
        sub.Callback = null;
        foreach (var feed in sub.Feeds)
        {
            if (!ReferenceEquals(feed.Owner, sub)) continue;
            feed.Owner = null;
            if (!_subscriptions.Values.Any(s => s.Feeds.Contains(feed))) _feeds.Remove(feed.Name);
        }
        sub.Feeds.Clear();
    }

    private void OnDataReceived(SimObjectData data)
    {
        if (!_byRequest.TryGetValue(data.RequestId, out var feed)) return;
        var owner = feed.Owner;
        if (owner == null || !owner.Active || owner.Callback == null) return;

        var raw = data.Values;
        if (raw.Length > 0 && raw[0] is double d) feed.Value = d;

        for (int i = 0; i < owner.Feeds.Count && i < owner.Values.Length; i++)
            owner.Values[i] = owner.Feeds[i].Value;

        try { owner.Callback(owner.Values); }
        catch { /* a failing consumer must not kill the dispatch loop */ }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var feed in _feeds.Values) feed.Owner = null;
        _feeds.Clear();
        _byRequest.Clear();
        _sc.OnConnected -= OnSimConnected;
        _sc.OnDataReceived -= OnDataReceived;
        GC.SuppressFinalize(this);
    }
}

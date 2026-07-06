using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;

namespace PCStatsMonitor.Core;

/// <summary>
/// The single background thread that owns all sensor providers, orchestrates polling
/// according to CadencePolicy, builds immutable SensorSnapshots, and publishes them
/// via a volatile reference (lock-free reads from UI thread).
///
/// Thread model:
///   - One dedicated Thread (BelowNormal, IsBackground).
///   - LHM provider and any non-thread-safe providers run serially on this thread.
///   - Thread-safe providers (NVML, sysfs) are dispatched via Parallel.ForEach.
///   - Publish path: Volatile.Write(ref _current, snap) then Action<SensorSnapshot> callback
///     which the caller marshals to the UI thread.
/// </summary>
public sealed class SensorPump : IDisposable
{
    private readonly ILogger<SensorPump> _log;
    private readonly CadencePolicy _cadence;
    private readonly ISensorProvider[] _providers;
    private readonly ISensorProvider[] _threadSafeProviders;
    private readonly ISensorProvider[] _serialProviders;

    private static readonly MetricKind[] AllMetricKinds = Enum.GetValues<MetricKind>();

    // Exponential moving average for temperature metrics — smooths sensor noise.
    // alpha=0.3: new reading gets 30% weight, old average gets 70%.
    private const double EmaAlpha = 0.3;
    private static readonly MetricKind[] TempKinds =
    [
        MetricKind.CpuTempPackage,
        MetricKind.GpuTempCore,
        MetricKind.StorageTempC,
    ];
    private readonly Dictionary<MetricKind, double> _emaTemp = new();

    private SensorSnapshot _current = SensorSnapshot.Empty;
    private volatile bool _windowVisible = true;
    private bool _wasPaused; // pump-thread only
    private volatile bool _disposed;

    private readonly Thread _thread;
    private readonly CancellationTokenSource _cts = new();

    // Hardware identities, resolved once at startup
    private HardwareIdentity _cpuId = HardwareIdentity.Unknown;
    private HardwareIdentity _gpuId = HardwareIdentity.Unknown;
    private HardwareIdentity _memId = HardwareIdentity.Unknown;
    private HardwareIdentity _stoId = HardwareIdentity.Unknown;

    /// <summary>
    /// Fired on the pump thread after each snapshot is published. Caller is responsible
    /// for marshaling to the UI thread (e.g., Dispatcher.UIThread.Post).
    /// </summary>
    public event Action<SensorSnapshot>? SnapshotPublished;

    /// <summary>Read the latest snapshot from any thread — lock-free.</summary>
    public SensorSnapshot Current
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => Volatile.Read(ref _current);
    }

    /// <summary>
    /// Set to false when the window is hidden/minimized to pause polling entirely;
    /// set back to true to resume at normal cadence on the next tick.
    /// </summary>
    public bool WindowVisible
    {
        set => _windowVisible = value;
    }

    public SensorPump(IEnumerable<ISensorProvider> providers, CadencePolicy cadence, ILogger<SensorPump> log)
    {
        _log = log;
        _cadence = cadence;
        _providers = providers.ToArray();
        _threadSafeProviders = _providers.Where(p => p.Capabilities.IsThreadSafe).ToArray();
        _serialProviders = _providers.Where(p => !p.Capabilities.IsThreadSafe).ToArray();

        _thread = new Thread(PumpLoop)
        {
            Name = "SensorPump",
            IsBackground = true,
            Priority = ThreadPriority.BelowNormal,
        };
    }

    public async Task StartAsync(CancellationToken ct = default)
    {
        // Initialize providers, disable those that fail
        var active = new List<ISensorProvider>(_providers.Length);
        foreach (var p in _providers)
        {
            try
            {
                await p.InitializeAsync(ct);
                active.Add(p);
                _log.LogInformation("Provider {Name} initialized with {Count} metrics",
                    p.Capabilities.Name, p.Capabilities.SupportedMetrics.Count);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Provider {Name} failed to initialize — skipping", p.Capabilities.Name);
            }
        }

        // Resolve hardware identities from all initialized providers
        ResolveIdentities(active);

        _thread.Start();
    }

    private void ResolveIdentities(IList<ISensorProvider> active)
    {
        foreach (var p in active)
        {
            _cpuId = p.ResolveIdentity("CPU") ?? _cpuId;
            _gpuId = p.ResolveIdentity("GPU") ?? _gpuId;
            _memId = p.ResolveIdentity("Memory") ?? _memId;
            _stoId = p.ResolveIdentity("Storage") ?? _stoId;
        }
        _log.LogInformation("Hardware: CPU={Cpu} GPU={Gpu} MEM={Mem} STO={Sto}",
            _cpuId, _gpuId, _memId, _stoId);
    }

    private void PumpLoop()
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        long tick = 0;

        while (!_disposed && !_cts.Token.IsCancellationRequested)
        {
            try
            {
                // Block until next tick — cheap, kernel-managed
                if (!timer.WaitForNextTickAsync(_cts.Token).AsTask().GetAwaiter().GetResult())
                    break;

                tick++;
                bool visible = _windowVisible;

                // Window hidden or minimized — full pause: no provider refresh, no snapshot,
                // no allocations. The kernel timer wait is the only work until shown again.
                if (!visible)
                {
                    _wasPaused = true;
                    continue;
                }

                // Fresh temps on resume — EMA seeded from pre-pause values would lag reality
                if (_wasPaused)
                {
                    _wasPaused = false;
                    _emaTemp.Clear();
                }

                // Determine which groups are due this tick
                var dueGroups = new HashSet<MetricGroup>(_cadence.GroupsDueOnTick(tick));
                if (dueGroups.Count == 0) continue;

                // Refresh thread-safe providers in parallel
                if (_threadSafeProviders.Length > 0)
                {
                    Parallel.ForEach(_threadSafeProviders, p =>
                    {
                        try { p.Refresh(MetricGroup.Fast); }
                        catch (Exception ex)
                        {
                            _log.LogDebug(ex, "Provider {Name} Refresh failed", p.Capabilities.Name);
                        }
                    });
                }

                // Refresh serial providers sequentially (LHM must be single-threaded)
                foreach (var p in _serialProviders)
                {
                    foreach (var group in dueGroups)
                    {
                        try { p.Refresh(group); }
                        catch (Exception ex)
                        {
                            _log.LogDebug(ex, "Provider {Name} Refresh({Group}) failed", p.Capabilities.Name, group);
                        }
                    }
                }

                // Build immutable snapshot
                var builder = ImmutableDictionary.CreateBuilder<MetricKind, double>();
                foreach (MetricKind kind in AllMetricKinds)
                {
                    // Try providers in priority order; first non-null wins
                    foreach (var p in _providers)
                    {
                        if (p.TryRead(kind, out double val))
                        {
                            builder[kind] = val;
                            break;
                        }
                    }
                }

                // Apply EMA smoothing to temperature metrics
                foreach (var kind in TempKinds)
                {
                    if (!builder.TryGetValue(kind, out double raw) || raw <= 0) continue;
                    if (_emaTemp.TryGetValue(kind, out double prev))
                        builder[kind] = EmaAlpha * raw + (1 - EmaAlpha) * prev;
                    _emaTemp[kind] = builder[kind];
                }

                var snap = new SensorSnapshot(
                    tick,
                    DateTime.UtcNow.Ticks,
                    _cpuId, _gpuId, _memId, _stoId,
                    builder.ToImmutable());

                Volatile.Write(ref _current, snap);
                SnapshotPublished?.Invoke(snap);

                if (tick <= 6)
                    _log.LogInformation(
                        "Tick{Tick} — CpuLoad:{Load:F1}%  CpuTemp:{Temp:F1}°C  CpuPower:{Power:F1}W  GpuLoad:{GpuLoad:F1}%  GpuTemp:{GpuTemp:F1}°C  GpuPower:{GpuPower:F1}W  RamUsed:{RamUsed:F2}GB  RamTotal:{RamTotal:F2}GB  RamLoad:{RamLoad:F1}%  StoTemp:{StoTemp:F1}°C",
                        tick,
                        snap.Get(MetricKind.CpuLoad),
                        snap.Get(MetricKind.CpuTempPackage),
                        snap.Get(MetricKind.CpuPower),
                        snap.Get(MetricKind.GpuLoad),
                        snap.Get(MetricKind.GpuTempCore),
                        snap.Get(MetricKind.GpuPower),
                        snap.Get(MetricKind.RamUsedBytes) / 1_073_741_824.0,
                        snap.Get(MetricKind.RamTotalBytes) / 1_073_741_824.0,
                        snap.Get(MetricKind.RamLoadPct),
                        snap.Get(MetricKind.StorageTempC));
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _log.LogError(ex, "Unexpected error in SensorPump loop at tick {Tick}", tick);
            }
        }

        _log.LogInformation("SensorPump stopped at tick {Tick}", tick);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _cts.Cancel();
        _thread.Join(timeout: TimeSpan.FromSeconds(3));
        _cts.Dispose();

        foreach (var p in _providers)
        {
            try { p.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
            catch (Exception ex) { _log.LogWarning(ex, "Error disposing provider {Name}", p.Capabilities.Name); }
        }
    }
}

using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Session;

namespace PCStatsMonitor.Providers.Windows;

/// <summary>
/// Frames-per-second monitor built on ETW frame-present telemetry (the
/// PresentMon approach): Windows' DXGI/D3D9 providers emit an event for every
/// frame any process presents — no injection into game processes, works for
/// borderless AND exclusive-fullscreen titles.
///
/// Requires administrator (real-time ETW session); Start() returns false when
/// unavailable and the monitor stays inert. Costs a kernel session + parse
/// thread while running — only start it when the user enables the FPS readout.
/// </summary>
public sealed class FpsMonitor : IDisposable
{
    private const string SessionName = "PCStatsMonitor-FPS";
    private const uint ProcessQueryLimitedInformation = 0x1000;

    // Same providers/events as PresentMon-style tools: match by GUID + raw
    // event ID — these events usually arrive without a decodable manifest, so
    // name-based matching sees them as "EventID(42)" and misses everything.
    private static readonly Guid DxgiProvider = new("CA11C036-0102-4A2D-A6AD-F03CFED5D3C9");   // Microsoft-Windows-DXGI
    private static readonly Guid D3D9Provider = new("783ACA0A-790E-4D7F-8451-AA850511C6B9");   // Microsoft-Windows-D3D9
    private static readonly Guid DxgKrnlProvider = new("802EC45A-1E99-4B83-9920-87C98277BA9D"); // Microsoft-Windows-DxgKrnl

    private const int DxgiPresentStart = 42;      // Present::Start (DX10/11/12)
    private const int D3D9PresentStart = 1;       // Present::Start (DX9)
    private const int DxgKrnlPresentInfo = 0xB8;  // Present::Info (184) — all APIs, kernel level
    private const int DxgKrnlFlipInfo = 0xA8;     // Flip::Info (168)
    private const int DxgKrnlBlitInfo = 0xA6;     // Blit::Info (166)

    // Present + Base keywords only — enabling the full DxgKrnl firehose floods
    // the session and drops events.
    private const ulong DxgKrnlKeywords = 0x8000000 | 0x1;

    private volatile bool _running;
    private volatile int _targetPid;
    private volatile string _targetProcessLabel = string.Empty;
    private volatile float _gameFps;

    private readonly object _gate = new();
    private int _lastPid;
    private double _startTs = -1;
    private int _dxgiCount;
    private int _d3d9Count;
    private int _dxgKrnlCount;

    private TraceEventSession? _session;
    private Task? _processTask;
    private Task? _watchTask;

    public bool IsRunning { get; private set; }

    public bool Start()
    {
        if (IsRunning) return true;
        try
        {
            // Stops any stale same-name session from a previous crashed run
            _session = new TraceEventSession(SessionName);
            _session.EnableProvider(DxgiProvider, TraceEventLevel.Informational);
            _session.EnableProvider(D3D9Provider, TraceEventLevel.Informational);
            _session.EnableProvider(DxgKrnlProvider, TraceEventLevel.Informational, DxgKrnlKeywords);
            _running = true;

            // AllEvents (not Dynamic.All): fires for every event including ones
            // TraceEvent has no manifest for — which is how these arrive.
            _session.Source.AllEvents += e =>
            {
                if (!_running)
                    return;

                int pid = e.ProcessID;
                int target = _targetPid;
                if (target == 0 || pid != target)
                    return;

                int id = (int)e.ID;
                Guid provider = e.ProviderGuid;

                bool isDxgiEvent = provider == DxgiProvider && id == DxgiPresentStart;
                bool isD3D9Event = provider == D3D9Provider && id == D3D9PresentStart;
                bool isDxgKrnlEvent = provider == DxgKrnlProvider &&
                                      (id == DxgKrnlPresentInfo ||
                                       id == DxgKrnlFlipInfo ||
                                       id == DxgKrnlBlitInfo);

                if (!isDxgiEvent && !isD3D9Event && !isDxgKrnlEvent)
                    return;

                // Event-header timestamp, not wall clock: real-time ETW delivers
                // events in batched buffer flushes, so callback time is bursty.
                double ts = e.TimeStampRelativeMSec / 1000.0;

                lock (_gate)
                {
                    if (pid != _lastPid)
                    {
                        _lastPid = pid;
                        _dxgiCount = 0;
                        _d3d9Count = 0;
                        _dxgKrnlCount = 0;
                        _startTs = ts;
                        return;
                    }

                    if (isDxgiEvent) _dxgiCount++;
                    if (isD3D9Event) _d3d9Count++;
                    if (isDxgKrnlEvent) _dxgKrnlCount++;

                    double elapsed = ts - _startTs;
                    if (elapsed < 1.0)
                        return;

                    int frameCount = 0;
                    if (_d3d9Count > 0)
                        frameCount = _d3d9Count;
                    else if (_dxgiCount > 0)
                        frameCount = _dxgiCount;
                    else if (_dxgKrnlCount > 0)
                    {
                        // DxgKrnl-only means no explicit DXGI/D3D9 Present call was seen —
                        // could be Vulkan/OpenGL, or a desktop app compositing through DWM.
                        // Real games render >=20fps; desktop apps typically don't, so below
                        // that threshold treat it as noise rather than a game.
                        double potentialFps = _dxgKrnlCount / elapsed;
                        if (potentialFps >= 20.0)
                            frameCount = _dxgKrnlCount;
                    }

                    _gameFps = frameCount > 0 ? (float)(frameCount / elapsed) : 0.0f;
                    _dxgiCount = 0;
                    _d3d9Count = 0;
                    _dxgKrnlCount = 0;
                    _startTs = ts;
                }
            };

            // Process() blocks for the session's lifetime; it returns when the
            // session is disposed.
            _processTask = Task.Run(() => _session.Source.Process());
            _watchTask = Task.Run(() => WatchForegroundProcess());
            IsRunning = true;
            return true;
        }
        catch
        {
            // Not elevated, or ETW unavailable — run without FPS
            _session?.Dispose();
            _session = null;
            _running = false;
            return false;
        }
    }

    public void Stop()
    {
        if (!IsRunning) return;
        IsRunning = false;
        _running = false;
        try { _session?.Dispose(); } catch { }
        _session = null;
        _targetPid = 0;
        _targetProcessLabel = string.Empty;
        _gameFps = 0.0f;
        _lastPid = 0;
        _dxgiCount = 0;
        _d3d9Count = 0;
        _dxgKrnlCount = 0;
        _startTs = -1;
    }

    /// <summary>
    /// FPS of the process owning the foreground window, as a bare number —
    /// the overlay supplies its own "FPS" label/icon. Null when not running.
    /// </summary>
    public string? ReadForegroundFpsText()
    {
        if (!IsRunning || !_running) return null;

        float fps = _gameFps;
        return $"{fps:0}";
    }

    public string? ReadForegroundProcessLabel()
    {
        if (!IsRunning || !_running) return null;
        return string.IsNullOrWhiteSpace(_targetProcessLabel) ? null : _targetProcessLabel;
    }

    private void WatchForegroundProcess()
    {
        while (_running)
        {
            try
            {
                var hwnd = GetForegroundWindow();
                if (hwnd == IntPtr.Zero)
                {
                    Thread.Sleep(250);
                    continue;
                }

                GetWindowThreadProcessId(hwnd, out uint pid);
                int targetPid = (int)pid;
                // Never target our own process (mirrors the C++ overlay's
                // fg != g_hwnd check): our windows present via DXGI too, so
                // focusing them would report our own redraw rate as "game FPS".
                // Keep the previous target instead — the game keeps rendering
                // while the user interacts with the overlay/settings.
                if (targetPid == 0 || targetPid == Environment.ProcessId)
                {
                    Thread.Sleep(250);
                    continue;
                }

                if (!TryGetProcessLabel(targetPid, out string label))
                {
                    lock (_gate)
                    {
                        _targetPid = 0;
                        _targetProcessLabel = string.Empty;
                        _gameFps = 0.0f;
                        _lastPid = 0;
                        _dxgiCount = 0;
                        _d3d9Count = 0;
                        _dxgKrnlCount = 0;
                        _startTs = -1;
                    }

                    Thread.Sleep(250);
                    continue;
                }

                if (targetPid != _targetPid)
                {
                    lock (_gate)
                    {
                        _targetPid = targetPid;
                        _targetProcessLabel = label;
                        _gameFps = 0.0f;
                        _lastPid = 0;
                        _dxgiCount = 0;
                        _d3d9Count = 0;
                        _dxgKrnlCount = 0;
                        _startTs = -1;
                    }
                }
                else if (!string.Equals(_targetProcessLabel, label, StringComparison.Ordinal))
                {
                    _targetProcessLabel = label;
                }
            }
            catch
            {
            }

            Thread.Sleep(250);
        }
    }

    private static bool TryGetProcessLabel(int pid, out string label)
    {
        label = string.Empty;

        if (!TryGetProcessPath(pid, out string path))
            return false;

        string fileName = Path.GetFileName(path);
        if (string.IsNullOrWhiteSpace(fileName))
            return false;

        string? description = null;
        try
        {
            description = FileVersionInfo.GetVersionInfo(path).FileDescription;
        }
        catch
        {
        }

        // Short display tag for the overlay pill: friendly description when the
        // exe has one ("VALORANT"), else the exe name without extension. Capped
        // so a long name can't stretch the pill.
        label = !string.IsNullOrWhiteSpace(description)
            ? description.Trim()
            : Path.GetFileNameWithoutExtension(fileName);
        if (label.Length > 20)
            label = label[..19] + "…";

        return true;
    }

    private static bool TryGetProcessPath(int pid, out string path)
    {
        path = string.Empty;

        IntPtr handle = OpenProcess(ProcessQueryLimitedInformation, false, pid);
        if (handle == IntPtr.Zero)
            return false;

        try
        {
            var builder = new StringBuilder(1024);
            int size = builder.Capacity;
            if (!QueryFullProcessImageName(handle, 0, builder, ref size))
                return false;

            path = builder.ToString();
            return !string.IsNullOrWhiteSpace(path);
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint dwDesiredAccess, bool bInheritHandle, int dwProcessId);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool QueryFullProcessImageName(IntPtr hProcess, int dwFlags, StringBuilder lpExeName, ref int lpdwSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr hObject);

    public void Dispose() => Stop();
}

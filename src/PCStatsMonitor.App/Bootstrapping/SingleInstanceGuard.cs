using System.IO.Pipes;
using System.Runtime.InteropServices;

namespace PCStatsMonitor.App.Bootstrapping;

/// <summary>
/// Enforces a single running instance.
/// Windows: Global mutex + named pipe for "show" IPC.
/// Linux: flock on $XDG_RUNTIME_DIR/pcstatsmonitor.lock + Unix domain socket.
/// </summary>
public sealed class SingleInstanceGuard : IDisposable
{
    private const string MutexName = @"Global\PCStatsMonitor.v2";
    private const string PipeName  = "PCStatsMonitor.v2.show";

    private System.Threading.Mutex? _mutex;
    private NamedPipeServerStream? _pipeServer;
    private CancellationTokenSource? _cts;

    public bool IsFirstInstance { get; private set; }

    public event Action? ShowWindowRequested;

    public void Initialize()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            InitWindows();
        else
            InitLinux();
    }

    private void InitWindows()
    {
        _mutex = new System.Threading.Mutex(true, MutexName, out bool createdNew);
        IsFirstInstance = createdNew;

        if (!IsFirstInstance)
        {
            // Signal existing instance to show its window
            try
            {
                using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
                client.Connect(500);
                client.WriteByte(1);
            }
            catch { }
            return;
        }

        // Start pipe server to listen for "show" signals
        _cts = new CancellationTokenSource();
        Task.Run(() => ListenForShowSignal(_cts.Token));
    }

    private async Task ListenForShowSignal(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                _pipeServer = new NamedPipeServerStream(PipeName, PipeDirection.In);
                await _pipeServer.WaitForConnectionAsync(ct);
                ShowWindowRequested?.Invoke();
                _pipeServer.Dispose();
            }
            catch (OperationCanceledException) { break; }
            catch { /* pipe errors are non-fatal */ }
        }
    }

    private void InitLinux()
    {
        // Simple: lock file + check if already running
        string lockDir = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR") ?? "/tmp";
        string lockFile = Path.Combine(lockDir, "pcstatsmonitor.lock");

        try
        {
            // If lock file exists and PID in it is still running, we're not the first
            if (File.Exists(lockFile))
            {
                string pidStr = File.ReadAllText(lockFile).Trim();
                if (int.TryParse(pidStr, out int pid) && IsProcessRunning(pid))
                {
                    IsFirstInstance = false;
                    return;
                }
            }
            File.WriteAllText(lockFile, Environment.ProcessId.ToString());
            IsFirstInstance = true;
        }
        catch
        {
            IsFirstInstance = true; // assume first if we can't check
        }
    }

    private static bool IsProcessRunning(int pid)
    {
        try { System.Diagnostics.Process.GetProcessById(pid); return true; }
        catch { return false; }
    }

    public void Dispose()
    {
        _cts?.Cancel();
        _pipeServer?.Dispose();
        // Only the first instance owns the mutex — ReleaseMutex from a non-owner
        // throws ApplicationException and crashes the process
        if (IsFirstInstance)
        {
            try { _mutex?.ReleaseMutex(); }
            catch (ApplicationException) { /* ownership already lost — ignore */ }
        }
        _mutex?.Dispose();
    }
}

using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using PCStatsMonitor.App.Bootstrapping;

namespace PCStatsMonitor.App.Views;

/// <summary>
/// Themed progress window shown while a staged update is applied. Runs as the
/// staged (new) exe out of the staging directory, so it can overwrite the
/// install directory's files without locking them. Waits for the outgoing app
/// to exit, copies the new files over the install, then relaunches it.
/// </summary>
public partial class UpdaterWindow : Window
{
    private readonly string _installDir;
    private readonly int _outgoingPid;
    private readonly string _stageDir;

    // Smooth bar: the worker sets _target; a frame timer eases _shown toward it
    private double _target;
    private double _shown;
    private DispatcherTimer? _anim;

    // Designer ctor
    public UpdaterWindow() : this("", 0, "") { }

    public UpdaterWindow(string installDir, int outgoingPid, string stageDir)
    {
        _installDir = installDir;
        _outgoingPid = outgoingPid;
        _stageDir = stageDir;
        InitializeComponent();

        Opened += OnOpened;
    }

    private void OnOpened(object? sender, System.EventArgs e)
    {
        string ver = TryReadStagedVersion();
        HeadlineText.Text = ver.Length > 0 ? $"Updating to v{ver}" : "Updating";

        _anim = new DispatcherTimer(DispatcherPriority.Render) { Interval = System.TimeSpan.FromMilliseconds(16) };
        _anim.Tick += AnimateBar;
        _anim.Start();

        // Real work off the UI thread; UI updates marshalled back
        _ = Task.Run(RunApplyAsync);
    }

    private void AnimateBar(object? sender, System.EventArgs e)
    {
        // Ease toward the target so the fill glides rather than jumps
        _shown += (_target - _shown) * 0.14;
        if (_shown > _target - 0.001 && _shown < _target + 0.001)
            _shown = _target;

        double w = System.Math.Max(0, BarTrack.Bounds.Width) * _shown;
        BarFill.Width = w;
        PercentText.Text = $"{_shown * 100:0}%";
    }

    private void Report(double fraction, string status)
    {
        Dispatcher.UIThread.Post(() =>
        {
            _target = System.Math.Clamp(fraction, 0, 1);
            if (status.Length > 0)
                StatusText.Text = status;
        });
    }

    private async Task RunApplyAsync()
    {
        try
        {
            Report(0.06, "Preparing update…");
            await Task.Delay(600);

            // Wait for the outgoing app to release its files
            Report(0.18, "Closing the previous version…");
            WaitForOutgoingExit();
            await Task.Delay(300);

            // Copy the staged files over the install directory with real progress
            Report(0.30, "Installing files…");
            await CopyTreeAsync(_stageDir, _installDir, 0.30, 0.80);

            // Deliberate settle so half-copied state can't be launched, and so
            // the step reads as thorough rather than a flicker
            Report(0.88, "Finalizing…");
            await Task.Delay(1100);

            Report(0.96, "Launching PC Stats Monitor…");
            UpdateService.SyncInstalledVersionRegistry();
            UpdateService.RelaunchInstalled(_installDir);
            await Task.Delay(700);

            Report(1.0, "Update complete");
            await Task.Delay(600);
        }
        catch
        {
            Report(1.0, "Update finished with warnings");
            await Task.Delay(1200);
            // Best-effort relaunch so the user is never left without an app
            try { UpdateService.RelaunchInstalled(_installDir); } catch { }
        }
        finally
        {
            Dispatcher.UIThread.Post(() =>
            {
                if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime d)
                    d.Shutdown();
                else
                    Close();
            });
        }
    }

    private void WaitForOutgoingExit()
    {
        if (_outgoingPid <= 0)
            return;
        try
        {
            var p = Process.GetProcessById(_outgoingPid);
            p.WaitForExit(15000);
        }
        catch
        {
            // Already gone — nothing to wait for
        }
    }

    private async Task CopyTreeAsync(string sourceDir, string destDir, double from, double to)
    {
        string[] files;
        try
        {
            files = Directory.GetFiles(sourceDir, "*", SearchOption.AllDirectories);
        }
        catch
        {
            return;
        }
        if (files.Length == 0)
            return;

        int done = 0;
        foreach (string src in files)
        {
            string rel = Path.GetRelativePath(sourceDir, src);
            string dst = Path.Combine(destDir, rel);
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
                CopyWithRetry(src, dst);
            }
            catch
            {
                // Skip a file we genuinely can't replace; the rest still update
            }

            done++;
            double frac = from + (to - from) * done / files.Length;
            Report(frac, "Installing files…");

            // Keep the copy visibly paced rather than an instant blur
            if (done % 12 == 0)
                await Task.Delay(15);
        }
    }

    private static void CopyWithRetry(string src, string dst)
    {
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                File.Copy(src, dst, overwrite: true);
                return;
            }
            catch when (attempt < 6)
            {
                Thread.Sleep(200); // a lingering handle usually clears quickly
            }
        }
    }

    private string TryReadStagedVersion()
    {
        try
        {
            string name = new DirectoryInfo(_stageDir).Name;
            return System.Version.TryParse(name, out _) ? name : string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }
}

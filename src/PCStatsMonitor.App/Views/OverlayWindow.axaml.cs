using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using PCStatsMonitor.Core;

namespace PCStatsMonitor.App.Views;

/// <summary>
/// Always-on-top, click-through desktop HUD showing CPU/GPU/RAM at a glance.
/// Covers the desktop and borderless-windowed apps (incl. most games); true
/// exclusive-fullscreen surfaces bypass the compositor and thus this window.
/// </summary>
public partial class OverlayWindow : Window
{
    private readonly SensorPump? _pump;
    private readonly Bootstrapping.AppSettings? _settings;
    private readonly Func<string?>? _fpsReader;
    private readonly DispatcherTimer _topmostWatchdog;
    private IntPtr _lastForeground;
    private int _startupTicks;

    // Parameterless ctor for the XAML designer only
    public OverlayWindow() : this(null) { }

    public OverlayWindow(SensorPump? pump, Bootstrapping.AppSettings? settings = null,
                         Func<string?>? fpsReader = null)
    {
        _pump = pump;
        _settings = settings;
        _fpsReader = fpsReader;
        InitializeComponent();

        if (_pump != null)
        {
            _pump.SnapshotPublished += OnSnapshot;
            Apply(_pump.Current);
        }

        ApplyVisibility();
        ApplyStyle();
        ApplyScale();

        Opened += (_, _) =>
        {
            Reposition();
            MakeClickThrough();
            PushToTop();
        };

        // SizeToContent resizes the window when live values replace the "—"
        // placeholders — re-pin the anchored corner or it walks off-screen.
        SizeChanged += (_, _) => Reposition();

        // Topmost watchdog. Games re-assert their own TOPMOST above ours, and
        // Avalonia keeps mapping the native window shortly after Opened (an
        // early one-shot pin doesn't stick). Re-pinning every tick flickers, so
        // pin on the first few ticks and then only when the foreground window
        // changes — the moment something can jump above us. Runs independent of
        // the sensor cadence. Exclusive-fullscreen games bypass the compositor
        // (nothing composited can cover them); borderless is what this wins.
        _topmostWatchdog = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(1),
        };
        _topmostWatchdog.Tick += (_, _) =>
        {
            var fg = GetForegroundWindow();
            if (fg != _lastForeground || _startupTicks < 3)
            {
                _lastForeground = fg;
                _startupTicks++;
                PushToTop();
            }
        };
        _topmostWatchdog.Start();
    }

    private void OnSnapshot(SensorSnapshot snap)
    {
        try
        {
            Dispatcher.UIThread.Post(() => Apply(snap), DispatcherPriority.Background);
        }
        catch (InvalidOperationException)
        {
            // Dispatcher shut down mid-publish (app exiting) — drop the frame.
        }
    }

    private void Apply(SensorSnapshot snap)
    {
        if (_settings?.OverlayShowFps == true)
        {
            string? fps = _fpsReader?.Invoke();
            FpsText.Text = string.IsNullOrWhiteSpace(fps) ? "—" : fps;
        }

        bool temps = _settings?.OverlayShowTemps ?? true;
        CpuText.Text = FormatLoadTemp(snap, MetricKind.CpuLoad, MetricKind.CpuTempPackage, temps);
        GpuText.Text = FormatLoadTemp(snap, MetricKind.GpuLoad, MetricKind.GpuTempCore, temps);

        if (snap.Has(MetricKind.RamUsedBytes))
        {
            double usedGb = snap.Get(MetricKind.RamUsedBytes) / (1024.0 * 1024 * 1024);
            RamText.Text = snap.Has(MetricKind.RamLoadPct)
                ? $"{usedGb:0.0} GB · {snap.Get(MetricKind.RamLoadPct):0}%"
                : $"{usedGb:0.0} GB";
        }
        else
        {
            RamText.Text = "—";
        }

        DiskText.Text = FormatLoadTemp(snap, MetricKind.StorageLoadPct, MetricKind.StorageTempC, temps);
    }

    private static string FormatLoadTemp(SensorSnapshot snap, MetricKind load, MetricKind temp, bool withTemp)
    {
        if (!snap.Has(load) && !(withTemp && snap.Has(temp)))
            return snap.Has(load) ? $"{snap.Get(load):0}%" : "—";
        string s = snap.Has(load) ? $"{snap.Get(load):0}%" : "—";
        if (withTemp && snap.Has(temp))
            s += $" · {snap.Get(temp):0}°C";
        return s;
    }

    /// <summary>Applies every overlay customization setting. Public: App calls it
    /// when settings change while the overlay is open (live preview).</summary>
    public void ApplySettings()
    {
        ApplyVisibility();
        ApplyStyle();
        ApplyScale();
        Reposition();
        // Temps toggle changes the value strings — re-render from latest data
        if (_pump != null)
            Apply(_pump.Current);
    }

    // Order MUST mirror the SettingOverlayFont ComboBox items in MainWindow.axaml.
    // Cascadia Mono ships with Windows 11 only — the Consolas fallback covers Win10.
    private static readonly string[] FontOptions =
        { "Inter", "Segoe UI", "Consolas", "Bahnschrift", "Arial", "Cascadia Mono, Consolas", "Verdana" };


    private void ApplyStyle()
    {
        if (_settings is null) return;

        // Font sizes: labels scale down from the value size, floor 7
        double fs = Math.Clamp(_settings.OverlayFontSize, 8, 20);
        double ls = Math.Max(7, fs * 0.76);
        int fontIdx = Math.Clamp(_settings.OverlayFontIndex, 0, FontOptions.Length - 1);
        var valueFont = Avalonia.Media.FontFamily.Parse(FontOptions[fontIdx] + ", Segoe UI");
        foreach (var t in new[] { FpsText, CpuText, GpuText, RamText, DiskText })
        {
            t.FontSize = fs;
            t.FontFamily = valueFont;
            // Keep the anti-jitter width proportional to the font
            t.MinWidth = (t == RamText ? 70 : t == FpsText ? 30 : 56) * (fs / 10.5);
        }

        // Identity: metric word label, or hand-drawn vector icon - never both
        bool icons = _settings.OverlayIcons;
        var labels = new[] { FpsLabel, CpuLabel, GpuLabel, RamLabel, DiskLabel };
        var iconBoxes = new[] { FpsIconBox, CpuIconBox, GpuIconBox, RamIconBox, DiskIconBox };
        double iconSize = Math.Max(11, fs * 1.15);
        for (int i = 0; i < labels.Length; i++)
        {
            labels[i].IsVisible = !icons;
            labels[i].FontFamily = valueFont;
            labels[i].FontSize = ls;
            iconBoxes[i].IsVisible = icons;
            iconBoxes[i].Width = iconSize;
            iconBoxes[i].Height = iconSize;
        }

        // Corner style
        Plate.CornerRadius = _settings.OverlayCorner switch
        {
            Bootstrapping.OverlayCorner.Rounded   => new CornerRadius(8),
            Bootstrapping.OverlayCorner.Rectangle => new CornerRadius(0),
            _ => new CornerRadius(99),
        };

        // Background plate: preset color at chosen opacity, or nothing at all
        if (_settings.OverlayBackground == Bootstrapping.OverlayBackground.Invisible)
        {
            Plate.Background = Avalonia.Media.Brushes.Transparent;
            Plate.BorderThickness = new Thickness(0);
        }
        else
        {
            var baseColor = _settings.OverlayBackground switch
            {
                Bootstrapping.OverlayBackground.Black => Avalonia.Media.Color.FromRgb(0x00, 0x00, 0x00),
                Bootstrapping.OverlayBackground.Slate => Avalonia.Media.Color.FromRgb(0x1B, 0x24, 0x30),
                _ => Avalonia.Media.Color.FromRgb(0x0D, 0x0D, 0x0F),
            };
            byte a = (byte)(Math.Clamp(_settings.OverlayOpacity, 0.0, 1.0) * 255);
            Plate.Background = new Avalonia.Media.SolidColorBrush(
                Avalonia.Media.Color.FromArgb(a, baseColor.R, baseColor.G, baseColor.B));
            // Near-zero opacity: drop the border too, otherwise an empty outline floats
            Plate.BorderThickness = new Thickness(a < 13 ? 0 : 1);
        }
    }

    private void ApplyVisibility()
    {
        bool fps  = _settings?.OverlayShowFps  ?? false;
        bool cpu  = _settings?.OverlayShowCpu  ?? true;
        bool gpu  = _settings?.OverlayShowGpu  ?? true;
        bool ram  = _settings?.OverlayShowRam  ?? true;
        bool disk = _settings?.OverlayShowDisk ?? false;

        FpsGroup.IsVisible  = fps;
        CpuGroup.IsVisible  = cpu;
        GpuGroup.IsVisible  = gpu;
        RamGroup.IsVisible  = ram;
        DiskGroup.IsVisible = disk;

        // A separator shows only between two visible groups: before each visible
        // group that has at least one visible group ahead of it.
        Sep0.IsVisible = cpu && fps;
        Sep1.IsVisible = gpu && (fps || cpu);
        Sep2.IsVisible = ram && (fps || cpu || gpu);
        Sep3.IsVisible = disk && (fps || cpu || gpu || ram);
    }

    private void ApplyScale()
    {
        double s = (_settings?.OverlaySize ?? Bootstrapping.OverlaySize.Medium) switch
        {
            Bootstrapping.OverlaySize.Small => 0.8,
            Bootstrapping.OverlaySize.Large => 1.3,
            _ => 1.0,
        };
        RootScale.LayoutTransform = new Avalonia.Media.ScaleTransform(s, s);
        // Scaling changes the desired size; SizeChanged → Reposition re-pins the corner
    }

    /// <summary>Pins the pill to the configured screen place (working area, so
    /// bottom rows sit just above the taskbar). Public: App calls it when the
    /// position setting changes while the overlay is open.</summary>
    public void Reposition()
    {
        var screen = Screens.Primary ?? (Screens.All.Count > 0 ? Screens.All[0] : null);
        if (screen is null) return;
        var area = screen.WorkingArea;
        double scale = RenderScaling;
        int w = (int)(Bounds.Width * scale);
        int h = (int)(Bounds.Height * scale);
        // User-tunable pixel gaps from the docked edges
        int padX = _settings?.OverlayOffsetX ?? 16;
        int padY = _settings?.OverlayOffsetY ?? 16;

        var pos = _settings?.OverlayPosition ?? Bootstrapping.OverlayPosition.TopRight;
        int x = pos switch
        {
            Bootstrapping.OverlayPosition.TopLeft or Bootstrapping.OverlayPosition.BottomLeft
                => area.X + padX,
            Bootstrapping.OverlayPosition.TopCenter or Bootstrapping.OverlayPosition.BottomCenter
                => area.X + (area.Width - w) / 2,
            _ => area.X + area.Width - w - padX,
        };
        int y = pos switch
        {
            Bootstrapping.OverlayPosition.BottomLeft or Bootstrapping.OverlayPosition.BottomCenter
                or Bootstrapping.OverlayPosition.BottomRight
                => area.Y + area.Height - h - padY,
            _ => area.Y + padY,
        };
        Position = new PixelPoint(x, y);
    }

    /// <summary>
    /// WS_EX_TRANSPARENT makes every pixel ignore mouse input (clicks land on
    /// whatever is underneath); NOACTIVATE keeps it from ever taking focus and
    /// TOOLWINDOW hides it from Alt-Tab.
    /// </summary>
    private void MakeClickThrough()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return;
        var handle = TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
        if (handle == IntPtr.Zero) return;

        const int GWL_EXSTYLE = -20;
        const long WS_EX_TRANSPARENT = 0x20, WS_EX_LAYERED = 0x80000,
                   WS_EX_NOACTIVATE = 0x8000000, WS_EX_TOOLWINDOW = 0x80;
        long style = GetWindowLongPtrW(handle, GWL_EXSTYLE).ToInt64();
        style |= WS_EX_TRANSPARENT | WS_EX_LAYERED | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW;
        SetWindowLongPtrW(handle, GWL_EXSTYLE, new IntPtr(style));
    }

    private void PushToTop()
    {
        var handle = TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
        if (handle == IntPtr.Zero) return;
        const uint SWP_NOMOVE = 0x2, SWP_NOSIZE = 0x1, SWP_NOACTIVATE = 0x10;
        SetWindowPos(handle, new IntPtr(-1) /* HWND_TOPMOST */, 0, 0, 0, 0,
            SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtrW(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtrW(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter,
        int x, int y, int cx, int cy, uint flags);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    protected override void OnClosed(EventArgs e)
    {
        _topmostWatchdog.Stop();
        if (_pump != null)
            _pump.SnapshotPublished -= OnSnapshot;
        base.OnClosed(e);
    }
}

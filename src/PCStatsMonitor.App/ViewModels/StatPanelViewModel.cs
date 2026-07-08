using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using PCStatsMonitor.Core;

namespace PCStatsMonitor.App.ViewModels;

public sealed class StatPanelViewModel : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    private string _title = "";
    private string _subtitle = "";
    private string _gaugeCaption = "";
    private double _progressPercent;
    private string _stat0Label = "", _stat0Value = "";
    private string _stat1Label = "", _stat1Value = "";
    private string _stat2Label = "", _stat2Value = "";
    private string _stat3Label = "", _stat3Value = "";
    private string _carouselPosition = "";
    private bool _hasCarousel;

    public string Title        { get => _title;         set => Set(ref _title, value); }
    public string Subtitle     { get => _subtitle;      set => Set(ref _subtitle, value); }
    public string GaugeCaption { get => _gaugeCaption;  set => Set(ref _gaugeCaption, value); }
    public double ProgressPercent { get => _progressPercent; set => Set(ref _progressPercent, value); }
    public string Stat0Label   { get => _stat0Label;    set => Set(ref _stat0Label, value); }
    public string Stat0Value   { get => _stat0Value;    set => Set(ref _stat0Value, value); }
    public string Stat1Label   { get => _stat1Label;    set => Set(ref _stat1Label, value); }
    public string Stat1Value   { get => _stat1Value;    set => Set(ref _stat1Value, value); }
    public string Stat2Label   { get => _stat2Label;    set => Set(ref _stat2Label, value); }
    public string Stat2Value   { get => _stat2Value;    set => Set(ref _stat2Value, value); }
    public string Stat3Label   { get => _stat3Label;    set => Set(ref _stat3Label, value); }
    public string Stat3Value   { get => _stat3Value;    set => Set(ref _stat3Value, value); }

    /// <summary>Carousel position label, e.g. "2 / 3". Set only for the STORAGE
    /// card by <see cref="StorageCarouselViewModel"/>; blank elsewhere.</summary>
    public string CarouselPosition { get => _carouselPosition; set => Set(ref _carouselPosition, value); }

    /// <summary>Show the ‹ n/N › nav only when there is more than one drive to cycle.</summary>
    public bool HasCarousel { get => _hasCarousel; set => Set(ref _hasCarousel, value); }

    /// <summary>Raised when a carousel arrow is clicked (payload: -1 = prev, +1 = next).
    /// Wired only by the STORAGE panel's carousel; other cards never fire it.</summary>
    public event Action<int>? CarouselStep;

    public void RaiseCarouselStep(int direction) => CarouselStep?.Invoke(direction);

    private void Set<T>(ref T field, T value, [CallerMemberName] string? prop = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(prop));
    }
}

/// <summary>
/// Converts raw SensorSnapshot values to formatted display strings.
/// Lives here to keep ViewModels thin and formatting logic testable.
/// </summary>
public static class SnapshotFormatter
{
    public static void ApplyCpu(StatPanelViewModel vm, SensorSnapshot snap)
    {
        double load = snap.Get(MetricKind.CpuLoad);
        vm.ProgressPercent = load;
        vm.GaugeCaption = "LOAD";
        vm.Stat0Label = "TEMPERATURE";
        vm.Stat0Value = FormatTemp(snap.Get(MetricKind.CpuTempPackage));
        vm.Stat1Label = "CLOCK";
        vm.Stat1Value = FormatClock(snap.Get(MetricKind.CpuClock));
        vm.Stat2Label = "POWER DRAW";
        vm.Stat2Value = FormatWatts(snap.Get(MetricKind.CpuPower));
        vm.Stat3Label = "FAN SPEED";
        vm.Stat3Value = FormatRpm(snap, MetricKind.CpuFanRpm);
    }

    public static void ApplyGpu(StatPanelViewModel vm, SensorSnapshot snap)
    {
        double load = snap.Get(MetricKind.GpuLoad);
        vm.ProgressPercent = load;
        vm.GaugeCaption = "LOAD";
        vm.Stat0Label = "TEMPERATURE";
        vm.Stat0Value = FormatTemp(snap.Get(MetricKind.GpuTempCore));
        vm.Stat1Label = "POWER DRAW";
        vm.Stat1Value = FormatWatts(snap.Get(MetricKind.GpuPower));
        vm.Stat2Label = "FAN SPEED";
        vm.Stat2Value = FormatRpm(snap, MetricKind.GpuFanRpm);
        vm.Stat3Label = "VRAM USED";
        vm.Stat3Value = FormatGb(snap.Get(MetricKind.GpuMemUsedBytes));
    }

    public static void ApplyMemory(StatPanelViewModel vm, SensorSnapshot snap)
    {
        double load = snap.Get(MetricKind.RamLoadPct);
        double usedGb  = snap.Get(MetricKind.RamUsedBytes)  / 1_073_741_824.0;
        double totalGb = snap.Get(MetricKind.RamTotalBytes) / 1_073_741_824.0;
        double freeGb  = totalGb - usedGb;

        vm.ProgressPercent = load;
        vm.GaugeCaption = "USED";
        vm.Stat0Label = "TOTAL RAM";
        vm.Stat0Value = totalGb > 0 ? $"{totalGb:F1} GB" : "N/A";
        vm.Stat1Label = "USED RAM";
        vm.Stat1Value = usedGb > 0 ? $"{usedGb:F1} GB" : "N/A";
        vm.Stat2Label = "FREE RAM";
        vm.Stat2Value = freeGb > 0 ? $"{freeGb:F1} GB" : "N/A";
        vm.Stat3Label = "USAGE";
        vm.Stat3Value = $"{load:F1}%";
    }

    public static void ApplyStorage(StatPanelViewModel vm, SensorSnapshot snap)
    {
        double usedBytes  = snap.Get(MetricKind.StorageUsedBytes);
        double totalBytes = snap.Get(MetricKind.StorageTotalBytes);
        double loadPct    = snap.Get(MetricKind.StorageLoadPct);

        vm.ProgressPercent = loadPct;
        vm.GaugeCaption = "USED";
        vm.Stat0Label = "TEMPERATURE";
        vm.Stat0Value = FormatTemp(snap.Get(MetricKind.StorageTempC));
        vm.Stat1Label = "USED SPACE";
        vm.Stat1Value = totalBytes > 0
            ? $"{usedBytes / 1_073_741_824.0:F0} / {totalBytes / 1_073_741_824.0:F0} GB"
            : "N/A";
        vm.Stat2Label = "ACTIVITY";
        vm.Stat2Value = snap.Has(MetricKind.StorageActivityPct)
            ? $"{snap.Get(MetricKind.StorageActivityPct):F0}%"
            : "N/A";
        vm.Stat3Label = "DRIVE LIFE";
        vm.Stat3Value = snap.Has(MetricKind.StorageLifePct)
            ? $"{snap.Get(MetricKind.StorageLifePct):F0}%"
            : "N/A";
    }

    /// <summary>
    /// Formats one physical drive (from SensorSnapshot.Drives) into the storage card.
    /// <paramref name="flatTempC"/>/<paramref name="flatLifePct"/> are the legacy flat
    /// metrics (LHM/IOCTL) — used only as a fallback when this drive's own value is missing
    /// (e.g. reliability counter blocked), so the system drive's card never regresses.
    /// </summary>
    public static void ApplyDrive(StatPanelViewModel vm, DriveReading d,
                                  double flatTempC = 0, double flatLifePct = -1)
    {
        vm.Subtitle = string.IsNullOrEmpty(d.Volumes) ? d.Model : $"{d.Model} · {d.Volumes}";
        vm.ProgressPercent = d.LoadPct;
        vm.GaugeCaption = "USED";

        double tempC = d.TempC > 0 ? d.TempC : flatTempC;
        double lifePct = d.LifePct >= 0 ? d.LifePct : flatLifePct;

        vm.Stat0Label = "TYPE";
        vm.Stat0Value = string.IsNullOrEmpty(d.TypeLabel) ? "Drive" : d.TypeLabel;
        vm.Stat1Label = "TEMPERATURE";
        vm.Stat1Value = FormatTemp(tempC);
        vm.Stat2Label = "USED SPACE";
        vm.Stat2Value = d.TotalBytes > 0
            ? $"{d.UsedBytes / 1_073_741_824.0:F0} / {d.TotalBytes / 1_073_741_824.0:F0} GB"
            : "N/A";
        vm.Stat3Label = "DRIVE LIFE";
        vm.Stat3Value = lifePct >= 0 ? $"{lifePct:F0}%" : "N/A";
    }

    private static string FormatTemp(double c)  => c > 0 ? $"{c:F1}°C" : "N/A";
    private static string FormatWatts(double w) => w > 0 ? $"{w:F1} W" : "N/A";

    private static string FormatClock(double mhz) =>
        mhz >= 1000 ? $"{mhz / 1000.0:F2} GHz" : mhz > 0 ? $"{mhz:F0} MHz" : "N/A";

    private static string FormatGb(double bytes) =>
        bytes > 0 ? $"{bytes / 1_073_741_824.0:F1} GB" : "N/A";

    // "0 RPM" is real data (a stopped semi-passive fan); N/A means no sensor exists.
    private static string FormatRpm(SensorSnapshot snap, MetricKind kind) =>
        snap.Has(kind) ? $"{snap.Get(kind):F0} RPM" : "N/A";
}

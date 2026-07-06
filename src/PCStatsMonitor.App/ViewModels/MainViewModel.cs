using PCStatsMonitor.Core;

namespace PCStatsMonitor.App.ViewModels;

public sealed class MainViewModel
{
    public StatPanelViewModel Cpu     { get; } = new() { Title = "CPU",     Subtitle = "Detecting..." };
    public StatPanelViewModel Gpu     { get; } = new() { Title = "GPU",     Subtitle = "Detecting..." };
    public StatPanelViewModel Memory  { get; } = new() { Title = "MEMORY",  Subtitle = "System Memory" };
    public StatPanelViewModel Storage { get; } = new() { Title = "STORAGE", Subtitle = "System Drive" };

    /// <summary>
    /// Called on the UI thread (marshaled by SnapshotSubscriber) with the latest snapshot.
    /// Diffs are handled inside StatPanelViewModel.Set — only changed properties raise events.
    /// </summary>
    public void Apply(SensorSnapshot snap)
    {
        // Update subtitles from identity once they arrive
        if (snap.Cpu.Model != "Unknown") Cpu.Subtitle = snap.Cpu.Model;
        if (snap.Gpu.Model != "Unknown") Gpu.Subtitle = snap.Gpu.Model;

        SnapshotFormatter.ApplyCpu(Cpu, snap);
        SnapshotFormatter.ApplyGpu(Gpu, snap);
        SnapshotFormatter.ApplyMemory(Memory, snap);
        SnapshotFormatter.ApplyStorage(Storage, snap);
    }
}

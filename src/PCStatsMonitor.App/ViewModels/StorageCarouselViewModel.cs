using System.Collections.Immutable;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using PCStatsMonitor.Core;

namespace PCStatsMonitor.App.ViewModels;

/// <summary>
/// Drives the STORAGE card as a carousel over every physical drive. Owns the drive list
/// and the selected index; renders the active drive into a shared <see cref="StatPanelViewModel"/>
/// so the existing StatPanelView binding is reused untouched. Nav chrome (arrows, "2 / 3")
/// binds to <see cref="HasMultiple"/> / <see cref="PositionText"/>.
///
/// When no per-drive inventory is available (Linux, or WMI blocked), it falls back to the
/// legacy single-drive formatting so nothing regresses.
/// </summary>
public sealed class StorageCarouselViewModel : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    private ImmutableArray<DriveReading> _drives = ImmutableArray<DriveReading>.Empty;
    private SensorSnapshot _lastSnap = SensorSnapshot.Empty;
    private int _index;
    private bool _wired;

    // Drive letter that hosts Windows (e.g. "C:") — the flat StorageTempC/Life apply to it.
    private static readonly string SystemVolume =
        (Path.GetPathRoot(Environment.SystemDirectory) ?? "").TrimEnd('\\');

    public bool HasMultiple => _drives.Length > 1;
    public string PositionText => _drives.Length > 1 ? $"{_index + 1} / {_drives.Length}" : "";

    /// <summary>Update the drive set from a snapshot and render the active drive into <paramref name="panel"/>.</summary>
    public void Apply(SensorSnapshot snap, StatPanelViewModel panel)
    {
        // The same panel instance is passed every tick — wire the nav hook once.
        if (!_wired)
        {
            panel.CarouselStep += direction => OnStep(direction, panel);
            _wired = true;
        }

        _lastSnap = snap;
        _drives = snap.Drives.IsDefault ? ImmutableArray<DriveReading>.Empty : snap.Drives;
        if (_index >= _drives.Length) _index = 0;
        Render(panel);
    }

    private void OnStep(int direction, StatPanelViewModel panel)
    {
        if (_drives.Length < 2) return;
        _index = (_index + direction + _drives.Length) % _drives.Length;
        Render(panel);
    }

    public void Next(StatPanelViewModel panel)
    {
        if (_drives.Length < 2) return;
        _index = (_index + 1) % _drives.Length;
        Render(panel);
    }

    public void Prev(StatPanelViewModel panel)
    {
        if (_drives.Length < 2) return;
        _index = (_index - 1 + _drives.Length) % _drives.Length;
        Render(panel);
    }

    private void Render(StatPanelViewModel panel)
    {
        if (_drives.IsDefaultOrEmpty)
        {
            SnapshotFormatter.ApplyStorage(panel, _lastSnap); // legacy single-drive path
        }
        else
        {
            var d = _drives[_index];
            // The flat LHM/IOCTL temp & life describe the system drive — offer them as a
            // fallback only for the card that actually hosts Windows.
            bool isSystemDrive = !string.IsNullOrEmpty(SystemVolume)
                && d.Volumes.Contains(SystemVolume, StringComparison.OrdinalIgnoreCase);
            double flatTemp = isSystemDrive ? _lastSnap.Get(MetricKind.StorageTempC) : 0;
            double flatLife = isSystemDrive && _lastSnap.Has(MetricKind.StorageLifePct)
                ? _lastSnap.Get(MetricKind.StorageLifePct) : -1;
            SnapshotFormatter.ApplyDrive(panel, d, flatTemp, flatLife);
        }

        // Diff-on-set inside StatPanelViewModel means these only raise events on
        // an actual change, so it's cheap to push every tick.
        panel.HasCarousel = _drives.Length > 1;
        panel.CarouselPosition = _drives.Length > 1 ? $"{_index + 1} / {_drives.Length}" : "";

        Raise(nameof(HasMultiple));
        Raise(nameof(PositionText));
    }

    private void Raise([CallerMemberName] string? prop = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(prop));
}

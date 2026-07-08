using Avalonia.Controls;
using Avalonia.Interactivity;
using PCStatsMonitor.App.ViewModels;

namespace PCStatsMonitor.App.Views;

public partial class StatPanelView : UserControl
{
    public StatPanelView()
    {
        InitializeComponent();
    }

    /// <summary>Carousel arrows — step the STORAGE card's drive selection. The
    /// panel VM relays the step to the carousel, which re-renders the shown drive.</summary>
    private void OnCarouselPrev(object? sender, RoutedEventArgs e)
    {
        if (DataContext is StatPanelViewModel vm) vm.RaiseCarouselStep(-1);
    }

    private void OnCarouselNext(object? sender, RoutedEventArgs e)
    {
        if (DataContext is StatPanelViewModel vm) vm.RaiseCarouselStep(+1);
    }
}

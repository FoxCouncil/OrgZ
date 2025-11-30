// Copyright (c) 2025 Fox Diller

using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using OrgZ.ViewModels;

namespace OrgZ.Views;

public partial class MainWindow : Window
{
    private readonly MainWindowViewModel _viewModel;

    public MainWindow()
    {
        InitializeComponent();

        Slider? slider = this.FindControl<Slider>("CurrentTimeSlider");

        slider.AddHandler(
            InputElement.PointerPressedEvent,
            Slider_PointerPressed,
            RoutingStrategies.Tunnel | RoutingStrategies.Bubble,
            handledEventsToo: true);

        slider.AddHandler(
            InputElement.PointerReleasedEvent,
            Slider_PointerReleased,
            RoutingStrategies.Tunnel | RoutingStrategies.Bubble,
            handledEventsToo: true);

        DataContext = _viewModel = new MainWindowViewModel(this);

        // Load files asynchronously after window is loaded
        Loaded += async (s, e) => await _viewModel.LoadAsync();
    }

    private void DataGrid_DoubleTapped(object? sender, Avalonia.Input.TappedEventArgs e)
    {
        _viewModel.DataGridRowDoubleClick();
    }

    private void Slider_ValueChanged(object? sender, Avalonia.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        _viewModel.CurrentVolumeChanged();
    }

    private void Slider_PointerPressed(object? sender, Avalonia.Input.PointerPressedEventArgs e)
    {
        _viewModel.CurrentTrackTimeNumberPointerPressed();
    }

    private void Slider_PointerReleased(object? sender, Avalonia.Input.PointerReleasedEventArgs e)
    {
        _viewModel.CurrentTrackTimeNumberPointerReleased();
    }
}
// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using OrgZ.Models;
using OrgZ.ViewModels;

namespace OrgZ.Views;

public partial class MainWindow : Window
{
    // Column indices matching the XAML definition order
    private const int ColPlaying = 0;
    private const int ColSource = 1;
    private const int ColTitle = 2;
    private const int ColArtist = 3;
    private const int ColAlbum = 4;
    private const int ColCountry = 5;
    private const int ColTags = 6;
    private const int ColYear = 7;
    private const int ColCodec = 8;
    private const int ColExtension = 9;
    private const int ColBitrate = 10;
    private const int ColHasAlbumArt = 11;
    private const int ColListeners = 12;

    private readonly MainWindowViewModel _viewModel;

    public MainWindow()
    {
        InitializeComponent();

        var slider = this.FindControl<Slider>("CurrentTimeSlider")!;

        slider.AddHandler(InputElement.PointerPressedEvent, Slider_PointerPressed, RoutingStrategies.Tunnel | RoutingStrategies.Bubble, handledEventsToo: true);
        slider.AddHandler(InputElement.PointerReleasedEvent, Slider_PointerReleased, RoutingStrategies.Tunnel | RoutingStrategies.Bubble, handledEventsToo: true);

        DataContext = _viewModel = new MainWindowViewModel(this);

        _viewModel.PropertyChanged += ViewModel_PropertyChanged;

        // Initialize UI state for the already-selected sidebar item
        // (PropertyChanged fired during constructor before handler was attached)
        var initialKind = _viewModel.SelectedSidebarItem?.Kind;
        var initialFavorites = _viewModel.SelectedSidebarItem?.IsFavorites == true;
        UpdateColumnVisibility(initialKind, initialFavorites);
        UpdateContextMenu(initialKind);
        UpdateFilterPanelVisibility(initialKind);

        var radioFilterPanel = this.FindControl<Controls.RadioFilterPanel>("RadioFilterPanel")!;
        radioFilterPanel.SyncRequested += () => _viewModel.LaunchRadioSync();

        var statusBar = this.FindControl<Controls.StatusBar>("MainStatusBar")!;
        statusBar.ErrorButtonClicked += async () => await _viewModel.ShowMessageLog();

        Loaded += async (s, e) =>
        {
            var handle = TryGetPlatformHandle();
            if (handle != null)
            {
                _viewModel.InitializeSmtc(handle.Handle);
                _viewModel.InitializeThumbBar(handle.Handle);
            }

            await _viewModel.LoadAsync();
        };
    }

    private async void ViewModel_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(MainWindowViewModel.SelectedSidebarItem))
        {
            return;
        }

        var sidebarItem = _viewModel.SelectedSidebarItem;
        var kind = sidebarItem?.Kind;
        var isFavorites = sidebarItem?.IsFavorites == true;

        UpdateColumnVisibility(kind, isFavorites);
        UpdateContextMenu(kind);
        UpdateFilterPanelVisibility(kind);

        // First-run only: if DB had no radio stations, fetch popular ones
        if (kind == MediaKind.Radio && _viewModel.FilteredItems.Count == 0 && !_viewModel.IsLoading)
        {
            await _viewModel.FetchPopularStationsAsync();
        }
    }

    private void UpdateColumnVisibility(MediaKind? kind, bool isFavorites = false)
    {
        var cols = MainDataGrid.Columns;
        bool music = kind == MediaKind.Music || isFavorites;
        bool radio = kind == MediaKind.Radio;

        // Always visible
        cols[ColPlaying].IsVisible = true;
        cols[ColTitle].IsVisible = true;

        // Radio only
        cols[ColSource].IsVisible = radio;
        cols[ColCountry].IsVisible = radio;
        cols[ColTags].IsVisible = radio;
        cols[ColBitrate].IsVisible = radio;
        cols[ColCodec].IsVisible = radio;
        cols[ColListeners].IsVisible = radio;

        // Music only (and Favorites)
        cols[ColArtist].IsVisible = music;
        cols[ColAlbum].IsVisible = music;
        cols[ColYear].IsVisible = music && !isFavorites;
        cols[ColExtension].IsVisible = music && !isFavorites;
        cols[ColHasAlbumArt].IsVisible = music && !isFavorites;
    }

    private void UpdateContextMenu(MediaKind? kind)
    {
        MainDataGrid.ContextMenu = kind == MediaKind.Radio
            ? (ContextMenu)Resources["RadioContextMenu"]!
            : (ContextMenu)Resources["MusicContextMenu"]!;
    }

    private void UpdateFilterPanelVisibility(MediaKind? kind)
    {
        RadioFilterPanel.IsVisible = kind == MediaKind.Radio;
    }

    private void DataGrid_DoubleTapped(object? sender, TappedEventArgs e)
    {
        _viewModel.DataGridRowDoubleClick();
    }

    private void Slider_ValueChanged(object? sender, Avalonia.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        _viewModel.CurrentVolumeChanged();
    }

    private void ContextMenu_Play(object? sender, RoutedEventArgs e)
    {
        _viewModel.DataGridRowDoubleClick();
    }

    private void ContextMenu_Favorite(object? sender, RoutedEventArgs e)
    {
        if (_viewModel.SelectedItem != null)
        {
            _viewModel.ToggleFavorite(_viewModel.SelectedItem);
        }
    }

    private async void ContextMenu_CopyUrl(object? sender, RoutedEventArgs e)
    {
        if (_viewModel.SelectedItem?.StreamUrl != null && Clipboard != null)
        {
            await Clipboard.SetTextAsync(_viewModel.SelectedItem.StreamUrl);
        }
    }

    private async void ContextMenu_GetInfo(object? sender, RoutedEventArgs e)
    {
        await _viewModel.ShowMediaInfo();
    }

    private void ContextMenu_Homepage(object? sender, RoutedEventArgs e)
    {
        if (_viewModel.SelectedItem?.HomepageUrl != null)
        {
            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = _viewModel.SelectedItem.HomepageUrl,
                    UseShellExecute = true
                };
                System.Diagnostics.Process.Start(psi);
            }
            catch
            {
                // Invalid URL
            }
        }
    }

    private void Slider_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        _viewModel.CurrentTrackTimeNumberPointerPressed();
    }

    private void Slider_PointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        _viewModel.CurrentTrackTimeNumberPointerReleased();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);

        if (FocusManager?.GetFocusedElement() is TextBox)
        {
            return;
        }

        switch (e.Key)
        {
            case Key.Space:
            {
                _viewModel.ButtonPlayPause();
                e.Handled = true;
                break;
            }

            case Key.I when e.KeyModifiers == KeyModifiers.Control:
            {
                _ = _viewModel.ShowMediaInfo();
                e.Handled = true;
                break;
            }
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        _viewModel.Dispose();
        base.OnClosed(e);
    }
}

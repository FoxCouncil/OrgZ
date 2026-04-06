// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.VisualTree;
using OrgZ.Models;
using OrgZ.ViewModels;
using Projektanker.Icons.Avalonia;

namespace OrgZ.Views;

public partial class MainWindow : Window
{
    private readonly MainWindowViewModel _viewModel;

    private readonly Dictionary<string, EventHandler<RoutedEventArgs>> _menuHandlers;

    private CancellationTokenSource? _liveBarAnimationCts;
    private CancellationTokenSource? _marquee1Cts;
    private CancellationTokenSource? _marquee2Cts;

    private string? _lastViewConfigKey;
    private readonly Dictionary<string, (double ScrollOffset, MediaItem? SelectedItem)> _viewStates = new();

    public MainWindow()
    {
        InitializeComponent();

        var slider = this.FindControl<Slider>("CurrentTimeSlider")!;

        slider.AddHandler(InputElement.PointerPressedEvent, Slider_PointerPressed, RoutingStrategies.Tunnel | RoutingStrategies.Bubble, handledEventsToo: true);
        slider.AddHandler(InputElement.PointerReleasedEvent, Slider_PointerReleased, RoutingStrategies.Tunnel | RoutingStrategies.Bubble, handledEventsToo: true);

        DataContext = _viewModel = new MainWindowViewModel(this);

        _menuHandlers = new Dictionary<string, EventHandler<RoutedEventArgs>>
        {
            ["Play"] = ContextMenu_Play,
            ["Favorite"] = ContextMenu_Favorite,
            ["GetInfo"] = ContextMenu_GetInfo,
            ["CopyUrl"] = ContextMenu_CopyUrl,
            ["Homepage"] = ContextMenu_Homepage,
        };

        _viewModel.PropertyChanged += ViewModel_PropertyChanged;
        _viewModel.ScrollToSelectedRequested = () =>
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                if (_viewModel.SelectedItem != null)
                {
                    MainDataGrid.ScrollIntoView(_viewModel.SelectedItem, null);
                }
            }, Avalonia.Threading.DispatcherPriority.Background);
        };
        _viewModel.GetScrollOffset = () => GetDataGridScrollViewer()?.Offset.Y ?? 0;
        _viewModel.SetScrollOffset = (offset) =>
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                var sv = GetDataGridScrollViewer();
                if (sv != null)
                {
                    sv.Offset = new Vector(sv.Offset.X, offset);
                }
            }, Avalonia.Threading.DispatcherPriority.Background);
        };

        // Initialize UI state for the already-selected sidebar item
        _lastViewConfigKey = _viewModel.SelectedSidebarItem?.ViewConfigKey;
        var initialConfig = ListViewConfigs.Get(_lastViewConfigKey);
        if (initialConfig != null)
        {
            ApplyViewConfig(initialConfig);
        }

        var radioFilterPanel = this.FindControl<Controls.RadioFilterPanel>("RadioFilterPanel")!;
        radioFilterPanel.SyncRequested += () => _viewModel.LaunchRadioSync();

        var statusBar = this.FindControl<Controls.StatusBar>("MainStatusBar")!;
        statusBar.ErrorButtonClicked += async () => await _viewModel.ShowMessageLog();

        Loaded += async (s, e) =>
        {
#if WINDOWS
            var handle = TryGetPlatformHandle();
            if (handle != null)
            {
                _viewModel.InitializeSmtc(handle.Handle);
                _viewModel.InitializeThumbBar(handle.Handle);
            }
#endif

            await _viewModel.LoadAsync();

            // Restore view state after items are loaded — use Background priority
            // so the DataGrid has time to measure and render rows first
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                Avalonia.Threading.Dispatcher.UIThread.Post(() => RestoreViewState(_lastViewConfigKey), Avalonia.Threading.DispatcherPriority.Background);
            }, Avalonia.Threading.DispatcherPriority.Render);
        };
    }

    private async void ViewModel_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainWindowViewModel.IsSeekEnabled))
        {
            UpdateLiveBarAnimation();
            return;
        }

        if (e.PropertyName == nameof(MainWindowViewModel.CurrentTrackLine1) || e.PropertyName == nameof(MainWindowViewModel.CurrentTrackLine2))
        {
            RestartMarquees();
            return;
        }

        if (e.PropertyName != nameof(MainWindowViewModel.SelectedSidebarItem))
        {
            return;
        }

        // Save state of the view we're leaving
        SaveViewState();

        var sidebarItem = _viewModel.SelectedSidebarItem;
        var config = ListViewConfigs.Get(sidebarItem?.ViewConfigKey);

        if (config != null)
        {
            ApplyViewConfig(config);
        }

        // Restore state of the view we're entering (deferred until items are bound)
        _lastViewConfigKey = sidebarItem?.ViewConfigKey;
        Avalonia.Threading.Dispatcher.UIThread.Post(() => RestoreViewState(_lastViewConfigKey), Avalonia.Threading.DispatcherPriority.Render);

        // First-run only: if DB had no radio stations, fetch popular ones
        var kind = sidebarItem?.Kind;
        if (kind == MediaKind.Radio && _viewModel.FilteredItems.Count == 0 && !_viewModel.IsLoading)
        {
            await _viewModel.FetchPopularStationsAsync();
        }
    }

    private ScrollViewer? GetDataGridScrollViewer()
    {
        return MainDataGrid.FindDescendantOfType<ScrollViewer>();
    }

    private void SaveViewState()
    {
        if (_lastViewConfigKey == null)
        {
            return;
        }

        var sv = GetDataGridScrollViewer();
        var offset = sv?.Offset.Y ?? 0;
        var selectedId = _viewModel.SelectedItem?.Id;
        _viewStates[_lastViewConfigKey] = (offset, _viewModel.SelectedItem);

        // Persist to settings
        Settings.Set($"OrgZ.View.{_lastViewConfigKey}.Scroll", offset);
        Settings.Set($"OrgZ.View.{_lastViewConfigKey}.SelectedId", selectedId ?? string.Empty);
        Settings.Save();
    }

    private void RestoreViewState(string? key)
    {
        if (key == null)
        {
            return;
        }

        // Try in-memory state first, then fall back to persisted settings
        if (_viewStates.TryGetValue(key, out var state))
        {
            if (state.SelectedItem != null && _viewModel.FilteredItems.Contains(state.SelectedItem))
            {
                _viewModel.SelectedItem = state.SelectedItem;
            }

            var sv = GetDataGridScrollViewer();
            if (sv != null)
            {
                sv.Offset = new Vector(sv.Offset.X, state.ScrollOffset);
            }
            return;
        }

        // Restore from persisted settings (app restart)
        var savedScroll = Settings.Get($"OrgZ.View.{key}.Scroll", 0.0);
        var savedSelectedId = Settings.Get($"OrgZ.View.{key}.SelectedId", string.Empty);

        if (!string.IsNullOrEmpty(savedSelectedId))
        {
            var item = _viewModel.FilteredItems.FirstOrDefault(i => i.Id == savedSelectedId);
            if (item != null)
            {
                _viewModel.SelectedItem = item;
            }
        }

        if (savedScroll > 0)
        {
            var sv = GetDataGridScrollViewer();
            if (sv != null)
            {
                sv.Offset = new Vector(sv.Offset.X, savedScroll);
            }
        }
    }

    private void UpdateLiveBarAnimation()
    {
        _liveBarAnimationCts?.Cancel();
        _liveBarAnimationCts = null;

        var indicator = this.FindControl<Border>("LiveStreamIndicator");
        if (indicator == null)
        {
            return;
        }

        if (_viewModel.IsSeekEnabled)
        {
            indicator.RenderTransform = null;
            return;
        }

        // Animate: slide the glow bar back and forth across the track (420px wide, indicator is 120px)
        var animation = new Animation
        {
            Duration = TimeSpan.FromSeconds(2),
            IterationCount = IterationCount.Infinite,
            PlaybackDirection = PlaybackDirection.Alternate,
            Easing = new SineEaseInOut(),
            Children =
            {
                new KeyFrame
                {
                    Cue = new Cue(0),
                    Setters = { new Setter(TranslateTransform.XProperty, 0.0) }
                },
                new KeyFrame
                {
                    Cue = new Cue(1),
                    Setters = { new Setter(TranslateTransform.XProperty, 300.0) }
                },
            }
        };

        indicator.RenderTransform = new TranslateTransform();
        _liveBarAnimationCts = new CancellationTokenSource();
        animation.RunAsync(indicator, _liveBarAnimationCts.Token);
    }

    private void RestartMarquees()
    {
        _marquee1Cts?.Cancel();
        _marquee2Cts?.Cancel();

        var tb1 = this.FindControl<TextBlock>("TrackLine1");
        var tb2 = this.FindControl<TextBlock>("TrackLine2");
        const double containerWidth = 420;

        ResetMarqueeTextBlock(tb1);
        ResetMarqueeTextBlock(tb2);

        var cts = new CancellationTokenSource();
        _marquee1Cts = cts;
        _marquee2Cts = cts;

        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            if (cts.IsCancellationRequested)
            {
                return;
            }

            var overflow1 = MeasureOverflow(tb1, containerWidth);
            var overflow2 = MeasureOverflow(tb2, containerWidth);

            // Center or extend each line
            SetupMarqueeTextBlock(tb1, overflow1, containerWidth);
            SetupMarqueeTextBlock(tb2, overflow2, containerWidth);

            if (overflow1 <= 0 && overflow2 <= 0)
            {
                return;
            }

            // Both lines scroll at the same speed (40px/s).
            // Shorter one waits at each end for the longer one to finish.
            // Full cycle: 5s dwell -> scroll forward -> 5s dwell -> scroll back
            var maxOverflow = Math.Max(overflow1, overflow2);
            var maxScrollSec = maxOverflow / 40.0;
            var dwellSec = 5.0;
            var totalSec = 2 * dwellSec + 2 * maxScrollSec;

            if (overflow1 > 0 && tb1 != null)
            {
                RunSyncedMarquee(tb1, overflow1, totalSec, dwellSec, maxScrollSec, cts);
            }

            if (overflow2 > 0 && tb2 != null)
            {
                RunSyncedMarquee(tb2, overflow2, totalSec, dwellSec, maxScrollSec, cts);
            }
        }, Avalonia.Threading.DispatcherPriority.Render);
    }

    private static void ResetMarqueeTextBlock(TextBlock? tb)
    {
        if (tb == null)
        {
            return;
        }

        tb.RenderTransform = null;
        tb.Width = double.NaN;
    }

    private static double MeasureOverflow(TextBlock? tb, double containerWidth)
    {
        if (tb == null || string.IsNullOrEmpty(tb.Text))
        {
            return 0;
        }

        tb.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        return Math.Max(0, tb.DesiredSize.Width - containerWidth);
    }

    private static void SetupMarqueeTextBlock(TextBlock? tb, double overflow, double containerWidth)
    {
        if (tb == null || string.IsNullOrEmpty(tb.Text))
        {
            return;
        }

        if (overflow <= 0)
        {
            tb.RenderTransform = new TranslateTransform((containerWidth - tb.DesiredSize.Width) / 2, 0);
        }
        else
        {
            tb.Width = tb.DesiredSize.Width;
            tb.RenderTransform = new TranslateTransform();
        }
    }

    private static void RunSyncedMarquee(TextBlock tb, double overflow, double totalSec, double dwellSec, double maxScrollSec, CancellationTokenSource cts)
    {
        // This line scrolls at 40px/s for its own distance, then waits for the longer line.
        // Cycle: 5s dwell | scroll fwd | wait | 5s dwell | scroll back | wait
        var lineScrollSec = overflow / 40.0;

        double CueFrac(double seconds) => Math.Min(seconds / totalSec, 1.0);

        var animation = new Animation
        {
            Duration = TimeSpan.FromSeconds(totalSec),
            IterationCount = IterationCount.Infinite,
            Children =
            {
                // Dwell at start
                new KeyFrame { Cue = new Cue(0), Setters = { new Setter(TranslateTransform.XProperty, 0.0) } },
                new KeyFrame { Cue = new Cue(CueFrac(dwellSec)), Setters = { new Setter(TranslateTransform.XProperty, 0.0) } },
                // Scroll forward (arrives early if shorter, holds at -overflow)
                new KeyFrame { Cue = new Cue(CueFrac(dwellSec + lineScrollSec)), Setters = { new Setter(TranslateTransform.XProperty, -overflow) } },
                // Dwell at end (starts when the longer line finishes forward scroll)
                new KeyFrame { Cue = new Cue(CueFrac(dwellSec + maxScrollSec + dwellSec)), Setters = { new Setter(TranslateTransform.XProperty, -overflow) } },
                // Scroll back (arrives early if shorter, holds at 0)
                new KeyFrame { Cue = new Cue(CueFrac(dwellSec + maxScrollSec + dwellSec + lineScrollSec)), Setters = { new Setter(TranslateTransform.XProperty, 0.0) } },
                // Wait for longer line to finish scrolling back
                new KeyFrame { Cue = new Cue(1.0), Setters = { new Setter(TranslateTransform.XProperty, 0.0) } },
            }
        };

        animation.RunAsync(tb, cts.Token);
    }

    private void ApplyViewConfig(ListViewConfig config)
    {
        BuildColumns(config.Columns);
        BuildContextMenu(config.ContextMenuItems);
        RadioFilterPanel.IsVisible = config.ShowRadioFilterPanel;
    }

    private void BuildColumns(List<ColumnDef> columnDefs)
    {
        MainDataGrid.Columns.Clear();

        foreach (var def in columnDefs)
        {
            DataGridColumn col = def.Type switch
            {
                ColumnType.CheckBox => new DataGridCheckBoxColumn
                {
                    Header = def.Header,
                    Binding = new Binding(def.BindingPath),
                },
                ColumnType.PlayIndicator => new DataGridTemplateColumn
                {
                    Header = def.Header,
                    CellTemplate = new FuncDataTemplate<MediaItem>((item, _) =>
                    {
                        var icon = new Icon
                        {
                            Value = "fa-solid fa-play",
                            FontSize = 14,
                            HorizontalAlignment = HorizontalAlignment.Center,
                            VerticalAlignment = VerticalAlignment.Center,
                            Foreground = new SolidColorBrush(Color.Parse("#0096FF")),
                        };
                        icon.Bind(IsVisibleProperty, new Binding("IsPlaying"));
                        return icon;
                    }),
                },
                ColumnType.FavoriteTitle => new DataGridTemplateColumn
                {
                    Header = def.Header,
                    CellTemplate = new FuncDataTemplate<MediaItem>((item, _) =>
                    {
                        var starEmpty = new Icon
                        {
                            Value = "fa-regular fa-star",
                            FontSize = 12,
                            Opacity = 0.15,
                        };
                        starEmpty.Bind(IsVisibleProperty, new Binding("!IsFavorite"));

                        var starFilled = new Icon
                        {
                            Value = "fa-solid fa-star",
                            FontSize = 12,
                            Foreground = new SolidColorBrush(Colors.Gold),
                        };
                        starFilled.Bind(IsVisibleProperty, new Binding("IsFavorite"));

                        var starPanel = new Panel
                        {
                            Width = 14,
                            Height = 14,
                            Children = { starEmpty, starFilled },
                        };

                        var titleBlock = new TextBlock
                        {
                            TextTrimming = TextTrimming.CharacterEllipsis,
                            VerticalAlignment = VerticalAlignment.Center,
                        };
                        titleBlock.Bind(TextBlock.TextProperty, new Binding("Title"));

                        var stack = new StackPanel
                        {
                            Orientation = Orientation.Horizontal,
                            VerticalAlignment = VerticalAlignment.Center,
                            Spacing = 6,
                            Margin = new Thickness(4, 0),
                            Children = { starPanel, titleBlock },
                        };
                        return stack;
                    }),
                },
                ColumnType.Centered => new DataGridTemplateColumn
                {
                    Header = def.Header,
                    CellTemplate = new FuncDataTemplate<MediaItem>((item, _) =>
                    {
                        var tb = new TextBlock
                        {
                            HorizontalAlignment = HorizontalAlignment.Center,
                            VerticalAlignment = VerticalAlignment.Center,
                        };
                        tb.Bind(TextBlock.TextProperty, new Binding(def.BindingPath));
                        return tb;
                    }),
                },
                ColumnType.RightAligned => new DataGridTemplateColumn
                {
                    Header = def.Header,
                    CellTemplate = new FuncDataTemplate<MediaItem>((item, _) =>
                    {
                        var tb = new TextBlock
                        {
                            HorizontalAlignment = HorizontalAlignment.Right,
                            VerticalAlignment = VerticalAlignment.Center,
                            Margin = new Thickness(8, 0),
                        };
                        tb.Bind(TextBlock.TextProperty, new Binding(def.BindingPath));
                        return tb;
                    }),
                },
                _ => new DataGridTextColumn
                {
                    Header = def.Header,
                    Binding = new Binding(def.BindingPath),
                },
            };

            col.Width = new DataGridLength(def.WidthValue, def.WidthType);
            col.CanUserSort = def.CanUserSort;
            col.CanUserResize = def.CanUserResize;
            col.CanUserReorder = def.CanUserReorder;

            MainDataGrid.Columns.Add(col);
        }
    }

    private void BuildContextMenu(List<ContextMenuItemDef> defs)
    {
        var menu = new ContextMenu();
        BuildMenuItems(menu.Items, defs);
        MainDataGrid.ContextMenu = menu;
    }

    private void BuildMenuItems(Avalonia.Controls.ItemCollection items, List<ContextMenuItemDef> defs)
    {
        foreach (var def in defs)
        {
            if (def.IsSeparator)
            {
                items.Add(new Separator());
                continue;
            }

            var menuItem = new Avalonia.Controls.MenuItem
            {
                IsEnabled = def.IsEnabled,
            };

            if (def.IsHeader)
            {
                menuItem.IsHitTestVisible = false;
                menuItem.FontWeight = FontWeight.Bold;

                // Bind header text dynamically for title/artist display
                if (def.Header.StartsWith("{SelectedItem."))
                {
                    var path = def.Header.TrimStart('{').TrimEnd('}');
                    menuItem.Bind(Avalonia.Controls.MenuItem.HeaderProperty, new Binding(path) { FallbackValue = "(Unknown)" });
                }
                else
                {
                    menuItem.Header = def.Header;
                }
            }
            else
            {
                menuItem.Header = def.Header;
            }

            if (def.CommandName != null && _menuHandlers.TryGetValue(def.CommandName, out var handler))
            {
                menuItem.Click += handler;
            }

            if (def.Children is { Count: > 0 })
            {
                BuildMenuItems(menuItem.Items, def.Children);
            }

            items.Add(menuItem);
        }
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

    private void NowPlaying_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        _viewModel.NavigateToPlaying();
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
        SaveViewState();
        _viewModel.Dispose();
        base.OnClosed(e);
    }
}

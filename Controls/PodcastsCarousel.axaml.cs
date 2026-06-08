// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

using System.Collections;
using System.Collections.Specialized;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.VisualTree;
using OrgZ.Models;
using OrgZ.ViewModels;

namespace OrgZ.Controls;

/// <summary>
/// iTunes-style podcast carousel: a paged 4-tile view of <see cref="PodcastFeed"/>
/// items wrapped in a purple cartouche with a recessed white card. Pagination is
/// internal — clicking the prev / next arrows advances the page; the pagination
/// dots reflect how many pages exist. Click on a tile opens the feed via the
/// hosting <see cref="PodcastsViewModel"/>.
/// </summary>
public partial class PodcastsCarousel : UserControl
{
    private const int ItemsPerPage = 4;

    public static readonly StyledProperty<string> TitleProperty =
        AvaloniaProperty.Register<PodcastsCarousel, string>(nameof(Title), string.Empty);

    public static readonly StyledProperty<IEnumerable?> ItemsProperty =
        AvaloniaProperty.Register<PodcastsCarousel, IEnumerable?>(nameof(Items));

    public string Title
    {
        get => GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    public IEnumerable? Items
    {
        get => GetValue(ItemsProperty);
        set => SetValue(ItemsProperty, value);
    }

    private int _currentPage;
    private INotifyCollectionChanged? _watchedItems;

    // The slice of feeds currently bound to PageItemsHost. Used to suppress
    // redundant ItemsSource swaps (see RenderPage).
    private List<PodcastFeed>? _lastSlice;

    public PodcastsCarousel()
    {
        InitializeComponent();
        RenderPage();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == ItemsProperty)
        {
            if (_watchedItems is not null)
            {
                _watchedItems.CollectionChanged -= OnItemsCollectionChanged;
            }

            _watchedItems = Items as INotifyCollectionChanged;

            if (_watchedItems is not null)
            {
                _watchedItems.CollectionChanged += OnItemsCollectionChanged;
            }

            _currentPage = 0;
            RenderPage();
        }
    }

    private void OnItemsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        RenderPage();
    }

    /// <summary>
    /// Refreshes the visible page of items, the pagination dots, and the
    /// prev / next button enablement to reflect the current items collection
    /// and selected page index.
    /// </summary>
    private void RenderPage()
    {
        var all = Items?.OfType<PodcastFeed>().ToList() ?? new List<PodcastFeed>();
        var totalPages = Math.Max(1, (int)Math.Ceiling(all.Count / (double)ItemsPerPage));

        if (_currentPage < 0)
        {
            _currentPage = 0;
        }

        if (_currentPage >= totalPages)
        {
            _currentPage = totalPages - 1;
        }

        // Only swap the ItemsSource when the visible window actually changes.
        // During the initial store load the bound collection grows item-by-item,
        // firing CollectionChanged on every Add; without this guard each Add tore
        // down and rebuilt all four tile containers — blanking and re-fetching
        // their artwork — which read as flicker. Reserving the body height (XAML
        // MinHeight) stops the column reflow; this stops the tile flicker.
        var slice = all.Skip(_currentPage * ItemsPerPage).Take(ItemsPerPage).ToList();
        if (_lastSlice is null || !slice.SequenceEqual(_lastSlice))
        {
            PageItemsHost.ItemsSource = slice;
            _lastSlice = slice;
        }

        var activeBrush = Application.Current?.FindResource("OrgZCarouselDotActiveBrush") as IBrush ?? Brushes.DarkGray;
        var inactiveBrush = Application.Current?.FindResource("OrgZCarouselDotInactiveBrush") as IBrush ?? Brushes.LightGray;
        var dots = new List<IBrush>(totalPages);
        for (int i = 0; i < totalPages; i++)
        {
            dots.Add(i == _currentPage ? activeBrush : inactiveBrush);
        }
        PageDots.ItemsSource = dots;

        PrevButton.IsEnabled = _currentPage > 0 && all.Count > 0;
        NextButton.IsEnabled = _currentPage < totalPages - 1 && all.Count > 0;
    }

    private void Prev_Click(object? sender, RoutedEventArgs e)
    {
        _currentPage--;
        RenderPage();
    }

    private void Next_Click(object? sender, RoutedEventArgs e)
    {
        _currentPage++;
        RenderPage();
    }

    /// <summary>
    /// Opens the clicked feed via the hosting <see cref="PodcastsViewModel"/>.
    /// The VM is found by walking up the visual tree because the carousel's
    /// own DataContext may already be the VM (inherited) or null at design
    /// time; we accept whichever ancestor carries it.
    /// </summary>
    private async void Item_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Button b && b.Tag is PodcastFeed feed && FindViewModel() is { } vm)
        {
            await vm.OpenFeedAsync(feed);
        }
    }

    private async void SeeAll_Click(object? sender, RoutedEventArgs e)
    {
        if (FindViewModel() is { } vm)
        {
            var feeds = Items?.OfType<PodcastFeed>().ToList() ?? new List<PodcastFeed>();
            await vm.ShowFeedList(Title, feeds);
        }
    }

    private PodcastsViewModel? FindViewModel()
    {
        Avalonia.Visual? cur = this;
        while (cur is not null)
        {
            if (cur is StyledElement el && el.DataContext is PodcastsViewModel vm)
            {
                return vm;
            }
            cur = cur.GetVisualParent();
        }
        return null;
    }
}

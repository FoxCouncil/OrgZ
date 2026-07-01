// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

using Avalonia;
using Avalonia.Controls;

namespace OrgZ.Controls;

/// <summary>
/// Side cards for the podcast store header section. Mirrors the chrome of
/// <see cref="PodcastsCarousel"/> - same surface, header strip, divider, and
/// recessed shadow - but with just a title and an empty body slot. Lets the
/// "Categories" and "Top Podcasts" columns flank the central carousel without
/// duplicating the chrome XAML.
/// </summary>
public partial class PodcastsCard : UserControl
{
    public static readonly StyledProperty<string> TitleProperty =
        AvaloniaProperty.Register<PodcastsCard, string>(nameof(Title), string.Empty);

    public static readonly StyledProperty<object?> BodyContentProperty =
        AvaloniaProperty.Register<PodcastsCard, object?>(nameof(BodyContent));

    public static readonly StyledProperty<object?> HeaderContentProperty =
        AvaloniaProperty.Register<PodcastsCard, object?>(nameof(HeaderContent));

    public string Title
    {
        get => GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    public object? BodyContent
    {
        get => GetValue(BodyContentProperty);
        set => SetValue(BodyContentProperty, value);
    }

    /// <summary>Optional right-aligned content in the card's header strip (e.g. a "Show All" link).</summary>
    public object? HeaderContent
    {
        get => GetValue(HeaderContentProperty);
        set => SetValue(HeaderContentProperty, value);
    }

    public PodcastsCard()
    {
        InitializeComponent();
    }
}

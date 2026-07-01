// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

using Avalonia;
using Avalonia.Controls;

namespace OrgZ.Controls;

/// <summary>
/// Shared frame for the view header bars (CD, iPod device, playlist/Favorites). Owns the single
/// Border + art|body|actions layout (plus an optional bottom footer) and the label/value text
/// styles, so spacing / alignment / styling changes happen here ONCE instead of in three
/// near-identical controls. Each view fills the <see cref="Art"/>, <see cref="Body"/>,
/// <see cref="Actions"/> and optional <see cref="Footer"/> slots with its own content; bindings in
/// that content resolve against the hosting view's DataContext as usual.
/// </summary>
public partial class MediaHeaderBar : UserControl
{
    public static readonly StyledProperty<object?> ArtProperty =
        AvaloniaProperty.Register<MediaHeaderBar, object?>(nameof(Art));

    public static readonly StyledProperty<object?> BodyProperty =
        AvaloniaProperty.Register<MediaHeaderBar, object?>(nameof(Body));

    public static readonly StyledProperty<object?> ActionsProperty =
        AvaloniaProperty.Register<MediaHeaderBar, object?>(nameof(Actions));

    public static readonly StyledProperty<object?> FooterProperty =
        AvaloniaProperty.Register<MediaHeaderBar, object?>(nameof(Footer));

    /// <summary>Side length of the square art tile on the left.</summary>
    public static readonly StyledProperty<double> ArtSizeProperty =
        AvaloniaProperty.Register<MediaHeaderBar, double>(nameof(ArtSize), 72d);

    public MediaHeaderBar()
    {
        InitializeComponent();
    }

    public object? Art { get => GetValue(ArtProperty); set => SetValue(ArtProperty, value); }
    public object? Body { get => GetValue(BodyProperty); set => SetValue(BodyProperty, value); }
    public object? Actions { get => GetValue(ActionsProperty); set => SetValue(ActionsProperty, value); }
    public object? Footer { get => GetValue(FooterProperty); set => SetValue(FooterProperty, value); }
    public double ArtSize { get => GetValue(ArtSizeProperty); set => SetValue(ArtSizeProperty, value); }
}

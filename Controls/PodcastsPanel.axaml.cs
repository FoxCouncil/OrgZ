// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

using Avalonia.Controls;

namespace OrgZ.Controls;

/// <summary>
/// Thin shell that hosts the three podcast sub-views (Store, Subscriptions,
/// FeedDetail) and swaps between them via IsVisible bindings on the inner
/// UserControls. All interaction logic lives in the sub-views; this class
/// exists only so XAML can place the shell as a single named control inside
/// MainWindow.
/// </summary>
public partial class PodcastsPanel : UserControl
{
    public PodcastsPanel()
    {
        InitializeComponent();
    }
}

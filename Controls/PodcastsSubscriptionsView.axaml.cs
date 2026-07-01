// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

using Avalonia.Controls;
using Avalonia.Interactivity;
using OrgZ.Models;
using OrgZ.ViewModels;

namespace OrgZ.Controls;

public partial class PodcastsSubscriptionsView : UserControl
{
    public PodcastsSubscriptionsView()
    {
        InitializeComponent();
    }

    private PodcastsViewModel? ViewModel => DataContext as PodcastsViewModel;

    private async void SubscriptionTile_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: PodcastSubscription sub } && ViewModel is { } vm)
        {
            await vm.OpenSubscriptionAsync(sub);
        }
    }
}

// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

using Avalonia.Controls;
using Avalonia.Interactivity;
using OrgZ.Models;
using OrgZ.ViewModels;

namespace OrgZ.Controls;

/// <summary>
/// The golden audiobooks store surface - landing sections + search + book detail. The library
/// grid that sits under it lives in MainWindow (the normal FilteredItems pipeline), not here.
/// </summary>
public partial class AudiobooksPanel : UserControl
{
    public AudiobooksPanel()
    {
        InitializeComponent();
    }

    private AudiobooksViewModel? Vm => DataContext as AudiobooksViewModel;

    private void BookTile_Click(object? sender, RoutedEventArgs e)
    {
        if (Vm is { } vm && (sender as Control)?.Tag is AudiobookListing book)
        {
            _ = vm.OpenBookAsync(book);
        }
    }
}

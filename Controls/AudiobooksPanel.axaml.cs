// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

using Avalonia.Controls;
using Avalonia.Input;
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

    private void StoreResult_DoubleTapped(object? sender, TappedEventArgs e)
    {
        if (Vm is { } vm && (sender as DataGrid)?.SelectedItem is AudiobookListing book)
        {
            _ = vm.OpenBookAsync(book);
        }
    }

    private void OwnedBook_Click(object? sender, RoutedEventArgs e)
    {
        // A row in the store-page picture list: clicking it plays or downloads the book.
        if (Vm is { } vm && (sender as Control)?.Tag is OwnedBook book)
        {
            vm.ActivateOwnedBook(book);
        }
    }

    private void OwnedBook_DoubleTapped(object? sender, TappedEventArgs e)
    {
        // A row in the full-view DataGrid: same gesture - play or download.
        if (Vm is { } vm && (sender as DataGrid)?.SelectedItem is OwnedBook book)
        {
            vm.ActivateOwnedBook(book);
        }
    }

    private void LibroBook_Click(object? sender, RoutedEventArgs e)
    {
        // A purchased tile IS its download gesture; an already-downloaded one has nothing to do.
        if (Vm is { } vm && (sender as Control)?.Tag is LibroBookRow { IsDownloaded: false } row)
        {
            _ = vm.DownloadLibroBookAsync(row.Book);
        }
    }

    private void LibroRegister_Click(object? sender, RoutedEventArgs e)
    {
        // Checkout / sign-up has no API and stays on libro.fm - open it in the browser.
        // (Swap in an Awin affiliate membership link here once approved.)
        OrgZ.Helpers.HtmlInlinesBuilder.OpenUrl("https://libro.fm/users/new");
    }
}

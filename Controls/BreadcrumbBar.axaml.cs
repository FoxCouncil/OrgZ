// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

using Avalonia.Controls;
using Avalonia.Interactivity;

namespace OrgZ.Controls;

public partial class BreadcrumbBar : UserControl
{
    public event Action? RootClicked;
    public event Action? ArtistClicked;

    public BreadcrumbBar()
    {
        InitializeComponent();
    }

    public void Update(DrillDownState? state)
    {
        if (state == null)
        {
            return;
        }

        var showArtist = state.Level >= DrillDownLevel.Albums && state.SelectedArtist != null;
        var showAlbum = state.Level >= DrillDownLevel.Songs && state.SelectedAlbum != null;

        Sep1.IsVisible = showArtist;
        ArtistButton.IsVisible = showArtist;
        ArtistButton.Content = state.SelectedArtist;

        Sep2.IsVisible = showAlbum;
        AlbumLabel.IsVisible = showAlbum;
        AlbumLabel.Text = state.SelectedAlbum;
    }

    private void Root_Click(object? sender, RoutedEventArgs e)
    {
        RootClicked?.Invoke();
    }

    private void Artist_Click(object? sender, RoutedEventArgs e)
    {
        ArtistClicked?.Invoke();
    }
}

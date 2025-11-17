// Copyright (c) 2025 Fox Diller

using Avalonia.Controls;
using OrgZ.Models;
using OrgZ.ViewModels;

namespace OrgZ;

public partial class MainWindow : Window
{
    private readonly MainWindowViewModel _viewModel;

    public MainWindow()
    {
        InitializeComponent();

        DataContext = _viewModel = new MainWindowViewModel(this);

        // Load files asynchronously after window is loaded
        Loaded += async (s, e) => await _viewModel.LoadAsync();
    }

    private void DoubleClickSongRow(object? sender, Avalonia.Input.TappedEventArgs e)
    {
        DataGrid? grid = sender as DataGrid;

        AudioFileInfo? item = grid?.SelectedItem as AudioFileInfo;

        _viewModel.PlayAudioFile(item);
    }

    private void ButtonBackTrack(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
    }

    private void ButtonPlay(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        _viewModel.Play();
    }

    private void ButtonPause(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        _viewModel.Pause();
    }

    private void ButtonStop(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        _viewModel.Stop();
    }

    private void ButtonNextTrack(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
    }
}
// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using OrgZ.ViewModels;
using Velopack;
using Velopack.Sources;

namespace OrgZ;

public partial class App : Application
{
    internal static string Version { get; } = typeof(App).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

    private const string SettingKey_FolderPath = "OrgZ.FolderPath";

    internal static string FolderPath = Settings.Get<string>(SettingKey_FolderPath) ?? string.Empty;

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);

#if DEBUG
        this.AttachDeveloperTools();
#endif
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            StartupTrace.Mark("avalonia init");

            var mainWindow = new MainWindow();
            StartupTrace.Mark("main window built");

            desktop.MainWindow = mainWindow;

            // The number that actually matters: when the user first sees something. Every
            // phase before this is invisible to them, which is why a slow one reads as a
            // crash.
            mainWindow.Opened += (_, _) => StartupTrace.ReportOnce("window shown");

            UpdateTitle(mainWindow);

            // Defer the first-run folder picker until the main window is actually shown.
            // On Linux the picker goes through xdg-desktop-portal over D-Bus; blocking
            // synchronously here (the old `.GetAwaiter().GetResult()` path) deadlocks the
            // UI thread and the main window never gets mapped.
            if (FolderPath == string.Empty)
            {
                mainWindow.Opened += OnMainWindowOpenedPickFolder;
            }
        }

        // Quiet check only: it sets the Help-menu label and does nothing else. No download,
        // no elevation, no prompt - the user starts the install when they choose to.
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime { MainWindow.DataContext: MainWindowViewModel vm })
        {
            _ = vm.RefreshUpdateStatusAsync();
        }

        base.OnFrameworkInitializationCompleted();
    }

    private static void UpdateTitle(Window window)
    {
        window.Title = FolderPath != string.Empty ? $"OrgZ v{Version} - {FolderPath}" : $"OrgZ v{Version} - [No folder selected]";
    }

    private async void OnMainWindowOpenedPickFolder(object? sender, EventArgs e)
    {
        if (sender is not Window window)
        {
            return;
        }

        window.Opened -= OnMainWindowOpenedPickFolder;

        var folders = await window.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Select OrgZ Folder",
            AllowMultiple = false,
        });

        if (folders == null || folders.Count == 0)
        {
            return;
        }

        FolderPath = folders[0].Path.LocalPath ?? string.Empty;
        Settings.Set(SettingKey_FolderPath, FolderPath);
        Settings.Save();
        UpdateTitle(window);
    }

    // Update checking now lives in UpdateService, reached from the Help menu. This used to
    // download in the background and let Velopack apply on a later start - which, once the
    // app installs to Program Files, means an unexplained UAC prompt at some future launch.
    // The check is quiet; the user decides when to install.

    private async void AboutMenuItem_Click(object? sender, EventArgs e)
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop
            && desktop.MainWindow?.DataContext is MainWindowViewModel vm)
        {
            await vm.ShowAbout();
        }
    }

    private void QuitMenuItem_Click(object? sender, EventArgs e)
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.Shutdown();
        }
    }

}
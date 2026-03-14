// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

using Avalonia;
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
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow = new MainWindow();

            if (FolderPath == string.Empty)
            {
                IStorageFolder? folder = AskForDirectory(desktop);

                FolderPath = folder?.Path.LocalPath ?? string.Empty;

                Settings.Set(SettingKey_FolderPath, FolderPath);
                Settings.Save();
            }

            desktop.MainWindow.Title = FolderPath != string.Empty ? $"OrgZ v{Version} - {FolderPath}" : $"OrgZ v{Version} - [No folder selected]";
        }

        _ = Task.Run(CheckForUpdatesAsync);

        base.OnFrameworkInitializationCompleted();
    }

    private static async Task CheckForUpdatesAsync()
    {
        try
        {
            var source = new GithubSource("https://github.com/FoxCouncil/OrgZ", null, false, null);
            var mgr = new UpdateManager(source, null, null);

            if (!mgr.IsInstalled)
            {
                return;
            }

            var update = await mgr.CheckForUpdatesAsync();

            if (update != null)
            {
                await mgr.DownloadUpdatesAsync(update, null, default);
            }
        }
        catch
        {
            // Update failures should never crash the app
        }
    }

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

    private static IStorageFolder? AskForDirectory(IClassicDesktopStyleApplicationLifetime desktop)
    {
        if (desktop == null || desktop.MainWindow == null)
        {
            return null;
        }

        IStorageProvider storage = desktop.MainWindow.StorageProvider;

        IReadOnlyList<IStorageFolder> selectedFolders = storage.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "Select OrgZ Folder", AllowMultiple = false }).GetAwaiter().GetResult();

        if (selectedFolders == null || selectedFolders.Count == 0)
        {
            return null;
        }

        return selectedFolders[0];
    }
}
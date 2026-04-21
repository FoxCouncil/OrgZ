// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

namespace OrgZ.Tests;

/// <summary>
/// Shares the "Settings" collection so xUnit serializes against SettingsTests /
/// ColumnStateStoreTests / etc. - all four mutate the global Settings override
/// and would race under the default class-parallel scheduler.
/// </summary>
[Collection("Settings")]
public class WindowSizeTrackerTests : IDisposable
{
    private readonly string _tempDir;

    public WindowSizeTrackerTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "OrgZ-WST-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        Settings.OverrideSettingsDirectory(_tempDir);
        WindowSizeTracker.ReloadForTesting();
    }

    public void Dispose()
    {
        Settings.OverrideSettingsDirectory(null);
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, recursive: true);
        }
    }

    [Fact]
    public void GetSaved_Returns_Null_Until_SetSaved_Is_Called()
    {
        Assert.Null(WindowSizeTracker.GetSaved("ghost"));

        WindowSizeTracker.SetSaved("ghost", 900, 600);

        var back = WindowSizeTracker.GetSaved("ghost");
        Assert.NotNull(back);
        Assert.Equal(900, back!.Value.Width);
        Assert.Equal(600, back.Value.Height);
    }

    [Fact]
    public void Saved_Sizes_Persist_Across_Reload()
    {
        WindowSizeTracker.SetSaved("Settings", 620, 540);
        WindowSizeTracker.SetSaved("Main", 1200, 760);

        WindowSizeTracker.ReloadForTesting();

        Assert.Equal(620, WindowSizeTracker.GetSaved("Settings")!.Value.Width);
        Assert.Equal(760, WindowSizeTracker.GetSaved("Main")!.Value.Height);
    }

    [Fact]
    public void ResetAll_Clears_All_Saved_Sizes()
    {
        WindowSizeTracker.SetSaved("A", 100, 100);
        WindowSizeTracker.SetSaved("B", 200, 200);

        WindowSizeTracker.ResetAll();

        Assert.Null(WindowSizeTracker.GetSaved("A"));
        Assert.Null(WindowSizeTracker.GetSaved("B"));
    }

    [Fact]
    public void ResetAll_Survives_Reload()
    {
        WindowSizeTracker.SetSaved("X", 999, 888);
        WindowSizeTracker.ResetAll();
        WindowSizeTracker.ReloadForTesting();

        Assert.Null(WindowSizeTracker.GetSaved("X"));
    }

    [Fact]
    public void SetSaved_Overwrites_Previous_Size_For_Same_Key()
    {
        WindowSizeTracker.SetSaved("MediaInfo", 560, 500);
        WindowSizeTracker.SetSaved("MediaInfo", 720, 640);

        var back = WindowSizeTracker.GetSaved("MediaInfo")!.Value;
        Assert.Equal(720, back.Width);
        Assert.Equal(640, back.Height);
    }
}

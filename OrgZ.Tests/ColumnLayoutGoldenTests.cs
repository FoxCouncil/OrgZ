// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

using System.Text;

namespace OrgZ.Tests;

/// <summary>
/// A golden snapshot of the columns EVERY view shows: order, header text, binding, type, width,
/// default visibility, and the sort/resize/reorder permissions.
///
/// This is a UX guard, not a design statement. The column definitions carry a lot of duplication
/// that's worth consolidating, and consolidation is exactly the kind of change that silently
/// shifts a width or drops a column from one view while looking correct in the diff. Anything
/// that changes what a user sees has to change this file too, deliberately.
/// </summary>
public sealed class ColumnLayoutGoldenTests
{
    private static string Describe(ListViewConfig config)
    {
        var sb = new StringBuilder();
        foreach (var c in config.Columns)
        {
            var perms = $"{(c.CanUserSort ? "s" : "-")}{(c.CanUserResize ? "r" : "-")}{(c.CanUserReorder ? "o" : "-")}";
            var width = c.WidthType switch
            {
                Avalonia.Controls.DataGridLengthUnitType.Auto => "auto",
                Avalonia.Controls.DataGridLengthUnitType.Star => $"{c.WidthValue:0.##}*",
                _ => $"{c.WidthValue:0}px",
            };
            sb.Append("  ")
              .Append(c.Header.Length == 0 ? "(blank)" : c.Header)
              .Append(" | ").Append(c.BindingPath)
              .Append(" | ").Append(c.Type)
              .Append(" | ").Append(width)
              .Append(" | ").Append(perms)
              .Append(c.IsDefaultVisible ? "" : " | hidden")
              .Append(c.StringFormat is null ? "" : $" | fmt={c.StringFormat}")
              .Append('\n');
        }

        return sb.ToString();
    }

    private static string Snapshot()
    {
        var sb = new StringBuilder();

        void Add(string name, ListViewConfig config)
        {
            sb.Append(name).Append(" [host=").Append(config.Host)
              .Append(config.GroupByPath is null ? "" : $", group={config.GroupByPath}")
              .Append("]\n").Append(Describe(config));
        }

        foreach (var key in new[] { "Music", "Radio", "Favorites", "BadFormat", "CdAudio", "Podcasts", "Audiobooks" })
        {
            Add(key, ListViewConfigs.Get(key)!);
        }

        Add("Device", ListViewConfigs.BuildDeviceConfig(@"E:\"));
        Add("Share", ListViewConfigs.BuildShareConfig("share-key"));
        foreach (var kind in new[] { MediaKind.Music, MediaKind.Podcast, MediaKind.Audiobook })
        {
            Add($"DeviceKind:{kind}", ListViewConfigs.BuildDeviceKindConfig(@"E:\", kind));
        }

        Add("DevicePlaylist", ListViewConfigs.BuildDevicePlaylistConfig("dev-pl", ["a", "b"]));
        Add("DevicePlaylists", ListViewConfigs.BuildDevicePlaylistsConfig(@"E:\"));
        Add("Playlist", ListViewConfigs.BuildPlaylistConfig(1, ["a", "b"]));

        return sb.ToString();
    }

    /// <summary>
    /// The committed golden. Kept as a file rather than a string constant so a change to what the
    /// user sees shows up as a readable diff in review. Regenerate deliberately with
    /// <c>ORGZ_UPDATE_COLUMN_GOLDEN=1</c>, then read the diff before committing it.
    /// </summary>
    private static string GoldenPath()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "OrgZ.Tests.csproj")))
        {
            dir = Path.GetDirectoryName(dir);
        }

        Assert.NotNull(dir);
        return Path.Combine(dir!, "Fixtures", "column-layout.golden.txt");
    }

    [Fact]
    public void Every_view_shows_the_columns_it_has_always_shown()
    {
        var actual = Snapshot().Replace("\r\n", "\n");
        var path = GoldenPath();

        if (Environment.GetEnvironmentVariable("ORGZ_UPDATE_COLUMN_GOLDEN") == "1")
        {
            File.WriteAllText(path, actual);
        }

        Assert.True(File.Exists(path), $"missing golden file: {path}");
        Assert.Equal(File.ReadAllText(path).Replace("\r\n", "\n"), actual);
    }
}

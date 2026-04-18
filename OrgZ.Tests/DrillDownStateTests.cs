// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

using System.ComponentModel;

namespace OrgZ.Tests;

public class DrillDownStateTests
{
    [Fact]
    public void Defaults_to_Artists_level_with_no_selections()
    {
        var state = new DrillDownState();
        Assert.Equal(DrillDownLevel.Artists, state.Level);
        Assert.Null(state.SelectedArtist);
        Assert.Null(state.SelectedAlbum);
    }

    [Fact]
    public void Level_setter_round_trips()
    {
        var state = new DrillDownState();
        foreach (DrillDownLevel level in Enum.GetValues<DrillDownLevel>())
        {
            state.Level = level;
            Assert.Equal(level, state.Level);
        }
    }

    [Fact]
    public void Selection_properties_round_trip()
    {
        var state = new DrillDownState
        {
            Level = DrillDownLevel.Albums,
            SelectedArtist = "Rush",
            SelectedAlbum = "Signals",
        };

        Assert.Equal(DrillDownLevel.Albums, state.Level);
        Assert.Equal("Rush", state.SelectedArtist);
        Assert.Equal("Signals", state.SelectedAlbum);
    }

    [Fact]
    public void Setting_Level_raises_PropertyChanged()
    {
        var state = new DrillDownState();
        var raised = new List<string?>();
        state.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        state.Level = DrillDownLevel.Albums;

        Assert.Contains(nameof(DrillDownState.Level), raised);
    }

    [Fact]
    public void Setting_same_value_does_not_raise_PropertyChanged()
    {
        // CommunityToolkit's [ObservableProperty] suppresses PropertyChanged when the
        // value didn't actually change — this is the standard MVVM convention and the
        // rest of the codebase relies on it (for example to avoid feedback loops in
        // sidebar selection bindings).
        var state = new DrillDownState { Level = DrillDownLevel.Albums };
        var raised = new List<string?>();
        state.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        state.Level = DrillDownLevel.Albums;   // unchanged
        Assert.Empty(raised);
    }
}

public class DrillDownEntryTests
{
    [Fact]
    public void Required_GroupKey_propagates_through_initializer()
    {
        var entry = new DrillDownEntry { GroupKey = "Rush" };
        Assert.Equal("Rush", entry.GroupKey);
        Assert.Equal(string.Empty, entry.SecondaryInfo);
        Assert.Equal(0, entry.ItemCount);
        Assert.Equal(TimeSpan.Zero, entry.TotalDuration);
    }

    [Fact]
    public void All_properties_set_via_initializer()
    {
        var entry = new DrillDownEntry
        {
            GroupKey = "Signals",
            SecondaryInfo = "8 tracks · 39:24",
            ItemCount = 8,
            TotalDuration = TimeSpan.FromSeconds(2364),
        };

        Assert.Equal("Signals", entry.GroupKey);
        Assert.Equal("8 tracks · 39:24", entry.SecondaryInfo);
        Assert.Equal(8, entry.ItemCount);
        Assert.Equal(TimeSpan.FromSeconds(2364), entry.TotalDuration);
    }
}

// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

namespace OrgZ.Tests;

public class ActivityItemTests
{
    [Theory]
    [InlineData(ActivityStatus.Pending, "fa-solid fa-clock")]
    [InlineData(ActivityStatus.Running, "fa-solid fa-spinner")]
    [InlineData(ActivityStatus.Completed, "fa-solid fa-check")]
    [InlineData(ActivityStatus.Failed, "fa-solid fa-xmark")]
    public void StatusIcon_MapsToCorrectIcon(ActivityStatus status, string expectedIcon)
    {
        var item = new ActivityItem { Status = status };
        Assert.Equal(expectedIcon, item.StatusIcon);
    }

    [Theory]
    [InlineData(ActivityStatus.Pending, "#888888")]
    [InlineData(ActivityStatus.Running, "#4A9EFF")]
    [InlineData(ActivityStatus.Completed, "#4CAF50")]
    [InlineData(ActivityStatus.Failed, "OrangeRed")]
    public void StatusColor_MapsToCorrectColor(ActivityStatus status, string expectedColor)
    {
        var item = new ActivityItem { Status = status };
        Assert.Equal(expectedColor, item.StatusColor);
    }

    [Fact]
    public void NewItem_HasUniqueId()
    {
        var a = new ActivityItem();
        var b = new ActivityItem();
        Assert.NotEqual(a.Id, b.Id);
    }

    [Fact]
    public void NewItem_DefaultsToPending()
    {
        var item = new ActivityItem();
        Assert.Equal(ActivityStatus.Pending, item.Status);
    }

    [Fact]
    public void NewItem_HasCreatedAtTimestamp()
    {
        var before = DateTime.UtcNow;
        var item = new ActivityItem();
        Assert.True(item.CreatedAt >= before);
    }

    [Fact]
    public void Progress_DefaultsToNull()
    {
        var item = new ActivityItem();
        Assert.Null(item.Progress);
    }

    [Fact]
    public void Error_DefaultsToNull()
    {
        var item = new ActivityItem();
        Assert.Null(item.Error);
    }

    [Fact]
    public void MutableProperties_CanBeUpdated()
    {
        var item = new ActivityItem
        {
            Title = "Copying files",
            Detail = "1 of 10",
            Status = ActivityStatus.Running,
            Progress = 0.1,
        };

        item.Detail = "5 of 10";
        item.Progress = 0.5;
        item.Status = ActivityStatus.Completed;

        Assert.Equal("5 of 10", item.Detail);
        Assert.Equal(0.5, item.Progress);
        Assert.Equal(ActivityStatus.Completed, item.Status);
    }
}

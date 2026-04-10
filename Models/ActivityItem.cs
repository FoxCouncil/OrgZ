// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

namespace OrgZ.Models;

public enum ActivityStatus
{
    Pending,
    Running,
    Completed,
    Failed
}

public partial class ActivityItem : ObservableObject
{
    public Guid Id { get; init; } = Guid.NewGuid();

    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;

    [ObservableProperty]
    private string _title = string.Empty;

    [ObservableProperty]
    private string _detail = string.Empty;

    [ObservableProperty]
    private ActivityStatus _status = ActivityStatus.Pending;

    [ObservableProperty]
    private double? _progress;

    [ObservableProperty]
    private string? _error;

    public string StatusIcon => Status switch
    {
        ActivityStatus.Pending => "fa-solid fa-clock",
        ActivityStatus.Running => "fa-solid fa-spinner",
        ActivityStatus.Completed => "fa-solid fa-check",
        ActivityStatus.Failed => "fa-solid fa-xmark",
        _ => "fa-solid fa-question"
    };

    public string StatusColor => Status switch
    {
        ActivityStatus.Completed => "#4CAF50",
        ActivityStatus.Failed => "OrangeRed",
        ActivityStatus.Running => "#4A9EFF",
        _ => "#888888"
    };
}

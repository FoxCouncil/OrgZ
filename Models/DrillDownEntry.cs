// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

namespace OrgZ.Models;

public class DrillDownEntry
{
    public required string GroupKey { get; init; }
    public string SecondaryInfo { get; init; } = string.Empty;
    public int ItemCount { get; init; }
    public TimeSpan TotalDuration { get; init; }
}

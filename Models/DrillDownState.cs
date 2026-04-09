// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

namespace OrgZ.Models;

public partial class DrillDownState : ObservableObject
{
    [ObservableProperty]
    private DrillDownLevel _level = DrillDownLevel.Artists;

    [ObservableProperty]
    private string? _selectedArtist;

    [ObservableProperty]
    private string? _selectedAlbum;
}

// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

using Avalonia.Controls;

namespace OrgZ.Controls;

/// <summary>
/// Play-column glyph for the CD view: shows the rip status of the bound
/// <see cref="OrgZ.Models.MediaItem"/> - grey static spinner (queued), black
/// spinning spinner (ripping), green check (done) - with the blue play icon
/// taking priority when the row is playing. Bindings resolve against the cell's
/// MediaItem DataContext.
/// </summary>
public partial class RipStatusIndicator : UserControl
{
    public RipStatusIndicator()
    {
        InitializeComponent();
    }
}

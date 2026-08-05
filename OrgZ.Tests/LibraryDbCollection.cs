// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

namespace OrgZ.Tests;

/// <summary>
/// Every test class that redirects or writes <c>library.db</c> belongs here.
///
/// MediaCache, PodcastCache and AcquisitionStore keep their tables in one file and resolve it
/// through a single <see cref="OrgZ.Services.LibraryDb"/> locator, so the service adopting its
/// owner's library moves all three together. That shared global state means their test classes
/// can't run in parallel any more; when each store carried its own path they could sit in
/// separate collections.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class LibraryDbCollection
{
    public const string Name = "LibraryDb";
}

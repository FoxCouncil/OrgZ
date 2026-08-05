// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

namespace OrgZ.Tests;

/// <summary>
/// Every test class that redirects or writes <c>library.db</c> belongs here.
///
/// MediaCache, PodcastCache and AcquisitionStore all keep their tables in ONE file and now
/// resolve it through one <see cref="OrgZ.Services.LibraryDb"/> locator - which is the point:
/// the service adopting its owner's library has to move all three together, and three private
/// copies of the path meant only MediaCache moved. The flip side is that their test classes
/// share global state, so they can no longer run in parallel with each other - previously each
/// store had its own path and the collections could be separate ("MediaCache" /
/// "AcquisitionStore" / none). One collection, no parallelism, no clobbering.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class LibraryDbCollection
{
    public const string Name = "LibraryDb";
}

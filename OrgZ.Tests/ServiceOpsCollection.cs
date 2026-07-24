// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

namespace OrgZ.Tests;

/// <summary>
/// Serializes the test classes that drive the background service's singletons - the
/// disc/sync busy gates and their in-flight job records. xUnit runs classes in parallel
/// by default, and a second class starting a job mid-assertion makes "is exactly one job
/// running?" flaky in a way that has nothing to do with the code under test. Same lesson
/// the Settings-directory race taught: shared global state needs an explicit collection.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public class ServiceOpsCollection
{
    public const string Name = "service-ops";
}

// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

using Xunit;

namespace OrgZ.Tests;

/// <summary>
/// Serializes the tests that bind real sockets, run libvlc, or drive the process-global
/// <c>ShareStreamRelay</c>. xUnit runs test CLASSES in parallel by default, and these
/// share mutable process state (the relay's static listener + share map) and compete for
/// the same loopback/multicast resources - concurrently, on a multi-NIC dev box, they
/// deadlock. Each passes fine alone; the collection makes the suite run them one at a
/// time, which is how the pre-commit hook stays reliable.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class RealSocketCollection
{
    public const string Name = "RealSockets";
}

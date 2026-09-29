using Xunit;

namespace DeskArcade.Tests;

/// <summary>
/// The tests that talk over real UDP sockets wait for replies in real time (a hello within a few seconds, a session
/// that times out after three). On a busy two-core CI runner the CPU-heavy tests running beside them could hold up
/// the links' background loops for longer than that, so these collections run on their own, after the others.
/// </summary>
[CollectionDefinition("lan", DisableParallelization = true)]
public sealed class LanCollection
{
}

[CollectionDefinition("room", DisableParallelization = true)]
public sealed class RoomCollection
{
}

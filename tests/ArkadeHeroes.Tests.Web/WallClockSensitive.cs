namespace ArkadeHeroes.Tests.Web;

/// <summary>
/// The collection for tests that measure WALL-CLOCK behaviour — how many requests land in a window, that a
/// timer stopped — rather than what some markup says.
///
/// <para>xUnit runs test classes in parallel by default, which is right for almost everything here: a bUnit
/// render is CPU-bound and independent. It is wrong for a test whose assertion is a RATE. Such a test is
/// measuring elapsed time, and elapsed time on a two-core CI runner is shared with whatever else is
/// rendering — so a tick that should have been dispatched and finished inside the window straggles past the
/// end of it and is counted as evidence of the very leak the test exists to detect.</para>
///
/// <para>Marking the collection non-parallel is the fix at the cause: the measurement is taken while
/// nothing else is competing for the clock. It deliberately does NOT touch any threshold — a timing test
/// made to pass by widening its tolerance stops being able to see the defect, which is the opposite of what
/// is wanted from the one test that proves a page does not leave a request loop running behind it.</para>
///
/// <para>The cost is that these run alone rather than alongside the rest. That is a few milliseconds on a
/// suite that finishes in five seconds, and it buys a green signal that means something.</para>
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public class WallClockSensitive
{
    public const string Name = "wall-clock-sensitive";
}

using System.Collections.Concurrent;
using ArkadeHeroes.Chain.Covenants;
using NArk.Arkade.Emulator;

namespace ArkadeHeroes.Tests;

/// <summary>
/// The emulator client is cached per endpoint because every covenant flow reaches for one and a
/// fresh <see cref="HttpClient"/> per call leaks sockets.
///
/// <para>What these tests DO pin: every caller of a given endpoint observes the same client, and
/// distinct endpoints stay separate. What they deliberately do NOT claim is that the factory ran
/// exactly once — that is the <c>Lazy(ExecutionAndPublication)</c> guarantee, and it is not
/// observable from here. <c>GetOrAdd</c> publishes one winning value whichever way the factory is
/// written, so a test asserting "all callers got the same instance" passes against the naive form
/// too; the cost of losing that guarantee is the DISCARDED clients, which the returned reference
/// cannot see. Checked by reverting the mode and watching this file stay green.</para>
/// </summary>
public class EmulatorEndpointTests
{
    [Fact]
    public void EveryConcurrentCallerObservesTheSameClient()
    {
        var url = $"http://localhost:7073/{Guid.NewGuid():N}/";
        var seen = new ConcurrentBag<EmulatorClient>();

        Parallel.For(0, 256, _ => seen.Add(EmulatorEndpoint.Client(url)));

        Assert.Equal(256, seen.Count);
        Assert.Single(seen.Distinct(ReferenceEqualityComparer.Instance));
    }

    [Fact]
    public void TheSameEndpointResolvesToTheSameClientHoweverItIsSpelled()
    {
        var root = $"http://localhost:7073/{Guid.NewGuid():N}";

        Assert.Same(EmulatorEndpoint.Client(root), EmulatorEndpoint.Client(root + "/"));
        Assert.Same(EmulatorEndpoint.Client(root), EmulatorEndpoint.Client(new Uri(root + "/")));
    }

    [Fact]
    public void DifferentEndpointsDoNotShareAClient()
    {
        var a = EmulatorEndpoint.Client($"http://localhost:7073/{Guid.NewGuid():N}/");
        var b = EmulatorEndpoint.Client($"http://localhost:7074/{Guid.NewGuid():N}/");

        Assert.NotSame(a, b);
    }
}

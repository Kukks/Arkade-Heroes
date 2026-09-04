using System.Collections.Concurrent;
using Microsoft.Extensions.Options;
using NArk.Arkade.Emulator;

namespace ArkadeHeroes.Chain.Covenants;

/// <summary>
/// Builds the SDK's <see cref="EmulatorClient"/> for a bare endpoint URI, for the covenant
/// flows that reach the emulator outside DI (probes, refund/reclaim flows, the chain service).
/// </summary>
public static class EmulatorEndpoint
{
    // One client per endpoint: the flows below are short-lived and called repeatedly, and a
    // fresh HttpClient each time leaks sockets in TIME_WAIT.
    private static readonly ConcurrentDictionary<string, EmulatorClient> Clients = new();

    public static EmulatorClient Client(Uri baseAddress)
    {
        ArgumentNullException.ThrowIfNull(baseAddress);
        return Client(baseAddress.ToString());
    }

    public static EmulatorClient Client(string baseAddress)
    {
        var root = baseAddress.TrimEnd('/') + "/";
        return Clients.GetOrAdd(root, static url => new EmulatorClient(
            new HttpClient(),
            Options.Create(new EmulatorClientOptions { ServerUrl = url })));
    }
}

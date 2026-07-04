using CliWrap;
using CliWrap.Buffered;

namespace ArkadeHeroes.Tests.E2E;

/// <summary>
/// Drives the denigiri regtest CLI (external/dotnet-sdk/regtest/regtest.mjs).
/// The stack's `start` seeds an `ark` client wallet inside the arkd container,
/// which we use as the test faucet for offchain funds.
/// </summary>
public static class RegtestHelper
{
    public static readonly Uri ArkdEndpoint = new("http://localhost:7070");

    /// <summary>Walks up from the test assembly to the repo root (contains regtest/regtest.mjs — the arkade-regtest master submodule).</summary>
    public static string FindRepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir, "regtest", "regtest.mjs")))
                return dir;
            dir = Path.GetDirectoryName(dir);
        }
        throw new InvalidOperationException(
            $"Could not locate regtest/regtest.mjs walking up from {AppContext.BaseDirectory}");
    }

    /// <summary>
    /// Runs `node regtest/regtest.mjs <args...>` from the repo root (the
    /// arkade-regtest@master submodule; .env.regtest is auto-discovered).
    /// No --env flag: the `ark`/`arkd` passthrough commands docker-exec into the
    /// running containers and reject unknown flags.
    /// </summary>
    public static async Task<string> RegtestCli(string[] args, CancellationToken ct = default)
    {
        var repoRoot = FindRepoRoot();

        var result = await Cli.Wrap("node")
            .WithArguments(["regtest/regtest.mjs", .. args])
            .WithWorkingDirectory(repoRoot)
            .WithValidation(CommandResultValidation.None)
            .ExecuteBufferedAsync(ct);

        if (result.ExitCode != 0)
            throw new InvalidOperationException(
                $"regtest.mjs {string.Join(' ', args)} failed (exit {result.ExitCode}): " +
                $"{result.StandardError.Trim()} {result.StandardOutput.Trim()}");
        return result.StandardOutput.Trim();
    }

    /// <summary>
    /// Sends offchain sats from the seeded ark client wallet to an Arkade
    /// address. The faucet's VTXOs expire on regtest (~hourly); on
    /// "not enough funds" we renew them once via a batch settle and retry.
    /// </summary>
    public static async Task<string> ArkSend(string arkadeAddress, long amountSats, CancellationToken ct = default)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await RegtestCli(["ark", "send", "--to", arkadeAddress, "--amount", amountSats.ToString(), "--password", "secret"], ct);
            }
            catch (InvalidOperationException ex) when (attempt < 3 && ex.Message.Contains("not enough funds"))
            {
                await RegtestCli(["ark", "settle", "--password", "secret"], ct);
            }
            catch (InvalidOperationException ex) when (attempt < 3 && ex.Message.Contains("ALREADY_SPENT"))
            {
                // Stale local coin cache after a concurrent send — re-selection heals it.
                await Task.Delay(2000, ct);
            }
        }
    }

    /// <summary>
    /// Mines n regtest blocks. Needed by timelocked (CLTV) spends: finality is
    /// judged against the chain's median-time-past, which only advances with
    /// fresh blocks — on a quiet regtest chain MTP lags wall-clock badly.
    /// </summary>
    public static Task<string> MineAsync(int blocks, CancellationToken ct = default)
        => RegtestCli(["mine", blocks.ToString()], ct);

    /// <summary>
    /// The chain's median-time-past (getblockchaininfo.mediantime). Consensus
    /// guarantees tip time &gt; MTP, so waiting for MTP ≥ a CLTV expiry is
    /// sufficient for arkd's forfeit-closure gate under either blocktime
    /// semantic (tip time or MTP).
    /// </summary>
    public static async Task<long> GetMedianTimeAsync(CancellationToken ct = default)
    {
        var json = await RegtestCli(["rpc", "getblockchaininfo"], ct);
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        return doc.RootElement.GetProperty("mediantime").GetInt64();
    }

    /// <summary>
    /// Mines one block at a time until the chain's median-time-past reaches
    /// <paramref name="unixSeconds"/>. Timelocked covenant spends must wait for
    /// this BEFORE submitting: arkd records a failure event for a refused
    /// submission, which permanently poisons that (deterministic) txid's event
    /// stream — a later accepted resubmission is never projected to the VTXO set.
    /// </summary>
    public static async Task WaitForChainTimeAsync(long unixSeconds, TimeSpan timeout, CancellationToken ct = default)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (await GetMedianTimeAsync(ct) >= unixSeconds) return;
            await MineAsync(1, ct);
            await Task.Delay(500, ct);
        }
        throw new TimeoutException($"Chain median time did not reach {unixSeconds} within {timeout}.");
    }

    /// <summary>Waits until arkd reports ready (wallet unlocked and synced).</summary>
    public static async Task WaitForArkdReadyAsync(TimeSpan timeout)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        var deadline = DateTime.UtcNow + timeout;
        string last = "unreachable";
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                var response = await http.GetAsync($"{ArkdEndpoint}v1/info");
                var body = await response.Content.ReadAsStringAsync();
                if (response.IsSuccessStatusCode && !body.Contains("not ready"))
                    return;
                last = body;
            }
            catch (Exception ex)
            {
                last = ex.Message;
            }
            await Task.Delay(2000);
        }
        throw new InvalidOperationException(
            "arkd is not ready. Start the regtest stack first (repo root):\n" +
            "  node regtest/regtest.mjs start --profile ark --profile emulator\n" +
            $"Last status: {last}");
    }
}

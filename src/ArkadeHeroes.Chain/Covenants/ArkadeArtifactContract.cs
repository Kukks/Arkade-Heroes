using NArk.Abstractions.Contracts;
using NArk.Abstractions.Extensions;
using NArk.Abstractions.Scripts;
using NArk.Core.Scripts;
using NBitcoin;
using NBitcoin.Scripting;

namespace ArkadeHeroes.Chain.Covenants;

/// <summary>
/// One named covenant spending path: its Arkade Script bytecode (fixed at
/// instantiation — the tweak binds to these exact bytes) and the witness the
/// packet entry expects at spend time. An optional <paramref name="LockTime"/>
/// puts a CLTV gate INTO THE TAPLEAF (enforced on the checkpoint spend by the
/// operator) — arkade-script-level CLTV is unusable because arkd derives the
/// canonical ark tx with locktime 0.
/// </summary>
public sealed record ArkadeContractFunction(string Name, byte[] ArkadeScript, LockTime? LockTime = null);

/// <summary>
/// An Arkade covenant contract instantiated from compiled/authored artifact
/// functions — the game's named replacement for anonymous generic contracts.
/// Each function becomes one collaborative tapleaf:
///
///   &lt;tweak(emulatorKey, fnScript)&gt; OP_CHECKSIGVERIFY &lt;operatorKey&gt; OP_CHECKSIG
///
/// where the emulator key is tweaked per function by its Arkade Script
/// (<see cref="ArkadeScriptTweak"/>), matching the emulator's
/// <c>ReadArkadeScript</c> leaf validation. Function arguments are NEVER baked
/// into the script (that would break the funding-time tweak); they ride the
/// EmulatorPacket entry witness as the VM's initial stack.
/// </summary>
public class ArkadeArtifactContract : ArkContract
{
    private readonly Dictionary<string, (ArkadeContractFunction Function, ScriptBuilder Leaf)> _functions;
    private readonly string _contractName;

    public ArkadeArtifactContract(
        string contractName,
        OutputDescriptor server,
        string emulatorSignerKeyHex,
        IReadOnlyList<ArkadeContractFunction> functions) : base(server)
    {
        if (functions.Count == 0)
            throw new ArgumentException("An artifact contract needs at least one function.", nameof(functions));

        _contractName = contractName;
        var serverKey = server.ToXOnlyPubKey();
        _functions = new Dictionary<string, (ArkadeContractFunction, ScriptBuilder)>();
        foreach (var function in functions)
        {
            var tweaked = ArkadeScriptTweak
                .ComputeCovenantPublicKey(emulatorSignerKeyHex, function.ArkadeScript)
                .ToXOnlyPubKey();
            ScriptBuilder condition = new NofNMultisigTapScript([tweaked]);
            if (function.LockTime is { } lockTime)
                condition = new CompositeTapScript(new LockTimeTapScript(lockTime), condition);
            var leaf = new CollaborativePathArkTapScript(serverKey, condition);
            _functions[function.Name] = (function, leaf);
        }
    }

    public override string Type => "arkade-artifact";

    public override ContractScope DefaultScope => ContractScope.Offchain;

    public IReadOnlyCollection<string> FunctionNames => _functions.Keys;

    /// <summary>The covenant bytecode for a function — what goes in the EmulatorPacket entry.</summary>
    public byte[] ScriptFor(string functionName) => Get(functionName).Function.ArkadeScript;

    /// <summary>The tapleaf builder for a function — what the spending coin uses.</summary>
    public ScriptBuilder LeafFor(string functionName) => Get(functionName).Leaf;

    private (ArkadeContractFunction Function, ScriptBuilder Leaf) Get(string functionName)
        => _functions.TryGetValue(functionName, out var entry)
            ? entry
            : throw new ArgumentException($"Contract '{_contractName}' has no function '{functionName}'.");

    protected override IEnumerable<ScriptBuilder> GetScriptBuilders()
        => _functions.Values.Select(v => v.Leaf);

    protected override Dictionary<string, string> GetContractData() => new()
    {
        ["contractName"] = _contractName,
        ["functions"] = string.Join(",", _functions.Keys),
    };
}

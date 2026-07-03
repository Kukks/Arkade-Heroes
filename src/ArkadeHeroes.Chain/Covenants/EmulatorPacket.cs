using NArk.Core.Assets;

namespace ArkadeHeroes.Chain.Covenants;

/// <summary>One input's covenant reveal: which vin, its Arkade Script, and the script's witness stack.</summary>
public sealed record EmulatorEntry(ushort Vin, byte[] Script, IReadOnlyList<byte[]> Witness);

/// <summary>
/// The Emulator Packet — TLV type 0x01 inside the ARK extension OP_RETURN —
/// carrying, per input, the Arkade Script bytecode and witness the emulator
/// must execute before co-signing with its script-tweaked key. Wire format
/// ported from arkade-os/emulator <c>pkg/arkade/emulator_packet.go</c>:
///
///   varint(entryCount)
///   per entry: vin(u16 LE) ‖ varint(scriptLen) ‖ script
///              ‖ varint(witnessBlobLen) ‖ [varint(items) ‖ (varint(len) ‖ item)…]
///
/// Plugs into NArk's <see cref="Extension"/> (magic "ARK" 0x41524B) alongside
/// asset packets.
/// </summary>
public sealed class EmulatorPacket : IExtensionPacket
{
    public const byte TypeByte = 0x01;
    public const int MaxEntryCount = 1_000;
    public const int MaxScriptLength = 10_000;

    public IReadOnlyList<EmulatorEntry> Entries { get; }

    public EmulatorPacket(IReadOnlyList<EmulatorEntry> entries)
    {
        if (entries.Count == 0)
            throw new ArgumentException("An emulator packet needs at least one entry.", nameof(entries));
        if (entries.Count > MaxEntryCount)
            throw new ArgumentException($"Too many entries ({entries.Count} > {MaxEntryCount}).", nameof(entries));
        var seen = new HashSet<ushort>();
        foreach (var entry in entries)
        {
            if (entry.Script.Length == 0)
                throw new ArgumentException($"Empty script for vin {entry.Vin}.", nameof(entries));
            if (entry.Script.Length > MaxScriptLength)
                throw new ArgumentException($"Script too large for vin {entry.Vin}.", nameof(entries));
            if (!seen.Add(entry.Vin))
                throw new ArgumentException($"Duplicate vin {entry.Vin}.", nameof(entries));
        }
        Entries = entries;
    }

    public byte PacketType => TypeByte;

    public byte[] SerializePacketData()
    {
        var writer = new BufferWriter();
        writer.WriteVarInt((ulong)Entries.Count);
        foreach (var entry in Entries)
        {
            writer.WriteUint16LE(entry.Vin);
            writer.WriteVarSlice(entry.Script);

            // Witness serialized exactly like psbt.WriteTxWitness: varint item
            // count, then varint-length-prefixed items — and that whole blob is
            // itself varint-length-prefixed in the entry.
            var witness = new BufferWriter();
            witness.WriteVarInt((ulong)entry.Witness.Count);
            foreach (var item in entry.Witness)
                witness.WriteVarSlice(item);
            writer.WriteVarSlice(witness.ToBytes());
        }
        return writer.ToBytes();
    }
}

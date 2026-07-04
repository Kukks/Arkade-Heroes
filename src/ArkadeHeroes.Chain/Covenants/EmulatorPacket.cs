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
        // CRITICAL framing detail: the packet's INNER fields use Bitcoin
        // CompactSize (Go `wire.WriteVarInt` / `psbt.WriteTxWitness`), NOT the
        // LEB128 varints NArk's BufferWriter/Extension use for the outer TLV.
        // The two encodings coincide below 128, which hides the difference
        // until scripts/witnesses grow (observed as emulator-side
        // "unexpected EOF" / garbage execution).
        var writer = new BufferWriter();
        WriteCompactSize(writer, (ulong)Entries.Count);
        foreach (var entry in Entries)
        {
            writer.WriteUint16LE(entry.Vin);
            WriteCompactSize(writer, (ulong)entry.Script.Length);
            writer.Write(entry.Script);

            // Witness serialized exactly like psbt.WriteTxWitness: CompactSize
            // item count, then CompactSize-length-prefixed items — and that
            // whole blob is itself CompactSize-length-prefixed in the entry.
            var witness = new BufferWriter();
            WriteCompactSize(witness, (ulong)entry.Witness.Count);
            foreach (var item in entry.Witness)
            {
                WriteCompactSize(witness, (ulong)item.Length);
                witness.Write(item);
            }
            var witnessBytes = witness.ToBytes();
            WriteCompactSize(writer, (ulong)witnessBytes.Length);
            writer.Write(witnessBytes);
        }
        return writer.ToBytes();
    }

    private static void WriteCompactSize(BufferWriter writer, ulong value)
    {
        switch (value)
        {
            case < 0xfd:
                writer.WriteByte((byte)value);
                break;
            case <= 0xffff:
                writer.WriteByte(0xfd);
                writer.WriteUint16LE((ushort)value);
                break;
            case <= 0xffffffff:
                writer.WriteByte(0xfe);
                writer.Write(BitConverter.GetBytes((uint)value));
                break;
            default:
                writer.WriteByte(0xff);
                writer.Write(BitConverter.GetBytes(value));
                break;
        }
    }
}

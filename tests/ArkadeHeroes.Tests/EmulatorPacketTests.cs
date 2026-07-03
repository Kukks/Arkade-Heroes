using ArkadeHeroes.Chain.Covenants;
using NArk.Core.Assets;

namespace ArkadeHeroes.Tests;

public class EmulatorPacketTests
{
    [Fact]
    public void SerializesToTheGoWireFormat()
    {
        // Hand-computed against emulator/pkg/arkade/emulator_packet.go:
        //   count=1 → 0x01
        //   vin=1 u16LE → 0x01 0x00
        //   script=[0x51] → len 0x01 + 0x51
        //   witness=[[0xAA,0xBB]] → blob = count 0x01 + len 0x02 + AA BB → blobLen 0x04 + blob
        var packet = new EmulatorPacket([new EmulatorEntry(1, [0x51], [[0xAA, 0xBB]])]);
        Assert.Equal("010100015104010 2AABB".Replace(" ", ""),
            Convert.ToHexString(packet.SerializePacketData()));
    }

    [Fact]
    public void EmptyWitnessSerializes()
    {
        // witness blob = count 0x00 → blobLen 0x01 + 0x00
        var packet = new EmulatorPacket([new EmulatorEntry(0, [0x51, 0x52], [])]);
        Assert.Equal("01000002515201 00".Replace(" ", ""),
            Convert.ToHexString(packet.SerializePacketData()));
    }

    [Fact]
    public void ValidationRejectsBadPackets()
    {
        Assert.Throws<ArgumentException>(() => new EmulatorPacket([]));
        Assert.Throws<ArgumentException>(() => new EmulatorPacket([new EmulatorEntry(0, [], [])]));
        Assert.Throws<ArgumentException>(() =>
            new EmulatorPacket([new EmulatorEntry(1, [0x51], []), new EmulatorEntry(1, [0x52], [])]));
    }

    [Fact]
    public void RidesInsideTheArkExtension()
    {
        var packet = new EmulatorPacket([new EmulatorEntry(0, [0x51], [[0x01]])]);
        var extension = new Extension([packet]);
        var payload = extension.Serialize();

        // OP_RETURN script whose payload starts with the ARK magic; the TLV
        // type byte 0x01 follows, then the varint length and our data.
        var script = NBitcoin.Script.FromBytesUnsafe(payload);
        Assert.True(Extension.IsExtension(script));

        var parsed = Extension.FromScript(script);
        var record = Assert.IsType<UnknownPacket>(
            parsed.Packets.Single(p => p.PacketType == EmulatorPacket.TypeByte));
        Assert.Equal(
            Convert.ToHexString(packet.SerializePacketData()),
            Convert.ToHexString(record.Data));
    }
}

using System.Buffers.Binary;
using System.IO.Compression;

namespace Trimūrti.Server.Protocol;

/// <summary>Implements the observed Aether Gazer TCP frame envelope. It does not decode game messages.</summary>
public sealed record GameFrame(uint Command, ushort Index, ushort ServerIndex, byte Version, byte[] Payload);

public static class GameFrameCodec
{
    public const int UpstreamHeaderSize = 13;
    public const int DownstreamHeaderSize = 11;
    private const int MaxPayloadSize = 8 * 1024 * 1024;

    public static bool TryReadUpstreamLength(ReadOnlySpan<byte> prefix, out int totalLength)
    {
        totalLength = 0;
        if (prefix.Length < 4) return false;
        var bodyLength = BinaryPrimitives.ReadUInt32LittleEndian(prefix);
        if (bodyLength < UpstreamHeaderSize - 4 || bodyLength > MaxPayloadSize + UpstreamHeaderSize - 4)
            throw new InvalidDataException($"Longitud de trama no válida: {bodyLength}.");
        totalLength = checked((int)bodyLength + 4);
        return true;
    }

    public static GameFrame ParseUpstream(ReadOnlySpan<byte> frame)
    {
        if (!TryReadUpstreamLength(frame, out var total) || frame.Length != total)
            throw new InvalidDataException("Trama incompleta o longitud incoherente.");
        var command = BinaryPrimitives.ReadUInt32LittleEndian(frame[5..9]);
        var index = BinaryPrimitives.ReadUInt16LittleEndian(frame[9..11]);
        var server = BinaryPrimitives.ReadUInt16LittleEndian(frame[11..13]);
        var payload = MaybeInflate(frame[UpstreamHeaderSize..]);
        return new GameFrame(command, index, server, frame[4], payload);
    }

    public static byte[] PackDownstream(uint command, ReadOnlySpan<byte> payload, ushort index = 0, ushort serverIndex = 0)
    {
        if (command > 0x1FFFF) throw new ArgumentOutOfRangeException(nameof(command));
        var bodyPayload = payload.ToArray();
        if (bodyPayload.Length > MaxPayloadSize) throw new InvalidDataException("Payload excede el máximo permitido.");
        if (bodyPayload.Length > 10_240) bodyPayload = Deflate(bodyPayload);
        var frameBodyLength = checked(9 + bodyPayload.Length);
        if (frameBodyLength > ushort.MaxValue) throw new InvalidDataException("La trama excede el límite del protocolo.");
        var frame = new byte[frameBodyLength + 2];
        BinaryPrimitives.WriteUInt16BigEndian(frame, (ushort)frameBodyLength);
        frame[2] = IsZlib(bodyPayload) ? (byte)1 : (byte)0;
        frame[3] = 0;
        frame[4] = command > ushort.MaxValue ? (byte)1 : (byte)0;
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(5), (ushort)command);
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(7), index);
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(9), serverIndex);
        bodyPayload.CopyTo(frame.AsSpan(DownstreamHeaderSize));
        return frame;
    }

    private static byte[] MaybeInflate(ReadOnlySpan<byte> payload)
    {
        if (!IsZlib(payload)) return payload.ToArray();
        using var input = new MemoryStream(payload.ToArray());
        using var zlib = new ZLibStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        var buffer = new byte[8192];
        int read;
        while ((read = zlib.Read(buffer)) > 0)
        {
            if (output.Length + read > MaxPayloadSize) throw new InvalidDataException("Payload descomprimido excede el máximo.");
            output.Write(buffer, 0, read);
        }
        return output.ToArray();
    }

    private static byte[] Deflate(byte[] bytes)
    {
        using var output = new MemoryStream();
        using (var zlib = new ZLibStream(output, CompressionLevel.Optimal, leaveOpen: true)) zlib.Write(bytes);
        var compressed = output.ToArray();
        return compressed.Length < bytes.Length ? compressed : bytes;
    }

    private static bool IsZlib(ReadOnlySpan<byte> payload) => payload.Length >= 2 && payload[0] == 0x78 && payload[1] is 0x01 or 0x5e or 0x9c or 0xda;
}

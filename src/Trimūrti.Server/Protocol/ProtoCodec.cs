using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace Trimūrti.Server.Protocol;

/// <summary>Schema-driven protobuf wire codec for the messages in the local protocol catalogue.</summary>
public sealed class ProtoCodec(ProtoSchema schema)
{
    private const int MaxDepth = 12;
    private const int MaxFieldLength = 65_536;
    private const int MaxMessageLength = 8 * 1024 * 1024;

    public Dictionary<string, object?> Decode(string messageName, ReadOnlySpan<byte> payload) => DecodeCore(messageName, payload, 0);

    public byte[] Encode(string messageName, IReadOnlyDictionary<string, object?> values) => EncodeCore(messageName, values, 0);

    private Dictionary<string, object?> DecodeCore(string messageName, ReadOnlySpan<byte> data, int depth)
    {
        var result = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (depth > MaxDepth) { result["_truncated"] = true; return result; }
        if (data.Length > MaxMessageLength) throw new InvalidDataException("Protobuf message exceeds size limit.");
        schema.TryGetFields(messageName, out var fields);
        fields ??= [];
        var offset = 0;
        while (offset < data.Length)
        {
            var tag = ReadVarint(data, ref offset);
            var number = checked((int)(tag >> 3));
            var wire = (int)(tag & 7);
            if (number == 0) throw new InvalidDataException("Protobuf field number zero is invalid.");
            fields.TryGetValue(number, out var field);
            object? value;
            if (wire == 0)
                value = InterpretVarint(field?.Type, ReadVarint(data, ref offset));
            else if (wire == 1)
            {
                Ensure(data, offset, 8);
                var raw = BinaryPrimitives.ReadUInt64LittleEndian(data[offset..]); offset += 8;
                value = field?.Type switch { "double" => BitConverter.UInt64BitsToDouble(raw), "sfixed64" => unchecked((long)raw), _ => raw };
            }
            else if (wire == 5)
            {
                Ensure(data, offset, 4);
                var raw = BinaryPrimitives.ReadUInt32LittleEndian(data[offset..]); offset += 4;
                value = field?.Type switch { "float" => BitConverter.UInt32BitsToSingle(raw), "sfixed32" => unchecked((int)raw), _ => raw };
            }
            else if (wire == 2)
            {
                var length = ReadVarint(data, ref offset);
                if (length > (ulong)(data.Length - offset)) throw new InvalidDataException("Protobuf length-delimited field exceeds message.");
                var actualLength = checked((int)length);
                var retained = Math.Min(actualLength, MaxFieldLength);
                var chunk = data.Slice(offset, retained);
                offset += actualLength;
                if (field is not null && field.Label == "repeated" && IsPackable(field.Type))
                {
                    var packed = new List<object?>();
                    var packedOffset = 0;
                    while (packedOffset < chunk.Length) packed.Add(DecodePackedValue(field.Type, chunk, ref packedOffset));
                    value = packed;
                }
                else if (field?.Type == "message")
                    value = DecodeCore(field.Reference ?? "", chunk, depth + 1);
                else if (field?.Type == "string")
                    value = Encoding.UTF8.GetString(chunk);
                else if (field?.Type == "bytes")
                    value = Convert.ToHexString(chunk).ToLowerInvariant();
                else
                    value = DecodeUnknownBytes(chunk);
                if (actualLength > retained && value is Dictionary<string, object?> nested) nested["_truncated"] = true;
            }
            else throw new InvalidDataException($"Unsupported protobuf wire type {wire} at field {number}.");

            var key = field?.Name ?? $"f{number}";
            if (field?.Label == "repeated")
            {
                if (!result.TryGetValue(key, out var prior) || prior is not List<object?> list)
                    result[key] = list = [];
                if (value is List<object?> packedValues) list.AddRange(packedValues); else list.Add(value);
            }
            else result[key] = value;
        }
        return result;
    }

    private byte[] EncodeCore(string messageName, IReadOnlyDictionary<string, object?> values, int depth)
    {
        if (depth > MaxDepth) throw new InvalidDataException("Protobuf nesting limit exceeded.");
        var fields = schema.ResolveFieldsForEncoding(messageName, values.Keys);
        var nameMap = fields.Values.ToDictionary(f => f.Name, f => f, StringComparer.Ordinal);
        using var output = new MemoryStream();
        foreach (var (key, value) in values)
        {
            if (key.StartsWith('_')) continue;
            ProtoField? field = null;
            if (int.TryParse(key, NumberStyles.None, CultureInfo.InvariantCulture, out var number)) fields.TryGetValue(number, out field);
            else nameMap.TryGetValue(key, out field);
            if (field is null && key.Length > 1 && key[0] == 'f' && int.TryParse(key.AsSpan(1), out number))
                field = new ProtoField(number, key, InferType(value), "", null);
            if (field is null) continue;
            if (value is null) continue;
            if (field.Label == "repeated" && value is System.Collections.IEnumerable sequence && value is not string && value is not byte[])
            {
                foreach (var item in sequence) if (item is not null) WriteField(output, field, item, depth);
            }
            else WriteField(output, field, value, depth);
            if (output.Length > MaxMessageLength) throw new InvalidDataException("Encoded protobuf message exceeds size limit.");
        }
        return output.ToArray();
    }

    private void WriteField(Stream output, ProtoField field, object value, int depth)
    {
        var type = field.Type;
        var wire = WireType(type);
        WriteVarint(output, ((ulong)field.Number << 3) | (uint)wire);
        if (wire == 0) WriteVarint(output, EncodeVarint(type, value));
        else if (wire == 1)
        {
            Span<byte> bytes = stackalloc byte[8];
            var raw = type switch { "double" => BitConverter.DoubleToUInt64Bits(Convert.ToDouble(value, CultureInfo.InvariantCulture)), "sfixed64" => unchecked((ulong)Convert.ToInt64(value, CultureInfo.InvariantCulture)), _ => Convert.ToUInt64(value, CultureInfo.InvariantCulture) };
            BinaryPrimitives.WriteUInt64LittleEndian(bytes, raw); output.Write(bytes);
        }
        else if (wire == 5)
        {
            Span<byte> bytes = stackalloc byte[4];
            var raw = type switch { "float" => BitConverter.SingleToUInt32Bits(Convert.ToSingle(value, CultureInfo.InvariantCulture)), "sfixed32" => unchecked((uint)Convert.ToInt32(value, CultureInfo.InvariantCulture)), _ => Convert.ToUInt32(value, CultureInfo.InvariantCulture) };
            BinaryPrimitives.WriteUInt32LittleEndian(bytes, raw); output.Write(bytes);
        }
        else
        {
            byte[] bytes;
            if (type == "message" && value is IReadOnlyDictionary<string, object?> nested)
                bytes = EncodeCore(field.Reference ?? "", nested, depth + 1);
            else if (type == "bytes" && value is string hex && IsHex(hex)) bytes = Convert.FromHexString(hex);
            else if (type == "bytes" && value is byte[] rawBytes) bytes = rawBytes;
            else bytes = Encoding.UTF8.GetBytes(Convert.ToString(value, CultureInfo.InvariantCulture) ?? "");
            WriteVarint(output, (ulong)bytes.Length); output.Write(bytes);
        }
    }

    private static object DecodePackedValue(string type, ReadOnlySpan<byte> data, ref int offset)
    {
        var wire = WireType(type);
        if (wire == 0) return InterpretVarint(type, ReadVarint(data, ref offset))!;
        if (wire == 5) { Ensure(data, offset, 4); var raw = BinaryPrimitives.ReadUInt32LittleEndian(data[offset..]); offset += 4; return type == "float" ? BitConverter.UInt32BitsToSingle(raw) : raw; }
        if (wire == 1) { Ensure(data, offset, 8); var raw = BinaryPrimitives.ReadUInt64LittleEndian(data[offset..]); offset += 8; return type == "double" ? BitConverter.UInt64BitsToDouble(raw) : raw; }
        throw new InvalidDataException("Invalid packed protobuf field type.");
    }

    private static object? InterpretVarint(string? type, ulong value) => type switch
    {
        "bool" => value != 0,
        "sint32" or "sint64" => unchecked((long)(value >> 1) ^ -((long)value & 1)),
        "int32" or "enum" => unchecked((int)value),
        "int64" => unchecked((long)value),
        _ => value
    };

    private static ulong EncodeVarint(string type, object value)
    {
        if (type == "bool") return Convert.ToBoolean(value, CultureInfo.InvariantCulture) ? 1UL : 0UL;
        if (type is "sint32" or "sint64") { var signed = Convert.ToInt64(value, CultureInfo.InvariantCulture); return unchecked((ulong)((signed << 1) ^ (signed >> 63))); }
        if (type is "int32" or "int64" or "enum") return unchecked((ulong)Convert.ToInt64(value, CultureInfo.InvariantCulture));
        return Convert.ToUInt64(value, CultureInfo.InvariantCulture);
    }

    private static int WireType(string type) => type switch
    {
        "double" or "fixed64" or "sfixed64" => 1,
        "string" or "bytes" or "message" => 2,
        "float" or "fixed32" or "sfixed32" => 5,
        _ => 0
    };

    private static bool IsPackable(string type) => type is not ("string" or "bytes" or "message");
    private static string InferType(object? value) => value switch { bool => "bool", int or long or uint or ulong => "int64", _ => "string" };
    private static string DecodeUnknownBytes(ReadOnlySpan<byte> bytes)
    {
        try { return new UTF8Encoding(false, true).GetString(bytes); }
        catch (DecoderFallbackException) { return Convert.ToHexString(bytes).ToLowerInvariant(); }
    }
    private static bool IsHex(string value) => value.Length % 2 == 0 && value.All(Uri.IsHexDigit);
    private static void Ensure(ReadOnlySpan<byte> data, int offset, int length)
    { if (offset < 0 || data.Length - offset < length) throw new InvalidDataException("Truncated protobuf field."); }

    private static ulong ReadVarint(ReadOnlySpan<byte> data, ref int offset)
    {
        ulong value = 0;
        for (var shift = 0; shift < 70; shift += 7)
        {
            if (offset >= data.Length) throw new InvalidDataException("Truncated protobuf varint.");
            var current = data[offset++];
            if (shift == 63 && current > 1) throw new InvalidDataException("Protobuf varint overflow.");
            value |= (ulong)(current & 0x7F) << shift;
            if ((current & 0x80) == 0) return value;
        }
        throw new InvalidDataException("Protobuf varint exceeds 10 bytes.");
    }

    private static void WriteVarint(Stream output, ulong value)
    {
        while (value >= 0x80) { output.WriteByte((byte)(value | 0x80)); value >>= 7; }
        output.WriteByte((byte)value);
    }
}

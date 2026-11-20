namespace Trimūrti.Server.Protocol;

/// <summary>
/// Builds structurally complete default messages from the protocol catalog (port of Alpha-Persei's
/// schema_default.py): repeated fields become empty lists, required scalars 0/""/false and required
/// nested messages are filled recursively, so the client's Lua never indexes a missing table.
/// </summary>
public sealed class SchemaDefaults(ProtoSchema schema)
{
    public bool Has(string message) => schema.TryGetFields(message, out _);

    public Dictionary<string, object?> Build(string message, int depth = 0)
    {
        var result = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (depth > 6 || !schema.TryGetFields(message, out var fields)) return result;
        foreach (var f in fields.Values)
        {
            if (string.Equals(f.Label, "repeated", StringComparison.OrdinalIgnoreCase))
            {
                result[f.Name] = Array.Empty<object>();
                continue;
            }
            var isMessage = f.Reference is { Length: > 0 } || string.Equals(f.Type, "message", StringComparison.OrdinalIgnoreCase);
            if (isMessage)
            {
                if (string.Equals(f.Label, "required", StringComparison.OrdinalIgnoreCase) && f.Reference is { Length: > 0 } reference)
                    result[f.Name] = Build(reference, depth + 1);
                continue;
            }
            result[f.Name] = f.Type switch
            {
                "string" => "",
                "bool" => false,
                "bytes" => Array.Empty<byte>(),
                "float" or "double" => 0d,
                _ => 0L
            };
        }
        return result;
    }

    /// <summary>Overlays <paramref name="values"/> on the defaults for <paramref name="message"/>.</summary>
    public Dictionary<string, object?> Complete(string message, IReadOnlyDictionary<string, object?> values)
    {
        var merged = Build(message);
        foreach (var (k, v) in values) merged[k] = v;
        return merged;
    }
}

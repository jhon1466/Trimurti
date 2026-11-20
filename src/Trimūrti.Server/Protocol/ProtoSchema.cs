using System.Text.Json;

namespace Trimūrti.Server.Protocol;

public sealed record ProtoField(int Number, string Name, string Type, string Label, string? Reference);

public sealed class ProtoSchema
{
    private readonly Dictionary<string, Dictionary<int, ProtoField>> _messages;
    private readonly Dictionary<string, List<Dictionary<int, ProtoField>>> _candidates;

    private ProtoSchema(
        Dictionary<string, Dictionary<int, ProtoField>> messages,
        Dictionary<string, List<Dictionary<int, ProtoField>>> candidates)
    {
        _messages = messages;
        _candidates = candidates;
    }

    public int MessageCount => _messages.Count;
    public bool TryGetFields(string name, out Dictionary<int, ProtoField> fields) =>
        _messages.TryGetValue(Normalize(name), out fields!);

    public Dictionary<int, ProtoField> ResolveFieldsForEncoding(string name, IEnumerable<string> keys)
    {
        var normalized = Normalize(name);
        if (!_candidates.TryGetValue(normalized, out var list) || list.Count == 0)
            return [];
        if (list.Count == 1)
            return list[0];

        var keySet = keys.Where(k => !k.StartsWith('_')).ToHashSet(StringComparer.Ordinal);
        if (keySet.Count == 0)
            return _messages.TryGetValue(normalized, out var def) ? def : list[0];

        Dictionary<int, ProtoField>? best = null;
        var bestMatches = -1;
        foreach (var candidate in list)
        {
            var matches = candidate.Values.Count(f => keySet.Contains(f.Name));
            if (matches > bestMatches || (matches == bestMatches && best is not null && candidate.Count < best.Count))
            {
                best = candidate;
                bestMatches = matches;
            }
        }

        return best ?? list[0];
    }

    public static ProtoSchema Load(string path)
    {
        using var document = JsonDocument.Parse(File.ReadAllBytes(path));
        var messages = new Dictionary<string, Dictionary<int, ProtoField>>(StringComparer.OrdinalIgnoreCase);
        var candidates = new Dictionary<string, List<Dictionary<int, ProtoField>>>(StringComparer.OrdinalIgnoreCase);
        foreach (var message in document.RootElement.GetProperty("messages").EnumerateArray())
        {
            if (!message.TryGetProperty("name", out var nameElement)) continue;
            var name = Normalize(nameElement.GetString() ?? "");
            if (name.Length == 0) continue;
            var fields = new Dictionary<int, ProtoField>();
            if (message.TryGetProperty("fields", out var fieldArray))
            {
                foreach (var field in fieldArray.EnumerateArray())
                {
                    if (!field.TryGetProperty("number", out var numberElement) || !numberElement.TryGetInt32(out var number) || number is < 1 or > 0x1FFFFFFF) continue;
                    var fieldName = field.TryGetProperty("name", out var fieldNameElement) ? fieldNameElement.GetString() ?? $"f{number}" : $"f{number}";
                    var type = field.TryGetProperty("type", out var typeElement) ? typeElement.GetString() ?? "" : "";
                    var label = field.TryGetProperty("label", out var labelElement) ? field.GetProperty("label").GetString() ?? "" : "";
                    var reference = field.TryGetProperty("ref", out var refElement) ? refElement.GetString() : null;
                    fields[number] = new ProtoField(number, fieldName, type, label, reference);
                }
            }

            if (!candidates.TryGetValue(name, out var candidateList))
                candidates[name] = candidateList = [];
            candidateList.Add(new Dictionary<int, ProtoField>(fields));

            if (!messages.TryGetValue(name, out var existing) || fields.Count > existing.Count)
                messages[name] = fields;
            else
                foreach (var pair in fields) existing.TryAdd(pair.Key, pair.Value);
        }
        return new ProtoSchema(messages, candidates);
    }

    private static string Normalize(string name) => name.Trim().TrimStart('.').ToLowerInvariant();
}

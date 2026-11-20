using System.Text.Json;

namespace Trimūrti.Server;

/// <summary>
/// Captured official login stream (Data/login_push.json, exported from Alpha-Persei-Cluster's login_push table).
/// The client initialises many modules (dorm, guild, chips, red dots, activities...) only from these frames;
/// missing ones leave Lua data tables nil and break views such as the player profile.
/// </summary>
public static class LoginPushCatalog
{
    private static readonly Lazy<IReadOnlyList<(uint Command, byte[] Payload)>> Frames = new(Load);

    /// <summary>
    /// Captured frames that are tied to the capture's resource version / activity season and crash the client
    /// when replayed against Trimūrti's own activity list. Skipped until their services are ported.
    /// 75009/89013/89021/89801: core verification (CoreVerificationChallengeData.GetAffixState: affix list nil).
    /// </summary>
    public static readonly HashSet<uint> Excluded = [30017, 53001, 83124, 73001, 73007, 16015, 20009, 50001, 14501, 14503, 58001, 58027, 58169, 58189, 58191, 58195, 85035, 51001, 67001, 28801, 28837, 83016, 83017, 83021, 83026, 89601, 61047, 89701, 84331, 60105, 60107, 28019, 89201, 89125, 89101, 90007, 88001, 88013, 88019, 88015, 88023, 88027, 88211, 88217, 88221, 88305, 88315, 75001, 75017, 83020, 86035, 79601, 89011, 89421, 89401, 68181, 68171, 24051, 24043, 89013, 89021, 89801, 89025, 89431, 49001, 49023, 18001, 18003, 18005,
        // 10039: the capture's gateway redirect (foreign server IP); 33001: guild boss state (no guild locally).
        10039, 33001,
        // 10043: captured identity ack with the capture's timestamp; the gateway already sent the real one.
        10043];

    /// <summary>Commands the last Merge still took from the capture (diagnostics: what is not generated yet).</summary>
    public static readonly SortedSet<uint> LastCaptured = [];

    private static IReadOnlyList<(uint Command, byte[] Payload)> Load()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Data", "login_push.json");
        if (!File.Exists(path)) return [];
        using var doc = JsonDocument.Parse(File.ReadAllBytes(path));
        return doc.RootElement.GetProperty("frames").EnumerateArray()
            .Select(f => (f.GetProperty("cmd").GetUInt32(), Convert.FromBase64String(f.GetProperty("payload").GetString() ?? "")))
            .ToArray();
    }

    /// <summary>
    /// Emits the captured stream in its original order, substituting the server's own dynamically built
    /// payload (first occurrence only) wherever one exists. Own frames absent from the capture are appended,
    /// and 10201 (end of login) always goes last.
    /// </summary>
    public static IReadOnlyList<(uint Command, byte[] Payload)> Merge(IReadOnlyList<(uint Command, byte[] Payload)> own)
    {
        const uint end = 10201;
        var ownByCmd = new Dictionary<uint, byte[]>();
        foreach (var (cmd, payload) in own) ownByCmd.TryAdd(cmd, payload);
        var result = new List<(uint, byte[])>();
        var used = new HashSet<uint>();
        LastCaptured.Clear();
        foreach (var (cmd, payload) in Frames.Value)
        {
            if (cmd == end) continue;
            // Our own frame takes the captured slot (keeps the client's expected login order); excluded commands
            // never fall back to the captured payload.
            if (ownByCmd.TryGetValue(cmd, out var mine))
            {
                if (used.Add(cmd)) result.Add((cmd, mine));
            }
            else if (!Excluded.Contains(cmd) && !LoginDynamicFrames.ActivityBound.Contains(cmd)) { result.Add((cmd, payload)); LastCaptured.Add(cmd); }
        }
        foreach (var (cmd, payload) in own)
            if (cmd != end && used.Add(cmd)) result.Add((cmd, payload));
        if (ownByCmd.TryGetValue(end, out var endPayload)) result.Add((end, endPayload));
        return result;
    }
}

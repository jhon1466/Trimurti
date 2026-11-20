using System.Text.Json;

namespace Trimūrti.Server;

public sealed record ClientBuild(string Build, string Version, int BaselineTheme);
public sealed record ClientBuildDetection(bool Detected, string? Build, string? Version, int? AppVersion, string? ManifestPath, string? Sha256, string Detail);

/// <summary>Version and compatibility metadata for supported desktop resource builds.</summary>
public sealed class ClientBuildCatalog
{
    private readonly Dictionary<string, ClientBuild> _builds;

    public ClientBuildCatalog()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Data", "Configs", "supported_client_versions.json");
        using var document = JsonDocument.Parse(File.ReadAllBytes(path));
        _builds = document.RootElement.GetProperty("supportedResourceBuilds").EnumerateArray()
            .Select(e => new ClientBuild(e.GetProperty("build").GetString()!, e.GetProperty("version").GetString()!, e.GetProperty("baselineTheme").GetInt32()))
            .ToDictionary(x => x.Build, StringComparer.Ordinal);
        DefaultBuild = document.RootElement.GetProperty("defaultBuild").GetString()!;
        InstalledClient = Detect();
        ActiveBuild = InstalledClient.Detected && InstalledClient.Build is { } detectedBuild ? detectedBuild : DefaultBuild;
    }

    public string DefaultBuild { get; }
    public string ActiveBuild { get; }
    public IReadOnlyCollection<ClientBuild> SupportedBuilds => _builds.Values;
    public ClientBuildDetection InstalledClient { get; }
    public bool TryGet(string build, out ClientBuild config) => _builds.TryGetValue(build, out config!);

    public IReadOnlyList<Dictionary<string, object?>> BuildInitialActivities(string build)
    {
        if (!_builds.TryGetValue(build, out var config)) return [];
        const long start = 1_577_836_800;
        const long end = 2_440_962_000;
        var definitions = config.BaselineTheme >= 44
            ? new (int Id, int State, int Theme, int Template, int[] Children)[]
            {
                (4410001, 1, 44, 100, [4435001,4435002,4435003,4435004,4435005]),
                (4400101, 1, 44, 1, []),
                (4400301, 0, 44, 3, []),
                (4400303, 0, 44, 3, []),
                (4407301, 1, 44, 73, []),
                (4407302, 0, 44, 73, []),
                (4435001, 1, 44, 350, []), (4435002, 1, 44, 350, []),
                (4435003, 1, 44, 350, []), (4435004, 1, 44, 350, []),
                (4435005, 1, 44, 350, [])
            }
            : new (int Id, int State, int Theme, int Template, int[] Children)[]
            {
                (4310001, 1, 43, 100, [4335001,4335002,4335003]),
                (4300101, 1, 43, 1, []),
                (4307301, 1, 43, 73, []),
                (4335001, 1, 43, 350, []), (4335002, 1, 43, 350, []), (4335003, 1, 43, 350, [])
            };
        return definitions.Select(x => new Dictionary<string, object?>
        {
            ["activity_id"] = x.Id,
            ["start_time"] = start,
            ["stop_time"] = end,
            ["state"] = 0,
            ["theme"] = x.Theme,
            ["template"] = x.Template,
            ["sub_activity_id_list"] = x.Children.Cast<object>().ToArray()
        }).ToArray();
    }

    public ClientBuildDetection Detect(string? gameOrStreamingAssetsPath = null)
    {
        var target = gameOrStreamingAssetsPath ?? Environment.GetEnvironmentVariable("TRIMURTI_CLIENT_PATH") ?? @"C:\Program Files\AetherGazerLauncher\AetherGazer";
        var candidates = new[]
        {
            Path.Combine(target, "AetherGazer_Data", "StreamingAssets", "Windows", "AssetHash_Info.bytes"),
            Path.Combine(target, "StreamingAssets", "Windows", "AssetHash_Info.bytes"),
            Path.Combine(target, "AetherGazer_Data", "StreamingAssets", "AssetHash_Info.bytes"),
            Path.Combine(target, "StreamingAssets", "AssetHash_Info.bytes")
        };
        var manifestPath = candidates.FirstOrDefault(File.Exists);
        if (manifestPath is null)
            return new ClientBuildDetection(false, null, null, null, null, null, $"No AssetHash_Info.bytes manifest found under '{target}'.");

        try
        {
            var info = new FileInfo(manifestPath);
            if (info.Length is <= 0 or > 32 * 1024 * 1024)
                return new ClientBuildDetection(false, null, null, null, manifestPath, null, "Manifest is empty or exceeds the 32 MiB size limit.");
            var bytes = File.ReadAllBytes(manifestPath);
            using var doc = JsonDocument.Parse(bytes);
            var root = doc.RootElement;
            var build = root.GetProperty("buildCode").ToString();
            var app = root.GetProperty("appVersion").GetInt32();
            var version = root.GetProperty("versionName").GetString();
            if (!root.TryGetProperty("assetHashList", out var list) || list.ValueKind != JsonValueKind.Array)
                return new ClientBuildDetection(false, build, version, app, manifestPath, Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)), "Manifest lacks assetHashList.");
            var validItems = list.EnumerateArray().All(item =>
            {
                if (item.ValueKind != JsonValueKind.String) return false;
                var fields = item.GetString()!.Split('|');
                return fields.Length == 3 && fields[0].Length > 0 &&
                    System.Text.RegularExpressions.Regex.IsMatch(fields[1], "^[0-9a-fA-F]{32}$") &&
                    long.TryParse(fields[2], out var size) && size >= 0;
            });
            if (!validItems)
                return new ClientBuildDetection(false, build, version, app, manifestPath, Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)), "Manifest contains malformed asset entries.");
            var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes));
            var isSupported = _builds.TryGetValue(build, out var known) && known.Version == version && app == 307;
            return new ClientBuildDetection(isSupported, build, version, app, manifestPath, hash,
                isSupported ? "Detected a registered PC resource build." : "Manifest is valid, but this app/resource build is not registered as supported PC compatibility.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException or KeyNotFoundException or FormatException)
        {
            return new ClientBuildDetection(false, null, null, null, manifestPath, null, $"Manifest could not be parsed: {ex.Message}");
        }
    }
}

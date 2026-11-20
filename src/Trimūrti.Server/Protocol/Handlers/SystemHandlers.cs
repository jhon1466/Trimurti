namespace Trimūrti.Server.Protocol.Handlers;

/// <summary>Handles game-channel heartbeat (10050 -> 10051).</summary>
public sealed class HeartbeatHandler(ProtoCodec codec) : IGameCommandHandler
{
    public uint Command => 10050;

    public ValueTask<IReadOnlyList<GameResponse>> HandleAsync(GameRequest request, CancellationToken cancellationToken) =>
        ValueTask.FromResult<IReadOnlyList<GameResponse>>([
            HandlerHelpers.Reply(codec, request, 10051, new Dictionary<string, object?>
            {
                ["state"] = 0,
                ["timestamp"] = checked((uint)DateTimeOffset.UtcNow.ToUnixTimeSeconds()),
                ["verify_timestamp"] = 316800
            })
        ]);
}

/// <summary>Handles simple timestamp ping (10300 -> 10301).</summary>
public sealed class PingTimestampHandler(ProtoCodec codec) : IGameCommandHandler
{
    public uint Command => 10300;

    public ValueTask<IReadOnlyList<GameResponse>> HandleAsync(GameRequest request, CancellationToken cancellationToken) =>
        ValueTask.FromResult<IReadOnlyList<GameResponse>>([
            HandlerHelpers.Reply(codec, request, 10301, new Dictionary<string, object?>
            {
                ["timestamp"] = checked((uint)DateTimeOffset.UtcNow.ToUnixTimeSeconds())
            })
        ]);
}

/// <summary>Handles client version / resource check (10400 -> 10401).</summary>
public sealed class ClientVersionHandler(ClientBuildCatalog builds, ProtoCodec codec) : IGameCommandHandler
{
    public uint Command => 10400;

    public ValueTask<IReadOnlyList<GameResponse>> HandleAsync(GameRequest request, CancellationToken cancellationToken)
    {
        var clientVersion = HandlerHelpers.ReadUInt(request.Body, "client_version", 307);
        var resVersion = uint.TryParse(builds.ActiveBuild, out var parsed) ? parsed : 321u;
        return ValueTask.FromResult<IReadOnlyList<GameResponse>>([
            HandlerHelpers.Reply(codec, request, 10401, new Dictionary<string, object?>
            {
                ["cdn_url"] = "http://127.0.0.1:5180/cdn",
                ["res_version"] = resVersion,
                ["client_version"] = clientVersion
            })
        ]);
    }
}

/// <summary>Client-side event/log upload (38014, no response). Logged for debugging client UI errors.</summary>
public sealed class ClientLogUploadHandler(ILogger<ClientLogUploadHandler> logger) : IGameCommandHandler
{
    public uint Command => 38014;

    public ValueTask<IReadOnlyList<GameResponse>> HandleAsync(GameRequest request, CancellationToken cancellationToken)
    {
        try { logger.LogInformation("Client log 38014: {Body}", System.Text.Json.JsonSerializer.Serialize(request.Body)); }
        catch (Exception ex) { logger.LogInformation("Client log 38014 (unserializable): {Error}", ex.Message); }
        return ValueTask.FromResult<IReadOnlyList<GameResponse>>([]);
    }
}

/// <summary>Client-only pushes that must not be answered (an unexpected reply breaks the client's protocol parser).
/// 54300: battle initialization notice before the client connects to the UDP battle server.</summary>
public sealed class NoReplyHandler(uint command) : IGameCommandHandler
{
    public uint Command => command;
    public ValueTask<IReadOnlyList<GameResponse>> HandleAsync(GameRequest request, CancellationToken cancellationToken) =>
        ValueTask.FromResult<IReadOnlyList<GameResponse>>([]);
    public static readonly uint[] Commands = [54300];
}

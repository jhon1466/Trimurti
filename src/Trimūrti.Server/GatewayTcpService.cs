using System.Net;
using System.Net.Sockets;
using Trimūrti.Server.Protocol;

namespace Trimūrti.Server;

/// <summary>Local gateway stage: token validation, game endpoint handoff, login verification and heartbeat.</summary>
public sealed class GatewayTcpService(AccountStore accounts, ProtoCodec codec, ILogger<GatewayTcpService> logger) : BackgroundService
{
    private TcpListener? _listener;
    public bool IsListening { get; private set; }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _listener = new TcpListener(IPAddress.Loopback, 8102);
        _listener.Start(128);
        IsListening = true;
        logger.LogInformation("Trimūrti gateway listening on 127.0.0.1:8102");
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                var client = await _listener.AcceptTcpClientAsync(stoppingToken);
                _ = HandleClientAsync(client, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        catch (SocketException) when (stoppingToken.IsCancellationRequested) { }
        finally { IsListening = false; _listener.Stop(); }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken cancellationToken)
    {
        var peer = client.Client.RemoteEndPoint;
        using (client)
        {
            var stream = client.GetStream();
            var prefix = new byte[4];
            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    if (!await ReadExactlyOrEofAsync(stream, prefix, cancellationToken)) break;
                    if (!GameFrameCodec.TryReadUpstreamLength(prefix, out var frameLength)) throw new InvalidDataException("Invalid gateway frame length.");
                    var bytes = new byte[frameLength]; prefix.CopyTo(bytes, 0);
                    if (!await ReadExactlyOrEofAsync(stream, bytes.AsMemory(4), cancellationToken)) throw new EndOfStreamException("Incomplete gateway frame.");
                    var frame = GameFrameCodec.ParseUpstream(bytes);
                    var response = await HandleFrameAsync(frame, cancellationToken);
                    if (response is null) continue;
                    var packed = GameFrameCodec.PackDownstream(response.Value.Command, response.Value.Payload, frame.Index, (ushort)(frame.ServerIndex + 1));
                    await stream.WriteAsync(packed, cancellationToken);
                }
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or SocketException or OperationCanceledException)
            { if (!cancellationToken.IsCancellationRequested) logger.LogDebug(ex, "Gateway connection ended peer={Peer}", peer); }
        }
    }

    private async ValueTask<(uint Command, byte[] Payload)?> HandleFrameAsync(GameFrame frame, CancellationToken cancellationToken)
    {
        if (frame.Command == 10038)
        {
            var request = codec.Decode("cs_10038", frame.Payload);
            var token = GetString(request, "token");
            var identity = await accounts.ResolveGatewayTokenAsync(token, cancellationToken);
            if (identity is null)
            {
                var denied = codec.Encode("sc_10039", new Dictionary<string, object?> { ["result"] = 2, ["timestamp"] = Now() });
                return (10039, denied);
            }

            var success = codec.Encode("sc_10039", new Dictionary<string, object?>
            {
                ["result"] = 0,
                ["server_id"] = 2,
                ["ip"] = "127.0.0.1",
                ["port"] = 8105,
                ["user_id"] = identity.UserId,
                ["gstoken"] = identity.GameToken,
                ["timestamp"] = Now(),
                ["is_new_player"] = 0
            });
            logger.LogInformation("Gateway authenticated local account uid={Uid}", identity.UserId);
            return (10039, success);
        }

        if (frame.Command == 10042)
        {
            var request = codec.Decode("cs_10042", frame.Payload);
            var uid = GetLong(request, "user_id");
            var valid = await accounts.ValidateGameTokenAsync(uid, GetString(request, "gstoken"), cancellationToken);
            var payload = codec.Encode("sc_10043", new Dictionary<string, object?>
            {
                ["result"] = valid ? 0 : 2,
                ["register_timestamp"] = valid ? GetLong(request, "timestamp") : 0,
                ["timestamp"] = Now(),
                ["verify_timestamp"] = valid ? 316800 : 0,
                ["uid_sign"] = valid ? "trimurti-local" : ""
            });
            return (10043, payload);
        }

        if (frame.Command == 10050)
        {
            var payload = codec.Encode("sc_10051", new Dictionary<string, object?>
            {
                ["state"] = 0, ["timestamp"] = Now(), ["verify_timestamp"] = 316800
            });
            return (10051, payload);
        }

        logger.LogWarning("Unmapped gateway command {Command}", frame.Command);
        return null;
    }

    private static uint Now() => checked((uint)DateTimeOffset.UtcNow.ToUnixTimeSeconds());
    private static string GetString(IReadOnlyDictionary<string, object?> data, string key) => data.TryGetValue(key, out var value) ? Convert.ToString(value) ?? "" : "";
    private static long GetLong(IReadOnlyDictionary<string, object?> data, string key) => data.TryGetValue(key, out var value) && value is not null ? Convert.ToInt64(value) : 0;

    private static async Task<bool> ReadExactlyOrEofAsync(Stream stream, Memory<byte> buffer, CancellationToken cancellationToken)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer[offset..], cancellationToken);
            if (read == 0) return offset == 0;
            offset += read;
        }
        return true;
    }
}

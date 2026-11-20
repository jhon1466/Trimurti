using System.Net;
using System.Net.Sockets;
using Trimūrti.Server.Protocol;

namespace Trimūrti.Server;

/// <summary>
/// Local TCP ingress for the game protocol. This first port milestone validates and parses
/// envelopes; business command handlers are intentionally not registered yet.
/// </summary>
public sealed class GameTcpGateway(AccountStore accounts, LoginBootstrap loginBootstrap, ProtoCodec codec, CommandDispatcher dispatcher, ILogger<GameTcpGateway> logger) : BackgroundService
{
    private TcpListener? _listener;
    public bool IsListening { get; private set; }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _listener = new TcpListener(IPAddress.Loopback, 8105);
        _listener.Start(128);
        IsListening = true;
        logger.LogInformation("Trimūrti game TCP gateway listening on 127.0.0.1:8105");
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
        finally
        {
            IsListening = false;
            _listener.Stop();
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken cancellationToken)
    {
        var remote = client.Client.RemoteEndPoint;
        using (client)
        {
            client.ReceiveTimeout = 15_000;
            client.SendTimeout = 15_000;
            var stream = client.GetStream();
            var prefix = new byte[4];
            long accountId = 0;
            var zeroFrames = 0;
            logger.LogInformation("Game TCP connection opened peer={Peer}", remote);
            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    if (!await ReadExactlyOrEofAsync(stream, prefix, cancellationToken)) break;
                    if (!GameFrameCodec.TryReadUpstreamLength(prefix, out var totalLength))
                        throw new InvalidDataException("No se pudo leer la longitud de la trama.");
                    var frameBytes = new byte[totalLength];
                    prefix.CopyTo(frameBytes, 0);
                    if (!await ReadExactlyOrEofAsync(stream, frameBytes.AsMemory(4), cancellationToken))
                        throw new EndOfStreamException("Conexión cerrada durante una trama.");
                    var frame = GameFrameCodec.ParseUpstream(frameBytes);
                    if (frame.Command == 10042)
                        logger.LogInformation("Game TCP RX cmd=10042 (identity/reconnect) idx={Index} srv={ServerIndex} peer={Peer}", frame.Index, frame.ServerIndex, remote);
                    if (frame.Command == 10042)
                    {
                        var request = codec.Decode("cs_10042", frame.Payload);
                        var uid = request.TryGetValue("user_id", out var uidValue) ? Convert.ToInt64(uidValue) : 0;
                        var gameToken = request.TryGetValue("gstoken", out var tokenValue) ? Convert.ToString(tokenValue) ?? "" : "";
                        var valid = await accounts.ValidateGameTokenAsync(uid, gameToken, cancellationToken);
                        if (valid) accountId = uid;
                        var ack = codec.Encode("sc_10043", new Dictionary<string, object?>
                        {
                            ["result"] = valid ? 0 : 2,
                            ["register_timestamp"] = request.TryGetValue("timestamp", out var ts) ? Convert.ToInt64(ts) : 0,
                            ["timestamp"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                            ["verify_timestamp"] = valid ? GameClock.VerifyTimestamp : 0,
                            ["uid_sign"] = valid ? "trimurti-local" : ""
                        });
                        await stream.WriteAsync(GameFrameCodec.PackDownstream(10043, ack, frame.Index, (ushort)(frame.ServerIndex + 1)), cancellationToken);
                        continue;
                    }
                    if (frame.Command == 10200)
                    {
                        if (accountId <= 0)
                        {
                            logger.LogWarning("Rejected login bootstrap before successful 10042 verification from {Peer}", remote);
                            break;
                        }
                        var loginFrames = await loginBootstrap.BuildAsync(accountId, frame.Index, frame.ServerIndex, cancellationToken);
                        foreach (var loginFrame in loginFrames)
                            await stream.WriteAsync(GameFrameCodec.PackDownstream(loginFrame.Command, loginFrame.Payload, loginFrame.Index, loginFrame.ServerIndex), cancellationToken);
                        logger.LogInformation("Sent {FrameCount} initial login frames for uid={Uid} (req idx={Index}, srv={ServerIndex})", loginFrames.Count, accountId, frame.Index, frame.ServerIndex);
                        logger.LogInformation("Login frame order: {Order}", string.Join(",", loginFrames.Select(f => f.Command)));
                        continue;
                    }
                    if (frame.Command == 0)
                    {
                        // Should not happen with correct framing: log it so a lost request (client timeout) is visible.
                        if ((frame.Index != 0 || frame.Payload.Length > 0) && zeroFrames++ < 3)
                            logger.LogWarning("Game TCP RX cmd=0 idx={Index} srv={ServerIndex} len={Len} ver={Ver} head={Head}",
                                frame.Index, frame.ServerIndex, frame.Payload.Length, frame.Version, Convert.ToHexString(frameBytes.AsSpan(0, Math.Min(32, frameBytes.Length))));
                        continue;
                    }
                    logger.LogInformation("Game TCP RX cmd={Cmd} idx={Index} srv={ServerIndex} len={Len} uid={Uid}", frame.Command, frame.Index, frame.ServerIndex, frame.Payload.Length, accountId);
                    if (frame.Command == 38014)
                    {
                        // Fire-and-forget client telemetry with non-standard wire framing (Alpha-Persei DROP whitelist).
                        logger.LogInformation("Client log 38014 raw: {Text}", new string(System.Text.Encoding.UTF8.GetString(frame.Payload).Select(c => char.IsControl(c) ? '|' : c).ToArray()));
                        continue;
                    }
                    if (frame.Command == 27012)
                    {
                        // Chat channel join: client waits for 27013 + 27005 (room 3) before continuing (e.g. daily sign 11010).
                        await stream.WriteAsync(GameFrameCodec.PackDownstream(27013, new byte[] { 0x08, 0x00 }, frame.Index, (ushort)(frame.ServerIndex + 1)), cancellationToken);
                        await stream.WriteAsync(GameFrameCodec.PackDownstream(27005, new byte[] { 0x08, 0x03 }, 0, (ushort)(frame.ServerIndex + 2)), cancellationToken);
                        continue;
                    }
                    IReadOnlyList<GameResponse> responses;
                    try { responses = await dispatcher.DispatchAsync(frame, accountId, cancellationToken); }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        // One failing command must not drop the whole connection (the client would reconnect and lose
                        // its pending requests). Log it and answer with a generic error result.
                        logger.LogError(ex, "Command {Cmd} failed idx={Index} uid={Uid}", frame.Command, frame.Index, accountId);
                        responses = frame.Index != 0
                            ? [new GameResponse(frame.Command + 1, frame.Index, (ushort)(frame.ServerIndex + 1), [0x08, 0x02])]
                            : [];
                    }
                    foreach (var response in responses)
                    {
                        var packed = GameFrameCodec.PackDownstream(response.Command, response.Payload, response.Index, response.ServerIndex);
                        await stream.WriteAsync(packed, cancellationToken);
                        logger.LogInformation("Game TCP TX cmd={Cmd} idx={Index} srv={ServerIndex} rawLen={RawLen} packedLen={PackedLen}", response.Command, response.Index, response.ServerIndex, response.Payload.Length, packed.Length);
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or SocketException or OperationCanceledException)
            {
                if (!cancellationToken.IsCancellationRequested)
                    logger.LogInformation(ex, "Game TCP connection ended peer={Peer} uid={Uid}", remote, accountId);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Game TCP handler failed peer={Peer} uid={Uid}", remote, accountId);
            }
            logger.LogInformation("Game TCP connection closed peer={Peer} uid={Uid}", remote, accountId);
        }
    }

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

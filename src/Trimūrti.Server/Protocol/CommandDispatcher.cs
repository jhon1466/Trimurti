using System.Collections.Concurrent;

namespace Trimūrti.Server.Protocol;

public sealed record GameRequest(uint Command, ushort Index, ushort ServerIndex, byte Version, long AccountId, IReadOnlyDictionary<string, object?> Body);
public sealed record GameResponse(uint Command, ushort Index, ushort ServerIndex, byte[] Payload);

public interface IGameCommandHandler
{
    uint Command { get; }
    ValueTask<IReadOnlyList<GameResponse>> HandleAsync(GameRequest request, CancellationToken cancellationToken);
}

/// <summary>Resolves client commands by ID, decodes cs_N protobuf payloads, and encodes response models.</summary>
public sealed class CommandDispatcher
{
    private readonly ConcurrentDictionary<uint, IGameCommandHandler> _handlers = new();
    private readonly ProtoCodec _codec;
    private readonly FallbackCatalog _fallbacks;
    private readonly ILogger<CommandDispatcher> _logger;

    public CommandDispatcher(ProtoCodec codec, FallbackCatalog fallbacks, IEnumerable<IGameCommandHandler> handlers, ILogger<CommandDispatcher> logger)
    {
        _codec = codec;
        _fallbacks = fallbacks;
        _logger = logger;
        foreach (var handler in handlers) Register(handler);
    }

    public int RegisteredHandlerCount => _handlers.Count;

    public void Register(IGameCommandHandler handler)
    {
        if (!_handlers.TryAdd(handler.Command, handler))
            throw new InvalidOperationException($"A handler is already registered for command {handler.Command}.");
    }

    public async ValueTask<IReadOnlyList<GameResponse>> DispatchAsync(GameFrame frame, long accountId, CancellationToken cancellationToken)
    {
        var commandName = $"cs_{frame.Command}";
        var body = _codec.Decode(commandName, frame.Payload);
        if (!_handlers.TryGetValue(frame.Command, out var handler))
        {
            var fallback = _fallbacks.Resolve(frame);
            if (fallback is not null)
            {
                _logger.LogInformation("Command {Command} answered using fallback catalog ({FrameCount} frames)", frame.Command, fallback.Count);
                return fallback;
            }
            _logger.LogWarning("No game operation registered for cmd={Command}; decoded fields={Fields}", frame.Command, string.Join(',', body.Keys));
            return Array.Empty<GameResponse>();
        }

        var request = new GameRequest(frame.Command, frame.Index, frame.ServerIndex, frame.Version, accountId, body);
        var responses = await handler.HandleAsync(request, cancellationToken);
        foreach (var response in responses)
        {
            // Pushes (index 0) may legitimately be empty (e.g. sc_44009 with no progress); replies always carry a result.
            if (response.Payload.Length > 0 || response.Index == 0) continue;
            throw new InvalidDataException($"Handler for {frame.Command} returned an empty payload for response {response.Command}.");
        }
        return responses;
    }

    public byte[] EncodeResponse(uint command, IReadOnlyDictionary<string, object?> payload) => _codec.Encode($"sc_{command}", payload);
}

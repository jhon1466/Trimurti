namespace Trimūrti.Server.Protocol.Handlers;

// Hero / equip / oath extras (Alpha-Persei operations.py: HeroUnlockSkinOp, ReqHeroHeartRateOp, OathOp,
// EquipQuickDressOp, ResolveModuleItemOp). Result-only ones (chips 500xx, enchant previews, chapter 19/V2 map
// events, module level-up...) are answered by the schema-complete fallback.

/// <summary>Heart-rate query (14122 -> 14123): fixed 100 like the reference server.</summary>
public sealed class HeroHeartRateHandler(ProtoCodec codec) : IGameCommandHandler
{
    public uint Command => 14122;

    public ValueTask<IReadOnlyList<GameResponse>> HandleAsync(GameRequest r, CancellationToken ct) =>
        ValueTask.FromResult<IReadOnlyList<GameResponse>>([HandlerHelpers.Reply(codec, r, 14123, new Dictionary<string, object?> { ["result"] = 0u, ["value"] = 100u })]);
}


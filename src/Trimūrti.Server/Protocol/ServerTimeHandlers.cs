namespace Trimūrti.Server.Protocol;

public sealed class RefreshTimeHandler(ProtoCodec codec) : IGameCommandHandler
{
    public uint Command => 10500;

    public ValueTask<IReadOnlyList<GameResponse>> HandleAsync(GameRequest request, CancellationToken cancellationToken)
    {
        var local = GameClock.Now;
        var daily = Next(local, 4, d => d.AddDays(1));
        var weekly = NextWeekly(local);
        var monthly = new DateTimeOffset(local.Year, local.Month, 1, 4, 0, 0, local.Offset).AddMonths(1);
        var payload = codec.Encode("sc_10501", new Dictionary<string, object?>
        {
            ["result"] = 0,
            ["timestamp"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            ["verify_timestamp"] = 316800,
            ["next_refresh_time"] = daily.ToUnixTimeSeconds(),
            ["next_weekly_refresh_time"] = weekly.ToUnixTimeSeconds(),
            ["next_monthly_refresh_time"] = monthly.ToUnixTimeSeconds()
        });
        return ValueTask.FromResult<IReadOnlyList<GameResponse>>([new GameResponse(10501, request.Index, (ushort)(request.ServerIndex + 1), payload)]);
    }

    private static DateTimeOffset Next(DateTimeOffset current, int hour, Func<DateTimeOffset, DateTimeOffset> advance)
    {
        var target = new DateTimeOffset(current.Year, current.Month, current.Day, hour, 0, 0, current.Offset);
        if (target <= current) target = advance(target);
        return target;
    }

    private static DateTimeOffset NextWeekly(DateTimeOffset current)
    {
        var monday = current.Date.AddDays(-(((int)current.DayOfWeek + 6) % 7)).AddHours(4);
        var target = new DateTimeOffset(monday, current.Offset);
        if (target <= current) target = target.AddDays(7);
        return target;
    }
}

public sealed class ServerTimestampHandler(ProtoCodec codec) : IGameCommandHandler
{
    public uint Command => 10700;

    public ValueTask<IReadOnlyList<GameResponse>> HandleAsync(GameRequest request, CancellationToken cancellationToken)
    {
        var payload = codec.Encode("sc_10701", new Dictionary<string, object?>
        {
            ["timestamp"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            ["timestamp_sign"] = ""
        });
        return ValueTask.FromResult<IReadOnlyList<GameResponse>>([new GameResponse(10701, request.Index, (ushort)(request.ServerIndex + 1), payload)]);
    }
}

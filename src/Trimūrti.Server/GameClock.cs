namespace Trimūrti.Server;

/// <summary>
/// The client runs on server time (China, UTC+8): daily reset 05:00, refresh points 04:00, weeks start Monday.
/// All day boundaries must use this clock, never the host's local time zone, or the client sees a different
/// "today" and keeps reopening daily popups.
/// </summary>
public static class GameClock
{
    public static readonly TimeSpan Offset = TimeSpan.FromHours(8);

    /// <summary>
    /// Value of every verify_timestamp field. The client calls TimeMgr.SetServerTime(timestamp, verify_timestamp) and
    /// derives its time zone as 345600 - verify_timestamp seconds (316800 → UTC+8). Sending the current time here
    /// shifts the client's "today" and makes it re-claim daily rewards on every login.
    /// </summary>
    public const long VerifyTimestamp = 316800;

    public static DateTimeOffset Now => DateTimeOffset.UtcNow.ToOffset(Offset);

    public static DateTimeOffset At(long unix) => DateTimeOffset.FromUnixTimeSeconds(unix).ToOffset(Offset);

    /// <summary>Calendar date of the game day (05:00 cutoff) for an instant.</summary>
    public static DateTime GameDay(DateTimeOffset t) => t.ToOffset(Offset).AddHours(-5).Date;

    public static DateTime Today => GameDay(DateTimeOffset.UtcNow);

    /// <summary>Unix time of the current game day's 05:00 (UTC+8).</summary>
    public static long TodayStartUnix => new DateTimeOffset(Today.AddHours(5), Offset).ToUnixTimeSeconds();

    /// <summary>Next occurrence of hh:00 server time.</summary>
    public static DateTimeOffset NextAt(int hour)
    {
        var now = Now;
        var t = new DateTimeOffset(now.Year, now.Month, now.Day, hour, 0, 0, Offset);
        return t <= now ? t.AddDays(1) : t;
    }
}

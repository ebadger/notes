namespace Notes.Api.Data;

internal static class UtcTimestamp
{
    public static DateTimeOffset Now()
    {
        var now = DateTimeOffset.UtcNow;
        return now.AddTicks(-(now.Ticks % TimeSpan.TicksPerMicrosecond));
    }
}

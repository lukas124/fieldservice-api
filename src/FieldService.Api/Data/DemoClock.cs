using FieldService.Api.Infrastructure;
using Microsoft.Extensions.Options;

namespace FieldService.Api.Data;

/// <summary>„Dziś” w strefie czasowej dnia pracy technika (<c>Demo:TimeZone</c>, domyślnie Europe/Warsaw).</summary>
public sealed class DemoClock(TimeProvider time, IOptions<DemoOptions> options)
{
    private readonly TimeZoneInfo _zone = FindZone(options.Value.TimeZone);

    public DateTimeOffset Now => Microseconds(time.GetUtcNow());

    public DateOnly Today => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(time.GetUtcNow(), _zone).DateTime);

    /// <summary>Godzina dnia <paramref name="day"/> w strefie technika jako czas UTC.</summary>
    public DateTimeOffset At(DateOnly day, int hour, int minute = 0)
    {
        var local = day.ToDateTime(new TimeOnly(hour, minute));
        return new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(local, _zone), TimeSpan.Zero);
    }

    public (DateTimeOffset From, DateTimeOffset To) DayRange(DateOnly day) => (At(day, 0), At(day.AddDays(1), 0));

    /// <summary>Dokładność do mikrosekundy — tyle przechowuje PostgreSQL (porównania po zapisie i odczycie).</summary>
    public static DateTimeOffset Microseconds(DateTimeOffset value) => new(value.UtcTicks - value.UtcTicks % 10, TimeSpan.Zero);

    private static TimeZoneInfo FindZone(string id)
    {
        try { return TimeZoneInfo.FindSystemTimeZoneById(id); }
        catch (TimeZoneNotFoundException) { return TimeZoneInfo.Utc; }
    }
}

using M0LTE.S5066.Core.Runtime;

namespace M0LTE.S5066.Core.Sis;

/// <summary>
/// Converts an absolute core time to the TimeToDie of a DATA S_PDU: the Julian day-of-year
/// modulo 16 and the UTC seconds after midnight with the least significant bit dropped
/// (A-0161 to A-0163, A-0166) [Ed4 A.3.1.1, p.A-16]. The core has no clock, so the UTC instant of
/// core time 0 comes from configuration (A-0251; core-architecture.md 3.1). Integer arithmetic
/// only, on non-negative values (core-architecture.md 6 rule 1).
/// </summary>
internal static class TtdCalendar
{
    /// <summary>Seconds in a UTC day.</summary>
    public const int SecondsPerDay = 86400;

    /// <summary>Days in a Gregorian year: day numbers run 1 to 365, or 366 in a leap year (A-0161).</summary>
    public static int DaysInYear(int year) =>
        (year % 4 == 0 && year % 100 != 0) || year % 400 == 0 ? 366 : 365;

    /// <summary>
    /// The TTD field value for the core time <paramref name="coreMs"/>. The instant is rounded down
    /// to a whole second and then to an even second by dropping the least significant bit
    /// (A-0163), so the encoded TTD is never later than the instant (A-0251).
    /// </summary>
    /// <param name="epoch">The UTC instant of core time 0.</param>
    /// <param name="coreMs">Milliseconds since core time 0, not negative.</param>
    public static SpduTtd Encode(TimeConfig epoch, long coreMs)
    {
        ArgumentNullException.ThrowIfNull(epoch);
        ArgumentOutOfRangeException.ThrowIfNegative(coreMs);
        long seconds = (epoch.EpochSecond * 1000L + coreMs) / 1000;
        long days = seconds / SecondsPerDay;
        int secondOfDay = (int)(seconds % SecondsPerDay);
        int year = epoch.EpochYear;
        long day = epoch.EpochDay + days;
        while (day > DaysInYear(year))
        {
            day -= DaysInYear(year);
            year++;
        }

        return new SpduTtd((byte)(day % 16), (ushort)(secondOfDay >> 1));
    }
}

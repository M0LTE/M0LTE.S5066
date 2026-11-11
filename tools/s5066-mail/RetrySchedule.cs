namespace M0LTE.S5066.Mail;

/// <summary>
/// When a mail that could not be passed on is tried again (interp V-0092): 1 minute after the
/// first failure, then twice as long each time up to an hour between tries, until it has waited
/// <see cref="GiveUpAfter"/> since it was taken in; then it is bounced.
/// </summary>
internal static class RetrySchedule
{
    /// <summary>The first wait.</summary>
    public static readonly TimeSpan First = TimeSpan.FromMinutes(1);

    /// <summary>The longest wait between two tries.</summary>
    public static readonly TimeSpan Longest = TimeSpan.FromHours(1);

    /// <summary>How long a mail is tried for before it is bounced.</summary>
    public static readonly TimeSpan GiveUpAfter = TimeSpan.FromDays(5);

    /// <summary>The wait after the <paramref name="failures"/>th failure (1 for the first).</summary>
    public static TimeSpan Delay(int failures)
    {
        if (failures < 1)
        {
            return TimeSpan.Zero;
        }

        int doublings = Math.Min(failures - 1, 16);
        TimeSpan d = First * (1 << doublings);
        return d < Longest ? d : Longest;
    }

    /// <summary>
    /// The next try after a failure, or null when the mail has been tried for long enough and is
    /// to be bounced.
    /// </summary>
    public static DateTimeOffset? Next(DateTimeOffset created, int failures, DateTimeOffset now)
    {
        if (now - created >= GiveUpAfter)
        {
            return null;
        }

        return now + Delay(failures);
    }
}

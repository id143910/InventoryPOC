using System.Globalization;
using System.Text.RegularExpressions;

namespace Inventory.Api.Queries;

/// <summary>
/// A date written relative to today, resolved when the query runs.
///
/// A saved query has to keep meaning: "expiring in the next 60 days" written as
/// 2026-11-29 is wrong by tomorrow, so it is written as <c>now+60d</c> and resolved
/// here, at each run. The shape is Grafana's and Kibana's - <c>now</c>,
/// <c>now-7d</c>, <c>now+3M</c> - because that is the one people have already met.
///
/// Anything not beginning with <c>now</c> is passed through untouched: a date picked
/// or typed as 2027-06-01 is still the commonest case, and dates are stored as
/// yyyy-MM-dd, so a resolved one is written the same way and compares as text.
/// </summary>
public static partial class Moment
{
    /// <summary>now, optionally shifted: a sign, a count, and a unit.</summary>
    [GeneratedRegex(@"^now\s*(?:([+-])\s*(\d{1,5})\s*([dwMy]))?$", RegexOptions.IgnoreCase)]
    private static partial Regex Shape();

    public static string Resolve(string value) => Resolve(value, DateTime.UtcNow);

    /// <summary>Today is a parameter so that this is testable without waiting a day.</summary>
    public static string Resolve(string value, DateTime today)
    {
        var asked = value.Trim();
        if (!asked.StartsWith("now", StringComparison.OrdinalIgnoreCase))
        {
            return value;
        }

        var match = Shape().Match(asked);
        if (!match.Success)
        {
            throw new QueryError(
                $"'{value}' is not a date I can work out; write it as now, now-7d, now+60d or now+3M "
                + "(d days, w weeks, M months, y years)");
        }

        var when = today.Date;
        if (match.Groups[1].Success)
        {
            var count = int.Parse(match.Groups[2].ValueSpan, CultureInfo.InvariantCulture);
            var forward = match.Groups[1].Value == "+" ? count : -count;
            // Case matters on the unit, as it does in Grafana: M is months.
            when = match.Groups[3].Value switch
            {
                "d" or "D" => when.AddDays(forward),
                "w" or "W" => when.AddDays(forward * 7),
                "M" => when.AddMonths(forward),
                "y" or "Y" => when.AddYears(forward),
                // The only remaining letter is a lowercase m, which reads as minutes
                // everywhere else and would mean nothing against a date.
                _ => throw new QueryError(
                    $"'{value}' shifts by m, which would be minutes; a date shifts by d, w, M or y"),
            };
        }
        return when.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    }
}

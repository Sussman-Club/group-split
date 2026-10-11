using System.Text.Json.Serialization;

namespace GroupSplit.Shared;

/// <summary>
/// The span a group's Activity tab opens on.
/// </summary>
/// <remarks>
/// The tab's presets without Custom: a fixed pair of dates belongs to one look at the ledger,
/// not to the group. A household opens on this month's bills; a trip that holds nothing but
/// the trip opens on all of it. Stored on the group by name.
/// </remarks>
[JsonConverter(typeof(JsonStringEnumConverter<ActivityPeriod>))]
public enum ActivityPeriod
{
    AllTime,
    ThisMonth,
    LastMonth,
    LastThreeMonths,
    ThisYear
}

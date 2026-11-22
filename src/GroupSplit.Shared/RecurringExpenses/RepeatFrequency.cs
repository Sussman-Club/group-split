using System.Text.Json.Serialization;

namespace GroupSplit.Shared;

/// <summary>
/// How often a repeating expense comes round.
/// </summary>
/// <remarks>
/// Counted from the day it starts on, so a monthly one started on the 31st falls on the last
/// day of a shorter month and on the 31st again after it. Travels and is stored as its name.
/// </remarks>
[JsonConverter(typeof(JsonStringEnumConverter<RepeatFrequency>))]
public enum RepeatFrequency
{
    Weekly,
    Monthly,
    Yearly
}

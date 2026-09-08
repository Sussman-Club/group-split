using GroupSplit.Shared;
using Microsoft.AspNetCore.Components;

namespace GroupSplit.App.Shared.Services.Settling;

/// <summary>
/// What the Settle page holds between the server render and the interactive one, so
/// arriving there does not read the plan twice.
/// </summary>
public class SettleTracker
{
    [PersistentState] public SettlementPlanResponse? Plan { get; set; }

    [PersistentState] public PagedResponse<SettlementResponse>? History { get; set; }
}

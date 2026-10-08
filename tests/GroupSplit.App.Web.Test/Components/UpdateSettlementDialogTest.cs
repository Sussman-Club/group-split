using Bunit;
using GroupSplit.App.Shared.Components;
using GroupSplit.Shared;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.JsonPatch.SystemTextJson;
using Microsoft.AspNetCore.JsonPatch.SystemTextJson.Operations;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using MudBlazor;

namespace GroupSplit.App.Web.Test.Components;

/// <summary>
/// Editing a repayment, and who it offers on either end.
/// </summary>
/// <remarks>
/// The dialog used to filter guests out of both pickers. That was right while they were
/// pending invitees the API refused to settle with; it is wrong now that a repayment to or
/// from a guest is an ordinary one recorded by a member -- a repayment Dani made could be
/// recorded and then never corrected, because Dani was not on the list.
/// </remarks>
public class UpdateSettlementDialogTest : ComponentTest
{
    private static readonly Guid GroupId = Guid.NewGuid();
    private static readonly UserInfo Me = new(Guid.NewGuid(), "Ana", "Ruiz", "ana@example.com");
    private static readonly UserInfo Dani = new(Guid.NewGuid(), "Dani", null, null, IsGuest: true);
    private static readonly UserInfo Luz = new(Guid.NewGuid(), "Luz", null, null, IsGuest: true);

    private readonly Mock<IGroupsClient> _groups = new();
    private readonly Mock<ITransactionsClient> _transactions = new();

    public UpdateSettlementDialogTest()
    {
        _groups
            .Setup(client => client.GetGroupMembersAsync(GroupId, It.IsAny<CancellationToken>()))
            .ReturnsAsync([Me, Dani, Luz]);

        Services.AddSingleton(_groups.Object);
        Services.AddSingleton(_transactions.Object);
    }

    /// <summary>A repayment from one guest to another, which is the case the filter made impossible to edit.</summary>
    private static TransactionResponse Repayment() => new()
    {
        Id = Guid.NewGuid(),
        Name = "Settlement",
        Amount = 15m,
        DateTime = DateTimeOffset.UtcNow,
        GroupId = GroupId,
        GroupName = "The flat",
        PaidByUserId = Dani.Id,
        PaidByUserName = "Dani",
        PaidByIsGuest = true,
        PaidToUserId = Luz.Id,
        PaidToUserName = "Luz"
    };

    [Fact]
    public async Task Guests_are_offered_on_both_ends_and_labelled_as_guests()
    {
        var popovers = Render<MudPopoverProvider>();
        var provider = Render<MudDialogProvider>();

        await provider.InvokeAsync(async () =>
            await Services.GetRequiredService<IDialogService>()
                .ShowAsync<UpdateSettlementDialog>("Edit settlement", new DialogParameters<UpdateSettlementDialog>
                {
                    { dialog => dialog.Original, Repayment() }
                }));

        var paidBy = provider.FindComponents<MudSelect<Guid>>()
            .Single(select => select.Instance.Label == "Paid By");

        // Both ends are guests, and both are chosen: a filtered list would have no item to
        // show for either.
        Assert.Equal(Dani.Id, paidBy.Instance.Value);

        await provider.InvokeAsync(() => paidBy.Instance.OpenMenu());

        popovers.WaitForAssertion(() =>
        {
            Assert.Contains("Ana Ruiz", popovers.Markup, StringComparison.Ordinal);
            Assert.Contains("Dani (guest)", popovers.Markup, StringComparison.Ordinal);
            Assert.Contains("Luz (guest)", popovers.Markup, StringComparison.Ordinal);
        });
    }

    private async Task<(IRenderedComponent<MudDialogProvider> Provider, IDialogReference Dialog)> Open(
        DateOnly? coversThrough)
    {
        Render<MudPopoverProvider>();
        var provider = Render<MudDialogProvider>();

        IDialogReference dialog = null!;
        await provider.InvokeAsync(async () =>
            dialog = await Services.GetRequiredService<IDialogService>()
                .ShowAsync<UpdateSettlementDialog>("Edit settlement", new DialogParameters<UpdateSettlementDialog>
                {
                    { d => d.Original, Repayment() },
                    { d => d.CoversThrough, coversThrough }
                }));

        return (provider, dialog);
    }

    private static async Task<JsonPatchDocument<UpdateTransactionRequest>> SaveAsync(
        IRenderedComponent<MudDialogProvider> provider, IDialogReference dialog)
    {
        await provider.FindAll("button").Single(button => button.TextContent.Trim() == "Save")
            .ClickAsync(new MouseEventArgs());

        return (await dialog.GetReturnValueAsync<JsonPatchDocument<UpdateTransactionRequest>>())!;
    }

    private static Operation<UpdateTransactionRequest>? CoversThroughOperation(
        JsonPatchDocument<UpdateTransactionRequest> patch) =>
        patch.Operations.SingleOrDefault(operation =>
            string.Equals(operation.path, "/CoversThrough", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The day a settlement covers through is shown, and saving without touching it sends
    /// nothing about it -- a corrected note must not restate the period.
    /// </summary>
    [Fact]
    public async Task The_period_it_covers_is_kept_when_it_is_left_alone()
    {
        var (provider, dialog) = await Open(new DateOnly(2026, 8, 31));

        provider.WaitForAssertion(() => Assert.Contains("Covers activity up to", provider.Markup));

        var patch = await SaveAsync(provider, dialog);

        Assert.Null(CoversThroughOperation(patch));
    }

    [Fact]
    public async Task Clearing_the_period_sends_it_as_cleared()
    {
        var (provider, dialog) = await Open(new DateOnly(2026, 8, 31));

        var picker = provider.FindComponents<MudDatePicker>()
            .Single(field => field.Instance.Label == "Covers activity up to");
        await provider.InvokeAsync(() => picker.Instance.DateChanged.InvokeAsync(null));

        var operation = CoversThroughOperation(await SaveAsync(provider, dialog));

        Assert.NotNull(operation);
        Assert.Null(operation.value);
    }
}

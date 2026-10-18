using Bunit;
using GroupSplit.App.Shared.Components;
using GroupSplit.Shared;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using MudBlazor;

namespace GroupSplit.App.Web.Test.Components;

public class RequestPaymentDialogTest : ComponentTest
{
    private readonly Guid _group = Guid.NewGuid();
    private readonly Guid _recipient = Guid.NewGuid();
    private readonly Mock<IGroupsClient> _groups = new();
    private static readonly DateOnly Through = new(2026, 8, 31);

    public RequestPaymentDialogTest()
    {
        Services.AddSingleton(_groups.Object);
        Services.AddSingleton(Mock.Of<ISettlement_requestsClient>());
        _groups.Setup(client => client.GetGroupUserBalanceAsync(_group, Through, It.IsAny<int?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new UserGroupBalanceResponse
            {
                OwedToYou = [new DebtInfo { UserId = _recipient, UserName = "Omar", Amount = 50 }]
            });
    }

    private async Task<IRenderedComponent<MudDialogProvider>> Open()
    {
        Render<MudPopoverProvider>();
        var provider = Render<MudDialogProvider>();
        await provider.InvokeAsync(() => Services.GetRequiredService<IDialogService>()
            .ShowAsync<RequestPaymentDialog>("Request payment", new DialogParameters<RequestPaymentDialog>
            {
                { d => d.GroupId, _group }, { d => d.RecipientUserId, _recipient },
                { d => d.RecipientName, "Omar" }, { d => d.CurrentDebt, 10m }
            }));
        return provider;
    }

    [Fact]
    public async Task Picking_a_day_suggests_its_balance_even_when_more_than_current_debt_and_sends_the_day()
    {
        var provider = await Open();
        var picker = provider.FindComponent<MudDatePicker>();
        await provider.InvokeAsync(() => picker.Instance.DateChanged.InvokeAsync(Through.ToDateTime(TimeOnly.MinValue)));
        provider.WaitForAssertion(() => Assert.Equal(50m, provider.FindComponent<MudNumericField<decimal>>().Instance.Value));
        await provider.FindAll("button").Single(button => button.TextContent.Trim() == "Create link")
            .ClickAsync(new MouseEventArgs());
        _groups.Verify(client => client.CreateGroupSettlementRequestAsync(_group,
            It.Is<CreateSettlementRequest>(request => request.RecipientUserId == _recipient &&
                request.Amount == 50 && request.Through == Through && request.UtcOffset != null),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Clearing_the_day_restores_the_current_balance_and_removes_the_cutoff()
    {
        var provider = await Open();
        var picker = provider.FindComponent<MudDatePicker>();
        await provider.InvokeAsync(() => picker.Instance.DateChanged.InvokeAsync(Through.ToDateTime(TimeOnly.MinValue)));
        await provider.InvokeAsync(() => picker.Instance.DateChanged.InvokeAsync(null));
        provider.WaitForAssertion(() => Assert.Equal(10m, provider.FindComponent<MudNumericField<decimal>>().Instance.Value));
        await provider.FindAll("button").Single(button => button.TextContent.Trim() == "Create link")
            .ClickAsync(new MouseEventArgs());
        _groups.Verify(client => client.CreateGroupSettlementRequestAsync(_group,
            It.Is<CreateSettlementRequest>(request => request.Amount == 10 && request.Through == null && request.UtcOffset == null),
            It.IsAny<CancellationToken>()), Times.Once);
    }
}

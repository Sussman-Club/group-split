using GroupSplit.App.Shared.Services;
using GroupSplit.App.Shared.Services.Commands;
using GroupSplit.App.Shared.Services.Errors;
using GroupSplit.Shared;
using Microsoft.AspNetCore.Components;
using Moq;
using MudBlazor;

namespace GroupSplit.App.Web.Test.State;

public sealed class ReceiptCreationCompletionTest
{
    [Fact]
    public async Task Success_and_optional_bank_matching_wait_until_the_original_file_is_attached()
    {
        var created = new TransactionResponse { Id = Guid.NewGuid(), Name = "Receipt", GroupName = "Home" };
        var transactions = new Mock<ITransactionsClient>();
        transactions.Setup(client => client.CreateTransactionAsync(It.IsAny<CreateTransactionRequest>(),
            It.IsAny<CancellationToken>())).ReturnsAsync(created);
        transactions.Setup(client => client.GetTransactionBankMatchesAsync(created.Id,
            It.IsAny<CancellationToken>())).ReturnsAsync(new List<BankTransactionResponse>());
        var attachments = new Mock<IReceiptAttachmentCommands>();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var uploaded = new TaskCompletionSource<ReceiptAttachmentResponse?>(TaskCreationOptions.RunContinuationsAsynchronously);
        attachments.Setup(service => service.UploadAsync(created.Id, It.IsAny<Microsoft.AspNetCore.Components.Forms.IBrowserFile>(),
            It.IsAny<CancellationToken>())).Returns(() => { started.SetResult(); return uploaded.Task; });
        var successes = new List<string>();
        var snackbar = new Mock<ISnackbar>();
        snackbar.Setup(service => service.Add(It.IsAny<string>(), It.IsAny<Severity>(),
                It.IsAny<Action<SnackbarOptions>>(), It.IsAny<string>()))
            .Callback((string text, Severity severity, Action<SnackbarOptions> _, string _) =>
            { if (severity == Severity.Success) successes.Add(text); });
        var errors = new ApiErrorPresenter(Mock.Of<NavigationManager>(), snackbar.Object);
        var commands = new TransactionCommands(transactions.Object, errors, snackbar.Object,
            Mock.Of<IDialogService>(), attachments.Object, Mock.Of<IReceiptCommands>(), new DataChangeNotifier());
        var recording = commands.CreateWithReceiptAsync(new CreateTransactionRequest { Receipt = new SaveReceiptRequest() },
            new BufferedBrowserFile("receipt.jpg", "image/jpeg", DateTimeOffset.UtcNow, [1, 2, 3]), null,
            TestContext.Current.CancellationToken);
        await started.Task.WaitAsync(TestContext.Current.CancellationToken);
        Assert.Empty(successes);
        Assert.False(recording.IsCompleted);
        transactions.Verify(client => client.GetTransactionBankMatchesAsync(created.Id,
            It.IsAny<CancellationToken>()), Times.Never);
        uploaded.SetResult(new ReceiptAttachmentResponse(Guid.NewGuid(), created.Id, null, null,
            "receipt.jpg", "image/jpeg", 3, DateTimeOffset.UtcNow));
        Assert.True(await recording);
        Assert.Equal("Receipt added to Home.", Assert.Single(successes));
    }
}

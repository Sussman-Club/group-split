using GroupSplit.API.Errors;
using GroupSplit.API.Extensions;
using GroupSplit.API.Services;
using GroupSplit.API.Services.ReceiptTranscription;
using GroupSplit.API.Services.ReceiptTranscription.AzureOpenAI;
using GroupSplit.Shared;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace GroupSplit.API.Test.Receipts;

public sealed class AzureOpenAIReceiptTranscriptionProviderTest
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    [InlineData(null)]
    public void Taxable_exempt_and_unknown_status_survive_azure_mapping(bool? taxable)
    {
        var receipt = AzureOpenAIReceiptTranscriptionProvider.ToReceipt(new()
        {
            Subtotal = 10m, Tax = 1m, Total = 11m,
            Items = [new() { Name = "Item", UnitPrice = 10m, Quantity = 1m, TotalPrice = 10m,
                IsTaxable = taxable, TaxAmount = 0m }]
        });
        Assert.Equal(taxable, Assert.Single(receipt.Items).IsTaxable);
        Assert.Equal(0m, receipt.Items[0].TaxAmount);
    }

    [Fact]
    public void Missing_printed_item_tax_is_left_for_the_backend_rate_calculator()
    {
        var receipt = AzureOpenAIReceiptTranscriptionProvider.ToReceipt(new()
        {
            Subtotal = 10m, Tax = .8m, Total = 10.8m,
            Items = [new() { Name = "Item", UnitPrice = 10m, Quantity = 1m, TotalPrice = 10m,
                TaxAmount = null, TaxRate = 8m, IsTaxable = true }]
        });
        var item = Assert.Single(receipt.Items);
        Assert.Equal(0m, item.TaxAmount);
        Assert.Equal(8m, item.TaxRate);
        Assert.True(item.IsTaxable);
    }

    [Fact]
    public async Task Sends_the_receipt_to_the_keyed_chat_client_and_maps_structured_output()
    {
        var client = new StubChatClient(
            "{\"merchantName\":\"Corner Pizza\",\"purchaseDate\":\"2026-09-23\",\"rawText\":\"PZA LG\"," +
            "\"subtotal\":16.00,\"tax\":1.60,\"tip\":0,\"total\":17.60," +
            "\"items\":[{\"name\":\"Pizza\",\"unitPrice\":18.00,\"quantity\":1," +
            "\"totalPrice\":18.00,\"taxAmount\":1.60,\"taxRate\":10,\"isTaxable\":true}," +
            "{\"name\":\"Coupon SAVE2\",\"unitPrice\":-2.00,\"quantity\":1," +
            "\"totalPrice\":-2.00,\"taxAmount\":0,\"taxRate\":null,\"isTaxable\":null}]}");
        var provider = new AzureOpenAIReceiptTranscriptionProvider(
            client,
            NullLogger<AzureOpenAIReceiptTranscriptionProvider>.Instance);

        var receipt = await provider.Transcribe(
            new ReceiptSourceDocument(Guid.NewGuid(), "dinner.jpg", "image/jpeg", [1, 2, 3]),
            TestContext.Current.CancellationToken);

        Assert.Equal(17.60m, receipt.Total);
        Assert.Equal(2, receipt.Items.Count);
        var item = receipt.Items[0];
        Assert.Equal("Pizza", item.Name);
        Assert.Equal(18m, item.TotalPrice);
        Assert.Equal(1.60m, item.TaxAmount);
        Assert.Equal(10m, item.TaxRate);
        Assert.True(item.IsTaxable);
        Assert.Equal("Corner Pizza", receipt.MerchantName);
        Assert.Equal(new DateOnly(2026, 9, 23), receipt.PurchaseDate);
        Assert.Equal(-2m, receipt.Items[1].TotalPrice);
        Assert.Contains("discount", client.Prompt, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("negative-priced item", client.Prompt, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("evidence-based name", client.Prompt, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Coupon [printed code]", client.Prompt, StringComparison.Ordinal);
        Assert.Contains("do not invent a product", client.Prompt, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("dinner.jpg", client.AttachmentDescription);
        Assert.Contains("Do not calculate item taxes", client.Prompt);
    }

    [Fact]
    public async Task A_date_the_model_wrote_another_way_is_dropped_rather_than_failing_the_reading()
    {
        var provider = new AzureOpenAIReceiptTranscriptionProvider(
            new StubChatClient("{\"purchaseDate\":\"23/09/2026\",\"subtotal\":5,\"tax\":0,\"tip\":0,\"total\":5," +
                "\"items\":[{\"name\":\"Soup\",\"unitPrice\":5,\"quantity\":1,\"totalPrice\":5,\"taxAmount\":0}]}"),
            NullLogger<AzureOpenAIReceiptTranscriptionProvider>.Instance);

        var receipt = await provider.Transcribe(
            new ReceiptSourceDocument(Guid.NewGuid(), "lunch.jpg", "image/jpeg", [1]),
            TestContext.Current.CancellationToken);

        Assert.Null(receipt.PurchaseDate);
        Assert.Equal("Soup", Assert.Single(receipt.Items).Name);
    }

    [Fact]
    public async Task Converts_chat_client_failures_to_a_retryable_bad_gateway()
    {
        var provider = new AzureOpenAIReceiptTranscriptionProvider(
            new StubChatClient(new HttpRequestException("Azure is unavailable")),
            NullLogger<AzureOpenAIReceiptTranscriptionProvider>.Instance);

        var error = await Assert.ThrowsAsync<BadGatewayException>(() => provider.Transcribe(
            new ReceiptSourceDocument(Guid.NewGuid(), "dinner.jpg", "image/jpeg", [1]),
            TestContext.Current.CancellationToken));

        Assert.Equal("RECEIPT_TRANSCRIPTION_PROVIDER_UNAVAILABLE", error.Code);
    }

    /// <summary>
    /// The prompt has the model work a unit price out from a total and a quantity, so thirds
    /// of a cent turn up. A bill refuses them, and the review dialog would show the rounded
    /// figure while sending this one back.
    /// </summary>
    [Fact]
    public async Task Money_comes_back_in_cents_and_quantities_and_rates_to_what_a_bill_keeps()
    {
        var provider = new AzureOpenAIReceiptTranscriptionProvider(
            new StubChatClient("{\"subtotal\":10.004,\"tax\":0.8255,\"tip\":1.005,\"total\":11.8345," +
                "\"items\":[{\"name\":\"Bananas\",\"unitPrice\":3.33333,\"quantity\":3.00049," +
                "\"totalPrice\":10.004,\"taxAmount\":0.8255,\"taxRate\":8.250049,\"isTaxable\":true}," +
                "{\"name\":\"Bag\",\"unitPrice\":0.1,\"quantity\":0,\"totalPrice\":0.1,\"taxAmount\":0,\"taxRate\":825}]}"),
            NullLogger<AzureOpenAIReceiptTranscriptionProvider>.Instance);

        var receipt = await provider.Transcribe(
            new ReceiptSourceDocument(Guid.NewGuid(), "market.jpg", "image/jpeg", [1]),
            TestContext.Current.CancellationToken);

        Assert.Equal(10.00m, receipt.Subtotal);
        Assert.Equal(0.83m, receipt.Tax);
        Assert.Equal(1.01m, receipt.Tip);
        Assert.Equal(11.83m, receipt.Total);
        var bananas = receipt.Items[0];
        Assert.Equal(3.33m, bananas.UnitPrice);
        Assert.Equal(3.000m, bananas.Quantity);
        Assert.Equal(10.00m, bananas.TotalPrice);
        Assert.Equal(0.83m, bananas.TaxAmount);
        Assert.Equal(8.2500m, bananas.TaxRate);
        // No quantity is one of it, and a rate past 100 % is a misreading, not a rate.
        Assert.Equal(1m, receipt.Items[1].Quantity);
        Assert.Null(receipt.Items[1].TaxRate);
    }

    [Fact]
    public async Task Null_lists_lines_and_names_are_read_rather_than_answering_a_500()
    {
        var noItems = await new AzureOpenAIReceiptTranscriptionProvider(
                new StubChatClient("{\"merchantName\":null,\"subtotal\":0,\"tax\":0,\"tip\":0,\"total\":0,\"items\":null}"),
                NullLogger<AzureOpenAIReceiptTranscriptionProvider>.Instance)
            .Transcribe(new ReceiptSourceDocument(Guid.NewGuid(), "blank.jpg", "image/jpeg", [1]),
                TestContext.Current.CancellationToken);

        Assert.Empty(noItems.Items);
        Assert.Null(noItems.MerchantName);

        var receipt = await new AzureOpenAIReceiptTranscriptionProvider(
                new StubChatClient("{\"merchantName\":\"   \",\"rawText\":null,\"subtotal\":4,\"tax\":0,\"tip\":0,\"total\":4," +
                    "\"items\":[null," +
                    "{\"name\":null,\"unitPrice\":4,\"quantity\":1,\"totalPrice\":4,\"taxAmount\":0}," +
                    "{\"name\":\" \",\"unitPrice\":0,\"quantity\":1,\"totalPrice\":0,\"taxAmount\":0}]}"),
                NullLogger<AzureOpenAIReceiptTranscriptionProvider>.Instance)
            .Transcribe(new ReceiptSourceDocument(Guid.NewGuid(), "smudged.jpg", "image/jpeg", [1]),
                TestContext.Current.CancellationToken);

        // The money is real, so the line stays under a name somebody will correct; a line
        // with neither a name nor any money is nothing to split.
        var item = Assert.Single(receipt.Items);
        Assert.Equal("Unnamed item", item.Name);
        Assert.Equal(4m, item.TotalPrice);
        Assert.Null(receipt.MerchantName);
    }

    [Fact]
    public async Task A_timeout_is_a_retryable_bad_gateway_but_a_caller_cancellation_stays_one()
    {
        // What the OpenAI SDK throws when its network timeout runs out.
        var timingOut = new AzureOpenAIReceiptTranscriptionProvider(
            new StubChatClient(new TaskCanceledException("The operation was cancelled because it exceeded the configured timeout.",
                new TimeoutException())),
            NullLogger<AzureOpenAIReceiptTranscriptionProvider>.Instance);

        var error = await Assert.ThrowsAsync<BadGatewayException>(() => timingOut.Transcribe(
            new ReceiptSourceDocument(Guid.NewGuid(), "dinner.jpg", "image/jpeg", [1]),
            TestContext.Current.CancellationToken));
        Assert.Equal("RECEIPT_TRANSCRIPTION_PROVIDER_UNAVAILABLE", error.Code);

        using var cancelled = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        await cancelled.CancelAsync();
        var abandoned = new AzureOpenAIReceiptTranscriptionProvider(
            new StubChatClient(new OperationCanceledException(cancelled.Token)),
            NullLogger<AzureOpenAIReceiptTranscriptionProvider>.Instance);

        await Assert.ThrowsAsync<OperationCanceledException>(() => abandoned.Transcribe(
            new ReceiptSourceDocument(Guid.NewGuid(), "dinner.jpg", "image/jpeg", [1]), cancelled.Token));
    }

    [Fact]
    public void Receipt_schema_and_contract_do_not_include_raw_source_text()
    {
        Assert.Null(typeof(AzureOpenAIReceiptTranscriptionProvider.AzureReceipt).GetProperty("RawText"));
        Assert.Null(typeof(SaveReceiptRequest).GetProperty("SourceValues"));
        Assert.Null(typeof(ReceiptResponse).GetProperty("SourceValues"));
    }

    [Fact]
    public void Leaves_transcription_unavailable_when_azure_openai_is_disabled()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ReceiptTranscription:Provider"] = "AzureOpenAI",
                ["AzureOpenAI:Enabled"] = "false"
            })
            .Build();
        var services = new ServiceCollection();
        services.AddReceiptTranscription(configuration);

        using var provider = services.BuildServiceProvider();
        var transcriptionProvider = provider.GetRequiredService<IReceiptTranscriptionProvider>();

        Assert.Equal("unconfigured", transcriptionProvider.Name);
    }

    [Fact]
    public void Resolves_the_selected_provider_from_the_same_keyed_chat_client_used_in_production()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ReceiptTranscription:Provider"] = "AzureOpenAI",
                ["AzureOpenAI:Enabled"] = "true"
            })
            .Build();
        var services = new ServiceCollection();
        services.AddReceiptTranscription(configuration);
        services.AddKeyedSingleton<IChatClient>(
            "receipt-transcription",
            new StubChatClient("{\"items\":[]}"));

        using var serviceProvider = services.BuildServiceProvider();
        var selected = serviceProvider.GetRequiredService<IReceiptTranscriptionProvider>();
        var keyed = serviceProvider.GetRequiredKeyedService<IReceiptTranscriptionProvider>("AzureOpenAI");

        Assert.Equal("Azure OpenAI", selected.Name);
        Assert.Same(selected, keyed);
    }

    private sealed class StubChatClient : IChatClient
    {
        private readonly string? response;
        private readonly Exception? failure;

        public StubChatClient(string response) => this.response = response;

        public StubChatClient(Exception failure) => this.failure = failure;

        public string Prompt { get; private set; } = string.Empty;

        public string AttachmentDescription { get; private set; } = string.Empty;

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            var message = messages.Single();
            Prompt = string.Join("\n", message.Contents.OfType<TextContent>().Select(content => content.Text));
            AttachmentDescription = string.Join(
                "\n",
                message.Contents.OfType<DataContent>().Select(content => content.Name ?? string.Empty));

            if (failure is not null)
                throw failure;

            var assistant = new ChatMessage(ChatRole.Assistant, response!);
            return Task.FromResult(new ChatResponse(assistant));
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation]
            CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            yield break;
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }
}

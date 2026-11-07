using GroupSplit.API.Extensions;
using GroupSplit.API.Services.Banking.Suggestions;
using GroupSplit.Data.Entities;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace GroupSplit.API.Test.Banking;

public class ReceiptDuplicateDiscriminatorTest
{
    [Theory]
    [InlineData("duplicate", .95, true)]
    [InlineData("duplicate", .89, false)]
    [InlineData("uncertain", .99, false)]
    [InlineData("duplicate", 1.1, false)]
    public async Task Suggests_only_a_high_confidence_duplicate(string choice, double confidence, bool expected)
    {
        var model = new Mock<IDecisionClient>();
        model.Setup(client => client.ChooseAsync(It.IsAny<object>(), It.IsAny<string>(),
                It.IsAny<IReadOnlyDictionary<string, string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DecisionAnswer(choice, confidence));
        var service = Create(model.Object);
        var first = Receipt("Coffee");
        var second = Receipt("Coffee");
        Assert.Equal(expected, await service.IsDuplicate(first, second, Guid.NewGuid(), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Missing_item_evidence_does_not_call_the_model()
    {
        var model = new Mock<IDecisionClient>(MockBehavior.Strict);
        var empty = new Receipt { MerchantName = "Shop", Subtotal = 5, Total = 5 };
        Assert.False(await Create(model.Object).IsDuplicate(empty, Receipt("Coffee"), Guid.NewGuid(), TestContext.Current.CancellationToken));
        model.VerifyNoOtherCalls();
    }

    private static ReceiptDuplicateDiscriminator Create(IDecisionClient client)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddHybridCache();
        var cache = services.BuildServiceProvider().GetRequiredService<HybridCache>();
        return new ReceiptDuplicateDiscriminator(client, cache,
            new SuggestionModelLimiter(Options.Create(new ApiRateLimits())),
            Options.Create(new FilingSuggestionOptions { Endpoint = new Uri("https://example.test/"), Deployment = "test", Key = "test" }),
            NullLogger<ReceiptDuplicateDiscriminator>.Instance);
    }

    private static Receipt Receipt(string item)
    {
        var receipt = new Receipt { MerchantName = "Shop", Subtotal = 5, Total = 5 };
        receipt.Items.Add(new ReceiptItem { Name = item, NormalizedName = item.ToLowerInvariant(), UnitPrice = 5, TotalPrice = 5 });
        return receipt;
    }
}

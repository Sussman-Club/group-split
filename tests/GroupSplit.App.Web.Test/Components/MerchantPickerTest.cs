using Bunit;
using GroupSplit.App.Shared.Components;
using GroupSplit.App.Shared.Services.Commands;
using GroupSplit.Shared;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using MudBlazor;

namespace GroupSplit.App.Web.Test.Components;

/// <summary>
/// The type-ahead on the two expense dialogs. What it may ask the server for is the part
/// worth pinning: the API answers a search of one letter with a refusal and any search with
/// at most twenty of the caller's own places, so the field waits for two letters and for
/// the person to pause.
/// </summary>
public class MerchantPickerTest : ComponentTest
{
    private readonly Mock<IMerchantCommands> _merchants = new();

    public MerchantPickerTest()
    {
        _merchants
            .Setup(m => m.SearchAsync(It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);

        Services.AddSingleton(_merchants.Object);
    }

    private MudAutocomplete<MerchantResponse> Autocomplete() =>
        Render<MerchantPicker>().FindComponent<MudAutocomplete<MerchantResponse>>().Instance;

    [Fact]
    public void It_waits_for_two_letters_and_a_pause()
    {
        var field = Autocomplete();

        Assert.Equal(2, field.MinCharacters);
        Assert.True(field.DebounceInterval > 0);
    }

    /// <summary>
    /// MinCharacters counts what was typed, spaces included, so "a " reaches the search;
    /// trimmed it is one letter, which the API would refuse.
    /// </summary>
    [Fact]
    public async Task One_letter_and_a_space_is_not_sent()
    {
        var found = await Autocomplete().SearchFunc!("a ", Xunit.TestContext.Current.CancellationToken);

        Assert.Empty(found);
        _merchants.Verify(m => m.SearchAsync(It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Two_letters_are_sent_trimmed()
    {
        await Autocomplete().SearchFunc!(" li ", Xunit.TestContext.Current.CancellationToken);

        _merchants.Verify(m => m.SearchAsync("li", It.IsAny<CancellationToken>()), Times.Once);
    }
}

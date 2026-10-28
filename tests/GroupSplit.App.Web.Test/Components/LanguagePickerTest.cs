using System.Globalization;
using Bunit;
using GroupSplit.App.Shared.Components;
using GroupSplit.App.Shared.Localization;
using GroupSplit.Shared;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using Microsoft.AspNetCore.Components;
using Moq;

namespace GroupSplit.App.Web.Test.Components;

public sealed class LanguagePickerTest : ComponentTest
{
    private readonly Mock<IUsersClient> _users = new();

    public LanguagePickerTest() => Services.AddSingleton(_users.Object);

    [Theory]
    [InlineData("es")]
    [InlineData("pt")]
    [InlineData("en")]
    public async Task Selection_saves_the_account_then_the_device_and_reloads(string language)
    {
        var picker = Render<LanguagePicker>();
        await picker.Find("select").ChangeAsync(new() { Value = language });
        _users.Verify(x => x.SetCurrentUserLanguageAsync(
            It.Is<LanguagePreferenceRequest>(r => r.Language == language), It.IsAny<CancellationToken>()), Times.Once);
        Assert.Equal(language, Assert.Single(JSInterop.Invocations["gs.language.save"]).Arguments[0]);
    }

    [Fact]
    public async Task Failed_save_keeps_the_device_preference_and_offers_retry()
    {
        _users.Setup(x => x.SetCurrentUserLanguageAsync(It.IsAny<LanguagePreferenceRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException());
        var picker = Render<LanguagePicker>();
        await picker.Find("select").ChangeAsync(new() { Value = "es" });
        Assert.Contains("could not be saved", picker.Find("[role=alert]").TextContent);
        Assert.Empty(JSInterop.Invocations["gs.language.save"]);
        Assert.False(picker.Find("select").HasAttribute("disabled"));
    }

    [Theory]
    [InlineData("es-MX", "es")]
    [InlineData("pt-BR", "pt")]
    [InlineData("en-GB", "en")]
    [InlineData("de-DE", "en")]
    [InlineData(null, "en")]
    public void Browser_regions_resolve_to_supported_languages(string? browser, string expected) =>
        Assert.Equal(expected, AppLanguages.Resolve(browser));

    [Theory]
    [InlineData("es", "Configuración")]
    [InlineData("pt", "Definições")]
    public void Resources_translate_and_missing_entries_have_readable_English_fallback(string language, string expected)
    {
        var previous = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(language);
            var l = Services.GetRequiredService<IStringLocalizer<AppStrings>>();
            Assert.Equal(expected, l["Settings"].Value);
            Assert.Equal("A new English sentence", l["A new English sentence"].Value);
        }
        finally { CultureInfo.CurrentUICulture = previous; }
    }

    [Theory]
    [InlineData("es")]
    [InlineData("pt")]
    public void An_account_language_different_from_startup_reloads_instead_of_mutating_render_culture(string language)
    {
        var persistence = new Mock<ILanguagePersistence>();
        Services.AddSingleton(persistence.Object);
        Render<LanguageSync>(p => p.Add(x => x.Language, language));
        persistence.Verify(x => x.ApplyAsync(language, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public void Matching_startup_language_does_not_trigger_a_reload()
    {
        var persistence = new Mock<ILanguagePersistence>();
        Services.AddSingleton(persistence.Object);
        var current = AppLanguages.Resolve(CultureInfo.CurrentUICulture.Name);
        Render<LanguageSync>(p => p.Add(x => x.Language, current));
        persistence.Verify(x => x.ApplyAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        Assert.Equal(current, Assert.Single(JSInterop.Invocations["gs.language.apply"]).Arguments[0]);
    }
}

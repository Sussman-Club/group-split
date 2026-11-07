using System.Globalization;
using System.Resources;
using GroupSplit.App.Shared.Extensions;
using GroupSplit.App.Shared.Localization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;

namespace GroupSplit.App.Web.Test.State;

public sealed class LanguageResourcesTest
{
    [Theory]
    [InlineData("es", "Review receipt match", "Revisar coincidencia del recibo")]
    [InlineData("pt", "Review receipt match", "Rever correspondência do recibo")]
    [InlineData("es", "Find an expense", "Buscar un gasto")]
    [InlineData("pt", "Find an expense", "Procurar uma despesa")]
    [InlineData("es", "Review matches", "Revisar coincidencias")]
    [InlineData("pt", "Review matches", "Rever correspondências")]
    [InlineData("es", "Your purchases", "Tus compras")]
    [InlineData("pt", "Your purchases", "As suas compras")]
    [InlineData("es", "{0} purchase waiting for review", "{0} compra pendiente de revisión")]
    [InlineData("es", "{0} purchases waiting for review", "{0} compras pendientes de revisión")]
    [InlineData("pt", "{0} purchase waiting for review", "{0} compra por rever")]
    [InlineData("pt", "{0} purchases waiting for review", "{0} compras por rever")]
    public void Inbox_review_actions_have_translations(string language, string key, string expected)
    {
        var manager = new ResourceManager(typeof(AppStrings));
        Assert.Equal(expected, manager.GetResourceSet(CultureInfo.GetCultureInfo(language), true, false)!.GetString(key));
    }
    [Theory]
    [InlineData("Tickets", "Tickets")]
    [InlineData("Waiting for customer", "Pendiente de respuesta del usuario")]
    [InlineData("Ticket management", "Gestión del ticket")]
    [InlineData("Conversation & notes", "Conversación y notas")]
    [InlineData("Internal note · staff only", "Nota interna · solo el equipo")]
    [InlineData("Report submitted", "Informe enviado")]
    public void Support_copy_has_explicit_Spanish_resources(string key, string expected)
    {
        var manager = new ResourceManager(typeof(AppStrings));
        var spanish = manager.GetResourceSet(CultureInfo.GetCultureInfo("es"), true, false)!;
        Assert.Equal(expected, spanish.GetString(key));
    }

    [Theory]
    [InlineData("es")]
    [InlineData("pt")]
    public void Built_in_rules_translate_but_custom_rule_names_remain_verbatim(string language)
    {
        var previous = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(language);
            var builtIn = new GroupSplit.Shared.SplitRuleResponse(Guid.NewGuid(), Guid.NewGuid(),
                "Everyone evenly", BuiltIn: true, DividesEvenly: true);
            Assert.Equal("Todos por igual", builtIn.DisplayName(null));
            Assert.Equal("Everyone evenly", (builtIn with { BuiltIn = false }).DisplayName(null));
        }
        finally { CultureInfo.CurrentUICulture = previous; }
    }

    [Fact]
    public void Resource_sources_have_no_keys_that_dotnet_would_silently_ignore()
    {
        foreach (var path in Directory.EnumerateFiles(Path.Combine(AppContext.BaseDirectory, "Localization"), "*.resx"))
        {
            var keys = System.Xml.Linq.XDocument.Load(path).Root!.Elements("data")
                .Select(node => (string)node.Attribute("name")!).ToArray();
            Assert.Equal(keys.Length, keys.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        }
    }

    [Theory]
    [InlineData("es", "Página siguiente")]
    [InlineData("pt", "Página seguinte")]
    public void Mud_controls_use_the_catalog_and_signal_missing_keys_for_English_fallback(string language, string expected)
    {
        var previous = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(language);
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddLocalization();
            using var provider = services.BuildServiceProvider();
            var l = provider.GetRequiredService<IStringLocalizer<AppStrings>>();
            var mud = new AppMudLocalizer(l);
            Assert.Equal(expected, mud["MudDataGridPager_NextPage"].Value);
            Assert.True(mud["A_future_MudBlazor_key"].ResourceNotFound);
        }
        finally { CultureInfo.CurrentUICulture = previous; }
    }

    [Fact]
    public void Every_language_has_exactly_the_same_nonempty_resource_keys()
    {
        var manager = new ResourceManager(typeof(AppStrings));
        var english = Read(CultureInfo.InvariantCulture);
        foreach (var language in GroupSplit.Shared.AppLanguages.Supported.Where(l => l != "en"))
        {
            var translated = Read(CultureInfo.GetCultureInfo(language));
            Assert.Equal(english.Keys.Order(), translated.Keys.Order());
            Assert.All(translated.Values, value => Assert.False(string.IsNullOrWhiteSpace(value)));
            foreach (var (key, value) in translated)
                Assert.Equal(Placeholders(english[key]), Placeholders(value));
        }
        Dictionary<string, string> Read(CultureInfo culture) => manager.GetResourceSet(culture, true, false)!
            .Cast<System.Collections.DictionaryEntry>().ToDictionary(e => (string)e.Key, e => (string)e.Value!);
        static string[] Placeholders(string value) => System.Text.RegularExpressions.Regex.Matches(value, @"\{\d+(?:[^}]*)\}")
            .Select(m => m.Value).Order().ToArray();
    }

    [Theory]
    [InlineData("en", ".")]
    [InlineData("es", ",")]
    [InlineData("pt", ",")]
    public void Culture_changes_number_format_but_never_the_expense_currency(string language, string separator)
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(language);
            Assert.Contains("12" + separator + "34", 12.34m.ToMoney("USD"));
            Assert.Contains("$", 12.34m.ToMoney());
            Assert.Contains("€", 12.34m.ToMoney("EUR"));
            Assert.DoesNotContain("€", 12.34m.ToMoney("USD"));
        }
        finally { CultureInfo.CurrentCulture = previous; }
    }
}

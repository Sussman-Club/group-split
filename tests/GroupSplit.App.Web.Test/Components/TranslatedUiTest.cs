using System.ComponentModel.DataAnnotations;
using System.Globalization;
using Bunit;
using GroupSplit.App.Shared.Components;
using GroupSplit.Shared.Localization;
using GroupSplit.Shared;

namespace GroupSplit.App.Web.Test.Components;

public sealed class TranslatedUiTest : ComponentTest
{
    [Theory]
    [InlineData("es", "categoría", "pagado por")]
    [InlineData("pt", "categoria", "pago por")]
    public void Expense_chips_translate_labels_and_accessibility_but_preserve_user_values(
        string language, string category, string paidBy)
    {
        using var culture = new UiCulture(language);
        using var context = new LocalizedContext();
        var empty = context.Render<ExpenseChip>(p => p.Add(x => x.Label, "category"));
        Assert.Contains(category, empty.Find(".v").TextContent);
        var selected = context.Render<ExpenseChip>(p => p.Add(x => x.Label, "payer")
            .Add(x => x.Lead, "paid by").Add(x => x.Value, "Ana & João").Add(x => x.Set, true));
        Assert.Contains(paidBy, selected.Find(".k").TextContent);
        Assert.Equal("Ana & João", selected.Find(".v").TextContent);
        Assert.DoesNotContain("Change it", selected.Find("button").GetAttribute("aria-label"));
    }

    [Theory]
    [InlineData("es", "El importe no puede ser cero.", "La fecha es obligatoria.")]
    [InlineData("pt", "O valor não pode ser zero.", "A data é obrigatória.")]
    public void Client_validation_and_server_messages_use_the_current_language(
        string language, string amount, string date)
    {
        using var culture = new UiCulture(language);
        var results = new List<ValidationResult>();
        var request = new CreateTransactionRequest();
        Validator.TryValidateObject(request, new ValidationContext(request), results, true);
        Assert.Contains(results, r => r.ErrorMessage == amount);
        Assert.Contains(results, r => r.ErrorMessage == date);
        Assert.Equal(amount, ValidationMessages.Translate("Amount cannot be zero."));
        Assert.Contains("128", ValidationMessages.Translate("Name must be at most 128 characters."));
        Assert.DoesNotContain("Name must", ValidationMessages.Translate("Name must be at most 128 characters."));
        Assert.Equal("Unknown server refusal", ValidationMessages.Translate("Unknown server refusal"));
    }

    private sealed class LocalizedContext : ComponentTest;

    private sealed class UiCulture : IDisposable
    {
        private readonly CultureInfo _previous = CultureInfo.CurrentUICulture;
        public UiCulture(string language) => CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(language);
        public void Dispose() => CultureInfo.CurrentUICulture = _previous;
    }
}

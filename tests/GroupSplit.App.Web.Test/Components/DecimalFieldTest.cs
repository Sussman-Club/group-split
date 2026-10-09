using System.Globalization;
using System.Text.RegularExpressions;
using Bunit;
using GroupSplit.App.Shared.Components;

namespace GroupSplit.App.Web.Test.Components;

/// <summary>
/// What an amount typed into the app is read as (#313). Every money, share and percentage
/// field is a <see cref="DecimalField{T}"/>, so these are the outcomes of all of them.
/// </summary>
/// <remarks>
/// Each case runs under en-US and es-ES, the two cultures that read the separators opposite
/// ways. The defect was a culture-driven parse, so a test that only ran under the build
/// machine's culture would pass on one of them whatever the field did.
/// </remarks>
public class DecimalFieldTest : ComponentTest
{
    public static TheoryData<string, string, decimal> Read => new()
    {
        // The case the issue is about: a phone keypad's decimal key. Never 1250.
        { "en-US", "12,50", 12.50m },
        { "es-ES", "12,50", 12.50m },
        { "en-US", "12.50", 12.50m },
        { "es-ES", "12.50", 12.50m },
        { "es-ES", "1234,56", 1234.56m },
        { "en-US", " 7 ", 7m },
        { "en-US", "-3,10", -3.10m },
        { "en-US", ",5", 0.5m }
    };

    public static TheoryData<string, string> Refused => new()
    {
        // Thousands separators, either way round: nothing is grouping, so neither is guessed.
        { "en-US", "1.234,56" },
        { "es-ES", "1.234,56" },
        { "en-US", "1,234.56" },
        { "es-ES", "1,234.56" },
        { "en-US", "1,234,567" },
        // Money holds cents. A third decimal is refused rather than rounded to a figure
        // nobody typed -- and rather than stored as 12.555 and shown as 12.56.
        { "en-US", "12.555" },
        { "es-ES", "12.555" },
        { "es-ES", "12,555" },
        { "en-US", "12 50" },
        { "en-US", "$12" }
    };

    [Theory]
    [MemberData(nameof(Read))]
    public void A_typed_amount_is_read_with_either_decimal_separator(string culture, string typed, decimal expected)
    {
        using var _ = new CultureScope(culture);
        decimal? changed = null;

        var field = Render<DecimalField<decimal>>(parameters => parameters
            .Add(f => f.ValueChanged, value => changed = value));

        field.Find("input").Change(typed);

        Assert.Equal(expected, changed);
        Assert.False(field.Instance.ConversionError);
    }

    [Theory]
    [MemberData(nameof(Refused))]
    public void A_typed_amount_that_could_be_read_two_ways_is_refused(string culture, string typed)
    {
        using var _ = new CultureScope(culture);
        var changes = new List<decimal>();

        var field = Render<DecimalField<decimal>>(parameters => parameters
            .Add(f => f.Value, 5m)
            .Add(f => f.ValueChanged, changes.Add));

        field.Find("input").Change(typed);
        field.Find("input").Blur();

        Assert.True(field.Instance.ConversionError);
        Assert.False(string.IsNullOrWhiteSpace(field.Instance.ConversionErrorMessage));

        // The model keeps no part of it: not 1250, not 123456, not 12.555 -- and not the
        // zero MudBlazor writes on a failed parse either. It still holds what it had.
        Assert.Empty(changes);

        // And the person is told, beside what they typed, not after Save.
        Assert.Contains(field.Instance.ConversionErrorMessage!, field.Markup);
        Assert.Equal(typed, field.Find("input").GetAttribute("value"));
    }

    [Fact]
    public void Correcting_a_refused_amount_reads_it_and_clears_the_error()
    {
        var changes = new List<decimal>();

        var field = Render<DecimalField<decimal>>(parameters => parameters
            .Add(f => f.Value, 5m)
            .Add(f => f.ValueChanged, changes.Add));

        field.Find("input").Change("1.234,56");
        field.Find("input").Change("1234,56");

        Assert.Equal([1234.56m], changes);
        Assert.False(field.Instance.ConversionError);
    }

    /// <summary>
    /// A value the owner sets -- "split evenly" over a field somebody mistyped -- is shown,
    /// rather than left behind the refused text.
    /// </summary>
    [Fact]
    public void A_new_value_from_the_owner_replaces_a_refused_text()
    {
        var field = Render<DecimalField<decimal>>(parameters => parameters
            .Add(f => f.Value, 5m)
            .Add(f => f.ValueChanged, _ => { }));

        field.Find("input").Change("1.234,56");

        field.Render(parameters => parameters.Add(f => f.Value, 33.33m));

        Assert.False(field.Instance.ConversionError);
        Assert.Equal("33.33", field.Find("input").GetAttribute("value"));
    }

    /// <summary>
    /// Leaving the field shows the number that will be stored, in the one format every
    /// amount is shown in -- so somebody who typed a comma sees what it was read as.
    /// </summary>
    [Theory]
    [InlineData("en-US")]
    [InlineData("es-ES")]
    public void What_was_read_is_shown_back_with_a_point_and_two_places(string culture)
    {
        using var _ = new CultureScope(culture);

        var field = Render<DecimalField<decimal>>(parameters => parameters
            .Add(f => f.ValueChanged, _ => { }));

        var input = field.Find("input");
        input.Change("1234,5");
        input.Blur();

        Assert.Equal("1234.50", field.Find("input").GetAttribute("value"));
    }

    [Theory]
    [InlineData("en-US")]
    [InlineData("es-ES")]
    public void A_receipt_quantity_holds_three_places_and_a_tax_rate_four(string culture)
    {
        using var _ = new CultureScope(culture);
        decimal? quantity = null;
        decimal? rate = null;

        var quantityField = Render<DecimalField<decimal?>>(parameters => parameters
            .Add(f => f.Places, 3)
            .Add(f => f.ValueChanged, value => quantity = value));
        var rateField = Render<DecimalField<decimal?>>(parameters => parameters
            .Add(f => f.Places, 4)
            .Add(f => f.ValueChanged, value => rate = value));

        quantityField.Find("input").Change("1,255");
        rateField.Find("input").Change("8,8750");

        Assert.Equal(1.255m, quantity);
        Assert.Equal(8.875m, rate);

        quantityField.Find("input").Change("1,2555");
        Assert.True(quantityField.Instance.ConversionError);
    }

    [Fact]
    public void A_cleared_optional_amount_is_no_amount_rather_than_an_error()
    {
        decimal? changed = 1m;

        var field = Render<DecimalField<decimal?>>(parameters => parameters
            .Add(f => f.Value, 4m)
            .Add(f => f.ValueChanged, value => changed = value));

        field.Find("input").Change("");

        Assert.Null(changed);
        Assert.False(field.Instance.ConversionError);
    }

    /// <summary>
    /// The guard that keeps this fixed. The defect was eight fields each parsing on their
    /// own, so one new <c>MudNumericField</c> for an amount would bring it straight back.
    /// Whole-number fields -- a rule's shares -- have no decimal separator to misread and
    /// may stay as they are.
    /// </summary>
    [Fact]
    public void No_field_in_the_app_reads_a_decimal_with_MudBlazors_own_parse()
    {
        var components = Path.Combine(RepositoryRoot(), "src", "GroupSplit.App");

        var offenders = Directory
            .EnumerateFiles(components, "*.razor", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                           && !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            .SelectMany(path => Regex.Matches(File.ReadAllText(path), @"<MudNumericField\b.*?/>", RegexOptions.Singleline)
                .Where(tag => !tag.Value.Contains("T=\"int\""))
                .Select(_ => Path.GetFileName(path)))
            .ToList();

        Assert.Empty(offenders);
    }

    private static string RepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "group-split.slnx")))
                return directory.FullName;
        }

        throw new InvalidOperationException("The test is not running inside the repository.");
    }

    /// <summary>Sets both cultures for one test and puts them back after it.</summary>
    private sealed class CultureScope : IDisposable
    {
        private readonly CultureInfo _culture = CultureInfo.CurrentCulture;
        private readonly CultureInfo _uiCulture = CultureInfo.CurrentUICulture;

        public CultureScope(string name)
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(name);
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(name);
        }

        public void Dispose()
        {
            CultureInfo.CurrentCulture = _culture;
            CultureInfo.CurrentUICulture = _uiCulture;
        }
    }
}

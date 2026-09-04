using System.ComponentModel.DataAnnotations;
using GroupSplit.Shared.CustomValidationAttributes;

namespace GroupSplit.API.Test.Validation;

/// <summary>
/// The two custom validation attributes guard every amount the API accepts, and neither
/// had a test. Several of these pin behaviour that is surprising rather than desirable —
/// they are marked as such, so that changing the behaviour breaks a test that says what
/// it was, instead of one that says it was correct.
/// </summary>
public class GreaterThanAttributeTests
{
    private static ValidationResult? Validate(decimal min, object? value) =>
        new GreaterThanAttribute((double)min)
            .GetValidationResult(value, new ValidationContext(new object()));

    [Theory]
    [InlineData(0.01)]
    [InlineData(1)]
    [InlineData(1_000_000)]
    public void A_value_above_the_minimum_passes(decimal value)
    {
        Assert.Null(Validate(0m, value));
    }

    [Fact]
    public void The_minimum_itself_is_not_greater_than_the_minimum()
    {
        Assert.NotNull(Validate(10m, 10m));
    }

    [Theory]
    [InlineData(9.99)]
    [InlineData(0)]
    [InlineData(-1)]
    public void A_value_at_or_below_the_minimum_fails(decimal value)
    {
        Assert.NotNull(Validate(10m, value));
    }

    [Fact]
    public void The_failure_message_names_the_minimum()
    {
        var result = Validate(5m, 1m);

        Assert.NotNull(result);
        Assert.Contains("5", result.ErrorMessage);
    }

    /// <summary>
    /// Surprising, and worth knowing about: the attribute rejects anything that is not a
    /// <see cref="decimal"/>, and <c>null</c> is not a decimal. On an optional property
    /// that means an absent value is rejected rather than skipped, which is the opposite
    /// of how every built-in attribute except <c>[Required]</c> behaves.
    /// </summary>
    [Fact]
    public void An_absent_value_is_rejected_rather_than_skipped()
    {
        Assert.NotNull(Validate(0m, null));
    }

    /// <summary>
    /// Same root cause as the null case: the type test is <c>is not decimal</c>, and an
    /// <see cref="int"/> does not satisfy it even though it converts to one implicitly.
    /// A property typed as anything but decimal therefore always fails.
    /// </summary>
    [Theory]
    [InlineData(42)]
    [InlineData(42.0d)]
    [InlineData("42")]
    public void A_value_that_is_not_a_decimal_is_rejected_whatever_it_holds(object value)
    {
        Assert.NotNull(Validate(0m, value));
    }
}

public class MaxDecimalPlacesAttributeTests
{
    [Theory]
    [InlineData(2, 1.23)]
    [InlineData(2, 1.2)]
    [InlineData(2, 1)]
    [InlineData(2, -1.23)]
    [InlineData(0, 5)]
    [InlineData(4, 0.0001)]
    public void A_value_within_the_allowed_scale_passes(int places, decimal value)
    {
        Assert.True(new MaxDecimalPlacesAttribute(places).IsValid(value));
    }

    [Theory]
    [InlineData(2, 1.234)]
    [InlineData(2, -1.234)]
    [InlineData(0, 5.5)]
    [InlineData(4, 0.00001)]
    public void A_value_with_too_many_places_fails(int places, decimal value)
    {
        Assert.False(new MaxDecimalPlacesAttribute(places).IsValid(value));
    }

    /// <summary>
    /// Trailing zeros are part of a decimal's representation but not of its scale for this
    /// purpose: 1.2300 is still two significant places once truncated, so it passes.
    /// </summary>
    [Fact]
    public void Trailing_zeros_do_not_count_against_the_scale()
    {
        Assert.True(new MaxDecimalPlacesAttribute(2).IsValid(1.2300m));
    }

    /// <summary>
    /// The mirror image of <see cref="GreaterThanAttributeTests"/>: this attribute takes
    /// the other branch on a non-decimal and passes it. So the two attributes on the same
    /// property disagree about what an absent value means.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData(42)]
    [InlineData("nonsense")]
    public void A_value_that_is_not_a_decimal_is_left_alone(object? value)
    {
        Assert.True(new MaxDecimalPlacesAttribute(2).IsValid(value));
    }

    [Fact]
    public void A_negative_scale_is_rejected_when_the_attribute_is_constructed()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new MaxDecimalPlacesAttribute(-1));
    }

    /// <summary>
    /// The scale is applied by multiplying, so a value large enough that
    /// <c>value * 10^places</c> leaves decimal's range throws out of validation rather
    /// than returning false. An amount that big is not a realistic request, but a
    /// validation attribute throwing where it is expected to answer is worth pinning:
    /// it surfaces as a 500, not a 400.
    /// </summary>
    [Fact]
    public void A_value_too_large_to_scale_throws_instead_of_failing_validation()
    {
        Assert.Throws<OverflowException>(
            () => new MaxDecimalPlacesAttribute(2).IsValid(decimal.MaxValue));
    }
}

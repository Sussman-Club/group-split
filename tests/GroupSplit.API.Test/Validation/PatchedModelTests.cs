using GroupSplit.API.Extensions;
using GroupSplit.Shared;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;

namespace GroupSplit.API.Test.Validation;

/// <summary>
/// The PATCH routes apply a JSON Patch and then hand the result to a service.
/// <c>AddValidation()</c> checks an endpoint's parameters, and on those routes the
/// parameter is the patch document — so nothing checked the model the patch produced, and
/// every annotation the create path enforces was skipped on update. This is the check that
/// closes that, tested here because the endpoints themselves have no harness yet.
/// </summary>
public class PatchedModelTests
{
    private static UpdateTransactionRequest Valid() => new()
    {
        Name = "Lunch",
        Description = "With the team",
        Amount = 12.34m,
        DateTime = DateTimeOffset.UtcNow,
        PaidByUserId = Guid.NewGuid(),
        RuleVersionId = Guid.NewGuid()
    };

    [Fact]
    public void A_model_that_satisfies_its_annotations_passes()
    {
        Assert.True(PatchedModel.IsValid(Valid(), out _));
    }

    [Fact]
    public void A_cleared_required_name_is_rejected()
    {
        Assert.False(PatchedModel.IsValid(Valid() with { Name = null! }, out _));
    }

    [Fact]
    public void A_name_past_its_length_limit_is_rejected()
    {
        Assert.False(PatchedModel.IsValid(Valid() with { Name = new string('x', 125) }, out _));
    }

    [Fact]
    public void A_description_past_its_length_limit_is_rejected()
    {
        Assert.False(
            PatchedModel.IsValid(Valid() with { Description = new string('x', 257) }, out _));
    }

    /// <summary>
    /// The case that motivated matching the create path's annotations on the update one:
    /// an edit could set fractions of a cent that creating the same transaction refused.
    /// </summary>
    [Fact]
    public void An_amount_with_more_than_two_decimal_places_is_rejected()
    {
        Assert.False(PatchedModel.IsValid(Valid() with { Amount = 10.005m }, out _));
    }

    /// <summary>
    /// An amount too large to scale used to throw out of the attribute. It reaches this
    /// path as an ordinary failure now, which is the difference between a 400 and a 500.
    /// </summary>
    [Fact]
    public void An_amount_too_large_to_check_is_rejected_rather_than_throwing()
    {
        Assert.False(PatchedModel.IsValid(Valid() with { Amount = decimal.MaxValue }, out _));
    }

    /// <summary>
    /// The response has to be the same shape the framework produces on the create routes,
    /// so a client parses one format whichever verb it used.
    /// </summary>
    [Fact]
    public void A_rejection_comes_back_as_a_validation_problem_naming_the_field()
    {
        Assert.False(PatchedModel.IsValid(Valid() with { Amount = 10.005m }, out var problem));

        var validationProblem = Assert.IsType<ProblemHttpResult>(problem);
        Assert.Equal(StatusCodes.Status400BadRequest, validationProblem.StatusCode);
    }

    [Fact]
    public void Every_broken_annotation_is_reported_not_just_the_first()
    {
        Assert.False(PatchedModel.IsValid(
            Valid() with { Name = null!, Amount = 10.005m }, out var problem));

        var validationProblem = Assert.IsType<ProblemHttpResult>(problem);
        var errors = Assert.IsAssignableFrom<HttpValidationProblemDetails>(validationProblem.ProblemDetails);

        Assert.Contains(nameof(UpdateTransactionRequest.Name), errors.Errors.Keys);
        Assert.Contains(nameof(UpdateTransactionRequest.Amount), errors.Errors.Keys);
    }

    /// <summary>
    /// Group and rule updates go through the same check, so they are covered here too.
    /// </summary>
    [Fact]
    public void The_same_check_covers_the_other_patch_routes()
    {
        Assert.True(PatchedModel.IsValid(new CreateGroupRequest { Name = "Trip" }, out _));
        Assert.False(PatchedModel.IsValid(new CreateGroupRequest { Name = null! }, out _));
    }
}

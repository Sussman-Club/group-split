using System.ComponentModel.DataAnnotations;

namespace GroupSplit.API.Extensions;

/// <summary>
/// Validation for a model produced by applying a JSON Patch.
/// </summary>
/// <remarks>
/// <c>AddValidation()</c> validates an endpoint's parameters. On the PATCH routes the
/// parameter is the <c>JsonPatchDocument</c>, not the model applying it produces, so the
/// model reached the service having been checked by nothing: every annotation the create
/// path enforces — required names, string lengths, the two-decimal limit on an amount —
/// was skipped on update. A name could be cleared, a description could be any length, and
/// an amount could carry fractions of a cent that the split arithmetic then divided up.
/// </remarks>
public static class PatchedModel
{
    /// <summary>
    /// Validates <paramref name="model"/>, handing back the response to return when it
    /// does not hold up.
    /// </summary>
    public static bool IsValid<T>(T model, out IResult problem) where T : notnull
    {
        var results = new List<ValidationResult>();

        if (Validator.TryValidateObject(
                model, new ValidationContext(model), results, validateAllProperties: true))
        {
            problem = Results.Empty;
            return true;
        }

        // Shaped the way the framework reports validation failures on the create routes,
        // so a client sees one format whichever verb it used.
        problem = Results.ValidationProblem(results
            .SelectMany(result => result.MemberNames
                .DefaultIfEmpty(string.Empty)
                .Select(member => (Member: member, Error: result.ErrorMessage ?? "Invalid value.")))
            .GroupBy(failure => failure.Member, failure => failure.Error)
            .ToDictionary(failures => failures.Key, failures => failures.ToArray()));

        return false;
    }
}

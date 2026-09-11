using GroupSplit.App.Shared.Services.Errors;
using GroupSplit.Shared;
using MudBlazor;

namespace GroupSplit.App.Shared.Services.Commands;

/// <inheritdoc cref="IMerchantCommands"/>
/// <remarks>
/// The reads swallow their failures on purpose, which is the one way these differ from the
/// other commands. Both feed a type-ahead that fires as somebody types, so a presenter on
/// each would turn a dropped connection into a queue of identical snackbars over a field
/// the person is still using. An empty list is what a failed search and a search with no
/// matches both look like to them, and neither is worth interrupting for.
/// <para>
/// <see cref="CreateAsync"/> is different in both directions: it is a write, and it is one
/// somebody clicked a button to ask for, so it says what happened and a refusal is shown.
/// It does not announce to the pages -- a place nothing points at yet cannot have changed
/// any figure or label on screen. The expense that is about to name it announces when it
/// saves.
/// </para>
/// </remarks>
public sealed class MerchantCommands(
    IMerchantsClient merchants,
    ApiErrorPresenter errors,
    ISnackbar snackbar) : IMerchantCommands
{
    public async Task<IReadOnlyList<MerchantResponse>> SearchAsync(string? search,
        CancellationToken ct = default)
    {
        try
        {
            return [.. await merchants.GetMerchantsAsync(search, ct)];
        }
        catch (OperationCanceledException)
        {
            // The type-ahead cancels the previous search on every keystroke, so this is the
            // ordinary path and not a failure. Rethrown rather than swallowed, because
            // MudAutocomplete uses it to tell a superseded search from an empty one.
            throw;
        }
        catch
        {
            return [];
        }
    }

    public async Task<MerchantResponse?> GetAsync(Guid merchantId, CancellationToken ct = default)
    {
        try
        {
            return await merchants.GetMerchantAsync(merchantId, ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // A merchant the caller cannot read leaves the field blank -- the picker's
            // ToStringFunc maps null to the empty string -- which is worse than showing a
            // name and better than refusing to open the dialog over a label.
            return null;
        }
    }

    public async Task<MerchantResponse?> CreateAsync(string name, CancellationToken ct = default)
    {
        MerchantResponse? created = null;

        var done = await errors.TryAsync(async () =>
        {
            created = await merchants.CreateMerchantAsync(new CreateMerchantRequest { Name = name }, ct);
            snackbar.Add($"{created.Name} added.", Severity.Success);
        }, "Could not add the place.");

        return done ? created : null;
    }
}

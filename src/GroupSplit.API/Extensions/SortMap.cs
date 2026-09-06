using System.Linq.Expressions;
using GroupSplit.API.Errors;
using GroupSplit.Shared;
using GroupSplit.Shared.Errors;

namespace GroupSplit.API.Extensions;

/// <summary>
/// The orders a listing offers, by name. A caller may sort by any key declared here and by
/// nothing else: the keys are the contract, so a column can be renamed or a projection
/// reshaped without a client discovering it through a 500.
/// </summary>
/// <remarks>
/// A map needs a <see cref="Default"/>, because a page of an unordered query is not a page:
/// two requests could return the same row twice, or never. <see cref="TieBreak"/> is for the
/// same reason one level down -- rows equal on the chosen key have to keep an order between
/// requests, and only a unique column can promise that.
/// </remarks>
public sealed class SortMap<T>
{
    private sealed record Ordering(Func<IQueryable<T>, bool, IOrderedQueryable<T>> Apply, bool DefaultDescending);

    private readonly Dictionary<string, Ordering> _keys = new(StringComparer.OrdinalIgnoreCase);
    private string? _defaultKey;
    private Func<IOrderedQueryable<T>, bool, IOrderedQueryable<T>>? _tieBreak;

    /// <summary>
    /// Offers <paramref name="name"/> as a sort key.
    /// </summary>
    /// <param name="defaultDescending">
    /// The direction to use when the caller names the key but not a direction. True for the
    /// ones people mean the other way round: dates and amounts read largest first.
    /// </param>
    public SortMap<T> Key<TKey>(string name, Expression<Func<T, TKey>> selector, bool defaultDescending = false)
    {
        // Generic in the key type rather than Expression<Func<T, object>>: the latter wraps
        // every value type in a Convert node, which the provider then has to see through.
        _keys[name] = new Ordering(
            (source, descending) => descending ? source.OrderByDescending(selector) : source.OrderBy(selector),
            defaultDescending);

        return this;
    }

    /// <summary>The order applied when the caller names no key. Required.</summary>
    public SortMap<T> Default(string name)
    {
        _defaultKey = name;
        return this;
    }

    /// <summary>
    /// Applied after the chosen key, so rows it cannot separate still come back in the same
    /// order every time. Give it something unique.
    /// </summary>
    public SortMap<T> TieBreak<TKey>(Expression<Func<T, TKey>> selector)
    {
        _tieBreak = (source, descending) => descending
            ? source.ThenByDescending(selector)
            : source.ThenBy(selector);

        return this;
    }

    /// <summary>The keys on offer, for the message a rejected one gets.</summary>
    public IReadOnlyCollection<string> Keys => _keys.Keys;

    internal IOrderedQueryable<T> Apply(IQueryable<T> source, SortRequest? sort)
    {
        if (_defaultKey is null || !_keys.ContainsKey(_defaultKey))
        {
            // A map built without a usable default is a mistake in this codebase, not
            // something a caller did, so it fails as one.
            throw new InvalidOperationException(
                $"The sort map for {typeof(T).Name} has no default key, or names one it does not offer.");
        }

        var requested = sort?.SortBy;
        var name = string.IsNullOrWhiteSpace(requested) ? _defaultKey : requested;

        if (!_keys.TryGetValue(name, out var ordering))
        {
            throw new ValidationException(ErrorCodes.BadRequest,
                $"Cannot sort by '{requested}'. This listing sorts by: {string.Join(", ", _keys.Keys)}.");
        }

        var descending = sort?.SortDescending ?? ordering.DefaultDescending;
        var ordered = ordering.Apply(source, descending);

        return _tieBreak is null ? ordered : _tieBreak(ordered, descending);
    }
}

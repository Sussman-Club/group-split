using Microsoft.AspNetCore.Components;

namespace GroupSplit.App.Shared.Services;

public class DisposingCascadingValueSource<TValue> : CascadingValueSource<TValue>, IDisposable
{
    public event Action? OnDisposing;

    public DisposingCascadingValueSource(TValue value, bool isFixed) : base(value, isFixed)
    {
    }

    public DisposingCascadingValueSource(string name, TValue value, bool isFixed) : base(name, value, isFixed)
    {
    }

    public DisposingCascadingValueSource(Func<TValue> initialValueFactory, bool isFixed) : base(initialValueFactory, isFixed)
    {
    }

    public DisposingCascadingValueSource(string name, Func<TValue> initialValueFactory, bool isFixed) : base(name, initialValueFactory, isFixed)
    {
    }

    public void Dispose()
    {
        OnDisposing?.Invoke();
        GC.SuppressFinalize(this);
    }
}
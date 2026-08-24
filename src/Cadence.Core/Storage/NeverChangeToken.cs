using Microsoft.Extensions.Primitives;

namespace Cadence.Storage;

/// <summary>A change token for sources whose configuration cannot change while the host runs.</summary>
internal sealed class NeverChangeToken : IChangeToken
{
    public static readonly NeverChangeToken Instance = new();

    private NeverChangeToken()
    {
    }

    public bool HasChanged => false;

    public bool ActiveChangeCallbacks => false;

    public IDisposable RegisterChangeCallback(Action<object?> callback, object? state)
        => NullDisposable.Instance;

    private sealed class NullDisposable : IDisposable
    {
        public static readonly NullDisposable Instance = new();

        public void Dispose()
        {
        }
    }
}

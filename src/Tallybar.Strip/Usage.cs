namespace Tallybar;

public enum FetchStatus { Ok, Stale, AuthError, Offline, NotConfigured }

/// <summary>One usage window ("session", "weekly", …) of one provider at one point in time.</summary>
public sealed record UsageSnapshot(
    string ProviderId,
    string WindowLabel,
    double Fraction,              // 0..1 of the window consumed; NaN if unknown
    DateTimeOffset? ResetsAt,
    DateTimeOffset FetchedAt,
    FetchStatus Status);

public interface IProvider
{
    string Id { get; }
    string DisplayName { get; }
    bool IsConfigured { get; }
    Task<IReadOnlyList<UsageSnapshot>> FetchAsync(CancellationToken ct);
}

/// <summary>Fixed-size ring buffer feeding the sparkline. Not thread-safe; UI-thread only.</summary>
public sealed class Ring(int capacity)
{
    private readonly float[] _buf = new float[capacity];
    private int _head;
    private int _count;

    public int Count => _count;

    public void Push(float v)
    {
        _buf[_head] = v;
        _head = (_head + 1) % _buf.Length;
        if (_count < _buf.Length) _count++;
    }

    /// <summary>Copies oldest→newest into <paramref name="dst"/>; returns items written.</summary>
    public int CopyLatest(Span<float> dst)
    {
        int n = Math.Min(_count, dst.Length);
        int start = (_head - n + _buf.Length * 2) % _buf.Length;
        for (int i = 0; i < n; i++)
            dst[i] = _buf[(start + i) % _buf.Length];
        return n;
    }
}

using System.Windows.Forms;

namespace Tallybar;

/// <summary>
/// Polls providers on the UI thread's timer and raises Updated with the freshest snapshots.
/// Failures keep the last snapshot (marked Stale) and back off exponentially to 30 min.
/// </summary>
public sealed class Poller : IDisposable
{
    private static readonly TimeSpan BaseInterval = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromMinutes(30);

    private readonly IReadOnlyList<IProvider> _providers;
    private readonly System.Windows.Forms.Timer _timer;
    private readonly Dictionary<string, List<UsageSnapshot>> _latest = [];
    private readonly Dictionary<string, int> _failures = [];
    private readonly Dictionary<string, DateTimeOffset> _nextDue = [];
    private bool _busy;

    public event Action? Updated;

    public Poller(IReadOnlyList<IProvider> providers)
    {
        _providers = providers;
        _timer = new System.Windows.Forms.Timer { Interval = 5_000 }; // tick fast, fetch when due
        _timer.Tick += async (_, _) => await TickAsync();
    }

    public void Start()
    {
        _timer.Start();
        _ = TickAsync(); // immediate first fetch
    }

    public IReadOnlyList<UsageSnapshot> Latest(string providerId)
        => _latest.TryGetValue(providerId, out var list) ? list : [];

    private async Task TickAsync()
    {
        if (_busy) return;
        _busy = true;
        try
        {
            foreach (IProvider p in _providers)
            {
                DateTimeOffset now = DateTimeOffset.UtcNow;
                if (_nextDue.TryGetValue(p.Id, out var due) && now < due) continue;

                if (!p.IsConfigured)
                {
                    _latest[p.Id] = [new(p.Id, "session", double.NaN, null, now, FetchStatus.NotConfigured)];
                    _nextDue[p.Id] = now + BaseInterval;
                    continue;
                }

                try
                {
                    var snaps = await p.FetchAsync(CancellationToken.None);
                    _latest[p.Id] = [.. snaps];
                    _failures[p.Id] = 0;
                    _nextDue[p.Id] = now + BaseInterval;
                }
                catch (UnauthorizedAccessException)
                {
                    _latest[p.Id] = [new(p.Id, "session", double.NaN, null, now, FetchStatus.AuthError)];
                    _nextDue[p.Id] = now + MaxBackoff; // creds won't fix themselves; re-check occasionally
                }
                catch
                {
                    // Network/parse error: keep last values, mark them stale, back off.
                    int fails = _failures.GetValueOrDefault(p.Id) + 1;
                    _failures[p.Id] = fails;
                    if (_latest.TryGetValue(p.Id, out var last))
                        _latest[p.Id] = [.. last.Select(s => s with { Status = FetchStatus.Stale })];
                    else
                        _latest[p.Id] = [new(p.Id, "session", double.NaN, null, now, FetchStatus.Offline)];

                    double factor = Math.Min(Math.Pow(2, fails), MaxBackoff / BaseInterval);
                    _nextDue[p.Id] = now + BaseInterval * factor;
                }
            }
        }
        finally
        {
            _busy = false;
        }
        Updated?.Invoke();
    }

    public void Dispose()
    {
        _timer.Stop();
        _timer.Dispose();
    }
}

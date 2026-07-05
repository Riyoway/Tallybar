using System.Windows.Forms;

namespace Tallybar;

/// <summary>
/// Polls enabled providers on the UI thread's timer and raises Updated with fresh
/// snapshots. Keeps a per-provider history ring for the sparkline. Failures keep the
/// last snapshot (marked Stale) and back off exponentially to 30 min.
/// </summary>
public sealed class Poller : IDisposable
{
    private readonly Settings _settings;
    private readonly System.Windows.Forms.Timer _timer;
    private readonly Dictionary<string, List<UsageSnapshot>> _latest = [];
    private readonly Dictionary<string, Ring> _history = [];
    private readonly Dictionary<string, int> _failures = [];
    private readonly Dictionary<string, DateTimeOffset> _nextDue = [];
    private bool _busy;

    public IReadOnlyList<IProvider> Providers { get; }

    public event Action? Updated;

    public Poller(IReadOnlyList<IProvider> providers, Settings settings)
    {
        Providers = providers;
        _settings = settings;
        _timer = new System.Windows.Forms.Timer { Interval = 5_000 }; // tick fast, fetch when due
        _timer.Tick += async (_, _) => await TickAsync();
    }

    public void Start()
    {
        _timer.Start();
        _ = TickAsync(); // immediate first fetch
    }

    /// <summary>Forget schedules and re-fetch everything on the next tick.</summary>
    public void RefreshNow()
    {
        _nextDue.Clear();
        _failures.Clear();
        _ = TickAsync();
    }

    public IReadOnlyList<UsageSnapshot> Latest(string providerId)
        => _latest.TryGetValue(providerId, out var list) ? list : [];

    public Ring History(string providerId)
    {
        if (!_history.TryGetValue(providerId, out Ring? ring))
            _history[providerId] = ring = new Ring(120);
        return ring;
    }

    private async Task TickAsync()
    {
        if (_busy) return;
        _busy = true;
        TimeSpan baseInterval = TimeSpan.FromSeconds(_settings.PollSeconds);
        try
        {
            foreach (IProvider p in Providers)
            {
                if (!_settings.IsProviderEnabled(p.Id)) continue;

                DateTimeOffset now = DateTimeOffset.UtcNow;
                if (_nextDue.TryGetValue(p.Id, out var due) && now < due) continue;

                if (!p.IsConfigured)
                {
                    _latest[p.Id] = [new(p.Id, "session", double.NaN, null, now, FetchStatus.NotConfigured)];
                    _nextDue[p.Id] = now + baseInterval;
                    continue;
                }

                try
                {
                    var snaps = await p.FetchAsync(CancellationToken.None);
                    _latest[p.Id] = [.. snaps];
                    _failures[p.Id] = 0;
                    _nextDue[p.Id] = now + baseInterval;
                    if (snaps.FirstOrDefault() is { } primary && !double.IsNaN(primary.Fraction))
                        History(p.Id).Push((float)primary.Fraction);
                }
                catch (UnauthorizedAccessException)
                {
                    // A transient auth blip (e.g. the CLI mid-refresh of its token file) must
                    // self-heal, so re-check every couple of minutes rather than stalling for
                    // 30 min — otherwise the app looks stuck until it's restarted.
                    _latest[p.Id] = [new(p.Id, "session", double.NaN, null, now, FetchStatus.AuthError)];
                    _nextDue[p.Id] = now + TimeSpan.FromMinutes(2);
                }
                catch (Exception e)
                {
                    // Network/parse error: keep last values, mark them stale, back off.
                    int fails = _failures.GetValueOrDefault(p.Id) + 1;
                    _failures[p.Id] = fails;
                    if (_latest.TryGetValue(p.Id, out var last))
                        _latest[p.Id] = [.. last.Select(s => s with { Status = FetchStatus.Stale })];
                    else
                        _latest[p.Id] = [new(p.Id, "session", double.NaN, null, now, FetchStatus.Offline)];

                    // Rate-limited: a fixed cool-off beats guessing.
                    if (e is System.Net.Http.HttpRequestException
                        { StatusCode: System.Net.HttpStatusCode.TooManyRequests })
                    {
                        _nextDue[p.Id] = now + TimeSpan.FromMinutes(5);
                    }
                    else
                    {
                        // Gentle linear backoff capped at 5 min, so a transient failure
                        // recovers in about a minute instead of stalling on "stale" for
                        // up to half an hour.
                        _nextDue[p.Id] = now + baseInterval * Math.Min(fails, 5);
                    }
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

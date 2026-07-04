using System.Windows.Forms;

namespace Tallybar;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        if (args.Contains("--probe"))
        {
            Probe();
            return;
        }

        using var single = new Mutex(initiallyOwned: true, "Tallybar.SingleInstance", out bool isNew);
        if (!isNew) return; // already running

        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        Settings settings = Settings.Load();
        using var poller = new Poller(Providers.All, settings);
        using var strip = new StripWindow(settings, poller);

        // Secondary-monitor strips, (re)built only when the wanted count actually changes,
        // so unrelated setting tweaks don't churn (or dispose subscribers mid-event).
        var secondaries = new List<StripWindow>();
        void RebuildSecondaries()
        {
            if (strip.IsDisposed) return;
            int want = settings.SecondaryTaskbars ? Math.Max(0, TaskbarLocator.Count() - 1) : 0;
            if (want == secondaries.Count) return;

            foreach (StripWindow s in secondaries) s.Dispose();
            secondaries.Clear();
            for (int i = 1; i <= want; i++)
            {
                var s = new StripWindow(settings, poller, taskbarIndex: i, isPrimary: false);
                secondaries.Add(s);
                s.Show();
            }
        }
        strip.DisplayChanged += RebuildSecondaries;
        settings.Changed += RebuildSecondaries;
        strip.HandleCreated += (_, _) =>
        {
            poller.Start();
            RebuildSecondaries();
        };

        Application.Run(strip);

        foreach (StripWindow s in secondaries) s.Dispose();
    }

    /// <summary>`Tallybar --probe`: fetch once and print what the strip would show. The
    /// smallest runnable check of the provider path (run from a console, no UI).</summary>
    private static void Probe()
    {
        foreach (IProvider provider in Providers.All)
        {
            Console.WriteLine($"provider={provider.Id} configured={provider.IsConfigured}");
            if (!provider.IsConfigured) continue;
            try
            {
                foreach (UsageSnapshot s in provider.FetchAsync(CancellationToken.None).GetAwaiter().GetResult())
                    Console.WriteLine(
                        $"  {s.WindowLabel,-14} {s.Fraction * 100,5:0.#}%  resets={s.ResetsAt:u}  status={s.Status}");
            }
            catch (Exception e)
            {
                Console.WriteLine($"  ERROR {e.GetType().Name}: {e.Message}");
                Environment.ExitCode = 1;
            }
        }
    }
}

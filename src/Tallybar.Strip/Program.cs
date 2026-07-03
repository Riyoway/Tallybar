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
        using var poller = new Poller([new ClaudeProvider(), new CodexProvider()], settings);
        using var strip = new StripWindow(settings, poller);

        // Secondary-monitor strips, rebuilt when displays or the setting change.
        var secondaries = new List<StripWindow>();
        void RebuildSecondaries()
        {
            foreach (StripWindow s in secondaries) s.Dispose();
            secondaries.Clear();
            if (!settings.SecondaryTaskbars) return;
            for (int i = 1; i < TaskbarLocator.Count(); i++)
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
        foreach (IProvider provider in (IProvider[])[new ClaudeProvider(), new CodexProvider()])
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

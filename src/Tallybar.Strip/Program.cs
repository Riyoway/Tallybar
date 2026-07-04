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

        if (args.Contains("--shot"))
        {
            string path = args[Array.IndexOf(args, "--shot") + 1];
            try
            {
                Application.EnableVisualStyles();
                var settings2 = Settings.Load();
                var providers = new IProvider[] { new ClaudeProvider(), new CodexProvider() };
                using var poller2 = new Poller(providers, settings2);
                foreach (IProvider prov in providers)
                {
                    if (!prov.IsConfigured) continue;
                    try { poller2.Seed(prov.Id, prov.FetchAsync(CancellationToken.None).GetAwaiter().GetResult()); }
                    catch { }
                }
                using var win = PopoverWindow.OpenForShot(settings2, poller2, new System.Drawing.Rectangle(1600, 1040, 150, 40));
                using var bmp = new System.Drawing.Bitmap(win.Width, win.Height);
                win.DrawToBitmap(bmp, new System.Drawing.Rectangle(0, 0, win.Width, win.Height));
                bmp.Save(path);
            }
            catch (Exception ex) { System.IO.File.WriteAllText(path + ".err.txt", ex.ToString()); }
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

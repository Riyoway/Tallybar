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

        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        var claude = new ClaudeProvider();
        using var poller = new Poller([claude]);
        using var strip = new StripWindow();

        poller.Updated += () => strip.ShowUsage(claude.DisplayName, poller.Latest(claude.Id));
        strip.HandleCreated += (_, _) => poller.Start();

        Application.Run(strip);
    }

    /// <summary>`Tallybar --probe`: fetch once and print what the strip would show. The
    /// smallest runnable check of the provider path (run from a console, no UI).</summary>
    private static void Probe()
    {
        var claude = new ClaudeProvider();
        Console.WriteLine($"provider={claude.Id} configured={claude.IsConfigured}");
        if (!claude.IsConfigured) return;
        try
        {
            foreach (UsageSnapshot s in claude.FetchAsync(CancellationToken.None).GetAwaiter().GetResult())
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

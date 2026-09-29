using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace DebuffRoulette;

/// <summary>Console flags for testing without the game: --roll, --apply, --cleanup, --autostart, --status, --panel.</summary>
internal static partial class Program
{
    // ---------------------- cli ----------------------

    private static int Cli(string[] args)
    {
        try
        {
            switch (args[0])
            {
                case "--roll":
                {
                    int n = args.Length > 1 ? int.Parse(args[1]) : 10;
                    var counts = new Dictionary<string, int>();
                    int empty = 0;
                    for (int i = 0; i < n; i++)
                    {
                        var c = _roulette.Roll(Rng);
                        if (c.Picks.Count == 0) empty++;
                        foreach (var p in c.Picks) counts[p.Id] = counts.GetValueOrDefault(p.Id) + 1;
                        if (n <= 30) Console.WriteLine($"{i + 1,3}: {c}");
                    }
                    Console.WriteLine($"\nclean matches: {empty}/{n} ({100.0 * empty / n:F1}%)");
                    foreach (var (id, cnt) in counts.OrderByDescending(kv => kv.Value))
                        Console.WriteLine($"  {id,-16} {cnt,6}  ({100.0 * cnt / n:F1}%)");
                    return 0;
                }
                case "--apply":
                {
                    var id = args[1];
                    int sec = args.Length > 2 ? int.Parse(args[2]) : 5;
                    if (!_effects.TryGetValue(id, out var eff))
                    {
                        Console.WriteLine($"no system effect '{id}' (cfg debuffs are tested in the game)");
                        return 1;
                    }
                    if (!eff.IsAvailable)
                    {
                        Console.WriteLine($"'{id}' is unavailable (see logs/app.log)");
                        return 1;
                    }
                    Console.WriteLine($"applying {id} for {sec} s...");
                    eff.Apply();
                    Thread.Sleep(sec * 1000);
                    eff.Revert();
                    Thread.Sleep(_cfg.General.FadeOutMs + 400); // let the fade-out finish before Dispose
                    Console.WriteLine("removed");
                    return 0;
                }
                case "--cleanup":
                    AudioModule.CleanupLeftovers(_log);
                    _cfgWriter.WriteResetOnly();
                    _cfgWriter.WriteKeyReset();
                    Console.WriteLine("cleaned up: mute lifted, APO cleared, debuff.cfg and debuff_key.cfg reset");
                    return 0;

                case "--autostart":
                    if (args.Length > 1 && args[1] == "on")
                    {
                        Autostart.Enable(ExePath);
                        Console.WriteLine($"start with Windows enabled -> {ExePath}");
                    }
                    else if (args.Length > 1 && args[1] == "off")
                    {
                        Autostart.Disable();
                        Console.WriteLine("start with Windows disabled");
                    }
                    else
                    {
                        Console.WriteLine($"start with Windows: {(Autostart.IsEnabled() ? "on" : "off")} (usage: --autostart on|off)");
                    }
                    return 0;

                case "--status":
                    Console.WriteLine(StatusText());
                    return 0;

                case "--panel":
                {
                    ApplicationConfiguration.Initialize();
                    var sample = new List<string> { "Grayscale", "No killfeed", "Bass boost" };
                    var panel = new StatusPanel(_log, () => Console.WriteLine("click: Settings"));
                    Console.WriteLine("panel preview, about 12 s (the cursor is free on the desktop): hover the buttons and click...");
                    panel.ShowPanel(sample);
                    Thread.Sleep(12000);
                    panel.Dispose();
                    return 0;
                }

                default:
                    Console.WriteLine("flags: --selftest | --roll [N] | --apply <id> [sec] | --cleanup | --autostart on|off | --status | --panel");
                    return 1;
            }
        }
        finally
        {
            foreach (var e in _effects.Values)
            {
                try { e.Dispose(); } catch { }
            }
        }
    }
}

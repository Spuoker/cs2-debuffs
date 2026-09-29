using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace DebuffRoulette;

/// <summary>The watchdog buddy and self-healing: run markers, coming back with the same combo, crash-loop protection.</summary>
internal static partial class Program
{
    // ---------------------- watchdog / self-healing ----------------------

    /// <summary>
    /// First run (a fresh install): enable start with Windows so the app lives alongside the game.
    /// Once only, guarded by a marker: after that the user is free to turn it off in the tray and we never push again.
    /// </summary>
    private static void EnsureAutostartOnFirstRun()
    {
        try
        {
            Directory.CreateDirectory(_stateDir);
            var marker = Path.Combine(_stateDir, "installed.flag");
            if (File.Exists(marker)) return;
            if (!Autostart.IsEnabled()) Autostart.Enable(ExePath);
            File.WriteAllText(marker, DateTime.UtcNow.ToString("o"));
            _log.Info("first run: start with Windows enabled");
        }
        catch (Exception ex) { _log.Error("could not enable start with Windows on the first run", ex); }
    }

    /// <summary>Marks that we are running, and returns true if this is a comeback after a recent kill.</summary>
    private static bool DetectReviveAndMarkRunning()
    {
        bool revived = false;
        try
        {
            // A stale stop.flag from the last clean exit must not get in the way of a future comeback.
            if (File.Exists(_stopFlag)) File.Delete(_stopFlag);

            if (File.Exists(_runningFlag)
                && long.TryParse(File.ReadAllText(_runningFlag).Trim(), out var ticks))
            {
                var age = DateTime.UtcNow - new DateTime(ticks, DateTimeKind.Utc);
                revived = age < TimeSpan.FromMinutes(10); // an old marker is garbage, not a comeback
            }
        }
        catch { }
        TouchRunningFlag();
        return revived;
    }

    private static void TouchRunningFlag()
    {
        try
        {
            Directory.CreateDirectory(_stateDir);
            File.WriteAllText(_runningFlag, DateTime.UtcNow.Ticks.ToString());
        }
        catch { }
    }

    private static void PersistArmed()
    {
        try
        {
            Directory.CreateDirectory(_stateDir);
            File.WriteAllLines(_armedFile, _armed.Picks.Select(p => p.Id));
            File.WriteAllText(_armedMapFile, _brain.Map);
            PersistApplied();
        }
        catch { }
    }

    /// <summary>Remember whether the debuffs are applied: otherwise killing the process would be a way to remove them.</summary>
    private static void PersistApplied()
    {
        try
        {
            Directory.CreateDirectory(_stateDir);
            File.WriteAllText(_appliedFile, _brain.Applied ? "1" : "0");
        }
        catch { }
    }

    private static bool TryLoadApplied()
    {
        try { return File.Exists(_appliedFile) && File.ReadAllText(_appliedFile).Trim() == "1"; }
        catch { return false; }
    }

    private static string TryLoadArmedMap()
    {
        try { return File.Exists(_armedMapFile) ? File.ReadAllText(_armedMapFile).Trim() : ""; }
        catch { return ""; }
    }

    private static bool TryLoadArmed(out Combo combo)
    {
        combo = Combo.Empty;
        try
        {
            if (!File.Exists(_armedFile)) return false;
            var byId = _cfg.Slots.SelectMany(s => s.Options).ToDictionary(o => o.Id);
            var picks = new List<OptionConfig>();
            foreach (var id in File.ReadAllLines(_armedFile))
                if (!string.IsNullOrWhiteSpace(id) && byId.TryGetValue(id.Trim(), out var o))
                    picks.Add(o);
            combo = new Combo(picks);
            return true;
        }
        catch { return false; }
    }

    private static Process? StartWatchdog()
    {
        if (!WantWatchdog) return null;   // the watchdog is part of strict mode, and of nothing else
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = ExePath,
                Arguments = $"--watchdog {Environment.ProcessId}",
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
            };
            var p = Process.Start(psi);
            _log.Info($"watchdog started (pid {p?.Id})");
            return p;
        }
        catch (Exception ex)
        {
            _log.Error("the watchdog did not start", ex);
            return null;
        }
    }

    /// <summary>The watchdog is needed while strict mode is in force: enabled and not waived by the key for this match.</summary>
    private static bool WantWatchdog => !_userExit && _cfg.General.StrictMode && !_brain.Waived;

    /// <summary>Strict mode was toggled (settings/tray/key during grace, or a new match): start or stop the watchdog.</summary>
    private static void SyncWatchdog()
    {
        bool alive = _watchdog is { HasExited: false };
        if (WantWatchdog && !alive) _watchdog = StartWatchdog();
        else if (!WantWatchdog && alive && !_userExit)
        {
            StopWatchdog();
            _watchdog = null;
            _log.Info("strict mode is not in force - the watchdog was stopped");
        }
    }

    private static void StopWatchdog()
    {
        try
        {
            if (_watchdog is { HasExited: false })
                _watchdog.Kill();
        }
        catch { }
    }

    /// <summary>
    /// The buddy: it waits for the main process to die. If that was a clean exit (stop.flag is present) it
    /// quietly leaves. Otherwise (killed via Task Manager) it clears the leftovers and starts the main
    /// process again. Safety valve: more than 5 restarts in 2 minutes means a crash loop, so we give up.
    /// </summary>
    private static int RunWatchdog(string pidStr)
    {
        var baseDir = AppContext.BaseDirectory;
        var dataDir = Path.Combine(baseDir, "data");
        var stateDir = Path.Combine(dataDir, "state");
        var stopFlag = Path.Combine(stateDir, "stop.flag");
        var log = new FileLog(Path.Combine(dataDir, "logs", "watchdog.log"));

        if (!int.TryParse(pidStr, out var pid)) return 1;

        try
        {
            using var main = Process.GetProcessById(pid);
            main.WaitForExit();
        }
        catch
        {
            // The process was already gone before we got a handle - treat that as an abnormal death.
        }

        if (File.Exists(stopFlag))
        {
            try { File.Delete(stopFlag); } catch { }
            log.Info("the main process exited cleanly - the watchdog is leaving");
            return 0;
        }

        if (TooManyRestarts(stateDir))
        {
            log.Warn("crash loop (>5 restarts in 2 min) - the watchdog gives up and clears the leftovers");
            AudioModule.CleanupLeftovers(log);
            ResetCs2Cfg(log);   // otherwise scoreboard/radar/crosshair stay gone: only the cfg reset brings them back
            return 0;
        }

        log.Warn("the main process was killed - clearing the leftovers and starting it again");
        AudioModule.CleanupLeftovers(log);
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = Environment.ProcessPath ?? Path.Combine(baseDir, "cs2-debuffs.exe"),
                Arguments = "--tray",   // quietly to the tray: nobody needs the settings window mid-match
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
            });
        }
        catch (Exception ex)
        {
            log.Error("could not start the main process", ex);
            ResetCs2Cfg(log);   // nobody left to start it - at least give the player their HUD back
        }
        return 0;
    }

    /// <summary>
    /// Put debuff.cfg and debuff_key.cfg back to the reset state. Needed when the main process died and
    /// nothing is left to start it: otherwise the player's bind keeps applying the cfg debuffs with nothing
    /// around to take them off.
    /// </summary>
    private static void ResetCs2Cfg(ILog log)
    {
        try
        {
            var w = new CfgWriter(SteamLocator.FindCs2CfgDir(log), log);
            w.WriteResetOnly();
            w.WriteKeyReset();
            log.Info("cfg debuffs reset (scoreboard/radar/killfeed/crosshair are back)");
        }
        catch (Exception ex) { log.Error("could not reset the CS2 cfg", ex); }
    }

    private static bool TooManyRestarts(string stateDir)
    {
        var f = Path.Combine(stateDir, "restarts.txt");
        var now = DateTime.UtcNow;
        var times = new List<DateTime>();
        try
        {
            if (File.Exists(f))
                foreach (var l in File.ReadAllLines(f))
                    if (long.TryParse(l, out var t)) times.Add(new DateTime(t, DateTimeKind.Utc));
        }
        catch { }
        times.Add(now);
        times = times.Where(t => now - t < TimeSpan.FromSeconds(120)).ToList();
        try
        {
            Directory.CreateDirectory(stateDir);
            File.WriteAllLines(f, times.Select(t => t.Ticks.ToString()));
        }
        catch { }
        return times.Count > 5;
    }

}

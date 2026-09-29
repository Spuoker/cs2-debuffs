using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace DebuffRoulette;

internal static partial class Program
{
    [DllImport("kernel32.dll")]
    private static extern bool AttachConsole(int pid);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    private const int VkTab = 0x09;

    /// <summary>Run an action on the main UI thread (clicks from the panel arrive on its own thread).</summary>
    private static void RunOnUi(Action a)
    {
        try
        {
            if (_ui is { IsHandleCreated: true } && _ui.InvokeRequired) _ui.BeginInvoke(a);
            else a();
        }
        catch (Exception ex) { _log.Error("RunOnUi", ex); }
    }

    private static readonly object Lock = new();
    private static readonly Random Rng = new();

    // The audio debuffs are tied to the cs2 process alone - window focus does not switch them off.
    private static readonly HashSet<string> AudioIds =
        new(StringComparer.Ordinal) { "no_sound", "mono", "bass_boost", "muffle", "white_noise" };

    // What is actually on right now (id -> the effect is applied).
    private static readonly HashSet<string> ActiveIds = new(StringComparer.Ordinal);

    private static AppConfig _cfg = null!;
    private static ILog _log = null!;
    private static Dictionary<string, IEffect> _effects = new();
    private static RouletteEngine _roulette = null!;
    private static CfgWriter _cfgWriter = null!;
    private static MatchJournal _journal = null!;
    private static GsiServer? _gsi;
    private static ComboBanner? _banner;
    private static StatusPanel? _statusPanel;
    private static SettingsForm? _settings;
    private static NotifyIcon? _tray;              // used for toggle notifications
    private static ToolStripMenuItem? _enabledItem; // the "Mod enabled" checkmark in the tray
    private static ToolStripMenuItem? _strictItem;  // the "Strict mode" checkmark in the tray
    private static bool _uninstalled;             // the mod is being uninstalled: on exit we clean up instead of rewriting the cfg
    private static bool _startMinimized;          // started by autostart (--tray): do not open the window
    private static Control _ui = null!;   // an invisible marshal onto the main UI thread (panel clicks live on their own thread)
    private static string _configPath = "";
    private static string _dataDir = "";        // the one folder next to the exe: config, logs, state, journal
    private static bool _strictWasOn;           // was strict mode on before the settings were edited (the grace period is only granted when turning it on)
    private static Combo _armed = Combo.Empty;
    private static MatchBrain _brain = null!;   // the match state machine - the single source of truth for what should be on
    private static bool _wantEffects;           // the state machine's decision for audio/color (SyncEffects adds the window-focus check)
    private static string _cfgState = "";
    private static string _keyCfgState = "";   // what is in debuff_key.cfg - what the next press will do       // what is in debuff.cfg: "reset" or "combo#N" - we write only when it changes
    private static int _comboGen;               // the current combo's number (part of _cfgState)
    private static bool _shutdown;
    private static bool _cs2Seen; // CS2 was seen running at least once (for exit_when_cs2_closes)

    // watchdog / self-healing
    private static Process? _watchdog;
    private static bool _userExit;
    private static string _stateDir = "";
    private static string _runningFlag = "";
    private static string _stopFlag = "";
    private static string _armedFile = "";
    private static string _armedMapFile = "";
    private static string _appliedFile = "";

    private static string ExePath => Environment.ProcessPath ?? Application.ExecutablePath;

    // Launching the exe again makes the live copy open its window (a cross-process Windows event).
    private const string ShowWindowEventName = "cs2-debuffs-show-window";
    private static EventWaitHandle? _showWindowEvent;   // keep the reference so the GC does not collect it

    private static void SignalShowWindow()
    {
        try
        {
            if (EventWaitHandle.TryOpenExisting(ShowWindowEventName, out var ev))
                using (ev) ev.Set();
        }
        catch { }
    }

    private static void StartShowWindowListener()
    {
        try
        {
            _showWindowEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowWindowEventName);
            var ev = _showWindowEvent;
            new Thread(() =>
            {
                while (!_shutdown)
                    if (ev.WaitOne(1000)) RunOnUi(OpenSettings);
            })
            { IsBackground = true, Name = "show-window-listener" }.Start();
        }
        catch (Exception ex) { _log.Error("could not start the relaunch listener", ex); }
    }

    [STAThread]
    private static int Main(string[] args)
    {
        // Watchdog buddy mode: minimal dependencies, no tray, no GSI, no effects.
        if (args.Length >= 2 && args[0] == "--watchdog")
            return RunWatchdog(args[1]);

        // The match state machine's self-test: pure logic, touches nothing (no effects, no CS2 folder, no mutex).
        if (args.Length == 1 && args[0] == "--selftest")
        {
            AttachConsole(-1);
            return MatchBrainSelfTest.Run(Console.Out) ? 0 : 1;
        }

        // Autostart with Windows passes --tray: quietly to the tray, no window. A manual launch
        // (double-clicking the exe, no arguments) opens the window so the app behaves like a normal one.
        bool silentTray = args.Length == 1 && args[0] == "--tray";
        _startMinimized = silentTray;
        bool cli = args.Length > 0 && !silentTray;
        using var mutex = new Mutex(true, "cs2-debuffs-single-instance", out bool owned);
        if (!owned && cli)
        {
            // The console commands create their own effects and tear them down on exit - and those are
            // system-wide (the APO file, the color matrix). Run them while a copy is live and that copy
            // loses everything until the end of the match, with strict mode none the wiser. So we refuse.
            AttachConsole(-1);
            Console.WriteLine("cs2-debuffs is already running - the command was cancelled.");
            Console.WriteLine("Stop it (tray -> Exit) and try again, or use the tray menu.");
            return 2;
        }
        if (!owned && !cli)
        {
            // A copy is already live. On a manual launch we ask it to show its window RIGHT AWAY (like any
            // normal app); on autostart (--tray) we stay quiet so the two launch paths do not fight.
            if (!silentTray) SignalShowWindow();

            // The previous instance may have been dying right then (or was killed, leaving the mutex
            // abandoned): in that case we pick up the baton ourselves. If it is alive we quietly leave
            // after 5 s (a background process, nothing visible).
            try { owned = mutex.WaitOne(5000); }
            catch (AbandonedMutexException) { owned = true; }
            if (!owned) return 0;
        }

        // Exactly one data folder next to the exe: config, logs, state, journal. Everything the app owns
        // goes only there, so the "release" is the exe plus data and nothing else gets scattered around.
        var baseDir = AppContext.BaseDirectory;
        _dataDir = Path.Combine(baseDir, "data");
        MigrateLegacyLayout(baseDir, _dataDir);
        try { Directory.CreateDirectory(_dataDir); } catch { }

        _stateDir = Path.Combine(_dataDir, "state");
        _runningFlag = Path.Combine(_stateDir, "running.flag");
        _stopFlag = Path.Combine(_stateDir, "stop.flag");
        _armedFile = Path.Combine(_stateDir, "armed.txt");
        _armedMapFile = Path.Combine(_stateDir, "armed_map.txt");
        _appliedFile = Path.Combine(_stateDir, "applied.txt");

        try
        {
            _log = new FileLog(Path.Combine(_dataDir, "logs", "app.log"));
        }
        catch (Exception ex)
        {
            // Usually this is a folder with no write access (Program Files). Without this message the app
            // would just die silently: it is a GUI subsystem binary, there is no console, and Windows shows
            // no error window.
            MessageBox.Show(
                "cs2-debuffs cannot write next to itself:\n" + ex.Message + "\n\n" +
                "Put the app somewhere you have write access: the desktop, Documents,\n" +
                "a folder of its own on a drive. It will not work in Program Files.",
                "cs2-debuffs");
            return 1;
        }
        if (cli) AttachConsole(-1);

        _configPath = Path.Combine(_dataDir, "debuffs.json");

        // The move into data may have failed (no rights, file in use). In that case we read the config
        // WHERE IT IS instead of writing a default over it - otherwise an update would silently wipe every
        // setting.
        var legacyConfig = Path.Combine(baseDir, "debuffs.json");
        if (!File.Exists(_configPath) && File.Exists(legacyConfig))
        {
            _configPath = legacyConfig;
            _log.Warn($"the config did not move into data - using the old one: {legacyConfig}");
        }
        try
        {
            if (ConfigLoader.EnsureExists(_configPath))
                _log.Info("first run: created data\\debuffs.json from the default settings");
            _cfg = ConfigLoader.Load(_configPath);
        }
        catch (Exception ex)
        {
            _log.Error("debuffs.json is broken", ex);
            if (!cli) MessageBox.Show($"debuffs.json is broken:\n{ex.Message}", "cs2-debuffs");
            return 1;
        }

        _journal = new MatchJournal(Path.Combine(_dataDir, "journal.jsonl"));
        _cfgWriter = new CfgWriter(SteamLocator.FindCs2CfgDir(_log), _log);

        var effects = new List<IEffect>();
        Safe(() => effects.AddRange(AudioModule.Create(_cfg, _log)), "the audio module");
        Safe(() => effects.AddRange(DisplayModule.Create(_cfg, _log)), "the display module");
        _effects = effects.ToDictionary(e => e.Id);
        _roulette = new RouletteEngine(_cfg, IsAvailable);
        _brain = new MatchBrain(() => _cfg.General.StrictMode);
        _strictWasOn = _cfg.General.StrictMode;

        if (cli) return Cli(args);

        ApplicationConfiguration.Initialize();
        AppDomain.CurrentDomain.ProcessExit += (_, _) => Shutdown();

        // Self-heal at startup: clear anything a killed previous instance may have left behind.
        AudioModule.CleanupLeftovers(_log);
        EnsureAutostartOnFirstRun();

        _ui = new Control();
        _ = _ui.Handle;   // force the handle on the main thread so BeginInvoke marshals clicks here
        StartShowWindowListener();
        _banner = new ComboBanner(_log);
        _statusPanel = new StatusPanel(_log, () => RunOnUi(OpenSettings));
        _cfgWriter.EnsureGsiFile(_cfg.General.GsiPort);
        _gsi = new GsiServer(_cfg.General.GsiPort, _log);
        _gsi.PhaseChanged += OnGsiPhase;
        _gsi.Menu += OnGsiMenu;
        _gsi.RoundStart += OnRoundStart;
        try
        {
            _gsi.Start();
        }
        catch (Exception ex)
        {
            _log.Error($"Could not start the GSI server on port {_cfg.General.GsiPort}", ex);
            MessageBox.Show($"Is port {_cfg.General.GsiPort} taken? Change gsi_port in debuffs.json.", "cs2-debuffs");
            return 1;
        }

        // Coming back after a kill: the same combo and the same map, not a new roll. Otherwise: a fresh roulette.
        bool revived = DetectReviveAndMarkRunning();
        if (revived && TryLoadArmed(out var restored))
        {
            lock (Lock) _brain.Restore(TryLoadArmedMap(), restored.Picks.Count == 0, TryLoadApplied()); // the same match will not re-roll the combo
            Arm(restored, "came back: the previous combo was restored", reroll: false);
        }
        else
        {
            RollAndArm();
        }
        Reconcile();   // outside a match: debuff.cfg holds the reset and no effects; it starts the watchdog itself if strict
        RunTray();
        Shutdown();
        return 0;
    }

    // ---------------------- lifecycle ----------------------

    private static bool IsAvailable(string id)
    {
        if (CfgWriter.IsCfgId(id)) return _cfgWriter.Available;
        if (!_effects.TryGetValue(id, out var e)) return false;
        return e.IsAvailable;
    }

    /// <param name="reroll">
    /// Re-roll the effects' random parameters (the colorblind type and such). Not when coming back:
    /// otherwise killing the process would change the effect, and the combo is promised to be one per match.
    /// </param>
    private static void Arm(Combo combo, string logMsg, bool reroll = true)
    {
        lock (Lock)
        {
            _armed = combo;
            // re-roll the effects' random parameters (the colorblind type and such) once per combo,
            // so they do not change every time the window regains focus.
            if (reroll)
                foreach (var e in _effects.Values)
                    if (e is IRerollable r) { try { r.Reroll(); } catch { } }
            _comboGen++;                                   // a new combo: Reconcile will rewrite debuff.cfg
            _brain.ComboEmpty = _armed.Picks.Count == 0;
            _journal.ComboArmed(_armed);
            PersistArmed();
            _log.Info($"{logMsg} ({_armed.Picks.Count} total)");
        }
    }

    private static void RollAndArm()
    {
        Combo rolled;
        lock (Lock) rolled = _roulette.Roll(Rng);
        Arm(rolled, "Combo ready");
    }

    // ---------------------- match: facts -> state machine -> Reconcile ----------------------
    // The handlers only pass the fact to the state machine and call Reconcile. They apply NOTHING
    // themselves and never write debuff.cfg - one place does that, Reconcile, per the machine's decision.

    private static void OnGsiPhase(string phase, string map, MatchEnd? end)
    {
        Transition t;
        lock (Lock) t = _brain.OnGsi(phase, map);

        switch (t)
        {
            case Transition.Ignored:
                return;
            case Transition.Unknown:
                _log.Warn($"unfamiliar GSI phase '{phase}' - leaving the state alone");
                return;
            case Transition.NewMatch:
            {
                Combo rolled;
                lock (Lock) rolled = _roulette.Roll(Rng);   // the settings window edits the very same config
                Arm(rolled, $"new match ({map})");
                break;
            }
            case Transition.MatchOver:
                if (end is not null) _journal.MatchEnded(end, _armed);
                break;
        }

        bool on = _brain.WantEffects(_cfg.General.Enabled);
        _log.Info($"match: {Describe(t)} - debuffs {(on ? "applied" : _brain.InMatch ? $"waiting for the key ({KeyLabel(_cfg.General.ToggleHotkey)})" : "removed")}");
        Reconcile();
        if (t is Transition.NewMatch or Transition.Return) ShowBanner(KeyNote());
        if (t == Transition.MatchOver) Notify($"Match over, debuffs removed. Tap {KeyLabel(_cfg.General.ToggleHotkey)} so the game gives back the scoreboard, radar and crosshair.");
    }

    /// <summary>The hint next to the combo banner: what to press to turn it on.</summary>
    private static string? KeyNote()
    {
        if (_armed.Picks.Count == 0 || _brain.Applied) return null;
        return $"Press {KeyLabel(_cfg.General.ToggleHotkey)} to apply the set"
               + (_cfg.General.StrictMode ? $" (strict mode: no way back after {(int)MatchBrain.StrictGrace.TotalSeconds} s)" : "");
    }

    /// <summary>
    /// Strict mode was ticked on while the debuffs were already applied: the grace period starts from that
    /// moment, otherwise the lock would snap shut instantly (and the warning promises 30 s to change your mind).
    /// </summary>
    private static void StartGraceIfApplied()
    {
        bool started;
        lock (Lock) started = _brain.StartGraceNow();
        if (started) StartGraceCountdown();
    }

    /// <summary>The strict grace period is over - say the lock has closed (if this is still the same match).</summary>
    private static void StartGraceCountdown()
    {
        int gen = _comboGen;
        _ = Task.Delay(MatchBrain.StrictGrace + TimeSpan.FromMilliseconds(300)).ContinueWith(_ =>
        {
            bool locked;
            lock (Lock) locked = gen == _comboGen && _brain.Locked;
            if (!locked) return;
            _log.Info("strict mode: the grace period is over - locked until the match ends");
            Notify("Strict mode is on until the match ends. You turned it on, now live with it.");
            RunOnUi(UpdateStrictMenu);
        });
    }

    private static void OnGsiMenu()
    {
        Transition t;
        lock (Lock) t = _brain.OnMenu();
        if (t != Transition.Abandon) return;
        _journal.Note("left the match - effects removed, cfg reset, combo kept (waiting for a reconnect)");
        _log.Info("match: left to the menu - removing everything, keeping the combo in case of a reconnect");
        Reconcile();
    }

    private static void OnRoundStart()
    {
        // A round turns nothing on by itself: only the key applies the debuffs.
        if (_cfg.General.BannerOnRoundStart && _brain.InMatch) ShowBanner();
    }

    private static string Describe(Transition t) => t switch
    {
        Transition.NewMatch => "new match, new combo",
        Transition.Continue => "the match went live",
        Transition.SidesSwitched => "sides switched - same match",
        Transition.Halftime => "halftime",
        Transition.Return => "back in the match - same combo",
        Transition.MatchOver => "match over, keeping the combo until the next one",
        Transition.Abandon => "left the match",
        _ => t.ToString(),
    };

    /// <summary>
    /// The ONE place that brings the world in line with the state machine's decision: debuff.cfg (written
    /// only when its contents change) and the set of audio/visual effects (through SyncEffects).
    /// </summary>
    private static void Reconcile()
    {
        lock (Lock)
        {
            if (_shutdown) return;
            bool enabled = _cfg.General.Enabled;
            _wantEffects = _brain.WantEffects(enabled);
            string cfg = _brain.WantCfgCombo(enabled) ? $"combo#{_comboGen}" : "reset";
            if (cfg != _cfgState)
            {
                if (cfg == "reset") _cfgWriter.WriteResetOnly();
                else _cfgWriter.WriteCombo(_armed);
                _cfgState = cfg;
            }

            // the key's file: what the NEXT press will do (so the HUD changes instantly)
            string keyCfg = _brain.KeyCfgCombo(enabled) ? $"combo#{_comboGen}" : "reset";
            if (keyCfg != _keyCfgState)
            {
                if (keyCfg == "reset") _cfgWriter.WriteKeyReset();
                else _cfgWriter.WriteKeyCombo(_armed);
                _keyCfgState = keyCfg;
            }
        }
        PersistApplied();   // killing the process must not be a way to remove the debuffs
        SyncWatchdog();
        SyncEffects();
    }

    /// <param name="note">A line above the set (the strict grace period) - in the same notification, so they do not talk over each other.</param>
    private static void ShowBanner(string? note = null)
    {
        if (_cfg.General.FaceitSafeMode)
        {
            // We draw no windows over the game, but we do announce the set through a SYSTEM notification:
            // that window belongs to the Windows shell, not to us - to an anti-cheat it looks like a Discord popup.
            if (!_cfg.General.ShowComboBanner)
            {
                if (note is not null) Notify(note);
                return;
            }
            var labels = CurrentComboLabels();
            string combo = labels.Count == 0 ? "Clean match - no debuffs" : "Set: " + string.Join(", ", labels);
            Notify(note is null ? combo : note + "\n" + combo);
            return;
        }
        if (note is not null) Notify(note);
        if (!_cfg.General.ShowComboBanner || _banner is null) return;
        _banner.Show(CurrentComboLabels());
    }

    /// <summary>
    /// Strict mode on/off from the tray. It can be turned off only when not locked (outside a match or
    /// during the grace period); turning it on asks for confirmation.
    /// </summary>
    private static void ToggleStrict()
    {
        if (_cfg.General.StrictMode)
        {
            if (StrictRefuses("turning strict mode off")) { UpdateStrictMenu(); return; }
        }
        else
        {
            var r = MessageBox.Show(StrictWarning, "cs2-debuffs - strict mode", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
            if (r != DialogResult.Yes) { UpdateStrictMenu(); return; }
        }
        bool on;
        lock (Lock)
        {
            on = _cfg.General.StrictMode = !_cfg.General.StrictMode;
        }
        PersistEnabled();
        lock (Lock) _strictWasOn = on;
        if (on) StartGraceIfApplied();
        Reconcile();
        _log.Info(on ? "strict mode ON (tray)" : "strict mode OFF (tray)");
        Notify(on ? "Strict mode on" : "Strict mode off");
        UpdateStrictMenu();
        UpdateEnabledMenu(_cfg.General.Enabled);
    }

    internal const string StrictWarning =
        "Strict mode - no mercy:\n\n" +
        "- once you apply the debuffs with the key, they stay until the match ends;\n" +
        "- turning the mod off, exiting, uninstalling, clearing leftovers and changing settings are all blocked too;\n" +
        "- kill the process in Task Manager and the watchdog brings it back with the same combo.\n\n" +
        "After applying you get 30 seconds to change your mind: hold the key for 2 s and strict mode is waived for this match.\n" +
        "It releases at the end of the match, or when you leave it for the menu.\n" +
        "A pair of processes watching each other looks suspicious on FACEIT.\n\nTurn it on?";

    private static void UpdateStrictMenu()
    {
        var item = _strictItem;
        if (item is null) return;
        bool on = _cfg.General.StrictMode;
        string state = _brain.Locked ? " - locked until the match ends" : _brain.InGrace ? $" - {(int)_brain.GraceLeft.TotalSeconds} s of grace" : _brain.Waived ? " - waived for this match" : "";
        RunOnUi(() => { try { item.Checked = on; item.Text = "Strict mode" + state; } catch { } });
    }

    private static List<string> CurrentComboLabels() =>
        _armed.Picks.Select(p => ComboBanner.Label(p.Id)).ToList();

    /// <summary>Strict mode is locked (a match is running): no removing, no exiting, no uninstalling, no settings changes.</summary>
    private static bool StrictLocked
    {
        get { lock (Lock) return _brain.Locked; }
    }

    /// <summary>A strict-mode refusal with an explanation. true means the action is blocked.</summary>
    private static bool StrictRefuses(string what)
    {
        if (!StrictLocked) return false;
        _log.Info($"strict mode: refused - {what}");
        Notify($"Strict mode: {what} is only possible after the match ends. You turned it on, now live with it.");
        return true;
    }

    /// <summary>The mod's master toggle (tray/settings). Off means the mod does nothing: Reconcile removes everything and resets the cfg.</summary>
    private static void ToggleEnabled()
    {
        if (StrictRefuses("turning the mod off")) { UpdateEnabledMenu(_cfg.General.Enabled); return; }
        bool nowOn;
        lock (Lock) nowOn = _cfg.General.Enabled = !_cfg.General.Enabled;
        PersistEnabled();
        Reconcile();
        _log.Info(nowOn ? "mod ON" : "mod OFF");
        Notify(nowOn ? "Mod enabled" : "Mod disabled");
        UpdateEnabledMenu(nowOn);
    }

    /// <summary>
    /// The key was pressed: apply or remove ALL debuffs at once. CS2 applies the HUD ones at the very same
    /// moment, through the same bind, out of the pre-loaded debuff.cfg (see MatchBrain).
    /// </summary>
    private static void OnKeyPress()
    {
        KeyResult r;
        bool startGrace;
        lock (Lock)
        {
            r = _brain.OnKey(_cfg.General.Enabled);
            startGrace = r == KeyResult.Applied && _brain.InGrace;
        }

        if (r is KeyResult.Applied or KeyResult.Removed)
        {
            string what = r == KeyResult.Applied ? "debuffs applied (key)" : "debuffs removed (key)";
            _journal.Note(what);
            _log.Info(what);
            Reconcile();
            UpdateStrictMenu();
            if (startGrace) StartGraceCountdown();
        }

        Notify(r switch
        {
            KeyResult.Applied => "Debuffs applied" + (_cfg.General.StrictMode
                ? $" - strict mode locks in {(int)MatchBrain.StrictGrace.TotalSeconds} s"
                : " - press again to remove"),
            KeyResult.Removed => "Debuffs removed - press again to bring them back",
            KeyResult.EmptyCombo => "You rolled a clean match - no debuffs this time. A new combo comes with the next match.",
            KeyResult.NotInMatch => "Not in a match - the key applies debuffs during a match",
            KeyResult.StrictLocked => "Strict mode: the debuffs stay until the match ends. You turned it on, now live with it.",
            KeyResult.StrictHoldToWaive => $"To waive strict mode for this match, hold {KeyLabel(_cfg.General.ToggleHotkey)} for {(int)MatchBrain.WaiveHold.TotalSeconds} s (within the first {(int)MatchBrain.StrictGrace.TotalSeconds} s)",
            _ => "The mod is off - enable it in the tray or in settings",
        });
    }

    /// <summary>The toggle key was held during the strict grace period - waive strict mode for this match.</summary>
    private static bool WaiveStrictByHold()
    {
        bool waived;
        lock (Lock) waived = _brain.WaiveByHold();
        if (!waived) return false;
        _journal.Note("strict mode waived by holding the key during the grace period (this match)");
        _log.Info("strict mode waived for this match by holding the key");
        Reconcile();
        UpdateStrictMenu();
        Notify("Strict mode waived for this match. From now on the key works as usual.");
        return true;
    }

    /// <summary>A tray notification (about a toggle and the like).</summary>
    private static void Notify(string text)
    {
        var t = _tray;
        if (t is null) return;
        RunOnUi(() =>
        {
            try
            {
                t.BalloonTipTitle = "cs2-debuffs";
                t.BalloonTipText = text;
                t.ShowBalloonTip(1500);
            }
            catch { }
        });
    }

    private static void UpdateEnabledMenu(bool on)
    {
        var item = _enabledItem;
        if (item != null) RunOnUi(() => { try { item.Checked = on; } catch { } });
    }

    /// <summary>Save debuffs.json (after enabled was changed from the tray or the key), with no "applies to the next match" note.</summary>
    private static void PersistEnabled()
    {
        try
        {
            File.WriteAllText(_configPath, JsonSerializer.Serialize(_cfg, ConfigLoader.JsonOpts));
        }
        catch (Exception ex) { _log.Error("could not save the on/off state", ex); }
    }

    /// <summary>Remove the mod from the system: autostart, GSI/cfg from the CS2 folder, mute/APO, markers. The user deletes the folder themselves.</summary>
    private static void Uninstall()
    {
        if (StrictRefuses("uninstalling the mod")) return;
        var r = MessageBox.Show(
            "Uninstall the mod from the system?\n\n" +
            "This removes: start with Windows, the GSI config and our cfg files from the CS2 folder, the " +
            "Include line from Equalizer APO; it also lifts the mute and puts the game settings back to normal.\n\n" +
            "After that, drop your own binds to our files in the CS2 console - the commands will be shown.\n\n" +
            "Delete the cs2-debuffs folder by hand - it will be opened in Explorer.\n\nContinue?",
            "cs2-debuffs - uninstall", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
        if (r != DialogResult.Yes) return;

        _uninstalled = true;
        _userExit = true; // the watchdog must not bring us back
        try
        {
            Autostart.Disable();
            lock (Lock) RevertAllLocked();
            AudioModule.CleanupLeftovers(_log);
            AudioModule.RemoveApoTraces(_log);

            // The in-game reset FIRST: the player still has binds to our files in CS2, and if we just delete
            // the files, the exec stops doing anything and the scoreboard, radar and crosshair stay gone.
            _cfgWriter.WriteResetOnly();
            _cfgWriter.WriteKeyReset();
            _cfgWriter.RemoveGsiOnly();

            // We do NOT touch installed.flag: otherwise the next accidental launch would enable autostart again.
            foreach (var f in new[] { _armedFile, _armedMapFile, _appliedFile, _runningFlag })
                try { if (File.Exists(f)) File.Delete(f); } catch { }

            var dir = Path.GetDirectoryName(ExePath);
            try { if (!string.IsNullOrEmpty(dir)) Process.Start(new ProcessStartInfo { FileName = dir, UseShellExecute = true }); } catch { }
            _log.Info("mod removed from the system (autostart, GSI and APO cleared; the cfg files left in the reset state)");
            string key = KeyLabel(_cfg.General.ToggleHotkey);
            MessageBox.Show(
                "Done. The mod is off the system and the debuffs are removed.\n\n" +
                "Your CS2 binds are still there - drop them in the console:\n" +
                "  bind w \"+forward\"\n" +
                "  bind ctrl \"+duck\"\n" +
                "  bind space \"+jump\"\n" +
                $"  unbind {Program.Cs2KeyName(_cfg.General.ToggleHotkey)}\n\n" +
                "debuff.cfg and debuff_key.cfg were left in the CS2 folder in the \"reset\" state,\n" +
                "so old binds cannot break anything. You can delete them once the binds are gone.\n\n" +
                "Delete the cs2-debuffs folder yourself - it has been opened in Explorer.", "cs2-debuffs");
        }
        catch (Exception ex) { _log.Error("uninstalling the mod", ex); }
        ExitByUser();
    }

    private static void OpenSettings()
    {
        try
        {
            if (_settings is { IsDisposed: false })
            {
                if (_settings.WindowState == FormWindowState.Minimized) _settings.WindowState = FormWindowState.Normal;
                _settings.TopMost = true;    // Windows will not let a background app steal focus - briefly raise it instead
                _settings.Activate();
                _settings.TopMost = false;
                return;
            }
            _settings = new SettingsForm(_cfg, ExePath, ApplyConfig, () => RunOnUi(Uninstall), IsAvailable, () => StrictLocked, Lock);
            _settings.FormClosed += (_, _) => _settings = null;
            _settings.Show();
            _settings.TopMost = true;    // opened from the tray or a relaunch - pull it to the front
            _settings.Activate();
            _settings.TopMost = false;
        }
        catch (Exception ex)
        {
            _log.Error("could not open settings", ex);
            MessageBox.Show($"Could not open settings:\n{ex.Message}", "cs2-debuffs");
        }
    }

    /// <summary>
    /// Settings saved: _cfg was already changed by the form (the same object the roulette reads), so we write
    /// it to disk. It takes effect from the next match; enabled flags, chances, the multiplier and the overlay
    /// are picked up on the fly.
    /// </summary>
    private static void ApplyConfig()
    {
        bool strictWas;
        lock (Lock) strictWas = _strictWasOn;
        try
        {
            var json = JsonSerializer.Serialize(_cfg, ConfigLoader.JsonOpts);
            // check that the result parses back before touching the working file
            if (JsonSerializer.Deserialize<AppConfig>(json, ConfigLoader.JsonOpts) is null)
                throw new InvalidDataException("serialization produced nothing");
            File.WriteAllText(_configPath, json);
            _log.Info("settings saved (the set and the chances apply from the next match)");
            UpdateEnabledMenu(_cfg.General.Enabled);
            if (_cfg.General.StrictMode && !strictWas) StartGraceIfApplied();   // only when turning it on, otherwise the grace period could be extended forever
            lock (Lock) _strictWasOn = _cfg.General.StrictMode;
            Reconcile();   // the master toggle from the window takes effect immediately, not from the next match
            UpdateStrictMenu();
        }
        catch (Exception ex)
        {
            _log.Error("could not save the settings", ex);
            MessageBox.Show($"Could not save the settings:\n{ex.Message}", "cs2-debuffs");
        }
    }

    /// <summary>
    /// Brings the effects that are actually on in line with the state machine's decision (_wantEffects).
    /// Audio runs for the whole match. Visuals only while the CS2 window is focused (minimize the game and
    /// the desktop is clean). Effects that are not in the current combo (left from the previous match) are
    /// removed. The cfg debuffs live inside the game itself, so they are not here. Called every 250 ms,
    /// which is why it returns immediately outside a match.
    /// </summary>
    private static void SyncEffects()
    {
        lock (Lock)
        {
            if (_shutdown) return;
            if (!_wantEffects && ActiveIds.Count == 0) return;   // outside a match and nothing is on - nothing to do
            bool inGame = _wantEffects && Cs2IsForeground();

            // leftovers from the previous combo (a new match with no gameover, e.g. a map rotation in live)
            foreach (var id in ActiveIds.ToList())
                if (!_armed.Picks.Any(p => p.Id == id)) SetEffect(id, false);

            foreach (var p in _armed.Picks)
            {
                if (p.Kind == "cfg") continue;
                bool want = _wantEffects && (AudioIds.Contains(p.Id) || inGame);
                if (want != ActiveIds.Contains(p.Id)) SetEffect(p.Id, want);
            }
        }
    }

    /// <summary>Turn one system effect on or off. Only under Lock.</summary>
    private static void SetEffect(string id, bool on)
    {
        if (!_effects.TryGetValue(id, out var e)) { ActiveIds.Remove(id); return; }
        try
        {
            if (on) { e.Apply(); ActiveIds.Add(id); _log.Info($"applied: {id}"); }
            else { e.Revert(); ActiveIds.Remove(id); _log.Info($"removed: {id}"); }
        }
        catch (Exception ex)
        {
            _log.Error($"effect {id}", ex);
            if (!on) ActiveIds.Remove(id);
        }
    }

    /// <summary>Whether CS2 is running. The Process objects are released right away (this is called from a timer every 15 s).</summary>
    private static bool Cs2Running()
    {
        var ps = Process.GetProcessesByName("cs2");
        foreach (var p in ps) p.Dispose();
        return ps.Length > 0;
    }

    // A "focused window -> is it CS2" cache: we resolve the process behind the window only when the window
    // changes, not every 250 ms (it used to call Process.GetProcessById on every tick).
    private static IntPtr _fgHwnd;
    private static bool _fgIsGame;

    /// <summary>true if the focused window is cs2's or one of our own overlays (so we do not switch ourselves off).</summary>
    private static bool Cs2IsForeground()
    {
        var h = GetForegroundWindow();
        if (h == _fgHwnd) return _fgIsGame;
        _fgHwnd = h;
        _fgIsGame = false;
        try
        {
            if (h == IntPtr.Zero) return false;
            GetWindowThreadProcessId(h, out uint pid);
            if (pid == 0) return false;
            if (pid == (uint)Environment.ProcessId) return _fgIsGame = true; // our own overlay - stay on
            using var p = Process.GetProcessById((int)pid);
            return _fgIsGame = string.Equals(p.ProcessName, "cs2", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// The old layout (debuffs.json, logs, state, journal.jsonl sitting right next to the exe) moves into
    /// data on the first run of a new version. Called BEFORE the log exists, so it fails silently.
    /// </summary>
    private static void MigrateLegacyLayout(string baseDir, string dataDir)
    {
        try
        {
            bool legacy = File.Exists(Path.Combine(baseDir, "debuffs.json"))
                          || Directory.Exists(Path.Combine(baseDir, "state"))
                          || Directory.Exists(Path.Combine(baseDir, "logs"));
            if (!legacy) return;

            Directory.CreateDirectory(dataDir);
            foreach (var name in new[] { "debuffs.json", "journal.jsonl" })
            {
                var from = Path.Combine(baseDir, name);
                var to = Path.Combine(dataDir, name);
                if (File.Exists(from) && !File.Exists(to)) File.Move(from, to);
            }
            foreach (var name in new[] { "logs", "state" })
            {
                var from = Path.Combine(baseDir, name);
                var to = Path.Combine(dataDir, name);
                if (Directory.Exists(from) && !Directory.Exists(to)) Directory.Move(from, to);
            }
        }
        catch { }
    }

    /// <summary>Remove every system effect (on exit or uninstall). Only under Lock.</summary>
    private static void RevertAllLocked()
    {
        _wantEffects = false;
        foreach (var id in ActiveIds.ToList())
            if (_effects.TryGetValue(id, out var e))
            {
                try { e.Revert(); } catch { }
            }
        ActiveIds.Clear();
    }

    private static void Shutdown()
    {
        lock (Lock)
        {
            if (_shutdown) return;
            _shutdown = true;
            RevertAllLocked();
            if (_uninstalled)
            {
                // Uninstall already did everything: the cfg files are left in the reset state (the player's binds still point at them).
            }
            else
            {
                _cfgWriter.WriteResetOnly();
                _cfgWriter.WriteKeyReset();    // otherwise the next key press would apply the dead app's set
            }
            // Any clean exit (including a Windows session shutdown) is not a kill: the watchdog must leave.
            try { Directory.CreateDirectory(_stateDir); File.WriteAllText(_stopFlag, DateTime.UtcNow.Ticks.ToString()); } catch { }
            _gsi?.Dispose();
            try { _banner?.Dispose(); } catch { }
            try { _statusPanel?.Dispose(); } catch { }
            foreach (var e in _effects.Values)
            {
                try { e.Dispose(); } catch { }
            }
            // A clean exit: drop the "running" marker so the next launch is a fresh start, not a comeback.
            try { if (File.Exists(_runningFlag)) File.Delete(_runningFlag); } catch { }
            _log.Info("exiting, everything removed");
        }
    }

    private static void Safe(Action a, string what)
    {
        try { a(); }
        catch (Exception ex) { _log.Error($"{what} failed to start - its debuffs drop out of the roulette", ex); }
    }

}

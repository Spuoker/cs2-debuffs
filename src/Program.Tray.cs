using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace DebuffRoulette;

/// <summary>Tray: the menu, hotkeys (the Tab panel and the toggle key), safety timers, status.</summary>
internal static partial class Program
{
    // ---------------------- tray ----------------------

    private static void RunTray()
    {
        using var icon = new NotifyIcon
        {
            Icon = SystemIcons.Application,
            Visible = true,
            Text = "cs2-debuffs",
        };
        _tray = icon;
        var menu = new ContextMenuStrip();
        _enabledItem = new ToolStripMenuItem("Mod enabled") { Checked = _cfg.General.Enabled };
        _enabledItem.Click += (_, _) => ToggleEnabled();
        menu.Items.Add(_enabledItem);
        _strictItem = new ToolStripMenuItem("Strict mode") { Checked = _cfg.General.StrictMode };
        _strictItem.Click += (_, _) => ToggleStrict();
        menu.Items.Add(_strictItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Settings...", null, (_, _) => OpenSettings());
        menu.Items.Add("Status...", null, (_, _) => MessageBox.Show(StatusText(), "cs2-debuffs"));
        menu.Items.Add("Show the set on screen", null, (_, _) => ShowBanner());
        menu.Items.Add("Active debuffs panel (Tab in game)", null, (_, _) =>
        {
            if (_cfg.General.FaceitSafeMode)
            {
                MessageBox.Show("FACEIT-safe mode keeps all windows off the game.", "cs2-debuffs");
                return;
            }
            _statusPanel?.Toggle(CurrentComboLabels());
        });
        menu.Items.Add("Clear leftovers (mute/APO)", null, (_, _) =>
        {
            if (StrictRefuses("clearing leftovers")) return;
            lock (Lock)
            {
                RevertAllLocked();             // otherwise the app still thinks the effects are live and will not re-apply them
                AudioModule.CleanupLeftovers(_log);
                _cfgState = _keyCfgState = "";  // force both files to be rewritten
            }
            Reconcile();                       // brings back whatever the match state calls for
            MessageBox.Show("Mute lifted, APO cleared, the CS2 files rewritten from scratch.", "cs2-debuffs");
        });
        var autostartItem = new ToolStripMenuItem("Start with Windows")
        {
            Checked = Autostart.IsEnabled(),
            CheckOnClick = true,
        };
        autostartItem.Click += (_, _) =>
        {
            try
            {
                if (autostartItem.Checked) Autostart.Enable(ExePath);
                else Autostart.Disable();
                _log.Info($"start with Windows: {(autostartItem.Checked ? "on" : "off")}");
            }
            catch (Exception ex) { _log.Error("could not toggle start with Windows", ex); }
        };
        menu.Items.Add(autostartItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Uninstall mod...", null, (_, _) => Uninstall());
        menu.Items.Add("Exit", null, (_, _) => { if (!StrictRefuses("exiting")) ExitByUser(); });
        icon.ContextMenuStrip = menu;
        icon.DoubleClick += (_, _) => OpenSettings();   // double-click the icon to open the window, like any normal app

        // The status panel (what is active plus buttons): shown WHILE Tab IS HELD, only in a live match
        // and with CS2 focused. The panel is click-through (it does not hold the mouse) and steals no focus
        // (MA_NOACTIVATE); it is raised hard on show and held softly afterwards, so it does not flicker.
        uint toggleVk = ParseVk(_cfg.General.ToggleHotkey);
        bool toggleHeld = false, panelShown = false;
        // During the strict grace period a press does not toggle - strict mode is dropped only by HOLDING
        // the key (WaiveHold), so a stray tap cannot do it. Outside the grace period it is a plain toggle.
        bool holdForWaive = false, holdDone = false;
        DateTime holdStart = default;
        // The watchdog may have started the app while the player is holding the key: do not count that as a fresh press.
        if (toggleVk != 0 && (GetAsyncKeyState((int)toggleVk) & 0x8000) != 0) toggleHeld = true;
        _log.Info("panel: shown while Tab is held (live match, CS2 focused)");
        string toggleKeyName = _cfg.General.ToggleHotkey ?? "";
        if (toggleVk != 0) _log.Info($"debuff toggle key: '{KeyLabel(toggleKeyName)}'");
        // 20 ms, not 80: the game runs debuff_key.cfg at the moment of the press, and a quick tap the app
        // missed would put the two out of sync until the first step. A human press lasts 50 ms or more.
        using var keyTimer = new System.Windows.Forms.Timer { Interval = 20 };
        keyTimer.Tick += (_, _) =>
        {
            bool tab = (GetAsyncKeyState(VkTab) & 0x8000) != 0;
            bool want = !_cfg.General.FaceitSafeMode
                        && tab && _brain.InMatch && _brain.Applied && _armed.Picks.Count > 0 && Cs2IsForeground();
            if (want && !panelShown) { panelShown = true; _statusPanel?.ShowPanel(CurrentComboLabels()); }
            else if (!want && panelShown) { panelShown = false; _statusPanel?.HidePanel(); }

            // the key was reassigned in settings - pick it up right away, no restart needed
            if (!string.Equals(toggleKeyName, _cfg.General.ToggleHotkey ?? "", StringComparison.Ordinal))
            {
                toggleKeyName = _cfg.General.ToggleHotkey ?? "";
                toggleVk = ParseVk(toggleKeyName);
                toggleHeld = holdForWaive = holdDone = false;
                if ((GetAsyncKeyState((int)toggleVk) & 0x8000) != 0) toggleHeld = true;   // already held - do not count it as a press
                _log.Info($"debuff toggle key: '{KeyLabel(toggleKeyName)}'");
            }

            if (toggleVk != 0)
            {
                bool down = (GetAsyncKeyState((int)toggleVk) & 0x8000) != 0;
                if (down && !toggleHeld)
                {
                    toggleHeld = true;
                    holdStart = DateTime.UtcNow;
                    holdDone = false;
                    lock (Lock) holdForWaive = _brain.InGrace && _cfg.General.Enabled;
                    if (!holdForWaive) OnKeyPress();
                }
                else if (down && holdForWaive && !holdDone && DateTime.UtcNow - holdStart >= MatchBrain.WaiveHold)
                {
                    holdDone = true;
                    if (!WaiveStrictByHold()) OnKeyPress();   // the grace period ran out mid-hold - say so instead of staying silent
                }
                else if (!down && toggleHeld)
                {
                    toggleHeld = false;
                    if (holdForWaive && !holdDone) OnKeyPress();   // a short tap during the grace period - hint to hold it
                    holdForWaive = false;
                }
            }
        };
        keyTimer.Start();

        // Fast focus polling: the visual effects fade out when CS2 loses focus and come back when it regains it.
        using var fgTimer = new System.Windows.Forms.Timer { Interval = 250 };
        fgTimer.Tick += (_, _) => SyncEffects();
        fgTimer.Start();

        // 3 s, not 15: that is how long the "kill the watchdog, then the main process, nobody left to restart" hole lives
        using var timer = new System.Windows.Forms.Timer { Interval = 3000 };
        timer.Tick += (_, _) =>
        {
            TouchRunningFlag(); // keep the marker fresh so a kill even an hour later still counts as recent

            // the watchdog died (killed too?) - start a new one, unless this is our own clean exit
            if (WantWatchdog && _watchdog is { HasExited: true })
            {
                _log.Warn("the watchdog died - starting a new one");
                _watchdog = StartWatchdog();
            }

            bool cs2Running = Cs2Running();
            if (cs2Running) _cs2Seen = true;

            // The "live alongside the game" model: CS2 was running and closed, so we exit cleanly.
            if (_cfg.General.ExitWhenCs2Closes && _cs2Seen && !cs2Running && !StrictLocked)
            {
                // StrictLocked: otherwise "kill cs2.exe in Task Manager" would be a legal way out of strict mode
                _log.Info("CS2 closed - exiting (exit_when_cs2_closes)");
                ExitByUser();
                return;
            }

            // Safety net: GSI went silent and cs2.exe is gone (the game was killed or crashed) - that is the
            // same thing as leaving the match: the state machine removes everything and keeps the combo in
            // case we come back to the same match. Two thresholds: with no game we wait the normal timeout;
            // with the game alive but GSI broken (port changed, gsi cfg deleted) we wait twice as long and
            // still remove.
            double silence = _gsi is null ? 0 : (DateTime.UtcNow - _gsi.LastPostUtc).TotalSeconds;
            double limit = _cfg.General.GsiSilenceRevertSeconds * (cs2Running ? 2 : 1);
            if (_brain.InMatch && _gsi is not null && silence > limit)
            {
                _log.Warn($"GSI silent for {(int)silence} s (cs2.exe {(cs2Running ? "alive" : "gone")}) - treating it as leaving the match");
                _gsi.ResetPhase();
                OnGsiMenu();
            }
        };
        timer.Start();

        if (!_startMinimized) OpenSettings(); // launched by hand: show the window right away; autostart: quietly to the tray
        Application.Run();
    }

    /// <summary>A clean exit from the tray menu: stop the watchdog so it does not bring us back.</summary>
    private static void ExitByUser()
    {
        _userExit = true;
        try
        {
            Directory.CreateDirectory(_stateDir);
            File.WriteAllText(_stopFlag, DateTime.UtcNow.Ticks.ToString());
        }
        catch { }
        StopWatchdog();
        Application.Exit();
    }

    private static string StatusText()
    {
        var ids = _effects.Values.Where(e => e.IsAvailable).Select(e => e.Id)
            .Concat(CfgIds().Where(_ => _cfgWriter.Available))
            .OrderBy(x => x);
        return $"""
            Mod: {(_cfg.General.Enabled ? "enabled" : "DISABLED")}
            FACEIT-safe mode: {(_cfg.General.FaceitSafeMode ? "ON (no windows over the game)" : "off")}
            Strict mode: {(_cfg.General.StrictMode ? (_brain.Locked ? "ON - LOCKED until the match ends" : _brain.InGrace ? $"ON - {(int)_brain.GraceLeft.TotalSeconds} s of grace left" : _brain.Waived ? "waived for this match" : "ON (locks in a match 30 s after you apply)") : "off")}
            Toggle key (everything at once): {KeyLabel(_cfg.General.ToggleHotkey)}  (in CS2: {BindCommand(_cfg.General.ToggleHotkey)})
            Debuffs right now: {(_brain.WantEffects(_cfg.General.Enabled) ? "applied" : "removed")}
            debuff.cfg right now: {(_brain.WantCfgCombo(_cfg.General.Enabled) ? "the set's cfg debuffs" : "reset")}
            CS2 cfg: {(_cfgWriter.Available ? "found" : "NOT FOUND")}
            GSI port: {_cfg.General.GsiPort}
            CS2 running: {(Cs2Running() ? "yes" : "no")}
            Start with Windows: {(Autostart.IsEnabled() ? "on" : "off")}
            Match: {PhaseName(_brain.Phase)}
            CS2 focused: {(Cs2IsForeground() ? "yes" : "no")}
            Watchdog: {(_watchdog is { HasExited: false } ? "alive" : "none")}
            Debuffs in the current combo: {_armed.Picks.Count}
            Available debuffs: {string.Join(", ", ids)}
            """;
    }

    private static string PhaseName(MatchPhase p) => p switch
    {
        MatchPhase.Warmup => "warmup",
        MatchPhase.Live => "live",
        MatchPhase.Halftime => "halftime (sides switching)",
        MatchPhase.Over => "over",
        _ => "not in a match",
    };

    private static IEnumerable<string> CfgIds() =>
        _cfg.Slots.SelectMany(s => s.Options).Where(o => o.Kind == "cfg").Select(o => o.Id);

    /// <summary>A human-readable key name: "D5" -> "5", "Oemplus" -> "+", "Next" -> "PgDn". Empty gives "the toggle key".</summary>
    internal static string KeyLabel(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "the toggle key";
        var t = name.Trim();
        if (t.Length == 2 && t[0] == 'D' && char.IsDigit(t[1])) return t[1..];
        if (t.StartsWith("NumPad", StringComparison.Ordinal) && t.Length == 7) return "Num " + t[6];
        return t switch
        {
            "Oemplus" => "+", "OemMinus" => "-", "Oemcomma" => ",", "OemPeriod" => ".",
            "OemQuestion" or "Oem2" => "/", "Oemtilde" or "Oem3" => "~", "OemOpenBrackets" or "Oem4" => "[",
            "OemCloseBrackets" or "Oem6" => "]", "OemSemicolon" or "Oem1" => ";", "OemQuotes" or "Oem7" => "'",
            "OemPipe" or "Oem5" or "OemBackslash" => "\\",
            "Prior" or "PageUp" => "PgUp", "Next" or "PageDown" => "PgDn",
            "Return" => "Enter", "Capital" or "CapsLock" => "CapsLock", "Back" => "Backspace",
            "Menu" or "LMenu" or "RMenu" => "Alt", "ControlKey" or "LControlKey" or "RControlKey" => "Ctrl",
            "ShiftKey" or "LShiftKey" or "RShiftKey" => "Shift", "XButton1" => "Mouse4", "XButton2" => "Mouse5",
            "MButton" => "Mouse3", "Multiply" => "Num *", "Add" => "Num +", "Subtract" => "Num -",
            "Divide" => "Num /", "Decimal" => "Num .",
            _ => t,
        };
    }

    /// <summary>
    /// The key name as CS2 calls it in a bind command: "D5" -> "5", "End" -> "END",
    /// "XButton1" -> "MOUSE4", "NumPad5" -> "KP_5". Empty means no key is assigned.
    /// </summary>
    internal static string Cs2KeyName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "";
        var t = name.Trim();
        if (t.Length == 1 && char.IsLetterOrDigit(t[0])) return t.ToLowerInvariant();
        if (t.Length == 2 && t[0] == 'D' && char.IsDigit(t[1])) return t[1..];
        if (t.StartsWith("NumPad", StringComparison.Ordinal) && t.Length == 7)
            return t[6] switch
            {
                '0' => "KP_INS", '1' => "KP_END", '2' => "KP_DOWNARROW", '3' => "KP_PGDN", '4' => "KP_LEFTARROW",
                '5' => "KP_5", '6' => "KP_RIGHTARROW", '7' => "KP_HOME", '8' => "KP_UPARROW", _ => "KP_PGUP",
            };
        if (t.Length >= 2 && t[0] == 'F' && char.IsDigit(t[1])) return t.ToUpperInvariant();   // F1..F12
        return t switch
        {
            "End" => "END", "Home" => "HOME", "Insert" => "INS", "Delete" => "DEL",
            "Prior" or "PageUp" => "PGUP", "Next" or "PageDown" => "PGDN",
            "Up" => "UPARROW", "Down" => "DOWNARROW", "Left" => "LEFTARROW", "Right" => "RIGHTARROW",
            "Space" => "SPACE", "Return" => "ENTER", "Back" => "BACKSPACE", "Tab" => "TAB", "Escape" => "ESCAPE",
            "Capital" or "CapsLock" => "CAPSLOCK", "Scroll" => "SCROLLLOCK", "Pause" => "PAUSE",
            "ShiftKey" or "LShiftKey" or "RShiftKey" => "SHIFT",
            "ControlKey" or "LControlKey" or "RControlKey" => "CTRL",
            "Menu" or "LMenu" or "RMenu" => "ALT",
            "XButton1" => "MOUSE4", "XButton2" => "MOUSE5", "MButton" => "MOUSE3",
            "Oemtilde" or "Oem3" => "`", "OemMinus" => "-", "Oemplus" => "=",
            "OemOpenBrackets" or "Oem4" => "[", "OemCloseBrackets" or "Oem6" => "]",
            "OemSemicolon" or "Oem1" => ";", "OemQuotes" or "Oem7" => "'",
            "OemPipe" or "Oem5" or "OemBackslash" => "\\",
            "Oemcomma" => ",", "OemPeriod" => ".", "OemQuestion" or "Oem2" => "/",
            "Multiply" => "KP_MULTIPLY", "Add" => "KP_PLUS", "Subtract" => "KP_MINUS",
            "Divide" => "KP_SLASH", "Decimal" => "KP_DEL",
            _ => t.ToUpperInvariant(),
        };
    }

    /// <summary>A ready-made CS2 console command that binds the toggle key for instant apply.</summary>
    internal static string BindCommand(string? keyName)
    {
        var k = Cs2KeyName(keyName);
        return k.Length == 0 ? "" : $"bind {k} " + '"' + "exec " + CfgWriter.KeyCfgName.Replace(".cfg", "") + '"';
    }

    /// <summary>Key name -> virtual-key code. 0 means unrecognized or disabled. It understands names from Keys
    /// (End, F8, Insert...), "D5" or a bare digit/letter, plus a few friendly aliases (ins/del/pgup...).</summary>
    private static uint ParseVk(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return 0;
        var t = name.Trim();

        if (t.Length == 1)
        {
            char c = char.ToUpperInvariant(t[0]);
            if ((c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9')) return c; // vk == ASCII for A-Z/0-9
        }

        switch (t.ToLowerInvariant()) // friendly aliases from older configs
        {
            case "ins": return 0x2D;
            case "del": return 0x2E;
            case "pgup": return 0x21;
            case "pgdn": return 0x22;
            case "scrolllock": return 0x91;
        }

        // Keys.End == 0x23 and so on - the enum values match the virtual-key codes.
        return Enum.TryParse<Keys>(t, ignoreCase: true, out var k) ? (uint)k : 0;
    }

}

using System.Diagnostics;

namespace DebuffRoulette;

/// <summary>
/// The settings window: switch debuffs on and off, tweak chances and the general options.
/// Saving takes effect from the NEXT match (the current combo is left alone).
/// </summary>
public sealed class SettingsForm : Form
{
    private static readonly Dictionary<string, string> SlotNames = new(StringComparer.Ordinal)
    {
        ["priority"] = "Priority (whole screen)",
        ["audio"] = "Audio",
        ["voice"] = "Voice",
        ["light_color"] = "Color",
        ["interface"] = "Interface",
    };

    private readonly AppConfig _cfg;
    private readonly string _exePath;
    private readonly Action _onApply;
    private readonly Action _onUninstall;
    private readonly Func<string, bool> _isAvailable;
    private readonly Func<bool> _isLocked;
    private readonly object _cfgLock;

    private static readonly Color UnavailableBack = Color.FromArgb(228, 228, 228);

    /// <summary>Why a debuff is unavailable on this machine - the caption on the greyed-out row.</summary>
    private static string UnavailableReason(OptionConfig o) =>
        o.Kind == "cfg" ? "the CS2 cfg folder was not found"
        : o.Id is "mono" or "bass_boost" or "muffle" ? "needs Equalizer APO"
        : "unavailable on this system (see logs\\app.log)";

    // Adjustable effect strength: the effect id, the parameter name in debuffs.json, the label, the range.
    private static readonly (string Id, string Param, string Label, decimal Min, decimal Max, decimal Step, int Dec)[] Strengths =
    {
        ("white_noise", "volume", "White noise - volume", 0m, 1m, 0.05m, 2),
        ("bass_boost", "gain_db", "Bass boost - gain, dB", 0m, 24m, 1m, 0),
        ("muffle", "fc_hz", "Underwater - cutoff, Hz (lower = muddier)", 300m, 4000m, 50m, 0),
        ("brightness_down", "scale", "Dimmed - multiplier (lower = darker)", 0.1m, 1m, 0.02m, 2),
        ("contrast_up", "contrast", "High contrast - strength", 1m, 3m, 0.1m, 1),
        ("washed_out", "contrast", "Washed out - contrast (lower = flatter)", 0.2m, 1m, 0.05m, 2),
        ("washed_out", "lift", "Washed out - black lift", 0m, 0.6m, 0.05m, 2),
        ("rainbow", "period_sec", "Shifting palette - period, s", 3m, 30m, 1m, 0),
    };

    private readonly List<(string Id, string Param, NumericUpDown Ctl)> _strengths = new();
    private readonly Dictionary<OptionConfig, (CheckBox On, NumericUpDown Chance)> _opts = new();

    // Conflicts: unique "never rolled together" pairs (the order inside a pair is normalized).
    private readonly List<(string A, string B)> _pairs = new();
    private ListBox _pairsList = null!;
    private ComboBox _pairA = null!, _pairB = null!;

    private sealed record EffectItem(string Id)
    {
        public override string ToString() => ComboBanner.Label(Id);
    }
    private NumericUpDown _mult = null!;
    private NumericUpDown _fadeIn = null!, _fadeOut = null!;
    private CheckBox _banner = null!, _bannerRound = null!, _exitWithCs2 = null!, _autostart = null!, _enabledCheck = null!, _faceitSafe = null!, _strict = null!;
    private Label _toggleKeyLabel = null!;
    private TextBox _bindBox = null!;
    private TextBox _apoDir = null!;
    private Label _apoFound = null!;
    private string _toggleKeyName = "";
    private bool _capturingKey;

    public SettingsForm(AppConfig cfg, string exePath, Action onApply, Action onUninstall, Func<string, bool> isAvailable, Func<bool> isLocked, object cfgLock)
    {
        _cfg = cfg;
        _exePath = exePath;
        _onApply = onApply;
        _onUninstall = onUninstall;
        _isAvailable = isAvailable;
        _isLocked = isLocked;
        _cfgLock = cfgLock;

        Text = "cs2-debuffs - settings";
        FormBorderStyle = FormBorderStyle.Sizable;
        StartPosition = FormStartPosition.CenterScreen;
        MaximizeBox = false;
        MinimizeBox = false;
        Font = new Font("Segoe UI", 9f);
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(660, 760);
        MinimumSize = new Size(648, 420);
        KeyPreview = true;   // we catch key presses to assign the toggle key

        Build();
    }

    /// <summary>Show what is actually in use: the manual path or whatever was auto-detected.</summary>
    private void SyncApoFound()
    {
        if (_apoFound is null) return;
        var manual = _apoDir.Text.Trim();
        if (manual.Length > 0)
        {
            bool ok = AudioModule.ApoManager.IsApoDir(manual);
            _apoFound.Text = ok ? "Set by hand - config.txt is there" : "Set by hand, but there is no config.txt in that folder";
            _apoFound.ForeColor = ok ? SystemColors.GrayText : Color.Firebrick;
            return;
        }
        if (AudioModule.ApoManager.TryFindInRegistry(out var dir))
        {
            _apoFound.Text = "Auto-detected: " + dir;
            _apoFound.ForeColor = SystemColors.GrayText;
        }
        else
        {
            _apoFound.Text = "Not found - mono, bass boost and underwater never roll. Click \"How to install\"";
            _apoFound.ForeColor = Color.Firebrick;
        }
    }

    private const string ApoUrl = "https://sourceforge.net/projects/equalizerapo/files/latest/download";

    /// <summary>
    /// The Equalizer APO instructions, right inside the app, so no txt file has to travel next to the exe.
    /// Without APO the mono / bass boost / underwater debuffs simply never roll; everything else works.
    /// </summary>
    private void ShowApoHelp()
    {
        var r = MessageBox.Show(this,
            "The mono, bass boost and underwater audio debuffs are done by Equalizer APO," + "\n" +
            "a separate free program. Without it they simply never roll; everything else works." + "\n" + "\n" +
            "How to install it (once):" + "\n" +
            "1. Download and run the Equalizer APO installer." + "\n" +
            "2. At the end of setup, tick YOUR headphones or speakers - whatever you play through." + "\n" +
            "3. Reboot the computer." + "\n" + "\n" +
            "Sound does not change (a common one with Realtek): Configurator -> Troubleshooting options ->" + "\n" +
            "\"Install as SFX/EFX (experimental)\" -> reboot." + "\n" + "\n" +
            "Installed it somewhere unusual? Point the field on the left at it with \"Browse...\" -" + "\n" +
            "that is the folder holding config.txt." + "\n" + "\n" +
            "Open the download page?",
            "cs2-debuffs - audio debuffs", MessageBoxButtons.YesNo, MessageBoxIcon.Information);
        if (r != DialogResult.Yes) return;
        try { Process.Start(new ProcessStartInfo { FileName = ApoUrl, UseShellExecute = true }); }
        catch (Exception ex) { MessageBox.Show(this, "The link did not open:" + "\n" + ex.Message + "\n" + "\n" + ApoUrl, "cs2-debuffs"); }
    }

    /// <summary>Refresh the ready-made bind command for the currently assigned key.</summary>
    private void SyncBindCommand()
    {
        if (_bindBox is null) return;
        var cmd = Program.BindCommand(_toggleKeyName);
        _bindBox.Text = cmd.Length == 0 ? "no key assigned" : cmd;
    }

    private static string KeyDisplay(string name) =>
        string.IsNullOrWhiteSpace(name) ? "- (off)" : Program.KeyLabel(name);

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (_capturingKey)
        {
            e.Handled = true;
            e.SuppressKeyPress = true;
            _capturingKey = false;
            if (e.KeyCode is Keys.Tab or Keys.W or Keys.A or Keys.S or Keys.D or Keys.Space or Keys.ControlKey or Keys.LControlKey)
            {
                // Tab holds the status panel; WASD/Space/Ctrl carry the exec debuff binds and the game itself.
                MessageBox.Show(this,
                    "That key is taken by the game: Tab shows the status panel, and WASD, Space and Ctrl carry" + "\n" +
                    "movement plus the exec debuff binds. Pick a free one: End, Insert, an F-key, a side mouse button.",
                    "cs2-debuffs");
                _toggleKeyLabel.Text = KeyDisplay(_toggleKeyName);
                return;
            }
            if (e.KeyCode != Keys.Escape && e.KeyCode != Keys.None)
                _toggleKeyName = e.KeyCode.ToString();
            _toggleKeyLabel.Text = KeyDisplay(_toggleKeyName);
            SyncBindCommand();
            return;
        }
        base.OnKeyDown(e);
    }

    private void Build()
    {
        // bottom: buttons
        var bottom = new Panel { Dock = DockStyle.Bottom, Height = 52, Padding = new Padding(10) };
        var save = new Button { Text = "Save", Width = 130, Height = 32, Dock = DockStyle.Right };
        var cancel = new Button { Text = "Cancel", Width = 100, Height = 32, Dock = DockStyle.Right };
        save.Click += (_, _) =>
        {
            if (_isLocked())   // a match may have started while the window was open
            {
                MessageBox.Show(this, "Strict mode: a match is running - settings cannot change until it ends.", "cs2-debuffs");
                return;
            }
            Apply();
            Close();
        };
        cancel.Click += (_, _) => Close();
        var uninstall = new Button { Text = "Uninstall mod...", Width = 130, Height = 32, Dock = DockStyle.Left, ForeColor = Color.Firebrick };
        uninstall.Click += (_, _) => { Close(); _onUninstall(); };
        bottom.Controls.Add(save);
        bottom.Controls.Add(new Label { Width = 10, Dock = DockStyle.Right });
        bottom.Controls.Add(cancel);
        bottom.Controls.Add(uninstall);
        Controls.Add(bottom);

        // the scrollable area
        var scroll = new Panel { Dock = DockStyle.Fill, AutoScroll = true, Padding = new Padding(10) };
        Controls.Add(scroll);
        var flow = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true, Width = 620 };
        scroll.Controls.Add(flow);

        // Strict mode during a match: the window is read-only (scrolling works, the controls do not).
        if (_isLocked())
        {
            var lockBar = new Label
            {
                Text = "Strict mode: a match is running. Settings, exit and uninstall are only available after it ends. You turned it on, now live with it.",
                Dock = DockStyle.Top, Height = 40, TextAlign = ContentAlignment.MiddleCenter,
                BackColor = Color.Firebrick, ForeColor = Color.White, Font = new Font(Font, FontStyle.Bold),
            };
            Controls.Add(lockBar);
            scroll.BringToFront();   // the Fill control must dock last, otherwise it slides under the banner
            flow.Enabled = false;
            save.Enabled = false;
            uninstall.Enabled = false;
        }

        flow.Controls.Add(BuildGeneral());
        foreach (var slot in _cfg.Slots)
            flow.Controls.Add(BuildSlot(slot));
        flow.Controls.Add(BuildStrengths());
        flow.Controls.Add(BuildConflicts());

        // Stretch the content with the window: otherwise the right column got clipped when resizing.
        void FitWidth()
        {
            int w = Math.Max(600, scroll.ClientSize.Width - scroll.Padding.Horizontal - 4);
            if (flow.Width == w) return;
            flow.Width = w;
            foreach (Control c in flow.Controls)
                c.MinimumSize = new Size(w - c.Margin.Horizontal, 0);
        }
        scroll.Resize += (_, _) => FitWidth();
        FitWidth();
    }

    private GroupBox BuildConflicts()
    {
        var g = new GroupBox { Text = "Conflicts - which debuffs never roll together", AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, MinimumSize = new Size(600, 0), Padding = new Padding(10) };
        var stack = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true, Dock = DockStyle.Top };

        // the current pairs from the config (unique, undirected)
        foreach (var slot in _cfg.Slots)
            foreach (var o in slot.Options)
                foreach (var c in o.Conflicts)
                    AddPair(o.Id, c);

        _pairsList = new ListBox { Width = 460, Height = 130 };
        RefreshPairsList();
        stack.Controls.Add(_pairsList);

        var ids = _cfg.Slots.SelectMany(s => s.Options).Select(o => o.Id).ToArray();
        _pairA = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 170 };
        _pairB = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 170 };
        foreach (var id in ids) { _pairA.Items.Add(new EffectItem(id)); _pairB.Items.Add(new EffectItem(id)); }

        var addBtn = new Button { Text = "Block pair", AutoSize = true };
        addBtn.Click += (_, _) =>
        {
            if (_pairA.SelectedItem is EffectItem a && _pairB.SelectedItem is EffectItem b && a.Id != b.Id)
            {
                AddPair(a.Id, b.Id);
                RefreshPairsList();
            }
        };
        var delBtn = new Button { Text = "Allow selected", AutoSize = true };
        delBtn.Click += (_, _) =>
        {
            int i = _pairsList.SelectedIndex;
            if (i >= 0 && i < _pairs.Count) { _pairs.RemoveAt(i); RefreshPairsList(); }
        };

        var row = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight, WrapContents = false, Margin = new Padding(0, 6, 0, 0) };
        row.Controls.Add(_pairA);
        row.Controls.Add(new Label { Text = "↔", AutoSize = true, Padding = new Padding(2, 6, 2, 0) });
        row.Controls.Add(_pairB);
        row.Controls.Add(addBtn);
        stack.Controls.Add(row);
        stack.Controls.Add(delBtn);
        stack.Controls.Add(new Label
        {
            Text = "Slot rules (max 2 audio/color, \"no sound\" closes the audio slot,\nthe trip blocks color) are not here - they live in debuffs.json.",
            AutoSize = true, ForeColor = SystemColors.GrayText, Margin = new Padding(0, 6, 0, 0),
        });

        g.Controls.Add(stack);
        return g;
    }

    private void AddPair(string x, string y)
    {
        var p = string.CompareOrdinal(x, y) <= 0 ? (x, y) : (y, x);
        if (!_pairs.Contains(p)) _pairs.Add(p);
    }

    private void RefreshPairsList()
    {
        _pairsList.BeginUpdate();
        _pairsList.Items.Clear();
        foreach (var (a, b) in _pairs)
            _pairsList.Items.Add($"{ComboBanner.Label(a)}  ↔  {ComboBanner.Label(b)}");
        _pairsList.EndUpdate();
    }

    private GroupBox BuildStrengths()
    {
        var g = new GroupBox { Text = "Effect strength (applies after the app restarts)", AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, MinimumSize = new Size(600, 0), Padding = new Padding(10) };
        var t = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Dock = DockStyle.Top };
        t.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 340));
        t.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        foreach (var s in Strengths)
        {
            decimal cur = (decimal)_cfg.GetParam(s.Id, s.Param, (double)s.Min);
            var spin = NumUpDown(s.Min, s.Max, s.Step, s.Dec, cur);
            AddRow(t, s.Label, spin);
            _strengths.Add((s.Id, s.Param, spin));
        }

        g.Controls.Add(t);
        return g;
    }

    private GroupBox BuildGeneral()
    {
        var g = new GroupBox { Text = "General", AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, MinimumSize = new Size(600, 0), Padding = new Padding(10) };
        var t = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Dock = DockStyle.Top };
        t.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 300));
        t.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        _enabledCheck = Check(_cfg.General.Enabled);
        AddRow(t, "Mod enabled (debuffs are applied)", _enabledCheck);

        _mult = NumUpDown(0, 3, 0.05m, 2, (decimal)_cfg.General.GlobalChanceMultiplier);
        AddRow(t, "Overall difficulty (chance multiplier)", _mult);

        _banner = Check(_cfg.General.ShowComboBanner);
        var bannerLbl = AddRow(t, "Combo banner at the start of a match", _banner);

        _bannerRound = Check(_cfg.General.BannerOnRoundStart);
        var bannerRoundLbl = AddRow(t, "Banner at the start of every round", _bannerRound);

        _fadeIn = NumUpDown(0, 3000, 10, 0, _cfg.General.FadeInMs);
        AddRow(t, "Fade-in (ms)", _fadeIn);
        _fadeOut = NumUpDown(0, 3000, 10, 0, _cfg.General.FadeOutMs);
        AddRow(t, "Fade-out (ms)", _fadeOut);

        _toggleKeyName = _cfg.General.ToggleHotkey ?? "";
        _toggleKeyLabel = new Label { Text = KeyDisplay(_toggleKeyName), AutoSize = true, Padding = new Padding(0, 6, 8, 0) };
        var assign = new Button { Text = "Assign", AutoSize = true };
        var clear = new Button { Text = "Off", AutoSize = true };
        assign.Click += (_, _) =>
        {
            _capturingKey = true;
            _toggleKeyLabel.Text = "press a key... (Esc cancels)";
            Focus();
        };
        clear.Click += (_, _) => { _capturingKey = false; _toggleKeyName = ""; _toggleKeyLabel.Text = KeyDisplay(""); SyncBindCommand(); };
        var keyRow = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight, WrapContents = false, Margin = new Padding(0) };
        keyRow.Controls.Add(_toggleKeyLabel);
        keyRow.Controls.Add(assign);
        keyRow.Controls.Add(clear);
        AddRow(t, "Toggle key: apply/remove ALL debuffs", keyRow);

        // The ready-made CS2 console command: without it the game will not apply the HUD debuffs.
        _bindBox = new TextBox { ReadOnly = true, Width = 196, Font = new Font("Consolas", 9f) };
        var copyBtn = new Button { Text = "Copy", AutoSize = true };
        copyBtn.Click += (_, _) =>
        {
            if (_bindBox.Text.Length == 0) return;
            try { Clipboard.SetText(_bindBox.Text); copyBtn.Text = "Copied"; } catch { }
        };
        var bindRow = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight, WrapContents = false, Margin = new Padding(0) };
        bindRow.Controls.Add(_bindBox);
        bindRow.Controls.Add(copyBtn);
        AddRow(t, "Paste this into the CS2 console (once):\notherwise the game will not apply scoreboard/radar/killfeed/crosshair", bindRow);
        SyncBindCommand();

        _apoDir = new TextBox { Text = _cfg.General.ApoConfigDir ?? "", Width = 200 };
        var apoBrowse = new Button { Text = "Browse...", AutoSize = true };
        apoBrowse.Click += (_, _) =>
        {
            using var d = new FolderBrowserDialog
            {
                Description = "The Equalizer APO folder that holds config.txt",
                UseDescriptionForTitle = true,
            };
            if (Directory.Exists(_apoDir.Text)) d.SelectedPath = _apoDir.Text;
            if (d.ShowDialog(this) == DialogResult.OK) _apoDir.Text = d.SelectedPath;
        };
        var apoHelp = new Button { Text = "How to install", AutoSize = true };
        apoHelp.Click += (_, _) => ShowApoHelp();
        _apoFound = new Label { AutoSize = true, ForeColor = SystemColors.GrayText, Margin = new Padding(0, 2, 0, 0) };
        _apoDir.TextChanged += (_, _) => SyncApoFound();
        var apoRow = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight, WrapContents = false, Margin = new Padding(0) };
        apoRow.Controls.Add(_apoDir);
        apoRow.Controls.Add(apoBrowse);
        apoRow.Controls.Add(apoHelp);
        AddRow(t, "Equalizer APO - the config.txt folder\n(empty = auto-detect; applies after a restart)", apoRow);
        AddRow(t, "", _apoFound);
        SyncApoFound();

        _faceitSafe = Check(_cfg.General.FaceitSafeMode);
        AddRow(t, "FACEIT-safe mode: no windows over the game\n(on by default; applies after a restart)", _faceitSafe);

        // In FACEIT-safe mode there are no banners at all, so their settings are greyed out and cannot look active.
        void SyncSafeMode()
        {
            bool overlaysAllowed = !_faceitSafe.Checked;
            SetRowEnabled(bannerLbl, _banner, overlaysAllowed);
            SetRowEnabled(bannerRoundLbl, _bannerRound, overlaysAllowed);
        }
        _faceitSafe.CheckedChanged += (_, _) => SyncSafeMode();
        SyncSafeMode();

        _strict = Check(_cfg.General.StrictMode);
        AddRow(t, "Strict mode: you turned it on, now live with it. Once applied,\nthe debuffs stay until the match ends, the app cannot be closed\nor killed (watchdog), settings are locked. 30 s to change your mind", _strict);
        _strict.CheckedChanged += (_, _) =>
        {
            if (_strict.Checked && !_cfg.General.StrictMode && Visible)
            {
                var r = MessageBox.Show(this, Program.StrictWarning, "cs2-debuffs - strict mode",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
                if (r != DialogResult.Yes) _strict.Checked = false;
            }
        };

        _exitWithCs2 = Check(_cfg.General.ExitWhenCs2Closes);
        AddRow(t, "Exit when CS2 closes", _exitWithCs2);

        _autostart = Check(Autostart.IsEnabled());
        AddRow(t, "Start with Windows", _autostart);

        g.Controls.Add(t);
        return g;
    }

    private GroupBox BuildSlot(SlotConfig slot)
    {
        string name = SlotNames.TryGetValue(slot.Name, out var n) ? n : slot.Name;
        var g = new GroupBox { Text = name, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, MinimumSize = new Size(600, 0), Padding = new Padding(10) };
        var t = new TableLayoutPanel { ColumnCount = 3, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Dock = DockStyle.Top };
        t.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 34));
        t.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 380));
        t.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        // Debuffs unavailable on this machine (no Equalizer APO, or the CS2 folder was not found): the whole
        // row gets a grey background plus the reason. The roulette skips them anyway - the window must not
        // pretend they work.
        var grayRows = new HashSet<int>();
        t.CellPaint += (_, e) =>
        {
            if (!grayRows.Contains(e.Row)) return;
            using var b = new SolidBrush(UnavailableBack);
            e.Graphics.FillRectangle(b, e.CellBounds);
        };

        foreach (var o in slot.Options)
        {
            bool available = _isAvailable(o.Id);
            var on = Check(o.Enabled);
            var chance = NumUpDown(0, 100, 1, 0, (decimal)Math.Round(o.Chance * 100));
            var lbl = new Label
            {
                Text = available ? ComboBanner.Label(o.Id) : $"{ComboBanner.Label(o.Id)}  - {UnavailableReason(o)}",
                AutoSize = true, Anchor = AnchorStyles.Left, Padding = new Padding(0, 6, 0, 0),
                BackColor = Color.Transparent,
            };
            var pct = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight, Margin = new Padding(0), BackColor = Color.Transparent };
            pct.Controls.Add(chance);
            pct.Controls.Add(new Label { Text = "%", AutoSize = true, Padding = new Padding(2, 6, 0, 0), BackColor = Color.Transparent });
            on.BackColor = Color.Transparent;

            int row = t.RowCount;
            t.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            t.Controls.Add(on, 0, row);
            t.Controls.Add(lbl, 1, row);
            t.Controls.Add(pct, 2, row);
            t.RowCount++;

            if (!available)
            {
                grayRows.Add(row);
                on.Enabled = chance.Enabled = false;   // the values are not lost, they just have no effect here
                lbl.ForeColor = SystemColors.GrayText;
            }

            _opts[o] = (on, chance);
        }

        g.Controls.Add(t);
        return g;
    }

    private void Apply()
    {
        // The roulette reads the config on the GSI thread (rolling a new combo), so we edit it under the
        // shared lock - otherwise a save exactly at match start hits "collection was modified".
        lock (_cfgLock) ApplyLocked();
    }

    private void ApplyLocked()
    {
        _cfg.General.Enabled = _enabledCheck.Checked;
        _cfg.General.GlobalChanceMultiplier = (double)_mult.Value;
        _cfg.General.ShowComboBanner = _banner.Checked;
        _cfg.General.BannerOnRoundStart = _bannerRound.Checked;
        _cfg.General.FadeInMs = (int)_fadeIn.Value;
        _cfg.General.FadeOutMs = (int)_fadeOut.Value;
        _cfg.General.ToggleHotkey = (_toggleKeyName ?? "").Trim();
        _cfg.General.ApoConfigDir = _apoDir.Text.Trim();
        _cfg.General.FaceitSafeMode = _faceitSafe.Checked;
        _cfg.General.StrictMode = _strict.Checked;
        _cfg.General.ExitWhenCs2Closes = _exitWithCs2.Checked;

        foreach (var (o, ctl) in _opts)
        {
            o.Enabled = ctl.On.Checked;
            o.Chance = (double)ctl.Chance.Value / 100.0;
        }

        foreach (var (id, param, ctl) in _strengths)
            _cfg.SetParam(id, param, (double)ctl.Value);

        // conflicts: rewrite the conflicts from the pair list (both directions - explicit and readable)
        var byId = _cfg.Slots.SelectMany(s => s.Options).ToDictionary(o => o.Id);
        foreach (var o in byId.Values) o.Conflicts.Clear();
        foreach (var (a, b) in _pairs)
        {
            if (!byId.TryGetValue(a, out var oa) || !byId.TryGetValue(b, out var ob)) continue;
            oa.Conflicts.Add(b);
            ob.Conflicts.Add(a);
        }

        try
        {
            if (_autostart.Checked) Autostart.Enable(_exePath);
            else Autostart.Disable();
        }
        catch { }

        _onApply();
    }

    // ---------- small helpers ----------

    private static CheckBox Check(bool value) => new() { Checked = value, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 3, 0, 0) };

    private static NumericUpDown NumUpDown(decimal min, decimal max, decimal step, int decimals, decimal value) => new()
    {
        Minimum = min,
        Maximum = max,
        Increment = step,
        DecimalPlaces = decimals,
        Value = Math.Clamp(value, min, max),
        Width = 70,
        TextAlign = HorizontalAlignment.Right,
    };

    /// <summary>A "label | control" row. Returns the label so it can be greyed out along with the control.</summary>
    private static Label AddRow(TableLayoutPanel t, string label, Control ctl)
    {
        int row = t.RowCount;
        t.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var lbl = new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left, Padding = new Padding(0, 5, 0, 0) };
        t.Controls.Add(lbl, 0, row);
        t.Controls.Add(ctl, 1, row);
        t.RowCount++;
        return lbl;
    }

    /// <summary>Grey out a row (control plus label) when the setting has no effect.</summary>
    private static void SetRowEnabled(Label lbl, Control ctl, bool enabled)
    {
        ctl.Enabled = enabled;
        lbl.ForeColor = enabled ? SystemColors.ControlText : SystemColors.GrayText;
    }
}

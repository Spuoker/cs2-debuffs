namespace DebuffRoulette;

public enum MatchPhase { None, Warmup, Live, Halftime, Over }

/// <summary>What the latest fact from the game means.</summary>
public enum Transition
{
    Ignored,        // nothing new (the same phase again)
    NewMatch,       // a new match: roll a new combo
    Continue,       // the same match goes on (warmup -> live)
    SidesSwitched,  // sides swapped (halftime -> live)
    Halftime,       // went to halftime
    Return,         // came back to the same match (reconnect) - combo and state are kept
    MatchOver,      // the match is over
    Abandon,        // left to the menu mid-match - the combo is kept in case of a reconnect
    Unknown,        // an unfamiliar phase - leave the state alone, just log it
}

public enum KeyResult { Applied, Removed, EmptyCombo, NotInMatch, ModDisabled, StrictLocked, StrictHoldToWaive }

/// <summary>
/// The match state machine - the ONE place that decides what should be active right now.
/// Pure logic with no I/O: it takes facts (the GSI phase, going to the menu, the key) keeps state,
/// and answers two questions - are the debuffs applied, and what should debuff.cfg be loaded with.
///
/// ONE KEY FOR EVERYTHING. The HUD debuffs (scoreboard/radar/crosshair) can only be applied by CS2
/// itself via `exec debuff`, so in-game the exec is bound to keys you press every second anyway
/// (`bind w "+forward; exec debuff"` and so on). debuff.cfg always holds the machine's CURRENT
/// decision (WantCfgCombo), so any such exec simply pulls the game up to what the app already turned
/// on: press the key and the HUD catches up with the audio and color on your very first step.
/// </summary>
public sealed class MatchBrain
{
    private readonly Func<bool> _strict;
    private readonly Func<DateTime> _now;
    private DateTime _graceUntil = DateTime.MinValue;
    private bool _awaitFirstGsi;   // we came back and are waiting for the first GSI post: the phase is unknown, but the lock already holds

    /// <summary>The strict-mode grace period: for this long after applying, strict mode can still be dropped.</summary>
    public static readonly TimeSpan StrictGrace = TimeSpan.FromSeconds(30);

    /// <summary>How long the key must be held during the grace period to drop strict mode (a stray tap will not).</summary>
    public static readonly TimeSpan WaiveHold = TimeSpan.FromSeconds(2);

    /// <param name="strict">Strict mode: once applied, the debuffs stay until the match ends.</param>
    /// <param name="now">The clock (for the self-test); UTC by default.</param>
    public MatchBrain(Func<bool>? strict = null, Func<DateTime>? now = null)
    {
        _strict = strict ?? (() => false);
        _now = now ?? (() => DateTime.UtcNow);
    }

    public MatchPhase Phase { get; private set; } = MatchPhase.None;

    /// <summary>The map the current combo was rolled for.</summary>
    public string Map { get; private set; } = "";

    /// <summary>The roll came out empty (a clean match). Set by the host after the roulette runs.</summary>
    public bool ComboEmpty { get; set; }

    /// <summary>The debuffs were applied with the key: both the system ones (audio/color) and the in-game HUD ones.</summary>
    public bool Applied { get; private set; }

    /// <summary>Strict mode was dropped by holding the key during the grace period, for this match only. A new match is strict again.</summary>
    public bool Waived { get; private set; }

    /// <summary>Strict mode is in force for this match.</summary>
    public bool Strict => _strict() && !Waived;

    public bool InMatch => Phase is MatchPhase.Warmup or MatchPhase.Live or MatchPhase.Halftime;

    /// <summary>The first 30 s after applying: strict mode already holds, but it can still be dropped by holding the key.</summary>
    public bool InGrace => Strict && InMatch && Applied && _now() < _graceUntil;

    public TimeSpan GraceLeft => InGrace ? _graceUntil - _now() : TimeSpan.Zero;

    /// <summary>
    /// Strict mode is locked: the debuffs are applied and the grace period is over, so they stay until the
    /// match ends. Right after the process comes back the phase is still unknown (GSI is silent until its
    /// first post), but the lock already holds - otherwise those couple of seconds would be a
    /// "kill the process, slip out in time" hole.
    /// </summary>
    public bool Locked => Strict && Applied && (InMatch || _awaitFirstGsi) && !InGrace;

    /// <summary>Whether audio/color should be on right now. The host adds the window-focus check for the visuals itself.</summary>
    public bool WantEffects(bool enabled) => enabled && InMatch && Applied && !ComboEmpty;

    /// <summary>
    /// What debuff.cfg should hold: true means the cfg part of the combo, false means reset only. This is the
    /// CURRENT state, not a future one: every in-game `exec debuff` pulls the HUD up to it.
    /// Outside a match it is always the reset - so the first exec cleans up the previous match.
    /// </summary>
    public bool WantCfgCombo(bool enabled) => enabled && InMatch && Applied && !ComboEmpty;

    /// <summary>
    /// Coming back after the process was killed: the same combo, the same map and the same "applied" state
    /// (otherwise killing the process would be a way out of strict mode). Going to the menu is a different
    /// story: there "applied" is honestly cleared, and nothing comes back on its own.
    /// </summary>
    public void Restore(string map, bool comboEmpty, bool applied)
    {
        Map = map ?? "";
        ComboEmpty = comboEmpty;
        Phase = MatchPhase.None;
        Applied = applied;
        Waived = false;
        _graceUntil = DateTime.MinValue;   // coming back is not applying: no grace period (otherwise killing would be a loophole)
        _awaitFirstGsi = applied;
    }

    /// <summary>A fact from GSI: the match phase and the map (arrives when either changes).</summary>
    public Transition OnGsi(string rawPhase, string map)
    {
        MatchPhase? p = rawPhase switch
        {
            "warmup" => MatchPhase.Warmup,
            "live" => MatchPhase.Live,
            "intermission" => MatchPhase.Halftime,
            "gameover" => MatchPhase.Over,
            _ => null,
        };
        if (p is null) return Transition.Unknown;
        _awaitFirstGsi = false;

        map ??= "";
        bool sameMap = Map.Length > 0 && string.Equals(map, Map, StringComparison.OrdinalIgnoreCase);
        var prev = Phase;
        if (p == prev && sameMap) return Transition.Ignored;

        switch (p.Value)
        {
            case MatchPhase.Over:
                if (prev == MatchPhase.Over) return Transition.Ignored;
                Phase = MatchPhase.Over;         // keep the combo until the next match
                Applied = false;                 // drop the system ones; the HUD ones come back on the next press (the file holds the reset)
                return Transition.MatchOver;

            case MatchPhase.Warmup:
                // back in the warmup of the same match (a reconnect) - that is a return; anything else is a new match
                if (prev == MatchPhase.None && sameMap) { Phase = MatchPhase.Warmup; return Transition.Return; }
                return BeginNewMatch(map, MatchPhase.Warmup);

            case MatchPhase.Live:
                if (!sameMap) return BeginNewMatch(map, MatchPhase.Live);         // a different map / map rotation
                if (prev == MatchPhase.Over) return BeginNewMatch(map, MatchPhase.Live);  // duels/DM/arena: a restart with no warmup
                if (prev == MatchPhase.None) { Phase = MatchPhase.Live; return Transition.Return; }
                Phase = MatchPhase.Live;
                return prev == MatchPhase.Halftime ? Transition.SidesSwitched : Transition.Continue;

            case MatchPhase.Halftime:
                if (!sameMap || prev == MatchPhase.Over) return BeginNewMatch(map, MatchPhase.Halftime);
                bool back = prev == MatchPhase.None;   // a reconnect straight into halftime
                Phase = MatchPhase.Halftime;
                return back ? Transition.Return : Transition.Halftime;
        }
        return Transition.Unknown;
    }

    /// <summary>A fact: the player is in the main menu (or the game went silent and closed).</summary>
    public Transition OnMenu()
    {
        _awaitFirstGsi = false;
        if (!InMatch) return Transition.Ignored;
        Phase = MatchPhase.None;   // the combo and the map stay - there may be a reconnect
        Applied = false;           // NOTHING turns itself on: once back, press the key again
        return Transition.Abandon;
    }

    /// <summary>
    /// A key press: apply or remove ALL debuffs. In strict mode they cannot be removed - during the grace
    /// period we hint "hold it", and after that it is a plain refusal.
    /// </summary>
    public KeyResult OnKey(bool enabled)
    {
        if (!enabled) return KeyResult.ModDisabled;
        if (!InMatch) return KeyResult.NotInMatch;
        if (ComboEmpty) return KeyResult.EmptyCombo;
        if (Strict && Applied) return InGrace ? KeyResult.StrictHoldToWaive : KeyResult.StrictLocked;

        Applied = !Applied;
        if (Applied) _graceUntil = _now() + StrictGrace;   // strict mode locks 30 s after applying
        return Applied ? KeyResult.Applied : KeyResult.Removed;
    }

    /// <summary>
    /// What to put into debuff_key.cfg - the file the NEXT key press will run:
    /// true means the cfg part of the combo (the press applies), false means the reset (the press removes).
    /// In strict mode nothing can be removed, so there it is always the combo.
    /// </summary>
    public bool KeyCfgCombo(bool enabled) => enabled && InMatch && !ComboEmpty && (Strict || !Applied);

    /// <summary>
    /// Strict mode was turned on while the debuffs were already applied: start the grace period now.
    /// true means the grace period started (there is something to remove).
    /// </summary>
    public bool StartGraceNow()
    {
        if (!Applied || !InMatch) return false;
        _graceUntil = _now() + StrictGrace;
        return true;
    }

    /// <summary>The key was held for WaiveHold during the grace period: strict mode is dropped for this match. true means dropped.</summary>
    public bool WaiveByHold()
    {
        if (!InGrace) return false;
        Waived = true;
        return true;
    }

    private Transition BeginNewMatch(string map, MatchPhase phase)
    {
        Map = map;
        Phase = phase;
        Applied = false;             // a new match - the debuffs wait for the key
        Waived = false;              // strict mode is in force again
        _graceUntil = DateTime.MinValue;
        return Transition.NewMatch;  // the host sets ComboEmpty after the roll
    }
}

/// <summary>
/// The state machine's self-test: `cs2-debuffs.exe --selftest`. It runs scenarios from real games through
/// MatchBrain with no effects, no windows and no writes to the CS2 folder - it touches nothing on the system.
/// </summary>
public static class MatchBrainSelfTest
{
    public static bool Run(TextWriter o)
    {
        int ok = 0, fail = 0;
        bool strict = false;
        var clock = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        MatchBrain Fresh(bool s = false) { strict = s; return new MatchBrain(() => strict, () => clock); }
        void Wait(double sec) => clock = clock.AddSeconds(sec);

        void Check(string name, bool cond)
        {
            if (cond) ok++; else { fail++; o.WriteLine($"  FAIL: {name}"); }
        }
        void Scenario(string title, Action body)
        {
            o.WriteLine($"- {title}");
            try { body(); }
            catch (Exception ex) { fail++; o.WriteLine($"  EXCEPTION: {ex.Message}"); }
        }

        Scenario("ordinary match: the key applies and removes everything, the end of the match removes it by itself", () =>
        {
            var b = Fresh();
            Check("warmup = new match", b.OnGsi("warmup", "de_dust2") == Transition.NewMatch);
            Check("clean before the key, and cfg holds the reset", !b.WantEffects(true) && !b.WantCfgCombo(true));
            Check("the key file holds the combo: a press will apply", b.KeyCfgCombo(true));
            Check("the key applies", b.OnKey(true) == KeyResult.Applied && b.WantEffects(true));
            Check("the combo is now in cfg - an in-game exec will pull the HUD along", b.WantCfgCombo(true));
            Check("and the key file holds the reset: the next press removes", !b.KeyCfgCombo(true));
            Check("warmup -> live = continue", b.OnGsi("live", "de_dust2") == Transition.Continue);
            Check("the key removes", b.OnKey(true) == KeyResult.Removed && !b.WantEffects(true));
            Check("and cfg holds the reset again", !b.WantCfgCombo(true));
            b.OnKey(true);
            Check("a side swap does not clear the applied state",
                b.OnGsi("intermission", "de_dust2") == Transition.Halftime
                && b.OnGsi("live", "de_dust2") == Transition.SidesSwitched && b.WantEffects(true));
            Check("the match is over", b.OnGsi("gameover", "de_dust2") == Transition.MatchOver);
            Check("after the end the system ones are off and cfg holds the reset (the first step brings the scoreboard back)", !b.WantEffects(true) && !b.WantCfgCombo(true));
            Check("a repeated gameover is ignored", b.OnGsi("gameover", "de_dust2") == Transition.Ignored);
        });

        Scenario("a new match does not carry the applied state over from the previous one", () =>
        {
            var b = Fresh();
            b.OnGsi("warmup", "de_mirage"); b.OnKey(true);
            b.OnGsi("gameover", "de_mirage");
            Check("new match = waiting for the key", b.OnGsi("warmup", "de_mirage") == Transition.NewMatch && !b.WantEffects(true) && !b.WantCfgCombo(true));
        });

        Scenario("duels/DM: gameover -> live with no warmup = new match", () =>
        {
            var b = Fresh();
            b.OnGsi("live", "duels_mirage"); b.ComboEmpty = true;
            Check("empty combo: the key honestly reports a clean match", b.OnKey(true) == KeyResult.EmptyCombo);
            Check("empty combo: cfg holds the reset", !b.WantCfgCombo(true) && !b.WantEffects(true));
            b.OnGsi("gameover", "duels_mirage");
            Check("a restart with no warmup = new match", b.OnGsi("live", "duels_mirage") == Transition.NewMatch);
            b.ComboEmpty = false;
            Check("the key works in the new match", b.OnKey(true) == KeyResult.Applied && b.WantEffects(true));
        });

        Scenario("leaving to the menu from any phase removes everything", () =>
        {
            foreach (var phases in new[] { new[] { "warmup" }, new[] { "warmup", "live" }, new[] { "warmup", "live", "intermission" } })
            {
                var b = Fresh();
                foreach (var ph in phases) b.OnGsi(ph, "de_mirage");
                b.OnKey(true);
                Check($"leaving from {phases[^1]} = abandoned match", b.OnMenu() == Transition.Abandon);
                Check($"after leaving from {phases[^1]} there are no effects and cfg holds the reset", !b.WantEffects(true) && !b.WantCfgCombo(true));
            }
            Check("the menu outside a match is ignored", Fresh().OnMenu() == Transition.Ignored);
        });

        Scenario("reconnect: the same combo and the same applied state", () =>
        {
            var b = Fresh();
            b.OnGsi("warmup", "de_inferno"); b.OnKey(true); b.OnGsi("live", "de_inferno"); b.OnMenu();
            Check("reconnect into live = return", b.OnGsi("live", "de_inferno") == Transition.Return);
            Check("after a return the debuffs are OFF - nothing turns itself on", !b.WantEffects(true) && !b.WantCfgCombo(true));
            Check("the same set, turned on with the key", b.OnKey(true) == KeyResult.Applied && b.WantEffects(true));

            b.OnGsi("intermission", "de_inferno"); b.OnMenu();
            Check("reconnect straight into halftime = return", b.OnGsi("intermission", "de_inferno") == Transition.Return);
            Check("then live = sides switched", b.OnGsi("live", "de_inferno") == Transition.SidesSwitched);

            var c = Fresh();
            c.OnGsi("warmup", "de_nuke"); c.OnMenu();
            Check("reconnect into the warmup of the same match = return, not a new combo", c.OnGsi("warmup", "de_nuke") == Transition.Return);

            var d = Fresh();
            d.OnGsi("warmup", "de_vertigo"); d.OnGsi("live", "de_vertigo");
            d.OnMenu(); d.OnGsi("live", "de_vertigo");
            Check("never applied - still clean after the reconnect", !d.WantEffects(true) && !d.WantCfgCombo(true));
        });

        Scenario("a map rotation in live on a community server = new match", () =>
        {
            var b = Fresh();
            b.OnGsi("live", "de_dust2"); b.OnKey(true);
            Check("a different map in live = new match", b.OnGsi("live", "de_mirage") == Transition.NewMatch);
            Check("and the debuffs wait for the key", !b.WantEffects(true));
        });

        Scenario("master toggle off: nothing happens", () =>
        {
            var b = Fresh();
            b.OnGsi("warmup", "de_cache");
            Check("no effects, cfg holds the reset", !b.WantEffects(false) && !b.WantCfgCombo(false));
            Check("the key reports that the mod is off", b.OnKey(false) == KeyResult.ModDisabled);
        });

        Scenario("the key outside a match, and an unfamiliar phase", () =>
        {
            var b = Fresh();
            Check("outside a match the key says not in a match", b.OnKey(true) == KeyResult.NotInMatch);
            b.OnGsi("warmup", "de_train");
            Check("an unfamiliar phase leaves the state alone", b.OnGsi("something_new", "de_train") == Transition.Unknown && b.Phase == MatchPhase.Warmup);
        });

        Scenario("strict mode: locks 30 s AFTER applying, releases at the end of the match", () =>
        {
            var b = Fresh(s: true);
            b.OnGsi("warmup", "de_mirage");
            Check("never applied - not locked, nothing to remove", !b.Locked && !b.InGrace);
            Check("the key applies", b.OnKey(true) == KeyResult.Applied && b.WantEffects(true));
            Check("strict mode: cfg holds the combo", b.WantCfgCombo(true));
            Check("strict mode: the key file holds the combo too - a press cannot remove it", b.KeyCfgCombo(true));
            Check("the first 30 s are the grace period", b.InGrace && !b.Locked);
            Check("a press during the grace period does not remove, it asks you to hold", b.OnKey(true) == KeyResult.StrictHoldToWaive && b.WantEffects(true));
            Wait(31);
            Check("locked once the grace period is over", b.Locked && !b.InGrace);
            Check("the key does not remove", b.OnKey(true) == KeyResult.StrictLocked && b.WantEffects(true));
            b.OnGsi("live", "de_mirage"); b.OnGsi("intermission", "de_mirage"); b.OnGsi("live", "de_mirage");
            Check("still locked after the side swap, and the debuffs are in place", b.Locked && b.WantEffects(true));
            b.OnGsi("gameover", "de_mirage");
            Check("the end of the match releases and removes", !b.Locked && !b.WantEffects(true));
        });

        Scenario("strict grace period: holding the key drops strict mode for this match", () =>
        {
            var b = Fresh(s: true);
            b.OnGsi("warmup", "de_inferno"); b.OnKey(true);
            Wait(10);
            Check("at second 10 a hold drops strict mode", b.WaiveByHold());
            Check("strict mode dropped: not locked, the debuffs are still applied", !b.Locked && !b.Strict && b.WantEffects(true));
            Wait(60);
            Check("and it does not lock after 30 s either", !b.Locked);
            Check("from then on the key works as usual", b.OnKey(true) == KeyResult.Removed && !b.WantEffects(true));
            Check("holding again does nothing", !b.WaiveByHold());
            b.OnGsi("gameover", "de_inferno"); b.OnGsi("warmup", "de_inferno");
            b.OnKey(true);
            Check("a new match - strict mode is in force again, with a grace period", b.Strict && b.InGrace);
        });

        Scenario("strict mode: killing the process does not remove the debuffs", () =>
        {
            var r = Fresh(s: true);                      // the process was killed and the watchdog started a new one
            r.Restore("de_dust2", comboEmpty: false, applied: true);
            Check("locked right after coming back, while GSI is still silent", r.Locked);
            r.OnGsi("live", "de_dust2");
            Check("the debuffs are on after coming back", r.WantEffects(true));
            Check("locked immediately, with no new grace period", r.Locked && !r.InGrace);
            Check("the key does not remove", r.OnKey(true) == KeyResult.StrictLocked);
        });

        Scenario("the process comes back mid-match (without strict mode)", () =>
        {
            var b = Fresh();
            b.Restore("de_overpass", comboEmpty: false, applied: true);
            Check("the first live post for the same map = return, same combo", b.OnGsi("live", "de_overpass") == Transition.Return);
            Check("the debuffs are in place", b.WantEffects(true));
            Check("and they can be removed", b.OnKey(true) == KeyResult.Removed);
        });

        o.WriteLine();
        o.WriteLine(fail == 0 ? $"SELFTEST GREEN: {ok} checks" : $"SELFTEST RED: {fail} failed, {ok} ok");
        return fail == 0;
    }
}

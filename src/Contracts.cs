using System.Text.Json;

namespace DebuffRoulette;

public interface ILog
{
    void Info(string msg);
    void Warn(string msg);
    void Error(string msg, Exception? ex = null);
}

/// <summary>
/// A system debuff (audio/display/overlay). Debuffs of kind "cfg" are handled by CfgWriter, not IEffect.
/// </summary>
public interface IEffect : IDisposable
{
    string Id { get; }

    /// <summary>false takes the debuff out of the roulette (no Equalizer APO, overlays not allowed, etc.)</summary>
    bool IsAvailable { get; }

    void Apply();

    /// <summary>Idempotent, and never throws.</summary>
    void Revert();
}

/// <summary>
/// An effect with a random parameter (e.g. the colorblind type): it is re-rolled once per combo
/// (when a new set is armed), not on every Apply - otherwise the type would change on every toggle.
/// </summary>
public interface IRerollable
{
    void Reroll();
}

// ---------------------- config (debuffs.json, snake_case) ----------------------

public sealed class AppConfig
{
    public GeneralConfig General { get; set; } = new();
    public List<SlotConfig> Slots { get; set; } = new();
    public Dictionary<string, JsonElement> Effects { get; set; } = new();
}

public sealed class GeneralConfig
{
    public int GsiPort { get; set; } = 3213;
    public int GsiSilenceRevertSeconds { get; set; } = 90;

    /// <summary>Global multiplier for every chance: 0.7 early on means more clean matches.</summary>
    public double GlobalChanceMultiplier { get; set; } = 1.0;

    /// <summary>Exit when CS2 closes (for the "lives alongside the game" model). By default it stays in the tray.</summary>
    public bool ExitWhenCs2Closes { get; set; } = false;

    /// <summary>Effect fade-in, ms. 0 means instant.</summary>
    public int FadeInMs { get; set; } = 220;

    /// <summary>Effect fade-out, ms. 0 means instant.</summary>
    public int FadeOutMs { get; set; } = 380;

    /// <summary>Show the on-screen banner listing the combo at the start of a match.</summary>
    public bool ShowComboBanner { get; set; } = true;

    /// <summary>Show the banner at the start of every round, not just every match.</summary>
    public bool BannerOnRoundStart { get; set; } = false;

    /// <summary>The mod is on. false means no debuffs are applied at all. Toggled from the tray, the settings window and the hotkey.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// ONE key for every debuff: apply/remove. The same key is bound inside CS2
    /// (bind &lt;key&gt; "exec debuff") - otherwise the game will not apply the HUD debuffs.
    /// Empty means off. A name from the Keys enum (End, F8, D5, A...).
    /// </summary>
    public string ToggleHotkey { get; set; } = "End";

    /// <summary>
    /// Manual path to Equalizer APO: the folder holding config.txt. Empty means we look it up in the
    /// registry ourselves (HKLM\SOFTWARE\EqualizerAPO\ConfigPath). Needed for portable or unusual installs.
    /// </summary>
    public string ApoConfigDir { get; set; } = "";

    /// <summary>
    /// Mode for FACEIT and other strict anti-cheats, ON BY DEFAULT (safe profile out of the box):
    /// we draw NO window over the game at all - no combo banner, no panel on Tab.
    /// The set is announced through a system notification instead. The debuffs themselves
    /// (audio/color/cfg) work as usual. Turn it off if you want the banner and the Tab panel.
    /// It guarantees nothing - their anti-cheat decides, not us.
    /// </summary>
    public bool FaceitSafeMode { get; set; } = true;

    /// <summary>
    /// Strict mode: "you turned it on, now live with it". Once the debuffs are applied with the key
    /// they stay until the match ends - you cannot remove them, turn the mod off, exit, uninstall,
    /// clear leftovers or change settings (the window becomes read-only). The first 30 s after applying
    /// are a grace period: holding the key for 2 s drops strict mode for this match. The watchdog pair
    /// brings a killed process back with the same combo and the same "applied" state. It releases when
    /// the match ends or you leave it. A pair of processes looks suspicious on FACEIT.
    /// </summary>
    public bool StrictMode { get; set; } = false;
}

public sealed class SlotConfig
{
    public string Name { get; set; } = "";

    /// <summary>"one_of": at most one, via a cumulative roll; "independent": every option is rolled on its own.</summary>
    public string Mode { get; set; } = "independent";

    public int MaxPicks { get; set; } = 99;

    /// <summary>Slots that get blocked if anything was rolled in this slot.</summary>
    public List<string> BlocksSlots { get; set; } = new();

    public List<OptionConfig> Options { get; set; } = new();
    public List<PullConfig> Pulls { get; set; } = new();
}

public sealed class OptionConfig
{
    public string Id { get; set; } = "";
    public double Chance { get; set; }

    /// <summary>Turned off by the user in settings: skipped by the roulette (the chance value is kept).</summary>
    public bool Enabled { get; set; } = true;

    public List<string> Conflicts { get; set; } = new();

    /// <summary>"system": an IEffect; "cfg": lines written into debuff.cfg.</summary>
    public string Kind { get; set; } = "system";

    /// <summary>If rolled, it takes the whole slot for itself (example: "no sound at all").</summary>
    public bool ClosesSlot { get; set; }
}

public sealed class PullConfig
{
    public string IfPicked { get; set; } = "";
    public string ThenId { get; set; } = "";
    public double Chance { get; set; }
}

public static class ConfigExtensions
{
    public static double GetParam(this AppConfig cfg, string effectId, string name, double def)
    {
        if (cfg.Effects.TryGetValue(effectId, out var el)
            && el.ValueKind == JsonValueKind.Object
            && el.TryGetProperty(name, out var v)
            && v.ValueKind == JsonValueKind.Number)
            return v.GetDouble();
        return def;
    }

    public static int GetParam(this AppConfig cfg, string effectId, string name, int def)
        => (int)Math.Round(cfg.GetParam(effectId, name, (double)def));

    /// <summary>Write one numeric effect parameter, keeping the effect's other parameters intact.</summary>
    public static void SetParam(this AppConfig cfg, string effectId, string name, double value)
    {
        var merged = new Dictionary<string, double>();
        if (cfg.Effects.TryGetValue(effectId, out var el) && el.ValueKind == JsonValueKind.Object)
            foreach (var p in el.EnumerateObject())
                if (p.Value.ValueKind == JsonValueKind.Number)
                    merged[p.Name] = p.Value.GetDouble();
        merged[name] = value;
        var json = JsonSerializer.Serialize(merged);
        cfg.Effects[effectId] = JsonSerializer.Deserialize<JsonElement>(json);
    }
}

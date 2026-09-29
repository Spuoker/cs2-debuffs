using System.Text;

namespace DebuffRoulette;

public sealed class CfgWriter
{
    public const string DebuffCfgName = "debuff.cfg";

    /// <summary>
    /// The file behind the toggle key: `bind END "exec debuff_key"`. It holds what the NEXT press
    /// should do - the game reads the file at the moment of the press, before the app can rewrite it.
    /// That is why the HUD changes right away instead of one press behind.
    /// </summary>
    public const string KeyCfgName = "debuff_key.cfg";
    public const string GsiCfgName = "gamestate_integration_debuffs.cfg";

    private readonly string? _cfgDir;
    private readonly ILog _log;

    // Every convar verified on CS2 (2026): no sv_cheats needed, they work in MM and on FACEIT.
    private static readonly Dictionary<string, (string[] Apply, string[] Reset)> Map = new()
    {
        ["no_voice"] = (
            new[] { "voice_modenable false" },
            new[] { "voice_modenable true" }),
        ["no_tab"] = (
            new[] { "unbind TAB" },
            new[] { "bind TAB \"+showscores\"" }),
        ["no_killfeed"] = (
            new[] { "cl_drawhud_force_deathnotices -1" },
            new[] { "cl_drawhud_force_deathnotices 0" }),
        ["no_radar"] = (
            new[] { "cl_drawhud_force_radar -1" },
            new[] { "cl_drawhud_force_radar 0" }),
        ["no_ids"] = (
            new[] { "cl_teamid_overhead_mode 0", "cl_drawhud_force_teamid_overhead -1", "hud_showtargetid false" },
            new[] { "cl_teamid_overhead_mode 3", "cl_drawhud_force_teamid_overhead 0", "hud_showtargetid true" }),
        ["no_crosshair"] = (
            new[] { "crosshair false" },
            new[] { "crosshair true" }),
    };

    public static bool IsCfgId(string id) => Map.ContainsKey(id);

    public CfgWriter(string? cfgDir, ILog log)
    {
        _cfgDir = cfgDir;
        _log = log;
    }

    public bool Available => _cfgDir != null;

    /// <summary>debuff.cfg: a full reset first, then the cfg part of the combo. Every exec cleans up the previous match.</summary>
    public void WriteCombo(Combo combo) { if (Available) Write(DebuffCfgName, ComboBody(combo)); }

    public void WriteResetOnly() { if (Available) Write(DebuffCfgName, ResetBody()); }

    /// <summary>debuff_key.cfg: what the next press of the toggle key will do.</summary>
    public void WriteKeyCombo(Combo combo) { if (Available) Write(KeyCfgName, ComboBody(combo)); }

    public void WriteKeyReset() { if (Available) Write(KeyCfgName, ResetBody()); }

    private static string ResetBody()
    {
        var sb = new StringBuilder();
        sb.AppendLine("// cs2-debuffs: full reset");
        AppendResets(sb);
        return sb.ToString();
    }

    private static string ComboBody(Combo combo)
    {
        var sb = new StringBuilder();
        sb.AppendLine("// cs2-debuffs: auto-generated, do not edit by hand");
        AppendResets(sb);
        foreach (var p in combo.Picks.Where(p => Map.ContainsKey(p.Id)))
            foreach (var l in Map[p.Id].Apply)
                sb.AppendLine(l);
        // no echo: keeps the exec bind from spamming the console (repeat presses are silently idempotent)
        return sb.ToString();
    }

    /// <summary>Deletes our files (debuff.cfg + the GSI config) from the CS2 cfg folder - used when uninstalling the mod.</summary>
    public void RemoveAllFiles()
    {
        if (_cfgDir is null) return;
        foreach (var name in new[] { DebuffCfgName, KeyCfgName, GsiCfgName })
        {
            try
            {
                var path = Path.Combine(_cfgDir, name);
                if (File.Exists(path)) { File.Delete(path); _log.Info($"deleted {name}"); }
            }
            catch (Exception ex) { _log.Error($"failed to delete {name}", ex); }
        }
    }

    /// <summary>
    /// Deletes only the GSI config. On uninstall we do NOT delete debuff.cfg and debuff_key.cfg:
    /// the player still has binds pointing at them in CS2, and if the files disappear the exec stops
    /// working, leaving scoreboard/radar/crosshair gone for good. We leave them in the "full reset" state.
    /// </summary>
    public void RemoveGsiOnly()
    {
        if (_cfgDir is null) return;
        try
        {
            var path = Path.Combine(_cfgDir, GsiCfgName);
            if (File.Exists(path)) { File.Delete(path); _log.Info($"deleted {GsiCfgName}"); }
        }
        catch (Exception ex) { _log.Error($"failed to delete {GsiCfgName}", ex); }
    }

    public void EnsureGsiFile(int port)
    {
        if (_cfgDir is null) return;
        var path = Path.Combine(_cfgDir, GsiCfgName);
        var body = $$"""
"cs2-debuffs"
{
    "uri" "http://127.0.0.1:{{port}}"
    "timeout" "5.0"
    "buffer" "0.1"
    "throttle" "0.3"
    "heartbeat" "3.0"
    "data"
    {
        "provider" "1"
        "map" "1"
        "round" "1"
        "player_id" "1"
    }
}
""";
        try
        {
            if (!File.Exists(path) || File.ReadAllText(path) != body)
            {
                File.WriteAllText(path, body);
                _log.Info($"GSI config written: {path} (restart CS2 if the game is running)");
            }
        }
        catch (Exception ex)
        {
            _log.Error("Failed to write the GSI config", ex);
        }
    }

    private static void AppendResets(StringBuilder sb)
    {
        foreach (var v in Map.Values)
            foreach (var l in v.Reset)
                sb.AppendLine(l);
    }

    private void Write(string name, string content)
    {
        try
        {
            File.WriteAllText(Path.Combine(_cfgDir!, name), content);
            _log.Info($"{name} updated");
        }
        catch (Exception ex)
        {
            _log.Error($"Failed to write {name}", ex);
        }
    }
}

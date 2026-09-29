using Microsoft.Win32;

namespace DebuffRoulette;

public static class SteamLocator
{
    /// <summary>Path to ...\Counter-Strike Global Offensive\game\csgo\cfg, or null if CS2 was not found.</summary>
    public static string? FindCs2CfgDir(ILog log)
    {
        try
        {
            var steamPath = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamPath", null) as string;
            if (string.IsNullOrEmpty(steamPath))
            {
                log.Warn("Steam not found in the registry (HKCU\\Software\\Valve\\Steam)");
                return null;
            }
            steamPath = steamPath.Replace('/', '\\');

            var libs = new List<string> { steamPath };
            var vdf = Path.Combine(steamPath, "steamapps", "libraryfolders.vdf");
            if (File.Exists(vdf))
            {
                foreach (var raw in File.ReadAllLines(vdf))
                {
                    var t = raw.Trim();
                    if (!t.StartsWith("\"path\"", StringComparison.OrdinalIgnoreCase)) continue;
                    var parts = t.Split('"', StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length >= 2)
                    {
                        var p = parts[^1].Replace(@"\\", @"\");
                        if (Directory.Exists(p)) libs.Add(p);
                    }
                }
            }

            foreach (var lib in libs.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                var cfg = Path.Combine(lib, "steamapps", "common",
                    "Counter-Strike Global Offensive", "game", "csgo", "cfg");
                if (Directory.Exists(cfg))
                {
                    log.Info($"CS2 cfg: {cfg}");
                    return cfg;
                }
            }

            log.Warn("CS2 not found in any Steam library - cfg debuffs disabled");
            return null;
        }
        catch (Exception ex)
        {
            log.Error("Failed to locate CS2", ex);
            return null;
        }
    }
}

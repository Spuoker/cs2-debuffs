using System.Text.Json;

namespace DebuffRoulette;

public static class ConfigLoader
{
    public static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        WriteIndented = true,
    };

    /// <summary>
    /// The default config is embedded in the exe: the release is a single file, and the app writes
    /// debuffs.json next to itself on first run. This also heals an accidentally deleted config.
    /// </summary>
    public static bool EnsureExists(string path)
    {
        if (File.Exists(path)) return false;
        using var src = typeof(ConfigLoader).Assembly.GetManifestResourceStream("debuffs.default.json")
                        ?? throw new InvalidDataException("the exe has no embedded default config");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var dst = File.Create(path);
        src.CopyTo(dst);
        return true;
    }

    public static AppConfig Load(string path)
    {
        var json = File.ReadAllText(path);
        var cfg = JsonSerializer.Deserialize<AppConfig>(json, JsonOpts)
                  ?? throw new InvalidDataException("debuffs.json: empty config");
        Validate(cfg);
        return cfg;
    }

    private static void Validate(AppConfig cfg)
    {
        var seen = new HashSet<string>();
        foreach (var slot in cfg.Slots)
        {
            if (slot.Mode is not ("one_of" or "independent"))
                throw new InvalidDataException($"slot '{slot.Name}': unknown mode '{slot.Mode}'");

            double oneOfSum = 0;
            foreach (var o in slot.Options)
            {
                if (string.IsNullOrWhiteSpace(o.Id))
                    throw new InvalidDataException($"slot '{slot.Name}': option without an id");
                if (!seen.Add(o.Id))
                    throw new InvalidDataException($"debuff '{o.Id}' appears twice in the config");
                if (o.Chance is < 0 or > 1)
                    throw new InvalidDataException($"'{o.Id}': chance must be within [0,1]");
                oneOfSum += o.Chance;
            }

            if (slot.Mode == "one_of" && oneOfSum > 1.0001)
                throw new InvalidDataException($"slot '{slot.Name}': one_of chances add up to more than 1");

            foreach (var p in slot.Pulls)
                if (slot.Options.All(o => o.Id != p.ThenId))
                    throw new InvalidDataException($"slot '{slot.Name}': pull points at unknown '{p.ThenId}'");
        }

        if (cfg.General.GlobalChanceMultiplier is < 0 or > 3)
            throw new InvalidDataException("global_chance_multiplier: sane range is [0,3]");
    }
}

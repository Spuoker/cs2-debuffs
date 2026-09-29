namespace DebuffRoulette;

public sealed record Combo(IReadOnlyList<OptionConfig> Picks)
{
    public static Combo Empty { get; } = new(Array.Empty<OptionConfig>());


    public override string ToString() =>
        Picks.Count == 0 ? "clean match" : string.Join(" + ", Picks.Select(p => p.Id));
}

public sealed class RouletteEngine
{
    private readonly AppConfig _cfg;
    private readonly Func<string, bool> _available;

    public RouletteEngine(AppConfig cfg, Func<string, bool> available)
    {
        _cfg = cfg;
        _available = available;
    }

    public Combo Roll(Random rng)
    {
        double mult = _cfg.General.GlobalChanceMultiplier;
        var picks = new List<OptionConfig>();
        var blockedSlots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var slot in _cfg.Slots)
        {
            if (blockedSlots.Contains(slot.Name)) continue;

            var avail = slot.Options.Where(o => o.Enabled && _available(o.Id)).ToList();
            var slotPicks = new List<OptionConfig>();

            if (slot.Mode == "one_of")
            {
                double r = rng.NextDouble();
                double acc = 0;
                foreach (var o in avail)
                {
                    acc += Chance(o.Chance, mult);
                    if (r < acc) { slotPicks.Add(o); break; }
                }
            }
            else // independent: the option order in the config is the priority order here
            {
                foreach (var o in avail)
                {
                    if (rng.NextDouble() >= Chance(o.Chance, mult)) continue;
                    if (o.ClosesSlot)
                    {
                        slotPicks.Clear();
                        slotPicks.Add(o);
                        break;
                    }
                    if (slotPicks.Count >= slot.MaxPicks) continue;
                    if (slotPicks.Any(p => ConflictsWith(p, o))) continue;
                    slotPicks.Add(o);
                }
            }

            // pulls: "if X was rolled, also try Y with chance Z" (on top of Y's own chance)
            if (!slotPicks.Any(p => p.ClosesSlot))
            {
                foreach (var pull in slot.Pulls)
                {
                    if (!slotPicks.Any(p => p.Id == pull.IfPicked)) continue;
                    if (slotPicks.Any(p => p.Id == pull.ThenId)) continue;
                    var target = avail.FirstOrDefault(o => o.Id == pull.ThenId);
                    if (target is null) continue;
                    if (slotPicks.Any(p => ConflictsWith(p, target))) continue;
                    if (rng.NextDouble() < Chance(pull.Chance, mult)) slotPicks.Add(target);
                }
            }

            if (slotPicks.Count > 0)
                foreach (var b in slot.BlocksSlots)
                    blockedSlots.Add(b);

            picks.AddRange(slotPicks);
        }

        // Cross-slot conflict resolution (e.g. no_sound vs no_radar - otherwise zero positional info):
        // on a conflict keep the one from the earlier slot and drop the later one.
        var final = new List<OptionConfig>();
        foreach (var p in picks)
            if (!final.Any(k => ConflictsWith(k, p)))
                final.Add(p);

        return new Combo(final);
    }

    private static double Chance(double c, double mult) => Math.Clamp(c * mult, 0, 1);

    private static bool ConflictsWith(OptionConfig a, OptionConfig b) =>
        a.Conflicts.Contains(b.Id) || b.Conflicts.Contains(a.Id);
}

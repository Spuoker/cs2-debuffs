using System.Text.Json;

namespace DebuffRoulette;

/// <summary>Journal: a jsonl file next to the exe. Use it later to correlate debuffs with results.</summary>
public sealed class MatchJournal
{
    private readonly string _path;
    private readonly object _lock = new();

    public MatchJournal(string path) => _path = path;

    public void ComboArmed(Combo combo) => Append(new
    {
        @event = "combo",
        ts = DateTime.Now,
        debuffs = combo.Picks.Select(p => p.Id).ToArray(),
    });

    public void MatchEnded(MatchEnd end, Combo combo) => Append(new
    {
        @event = "match",
        ts = DateTime.Now,
        map = end.Map,
        score_ct = end.ScoreCt,
        score_t = end.ScoreT,
        player_team = end.PlayerTeam,
        debuffs = combo.Picks.Select(p => p.Id).ToArray(),
    });

    public void Note(string text) => Append(new { @event = "note", ts = DateTime.Now, text });

    private void Append(object o)
    {
        try
        {
            var line = JsonSerializer.Serialize(o);
            lock (_lock) File.AppendAllText(_path, line + Environment.NewLine);
        }
        catch
        {
            // the journal must never crash the app
        }
    }
}

using System.Net;
using System.Text.Json;

namespace DebuffRoulette;

public sealed record MatchEnd(string Map, int ScoreCt, int ScoreT, string? PlayerTeam);

/// <summary>
/// Game State Integration receiver: CS2 POSTs JSON to localhost on its own.
/// It only REPORTS facts - match phase and map, returning to the menu, round start. What those facts
/// mean (a new match, a side swap, a reconnect...) is decided by MatchBrain, not by this class.
/// </summary>
public sealed class GsiServer : IDisposable
{
    private readonly HttpListener _listener = new();
    private readonly ILog _log;
    private Thread? _thread;
    private volatile bool _stop;
    private string _phase = "";
    private string _roundPhase = "";
    private string _mapName = "";
    private int _menuStreak;

    /// <summary>Phase or map changed: (raw phase, map, score - only on gameover).</summary>
    public event Action<string, string, MatchEnd?>? PhaseChanged;

    /// <summary>The player is in the main menu (two posts in a row with no map).</summary>
    public event Action? Menu;

    /// <summary>A round has started (freezetime).</summary>
    public event Action? RoundStart;

    public DateTime LastPostUtc { get; private set; } = DateTime.MinValue;

    public GsiServer(int port, ILog log)
    {
        _log = log;
        _listener.Prefixes.Add($"http://127.0.0.1:{port}/");
    }

    public void Start()
    {
        _listener.Start();
        _thread = new Thread(Loop) { IsBackground = true, Name = "gsi" };
        _thread.Start();
        _log.Info($"GSI server listening on {_listener.Prefixes.First()}");
    }

    /// <summary>Forget the tracked state: the next post with a map arrives as a fresh fact.</summary>
    public void ResetPhase()
    {
        _phase = "";
        _roundPhase = "";
        _mapName = "";
    }

    private void Loop()
    {
        while (!_stop)
        {
            HttpListenerContext ctx;
            try { ctx = _listener.GetContext(); }
            catch { break; }

            try { Handle(ctx); }
            catch (Exception ex) { _log.Error("GSI: failed to handle a post", ex); }
            finally { try { ctx.Response.Close(); } catch { } }
        }
    }

    private void Handle(HttpListenerContext ctx)
    {
        if (ctx.Request.HttpMethod != "POST")
        {
            ctx.Response.StatusCode = 405;
            return;
        }

        string body;
        using (var sr = new StreamReader(ctx.Request.InputStream, ctx.Request.ContentEncoding))
            body = sr.ReadToEnd();

        LastPostUtc = DateTime.UtcNow;
        ctx.Response.StatusCode = 200;

        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;

        if (!root.TryGetProperty("map", out var map))
        {
            // main menu / lobby: report once, on the second consecutive post without a map
            if (++_menuStreak == 2)
            {
                ResetPhase();
                _log.Info("GSI: player is in the menu");
                Menu?.Invoke();
            }
            return;
        }

        _menuStreak = 0;
        string phase = map.TryGetProperty("phase", out var ph) ? ph.GetString() ?? "" : "";
        string name = map.TryGetProperty("name", out var nm) ? nm.GetString() ?? "?" : "?";
        bool mapChanged = !string.Equals(name, _mapName, StringComparison.OrdinalIgnoreCase);

        if (phase != _phase || mapChanged)
        {
            _log.Info($"GSI: {name}, phase '{_phase}' -> '{phase}'");
            _phase = phase;
            _mapName = name;
            MatchEnd? end = null;
            if (phase == "gameover")
            {
                string? team = root.TryGetProperty("player", out var pl) && pl.TryGetProperty("team", out var tm)
                    ? tm.GetString() : null;
                end = new MatchEnd(name, ReadScore(map, "team_ct"), ReadScore(map, "team_t"), team);
            }
            PhaseChanged?.Invoke(phase, name, end);
        }

        if (root.TryGetProperty("round", out var round) && round.TryGetProperty("phase", out var rph))
        {
            string rp = rph.GetString() ?? "";
            if (rp != _roundPhase)
            {
                _roundPhase = rp;
                if (rp == "freezetime") RoundStart?.Invoke();
            }
        }
    }

    private static int ReadScore(JsonElement map, string team) =>
        map.TryGetProperty(team, out var t) && t.TryGetProperty("score", out var s) && s.TryGetInt32(out var v)
            ? v
            : 0;

    public void Dispose()
    {
        _stop = true;
        try { _listener.Stop(); } catch { }
        try { _listener.Close(); } catch { }
    }
}

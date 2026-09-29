using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using System.Text;
using Microsoft.Win32;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace DebuffRoulette;

/// <summary>
/// Audio debuffs: muting cs2, effects through Equalizer APO (mono/bass_boost/muffle), pink noise.
/// All of them fade in and out (fade_in_ms/fade_out_ms).
/// </summary>
public static class AudioModule
{
    public static IReadOnlyList<IEffect> Create(AppConfig cfg, ILog log)
    {
        int fin = Math.Max(0, cfg.General.FadeInMs);
        int fout = Math.Max(0, cfg.General.FadeOutMs);

        // manual Equalizer APO path (for portable or unusual installs) - takes priority over the registry
        ApoManager.ManualDir = cfg.General.ApoConfigDir;
        if (!string.IsNullOrWhiteSpace(cfg.General.ApoConfigDir))
            log.Info(ApoManager.IsApoDir(cfg.General.ApoConfigDir)
                ? $"Equalizer APO: manual path -> {cfg.General.ApoConfigDir}"
                : $"Equalizer APO: the given folder is no good (no config.txt): {cfg.General.ApoConfigDir} - falling back to the registry");

        var apo = new ApoManager(cfg, log);
        return new IEffect[]
        {
            new NoSoundEffect(log, fin, fout),
            new ApoEffect("mono", apo),
            new ApoEffect("bass_boost", apo),
            new ApoEffect("muffle", apo),
            new WhiteNoiseEffect(cfg, log, fin, fout),
        };
    }

    /// <summary>
    /// Clears leftovers from an abnormal exit (killed via Task Manager):
    /// restores the cs2 volume and empties the Equalizer APO file. Needs nothing, throws nothing.
    /// </summary>
    public static void CleanupLeftovers(ILog log)
    {
        try
        {
            int restored = NoSoundEffect.SetVolumeForCs2(1f);
            if (restored > 0) log.Info($"self-heal: restored the volume of {restored} cs2 sessions");
        }
        catch (Exception ex) { log.Warn($"self-heal: could not restore audio: {ex.Message}"); }

        try
        {
            if (ApoManager.TryFindConfigDir(out var dir))
            {
                var own = Path.Combine(dir, "cs2-debuffs.txt");
                if (File.Exists(own) && new FileInfo(own).Length > 0)
                {
                    File.WriteAllText(own, "");
                    log.Info("self-heal: the Equalizer APO file was emptied");
                }
            }
        }
        catch (Exception ex) { log.Warn($"self-heal: could not clear APO: {ex.Message}"); }
    }

    /// <summary>
    /// Uninstall: remove our Include line from the Equalizer APO config.txt and delete our own file.
    /// Without this, "Uninstall mod" would leave a permanent reference to our file in someone else's program.
    /// </summary>
    public static void RemoveApoTraces(ILog log)
    {
        try
        {
            if (!ApoManager.TryFindConfigDir(out var dir)) return;

            var configTxt = Path.Combine(dir, "config.txt");
            if (File.Exists(configTxt))
            {
                var kept = File.ReadAllLines(configTxt)
                    .Where(l => !string.Equals(l.Trim(), ApoManager.IncludeLine, StringComparison.OrdinalIgnoreCase))
                    .ToArray();
                File.WriteAllLines(configTxt, kept);
                log.Info("Equalizer APO: the Include line was removed from config.txt");
            }

            var own = Path.Combine(dir, ApoManager.OwnFileName);
            if (File.Exists(own)) { File.Delete(own); log.Info("Equalizer APO: our file was deleted"); }
        }
        catch (Exception ex) { log.Warn($"could not remove the Equalizer APO traces: {ex.Message}"); }
    }

    // ---------------------- no_sound ----------------------

    /// <summary>
    /// Smooth game mute: the volume of the cs2 sessions goes 1 -> 0 over fade_in_ms and back over fade_out_ms.
    /// While muted, an occasional rescan picks up new sessions (map change, device reconnect).
    /// </summary>
    private sealed class NoSoundEffect : IEffect
    {
        private const string ProcessName = "cs2";
        private const int RampIntervalMs = 40;
        private const int HoldIntervalMs = 1500;

        private readonly ILog _log;
        private readonly int _fadeInMs;
        private readonly int _fadeOutMs;
        private readonly object _sync = new();
        private System.Threading.Timer? _timer;
        private float _cur = 1f;      // current cs2 volume (1 normal .. 0 silent)
        private float _target = 1f;
        private long _lastTick;
        private bool _scanErrorLogged;

        public NoSoundEffect(ILog log, int fadeInMs, int fadeOutMs)
        {
            _log = log;
            _fadeInMs = fadeInMs;
            _fadeOutMs = fadeOutMs;
        }

        public string Id => "no_sound";
        public bool IsAvailable => true;

        public void Apply()
        {
            lock (_sync)
            {
                _target = 0f;
                _scanErrorLogged = false;
                _lastTick = Environment.TickCount64;
                KickTimer();
                _log.Info($"no_sound: fading cs2 out over {_fadeInMs} ms");
            }
        }

        public void Revert()
        {
            lock (_sync)
            {
                if (_target >= 1f && _timer == null) return;
                _target = 1f;
                _lastTick = Environment.TickCount64;
                KickTimer();
            }
        }

        public void Dispose()
        {
            lock (_sync)
            {
                _target = 1f;
                _cur = 1f;
                try { SetVolumeForCs2(1f); } catch { }
                _timer?.Dispose();
                _timer = null;
            }
        }

        private void KickTimer()
        {
            _timer ??= new System.Threading.Timer(OnTick);
            _timer.Change(0, RampIntervalMs);
        }

        private void OnTick(object? state)
        {
            if (!Monitor.TryEnter(_sync)) return;
            try
            {
                long now = Environment.TickCount64;
                long dt = Math.Max(0, now - _lastTick);
                _lastTick = now;

                if (_cur != _target)
                {
                    bool down = _target < _cur;
                    int dur = down ? _fadeInMs : _fadeOutMs; // muting is the fade-in, restoring is the fade-out
                    if (dur <= 0) _cur = _target;
                    else _cur = down
                        ? Math.Max(_target, _cur - (float)dt / dur)
                        : Math.Min(_target, _cur + (float)dt / dur);
                }

                SetVolumeForCs2(_cur);

                if (_cur == _target)
                {
                    if (_target >= 1f) { _timer?.Dispose(); _timer = null; } // audio is back - no timer needed
                    else _timer?.Change(HoldIntervalMs, HoldIntervalMs);      // muted: rescan occasionally for new sessions
                }
            }
            catch (Exception ex)
            {
                if (!_scanErrorLogged) { _scanErrorLogged = true; _log.Warn($"no_sound: rescan failed: {ex.Message}"); }
            }
            finally { Monitor.Exit(_sync); }
        }

        /// <summary>A fresh walk over the render devices; sets the volume on every cs2 session (and clears Mute).</summary>
        internal static int SetVolumeForCs2(float vol)
        {
            var pids = new HashSet<uint>();
            foreach (var p in Process.GetProcessesByName(ProcessName))
                using (p) pids.Add((uint)p.Id);
            if (pids.Count == 0) return 0;

            int touched = 0;
            using var enumerator = new MMDeviceEnumerator();
            foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
            {
                using (device)
                {
                    try
                    {
                        var manager = device.AudioSessionManager;
                        manager.RefreshSessions();
                        var sessions = manager.Sessions;
                        for (int i = 0; i < sessions.Count; i++)
                        {
                            var session = sessions[i];
                            try
                            {
                                if (pids.Contains(session.GetProcessID))
                                {
                                    var sav = session.SimpleAudioVolume;
                                    sav.Mute = false;
                                    sav.Volume = vol;
                                    touched++;
                                }
                            }
                            finally { session.Dispose(); }
                        }
                    }
                    catch
                    {
                        // One broken device (a virtual cable and such) must not get in the way of the rest.
                    }
                }
            }
            return touched;
        }
    }

    // ---------------------- mono / bass_boost / muffle (Equalizer APO) ----------------------

    /// <summary>A thin wrapper: the effect itself just switches an id on or off in the shared ApoManager.</summary>
    private sealed class ApoEffect : IEffect
    {
        private readonly ApoManager _manager;

        public ApoEffect(string id, ApoManager manager)
        {
            Id = id;
            _manager = manager;
        }

        public string Id { get; }
        public bool IsAvailable => _manager.IsAvailable;
        public void Apply() => _manager.Enable(Id);
        public void Revert() => _manager.Disable(Id);          // fades out; Disable never throws
        public void Dispose() => _manager.DisableImmediate(Id); // on exit: right away, no fade
    }

    /// <summary>
    /// The shared Equalizer APO manager. It keeps the set of active ids and one overall "strength" 0..1
    /// that it ramps toward the target, rewriting cs2-debuffs.txt on every step - APO picks up a file save
    /// on the fly. Strength 1 means the effects at full; when everything is switched off it fades to 0 and
    /// leaves an empty file.
    /// </summary>
    internal sealed class ApoManager
    {
        internal const string IncludeLine = "Include: cs2-debuffs.txt";
        internal const string OwnFileName = "cs2-debuffs.txt";
        private const int RampIntervalMs = 30; // every step reloads the APO config; more often = smoother

        private readonly AppConfig _cfg;
        private readonly ILog _log;
        private readonly int _fadeInMs;
        private readonly int _fadeOutMs;
        private readonly object _sync = new();
        private readonly HashSet<string> _active = new(StringComparer.Ordinal);
        private string? _cachedDir;
        private bool _includeEnsured;

        private System.Threading.Timer? _timer;
        private float _strength;
        private float _strengthTarget;
        private long _lastTick;

        public ApoManager(AppConfig cfg, ILog log)
        {
            _cfg = cfg;
            _log = log;
            _fadeInMs = Math.Max(0, cfg.General.FadeInMs);
            _fadeOutMs = Math.Max(0, cfg.General.FadeOutMs);
        }

        public bool IsAvailable => TryGetConfigDir(out _);

        public void Enable(string id)
        {
            lock (_sync)
            {
                if (!TryGetConfigDir(out var dir))
                    throw new InvalidOperationException(
                        "Equalizer APO not found: no HKLM\\SOFTWARE\\EqualizerAPO\\ConfigPath or no config.txt.");
                EnsureInclude(dir);
                _active.Add(id);
                _strengthTarget = 1f;
                _lastTick = Environment.TickCount64;
                KickTimer();
                _log.Info($"Equalizer APO: fading '{id}' in (active: {string.Join(", ", _active)})");
            }
        }

        /// <summary>Never throws - it is called from Revert/Dispose.</summary>
        public void Disable(string id)
        {
            lock (_sync)
            {
                try
                {
                    if (!_active.Remove(id)) return; // already off - Revert is idempotent
                    if (_active.Count == 0)
                    {
                        _strengthTarget = 0f;          // fade down to zero, then write an empty file
                        _lastTick = Environment.TickCount64;
                        KickTimer();
                        _log.Info($"Equalizer APO: fading '{id}' out, the file will be emptied once it is done");
                    }
                    else if (TryGetConfigDir(out var dir))
                    {
                        WriteOwnFile(dir, _strength); // the others stay - just rewrite without the one we removed
                        _log.Info($"Equalizer APO: '{id}' removed (still active: {string.Join(", ", _active)})");
                    }
                }
                catch (Exception ex)
                {
                    _log.Error($"Equalizer APO: could not update {OwnFileName} after removing '{id}'", ex);
                }
            }
        }

        /// <summary>Finalize the state immediately, with no fade - used when the app exits.</summary>
        public void DisableImmediate(string id)
        {
            lock (_sync)
            {
                try
                {
                    _active.Remove(id);
                    _timer?.Dispose();
                    _timer = null;
                    if (!TryGetConfigDir(out var dir)) return;
                    if (_active.Count == 0)
                    {
                        _strength = 0f;
                        _strengthTarget = 0f;
                        File.WriteAllText(Path.Combine(dir, OwnFileName), "");
                    }
                    else
                    {
                        _strength = 1f;
                        _strengthTarget = 1f;
                        WriteOwnFile(dir, 1f);
                    }
                }
                catch { }
            }
        }

        private void KickTimer()
        {
            _timer ??= new System.Threading.Timer(OnTick);
            _timer.Change(0, RampIntervalMs);
        }

        private void OnTick(object? state)
        {
            if (!Monitor.TryEnter(_sync)) return;
            try
            {
                if (!TryGetConfigDir(out var dir)) { _timer?.Dispose(); _timer = null; return; }

                long now = Environment.TickCount64;
                long dt = Math.Max(0, now - _lastTick);
                _lastTick = now;

                if (_strength != _strengthTarget)
                {
                    bool up = _strengthTarget > _strength;
                    int dur = up ? _fadeInMs : _fadeOutMs;
                    if (dur <= 0) _strength = _strengthTarget;
                    else _strength = up
                        ? Math.Min(_strengthTarget, _strength + (float)dt / dur)
                        : Math.Max(_strengthTarget, _strength - (float)dt / dur);
                }

                WriteOwnFile(dir, _strength);

                if (_strength == _strengthTarget)
                {
                    _timer?.Dispose();
                    _timer = null;
                }
            }
            catch (Exception ex) { _log.Warn($"Equalizer APO: fade step failed: {ex.Message}"); }
            finally { Monitor.Exit(_sync); }
        }

        private bool TryGetConfigDir(out string dir)
        {
            if (_cachedDir != null) { dir = _cachedDir; return true; }
            if (TryFindConfigDir(out dir)) { _cachedDir = dir; return true; }
            return false;
        }

        /// <summary>The manual path from settings (apo_config_dir). Empty means we look in the registry.</summary>
        internal static string? ManualDir;

        /// <summary>An APO folder is valid if it contains config.txt.</summary>
        internal static bool IsApoDir(string? path) =>
            !string.IsNullOrWhiteSpace(path) && Directory.Exists(path) && File.Exists(Path.Combine(path, "config.txt"));

        /// <summary>
        /// Path to the APO config folder: the MANUAL path from settings first, then
        /// HKLM\SOFTWARE\EqualizerAPO\ConfigPath. Uncached, so the static cleanup helper can use it too.
        /// </summary>
        internal static bool TryFindConfigDir(out string dir)
        {
            dir = "";
            if (IsApoDir(ManualDir)) { dir = ManualDir!; return true; }   // the manual path wins
            return TryFindInRegistry(out dir);
        }

        /// <summary>Registry only, ignoring the manual path - the settings window needs to show what was auto-detected.</summary>
        internal static bool TryFindInRegistry(out string dir)
        {
            dir = "";
            try
            {
                using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
                using var key = baseKey.OpenSubKey(@"SOFTWARE\EqualizerAPO");
                if (key?.GetValue("ConfigPath") is not string path || string.IsNullOrWhiteSpace(path))
                    return false;
                if (!IsApoDir(path)) return false;
                dir = path;
                return true;
            }
            catch { return false; }
        }

        private void EnsureInclude(string dir)
        {
            if (_includeEnsured) return;

            var configTxt = Path.Combine(dir, "config.txt");
            bool found = false;
            foreach (var line in File.ReadAllLines(configTxt))
                if (string.Equals(line.Trim(), IncludeLine, StringComparison.OrdinalIgnoreCase)) { found = true; break; }
            if (!found)
            {
                File.AppendAllText(configTxt, Environment.NewLine + IncludeLine + Environment.NewLine);
                _log.Info($"Equalizer APO: added the line \"{IncludeLine}\" to config.txt");
            }

            var own = Path.Combine(dir, OwnFileName);
            if (!File.Exists(own)) File.WriteAllText(own, "");
            _includeEnsured = true;
        }

        /// <summary>Writes the active effects scaled by strength s (0..1). At s close to 0 the file is left empty.</summary>
        private void WriteOwnFile(string dir, float s)
        {
            var inv = CultureInfo.InvariantCulture;
            var sb = new StringBuilder();

            if (s > 0.001f)
            {
                if (_active.Contains("mono"))
                {
                    // s=0 -> stereo (identity), s=1 -> full mono
                    float a = 1f - 0.5f * s;
                    float b = 0.5f * s;
                    sb.AppendLine($"Copy: L={a.ToString("0.###", inv)}*L+{b.ToString("0.###", inv)}*R "
                                + $"R={b.ToString("0.###", inv)}*L+{a.ToString("0.###", inv)}*R");
                }

                int n = 0;
                if (_active.Contains("bass_boost"))
                {
                    int fc = _cfg.GetParam("bass_boost", "fc_hz", 100);
                    double gain = _cfg.GetParam("bass_boost", "gain_db", 12.0) * s; // the boost grows with strength
                    n++;
                    sb.AppendLine($"Filter {n}: ON LS Fc {fc} Hz Gain {gain.ToString("0.##", inv)} dB");
                }
                if (_active.Contains("muffle"))
                {
                    int targetFc = _cfg.GetParam("muffle", "fc_hz", 1100);
                    // s=0 -> cutoff around 20 kHz (barely any filter), s=1 -> the target frequency (muffled).
                    // Two LP stages. The sweep is GEOMETRIC (logarithmic in frequency) - it sounds even, with no
                    // jolt near the low end.
                    double target = Math.Max(20.0, targetFc);
                    int fc = (int)Math.Round(20000.0 * Math.Pow(target / 20000.0, s));
                    n++; sb.AppendLine($"Filter {n}: ON LP Fc {fc} Hz");
                    n++; sb.AppendLine($"Filter {n}: ON LP Fc {fc} Hz");
                }
            }

            File.WriteAllText(Path.Combine(dir, OwnFileName), sb.ToString());
        }
    }

    // ---------------------- white_noise ----------------------

    /// <summary>Masking pink noise via WaveOutEvent, with the volume fading up and down.</summary>
    private sealed class WhiteNoiseEffect : IEffect
    {
        private readonly AppConfig _cfg;
        private readonly ILog _log;
        private readonly int _fadeInMs;
        private readonly int _fadeOutMs;
        private readonly object _sync = new();
        private WaveOutEvent? _waveOut;
        private PinkNoiseProvider? _provider;

        public WhiteNoiseEffect(AppConfig cfg, ILog log, int fadeInMs, int fadeOutMs)
        {
            _cfg = cfg;
            _log = log;
            _fadeInMs = fadeInMs;
            _fadeOutMs = fadeOutMs;
        }

        public string Id => "white_noise";
        public bool IsAvailable => true;

        public void Apply()
        {
            lock (_sync)
            {
                float volume = (float)Math.Clamp(_cfg.GetParam("white_noise", "volume", 0.25), 0.0, 1.0);
                if (_waveOut != null) { _provider?.SetTarget(volume, _fadeInMs); return; } // already playing - just bring the volume back
                WaveOutEvent? player = null;
                try
                {
                    var prov = new PinkNoiseProvider(0f);   // start from silence and fade up
                    prov.SetTarget(volume, _fadeInMs);
                    player = new WaveOutEvent();
                    player.PlaybackStopped += (_, e) =>
                    {
                        if (e.Exception != null) _log.Warn($"white_noise: playback was interrupted: {e.Exception.Message}");
                    };
                    player.Init(prov);
                    player.Play();
                    _waveOut = player;
                    _provider = prov;
                    _log.Info($"white_noise: fading the noise up to {volume.ToString("0.##", CultureInfo.InvariantCulture)}");
                }
                catch (Exception ex)
                {
                    _log.Error("white_noise: could not start the noise", ex);
                    try { player?.Dispose(); } catch { }
                }
            }
        }

        public void Revert()
        {
            lock (_sync)
            {
                var player = _waveOut;
                var prov = _provider;
                if (player == null) return;
                _waveOut = null;
                _provider = null;

                prov?.SetTarget(0f, _fadeOutMs);        // fade down to silence
                // stop once the fade is done
                System.Threading.Timer? t = null;
                t = new System.Threading.Timer(_ =>
                {
                    StopPlayer(player);
                    t?.Dispose();
                });
                t.Change(_fadeOutMs + 200, Timeout.Infinite);
            }
        }

        public void Dispose()
        {
            lock (_sync)
            {
                var player = _waveOut;
                _waveOut = null;
                _provider = null;
                if (player != null) StopPlayer(player);
            }
        }

        private void StopPlayer(WaveOutEvent player)
        {
            try { player.Stop(); player.Dispose(); _log.Info("white_noise: the noise was stopped"); }
            catch (Exception ex) { _log.Error("white_noise: failed to stop the noise", ex); }
        }
    }

    /// <summary>
    /// Pink noise via Voss-McCartney: 16 generators, the k-th updated every 2^k samples, plus a white
    /// component. The volume walks toward its target (for fade-in/out) one step per sample.
    /// </summary>
    private sealed class PinkNoiseProvider : ISampleProvider
    {
        private const int Rows = 16;

        private readonly float[] _rows = new float[Rows];
        private readonly Random _rng = new();
        private readonly object _volLock = new();
        private float _volume;
        private float _targetVolume;
        private float _step;          // volume change per sample
        private float _sum;
        private uint _counter;

        public PinkNoiseProvider(float volume)
        {
            _volume = volume;
            _targetVolume = volume;
        }

        public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(44100, 2);

        public void SetTarget(float target, int rampMs)
        {
            lock (_volLock)
            {
                _targetVolume = Math.Clamp(target, 0f, 1f);
                _step = rampMs <= 0 ? 1f : 1f / (rampMs / 1000f * WaveFormat.SampleRate);
            }
        }

        public int Read(float[] buffer, int offset, int count)
        {
            float vol, target, step;
            lock (_volLock) { vol = _volume; target = _targetVolume; step = _step; }

            for (int i = 0; i < count; i += 2)
            {
                if (vol != target)
                    vol = vol < target ? Math.Min(target, vol + step) : Math.Max(target, vol - step);

                _counter++;
                int row = BitOperations.TrailingZeroCount(_counter);
                if (row >= Rows) row = Rows - 1;

                _sum -= _rows[row];
                float v = (float)(_rng.NextDouble() * 2.0 - 1.0);
                _rows[row] = v;
                _sum += v;

                float white = (float)(_rng.NextDouble() * 2.0 - 1.0);
                float sample = (_sum + white) / (Rows + 1) * vol;

                buffer[offset + i] = sample;
                if (i + 1 < count) buffer[offset + i + 1] = sample;
            }

            lock (_volLock) { _volume = vol; }
            return count;
        }
    }
}

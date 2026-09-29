using System.Collections.Concurrent;
using System.Runtime.InteropServices;

namespace DebuffRoulette;

/// <summary>
/// Display debuffs: color matrices through the Magnification API (grayscale, colorblind, invert,
/// contrast_up, brightness_down, washed_out, rainbow).
/// </summary>
public static class DisplayModule
{
    public static IReadOnlyList<IEffect> Create(AppConfig cfg, ILog log)
    {
        // One Magnification host for every matrix effect; it lives as long as at least one of them does.
        var host = new MagnificationHost(log, cfg.General.FadeInMs, cfg.General.FadeOutMs);

        float contrast = (float)cfg.GetParam("contrast_up", "contrast", 1.7);
        float brightScale = (float)cfg.GetParam("brightness_down", "scale", 0.42);
        float washContrast = (float)cfg.GetParam("washed_out", "contrast", 0.5);
        float washLift = (float)cfg.GetParam("washed_out", "lift", 0.35);

        return new List<IEffect>
        {
            new MatrixEffect("grayscale", host, ColorMatrices.Grayscale),
            new MatrixEffect("colorblind", host, () => ColorMatrices.RandomColorblind(log)),
            new MatrixEffect("invert", host, ColorMatrices.Invert),
            new MatrixEffect("contrast_up", host,
                () => ColorMatrices.ScaleOffset(contrast, (1f - contrast) / 2f)),
            new MatrixEffect("brightness_down", host,
                () => ColorMatrices.ScaleOffset(brightScale, 0f)),
            // A milky picture: contrast down plus lifted blacks; with the defaults the range is 0.425..0.925, so nothing clips.
            new MatrixEffect("washed_out", host,
                () => ColorMatrices.ScaleOffset(washContrast, (1f - washContrast) / 2f + washLift * (1f - washContrast))),
            new RainbowEffect(cfg, host, log),
        };
    }
}

// ============================================================================
// The Magnification API host: a dedicated thread plus a command queue.
// ============================================================================

/// <summary>
/// A wrapper over magnification.dll. Every Mag* call happens strictly on one background thread
/// (MagInitialize/MagUninitialize must run on it too, and the process keeps the effect alive).
/// Active matrices are kept in insertion order and the result is their product.
/// </summary>
internal sealed class MagnificationHost
{
    private readonly ILog _log;
    private readonly BlockingCollection<Action> _queue = new();
    private readonly Thread _thread;
    private readonly ManualResetEventSlim _ready = new(false);

    // Host thread only:
    private readonly List<string> _order = new();
    private readonly Dictionary<string, float[]> _active = new();

    private volatile bool _available;
    private int _refs;      // how many effects still hold the host
    private int _shutdown;

    // Fade-in/fade-out durations (ms). 0 means instant.
    private readonly int _fadeInMs;
    private readonly int _fadeOutMs;

    // Effect-strength animation state - host thread only.
    private float _t;                 // current strength factor [0..1]
    private float _tTarget;           // target: 1 (matrices present) or 0 (none)
    private bool _animating;          // is t currently animating toward the target
    private long _lastTickTicks;      // timestamp of the previous frame (Environment.TickCount64)

    public MagnificationHost(ILog log, int fadeInMs, int fadeOutMs)
    {
        _log = log;
        _fadeInMs = Math.Max(0, fadeInMs);
        _fadeOutMs = Math.Max(0, fadeOutMs);
        _thread = new Thread(Loop) { IsBackground = true, Name = "magnification-host" };
        _thread.Start();
    }

    public bool IsAvailable
    {
        get
        {
            _ready.Wait(TimeSpan.FromSeconds(5));
            return _available;
        }
    }

    public void AddRef() => Interlocked.Increment(ref _refs);

    /// <summary>Called from Dispose of every matrix effect; the last one shuts the host down.</summary>
    public void Release()
    {
        if (Interlocked.Decrement(ref _refs) <= 0)
            Shutdown();
    }

    public void Set(string id, float[] matrix)
    {
        if (!IsAvailable) return;
        Post(() =>
        {
            if (_active.ContainsKey(id))
                _active[id] = matrix;      // replace in place so the order stays put (matters for rainbow)
            else
            {
                _order.Add(id);
                _active[id] = matrix;
            }
            ApplyCombined();
        });
    }

    public void Remove(string id)
    {
        if (!IsAvailable) return;
        Post(() =>
        {
            if (_active.Remove(id))
            {
                _order.Remove(id);
                ApplyCombined();
            }
        });
    }

    private void Post(Action action)
    {
        try
        {
            if (!_queue.IsAddingCompleted)
                _queue.Add(action);
        }
        catch (InvalidOperationException) { } // the queue is already closed - the host is shutting down
    }

    // the combined matrix we fade out from once no active matrices are left
    private float[]? _lastCombined;

    private void Loop()
    {
        _available = MagInitialize();
        if (!_available)
            _log.Warn("Magnification API unavailable (MagInitialize=false) - color effects are disabled");
        _ready.Set();
        if (!_available) return;

        _lastTickTicks = Environment.TickCount64;

        // While a fade is running we tick about every 16 ms (one animation frame on the Mag* thread).
        // Otherwise we sleep until the next command: the thread used to wake 60 times a second for the
        // whole life of the app, even with no effects active.
        while (!(_queue.IsAddingCompleted && _queue.Count == 0))
        {
            Action? action = null;
            bool got;
            try { got = _queue.TryTake(out action, _animating ? 16 : Timeout.Infinite); }
            catch { break; }

            if (got && action != null)
            {
                try { action(); }
                catch (Exception ex) { _log.Error("Error on the Magnification thread", ex); }
            }
            Tick();
        }

        try { ApplyMatrix(ColorMatrices.Identity()); } catch { }
        try { MagUninitialize(); } catch { }
    }

    /// <summary>Called from Set/Remove on the host thread: recompute the strength target and repaint.</summary>
    private void ApplyCombined()
    {
        _tTarget = _active.Count > 0 ? 1f : 0f;
        int dur = _tTarget > _t ? _fadeInMs : _fadeOutMs;
        if (dur <= 0) _t = _tTarget;           // fading disabled - jump straight there
        _animating = Math.Abs(_t - _tTarget) > 0.0001f;
        _lastTickTicks = Environment.TickCount64;
        RenderCurrent();
    }

    private void Tick()
    {
        if (!_animating) return;
        long now = Environment.TickCount64;
        long dtMs = Math.Max(0, now - _lastTickTicks);
        _lastTickTicks = now;

        int dur = _tTarget > _t ? _fadeInMs : _fadeOutMs;
        if (dur <= 0) _t = _tTarget;
        else
        {
            float step = (float)dtMs / dur;
            _t = _tTarget > _t ? Math.Min(_tTarget, _t + step) : Math.Max(_tTarget, _t - step);
        }
        if (Math.Abs(_t - _tTarget) < 0.001f) { _t = _tTarget; _animating = false; }
        RenderCurrent();
    }

    private float[] ComputeCombined()
    {
        var m = ColorMatrices.Identity();
        foreach (var id in _order)
            m = ColorMatrices.Multiply(m, _active[id]);
        return m;
    }

    private void RenderCurrent()
    {
        // While anything is active we compute the product and remember it; when nothing is left we fade out from the last one.
        float[] combined = _active.Count > 0
            ? (_lastCombined = ComputeCombined())
            : (_lastCombined ?? ColorMatrices.Identity());
        ApplyMatrix(LerpFromIdentity(combined, _t));
    }

    /// <summary>Linear interpolation from the identity matrix to the target one by strength t.</summary>
    private static float[] LerpFromIdentity(float[] combined, float t)
    {
        if (t >= 1f) return combined;
        var id = ColorMatrices.Identity();
        if (t <= 0f) return id;
        var r = new float[25];
        for (int i = 0; i < 25; i++)
            r[i] = id[i] + (combined[i] - id[i]) * t;
        return r;
    }

    private void ApplyMatrix(float[] m)
    {
        var effect = new MAGCOLOREFFECT { transform = m };
        if (!MagSetFullscreenColorEffect(ref effect))
            _log.Warn("MagSetFullscreenColorEffect returned false");
    }

    private void Shutdown()
    {
        if (Interlocked.Exchange(ref _shutdown, 1) != 0) return;
        try { _queue.CompleteAdding(); } catch { }
        try
        {
            if (_thread.IsAlive && !_thread.Join(3000))
                _log.Warn("The Magnification thread did not finish within 3 s");
        }
        catch { }
    }

    // ---------- P/Invoke ----------

    [StructLayout(LayoutKind.Sequential)]
    private struct MAGCOLOREFFECT
    {
        // 5x5 row-major; out = row vector * matrix (rows = input channels)
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 25)]
        public float[] transform;
    }

    [DllImport("magnification.dll")]
    private static extern bool MagInitialize();

    [DllImport("magnification.dll")]
    private static extern bool MagUninitialize();

    [DllImport("magnification.dll")]
    private static extern bool MagSetFullscreenColorEffect(ref MAGCOLOREFFECT pEffect);
}

// ============================================================================
// 5x5 matrices (row-major, rows = input channels, same as the MS grayscale sample).
// ============================================================================

internal static class ColorMatrices
{
    public static float[] Identity() => new float[]
    {
        1, 0, 0, 0, 0,
        0, 1, 0, 0, 0,
        0, 0, 1, 0, 0,
        0, 0, 0, 1, 0,
        0, 0, 0, 0, 1,
    };

    /// <summary>The product a*b: a is applied first, then b (out = v*a*b).</summary>
    public static float[] Multiply(float[] a, float[] b)
    {
        var r = new float[25];
        for (int i = 0; i < 5; i++)
            for (int j = 0; j < 5; j++)
            {
                float s = 0;
                for (int k = 0; k < 5; k++)
                    s += a[i * 5 + k] * b[k * 5 + j];
                r[i * 5 + j] = s;
            }
        return r;
    }

    public static float[] Grayscale() => new float[]
    {
        0.2126f, 0.2126f, 0.2126f, 0, 0,
        0.7152f, 0.7152f, 0.7152f, 0, 0,
        0.0722f, 0.0722f, 0.0722f, 0, 0,
        0,       0,       0,       1, 0,
        0,       0,       0,       0, 1,
    };

    public static float[] Invert() => new float[]
    {
        -1,  0,  0, 0, 0,
         0, -1,  0, 0, 0,
         0,  0, -1, 0, 0,
         0,  0,  0, 1, 0,
         1,  1,  1, 0, 1,
    };

    /// <summary>diag(scale) over RGB plus the same offset in the translation row.</summary>
    public static float[] ScaleOffset(float scale, float offset) => new float[]
    {
        scale, 0,     0,     0, 0,
        0,     scale, 0,     0, 0,
        0,     0,     scale, 0, 0,
        0,     0,     0,     1, 0,
        offset, offset, offset, 0, 1,
    };

    // Colorblindness simulation matrices, written in row convention (a row = one input channel's contribution).
    private static readonly (string Name, float[] Rgb)[] ColorblindSims =
    {
        ("deuteranopia", new[] { 0.625f, 0.375f, 0f,      0.7f,   0.3f,   0f,      0f, 0.3f,   0.7f }),
        ("protanopia",   new[] { 0.567f, 0.433f, 0f,      0.558f, 0.442f, 0f,      0f, 0.242f, 0.758f }),
        ("tritanopia",   new[] { 0.95f,  0.05f,  0f,      0f,     0.433f, 0.567f,  0f, 0.475f, 0.525f }),
    };

    public static float[] RandomColorblind(ILog log)
    {
        var (name, rgb) = ColorblindSims[Random.Shared.Next(ColorblindSims.Length)];
        log.Info($"colorblind: simulating {name}");
        var m = Identity();
        for (int row = 0; row < 3; row++)
            for (int col = 0; col < 3; col++)
                m[row * 5 + col] = rgb[row * 3 + col];
        return m;
    }

    /// <summary>
    /// The standard hue-rotate matrix (SVG feColorMatrix, luminance 0.213/0.715/0.072),
    /// transposed to match Magnification's row convention.
    /// </summary>
    public static float[] HueRotate(double degrees)
    {
        double rad = degrees * Math.PI / 180.0;
        float c = (float)Math.Cos(rad), s = (float)Math.Sin(rad);
        var m = Identity();
        m[0]  = 0.213f + c * 0.787f - s * 0.213f;
        m[1]  = 0.213f - c * 0.213f + s * 0.143f;
        m[2]  = 0.213f - c * 0.213f - s * 0.787f;
        m[5]  = 0.715f - c * 0.715f - s * 0.715f;
        m[6]  = 0.715f + c * 0.285f + s * 0.140f;
        m[7]  = 0.715f - c * 0.715f + s * 0.715f;
        m[10] = 0.072f - c * 0.072f + s * 0.928f;
        m[11] = 0.072f - c * 0.072f - s * 0.283f;
        m[12] = 0.072f + c * 0.928f + s * 0.072f;
        return m;
    }
}

// ============================================================================
// Simple matrix effects.
// ============================================================================

internal sealed class MatrixEffect : IEffect, IRerollable
{
    private readonly MagnificationHost _host;
    private readonly Func<float[]> _matrixFactory; // the factory (for colorblind it returns a random matrix)
    private float[] _matrix;                        // the chosen matrix, stable until the next Reroll
    private bool _disposed;

    public MatrixEffect(string id, MagnificationHost host, Func<float[]> matrixFactory)
    {
        Id = id;
        _host = host;
        _matrixFactory = matrixFactory;
        _matrix = matrixFactory();
        _host.AddRef();
    }

    public string Id { get; }

    public bool IsAvailable => _host.IsAvailable;

    /// <summary>Pick a new matrix (for random ones like colorblind) - once per combo, not on every toggle.</summary>
    public void Reroll() => _matrix = _matrixFactory();

    public void Apply()
    {
        if (!_host.IsAvailable)
            throw new InvalidOperationException($"{Id}: Magnification API unavailable");
        _host.Set(Id, _matrix);
    }

    public void Revert()
    {
        try { _host.Remove(Id); } catch { }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Revert();
        _host.Release();
    }
}

// ============================================================================
// rainbow: hue rotation plus a brightness lift, refreshed by a 33 ms timer.
// ============================================================================

internal sealed class RainbowEffect : IEffect
{
    private const double TickSec = 0.033;

    private readonly MagnificationHost _host;
    private readonly ILog _log;
    private readonly double _periodSec;
    private readonly float[] _overexposure;
    private readonly object _lock = new();
    private System.Threading.Timer? _timer;
    private double _angle;
    private bool _disposed;

    public RainbowEffect(AppConfig cfg, MagnificationHost host, ILog log)
    {
        _host = host;
        _log = log;
        _periodSec = Math.Max(0.5, cfg.GetParam("rainbow", "period_sec", 10.0));
        float ov = (float)cfg.GetParam("rainbow", "overexposure", 1.2);
        _overexposure = ColorMatrices.ScaleOffset(ov, 0.03f);
        _host.AddRef();
    }

    public string Id => "rainbow";

    public bool IsAvailable => _host.IsAvailable;

    public void Apply()
    {
        if (!_host.IsAvailable)
            throw new InvalidOperationException("rainbow: Magnification API unavailable");
        lock (_lock)
        {
            if (_timer != null) return; // already spinning
            _angle = 0;
            _timer = new System.Threading.Timer(Tick, null, 0, 33);
        }
        _log.Info($"rainbow: period {_periodSec:0.#} s");
    }

    private void Tick(object? state)
    {
        // The lock guarantees that no host.Set slips through after Revert.
        lock (_lock)
        {
            if (_timer == null) return;
            _angle = (_angle + 360.0 / _periodSec * TickSec) % 360.0;
            _host.Set(Id, ColorMatrices.Multiply(ColorMatrices.HueRotate(_angle), _overexposure));
        }
    }

    public void Revert()
    {
        try
        {
            lock (_lock)
            {
                _timer?.Dispose();
                _timer = null;
            }
            _host.Remove(Id);
        }
        catch { }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Revert();
        _host.Release();
    }
}

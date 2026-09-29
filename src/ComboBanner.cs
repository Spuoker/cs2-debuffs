using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace DebuffRoulette;

/// <summary>
/// On-screen banner listing the active debuffs. Its own layered click-through window
/// (WDA_EXCLUDEFROMCAPTURE - invisible to capture and streams), fading in and out.
/// Shown at the start of a match or round so the player sees what they rolled.
/// </summary>
public sealed class ComboBanner : IDisposable
{
    // Human-readable debuff names (id -> label).
    private static readonly Dictionary<string, string> Labels = new(StringComparer.Ordinal)
    {
        ["no_sound"] = "No sound",
        ["mono"] = "Mono",
        ["bass_boost"] = "Bass boost",
        ["muffle"] = "Underwater",
        ["white_noise"] = "White noise",
        ["no_voice"] = "No voice",
        ["grayscale"] = "Grayscale",
        ["colorblind"] = "Colorblind",
        ["invert"] = "Inverted colors",
        ["contrast_up"] = "High contrast",
        ["brightness_down"] = "Dimmed",
        ["washed_out"] = "Washed out",
        ["rainbow"] = "Shifting palette",
        ["no_tab"] = "No scoreboard",
        ["no_killfeed"] = "No killfeed",
        ["no_radar"] = "No radar",
        ["no_ids"] = "No teammate IDs",
        ["no_crosshair"] = "No crosshair",
    };

    public static string Label(string id) => Labels.TryGetValue(id, out var s) ? s : id;

    private const int FadeInMs = 200;
    private const int HoldMs = 5000;
    private const int FadeOutMs = 500;

    private readonly ILog _log;
    private readonly object _sync = new();
    private Thread? _thread;
    private BannerForm? _form;

    public ComboBanner(ILog log) => _log = log;

    /// <summary>Show the banner. Thread-safe; calling it again restarts the show.</summary>
    public void Show(IReadOnlyList<string> lines)
    {
        try
        {
            lock (_sync)
            {
                if (_thread == null)
                {
                    _thread = new Thread(() =>
                    {
                        try
                        {
                            _form = new BannerForm();
                            System.Windows.Forms.Application.Run(_form);
                        }
                        catch (Exception ex) { _log.Error("banner: the window thread crashed", ex); }
                    })
                    { IsBackground = true, Name = "combo-banner" };
                    _thread.SetApartmentState(ApartmentState.STA);
                    _thread.Start();

                    // wait briefly for the window to initialize
                    for (int i = 0; i < 50 && _form is not { IsHandleReady: true }; i++)
                        Thread.Sleep(10);
                }
            }

            var f = _form;
            if (f is { IsHandleReady: true })
                f.BeginInvoke(() => f.ShowCombo(lines));
        }
        catch (Exception ex) { _log.Error("banner: failed to show", ex); }
    }

    public void Dispose()
    {
        try
        {
            var f = _form;
            if (f is { IsHandleReady: true })
                f.BeginInvoke(() => f.Close());
        }
        catch { }
    }

    private sealed class BannerForm : Form
    {
        private const int WsExLayered = 0x80000;
        private const int WsExTransparent = 0x20;
        private const int WsExNoActivate = 0x8000000;
        private const int WsExToolWindow = 0x80;
        private const uint WdaExcludeFromCapture = 0x11;

        private readonly System.Windows.Forms.Timer _anim = new() { Interval = 16 };
        private IReadOnlyList<string> _lines = Array.Empty<string>();
        private double _alpha;          // current opacity, 0..1
        private int _phase;             // 0 idle, 1 fade-in, 2 hold, 3 fade-out
        private DateTime _phaseStart;

        public bool IsHandleReady { get; private set; }

        [DllImport("user32.dll")] private static extern bool SetWindowDisplayAffinity(IntPtr hWnd, uint dwAffinity);

        public BannerForm()
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            TopMost = true;
            var scr = Screen.PrimaryScreen!.Bounds;
            // banner sits top-center
            Width = 520;
            Height = 260;
            Left = scr.Left + (scr.Width - Width) / 2;
            Top = scr.Top + (int)(scr.Height * 0.10);

            _anim.Tick += (_, _) => OnAnim();
        }

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ExStyle |= WsExLayered | WsExTransparent | WsExNoActivate | WsExToolWindow;
                return cp;
            }
        }

        protected override bool ShowWithoutActivation => true;

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            try { SetWindowDisplayAffinity(Handle, WdaExcludeFromCapture); } catch { }
            IsHandleReady = true;
        }

        public void ShowCombo(IReadOnlyList<string> lines)
        {
            _lines = lines;
            _phase = 1;
            _phaseStart = DateTime.UtcNow;
            if (!Visible) Show();
            _anim.Start();
            Redraw();
        }

        private void OnAnim()
        {
            double ms = (DateTime.UtcNow - _phaseStart).TotalMilliseconds;
            switch (_phase)
            {
                case 1: // fade-in
                    _alpha = Math.Min(1.0, ms / FadeInMs);
                    if (_alpha >= 1.0) { _phase = 2; _phaseStart = DateTime.UtcNow; }
                    break;
                case 2: // hold
                    _alpha = 1.0;
                    if (ms >= HoldMs) { _phase = 3; _phaseStart = DateTime.UtcNow; }
                    break;
                case 3: // fade-out
                    _alpha = Math.Max(0.0, 1.0 - ms / FadeOutMs);
                    if (_alpha <= 0.0) { _phase = 0; _anim.Stop(); }
                    break;
                default:
                    _anim.Stop();
                    return;
            }
            Redraw();
        }

        private void Redraw()
        {
            using var bmp = new Bitmap(Width, Height, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
                g.Clear(Color.Transparent);

                byte a = (byte)(_alpha * 255);
                byte bgA = (byte)(_alpha * 205);

                // rounded background plate
                var rect = new Rectangle(6, 6, Width - 12, Height - 12);
                using (var path = RoundedRect(rect, 16))
                using (var bg = new SolidBrush(Color.FromArgb(bgA, 18, 18, 22)))
                using (var pen = new Pen(Color.FromArgb((byte)(_alpha * 140), 240, 90, 60), 2))
                {
                    g.FillPath(bg, path);
                    g.DrawPath(pen, path);
                }

                using var title = new Font("Segoe UI", 15f, FontStyle.Bold);
                using var item = new Font("Segoe UI", 13f, FontStyle.Regular);
                using var titleBrush = new SolidBrush(Color.FromArgb(a, 240, 90, 60));
                using var textBrush = new SolidBrush(Color.FromArgb(a, 235, 235, 240));

                string header = _lines.Count == 0 ? "Clean match" : "You rolled:";
                g.DrawString(header, title, titleBrush, 24, 20);

                int y = 58;
                foreach (var line in _lines)
                {
                    g.DrawString("•  " + line, item, textBrush, 28, y);
                    y += 30;
                    if (y > Height - 30) break;
                }
            }

            PushLayered(bmp);
        }

        private void PushLayered(Bitmap bmp) => LayeredWindow.Push(Handle, bmp, Left, Top);

        private static GraphicsPath RoundedRect(Rectangle r, int radius)
        {
            int d = radius * 2;
            var path = new GraphicsPath();
            path.AddArc(r.X, r.Y, d, d, 180, 90);
            path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) _anim.Dispose();
            base.Dispose(disposing);
        }
    }
}

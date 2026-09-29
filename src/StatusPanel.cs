using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace DebuffRoulette;

/// <summary>
/// Black-and-white "what is active right now" panel at the right edge, plus a Settings button.
/// Its own layered window (excluded from capture), click-through everywhere EXCEPT the buttons
/// (WM_NCHITTEST). Program owns the trigger: it shows while Tab is held, in a live match and with CS2
/// focused (GSI does not report whether the scoreboard or menu is open - the key catches that).
/// To actually CLICK a button, right-click on the scoreboard so CS2 releases the cursor. The window is
/// topmost on its own (not SetParent - safer with anti-cheat) and positioned against the CS2 window rect.
/// </summary>
public sealed class StatusPanel : IDisposable
{
    private readonly ILog _log;
    private readonly Action _onSettings;
    private readonly object _sync = new();
    private Thread? _thread;
    private PanelForm? _form;

    /// <param name="onSettings">Click on the panel's Settings button (the caller marshals it to the UI thread).</param>
    public StatusPanel(ILog log, Action onSettings)
    {
        _log = log;
        _onSettings = onSettings;
    }

    /// <summary>Show it (key-held mode). lines is the current set, using human-readable names.</summary>
    public void ShowPanel(IReadOnlyList<string> lines)
    {
        try
        {
            EnsureForm();
            var f = _form;
            if (f is { IsHandleReady: true })
                f.BeginInvoke(() => f.ShowWith(lines));
        }
        catch (Exception ex) { _log.Error("panel: failed to show", ex); }
    }

    /// <summary>Hide it (the key was released).</summary>
    public void HidePanel()
    {
        try
        {
            var f = _form;
            if (f is { IsHandleReady: true })
                f.BeginInvoke(() => f.HideNow());
        }
        catch { }
    }

    /// <summary>Toggle, for the tray menu item.</summary>
    public void Toggle(IReadOnlyList<string> lines)
    {
        try
        {
            EnsureForm();
            var f = _form;
            if (f is { IsHandleReady: true })
                f.BeginInvoke(() => f.ToggleWith(lines));
        }
        catch (Exception ex) { _log.Error("panel: failed to toggle", ex); }
    }

    public void Dispose()
    {
        try
        {
            var f = _form;
            if (f is { IsHandleReady: true }) f.BeginInvoke(() => f.Close());
        }
        catch { }
    }

    private void EnsureForm()
    {
        lock (_sync)
        {
            if (_thread != null) return;
            _thread = new Thread(() =>
            {
                try
                {
                    _form = new PanelForm(_onSettings);
                    System.Windows.Forms.Application.Run(_form);
                }
                catch (Exception ex) { _log.Error("panel: the window thread crashed", ex); }
            })
            { IsBackground = true, Name = "status-panel" };
            _thread.SetApartmentState(ApartmentState.STA);
            _thread.Start();
            for (int i = 0; i < 50 && _form is not { IsHandleReady: true }; i++)
                Thread.Sleep(10);
        }
    }

    private sealed class PanelForm : Form
    {
        private const int WsExLayered = 0x80000;
        private const int WsExNoActivate = 0x8000000;
        private const int WsExToolWindow = 0x80;
        private const uint WdaExcludeFromCapture = 0x11;

        private const int WmMouseActivate = 0x0021;
        private const int WmNcHitTest = 0x0084;
        private const int WmLButtonDown = 0x0201;
        private const int WmLButtonUp = 0x0202;
        private const int HtTransparent = -1;
        private const int HtClient = 1;
        private const int MaNoActivate = 3;

        private IReadOnlyList<string> _lines = Array.Empty<string>();
        private bool _shown;

        private readonly Action _onSettings;
        private Rectangle _btnSettings;               // client px, matches the one we draw
        private int _pressed;                             // 0 none, 1 settings (pressed highlight)
        private int _downOn;                              // which button got the mouse-down (a click is down+up on the same one)
        private Rectangle _bounds;                        // the CS2 window rect at show time (we stick to it)

        public bool IsHandleReady { get; private set; }

        [DllImport("user32.dll")] private static extern bool SetWindowDisplayAffinity(IntPtr hWnd, uint dwAffinity);
        [DllImport("user32.dll")] private static extern bool ScreenToClient(IntPtr hWnd, ref Point pt);
        [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hWnd, out RectN r);

        [StructLayout(LayoutKind.Sequential)] private struct RectN { public int L, T, R, B; }

        public PanelForm(Action onSettings)
        {
            _onSettings = onSettings;
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            TopMost = true;
            Width = 300;
            Height = 400;
        }

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                // NO WS_EX_TRANSPARENT: without it the window receives the mouse, so we can catch button
                // clicks via WM_NCHITTEST. Layering (per-pixel alpha) comes from WsExLayered, which is
                // orthogonal to hit-testing.
                cp.ExStyle |= WsExLayered | WsExNoActivate | WsExToolWindow;
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

        public void ShowWith(IReadOnlyList<string> lines)
        {
            _lines = lines;
            if (_shown) return;
            _shown = true;
            _bounds = TargetBounds();   // stick to the CS2 window, not the whole monitor
            if (!Visible) Show();
            Render();
            ForceTopmostHard();
            // We do NOT run a periodic re-topmost - that was the flicker. A topmost window already sits above a borderless game.
        }

        public void HideNow()
        {
            if (!_shown) return;
            _shown = false;
            Hide();
        }

        public void ToggleWith(IReadOnlyList<string> lines)
        {
            if (_shown) HideNow();
            else ShowWith(lines);
        }

        private void ForceTopmostHard() => LayeredWindow.RaiseTopmost(Handle, hard: true);  // on show, jump above CS2

        // A click-through window, but the buttons are clickable. Everything outside them goes to the game (HTTRANSPARENT).
        protected override void WndProc(ref Message m)
        {
            switch (m.Msg)
            {
                case WmMouseActivate:
                    // Clicking a button must NOT activate the overlay and steal focus from the borderless game.
                    m.Result = (IntPtr)MaNoActivate;
                    return;

                case WmNcHitTest:
                {
                    var p = Unpack(m.LParam);       // screen px
                    ScreenToClient(Handle, ref p);  // -> client px (the same space as the bitmap)
                    bool hit = _shown && _btnSettings.Contains(p);
                    m.Result = (IntPtr)(hit ? HtClient : HtTransparent);
                    return;
                }

                case WmLButtonDown when _shown:
                {
                    var p = Unpack(m.LParam);       // client
                    // NO SetCapture! On a click-through window the mouse capture sticks and freezes the cursor in the game.
                    _downOn = _btnSettings.Contains(p) ? 1 : 0;
                    if (_pressed != _downOn) { _pressed = _downOn; Render(); }
                    return;
                }

                case WmLButtonUp when _shown:
                {
                    var p = Unpack(m.LParam);       // client
                    int up = _btnSettings.Contains(p) ? 1 : 0;
                    int fire = up != 0 && up == _downOn ? up : 0;  // a click counts only if down and up landed on the same button
                    _downOn = 0;
                    if (_pressed != 0) { _pressed = 0; Render(); }
                    if (fire == 1) { try { _onSettings(); } catch { } }
                    return;
                }
            }
            base.WndProc(ref m);
        }

        private static Point Unpack(IntPtr lParam)
        {
            int lp = unchecked((int)(long)lParam);
            return new Point((short)(lp & 0xFFFF), (short)((lp >> 16) & 0xFFFF));
        }

        // The CS2 window rect (what we stick to). No game running means the whole primary monitor.
        private static Rectangle TargetBounds()
        {
            try
            {
                var procs = System.Diagnostics.Process.GetProcessesByName("cs2");
                foreach (var p in procs)
                {
                    using var _ = p;   // MainWindowHandle opens a process handle - release it
                    var h = p.MainWindowHandle;
                    if (h != IntPtr.Zero && GetWindowRect(h, out var r))
                    {
                        int w = r.R - r.L, ht = r.B - r.T;
                        if (w > 100 && ht > 100) return new Rectangle(r.L, r.T, w, ht);
                    }
                }
            }
            catch { }
            return Screen.PrimaryScreen!.Bounds;
        }

        private void Render()
        {
            var scr = _bounds.Width > 0 ? _bounds : Screen.PrimaryScreen!.Bounds;

            using var title = new Font("Segoe UI", 13f, FontStyle.Bold);
            using var item = new Font("Segoe UI", 12f, FontStyle.Regular);

            const int w = 300, lineH = 26;
            const int btnH = 34, btnPadX = 14, btnTopGap = 12, bottomPad = 14;


            int listH = Math.Max(1, _lines.Count) * lineH;
            int contentH = 52 + listH + btnTopGap + btnH + bottomPad;
            int h = Math.Min(scr.Height - 40, contentH);

            Width = w;
            Height = h;
            Left = scr.Right - w - 18;               // right edge
            Top = scr.Top + (scr.Height - h) / 2;    // vertically centered

            int btnY = h - bottomPad - btnH;
            int btnW = w - btnPadX * 2;   // single button, so it spans the whole panel width
            _btnSettings = new Rectangle(btnPadX, btnY, btnW, btnH);

            using var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
                g.Clear(Color.Transparent);

                var rect = new Rectangle(4, 4, w - 8, h - 8);
                using (var path = Rounded(rect, 14))
                using (var bg = new SolidBrush(Color.FromArgb(238, 12, 12, 12)))     // solid dark, readable over a busy scene
                using (var border = new Pen(Color.FromArgb(235, 235, 235, 235), 2))  // black-and-white border
                {
                    g.FillPath(bg, path);
                    g.DrawPath(border, path);
                }

                using var head = new SolidBrush(Color.FromArgb(255, 245, 245, 245));
                using var txt = new SolidBrush(Color.FromArgb(255, 225, 225, 225));

                g.DrawString(_lines.Count == 0 ? "Clean" : "Active", title, head, 18, 16);
                using (var sep = new Pen(Color.FromArgb(120, 235, 235, 235), 1))
                    g.DrawLine(sep, 18, 42, w - 18, 42);

                int y = 52;
                foreach (var line in _lines)
                {
                    if (y > btnY - btnTopGap - lineH) break;   // do not run into the buttons
                    g.DrawString("•  " + line, item, txt, 18, y);
                    y += lineH;
                }

                DrawButton(g, _btnSettings, "Settings", _pressed == 1);
            }

            PushLayered(bmp);
        }

        // The button is drawn straight into the window bitmap: child controls do not work in a layered window.
        private static void DrawButton(Graphics g, Rectangle r, string text, bool pressed)
        {
            const int a = 255;
            Color fill = pressed ? Color.FromArgb(255, 74, 74, 84) : Color.FromArgb(255, 44, 44, 50);
            Color brd = Color.FromArgb(a, 210, 210, 210);
            Color fg = Color.FromArgb(a, 240, 240, 240);
            using var path = Rounded(r, 8);
            using var bg = new SolidBrush(fill);
            using var pen = new Pen(brd, 1.5f);
            using var tb = new SolidBrush(fg);
            using var f = new Font("Segoe UI", 10.5f, FontStyle.Bold);
            using var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            g.FillPath(bg, path);
            g.DrawPath(pen, path);
            g.DrawString(text, f, tb, r, sf);
        }

        private void PushLayered(Bitmap bmp) => LayeredWindow.Push(Handle, bmp, Left, Top);

        private static GraphicsPath Rounded(Rectangle r, int radius)
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
    }
}

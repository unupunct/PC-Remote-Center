using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using RemoteControlServer.Terminal;

namespace RemoteControlServer.App.Controls;

/// <summary>Renders a <see cref="TerminalSession"/>'s screen (the same emulator state the phone sees) and sends keyboard input to it.</summary>
public sealed class TerminalView : FrameworkElement
{
    private readonly Typeface _face = new(new FontFamily("Cascadia Mono, Consolas, Courier New"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
    private readonly Typeface _bold = new(new FontFamily("Cascadia Mono, Consolas, Courier New"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal);
    private const double FontSize_ = 15;
    private double _cw = 9, _ch = 18;
    private TerminalSession? _session;
    private bool _dirty;
    private readonly DispatcherTimer _timer;
    private readonly DispatcherTimer _resizeTimer;
    private int _scrollOffset; // lines scrolled back (0 = live)
    private (int c, int r)? _selA, _selB;
    private ScreenState? _lastState;
    private Run[][]? _viewRows;

    public static readonly Color DefaultFg = Color.FromRgb(0xCC, 0xCC, 0xCC), DefaultBg = Color.FromRgb(0x0C, 0x0C, 0x0C);

    public TerminalView()
    {
        Focusable = true; FocusVisualStyle = null; Cursor = Cursors.IBeam;
        var ft = new FormattedText("M", CultureInfo.InvariantCulture, FlowDirection.LeftToRight, _face, FontSize_, Brushes.White, 1.0);
        _cw = ft.WidthIncludingTrailingWhitespace; _ch = Math.Ceiling(ft.Height);
        _timer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(40) };
        _timer.Tick += (_, _) => { if (_dirty) { _dirty = false; InvalidateVisual(); } };
        _resizeTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        _resizeTimer.Tick += (_, _) => { _resizeTimer.Stop(); ApplyResize(); };
        Loaded += (_, _) => _timer.Start();
        Unloaded += (_, _) => _timer.Stop();
    }

    public double CellWidth => _cw;
    public double CellHeight => _ch;

    public TerminalSession? Session
    {
        get => _session;
        set
        {
            if (_session != null) _session.OutputChanged -= OnChanged;
            _session = value;
            if (_session != null) _session.OutputChanged += OnChanged;
            _dirty = true;
        }
    }

    private void OnChanged() { _dirty = true; }

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        _resizeTimer.Stop(); _resizeTimer.Start();
    }

    private void ApplyResize()
    {
        if (_session == null || !_session.IsRunning) return;
        int cols = Math.Max(20, (int)(ActualWidth / _cw)), rows = Math.Max(5, (int)(ActualHeight / _ch));
        if (cols == _session.Screen.Cols && rows == _session.Screen.Rows) return;
        try { _session.Resize(cols, rows); } catch { }
    }

    protected override void OnRender(DrawingContext dc)
    {
        dc.DrawRectangle(new SolidColorBrush(DefaultBg), null, new Rect(0, 0, ActualWidth, ActualHeight));
        if (_session == null) return;
        var st = _session.Screen.SnapshotScreen();
        _lastState = st;
        Run[][] rows;
        int cursorRow = -1;
        if (_scrollOffset > 0)
        {
            var full = _session.Screen.Snapshot(0, 3000);
            var all = full.Added.Concat(full.Screen).ToArray();
            int end = all.Length - _scrollOffset;
            int startIdx = Math.Max(0, end - st.Rows);
            rows = all.Skip(startIdx).Take(Math.Min(st.Rows, end)).ToArray();
        }
        else { rows = st.Screen; cursorRow = st.CursorY; }
        _viewRows = rows;
        double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;

        for (int r = 0; r < rows.Length; r++)
        {
            double x = 0, y = r * _ch;
            foreach (var run in rows[r])
            {
                double w = run.Text.Length * _cw;
                if (run.Bg != -1) dc.DrawRectangle(new SolidColorBrush(ToColor(run.Bg)), null, new Rect(x, y, w, _ch));
                if (run.Text.Trim().Length > 0)
                {
                    var fg = run.Fg == -1 ? DefaultFg : ToColor(run.Fg);
                    if ((run.Flags & RunFlags.Dim) != 0) fg = Color.FromArgb(160, fg.R, fg.G, fg.B);
                    var ft = new FormattedText(run.Text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                        (run.Flags & RunFlags.Bold) != 0 ? _bold : _face, FontSize_, new SolidColorBrush(fg), dpi);
                    if ((run.Flags & RunFlags.Underline) != 0) ft.SetTextDecorations(TextDecorations.Underline);
                    dc.DrawText(ft, new Point(x, y));
                }
                x += w;
            }
        }

        if (_selA != null && _selB != null)
        {
            var (a, b) = Ordered(_selA.Value, _selB.Value);
            var sel = new SolidColorBrush(Color.FromArgb(90, 0x3B, 0x82, 0xF6));
            for (int r = a.r; r <= b.r && r < rows.Length; r++)
            {
                int c0 = r == a.r ? a.c : 0, c1 = r == b.r ? b.c : st.Cols - 1;
                dc.DrawRectangle(sel, null, new Rect(c0 * _cw, r * _ch, (c1 - c0 + 1) * _cw, _ch));
            }
        }

        if (cursorRow >= 0 && st.CursorVisible && _session.IsRunning && IsKeyboardFocusWithin)
            dc.DrawRectangle(new SolidColorBrush(Color.FromArgb(160, 0x22, 0xD3, 0xEE)), null, new Rect(st.CursorX * _cw, cursorRow * _ch, _cw, _ch));
        else if (cursorRow >= 0 && st.CursorVisible && _session.IsRunning)
            dc.DrawRectangle(null, new Pen(new SolidColorBrush(Color.FromRgb(0x22, 0xD3, 0xEE)), 1), new Rect(st.CursorX * _cw, cursorRow * _ch, _cw, _ch));
    }

    private static Color ToColor(int rgb) => Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);

    // ------------------------------------------------------------------ mouse: selection and scrollback

    private (int c, int r) CellAt(Point p) => ((int)Math.Clamp(p.X / _cw, 0, 399), (int)Math.Clamp(p.Y / _ch, 0, 199));

    private static ((int c, int r), (int c, int r)) Ordered((int c, int r) a, (int c, int r) b) =>
        (a.r < b.r || (a.r == b.r && a.c <= b.c)) ? (a, b) : (b, a);

    protected override void OnMouseDown(MouseButtonEventArgs e)
    {
        Focus();
        if (e.ChangedButton == MouseButton.Left) { _selA = CellAt(e.GetPosition(this)); _selB = _selA; CaptureMouse(); _dirty = true; }
        else if (e.ChangedButton == MouseButton.Right) { if (HasSelection) CopySelection(); else Paste(); }
        base.OnMouseDown(e);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (IsMouseCaptured && e.LeftButton == MouseButtonState.Pressed) { _selB = CellAt(e.GetPosition(this)); _dirty = true; }
        base.OnMouseMove(e);
    }

    protected override void OnMouseUp(MouseButtonEventArgs e)
    {
        if (IsMouseCaptured) ReleaseMouseCapture();
        if (_selA != null && _selA == _selB) { _selA = _selB = null; _dirty = true; }
        base.OnMouseUp(e);
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        _scrollOffset = Math.Max(0, _scrollOffset + (e.Delta > 0 ? 3 : -3));
        _dirty = true;
        base.OnMouseWheel(e);
    }

    private bool HasSelection => _selA != null && _selB != null && _selA != _selB;

    public void CopySelection()
    {
        if (!HasSelection || _viewRows == null || _lastState == null) return;
        var (a, b) = Ordered(_selA!.Value, _selB!.Value);
        var lines = new List<string>();
        for (int r = a.r; r <= b.r && r < _viewRows.Length; r++)
        {
            var text = string.Concat(_viewRows[r].Select(x => x.Text)).PadRight(_lastState.Cols);
            int c0 = r == a.r ? a.c : 0, c1 = r == b.r ? b.c : _lastState.Cols - 1;
            lines.Add(text.Substring(Math.Min(c0, text.Length), Math.Max(0, Math.Min(c1 + 1, text.Length) - Math.Min(c0, text.Length))).TrimEnd());
        }
        try { Clipboard.SetText(string.Join("\r\n", lines)); } catch { }
        _selA = _selB = null; _dirty = true;
    }

    public void Paste()
    {
        if (_session is not { IsRunning: true }) return;
        try
        {
            if (!Clipboard.ContainsText()) return;
            var t = Clipboard.GetText().Replace("\r\n", "\r").Replace('\n', '\r');
            _session.WriteRaw(t.Length > 8000 ? t[..8000] : t);
        }
        catch { }
    }

    // ------------------------------------------------------------------ keyboard

    protected override void OnGotKeyboardFocus(KeyboardFocusChangedEventArgs e) { _dirty = true; base.OnGotKeyboardFocus(e); }
    protected override void OnLostKeyboardFocus(KeyboardFocusChangedEventArgs e) { _dirty = true; base.OnLostKeyboardFocus(e); }

    protected override void OnTextInput(TextCompositionEventArgs e)
    {
        if (_session is { IsRunning: true } && !string.IsNullOrEmpty(e.Text) && !e.Text.All(char.IsControl))
        {
            _scrollOffset = 0;
            Send(e.Text);
            e.Handled = true;
        }
        base.OnTextInput(e);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (_session == null) return;
        var ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
        var shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;

        if (ctrl && key == Key.C && (HasSelection || shift)) { CopySelection(); e.Handled = true; return; }
        if ((ctrl && key == Key.V) || (shift && key == Key.Insert)) { Paste(); e.Handled = true; return; }
        if (shift && key == Key.PageUp) { _scrollOffset += 15; _dirty = true; e.Handled = true; return; }
        if (shift && key == Key.PageDown) { _scrollOffset = Math.Max(0, _scrollOffset - 15); _dirty = true; e.Handled = true; return; }
        if (!_session.IsRunning) return;

        string? seq = key switch
        {
            Key.Enter => "\r", Key.Back => "\x7f", Key.Tab => shift ? "\x1b[Z" : "\t", Key.Escape => "\x1b",
            Key.Up => "\x1b[A", Key.Down => "\x1b[B", Key.Right => "\x1b[C", Key.Left => "\x1b[D",
            Key.Home => "\x1b[H", Key.End => "\x1b[F", Key.Delete => "\x1b[3~", Key.Insert => "\x1b[2~",
            Key.PageUp => "\x1b[5~", Key.PageDown => "\x1b[6~",
            Key.F1 => "\x1bOP", Key.F2 => "\x1bOQ", Key.F3 => "\x1bOR", Key.F4 => "\x1bOS", Key.F5 => "\x1b[15~",
            Key.F6 => "\x1b[17~", Key.F7 => "\x1b[18~", Key.F8 => "\x1b[19~", Key.F9 => "\x1b[20~", Key.F10 => "\x1b[21~",
            Key.F11 => "\x1b[23~", Key.F12 => "\x1b[24~",
            _ => null,
        };
        if (seq == null && ctrl && key >= Key.A && key <= Key.Z) seq = ((char)(key - Key.A + 1)).ToString();
        if (seq != null)
        {
            _scrollOffset = 0;
            if (seq == "\x03") { try { _session.Interrupt(); } catch { } }
            else Send(seq);
            e.Handled = true;
        }
        base.OnKeyDown(e);
    }

    private void Send(string s) { try { _session!.WriteRaw(s); } catch { } }
}

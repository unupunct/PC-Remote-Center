using System.Text;

namespace RemoteControlServer.Terminal;

public readonly record struct Run(string Text, int Fg, int Bg, byte Flags)
{
    public object[] ToWire() => [Text, Fg, Bg, (int)Flags];
}

public static class RunFlags
{
    public const byte Bold = 1, Dim = 2, Italic = 4, Underline = 8, Inverse = 16;
}

public sealed class ScreenState
{
    public int Cols { get; init; }
    public int Rows { get; init; }
    public int CursorX { get; init; }
    public int CursorY { get; init; }
    public bool CursorVisible { get; init; }
    public bool AltScreen { get; init; }
    public string Title { get; init; } = "";
    /// <summary>Absolute number of lines that have ever scrolled into the scrollback.</summary>
    public long ScrollbackTotal { get; init; }
    /// <summary>Absolute index of the oldest scrollback line still retained (rises when lines are trimmed or the buffer is cleared).</summary>
    public long ScrollbackStart { get; init; }
    public Run[][] Screen { get; init; } = [];
    /// <summary>Scrollback lines with absolute indexes [AddedFrom, ScrollbackTotal).</summary>
    public Run[][] Added { get; init; } = [];
    public long AddedFrom { get; init; }
}

/// <summary>
/// A small xterm-style terminal emulator. It consumes the VT byte stream produced by ConPTY and keeps a
/// screen grid plus scrollback. The same emulator state feeds the visible Windows terminal window and
/// the Android client. Replies required by ConPTY (cursor position / device attributes) go to <see cref="Reply"/>.
/// </summary>
public sealed class VtScreen
{
    private struct Cell { public int Ch; public int Fg; public int Bg; public byte Flags; }

    private const int DefaultFg = 0xCCCCCC, DefaultBg = 0x0C0C0C;
    private readonly object _lock = new();
    private readonly Decoder _decoder = new UTF8Encoding(false).GetDecoder();
    private readonly List<Run[]> _scroll = [];
    private long _scrollBase;
    private readonly int _maxScrollback;

    private Cell[,] _main, _alt;
    private Cell[,] _grid;
    private bool _isAlt;
    private int _cols, _rows, _x, _y, _top, _bottom;
    private int _sx, _sy; // saved cursor
    private bool _wrapPending, _autoWrap = true, _cursorVisible = true;
    private int _curFg = -1, _curBg = -1; private byte _curFlags;
    private string _title = "";

    private enum St { Ground, Esc, EscInter, Csi, Osc, OscEsc, Str, StrEsc }
    private St _st = St.Ground;
    private readonly StringBuilder _buf = new();
    private int _hiSurrogate;

    /// <summary>True after ConPTY sent ESC[?9001h: it then wants key events in win32-input-mode form.</summary>
    public bool Win32InputMode { get; private set; }

    public event Action<string>? Reply;
    public event Action? Changed;

    public VtScreen(int cols = 120, int rows = 30, int maxScrollback = 5000)
    {
        _cols = cols; _rows = rows; _maxScrollback = maxScrollback;
        _main = new Cell[rows, cols]; _alt = new Cell[rows, cols]; _grid = _main;
        _bottom = rows - 1;
        ClearGrid(_main); ClearGrid(_alt);
    }

    public int Cols { get { lock (_lock) return _cols; } }
    public int Rows { get { lock (_lock) return _rows; } }
    public string Title { get { lock (_lock) return _title; } }

    // ------------------------------------------------------------------ input

    public void Feed(byte[] data, int count)
    {
        var chars = new char[_decoder.GetCharCount(data, 0, count, false)];
        _decoder.GetChars(data, 0, count, chars, 0, false);
        lock (_lock)
        {
            foreach (var ch in chars) Process(ch);
        }
        Changed?.Invoke();
    }

    public void FeedText(string s)
    {
        var b = Encoding.UTF8.GetBytes(s);
        Feed(b, b.Length);
    }

    private void Process(char c)
    {
        switch (_st)
        {
            case St.Ground:
                if (c == '\x1b') { _st = St.Esc; return; }
                if (c < 0x20 || c == 0x7f) { Control(c); return; }
                Print(c); return;
            case St.Esc:
                switch (c)
                {
                    case '[': _buf.Clear(); _st = St.Csi; return;
                    case ']': _buf.Clear(); _st = St.Osc; return;
                    case 'P': case 'X': case '^': case '_': _buf.Clear(); _st = St.Str; return;
                    case '(': case ')': case '*': case '+': case '#': case '%': case ' ': _st = St.EscInter; return;
                    case 'M': ReverseIndex(); _st = St.Ground; return;
                    case 'D': LineFeed(); _st = St.Ground; return;
                    case 'E': _x = 0; LineFeed(); _st = St.Ground; return;
                    case '7': _sx = _x; _sy = _y; _st = St.Ground; return;
                    case '8': _x = Math.Min(_sx, _cols - 1); _y = Math.Min(_sy, _rows - 1); _wrapPending = false; _st = St.Ground; return;
                    case 'c': HardReset(); _st = St.Ground; return;
                    default: _st = St.Ground; return; // '=', '>', etc. ignored
                }
            case St.EscInter: _st = St.Ground; return;
            case St.Csi:
                if (c >= 0x40 && c <= 0x7e) { Csi(_buf.ToString(), c); _st = St.Ground; }
                else if (c == '\x1b') { _st = St.Esc; }
                else if (_buf.Length < 256) _buf.Append(c);
                return;
            case St.Osc:
                if (c == '\a') { Osc(_buf.ToString()); _st = St.Ground; }
                else if (c == '\x1b') _st = St.OscEsc;
                else if (_buf.Length < 2048) _buf.Append(c);
                return;
            case St.OscEsc:
                if (c == '\\') { Osc(_buf.ToString()); _st = St.Ground; }
                else { _st = St.Ground; }
                return;
            case St.Str:
                if (c == '\x1b') _st = St.StrEsc; else if (c == '\a') _st = St.Ground;
                return;
            case St.StrEsc:
                _st = c == '\\' ? St.Ground : St.Str;
                return;
        }
    }

    private void Control(char c)
    {
        switch (c)
        {
            case '\r': _x = 0; _wrapPending = false; break;
            case '\n': case '\v': case '\f': LineFeed(); break;
            case '\b': if (_x > 0) _x--; _wrapPending = false; break;
            case '\t': _x = Math.Min(((_x / 8) + 1) * 8, _cols - 1); _wrapPending = false; break;
        }
    }

    private void Print(char c)
    {
        int cp = c;
        if (char.IsHighSurrogate(c)) { _hiSurrogate = c; return; }
        if (char.IsLowSurrogate(c))
        {
            if (_hiSurrogate == 0) return;
            cp = char.ConvertToUtf32((char)_hiSurrogate, c); _hiSurrogate = 0;
        }
        if (_wrapPending)
        {
            if (_autoWrap) { _x = 0; LineFeed(); }
            _wrapPending = false;
        }
        _grid[_y, _x] = new Cell { Ch = cp, Fg = _curFg, Bg = _curBg, Flags = _curFlags };
        if (_x == _cols - 1) _wrapPending = true; else _x++;
    }

    private void LineFeed()
    {
        if (_y == _bottom) ScrollUp(1);
        else if (_y < _rows - 1) _y++;
        _wrapPending = false;
    }

    private void ReverseIndex()
    {
        if (_y == _top) ScrollDown(1);
        else if (_y > 0) _y--;
        _wrapPending = false;
    }

    private void ScrollUp(int n)
    {
        n = Math.Min(n, _bottom - _top + 1);
        for (int i = 0; i < n; i++)
        {
            if (!_isAlt && _top == 0) PushScrollback(RowToRuns(_grid, 0));
            for (int r = _top; r < _bottom; r++)
                for (int c = 0; c < _cols; c++) _grid[r, c] = _grid[r + 1, c];
            ClearRow(_grid, _bottom);
        }
    }

    private void ScrollDown(int n)
    {
        n = Math.Min(n, _bottom - _top + 1);
        for (int i = 0; i < n; i++)
        {
            for (int r = _bottom; r > _top; r--)
                for (int c = 0; c < _cols; c++) _grid[r, c] = _grid[r - 1, c];
            ClearRow(_grid, _top);
        }
    }

    private void PushScrollback(Run[] row)
    {
        _scroll.Add(row);
        if (_scroll.Count > _maxScrollback) { _scroll.RemoveAt(0); _scrollBase++; }
    }

    // ------------------------------------------------------------------ escape sequences

    private static int[] ParseParams(string s, out char prefix)
    {
        prefix = '\0';
        if (s.Length > 0 && (s[0] == '?' || s[0] == '>' || s[0] == '<' || s[0] == '=')) { prefix = s[0]; s = s[1..]; }
        // strip intermediates
        int end = s.Length; while (end > 0 && s[end - 1] >= 0x20 && s[end - 1] <= 0x2f) end--;
        s = s[..end];
        if (s.Length == 0) return [];
        return s.Split(';', ':').Select(p => int.TryParse(p, out var v) ? v : 0).ToArray();
    }

    private void Csi(string raw, char fin)
    {
        var p = ParseParams(raw, out var prefix);
        int P(int i, int def) => i < p.Length && p[i] > 0 ? p[i] : def;
        bool hasIntermediate = raw.Length > 0 && raw[^1] >= 0x20 && raw[^1] <= 0x2f;
        if (hasIntermediate) return; // e.g. DECSCUSR 'ESC[ q' - not needed
        switch (fin)
        {
            case 'A': _y = Math.Max(0, _y - P(0, 1)); _wrapPending = false; break;
            case 'B': _y = Math.Min(_rows - 1, _y + P(0, 1)); _wrapPending = false; break;
            case 'C': _x = Math.Min(_cols - 1, _x + P(0, 1)); _wrapPending = false; break;
            case 'D': _x = Math.Max(0, _x - P(0, 1)); _wrapPending = false; break;
            case 'E': _y = Math.Min(_rows - 1, _y + P(0, 1)); _x = 0; _wrapPending = false; break;
            case 'F': _y = Math.Max(0, _y - P(0, 1)); _x = 0; _wrapPending = false; break;
            case 'G': case '`': _x = Math.Clamp(P(0, 1) - 1, 0, _cols - 1); _wrapPending = false; break;
            case 'H': case 'f': _y = Math.Clamp(P(0, 1) - 1, 0, _rows - 1); _x = Math.Clamp(P(1, 1) - 1, 0, _cols - 1); _wrapPending = false; break;
            case 'd': _y = Math.Clamp(P(0, 1) - 1, 0, _rows - 1); _wrapPending = false; break;
            case 'J': EraseDisplay(p.Length > 0 ? p[0] : 0); break;
            case 'K': EraseLine(p.Length > 0 ? p[0] : 0); break;
            case 'L': if (_y >= _top && _y <= _bottom) InsertLines(P(0, 1)); break;
            case 'M': if (_y >= _top && _y <= _bottom) DeleteLines(P(0, 1)); break;
            case '@': InsertChars(P(0, 1)); break;
            case 'P': DeleteChars(P(0, 1)); break;
            case 'X': { int n = Math.Min(P(0, 1), _cols - _x); for (int i = 0; i < n; i++) _grid[_y, _x + i] = Blank(); break; }
            case 'S': ScrollUp(P(0, 1)); break;
            case 'T': ScrollDown(P(0, 1)); break;
            case 'm': Sgr(p); break;
            case 'r':
            {
                int t = P(0, 1) - 1, b = (p.Length > 1 && p[1] > 0 ? p[1] : _rows) - 1;
                if (t < b && b < _rows) { _top = t; _bottom = b; _x = 0; _y = 0; _wrapPending = false; }
                break;
            }
            case 's': if (prefix == '\0') { _sx = _x; _sy = _y; } break;
            case 'u': if (prefix == '\0') { _x = Math.Min(_sx, _cols - 1); _y = Math.Min(_sy, _rows - 1); _wrapPending = false; } break;
            case 'h': case 'l': Mode(p, prefix, fin == 'h'); break;
            case 'n':
                if (prefix == '\0' && p.Length > 0 && p[0] == 6) Reply?.Invoke($"\x1b[{_y + 1};{_x + 1}R");
                else if (prefix == '\0' && p.Length > 0 && p[0] == 5) Reply?.Invoke("\x1b[0n");
                break;
            case 'c':
                if (prefix == '\0') Reply?.Invoke("\x1b[?1;0c");
                else if (prefix == '>') Reply?.Invoke("\x1b[>0;10;1c");
                break;
        }
    }

    private void Mode(int[] p, char prefix, bool set)
    {
        if (prefix != '?') return;
        foreach (var m in p)
        {
            switch (m)
            {
                case 7: _autoWrap = set; break;
                case 25: _cursorVisible = set; break;
                case 9001: Win32InputMode = set; break;
                case 47: case 1047: case 1049: SwitchScreen(set, m == 1049); break;
            }
        }
    }

    private void SwitchScreen(bool alt, bool saveCursor)
    {
        if (alt == _isAlt) return;
        if (alt)
        {
            if (saveCursor) { _sx = _x; _sy = _y; }
            _isAlt = true; _grid = _alt; ClearGrid(_alt); _top = 0; _bottom = _rows - 1;
        }
        else
        {
            _isAlt = false; _grid = _main; _top = 0; _bottom = _rows - 1;
            if (saveCursor) { _x = Math.Min(_sx, _cols - 1); _y = Math.Min(_sy, _rows - 1); }
        }
        _wrapPending = false;
    }

    private void Osc(string s)
    {
        int semi = s.IndexOf(';');
        if (semi < 0) return;
        if (s[..semi] is "0" or "2") _title = new string(s[(semi + 1)..].Where(ch => !char.IsControl(ch)).ToArray());
    }

    private void Sgr(int[] p)
    {
        if (p.Length == 0) p = [0];
        for (int i = 0; i < p.Length; i++)
        {
            int v = p[i];
            switch (v)
            {
                case 0: _curFg = _curBg = -1; _curFlags = 0; break;
                case 1: _curFlags |= RunFlags.Bold; break;
                case 2: _curFlags |= RunFlags.Dim; break;
                case 3: _curFlags |= RunFlags.Italic; break;
                case 4: _curFlags |= RunFlags.Underline; break;
                case 7: _curFlags |= RunFlags.Inverse; break;
                case 22: _curFlags &= unchecked((byte)~(RunFlags.Bold | RunFlags.Dim)); break;
                case 23: _curFlags &= unchecked((byte)~RunFlags.Italic); break;
                case 24: _curFlags &= unchecked((byte)~RunFlags.Underline); break;
                case 27: _curFlags &= unchecked((byte)~RunFlags.Inverse); break;
                case >= 30 and <= 37: _curFg = Palette(v - 30); break;
                case 39: _curFg = -1; break;
                case >= 40 and <= 47: _curBg = Palette(v - 40); break;
                case 49: _curBg = -1; break;
                case >= 90 and <= 97: _curFg = Palette(v - 90 + 8); break;
                case >= 100 and <= 107: _curBg = Palette(v - 100 + 8); break;
                case 38: case 48:
                {
                    int color = -1;
                    if (i + 1 < p.Length && p[i + 1] == 5 && i + 2 < p.Length) { color = Palette(Math.Clamp(p[i + 2], 0, 255)); i += 2; }
                    else if (i + 1 < p.Length && p[i + 1] == 2 && i + 4 < p.Length)
                    {
                        color = (Math.Clamp(p[i + 2], 0, 255) << 16) | (Math.Clamp(p[i + 3], 0, 255) << 8) | Math.Clamp(p[i + 4], 0, 255);
                        i += 4;
                    }
                    if (v == 38) _curFg = color; else _curBg = color;
                    break;
                }
            }
        }
    }

    private static readonly int[] Base16 =
    [
        0x0C0C0C, 0xC50F1F, 0x13A10E, 0xC19C00, 0x0037DA, 0x881798, 0x3A96DD, 0xCCCCCC,
        0x767676, 0xE74856, 0x16C60C, 0xF9F1A5, 0x3B78FF, 0xB4009E, 0x61D6D6, 0xF2F2F2,
    ];

    public static int Palette(int i)
    {
        if (i < 16) return Base16[i];
        if (i < 232)
        {
            i -= 16;
            int[] lv = [0, 95, 135, 175, 215, 255];
            return (lv[i / 36] << 16) | (lv[(i / 6) % 6] << 8) | lv[i % 6];
        }
        int g = 8 + (i - 232) * 10;
        return (g << 16) | (g << 8) | g;
    }

    // ------------------------------------------------------------------ editing primitives

    private Cell Blank() => new() { Ch = 0, Fg = -1, Bg = _curBg, Flags = 0 };

    private static void ClearGrid(Cell[,] g)
    {
        for (int r = 0; r < g.GetLength(0); r++) ClearRow(g, r);
    }

    private static void ClearRow(Cell[,] g, int r)
    {
        for (int c = 0; c < g.GetLength(1); c++) g[r, c] = new Cell { Fg = -1, Bg = -1 };
    }

    private void EraseRange(int row, int from, int to)
    {
        for (int c = from; c <= to && c < _cols; c++) _grid[row, c] = Blank();
    }

    private void EraseDisplay(int mode)
    {
        switch (mode)
        {
            case 0: EraseRange(_y, _x, _cols - 1); for (int r = _y + 1; r < _rows; r++) EraseRange(r, 0, _cols - 1); break;
            case 1: for (int r = 0; r < _y; r++) EraseRange(r, 0, _cols - 1); EraseRange(_y, 0, _x); break;
            case 2: for (int r = 0; r < _rows; r++) EraseRange(r, 0, _cols - 1); break;
            case 3: _scrollBase += _scroll.Count; _scroll.Clear(); break;
        }
    }

    private void EraseLine(int mode)
    {
        switch (mode)
        {
            case 0: EraseRange(_y, _x, _cols - 1); break;
            case 1: EraseRange(_y, 0, _x); break;
            case 2: EraseRange(_y, 0, _cols - 1); break;
        }
    }

    private void InsertLines(int n)
    {
        n = Math.Min(n, _bottom - _y + 1);
        for (int i = 0; i < n; i++)
        {
            for (int r = _bottom; r > _y; r--) for (int c = 0; c < _cols; c++) _grid[r, c] = _grid[r - 1, c];
            ClearRow(_grid, _y);
        }
    }

    private void DeleteLines(int n)
    {
        n = Math.Min(n, _bottom - _y + 1);
        for (int i = 0; i < n; i++)
        {
            for (int r = _y; r < _bottom; r++) for (int c = 0; c < _cols; c++) _grid[r, c] = _grid[r + 1, c];
            ClearRow(_grid, _bottom);
        }
    }

    private void InsertChars(int n)
    {
        n = Math.Min(n, _cols - _x);
        for (int c = _cols - 1; c >= _x + n; c--) _grid[_y, c] = _grid[_y, c - n];
        for (int c = _x; c < _x + n; c++) _grid[_y, c] = Blank();
    }

    private void DeleteChars(int n)
    {
        n = Math.Min(n, _cols - _x);
        for (int c = _x; c < _cols - n; c++) _grid[_y, c] = _grid[_y, c + n];
        for (int c = _cols - n; c < _cols; c++) _grid[_y, c] = Blank();
    }

    private void HardReset()
    {
        ClearGrid(_main); ClearGrid(_alt); _grid = _main; _isAlt = false;
        _x = _y = 0; _top = 0; _bottom = _rows - 1; _curFg = _curBg = -1; _curFlags = 0; _wrapPending = false; _autoWrap = true;
    }

    // ------------------------------------------------------------------ public control

    public void Resize(int cols, int rows)
    {
        lock (_lock)
        {
            if (cols == _cols && rows == _rows) return;
            _main = ResizeGrid(_main, cols, rows); _alt = ResizeGrid(_alt, cols, rows);
            _grid = _isAlt ? _alt : _main;
            _cols = cols; _rows = rows; _top = 0; _bottom = rows - 1;
            _x = Math.Min(_x, cols - 1); _y = Math.Min(_y, rows - 1); _wrapPending = false;
        }
        Changed?.Invoke();
    }

    private static Cell[,] ResizeGrid(Cell[,] old, int cols, int rows)
    {
        var g = new Cell[rows, cols];
        ClearGrid(g);
        for (int r = 0; r < Math.Min(rows, old.GetLength(0)); r++)
            for (int c = 0; c < Math.Min(cols, old.GetLength(1)); c++) g[r, c] = old[r, c];
        return g;
    }

    /// <summary>Drops the scrollback and blanks the visible screen (used by the explicit "clear" action).</summary>
    public void ClearAll()
    {
        lock (_lock)
        {
            _scrollBase += _scroll.Count; _scroll.Clear();
            for (int r = 0; r < _rows; r++) EraseRange(r, 0, _cols - 1);
            _x = 0; _y = 0; _wrapPending = false;
        }
        Changed?.Invoke();
    }

    // ------------------------------------------------------------------ snapshots

    private Run[] RowToRuns(Cell[,] g, int row)
    {
        var runs = new List<Run>();
        var sb = new StringBuilder();
        int fg = 0, bg = 0; byte fl = 0; bool open = false;
        int last = _cols - 1;
        while (last >= 0 && g[row, last].Ch is 0 or ' ' && g[row, last].Bg == -1) last--;
        for (int c = 0; c <= last; c++)
        {
            var cell = g[row, c];
            int f = cell.Fg, b = cell.Bg; byte flags = (byte)(cell.Flags & ~RunFlags.Inverse);
            if ((cell.Flags & RunFlags.Inverse) != 0)
            {
                int rf = b == -1 ? DefaultBg : b, rb = f == -1 ? DefaultFg : f;
                f = rf; b = rb;
            }
            if (open && (f != fg || b != bg || flags != fl))
            {
                runs.Add(new Run(sb.ToString(), fg, bg, fl)); sb.Clear(); open = false;
            }
            if (!open) { fg = f; bg = b; fl = flags; open = true; }
            sb.Append(cell.Ch == 0 ? " " : char.ConvertFromUtf32(cell.Ch));
        }
        if (open) runs.Add(new Run(sb.ToString(), fg, bg, fl));
        return [.. runs];
    }

    /// <summary>Captures the current screen and the scrollback lines added since absolute index <paramref name="sinceAbs"/>.</summary>
    public ScreenState Snapshot(long sinceAbs, int maxAdded = 2000)
    {
        lock (_lock)
        {
            long total = _scrollBase + _scroll.Count;
            long from = Math.Max(sinceAbs, Math.Max(_scrollBase, total - maxAdded));
            if (from > total) from = total;
            var added = _scroll.Skip((int)(from - _scrollBase)).ToArray();
            var screen = new Run[_rows][];
            for (int r = 0; r < _rows; r++) screen[r] = RowToRuns(_grid, r);
            return new ScreenState
            {
                Cols = _cols, Rows = _rows, CursorX = _x, CursorY = _y, CursorVisible = _cursorVisible, AltScreen = _isAlt,
                Title = _title, ScrollbackTotal = total, ScrollbackStart = _scrollBase, Screen = screen, Added = added, AddedFrom = from,
            };
        }
    }

    /// <summary>Just the visible screen (no scrollback) - cheap enough to call for every repaint.</summary>
    public ScreenState SnapshotScreen() => Snapshot(long.MaxValue);

    /// <summary>Plain text of scrollback + screen (trailing blank lines removed). Handy for copy and tests.</summary>
    public string GetText()
    {
        lock (_lock)
        {
            var lines = _scroll.Select(r => string.Concat(r.Select(x => x.Text)).TrimEnd()).ToList();
            for (int r = 0; r < _rows; r++) lines.Add(string.Concat(RowToRuns(_grid, r).Select(x => x.Text)).TrimEnd());
            while (lines.Count > 0 && lines[^1].Length == 0) lines.RemoveAt(lines.Count - 1);
            return string.Join('\n', lines);
        }
    }
}

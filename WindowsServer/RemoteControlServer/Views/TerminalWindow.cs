using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using RemoteControlServer.App.Controls;
using RemoteControlServer.Terminal;

namespace RemoteControlServer.App.Views;

/// <summary>
/// The visible terminal window on the PC. It renders the very same ConPTY session that the phone controls, so
/// everything typed on the phone appears here, and vice versa. Closing the window only hides it - the shell keeps running.
/// </summary>
public sealed class TerminalWindow : Window
{
    private readonly TerminalView _view = new();
    private readonly TextBlock _status = new() { Foreground = new SolidColorBrush(Color.FromRgb(0x8B, 0x93, 0xA7)), Margin = new Thickness(10, 3, 10, 3), FontSize = 12 };
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    public TerminalSession Session { get; private set; }
    public bool AllowClose { get; set; }

    public TerminalWindow(TerminalSession session)
    {
        Session = session;
        Background = new SolidColorBrush(TerminalView.DefaultBg);
        Width = 120 * _view.CellWidth + 40; Height = 30 * _view.CellHeight + 70;
        Icon = System.Windows.Media.Imaging.BitmapFrame.Create(new Uri("pack://application:,,,/Assets/app.ico"));
        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition());
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var border = new Border { Padding = new Thickness(6), Child = _view };
        Grid.SetRow(border, 0); grid.Children.Add(border);
        var bar = new Border { Background = new SolidColorBrush(Color.FromRgb(0x14, 0x16, 0x1B)), Child = _status };
        Grid.SetRow(bar, 1); grid.Children.Add(bar);
        Content = grid;
        Attach(session);
        _timer.Tick += (_, _) => Refresh();
        _timer.Start();
        Loaded += (_, _) => { _view.Focus(); Refresh(); };
        Activated += (_, _) => _view.Focus();
    }

    public void Attach(TerminalSession s)
    {
        Session = s;
        _view.Session = s;
        Refresh();
    }

    private void Refresh()
    {
        var s = Session;
        Title = $"PC Remote Center — {s.Title}" + (string.IsNullOrWhiteSpace(s.Screen.Title) ? "" : $" — {s.Screen.Title}");
        _status.Text = s.IsRunning
            ? $"{s.Title}  •  pid {s.ProcessId}  •  {s.Screen.Cols}×{s.Screen.Rows}  •  shared live with connected phones"
            : $"{s.Title} has exited (code {s.ExitCode}). Restart it from the dashboard (Terminal Sessions) or from the phone.";
        _status.Foreground = s.IsRunning ? new SolidColorBrush(Color.FromRgb(0x8B, 0x93, 0xA7)) : new SolidColorBrush(Color.FromRgb(0xFB, 0xBF, 0x24));
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (!AllowClose) { e.Cancel = true; Hide(); return; }
        _timer.Stop();
        _view.Session = null;
        base.OnClosing(e);
    }
}

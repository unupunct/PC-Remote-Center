using System.ComponentModel;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using RemoteControlServer.App.Services;
using RemoteControlServer.App.ViewModels;
using RemoteControlServer.Models;
using RemoteControlServer.Networking;
using RemoteControlServer.Shortcuts;
using RemoteControlServer.Storage;
using Microsoft.Win32;

namespace RemoteControlServer.App.Views;

public partial class MainWindow : Window
{
    private readonly AppViewModel _vm;
    private readonly TerminalWindowService _terms;
    private readonly System.Windows.Threading.DispatcherTimer _termTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private string? _editingShortcutId;
    public bool ReallyClose { get; set; }

    public MainWindow(AppViewModel vm, TerminalWindowService terms)
    {
        _vm = vm; _terms = terms;
        InitializeComponent();
        DataContext = vm;
        Icon = System.Windows.Media.Imaging.BitmapFrame.Create(new Uri("pack://application:,,,/Assets/app.ico"));
        IsVisibleChanged += (_, _) => _vm.SetVisible(IsVisible && WindowState != WindowState.Minimized);
        _termTimer.Tick += (_, _) => RefreshTerminals();
        _termTimer.Start();
        LoadSettingsIntoForms();
        RefreshTerminals();
        _vm.LoadWol();
    }

    private IEnumerable<RadioButton> FindRadios(DependencyObject root)
    {
        for (int i = 0; i < VisualTreeHelper_Count(root); i++)
        {
            var c = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
            if (c is RadioButton rb && rb.GroupName == "nav") yield return rb;
            foreach (var r in FindRadios(c)) yield return r;
        }
    }
    private static int VisualTreeHelper_Count(DependencyObject d) => System.Windows.Media.VisualTreeHelper.GetChildrenCount(d);

    private void OnNav(object sender, RoutedEventArgs e)
    {
        var tag = (sender as RadioButton)?.Tag as string;
        foreach (var name in new[] { "PageOverview", "PageHardware", "PageTerminals", "PageDevices", "PageShortcuts", "PageLogs", "PageNetwork", "PageSecurity" })
            if (FindName(name) is UIElement page) page.Visibility = name == tag ? Visibility.Visible : Visibility.Collapsed;
        if (tag == "PageLogs") { _vm.RefreshAudit(); _vm.RefreshHistory(); }
        if (tag == "PageNetwork") _vm.LoadWol();
    }

    // ---- window behaviour: minimising / closing hides to the tray, the server keeps running
    protected override void OnStateChanged(EventArgs e)
    {
        base.OnStateChanged(e);
        if (WindowState == WindowState.Minimized && _vm.Core.Settings.MinimizeToTray) Hide();
        _vm.SetVisible(IsVisible && WindowState != WindowState.Minimized);
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (ReallyClose) { base.OnClosing(e); return; }
        if (_vm.Core.Settings.MinimizeToTray) { e.Cancel = true; Hide(); return; }
        var r = MessageBox.Show(this, "Closing the window exits the application and stops the remote-control server.\n\nExit now?", "PC Remote Center", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (r != MessageBoxResult.Yes) { e.Cancel = true; return; }
        ReallyClose = true;
        ((App)Application.Current).ExitApp();
    }

    public void ShowAndActivate()
    {
        Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate(); Topmost = true; Topmost = false;
    }

    // ---- server
    private async void OnStartServer(object s, RoutedEventArgs e) => await Run(() => _vm.Core.StartAsync());
    private async void OnStopServer(object s, RoutedEventArgs e) => await Run(() => _vm.Core.StopAsync());
    private async void OnRestartServer(object s, RoutedEventArgs e) => await Run(() => _vm.Core.RestartAsync());

    private async Task Run(Func<Task> a)
    {
        try { await a(); }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "PC Remote Center", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }

    // ---- terminals
    private void RefreshTerminals()
    {
        void Set(TextBlock t, Button b, string shell)
        {
            var s = _vm.Runtime.Terminals.Get(shell);
            t.Text = s == null ? "Not started yet. It starts automatically when a phone opens it, or press Start."
                : s.IsRunning ? $"Running · pid {s.ProcessId} · {s.Screen.Cols}×{s.Screen.Rows} · started {s.StartedUtc.ToLocalTime():HH:mm:ss}"
                : $"Exited with code {s.ExitCode}. Press Restart to create a new session.";
            b.Content = s == null ? "Start" : "Restart";
        }
        Set(txtCmdStatus, btnCmdRestart, "cmd"); Set(txtPsStatus, btnPsRestart, "powershell");
    }

    private void OnShowTerminal(object s, RoutedEventArgs e)
    {
        var shell = (string)((Button)s).Tag;
        try { _vm.Runtime.Terminals.EnsureStarted(shell); _terms.Show(shell); }
        catch (Exception ex) { MessageBox.Show(this, $"Could not start {shell}: {ex.Message}", "PC Remote Center", MessageBoxButton.OK, MessageBoxImage.Warning); }
        RefreshTerminals();
    }

    private void OnRestartTerminal(object s, RoutedEventArgs e)
    {
        var shell = (string)((Button)s).Tag;
        var existing = _vm.Runtime.Terminals.Get(shell);
        if (existing is { IsRunning: true } &&
            MessageBox.Show(this, "Restarting ends everything running in this shell. Continue?", "Restart shell", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        try
        {
            if (existing == null) _vm.Runtime.Terminals.EnsureStarted(shell); else _vm.Runtime.Terminals.Restart(shell);
            _vm.Core.Audit.Write("owner", "terminal.restart", shell);
        }
        catch (Exception ex) { MessageBox.Show(this, $"Could not start {shell}: {ex.Message}", "PC Remote Center", MessageBoxButton.OK, MessageBoxImage.Warning); }
        RefreshTerminals();
    }

    // ---- devices
    private Permission SelectedPerms() =>
        (chkMon.IsChecked == true ? Permission.Monitoring : 0) | (chkTerm.IsChecked == true ? Permission.Terminal : 0) |
        (chkLaunch.IsChecked == true ? Permission.Launch : 0) | (chkPower.IsChecked == true ? Permission.Power : 0) | (chkAdmin.IsChecked == true ? Permission.Admin : 0);

    private void OnCreatePairing(object s, RoutedEventArgs e)
    {
        var p = SelectedPerms();
        if (p == Permission.None) { MessageBox.Show(this, "Select at least one permission.", "Pairing"); return; }
        try { _vm.CreatePairing(p); }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Pairing", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }

    private void OnCancelPairing(object s, RoutedEventArgs e) => _vm.CancelPairing();

    private DeviceVm? SelDevice() => gridDevices.SelectedItem as DeviceVm;

    private void OnRevokeDevice(object s, RoutedEventArgs e)
    {
        var d = SelDevice(); if (d == null) return;
        if (MessageBox.Show(this, $"Revoke '{d.Name}'?\nIt is disconnected immediately and can only connect again after pairing anew.", "Revoke device", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        _vm.Core.Devices.Revoke(d.Id);
        _vm.Core.Audit.Write("owner", "device.revoked", d.Name);
    }

    private void OnDeleteDevice(object s, RoutedEventArgs e)
    {
        var d = SelDevice(); if (d == null) return;
        if (MessageBox.Show(this, $"Remove '{d.Name}' from the list permanently?\nIt is revoked and disconnected first.", "Remove device", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        _vm.Core.Devices.Revoke(d.Id); _vm.Core.Devices.Delete(d.Id);
        _vm.Core.Audit.Write("owner", "device.removed", d.Name);
    }

    private void OnDisconnect(object s, RoutedEventArgs e)
    {
        if (gridConnected.SelectedItem is not ConnVm c) return;
        _vm.Core.Disconnect(c.DeviceId);
        _vm.Core.Audit.Write("owner", "device.disconnect", c.Name);
    }

    private void OnEditPermissions(object s, RoutedEventArgs e)
    {
        var d = SelDevice(); if (d == null) return;
        var boxes = Enum.GetValues<Permission>().Where(p => p != Permission.None).Select(p => new CheckBox { Content = p.ToString(), IsChecked = d.Perm.HasFlag(p), Margin = new Thickness(0, 4, 0, 4), Foreground = (System.Windows.Media.Brush)FindResource("TextBrush") }).ToList();
        var ok = new Button { Content = "Save", Style = (Style)FindResource("Primary"), Margin = new Thickness(0, 12, 0, 0), HorizontalAlignment = HorizontalAlignment.Left };
        var sp = new StackPanel { Margin = new Thickness(20) };
        sp.Children.Add(new TextBlock { Text = $"Permissions for {d.Name}", FontSize = 15, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 8) });
        foreach (var b in boxes) sp.Children.Add(b);
        sp.Children.Add(ok);
        var w = new Window { Title = "Edit permissions", Content = sp, SizeToContent = SizeToContent.WidthAndHeight, MinWidth = 320, Owner = this, WindowStartupLocation = WindowStartupLocation.CenterOwner, Background = (System.Windows.Media.Brush)FindResource("BgBrush"), ResizeMode = ResizeMode.NoResize };
        ok.Click += (_, _) => { w.DialogResult = true; };
        if (w.ShowDialog() != true) return;
        var np = Permission.None;
        foreach (var b in boxes) if (b.IsChecked == true) np |= Enum.Parse<Permission>((string)b.Content);
        _vm.Core.Devices.SetPermissions(d.Id, np);
        _vm.Core.Audit.Write("owner", "device.permissions", $"{d.Name}: {string.Join(',', np.ToNames())}");
    }

    // ---- shortcuts
    private Shortcut? SelShortcut() => gridShortcuts.SelectedItem as Shortcut;

    private void OnShortcutSelected(object s, SelectionChangedEventArgs e)
    {
        var sc = SelShortcut(); if (sc == null) return;
        _editingShortcutId = sc.Id; txtShortcutTitle.Text = "Edit shortcut";
        scName.Text = sc.Name; scDesc.Text = sc.Description; scIcon.Text = sc.Icon; scPath.Text = sc.Path; scArgs.Text = sc.Arguments; scDir.Text = sc.WorkingDirectory;
        scConfirm.IsChecked = sc.Confirm;
        (sc.RunMode switch { "minimized" => rmMin, "maximized" => rmMax, "hidden" => rmHidden, "elevated" => rmElev, _ => rmNormal }).IsChecked = true;
        txtShortcutMsg.Text = "";
    }

    private void OnShortcutNew(object s, RoutedEventArgs e)
    {
        _editingShortcutId = null; txtShortcutTitle.Text = "New shortcut"; gridShortcuts.SelectedItem = null;
        scName.Text = scDesc.Text = scPath.Text = scArgs.Text = scDir.Text = ""; scIcon.Text = "apps"; scConfirm.IsChecked = false; rmNormal.IsChecked = true; txtShortcutMsg.Text = "";
    }

    private void OnBrowseShortcut(object s, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Filter = "Programs (*.exe;*.msc;*.cpl;*.lnk)|*.exe;*.msc;*.cpl;*.lnk" };
        if (dlg.ShowDialog(this) == true) scPath.Text = dlg.FileName;
    }

    private void OnShortcutSave(object s, RoutedEventArgs e)
    {
        var sc = new Shortcut
        {
            Id = _editingShortcutId ?? Guid.NewGuid().ToString("N"), Name = scName.Text, Description = scDesc.Text, Icon = scIcon.Text.Trim(), Path = scPath.Text.Trim(),
            Arguments = scArgs.Text, WorkingDirectory = scDir.Text.Trim(), Confirm = scConfirm.IsChecked == true,
            RunMode = rmMin.IsChecked == true ? "minimized" : rmMax.IsChecked == true ? "maximized" : rmHidden.IsChecked == true ? "hidden" : rmElev.IsChecked == true ? "elevated" : "normal",
        };
        var existing = _editingShortcutId == null ? null : _vm.Core.Shortcuts.Get(_editingShortcutId);
        if (existing != null) sc.SortOrder = existing.SortOrder;
        try
        {
            ShortcutService.Validate(sc, _vm.Core.Settings.AllowElevatedLaunch);
            _vm.Core.Shortcuts.Upsert(sc);
            _vm.Core.Audit.Write("owner", existing == null ? "shortcut.add" : "shortcut.edit", sc.Name);
            _editingShortcutId = sc.Id; txtShortcutTitle.Text = "Edit shortcut"; txtShortcutMsg.Text = "Saved.";
        }
        catch (ProtocolException ex) { txtShortcutMsg.Text = ex.Message; }
    }

    private void OnShortcutDelete(object s, RoutedEventArgs e)
    {
        var sc = SelShortcut(); if (sc == null) return;
        if (MessageBox.Show(this, $"Delete shortcut '{sc.Name}'?", "Delete", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        _vm.Core.Shortcuts.Delete(sc.Id); _vm.Core.Audit.Write("owner", "shortcut.delete", sc.Name); OnShortcutNew(s, e);
    }

    private void MoveShortcut(int dir)
    {
        var sc = SelShortcut(); if (sc == null) return;
        var ids = _vm.Core.Shortcuts.List().Select(x => x.Id).ToList();
        int i = ids.IndexOf(sc.Id), j = i + dir;
        if (j < 0 || j >= ids.Count) return;
        (ids[i], ids[j]) = (ids[j], ids[i]);
        _vm.Core.Shortcuts.Reorder(ids);
        gridShortcuts.SelectedItem = _vm.Shortcuts.FirstOrDefault(x => x.Id == sc.Id);
    }
    private void OnShortcutUp(object s, RoutedEventArgs e) => MoveShortcut(-1);
    private void OnShortcutDown(object s, RoutedEventArgs e) => MoveShortcut(1);

    private void OnShortcutTest(object s, RoutedEventArgs e)
    {
        var sc = SelShortcut(); if (sc == null) return;
        try { _vm.Runtime.Shortcuts.Run(sc, null); _vm.Core.Audit.Write("owner", "shortcut.run", sc.Name); }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Run shortcut", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }

    // ---- logs
    private void OnRefreshLog(object s, RoutedEventArgs e) { _vm.RefreshAudit(); _vm.RefreshHistory(); }

    private void OnLogFilter(object s, TextChangedEventArgs e)
    {
        var view = CollectionViewSource.GetDefaultView(_vm.Audit);
        var f = logFilter.Text.Trim();
        view.Filter = string.IsNullOrEmpty(f) ? null : o => o is AuditVm a && (a.Actor + " " + a.Action + " " + a.Detail).Contains(f, StringComparison.OrdinalIgnoreCase);
    }

    private void OnExportLog(object s, RoutedEventArgs e)
    {
        var dlg = new SaveFileDialog { Filter = "CSV (*.csv)|*.csv", FileName = $"pc-remote-center-audit-{DateTime.Now:yyyyMMdd}.csv" };
        if (dlg.ShowDialog(this) != true) return; // SaveFileDialog already asks before overwriting
        var sb = new StringBuilder("time,actor,action,detail,result\r\n");
        static string Q(string v) => "\"" + v.Replace("\"", "\"\"") + "\"";
        foreach (var a in _vm.Core.Audit.Recent(100000).Select(x => new AuditVm { Time = x.TimeUtc.ToString("o"), Actor = x.Actor, Action = x.Action, Detail = x.Detail, Result = x.Success ? "ok" : "failed" }))
            sb.Append($"{a.Time},{Q(a.Actor)},{Q(a.Action)},{Q(a.Detail)},{a.Result}\r\n");
        File.WriteAllText(dlg.FileName, sb.ToString(), new UTF8Encoding(true));
    }

    // ---- settings forms
    private void LoadSettingsIntoForms()
    {
        var st = _vm.Core.SettingsStore.Load();
        netPort.Text = st.Port.ToString(); netDiscovery.IsChecked = st.DiscoveryEnabled; netLanOnly.IsChecked = st.LanOnly; netExtra.Text = st.ExtraAllowedNetworks; netName.Text = st.ComputerName;
        secElevated.IsChecked = st.AllowElevatedLaunch; secSession.Text = st.SessionMaxHours.ToString(); secAuth.Text = st.AuthTimeoutSeconds.ToString(); secPair.Text = st.PairingCodeMinutes.ToString(); secRetention.Text = st.LogRetentionDays.ToString();
        genStartup.IsChecked = StartupRegistration.IsEnabled(); genTray.IsChecked = st.MinimizeToTray; genAutoStart.IsChecked = st.AutoStartServer; genTermWin.IsChecked = st.ShowTerminalWindows;
    }

    private async void OnSaveNetwork(object s, RoutedEventArgs e)
    {
        netMsg.Text = "";
        if (!int.TryParse(netPort.Text, out var port) || port is < 1024 or > 65535) { netMsg.Text = "Port must be a number between 1024 and 65535."; return; }
        foreach (var part in netExtra.Text.Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries))
            if (!Security.NetworkFilter.TryCidr(part, out _, out _)) { netMsg.Text = $"'{part}' is not a valid address range (use e.g. 10.8.0.0/24)."; return; }
        if (string.IsNullOrWhiteSpace(netName.Text)) { netMsg.Text = "Enter a computer name."; return; }
        if (netLanOnly.IsChecked != true && MessageBox.Show(this, "Accepting connections from any network is risky. Only do this behind a VPN or a firewall that blocks the internet.\n\nContinue?", "Allow all networks", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        var st = _vm.Core.SettingsStore.Load();
        st.Port = port; st.DiscoveryEnabled = netDiscovery.IsChecked == true; st.LanOnly = netLanOnly.IsChecked == true; st.ExtraAllowedNetworks = netExtra.Text.Trim(); st.ComputerName = netName.Text.Trim();
        _vm.Core.SettingsStore.Save(st);
        _vm.Core.Audit.Write("owner", "settings.network", $"port {port}, lan-only {st.LanOnly}");
        try { await _vm.Core.RestartAsync(); netMsg.Text = "Saved. Server restarted."; }
        catch (Exception ex) { netMsg.Text = "Saved, but the server could not start: " + ex.Message; }
    }

    private void OnSaveSecurity(object s, RoutedEventArgs e)
    {
        if (!int.TryParse(secSession.Text, out var sh) || sh is < 1 or > 720 || !int.TryParse(secAuth.Text, out var at) || at is < 3 or > 120 ||
            !int.TryParse(secPair.Text, out var pm) || pm is < 1 or > 30 || !int.TryParse(secRetention.Text, out var rd) || rd is < 1 or > 3650)
        { MessageBox.Show(this, "Please enter valid numbers (session 1-720 h, auth 3-120 s, pairing 1-30 min, retention 1-3650 days).", "Security"); return; }
        var st = _vm.Core.SettingsStore.Load();
        if (secElevated.IsChecked == true && !st.AllowElevatedLaunch &&
            MessageBox.Show(this, "Allow phones with the 'admin' permission to start programs elevated? Windows will still show a UAC prompt on this PC each time.", "Elevated launching", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
        { secElevated.IsChecked = false; return; }
        st.AllowElevatedLaunch = secElevated.IsChecked == true; st.SessionMaxHours = sh; st.AuthTimeoutSeconds = at; st.PairingCodeMinutes = pm; st.LogRetentionDays = rd;
        _vm.Core.SettingsStore.Save(st);
        _vm.Core.Audit.Write("owner", "settings.security", $"elevated launch {st.AllowElevatedLaunch}");
    }

    private void OnSaveGeneral(object s, RoutedEventArgs e)
    {
        var st = _vm.Core.SettingsStore.Load();
        st.MinimizeToTray = genTray.IsChecked == true; st.AutoStartServer = genAutoStart.IsChecked == true; st.ShowTerminalWindows = genTermWin.IsChecked == true;
        st.StartWithWindows = genStartup.IsChecked == true;
        try { StartupRegistration.Set(st.StartWithWindows); }
        catch (Exception ex) { MessageBox.Show(this, "Could not change the Windows startup entry: " + ex.Message); return; }
        _vm.Core.SettingsStore.Save(st);
        _vm.Core.Audit.Write("owner", "settings.general");
    }

    private async void OnRegenCert(object s, RoutedEventArgs e)
    {
        if (MessageBox.Show(this, "Create a new TLS certificate?\n\nEvery paired phone will refuse to connect until it is paired again.", "New certificate", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        await Run(async () =>
        {
            await _vm.Core.StopAsync();
            _vm.Core.Certs.Regenerate();
            _vm.Core.Audit.Write("owner", "certificate.regenerated");
            await _vm.Core.StartAsync();
            _vm.RefreshServer();
        });
    }

    private void OnCreateFirewall(object s, RoutedEventArgs e)
    {
        var st = _vm.Core.Settings;
        if (MessageBox.Show(this, $"Allow PC Remote Center through Windows Firewall?\n\n• TCP {st.Port} (phone connections) and UDP {st.DiscoveryPort} (discovery)\n• Private networks only, from the local subnet only\n\nWindows will ask for administrator permission.", "Firewall rule", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
        try { FirewallService.CreateRules(st.Port, st.DiscoveryPort); _vm.Core.Audit.Write("owner", "firewall.rule.created"); MessageBox.Show(this, "Firewall rule created.", "Firewall"); }
        catch (OperationCanceledException ex) { MessageBox.Show(this, ex.Message, "Firewall"); }
        catch (Exception ex) { MessageBox.Show(this, "Could not create the rule: " + ex.Message, "Firewall", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }

    private void OnRemoveFirewall(object s, RoutedEventArgs e)
    {
        if (MessageBox.Show(this, "Remove the PC Remote Center firewall rules? Phones on the network may no longer reach this PC.", "Firewall rule", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
        try { FirewallService.RemoveRules(); _vm.Core.Audit.Write("owner", "firewall.rule.removed"); }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Firewall"); }
    }

    private void OnRefreshWol(object s, RoutedEventArgs e) => _vm.LoadWol();

    private void OnBackup(object s, RoutedEventArgs e)
    {
        var dlg = new SaveFileDialog { Filter = "PC Remote Center backup (*.json)|*.json", FileName = $"pc-remote-center-backup-{DateTime.Now:yyyyMMdd}.json", OverwritePrompt = true };
        if (dlg.ShowDialog(this) != true) return;
        try { _vm.Backup.Export(dlg.FileName, bkDevices.IsChecked == true, overwrite: true); _vm.Core.Audit.Write("owner", "backup.export"); MessageBox.Show(this, "Backup saved.", "Backup"); }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Backup", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }

    private void OnRestore(object s, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Filter = "PC Remote Center backup (*.json)|*.json" };
        if (dlg.ShowDialog(this) != true) return;
        try
        {
            var sum = _vm.Backup.Inspect(dlg.FileName);
            var devices = sum.DeviceCount > 0 && MessageBox.Show(this, $"The backup contains {sum.DeviceCount} paired device(s). Restore them too?", "Restore", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;
            if (MessageBox.Show(this, $"Restore this backup ({sum.CreatedUtc})?\n\nThis REPLACES your current settings and all {_vm.Shortcuts.Count} shortcuts with {sum.ShortcutCount} from the backup.", "Restore configuration", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
            _vm.Backup.Restore(dlg.FileName, devices);
            _vm.Core.Audit.Write("owner", "backup.restore");
            LoadSettingsIntoForms(); _vm.RefreshAll();
            MessageBox.Show(this, "Restored. Restart the server from Overview to apply network settings.", "Restore");
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Restore", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }
}

using System.Runtime.InteropServices;
using System.Text.Json;
using LLMHostProject.Core;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace LLMHostProject.Desktop;

/// <summary>Where the window was, so it comes back the way the user left it.</summary>
public class WindowState_
{
    public int X { get; set; }
    public int Y { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public bool Maximized { get; set; }
}

/// <summary>
/// The application window: a native window owned by this executable that hosts the web UI in an embedded
/// WebView2 control (no external browser is started). It lives in the notification area too: closing it can keep
/// the server running, a second launch brings it back, and the app can start with Windows.
/// </summary>
public sealed class MainWindow : Form
{
    private readonly WebView2 _web = new() { Dock = DockStyle.Fill };
    private readonly string _url;
    private readonly NotifyIcon _tray = new();
    private readonly ToolStripMenuItem _autoStartItem = new("Start with Windows") { CheckOnClick = true };
    private readonly ToolStripMenuItem _trayItem = new("Keep running in the tray when closed") { CheckOnClick = true };
    private readonly ToolStripMenuItem _updateItem = new("Check for updates") ;
    private bool _quitting;
    private bool _hintShown;
    private readonly bool _startHidden;
    private UpdateInfo? _update;
    private readonly System.Windows.Forms.Timer _updateTimer = new() { Interval = 12 * 60 * 60 * 1000 };

    private static string StateFile => Path.Combine(AppPaths.Root, "window.json");
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValue = "LLMHost";

    public MainWindow(string url, bool startHidden = false)
    {
        _url = url;
        _startHidden = startHidden;
        Text = "LLMHost";
        StartPosition = FormStartPosition.CenterScreen;
        var area = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1440, 900);
        ClientSize = new Size(Math.Min(1400, (int)(area.Width * 0.85)), Math.Min(900, (int)(area.Height * 0.85)));
        MinimumSize = new Size(900, 600);
        BackColor = Color.FromArgb(0x14, 0x16, 0x1a);
        try { Icon = Icon.ExtractAssociatedIcon(Environment.ProcessPath!); } catch { }
        Controls.Add(_web);
        RestoreState();
        BuildTray();
        Load += async (_, _) => await InitializeAsync();
        FormClosing += OnClosing;
        Resize += (_, _) => { if (WindowState == FormWindowState.Minimized) { /* keep in the task bar */ } };
        ResizeEnd += (_, _) => SaveState();
    }

    // ------------------------------------------------------------------ window state

    private void RestoreState()
    {
        try
        {
            if (!File.Exists(StateFile)) return;
            var s = JsonSerializer.Deserialize<WindowState_>(File.ReadAllText(StateFile), Json.Options);
            if (s == null || s.Width < 300 || s.Height < 300) return;
            var bounds = new Rectangle(s.X, s.Y, s.Width, s.Height);
            // only when it is still (mostly) on a visible screen
            if (!Screen.AllScreens.Any(sc => sc.WorkingArea.IntersectsWith(bounds))) return;
            StartPosition = FormStartPosition.Manual;
            Bounds = bounds;
            if (s.Maximized) WindowState = FormWindowState.Maximized;
        }
        catch { /* a damaged file just means defaults */ }
    }

    private void SaveState()
    {
        try
        {
            var b = WindowState == FormWindowState.Normal ? Bounds : RestoreBounds;
            if (b.Width < 300 || b.Height < 300) return;
            AppPaths.EnsureCreated();
            File.WriteAllText(StateFile, JsonSerializer.Serialize(new WindowState_
            { X = b.X, Y = b.Y, Width = b.Width, Height = b.Height, Maximized = WindowState == FormWindowState.Maximized }, Json.Options));
        }
        catch { }
    }

    // ------------------------------------------------------------------ tray

    private static bool AutoStartEnabled()
    {
        using var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunKey);
        return k?.GetValue(RunValue) != null;
    }

    public static void SetAutoStart(bool on)
    {
        using var k = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(RunKey);
        if (on) k.SetValue(RunValue, $"\"{Environment.ProcessPath}\" --minimized");
        else k.DeleteValue(RunValue, false);
    }

    private void BuildTray()
    {
        var menu = new ContextMenuStrip();
        var show = new ToolStripMenuItem("Show LLMHost", null, (_, _) => ShowFromTray()) { Font = new Font(SystemFonts.MenuFont!, FontStyle.Bold) };
        menu.Items.Add(show);
        menu.Items.Add(new ToolStripSeparator());
        _autoStartItem.Checked = AutoStartEnabled();
        _autoStartItem.CheckedChanged += (_, _) => { try { SetAutoStart(_autoStartItem.Checked); } catch (Exception ex) { MessageBox.Show(this, ex.Message, "LLMHost"); } };
        menu.Items.Add(_autoStartItem);
        _trayItem.Checked = AppSettings.Current.MinimizeToTray;
        _trayItem.CheckedChanged += (_, _) => { AppSettings.Current.MinimizeToTray = _trayItem.Checked; AppSettings.Current.Save(); };
        menu.Items.Add(_trayItem);
        _updateItem.Click += async (_, _) => await CheckForUpdatesAsync(interactive: true);
        menu.Items.Add(_updateItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("Quit", null, (_, _) => { _quitting = true; Close(); }));

        _tray.Icon = Icon ?? SystemIcons.Application;
        _tray.Text = "LLMHost";
        _tray.ContextMenuStrip = menu;
        _tray.Visible = true;
        _tray.DoubleClick += (_, _) => ShowFromTray();
        _tray.BalloonTipClicked += (_, _) =>
        {
            if (_balloon == null && _update != null) _ = OfferInstallAsync(_update); else OnBalloonClicked();
        };
    }

    public void ShowFromTray()
    {
        if (InvokeRequired) { BeginInvoke(ShowFromTray); return; }
        Show();
        if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
        Activate();
        TopMost = true; TopMost = false;      // bring to the front
    }

    protected override void SetVisibleCore(bool value)
    {
        // start hidden in the tray when launched at logon
        if (_startHidden && !IsHandleCreated) { CreateHandle(); value = false; }
        base.SetVisibleCore(value);
    }

    private void OnClosing(object? sender, FormClosingEventArgs e)
    {
        SaveState();
        if (!_quitting && e.CloseReason == CloseReason.UserClosing && AppSettings.Current.MinimizeToTray)
        {
            e.Cancel = true;
            Hide();
            if (!_hintShown)
            {
                _hintShown = true;
                _tray.ShowBalloonTip(3000, "LLMHost is still running", "The server keeps running in the notification area. Use the tray icon to quit.", ToolTipIcon.Info);
            }
            return;
        }
        _tray.Visible = false;
        _tray.Dispose();
    }

    // ------------------------------------------------------------------ updates

    private static void OpenUrl(string url)
    {
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true }); } catch { }
    }

    /// <summary>Asks to install the update when the feed gave a verifiable package, otherwise to open the download page.</summary>
    private async Task OfferInstallAsync(UpdateInfo u)
    {
        bool canInstall = u.Asset?.Sha256 != null;
        string ask = $"Version {u.Version} is available (you have {UpdateChecker.CurrentVersion}).\n\n{u.Notes}\n\n" + (canInstall ? "Download and install it now? LLMHost will close and start again." : "Open the download page?");
        if (MessageBox.Show(this, ask, "LLMHost update", MessageBoxButtons.YesNo) != DialogResult.Yes) return;
        if (!canInstall) { OpenUrl(u.Url); return; }
        try
        {
            Cursor = Cursors.WaitCursor;
            string package = await UpdateService.DownloadAsync(u.Asset!);
            var mode = LLMHostProject.Cli.Daemon.Read() != null ? UpdateService.RunMode.Daemon : UpdateService.RunMode.Window;
            UpdateService.StartUpdater(UpdateService.BuildPlan(package, u.Version, mode));
            _quitting = true; Close();
        }
        catch (Exception ex) { Cursor = Cursors.Default; MessageBox.Show(this, "The update was not installed:\n" + ex.Message, "LLMHost update"); }
    }

    private async Task CheckForUpdatesAsync(bool interactive)
    {
        if (string.IsNullOrWhiteSpace(AppSettings.Current.UpdateFeedUrl))
        {
            if (interactive)
                MessageBox.Show(this, "No update feed is configured.\n\nSet one with: llmhost config updatefeed <url>\n(or in Server && Settings).", "LLMHost");
            return;
        }
        var u = await UpdateChecker.CheckAsync();
        _update = u;
        if (u != null)
        {
            _updateItem.Text = $"Update available: {u.Version} (click to install)";
            if (interactive) await OfferInstallAsync(u);
        }
        else if (interactive)
            MessageBox.Show(this, UpdateChecker.LastError != null ? "Could not check for updates:\n" + UpdateChecker.LastError : $"You have the latest version ({UpdateChecker.CurrentVersion}).", "LLMHost");
    }

    // ------------------------------------------------------------------ web view

    // ---- notifications: shown from the tray when the window is minimised, hidden or not in front
    private NotificationEvent? _balloon;

    private void OnNotification(NotificationEvent e)
    {
        try
        {
            BeginInvoke(() =>
            {
                var s = AppSettings.Current.Notifications;
                bool inFront = Visible && WindowState != FormWindowState.Minimized && ContainsFocus;
                if (inFront && s.OnlyWhenMinimised && e.Kind != "test") return;
                _balloon = e;
                _tray.ShowBalloonTip(6000, e.Title, e.Body, e.Kind.EndsWith("failed") ? ToolTipIcon.Warning : ToolTipIcon.Info);
            });
        }
        catch { }
    }

    /// <summary>A click on the notification brings the window back and opens the view it is about.</summary>
    private async void OnBalloonClicked()
    {
        var e = _balloon; _balloon = null;
        ShowFromTray();
        if (e == null) return;
        if (e.Kind == "update" && _update != null) { _ = OfferInstallAsync(_update); return; }
        try { await _web.CoreWebView2.ExecuteScriptAsync($"typeof openView === 'function' && openView({System.Text.Json.JsonSerializer.Serialize(e.View)})"); } catch { }
    }

    // ---- theme: the page draws itself, the window frame and the browser's own parts (scroll bars, form controls) follow too
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    private void OnSettingsSaved() { try { BeginInvoke(ApplyTheme); } catch { } }

    private static bool SystemUsesDark()
    {
        try { return Microsoft.Win32.Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "AppsUseLightTheme", 1) is int v && v == 0; }
        catch { return false; }
    }

    private void ApplyTheme()
    {
        string theme = AppSettings.Current.UiTheme;
        bool dark = theme == "dark" || (theme == "system" && SystemUsesDark());
        try
        {
            _web.CoreWebView2.Profile.PreferredColorScheme = theme == "dark" ? CoreWebView2PreferredColorScheme.Dark
                : theme == "light" ? CoreWebView2PreferredColorScheme.Light : CoreWebView2PreferredColorScheme.Auto;
        }
        catch { }
        try { int v = dark ? 1 : 0; DwmSetWindowAttribute(Handle, 20, ref v, 4); } catch { }     // immersive dark title bar
    }

    private async Task InitializeAsync()
    {
        try
        {
            string data = Path.Combine(AppPaths.Root, "webview2");
            var env = await CoreWebView2Environment.CreateAsync(null, data);
            await _web.EnsureCoreWebView2Async(env);
            var core = _web.CoreWebView2;
            core.Settings.IsStatusBarEnabled = false;
            core.Settings.IsZoomControlEnabled = true;
            // the page binds its own shortcuts (Ctrl+N, Ctrl+P, Ctrl+K ...): the browser must not claim them first
            core.Settings.AreBrowserAcceleratorKeysEnabled = false;
            ApplyTheme();
            AppSettings.Saved += OnSettingsSaved;
            // web notifications of the page (permission is ours to give: this is our own page); everything else goes through the tray below
            core.PermissionRequested += (_, e) => { if (e.PermissionKind == CoreWebView2PermissionKind.Notifications) e.State = CoreWebView2PermissionState.Allow; };
            NotificationHub.Published += OnNotification;

            // keep every navigation inside this window, never spawn an external browser window
            core.NewWindowRequested += (_, e) =>
            {
                e.Handled = true;
                if (Uri.TryCreate(e.Uri, UriKind.Absolute, out var u) && u.Host == new Uri(_url).Host) core.Navigate(e.Uri);
                else OpenUrl(e.Uri);        // external links open in the default browser
            };
            core.DocumentTitleChanged += (_, _) =>
            {
                var t = core.DocumentTitle;
                if (!string.IsNullOrWhiteSpace(t)) Text = t;
            };
            core.NavigationCompleted += (_, e) =>
                LogBuffer.Add("ui", e.IsSuccess ? $"Window loaded {core.Source}" : $"Window navigation failed: {e.WebErrorStatus}");

            core.Navigate(_url);

            // update check: only when the user turned it on
            if (AppSettings.Current.CheckUpdates && !string.IsNullOrWhiteSpace(AppSettings.Current.UpdateFeedUrl))
            {
                _updateTimer.Tick += async (_, _) => await CheckForUpdatesAsync(false);
                _updateTimer.Start();
                _ = Task.Delay(10_000).ContinueWith(_ => BeginInvoke(async () => await CheckForUpdatesAsync(false)));
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show(this,
                "The embedded web view could not be started:\n\n" + ex.Message +
                "\n\nThe Microsoft Edge WebView2 Runtime is required (https://go.microsoft.com/fwlink/p/?LinkId=2124703).\n" +
                $"The server keeps running at {_url}.",
                "LLMHost", MessageBoxButtons.OK, MessageBoxIcon.Error);
            _quitting = true;
            Close();
        }
    }
}

public static class DesktopHost
{
    [DllImport("kernel32.dll")] private static extern IntPtr GetConsoleWindow();
    [DllImport("kernel32.dll")] private static extern uint GetConsoleProcessList(uint[] processList, uint count);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hWnd, int cmd);

    private static string Suffix => Environment.UserName.Replace('\\', '_');
    private const string ShowEventPrefix = "LLMHost.Desktop.Show.";
    private const string MutexPrefix = "LLMHost.Desktop.";

    public static bool IsWebViewAvailable(out string detail)
    {
        try
        {
            detail = CoreWebView2Environment.GetAvailableBrowserVersionString();
            return !string.IsNullOrEmpty(detail);
        }
        catch (Exception ex) { detail = ex.Message; return false; }
    }

    /// <summary>Hides the console window when the exe was started by double-click (it owns its console).</summary>
    public static void HideOwnedConsole()
    {
        try
        {
            var h = GetConsoleWindow();
            if (h == IntPtr.Zero) return;
            if (GetConsoleProcessList(new uint[4], 4) <= 1) ShowWindow(h, 0);
        }
        catch { }
    }

    private static Mutex? _instanceMutex;

    /// <summary>
    /// True when this is the only desktop instance. Otherwise the running one is asked to show its window and
    /// this process should exit.
    /// </summary>
    public static bool AcquireSingleInstance()
    {
        _instanceMutex = new Mutex(true, MutexPrefix + Suffix, out bool createdNew);
        if (createdNew) return true;
        try
        {
            using var ev = EventWaitHandle.OpenExisting(ShowEventPrefix + Suffix);
            ev.Set();
        }
        catch { }
        return false;
    }

    /// <summary>Shows the window and completes (on a dedicated STA thread) when the user quits.</summary>
    public static Task RunWindowAsync(string url, bool startHidden = false)
    {
        var tcs = new TaskCompletionSource();
        var t = new Thread(() =>
        {
            try
            {
                ApplicationConfiguration.Initialize();
                var win = new MainWindow(url, startHidden);
                // a second launch signals this event: bring the window back
                var showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventPrefix + Suffix);
                var waiter = new Thread(() =>
                {
                    while (true)
                    {
                        showEvent.WaitOne();
                        try { if (win.IsHandleCreated && !win.IsDisposed) win.ShowFromTray(); else break; } catch { break; }
                    }
                }) { IsBackground = true, Name = "LLMHost show-window" };
                waiter.Start();
                Application.Run(win);
                tcs.TrySetResult();
            }
            catch (Exception ex) { tcs.TrySetException(ex); }
        }) { Name = "LLMHost UI", IsBackground = false };
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        return tcs.Task;
    }
}

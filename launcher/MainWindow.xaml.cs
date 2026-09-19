using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace ProjectDoorango;

public partial class MainWindow : Window
{
    readonly LauncherService service;
    readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromSeconds(8) };
    readonly object logLock = new();
    Preferences prefs;
    RepositorySnapshot? release;
    CancellationTokenSource? operation;
    Task? activeTask;
    bool busy, refreshing, closing, allowClose;
    readonly bool preview;

    public MainWindow(string root, bool preview = false)
    {
        InitializeComponent();
        this.preview = preview;
        service = new LauncherService(root, Log);
        prefs = service.LoadPreferences();
        GatewayInput.Text = prefs.Gateway;
        AutoStartInput.IsChecked = prefs.AutoStart;
        RootLabel.Text = "Installation folder: " + service.Root;
        ShowPage("Home");
        UpdateControls();
        Log("ProjectDoorango launcher ready. No downloads begin automatically.");
        if (preview) { StatusBadge.Text = "●  READY TO START"; return; }
        timer.Tick += async (_, _) => await RefreshStatus();
        Loaded += async (_, _) => { await RefreshStatus(); timer.Start(); };
        Closing += OnClosing;
    }

    void Log(string text)
    {
        string line = $"[{DateTime.Now:HH:mm:ss}] {text}";
        if (service != null)
            lock (logLock)
                try { File.AppendAllText(Path.Combine(service.State, "launcher.log"), line + Environment.NewLine); }
                catch (IOException) { }
        Dispatcher.BeginInvoke(() =>
        {
            if (LogBox.Text.Length > 60000) LogBox.Text = LogBox.Text[^40000..];
            LogBox.AppendText(line + Environment.NewLine);
            LogBox.ScrollToEnd();
        });
    }

    void UpdateControls()
    {
        ClientStatus.Text = service.GameInstalled ? "Ready to play" : "Download needed";
        string installed = service.InstalledVersion();
        ClientVersion.Text = installed.Length == 40 ? "ProjectDoorango · " + installed[..7] : installed;
        PlayButton.Content = service.GameInstalled ? "Play Durango  →" : "Install & Play  ↓";
        PlayDescription.Text = service.GameInstalled
            ? "Your game is installed. Play starts your server and opens Durango."
            : "One click downloads the game, sets up your world, and starts your adventure.";
        GatewaySummary.Text = prefs.Gateway;
        ServerStatus.Text = service.OwnsServer ? "Running" : service.ServerInstalled ? "Ready to start" : "Not installed";
        ServerHint.Text = service.OwnsServer ? "Started by this launcher" : "Your own world, on this PC";
        PlayButton.IsEnabled = !busy;
        StartButton.IsEnabled = !busy && !service.OwnsServer && service.ServerInstalled;
        StopButton.IsEnabled = !busy && service.OwnsServer;
        SaveButton.IsEnabled = !busy && !service.OwnsServer;
        GatewayInput.IsEnabled = !busy && !service.OwnsServer;
        AutoStartInput.IsEnabled = !busy;
        CheckButton.IsEnabled = !busy;
        InstallClientButton.IsEnabled = !busy && release != null;
        InstallServerButton.IsEnabled = !busy && !Directory.Exists(service.Server);
        InstallServerButton.Content = Directory.Exists(service.Server) ? "Server folder already present" : "Download server files";
    }

    async Task RefreshStatus()
    {
        if (refreshing || closing || preview) return;
        refreshing = true;
        try
        {
            bool online = await service.IsOnlineAsync(prefs.Gateway);
            if (closing) return;
            StatusBadge.Text = online ? "●  SERVER ONLINE" : LauncherService.CanStartLocal(prefs.Gateway) && prefs.AutoStart ? "●  STARTS WHEN YOU PLAY" : "●  SERVER OFFLINE";
            StatusBadge.Foreground = (Brush)new BrushConverter().ConvertFromString(online ? "#F0BE76" : "#E1BF84")!;
            UpdateControls();
            if (online && !service.OwnsServer)
            {
                bool local = LauncherService.ParseGateway(prefs.Gateway).IsLoopback;
                ServerStatus.Text = local ? "Already online" : "Remote connection";
                ServerHint.Text = local ? "Managed outside this launcher" : "Local server is optional";
                if (local) StartButton.IsEnabled = false;
            }
        }
        catch (Exception e) when (e is InvalidOperationException or UriFormatException)
        { StatusBadge.Text = "●  CHECK CONNECTION SETTINGS"; }
        finally { refreshing = false; }
    }

    async Task Run(string message, Func<CancellationToken, Task> action, bool cancellable = true)
    {
        if (busy) return;
        busy = true;
        operation = new CancellationTokenSource();
        OperationText.Text = message;
        Progress.Visibility = Visibility.Visible;
        Progress.IsIndeterminate = true;
        CancelButton.Visibility = cancellable ? Visibility.Visible : Visibility.Collapsed;
        UpdateControls();
        activeTask = Execute();
        await activeTask;

        async Task Execute()
        {
            try { await action(operation.Token); OperationText.Text = "Done. Ready when you are."; }
            catch (OperationCanceledException)
            {
                OperationText.Text = operation.IsCancellationRequested ? "Cancelled. Your installation is safe." : "Connection timed out. Please try again.";
                Log(OperationText.Text);
            }
            catch (Exception e)
            {
                OperationText.Text = e.Message;
                Log("ERROR: " + e.Message);
            }
            finally
            {
                busy = false;
                operation.Dispose(); operation = null;
                Progress.Visibility = Visibility.Collapsed;
                CancelButton.Visibility = Visibility.Collapsed;
                UpdateControls();
                await RefreshStatus();
            }
        }
    }

    IProgress<TransferProgress> Transfer() => new Progress<TransferProgress>(p =>
    {
        OperationText.Text = p.Message;
        Progress.IsIndeterminate = p.Percent < 0;
        Progress.Value = Math.Max(0, p.Percent);
    });

    void Navigate(object sender, RoutedEventArgs e) => ShowPage((string)((Button)sender).Tag);
    internal void ShowPage(string name)
    {
        var pages = new[] { ("Home", HomePage, HomeNav), ("Downloads", DownloadsPage, DownloadsNav),
            ("Settings", SettingsPage, SettingsNav), ("Activity", (FrameworkElement)ActivityPage, ActivityNav) };
        foreach (var (key, page, button) in pages)
        {
            page.Visibility = key == name ? Visibility.Visible : Visibility.Collapsed;
            button.Background = key == name ? new SolidColorBrush(Color.FromRgb(47, 43, 36)) : Brushes.Transparent;
            button.Foreground = key == name ? new SolidColorBrush(Color.FromRgb(240, 190, 118)) : Brushes.LightGray;
        }
        PageTitle.Text = name switch { "Home" => "Welcome to Doorango", "Downloads" => "Downloads & updates", "Settings" => "Settings", _ => "Troubleshooting" };
    }

    async void Play(object sender, RoutedEventArgs e)
    {
        await Run("Preparing your expedition…", async token =>
        {
            if (!service.GameInstalled)
            {
                release = await service.LatestSnapshotAsync(token);
                await service.InstallClientAsync(release, prefs, Transfer(), token);
            }
            if (prefs.AutoStart && LauncherService.CanStartLocal(prefs.Gateway) && !service.ServerInstalled)
                await service.InstallServerAsync(Transfer(), token, release);
            service.SavePreferences(prefs);
            if (!await service.IsOnlineAsync(prefs.Gateway, token))
            {
                if (prefs.AutoStart && LauncherService.CanStartLocal(prefs.Gateway)) await service.StartServerAsync(prefs.Gateway, token);
                else throw new InvalidOperationException("The selected server is offline. Start it or change your connection in Settings.");
            }
            token.ThrowIfCancellationRequested();
            service.LaunchGame();
        });
    }

    async void StartServer(object sender, RoutedEventArgs e) => await Run("Starting local server…", t => service.StartServerAsync(prefs.Gateway, t));
    async void StopServer(object sender, RoutedEventArgs e) => await Run("Saving and stopping server…", _ => service.StopServerAsync(), false);
    async void RefreshClick(object sender, RoutedEventArgs e) => await RefreshStatus();
    async void CheckRelease(object sender, RoutedEventArgs e) => await CheckLatest();
    Task CheckLatest() => Run("Checking ProjectDoorango on GitHub…", async token =>
    {
        release = await service.LatestSnapshotAsync(token);
        string status = service.InstalledVersion() == release.Commit ? "Up to date. " : "";
        ReleaseInfo.Text = $"{status}Revision {release.Revision}  ·  {release.ClientSize / 1073741824d:F2} GiB installed  ·  Sksandeep144/ProjectDoorango";
        InstallClientButton.Content = service.GameInstalled ? $"Install / repair {release.Revision}" : $"Install {release.Revision}";
        Log("Latest ProjectDoorango revision: " + release.Commit + " (per-file Git verification).");
    });

    async void InstallClient(object sender, RoutedEventArgs e)
    {
        if (release == null) return;
        if (service.GameInstalled && MessageBox.Show(this,
            $"Install the client from ProjectDoorango revision {release.Revision} ({release.ClientSize / 1073741824d:F2} GiB installed)? The repository archive will be downloaded if it is not cached. Your current installation will be backed up and account settings preserved.",
            "Install / repair client", MessageBoxButton.OKCancel, MessageBoxImage.Information) != MessageBoxResult.OK) return;
        await Run("Preparing client download…", token => service.InstallClientAsync(release, prefs, Transfer(), token));
    }

    async void InstallServer(object sender, RoutedEventArgs e) => await Run("Downloading server and assets…", t => service.InstallServerAsync(Transfer(), t, release));
    void Cancel(object sender, RoutedEventArgs e) { operation?.Cancel(); OperationText.Text = "Cancelling safely…"; }

    async void SaveSettings(object sender, RoutedEventArgs e)
    {
        await Run("Saving settings…", _ =>
        {
            var updated = new Preferences(GatewayInput.Text.Trim(), AutoStartInput.IsChecked == true);
            service.SavePreferences(updated);
            prefs = updated;
            Log("Connection settings saved. Account key preserved.");
            return Task.CompletedTask;
        });
    }

    void Open(string path) { try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); } catch (Exception e) { OperationText.Text = e.Message; } }
    void OpenGameFolder(object sender, RoutedEventArgs e) { Directory.CreateDirectory(service.Game); Open(service.Game); }
    void OpenPlayerLog(object sender, RoutedEventArgs e)
    {
        string path = Path.Combine(service.Game, "player.log");
        if (File.Exists(path)) Open(path); else OperationText.Text = "No player.log yet. Launch the game once to create it.";
    }
    void OpenBackups(object sender, RoutedEventArgs e) { string path = Path.Combine(service.State, "backups"); Directory.CreateDirectory(path); Open(path); }
    void OpenLauncherLog(object sender, RoutedEventArgs e) => Open(Path.Combine(service.State, "launcher.log"));
    void OpenSdk(object sender, RoutedEventArgs e) => Open("https://dotnet.microsoft.com/en-us/download/dotnet/9.0");
    void OpenCredits(object sender, RoutedEventArgs e) => Open("https://github.com/ShuuuuShi/Durango-CustomServer");

    async void OnClosing(object? sender, CancelEventArgs e)
    {
        if (allowClose) return;
        e.Cancel = true;
        if (closing) return;
        closing = true;
        timer.Stop();
        operation?.Cancel();
        if (activeTask != null) await activeTask;
        try
        {
            await service.StopServerAsync();
            service.Dispose();
            allowClose = true;
            Close();
        }
        catch (Exception error)
        {
            OperationText.Text = error.Message;
            Log("Could not close safely: " + error.Message);
            closing = false;
            timer.Start();
        }
    }
}

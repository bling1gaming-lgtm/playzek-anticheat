using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using Playzek.Client.Core;

namespace Playzek.Desktop;

/// <summary>
/// Interfața jucătorului. Nu face verificări și nu ține token-uri: trimite comenzi serviciului și afișează
/// exact starea raportată de serviciu (care, la rândul ei, vine din confirmările serverului).
/// </summary>
public partial class MainWindow : Window
{
    private readonly DispatcherTimer _poll = new() { Interval = TimeSpan.FromSeconds(1) };
    private ProtectionStatus? _last;
    private bool _registerMode;
    private bool _busy;

    public MainWindow()
    {
        InitializeComponent();
        _poll.Tick += async (_, _) => await RefreshAsync();
        Loaded += async (_, _) => { await RefreshAsync(); _poll.Start(); };
    }

    private async Task RefreshAsync()
    {
        if (_busy) return;
        var r = await Ipc.SendAsync(new IpcRequest(IpcCommands.Status), CancellationToken.None, timeoutMs: 800);
        Render(r);
    }

    private async Task<IpcResponse> CommandAsync(IpcRequest request, int timeoutMs = 20000)
    {
        _busy = true;
        try
        {
            using var cts = new CancellationTokenSource(timeoutMs);
            var r = await Ipc.SendAsync(request, cts.Token);
            Render(r);
            return r;
        }
        catch (OperationCanceledException)
        {
            return new IpcResponse(false, "timeout", "Serviciul nu a răspuns la timp.", _last);
        }
        finally { _busy = false; }
    }

    private void Render(IpcResponse r)
    {
        if (r.Status is null)
        {
            ServiceBanner.Visibility = Visibility.Visible;
            ServiceBannerText.Text = r.Message ?? "Serviciul de protecție nu răspunde.";
            BackendDot.Fill = (Brush)FindResource("Muted");
            BackendText.Text = "Serviciu indisponibil";
            EnableButton.IsEnabled = LoginButton.IsEnabled = false;
            return;
        }
        ServiceBanner.Visibility = Visibility.Collapsed;
        LoginButton.IsEnabled = true;

        var s = r.Status;
        _last = s;
        VersionText.Text = $"Client {s.ClientVersion} · {s.WindowsVersion}";
        (BackendDot.Fill, BackendText.Text) = s.Backend switch
        {
            BackendState.Online => ((Brush)FindResource("Green"), $"Server PlayZek conectat · {Local(s.LastBackendContact)}"),
            BackendState.Offline => ((Brush)FindResource("Red"), "Server PlayZek indisponibil"),
            _ => ((Brush)FindResource("Muted"), "Server: necontactat încă"),
        };

        var loggedIn = s.Account == AccountState.LoggedIn;
        LoginPanel.Visibility = loggedIn ? Visibility.Collapsed : Visibility.Visible;
        DashboardPanel.Visibility = loggedIn ? Visibility.Visible : Visibility.Collapsed;
        if (!loggedIn) return;

        AccountName.Text = s.Username ?? "—";
        AccountSteam.Text = s.SteamId64 ?? "nelegat — serverele nu te pot autoriza";
        AccountStatus.Text = s.BanMessage ?? (s.AccountStatus == "Active" ? "Activ" : s.AccountStatus ?? "—");
        DeviceText.Text = s.DeviceId is null ? $"{s.DeviceName} — neînregistrat ({s.DeviceStatus})" : $"{s.DeviceName} — înregistrat";

        var (dot, title) = s.Protection switch
        {
            ProtectionState.Active => ("Green", "Protejat"),
            ProtectionState.Starting => ("Amber", "Se pornește…"),
            ProtectionState.Reconnecting => ("Amber", "Reconectare…"),
            ProtectionState.Stopped => ("Red", "Oprită"),
            _ => ("Muted", "Inactivă"),
        };
        ProtectionDot.Fill = (Brush)FindResource(dot);
        ProtectionText.Text = title;
        ProtectionDetail.Text = s.ProtectionMessage ?? "Activează protecția înainte să intri pe un server PlayZek.";
        var running = s.Protection is ProtectionState.Active or ProtectionState.Starting or ProtectionState.Reconnecting;
        EnableButton.Visibility = running ? Visibility.Collapsed : Visibility.Visible;
        DisableButton.Visibility = running ? Visibility.Visible : Visibility.Collapsed;
        EnableButton.IsEnabled = s.DeviceId is not null && !_busy;
        GameBox.IsEnabled = !running;

        SessionValidation.Text = s.Protection == ProtectionState.Active ? "validă (confirmată de server)"
            : s.ProtectionCode is { } c && c != "ok" ? c : "—";
        LastHeartbeat.Text = Local(s.LastHeartbeatAt);
        SessionExpires.Text = Local(s.SessionExpiresAt);
        SessionId.Text = s.SessionId?.ToString() ?? "—";

        ChecksMeta.Text = s.ChecksAt is null ? "Nicio verificare încă."
            : $"Ultima verificare: {Local(s.ChecksAt)} · {(s.ChecksReported ? "raportată serverului" : "neraportată (server indisponibil)")}";
        ChecksList.ItemsSource = s.Checks.Select(CheckRow.From).ToList();
        EventsList.ItemsSource = s.Events.Select(e => new EventRow(
            e.At.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture), e.Message,
            (Brush)FindResource(e.Level == "Warning" ? "Amber" : "Silver"))).ToList();
    }

    private static string Local(DateTime? utc) =>
        utc is { } t ? DateTime.SpecifyKind(t, DateTimeKind.Utc).ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture) : "—";

    private async void OnLoginClick(object sender, RoutedEventArgs e)
    {
        LoginError.Visibility = Visibility.Collapsed;
        LoginButton.IsEnabled = false;
        var req = _registerMode
            ? new IpcRequest(IpcCommands.Register, UsernameBox.Text.Trim(), PasswordBox.Password, SteamIdBox.Text.Trim())
            : new IpcRequest(IpcCommands.Login, UsernameBox.Text.Trim(), PasswordBox.Password);
        var r = await CommandAsync(req);
        LoginButton.IsEnabled = true;
        if (r.Ok) { PasswordBox.Clear(); return; }
        LoginError.Text = r.Message;
        LoginError.Visibility = Visibility.Visible;
    }

    private void OnToggleModeClick(object sender, RoutedEventArgs e)
    {
        _registerMode = !_registerMode;
        LoginTitle.Text = _registerMode ? "Cont nou PlayZek" : "Autentificare";
        LoginButton.Content = _registerMode ? "Creează contul" : "Autentificare";
        ToggleModeButton.Content = _registerMode ? "Am deja cont" : "Nu ai cont? Creează unul";
        SteamPanel.Visibility = _registerMode ? Visibility.Visible : Visibility.Collapsed;
        LoginError.Visibility = Visibility.Collapsed;
    }

    private async void OnEnableClick(object sender, RoutedEventArgs e)
    {
        var game = GameCs2.IsChecked == true ? "cs2" : "cs16";
        var r = await CommandAsync(new IpcRequest(IpcCommands.EnableProtection, Game: game));
        if (!r.Ok && r.Code != "network_error") MessageBox.Show(this, r.Message, "PLAYZEK ANTI-CHEAT", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    private async void OnDisableClick(object sender, RoutedEventArgs e) =>
        await CommandAsync(new IpcRequest(IpcCommands.DisableProtection));

    private async void OnLogoutClick(object sender, RoutedEventArgs e) =>
        await CommandAsync(new IpcRequest(IpcCommands.Logout));

    private async void OnRunChecksClick(object sender, RoutedEventArgs e)
    {
        RunChecksButton.IsEnabled = false;
        await CommandAsync(new IpcRequest(IpcCommands.RunChecks), 60000);
        RunChecksButton.IsEnabled = true;
    }

    /// <summary>Exportă raportul de securitate al acestui PC (fără token-uri) într-un fișier JSON ales de jucător.</summary>
    private void OnExportClick(object sender, RoutedEventArgs e)
    {
        if (_last is null) return;
        var dialog = new SaveFileDialog
        {
            FileName = $"playzek-raport-{DateTime.Now:yyyyMMdd-HHmmss}.json",
            Filter = "Raport JSON (*.json)|*.json",
        };
        if (dialog.ShowDialog(this) != true) return;
        var report = new
        {
            generatedAt = DateTime.UtcNow,
            _last.ClientVersion,
            _last.WindowsVersion,
            player = _last.Username,
            deviceId = _last.DeviceId,
            protection = _last.Protection.ToString(),
            sessionId = _last.SessionId,
            checks = _last.Checks,
            events = _last.Events,
        };
        File.WriteAllText(dialog.FileName, JsonSerializer.Serialize(report, new JsonSerializerOptions(BackendClient.Json) { WriteIndented = true }));
    }

    private sealed record EventRow(string Time, string Message, Brush Brush);

    private sealed record CheckRow(string DisplayName, string Summary, string Badge, Brush BadgeBrush)
    {
        private static readonly Brush Green = new SolidColorBrush(Color.FromRgb(0x3D, 0xD6, 0x8C));
        private static readonly Brush Amber = new SolidColorBrush(Color.FromRgb(0xF5, 0xB0, 0x3D));
        private static readonly Brush Red = new SolidColorBrush(Color.FromRgb(0xE5, 0x38, 0x3B));
        private static readonly Brush Grey = new SolidColorBrush(Color.FromRgb(0x8A, 0x91, 0x9C));

        public static CheckRow From(CheckOutcome c) => c.Result switch
        {
            "Pass" => new(c.DisplayName, c.Summary, "OK", Green),
            "Warning" => new(c.DisplayName, c.Summary, "ATENȚIE", Amber),
            "Fail" => new(c.DisplayName, c.Summary, "NESUPORTAT", Red),
            "NotApplicable" => new(c.DisplayName, c.Summary, "N/A", Grey),
            _ => new(c.DisplayName, c.Summary, "EROARE", Amber),
        };
    }
}

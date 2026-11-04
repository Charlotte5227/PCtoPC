using System.Diagnostics;
using System.IO;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using PCtoPC.Core;
using Validation = PCtoPC.Core.Validation;

namespace PCtoPC.Client;

public partial class MainWindow : Window
{
    private readonly ClientSettings settings;
    private readonly string settingsPath = Path.Combine(SecretFile.DefaultDirectory, "client.secret");
    private readonly bool preview;
    private bool busy;
    private PcProfile? Selected => ProfileList.SelectedItem as PcProfile;
    private static readonly string[] Resolutions = ["1280x720", "1920x1080", "2560x1440", "3840x2160"];
    public MainWindow(bool preview = false)
    {
        this.preview = preview;
        InitializeComponent();
        settings = !preview && File.Exists(settingsPath) ? JsonData.Read<ClientSettings>(SecretFile.Read(settingsPath)) : new ClientSettings();
        if (settings.MoonlightPath.Length == 0) settings.MoonlightPath = Moonlight.Find() ?? "";
        MoonlightPath.Text = settings.MoonlightPath;
        Resolution.ItemsSource = Resolutions;
        FrameRate.ItemsSource = new[] { 30, 60, 90, 120, 144, 240 };
        Codec.ItemsSource = new[] { "H.264", "HEVC", "AV1", "auto" };
        if (preview)
        {
            settings.Profiles.Add(new PcProfile { Name = "リビングのPC", Host = "192.168.1.20", Mac = "02:12:34:56:78:9A", WanWakeHost = "home.example.net" });
            settings.Profiles.Add(new PcProfile { Name = "仕事用PC", Host = "work-pc.example.ts.net" });
        }
        RefreshProfiles();
        if (settings.Profiles.Count > 0) ProfileList.SelectedIndex = 0;
        else LoadForm(new PcProfile());
    }
    private void Persist()
    {
        if (preview) return;
        SecretFile.Write(settingsPath, JsonSerializer.Serialize(settings, JsonData.Options));
    }
    private void RefreshProfiles()
    {
        var selection = Selected;
        ProfileList.ItemsSource = null;
        ProfileList.ItemsSource = settings.Profiles;
        WakeProfilePick.ItemsSource = null;
        WakeProfilePick.ItemsSource = settings.Profiles;
        if (selection != null && settings.Profiles.Contains(selection)) ProfileList.SelectedItem = selection;
        Relay.ItemsSource = new[] { new PcProfile { Id = "", Name = "（中継PCを選択）" } }.Concat(settings.Profiles.Where(p => p.Agent != null)).ToList();
    }
    private void LoadForm(PcProfile profile)
    {
        PcName.Text = profile.Name; HostName.Text = profile.Host; AppName.Text = profile.Application;
        Resolution.SelectedItem = $"{profile.Width}x{profile.Height}"; FrameRate.SelectedItem = profile.Fps;
        Codec.SelectedItem = profile.Codec; Bitrate.Text = (profile.BitrateKbps / 1000m).ToString();
        Fullscreen.IsChecked = profile.Fullscreen; Overlay.IsChecked = profile.Overlay;
        MacAddress.Text = profile.Mac; BroadcastAddress.Text = profile.Broadcast; WakeId.Text = profile.WakeTargetId;
        WanHostName.Text = profile.WanWakeHost; WanPort.Text = profile.WanWakePort.ToString();
        WakeProfilePick.SelectedItem = profile;
        Relay.SelectedItem = Relay.Items.Cast<PcProfile>().FirstOrDefault(p => p.Id == profile.RelayProfileId);
        AgentInfo.Text = profile.Agent == null ? "電源操作には被接続側Agentの接続ファイルが必要です。" : $"Agent登録済み · {profile.Agent.Host}:{profile.Agent.Port}";
    }
    private PcProfile SaveForm()
    {
        var original = Selected ?? throw new ArgumentException("先に接続先を追加してください。");
        // Validate a copy so a rejected edit cannot corrupt the stored profile.
        var profile = JsonData.Read<PcProfile>(JsonSerializer.Serialize(original, JsonData.Options));
        profile.Name = PcName.Text.Trim(); profile.Host = HostName.Text.Trim(); profile.Application = AppName.Text.Trim();
        var resolution = (Resolution.SelectedItem as string ?? throw new ArgumentException("解像度を選択してください。")).Split('x');
        profile.Width = int.Parse(resolution[0]); profile.Height = int.Parse(resolution[1]);
        profile.Fps = FrameRate.SelectedItem is int fps ? fps : 60;
        profile.BitrateKbps = checked((int)(decimal.Parse(Bitrate.Text) * 1000m));
        profile.Codec = Codec.SelectedItem as string ?? "H.264";
        profile.Fullscreen = Fullscreen.IsChecked == true; profile.Overlay = Overlay.IsChecked == true;
        profile.Mac = MacAddress.Text.Trim(); profile.Broadcast = BroadcastAddress.Text.Trim();
        profile.WanWakeHost = WanHostName.Text.Trim();
        if (!int.TryParse(WanPort.Text.Trim(), out var wanPort)) throw new ArgumentException("外部WoLのUDPポートは1～65535の整数で指定してください。");
        profile.WanWakePort = wanPort;
        profile.RelayProfileId = (Relay.SelectedItem as PcProfile)?.Id ?? ""; profile.WakeTargetId = WakeId.Text.Trim();
        profile.Validate();
        settings.Profiles[settings.Profiles.IndexOf(original)] = profile;
        Persist(); RefreshProfiles(); ProfileList.SelectedItem = profile;
        return profile;
    }
    private void ProfileList_SelectionChanged(object sender, SelectionChangedEventArgs e) { if (Selected is { } profile) LoadForm(profile); }
    private void WakeProfilePick_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (WakeProfilePick.SelectedItem is PcProfile profile && !ReferenceEquals(ProfileList.SelectedItem, profile)) ProfileList.SelectedItem = profile;
    }
    public void ShowWanPreview() => MainTabs.SelectedIndex = 1;
    private void Add_Click(object sender, RoutedEventArgs e)
    {
        var profile = new PcProfile(); settings.Profiles.Add(profile); RefreshProfiles(); ProfileList.SelectedItem = profile;
        StatusText.Text = "アドレスを入力して保存してください。";
    }
    private void Delete_Click(object sender, RoutedEventArgs e) => RunSync(() =>
    {
        if (Selected is not { } profile) return;
        if (MessageBox.Show($"「{profile.Name}」を削除しますか？", "接続先の削除", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
        settings.Profiles.Remove(profile);
        foreach (var other in settings.Profiles.Where(p => p.RelayProfileId == profile.Id)) other.RelayProfileId = "";
        Persist(); RefreshProfiles();
        if (settings.Profiles.Count > 0) ProfileList.SelectedIndex = 0; else LoadForm(new PcProfile());
    });
    private void Save_Click(object sender, RoutedEventArgs e) => RunSync(() => { SaveForm(); Log("接続先を保存しました。"); });
    private void Import_Click(object sender, RoutedEventArgs e) => RunSync(() =>
    {
        var dialog = new OpenFileDialog { Filter = "PCtoPC接続ファイル|*.pctopc.json|JSON|*.json" };
        if (dialog.ShowDialog() != true) return;
        if (new FileInfo(dialog.FileName).Length > 16384) throw new ArgumentException("接続ファイルが大きすぎます。");
        var pairing = JsonData.Read<Pairing>(File.ReadAllText(dialog.FileName)); pairing.Validate();
        if (MessageBox.Show($"信頼するPCから直接受け取ったファイルですか？\n\n{pairing.Name}\n{pairing.Host}:{pairing.Port}\n証明書 SHA-256:\n{pairing.CertificateSha256}\n\n取り込み後は接続ファイルを削除してください。", "Agentの接続情報を取り込む", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
        var existing = settings.Profiles.FirstOrDefault(p => p.Agent?.CertificateSha256 == pairing.CertificateSha256);
        if (existing == null) { existing = new PcProfile { Name = pairing.Name, Host = pairing.Host }; settings.Profiles.Add(existing); }
        existing.Agent = pairing; Persist(); RefreshProfiles(); ProfileList.SelectedItem = existing;
        Log("接続ファイルを取り込みました。Moonlightのペアリングは別途必要です。");
    });
    private string MoonlightExecutable()
    {
        var path = MoonlightPath.Text.Trim();
        if (!Path.IsPathFullyQualified(path) || !File.Exists(path) || !path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            throw new FileNotFoundException("設定とログからMoonlight.exeを選択してください。未導入の場合はMoonlight公式からインストールしてください。");
        return path;
    }
    private void Connect_Click(object sender, RoutedEventArgs e) => RunSync(() =>
    {
        var profile = SaveForm(); var info = Moonlight.Stream(MoonlightExecutable(), profile);
        var process = Process.Start(info) ?? throw new IOException("Moonlightを起動できません。");
        process.EnableRaisingEvents = true;
        process.Exited += (_, _) => { var code = process.ExitCode; process.Dispose(); Dispatcher.InvokeAsync(() => Log($"Moonlight終了 · code {code}")); };
        Log($"Moonlightを起動しました · {profile.Name} · {profile.Width}×{profile.Height} / {profile.Fps}fps");
    });
    private async void Pair_Click(object sender, RoutedEventArgs e) => await RunAsync(async () =>
    {
        var profile = SaveForm();
        var pin = RandomNumberGenerator.GetInt32(1000, 10000).ToString();
        var dialog = new Window { Owner = this, Title = "SunshineでPINを入力してください", Width = 500, Height = 250, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        dialog.Content = new TextBlock { Text = $"接続先のSunshine設定画面の「PIN」に入力してください。\n\n{pin}\n\nこのウィンドウを閉じてもペアリングは最大90秒待機します。", Margin = new Thickness(24), FontSize = 18, Foreground = System.Windows.Media.Brushes.Black };
        dialog.Show();
        try
        {
            var result = await ExecuteAsync(Moonlight.Command(MoonlightExecutable(), "pair", profile.Host, "--pin", pin), TimeSpan.FromSeconds(90));
            if (result.Code != 0) throw new IOException("ペアリングに失敗しました。\n" + result.Output);
            Log("Moonlightのペアリングが完了しました。");
        }
        finally { dialog.Close(); }
    });
    private void BrowseMoonlight_Click(object sender, RoutedEventArgs e) => RunSync(() =>
    {
        var dialog = new OpenFileDialog { Filter = "Moonlight|Moonlight.exe|実行ファイル|*.exe" };
        if (dialog.ShowDialog() != true) return;
        settings.MoonlightPath = dialog.FileName; MoonlightPath.Text = dialog.FileName; Persist(); Log("Moonlightの場所を保存しました。");
    });
    private void LanPreset_Click(object sender, RoutedEventArgs e) { Resolution.SelectedItem = "1920x1080"; FrameRate.SelectedItem = 60; Bitrate.Text = "20"; Codec.SelectedItem = "H.264"; }
    private void WanPreset_Click(object sender, RoutedEventArgs e) { Resolution.SelectedItem = "1280x720"; FrameRate.SelectedItem = 60; Bitrate.Text = "8"; Codec.SelectedItem = "H.264"; }
    private Pairing RequiredAgent(PcProfile profile) => profile.Agent ?? throw new ArgumentException("被接続側でAgentを初期化し、接続ファイルを取り込んでください。");
    private async void Status_Click(object sender, RoutedEventArgs e) => await RunAsync(async () =>
    {
        var profile = SaveForm(); using var agent = new AgentClient(RequiredAgent(profile));
        var status = await agent.StatusAsync();
        Log($"Agent {status.Name} · 電源許可: {status.PowerEnabled} · 模擬動作: {status.DryRun} · 予約: {status.PendingAction ?? "なし"} · Wake ID: {string.Join(", ", status.WakeTargets)}");
    });
    private Task PowerAsync(string action, string label) => RunAsync(async () =>
    {
        var profile = SaveForm(); var pairing = RequiredAgent(profile);
        if (action != "cancel" && MessageBox.Show($"「{profile.Name}」で{label}を実行しますか？\n\n電源管理の接続先: {pairing.Host}:{pairing.Port}\n保存していない作業を確認してください。10秒間は取り消せます。", "電源操作", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        using var agent = new AgentClient(pairing); Log(await agent.PowerAsync(action));
    });
    private async void Sleep_Click(object sender, RoutedEventArgs e) => await PowerAsync("sleep", "スリープ");
    private async void Restart_Click(object sender, RoutedEventArgs e) => await PowerAsync("restart", "再起動");
    private async void Shutdown_Click(object sender, RoutedEventArgs e) => await PowerAsync("shutdown", "シャットダウン");
    private async void Cancel_Click(object sender, RoutedEventArgs e) => await PowerAsync("cancel", "取り消し");
    private async void WakeLan_Click(object sender, RoutedEventArgs e) => await RunAsync(async () =>
    {
        var profile = SaveForm(); await WakeOnLan.SendAsync(profile.Mac, profile.Broadcast); Log("LANへWoLを送信しました。起動完了は別途確認してください。");
    });
    private async void WakeWan_Click(object sender, RoutedEventArgs e) => await RunAsync(async () =>
    {
        var profile = SaveForm();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        try
        {
            var endpoint = await WakeOnLan.SendToHostAsync(profile.Mac, profile.WanWakeHost, profile.WanWakePort, timeout.Token);
            Log($"外部WoLを3回送信しました · {profile.WanWakeHost} → {endpoint} · MAC {profile.Mac}。起動完了は別途確認してください。");
        }
        catch (OperationCanceledException) { throw new IOException("外部WoLの送信がタイムアウトしました。送信先とDNS設定を確認してください。"); }
        catch (SocketException ex) { throw new IOException("外部WoLを送信できません。送信先のIPv4／DDNS名とネットワーク接続を確認してください。\n" + ex.Message, ex); }
    });
    private async void WakeRelay_Click(object sender, RoutedEventArgs e) => await RunAsync(async () =>
    {
        var profile = SaveForm();
        var relay = settings.Profiles.FirstOrDefault(p => p.Id == profile.RelayProfileId) ?? throw new ArgumentException("常時稼働する中継PCを選択してください。");
        if (relay.Id == profile.Id) throw new ArgumentException("起動するPCとは別の中継PCを選択してください。");
        using var agent = new AgentClient(RequiredAgent(relay)); Log(await agent.WakeAsync(profile.WakeTargetId));
    });
    private async void Diagnose_Click(object sender, RoutedEventArgs e) => await RunAsync(async () =>
    {
        var profile = SaveForm(); var timer = Stopwatch.StartNew();
        using var tcp = new TcpClient();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(4));
        try
        {
            await tcp.ConnectAsync(profile.Host, 47989, timeout.Token);
            Log($"Sunshine TCP 47989 到達可 · 接続確立 {timer.ElapsedMilliseconds}ms（映像遅延ではありません）");
        }
        catch (Exception ex) when (ex is SocketException or OperationCanceledException) { Log("Sunshine TCP 47989へ到達できません。起動・ファイアウォール・接続先を確認してください。"); }
        var tailscale = FindTailscale();
        if (tailscale != null)
        {
            var result = await ExecuteAsync(Moonlight.Command(tailscale, "ping", "--c", "3", "--timeout", "3s", profile.Host), TimeSpan.FromSeconds(15));
            Log("Tailscale ping（LAN専用アドレスでは失敗する場合があります）:\n" + result.Output);
        }
        if (profile.Agent != null)
        {
            using var agent = new AgentClient(profile.Agent); var status = await agent.StatusAsync(); Log($"Agent到達可 · {status.Name} · power={status.PowerEnabled}");
        }
    });
    private static string? FindTailscale()
    {
        var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Tailscale", "tailscale.exe");
        return File.Exists(path) ? path : null;
    }
    private async void Tailscale_Click(object sender, RoutedEventArgs e) => await RunAsync(async () =>
    {
        var path = FindTailscale() ?? throw new FileNotFoundException("Tailscaleをインストールしてログインしてください。");
        var result = await ExecuteAsync(Moonlight.Command(path, "status", "--json"), TimeSpan.FromSeconds(10));
        if (result.Code != 0) throw new IOException("Tailscale: " + result.Output);
        using var json = JsonDocument.Parse(result.Output);
        var peers = new List<PcProfile>();
        if (json.RootElement.TryGetProperty("Peer", out var peerObject) && peerObject.ValueKind == JsonValueKind.Object)
            foreach (var entry in peerObject.EnumerateObject())
            {
                var item = entry.Value;
                if (!item.TryGetProperty("TailscaleIPs", out var ips) || ips.GetArrayLength() == 0) continue;
                var ip = ips[0].GetString()!;
                var name = item.TryGetProperty("HostName", out var n) ? n.GetString() ?? ip : ip;
                peers.Add(new PcProfile { Name = name, Host = ip });
            }
        if (peers.Count == 0) throw new IOException("Tailscaleの他の端末が見つかりません。");
        var list = new ListBox { ItemsSource = peers, Margin = new Thickness(16), DisplayMemberPath = "Name" };
        var picker = new Window { Owner = this, Title = "追加するTailscale端末をダブルクリック", Width = 460, Height = 400, Content = list, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        list.MouseDoubleClick += (_, _) =>
        {
            if (list.SelectedItem is not PcProfile profile) return;
            settings.Profiles.Add(profile); Persist(); RefreshProfiles(); ProfileList.SelectedItem = profile; picker.Close();
        };
        picker.ShowDialog();
    });
    private static string AgentExecutable()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "agent", "PCtoPC.Agent.exe");
        if (!File.Exists(path)) path = Path.Combine(AppContext.BaseDirectory, "PCtoPC.Agent.exe");
        return File.Exists(path) ? path : throw new FileNotFoundException("PCtoPC.Agent.exeが見つかりません。配布ZIPをフォルダーごと展開してください。");
    }
    private async void InitAgent_Click(object sender, RoutedEventArgs e) => await RunAsync(async () =>
    {
        var host = Validation.Host(LocalHost.Text); var listen = System.Net.IPAddress.Parse(ListenAddress.Text.Trim()).ToString();
        var dialog = new SaveFileDialog { Filter = "PCtoPC接続ファイル|*.pctopc.json", FileName = Environment.MachineName + ".pctopc.json" };
        if (dialog.ShowDialog() != true) return;
        var info = Moonlight.Command(AgentExecutable(), "init", "--host", host, "--listen", listen, "--out", dialog.FileName);
        if (EnablePower.IsChecked == true) info.ArgumentList.Add("--enable-power");
        var result = await ExecuteAsync(info, TimeSpan.FromSeconds(30));
        if (result.Code != 0) throw new IOException(result.Output);
        Log(result.Output);
        MessageBox.Show("接続ファイルを保存しました。操作するPCへ安全に渡してください。\n続けてAgentを起動し、READMEのファイアウォール設定を行ってください。", "初期化完了");
    });
    private void StartAgent_Click(object sender, RoutedEventArgs e) => RunSync(() =>
    {
        Process.Start(new ProcessStartInfo(AgentExecutable(), "serve") { UseShellExecute = true }); Log("Agentの起動を要求しました。コンソールの起動結果を確認してください。");
    });
    private static void OpenUrl(string url) => Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
    private void SunshineSite_Click(object sender, RoutedEventArgs e) => RunSync(() => OpenUrl("https://github.com/LizardByte/Sunshine/releases/latest"));
    private void MoonlightSite_Click(object sender, RoutedEventArgs e) => RunSync(() => OpenUrl("https://moonlight-stream.org/"));
    private void TailscaleSite_Click(object sender, RoutedEventArgs e) => RunSync(() => OpenUrl("https://tailscale.com/download/windows"));
    private void SunshineSettings_Click(object sender, RoutedEventArgs e) => RunSync(() => OpenUrl("https://localhost:47990"));
    private async Task RunAsync(Func<Task> action)
    {
        if (busy) { StatusText.Text = "前の操作が完了するまでお待ちください。"; return; }
        busy = true; StatusText.Text = "処理中…";
        try { await action(); }
        catch (Exception ex) { Report(ex); }
        finally { busy = false; if (StatusText.Text == "処理中…") StatusText.Text = "待機中"; }
    }
    private void RunSync(Action action) { try { action(); } catch (Exception ex) { Report(ex); } }
    private void Report(Exception ex) { Log("エラー: " + ex.Message); MessageBox.Show(this, ex.Message, "PCtoPC", MessageBoxButton.OK, MessageBoxImage.Information); }
    private void Log(string text)
    {
        LogBox.AppendText($"[{DateTime.Now:HH:mm:ss}] {text}\n"); LogBox.ScrollToEnd(); StatusText.Text = text.Split('\n')[0];
    }
    private static async Task<(int Code, string Output)> ExecuteAsync(ProcessStartInfo info, TimeSpan timeout)
    {
        info.RedirectStandardOutput = true; info.RedirectStandardError = true; info.CreateNoWindow = true;
        using var process = Process.Start(info) ?? throw new IOException("実行ファイルを起動できません。");
        var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
        using var cancellation = new CancellationTokenSource(timeout);
        try { await process.WaitForExitAsync(cancellation.Token); }
        catch (OperationCanceledException) { try { process.Kill(true); } catch (InvalidOperationException) { } throw new IOException("処理がタイムアウトしました。"); }
        return (process.ExitCode, (await stdout + "\n" + await stderr).Trim());
    }
}

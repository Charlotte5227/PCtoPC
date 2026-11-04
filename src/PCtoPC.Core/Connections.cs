using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace PCtoPC.Core;

public static class Moonlight
{
    public static ProcessStartInfo Stream(string executable, PcProfile profile)
    {
        profile.Validate();
        return Command(executable, "stream", profile.Host, profile.Application,
            "--resolution", $"{profile.Width}x{profile.Height}", "--fps", profile.Fps.ToString(),
            "--bitrate", profile.BitrateKbps.ToString(), "--video-codec", profile.Codec,
            "--video-decoder", "hardware", "--display-mode", profile.Fullscreen ? "borderless" : "windowed",
            "--absolute-mouse", "--no-vsync", "--no-frame-pacing", "--no-quit-after", "--no-hdr", "--no-yuv444",
            "--capture-system-keys", "always", "--audio-config", "stereo",
            profile.Overlay ? "--performance-overlay" : "--no-performance-overlay");
    }
    public static ProcessStartInfo Command(string executable, params string[] arguments)
    {
        var info = new ProcessStartInfo(executable) { UseShellExecute = false };
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        return info;
    }
    public static string? Find()
    {
        string[] candidates = [Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Moonlight Game Streaming", "Moonlight.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Moonlight Game Streaming", "Moonlight.exe")];
        return candidates.FirstOrDefault(File.Exists);
    }
}

public static class WakeOnLan
{
    public static byte[] MagicPacket(string mac)
    {
        mac = mac.Replace(":", "").Replace("-", "");
        if (mac.Length != 12 || !mac.All(Uri.IsHexDigit)) throw new ArgumentException("MACは AA:BB:CC:DD:EE:FF の形式です。");
        var address = Convert.FromHexString(mac);
        if ((address[0] & 1) != 0 || address.All(b => b == 0)) throw new ArgumentException("ユニキャストMACアドレスが必要です。");
        var packet = new byte[102];
        Array.Fill(packet, (byte)255, 0, 6);
        for (var i = 0; i < 16; i++) address.CopyTo(packet, 6 + i * 6);
        return packet;
    }
    public static async Task SendAsync(string mac, string broadcast, string? localAddress = null, CancellationToken cancellationToken = default)
    {
        var packet = MagicPacket(mac);
        var address = Validation.IPv4(broadcast);
        using var udp = new UdpClient(AddressFamily.InterNetwork) { EnableBroadcast = true };
        if (!string.IsNullOrWhiteSpace(localAddress)) udp.Client.Bind(new IPEndPoint(Validation.IPv4(localAddress), 0));
        for (var i = 0; i < 3; i++)
        {
            await udp.SendAsync(packet, new IPEndPoint(address, 9), cancellationToken);
            await Task.Delay(50, cancellationToken);
        }
    }
}

public sealed class AgentClient : IDisposable
{
    private readonly HttpClient http;
    public AgentClient(Pairing pairing)
    {
        pairing.Validate();
        var pin = pairing.CertificateSha256;
        var handler = new HttpClientHandler { AllowAutoRedirect = false, UseProxy = false };
        handler.ServerCertificateCustomValidationCallback = (_, certificate, _, _) =>
            certificate != null && CertificateMatches(certificate, pin);
        http = new HttpClient(handler) { BaseAddress = pairing.BaseUri, Timeout = TimeSpan.FromSeconds(8) };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", pairing.Token);
    }
    public static bool CertificateMatches(X509Certificate2 certificate, string pin)
    {
        Validation.Hex64(pin);
        var now = DateTime.UtcNow;
        return now >= certificate.NotBefore.ToUniversalTime() && now <= certificate.NotAfter.ToUniversalTime()
            && CryptographicOperations.FixedTimeEquals(certificate.GetCertHash(HashAlgorithmName.SHA256), Convert.FromHexString(pin));
    }
    public async Task<AgentStatus> StatusAsync(CancellationToken cancellationToken = default) =>
        await http.GetFromJsonAsync<AgentStatus>("v1/status", JsonData.Options, cancellationToken) ?? throw new IOException("応答が空です。");
    public Task<string> PowerAsync(string action, CancellationToken cancellationToken = default) => PostAsync("v1/power", new PowerRequest(action), cancellationToken);
    public Task<string> WakeAsync(string id, CancellationToken cancellationToken = default)
    {
        Validation.Id(id);
        return PostAsync("v1/wake/" + Uri.EscapeDataString(id), new { }, cancellationToken);
    }
    private async Task<string> PostAsync<T>(string path, T body, CancellationToken cancellationToken)
    {
        using var response = await http.PostAsJsonAsync(path, body, JsonData.Options, cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new IOException($"Agent: {(int)response.StatusCode} {await response.Content.ReadAsStringAsync(cancellationToken)}");
        return (await response.Content.ReadFromJsonAsync<ApiReply>(JsonData.Options, cancellationToken))?.Message ?? "完了";
    }
    public void Dispose() => http.Dispose();
}

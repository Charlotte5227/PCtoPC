using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace PCtoPC.Core;

public static partial class Validation
{
    [GeneratedRegex(@"^[a-zA-Z0-9][a-zA-Z0-9.\-]{0,252}$")]
    private static partial Regex HostPattern();
    [GeneratedRegex(@"^[a-fA-F0-9]{64}$")]
    private static partial Regex HexPattern();
    [GeneratedRegex(@"^[a-zA-Z0-9][a-zA-Z0-9_\-]{0,63}$")]
    private static partial Regex IdPattern();
    public static string Host(string host)
    {
        host = host.Trim();
        if (!IPAddress.TryParse(host, out _) && !HostPattern().IsMatch(host))
            throw new ArgumentException("IPアドレスまたはホスト名を入力してください（ポート・URLは不要）。");
        if (host.Contains('%')) throw new ArgumentException("スコープ付きIPv6は未対応です。");
        return host;
    }
    public static void Hex64(string value)
    {
        if (!HexPattern().IsMatch(value)) throw new ArgumentException("認証情報が不正です。");
    }
    public static void Id(string value)
    {
        if (!IdPattern().IsMatch(value)) throw new ArgumentException("IDは英数字・ハイフン・アンダースコアの64文字以内です。");
    }
    public static IPAddress IPv4(string value)
    {
        if (!IPAddress.TryParse(value, out var address) || address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
            throw new ArgumentException("IPv4アドレスを入力してください。");
        return address;
    }
}

public sealed class Pairing
{
    public int Version { get; set; } = 1;
    public string Name { get; set; } = Environment.MachineName;
    public string Host { get; set; } = "";
    public int Port { get; set; } = 47831;
    public string CertificateSha256 { get; set; } = "";
    public string Token { get; set; } = "";
    public Uri BaseUri => new UriBuilder(Uri.UriSchemeHttps, Validation.Host(Host), Port).Uri;
    public void Validate()
    {
        if (Version != 1) throw new ArgumentException("この接続ファイルのバージョンは未対応です。");
        Host = Validation.Host(Host);
        if (Port is < 1024 or > 65535) throw new ArgumentException("ポートは1024～65535です。");
        Validation.Hex64(CertificateSha256);
        Validation.Hex64(Token);
        if (string.IsNullOrWhiteSpace(Name) || Name.Length > 100) throw new ArgumentException("PC名は1～100文字です。");
    }
}

public sealed class PcProfile
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "新しいPC";
    public string Host { get; set; } = "";
    public string Application { get; set; } = "Desktop";
    public Pairing? Agent { get; set; }
    public int Width { get; set; } = 1920;
    public int Height { get; set; } = 1080;
    public int Fps { get; set; } = 60;
    public int BitrateKbps { get; set; } = 20000;
    public string Codec { get; set; } = "H.264";
    public bool Fullscreen { get; set; }
    public bool Overlay { get; set; } = true;
    public string Mac { get; set; } = "";
    public string Broadcast { get; set; } = "255.255.255.255";
    public string RelayProfileId { get; set; } = "";
    public string WakeTargetId { get; set; } = "";
    public void Validate()
    {
        Host = Validation.Host(Host);
        if (string.IsNullOrWhiteSpace(Name) || Name.Length > 100) throw new ArgumentException("PC名は1～100文字です。");
        if (string.IsNullOrWhiteSpace(Application) || Application.StartsWith('-') || Application.Length > 200 || Application.Any(char.IsControl))
            throw new ArgumentException("配信アプリ名が不正です。");
        if (Width is < 640 or > 7680 || Height is < 360 or > 4320 || Fps is < 30 or > 240 || BitrateKbps is < 1000 or > 150000)
            throw new ArgumentException("画質設定の範囲が不正です。");
        if (Codec is not ("auto" or "H.264" or "HEVC" or "AV1")) throw new ArgumentException("コーデックが不正です。");
        Agent?.Validate();
        if (Mac.Length > 0) { WakeOnLan.MagicPacket(Mac); Validation.IPv4(Broadcast); }
        if (WakeTargetId.Length > 0) Validation.Id(WakeTargetId);
    }
    public override string ToString() => Name;
}

public sealed class ClientSettings
{
    public string MoonlightPath { get; set; } = "";
    public List<PcProfile> Profiles { get; set; } = [];
}

public sealed record WakeTarget(string Id, string Mac, string Broadcast, string? LocalAddress = null);
public sealed record AgentStatus(string Name, bool PowerEnabled, bool DryRun, string[] WakeTargets, string? PendingAction);
public sealed record PowerRequest(string Action);
public sealed record ApiReply(string Message);

public static class JsonData
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    public static T Read<T>(string json) => JsonSerializer.Deserialize<T>(json, Options) ?? throw new ArgumentException("JSONが空です。");
}

public static class Tokens
{
    public static string New() => Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
    public static string Hash(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
    public static bool Verify(string token, string expectedHash)
    {
        if (token.Length != 64) return false;
        return CryptographicOperations.FixedTimeEquals(Convert.FromHexString(Hash(token)), Convert.FromHexString(expectedHash));
    }
}

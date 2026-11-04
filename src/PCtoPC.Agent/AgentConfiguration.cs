using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using PCtoPC.Core;

namespace PCtoPC.Agent;

public sealed class AgentConfiguration
{
    public string Name { get; set; } = Environment.MachineName;
    public string ListenAddress { get; set; } = "127.0.0.1";
    public int Port { get; set; } = 47831;
    public bool PowerEnabled { get; set; }
    public string TokenHash { get; set; } = "";
    public string CertificatePfx { get; set; } = "";
    public List<WakeTarget> WakeTargets { get; set; } = [];
    public static string FilePath(string directory) => Path.Combine(directory, "agent.secret");
    public static AgentConfiguration Load(string directory) => JsonData.Read<AgentConfiguration>(SecretFile.Read(FilePath(directory)));
    public void Save(string directory) => SecretFile.Write(FilePath(directory), JsonSerializer.Serialize(this, JsonData.Options));
    // Windows Schannel cannot serve TLS with EphemeralKeySet. UserKeySet gives it
    // a temporary user-scoped provider key, deleted when this certificate is disposed.
    public X509Certificate2 Certificate() => X509CertificateLoader.LoadPkcs12(Convert.FromBase64String(CertificatePfx), null,
        OperatingSystem.IsWindows() ? X509KeyStorageFlags.UserKeySet : X509KeyStorageFlags.EphemeralKeySet);
    public static void Initialize(Arguments args, string directory)
    {
        var path = FilePath(directory);
        if (File.Exists(path) && !args.Has("reset"))
            throw new ArgumentException("初期化済みです。認証を失効させて作り直す場合だけ --reset を付けてください。");
        var host = Validation.Host(args.Required("host"));
        var output = Path.GetFullPath(args.Required("out"));
        if (File.Exists(output)) throw new ArgumentException("接続ファイルが既にあります。別の --out を指定してください。");
        var port = int.Parse(args.Get("port", "47831"));
        if (port is < 1024 or > 65535) throw new ArgumentException("ポートの範囲が不正です。");
        var listen = System.Net.IPAddress.Parse(args.Get("listen", "127.0.0.1")).ToString();
        using var key = RSA.Create(3072);
        var request = new CertificateRequest("CN=PCtoPC Agent", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var names = new SubjectAlternativeNameBuilder();
        if (System.Net.IPAddress.TryParse(host, out var hostIp)) names.AddIpAddress(hostIp); else names.AddDnsName(host);
        request.CertificateExtensions.Add(names.Build());
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new("1.3.6.1.5.5.7.3.1") }, false));
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddYears(2));
        var token = Tokens.New();
        var config = new AgentConfiguration
        {
            Name = args.Get("name", Environment.MachineName), ListenAddress = listen, Port = port,
            PowerEnabled = args.Has("enable-power"), TokenHash = Tokens.Hash(token),
            CertificatePfx = Convert.ToBase64String(certificate.Export(X509ContentType.Pfx))
        };
        var pairing = new Pairing { Name = config.Name, Host = host, Port = port, Token = token,
            CertificateSha256 = certificate.GetCertHashString(HashAlgorithmName.SHA256) };
        pairing.Validate();
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        // Exclusive creation avoids silently replacing an existing credential file.
        using (var file = SecretFile.NewPrivateFile(output))
        {
            var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(pairing, JsonData.Options));
            file.Write(bytes);
        }
        try { config.Save(directory); }
        catch { File.Delete(output); throw; }
        Console.WriteLine($"Initialized. Pairing file: {output}");
        Console.WriteLine($"Listen: https://{listen}:{port}; power enabled: {config.PowerEnabled}");
        Console.WriteLine("Transfer the pairing file privately. Delete it after import. Never commit it.");
    }
}

public sealed class Arguments
{
    private readonly Dictionary<string, string> values = new(StringComparer.Ordinal);
    public Arguments(string[] args)
    {
        for (var i = 1; i < args.Length; i++)
        {
            if (!args[i].StartsWith("--", StringComparison.Ordinal)) throw new ArgumentException("不明な引数です: " + args[i]);
            var key = args[i][2..];
            var value = i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal) ? args[++i] : "true";
            if (!values.TryAdd(key, value)) throw new ArgumentException("引数が重複しています: " + key);
        }
        string[] known = ["data-dir", "host", "out", "name", "listen", "port", "enable-power", "reset", "dry-run", "id", "mac", "broadcast", "local-address"];
        foreach (var key in values.Keys) if (!known.Contains(key)) throw new ArgumentException("不明な引数です: " + key);
    }
    public bool Has(string key) => values.ContainsKey(key);
    public string Get(string key, string fallback) => values.GetValueOrDefault(key, fallback);
    public string Required(string key) => values.TryGetValue(key, out var value) && value != "true" ? value : throw new ArgumentException("--" + key + " が必要です。");
}

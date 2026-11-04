using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using PCtoPC.Agent;
using PCtoPC.Core;

var failures = 0;
var passed = 0;
async Task Test(string name, Func<Task> action)
{
    try { await action(); Console.WriteLine("PASS " + name); passed++; }
    catch (Exception ex) { failures++; Console.Error.WriteLine("FAIL " + name + ": " + ex.Message); }
}
void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
void Reject(Action action)
{
    try { action(); } catch (ArgumentException) { return; }
    throw new Exception("Invalid input was accepted");
}
var directory = Path.GetFullPath(Path.Combine("test-results", Guid.NewGuid().ToString("N")));
Directory.CreateDirectory(directory);
await Test("WoL packet and invalid MACs", () =>
{
    var bytes = WakeOnLan.MagicPacket("02:12:34:56:78:9A");
    Check(bytes.Length == 102 && bytes.Take(6).All(b => b == 255), "Bad magic header");
    for (var i = 0; i < 16; i++) Check(bytes.Skip(6 + i * 6).Take(6).SequenceEqual(Convert.FromHexString("02123456789A")), "Bad repetition");
    Reject(() => WakeOnLan.MagicPacket("not a MAC")); Reject(() => WakeOnLan.MagicPacket("FF:FF:FF:FF:FF:FF"));
    return Task.CompletedTask;
});
await Test("Real UDP WoL delivery to loopback receiver", async () =>
{
    using var receiver = new UdpClient(new IPEndPoint(IPAddress.Loopback, 9));
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
    var receive = receiver.ReceiveAsync(timeout.Token);
    await WakeOnLan.SendAsync("02:12:34:56:78:9A", "127.0.0.1");
    Check((await receive).Buffer.SequenceEqual(WakeOnLan.MagicPacket("02:12:34:56:78:9A")), "UDP payload mismatch");
});
await Test("WAN WoL uses the requested UDP port and sends three intact packets", async () =>
{
    using var receiver = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
    var port = ((IPEndPoint)receiver.Client.LocalEndPoint!).Port;
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
    var endpoint = await WakeOnLan.SendToHostAsync("02:12:34:56:78:9A", "127.0.0.1", port, timeout.Token);
    Check(endpoint.Address.Equals(IPAddress.Loopback) && endpoint.Port == port, "Wrong destination");
    for (var i = 0; i < 3; i++)
        Check((await receiver.ReceiveAsync(timeout.Token)).Buffer.SequenceEqual(WakeOnLan.MagicPacket("02:12:34:56:78:9A")), "WAN UDP payload mismatch");
    Check(receiver.Available == 0, "Unexpected extra datagrams");
});
await Test("WAN WoL resolves a hostname to IPv4 and respects cancellation", async () =>
{
    using var receiver = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
    var port = ((IPEndPoint)receiver.Client.LocalEndPoint!).Port;
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
    var endpoint = await WakeOnLan.SendToHostAsync("02:12:34:56:78:9A", "localhost", port, timeout.Token);
    Check(endpoint.Address.Equals(IPAddress.Loopback), "Hostname was not resolved to loopback IPv4");
    for (var i = 0; i < 3; i++) await receiver.ReceiveAsync(timeout.Token);
    using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
    try { await WakeOnLan.SendToHostAsync("02:12:34:56:78:9A", "localhost", port, cancelled.Token); throw new Exception("Cancellation was ignored"); }
    catch (OperationCanceledException) { }
    Check(receiver.Available == 0, "Cancelled operation sent datagrams");
});
await Test("WAN settings reject URLs, multicast, IPv6 and out-of-range ports", async () =>
{
    foreach (var host in new[] { "", "--help", "https://home.example.net", "home.example.net:3000", "999.999.999.999", "255.255.255.255", "0.0.0.0", "224.0.0.1", "::1" })
    {
        try { await WakeOnLan.SendToHostAsync("02:12:34:56:78:9A", host, 3000); throw new Exception("Invalid WAN host accepted: " + host); }
        catch (ArgumentException) { }
    }
    foreach (var port in new[] { -1, 0, 65536 })
    {
        try { await WakeOnLan.SendToHostAsync("02:12:34:56:78:9A", "127.0.0.1", port); throw new Exception("Invalid port accepted"); }
        catch (ArgumentException) { }
    }
    Check(Validation.WakeHost(" home.example.net ") == "home.example.net", "DDNS name rejected");
    Validation.UdpPort(1); Validation.UdpPort(65535);
});
await Test("Old profiles retain defaults; WAN settings persist without changing stream or Agent hosts", () =>
{
    var old = JsonData.Read<PcProfile>("""{"name":"old","host":"100.80.1.2","mac":"02:12:34:56:78:9A"}""");
    old.Validate(); Check(old.WanWakeHost == "" && old.WanWakePort == 3000, "Old profile needs migration");
    old.Agent = new Pairing { Host = "power.example.net", CertificateSha256 = Tokens.New(), Token = Tokens.New() };
    old.WanWakeHost = "wake.example.net"; old.WanWakePort = 43000; old.Validate();
    var file = Path.Combine(directory, "wan-profile.secret");
    SecretFile.Write(file, JsonSerializer.Serialize(old, JsonData.Options));
    var restored = JsonData.Read<PcProfile>(SecretFile.Read(file)); restored.Validate();
    Check(restored.WanWakeHost == "wake.example.net" && restored.WanWakePort == 43000, "WAN settings were not saved");
    Check(restored.Host == "100.80.1.2" && restored.Agent?.Host == "power.example.net" && restored.Mac == old.Mac, "Existing connection settings were overwritten");
    return Task.CompletedTask;
});
await Test("Reject CLI option injection and preserve app names as one argument", () =>
{
    var profile = new PcProfile { Host = "100.80.1.2", Application = "Desktop Work" };
    var command = Moonlight.Stream("Moonlight.exe", profile);
    Check(!command.UseShellExecute && command.ArgumentList[2] == "Desktop Work", "Unsafe argument transport");
    Check(command.ArgumentList.Contains("--no-vsync") && command.ArgumentList.Contains("hardware"), "Missing latency settings");
    Check(command.ArgumentList.Contains("20000"), "Bitrate must be Kbps");
    profile.Host = "--help"; Reject(() => profile.Validate());
    profile.Host = "https://host/"; Reject(() => profile.Validate());
    profile.Host = "192.168.1.20"; profile.Application = "--help"; Reject(() => profile.Validate());
    profile.Application = "Desktop"; profile.Fps = 1000; Reject(() => profile.Validate());
    return Task.CompletedTask;
});
await Test("Secret storage round trip and DPAPI encryption", () =>
{
    var file = Path.Combine(directory, "roundtrip.secret");
    var secret = "never-store-this-token-in-plaintext";
    SecretFile.Write(file, secret); Check(SecretFile.Read(file) == secret, "Cannot decrypt");
    if (OperatingSystem.IsWindows()) Check(!System.Text.Encoding.UTF8.GetString(File.ReadAllBytes(file)).Contains(secret), "DPAPI was not applied");
    SecretFile.Write(file, "replaced"); Check(SecretFile.Read(file) == "replaced", "Atomic replacement failed");
    return Task.CompletedTask;
});
await Test("Token verification rejects other tokens and malformed input", () =>
{
    var token = Tokens.New(); Check(Tokens.Verify(token, Tokens.Hash(token)), "Correct token rejected");
    Check(!Tokens.Verify(Tokens.New(), Tokens.Hash(token)), "Wrong token accepted");
    Check(!Tokens.Verify("", Tokens.Hash(token)), "Empty token accepted");
    return Task.CompletedTask;
});
await Test("Certificate pinning rejects different and expired certificates", () =>
{
    using var rsa = RSA.Create(2048);
    var request = new CertificateRequest("CN=test", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
    using var cert = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddDays(1));
    Check(AgentClient.CertificateMatches(cert, cert.GetCertHashString(HashAlgorithmName.SHA256)), "Correct pin rejected");
    Check(!AgentClient.CertificateMatches(cert, Tokens.New()), "Wrong pin accepted");
    using var expired = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-3), DateTimeOffset.UtcNow.AddDays(-2));
    Check(!AgentClient.CertificateMatches(expired, expired.GetCertHashString(HashAlgorithmName.SHA256)), "Expired certificate accepted");
    return Task.CompletedTask;
});
await Test("Power is opt-in; cancellation and duplicate scheduling", () =>
{
    using var disabled = new PowerController(false, true, NullLogger<PowerController>.Instance);
    Check(disabled.Request("shutdown").Code == 403, "Power defaults must deny");
    using var power = new PowerController(true, true, NullLogger<PowerController>.Instance);
    Check(power.Request("format").Code == 400, "Arbitrary action accepted");
    Check(power.Request("shutdown").Code == 202 && power.Pending == "shutdown", "Not scheduled");
    Check(power.Request("restart").Code == 409, "Concurrent power actions accepted");
    Check(power.Request("cancel").Code == 200 && power.Pending == null, "Cancellation failed");
    Check(power.Request("cancel").Code == 409, "Cancelled twice");
    return Task.CompletedTask;
});

// End-to-end tests launch a separate real HTTPS server on loopback, always in dry-run mode.
await Test("HTTPS integration: auth, pinned TLS, status, power cancellation, WoL allowlist, rotation", async () =>
{
    var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
    var port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop();
    var pairingPath = Path.Combine(directory, "test.pctopc.json");
    var agentDll = typeof(AgentConfiguration).Assembly.Location;
    var publishedAgent = Environment.GetEnvironmentVariable("PCTOPC_TEST_AGENT_EXE");
    ProcessStartInfo AgentCommand(params string[] arguments) => string.IsNullOrWhiteSpace(publishedAgent)
        ? Moonlight.Command("dotnet", [agentDll, .. arguments]) : Moonlight.Command(publishedAgent, arguments);
    async Task<int> Command(params string[] arguments)
    {
        var info = AgentCommand(arguments);
        info.ArgumentList.Add("--data-dir"); info.ArgumentList.Add(directory);
        info.RedirectStandardOutput = true; info.RedirectStandardError = true; info.CreateNoWindow = true;
        using var process = Process.Start(info)!;
        var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        try { await process.WaitForExitAsync(cancellation.Token); }
        catch { process.Kill(true); throw; }
        var output = await stdout + await stderr;
        if (process.ExitCode != 0) Console.WriteLine(output);
        return process.ExitCode;
    }
    Check(await Command("init", "--host", "127.0.0.1", "--out", pairingPath, "--port", port.ToString(), "--enable-power") == 0, "init failed");
    Check(await Command("add-wake-target", "--id", "office", "--mac", "02:12:34:56:78:9A", "--broadcast", "127.0.0.1") == 0, "add-wake-target failed");
    var pairing = JsonData.Read<Pairing>(File.ReadAllText(pairingPath));
    var info = AgentCommand("serve", "--data-dir", directory, "--dry-run");
    info.Environment["Logging__LogLevel__Default"] = "Debug";
    info.RedirectStandardOutput = true; info.RedirectStandardError = true; info.CreateNoWindow = true;
    using var server = Process.Start(info)!;
    var log = server.StandardOutput.ReadToEndAsync(); var error = server.StandardError.ReadToEndAsync();
    try
    {
        using var client = new AgentClient(pairing);
        AgentStatus? status = null;
        Exception? lastConnectionError = null;
        for (var i = 0; i < 60; i++)
        {
            if (server.HasExited) throw new Exception("Server failed: " + await log + await error);
            try { status = await client.StatusAsync(); break; } catch (HttpRequestException ex) { lastConnectionError = ex; await Task.Delay(100); }
        }
        Check(status is { DryRun: true, PowerEnabled: true } && status.WakeTargets.Contains("office"), "Bad authenticated status: " + (status == null ? lastConnectionError?.ToString() : JsonSerializer.Serialize(status)));
        var badToken = JsonData.Read<Pairing>(File.ReadAllText(pairingPath)); badToken.Token = Tokens.New();
        using var unauthorized = new AgentClient(badToken);
        try { await unauthorized.StatusAsync(); throw new Exception("Wrong token accepted"); }
        catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.Unauthorized) { }
        var badPin = JsonData.Read<Pairing>(File.ReadAllText(pairingPath)); badPin.CertificateSha256 = Tokens.New();
        using var wrongServer = new AgentClient(badPin);
        try { await wrongServer.StatusAsync(); throw new Exception("Wrong certificate accepted"); }
        catch (HttpRequestException ex) when (ex.HttpRequestError == HttpRequestError.SecureConnectionError) { }
        await client.PowerAsync("sleep"); Check((await client.StatusAsync()).PendingAction == "sleep", "Power status missing");
        await client.PowerAsync("cancel"); Check((await client.StatusAsync()).PendingAction == null, "Power not cancelled");
        Check((await client.WakeAsync("office")).Contains("DRY RUN"), "WoL dry run failed");
        try { await client.WakeAsync("unregistered"); throw new Exception("Arbitrary WoL target accepted"); }
        catch (IOException ex) when (ex.Message.Contains("404")) { }
        // Let one safe action complete to exercise delayed execution and cleanup.
        await client.PowerAsync("shutdown"); await Task.Delay(TimeSpan.FromSeconds(11));
        Check((await client.StatusAsync()).PendingAction == null, "Completed dry run remained pending");
    }
    finally
    {
        if (!server.HasExited) server.Kill(true);
        await server.WaitForExitAsync();
        File.WriteAllText(Path.Combine(directory, "server.log"), await log + await error);
    }
    var rotatedPath = Path.Combine(directory, "rotated.pctopc.json");
    Check(await Command("init", "--reset", "--host", "127.0.0.1", "--out", rotatedPath) == 0, "Rotation failed");
    var rotated = JsonData.Read<Pairing>(File.ReadAllText(rotatedPath));
    Check(rotated.Token != pairing.Token && rotated.CertificateSha256 != pairing.CertificateSha256, "Old credentials remain valid");
    Check(!AgentConfiguration.Load(directory).PowerEnabled, "Rotation must not retain power permission implicitly");
});
// Remove generated credentials, retain only non-secret server log for diagnosis.
foreach (var file in Directory.GetFiles(directory).Where(f => !f.EndsWith(".log", StringComparison.Ordinal))) File.Delete(file);
Console.WriteLine($"{passed} passed, {failures} failed");
return failures == 0 ? 0 : 1;

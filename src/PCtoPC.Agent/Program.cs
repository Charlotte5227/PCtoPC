using System.Net;
using System.Security.Authentication;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;
using PCtoPC.Agent;
using PCtoPC.Core;

try
{
    if (args.Length == 0 || args[0] is "help" or "--help")
    {
        Console.WriteLine("""
        PCtoPC Agent 0.1
        init --host <LAN IP or Tailscale DNS> --out <private .pctopc.json> [--listen <local IP>] [--port 47831] [--enable-power] [--reset]
        serve [--dry-run]
        add-wake-target --id <id> --mac <MAC> --broadcast <LAN broadcast> [--local-address <LAN interface IP>]
        All commands accept --data-dir <directory>. Default: current user's LocalAppData/PCtoPC.
        init defaults to loopback only. Choose a LAN/Tailscale interface IP to accept remote requests.
        Use --listen 0.0.0.0 only with a scoped firewall rule. Do not forward the port to the internet.
        --reset replaces the certificate and token, revoking all old pairing files. Stop Agent first.
        """);
        return 0;
    }
    var parsed = new Arguments(args);
    var directory = Path.GetFullPath(parsed.Get("data-dir", SecretFile.DefaultDirectory));
    switch (args[0])
    {
        case "init": AgentConfiguration.Initialize(parsed, directory); return 0;
        case "add-wake-target":
            var configuration = AgentConfiguration.Load(directory);
            var id = parsed.Required("id");
            Validation.Id(id);
            var mac = parsed.Required("mac");
            WakeOnLan.MagicPacket(mac);
            var broadcast = Validation.IPv4(parsed.Required("broadcast")).ToString();
            var local = parsed.Has("local-address") ? Validation.IPv4(parsed.Required("local-address")).ToString() : null;
            configuration.WakeTargets.RemoveAll(t => t.Id == id);
            configuration.WakeTargets.Add(new WakeTarget(id, mac, broadcast, local));
            configuration.Save(directory);
            Console.WriteLine("Wake target saved. Restart Agent to apply.");
            return 0;
        case "serve": break;
        default: throw new ArgumentException("Unknown command. Run --help.");
    }
    var config = AgentConfiguration.Load(directory);
    var dryRun = parsed.Has("dry-run");
    using var certificate = config.Certificate();
    var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = [] });
    builder.WebHost.ConfigureKestrel(options =>
    {
        options.AddServerHeader = false;
        options.Limits.MaxRequestBodySize = 4096;
        options.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(10);
        options.Listen(IPAddress.Parse(config.ListenAddress), config.Port, listen => listen.UseHttps(https =>
        {
            https.ServerCertificate = certificate;
            https.SslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13;
        }));
    });
    builder.Services.AddRateLimiter(options =>
    {
        options.RejectionStatusCode = 429;
        // One bounded global limiter also limits unauthenticated attempts without unbounded IP partitions.
        options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(_ => RateLimitPartition.GetFixedWindowLimiter("agent",
            _ => new FixedWindowRateLimiterOptions { PermitLimit = 120, Window = TimeSpan.FromMinutes(1), QueueLimit = 0, AutoReplenishment = true }));
    });
    var app = builder.Build();
    using var power = new PowerController(config.PowerEnabled, dryRun, app.Services.GetRequiredService<ILogger<PowerController>>());
    app.Lifetime.ApplicationStopping.Register(power.Dispose);
    app.UseRateLimiter();
    app.Use(async (context, next) =>
    {
        context.Response.Headers.CacheControl = "no-store";
        var header = context.Request.Headers.Authorization.ToString();
        if (!header.StartsWith("Bearer ", StringComparison.Ordinal) || !Tokens.Verify(header[7..], config.TokenHash))
        {
            context.Response.StatusCode = 401;
            await context.Response.WriteAsJsonAsync(new ApiReply("認証が必要です。"));
            return;
        }
        await next(context);
    });
    app.MapGet("/v1/status", () => new AgentStatus(config.Name, config.PowerEnabled && (OperatingSystem.IsWindows() || dryRun), dryRun,
        config.WakeTargets.Select(t => t.Id).ToArray(), power.Pending));
    app.MapPost("/v1/power", (PowerRequest request) =>
    {
        var result = power.Request(request.Action);
        return Results.Json(new ApiReply(result.Message), statusCode: result.Code);
    });
    app.MapPost("/v1/wake/{id}", async (string id) =>
    {
        // Remote callers may only reference an allowlisted target, never supply an arbitrary MAC/address.
        var target = config.WakeTargets.FirstOrDefault(t => t.Id == id);
        if (target == null) return Results.NotFound(new ApiReply("登録されていないWakeターゲットです。"));
        if (!dryRun) await WakeOnLan.SendAsync(target.Mac, target.Broadcast, target.LocalAddress);
        app.Logger.LogInformation("{Mode}Wake target {Target}", dryRun ? "DRY RUN " : "", target.Id);
        return Results.Ok(new ApiReply(dryRun ? "DRY RUN: WoL送信を模擬しました。" : "WoLを送信しました。起動完了は別途確認してください。"));
    });
    app.Logger.LogInformation("PCtoPC Agent: power enabled {PowerEnabled}; dry-run {DryRun}", config.PowerEnabled, dryRun);
    await app.RunAsync();
    return 0;
}
catch (Exception ex)
{
    Console.Error.WriteLine("PCtoPC Agent: " + ex.Message);
    return 1;
}

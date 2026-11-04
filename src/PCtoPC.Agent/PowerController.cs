using System.Diagnostics;
using System.Runtime.InteropServices;
using PCtoPC.Core;

namespace PCtoPC.Agent;

public sealed class PowerController(bool enabled, bool dryRun, ILogger<PowerController> logger) : IDisposable
{
    private readonly object gate = new();
    private CancellationTokenSource? pending;
    private string? action;
    public string? Pending { get { lock (gate) return action; } }
    public (int Code, string Message) Request(string requested)
    {
        lock (gate)
        {
            if (!enabled) return (403, "電源操作は被接続側で無効です。");
            if (requested == "cancel")
            {
                if (pending == null) return (409, "取り消せる操作はありません。");
                pending.Cancel();
                pending = null;
                action = null;
                return (200, "電源操作を取り消しました。");
            }
            if (requested is not ("shutdown" or "restart" or "sleep")) return (400, "不明な電源操作です。");
            if (!OperatingSystem.IsWindows() && !dryRun) return (400, "この端末はWoL中継専用です。電源操作はWindowsのみ対応です。");
            if (pending != null) return (409, "電源操作が予約済みです。先に取り消してください。");
            var source = new CancellationTokenSource();
            pending = source;
            action = requested;
            _ = ExecuteLaterAsync(requested, source);
            return (202, dryRun ? "DRY RUN: 10秒後に模擬実行します。電源は変更しません。" : "10秒後に実行します。取り消しボタンで中止できます。");
        }
    }
    private async Task ExecuteLaterAsync(string requested, CancellationTokenSource source)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(10), source.Token);
            lock (gate)
            {
                if (source.IsCancellationRequested) return;
                // Cancellation and execution have a single ordering point.
                if (dryRun) logger.LogInformation("DRY RUN power {Action}", requested);
                else if (requested == "sleep")
                {
                    if (!SetSuspendState(false, false, false)) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
                }
                else
                {
                    var executable = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "shutdown.exe");
                    using var process = Process.Start(Moonlight.Command(executable, requested == "shutdown" ? "/s" : "/r", "/t", "0"))
                        ?? throw new IOException("shutdown.exeを起動できません。");
                    // No /f, and /t 0 avoids Windows implicitly forcing applications closed.
                    logger.LogInformation("Requested power {Action}", requested);
                }
                pending = null;
                action = null;
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { logger.LogError(ex, "Power action failed"); }
        finally
        {
            lock (gate) if (ReferenceEquals(pending, source)) { pending = null; action = null; }
            source.Dispose();
        }
    }
    [DllImport("powrprof.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.U1)]
    private static extern bool SetSuspendState([MarshalAs(UnmanagedType.U1)] bool hibernate, [MarshalAs(UnmanagedType.U1)] bool forceCritical, [MarshalAs(UnmanagedType.U1)] bool disableWakeEvent);
    public void Dispose() { lock (gate) pending?.Cancel(); }
}

using System.Text.RegularExpressions;

namespace PortProxyManager;

/// <summary>
/// 通过 sc.exe 把本程序注册为 Windows 服务，实现开机自启（LocalSystem 账户，无需登录、无 UAC 弹窗）。
/// 注意：schtasks 计划任务在本机被安全策略封禁，服务是唯一可行的无人值守自启方案。
/// </summary>
public sealed class ServiceManager
{
    public const string ServiceName = "PortProxyManager";

    /// <summary>当前进程 exe；通过 dotnet.exe 运行（开发态）时返回空，表示不能装服务。</summary>
    public static string ExePath
    {
        get
        {
            var p = Environment.ProcessPath ?? "";
            return Path.GetFileName(p).Equals("dotnet.exe", StringComparison.OrdinalIgnoreCase) ? "" : p;
        }
    }

    /// <summary>是否运行在 Windows 服务上下文（LocalSystem 账户）。</summary>
    public static bool RunningAsService
    {
        get
        {
            if (!OperatingSystem.IsWindows()) return false;
            using var id = System.Security.Principal.WindowsIdentity.GetCurrent();
            return id.IsSystem;
        }
    }

    private static Task<NetshResult> ScAsync(string args, CancellationToken ct = default) =>
        CmdRunner.RunAsync($"sc {args}", ct);

    public async Task<ServiceStatus> QueryAsync(CancellationToken ct = default)
    {
        var q = await ScAsync($"query {ServiceName}", ct);
        if (!q.Ok) return new ServiceStatus { Installed = false, Message = "服务未安装" };

        // sc query 的 STATE 关键字不随系统语言变化（STOPPED/RUNNING），双保险再匹配状态码 4
        var running = q.Output.Contains("RUNNING", StringComparison.OrdinalIgnoreCase)
                      || Regex.IsMatch(q.Output, @"STATE\s*:?\s*4\b");

        var bin = "";
        var qc = await CmdRunner.RunAsync(
            $"reg query HKLM\\SYSTEM\\CurrentControlSet\\Services\\{ServiceName} /v ImagePath", ct);
        var m = Regex.Match(qc.Output, @"REG_SZ\s+(\S.*)", RegexOptions.IgnoreCase);
        if (m.Success) bin = m.Groups[1].Value.Trim().Trim('"');

        var startMode = "";
        var qs = await CmdRunner.RunAsync(
            $"reg query HKLM\\SYSTEM\\CurrentControlSet\\Services\\{ServiceName} /v Start", ct);
        var ms = Regex.Match(qs.Output, @"REG_SZ\s+0x([0-9a-fA-F]+)", RegexOptions.IgnoreCase);
        if (ms.Success)
        {
            startMode = Convert.ToInt32(ms.Groups[1].Value, 16) switch
            {
                2 => "自动",
                3 => "手动",
                4 => "已禁用",
                _ => "0x" + ms.Groups[1].Value,
            };
        }

        var exe = ExePath;
        var matches = exe.Length > 0 && bin.Length > 0 &&
                      string.Equals(NormPath(bin), NormPath(exe), StringComparison.OrdinalIgnoreCase);
        return new ServiceStatus
        {
            Installed = true,
            Running = running,
            StartMode = startMode,
            BinaryPath = bin,
            PathMatches = matches,
        };
    }

    public async Task<(bool ok, string message)> InstallAsync(CancellationToken ct = default)
    {
        if (!NetshService.IsAdministrator())
            return (false, "安装服务需要管理员权限，请以管理员身份运行本工具后重试");

        var exe = ExePath;
        if (exe.Length == 0)
            return (false, "请使用发布目录（publish）中的 PortProxyManager.exe 安装服务，当前通过 dotnet 运行无法注册");

        // binPath= 后的值整体带引号且路径自身也要带引号（sc 的标准写法）
        var create = await ScAsync(
            $"create {ServiceName} binPath= \"\\\"{exe}\\\"\" start= auto DisplayName= \"PortProxy Manager\"", ct);
        if (!create.Ok) return (false, "安装失败：" + create.Message);

        await ScAsync($"description {ServiceName} \"netsh portproxy 端口转发管理服务：开机自启并自动巡检恢复规则\"", ct);
        // 崩溃自动拉起：1 分钟后重启，无限次
        await ScAsync($"failure {ServiceName} reset= 86400 actions= restart/60000/restart/60000/restart/60000", ct);

        var start = await ScAsync($"start {ServiceName}", ct);
        return (true, start.Ok
            ? "服务已安装并启动，开机将自动运行（无需再手动双击 start-admin.bat）"
            : "服务已安装为开机自启，但本次启动失败：" + start.Message);
    }

    public async Task<(bool ok, string message)> UninstallAsync(CancellationToken ct = default)
    {
        if (!NetshService.IsAdministrator())
            return (false, "卸载服务需要管理员权限");

        await ScAsync($"stop {ServiceName}", ct);
        var del = await ScAsync($"delete {ServiceName}", ct);
        return del.Ok
            ? (true, "服务已卸载，开机不再自启")
            : (false, "卸载失败：" + del.Message);
    }

    public async Task<(bool ok, string message)> StartAsync(CancellationToken ct = default)
    {
        var r = await ScAsync($"start {ServiceName}", ct);
        return r.Ok ? (true, "服务已启动") : (false, "启动失败：" + r.Message);
    }

    public async Task<(bool ok, string message)> StopAsync(CancellationToken ct = default)
    {
        var r = await ScAsync($"stop {ServiceName}", ct);
        // 1062 = 服务未启动，视为已停止
        return r.Ok || r.Output.Contains("1062") ? (true, "服务已停止") : (false, "停止失败：" + r.Message);
    }

    public async Task RestartAsync(CancellationToken ct = default)
    {
        await StopAsync(ct);
        await Task.Delay(1200, ct);
        await StartAsync(ct);
    }

    private static string NormPath(string p) =>
        p.Trim().Trim('"').Replace("/", "\\").TrimEnd('\\');
}

using System.Security.Principal;

namespace PortProxyManager;

public sealed class NetshResult
{
    public bool Ok { get; init; }
    public int ExitCode { get; init; }
    public string Output { get; init; } = "";
    public string Error { get; init; } = "";

    public string Message => (Ok ? "" : (Error + " " + Output).Trim());
}

public sealed class ActualRule
{
    public string Mode { get; init; } = "v4tov4";
    public string ListenAddress { get; init; } = "";
    public int ListenPort { get; init; }
    public string ConnectAddress { get; init; } = "";
    public int ConnectPort { get; init; }

    public string Key => $"{ListenAddress}:{ListenPort}";
    public string Describe() => $"[{Mode}] {ListenAddress}:{ListenPort} -> {ConnectAddress}:{ConnectPort}";
}

public sealed class NetshService
{
    /// <summary>netsh portproxy 支持的四种模式，逐个查询以准确归属地址族。</summary>
    private static readonly string[] AllModes = { "v4tov4", "v4tov6", "v6tov4", "v6tov6" };

    private readonly ILogger<NetshService> _log;

    public NetshService(ILogger<NetshService> log) => _log = log;

    public static bool IsAdministrator()
    {
        if (!OperatingSystem.IsWindows()) return false;
        using var id = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
    }

    public Task<NetshResult> RunAsync(string args, CancellationToken ct = default) =>
        CmdRunner.RunAsync("netsh " + args, ct, _log);

    /// <summary>并行查询四种模式的全部规则。</summary>
    public async Task<List<ActualRule>> ShowAsync(CancellationToken ct = default)
    {
        var all = await Task.WhenAll(AllModes.Select(m => ShowModeAsync(m, ct)));
        return all.SelectMany(x => x).ToList();
    }

    /// <summary>
    /// 解析单个模式的 netsh 输出：按空白分词取 4 列（监听地址 监听端口 目标地址 目标端口），
    /// 不依赖中英文表头，IPv4/IPv6 长地址通吃。
    /// </summary>
    private async Task<List<ActualRule>> ShowModeAsync(string mode, CancellationToken ct)
    {
        var r = await RunAsync($"interface portproxy show {mode}", ct);
        var list = new List<ActualRule>();
        foreach (var raw in r.Output.Split('\n'))
        {
            var tokens = raw.TrimEnd('\r').Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length != 4) continue;

            var listen = tokens[0].Trim('[', ']');
            var connect = tokens[2].Trim('[', ']');
            if (listen != "*" && !RuleValidator.IsIp(listen)) continue;
            if (!RuleValidator.IsIp(connect)) continue;
            if (!int.TryParse(tokens[1], out var lp) || lp is < 1 or > 65535) continue;
            if (!int.TryParse(tokens[3], out var cp) || cp is < 1 or > 65535) continue;

            list.Add(new ActualRule
            {
                Mode = mode,
                ListenAddress = listen,
                ListenPort = lp,
                ConnectAddress = connect,
                ConnectPort = cp,
            });
        }
        return list;
    }

    public Task<NetshResult> AddAsync(PortProxyRule r, CancellationToken ct = default) =>
        RunAsync($"interface portproxy add {r.Mode} listenaddress={RuleValidator.Norm(r.ListenAddress)} listenport={r.ListenPort} connectaddress={RuleValidator.Norm(r.ConnectAddress)} connectport={r.ConnectPort}", ct);

    public Task<NetshResult> DeleteAsync(PortProxyRule r, CancellationToken ct = default) =>
        RunAsync($"interface portproxy delete {r.Mode} listenaddress={RuleValidator.Norm(r.ListenAddress)} listenport={r.ListenPort}", ct);

    public Task<NetshResult> DeleteActualAsync(ActualRule r, CancellationToken ct = default) =>
        RunAsync($"interface portproxy delete {r.Mode} listenaddress={r.ListenAddress} listenport={r.ListenPort}", ct);

    public Task<NetshResult> ResetAsync(CancellationToken ct = default) =>
        RunAsync("interface portproxy reset", ct);

    public Task<NetshResult> AllowFirewallAsync(int port, CancellationToken ct = default) =>
        RunAsync($"advfirewall firewall add rule name=\"PortProxy_{port}\" dir=in action=allow protocol=TCP localport={port}", ct);

    /// <summary>幂等放通转发端口：规则已存在则跳过，避免反复应用堆积同名规则。</summary>
    public async Task<NetshResult> EnsureFirewallAsync(int port, CancellationToken ct = default)
    {
        var q = await RunAsync($"advfirewall firewall show rule name=\"PortProxy_{port}\"", ct);
        if (q.Ok) return q; // 已存在（show 返回 0）
        return await AllowFirewallAsync(port, ct);
    }

    /// <summary>幂等放通 Web 管理页端口，让局域网内其他设备能访问。</summary>
    public async Task<NetshResult> EnsureWebFirewallAsync(int port, CancellationToken ct = default)
    {
        var q = await RunAsync($"advfirewall firewall show rule name=\"PortProxyManager_Web_{port}\"", ct);
        if (q.Ok) return q; // 已存在
        return await AllowWebFirewallAsync(port, ct);
    }

    public Task<NetshResult> AllowWebFirewallAsync(int port, CancellationToken ct = default) =>
        RunAsync($"advfirewall firewall add rule name=\"PortProxyManager_Web_{port}\" dir=in action=allow protocol=TCP localport={port}", ct);
}

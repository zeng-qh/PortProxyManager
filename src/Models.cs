using System.Net;
using System.Net.Sockets;

namespace PortProxyManager;

public sealed class PortProxyRule
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];
    public string ListenAddress { get; set; } = "0.0.0.0";
    public int ListenPort { get; set; }
    public string ConnectAddress { get; set; } = "";
    public int ConnectPort { get; set; }
    public bool Enabled { get; set; } = true;
    public bool OpenFirewall { get; set; }
    public string Note { get; set; } = "";
    public DateTime CreatedAt { get; set; } = DateTime.Now;

    public string Key => $"{ListenAddress}:{ListenPort}";

    /// <summary>netsh 模式由两端地址族自动推导：v4tov4 / v6tov4 / v4tov6 / v6tov6，用户无需手选。</summary>
    public string Mode => RuleValidator.DeriveMode(ListenAddress, ConnectAddress);

    public string Describe() => $"[{Mode}] {ListenAddress}:{ListenPort} -> {ConnectAddress}:{ConnectPort}";
}

public sealed class AppSettings
{
    /// <summary>自动巡检间隔（秒），最小 5 秒。</summary>
    public int IntervalSec { get; set; } = 30;

    /// <summary>是否开启自动巡检恢复。</summary>
    public bool AutoRecover { get; set; } = true;

    /// <summary>巡检时是否删除系统中存在、但本工具未登记的规则。</summary>
    public bool RemoveUnknown { get; set; }

    /// <summary>恢复规则时是否顺带放通 Windows 防火墙入站端口。</summary>
    public bool ApplyFirewall { get; set; }

    /// <summary>Web 管理页监听地址：127.0.0.1 仅本机，0.0.0.0 / :: 允许局域网访问（保存后需重启生效）。</summary>
    public string ListenHost { get; set; } = "127.0.0.1";

    /// <summary>Web 管理页监听端口。</summary>
    public int ListenPort { get; set; } = 5577;

    /// <summary>启用访问令牌鉴权：默认开启，所有 /api/* 请求必须携带令牌。</summary>
    public bool AuthEnabled { get; set; } = true;

    /// <summary>访问令牌，默认 admin，首次进入强制修改；忘记时删除 data\settings.json 重置。</summary>
    public string AuthToken { get; set; } = "admin";
}

/// <summary>Windows 服务状态（sc / reg 查询结果）。</summary>
public sealed class ServiceStatus
{
    public bool Installed { get; init; }
    public bool Running { get; init; }
    public string StartMode { get; init; } = "";
    public string BinaryPath { get; init; } = "";
    public bool PathMatches { get; init; }
    public string Message { get; init; } = "";
}

/// <summary>本机网卡地址，用于页面默认带出监听地址（含 IPv6 全局地址）。</summary>
public sealed record LocalIpInfo(string Name, string Address);

/// <summary>批量导入请求。</summary>
public sealed class ImportRequest
{
    /// <summary>true = 清空现有规则后仅保留导入的；false = 合并（同监听键以导入为准）。</summary>
    public bool Replace { get; set; }

    public List<PortProxyRule> Rules { get; set; } = new();
}

/// <summary>修改访问令牌请求。</summary>
public sealed class ChangeTokenRequest
{
    public string NewToken { get; set; } = "";
}

public sealed class LogEntry
{
    public DateTime Time { get; set; }
    public string Level { get; set; } = "INFO";
    public string Message { get; set; } = "";
}

public sealed class ReconcileReport
{
    public List<string> Added { get; } = new();
    public List<string> Removed { get; } = new();
    public List<string> Failed { get; } = new();
    public List<string> Unchanged { get; } = new();

    public bool HasChange => Added.Count > 0 || Removed.Count > 0 || Failed.Count > 0;
}

public static class RuleValidator
{
    /// <summary>严格校验，防止用户输入被拼进命令行。</summary>
    public static bool TryValidate(PortProxyRule r, out string error)
    {
        error = "";
        if (!IsIp(r.ListenAddress)) { error = "监听地址必须是合法 IPv4/IPv6 地址"; return false; }
        if (!IsIp(r.ConnectAddress)) { error = "目标地址必须是合法 IPv4/IPv6 地址"; return false; }
        if (r.ListenPort is < 1 or > 65535) { error = "监听端口必须在 1-65535 之间"; return false; }
        if (r.ConnectPort is < 1 or > 65535) { error = "目标端口必须在 1-65535 之间"; return false; }
        return true;
    }

    /// <summary>去掉 IPv6 可能的方括号写法。</summary>
    public static string Norm(string? s) => (s ?? "").Trim().Trim('[', ']');

    /// <summary>合法 IPv4/IPv6；拒绝带 %zone 的链路本地地址，也拒绝被 TryParse 当成 IPv4 的纯数字。</summary>
    public static bool IsIp(string? s)
    {
        var t = Norm(s);
        if (t.Length == 0) return false;
        if (t.Contains('%')) return false;
        if (!t.Contains('.') && !t.Contains(':')) return false;
        return IPAddress.TryParse(t, out _);
    }

    public static bool IsV6(string? s) =>
        IsIp(s) && IPAddress.Parse(Norm(s)).AddressFamily == AddressFamily.InterNetworkV6;

    /// <summary>按两端地址族推导 netsh 模式。</summary>
    public static string DeriveMode(string listen, string connect)
    {
        var v6l = IsV6(listen);
        var v6c = IsV6(connect);
        return (v6l, v6c) switch
        {
            (true, true) => "v6tov6",
            (true, false) => "v6tov4",
            (false, true) => "v4tov6",
            _ => "v4tov4",
        };
    }

    public const string DefaultToken = "admin";

    /// <summary>访问令牌校验：至少 4 位、不能用默认 admin。</summary>
    public static bool IsTokenValid(string? t, out string error)
    {
        error = "";
        if (string.IsNullOrWhiteSpace(t) || t.Trim().Length < 4)
        {
            error = "令牌至少需要 4 个字符";
            return false;
        }
        if (t.Trim().Equals(DefaultToken, StringComparison.OrdinalIgnoreCase))
        {
            error = "不能使用默认令牌 admin，请换一个";
            return false;
        }
        return true;
    }
}

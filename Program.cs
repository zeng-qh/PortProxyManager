using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.FileProviders;
using PortProxyManager;

// 从多个候选位置定位 wwwroot：发布目录 / 启动目录 / 开发目录，避免换个 exe 就找不到页面
static string? FindWebRoot()
{
    var procDir = Path.GetDirectoryName(Environment.ProcessPath ?? string.Empty) ?? string.Empty;
    var candidates = new[]
    {
        Path.Combine(AppContext.BaseDirectory, "wwwroot"),
        Path.Combine(procDir, "wwwroot"),
        Path.Combine(Environment.CurrentDirectory, "wwwroot"),
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "wwwroot"),
        Path.Combine(procDir, "..", "..", "..", "..", "wwwroot"),
    };
    foreach (var c in candidates)
    {
        if (string.IsNullOrWhiteSpace(c)) continue;
        string full;
        try { full = Path.GetFullPath(c); } catch { continue; }
        if (File.Exists(Path.Combine(full, "index.html"))) return full;
    }
    return null;
}

var baseDir = AppContext.BaseDirectory;
var webRoot = FindWebRoot();

var dataDir = Path.Combine(baseDir, "data");
var logDir = Path.Combine(baseDir, "logs");
Directory.CreateDirectory(dataDir);
Directory.CreateDirectory(logDir);

var log = new LogSink(logDir);
var settingsFile = Path.Combine(dataDir, "settings.json");
var firstRun = !File.Exists(settingsFile);
var store = new RuleStore(Path.Combine(dataDir, "rules.json"), settingsFile);

// 必须在创建 builder 时一次性指定：运行后用 UseWebRoot 改会抛 NotSupportedException
var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    ContentRootPath = baseDir,
    WebRootPath = webRoot,
});

// 作为 Windows 服务运行时挂接 SCM（未以服务方式运行时内部会自动跳过）
if (OperatingSystem.IsWindows()) builder.Host.UseWindowsService();

// Web 监听地址/端口：命令行 --urls 优先，其次读设置文件，最后默认 0.0.0.0:5577
// boundPort 记录实际监听端口，启动时用于幂等放通防火墙
var boundPort = 5577;
var urlsCfg = builder.Configuration["urls"];
if (string.IsNullOrWhiteSpace(urlsCfg))
{
    var st0 = store.LoadSettings();
    var host0 = RuleValidator.IsIp(st0.ListenHost) ? st0.ListenHost : "0.0.0.0";
    var port0 = st0.ListenPort is >= 1 and <= 65535 ? st0.ListenPort : 5577;
    builder.WebHost.UseUrls($"http://{host0}:{port0}");
    boundPort = port0;
}
else if (Uri.TryCreate(urlsCfg.Split(';')[0].Trim(), UriKind.Absolute, out var u0))
{
    boundPort = u0.Port;
}

builder.Logging.ClearProviders();
builder.Logging.AddConsole();
builder.Logging.SetMinimumLevel(LogLevel.Warning);

builder.Services.AddSingleton(log);
builder.Services.AddSingleton(store);
builder.Services.AddSingleton<NetshService>();
builder.Services.AddSingleton<ServiceManager>();
builder.Services.AddSingleton<ReconcileService>();
builder.Services.AddHostedService<WatchdogService>();

var app = builder.Build();

// 访问令牌鉴权：启用后所有 /api/*（仅豁免精确的 /api/auth）必须携带令牌（请求头 X-Auth-Token 或 ?token=）
app.Use(async (ctx, next) =>
{
    var st = store.LoadSettings();
    var path = ctx.Request.Path;
    if (st.AuthEnabled && !string.IsNullOrEmpty(st.AuthToken)
        && path.StartsWithSegments("/api") && path != "/api/auth")
    {
        var provided = ctx.Request.Headers["X-Auth-Token"].FirstOrDefault()
                       ?? ctx.Request.Query["token"].FirstOrDefault();
        if (string.IsNullOrEmpty(provided) || provided != st.AuthToken)
        {
            await Task.Delay(500); // 简单防爆破
            ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await ctx.Response.WriteAsJsonAsync(new { ok = false, message = "未授权：缺少或错误的访问令牌" });
            return;
        }
    }
    await next();
});

if (webRoot != null)
{
    var provider = new PhysicalFileProvider(webRoot);
    app.UseDefaultFiles(new DefaultFilesOptions { FileProvider = provider });
    // index.html 禁缓存：程序更新后重新打开页面必定拿到最新 UI
    app.UseStaticFiles(new StaticFileOptions
    {
        FileProvider = provider,
        OnPrepareResponse = ctx =>
        {
            if (ctx.File.Name == "index.html")
                ctx.Context.Response.Headers["Cache-Control"] = "no-cache";
        }
    });
}

// 兜底：静态文件万一没命中，也保证 / 和 /index.html 一定能返回页面
app.MapGet("/", ServeIndex);
app.MapGet("/index.html", ServeIndex);

IResult ServeIndex(HttpContext ctx)
{
    ctx.Response.Headers["Cache-Control"] = "no-cache";
    var file = webRoot == null ? null : Path.Combine(webRoot, "index.html");
    if (file != null && File.Exists(file))
        return Results.Content(File.ReadAllText(file), "text/html; charset=utf-8");

    // 兜底：用内嵌到程序集里的页面，保证任何目录下都能打开
    using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("PortProxyManager.index.html");
    if (s == null)
        return Results.Content("<meta charset=\"utf-8\"><h3>index.html 未找到</h3>", "text/html; charset=utf-8");
    using var r = new StreamReader(s, Encoding.UTF8);
    return Results.Content(r.ReadToEnd(), "text/html; charset=utf-8");
}

// 本机可用地址（IPv4 + IPv6 全局地址），供页面默认带出监听地址
static List<LocalIpInfo> GetLocalIps()
{
    var list = new List<LocalIpInfo>();
    foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
    {
        if (ni.OperationalStatus != OperationalStatus.Up) continue;
        if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
        foreach (var ua in ni.GetIPProperties().UnicastAddresses)
        {
            if (ua.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
            {
                var addr = ua.Address.ToString();
                if (addr.StartsWith("169.254.")) continue; // APIPA，排除
                list.Add(new LocalIpInfo(ni.Name, addr));
            }
            else if (ua.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6)
            {
                var a = ua.Address;
                if (a.IsIPv6LinkLocal || a.IsIPv6SiteLocal) continue; // fe80::/fec0:: 排除（带 zone 不可用）
                if (IPAddress.IPv6Loopback.Equals(a)) continue;
                list.Add(new LocalIpInfo(ni.Name, a.ToString()));
            }
        }
    }
    // 优先：物理网卡（排除 VMware / Hyper-V / 虚拟网卡），IPv4 靠前
    bool Virtual(string name) =>
        name.Contains("VMware", StringComparison.OrdinalIgnoreCase)
        || name.Contains("VMnet", StringComparison.OrdinalIgnoreCase)
        || name.Contains("Hyper-V", StringComparison.OrdinalIgnoreCase)
        || name.Contains("Virtual", StringComparison.OrdinalIgnoreCase)
        || name.Contains("vEthernet", StringComparison.OrdinalIgnoreCase)
        || name.Contains("Loopback", StringComparison.OrdinalIgnoreCase);
    return list.OrderBy(x => Virtual(x.Name) ? 1 : 0)
               .ThenBy(x => x.Name.StartsWith("WLAN", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
               .ThenBy(x => x.Address.Contains(':') ? 1 : 0)
               .ThenBy(x => x.Address)
               .ToList();
}

var api = app.MapGroup("/api");

api.MapGet("/state", async (NetshService netsh, RuleStore s, ReconcileService rec, LogSink l) =>
{
    var actual = await netsh.ShowAsync();
    return Results.Ok(new
    {
        isAdmin = NetshService.IsAdministrator(),
        runningAsService = ServiceManager.RunningAsService,
        exePath = ServiceManager.ExePath,
        settings = s.LoadSettings(),
        rules = s.Load(),
        actual,
        watchdog = new { lastRun = rec.LastRun, lastOk = rec.LastOk, lastSummary = rec.LastSummary },
        localIps = GetLocalIps(),
        logs = l.Recent(200),
    });
});

// 令牌校验：登录浮层用它验证输入的令牌是否正确
api.MapGet("/auth", (HttpRequest req, RuleStore s) =>
{
    var st = s.LoadSettings();
    if (!st.AuthEnabled || string.IsNullOrEmpty(st.AuthToken))
        return Results.Ok(new { ok = true, authRequired = false });

    var provided = req.Headers["X-Auth-Token"].FirstOrDefault() ?? req.Query["token"].FirstOrDefault();
    return provided == st.AuthToken
        ? Results.Ok(new { ok = true, authRequired = true })
        : Results.Json(new { ok = false, message = "访问令牌错误" }, statusCode: 401);
});

// 修改访问令牌（受鉴权保护：必须携带当前令牌；默认 admin 属于首次部署的初始口令）
api.MapPost("/auth/change", (ChangeTokenRequest input, RuleStore s, LogSink l) =>
{
    var t = (input.NewToken ?? "").Trim();
    if (!RuleValidator.IsTokenValid(t, out var err))
        return Results.BadRequest(new { ok = false, message = err });

    var st = s.LoadSettings();
    st.AuthToken = t;
    st.AuthEnabled = true;
    s.SaveSettings(st);
    l.Warn("访问令牌已修改，已登录的其他设备需重新输入新令牌");
    return Results.Ok(new { ok = true, message = "令牌已更新，请用新令牌重新登录其他设备" });
});

// 新增规则：保存后立刻执行 netsh，立即生效
api.MapPost("/rules", async (PortProxyRule input, RuleStore s, NetshService netsh, LogSink l) =>
{
    if (!RuleValidator.TryValidate(input, out var err))
    {
        l.Warn($"新增被拒绝：{err}");
        return Results.BadRequest(new { ok = false, message = err });
    }

    var rules = s.Load();
    if (rules.Any(r => r.Key == input.Key && r.Id != input.Id))
        return Results.BadRequest(new { ok = false, message = $"监听 {input.Key} 已存在，请修改后重试" });

    if (string.IsNullOrWhiteSpace(input.Id)) input.Id = Guid.NewGuid().ToString("N")[..8];
    input.CreatedAt = DateTime.Now;
    rules.Add(input);
    s.Save(rules);
    l.Info($"新增规则 {input.Describe()}");

    var applied = await ApplyOne(input, netsh, l);
    return Results.Ok(new { ok = applied.ok, message = applied.message, rule = input });
});

// 修改规则：监听键变了先删旧的，再按新配置生效
api.MapPut("/rules/{id}", async (string id, PortProxyRule input, RuleStore s, NetshService netsh, LogSink l) =>
{
    if (!RuleValidator.TryValidate(input, out var err))
        return Results.BadRequest(new { ok = false, message = err });

    var rules = s.Load();
    var idx = rules.FindIndex(r => r.Id == id);
    if (idx < 0) return Results.NotFound(new { ok = false, message = "规则不存在" });

    var old = rules[idx];
    if (old.Key != input.Key)
    {
        await netsh.DeleteAsync(old);
        l.Info($"移除旧规则 {old.Describe()}");
    }

    input.Id = id;
    input.CreatedAt = old.CreatedAt;
    rules[idx] = input;
    s.Save(rules);
    l.Info($"更新规则 {input.Describe()}");

    var applied = await ApplyOne(input, netsh, l);
    return Results.Ok(new { ok = applied.ok, message = applied.message, rule = input });
});

api.MapDelete("/rules/{id}", async (string id, RuleStore s, NetshService netsh, LogSink l) =>
{
    var rules = s.Load();
    var target = rules.FirstOrDefault(r => r.Id == id);
    if (target == null) return Results.NotFound(new { ok = false, message = "规则不存在" });

    rules.Remove(target);
    s.Save(rules);

    var del = await netsh.DeleteAsync(target);
    l.Info($"删除规则 {target.Describe()} => {(del.Ok ? "已移除" : del.Message)}");
    return Results.Ok(new { ok = del.Ok, message = del.Ok ? "已删除并同步到系统" : del.Message });
});

api.MapPost("/rules/{id}/toggle", async (string id, RuleStore s, NetshService netsh, LogSink l) =>
{
    var rules = s.Load();
    var target = rules.FirstOrDefault(r => r.Id == id);
    if (target == null) return Results.NotFound(new { ok = false, message = "规则不存在" });

    target.Enabled = !target.Enabled;
    s.Save(rules);

    var r = target.Enabled ? await netsh.AddAsync(target) : await netsh.DeleteAsync(target);
    l.Info($"{(target.Enabled ? "启用" : "禁用")}规则 {target.Describe()} => {(r.Ok ? "成功" : r.Message)}");
    return Results.Ok(new { ok = r.Ok, message = r.Ok ? (target.Enabled ? "已启用" : "已禁用") : r.Message });
});

// 导出规则：JSON 附件下载
api.MapGet("/rules/export", (RuleStore s) =>
{
    var payload = new
    {
        version = 1,
        exportedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
        rules = s.Load(),
    };
    var json = JsonSerializer.Serialize(payload, new JsonSerializerOptions
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    });
    var bytes = Encoding.UTF8.GetBytes(json);
    return Results.File(bytes, "application/json",
        fileDownloadName: $"portproxy-rules-{DateTime.Now:yyyyMMdd-HHmmss}.json");
});

// 批量导入：合并（同监听键以导入为准）或替换
api.MapPost("/rules/import", async (ImportRequest input, RuleStore s, NetshService netsh, ReconcileService rec, LogSink l) =>
{
    if (input.Rules.Count == 0)
        return Results.BadRequest(new { ok = false, message = "导入内容为空或格式不正确" });

    var details = new List<object>();
    var valid = new List<PortProxyRule>();
    var seenKeys = new HashSet<string>();
    int line = 0;
    foreach (var r in input.Rules)
    {
        line++;
        if (!RuleValidator.TryValidate(r, out var err))
        {
            details.Add(new { ok = false, line, rule = $"{r.ListenAddress}:{r.ListenPort}", message = err });
            continue;
        }
        if (!seenKeys.Add(r.Key))
        {
            details.Add(new { ok = false, line, rule = r.Describe(), message = "重复的监听地址，已跳过" });
            continue;
        }
        r.Id = Guid.NewGuid().ToString("N")[..8];
        r.CreatedAt = DateTime.Now;
        valid.Add(r);
        details.Add(new { ok = true, line, rule = r.Describe(), message = "校验通过" });
    }

    if (valid.Count == 0)
        return Results.BadRequest(new { ok = false, message = "没有一条规则校验通过", details });

    var rules = s.Load();
    if (input.Replace)
    {
        foreach (var old in rules.Where(x => x.Enabled).ToList())
            await netsh.DeleteAsync(old);
        rules.Clear();
    }
    else
    {
        rules.RemoveAll(x => valid.Any(v => v.Key == x.Key));
    }
    rules.AddRange(valid);
    s.Save(rules);
    l.Info($"导入规则 {valid.Count} 条（替换模式：{(input.Replace ? "开" : "关")}）");

    var report = await rec.RunAsync(false);
    return Results.Ok(new
    {
        ok = true,
        message = $"导入 {valid.Count} 条并已应用：新增 {report.Added.Count}，失败 {report.Failed.Count}",
        details,
    });
});

// 全量应用：把所有期望规则重新对齐到系统
api.MapPost("/apply", async (ReconcileService rec, LogSink l) =>
{
    l.Info("手动触发全量应用");
    var report = await rec.RunAsync(true);
    return Results.Ok(new
    {
        ok = report.Failed.Count == 0,
        message = $"新增 {report.Added.Count} / 移除 {report.Removed.Count} / 失败 {report.Failed.Count}",
        report,
    });
});

// 危险操作：清空系统全部 portproxy 规则（登记的规则随后会被巡检补回）
api.MapPost("/reset", async (NetshService netsh, ReconcileService rec, LogSink l) =>
{
    var r = await netsh.ResetAsync();
    l.Warn($"清空系统规则 => {(r.Ok ? "成功" : r.Message)}");
    if (r.Ok) await rec.RunAsync(false);
    return Results.Ok(new { ok = r.Ok, message = r.Ok ? "已清空并重新应用登记规则" : r.Message });
});

api.MapPost("/settings", async (AppSettings input, RuleStore s, NetshService netsh, LogSink l) =>
{
    var host = (input.ListenHost ?? "").Trim();
    if (host.Length == 0) host = "127.0.0.1";
    if (!RuleValidator.IsIp(host))
        return Results.BadRequest(new { ok = false, message = "Web 监听地址必须是合法 IP（127.0.0.1 / 0.0.0.0 / ::）" });
    if (input.ListenPort is < 1 or > 65535)
        return Results.BadRequest(new { ok = false, message = "Web 监听端口必须在 1-65535 之间" });
    if (input.IntervalSec < 5) input.IntervalSec = 5;

    var old = s.LoadSettings();
    input.ListenHost = host;
    if (input.AuthEnabled && string.IsNullOrWhiteSpace(input.AuthToken))
        input.AuthToken = old.AuthToken; // 留空则沿用当前令牌
    // 开启鉴权时不允许沿用默认 admin（首次进入会被强制修改，这里兜底拦截）
    if (input.AuthEnabled && !RuleValidator.IsTokenValid(input.AuthToken, out var tokenErr))
        return Results.BadRequest(new { ok = false, message = tokenErr });

    var bindingChanged = !string.Equals(old.ListenHost, input.ListenHost, StringComparison.OrdinalIgnoreCase)
                         || old.ListenPort != input.ListenPort;

    s.SaveSettings(input);

    // 允许远程访问（非环回地址）时自动放通 Web 端口的入站防火墙，其他设备才能访问
    var remote = !IPAddress.IsLoopback(IPAddress.Parse(host));
    if (remote && NetshService.IsAdministrator())
    {
        var fw = await netsh.AllowWebFirewallAsync(input.ListenPort);
        l.Info($"放通 Web 端口 {input.ListenPort} 防火墙 => {(fw.Ok ? "成功" : fw.Message)}");
    }

    l.Info($"设置更新：巡检 {input.IntervalSec}s / 自动恢复 {input.AutoRecover} / 清理未登记 {input.RemoveUnknown} / Web 监听 {host}:{input.ListenPort} / 鉴权 {(input.AuthEnabled ? "开" : "关")}");
    return Results.Ok(new
    {
        ok = true,
        settings = s.LoadSettings(),
        bindingChanged,
        message = bindingChanged ? "设置已保存；Web 监听配置有变更，重启后生效" : "设置已保存",
    });
});

// Windows 服务（开机自启）状态与操作
api.MapGet("/service", async (ServiceManager sm) =>
{
    var status = await sm.QueryAsync();
    return Results.Ok(new
    {
        ok = true,
        runningAsService = ServiceManager.RunningAsService,
        exePath = ServiceManager.ExePath,
        isAdmin = NetshService.IsAdministrator(),
        status,
    });
});

api.MapPost("/service/install", async (ServiceManager sm, LogSink l) =>
{
    var r = await sm.InstallAsync();
    l.Info($"安装开机自启服务 => {r.message}");
    return Results.Ok(new { ok = r.ok, message = r.message });
});

api.MapPost("/service/uninstall", async (ServiceManager sm, LogSink l) =>
{
    var r = await sm.UninstallAsync();
    l.Info($"卸载开机自启服务 => {r.message}");
    return Results.Ok(new { ok = r.ok, message = r.message });
});

api.MapPost("/service/start", async (ServiceManager sm, LogSink l) =>
{
    var r = await sm.StartAsync();
    l.Info($"启动服务 => {r.message}");
    return Results.Ok(new { ok = r.ok, message = r.message });
});

api.MapPost("/service/stop", async (ServiceManager sm, LogSink l) =>
{
    var r = await sm.StopAsync();
    l.Info($"停止服务 => {r.message}");
    return Results.Ok(new { ok = r.ok, message = r.message });
});

// 重启程序/服务：Web 监听配置变更后使其生效
api.MapPost("/app/restart", (ServiceManager sm, LogSink l) =>
{
    l.Warn("收到重启请求，稍后自动重启以应用监听配置");
    if (ServiceManager.RunningAsService)
    {
        _ = Task.Run(async () =>
        {
            try { await sm.RestartAsync(); }
            catch (Exception ex) { l.Error($"服务重启失败：{ex.Message}"); }
        });
        return Results.Ok(new { ok = true, message = "服务重启中，请几秒后刷新页面" });
    }

    // 控制台模式：拉起新实例后退出当前进程（子进程继承当前提权状态）
    _ = Task.Run(async () =>
    {
        try
        {
            await Task.Delay(800);
            var exe = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exe))
            {
                l.Error("自重启失败：无法确定程序路径");
                return;
            }
            // 过滤掉命令行里混入的 dll 名，避免拼进新进程参数
            var cargs = string.Join(' ', Environment.GetCommandLineArgs().Skip(1)
                .Where(a => !a.Trim('"').EndsWith(".dll", StringComparison.OrdinalIgnoreCase)));

            // UseShellExecute=true：子进程开自己的控制台窗口，不继承父进程的 stdout 管道，
            // 否则父进程退出后子进程第一次写控制台就会因句柄失效而崩溃
            if (Path.GetFileName(exe).Equals("dotnet.exe", StringComparison.OrdinalIgnoreCase))
            {
                var dll = Path.Combine(baseDir, "PortProxyManager.dll");
                Process.Start(new ProcessStartInfo
                {
                    FileName = "dotnet",
                    Arguments = $"\"{dll}\" {cargs}",
                    UseShellExecute = true,
                    WorkingDirectory = baseDir,
                });
            }
            else
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = exe,
                    Arguments = cargs,
                    UseShellExecute = true,
                    WorkingDirectory = baseDir,
                });
            }
        }
        catch (Exception ex)
        {
            l.Error($"自重启失败：{ex.Message}");
            return;
        }
        Environment.Exit(0);
    });
    return Results.Ok(new { ok = true, message = "程序重启中，请几秒后刷新页面" });
});

api.MapGet("/logs", (LogSink l, int? n) => Results.Ok(l.Recent(n ?? 200)));

static async Task<(bool ok, string message)> ApplyOne(PortProxyRule rule, NetshService netsh, LogSink l)
{
    if (!rule.Enabled)
    {
        var d = await netsh.DeleteAsync(rule);
        l.Info($"规则未启用，已确保系统无该规则：{rule.Describe()}");
        return (d.Ok, "已保存（未启用，不生效）");
    }

    var add = await netsh.AddAsync(rule);
    if (!add.Ok)
    {
        l.Error($"立即生效失败 {rule.Describe()} - {add.Message}");
        return (false, $"已保存，但执行失败：{add.Message}");
    }

    if (rule.OpenFirewall) await netsh.EnsureFirewallAsync(rule.ListenPort);

    l.Ok($"立即生效成功 {rule.Describe()}");
    return (true, "已保存并立即生效");
}

app.MapGet("/health", () => Results.Ok(new { ok = true, time = DateTime.Now }));

// 启动即幂等放通 Web 端口防火墙（默认 0.0.0.0 监听，局域网可通过本机 IP 访问）
if (NetshService.IsAdministrator())
{
    var nfw = app.Services.GetRequiredService<NetshService>();
    var fw = await nfw.EnsureWebFirewallAsync(boundPort);
    log.Info(fw.Ok
        ? $"Web 端口 {boundPort} 防火墙已放通（幂等检查）"
        : $"Web 端口 {boundPort} 防火墙放通失败：{fw.Message}");
    Console.WriteLine($" Web 端口防火墙: {(fw.Ok ? $"已放通 ({boundPort})" : "放通失败：" + fw.Message)}");
}
else
{
    Console.WriteLine(" Web 端口防火墙: 非管理员，跳过放通");
}

var url = builder.Configuration["urls"]
          ?? $"http://{store.LoadSettings().ListenHost}:{store.LoadSettings().ListenPort}";

Console.WriteLine("==============================================");
Console.WriteLine(" PortProxy Manager 已启动");
Console.WriteLine($" 运行方式: {(ServiceManager.RunningAsService ? "Windows 服务（开机自启）" : "控制台")}");
Console.WriteLine($" 管理页面: {url}");
Console.WriteLine($" 数据目录: {dataDir}");
Console.WriteLine($" 页面目录: {webRoot ?? "(未找到 wwwroot，使用内嵌页面)"}");
Console.WriteLine($" 管理员权限: {NetshService.IsAdministrator()}");
if (firstRun)
    Console.WriteLine(" 首次运行: 访问令牌默认 admin，进入页面后会要求你立即修改；忘记令牌可删除 data\\settings.json 重置");
Console.WriteLine("==============================================");

app.Run();

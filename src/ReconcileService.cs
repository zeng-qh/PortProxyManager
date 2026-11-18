namespace PortProxyManager;

/// <summary>比对「期望规则」与「系统实际规则」，补齐缺失、纠正漂移。</summary>
public sealed class ReconcileService
{
    private readonly NetshService _netsh;
    private readonly RuleStore _store;
    private readonly LogSink _log;

    public ReconcileService(NetshService netsh, RuleStore store, LogSink log)
    {
        _netsh = netsh;
        _store = store;
        _log = log;
    }

    public DateTime LastRun { get; private set; }
    public bool LastOk { get; private set; } = true;
    public string LastSummary { get; private set; } = "尚未巡检";

    /// <param name="forceFirewall">true 时对已登记规则强制放通防火墙入站端口。</param>
    public async Task<ReconcileReport> RunAsync(bool forceFirewall, CancellationToken ct = default)
    {
        var report = new ReconcileReport();
        LastRun = DateTime.Now;

        var settings = _store.LoadSettings();
        var expected = _store.Load().Where(r => r.Enabled).ToList();
        var actual = await _netsh.ShowAsync(ct);
        var actualMap = actual.ToDictionary(a => a.Key, a => a);

        // 1. 补齐 / 纠正
        foreach (var rule in expected)
        {
            actualMap.TryGetValue(rule.Key, out var cur);
            if (cur != null && cur.Mode == rule.Mode && cur.ConnectAddress == rule.ConnectAddress && cur.ConnectPort == rule.ConnectPort)
            {
                report.Unchanged.Add(rule.Describe());
                if (rule.OpenFirewall || forceFirewall) await OpenFirewallAsync(rule, report, ct);
                continue;
            }

            if (cur != null)
            {
                var del = await _netsh.DeleteActualAsync(cur, ct);
                if (!del.Ok)
                {
                    report.Failed.Add($"删除漂移规则失败 {cur.Describe()} - {del.Message}");
                    _log.Error($"删除漂移规则失败 {cur.Describe()} - {del.Message}");
                    continue;
                }
                report.Removed.Add($"漂移 {cur.Describe()}");
            }

            var add = await _netsh.AddAsync(rule, ct);
            if (add.Ok)
            {
                report.Added.Add(rule.Describe());
                _log.Ok($"已生效 {rule.Describe()}");
            }
            else
            {
                report.Failed.Add($"添加失败 {rule.Describe()} - {add.Message}");
                _log.Error($"添加失败 {rule.Describe()} - {add.Message}");
            }

            if (rule.OpenFirewall || forceFirewall) await OpenFirewallAsync(rule, report, ct);
        }

        // 2. 清理未登记规则（可选）
        if (settings.RemoveUnknown)
        {
            var expectedKeys = expected.Select(r => r.Key).ToHashSet();
            foreach (var a in actual)
            {
                if (expectedKeys.Contains(a.Key)) continue;
                var del = await _netsh.DeleteActualAsync(a, ct);
                if (del.Ok)
                {
                    report.Removed.Add($"未登记 {a.Describe()}");
                    _log.Warn($"已删除未登记规则 {a.Describe()}");
                }
                else
                {
                    report.Failed.Add($"删除未登记规则失败 {a.Describe()} - {del.Message}");
                }
            }
        }

        // 3. 禁用但仍在系统中生效的规则
        var disabled = _store.Load().Where(r => !r.Enabled).ToList();
        foreach (var r in disabled)
        {
            if (!actualMap.ContainsKey(r.Key)) continue;
            var del = await _netsh.DeleteAsync(r, ct);
            if (del.Ok)
            {
                report.Removed.Add($"已禁用 {r.Describe()}");
                _log.Info($"已移除被禁用的规则 {r.Describe()}");
            }
            else
            {
                report.Failed.Add($"移除被禁用规则失败 {r.Describe()} - {del.Message}");
            }
        }

        LastOk = report.Failed.Count == 0;
        LastSummary = $"新增 {report.Added.Count}，移除 {report.Removed.Count}，失败 {report.Failed.Count}，无变化 {report.Unchanged.Count}";
        if (report.HasChange) _log.Info($"巡检完成：{LastSummary}");
        return report;
    }

    private async Task OpenFirewallAsync(PortProxyRule rule, ReconcileReport report, CancellationToken ct)
    {
        var fw = await _netsh.AllowFirewallAsync(rule.ListenPort, ct);
        if (!fw.Ok && !fw.Message.Contains("已存在", StringComparison.OrdinalIgnoreCase))
        {
            report.Failed.Add($"防火墙放通失败 端口 {rule.ListenPort} - {fw.Message}");
        }
    }
}

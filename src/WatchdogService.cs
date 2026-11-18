namespace PortProxyManager;

/// <summary>自动巡检后台服务：网络波动导致规则丢失时自动补回来。</summary>
public sealed class WatchdogService : BackgroundService
{
    private readonly ReconcileService _reconciler;
    private readonly RuleStore _store;
    private readonly LogSink _log;

    public WatchdogService(ReconcileService reconciler, RuleStore store, LogSink log)
    {
        _reconciler = reconciler;
        _store = store;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _log.Info($"巡检服务启动，管理员权限：{NetshService.IsAdministrator()}");

        // 启动后先跑一次，把已登记规则立即同步到系统
        await SafeReconcile(stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            var s = _store.LoadSettings();
            var interval = Math.Max(5, s.IntervalSec);
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(interval), stoppingToken);
            }
            catch (TaskCanceledException)
            {
                break;
            }

            if (!s.AutoRecover) continue;
            await SafeReconcile(stoppingToken);
        }

        _log.Info("巡检服务已停止");
    }

    private async Task SafeReconcile(CancellationToken ct)
    {
        try
        {
            await _reconciler.RunAsync(false, ct);
        }
        catch (OperationCanceledException)
        {
            // 正常退出
        }
        catch (Exception ex)
        {
            _log.Error($"巡检异常：{ex.Message}");
        }
    }
}

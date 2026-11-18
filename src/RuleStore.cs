using System.Text.Json;

namespace PortProxyManager;

/// <summary>期望规则的本地持久化（JSON），带进程内锁避免并发写坏文件。</summary>
public sealed class RuleStore
{
    private readonly string _ruleFile;
    private readonly string _settingFile;
    private readonly object _lock = new();
    private static readonly JsonSerializerOptions Opts = new() { WriteIndented = true };

    public RuleStore(string ruleFile, string settingFile)
    {
        _ruleFile = ruleFile;
        _settingFile = settingFile;
        Directory.CreateDirectory(Path.GetDirectoryName(ruleFile)!);
    }

    public List<PortProxyRule> Load()
    {
        lock (_lock)
        {
            if (!File.Exists(_ruleFile)) return new List<PortProxyRule>();
            try
            {
                return JsonSerializer.Deserialize<List<PortProxyRule>>(File.ReadAllText(_ruleFile)) ?? new();
            }
            catch
            {
                return new List<PortProxyRule>();
            }
        }
    }

    public void Save(List<PortProxyRule> rules)
    {
        lock (_lock)
        {
            var tmp = _ruleFile + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(rules, Opts));
            File.Move(tmp, _ruleFile, true);
        }
    }

    public AppSettings LoadSettings()
    {
        lock (_lock)
        {
            if (!File.Exists(_settingFile)) return new AppSettings();
            try
            {
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(_settingFile)) ?? new AppSettings();
            }
            catch
            {
                return new AppSettings();
            }
        }
    }

    public void SaveSettings(AppSettings s)
    {
        lock (_lock)
        {
            if (s.IntervalSec < 5) s.IntervalSec = 5;
            File.WriteAllText(_settingFile, JsonSerializer.Serialize(s, Opts));
        }
    }
}

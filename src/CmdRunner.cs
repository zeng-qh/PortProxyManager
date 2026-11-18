using System.Diagnostics;
using System.Text;

namespace PortProxyManager;

/// <summary>统一命令执行器：cmd /c "chcp 65001 &gt;nul &amp; {cmd}"，强制 UTF-8 输出，规避中文 Windows GBK 乱码。</summary>
public static class CmdRunner
{
    public static async Task<NetshResult> RunAsync(string command, CancellationToken ct = default, ILogger? logger = null)
    {
        var psi = new ProcessStartInfo("cmd.exe")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = Environment.SystemDirectory,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        psi.ArgumentList.Add("/c");
        psi.ArgumentList.Add($"chcp 65001 >nul & {command}");

        try
        {
            using var p = Process.Start(psi) ?? throw new InvalidOperationException("无法启动 cmd.exe");
            var so = await p.StandardOutput.ReadToEndAsync(ct);
            var se = await p.StandardError.ReadToEndAsync(ct);
            await p.WaitForExitAsync(ct);
            return new NetshResult
            {
                Ok = p.ExitCode == 0,
                ExitCode = p.ExitCode,
                Output = so.Trim(),
                Error = se.Trim(),
            };
        }
        catch (Exception ex)
        {
            logger?.LogError(ex, "命令执行异常: {Command}", command);
            return new NetshResult { Ok = false, ExitCode = -1, Error = ex.Message };
        }
    }
}

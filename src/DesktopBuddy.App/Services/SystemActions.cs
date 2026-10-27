using System.Diagnostics;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using DesktopBuddy.Models;
using Forms = System.Windows.Forms;
using Drawing = System.Drawing;

namespace DesktopBuddy.Services;

public static class SystemActions
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValueName = "DesktopBuddy";

    public static void OpenApprovedTarget(ApprovedTarget target)
        => Process.Start(CreateStartInfo(target));

    internal static ProcessStartInfo CreateStartInfo(ApprovedTarget target)
    {
        if (target.Kind == "url")
        {
            if (!Uri.TryCreate(target.Value, UriKind.Absolute, out var uri)
                || uri.Scheme is not ("http" or "https" or "mailto"))
                throw new InvalidOperationException("仅允许 http、https 和 mailto 链接。");
            return new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true };
        }

        if (target.Kind == "uwp")
        {
            if (string.IsNullOrWhiteSpace(target.Value)) throw new InvalidOperationException("UWP AUMID 不能为空。");
            var parts = target.Value.Split('!');
            if (parts.Length != 2 || parts.Any(part => part.Length == 0 || part.Length > 100
                || part.Any(ch => !(char.IsLetterOrDigit(ch) || ch is '.' or '_' or '-'))))
                throw new InvalidOperationException("UWP AUMID 格式无效。");
            return new ProcessStartInfo($"shell:AppsFolder\\{target.Value}") { UseShellExecute = true };
        }

        if (target.Kind != "path") throw new InvalidOperationException("白名单目标类型无效。");
        var path = Path.GetFullPath(target.Value);
        if (!File.Exists(path)) throw new FileNotFoundException("找不到已批准的程序或快捷方式。", path);
        var extension = Path.GetExtension(path);
        if (!extension.Equals(".exe", StringComparison.OrdinalIgnoreCase)
            && !extension.Equals(".lnk", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("仅允许打开 exe 或 lnk 文件。");
        return new ProcessStartInfo(path) { UseShellExecute = true };
    }

    public static void SetAutoStart(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true)
            ?? throw new InvalidOperationException("无法打开当前用户的开机启动设置。");
        if (!enabled)
        {
            key.DeleteValue(RunValueName, throwOnMissingValue: false);
            return;
        }

        var exe = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(exe) || !exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("请先发布为独立 exe，再启用开机启动。");
        key.SetValue(RunValueName, $"\"{exe}\" --startup-hidden", RegistryValueKind.String);
    }

    public static string CaptureVirtualDesktop()
    {
        var bounds = Forms.SystemInformation.VirtualScreen;
        if (bounds.Width <= 0 || bounds.Height <= 0) throw new InvalidOperationException("无法读取显示器范围。");
        using var bitmap = new Drawing.Bitmap(bounds.Width, bounds.Height);
        using (var graphics = Drawing.Graphics.FromImage(bitmap))
            graphics.CopyFromScreen(bounds.Left, bounds.Top, 0, 0, bounds.Size, Drawing.CopyPixelOperation.SourceCopy);

        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "保存桌面截图",
            Filter = "PNG 图片|*.png",
            FileName = $"DesktopBuddy-{DateTime.Now:yyyyMMdd-HHmmss}.png",
            AddExtension = true,
            DefaultExt = ".png"
        };
        if (dialog.ShowDialog() != true) return "已取消截图保存。";
        bitmap.Save(dialog.FileName, ImageFormat.Png);
        return $"截图已保存：{dialog.FileName}";
    }

    public static void LockComputer()
    {
        if (!NativeMethods.LockWorkStation())
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "Windows 未能锁定工作站。");
    }
}

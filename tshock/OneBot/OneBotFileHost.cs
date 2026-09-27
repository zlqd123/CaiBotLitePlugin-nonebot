using System.Text;
using TShockAPI;

namespace CaiBotLite.OneBot;

/// <summary>
/// 把 map / 存档这类大文件挂到本服自带的 HTTP 服务上, 让机器人可以发链接
/// </summary>
public static class OneBotFileHost
{
    private const string RoutePrefix = "/files/";

    private static string RootPath => Path.Combine(TShock.SavePath, "caibotlite", "files");

    /// <summary>
    /// 保存一个文件并返回可下载的链接, 返回 null 表示当前配置不支持外发文件
    /// </summary>
    internal static string? Publish(string fileName, byte[] data, string kind)
    {
        var config = Config.Settings.OneBot;
        var publicUrl = config.GetPublicUrl();
        if (string.IsNullOrEmpty(publicUrl))
        {
            TShock.Log.ConsoleInfo("[CaiBotLite]未配置 \"本服公开地址\", 无法外发文件, 请检查 OneBot 配置");
            return null;
        }

        if (data.Length > Math.Max(1024, config.MaxAttachmentBytes))
        {
            TShock.Log.ConsoleInfo($"[CaiBotLite]文件 {fileName} 体积({data.Length / 1024 / 1024}MB)超过上限, 已跳过");
            return null;
        }

        try
        {
            Directory.CreateDirectory(RootPath);
            Cleanup(config.AttachmentKeepHours);

            var token = Guid.NewGuid().ToString("N")[..12];
            var name = SanitizeFileName(fileName);
            File.WriteAllBytes(Path.Combine(RootPath, $"{token}_{name}"), data);
            return $"{publicUrl}{RoutePrefix}{token}/{Uri.EscapeDataString(name)}";
        }
        catch (Exception e)
        {
            TShock.Log.ConsoleError($"[CaiBotLite]保存附件 {fileName} 失败: {e.Message}");
            return null;
        }
    }

    /// <summary>
    /// 校验下载请求, 通过则返回本地文件路径
    /// </summary>
    internal static bool TryResolve(string token, string fileName, out string path)
    {
        path = "";
        try
        {
            if (string.IsNullOrWhiteSpace(token) || string.IsNullOrWhiteSpace(fileName))
            {
                return false;
            }

            // token 与文件名都必须是"安全"的, 不允许任何路径分隔符
            if (!IsSafe(token) || !IsSafe(fileName))
            {
                return false;
            }

            var fullPath = Path.Combine(RootPath, $"{token}_{fileName}");
            if (!File.Exists(fullPath))
            {
                return false;
            }

            path = fullPath;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool IsSafe(string value)
    {
        if (value.Length > 128)
        {
            return false;
        }

        foreach (var c in value)
        {
            if (!IsSafeChar(c))
            {
                return false;
            }
        }

        return value != "." && value != "..";
    }

    private static string SanitizeFileName(string fileName)
    {
        var name = Path.GetFileName(fileName);
        var builder = new StringBuilder(name.Length);
        foreach (var c in name)
        {
            builder.Append(IsSafeChar(c) ? c : '_');
        }

        return builder.Length == 0 ? "file" : builder.ToString();
    }

    private static bool IsSafeChar(char c)
    {
        return char.IsLetterOrDigit(c) || c is '_' or '-' or '.' or ' ';
    }

    private static void Cleanup(int keepHours)
    {
        try
        {
            if (!Directory.Exists(RootPath))
            {
                return;
            }

            var expired = DateTime.Now.AddHours(-Math.Max(1, keepHours));
            foreach (var file in Directory.EnumerateFiles(RootPath))
            {
                if (File.GetLastWriteTime(file) < expired)
                {
                    File.Delete(file);
                }
            }
        }
        catch (Exception e)
        {
            TShock.Log.ConsoleInfo($"[CaiBotLite]清理历史附件失败: {e.Message}");
        }
    }
}

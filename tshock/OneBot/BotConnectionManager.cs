using CaiBotLite.Common;
using CaiBotLite.Enums;
using TShockAPI;

namespace CaiBotLite.OneBot;

/// <summary>
/// 机器人连接管理器, 负责按配置创建/销毁当前的连接并把数据交给 <see cref="CaiBotApi" />
/// </summary>
public static class BotConnectionManager
{
    private static IBotConnection? _connection;
    private static readonly object SyncRoot = new ();

    /// <summary>
    /// 当前的连接
    /// </summary>
    public static IBotConnection? Connection
    {
        get
        {
            lock (SyncRoot)
            {
                return _connection;
            }
        }
    }

    /// <summary>
    /// 当前是否处于可用状态
    /// </summary>
    public static bool IsConnected => Connection?.IsConnected == true;

    /// <summary>
    /// 是否处于 OneBot 模式
    /// </summary>
    public static bool IsOneBotMode => Config.Settings.OneBot.IsEnabled;

    /// <summary>
    /// 当前状态描述
    /// </summary>
    public static string Status => Connection?.Status ?? "未连接";

    internal static void Init()
    {
        Reload();
    }

    internal static void Stop()
    {
        lock (SyncRoot)
        {
            _connection?.Stop();
            _connection?.Dispose();
            _connection = null;
        }
    }

    /// <summary>
    /// 按当前配置重建连接, /cbl mode 与 /reload 时使用
    /// </summary>
    public static void Reload()
    {
        lock (SyncRoot)
        {
            _connection?.Stop();
            _connection?.Dispose();
            _connection = null;

            if (Config.Settings.OneBot.IsEnabled)
            {
                Config.Settings.OneBot.EnsureSecret();
                _connection = new OneBotConnection();
                TShock.Log.ConsoleInfo("[CaiBotLite]已切换到 OneBot v11 模式, " +
                                       $"通道: {Config.Settings.OneBot.Channel}");
            }
            else
            {
                _connection = new CaiBotConnection();
            }

            _connection.Start();
        }
    }

    /// <summary>
    /// 发送一条数据包
    /// </summary>
    public static void Send(string package)
    {
        var connection = Connection;
        if (connection == null)
        {
            TShock.Log.ConsoleError("[CaiBotLite]当前没有可用的机器人连接, 数据包已丢弃!");
            return;
        }

        connection.Send(package);
    }

    /// <summary>
    /// 把收到的一条 CaiBotLite 数据包交给处理逻辑(不阻塞接收线程)
    /// </summary>
    internal static void Dispatch(string package)
    {
        _ = Task.Run(() =>
        {
            try
            {
                if (CaiBotLite.DebugMode)
                {
                    TShock.Log.ConsoleInfo($"[CaiBotLite]开始处理: {package}");
                }

                CaiBotApi.HandleMessage(package);
            }
            catch (Exception e)
            {
                TShock.Log.ConsoleError("[CaiBotLite]处理消息时发生错误: \n" + $"{e}");
                ReplyError(package, e);
            }
        });
    }

    /// <summary>
    /// 处理请求时出错的话, 立刻回一个 error 应答, 免得机器人那边干等到超时
    /// </summary>
    private static void ReplyError(string package, Exception e)
    {
        try
        {
            var node = Newtonsoft.Json.Linq.JObject.Parse(package);
            if (node["is_request"]?.ToObject<bool>() != true)
            {
                return;
            }

            var requestId = node["request_id"]?.ToObject<string>();
            if (string.IsNullOrEmpty(requestId))
            {
                return;
            }

            new PackageWriter(PackageType.Error, true, requestId)
                .Write("error", $"{node["type"]} 处理失败: {e.Message}")
                .Send();
        }
        catch (Exception replyException)
        {
            TShock.Log.ConsoleError($"[CaiBotLite]回传错误信息时发生错误: {replyException.Message}");
        }
    }

    /// <summary>
    /// 连接成功后发送的握手包, 两种连接方式共用
    /// </summary>
    internal static void SendHello()
    {
        new PackageWriter(PackageType.Hello, false, null)
            .Write("server_core_version", TShock.VersionNum.ToString())
            .Write("plugin_version", CaiBotLite.VersionNum)
            .Write("game_version", Terraria.Main.versionNumber)
            .Write("enable_whitelist", false)
            .Write("system", System.Runtime.InteropServices.RuntimeInformation.RuntimeIdentifier)
            .Write("server_name", TShock.Config.Settings.UseServerName ? TShock.Config.Settings.ServerName : Terraria.Main.worldName)
            .Write("server_id", Config.Settings.OneBot.ServerId)
            .Write("protocol", "onebot_v11")
            // 让机器人知道 map / 存档的下载链接在哪个地址, 由它决定能不能用
            .Write("public_url", Config.Settings.OneBot.GetPublicUrl())
            .Write("settings", new Dictionary<string, object>())
            .Send();
    }
}

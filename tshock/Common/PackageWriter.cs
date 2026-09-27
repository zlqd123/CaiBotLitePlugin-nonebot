using CaiBotLite.Enums;
using CaiBotLite.Models;
using CaiBotLite.OneBot;
using TShockAPI;

namespace CaiBotLite.Common;

[Serializable]
public class PackageWriter(PackageType packageType, bool isRequest, string? requestId)
{
    private static bool Debug => CaiBotLite.DebugMode;
    public Package Package = new (Direction.ToBot, packageType, isRequest, requestId);

    /// <summary>
    /// 这条数据包要回复到哪里(仅 OneBot 模式使用)
    /// </summary>
    public OneBotTarget? Target;

    public PackageWriter Write(string key, object value)
    {
        this.Package.Payload.Add(key, value);
        return this;
    }

    public void Send()
    {
        try
        {
            if (this.Target != null)
            {
                this.Package.Payload[OneBotProtocol.TargetKey] = this.Target;
            }

            var message = this.Package.ToJson();
            if (Debug)
            {
                TShock.Log.ConsoleInfo($"[CaiBotLite]发送BOT数据包：{message}");
            }

            BotConnectionManager.Send(message);
        }
        catch (Exception e)
        {
            TShock.Log.ConsoleInfo($"[CaiBotLite]发送数据包时发生错误：{e}");
        }
    }
}

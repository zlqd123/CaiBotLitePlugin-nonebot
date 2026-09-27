using CaiBotLite.Models;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using TShockAPI;

namespace CaiBotLite.OneBot;

/// <summary>
/// CaiBotLite 与 OneBot v11 机器人之间的桥接协议
/// <para>
/// 协议本身极其简单, 一条连接上流动的每一行都是一个 <see cref="OneBotEnvelope" />:
/// <code>
/// { "v": 1, "secret": "密钥", "package": "CaiBotLite 数据包(JSON字符串)" }
/// </code>
/// <see cref="OneBotEnvelope.Package" /> 就是 CaiBotLite 原封不动的数据包,
/// 所以本插件内部处理消息的逻辑(CaiBotApi)完全不需要感知 OneBot 的存在.
/// </para>
/// <para>
/// 机器人发往服务器的数据包会在 <c>payload.__bridge</c> 里带上"这条消息要回复到哪",
/// 服务器在生成应答数据包时会把它原样带回去(见 <see cref="PackageWriter.Target" />),
/// 于是机器人就知道该把结果发到哪个群/哪个 QQ.
/// </para>
/// </summary>
public static class OneBotProtocol
{
    /// <summary>
    /// 桥接协议版本
    /// </summary>
    public const int ProtocolVersion = 1;

    /// <summary>
    /// 数据包 <c>payload</c> 中携带"回复目标"的键名
    /// </summary>
    public const string TargetKey = "__bridge";

    /// <summary>
    /// 数据包 <c>payload</c> 中携带"附件信息"的键名
    /// </summary>
    public const string FileKey = "__file";

    /// <summary>
    /// 解析收到的一条桥接数据, 返回 <c>null</c> 表示这不是一条合法的数据
    /// </summary>
    internal static OneBotEnvelope? TryParse(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        try
        {
            return JsonConvert.DeserializeObject<OneBotEnvelope>(raw);
        }
        catch (Exception e)
        {
            // 之前这里直接吞掉异常, 出问题时控制台只有一个"无法解析", 完全查不下去
            TShock.Log.ConsoleError(
                $"[CaiBotLite]解析桥接数据失败: {e.GetType().Name}: {e.Message}\n" +
                $"[CaiBotLite]原文({raw.Length} 字符): {Preview(raw)}\n" +
                $"[CaiBotLite]首字节: {(raw.Length > 0 ? string.Join(" ", System.Text.Encoding.UTF8.GetBytes(raw).Take(16).Select(b => b.ToString("X2"))) : "(空)")}");
            return null;
        }
    }

    /// <summary>
    /// 截一段原始文本用于排查
    /// </summary>
    internal static string Preview(string raw)
    {
        var text = raw.Replace("\r", "\\r").Replace("\n", "\\n");
        return text.Length <= 400 ? text : text[..400] + $"...(共{text.Length}字符)";
    }

    /// <summary>
    /// 校验密钥, 密钥为空时一律通过(方便内网/localhost 调试)
    /// </summary>
    internal static bool CheckSecret(string? expected, string? actual)
    {
        if (string.IsNullOrEmpty(expected))
        {
            return true;
        }

        return string.Equals(expected, actual, StringComparison.Ordinal);
    }
}

/// <summary>
/// 一条桥接数据
/// </summary>
public sealed class OneBotEnvelope
{
    [JsonProperty("v")]
    public int Version { get; set; } = OneBotProtocol.ProtocolVersion;

    [JsonProperty("secret")]
    public string? Secret { get; set; }

    /// <summary>
    /// CaiBotLite 数据包(JSON 字符串)
    /// </summary>
    [JsonProperty("package")]
    public string? Package { get; set; }
}

/// <summary>
/// 一条应答消息的发送目标
/// </summary>
public sealed class OneBotTarget
{
    /// <summary>
    /// 发送这条消息的机器人账号
    /// </summary>
    [JsonProperty("self_id")]
    public long SelfId { get; set; }

    /// <summary>
    /// 目标群号, 0 表示不是群消息
    /// </summary>
    [JsonProperty("group_id")]
    public long GroupId { get; set; }

    /// <summary>
    /// 发送者 QQ 号
    /// </summary>
    [JsonProperty("user_id")]
    public long UserId { get; set; }

    /// <summary>
    /// 被回复的那条消息, 用于"回复"而不是"另起一言"
    /// </summary>
    [JsonProperty("message_id")]
    public long MessageId { get; set; }

    /// <summary>
    /// 会话标识, 机器人用它区分不同功能的会话(如商店会话)
    /// </summary>
    [JsonProperty("session")]
    public string Session { get; set; } = "";

    /// <summary>
    /// 是否需要@发送者
    /// </summary>
    [JsonProperty("at_sender")]
    public bool AtSender { get; set; }

    /// <summary>
    /// 发送者在本服务器上的白名单状态等附加信息, 仅供机器人端使用
    /// </summary>
    [JsonProperty("nickname")]
    public string Nickname { get; set; } = "";

    /// <summary>
    /// 是否是管理员(机器人端判定)
    /// </summary>
    [JsonProperty("is_admin")]
    public bool IsAdmin { get; set; }

    /// <summary>
    /// 机器人告诉服务器的附件外发偏好
    /// <list type="bullet">
    ///     <item>null / link: 走下载链接(默认)</item>
    ///     <item>inline: 本服文件服务在机器人那边打不开, 直接把内容用 base64 发过来</item>
    /// </list>
    /// </summary>
    [JsonProperty("file_mode")]
    public string? FileMode { get; set; }
}

/// <summary>
/// 附件信息, 用来把大文件(map/存档)变成一条可下载的链接
/// </summary>
public sealed class OneBotFileInfo
{
    [JsonProperty("name")]
    public string Name { get; set; } = "";

    [JsonProperty("url")]
    public string Url { get; set; } = "";

    [JsonProperty("size")]
    public long Size { get; set; }

    /// <summary>
    /// image 表示可以直接用图片消息段发送, file 表示用文件消息段发送
    /// </summary>
    [JsonProperty("kind")]
    public string Kind { get; set; } = "file";
}

internal static class OneBotPayload
{
    /// <summary>
    /// 把请求里带来的回复目标取出来, 同时从 payload 中移除(不污染 CaiBotLite 协议)
    /// </summary>
    internal static OneBotTarget? ExtractTarget(Payload payload)
    {
        if (!payload.TryGetValue(OneBotProtocol.TargetKey, out var value))
        {
            return null;
        }

        payload.Remove(OneBotProtocol.TargetKey);
        return value switch
        {
            JObject obj => obj.ToObject<OneBotTarget>(),
            _ => null
        };
    }
}

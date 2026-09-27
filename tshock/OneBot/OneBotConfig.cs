using Newtonsoft.Json;
using TShockAPI;

namespace CaiBotLite.OneBot;

/// <summary>
/// OneBot v11 相关配置
/// </summary>
public sealed class OneBotConfig
{
    /// <summary>
    /// 机器人连接方式
    /// <list type="bullet">
    ///     <item>caibot: 连接 CaiBot 官方机器人(默认)</item>
    ///     <item>onebot: 连接自建的 OneBot v11 机器人(nonebot / NapCat / Lagrange 等)</item>
    /// </list>
    /// </summary>
    [JsonProperty("机器人连接方式")]
    public string Mode { get; set; } = "caibot";

    /// <summary>
    /// OneBot 通道形式
    /// <list type="bullet">
    ///     <item>auto(默认): 同时监听并主动连接, 谁先连上就用谁, 适合两边网络不互通的情况</item>
    ///     <item>client: 只由本服主动连接机器人(适合本服有公网地址、机器人在 NAT 后)</item>
    ///     <item>server: 只监听, 由机器人反向 WebSocket 连接本服(适合本服有公网地址)</item>
    ///     <item>http: 只监听, 由机器人用 HTTP 主动推送</item>
    /// </list>
    /// </summary>
    [JsonProperty("OneBot通道")]
    public string Channel { get; set; } = "auto";

    /// <summary>
    /// 机器人侧桥接地址(auto / client / http 通道使用, 留空则只监听)
    /// <list type="bullet">
    ///     <item>client: ws://127.0.0.1:8080/caibotlite</item>
    ///     <item>http: http://127.0.0.1:8080/caibotlite/push</item>
    /// </list>
    /// </summary>
    [JsonProperty("机器人地址")]
    public string BotUrl { get; set; } = "";

    /// <summary>
    /// 本服监听地址(auto / server / http 通道使用), 需要以 / 结尾
    /// <para>不占用游戏端口 7777, 也不依赖 TShock REST API, 默认 17779</para>
    /// </summary>
    [JsonProperty("本服监听地址")]
    public string ListenUrl { get; set; } = "http://*:17779/";

    /// <summary>
    /// 本服对外可访问的地址, 用于拼接 map / 存档的下载链接, 例如 http://1.2.3.4:17779
    /// <para>只有"机器人能访问到本服"时才需要填(本服有公网地址/做了内网穿透)。
    /// 留空时地图图片会改用 base64 直接内联发送, 不影响功能</para>
    /// </summary>
    [JsonProperty("本服公开地址")]
    public string PublicUrl { get; set; } = "";

    /// <summary>
    /// 通讯密钥, 两端必须一致; 留空则自动生成
    /// </summary>
    [JsonProperty("通讯密钥")]
    public string Secret { get; set; } = "";

    /// <summary>
    /// 本服务器在机器人侧的标识, 留空时使用世界名
    /// </summary>
    [JsonProperty("服务器标识")]
    public string ServerId { get; set; } = "";

    /// <summary>
    /// 机器人侧未指定回复目标时(例如白名单校验)默认发送到的群号, 0 表示不指定
    /// </summary>
    [JsonProperty("默认回复群号")]
    public long DefaultGroupId { get; set; }

    /// <summary>
    /// 是否在查背包等数据包中附带物品名称(由服务器提供, 机器人无需内置物品表)
    /// </summary>
    [JsonProperty("附送物品名称")]
    public bool ProvideItemNames { get; set; } = true;

    /// <summary>
    /// 附件大小上限(字节), 超过则不发送附件
    /// </summary>
    [JsonProperty("附件大小上限")]
    public int MaxAttachmentBytes { get; set; } = 32 * 1024 * 1024;

    /// <summary>
    /// 内联 base64 附件大小上限(字节), 超过则只发下载链接
    /// <para>本服没有公网地址时, 地图图片只能走 base64 内联, QQ 侧单条消息建议不超过 8MB</para>
    /// </summary>
    [JsonProperty("内联附件上限")]
    public int MaxInlineBytes { get; set; } = 8 * 1024 * 1024;

    /// <summary>
    /// 附件在本地保留的时长(小时)
    /// </summary>
    [JsonProperty("附件保留小时")]
    public int AttachmentKeepHours { get; set; } = 6;

    /// <summary>
    /// 重连间隔(秒)
    /// </summary>
    [JsonProperty("重连间隔")]
    public int ReconnectSeconds { get; set; } = 5;

    /// <summary>
    /// 心跳间隔(秒)
    /// </summary>
    [JsonProperty("心跳间隔")]
    public int HeartbeatSeconds { get; set; } = 60;

    /// <summary>
    /// 机器人允许远程执行(数据包类型 call_command)的指令名白名单
    /// <para>
    /// 远程执行用的是 CaiBotPlayer 这个虚拟身份, 它固定拥有超级管理员权限, 所以
    /// TShock 自身的权限检查在这里完全不起作用 —— 白名单是唯一的安全边界。
    /// 只有写在这里的指令名(不区分大小写)才放行, 留空则一条都不允许。
    /// </para>
    /// <para>
    /// 默认放行: time 调时间、clear 停雨停风、wind 调风力、worldevent 触发/结束天气事件,
    /// 以及一批纯查询指令。
    /// worldevent 的事件类型: meteor / fullmoon / bloodmoon / eclipse / invasion /
    /// sandstorm / rain / lanternsnight / meteorshower( invasion 还能带
    /// goblins / snowmen / pirates / pumpkinmoon / frostmoon / martians),
    /// 不给参数时服务器会自己列出。
    /// /give /ban /sudo /off 这类绝对不要加。
    /// </para>
    /// <para>
    /// 这份名单是最后一道防线, 覆盖范围应当**大于**机器人侧的放行范围。
    /// nonebot 插件的 commands.json 里 "/泰拉 执行" 的"开放指令"决定普通玩家
    /// 能发什么(默认只有 time / clear / wind / worldevent), 这里的纯查询指令
    /// 是留给机器人管理员(CAIBOTLITE__ADMINS)用的。
    /// </para>
    /// </summary>
    [JsonProperty("允许远程执行的指令")]
    public List<string> RemoteCommandAllowList { get; set; } =
    [
        "time", "clear", "wind", "worldevent",
        "version", "motd", "rules", "playing", "serverinfo", "worldinfo",
        "moonphase", "death", "bossinfo", "help", "aliases"
    ];

    /// <summary>
    /// 判断某条远程指令是否在白名单里
    /// <para>只取第一个词做匹配, 所以 "/time" 与 "time night" 都算同一条</para>
    /// </summary>
    internal bool IsRemoteCommandAllowed(string? command)
    {
        if (string.IsNullOrWhiteSpace(command))
        {
            return false;
        }

        var name = command.Trim().TrimStart('/');
        var head = name.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.TrimStart('/');
        if (string.IsNullOrEmpty(head))
        {
            return false;
        }

        foreach (var allowed in this.RemoteCommandAllowList)
        {
            if (!string.IsNullOrWhiteSpace(allowed) &&
                string.Equals(allowed.Trim().TrimStart('/'), head, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// 是否运行在 OneBot 模式
    /// </summary>
    public bool IsEnabled => string.Equals(this.Mode, "onebot", StringComparison.OrdinalIgnoreCase);

    internal static readonly string[] SupportedModes = ["caibot", "onebot"];

    internal static readonly string[] SupportedChannels = ["auto", "client", "server", "http"];

    /// <summary>
    /// 首次运行时生成一个通讯密钥, 避免桥接端口裸奔
    /// </summary>
    internal void EnsureSecret()
    {
        if (!string.IsNullOrEmpty(this.Secret))
        {
            return;
        }

        this.Secret = Guid.NewGuid().ToString("N")[..16];
        Config.Settings.Write();
        TShock.Log.ConsoleInfo($"[CaiBotLite]已自动生成 OneBot 通讯密钥: {this.Secret} (请填写到 nonebot 插件配置中)");
    }

    /// <summary>
    /// 是否需要本服监听端口(auto / server / http 通道)
    /// </summary>
    internal bool NeedListener => GetChannel() is "auto" or "server" or "http";

    /// <summary>
    /// 是否需要主动连接机器人(auto / client 通道, 且填了机器人地址)
    /// </summary>
    internal bool NeedDialer => GetChannel() is "auto" or "client";

    /// <summary>
    /// 归一化后的通道名
    /// </summary>
    internal string GetChannel()
    {
        var channel = this.Channel?.Trim().ToLowerInvariant() ?? "auto";
        return SupportedChannels.Contains(channel) ? channel : "auto";
    }

    /// <summary>
    /// 监听地址, 端口为 0 时使用默认端口 17779
    /// <para>主机名建议用 <c>*</c>: Windows 的 HttpListener 把 <c>0.0.0.0</c> 当通配处理,
    /// 但在 Linux 上可能绑不上; <c>*</c> 两边都稳</para>
    /// </summary>
    internal string GetListenUrl()
    {
        var url = string.IsNullOrWhiteSpace(this.ListenUrl) ? "http://*:17779/" : this.ListenUrl.Trim();
        if (!url.EndsWith('/'))
        {
            url += "/";
        }

        return url;
    }

    /// <summary>
    /// 公开地址(不带结尾斜杠)
    /// </summary>
    internal string GetPublicUrl()
    {
        return string.IsNullOrWhiteSpace(this.PublicUrl) ? "" : this.PublicUrl.TrimEnd('/');
    }
}

using CaiBotLite.Enums;
using CaiBotLite.Models;
using CaiBotLite.OneBot;
using Microsoft.Xna.Framework;
using Terraria;
using TerrariaApi.Server;
using TShockAPI;
using CaiBotPlayer = CaiBotLite.Models.CaiBotPlayer;

namespace CaiBotLite.Common;

internal static class CaiBotApi
{
    internal static void HandleMessage(string receivedData)
    {
        var package = Package.Parse(receivedData);
        var packetWriter = new PackageWriter(package.Type, package.IsRequest, package.RequestId);
        packetWriter.Target = OneBotPayload.ExtractTarget(package.Payload);
        try
        {
            switch (package.Type)
            {
                case PackageType.UnbindServer:
                    TShock.Log.ConsoleInfo("[CaiBotLite]BOT发送解绑命令...");
                    var reason = package.Read<string>("reason");
                    TShock.Log.ConsoleInfo($"[CaiBotLite]原因: {reason}");
                    if (BotConnectionManager.IsOneBotMode)
                    {
                        TShock.Log.ConsoleInfo("[CaiBotLite]OneBot 模式下无需绑定, 已忽略该解绑请求");
                        break;
                    }

                    Config.Settings.Token = string.Empty;
                    Config.Settings.Write();
                    CaiBotLite.GenBindCode(EventArgs.Empty);
                    BotConnectionManager.Reload();
                    break;
                case PackageType.CallCommand:
                    var command = package.Read<string>("command");
                    var userOpenId = package.Read<string>("user_open_id");
                    var groupOpenId = package.Read<string>("group_open_id");

                    // 远程执行跑在 CaiBotPlayer(固定超管)上, TShock 自身的权限检查对它无效,
                    // 所以必须在这里卡一道白名单, 这是唯一真正拦得住的地方。
                    if (!Config.Settings.OneBot.IsRemoteCommandAllowed(command))
                    {
                        TShock.Log.ConsoleError($"[CaiBotLite]已拒绝机器人执行不在白名单里的指令: {command}");
                        packetWriter
                            .Write("is_text", true)
                            .Write("error", "该指令不在允许执行的名单里")
                            .Write("output", $"命令 [{command}] 未被允许执行\n" +
                                            $"当前允许: {string.Join(", ", Config.Settings.OneBot.RemoteCommandAllowList)}")
                            .Send();
                        return;
                    }

                    CaiBotPlayer tr = new ();
                    Commands.HandleCommand(tr, command);
                    TShock.Utils.SendLogs($"[CaiBotLite] \"{DescribeUser(userOpenId, packetWriter.Target)}\"" +
                                          $"来自{DescribeGroup(groupOpenId, packetWriter.Target)}执行了: {command}",
                        Color.PaleVioletRed);
                    packetWriter
                        .Write("output", tr.GetCommandOutput())
                        .Send();
                    break;
                case PackageType.PlayerList:
                    packetWriter
                        .Write("server_name", string.IsNullOrEmpty(Main.worldName) ? "地图还没加载捏~" : Main.worldName)
                        .Write("player_list", TShock.Players.Where(x => x is { Active: true }).Select(x => x.Name))
                        .Write("current_online", TShock.Utils.GetActivePlayerCount())
                        .Write("max_online", TShock.Config.Settings.MaxSlots)
                        .Write("process", Config.Settings.ShowProcessInPlayerList ? Utils.GetWorldProcess() : "")
                        .Send();
                    break;
                case PackageType.Progress:

                    var bossLock = new Dictionary<string, string>();


                    if (BossLockSupport.Support)
                    {
                        bossLock = BossLockSupport.GetLockBosses();
                    }

                    if (ProgressControlSupport.Support)
                    {
                        var progressControlBosses = ProgressControlSupport.GetLockBosses();
                        bossLock = bossLock.Count < progressControlBosses.Count ? progressControlBosses : bossLock;
                    }

                    packetWriter
                        .Write("is_text", false)
                        .Write("process", Utils.GetProcessList())
                        .Write("kill_counts", Utils.GetKillCountList())
                        .Write("boss_lock", bossLock)
                        .Write("world_name", Main.worldName)
                        .Write("drunk_world", Main.drunkWorld)
                        .Write("zenith_world", Main.zenithWorld)
                        .Write("world_icon", Utils.GetWorldIconName())
                        .Send();
                    break;
                case PackageType.LookBag:
                    var lookBagName = package.Read<string>("player_name");

                    var lookPlr = TShock.Players.FirstOrDefault(x => x?.Name == lookBagName);
                    if (lookPlr != null)
                    {
                        WriteLookBag(packetWriter, LookBag.LookOnline(lookPlr.TPlayer));
                        break;
                    }

                    var acc = TShock.UserAccounts.GetUserAccountByName(lookBagName);
                    if (acc == null)
                    {
                        packetWriter
                            .Write("exist", 0)
                            .Send();
                        break;
                    }

                    var data = TShock.CharacterDB.GetPlayerData(new TSPlayer(-1), acc.ID);
                    if (data == null)
                    {
                        packetWriter
                            .Write("exist", false)
                            .Send();
                        break;
                    }

                    WriteLookBag(packetWriter, LookBag.LookOffline(acc, data));
                    break;
                case PackageType.MapImage:
                    var imageBytes = MapGeneratorSupport.CreatMapImgBytes();
                    if (WriteAttachment(packetWriter, "地图.png", imageBytes, "image"))
                    {
                        break;
                    }

                    // 没有下载链接可用(非 OneBot 模式或本服没有公网地址)
                    var mapImage = CanInline(imageBytes.LongLength);
                    packetWriter
                        .Write("name", "地图.png")
                        .Write("too_large", !mapImage)
                        .Write("base64", mapImage ? Utils.CompressBase64(Convert.ToBase64String(imageBytes)) : "")
                        .Send();
                    break;
                case PackageType.MapFile:
                    var mapFile = MapGeneratorSupport.CreateMapFile();
                    if (WriteAttachment(packetWriter, mapFile.Item2, mapFile.Item1, "file"))
                    {
                        break;
                    }

                    var mapFileInline = CanInline(mapFile.Item1.LongLength);
                    packetWriter
                        .Write("name", mapFile.Item2)
                        .Write("too_large", !mapFileInline)
                        .Write("base64", mapFileInline
                            ? Utils.CompressBase64(Convert.ToBase64String(mapFile.Item1))
                            : "")
                        .Send();

                    break;
                case PackageType.WorldFile:
                    var worldName = Path.GetFileName(Main.worldPathName);
                    if (WriteAttachment(packetWriter, worldName, File.ReadAllBytes(Main.worldPathName), "file"))
                    {
                        break;
                    }

                    var worldInline = CanInline(new FileInfo(Main.worldPathName).Length);
                    packetWriter
                        .Write("name", worldName)
                        .Write("too_large", !worldInline)
                        .Write("base64", worldInline
                            ? Utils.CompressBase64(Utils.FileToBase64String(Main.worldPathName))
                            : "")
                        .Send();

                    break;
                case PackageType.PluginList:
                    var pluginList = ServerApi.Plugins.Select(p => new PluginInfo(p.Plugin.Name, p.Plugin.Description, p.Plugin.Author, p.Plugin.Version)).ToList();
                    packetWriter
                        .Write("is_mod", false)
                        .Write("plugins", pluginList)
                        // 把远程执行白名单一起报出去。机器人侧要显示"当前允许执行什么",
                        // 也需要据此校验自己公开的那部分有没有和服务端脱节——
                        // 权威只有这一份(CaiBotLite.json), 机器人不该自己抄一份。
                        .Write("allowed_commands", Config.Settings.OneBot.RemoteCommandAllowList
                            .Where(x => !string.IsNullOrWhiteSpace(x))
                            .Select(x => x.Trim().TrimStart('/'))
                            .ToList())
                        .Send();
                    break;
                case PackageType.RankData:
                    var rankType = package.Read<string>("rank_type");
                    var arg = package.Read<string>("arg");

                    var rankTypeEnum = Rank.GetRankTypeByName(rankType);

                    switch (rankTypeEnum)
                    {
                        case RankTypes.Boss:
                            var bosses = Rank.GetBossByIdOrName(arg);
                            switch (bosses.Count)
                            {
                                case 0:
                                    packetWriter
                                        .Write("rank_type_support", true)
                                        .Write("need_arg", true)
                                        .Write("arg_support", false)
                                        .Write("message", "没有找到任何相关的BOSS呢~")
                                        .Write("support_args", Array.Empty<string>())
                                        .Send();
                                    break;
                                case > 1:
                                    packetWriter
                                        .Write("rank_type_support", true)
                                        .Write("need_arg", true)
                                        .Write("arg_support", false)
                                        .Write("message", "找到多个匹配的BOSS:\n")
                                        .Write("support_args", bosses.Select(x => $"{x.TypeName} ({x.type})"))
                                        .Send();
                                    break;
                                case 1:
                                    var boss = bosses.First();
                                    packetWriter
                                        .Write("rank_type_support", true)
                                        .Write("need_arg", true)
                                        .Write("arg_support", true)
                                        .Write("rank", Rank.GetBossRank(boss))
                                        .Send();
                                    break;
                            }

                            break;
                        case RankTypes.EconomicCoin:
                            if (string.IsNullOrEmpty(arg))
                            {
                                packetWriter
                                    .Write("rank_type_support", true)
                                    .Write("need_arg", true)
                                    .Write("arg_support", false)
                                    .Write("message", $"需要参数[货币], 以下是支持的货币:\n")
                                    .Write("support_args", EconomicSupport.SupportCoins)
                                    .Send();
                                return;
                            }

                            if (!EconomicSupport.SupportCoins.Contains(arg))
                            {
                                packetWriter
                                    .Write("rank_type_support", true)
                                    .Write("need_arg", true)
                                    .Write("arg_support", false)
                                    .Write("message", $"没有找到{arg}, 以下是支持的货币:\n")
                                    .Write("support_args", EconomicSupport.SupportCoins)
                                    .Send();
                                return;
                            }

                            packetWriter
                                .Write("rank_type_support", true)
                                .Write("need_arg", true)
                                .Write("arg_support", true)
                                .Write("rank", EconomicSupport.GetCoinRank(arg))
                                .Send();


                            break;
                        case RankTypes.Death:
                            packetWriter
                                .Write("rank_type_support", true)
                                .Write("need_arg", false)
                                .Write("rank", Rank.GetDeathRank())
                                .Send();
                            break;
                        case RankTypes.Online:
                            packetWriter
                                .Write("rank_type_support", true)
                                .Write("need_arg", false)
                                .Write("rank", Rank.GetOnlineRank())
                                .Send();
                            break;
                        case RankTypes.Fishing:
                            packetWriter
                                .Write("rank_type_support", true)
                                .Write("need_arg", false)
                                .Write("rank", Rank.GetFishingRank())
                                .Send();
                            break;
                        case RankTypes.Unknown:
                            packetWriter
                                .Write("rank_type_support", false)
                                .Write("support_rank_types", Rank.SupportRankTypes)
                                .Send();
                            break;
                    }

                    break;
                case PackageType.Hello:
                case PackageType.Heartbeat:
                case PackageType.Unknown:
                case PackageType.Error:
                // 白名单(Whitelist) / 自踢(SelfKick) / 商店(ShopBuy, ShopCondition) 已按需下线,
                // 收到这些包直接忽略, 避免旧版机器人发过来的请求产生副作用
                default:
                    break;
            }
        }
        catch (Exception ex)
        {
            TShock.Log.ConsoleError($"[CaiBotLite] 处理BOT数据包时出错:\n" +
                                    $"{ex}\n" +
                                    $"源数据包: {receivedData}");

            packetWriter.Package.Type = PackageType.Error;
            packetWriter.Write("error", ex.ToString())
                .Send();
        }
    }

    /// <summary>
    /// 查背包的应答, OneBot 模式下会顺带把物品/增益名称发过去
    /// </summary>
    private static void WriteLookBag(PackageWriter packetWriter, LookBag lookBag)
    {
        packetWriter
            .Write("is_text", false)
            .Write("name", lookBag.Name)
            .Write("exist", true)
            .Write("life", $"{lookBag.Health}/{lookBag.MaxHealth}")
            .Write("mana", $"{lookBag.Mana}/{lookBag.MaxMana}")
            .Write("quests_completed", lookBag.QuestsCompleted)
            .Write("inventory", lookBag.ItemList)
            .Write("buffs", lookBag.Buffs)
            .Write("enhances", lookBag.Enhances)
            .Write("economic", EconomicData.GetEconomicData(lookBag.Name));

        if (BotConnectionManager.IsOneBotMode && Config.Settings.OneBot.ProvideItemNames)
        {
            packetWriter
                .Write("item_names", OneBotNameProvider.BuildItemNames(lookBag.ItemList.Select(x => x[0])))
                .Write("buff_names", OneBotNameProvider.BuildBuffNames(lookBag.Buffs))
                .Write("enhance_names", OneBotNameProvider.BuildItemNames(lookBag.Enhances));
        }

        packetWriter.Send();
    }

    /// <summary>
    /// OneBot 模式下把大文件挂到本服的文件服务上, 返回 true 表示已经处理
    /// <para>本服没有公网地址(机器人访问不到本服)时返回 false, 由调用方退回 base64 内联</para>
    /// </summary>
    private static bool WriteAttachment(PackageWriter packetWriter, string fileName, byte[] data, string kind)
    {
        if (!BotConnectionManager.IsOneBotMode)
        {
            return false;
        }

        // 机器人说它那边打不开本服的文件服务(常见于机器人跑在无公网容器里), 直接退回 base64
        if (packetWriter.Target?.FileMode == "inline")
        {
            return false;
        }

        if (data.LongLength > Config.Settings.OneBot.MaxAttachmentBytes)
        {
            TShock.Log.ConsoleInfo(
                $"[CaiBotLite]{fileName} 超过附件大小上限({Config.Settings.OneBot.MaxAttachmentBytes} 字节), 已跳过");
            packetWriter
                .Write("name", fileName)
                .Write("too_large", true)
                .Send();
            return true;
        }

        var url = OneBotFileHost.Publish(fileName, data, kind);
        if (url == null)
        {
            return false;
        }

        packetWriter
            .Write("name", fileName)
            .Write(OneBotProtocol.FileKey, new OneBotFileInfo
            {
                Name = fileName,
                Url = url,
                Size = data.LongLength,
                Kind = kind
            })
            .Send();
        return true;
    }

    /// <summary>
    /// 没有下载链接可用时, 判断能否退回 base64 内联发送
    /// <para>官方 CaiBot 机器人保持原样(一直是内联); OneBot 模式下受"内联附件上限"约束,
    /// 免得一条几十 MB 的消息把 QQ 端撑爆</para>
    /// </summary>
    private static bool CanInline(long length)
    {
        if (!BotConnectionManager.IsOneBotMode)
        {
            return true;
        }

        return length <= Config.Settings.OneBot.MaxInlineBytes;
    }

    /// <summary>
    /// 日志中显示执行指令的人
    /// </summary>
    private static string DescribeUser(string userOpenId, OneBotTarget? target)
    {
        if (!string.IsNullOrEmpty(userOpenId))
        {
            return userOpenId;
        }

        if (target == null)
        {
            return "OneBot";
        }

        return string.IsNullOrEmpty(target.Nickname) ? target.UserId.ToString() : target.Nickname;
    }

    /// <summary>
    /// 日志中显示执行指令的群
    /// </summary>
    private static string DescribeGroup(string groupOpenId, OneBotTarget? target)
    {
        if (!string.IsNullOrEmpty(groupOpenId))
        {
            return $"群\"{groupOpenId}\"";
        }

        if (target is { GroupId: > 0 })
        {
            return $"群{target.GroupId}";
        }

        return "私聊";
    }
}
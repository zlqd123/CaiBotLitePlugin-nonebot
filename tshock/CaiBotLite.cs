using CaiBotLite.Common;
using CaiBotLite.Models;
using CaiBotLite.OneBot;
using System.Reflection;
using Terraria;
using TerrariaApi.Server;
using TShockAPI;
using TShockAPI.Hooks;
using Program = Terraria.Program;

namespace CaiBotLite;

[ApiVersion(2, 1)]
// ReSharper disable once ClassNeverInstantiated.Global
public class CaiBotLite(Main game) : TerrariaPlugin(game)
{
    public static readonly Version VersionNum = new (2026, 7, 8, 1);
    internal static int InitCode = -1;
    internal static bool DebugMode = Program.LaunchParameters.ContainsKey("-caidebug");

    /// <summary>
    /// 每次加载生成一个, 用来发现"插件被加载了两次"(比如两个目录各有一份 DLL)
    /// </summary>
    internal static readonly string InstanceId = Guid.NewGuid().ToString("N")[..6];

    private const string CharacterInfoKey = "CaiBotLite.CharacterInfo";
    public override string Author => "Cai,羽学,西江";
    public override string Description => "CaiBot官方机器人的适配插件";
    public override string Name => "CaiBotLitePlugin";

    public override Version Version => VersionNum;


    public override void Initialize()
    {
        TShock.Log.ConsoleInfo(
            $"[CaiBotLite]插件载入: v{Version} 程序集={System.Reflection.Assembly.GetExecutingAssembly().Location} " +
            $"实例={InstanceId} 进程={Environment.ProcessId}");

        AppDomain.CurrentDomain.AssemblyResolve += this.CurrentDomain_AssemblyResolve;
        Config.Settings.Read();
        Config.Settings.Write();
        Database.Init();
        ServerApi.Hooks.GamePostInitialize.Register(this, GenBindCode);
        ServerApi.Hooks.NpcKilled.Register(this, OnNpcKilled);
        ServerApi.Hooks.ServerLeave.Register(this, OnServerLeave);
        ServerApi.Hooks.GamePostUpdate.Register(this, OnGameUpdate);
        GeneralHooks.ReloadEvent += GeneralHooksOnReloadEvent;
        PlayerHooks.PlayerPostLogin += PlayerHooksOnPlayerPostLogin;
        GetDataHandlers.KillMe.Register(KillMe, HandlerPriority.Highest);
        MapGeneratorSupport.Init();
        EconomicSupport.Init();
        BossLockSupport.Init();
        ProgressControlSupport.Init();
        BotConnectionManager.Init();
        Commands.ChatCommands.Add(new Command("caibotlite.admin", CaiBotCommand, "caibotlite", "cbl"));
        ClearCharacterInfoForActivePlayers();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            var asm = Assembly.GetExecutingAssembly();
            Commands.ChatCommands.RemoveAll(c => c.CommandDelegate.Method.DeclaringType?.Assembly == asm);
            AppDomain.CurrentDomain.AssemblyResolve -= this.CurrentDomain_AssemblyResolve;
            ServerApi.Hooks.GamePostInitialize.Deregister(this, GenBindCode);
            ServerApi.Hooks.NpcKilled.Deregister(this, OnNpcKilled);
            ServerApi.Hooks.ServerLeave.Deregister(this, OnServerLeave);
            ServerApi.Hooks.GamePostUpdate.Deregister(this, OnGameUpdate);
            GeneralHooks.ReloadEvent -= GeneralHooksOnReloadEvent;
            PlayerHooks.PlayerPostLogin -= PlayerHooksOnPlayerPostLogin;
            GetDataHandlers.KillMe.UnRegister(KillMe);
            BotConnectionManager.Stop();
            ClearCharacterInfoForActivePlayers();
        }

        base.Dispose(disposing);
    }

    private static int _timer;

    private static void OnGameUpdate(EventArgs args)
    {
        if (_timer >= 60 * 60 * 5)
        {
            foreach (var player in TShock.Players.Where(x => x is { Active: true }))
            {
                var characterInfo = player.GetData<CaiCharacterInfo>(CharacterInfoKey);
                characterInfo?.CreatOrUpdate();
            }

            _timer = 0;
        }

        _timer++;
    }

    private static void KillMe(object? sender, GetDataHandlers.KillMeEventArgs e)
    {
        var characterInfo = e.Player.GetData<CaiCharacterInfo>(CharacterInfoKey);
        if (characterInfo == null)
        {
            return;
        }

        characterInfo.Death++;
    }

    private static void OnServerLeave(LeaveEventArgs args)
    {
        var player = TShock.Players[args.Who];
        var characterInfo = player?.GetData<CaiCharacterInfo>(CharacterInfoKey);
        characterInfo?.CreatOrUpdate();
    }

    private static void PlayerHooksOnPlayerPostLogin(PlayerPostLoginEventArgs e)
    {
        var characterInfo = CaiCharacterInfo.GetByName(e.Player.Account.Name)
                            // ReSharper disable once ArrangeObjectCreationWhenTypeNotEvident
                            ?? new () { AccountName = e.Player.Account.Name };
        e.Player.RemoveData(CharacterInfoKey);
        e.Player.SetData(CharacterInfoKey, characterInfo);
    }

    private static void OnNpcKilled(NpcKilledEventArgs args)
    {
        if (!args.npc.boss)
        {
            return;
        }

        for (var i = 0; i < byte.MaxValue; i++)
        {
            if (!args.npc.playerInteraction[i])
            {
                continue;
            }

            var player = TShock.Players[i];
            // ReSharper disable once UseNullPropagation
            if (player == null)
            {
                return;
            }

            var characterInfo = player.GetData<CaiCharacterInfo>(CharacterInfoKey);
            if (characterInfo == null)
            {
                return;
            }

            var bossInfo = characterInfo.BossKills.FirstOrDefault(x => x.BossId == args.npc.type);
            if (bossInfo == null)
            {
                characterInfo.BossKills.Add(new BossKillInfo { AccountName = player.Account.Name, BossId = args.npc.type, KillCounts = 1 });
            }
            else
            {
                bossInfo.KillCounts++;
            }
        }
    }

    private static void GeneralHooksOnReloadEvent(ReloadEventArgs e)
    {
        Config.Settings.Read();
        BotConnectionManager.Reload();
        e.Player.SendSuccessMessage("[CaiBotLite]配置文件已重载 :)");
    }

    private static void CaiBotCommand(CommandArgs args)
    {
        var plr = args.Player;

        if (args.Parameters.Count == 0)
        {
            ShowHelpText();
            return;
        }


        switch (args.Parameters[0].ToLowerInvariant())
        {
            default:
                ShowHelpText();
                break;
            case "test":
                _timer = 60 * 60 * 5;
                Console.WriteLine("你怎么知道Cai喜欢留一个测试命令?");
                break;
            case "reset":
                CaiCharacterInfo.CleanAll();
                Mail.CleanAll();
                plr.SendInfoMessage($"[CaiBotLite]已重置统计数据!");
                break;
            case "help":
                ShowHelpText();
                return;
            case "信息":
            case "info":
                plr.SendInfoMessage($"[CaiBot信息]\n" +
                                    $"插件版本: v{VersionNum}\n" +
                                    $"机器人连接: {BotConnectionManager.Status}\n" +
                                    $"机器人模式: {(BotConnectionManager.IsOneBotMode ? "OneBot v11" : "CaiBot 官方")}\n" +
                                    $"设置QQ群: {(Config.Settings.GroupNumber == 0L ? "未设置" : Config.Settings.GroupNumber)}\n" +
                                    $"绑定状态: {Config.Settings.Token != ""}\n" +
                                    $"Debug模式: {DebugMode}\n" +
                                    $"MapGenerator支持: {MapGeneratorSupport.Support}\n" +
                                    $"ProgressControl支持: {ProgressControlSupport.Support}\n" +
                                    $"BossLockSupport支持: {BossLockSupport.Support}\n" +
                                    $"Economic API支持: {EconomicSupport.GetCoinsSupport}\n" +
                                    $"Economic RPG支持: {EconomicSupport.GetLevelNameSupport}\n" +
                                    $"Economic Skill支持: {EconomicSupport.GetSkillSupport}\n"
                );
                break;
            case "调试":
            case "debug":
                DebugMode = !DebugMode;
                plr.SendInfoMessage($"[CaiBotLite]调试模式已{(DebugMode ? "开启" : "关闭")}!");
                break;
            case "验证码":
            case "code":
                if (!string.IsNullOrEmpty(Config.Settings.Token))
                {
                    plr.SendInfoMessage("[CaiBotLite]服务器已绑定无法生成验证码!");
                    return;
                }

                GenBindCode(EventArgs.Empty);
                plr.SendInfoMessage("[CaiBotLite]验证码已生成,请在后台查看喵~");
                break;

            case "解绑":
            case "unbind":
                if (!BotConnectionManager.IsOneBotMode)
                {
                    if (string.IsNullOrEmpty(Config.Settings.Token))
                    {
                        plr.SendInfoMessage("[CaiBotLite]服务器没有绑定任何群哦!");
                        return;
                    }

                    Config.Settings.Token = string.Empty;
                    Config.Settings.Write();
                }

                BotConnectionManager.Reload();
                GenBindCode(EventArgs.Empty);
                plr.SendInfoMessage("[CaiBotLite]验证码已生成,请在后台查看喵~");
                break;
            case "群号":
            case "group":
                if (args.Parameters.Count < 2)
                {
                    plr.SendErrorMessage($"格式错误!" +
                                         $"正确格式: /caibotlite group <群号>");
                    return;
                }

                if (!long.TryParse(args.Parameters[1], out Config.Settings.GroupNumber))
                {
                    plr.SendErrorMessage($"无效参数,群号必须是长整数!");
                    return;
                }

                Config.Settings.Write();
                plr.SendInfoMessage($"[CaiBotLite]白名单提示群号已改为{Config.Settings.GroupNumber}");
                break;
            case "onebot":
                OneBotCommand(args);
                break;
        }

        return;

        void ShowHelpText()
        {
            if (!PaginationTools.TryParsePageNumber(args.Parameters, 1, plr, out var pageNumber))
            {
                return;
            }

            List<string> lines =
            [
                "/cbl debug CaiBot调试开关",
                "/cbl reset 重置数据统计",
                "/cbl code 生成并且展示验证码",
                "/cbl info 显示CaiBot的一些信息",
                "/cbl unbind 主动解除绑定",
                "/cbl whitelist 开关白名单",
                "/cbl group <群号> 设置踢出显示的群号",
                "/cbl onebot <子命令> 管理OneBot v11连接, 输入/cbl onebot help查看帮助",
                "/cbl test Cai保留用于测试的命令, 乱用可能会爆掉"
            ];

            PaginationTools.SendPage(
                plr, pageNumber, lines,
                new PaginationTools.Settings { HeaderFormat = "帮助 ({0}/{1})：", FooterFormat = "输入 {0}caibotlite help {{0}} 查看更多".SFormat(Commands.Specifier) }
            );
        }
    }

    /// <summary>
    /// /cbl onebot xxx, 用于在游戏内快速配置 OneBot 连接
    /// </summary>
    private static void OneBotCommand(CommandArgs args)
    {
        var plr = args.Player;
        var oneBot = Config.Settings.OneBot;

        if (args.Parameters.Count < 2)
        {
            plr.SendInfoMessage($"[CaiBotLite] OneBot 配置:\n" +
                                $"模式: {oneBot.Mode} ({(oneBot.IsEnabled ? "OneBot v11" : "CaiBot 官方")})\n" +
                                $"通道: {oneBot.GetChannel()}\n" +
                                $"机器人地址: {(string.IsNullOrEmpty(oneBot.BotUrl) ? "未设置(只监听)" : oneBot.BotUrl)}\n" +
                                $"本服监听: {oneBot.GetListenUrl()}\n" +
                                $"公开地址: {(string.IsNullOrEmpty(oneBot.PublicUrl) ? "未设置(map改用base64内联)" : oneBot.PublicUrl)}\n" +
                                $"服务器标识: {(string.IsNullOrEmpty(oneBot.ServerId) ? "未设置" : oneBot.ServerId)}\n" +
                                $"通讯密钥: {(string.IsNullOrEmpty(oneBot.Secret) ? "未设置" : oneBot.Secret)}\n" +
                                $"连接状态: {BotConnectionManager.Status}");

            if (!oneBot.IsEnabled)
            {
                plr.SendWarningMessage("[CaiBotLite] 当前是 CaiBot 官方模式, 自建 OneBot 桥接【没有启动】, " +
                                       "本服监听端口不会有任何服务应答。\n" +
                                       "改用 nonebot 的话执行: /cbl onebot mode onebot");
            }

            return;
        }

        string Get(int index) => args.Parameters[index];

        switch (Get(1).ToLowerInvariant())
        {
            case "help":
                plr.SendInfoMessage("[CaiBotLite] /cbl onebot <子命令> [参数]\n" +
                                    "  mode <caibot|onebot> 切换机器人类型(用 nonebot 必须先切到 onebot)\n" +
                                    "  channel <auto|client|server|http> 切换通道(auto=监听+主动连, 先到先得)\n" +
                                    "  url <地址> 设置机器人地址(留空则只等机器人连过来)\n" +
                                    "  listen <地址> 设置本服监听地址(默认 http://*:17779/)\n" +
                                    "  public <地址> 设置本服公开地址(机器人要能访问到, 否则map走内联)\n" +
                                    "  secret <密钥> 设置通讯密钥\n" +
                                    "  id <标识> 设置服务器标识\n" +
                                    "  group <群号> 设置默认回复群号\n" +
                                    "  exec [+/-\u003c指令\u003e] 查看/增删远程执行白名单\n" +
                                    "  reload 重启连接使配置生效");
                break;
            case "mode":
                if (args.Parameters.Count < 3 || !OneBotConfig.SupportedModes.Contains(Get(2).ToLowerInvariant()))
                {
                    plr.SendErrorMessage($"格式错误! 可选值: {string.Join('/', OneBotConfig.SupportedModes)}");
                    return;
                }

                oneBot.Mode = Get(2).ToLowerInvariant();
                Config.Settings.Write();
                BotConnectionManager.Reload();
                plr.SendInfoMessage($"[CaiBotLite]机器人模式已切换为 {oneBot.Mode}");
                break;
            case "channel":
                if (args.Parameters.Count < 3 || !OneBotConfig.SupportedChannels.Contains(Get(2).ToLowerInvariant()))
                {
                    plr.SendErrorMessage($"格式错误! 可选值: {string.Join('/', OneBotConfig.SupportedChannels)}");
                    return;
                }

                oneBot.Channel = Get(2).ToLowerInvariant();
                Config.Settings.Write();
                BotConnectionManager.Reload();
                plr.SendInfoMessage($"[CaiBotLite]OneBot 通道已切换为 {oneBot.Channel}");
                break;
            case "url":
                oneBot.BotUrl = args.Parameters.Count > 2 ? Get(2) : "";
                Config.Settings.Write();
                BotConnectionManager.Reload();
                plr.SendInfoMessage($"[CaiBotLite]机器人地址已设置为 {(string.IsNullOrEmpty(oneBot.BotUrl) ? "空(只等机器人连过来)" : oneBot.BotUrl)}");
                break;
            case "listen":
                oneBot.ListenUrl = args.Parameters.Count > 2 ? Get(2) : "";
                Config.Settings.Write();
                BotConnectionManager.Reload();
                plr.SendInfoMessage($"[CaiBotLite]本服监听地址已设置为 {(string.IsNullOrEmpty(oneBot.ListenUrl) ? "默认(17779)" : oneBot.GetListenUrl())}");
                break;
            case "public":
                oneBot.PublicUrl = args.Parameters.Count > 2 ? Get(2) : "";
                Config.Settings.Write();
                plr.SendInfoMessage($"[CaiBotLite]本服公开地址已设置为 {(string.IsNullOrEmpty(oneBot.PublicUrl) ? "空" : oneBot.PublicUrl)}");
                break;
            case "secret":
                if (args.Parameters.Count < 3)
                {
                    plr.SendErrorMessage("格式错误! 正确格式: /cbl onebot secret <密钥>");
                    return;
                }

                oneBot.Secret = Get(2);
                Config.Settings.Write();
                BotConnectionManager.Reload();
                plr.SendInfoMessage("[CaiBotLite]通讯密钥已更新");
                break;
            case "id":
                oneBot.ServerId = args.Parameters.Count > 2 ? Get(2) : "";
                Config.Settings.Write();
                plr.SendInfoMessage($"[CaiBotLite]服务器标识已设置为 {(string.IsNullOrEmpty(oneBot.ServerId) ? "空" : oneBot.ServerId)}");
                break;
            case "group":
                if (args.Parameters.Count < 3 || !long.TryParse(Get(2), out long groupId))
                {
                    plr.SendErrorMessage("格式错误! 正确格式: /cbl onebot group <群号>");
                    return;
                }

                oneBot.DefaultGroupId = groupId;
                Config.Settings.Write();
                plr.SendInfoMessage($"[CaiBotLite]默认回复群号已设置为 {(groupId == 0 ? "空" : groupId.ToString())}");
                break;
            case "exec":
                if (args.Parameters.Count < 3)
                {
                    plr.SendInfoMessage($"[CaiBotLite] 远程执行白名单(共 {oneBot.RemoteCommandAllowList.Count} 条):\n" +
                                        $"  {string.Join(", ", oneBot.RemoteCommandAllowList)}\n" +
                                        "  /cbl onebot exec + <指令> 放行\n" +
                                        "  /cbl onebot exec - <指令> 禁止\n" +
                                        "注意: 远程执行使用固定超管身份, 白名单是唯一的安全边界, " +
                                        "别加 /give /sudo /off 这种");
                    return;
                }

                var execArgs = string.Join(" ", args.Parameters.Skip(2)).Trim();
                if (execArgs.StartsWith('+'))
                {
                    var name = execArgs[1..].Trim().TrimStart('/');
                    if (string.IsNullOrWhiteSpace(name))
                    {
                        plr.SendErrorMessage("格式错误! 正确格式: /cbl onebot exec + <指令>");
                        return;
                    }

                    if (!oneBot.RemoteCommandAllowList.Contains(name, StringComparer.OrdinalIgnoreCase))
                    {
                        oneBot.RemoteCommandAllowList.Add(name);
                        Config.Settings.Write();
                    }

                    plr.SendInfoMessage($"[CaiBotLite]已放行指令: {name}");
                }
                else if (execArgs.StartsWith('-'))
                {
                    var name = execArgs[1..].Trim().TrimStart('/');
                    var removed = oneBot.RemoteCommandAllowList.RemoveAll(x =>
                        string.Equals(x.Trim().TrimStart('/'), name, StringComparison.OrdinalIgnoreCase));
                    if (removed > 0)
                    {
                        Config.Settings.Write();
                    }

                    plr.SendInfoMessage(removed > 0
                        ? $"[CaiBotLite]已禁止指令: {name}"
                        : $"[CaiBotLite]白名单里本来就没有 {name}");
                }
                else
                {
                    plr.SendErrorMessage("格式错误! 正确格式: /cbl onebot exec +/- <指令>");
                }

                break;
            case "reload":
                Config.Settings.Write();
                BotConnectionManager.Reload();
                plr.SendInfoMessage($"[CaiBotLite]连接已重启, 当前状态: {BotConnectionManager.Status}");
                break;
            default:
                plr.SendErrorMessage("未知的子命令, 输入 /cbl onebot help 查看帮助");
                break;
        }
    }


    public static void GenBindCode(EventArgs? args)
    {
        if (!string.IsNullOrEmpty(Config.Settings.Token))
        {
            return;
        }

        InitCode = new Random().Next(10000000, 99999999);
        TShock.Log.ConsoleError($"[CaiBotLite]您的服务器绑定码为: {InitCode}");
    }
    
    private static void ClearCharacterInfoForActivePlayers()
    {
        foreach (var player in TShock.Players.Where(x => x is { Active: true }))
        {
            player.RemoveData(CharacterInfoKey);
        }
    }


    #region 加载前置

    private Assembly? CurrentDomain_AssemblyResolve(object? sender, ResolveEventArgs args)
    {
        var resourceName =
            $"embedded.{new AssemblyName(args.Name).Name}.dll";
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resourceName);
        if (stream == null)
        {
            return null;
        }

        var assemblyData = new byte[stream.Length];
        _ = stream.Read(assemblyData, 0, assemblyData.Length);
        return Assembly.Load(assemblyData);
    }

    #endregion
}
using System.Net.WebSockets;
using System.Text;
using CaiBotLite.Common;
using CaiBotLite.Enums;
using Newtonsoft.Json;
using TShockAPI;

namespace CaiBotLite.OneBot;

/// <summary>
/// OneBot v11 机器人的连接
/// <para>
/// 这里并不直接使用 OneBot 的 API, 桥接协议本身只有一条 WebSocket / HTTP 通道,
/// 所有的消息发送都由 nonebot 侧的插件完成, 因此本插件对 OneBot 实现(Lagrange / NapCat / LLOneBot ...)
/// 没有任何要求.
/// </para>
/// </summary>
public sealed class OneBotConnection : IBotConnection
{
    private const string HttpEchoRoute = "/caibotlite/push";

    private OneBotListener? _listener;
    private WebSocket? _webSocket;
    private HttpClient? _httpClient;
    private readonly SemaphoreSlim _sendLock = new (1, 1);
    private readonly object _attachLock = new ();
    private volatile bool _stopped;
    private volatile string _status = "未连接";

    public string Name => "OneBot v11";

    public bool IsConnected => _webSocket?.State == WebSocketState.Open;

    public string Status => _status;

    public void Start()
    {
        _stopped = false;
        Config.Settings.OneBot.EnsureSecret();

        var oneBot = Config.Settings.OneBot;
        TShock.Log.ConsoleInfo(
            $"[CaiBotLite]启动桥接(实例={CaiBotLite.InstanceId}): 模式={oneBot.Mode} 通道={GetChannel()} " +
            $"监听地址={oneBot.GetListenUrl()} 机器人地址={(string.IsNullOrEmpty(oneBot.BotUrl) ? "(空)" : oneBot.BotUrl)} " +
            $"公开地址={(string.IsNullOrEmpty(oneBot.PublicUrl) ? "(空)" : oneBot.PublicUrl)} " +
            $"密钥={(string.IsNullOrEmpty(oneBot.Secret) ? "(空!)" : oneBot.Secret)} " +
            $"调试={(CaiBotLite.DebugMode ? "开" : "关")}");

        var channel = GetChannel();
        if (Config.Settings.OneBot.NeedListener)
        {
            _listener = new OneBotListener();
            _listener.OnPackage += package => BotConnectionManager.Dispatch(package);
            _listener.OnWebSocket += OnWebSocketConnected;
            _listener.Start();
            _status = "等待机器人接入";
        }

        if (Config.Settings.OneBot.NeedDialer)
        {
            if (string.IsNullOrEmpty(Config.Settings.OneBot.BotUrl))
            {
                TShock.Log.ConsoleInfo(
                    "[CaiBotLite]未配置『机器人地址』, 仅监听本服端口; 若本服没有公网地址请填写机器人地址");
            }
            else
            {
                _status = "正在连接机器人";
                _ = Task.Run(ConnectLoopAsync);
            }
        }

        if (GetChannel() == "http")
        {
            _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            _status = "等待机器人推送";
            // http 通道没有"连接"这个动作, 直接把握手信息推给机器人
            BotConnectionManager.SendHello();
        }

        _ = Task.Run(HeartBeatLoopAsync);
    }

    public void Stop()
    {
        _stopped = true;
        try
        {
            _webSocket?.Dispose();
        }
        catch
        {
            // 忽略
        }

        _webSocket = null;

        _listener?.Dispose();
        _listener = null;

        _httpClient?.Dispose();
        _httpClient = null;
        _status = "已停止";
    }

    public void Send(string package)
    {
        var envelope = new OneBotEnvelope
        {
            Secret = Config.Settings.OneBot.Secret,
            Package = package
        };
        var json = JsonConvert.SerializeObject(envelope);

        if (CaiBotLite.DebugMode)
        {
            TShock.Log.ConsoleInfo($"[CaiBotLite]发送BOT数据包：{package}");
        }

        var webSocket = _webSocket;
        if (webSocket?.State == WebSocketState.Open)
        {
            _ = SendWebSocketAsyncSafe(webSocket, json);
            return;
        }

        if (GetChannel() == "http")
        {
            _ = PostAsync(json);
            return;
        }

        TShock.Log.ConsoleError(
            $"[CaiBotLite]当前没有可用的机器人连接({_status}, socket=" +
            $"{(webSocket == null ? "null" : webSocket.State.ToString())}), 数据包已丢弃!");
    }

    public void Dispose()
    {
        Stop();
        GC.SuppressFinalize(this);
    }

    private static string GetChannel()
    {
        return Config.Settings.OneBot.GetChannel();
    }

    /// <summary>
    /// 两条链路(本服监听 / 本服主动连接)抢同一个输出通道, 先连上的生效, 另一条自动断开
    /// </summary>
    private bool Attach(WebSocket webSocket, string describe)
    {
        WebSocket? stale;
        lock (_attachLock)
        {
            var current = _webSocket;
            if (current is { State: WebSocketState.Open })
            {
                return false;
            }

            stale = current;
            _webSocket = webSocket;
            _status = describe;
        }

        try
        {
            stale?.Dispose();
        }
        catch
        {
            // 忽略
        }

        TShock.Log.ConsoleInfo($"[CaiBotLite]已连接 OneBot 机器人: {describe}");
        BotConnectionManager.SendHello();
        return true;
    }

    private void Detach(WebSocket webSocket)
    {
        lock (_attachLock)
        {
            if (ReferenceEquals(_webSocket, webSocket))
            {
                _webSocket = null;
                _status = "连接已断开";
            }
        }
    }

    #region 主动连接(自动 / client 通道)

    private async Task ConnectLoopAsync()
    {
        var interval = Math.Max(1, Config.Settings.OneBot.ReconnectSeconds);
        var failures = 0;

        while (!_stopped)
        {
            try
            {
                // 已经有链路连上了(比如机器人主动连过来的), 主动连接这条链路待命即可
                if (IsConnected)
                {
                    failures = 0;
                    await Task.Delay(TimeSpan.FromSeconds(interval));
                    continue;
                }

                if (string.IsNullOrEmpty(Config.Settings.OneBot.BotUrl))
                {
                    _status = "未配置机器人地址";
                    await Task.Delay(TimeSpan.FromSeconds(interval));
                    continue;
                }

                var uri = BuildWebSocketUri(Config.Settings.OneBot.BotUrl);
                var client = new ClientWebSocket();
                client.Options.KeepAliveInterval = TimeSpan.FromSeconds(30);
                await client.ConnectAsync(uri, CancellationToken.None);

                if (!Attach(client, $"{uri.Scheme}://{uri.Host}:{uri.Port}"))
                {
                    // 另一条链路先连上了, 这条让位
                    client.Dispose();
                    await Task.Delay(TimeSpan.FromSeconds(interval));
                    continue;
                }

                failures = 0;
                await ReceiveLoopAsync(client);
            }
            catch (Exception e)
            {
                failures++;
                _status = "连接失败";
                // 对方不可达时不要刷屏
                if (failures == 1 || failures % 20 == 0)
                {
                    TShock.Log.ConsoleInfo($"[CaiBotLite]连接 OneBot 机器人失败: {e.Message}");
                }
            }

            try
            {
                _webSocket?.Dispose();
            }
            catch
            {
                // 忽略
            }

            _webSocket = null;

            if (_stopped)
            {
                return;
            }

            await Task.Delay(TimeSpan.FromSeconds(interval));
        }
    }

    private static Uri BuildWebSocketUri(string botUrl)
    {
        var url = botUrl.Trim();
        if (!url.Contains("://"))
        {
            url = "ws://" + url;
        }
        else if (url.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
        {
            url = "ws://" + url[7..];
        }
        else if (url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            url = "wss://" + url[8..];
        }

        if (!url.Contains('?'))
        {
            url += $"?secret={Uri.EscapeDataString(Config.Settings.OneBot.Secret)}";
        }

        return new Uri(url);
    }

    #endregion

    #region 被动连接(auto / server / http 通道)

    private void OnWebSocketConnected(WebSocket webSocket)
    {
        if (!Attach(webSocket, $"机器人已接入 {Config.Settings.OneBot.GetListenUrl()}"))
        {
            try
            {
                webSocket.CloseAsync(WebSocketCloseStatus.PolicyViolation, "已存在其它连接", CancellationToken.None)
                    .Wait(TimeSpan.FromSeconds(3));
                webSocket.Dispose();
            }
            catch
            {
                // 忽略
            }

            return;
        }

        // 监听接入的连接不会经过 ConnectLoopAsync, 必须在这里自己把读取循环拉起来,
        // 否则没人调用 ReceiveAsync -> 不回 pong -> 机器人按 keepalive 规则断开
        _ = Task.Run(() => ReceiveLoopAsync(webSocket));
    }

    private async Task PostAsync(string json)
    {
        var url = Config.Settings.OneBot.BotUrl?.Trim() ?? "";
        if (string.IsNullOrEmpty(url))
        {
            return;
        }

        if (!url.Contains("://"))
        {
            url = "http://" + url;
        }

        if (!url.EndsWith(HttpEchoRoute))
        {
            url = url.TrimEnd('/') + HttpEchoRoute;
        }

        try
        {
            using var content = new StringContent(json, Encoding.UTF8, "application/json");
            using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = content };
            request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {Config.Settings.OneBot.Secret}");
            var response = await (_httpClient ??= new HttpClient()).SendAsync(request);
            if (!response.IsSuccessStatusCode)
            {
                TShock.Log.ConsoleInfo($"[CaiBotLite]推送数据包失败: {(int)response.StatusCode}");
                _status = "推送失败";
            }
            else
            {
                _status = "已连接";
            }
        }
        catch (Exception e)
        {
            _status = "推送失败";
            if (CaiBotLite.DebugMode)
            {
                TShock.Log.ConsoleInfo($"[CaiBotLite]推送数据包异常: {e.Message}");
            }
        }
    }

    #endregion

    private async Task ReceiveLoopAsync(WebSocket webSocket)
    {
        var buffer = new byte[8192];
        try
        {
            while (!_stopped && webSocket.State == WebSocketState.Open)
            {
                using var stream = new MemoryStream();
                WebSocketReceiveResult result;
                do
                {
                    result = await webSocket.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        break;
                    }

                    await stream.WriteAsync(buffer.AsMemory(0, result.Count));
                } while (!result.EndOfMessage);

                if (result.MessageType == WebSocketMessageType.Close)
                {
                    _status = "已断开";
                    if (result.CloseStatusDescription is { Length: > 0 } reason)
                    {
                        TShock.Log.ConsoleInfo($"[CaiBotLite]机器人断开连接: {reason}");
                    }

                    return;
                }

                HandleRaw(Encoding.UTF8.GetString(stream.ToArray()));
            }
        }
        catch (Exception e)
        {
            _status = "连接异常";
            TShock.Log.ConsoleError($"[CaiBotLite]接收机器人数据时出错: {e.Message}");
            try
            {
                webSocket.Abort();
            }
            catch
            {
                // 忽略
            }
        }
        finally
        {
            // 无论怎么退出都要摘掉, 不然机器人重连时会被判定成"已存在其它连接"
            Detach(webSocket);
        }
    }

    private void HandleRaw(string raw)
    {
        if (CaiBotLite.DebugMode)
        {
            TShock.Log.ConsoleInfo(
                $"[CaiBotLite]收到 {raw.Length} 字符: {Describe(raw)}\n" +
                $"[CaiBotLite]首字节: {Hex(raw)}");
        }

        var envelope = OneBotProtocol.TryParse(raw);
        if (envelope == null || string.IsNullOrWhiteSpace(envelope.Package))
        {
            TShock.Log.ConsoleInfo(
                $"[CaiBotLite]收到无法解析的桥接数据, 已忽略(包内容: " +
                $"{(envelope == null ? "解析失败, 原因见上一行" : $"{(envelope.Package == null ? "package 字段缺失" : "package 字段为空")}")})");
            return;
        }

        if (!OneBotProtocol.CheckSecret(Config.Settings.OneBot.Secret, envelope.Secret))
        {
            TShock.Log.ConsoleError(
                $"[CaiBotLite]桥接数据密钥不匹配, 已丢弃! 收到={Describe2(envelope.Secret)} " +
                $"本服={Describe2(Config.Settings.OneBot.Secret)}");
            return;
        }

        if (CaiBotLite.DebugMode)
        {
            TShock.Log.ConsoleInfo($"[CaiBotLite]分发数据包: {envelope.Package}");
        }

        BotConnectionManager.Dispatch(envelope.Package);
    }

    private static string Describe2(string? value)
    {
        return value == null ? "(null)" : $"\"{value}\"";
    }

    /// <summary>
    /// 排查问题时把原始数据截一段打出来, 免得只能看到一个"解析失败"
    /// </summary>
    private static string Describe(string raw)
    {
        var text = raw.Replace("\r", "\\r").Replace("\n", "\\n");
        return text.Length <= 200 ? text : text[..200] + $"...(共{text.Length}字符)";
    }

    private static string Hex(string raw)
    {
        return string.Join(" ", Encoding.UTF8.GetBytes(raw).Take(16).Select(b => b.ToString("X2")));
    }

    private static async Task SendWebSocketAsync(WebSocket webSocket, string json)
    {
        try
        {
            var bytes = Encoding.UTF8.GetBytes(json);
            await webSocket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, CancellationToken.None);
        }
        catch (Exception e)
        {
            TShock.Log.ConsoleError($"[CaiBotLite]发送数据包时发生错误: {e.GetType().Name}: {e.Message}");
        }
    }

    private async Task SendWebSocketAsyncSafe(WebSocket webSocket, string json)
    {
        // WebSocket 不允许并发发送, 白名单校验(主线程)和心跳(定时器)可能同时触发
        await _sendLock.WaitAsync();
        try
        {
            await SendWebSocketAsync(webSocket, json);
        }
        finally
        {
            _ = _sendLock.Release();
        }
    }

    private async Task HeartBeatLoopAsync()
    {
        var seconds = Math.Max(10, Config.Settings.OneBot.HeartbeatSeconds);
        while (!_stopped)
        {
            await Task.Delay(TimeSpan.FromSeconds(seconds));
            if (_stopped)
            {
                return;
            }

            if (IsConnected || GetChannel() == "http")
            {
                new PackageWriter(PackageType.Heartbeat, false, null).Send();
            }
        }
    }
}

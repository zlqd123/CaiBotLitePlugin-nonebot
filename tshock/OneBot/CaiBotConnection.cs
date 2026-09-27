using CaiBotLite.Common;
using CaiBotLite.Enums;
using Newtonsoft.Json.Linq;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using Terraria;
using TShockAPI;

namespace CaiBotLite.OneBot;

/// <summary>
/// CaiBot 官方机器人的连接(与适配插件原本的实现完全一致)
/// </summary>
public sealed class CaiBotConnection : IBotConnection
{
    private const string BotServerUrl = "api.terraria.ink:22338";

    private ClientWebSocket? _webSocket;
    private readonly SemaphoreSlim _sendLock = new (1, 1);
    private volatile bool _isStop;
    private volatile string _status = "未连接";

    public string Name => "CaiBot";

    public bool IsConnected => _webSocket?.State == WebSocketState.Open;

    public string Status => _webSocket?.State == WebSocketState.Open ? "已连接" : _status;

    public void Start()
    {
        _isStop = false;
        _ = Task.Factory.StartNew(ConnectLoop, TaskCreationOptions.LongRunning);
        _ = Task.Factory.StartNew(HeartBeatLoop, TaskCreationOptions.LongRunning);
    }

    public void Stop()
    {
        _isStop = true;
        try
        {
            _webSocket?.Dispose();
        }
        catch
        {
            // 忽略释放异常
        }

        _webSocket = null;
    }

    public void Send(string package)
    {
        SendRaw(package);
    }

    public void Dispose()
    {
        Stop();
        GC.SuppressFinalize(this);
    }

    private void SendRaw(string package)
    {
        try
        {
            var webSocket = _webSocket;
            if (webSocket?.State != WebSocketState.Open)
            {
                return;
            }

            var messageBytes = Encoding.UTF8.GetBytes(package);
            // WebSocket 不允许并发发送, 白名单校验(主线程)和心跳(定时器)可能同时触发
            _ = SendRawAsync(webSocket, messageBytes);
        }
        catch (Exception e)
        {
            TShock.Log.ConsoleInfo($"[CaiBotLite]发送数据包时发生错误: {e}");
        }
    }

    private async Task SendRawAsync(WebSocket webSocket, byte[] messageBytes)
    {
        await _sendLock.WaitAsync();
        try
        {
            await webSocket.SendAsync(new ArraySegment<byte>(messageBytes), WebSocketMessageType.Text, true,
                CancellationToken.None);
        }
        catch (Exception e)
        {
            TShock.Log.ConsoleInfo($"[CaiBotLite]发送数据包时发生错误: {e.Message}");
        }
        finally
        {
            _ = _sendLock.Release();
        }
    }

    private async Task HeartBeatLoop()
    {
        while (!_isStop)
        {
            await Task.Delay(TimeSpan.FromSeconds(60));
            try
            {
                if (_webSocket?.State == WebSocketState.Open)
                {
                    new PackageWriter(PackageType.Heartbeat, false, null).Send();
                }
            }
            catch
            {
                TShock.Log.ConsoleInfo("[CaiBotLite]心跳包发送失败!");
            }
        }
    }

    private async Task ConnectLoop()
    {
        while (!_isStop)
        {
            try
            {
                var webSocket = new ClientWebSocket();
                _webSocket = webSocket;

                while (string.IsNullOrEmpty(Config.Settings.Token))
                {
                    await Task.Delay(TimeSpan.FromSeconds(10));
                    if (_isStop)
                    {
                        return;
                    }

                    HttpClient client = new () { Timeout = TimeSpan.FromSeconds(5.0) };
                    var response = await client.GetAsync($"https://{BotServerUrl}/server/token/{CaiBotLite.InitCode}");
                    if (response.StatusCode != HttpStatusCode.OK || Config.Settings.Token != "")
                    {
                        continue;
                    }

                    var responseBody = await response.Content.ReadAsStringAsync();
                    var json = JObject.Parse(responseBody);
                    Config.Settings.Token = json["token"]!.ToString();
                    Config.Settings.GroupOpenId = json["group_open_id"]!.ToString();
                    Config.Settings.Write();
                    TShock.Log.ConsoleInfo("[CaiBotLite]被动绑定成功!");
                }

                webSocket.Options.SetRequestHeader("authorization", $"Bearer {Config.Settings.Token}");
                await webSocket.ConnectAsync(new Uri($"wss://{BotServerUrl}/server/ws/{Config.Settings.GroupOpenId}/tshock/"),
                    CancellationToken.None);

                BotConnectionManager.SendHello();
                _status = "已连接";
                TShock.Log.ConsoleInfo("[CaiBotLite]Bot连接成功...");

                while (!_isStop && webSocket.State == WebSocketState.Open)
                {
                    var buffer = new byte[1024];
                    using var memoryStream = new MemoryStream();

                    WebSocketReceiveResult result;
                    do
                    {
                        result = await webSocket.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None);
                        await memoryStream.WriteAsync(buffer.AsMemory(0, result.Count));
                    } while (!result.EndOfMessage);

                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        await webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, string.Empty, CancellationToken.None);
                        var statusCode = (int) result.CloseStatus!;
                        switch (statusCode)
                        {
                            case 4003:
                                Config.Settings.Token = "";
                                Config.Settings.Write();
                                TShock.Log.ConsoleError("[CaiBotLite]服务器认证失败, 请重新绑定!");
                                TShock.Log.ConsoleError($"原因({statusCode}): {result.CloseStatusDescription}");
                                CaiBotLite.GenBindCode(null);
                                break;
                            default:
                                TShock.Log.ConsoleError("[CaiBotLite]Bot主动断开连接!");
                                TShock.Log.ConsoleError($"原因({statusCode}): {result.CloseStatusDescription}");
                                break;
                        }

                        break;
                    }

                    // 原实现只在最后一段里取数据, 这里统一按完整帧解析, 避免长包被截断
                    var receivedData = Encoding.UTF8.GetString(memoryStream.ToArray());
                    if (string.IsNullOrWhiteSpace(receivedData))
                    {
                        continue;
                    }

                    if (CaiBotLite.DebugMode)
                    {
                        TShock.Log.ConsoleInfo($"[CaiBotLite]收到BOT数据包: {receivedData}");
                    }

                    BotConnectionManager.Dispatch(receivedData);
                }
            }
            catch (Exception ex)
            {
                _status = "已断开";
                TShock.Log.ConsoleInfo("[CaiBotLite]Bot断开连接...");
                if (!_isStop)
                {
                    TShock.Log.ConsoleError(ex.ToString());
                }
            }

            await Task.Delay(5000);
        }
    }
}

using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;
using TShockAPI;

namespace CaiBotLite.OneBot;

/// <summary>
/// 本服的桥接监听器(server / http / auto 通道使用)
/// <list type="bullet">
///     <item><c>POST /onebot/v11/event</c> 接收机器人推送的数据包</item>
///     <item><c>GET  /onebot/v11/ws</c> 机器人反向连接(需要在 URL 上带 <c>?secret=</c>)</item>
///     <item><c>GET  /files/{token}/{name}</c> 下载 map / 存档</item>
///     <item><c>GET  /health</c> 健康检查</item>
/// </list>
/// <para>
/// 这里没有用 <see cref="HttpListener" />: 它依赖 Windows 的 HTTP.SYS, 既不接受
/// <c>0.0.0.0</c> 这种字面 IP 前缀(会抛"不支持该请求"), 通配符前缀又需要管理员权限
/// 做 URL 预留, 在 Linux 容器里也不一样。所以直接自己解析 HTTP + 做 WebSocket 握手。
/// </para>
/// </summary>
public sealed class OneBotListener : IDisposable
{
    private const string EventRoute = "/onebot/v11/event";
    private const string WebSocketRoute = "/onebot/v11/ws";
    private const string FileRoute = "/files/";

    /// <summary>RFC 6455 规定的握手魔数</summary>
    private const string WsGuid = "258EAFA5-E914-47DA-95CA-C5AB0DC85B11";

    private const int MaxHeaderSize = 16 * 1024;
    private const int MaxBodySize = 16 * 1024 * 1024;
    private static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(15);

    private TcpListener? _listener;
    private volatile bool _running;
    private string _url = "";

    /// <summary>
    /// 收到一条来自机器人的数据(CaiBotLite 数据包 JSON)
    /// </summary>
    public event Action<string>? OnPackage;

    /// <summary>
    /// 有新的 WebSocket 连接接入
    /// </summary>
    public event Action<WebSocket>? OnWebSocket;

    public bool IsRunning => _running;

    public string Status => _running ? $"监听中({_url})" : "未监听";

    public void Start()
    {
        if (_running)
        {
            return;
        }

        var (host, port) = ParseListenUrl(Config.Settings.OneBot.GetListenUrl());
        try
        {
            _listener = new TcpListener(ResolveAddress(host), port);
            _listener.Start();
        }
        catch (Exception e)
        {
            TShock.Log.ConsoleError($"[CaiBotLite]监听 {host}:{port} 失败: {e.Message}");
            TShock.Log.ConsoleError(
                "[CaiBotLite]请确认该端口没有被别的程序占用, 云主机安全组/防火墙也已经放行。");
            _listener = null;
            return;
        }

        _url = $"{host}:{port}";
        _running = true;
        TShock.Log.ConsoleInfo(
            $"[CaiBotLite]OneBot 桥接服务已启动: http://{_url}/  (路由 {EventRoute}, {WebSocketRoute}, {FileRoute}{{token}}/{{name}})");
        _ = Task.Run(AcceptLoopAsync);
    }

    public void Stop()
    {
        if (!_running)
        {
            return;
        }

        _running = false;
        try
        {
            _listener?.Stop();
        }
        catch
        {
            // 忽略
        }

        _listener = null;
    }

    public void Dispose()
    {
        Stop();
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// 解析监听地址, 只取主机和端口(路径部分用于反向代理前缀, 路由按后缀匹配所以不用管)
    /// </summary>
    private static (string Host, int Port) ParseListenUrl(string url)
    {
        try
        {
            if (!url.Contains("://"))
            {
                url = "http://" + url;
            }

            var uri = new Uri(url);
            var port = uri.Port > 0 ? uri.Port : 17779;
            return (uri.Host, port);
        }
        catch
        {
            return ("0.0.0.0", 17779);
        }
    }

    /// <summary>
    /// 通配写法一律绑所有网卡, 具体 IP 就只绑那个 IP
    /// </summary>
    private static IPAddress ResolveAddress(string host)
    {
        if (string.IsNullOrWhiteSpace(host) ||
            host is "0.0.0.0" or "*" or "+" or "::" or "[::]" or "any")
        {
            return IPAddress.Any;
        }

        host = host.Trim('[', ']');
        if (IPAddress.TryParse(host, out var address))
        {
            return address;
        }

        try
        {
            var resolved = Dns.GetHostAddresses(host).FirstOrDefault();
            if (resolved != null)
            {
                return resolved;
            }
        }
        catch
        {
            // 解析不了就退回所有网卡
        }

        return IPAddress.Any;
    }

    private async Task AcceptLoopAsync()
    {
        var listener = _listener;
        while (_running && listener != null)
        {
            TcpClient client;
            try
            {
                client = await listener.AcceptTcpClientAsync();
            }
            catch (Exception)
            {
                return;
            }

            _ = Task.Run(() => ServeAsync(client));
        }
    }

    private async Task ServeAsync(TcpClient client)
    {
        NetworkStream stream;
        try
        {
            client.NoDelay = true;
            stream = client.GetStream();
        }
        catch
        {
            client.Dispose();
            return;
        }

        var response = new HttpResponse(stream);
        try
        {
            var request = await HttpRequest.ReadAsync(stream, client.Client.RemoteEndPoint?.ToString() ?? "?");
            if (request == null)
            {
                return;
            }

            if (request.IsWebSocketUpgrade && EndsWith(request.Path, WebSocketRoute))
            {
                // 交给 BotConnectionManager 之后这个 socket 就归它管了, 这里不能再关
                var handedOver = await HandshakeWebSocketAsync(stream, request);
                if (handedOver)
                {
                    response.HandedOver = true;
                }

                return;
            }

            await HandleAsync(request, response);
        }
        catch (Exception e)
        {
            TShock.Log.ConsoleError($"[CaiBotLite]处理桥接请求时出错: {e.Message}");
            if (!response.Started)
            {
                await response.WriteJsonAsync(500,
                    new { status = "failed", retcode = 100, message = e.Message });
            }
        }
        finally
        {
            // socket 已经交给 WebSocket 处理器的话, 生命周期就不归我们了
            if (!response.HandedOver)
            {
                try
                {
                    stream.Dispose();
                }
                catch
                {
                    // 忽略
                }

                client.Dispose();
            }
        }
    }

    private async Task HandleAsync(HttpRequest request, HttpResponse response)
    {
        var path = request.Path;

        if (request.Method == "OPTIONS")
        {
            await response.WriteJsonAsync(204, new { });
            return;
        }

        // 按后缀匹配, 前面挂了反向代理 / 带了路径前缀也能命中。
        // 文件路由是 /files/{token}/{name}, 前缀在中间, 所以用 IndexOf 而不是 EndsWith
        var fileIndex = path.IndexOf(FileRoute, StringComparison.OrdinalIgnoreCase);
        if (fileIndex >= 0)
        {
            await ServeFileAsync(request, path, fileIndex, response);
            return;
        }

        if (EndsWith(path, EventRoute))
        {
            await ReceiveAsync(request, response);
            return;
        }

        if (EndsWith(path, WebSocketRoute))
        {
            await response.WriteJsonAsync(400, new { status = "failed", message = "不是 WebSocket 请求" });
            return;
        }

        if (EndsWith(path, "/health"))
        {
            await response.WriteJsonAsync(200, new
            {
                status = "ok",
                plugin = "CaiBotLite",
                channel = Config.Settings.OneBot.GetChannel(),
                mode = Config.Settings.OneBot.IsEnabled ? "onebot" : "caibot",
                routes = new[] { "POST /onebot/v11/event", "GET /onebot/v11/ws", "GET /files/{token}/{name}" }
            });
            return;
        }

        // 未识别的请求多半是探活/扫描/代理, 把来源记下来方便排查
        TShock.Log.ConsoleInfo(
            $"[CaiBotLite]忽略了 {request.Method} {path}(来源 {request.RemoteEndPoint}, " +
            $"UA: {request.Headers.GetValueOrDefault("User-Agent") ?? "无"})");
        await response.WriteJsonAsync(404, new
        {
            status = "failed",
            message = "这不是 CaiBotLite 桥接接口",
            routes = new[]
            {
                "POST /onebot/v11/event", "GET /onebot/v11/ws", "GET /files/{token}/{name}", "GET /health"
            }
        });
    }

    private async Task ReceiveAsync(HttpRequest request, HttpResponse response)
    {
        if (request.Method != "POST")
        {
            await response.WriteJsonAsync(405, new { status = "failed", retcode = 100, message = "请使用 POST" });
            return;
        }

        var body = request.Body;
        var envelope = OneBotProtocol.TryParse(body);
        if (envelope == null || string.IsNullOrWhiteSpace(envelope.Package))
        {
            await response.WriteJsonAsync(400, new { status = "failed", retcode = 100, message = "数据包格式错误" });
            return;
        }

        if (!OneBotProtocol.CheckSecret(Config.Settings.OneBot.Secret, envelope.Secret ?? request.GetToken()))
        {
            TShock.Log.ConsoleError("[CaiBotLite]桥接数据密钥不匹配, 已丢弃!");
            await response.WriteJsonAsync(401, new { status = "failed", retcode = 1401, message = "密钥错误" });
            return;
        }

        if (CaiBotLite.DebugMode)
        {
            TShock.Log.ConsoleInfo($"[CaiBotLite]收到BOT数据包: {envelope.Package}");
        }

        OnPackage?.Invoke(envelope.Package);
        await response.WriteJsonAsync(200, new { status = "ok", retcode = 0, data = (object?)null });
    }

    /// <returns>true 表示 socket 已经交给 <see cref="OnWebSocket" /> 的处理方, 调用方不要再关它</returns>
    private async Task<bool> HandshakeWebSocketAsync(NetworkStream stream, HttpRequest request)
    {
        if (!OneBotProtocol.CheckSecret(Config.Settings.OneBot.Secret, request.GetToken()))
        {
            TShock.Log.ConsoleError("[CaiBotLite]有个机器人想接入但密钥不对, 已拒绝");
            var denied = new HttpResponse(stream);
            await denied.WriteJsonAsync(401, new { status = "failed", retcode = 1401, message = "密钥错误" });
            return false;
        }

        var key = request.Headers.GetValueOrDefault("Sec-WebSocket-Key");
        if (string.IsNullOrEmpty(key))
        {
            var bad = new HttpResponse(stream);
            await bad.WriteJsonAsync(400, new { status = "failed", message = "缺少 Sec-WebSocket-Key" });
            return false;
        }

        var accept = Convert.ToBase64String(SHA1.HashData(Encoding.ASCII.GetBytes(key + WsGuid)));
        var head = Encoding.ASCII.GetBytes(
            "HTTP/1.1 101 Switching Protocols\r\n" +
            "Upgrade: websocket\r\n" +
            "Connection: Upgrade\r\n" +
            $"Sec-WebSocket-Accept: {accept}\r\n\r\n");
        await stream.WriteAsync(head);
        await stream.FlushAsync();

        // 交出去之后不能再设读取超时, 否则长连接会被打断
        stream.ReadTimeout = Timeout.Infinite;
        stream.WriteTimeout = Timeout.Infinite;

        TShock.Log.ConsoleInfo(
            $"[CaiBotLite]机器人已接入桥接服务({request.RemoteEndPoint}) 实例={CaiBotLite.InstanceId}");

        // 生命周期交给 BotConnectionManager(它在断开时会 Dispose), 这里千万别再 using
        var webSocket = WebSocket.CreateFromStream(stream, isServer: true, subProtocol: null,
            keepAliveInterval: TimeSpan.FromSeconds(30));
        OnWebSocket?.Invoke(webSocket);
        return true;
    }

    private static async Task ServeFileAsync(HttpRequest request, string path, int fileIndex, HttpResponse response)
    {
        // /files/{token}/{name}, 允许前面带路径前缀
        if (request.Method != "GET" && request.Method != "HEAD")
        {
            await response.WriteJsonAsync(405, new { status = "failed", message = "请使用 GET" });
            return;
        }

        var rest = path[(fileIndex + FileRoute.Length)..].Split('/', 2);
        if (rest.Length != 2 || !OneBotFileHost.TryResolve(rest[0], Uri.UnescapeDataString(rest[1]), out var filePath))
        {
            await response.WriteJsonAsync(404, new { status = "failed", message = "文件不存在" });
            return;
        }

        var fileInfo = new FileInfo(filePath);
        await response.SendFileAsync(fileInfo, GetContentType(fileInfo.Extension), request.Method == "HEAD");
    }

    private static bool EndsWith(string path, string suffix)
    {
        return path.EndsWith(suffix, StringComparison.OrdinalIgnoreCase);
    }

    private static string GetContentType(string extension)
    {
        return extension.ToLowerInvariant() switch
        {
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".wld" => "application/octet-stream",
            ".map" => "application/octet-stream",
            ".zip" => "application/zip",
            _ => "application/octet-stream"
        };
    }

    #region 迷你 HTTP

    /// <summary>
    /// 只实现桥接需要的部分: 请求行 + 头 + Content-Length 请求体
    /// </summary>
    private sealed class HttpRequest
    {
        private readonly Dictionary<string, string> _query;

        public string Method { get; private set; } = "GET";
        public string Path { get; private set; } = "/";
        public string Body { get; private set; } = "";
        public string RemoteEndPoint { get; set; } = "?";
        public Dictionary<string, string> Headers { get; } = new(StringComparer.OrdinalIgnoreCase);

        public bool IsWebSocketUpgrade =>
            Headers.TryGetValue("Upgrade", out var upgrade) &&
            upgrade.Contains("websocket", StringComparison.OrdinalIgnoreCase);

        private HttpRequest(Dictionary<string, string> query)
        {
            _query = query;
        }

        public string? GetToken()
        {
            var authorization = Headers.GetValueOrDefault("Authorization");
            if (!string.IsNullOrEmpty(authorization) &&
                authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            {
                return authorization[7..].Trim();
            }

            return _query.GetValueOrDefault("secret");
        }

        public static async Task<HttpRequest?> ReadAsync(NetworkStream stream, string remoteEndPoint = "?")
        {
            var buffer = new byte[8192];
            var head = new MemoryStream();
            var matched = 0;
            var deadline = DateTime.UtcNow + ReadTimeout;

            // 读到 \r\n\r\n 为止
            while (DateTime.UtcNow < deadline)
            {
                var read = await stream.ReadAsync(buffer);
                if (read <= 0)
                {
                    return null;
                }

                head.Write(buffer, 0, read);
                if (head.Length > MaxHeaderSize)
                {
                    return null;
                }

                matched = FindHeaderEnd(head.GetBuffer(), (int)head.Length);
                if (matched > 0)
                {
                    break;
                }
            }

            if (matched <= 0)
            {
                return null;
            }

            var text = Encoding.UTF8.GetString(head.GetBuffer(), 0, matched);
            var lines = text.Split("\r\n", StringSplitOptions.None);
            var parts = lines[0].Split(' ');
            if (parts.Length < 2)
            {
                return null;
            }

            var query = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var target = parts[1];
            var mark = target.IndexOf('?');
            if (mark >= 0)
            {
                foreach (var pair in target[(mark + 1)..].Split('&', StringSplitOptions.RemoveEmptyEntries))
                {
                    var eq = pair.IndexOf('=');
                    if (eq > 0)
                    {
                        query[Uri.UnescapeDataString(pair[..eq])] = Uri.UnescapeDataString(pair[(eq + 1)..]);
                    }
                }
            }

            var request = new HttpRequest(query)
            {
                Method = parts[0].ToUpperInvariant(),
                Path = mark >= 0 ? target[..mark] : target,
                RemoteEndPoint = remoteEndPoint,
            };

            for (var i = 1; i < lines.Length; i++)
            {
                var colon = lines[i].IndexOf(':');
                if (colon <= 0)
                {
                    continue;
                }

                request.Headers[lines[i][..colon].Trim()] = lines[i][(colon + 1)..].Trim();
            }

            // 请求体
            var consumed = matched;
            var length = 0;
            if (int.TryParse(request.Headers.GetValueOrDefault("Content-Length"), out var parsed) && parsed > 0)
            {
                if (parsed > MaxBodySize)
                {
                    return null;
                }

                length = parsed;
            }

            var available = (int)head.Length - consumed;
            var body = new byte[Math.Max(length, available)];
            var have = Math.Min(available, length);
            Array.Copy(head.GetBuffer(), consumed, body, 0, have);

            while (have < length)
            {
                var read = await stream.ReadAsync(buffer.AsMemory(0, Math.Min(buffer.Length, length - have)));
                if (read <= 0)
                {
                    break;
                }

                Array.Copy(buffer, 0, body, have, read);
                have += read;
            }

            request.Body = Encoding.UTF8.GetString(body, 0, have);
            return request;
        }

        private static int FindHeaderEnd(byte[] data, int length)
        {
            for (var i = 3; i < length; i++)
            {
                if (data[i - 3] == '\r' && data[i - 2] == '\n' && data[i - 1] == '\r' && data[i] == '\n')
                {
                    return i + 1;
                }
            }

            return 0;
        }
    }

    /// <summary>
    /// 只支持 Content-Length 的响应(不 chunked), 桥接的数据量够用了
    /// </summary>
    private sealed class HttpResponse
    {
        private static readonly Dictionary<int, string> Reasons = new()
        {
            [200] = "OK",
            [204] = "No Content",
            [400] = "Bad Request",
            [401] = "Unauthorized",
            [404] = "Not Found",
            [405] = "Method Not Allowed",
            [500] = "Internal Server Error"
        };

        private readonly NetworkStream _stream;
        private readonly List<KeyValuePair<string, string>> _headers = new();

        public bool Started { get; private set; }

        /// <summary>连接是否已经交给 WebSocket 处理器, 为 true 时不能再关底层流</summary>
        public bool HandedOver { get; set; }

        public HttpResponse(NetworkStream stream)
        {
            _stream = stream;
        }

        public async Task WriteJsonAsync(int statusCode, object body)
        {
            var bytes = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(body));
            _headers.Add(new("Content-Type", "application/json; charset=utf-8"));
            await StartAsync(statusCode, bytes.Length);
            await _stream.WriteAsync(bytes);
            await _stream.FlushAsync();
        }

        public async Task SendFileAsync(FileInfo info, string contentType, bool headOnly)
        {
            _headers.Add(new("Content-Type", contentType));
            _headers.Add(new("Content-Disposition",
                $"attachment; filename*=UTF-8''{Uri.EscapeDataString(info.Name)}"));
            _headers.Add(new("Cache-Control", "no-store"));
            await StartAsync(200, info.Length);

            if (headOnly)
            {
                await _stream.FlushAsync();
                return;
            }

            await using var fs = new FileStream(info.FullName, FileMode.Open, FileAccess.Read, FileShare.Read,
                64 * 1024, true);
            await fs.CopyToAsync(_stream);
            await _stream.FlushAsync();
        }

        private async Task StartAsync(int statusCode, long contentLength)
        {
            if (Started)
            {
                return;
            }

            Started = true;
            var reason = Reasons.TryGetValue(statusCode, out var text) ? text : "OK";
            var head = new StringBuilder();
            head.Append($"HTTP/1.1 {statusCode} {reason}\r\n");
            head.Append($"Content-Length: {contentLength}\r\n");
            head.Append("Connection: close\r\n");
            foreach (var header in _headers)
            {
                head.Append($"{header.Key}: {header.Value}\r\n");
            }

            head.Append("\r\n");
            await _stream.WriteAsync(Encoding.UTF8.GetBytes(head.ToString()));
            await _stream.FlushAsync();
        }
    }

    #endregion
}

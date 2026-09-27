namespace CaiBotLite.OneBot;

/// <summary>
/// 机器人连接, 屏蔽 CaiBot 官方机器人与 OneBot v11 机器人的差异
/// </summary>
public interface IBotConnection : IDisposable
{
    /// <summary>
    /// 连接名称
    /// </summary>
    string Name { get; }

    /// <summary>
    /// 是否处于可用状态
    /// </summary>
    bool IsConnected { get; }

    /// <summary>
    /// 当前状态描述, 用于 /cbl info
    /// </summary>
    string Status { get; }

    /// <summary>
    /// 启动连接(内部自行重连)
    /// </summary>
    void Start();

    /// <summary>
    /// 停止连接
    /// </summary>
    void Stop();

    /// <summary>
    /// 发送一条 CaiBotLite 数据包(JSON 字符串), 该方法不会阻塞调用线程
    /// </summary>
    void Send(string package);
}

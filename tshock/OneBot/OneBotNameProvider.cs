using System.Collections.Concurrent;
using Terraria;

namespace CaiBotLite.OneBot;

/// <summary>
/// 物品 / 增益名称提供者
/// <para>
/// 官方机器人服务端自带一份物品表, 自建机器人一般没有, 这里直接把服务器上的名称一起发过去.
/// </para>
/// </summary>
public static class OneBotNameProvider
{
    private static readonly ConcurrentDictionary<int, string> ItemCache = new ();
    private static readonly ConcurrentDictionary<int, string> BuffCache = new ();

    internal static string GetItemName(int netId)
    {
        if (netId <= 0)
        {
            return "";
        }

        return ItemCache.GetOrAdd(netId, id =>
        {
            try
            {
                return Lang.GetItemName(id).ToString() ?? "";
            }
            catch
            {
                return "";
            }
        });
    }

    internal static string GetBuffName(int buffId)
    {
        if (buffId <= 0)
        {
            return "";
        }

        return BuffCache.GetOrAdd(buffId, id =>
        {
            try
            {
                return Lang.GetBuffName(id).ToString() ?? "";
            }
            catch
            {
                return "";
            }
        });
    }

    /// <summary>
    /// 生成 净ID -> 名称 的字典, 键使用字符串以便直接塞进 JSON
    /// </summary>
    internal static Dictionary<string, string> BuildItemNames(IEnumerable<int> netIds)
    {
        var result = new Dictionary<string, string>();
        foreach (var netId in netIds)
        {
            var name = GetItemName(netId);
            if (string.IsNullOrEmpty(name))
            {
                continue;
            }

            result[netId.ToString()] = name;
        }

        return result;
    }

    internal static Dictionary<string, string> BuildBuffNames(IEnumerable<int> buffIds)
    {
        var result = new Dictionary<string, string>();
        foreach (var buffId in buffIds)
        {
            var name = GetBuffName(buffId);
            if (string.IsNullOrEmpty(name))
            {
                continue;
            }

            result[buffId.ToString()] = name;
        }

        return result;
    }
}

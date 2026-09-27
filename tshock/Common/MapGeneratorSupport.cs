using System.Collections;
using System.Reflection;
using System.Runtime.CompilerServices;
using TerrariaApi.Server;

namespace CaiBotLite.Common;

internal static class MapGeneratorSupport
{
    public static bool Support { get; set; }

    /// <summary>GenerateMap.MapGenerator 的类型。没装 GenerateMap 时是 null。</summary>
    private static Type? MapGenerator { get; set; }

    internal static void Init()
    {
        var pluginContainer = ServerApi.Plugins.FirstOrDefault(x => x.Plugin.Name == "GenerateMap");
        if (pluginContainer is not null)
        {
            Support = true;
        }

        // 走反射拿: 本项目不引用 GenerateMap, 免得没装这个插件的人
        // 也得把 GenerateMap.dll 拖进 ServerPlugins 目录
        MapGenerator = Type.GetType("GenerateMap.MapGenerator, GenerateMap");
        if (MapGenerator is null)
        {
            Support = false;
        }
    }

    private static object? Invoke(string method)
    {
        var target = MapGenerator
                     ?? throw new NotSupportedException("需要安装GenerateMap插件");

        var member = target.GetMethod(method, BindingFlags.Public | BindingFlags.Static)
                     ?? throw new NotSupportedException($"GenerateMap 版本不兼容, 找不到 {method}()");

        return member.Invoke(null, null);
    }

    internal static byte[] CreatMapImgBytes()
    {
        ThrowIfNotSupported();
        return Invoke("CreatMapImgBytes") as byte[]
               ?? throw new NotSupportedException("GenerateMap 没有返回图片数据");
    }

    internal static (byte[], string) CreateMapFile()
    {
        ThrowIfNotSupported();

        // 上游返回的是 (byte[] File, string Name) 值元组。值元组的成员是显式实现,
        // Item1 / Item2 拿不到, 但它一定实现 ITuple, 用它取第 n 个元素。
        if (Invoke("CreatMapFile") is not ITuple mapFile)
        {
            throw new NotSupportedException("GenerateMap 没有返回地图文件");
        }

        if (mapFile[0] is not byte[] bytes)
        {
            throw new NotSupportedException("GenerateMap 返回的地图是空的");
        }

        return (bytes, mapFile[1]?.ToString() ?? "");
    }

    private static void ThrowIfNotSupported()
    {
        if (!Support)
        {
            throw new NotSupportedException("需要安装GenerateMap插件");
        }
    }
}

using System.Collections;
using System.Reflection;
using TerrariaApi.Server;

namespace CaiBotLite.Common;

public static class ProgressControlSupport
{
    public static bool Support { get; private set; }

    /// <summary>ProgressControl.PControl 的类型。没装 ProgressControls 时是 null。</summary>
    private static Type? PControl { get; set; }

    public static void Init()
    {
        var pluginContainer = ServerApi.Plugins.FirstOrDefault(x => x.Plugin.Name == "ProgressControls");
        if (pluginContainer is not null)
        {
            Support = true;
        }

        // 走反射拿: 本项目不引用 ProgressControls, 免得没装这个插件的人
        // 也得把它拖进 ServerPlugins 目录
        PControl = Type.GetType("ProgressControl.PControl, ProgressControls");
        if (PControl is null)
        {
            Support = false;
        }
    }

    public static Dictionary<string, string> GetLockBosses()
    {
        if (!Support)
        {
            throw new NotSupportedException("没有安装ProgressControls插件!");
        }

        var type = PControl ?? throw new NotSupportedException("ProgressControls 版本不兼容");
        var config = type.GetField("config", BindingFlags.Public | BindingFlags.Static)?.GetValue(null)
                     ?? throw new NotSupportedException("ProgressControls 版本不兼容, 拿不到 PControl.config");

        var enable = (bool?)ReadMember(config, "OpenAutoControlProgressLock") ?? false;
        var initDate = (DateTime?)ReadMember(config, "StartServerDate") ?? DateTime.MinValue;
        var lockedBosses = ReadMember(config, "ProgressLockTimeForStartServerDate") as IDictionary
                           ?? new Dictionary<object, object>();

        var result = new Dictionary<string, string>();

        if (!enable)
        {
            return result;
        }
        
        var bossIdNameToIdentity = new Dictionary<string, string>
        {
            { "史莱姆王", "King Slime" },
            { "克苏鲁之眼", "Eye of Cthulhu" },
            { "世界吞噬者", "Eater of Worlds" },
            { "克苏鲁之脑", "Brain of Cthulhu" },
            { "蜂后", "Queen Bee" },
            { "巨鹿", "Deerclops" },
            { "骷髅王", "Skeletron" },
            { "血肉墙", "Wall of Flesh" },
            { "史莱姆皇后", "Queen Slime" },
            { "双子魔眼", "The Twins" },
            { "毁灭者", "The Destroyer" },
            { "机械骷髅王", "Skeletron Prime" },
            { "世纪之花", "Plantera" },
            { "石巨人", "Golem" },
            { "猪龙鱼公爵", "Duke Fishron" },
            { "光之女皇", "Empress of Light" },
            { "拜月教教徒", "Lunatic Cultist" },
            { "月亮领主", "Moon Lord" }
        };
        
        
        foreach (DictionaryEntry lockedBoss in lockedBosses)
        {
            if (lockedBoss.Key?.ToString() is not string key)
            {
                continue;
            }

            if (!bossIdNameToIdentity.TryGetValue(key, out var bossName))
            {
                continue;
            }

            var hours = lockedBoss.Value switch
            {
                double d => d,
                float f => f,
                int i => i,
                long l => l,
                _ => 0d
            };

            var unlockTime = initDate + TimeSpan.FromHours(hours);
            if (unlockTime <= DateTime.Now)
            {
                continue;
            }

            result[bossName] = TimeFormat(unlockTime);
        }

        return result;
    }

    /// <summary>反射读属性, 读不到就返回 null。</summary>
    private static object? ReadMember(object? instance, string name)
    {
        if (instance is null)
        {
            return null;
        }

        var type = instance.GetType();
        var member = (MemberInfo?)type.GetProperty(name, BindingFlags.Public | BindingFlags.Instance)
                     ?? type.GetField(name, BindingFlags.Public | BindingFlags.Instance);

        return member switch
        {
            PropertyInfo property => property.GetValue(instance),
            FieldInfo field => field.GetValue(instance),
            _ => null
        };
    }

    public static string TimeFormat(DateTime dateTime)
    {
        var today = DateTime.Today;
        var inputDateWithoutTime = dateTime.Date;

        var daysDifference = (inputDateWithoutTime - today).Days;

        if (daysDifference > 365)
        {
            return "已锁定";
        }

        // 获取本周的开始日期(周一)
        var startOfWeek = today.AddDays(-(int) today.DayOfWeek + (int) DayOfWeek.Monday);
        if (today.DayOfWeek == DayOfWeek.Sunday)
        {
            startOfWeek = today.AddDays(-6);
        }

        // 获取下周的开始日期
        var startOfNextWeek = startOfWeek.AddDays(7);

        switch (daysDifference)
        {
            // 今天/明天/后天
            case 0:
                return dateTime.ToString("今天HH:mm");
            case 1:
                return dateTime.ToString("明天HH:mm");
            case 2:
                return dateTime.ToString("后天HH:mm");
            // 昨天/前天
            case -1:
                return dateTime.ToString("昨天HH:mm");
            case -2:
                return dateTime.ToString("前天HH:mm");
            // 本周内(周一到周日)
            case >= 0 when inputDateWithoutTime < startOfNextWeek:
                return dateTime.ToString($"周{ConvertToChineseWeekDay(dateTime.DayOfWeek)}HH:mm");
        }

        // 下周内
        if (inputDateWithoutTime >= startOfNextWeek && inputDateWithoutTime < startOfNextWeek.AddDays(7))
        {
            return dateTime.ToString($"下周{ConvertToChineseWeekDay(dateTime.DayOfWeek)}HH:mm");
        }

        // 其他情况
        return dateTime.ToString("M月d日HH:mm");
    }

// 辅助方法：将DayOfWeek转换为中文
    private static string ConvertToChineseWeekDay(DayOfWeek dayOfWeek)
    {
        return dayOfWeek switch
        {
            DayOfWeek.Sunday => "日",
            DayOfWeek.Monday => "一",
            DayOfWeek.Tuesday => "二",
            DayOfWeek.Wednesday => "三",
            DayOfWeek.Thursday => "四",
            DayOfWeek.Friday => "五",
            DayOfWeek.Saturday => "六",
            _ => throw new ArgumentOutOfRangeException()
        };
    }
}
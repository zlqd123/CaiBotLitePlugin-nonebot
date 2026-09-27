using System.Reflection;
using System.Runtime.CompilerServices;
using CaiBotLite.Enums;
using TerrariaApi.Server;

namespace CaiBotLite.Common;

/// <summary>
/// Economics 系列插件(Economics.Core / .RPG / .Skill)的可选集成。
///
/// <para>
/// 全部走反射, 本项目**不**在编译期引用它们——没装 Economics 的人不必把它
/// 的一堆 DLL 拖进 ServerPlugins 目录, 装了的照常能用。
/// </para>
/// </summary>
public static class EconomicSupport
{
    public static bool GetCoinsSupport { get; private set; }
    public static bool GetLevelNameSupport { get; private set; }
    public static bool GetSkillSupport { get; private set; }

    private static Type? CurrencyService { get; set; }
    private static Type? SettingType { get; set; }
    private static Type? PlayerLevelManager { get; set; }
    private static Type? PlayerSkillManager { get; set; }

    public static void Init()
    {
        var pluginContainer = ServerApi.Plugins.FirstOrDefault(x => x.Plugin.Name == "Economics.Core");
        if (pluginContainer is not null)
        {
            GetCoinsSupport = true;
        }

        pluginContainer = ServerApi.Plugins.FirstOrDefault(x => x.Plugin.Name == "Economics.RPG");
        if (pluginContainer is not null)
        {
            GetLevelNameSupport = true;
        }

        pluginContainer = ServerApi.Plugins.FirstOrDefault(x => x.Plugin.Name == "Economics.Skill");
        if (pluginContainer is not null)
        {
            GetSkillSupport = true;
        }

        CurrencyService = Type.GetType("Economics.Core.Economics.CurrencyService, Economics.Core");
        SettingType = Type.GetType("Economics.Core.ConfigFiles.Setting, Economics.Core");
        PlayerLevelManager = Type.GetType("Economics.RPG.PlayerLevelManager, Economics.RPG");
        PlayerSkillManager = Type.GetType("Economics.Skill.DB.PlayerSKillManager, Economics.Skill");

        // 装了插件却没解析出类型 = 版本对不上, 当作不支持, 别等到用的时候才炸
        if (CurrencyService is null || SettingType is null)
        {
            GetCoinsSupport = false;
        }

        if (PlayerLevelManager is null)
        {
            GetLevelNameSupport = false;
        }

        if (PlayerSkillManager is null)
        {
            GetSkillSupport = false;
        }

        if (GetCoinsSupport)
        {
            Rank.RankTypeMappings.Add("货币", RankTypes.EconomicCoin);
        }
    }

    public static bool IsSupported(string feature)
    {
        return feature switch
        {
            nameof(GetCoins) => GetCoinsSupport,
            nameof(GetLevelName) => GetLevelNameSupport,
            nameof(GetSkill) => GetSkillSupport,
            nameof(GetCoinRank) => GetCoinsSupport,
            nameof(SupportCoins) => GetCoinsSupport,
            _ => false
        };
    }

    public static string GetCoins(string name)
    {
        ThrowIfNotSupported();
        return GetNewCoins(name);
    }

    /// <summary>服务器上配置了哪些货币。</summary>
    public static List<string> SupportCoins
    {
        get
        {
            ThrowIfNotSupported();
            return CurrencyNames()
                .Select(x => (string?)ReadMember(x, "Name") ?? "")
                .Where(x => x.Length > 0)
                .ToList();
        }
    }

    public static Rank GetCoinRank(string type)
    {
        ThrowIfNotSupported();
        return new Rank($"{type}排行", GetAllCurrencyRecords()
            .Where(c => (string?)ReadMember(c, "CurrencyType") == type)
            .Select(c => new
            {
                Name = (string?)ReadMember(c, "PlayerName") ?? "",
                // 余额 + 货币名, 跟原来的展示方式一致
                Number = ToDecimal(ReadMember(c, "Number")) + (string?)ReadMember(c, "CurrencyType")
            })
            .OrderByDescending(x => x.Number)
            .ToDictionary(x => x.Name, x => x.Number));
    }

    private static string GetNewCoins(string name)
    {
        return string.Join('\n', CurrencyNames().Select(x =>
        {
            var currency = (string?)ReadMember(x, "Name") ?? "";
            var service = CurrencyService
                          ?? throw new NotSupportedException("Economics.Core 版本不兼容");

            var balance = service.GetMethod("GetBalance", BindingFlags.Public | BindingFlags.Static)
                          ?.Invoke(null, new object?[] { name, currency });

            // 上游返回的是 Result<decimal>, 公开字段叫 IsSuccess / Value
            var isSuccess = (bool?)ReadMember(balance, "IsSuccess") ?? false;
            var value = ToDecimal(ReadMember(balance, "Value"));
            return $"{currency}x{(isSuccess ? value : 0m)}";
        }));
    }

    public static string GetLevelName(string name)
    {
        ThrowIfNotSupported();

        var manager = PlayerLevelManager
                      ?? throw new NotSupportedException("Economics.RPG 版本不兼容");

        var getLevel = manager.GetMethod("GetLevel", BindingFlags.Public | BindingFlags.Static)
                       ?? throw new NotSupportedException("Economics.RPG 版本不兼容, 找不到 GetLevel()");

        var levelName = (string?)ReadMember(getLevel.Invoke(null, new object?[] { name }), "Name") ?? "";
        return $"职业:{(string.IsNullOrEmpty(levelName) ? "无" : levelName)}";
    }

    public static string GetSkill(string name)
    {
        ThrowIfNotSupported();

        var manager = PlayerSkillManager
                      ?? throw new NotSupportedException("Economics.Skill 版本不兼容");

        var query = manager.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static)
                        ?.GetValue(null)
                    ?? throw new NotSupportedException("Economics.Skill 版本不兼容, 拿不到 PlayerSKillManager.Instance");

        if (query.GetType().GetMethod("QuerySkill", BindingFlags.Public | BindingFlags.Instance)
                ?.Invoke(query, new object?[] { name }) is not System.Collections.IEnumerable skills)
        {
            return "技能:无";
        }

        var names = new List<string>();
        foreach (var skill in skills)
        {
            // 每项是一个包装类, 真正要取里面的 Skill 属性
            names.Add((string?)ReadMember(ReadMember(skill, "Skill"), "Name") ?? "");
        }

        return names.Count == 0 || names.Any(string.IsNullOrEmpty)
            ? "技能:无"
            : $"技能:{string.Join(',', names)}";
    }

    /// <summary>Setting.Instance.Currencies, 一组带 Name 属性的配置对象。</summary>
    private static IEnumerable<object> CurrencyNames()
    {
        var setting = SettingType
                      ?.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static)
                      ?.GetValue(null)
                      ?? throw new NotSupportedException("Economics.Core 版本不兼容, 拿不到 Setting.Instance");

        if (SettingType.GetProperty("Currencies")?.GetValue(setting) is not System.Collections.IEnumerable currencies)
        {
            yield break;
        }

        foreach (var currency in currencies)
        {
            if (currency is not null)
            {
                yield return currency;
            }
        }
    }

    /// <summary>CurrencyService.GetAllCurrencyRecords()</summary>
    private static IEnumerable<object> GetAllCurrencyRecords()
    {
        var service = CurrencyService
                      ?? throw new NotSupportedException("Economics.Core 版本不兼容");

        if (service.GetMethod("GetAllCurrencyRecords", BindingFlags.Public | BindingFlags.Static)
                ?.Invoke(null, null) is not System.Collections.IEnumerable records)
        {
            yield break;
        }

        foreach (var record in records)
        {
            if (record is not null)
            {
                yield return record;
            }
        }
    }

    /// <summary>反射读属性, 读不到就返回 null(不抛, 免得一个字段改名整个功能挂掉)。</summary>
    private static object? ReadMember(object? instance, string name)
    {
        if (instance is null)
        {
            return null;
        }

        var type = instance.GetType();
        var member = type.GetProperty(name, BindingFlags.Public | BindingFlags.Instance)
                     ?? (MemberInfo?)type.GetField(name, BindingFlags.Public | BindingFlags.Instance);
        return member switch
        {
            PropertyInfo property => property.GetValue(instance),
            FieldInfo field => field.GetValue(instance),
            _ => null
        };
    }

    private static decimal ToDecimal(object? value)
    {
        return value switch
        {
            decimal d => d,
            int i => i,
            long l => l,
            double dbl => (decimal)dbl,
            _ => 0m
        };
    }

    private static void ThrowIfNotSupported([CallerMemberName] string memberName = "")
    {
        if (!IsSupported(memberName))
        {
            throw new NotSupportedException(memberName);
        }
    }
}

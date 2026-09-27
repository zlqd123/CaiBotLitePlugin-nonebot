using System.Reflection;
using Terraria.ID;
using TerrariaApi.Server;

namespace CaiBotLite.Common;

public static class BossLockSupport
{
    public static bool Support { get; private set; }

    public static void Init()
    {
        var pluginContainer = ServerApi.Plugins.FirstOrDefault(x => x.Plugin.Name == "BossLock");
        if (pluginContainer is not null)
        {
            Support = true;
        }
    }

    public static Dictionary<string, string> GetLockBosses()
    {
        if (!Support)
        {
            throw new NotSupportedException("没有安装BossLock插件!");
        }

        // BossLock.Database 走反射拿: 本项目不引用 BossLock, 免得没装这个插件
        // 的人也得把它拖进 ServerPlugins 目录
        var database = Type.GetType("BossLock.Database, BossLock")
                       ?? throw new NotSupportedException("BossLock 版本不兼容, 找不到 BossLock.Database");
        var getAllLocked = database.GetMethod("GetAllLocked", BindingFlags.Public | BindingFlags.Static)
                           ?? throw new NotSupportedException("BossLock 版本不兼容, 找不到 GetAllLocked()");

        if (getAllLocked.Invoke(null, null) is not System.Collections.IDictionary lockedBosses)
        {
            return new Dictionary<string, string>();
        }

        var result = new Dictionary<string, string>();

        var bossIdToName = new Dictionary<int, string>
        {
            { NPCID.KingSlime, "King Slime" },
            { NPCID.EyeofCthulhu, "Eye of Cthulhu" },
            { NPCID.EaterofWorldsHead, "Eater of Worlds" },
            { NPCID.BrainofCthulhu, "Brain of Cthulhu" },
            { NPCID.QueenBee, "Queen Bee" },
            { NPCID.Deerclops, "Deerclops" },
            { NPCID.SkeletronHand, "Skeletron" },
            { NPCID.WallofFlesh, "Wall of Flesh" },
            { NPCID.QueenSlimeBoss, "Queen Slime" },
            { NPCID.Retinazer, "The Twins" },
            { NPCID.Spazmatism, "The Twins" },
            { NPCID.TheDestroyer, "The Destroyer" },
            { NPCID.SkeletronPrime, "Skeletron Prime" },
            { NPCID.Plantera, "Plantera" },
            { NPCID.Golem, "Golem" },
            { NPCID.DukeFishron, "Duke Fishron" },
            { NPCID.HallowBoss, "Empress of Light" },
            { NPCID.CultistBoss, "Lunatic Cultist" },
            { NPCID.MoonLordCore, "Moon Lord" }
        };

        foreach (System.Collections.DictionaryEntry lockedBoss in lockedBosses)
        {
            if (lockedBoss.Key is not int bossId)
            {
                continue;
            }

            if (!bossIdToName.TryGetValue(bossId, out var bossName))
            {
                continue;
            }

            var reason = lockedBoss.Value?.ToString() ?? "";
            if (bossId is NPCID.Retinazer or NPCID.Spazmatism)
            {
                if (!result.ContainsKey(bossName))
                {
                    result[bossName] = reason;
                }
            }
            else
            {
                result[bossName] = reason;
            }
        }

        return result;
    }
}
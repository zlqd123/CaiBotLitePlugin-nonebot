"""TShock 端数据的中文名称对照表。

服务器直接发过来的是英文 key / snake_case 枚举名, 这里统一翻译成中文,
翻译不出来时原样显示, 保证不会出现 KeyError。
"""

from __future__ import annotations

from .models import to_snake_case

#: 进度条件(ProgressType 枚举)的中文名
PROGRESS_NAMES: dict[str, str] = {
    "EyeOfCthulhu": "克苏鲁之眼",
    "SlimeKing": "史莱姆王",
    "EvilBoss": "克苏鲁之脑/世界吞噬者",
    "Skeletron": "骷髅王",
    "QueenBee": "蜂王",
    "Deerclops": "独眼巨鹿",
    "WallOfFlesh": "血肉墙",
    "MechBossAny": "机械三王",
    "TheTwins": "双子魔眼",
    "TheDestroyer": "毁灭者",
    "SkeletronPrime": "机械骷髅王",
    "Plantera": "世纪之花",
    "Golem": "石巨人",
    "DukeFishron": "猪龙鱼公爵",
    "LunaticCultist": "拜月教徒",
    "Moonlord": "月亮领主",
    "EmpressOfLight": "光之女皇",
    "QieenSlime": "史莱姆皇后",
    "HalloweenTree": "哀木",
    "HalloweenKing": "南瓜王",
    "ChristmasTree": "长绿尖叫怪",
    "ChristmasIceQueen": "冰雪女皇",
    "ChristmasSantank": "圣诞坦克",
    "Martians": "火星人",
    "Clown": "小丑",
    "TowerSolar": "日耀柱",
    "TowerVortex": "星旋柱",
    "TowerNebula": "星云柱",
    "TowerStardust": "星尘柱",
    "Goblins": "哥布林入侵",
    "Pirates": "海盗入侵",
    "Frost": "霜月",
    "BloodMoon": "血月",
    "DrakMageT1": "旧日一(黑暗法师)",
    "OrgeT2": "旧日二(巨魔)",
    "BetsyT3": "旧日三(贝齐斯)",
    "Raining": "下雨",
    "DyaTime": "白天",
    "Night": "夜晚",
    "WindyDay": "大风天",
    "Halloween": "万圣节",
    "Party": "生日派对",
    "DrunkWorld": "醉酒世界",
    "tenthAnniversaryWorld": "十周年世界",
    "ForTheWorthy": "为世存善",
    "RemixWorld": "颠倒世界",
    "NotTheBeesWorld": "蜂蜜世界",
    "DontStarveWorld": "饥荒世界",
    "zenithWorld": "天顶世界",
    "NoTrapsWorld": "危机世界",
    "ShoppingZone_Forest": "森林",
    "ZoneJungle": "丛林",
    "ZoneDesert": "沙漠",
    "ZoneSnow": "雪原",
    "ZoneUnderworldHeight": "地狱高度",
    "ZoneBeach": "海洋",
    "ZoneHallow": "神圣",
    "ZoneGlowshroom": "蘑菇",
    "ZoneShimmer": "微光",
    "ZoneCorrupt": "腐化",
    "ZoneCrimson": "猩红",
    "ZoneDungeon": "地牢",
    "ZoneGraveyard": "墓地",
    "ZoneLihzhardTemple": "神庙",
    "ZoneHive": "蜂巢",
    "ZoneSandstorm": "沙尘暴",
    "ZoneSkyHeight": "天空",
    "ZoneRockLayerHeight": "岩层",
    "ZoneDirtLayerHeight": "土层",
    "Inferno": "地狱",
    "ZoneUndergroundDesert": "地下沙漠",
    "FullMoon": "满月",
    "WaningGibbous": "亏凸月",
    "ThirdQuarter": "下弦月",
    "WaningCrescen": "残月",
    "NewMoon": "新月",
    "WaxingCrescent": "娥眉月",
    "FirstQuarter": "上弦月",
    "WaxingGibbous": "盈凸月",
    "Unknown": "服务器专用",
}

#: snake_case(枚举名) -> 中文名
PROGRESS_BY_SNAKE: dict[str, str] = {to_snake_case(k): v for k, v in PROGRESS_NAMES.items()}

#: 进度数据包(Utils.GetProcessList)里的 key
BOSS_NAMES: dict[str, str] = {
    "King Slime": "史莱姆王",
    "Eye of Cthulhu": "克苏鲁之眼",
    "Eater of Worlds or Brain of Cthulhu": "克苏鲁之脑/世界吞噬者",
    "Eater of Worlds": "世界吞噬者",
    "Brain of Cthulhu": "克苏鲁之脑",
    "Queen Bee": "蜂王",
    "Deerclops": "独眼巨鹿",
    "Skeletron": "骷髅王",
    "Wall of Flesh": "血肉墙",
    "Queen Slime": "史莱姆皇后",
    "The Destroyer": "毁灭者",
    "The Twins": "双子魔眼",
    "Skeletron Prime": "机械骷髅王",
    "Plantera": "世纪之花",
    "Golem": "石巨人",
    "Duke Fishron": "猪龙鱼公爵",
    "Empress of Light": "光之女皇",
    "Lunatic Cultist": "拜月教徒",
    "Moon Lord": "月亮领主",
    "Tower Solar": "日耀柱",
    "Tower Nebula": "星云柱",
    "Tower Vortex": "星旋柱",
    "Tower Stardust": "星尘柱",
    "Pillars": "四柱",
    "Goblins": "哥布林入侵",
    "Pirates": "海盗入侵",
    "Frost": "霜月",
    "Frost Moon": "圣诞夜",
    "Pumpkin Moon": "万圣节",
    "Martians": "火星人",
    "DD2InvasionT1": "旧日一(黑暗法师)",
    "DD2InvasionT2": "旧日二(巨魔)",
    "DD2InvasionT3": "旧日三(贝齐斯)",
}

#: 排行榜类型
RANK_NAMES: dict[str, str] = {
    "boss": "BOSS 击杀",
    "死亡": "死亡",
    "在线": "在线时长",
    "钓鱼": "渔夫任务",
    "货币": "货币",
}

#: 白名单结果
WHITELIST_NAMES: dict[str, str] = {
    "accept": "通过",
    "need_login": "需要登录授权",
    "not_in_whitelist": "不在白名单",
    "in_group_blacklist": "被本群拉黑",
    "in_bot_blacklist": "被全局拉黑",
    "unknown": "未知",
}


def progress_name(value: str) -> str:
    """把 ``eye_of_cthulhu`` 之类的进度枚举翻译成中文。"""
    return PROGRESS_BY_SNAKE.get(value, value)


def boss_name(value: str) -> str:
    return BOSS_NAMES.get(value, value)


def rank_name(value: str) -> str:
    return RANK_NAMES.get(value, value)

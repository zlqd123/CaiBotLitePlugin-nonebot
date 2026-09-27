"""渲染器测试。

重点是键名大小写: TShock 把 C# 插件对象直接序列化了, 键名是大写的
Name/Author/Version, 按小写取会一个都取不到。
"""

from __future__ import annotations

import os
import sys

sys.path.insert(0, os.path.join(os.path.dirname(__file__), "..", "src"))

import nonebot  # noqa: E402

nonebot.init()

from nonebot_plugin_caibotlite.render import (  # noqa: E402
    render_conditions,
    render_player_list,
    render_plugins,
    render_progress,
    render_rank,
)

failures = 0


def check(name: str, condition: bool, detail: str = "") -> None:
    global failures
    if condition:
        print(f"  OK   {name}")
    else:
        failures += 1
        print(f"  FAIL {name} {detail}")


print("-- 插件列表(真实 TShock 是 PascalCase) --")
real = {
    "is_mod": False,
    "plugins": [
        {"Name": "LazyAPI", "Author": "cc004", "Description": "None", "Version": "1.0.3.0"},
        {"Name": "Chameleon", "Author": "mistzzt", "Description": "账户系统", "Version": "1.1.2"},
    ],
}
text = render_plugins(real).text
check("PascalCase 能渲染出插件名", "LazyAPI" in text and "Chameleon" in text, text)
check("PascalCase 能渲染出版本", "1.0.3.0" in text, text)
check("数量正确", "2 个" in text, text)
check("作者为 None 时不显示", "作者: None" not in text, text)

lower = {"plugins": [{"name": "A", "version": "1.0", "author": "B"}]}
check("小写也能渲染(兼容)", "A" in render_plugins(lower).text)

check("空列表给出提示", "没有安装任何插件" in render_plugins({"plugins": []}).text)
check("缺字段也不炸", "未命名" in render_plugins({"plugins": [{"Version": "1"}]}).text)

print("-- 在线列表 --")
text = render_player_list(
    {"server_name": "ali88", "player_list": ["张三", "李四"], "current_online": 2, "max_online": 8}
).text
check("显示世界名", "ali88" in text, text)
check("显示在线人数", "2/8" in text, text)
check("显示玩家", "张三" in text and "李四" in text, text)
check("空服提示", "暂无玩家在线" in render_player_list({"player_list": []}).text)

print("-- 世界进度 --")
text = render_progress(
    {
        "world_name": "ali88",
        "drunk_world": True,
        "process": {"King Slime": 1, "Eye of Cthulhu": 1},
        "kill_counts": {"King Slime": 3},
        "boss_lock": {"Plantera": 1},
    }
).text
check("显示世界名", "ali88" in text, text)
check("显示醉酒", "醉酒" in text, text)
check("显示已击败 BOSS", "史莱姆王" in text and "克苏鲁之眼" in text, text)
check("显示击杀次数", "史莱姆王×3" in text, text)
check("显示进度锁", "世纪之花" in text, text)
check("空世界给兜底文案", "还没有任何进度记录" in render_progress({}).text)

print("-- 排行 --")
text = render_rank(
    {"rank": {"title": "死亡排行", "rank_lines": {"1": "小明 12 次", "2": "小红 8 次"}}}
).text
check("显示标题", "死亡排行" in text, text)
check("显示条目", "小明" in text, text)
check("空排行提示", "暂无数据" in render_rank({"rank": {"title": "死亡排行", "rank_lines": {}}}).text)

print("-- 商店条件 --")
text = render_conditions({"unmet_conditions": ["需要已击败 史莱姆王", "需要 100 硬币"]}).text
check("列出未达成条件", "史莱姆王" in text and "100 硬币" in text, text)
check("无未达成条件", "满足" in render_conditions({"unmet_conditions": []}).text or "无需" in render_conditions({"unmet_conditions": []}).text)

print()
print("全部通过" if failures == 0 else f"{failures} 项失败")
sys.exit(0 if failures == 0 else 1)

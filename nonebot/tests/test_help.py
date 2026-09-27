"""验证指令表(commands.json)的加载与权限分界。

规则: 帮助里出现的 = 所有人可用; 没出现的 = 只有机器人管理员可用, 群管理也不行。
"""

from __future__ import annotations

import json
import os
import sys
import tempfile
from pathlib import Path

sys.path.insert(0, os.path.join(os.path.dirname(__file__), "..", "src"))

import nonebot  # noqa: E402

nonebot.init()

from nonebot_plugin_caibotlite.commands import CommandTable  # noqa: E402
from nonebot_plugin_caibotlite.plugin import build_help  # noqa: E402

failures = 0


def check(name: str, condition: bool, detail: str = "") -> None:
    global failures
    if condition:
        print(f"  OK   {name}")
    else:
        failures += 1
        print(f"  FAIL {name} {detail}")


print("-- 载入随插件发布的指令表 --")
table = CommandTable.load()
check("读到了", not table.is_empty, "commands.json 没读到")
check("有指令", len(table.specs) >= 8, str(len(table.specs)))

print()
print("-- 公开指令(帮助里显示 = 所有人可用) --")
public = {s.name for s in table.public}
for name in ["帮助", "进度", "在线", "排行", "查背包", "插件列表", "执行"]:
    check(f"{name} 是公开的", name in public, str(sorted(public)))

print()
print("-- 隐藏指令(只有机器人管理员能用) --")
hidden = {s.name for s in table.specs if not s.is_public}
for name in ["上传地图", "下载地图", "下载存档"]:
    check(f"{name} 隐藏在帮助外", name in hidden, str(sorted(hidden)))

print()
print("-- /泰拉 执行 的默认开放名单 --")
ex = table.get("执行")
check("执行是公开的", ex.is_public)
check("默认开放 time/clear/wind/worldevent",
      ex.open_commands == ["time", "clear", "wind", "worldevent"], str(ex.open_commands))
check("time 在名单里", ex.is_open_subcommand(["time", "night"]))
check("clear 在名单里", ex.is_open_subcommand(["clear"]))
check("wind 在名单里", ex.is_open_subcommand(["wind", "5"]))
check("worldevent 在名单里", ex.is_open_subcommand(["worldevent", "rain"]))
check("give 不在名单里", not ex.is_open_subcommand(["give", "350", "999"]))
check("sudo 不在名单里", not ex.is_open_subcommand(["sudo", "bob", "x"]))
check("大小写不敏感", ex.is_open_subcommand(["TIME", "night"]))
check("其它公开指令没有这个限制", table.get("进度").is_open_subcommand([]))

print()
text = build_help("测试服", table)
print(text)
for name in hidden:
    check(f"帮助里没有 {name}", name not in text, text)
for name in ["进度", "在线", "排行", "查背包", "帮助", "执行"]:
    check(f"帮助里有 {name}", name in text, text)
check("帮助里列出了默认开放名单", "默认开放: time  clear  wind  worldevent" in text, text)
check("帮助里有详情行", "wiki" in text, text)

# 没拿到服务端名单时不该出现那一行
assert "服务器当前允许" not in text, text
print("  OK   没连上服务器时不显示服务端名单")

with_list = build_help("测试服", table, ["time", "clear", "wind", "worldevent", "motd"])
check("显示服务端真实名单", "服务器当前允许远程执行: time, clear, wind, worldevent, motd" in with_list,
      with_list)

print()
print("-- 别名与参数映射 --")
check("别名查进度 -> 进度", table.get("查进度") is table.get("进度"))
check("别名 tl 命令 -> 执行", table.get("cmd") is table.get("执行"))
rank = table.get("排行")
check("排行映射 boss排行 -> boss", rank.map_arg("boss排行") == "boss", rank.map_arg("boss排行"))
check("排行映射 金币 -> 货币", rank.map_arg("金币") == "货币", rank.map_arg("金币"))
check("排行需要参数", rank.need_arg)
check("进度不需要参数", not table.get("进度").need_arg)
check("查背包需要参数", table.get("查背包").need_arg)

print()
print("-- payload 拼装 --")
check("look_bag 带角色名", table.get("查背包").build_payload(["Steve"]) == {"player_name": "Steve"})
exec_spec = table.get("执行")
check(
    "call_command 拼命令",
    exec_spec.build_payload(["time", "night"])["command"] == "time night",
    str(exec_spec.build_payload(["time", "night"])),
)
check("call_command 带必读的空字段", exec_spec.build_payload(["time"])["user_open_id"] == "")
check("rank_data 拆两个参数", table.get("排行").build_payload(["死亡", "Steve"]) ==
      {"rank_type": "死亡", "arg": "Steve"})
check("progress 无 payload", table.get("进度").build_payload([]) == {})

print()
print("-- 自定义指令表(改 JSON 就能改行为) --")
with tempfile.TemporaryDirectory() as tmp:
    custom = Path(tmp) / "commands.json"
    custom.write_text(json.dumps({
        "_说明": "注释会被忽略",
        "你好": {"用法": "/泰拉 你好", "说明": "打个招呼", "权限": "user"},
        "偷偷": {"用法": "/泰拉 偷偷", "说明": "只有管理员", "权限": "botadmin",
                 "包": "progress"},
        "坏权限": {"权限": "瞎写的"},
    }, ensure_ascii=False), encoding="utf-8")

    t2 = CommandTable.load(custom)
    check("读到自定义表", len(t2.specs) == 3, str(len(t2.specs)))
    check("注释被忽略", t2.get("_说明") is None)
    check("你好是公开的", t2.get("你好").is_public)
    check("偷偷是隐藏的", not t2.get("偷偷").is_public)
    check("坏权限按公开处理", t2.get("坏权限").is_public, t2.get("坏权限").perm)
    help2 = build_help("x", t2)
    check("帮助只列公开的", "你好" in help2 and "偷偷" not in help2 and "坏权限" in help2, help2)

print()
print("-- 显式指定一个不存在的文件: 不该偷偷回退到默认表 --")
missing = CommandTable.load("/绝对/不存在的/commands.json")
check("空表是安全的", missing.is_empty, f"却读到了 {len(missing.specs)} 条")
check("空表不抛异常", build_help("x", missing).startswith("【x】"), build_help("x", missing))
check("空表时 get 全部返回 None", missing.get("进度") is None)

print()
print("全部通过" if failures == 0 else f"{failures} 项失败")
sys.exit(0 if failures == 0 else 1)

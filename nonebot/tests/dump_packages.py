"""导出实际会发出去的报文, 供 C# 端(prototest)解析校验。

白名单 / 商店 / 自踢 已下线, 这里不再导出对应的报文。
"""

from __future__ import annotations

import json
import os
import sys
from pathlib import Path

sys.path.insert(0, os.path.join(os.path.dirname(__file__), "..", "src"))

import nonebot  # noqa: E402

nonebot.init(driver="~none", log_level="ERROR")

from nonebot_plugin_caibotlite.models import ReplyTarget, build_package  # noqa: E402

target = ReplyTarget(self_id=1, group_id=123456, user_id=999, message_id=42)

packages = [
    build_package("player_list", target=target),
    build_package("progress", target=target),
    build_package("look_bag", {"player_name": "Steve"}, target=target),
    build_package("rank_data", {"rank_type": "死亡", "arg": ""}, target=target),
    build_package("rank_data", {"rank_type": "boss", "arg": "史莱姆王"}, target=target),
    build_package("plugin_list", target=target),
    build_package("map_image", target=target),
    build_package("map_file", target=target),
    build_package("world_file", target=target),
    build_package(
        "call_command",
        {"command": "time night", "user_open_id": "", "group_open_id": ""},
        target=target,
    ),
    build_package(
        "call_command",
        {"command": "worldevent rain", "user_open_id": "", "group_open_id": ""},
        target=target,
    ),
]

out = Path(__file__).resolve().parents[1] / "tests" / "samples"
out.mkdir(exist_ok=True)
(out / "packages.json").write_text(
    json.dumps(packages, ensure_ascii=False, indent=2), encoding="utf-8"
)
print(f"已导出 {len(packages)} 条报文 -> {out / 'packages.json'}")

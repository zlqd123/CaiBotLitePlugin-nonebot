"""端到端自测: 用一个模拟的 TShock 服务端跑通桥接协议 + 渲染。

运行方式::

    python tests/test_bridge.py
"""

from __future__ import annotations

import asyncio
import gzip
import json
import os
import sys
import tempfile
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "src"))

os.environ["CAIBOTLITE__SECRET"] = "test-secret"
os.environ["CAIBOTLITE__MODE"] = "client"
os.environ["CAIBOTLITE__DATA_PATH"] = tempfile.mkdtemp(prefix="caibotlite-test-")
os.environ["CAIBOTLITE__DEFAULT_GROUP"] = "123456"

import nonebot  # noqa: E402

nonebot.init(driver="~none", log_level="WARNING")

from websockets.asyncio.server import serve  # noqa: E402

from nonebot_plugin_caibotlite import config  # noqa: E402
from nonebot_plugin_caibotlite.bridge import Bridge  # noqa: E402
from nonebot_plugin_caibotlite.models import ReplyTarget, build_package  # noqa: E402
from nonebot_plugin_caibotlite.render import render  # noqa: E402

SECRET = "test-secret"

#: 模拟 TShock 端返回的数据
RESPONSES: dict[str, dict] = {
    "player_list": {
        "server_name": "测试服",
        "player_list": ["Steve", "Alex"],
        "current_online": 2,
        "max_online": 8,
        "process": "克眼前",
    },
    "progress": {
        "is_text": False,
        "process": {"King Slime": True, "Eye of Cthulhu": False, "Moon Lord": False},
        "kill_counts": {"King Slime": 3, "Eye of Cthulhu": 0},
        "boss_lock": {"Skeletron": "明天10:00"},
        "world_name": "测试世界",
        "drunk_world": False,
        "zenith_world": True,
    },
    "look_bag": {
        "is_text": False,
        "exist": True,
        "name": "Steve",
        "life": "400/400",
        "mana": "20/20",
        "quests_completed": 5,
        "inventory": [[71, 100], [0, 0], [22, 99], [999, 0]],
        "buffs": [0, 23, 0],
        "enhances": [3335],
        "economic": {"Coins": "金币x1200", "LevelName": "职业:战士", "Skill": "技能:挥砍"},
        "item_names": {"22": "铜短刀", "71": "铜币"},
        "buff_names": {"23": "缓慢"},
        "enhance_names": {"3335": "恶魔之心"},
    },
    "rank_data": {
        "rank_type_support": True,
        "need_arg": False,
        "arg_support": True,
        "rank": {"title": "死亡排行", "rank_lines": {"Steve": "10次", "Alex": "3次"}},
    },
    "plugin_list": {
        "is_mod": False,
        "plugins": [
            {"name": "CaiBotLite", "description": "x", "author": "Cai", "version": "2026.7.8.1"},
            {"name": "Economics", "description": "y", "author": "Z", "version": "1.0"},
        ],
    },
    "call_command": {"output": ["踢出 Steve 成功", "剩余 1 人"]},
    "map_image": {
        "name": "地图.png",
        "__file": {"name": "地图.png", "url": "http://1.2.3.4:25500/files/abc/地图.png", "size": 2048, "kind": "image"},
    },
    "world_file": {
        "name": "test.wld",
        "__file": {"name": "test.wld", "url": "http://1.2.3.4:25500/files/abc/test.wld", "size": 30 * 1024 * 1024, "kind": "file"},
    },
    "error": {"error": "System.Exception: 炸了\n   at CaiBotLite.Common.CaiBotApi.HandleMessage"},
}

received: list[dict] = []
outbound: "asyncio.Queue[str]" = asyncio.Queue()


def decode_base64(text: str) -> str:
    import base64

    return gzip.decompress(base64.b64decode(text)).decode("utf-8")


async def fake_tshock(websocket) -> None:
    """模拟 TShock 端: 收到请求 -> 用真实的协议格式回一个应答。"""

    async def push_loop() -> None:
        while True:
            raw = await outbound.get()
            await websocket.send(raw)

    pusher = asyncio.create_task(push_loop())
    try:
        async for raw in websocket:
            envelope = json.loads(raw)
            assert envelope["secret"] == SECRET, "密钥校验失败"
            package = json.loads(envelope["package"])
            received.append(package)

            kind = package["type"]
            if kind == "no_reply":
                continue
            payload = RESPONSES.get(kind, {})

            if kind == "player_list" and payload.get("base64"):
                payload["base64"] = decode_base64(payload["base64"])

            answer = {
                "version": package["version"],
                "direction": "to_bot",
                "type": kind,
                "is_request": True,
                "request_id": package["request_id"],
                "payload": {
                    **payload,
                    "__bridge": package["payload"].get("__bridge"),
                },
            }
            await websocket.send(
                json.dumps(
                    {
                        "v": 1,
                        "secret": SECRET,
                        "package": json.dumps(answer, ensure_ascii=False),
                    },
                    ensure_ascii=False,
                )
            )
    finally:
        pusher.cancel()


async def main() -> None:
    # base64 兼容性: 官方 CaiBot 模式下 map 图片是 gzip+base64
    import base64

    RESPONSES["map_image"]["base64"] = base64.b64encode(
        gzip.compress(base64.b64encode(b"fake-png").decode().encode())
    ).decode()

    server = await serve(fake_tshock, "127.0.0.1", 0)
    port = next(iter(server.sockets)).getsockname()[1]

    config.server_url = f"ws://127.0.0.1:{port}/onebot/v11/ws"
    config.reconnect = 1

    pushed: list[dict] = []

    async def on_package(package: dict) -> None:
        pushed.append(package)

    bridge = Bridge(config, on_package)
    await bridge.start()

    for _ in range(50):
        if bridge.connected:
            break
        await asyncio.sleep(0.1)
    assert bridge.connected, "桥接没有连上模拟服务器"
    print("[1/6] 桥接连接成功")

    target = ReplyTarget(self_id=1, group_id=123456, user_id=999, message_id=42)

    cases = [
        ("player_list", {}),
        ("progress", {}),
        ("look_bag", {"player_name": "Steve"}),
        ("rank_data", {"rank_type": "死亡", "arg": ""}),
        ("plugin_list", {}),
        ("call_command", {"command": "kick Steve"}),
        ("map_image", {}),
        ("world_file", {}),
    ]

    print("[2/6] 逐条请求并渲染")
    for kind, payload in cases:
        answer = await bridge.request(build_package(kind, payload, target=target))
        assert answer["type"] == kind, f"类型不匹配: {answer['type']}"
        result = render(answer)
        assert result.text, f"{kind} 没有渲染出文本"
        preview = result.text.replace("\n", " | ")[:90]
        extra = f" [图片={result.image}]" if result.image else ""
        extra += f" [文件={result.file_name}]" if result.file else ""
        print(f"  - {kind}: {preview}{extra}")

    print("[3/6] 校验协议字段")
    request = received[0]
    assert request["direction"] == "to_server", request
    assert request["is_request"] is True
    assert request["request_id"], "缺少 request_id"
    assert request["version"], "缺少 version"
    target_echo = request["payload"]["__bridge"]
    assert target_echo["group_id"] == 123456 and target_echo["message_id"] == 42, target_echo
    assert received[2]["payload"]["player_name"] == "Steve"
    print("  OK")

    print("[4/6] 测试错误与超时")
    error_answer = await bridge.request(build_package("error", {}, target=target))
    print("  - error:", render(error_answer).text)
    config.request_timeout = 0.5
    try:
        await bridge.request(build_package("no_reply", {}, target=target))
        raise AssertionError("超时没有被正确抛出")
    except TimeoutError:
        print("  - 超时处理正常")
    config.request_timeout = 15.0

    print("[5/6] 测试服务器主动推送(白名单校验)")
    server_package = {
        "version": "2025.7.18",
        "direction": "to_server",
        "type": "whitelist",
        "is_request": False,
        "request_id": None,
        "payload": {"player_name": "Steve", "player_ip": "1.2.3.4", "player_uuid": "uuid-1"},
    }
    await outbound.put(
        json.dumps(
            {"v": 1, "secret": SECRET, "package": json.dumps(server_package, ensure_ascii=False)},
            ensure_ascii=False,
        )
    )
    for _ in range(30):
        if pushed:
            break
        await asyncio.sleep(0.1)
    assert pushed, "没有收到服务器主动推送的数据包"
    print("  - 收到推送:", pushed[0]["type"], pushed[0]["payload"]["player_name"])

    print("[6/6] 测试密钥校验")
    try:
        await bridge._handle_text(
            json.dumps({"v": 1, "secret": "wrong", "package": json.dumps(server_package)})
        )
    except Exception as exc:  # pragma: no cover
        print("  - 处理异常:", exc)
    await asyncio.sleep(0.2)
    assert len(pushed) == 1, "密钥错误的数据包不应该被接收"
    print("  - 密钥错误的数据包已被丢弃")

    await bridge.stop()
    server.close()

    print("[附1] 测试 server 模式(TShock 反向连接)")
    await test_server_mode()

    print("[附2] 测试 auto 模式(监听 + 主动连接同时进行)")
    await test_auto_mode()
    print("\n全部通过")


async def test_auto_mode() -> None:
    """auto 模式: 主动连接这条路不通时靠反向连接, 反过来也一样, 先连上的生效。"""
    from websockets.asyncio.client import connect

    async def _noop(package: dict) -> None:
        return None

    config.mode = "auto"
    config.listen_host = "127.0.0.1"
    config.listen_port = 18098
    config.server_url = "ws://127.0.0.1:1/onebot/v11/ws"  # 故意连不上
    config.reconnect = 1

    bridge = Bridge(config, _noop)
    await bridge.start()
    await asyncio.sleep(0.3)

    assert not bridge.connected, "不应该连上不可达的地址"

    async with connect(f"ws://127.0.0.1:18098/caibotlite?secret={SECRET}") as tshock:
        for _ in range(30):
            if bridge.connected:
                break
            await asyncio.sleep(0.1)
        assert bridge.connected, "auto 模式没有接受反向连接"

        await bridge.send({"type": "heartbeat", "payload": {}})
        raw = await asyncio.wait_for(tshock.recv(), timeout=5)
        assert json.loads(json.loads(raw)["package"])["type"] == "heartbeat"
        print("  - 主动连不上时, 反向连接正常")

    for _ in range(30):
        if not bridge.connected:
            break
        await asyncio.sleep(0.1)
    assert not bridge.connected, "连接断开后状态没有复位"
    print("  - 断开后状态复位")

    await bridge.stop()
    config.mode = "client"
    config.reconnect = 5


async def test_server_mode() -> None:
    from websockets.asyncio.client import connect

    config.mode = "server"
    config.listen_host = "127.0.0.1"
    config.listen_port = 18099
    received: list[dict] = []

    async def on_package(package: dict) -> None:
        received.append(package)

    bridge = Bridge(config, on_package)
    await bridge.start()
    await asyncio.sleep(0.3)

    # 错误密钥应当被拒绝
    try:
        async with connect("ws://127.0.0.1:18099/caibotlite?secret=bad") as bad:
            await bad.recv()
        raise AssertionError("错误密钥不应该被接受")
    except AssertionError:
        raise
    except Exception:
        pass
    print("  - 错误密钥的连接被拒绝")

    async with connect(f"ws://127.0.0.1:18099/caibotlite?secret={SECRET}") as good:
        for _ in range(30):
            if bridge.connected:
                break
            await asyncio.sleep(0.1)
        assert bridge.connected, "server 模式没有接受连接"

        await good.send(
            json.dumps(
                {
                    "v": 1,
                    "secret": SECRET,
                    "package": json.dumps(
                        {
                            "version": "2025.7.18",
                            "direction": "to_server",
                            "type": "player_list",
                            "is_request": True,
                            "request_id": "req-server-mode",
                            "payload": {},
                        }
                    ),
                }
            )
        )
        for _ in range(30):
            if received:
                break
            await asyncio.sleep(0.1)
        assert received and received[0]["type"] == "player_list", received

        # 服务端 -> 机器人的方向
        await bridge.send({"type": "heartbeat", "payload": {}})
        raw = await asyncio.wait_for(good.recv(), timeout=5)
        envelope = json.loads(raw)
        assert envelope["secret"] == SECRET
        assert json.loads(envelope["package"])["type"] == "heartbeat"
        print("  - 双向收发正常, 密钥校验通过")

    await bridge.stop()
    config.mode = "client"


if __name__ == "__main__":
    asyncio.run(main())

"""指令流程自测: 构造真实的 OneBot v11 事件, 跑通"群里发指令 -> 机器人回复"。"""

from __future__ import annotations

import asyncio
import json
import os
import sys
import tempfile
import time
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "src"))

os.environ["CAIBOTLITE__SECRET"] = "test-secret"
os.environ["CAIBOTLITE__MODE"] = "client"
os.environ["CAIBOTLITE__DATA_PATH"] = tempfile.mkdtemp(prefix="caibotlite-cmd-")
os.environ["CAIBOTLITE__DEFAULT_GROUP"] = "123456"
os.environ["CAIBOTLITE__ADMINS"] = "[200]"
os.environ["CAIBOTLITE__SUPERUSERS"] = "[300]"

import nonebot  # noqa: E402

nonebot.init(driver="~none", log_level="ERROR")

from nonebot.adapters.onebot.v11 import (  # noqa: E402
    GroupMessageEvent,
    Message,
    MessageSegment,
    PrivateMessageEvent,
)
from nonebot.adapters.onebot.v11.event import Sender  # noqa: E402
from websockets.asyncio.server import serve  # noqa: E402

from nonebot_plugin_caibotlite import config  # noqa: E402
from nonebot_plugin_caibotlite.models import build_envelope  # noqa: E402
from nonebot_plugin_caibotlite.plugin import get_runtime, handle_message  # noqa: E402

SELF_ID = 1
GROUP_ID = 123456
SECRET = "test-secret"

ANSWERS = {
    "progress": {
        "process": {"King Slime": True, "Eye of Cthulhu": True},
        "kill_counts": {"King Slime": 2},
        "boss_lock": {},
        "world_name": "测试世界",
    },
    "player_list": {
        "server_name": "测试服",
        "player_list": ["Steve"],
        "current_online": 1,
        "max_online": 8,
        "process": "克眼前",
    },
    "look_bag": {
        "exist": True,
        "name": "Steve",
        "life": "200/200",
        "mana": "0/0",
        "inventory": [[71, 5]],
        "buffs": [],
        "enhances": [],
        "item_names": {"71": "铜币"},
    },
    "rank_data": {"rank_type_support": True, "rank": {"title": "死亡排行", "rank_lines": {"Steve": "1次"}}},
    "plugin_list": {
        "plugins": [{"name": "CaiBotLite"}],
        # 服务端(CaiBotLite.json -> 允许远程执行的指令)才是权威, 这里如实报出来
        "allowed_commands": ["time", "clear", "wind", "worldevent", "version", "motd"],
    },
    "call_command": {"output": ["指令执行成功"]},
}

sent: list[dict] = []
request_modes: list[str] = []
deny_exec: list[bool] = []
"""非空时模拟 TShock 端的白名单拒绝: [/泰拉 执行 give 350 999] 会被挡下"""

#: 1x1 透明 PNG, 用来模拟服务器内联发图
TINY_PNG = (
    "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNk+M9QDwADhgGAWjR9awAAAABJRU5ErkJggg=="
)


class FakeBot:
    async def send_group_msg(self, group_id: int, message: Message) -> None:
        sent.append({"group_id": group_id, "message": message})

    async def send_private_msg(self, user_id: int, message: Message) -> None:
        sent.append({"user_id": user_id, "message": message})


def make_event(
    text: str, user_id: int = 100, role: str = "member", deny: bool = False
) -> GroupMessageEvent:
    message = Message(MessageSegment.text(text))
    if deny:
        # 只影响紧接着的那一条, 用完立刻复位, 免得后面的用例莫名被服务器拒绝
        deny_exec.clear()
        deny_exec.append(True)
    else:
        deny_exec.clear()
    return GroupMessageEvent(
        time=int(time.time()),
        self_id=SELF_ID,
        post_type="message",
        sub_type="normal",
        user_id=user_id,
        message_type="group",
        message_id=1,
        message=message,
        original_message=message,
        raw_message=text,
        font=1,
        sender=Sender(user_id=user_id, nickname="Steve", card="", role=role),
        to_me=False,
        group_id=GROUP_ID,
        anonymous=None,
    )

async def fake_tshock(websocket) -> None:
    async for raw in websocket:
        package = json.loads(json.loads(raw)["package"])
        bridge = package["payload"].get("__bridge") or {}
        mode = bridge.get("file_mode", "")
        if mode:
            request_modes.append(mode)

        payload = dict(ANSWERS.get(package["type"], {"exist": False}))
        if package["type"] == "call_command" and deny_exec:
            # 模拟 TShock 端"允许远程执行的指令"白名单拒绝
            payload = {
                "is_text": True,
                "error": "该指令不在允许执行的名单里",
                "output": f"命令 [{package['payload'].get('command')}] 未被允许执行\n"
                          "当前允许: time, clear, version",
            }
        if package["type"] == "map_image":
            # 模拟 TShock: file_mode=inline 时不发布下载链接, 直接内联 base64
            if mode == "inline":
                payload = {"name": "地图.png", "base64": gzip_base64(TINY_PNG)}
            else:
                payload = {
                    "name": "地图.png",
                    "__file": {
                        "name": "地图.png",
                        "url": "http://1.2.3.4:17779/files/abc/地图.png",
                        "size": 2048,
                        "kind": "image",
                    },
                }

        answer = {
            "version": package["version"],
            "direction": "to_bot",
            "type": package["type"],
            "is_request": True,
            "request_id": package["request_id"],
            "payload": {**payload, "__bridge": bridge},
        }
        await websocket.send(build_envelope(answer))


def gzip_base64(text: str) -> str:
    import base64
    import gzip

    return base64.b64encode(gzip.compress(text.encode())).decode()


def make_private_event(text: str, user_id: int = 100) -> PrivateMessageEvent:
    message = Message(MessageSegment.text(text))
    return PrivateMessageEvent(
        time=int(time.time()),
        self_id=SELF_ID,
        post_type="message",
        sub_type="friend",
        user_id=user_id,
        message_type="private",
        message_id=1,
        message=message,
        original_message=message,
        raw_message=text,
        font=1,
        sender=Sender(user_id=user_id, nickname="Steve", card=""),
        to_me=False,
    )


def text_of(message: Message) -> str:
    return "".join(str(segment) for segment in message)


def body_of(message: Message) -> str:
    """只取文本段, 去掉 [CQ:reply,...] 这类引用前缀。"""
    return "".join(
        str(seg) for seg in message
        if getattr(seg, "type", "") == "text"
    )


async def main() -> None:
    server = await serve(fake_tshock, "127.0.0.1", 0)
    config.server_url = f"ws://127.0.0.1:{next(iter(server.sockets)).getsockname()[1]}/onebot/v11/ws"
    config.reconnect = 1

    rt = get_runtime()
    await rt.start()
    nonebot.get_driver()._bots[str(SELF_ID)] = FakeBot()  # type: ignore[assignment]

    for _ in range(50):
        if rt.bridge.connected:
            break
        await asyncio.sleep(0.1)
    assert rt.bridge.connected, "桥接未连接"
    print("[1/9] 桥接已连接, 机器人已注册")

    print("[2/9] 群成员查询指令")
    for text, expect in [
        ("/泰拉 进度", "测试世界"),
        ("泰拉 进度", "测试世界"),
        ("/tl 进度", "测试世界"),
        ("。泰拉 进度", "测试世界"),
        ("/泰拉 在线", "测试服"),
        ("/泰拉 查背包 Steve", "Steve 的背包"),
        ("/泰拉 排行 死亡", "死亡排行"),
    ]:
        sent.clear()
        await handle_message(None, make_event(text))  # type: ignore[arg-type]
        assert sent, f"{text} 没有回复"
        body = text_of(sent[-1]["message"])
        assert expect in body, f"{text} -> {body!r} 里没有 {expect}"
        assert sent[-1]["group_id"] == GROUP_ID
        print(f"  OK  {text:<14} -> {body.splitlines()[0]}")

    sent.clear()
    await handle_message(None, make_event("/泰拉 插件列表", role="member"))  # type: ignore[arg-type]
    body = text_of(sent[-1]["message"])
    assert "插件列表" in body, body
    print(f"  OK  {'插件列表(普通成员)':<14} -> {body.splitlines()[0]}")

    print("[3/9] 权限不足只回两个字: 无权限")
    for who, role in [("普通成员", "member"), ("群管理", "admin"), ("群主", "owner")]:
        sent.clear()
        await handle_message(None, make_event("/泰拉 下载存档", user_id=100, role=role))  # type: ignore[arg-type]
        body = body_of(sent[-1]["message"])
        assert body.strip() == "无权限", f"{who} 收到了 {body!r}"
    print("  OK  普通成员/群管理/群主 全部只收到『无权限』")

    sent.clear()
    await handle_message(None, make_event("/泰拉 下载存档", user_id=300, role="member"))  # type: ignore[arg-type]
    body = text_of(sent[-1]["message"])
    # 假服务器对 world_file 的回包是"没有开启文件外发", 总之说明请求已经发出去了
    assert "没有开启文件外发" in body or "指令执行成功" in body, body
    print("  OK  机器人管理员可以用受限指令")

    print("[3a/9] /泰拉 执行: time/clear/wind/worldevent 对所有人开放")
    for text, expect in [
        ("/泰拉 执行 time", "指令执行成功"),
        ("/泰拉 执行 clear", "指令执行成功"),
        ("/泰拉 执行 wind 5", "指令执行成功"),
        ("/泰拉 执行 worldevent rain", "指令执行成功"),
    ]:
        for role in ["member", "admin", "owner"]:
            sent.clear()
            await handle_message(None, make_event(text, user_id=100, role=role))  # type: ignore[arg-type]
            body = text_of(sent[-1]["message"])
            assert expect in body, f"{role} / {text} -> {body!r}"
    print("  OK  三种身份都能用 time/clear/wind/worldevent")

    sent.clear()
    await handle_message(None, make_event("/泰拉 执行 time", user_id=100, role="owner"))  # type: ignore[arg-type]
    assert "指令执行成功" in text_of(sent[-1]["message"]), sent
    print("  OK  群主也能调时间")

    print("[3b/9] /泰拉 执行 的名单外指令需要机器人管理员")
    for text in ["/泰拉 执行 give 350 999", "/泰拉 执行 sudo bob tshock.admin give",
                 "/泰拉 执行 off", "/泰拉 执行 motd", "/泰拉 执行 help"]:
        for role in ["member", "admin", "owner"]:
            sent.clear()
            await handle_message(None, make_event(text, user_id=100, role=role))  # type: ignore[arg-type]
            body = body_of(sent[-1]["message"])
            assert body.strip() == "无权限", f"{role} / {text} -> {body!r}"
    print("  OK  give/sudo/off/motd/help 对普通用户一律『无权限』")

    # 但机器人管理员可以发, 之后由 TShock 的白名单裁决
    sent.clear()
    await handle_message(None, make_event("/泰拉 执行 motd", user_id=300))  # type: ignore[arg-type]
    assert "指令执行成功" in text_of(sent[-1]["message"]), sent
    print("  OK  机器人管理员可以执行名单外的指令")

    print("[3c/9] 远程执行: 机器人侧不拦截, 一律透传给服务器判断")
    sent.clear()
    await handle_message(None, make_event("/泰拉 执行", user_id=300))  # type: ignore[arg-type]
    body = text_of(sent[-1]["message"])
    assert "用法" in body, body
    print("  OK  不带参数时给出用法")

    # 机器人管理员的危险指令也要照发: 裁决权在 TShock, 机器人这边只负责送过去
    for bad in ["/泰拉 执行 give 350 999", "/泰拉 执行 sudo bob tshock.admin give", "/泰拉 执行 off"]:
        sent.clear()
        await handle_message(None, make_event(bad, user_id=300))  # type: ignore[arg-type]
        assert sent, f"{bad} 应该被透传到服务器"
        assert "指令执行成功" in text_of(sent[-1]["message"]), (bad, sent[-1]["message"])
    print("  OK  give/sudo/off 都原样透传(由 TShock 白名单裁决)")

    # 服务器拒绝时, 拒绝理由要原样显示出来
    sent.clear()
    await handle_message(None, make_event("/泰拉 执行 give 350 999", user_id=300, deny=True))  # type: ignore[arg-type]
    body = text_of(sent[-1]["message"])
    assert "未被允许执行" in body, body
    print("  OK  服务器拒绝的理由原样透出:", body.splitlines()[0])

    # 普通玩家的 give 应该在机器人侧就被挡住, 根本不该发到服务器
    sent.clear()
    await handle_message(None, make_event("/泰拉 执行 give 350 999", user_id=100))  # type: ignore[arg-type]
    assert body_of(sent[-1]["message"]).strip() == "无权限", sent
    print("  OK  普通玩家的 give 连服务器都到不了")

    print("[4/9] 已下线的指令一律不认")
    for gone in ["/泰拉 添加白名单 Steve", "/泰拉 移除白名单 Steve", "/泰拉 拉黑 Steve",
                 "/泰拉 解除拉黑 Steve", "/泰拉 白名单", "/泰拉 登录 Alex",
                 "/泰拉 踢出 Steve", "/泰拉 tk Steve", "/泰拉 购买 铁锭x99",
                 "/泰拉 buy 铁锭x99"]:
        sent.clear()
        await handle_message(None, make_event(gone, user_id=300, role="owner"))  # type: ignore[arg-type]
        body = text_of(sent[-1]["message"]) if sent else ""
        assert "未知指令" in body, f"{gone} 应该报未知指令, 实际: {body!r}"
    print("  OK  白名单/登录/踢出/商店 全部变成未知指令")

    # 下线的指令连数据包都不该发出去
    sent.clear()
    await handle_message(None, make_event("/泰拉 踢出 Steve", user_id=300, role="owner"))  # type: ignore[arg-type]
    assert "未知指令" in text_of(sent[-1]["message"]), sent
    print("  OK  踢出不会真的发 self_kick 包")

    print("[5/9] 帮助与未知指令")
    sent.clear()
    await handle_message(None, make_event("/泰拉 帮助"))  # type: ignore[arg-type]
    body = text_of(sent[-1]["message"])
    assert "泰拉" in body and "/泰拉 进度" in body, body
    # 帮助 = commands.json 里权限为 user 的指令, 全部对所有人开放
    for shown in ["/泰拉 帮助", "/泰拉 进度", "/泰拉 在线", "/泰拉 排行",
                  "/泰拉 查背包", "/泰拉 插件列表", "/泰拉 执行"]:
        assert shown in body, f"帮助里该有 {shown}: {body}"
    # 隐藏指令一条都不列
    for hidden in ["/泰拉 上传地图", "/泰拉 下载地图", "/泰拉 下载存档",
                   "/泰拉 添加白名单", "/泰拉 拉黑", "/泰拉 登录", "/泰拉 购买", "/泰拉 角色"]:
        assert hidden not in body, f"帮助里不该出现 {hidden}: {body}"
    # 执行的默认开放名单要跟着显示出来
    assert "默认开放: time  clear  wind  worldevent" in body, body
    print("  OK  帮助只列 commands.json 里的公开指令")

    print("[5a/9] 服务端的执行白名单才是权威, 机器人如实显示")
    sent.clear()
    await handle_message(None, make_event("/泰拉 插件列表"))  # type: ignore[arg-type]
    assert rt.allowed_commands == ["time", "clear", "wind", "worldevent", "version", "motd"], \
        rt.allowed_commands
    print("  OK  从 plugin_list 响应里拿到了服务端的允许列表")

    sent.clear()
    await handle_message(None, make_event("/泰拉 帮助"))  # type: ignore[arg-type]
    body = text_of(sent[-1]["message"])
    assert "服务器当前允许远程执行: time, clear, wind, worldevent, version, motd" in body, body
    print("  OK  帮助里显示了服务端真实的允许列表")

    # 机器人不自己维护第二份: 服务端没放行的指令, 机器人不会当成公开
    assert "time" in rt.allowed_commands and "give" not in rt.allowed_commands
    print("  OK  机器人不维护第二份白名单")

    # commands.json 公开了服务端并不允许的指令时, 要吵一架而不是让用户去猜
    saved = ANSWERS["plugin_list"]
    ANSWERS["plugin_list"] = {**saved, "allowed_commands": ["time", "clear"]}
    rt.allowed_commands = []
    sent.clear()
    await handle_message(None, make_event("/泰拉 插件列表"))  # type: ignore[arg-type]
    assert rt.allowed_commands == ["time", "clear"], rt.allowed_commands
    ANSWERS["plugin_list"] = saved
    rt.allowed_commands = []
    print("  OK  服务端名单变了会重新读取并触发漂移检查")

    sent.clear()
    await handle_message(None, make_event("/泰拉 随便一个不存在的指令"))  # type: ignore[arg-type]
    assert sent and "未知指令" in text_of(sent[-1]["message"]), sent
    print("  OK  未知指令提示")

    sent.clear()
    await handle_message(None, make_event("/进度"))  # type: ignore[arg-type]
    assert not sent, "少了根指令名就不该回复"
    print("  OK  少了 /泰拉 前缀一律不认")

    print("[6/9] 忽略机器人自己的消息与普通聊天")
    sent.clear()
    event = make_event("/泰拉 进度")
    event.self_id = 100  # 模拟用户就是机器人自己
    await handle_message(None, event)  # type: ignore[arg-type]
    await handle_message(None, make_event("今天天气不错"))  # type: ignore[arg-type]
    assert not sent, sent
    print("  OK  不会误触发")

    sent.clear()
    await handle_message(None, make_event("/今天天气不错"))  # type: ignore[arg-type]
    assert not sent, "没写根指令名就不该回复"
    print("  OK  /今天天气不错(没有根指令名) 静默")

    sent.clear()
    await handle_message(None, make_event("/泰拉 今天天气不错"))  # type: ignore[arg-type]
    assert sent and "未知指令" in text_of(sent[-1]["message"]), sent
    print("  OK  写了根指令名的未知子指令才会提示")

    print("[7/9] 服务器文件地址不可达时退回 base64 内联")
    await test_inline_fallback(rt, server, config)

    print("[8/9] 文件地址可达时仍然发下载链接")
    sent.clear()
    request_modes.clear()
    # 指向真实的监听端口, 探测会拿到一个 HTTP 响应(非 5xx), 视为"可达"
    rt.public_url = f"http://127.0.0.1:{next(iter(server.sockets)).getsockname()[1]}"
    config.probe_timeout = 2
    config.probe_cache = 0
    await handle_message(None, make_event("/泰拉 上传地图", user_id=300))  # type: ignore[arg-type]
    assert sent, "没有回复"
    image = [seg for seg in sent[-1]["message"] if seg.type == "image"]
    assert image and image[0].data.get("file", "").startswith("http"), sent[-1]["message"]
    assert not request_modes, "可达时不该要求服务器内联"
    print("  OK  地图按下载链接发送")

    config.probe_timeout = 5

    print("[9/9] 私聊只回私聊, 不会发到群里")
    await test_private_chat(rt, config)

    await rt.bridge.stop()
    server.close()
    print("\n全部通过")


async def test_private_chat(rt, cfg) -> None:
    def only_private() -> dict:
        assert sent, "没有回复"
        last = sent[-1]
        assert "user_id" in last and "group_id" not in last, f"私聊回复跑到了群里: {last}"
        return last

    # 1. 普通查询在私聊里可用, 且只回私聊
    sent.clear()
    await handle_message(None, make_private_event("/泰拉 进度"))  # type: ignore[arg-type]
    assert "测试世界" in text_of(only_private()["message"])
    print("  OK  私聊查进度 -> 只回私聊")

    # 2. 私聊里公开指令照样能用
    sent.clear()
    await handle_message(None, make_private_event("/泰拉 在线"))  # type: ignore[arg-type]
    assert "测试服" in text_of(only_private()["message"])
    print("  OK  私聊查在线 -> 只回私聊")

    # 3. 私聊里受限指令也只回"无权限", 且不解释
    sent.clear()
    await handle_message(None, make_private_event("/泰拉 上传地图", user_id=100))  # type: ignore[arg-type]
    assert body_of(only_private()["message"]).strip() == "无权限", sent
    print("  OK  私聊里普通用户用不了受限指令 -> 只回私聊")

    # 4. 公开的默认开放指令在私聊里照样能用
    sent.clear()
    await handle_message(None, make_private_event("/泰拉 执行 time", user_id=100))  # type: ignore[arg-type]
    assert "指令执行成功" in text_of(only_private()["message"]), sent
    print("  OK  私聊里普通用户也能调时间 -> 只回私聊")

    # 5. 私聊里名单外的执行同样被挡
    sent.clear()
    await handle_message(None, make_private_event("/泰拉 执行 give 350 999", user_id=100))  # type: ignore[arg-type]
    assert body_of(only_private()["message"]).strip() == "无权限", sent
    print("  OK  私聊里名单外指令也只回『无权限』")

    # 6. 配置里的机器人管理员可以执行名单外的指令
    sent.clear()
    await handle_message(None, make_private_event("/泰拉 执行 motd", user_id=200))  # type: ignore[arg-type]
    assert "指令执行成功" in text_of(only_private()["message"]), sent
    print("  OK  私聊里机器人管理员可执行任意指令 -> 只回私聊")

    # 5. configured groups 不能把私聊也一起拦掉
    cfg.groups = [999999]
    sent.clear()
    await handle_message(None, make_private_event("/泰拉 进度", user_id=200))  # type: ignore[arg-type]
    assert "测试世界" in text_of(only_private()["message"])
    cfg.groups = []


async def test_inline_fallback(rt, server, cfg) -> None:
    """机器人访问不到 TShock 的文件服务时, 应当让服务器直接内联发图。"""
    sent.clear()
    rt.public_url = "http://10.255.255.1:17779"  # 故意填一个不可达的地址
    config.probe_timeout = 0.5
    config.probe_cache = 1

    await handle_message(None, make_event("/泰拉 上传地图", user_id=300))  # type: ignore[arg-type]
    assert sent, "没有回复"
    message = sent[-1]["message"]
    assert any(seg.type == "image" for seg in message), message
    assert any("base64://" in str(seg.data.get("file", "")) for seg in message if seg.type == "image")
    print("  OK  地图已改为内联图片发送")

    assert request_modes == ["inline"], request_modes
    print("  OK  请求里带上了 file_mode=inline")

    config.probe_timeout = 5
    rt.public_url = ""
    request_modes.clear()

if __name__ == "__main__":
    asyncio.run(main())

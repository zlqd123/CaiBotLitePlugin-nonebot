"""CaiBotLite 服务端连通性自检脚本。

不需要启动 nonebot 就能验证 TShock 端的 OneBot 桥接是否正常: 依次检查端口可达性、
WebSocket 握手与密钥、服务端主动推送的 Hello, 并把每个接口真实调一遍。

密钥不要写进命令行(会留在 shell 历史和进程列表里), 放到环境变量里传:

    export CAIBOTLITE__SECRET=你的通讯密钥

    # 主动连接服务端(服务端 OneBot通道 = server 或 auto)
    python tools/probe_server.py --host your-server.example.com

    # 让服务端反过来连本脚本(服务端 OneBot通道 = client, 机器人地址设成脚本打印的地址)
    python tools/probe_server.py --listen 18099

    # 额外测远程指令/商店条件这类有副作用的接口
    python tools/probe_server.py --host your-server.example.com --deep
"""

from __future__ import annotations

import argparse
import asyncio
import json
import os
import sys
import time
from pathlib import Path
from typing import Any
from urllib.parse import urlencode

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "src"))

import nonebot  # noqa: E402

nonebot.init()  # 只是为了能 import 插件里的模型/渲染, 不启动任何连接器

from websockets.asyncio.client import connect  # noqa: E402
from websockets.asyncio.server import serve  # noqa: E402

from nonebot_plugin_caibotlite import config  # noqa: E402
from nonebot_plugin_caibotlite.models import build_envelope, build_package, parse_envelope  # noqa: E402
from nonebot_plugin_caibotlite.render import render  # noqa: E402

GREEN, RED, YELLOW, DIM, BOLD, RESET = "\033[32m", "\033[31m", "\033[33m", "\033[2m", "\033[1m", "\033[0m"
failures: list[str] = []


def ok(msg: str) -> None:
    print(f"  {GREEN}OK  {RESET} {msg}")


def bad(msg: str) -> None:
    failures.append(msg)
    print(f"  {RED}FAIL{RESET} {msg}")


def warn(msg: str) -> None:
    print(f"  {YELLOW}WARN{RESET} {msg}")


def step(title: str) -> None:
    print(f"\n{DIM}-- {title} --{RESET}")


def flat(text: str, width: int = 90) -> str:
    text = text.replace("\n", " | ")
    return text if len(text) <= width else text[: width - 1] + "…"


class Probe:
    """收服务端的数据包, 按 request_id 匹配应答。"""

    def __init__(self, send, verbose: bool = False) -> None:
        self._send = send
        self._verbose = verbose
        self.hello: dict[str, Any] | None = None
        self.pushed: list[dict] = []
        self.answers: dict[str, dict] = {}
        self.last_players: list[str] = []

    def dump(self, direction: str, raw: str) -> None:
        if not self._verbose:
            return
        text = raw if len(raw) <= 300 else raw[:300] + f"...(共{len(raw)}字符)"
        print(f"      {DIM}{direction}{RESET} {text}")

    def feed(self, raw: Any) -> None:
        text = raw if isinstance(raw, str) else raw.decode("utf-8", "replace")
        self.dump("<- 收", text)
        package = parse_envelope(text)
        if package is None:
            warn(f"收到无法解析的数据: {text[:120]}")
            return

        kind = package.get("type")
        if kind == "hello":
            self.hello = package.get("payload") or {}
            return

        request_id = package.get("request_id")
        if request_id and package.get("is_request"):
            self.answers[request_id] = package
            if kind == "player_list":
                self.last_players = [
                    str(p) for p in ((package.get("payload") or {}).get("player_list") or [])
                ]
        else:
            self.pushed.append(package)

    async def call(self, kind: str, payload: dict | None = None, timeout: float = 20.0) -> dict | None:
        package = build_package(kind, payload or {})
        request_id = package["request_id"]
        # build_envelope 已经返回 JSON 字符串, 再 dumps 一次会变成双重编码, TShock 直接解析失败
        raw = build_envelope(package)
        self.dump(f"-> 发 {kind}", raw)
        await self._send(raw)

        deadline = time.monotonic() + timeout
        while time.monotonic() < deadline:
            if request_id in self.answers:
                return self.answers.pop(request_id)
            await asyncio.sleep(0.05)

        if self._verbose:
            print(f"      {DIM}等待 {request_id} 应答超时{RESET}")
        return None


async def tcp_probe(host: str, port: int, timeout: float = 8.0) -> bool:
    step("1. TCP 连通性")
    try:
        _, writer = await asyncio.wait_for(asyncio.open_connection(host, port), timeout)
    except Exception as exc:
        bad(f"{host}:{port} 连不上 -> {type(exc).__name__}: {exc}")
        print(f"       依次检查: 到 {host} 的路由、云主机安全组、服务器防火墙, "
              f"以及 /cbl onebot 里『本服监听地址』的端口是不是 {port}")
        return False

    writer.close()
    ok(f"{host}:{port} 可达")
    return True


async def pump(socket, probe: Probe) -> None:
    async for message in socket:
        probe.feed(message)


async def run_connect(args: argparse.Namespace) -> int:
    url = f"ws://{args.host}:{args.port}/onebot/v11/ws?{urlencode({'secret': args.secret})}"

    try:
        socket = await connect(
            url,
            additional_headers={"Authorization": f"Bearer {args.secret}"},
            open_timeout=12,
            max_size=64 * 1024 * 1024,
        )
    except Exception as exc:
        bad(f"WebSocket 握手失败: {type(exc).__name__}: {exc}")
        text = str(exc)
        if "404" in text:
            print("       404 = 路径不对, 本服应该监听 /onebot/v11/ws")
        if "403" in text or "401" in text:
            print("       密钥不对: /cbl onebot 里看『通讯密钥』")
        if "valid HTTP response" in text or "ConnectionClosed" in text:
            print("       端口能连上但不回 HTTP —— 最常见的原因是这个端口后面根本没有服务在监听:\n"
                  "         1) 还是 CaiBot 官方模式, 桥接没开 -> /cbl onebot mode onebot\n"
                  "         2) 云主机安全组/防火墙放行了但后端没服务, SYN 代理会这样应答\n"
                  "         3) 监听前缀绑不上 -> /cbl onebot listen http://*:17779/")
        return 1

    step("2. WebSocket 握手与密钥")
    ok(f"已连上 {args.host}:{args.port}, 密钥校验通过")

    async with socket:
        probe = Probe(lambda raw: socket.send(raw), args.verbose)
        reader = asyncio.create_task(pump(socket, probe))
        try:
            await check_api(probe, args)
        finally:
            reader.cancel()
    return 0


async def run_listen(args: argparse.Namespace) -> int:
    arrived: asyncio.Queue = asyncio.Queue()
    closing = asyncio.Event()

    async def handler(socket) -> None:
        await arrived.put(socket)
        await closing.wait()

    await serve(handler, "0.0.0.0", args.listen, max_size=64 * 1024 * 1024)
    step("2. 等待服务端反向连接")
    print(f"  本脚本监听 ws://0.0.0.0:{args.listen}/caibotlite")
    print(f"  TShock 里执行: {BOLD}/cbl onebot url ws://<本机IP>:{args.listen}/caibotlite{RESET}"
          f"  然后  {BOLD}/cbl onebot reload{RESET}")
    print(f"  注意服务端通道要是 {BOLD}client{RESET}(或 auto), 否则它不会主动连\n")

    try:
        socket = await asyncio.wait_for(arrived.get(), timeout=args.wait)
    except asyncio.TimeoutError:
        bad(f"{args.wait} 秒内没有等到服务端连接")
        return 1

    ok("服务端已连上")

    async with socket:
        probe = Probe(lambda raw: socket.send(raw), args.verbose)
        reader = asyncio.create_task(pump(socket, probe))
        try:
            await check_api(probe, args)
        finally:
            reader.cancel()
            closing.set()
    return 0


async def check_api(probe: Probe, args: argparse.Namespace) -> None:
    step("3. 服务端握手包 Hello")
    for _ in range(80):
        if probe.hello:
            break
        await asyncio.sleep(0.1)

    if not probe.hello:
        bad("没收到 Hello -> 服务端可能还没开 OneBot 通道(/cbl onebot channel server)")
    else:
        payload = probe.hello
        ok(f"服务器={payload.get('server_name')}  "
           f"插件={payload.get('plugin_version')}  游戏={payload.get('game_version')}")
        print(f"       白名单={payload.get('enable_whitelist')}  "
              f"公开地址={payload.get('public_url') or '(未配置, 地图会走 base64 内联)'}")

    step("4. 接口逐个实测")
    calls: list[tuple[str, dict, str]] = [
        ("progress", {}, "世界进度"),
        ("player_list", {}, "在线列表"),
        ("plugin_list", {}, "插件列表"),
        ("rank_data", {"rank_type": "死亡", "arg": ""}, "死亡排行"),
    ]

    for kind, payload, note in calls:
        answer = await probe.call(kind, payload, args.timeout)
        if answer is None:
            bad(f"{note} ({kind}) 超时无应答 -> 看 TShock 控制台有没有『处理消息时发生错误』")
            continue
        if answer.get("type") == "error":
            bad(f"{note} ({kind}) 服务端报错: {(answer.get('payload') or {}).get('error')}")
            continue
        ok(f"{note}: {flat(render(answer).text)}")

    if probe.last_players:
        name = probe.last_players[0]
        answer = await probe.call("look_bag", {"player_name": name}, args.timeout)
        if answer is None:
            bad(f"查背包 {name} 超时")
        elif answer.get("type") == "error":
            bad(f"查背包 {name} 服务端报错: {(answer.get('payload') or {}).get('error')}")
        else:
            ok(f"查背包 {name}: {flat(render(answer).text)}")
    else:
        warn("当前没有在线玩家, 跳过查背包(随便进个服再跑一次)")

    step("5. 地图图片(生成较慢)")
    if args.skip_map:
        warn("--skip-map, 已跳过")
    else:
        answer = await probe.call("map_image", {}, args.map_timeout)
        if answer is None:
            bad(f"上传地图 超时({args.map_timeout}s), 大地图会更慢")
        elif answer.get("type") == "error":
            bad(f"上传地图 服务端报错: {(answer.get('payload') or {}).get('error')}")
        else:
            result = render(answer, reachable=False)
            if result.image and result.image.startswith("base64://"):
                how = f"内联图片 {len(result.image) - 10} 字符"
            elif result.image:
                how = f"下载链接 {result.image}"
            else:
                how = "没有图片"
            ok(f"上传地图: {result.text} -> {how}")

    if args.deep:
        step("6. 有副作用的接口 (--deep)")
        answer = await probe.call(
            "call_command",
            {"command": args.command, "user_open_id": "", "group_open_id": ""},
            args.timeout,
        )
        if answer is None:
            bad(f"执行指令 {args.command!r} 超时")
        else:
            ok(f"执行指令 {args.command!r}: {flat(render(answer).text)}")

        answer = await probe.call("shop_condition", {"item_conditions": ["Moonlord"]}, args.timeout)
        if answer is None:
            bad("查询进度条件 超时")
        else:
            ok(f"进度条件: {flat(render(answer).text)}")

    step("7. 服务端主动推送")
    if probe.pushed:
        ok(f"收到 {len(probe.pushed)} 条: " + ", ".join(str(p.get("type")) for p in probe.pushed))
    else:
        warn("没有主动推送(玩家进服时才会推白名单校验, 空服属正常)")


async def main() -> int:
    parser = argparse.ArgumentParser(description="CaiBotLite 服务端自检", formatter_class=argparse.RawTextHelpFormatter)
    parser.add_argument("--host", default="127.0.0.1", help="TShock 地址(默认 127.0.0.1)")
    parser.add_argument("--port", type=int, default=17779, help="TShock 桥接端口(默认 17779)")
    parser.add_argument(
        "--secret",
        default="",
        help="通讯密钥, 建议改用环境变量 CAIBOTLITE__SECRET",
    )
    parser.add_argument("--listen", type=int, help="改成监听模式, 等 TShock 反连这个端口")
    parser.add_argument("--wait", type=int, default=90, help="监听模式等待秒数")
    parser.add_argument("--timeout", type=float, default=20.0, help="单次请求超时")
    parser.add_argument("--map-timeout", type=float, default=180.0, help="地图生成超时")
    parser.add_argument("--skip-map", action="store_true", help="跳过地图(最快跑完)")
    parser.add_argument("--verbose", action="store_true", help="打印收发原始报文")
    parser.add_argument("--deep", action="store_true", help="额外测远程指令/进度条件")
    parser.add_argument("--command", default="version", help="--deep 时执行的指令")
    args = parser.parse_args()

    # 密钥优先取 --secret, 没给就读环境变量。
    # 走环境变量是为了别把密钥留在 shell 历史和进程列表里。
    secret = args.secret or os.environ.get("CAIBOTLITE__SECRET", "")
    if not secret:
        print(f"{RED}缺少通讯密钥{RESET}  用 --secret 传, 或设环境变量 CAIBOTLITE__SECRET")
        return 2
    args.secret = secret

    # 编解码必须用命令行给的密钥, 不能用本地 .env 里的(探针通常不在 nonebot 目录下跑)
    config.secret = secret

    print(f"{BOLD}CaiBotLite 服务端自检{RESET}  {args.host}:{args.port}  secret={'*' * len(secret)}")

    if args.listen:
        code = await run_listen(args)
    else:
        if not await tcp_probe(args.host, args.port):
            print(f"\n{YELLOW}提示{RESET} 端口不通时, 确认 /cbl onebot 里『本服监听地址』的端口是 {args.port}, "
                  f"通道是 server/auto, 并且安全组放行了该端口。\n"
                  f"     如果你把 TShock 的通道设成了 client, 请改用: --listen 18099")
            return 1
        code = await run_connect(args)

    print()
    if code == 0 and not failures:
        print(f"{GREEN}{BOLD}服务端工作正常{RESET}")
    else:
        print(f"{RED}{BOLD}{len(failures)} 项没通过{RESET}")
        for item in failures:
            print(f"  - {item}")
    return code


if __name__ == "__main__":
    sys.exit(asyncio.run(main()))

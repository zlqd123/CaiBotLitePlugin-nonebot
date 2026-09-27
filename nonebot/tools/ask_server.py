"""直接问服务器: 把 /泰拉 执行 <指令> 的应答原文打出来。

用来确认某个指令在**你这台服务器**上到底存不存在、参数怎么写。
不带参数执行通常会返回该指令的帮助文本。
"""

from __future__ import annotations

import argparse
import asyncio
import os
import sys
import time

sys.path.insert(0, os.path.join(os.path.dirname(__file__), "..", "src"))

import nonebot  # noqa: E402

nonebot.init()

from nonebot_plugin_caibotlite.config import config  # noqa: E402
from nonebot_plugin_caibotlite.models import build_envelope, build_package, parse_envelope  # noqa: E402


async def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--host", default="127.0.0.1")
    parser.add_argument("--port", type=int, default=17779)
    parser.add_argument(
        "--secret",
        default="",
        help="通讯密钥, 建议改用环境变量 CAIBOTLITE__SECRET",
    )
    parser.add_argument("--timeout", type=float, default=15.0)
    parser.add_argument("commands", nargs="+")
    args = parser.parse_args()

    # 密钥优先取 --secret, 没给就读环境变量, 避免留在 shell 历史里
    secret = args.secret or os.environ.get("CAIBOTLITE__SECRET", "")
    if not secret:
        print("缺少通讯密钥: 用 --secret 传, 或设环境变量 CAIBOTLITE__SECRET", file=sys.stderr)
        return 2
    config.secret = secret

    import websockets

    url = f"ws://{args.host}:{args.port}/onebot/v11/ws?secret={secret}"
    async with websockets.connect(url) as ws:
        hello = json.loads(await asyncio.wait_for(ws.recv(), args.timeout))
        package = parse_envelope(json.dumps(hello, ensure_ascii=False))
        payload = (package or {}).get("payload") or {}
        print(f"服务器 {payload.get('server_name')}  插件 {payload.get('plugin_version')}\n")

        for command in args.commands:
            req = build_package(
                "call_command",
                {"command": command, "user_open_id": "", "group_open_id": ""},
            )
            request_id = req["request_id"]
            await ws.send(build_envelope(req))

            answer = None
            deadline = time.monotonic() + args.timeout
            while time.monotonic() < deadline and answer is None:
                try:
                    raw = await asyncio.wait_for(ws.recv(), deadline - time.monotonic())
                except asyncio.TimeoutError:
                    break
                got = parse_envelope(raw if isinstance(raw, str) else raw.decode("utf-8", "replace"))
                if got and got.get("request_id") == request_id:
                    answer = got

            print(f"===== /{command} =====")
            if answer is None:
                print("  (超时, 服务端没有应答)")
            else:
                out = (answer.get("payload") or {}).get("output")
                print(out if out else f"  (无输出) {answer}")
            print()

    return 0


import json  # noqa: E402

if __name__ == "__main__":
    sys.exit(asyncio.run(main()))

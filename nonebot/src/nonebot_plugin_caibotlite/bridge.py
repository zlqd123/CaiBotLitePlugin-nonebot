"""与 TShock 端之间的桥接通道。

支持四种模式, 协议完全一致(见 :mod:`.models`):

* ``auto``:   同时监听并主动连接, 谁先连上就用谁(默认, 适合两边网络不互通的场景)
* ``client``: 机器人主动连接 TShock, 请求与应答都走这条 WebSocket
* ``server``: TShock 反向连接机器人监听的 WebSocket
* ``http``:   机器人用 HTTP POST 推送请求, 应答仍然通过 WebSocket 返回

两台机器不在同一网络时, 只要其中一台有公网地址(或做了内网穿透)就能配通:
有公网地址的那台当"被连接方", 另一台主动连过去, ``auto`` 模式下两条路都会尝试。
"""

from __future__ import annotations

import asyncio
import contextlib
import json
import time
from typing import Awaitable, Callable
from urllib.parse import parse_qsl, urlencode, urlparse, urlunparse

import httpx
from nonebot.log import logger
from websockets.asyncio.client import connect
from websockets.asyncio.server import ServerConnection, serve

from .config import Config
from .models import build_envelope, parse_envelope

logger = logger.bind(name="caibotlite")

PackageHandler = Callable[[dict], Awaitable[None]]


class Bridge:
    def __init__(self, cfg: Config, on_package: PackageHandler) -> None:
        self.cfg = cfg
        self._on_package = on_package
        self._out: ServerConnection | None = None
        self._tasks: set[asyncio.Task] = set()
        self._pending: dict[str, asyncio.Future] = {}
        self._stop = asyncio.Event()
        self._send_lock = asyncio.Lock()
        self._client: httpx.AsyncClient | None = None
        self._client_probe: dict[str, tuple[bool, float]] = {}
        self._http: httpx.AsyncClient | None = None
        self._last_bad_frame = 0.0

    # ------------------------------------------------------------------ 生命周期

    async def start(self) -> None:
        if not self.cfg.secret:
            raise RuntimeError("请先在配置中填写 CAIBOTLITE__SECRET(与 TShock 端的通讯密钥一致)")

        mode = self.cfg.mode
        if mode in ("server", "auto"):
            await self._start_server()
        if mode in ("client", "auto"):
            self._spawn(self._client_loop())

        if mode not in ("client", "server", "http", "auto"):
            raise RuntimeError(f"未知的 CAIBOTLITE__MODE: {mode}")

        logger.info(f"CaiBotLite 桥接已启动, 模式={mode}")

    async def _start_server(self) -> None:
        path = self.cfg.listen_path or "/caibotlite"
        await serve(
            self._on_connection,
            self.cfg.listen_host,
            self.cfg.listen_port,
            ping_interval=20,
            ping_timeout=20,
            max_size=16 * 1024 * 1024,
        )
        logger.info(
            f"CaiBotLite 正在监听 ws://{self.cfg.listen_host}:{self.cfg.listen_port}{path}"
            f" (TShock 端 OneBot.机器人地址 填这个, 模式选 server 或 auto)"
        )

    async def stop(self) -> None:
        self._stop.set()
        for task in list(self._tasks):
            task.cancel()
        self._tasks.clear()

        if self._out is not None:
            with contextlib.suppress(Exception):
                await self._out.close()
            self._out = None

        for client in (self._client, self._http):
            if client is not None:
                with contextlib.suppress(Exception):
                    await client.aclose()
        self._client = self._http = None

        for future in self._pending.values():
            if not future.done():
                future.set_exception(ConnectionError("桥接已关闭"))
        self._pending.clear()

    def _spawn(self, coro) -> asyncio.Task:
        task = asyncio.create_task(coro)
        self._tasks.add(task)
        task.add_done_callback(self._tasks.discard)
        return task

    @property
    def connected(self) -> bool:
        return self._out is not None

    # ------------------------------------------------------------------ 连接处理

    def _attach(self, connection, describe: str) -> bool:
        """先连上的生效, 另一条链路让位, 避免两条连接同时存在。"""
        previous = self._out
        if previous is not None:
            self._spawn(self._close_later(previous, "已存在另一条可用连接"))
        self._out = connection
        logger.info(f"TShock 已连接: {describe}")
        return True

    async def _close_later(self, connection, reason: str) -> None:
        with contextlib.suppress(Exception):
            await connection.close(code=1000, reason=reason)

    async def _on_connection(self, connection: ServerConnection) -> None:
        secret = _query_secret(connection.request.path)
        if secret is not None and secret != self.cfg.secret:
            logger.warning("CaiBotLite 拒绝了一个密钥错误的连接")
            await connection.close(code=1008, reason="bad secret")
            return

        self._attach(connection, f"反向连接 {connection.request.path.split('?')[0]}")
        try:
            async for message in connection:
                await self._handle_text(message)
        except Exception as exc:  # pragma: no cover - 网络异常
            logger.debug(f"CaiBotLite 连接异常: {exc}")
        finally:
            if self._out is connection:
                self._out = None
            logger.info("TShock 已断开(反向连接)")

    async def _client_loop(self) -> None:
        url = self._server_ws_url()
        failures = 0
        while not self._stop.is_set():
            try:
                if self._out is not None:
                    # 已经有 TShock 连过来了, 主动连接这条路待命
                    failures = 0
                    await asyncio.sleep(max(1, self.cfg.reconnect))
                    continue

                async with connect(
                    url,
                    additional_headers={"Authorization": f"Bearer {self.cfg.secret}"},
                    ping_interval=20,
                    ping_timeout=20,
                    max_size=16 * 1024 * 1024,
                ) as connection:
                    self._attach(connection, url.split("?")[0])
                    failures = 0
                    async for message in connection:
                        await self._handle_text(message)
            except asyncio.CancelledError:
                raise
            except Exception as exc:
                failures += 1
                # 对方不可达时不要刷屏
                if failures == 1 or failures % 20 == 0:
                    logger.warning(f"连接 TShock 失败({url.split('?')[0]}): {exc}")
            finally:
                if self._out is not None:
                    self._out = None

            if self._stop.is_set():
                break
            await asyncio.sleep(max(1, self.cfg.reconnect))

    def _server_ws_url(self) -> str:
        url = self.cfg.server_url.strip()
        if "://" not in url:
            url = "ws://" + url
        parsed = urlparse(url)
        if parsed.scheme == "http":
            parsed = parsed._replace(scheme="ws")
        elif parsed.scheme == "https":
            parsed = parsed._replace(scheme="wss")
        query = dict(parse_qsl(parsed.query))
        query.setdefault("secret", self.cfg.secret)
        return urlunparse(parsed._replace(query=urlencode(query)))

    # ------------------------------------------------------------------ 收发

    def _warn_unparsable(self, raw: str) -> None:
        """解析失败要能看出原因, 否则密钥填错时一点线索都没有。"""
        now = time.monotonic()
        if now - self._last_bad_frame < 60:
            return

        self._last_bad_frame = now
        try:
            envelope = json.loads(raw)
            reason = (
                f"密钥不一致(收到的 {envelope.get('secret')!r}, 期望 {self.cfg.secret!r})"
                if isinstance(envelope, dict)
                and envelope.get("package") is not None
                and envelope.get("secret") != self.cfg.secret
                else "格式不是 CaiBotLite 桥接信封"
            )
        except (TypeError, ValueError):
            reason = "不是合法的 JSON"

        logger.warning(f"CaiBotLite 收到无法解析的数据, 已丢弃: {reason}")

    async def _handle_text(self, raw: str | bytes) -> None:
        if isinstance(raw, bytes):
            raw = raw.decode("utf-8", "ignore")

        package = parse_envelope(raw)
        if package is None:
            self._warn_unparsable(raw)
            return

        if self.cfg.debug:
            logger.info(f"CaiBotLite 收到数据包: {json.dumps(package, ensure_ascii=False)}")

        request_id = package.get("request_id")
        future = self._pending.pop(request_id, None) if request_id else None
        if future is not None and not future.done():
            future.set_result(package)
            return

        self._spawn(self._on_package(package))

    async def send(self, package: dict) -> bool:
        """把一个数据包发给 TShock。"""
        body = build_envelope(package)

        if self.cfg.mode == "http":
            return await self._post(body)

        if self._out is None:
            logger.warning("CaiBotLite: TShock 未连接, 数据包已丢弃")
            return False

        async with self._send_lock:
            try:
                await self._out.send(body)
            except Exception as exc:
                logger.warning(f"CaiBotLite 发送失败: {exc}")
                return False
        return True

    async def _post(self, body: str) -> bool:
        url = self.cfg.push_url.strip() or _http_event_url(self.cfg.server_url)
        if self._http is None:
            self._http = httpx.AsyncClient(timeout=10.0)
        try:
            response = await self._http.post(
                url,
                content=body.encode("utf-8"),
                headers={
                    "Content-Type": "application/json",
                    "Authorization": f"Bearer {self.cfg.secret}",
                },
            )
            response.raise_for_status()
        except Exception as exc:
            logger.warning(f"CaiBotLite 推送失败({url}): {exc}")
            return False
        return True

    async def reachable(self, url: str) -> bool:
        """探测 TShock 提供的下载链接在机器人这边能不能打开。

        TShock 与机器人不在同一网络时, 地图/存档的下载链接可能根本不可达,
        这时候图片要退回 base64 内联发送。结果按主机缓存, 避免每次都去试。
        """
        host = urlparse(url).netloc
        cached = self._client_probe.get(host)
        now = time.monotonic()
        if cached is not None and now - cached[1] < max(30, self.cfg.probe_cache):
            return cached[0]

        ok = False
        if self._client is None:
            self._client = httpx.AsyncClient(timeout=max(1.0, self.cfg.probe_timeout))
        try:
            response = await self._client.get(url, headers={"Range": "bytes=0-1"})
            ok = response.status_code < 500
        except Exception:
            ok = False

        self._client_probe[host] = (ok, now)
        if not ok:
            logger.warning(f"TShock 的文件地址不可达: {url} (请检查 TShock 端的『本服公开地址』)")
        return ok

    async def request(self, package: dict, timeout: float | None = None) -> dict:
        """发送一个请求并等待 TShock 的应答。"""
        request_id = package.get("request_id")
        future: asyncio.Future = asyncio.get_running_loop().create_future()
        if request_id:
            self._pending[request_id] = future

        try:
            if not await self.send(package):
                raise ConnectionError("与服务器的连接不可用")
            return await asyncio.wait_for(future, timeout or self.cfg.request_timeout)
        except asyncio.TimeoutError as exc:
            raise TimeoutError("服务器响应超时") from exc
        finally:
            if request_id:
                self._pending.pop(request_id, None)


def _query_secret(path: str) -> str | None:
    if "?" not in path:
        return None
    query = path.split("?", 1)[1]
    for item in query.split("&"):
        key, _, value = item.partition("=")
        if key == "secret":
            return value
    return None


def _http_event_url(server_url: str) -> str:
    """把 ``ws://host:port/onebot/v11/ws`` 转换成 ``http://host:port/onebot/v11/event``。"""
    url = server_url.strip()
    if "://" not in url:
        url = "http://" + url
    parsed = urlparse(url)
    scheme = "http" if parsed.scheme in ("ws", "http") else "https"
    path = parsed.path
    if path.endswith("/ws"):
        path = path[:-3] + "event"
    else:
        path = path.rstrip("/") + "/onebot/v11/event"
    return urlunparse((scheme, parsed.netloc, path, "", "", ""))

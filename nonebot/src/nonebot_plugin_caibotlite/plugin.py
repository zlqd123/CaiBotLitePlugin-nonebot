"""插件主体: 指令解析、权限控制与消息发送。

白名单管理 / 设备登录 / 商店 / 踢出 已按需下线, 服务端不再处理对应数据包。
"""

from __future__ import annotations

import asyncio

from typing import Any

from nonebot import get_bots, get_driver
from nonebot.adapters.onebot.v11 import Bot, Message, MessageEvent, MessageSegment
from nonebot.log import logger
from nonebot.matcher import Matcher
from nonebot.plugin import on_message

from .bridge import Bridge
from .commands import CommandSpec, CommandTable
from .config import Config, config
from .models import ReplyTarget, build_package, get_file, get_target
from .render import RenderResult, render

logger = logger.bind(name="caibotlite")

matcher = on_message(priority=50, block=False)

#: 权限不足时的统一提示。刻意不解释原因, 也不告诉对方怎么绕过
NO_PERMISSION = "无权限"


def _is_bot_admin(user_id: int) -> bool:
    """机器人管理员: .env 里的 CAIBOTLITE__ADMINS / CAIBOTLITE__SUPERUSERS。

    刻意**不看**群管理身份——受限指令只对服主开放, 群的 owner/admin 也拦在外面。
    """
    return user_id in config.superusers or user_id in config.admins


def _user_id(event: MessageEvent) -> int:
    """``event.get_user_id()`` 返回的是字符串, 而配置里存的是 int, 这里统一转换。"""
    try:
        return int(event.get_user_id())
    except (TypeError, ValueError):
        return 0


def _group_id(event: MessageEvent) -> int:
    """OneBot v11 适配器没有 get_group_id, 私聊事件上也没有 group_id 字段。"""
    return getattr(event, "group_id", 0) or 0


def _role(event: MessageEvent) -> str:
    """群成员身份: member / admin / owner, 私聊统一按 member 处理。"""
    sender = getattr(event, "sender", None)
    return getattr(sender, "role", None) or "member"


def _group_allowed(group_id: int, user_id: int) -> bool:
    if not config.groups or _is_bot_admin(user_id):
        return True
    return group_id in config.groups


def _is_manager(role: str | None) -> bool:
    return role in ("admin", "owner")


class CaiBotLite:
    """把 bridge / 渲染 / 发送串起来的运行时对象。"""

    def __init__(self, cfg: Config) -> None:
        self.cfg = cfg
        self.bridge = Bridge(cfg, self.on_package)
        self.server_name = cfg.server_name
        self.public_url = ""
        self.commands = CommandTable.load(cfg.commands_path or None)
        #: 服务端(CaiBotLite.json)真正的远程执行白名单, 由 /泰拉 插件列表 的响应带回来。
        #: 权威只有服务端那一份, 这里只做缓存和展示, 不自己维护第二份。
        self.allowed_commands: list[str] = []

    # ------------------------------------------------------------------ 启动

    async def start(self) -> None:
        if not self.cfg.enabled:
            logger.info("CaiBotLite 插件已禁用")
            return
        logger.info(
            f"启动中: 模式={self.cfg.mode} 地址={self.cfg.server_url or '(只监听)'} "
            f"监听={self.cfg.listen_host}:{self.cfg.listen_port} "
            f"前缀={self.cfg.prefix} 根指令={self.cfg.roots} "
            f"开放群={self.cfg.groups or '(不限制)'} "
            f"管理员={self.cfg.admins or '(无)'} 超管={self.cfg.superusers or '(无)'} "
            f"调试={self.cfg.debug}"
        )
        await self.bridge.start()

    async def stop(self) -> None:
        await self.bridge.stop()

    async def refresh_allowed_commands(self) -> None:
        """连上服务器后问一次"当前允许远程执行什么"。

        这是 /泰拉 插件列表 的附带信息, 顺带拉一次就能拿到, 不用等谁先发那条指令。
        拿不到也没关系: 帮助里那一行会自动省略, 权限判断仍然只依赖本地指令表。
        """
        if not self.bridge.connected:
            return
        try:
            await self.call(build_package("plugin_list"), None)
        except Exception as exc:  # 拉不到就当没有, 不影响其它功能
            logger.debug(f"拉取服务端执行白名单失败: {exc}")

    # ------------------------------------------------------------------ 发消息

    def pick_bot(self, self_id: int = 0) -> Bot | None:
        bots = get_bots()
        if not bots:
            return None
        if self_id:
            bot = bots.get(str(self_id))
            if bot is not None:
                return bot
        return next(iter(bots.values()))

    async def send(self, target: ReplyTarget | None, result: RenderResult) -> None:
        bot = self.pick_bot(target.self_id if target else 0)
        if bot is None:
            logger.warning("没有可用的 OneBot 连接, 消息已丢弃")
            return

        message = Message()
        if result.image:
            message += MessageSegment.image(result.image)
        if result.file:
            message += MessageSegment("file", {"file": result.file, "name": result.file_name})
        if result.text:
            message += MessageSegment.text(result.text)
        if not message:
            logger.warning("渲染结果为空, 没有可发送的内容")
            return

        group_id = target.group_id if target else 0
        user_id = target.user_id if target else 0
        if not group_id and not user_id:
            # 只有"服务器主动发来、又没有指定回送目标"的数据包才会走默认群,
            # 私聊的回复带着 user_id, 绝不会掉到这里再发到群里去
            group_id = self.cfg.default_group
        if not group_id and not user_id:
            logger.warning("没有配置默认回复群, 消息已丢弃")
            return

        reply = (
            MessageSegment("reply", {"id": target.message_id})
            if target and target.message_id
            else None
        )

        try:
            await self._raw_send(bot, group_id, user_id, (reply + message) if reply else message)
        except Exception as exc:
            if reply is None:
                logger.warning(f"发送消息失败: {exc}")
                return
            # 有的实现不支持 reply 消息段, 退化成普通消息再试一次
            try:
                await self._raw_send(bot, group_id, user_id, message)
            except Exception as retry_exc:
                logger.warning(f"发送消息失败: {retry_exc}")

    @staticmethod
    async def _raw_send(bot: Bot, group_id: int, user_id: int, message: Message) -> None:
        if group_id:
            await bot.send_group_msg(group_id=group_id, message=message)
        else:
            await bot.send_private_msg(user_id=user_id, message=message)

    async def reply_text(self, event: MessageEvent, text: str) -> None:
        await self.send(ReplyTarget.from_event(event), RenderResult(text=text))

    # ------------------------------------------------------------------ 收发包

    async def call(self, package: dict[str, Any], target: ReplyTarget | None) -> RenderResult:
        """向服务器发一个请求并把结果渲染出来。

        ``target`` 为 None 表示只是想问问服务器状态, 不需要知道回复发给谁。
        """
        package = dict(package)
        payload = dict(package.get("payload") or {})
        payload["__bridge"] = target.to_dict() if target else {}
        package["payload"] = payload

        try:
            answer = await self.bridge.request(package)
        except TimeoutError:
            return RenderResult(text="服务器响应超时, 请稍后再试")
        except ConnectionError:
            return RenderResult(text="服务器当前未连接, 请稍后再试")

        self._remember_allowed_commands(answer)
        return render(answer)

    def _remember_allowed_commands(self, package: dict[str, Any]) -> None:
        """从 plugin_list 响应里取出服务端的远程执行白名单。

        服务端(CaiBotLite.json -> OneBot -> 允许远程执行的指令)是唯一的权威,
        机器人侧不维护第二份。这里只缓存下来用于:
        1. 帮助里如实显示"服务器当前允许执行什么";
        2. 校验 commands.json 里公开的那部分有没有和服务端脱节。
        """
        if str(package.get("type") or "") != "plugin_list":
            return

        allowed = package.get("payload", {}).get("allowed_commands")
        if not isinstance(allowed, list) or not allowed:
            return

        names = [str(c).strip().lstrip("/").lower() for c in allowed if str(c).strip()]
        if names == self.allowed_commands:
            return

        self.allowed_commands = names
        self._warn_drift()

    def _warn_drift(self) -> None:
        """commands.json 公开的指令如果服务端并不允许, 用户就会看到一句莫名其妙的
        "未被允许执行"。这里提前吵一架, 比让用户去猜要好。"""
        if not self.allowed_commands:
            return

        server = set(self.allowed_commands)
        for spec in self.commands.specs:
            for name in spec.open_commands:
                if name not in server:
                    logger.warning(
                        f"指令表的『{spec.name}』把 {name} 列在开放指令里, "
                        f"但服务端(CaiBotLite.json -> 允许远程执行的指令)没有放行它, "
                        f"普通人发这条会收到『未被允许执行』。请改其中一边。"
                    )

    async def file_target(self, target: ReplyTarget) -> ReplyTarget:
        """请求 map / 存档前, 先确认服务器给的文件地址在本机能不能打开。

        机器人跑在无公网容器里、而 TShock 的文件端口没暴露时, 下载链接是打不开的,
        这时让服务器直接把内容用 base64 发过来, 而不是给一个点不动的链接。
        """
        if not self.public_url:
            return target

        if await self.bridge.reachable(f"{self.public_url}/files/probe"):
            return target

        target.file_mode = "inline"
        return target

    async def on_package(self, package: dict[str, Any]) -> None:
        """处理服务器主动发来的数据包。"""
        package_type = str(package.get("type") or "")
        payload = package.get("payload") or {}
        target = get_target(package)

        if package_type == "whitelist":
            # 服务端已不再用这个包做登录裁决(玩家登录不再经过机器人),
            # 收到只记录一下, 不做任何处理
            logger.info(f"收到白名单校验包(服务端已下线该功能, 忽略): {payload.get('player_name')}")
            return

        if package_type == "hello":
            self.server_name = str(payload.get("server_name") or self.cfg.server_name)
            self.public_url = str(payload.get("public_url") or "").rstrip("/")
            logger.info(
                f"已收到服务器握手: {self.server_name} (TShock {payload.get('server_core_version')})"
            )
            # 趁这条握手还在事件循环里, 顺手问一次服务端当前允许远程执行什么
            asyncio.create_task(self.refresh_allowed_commands())
            return

        if package_type in ("heartbeat", "unknown"):
            return

        if package_type == "unbind_server":
            logger.warning(f"服务器请求解绑: {payload.get('reason')}")
            return

        # 附件类数据包: 先确认下载链接在机器人这边能不能打开, 打不开就退回 base64 内联
        reachable = True
        if package_type in ("map_image", "map_file", "world_file"):
            info = get_file(package)
            url = str((info or {}).get("url") or "")
            if url:
                reachable = await self.bridge.reachable(url)

        await self.send(target, render(package, reachable))

    # ------------------------------------------------------------------ 权限

    async def check_permission(self, event: MessageEvent, spec: CommandSpec, args: list[str]) -> bool:
        """按 commands.json 里的权限放行。

        ``user``    对所有人开放
        ``botadmin`` 只认 ``CAIBOTLITE__ADMINS`` / ``CAIBOTLITE__SUPERUSERS``
                    里的 QQ, 群管理不算

        另外, 公开指令可以带 ``开放指令`` 列表(比如 ``/泰拉 执行`` 只默认放行
        time / clear / wind / worldevent): 名单外的指令按 ``botadmin`` 处理,
        其它人一律得到"无权限"。
        """
        user_id = _user_id(event)
        group_id = _group_id(event)

        if group_id and not _group_allowed(group_id, user_id):
            await self.reply_text(event, "当前群未开放该功能")
            return False

        if _is_bot_admin(user_id):
            return True

        if spec.is_public and (not spec.open_commands or spec.is_open_subcommand(args)):
            return True

        await self.reply_text(event, NO_PERMISSION)
        return False


#: 帮助的固定头部。用法/说明/权限都来自 commands.json, 这里只放不变的引导语
HELP_HEADER = "所有指令都要以 /泰拉 开头, 后面跟指令名和参数。"


def build_help(server_name: str, table: CommandTable, allowed: list[str] | None = None) -> str:
    """生成 /泰拉 帮助。

    内容完全由 ``commands.json`` 里 ``权限`` 为 ``user`` 的指令拼出来:
    改了 JSON, 帮助跟着变, 不用动代码。
    ``开放指令`` 名单会自动列在对应指令下面, 不会和代码脱节;
    传了 ``allowed``(从服务端拉到的真实白名单)时再补一行, 让人看得见全貌。
    """
    specs = table.public
    if not specs:
        return f"【{server_name}】\n指令表里没有配置任何对所有人开放的指令。"

    rows: list[str] = []
    for spec in specs:
        rows.append(f"  {spec.usage:<22}{spec.desc}".rstrip())
        if spec.open_commands:
            rows.append(f"    默认开放: {'  '.join(spec.open_commands)}")
        rows.extend(f"    {d}" for d in spec.details)

    if allowed and any(s.open_commands for s in specs):
        rows.append(f"    服务器当前允许远程执行: {', '.join(allowed)}")

    return "\n".join(
        [
            f"【{server_name}】",
            "",
            HELP_HEADER,
            "",
            *rows,
            "",
        ]
    )


#: 会带附件的数据包, 请求前要先确认服务器的文件地址在本机能不能打开
FILE_PACKAGES = ("map_image", "map_file", "world_file")


def parse_command(text: str) -> tuple[str, list[str], bool] | None:
    """从消息里解析出指令名与参数。

    指令必须写成 ``/泰拉 进度`` 这种形式: 去掉前缀后, 第一个词必须命中
    :attr:`Config.roots`(泰拉 / tl), 第二个词才是真正的指令名。

    第三个返回值表示这条消息是不是"裸指令"(既没有前缀, 也没有根指令名)。
    裸指令下如果匹配不到任何指令, 就不回复任何东西, 避免把普通聊天也当成指令刷屏。
    """
    text = text.strip()
    if not text:
        return None

    body = text
    explicit = False
    for prefix in config.prefix:
        if prefix and text.startswith(prefix):
            body = text[len(prefix) :].strip()
            explicit = True
            break

    if not body:
        return None

    parts = [p for p in body.split() if p]

    roots = [r for r in config.roots if r]
    if roots:
        if not parts:
            return None
        if parts[0].lower() not in {r.lower() for r in roots}:
            # 没写根指令名, 就不当成指令处理
            return None
        parts = parts[1:]
        explicit = True
        if not parts:
            return None

    return parts[0], parts[1:], explicit


# ------------------------------------------------------------------ 运行时

runtime: CaiBotLite | None = None


def get_runtime() -> CaiBotLite:
    global runtime
    if runtime is None:
        runtime = CaiBotLite(config)
    return runtime


# ------------------------------------------------------------------ 事件处理


@matcher.handle()
async def handle_message(matcher_obj: Matcher, event: MessageEvent) -> None:
    try:
        await _handle_message(event)
    except Exception as exc:
        # 任何异常都要让提问的人看见, 而不是自己在日志里闷掉
        logger.opt(exception=exc).error(f"处理消息时出错: {exc}")
        try:
            await get_runtime().reply_text(event, f"处理指令时出错: {exc}"[:500])
        except Exception:
            logger.opt(exception=exc).error("出错后连提示都发不出去")


async def _handle_message(event: MessageEvent) -> None:
    rt = get_runtime()
    text = event.get_plaintext()
    if not rt.cfg.enabled:
        logger.debug("插件未启用, 已忽略消息")
        return
    if _user_id(event) == event.self_id:
        return

    # 不打印每条消息: 群里说什么都会进来, 打日志既刷屏又没有信息量。
    # 只在确实是一条指令时才记录, 便于排查"发了没反应"。
    parsed = parse_command(text)
    if parsed is None:
        return
    name, args, explicit = parsed

    if config.debug:
        logger.info(f"指令: {name} 参数={args} 显式={explicit} 来自群 {_group_id(event)}")

    table = rt.commands
    if table.is_empty:
        # 指令表没读到, 再说什么都是没意义的
        if explicit:
            await rt.reply_text(event, "指令表 commands.json 没读到, 机器人暂不可用")
        return

    spec = table.get(name)
    if spec is None:
        if explicit:
            await rt.reply_text(event, f"未知指令: 泰拉 {name}\n发送 /泰拉 帮助 查看可用指令")
        return

    # 帮助这类命令不需要服务器参与, 直接在本地出结果
    if not spec.package:
        await rt.reply_text(event, build_help(rt.server_name, table, rt.allowed_commands))
        return

    if spec.need_arg and not args:
        await rt.reply_text(event, f"用法: {spec.usage}\n{spec.desc}")
        return

    # 参数齐了再查权限: 开放指令名单靠第一个参数判断, 缺参数时该给用法而不是"无权限"
    if not await rt.check_permission(event, spec, args):
        return

    target = ReplyTarget.from_event(event)
    if spec.package in FILE_PACKAGES:
        # 服务器的文件地址打不开时, 让它改成 base64 内联发
        target = await rt.file_target(target)

    # 能不能执行完全由 TShock 端的"允许远程执行的指令"决定
    # (CaiBotPlayer 是固定超管身份, 那边的白名单才是真正的安全边界)。
    # 拒绝时服务端会回一段带原因的 output, 原样透出即可。
    package = build_package(spec.package, spec.build_payload(args), target=target)
    await rt.send(target, await rt.call(package, target))


# ------------------------------------------------------------------ 生命周期

driver = get_driver()


@driver.on_startup
async def _startup() -> None:
    await get_runtime().start()


@driver.on_shutdown
async def _shutdown() -> None:
    if runtime is not None:
        await runtime.stop()

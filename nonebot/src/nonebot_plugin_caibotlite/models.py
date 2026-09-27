"""CaiBotLite 数据包与桥接协议。

桥接协议非常薄: 一条 WebSocket / HTTP 通道上流动的每一行都是一个信封::

    {"v": 1, "secret": "密钥", "package": "CaiBotLite 数据包(JSON字符串)"}

``package`` 就是 TShock 端使用的原始数据包, 因此两端可以各自独立演进.
"""

from __future__ import annotations

import json
import re
import uuid
from typing import Any

from .config import config

PROTOCOL_VERSION = 1

TARGET_KEY = "__bridge"
FILE_KEY = "__file"

#: 各种数据包的协议版本, 与 TShock 端 ``PackageType.GetVersion()`` 保持一致
PACKAGE_VERSIONS: dict[str, str] = {
    "hello": "2025.7.18",
    "whitelist": "2025.7.18",
    "player_list": "2025.7.18",
    "progress": "2025.7.18",
    "look_bag": "2025.7.18",
    "world_file": "2025.7.18",
    "map_file": "2025.7.18",
    "map_image": "2025.7.18",
    "self_kick": "2025.7.18",
    "call_command": "2025.7.18",
    "unbind_server": "2025.7.25",
    "heartbeat": "2025.7.25",
    "rank_data": "2025.7.25",
    "plugin_list": "2025.7.25",
    "shop_buy": "2025.7.25",
    "shop_condition": "2025.7.25",
    "error": "2026.2.14",
    "unknown": "2025.7.18",
}

_SNAKE_BOUNDARY = re.compile(r"(?<=[a-z0-9])(?=[A-Z])")


def to_snake_case(name: str) -> str:
    """把 ``EyeOfCthulhu`` 转换成 ``eye_of_cthulhu``, 与 Newtonsoft 行为一致。"""
    return _SNAKE_BOUNDARY.sub("_", name).lower()


class ReplyTarget:
    """一条消息应该回复到哪里。"""

    __slots__ = (
        "self_id",
        "group_id",
        "user_id",
        "message_id",
        "session",
        "at_sender",
        "nickname",
        "is_admin",
        "file_mode",
    )
    def __init__(
        self,
        self_id: int = 0,
        group_id: int = 0,
        user_id: int = 0,
        message_id: int = 0,
        session: str = "",
        at_sender: bool = False,
        nickname: str = "",
        is_admin: bool = False,
        file_mode: str = "",
    ) -> None:
        self.self_id = self_id
        self.group_id = group_id
        self.user_id = user_id
        self.message_id = message_id
        self.session = session
        self.at_sender = at_sender
        self.nickname = nickname
        self.is_admin = is_admin
        # 附件外发偏好: link=用下载链接(默认), inline=让服务器直接 base64 内联
        self.file_mode = file_mode

    @classmethod
    def from_event(cls, event: Any) -> ReplyTarget:
        """从 OneBot v11 的消息事件构造回复目标。

        注意 OneBot v11 适配器并没有 ``get_group_id()`` / ``get_role()``,
        群号与身份都要自己从事件上取, 私聊事件上并不存在这些字段。
        """
        group_id = getattr(event, "group_id", 0) or 0
        sender = getattr(event, "sender", None)
        nickname = ""
        if sender is not None:
            nickname = getattr(sender, "card", "") or getattr(sender, "nickname", "") or ""

        return cls(
            self_id=getattr(event, "self_id", 0),
            group_id=group_id,
            user_id=getattr(event, "user_id", 0),
            message_id=getattr(event, "message_id", 0),
            at_sender=False,
            nickname=nickname,
        )

    def to_dict(self) -> dict[str, Any]:
        data = {
            "self_id": self.self_id,
            "group_id": self.group_id,
            "user_id": self.user_id,
            "message_id": self.message_id,
            "session": self.session,
            "at_sender": self.at_sender,
            "nickname": self.nickname,
            "is_admin": self.is_admin,
        }
        if self.file_mode:
            data["file_mode"] = self.file_mode
        return data

    def __repr__(self) -> str:  # pragma: no cover - 调试用
        return f"<ReplyTarget group={self.group_id} user={self.user_id}>"


def build_package(
    package_type: str,
    payload: dict[str, Any] | None = None,
    *,
    is_request: bool = True,
    request_id: str | None = None,
    target: ReplyTarget | None = None,
) -> dict[str, Any]:
    """构造一个 CaiBotLite 数据包。"""
    body: dict[str, Any] = dict(payload or {})
    if target is not None:
        body[TARGET_KEY] = target.to_dict()

    return {
        "version": PACKAGE_VERSIONS.get(package_type, "2025.7.18"),
        "direction": "to_server",
        "type": package_type,
        "is_request": is_request,
        "request_id": request_id or uuid.uuid4().hex,
        "payload": body,
    }


def build_envelope(package: dict[str, Any]) -> str:
    """把数据包包装成桥接信封。"""
    return json.dumps(
        {
            "v": PROTOCOL_VERSION,
            "secret": config.secret,
            "package": json.dumps(package, ensure_ascii=False),
        },
        ensure_ascii=False,
    )


def parse_envelope(raw: str) -> dict[str, Any] | None:
    """解析桥接信封, 返回其中的 CaiBotLite 数据包。"""
    try:
        envelope = json.loads(raw)
    except (TypeError, ValueError):
        return None

    if not isinstance(envelope, dict):
        return None

    if envelope.get("secret") != config.secret:
        return None

    package = envelope.get("package")
    if isinstance(package, str):
        try:
            package = json.loads(package)
        except ValueError:
            return None

    if not isinstance(package, dict) or "type" not in package:
        return None

    return package


def get_target(package: dict[str, Any]) -> ReplyTarget | None:
    """取出数据包里的回复目标。"""
    raw = (package.get("payload") or {}).get(TARGET_KEY)
    if not isinstance(raw, dict):
        return None

    return ReplyTarget(
        self_id=raw.get("self_id", 0),
        group_id=raw.get("group_id", 0),
        user_id=raw.get("user_id", 0),
        message_id=raw.get("message_id", 0),
        session=raw.get("session", ""),
        at_sender=raw.get("at_sender", False),
        nickname=raw.get("nickname", ""),
        is_admin=raw.get("is_admin", False),
    )


def get_file(package: dict[str, Any]) -> dict[str, Any] | None:
    """取出数据包里的附件信息。"""
    raw = (package.get("payload") or {}).get(FILE_KEY)
    return raw if isinstance(raw, dict) else None

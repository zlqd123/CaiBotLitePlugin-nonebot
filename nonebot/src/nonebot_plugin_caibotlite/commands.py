"""从 JSON 读取指令表。

指令的名字、说明、权限、别名、参数映射全部来自 ``commands.json``, 不写死在代码里:
改文件就能增删指令、调整权限, 不用重新打包插件。

权限只有两种:

* ``user``     所有人可用, 并且会出现在 ``/泰拉 帮助`` 里
* ``botadmin`` 只有机器人管理员(``CAIBOTLITE__ADMINS`` / ``CAIBOTLITE__SUPERUSERS``)
  可用, 群管理也不行, 并且不会出现在帮助里
"""

from __future__ import annotations

import json
import os
from pathlib import Path
from typing import Any

from nonebot.log import logger

logger = logger.bind(name="caibotlite")

PERM_USER = "user"
PERM_BOTADMIN = "botadmin"

#: JSON 里允许省略的键及其默认值
_DEFAULTS: dict[str, Any] = {
    "用法": "",
    "说明": "",
    "权限": PERM_USER,
    "包": "",
    "参数": "",
    "别名": [],
    "参数映射": {},
    "详情": [],
    "开放指令": [],
}


class CommandSpec:
    """一条指令的定义。"""

    __slots__ = ("name", "usage", "desc", "perm", "package", "arg", "aliases",
                 "arg_map", "details", "open_commands")

    def __init__(self, name: str, raw: dict[str, Any]) -> None:
        self.name = name
        self.usage = str(raw.get("用法") or f"/泰拉 {name}")
        self.desc = str(raw.get("说明") or "")
        self.perm = str(raw.get("权限") or PERM_USER).strip().lower()
        if self.perm not in (PERM_USER, PERM_BOTADMIN):
            logger.warning(f"指令 {name} 的权限 {self.perm!r} 不认识, 按所有人可用处理")
            self.perm = PERM_USER
        self.package = str(raw.get("包") or "")
        self.arg = str(raw.get("参数") or "")
        self.aliases = [str(a) for a in (raw.get("别名") or [])]
        self.arg_map = {str(k).lower(): str(v) for k, v in (raw.get("参数映射") or {}).items()}
        self.details = [str(d) for d in (raw.get("详情") or [])]
        self.open_commands = [str(c).lower() for c in (raw.get("开放指令") or [])]

    @property
    def is_public(self) -> bool:
        """是否出现在帮助里(也就意味着出现在这份表里就有基本权限)。"""
        return self.perm == PERM_USER

    @property
    def need_arg(self) -> bool:
        return bool(self.arg)

    def is_open_subcommand(self, args: list[str]) -> bool:
        """这条指令的第一个参数是否在 ``开放指令`` 名单里。

        名单为空表示这条指令不受这一层限制(公开指令人人可用)。
        """
        if not self.open_commands:
            return True
        return bool(args) and args[0].lower() in self.open_commands

    def map_arg(self, value: str) -> str:
        """把用户写的参数翻译成服务器要的值(没配置映射就原样返回)。"""
        return self.arg_map.get(value.lower(), value)

    def build_payload(self, args: list[str]) -> dict[str, Any]:
        """按数据包类型拼出 ``payload``。"""
        if self.package == "call_command":
            # user_open_id / group_open_id 是服务端必读字段, OneBot 模式下用 __bridge 代替,
            # 这里仍然要传空串, 否则服务端会抛 KeyNotFoundException
            return {
                "command": " ".join(args),
                "user_open_id": "",
                "group_open_id": "",
            }

        if self.package == "rank_data":
            return {
                "rank_type": self.map_arg(args[0]) if args else "",
                "arg": " ".join(args[1:]),
            }

        if self.package == "look_bag":
            return {"player_name": args[0] if args else ""}

        return {}


class CommandTable:
    """指令表: 名字(含别名) -> :class:`CommandSpec`。"""

    def __init__(self) -> None:
        self._by_name: dict[str, CommandSpec] = {}
        self._specs: list[CommandSpec] = []
        self.path = ""

    # ------------------------------------------------------------------ 加载

    @classmethod
    def load(cls, path: str | os.PathLike[str] | None = None) -> CommandTable:
        table = cls()

        if path:
            # 显式指定了路径就用它。指错了要报错, 不能悄悄换成默认那份——
            # 否则你以为改的是自己的配置表, 实际跑的是另一份。
            target = Path(path)
            if not target.is_file():
                logger.error(f"指定的指令表 {target} 不存在, 机器人不会响应任何指令")
                return table
        else:
            target = cls.default_path()
            if not target.is_file():
                # 兜底: 挨个常见位置找一遍, 免得因为工作目录不同就读不到
                for candidate in cls.candidates():
                    if candidate.is_file():
                        target = candidate
                        break
                else:
                    logger.error(
                        f"没找到指令表 commands.json(找过 "
                        f"{', '.join(str(c) for c in cls.candidates())}), "
                        f"机器人不会响应任何指令"
                    )
                    return table

        try:
            raw = json.loads(target.read_text(encoding="utf-8"))
        except Exception as exc:
            logger.error(f"读取指令表 {target} 失败: {exc}, 机器人不会响应任何指令")
            return table

        if not isinstance(raw, dict):
            logger.error(f"指令表 {target} 格式不对: 顶层必须是对象")
            return table

        table.path = str(target)
        index: dict[str, CommandSpec] = {}
        for name, spec in raw.items():
            if name.startswith("_") or not isinstance(spec, dict):
                continue  # 下划线开头的是注释

            merged = {**_DEFAULTS, **spec}
            item = CommandSpec(str(name), merged)
            table._specs.append(item)
            index[item.name.lower()] = item
            for alias in item.aliases:
                index.setdefault(alias.lower(), item)

        table._by_name = index
        logger.info(f"已载入指令表 {target.name}: {len(table._specs)} 条指令, "
                    f"其中 {sum(1 for s in table._specs if s.is_public)} 条对所有人开放")
        return table

    @staticmethod
    def candidates() -> list[Path]:
        here = Path(__file__).resolve().parent
        return [
            here / "commands.json",
            Path.cwd() / "commands.json",
            Path.cwd() / "config" / "commands.json",
        ]

    @staticmethod
    def default_path() -> Path:
        return CommandTable.candidates()[0]

    # ------------------------------------------------------------------ 查询

    def get(self, name: str) -> CommandSpec | None:
        return self._by_name.get(name.lower())

    @property
    def specs(self) -> list[CommandSpec]:
        return list(self._specs)

    @property
    def public(self) -> list[CommandSpec]:
        """会出现在帮助里的指令, 按 JSON 里的顺序。"""
        return [s for s in self._specs if s.is_public]

    @property
    def is_empty(self) -> bool:
        return not self._specs

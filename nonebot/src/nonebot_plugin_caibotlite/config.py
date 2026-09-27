"""插件配置。

所有配置项都可以通过环境变量或者 ``.env`` 文件设置, 前缀为 ``CAIBOTLITE__``,
例如::

    CAIBOTLITE__SECRET=0123456789abcdef
    CAIBOTLITE__MODE=auto
    CAIBOTLITE__SERVER_URL=ws://1.2.3.4:17779/onebot/v11/ws
"""

from typing import Any, Literal

from pydantic import Field

try:
    from pydantic_settings import BaseSettings, SettingsConfigDict
except ImportError:  # pragma: no cover - 容器里直接拷源码时可能没装
    # pydantic-settings 是独立包, 缺了不该让整个插件加载失败(否则机器人完全不工作)。
    # 这里自己实现一份等价的: 读 .env 与环境变量里的 CAIBOTLITE__ 前缀, 类型处理和
    # pydantic-settings 保持一致(list 用 JSON 数组或逗号分隔, bool 认 1/true/yes/on)。
    import os as _os

    from pydantic import BaseModel, TypeAdapter, ValidationError

    def SettingsConfigDict(**kwargs: Any) -> dict[str, Any]:  # type: ignore[misc]
        return dict(kwargs)

    def _coerce(annotation: Any, raw: str) -> Any:  # type: ignore[misc]
        text = raw.strip()
        origin = getattr(annotation, "__origin__", None)
        args = getattr(annotation, "__args__", ())

        if origin is list:
            item_type = args[0] if args else str

            def _one(x: Any) -> Any:
                return item_type(x) if item_type in (int, float) else str(x)

            if text.startswith("[") and text.endswith("]"):
                import json

                try:
                    return [_one(x) for x in json.loads(text)]
                except Exception:
                    pass  # 写成 [泰拉,tt] 这种没加引号的, 退回按逗号切
            return [
                _one(x)
                for x in text.strip("[]").replace("，", " ").replace(",", " ").split()
            ]

        if annotation is bool:
            return text.lower() in ("1", "true", "yes", "on")

        if annotation is int:
            return int(text)

        if annotation is float:
            return float(text)

        return text

    class BaseSettings(BaseModel):  # type: ignore[no-redef,misc]
        """等价于 pydantic_settings.BaseSettings 的最小实现。"""

        model_config: Any = {}

        def __init__(self, **data: Any) -> None:
            prefix = "CAIBOTLITE__"
            env_file = self.model_config.get("env_file", ".env")
            for path in [env_file] if isinstance(env_file, str) else list(env_file or []):
                try:
                    with open(path, encoding="utf-8") as handle:
                        for line in handle:
                            line = line.strip()
                            if not line or line.startswith("#") or "=" not in line:
                                continue
                            key, _, raw = line.partition("=")
                            # 真实环境变量优先于 .env
                            _os.environ.setdefault(key.strip(), raw.strip().strip("'\""))
                except OSError:
                    pass

            values: dict[str, Any] = {}
            for name, field in type(self).model_fields.items():
                raw = _os.environ.get(prefix + name.upper())
                if raw is None:
                    continue
                try:
                    values[name] = TypeAdapter(field.annotation).validate_python(
                        _coerce(field.annotation, raw)
                    )
                except (ValidationError, ValueError) as exc:
                    # 单个值写错只丢这一个字段并提示, 不让整个插件加载失败
                    import sys as _sys

                    print(
                        f"[caibotlite] 配置项 {prefix}{name.upper()} 的值有问题, 已忽略: {exc}",
                        file=_sys.stderr,
                    )

            values.update(data)
            super().__init__(**values)

Mode = Literal["auto", "client", "server", "http"]
"""桥接模式:

* ``auto``    同时监听并主动连接, 谁先连上就用谁(默认, 适合两边网络不互通的场景)
* ``client``  机器人主动连接 TShock(适合 TShock 有公网地址)
* ``server``  机器人监听端口, 让 TShock 反向连接(适合机器人有公网地址)
* ``http``    机器人用 HTTP 推送请求, 回复走 WebSocket
"""


class Config(BaseSettings):
    model_config = SettingsConfigDict(
        env_file=".env",
        env_file_encoding="utf-8",
        env_prefix="CAIBOTLITE__",
        extra="ignore",
    )

    enabled: bool = True
    """是否启用插件"""

    secret: str = ""
    """与 TShock 端 ``OneBot.通讯密钥`` 一致"""

    mode: Mode = "auto"
    """桥接模式"""

    server_url: str = "ws://127.0.0.1:17779/onebot/v11/ws"
    """``client`` / ``http`` / ``auto`` 模式下 TShock 侧的地址"""

    push_url: str = ""
    """``http`` 模式下的推送地址, 留空则由 server_url 推导"""

    listen_host: str = "0.0.0.0"
    listen_port: int = 8080
    listen_path: str = "/caibotlite"
    """``server`` / ``auto`` 模式下 WebSocket 服务端的监听参数"""

    server_name: str = "Terraria 服务器"
    """展示用的服务器名, 收到 Hello 数据包后会被服务器名覆盖"""

    default_group: int = 0
    """服务器主动发来的数据包(白名单校验等)默认回送到哪个群"""

    groups: list[int] = Field(default_factory=list)
    """允许使用机器人的群号, 留空表示不限制"""

    admins: list[int] = Field(default_factory=list)
    """管理员 QQ 号, 可以执行远程指令 / 管理白名单"""

    superusers: list[int] = Field(default_factory=list)
    """超级管理员 QQ 号, 无视群白名单限制"""

    prefix: list[str] = Field(default_factory=lambda: ["", "/", "。"])
    """指令前缀, 空字符串表示裸指令也可用"""

    roots: list[str] = Field(default_factory=lambda: ["泰拉", "tl"])
    """根指令名, 写完前缀后必须先出现它, 比如 /泰拉 进度。留空等于不校验根指令名"""

    commands_path: str = ""
    """指令表 commands.json 的路径, 留空则用插件目录下的那一份。

    指令的名字、说明、权限、别名、参数映射全在那个文件里, 改它就能调整
    机器人支持哪些指令、哪些对所有人开放, 不用改代码。
    """

    rank_limit: int = 10
    """排行榜最多显示多少条"""

    request_timeout: float = 15.0
    """等待服务器应答的超时时间(秒)"""

    reconnect: int = 5
    """断线重连间隔(秒)"""

    probe_timeout: float = 5.0
    """探测 TShock 下载链接是否可达的超时时间(秒)"""

    probe_cache: int = 300
    """下载链接可达性结果的缓存时间(秒)"""

    debug: bool = False
    """打印桥接数据与每条收到的消息"""


config = Config()

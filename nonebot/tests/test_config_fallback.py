"""验证没有 pydantic-settings 时插件仍能加载并正确读配置。

模拟容器里直接拷源码、忘记装依赖的情况(现场就是这么炸的)。
"""

from __future__ import annotations

import builtins
import os
import sys
import tempfile

sys.path.insert(0, os.path.join(os.path.dirname(__file__), "..", "src"))

import nonebot  # noqa: E402

nonebot.init()

failures = 0


def check(name: str, condition: bool, detail: str = "") -> None:
    global failures
    if condition:
        print(f"  OK   {name}")
    else:
        failures += 1
        print(f"  FAIL {name} {detail}")


print("-- 正常路径: pydantic-settings 已安装 --")
from nonebot_plugin_caibotlite.config import config  # noqa: E402

check("secret 是字符串", isinstance(config.secret, str), repr(config.secret))
check("prefix 是列表", isinstance(config.prefix, list), repr(config.prefix))
check("roots 含泰拉", "泰拉" in config.roots, repr(config.roots))

print()
print("-- 降级路径: 屏蔽 pydantic_settings 后重新加载 --")

with tempfile.TemporaryDirectory() as tmp:
    env_path = os.path.join(tmp, ".env")
    with open(env_path, "w", encoding="utf-8") as handle:
        handle.write("CAIBOTLITE__SECRET=from-env-file\n")
        handle.write("CAIBOTLITE__DEBUG=true\n")
        handle.write("CAIBOTLITE__DEFAULT_GROUP=123456\n")
        handle.write("CAIBOTLITE__GROUPS=[111,222]\n")
        handle.write("CAIBOTLITE__RECONNECT=9\n")
        handle.write("CAIBOTLITE__RANK_LIMIT=3\n")
        handle.write("# 注释行\n")
        handle.write("CAIBOTLITE__ROOTS=[泰拉,tt]\n")
        # 写坏的值: 只应丢掉这一个字段, 插件照样要能起来
        handle.write("CAIBOTLITE__MODE=写错了\n")
        handle.write("CAIBOTLITE__LISTEN_PORT=不是数字\n")

    cwd = os.getcwd()
    os.chdir(tmp)

    # 把已导入的模块和 pydantic_settings 一起挡掉
    for mod in [m for m in sys.modules if m.startswith("nonebot_plugin_caibotlite")]:
        del sys.modules[mod]

    real_import = builtins.__import__

    def fake_import(name, globals=None, locals=None, fromlist=(), level=0):
        if name == "pydantic_settings":
            raise ImportError("No module named 'pydantic_settings'")
        return real_import(name, globals, locals, fromlist, level)

    builtins.__import__ = fake_import
    try:
        from nonebot_plugin_caibotlite.config import Config, config as fallback_config

        check("仍然能实例化", isinstance(fallback_config, Config), type(fallback_config).__name__)
        check("读到 .env 的 secret", fallback_config.secret == "from-env-file", repr(fallback_config.secret))
        check("读到 bool", fallback_config.debug is True, repr(fallback_config.debug))
        check("读到 int", fallback_config.default_group == 123456, repr(fallback_config.default_group))
        check("读到 int 字段", fallback_config.reconnect == 9, repr(fallback_config.reconnect))
        check("读到 list[int]", fallback_config.groups == [111, 222], repr(fallback_config.groups))
        check("list 类型正确", all(isinstance(x, int) for x in fallback_config.groups), repr(fallback_config.groups))
        check("读到 list[str]", fallback_config.roots == ["泰拉", "tt"], repr(fallback_config.roots))
        check("没配的用默认值", fallback_config.rank_limit == 3, repr(fallback_config.rank_limit))
        check("默认值兜底 listen_port", fallback_config.listen_port == 8080, repr(fallback_config.listen_port))
        # 关键: 写错的配置项只丢自己, 不能连累插件加载失败
        check("写错的 mode 被丢掉", fallback_config.mode == "auto", repr(fallback_config.mode))
        check("写错的端口被丢掉", fallback_config.listen_port == 8080, repr(fallback_config.listen_port))
    except Exception as exc:
        import traceback

        traceback.print_exc()
        check("降级路径可用", False, f"{type(exc).__name__}: {exc}")
    finally:
        builtins.__import__ = real_import
        os.chdir(cwd)
        for mod in [m for m in sys.modules if m.startswith("nonebot_plugin_caibotlite")]:
            del sys.modules[mod]

print()
print("全部通过" if failures == 0 else f"{failures} 项失败")
sys.exit(0 if failures == 0 else 1)

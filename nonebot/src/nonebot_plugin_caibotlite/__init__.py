"""CaiBotLite 的 NoneBot 侧适配插件。

与 TShock 端 ``CaiBotLite`` 插件配套使用, 让任意基于 OneBot v11 的机器人
(Lagrange.Core / NapCat / LLOneBot ...) 能够为 Terraria 服务器提供
进度、背包、排行、地图与远程指令等功能。玩家登录不经由本插件。

.. code-block:: text

    CAIBOTLITE__SECRET=0123456789abcdef   # 与 TShock 端 OneBot.通讯密钥 一致
    CAIBOTLITE__MODE=auto                 # 跨网络时先通的那条链路生效
    CAIBOTLITE__SERVER_URL=ws://127.0.0.1:17779/onebot/v11/ws
"""

from nonebot.plugin import PluginMetadata

from .config import Config, config
from .plugin import CaiBotLite, get_runtime, matcher

__all__ = ["CaiBotLite", "Config", "config", "get_runtime", "matcher"]

__version__ = "1.0.0"

__plugin_meta__ = PluginMetadata(
    name="CaiBotLite",
    description="CaiBotLite 的 NoneBot 侧适配插件(进度/背包/排行/远程指令)",
    usage=(
        "1. 在 TShock 端执行 /cbl onebot 复制通讯密钥\n"
        "2. 在 .env 中配置 CAIBOTLITE__SECRET 与 CAIBOTLITE__MODE(默认 auto, 跨网络也能通)\n"
        "3. 在群里发送 /泰拉 帮助 查看全部指令"
    ),
    type="application",
    homepage="https://github.com/zlqd123/CaiBotLitePlugin-nonebot",
    supported_adapters={"OneBot V11"},
)

# nonebot-plugin-caibotlite

`CaiBotLite` 的 NoneBot 侧适配插件。配合 TShock 端的 `CaiBotLite` 插件，可以让
**任意基于 OneBot v11 的机器人**（Lagrange.Core / NapCat / LLOneBot / go-cqhttp …）
为 Terraria 服务器提供进度、背包、排行、地图、远程指令等功能。

> 这是 [`CaiBotLitePlugin-nonebot`](https://github.com/zlqd123/CaiBotLitePlugin-nonebot)
> 仓库的一部分，整体说明（架构、TShock 端怎么装、鉴权在哪一边）见[根目录 README](../README.md)。
>
> 与官方 CaiBot 机器人的区别：数据全部由你自己保管，机器人实现也可以自由替换。
> **登录不由机器人裁决**，玩家进服走 TShock 自己的流程。

## 功能

| 分类 | 能力 |
| --- | --- |
| 查询 | 世界进度、在线列表、玩家背包、排行榜（BOSS/死亡/在线/钓鱼/货币）、插件列表 |
| 地图 | 生成并发送地图图片、发送 `.map`、发送 `.wld` 存档(不在帮助里, 直接发指令名) |
| 管理 | 远程执行服务器指令（白名单放行）、查看服务器插件列表 |

## 安装

```bash
nb plugin install nonebot-plugin-caibotlite
```

或者手动放进 `src/plugins/` 后在 `pyproject.toml` 里声明：

```toml
[tool.nonebot]
plugins = ["nonebot_plugin_caibotlite"]
```

### ⚠️ 直接把源码拷进容器目录时

Docker 里常见的做法是把插件挂载到 `/app/plugins/nonebot_plugin_caibotlite/`。
**这样 `pyproject.toml` 不会生效，依赖不会自动安装**，缺包会在启动时报：

```
Failed to import "nonebot_plugin_caibotlite"
ModuleNotFoundError: No module named 'pydantic_settings'
```

两个办法，任选一个：

1. **装依赖**（推荐）：

   ```bash
   pip install pydantic-settings websockets
   ```

2. **不装也能用**：`config.py` 内置了降级实现，缺 `pydantic-settings` 时会自己读
   `.env` 与环境变量，类型处理保持一致（list 支持 `[1,2]` JSON 数组，
   也支持 `泰拉,tl` 这种逗号分隔）。单个配置值写错只会丢掉那一个字段并打警告，
   不会让整个插件加载失败。

不管用哪种方式，**装完都要重启 nonebot**，并在启动日志里确认能看到：

```
[SUCCESS] nonebot_plugin_caibotlite loaded
启动中: 模式=... 前缀=... 根指令=['泰拉','tl'] 开放群=... 调试=...
```

没看到这两行就是插件没加载，机器人会**完全不响应任何指令且不报错**——
这是最容易误判成"服务端挂了"的情况。

## 配置

在 `.env` 中配置（所有配置项前缀均为 `CAIBOTLITE__`）：

```dotenv
# 与 TShock 端 "CaiBotLite.json" -> OneBot -> 通讯密钥 一致（游戏内 /cbl onebot 可查看）
CAIBOTLITE__SECRET=0123456789abcdef

# auto  : 同时监听并主动连接，谁先连上用谁（默认，跨网络首选）
# server: 只监听，让 TShock 反连（机器人有公网地址时用）
# client: 只主动连 TShock（TShock 有公网地址时用）
# http  : 用 HTTP 推送请求，应答走 WebSocket
CAIBOTLITE__MODE=auto

# client / auto 模式下 TShock 的地址；TShock 没有公网地址时留空即可
CAIBOTLITE__SERVER_URL=ws://127.0.0.1:17779/onebot/v11/ws

# server / auto 模式下监听地址，TShock 端 "机器人地址" 填 ws://你的IP:8080/caibotlite
CAIBOTLITE__LISTEN_HOST=0.0.0.0
CAIBOTLITE__LISTEN_PORT=8080

# 服务器主动发来的数据包(白名单校验)默认回送到哪个群
CAIBOTLITE__DEFAULT_GROUP=123456789

# 允许使用机器人的群(留空=不限制)
CAIBOTLITE__GROUPS=[]
# 超级管理员 QQ, 可以执行 /执行 <指令>
CAIBOTLITE__ADMINS=[111111111]
```

| 配置项 | 默认值 | 说明 |
| --- | --- | --- |
| `CAIBOTLITE__ENABLED` | `true` | 是否启用 |
| `CAIBOTLITE__SECRET` | - | 通讯密钥, **必填** |
| `CAIBOTLITE__MODE` | `auto` | `auto` / `client` / `server` / `http` |
| `CAIBOTLITE__SERVER_URL` | `ws://127.0.0.1:17779/onebot/v11/ws` | TShock 侧地址 |
| `CAIBOTLITE__PUSH_URL` | 空 | `http` 模式下的推送地址, 留空自动由 `SERVER_URL` 推导 |
| `CAIBOTLITE__LISTEN_HOST` / `_PORT` | `0.0.0.0` / `8080` | 监听地址 |
| `CAIBOTLITE__DEFAULT_GROUP` | `0` | 服务器主动发来的数据包默认回送到哪个群 |
| `CAIBOTLITE__GROUPS` | `[]` | 允许使用机器人的群号, 空 = 不限制 |
| `CAIBOTLITE__ADMINS` | `[]` | 机器人管理员 QQ, 只有他们能用隐藏指令(群管理不行) |
| `CAIBOTLITE__SUPERUSERS` | `[]` | 超级管理员 QQ(不受群限制) |
| `CAIBOTLITE__COMMANDS_PATH` | 空 | 指令表路径, 留空用插件目录下的 `commands.json` |
| `CAIBOTLITE__PREFIX` | `["", "/", "。"]` | 指令前缀。**注意: 无论用哪种前缀, 根指令名都必须在**, 所以 `/进度` 不会生效, 要写 `/泰拉 进度` |
| `CAIBOTLITE__ROOTS` | `["泰拉", "tl"]` | 根指令名, 前缀后必须先出现它; 留空 = 不校验根指令名 |
| `CAIBOTLITE__RANK_LIMIT` | `10` | 排行榜显示条数 |
| `CAIBOTLITE__REQUEST_TIMEOUT` | `15` | 等待服务器应答超时(秒) |
| `CAIBOTLITE__PROBE_TIMEOUT` | `5` | 探测 TShock 下载链接是否可达的超时(秒) |
| `CAIBOTLITE__PROBE_CACHE` | `300` | 下载链接可达性结果的缓存时间(秒) |
| `CAIBOTLITE__DEBUG` | `false` | 打印桥接数据 |

## 跨网络部署(TShock 与机器人不在同一网络)

两端只有一台能被另一台访问到就能配通。两端的模式名是**各自视角**的，配对关系如下：

| 谁能被另一端访问到 | TShock 端 `OneBot通道` | 机器人端 `CAIBOTLITE__MODE` | 谁去连谁 |
| --- | --- | --- | --- |
| **TShock** 有公网 IP/域名 | `server`（监听 17779） | `client`（主动连） | 机器人 → TShock |
| **机器人** 有公网 IP/域名 | `client`（主动连） | `server`（监听 8080） | TShock → 机器人 |
| 不确定 / 两边都行 | `auto` | `auto` | 两条都试，先通的生效 |

记忆方式：`client` = 自己是客户端（去连别人），`server` = 自己是服务端（等别人连），`auto` = 两边身份都试、先通先用。

| 两端都没公网地址 | 任选一端做内网穿透（frp / tailscale / cloudflared），恢复其中一条链路后按上面配 |

要点：

- 桥接**不复用游戏端口 7777，也不走 TShock REST API**，而是独立端口 17779，可以单独做防火墙和端口转发，不影响玩家进服。
- 发送地图/存档用的是 `TShock 端 OneBot.本服公开地址` 拼出来的下载链接，这个地址必须能被**跑 OneBot 实现的那台机器**访问到。插件会在请求附件前先探测一次：打不开时自动让服务器改成 base64 内联发送（地图图片功能不受影响），存档会提示管理员检查 `本服公开地址`。
- 桥接全程走 `通讯密钥` 校验，即使端口暴露到公网别人也拿不到数据。

## TShock 端配置

游戏内执行 `/cbl onebot` 查看与修改，也可以直接改 `tshock/CaiBotLite.json`：

```json5
{
  "OneBot": {
    "机器人连接方式": "onebot",   // caibot = 官方机器人, onebot = 本插件
    "OneBot通道": "auto",         // auto = 监听+主动连, 先到先得
    "机器人地址": "",             // client 模式: ws://127.0.0.1:8080/caibotlite
    "本服监听地址": "http://0.0.0.0:17779/",
    "本服公开地址": "",           // 发送 map/存档时需要, 机器人访问不到本服就留空
    "通讯密钥": "0123456789abcdef",
    "默认回复群号": 123456789,
    "附送物品名称": true
  }
}
```

## 指令

**所有指令都必须写成 `/泰拉 xxx`**: 去掉 `/` 之后第一个词必须是根指令名
`泰拉`(或简写 `tl`, 见 `CAIBOTLITE__ROOTS`)。`/进度` 这种少写根指令名的形式
一律不认, 也不会回复——避免把普通聊天误当指令。

指令表在 [`commands.json`](src/nonebot_plugin_caibotlite/commands.json) 里,
**没有写死在代码中**。下面两张表是那个文件当前的内容, 改文件即生效, 不用改代码。

### 对所有人开放(会出现在 `/泰拉 帮助` 里)

| 指令 | 说明 |
| --- | --- |
| `/泰拉 帮助` | 指令列表 |
| `/泰拉 进度` | 世界进度、已击败 BOSS、进度锁 |
| `/泰拉 在线` | 在线人数与玩家列表 |
| `/泰拉 排行 <类型> [参数]` | `boss 史莱姆王`、`死亡`、`在线`、`钓鱼`、`货币 金币` |
| `/泰拉 查背包 <角色名>` | 背包 / 装备 / 存钱罐 / 状态 / 永久强化 / 经济, 谁都能查 |
| `/泰拉 插件列表` | 服务器插件列表 |
| `/泰拉 执行 time` | 改时间(不接参数则查看当前时间) |
| `/泰拉 执行 clear` | 停雨停风 |
| `/泰拉 执行 wind <风速>` | 调风力 |
| `/泰拉 执行 worldevent <事件>` | 造天气, 如 `rain` `meteor` `bloodmoon`; 不带参数服务器会自己列 |

### 需要机器人管理员

`.env` 里 `CAIBOTLITE__ADMINS` / `CAIBOTLITE__SUPERUSERS` 中的 QQ 才能用。
**群管理身份不算, 群的 owner / admin 也一样会被挡住。** 权限不足时机器人只回两个字
`无权限`——不解释原因, 也不告诉对方怎么绕过。

| 指令 | 说明 |
| --- | --- |
| `/泰拉 执行 <其它指令>` | 除上面四个以外的服务器指令 |
| `/泰拉 上传地图` | 生成并发送地图图片(需服务器装 `GenerateMap`) |
| `/泰拉 下载地图` | 发送 `.map` 文件(需 `GenerateMap`) |
| `/泰拉 下载存档` | 发送 `.wld` 存档(需 `GenerateMap`) |

> `/泰拉 执行` 发出后还会被 TShock 端的 `允许远程执行的指令` 再筛一遍。
> 那份名单应当**大于**默认开放的这四条: 机器人管理员能用的更多, 普通玩家只有
> time / clear / wind / worldevent。

### 哪边在鉴权

两层，职责不同：

| | TShock 端（`CaiBotLite.json`） | nonebot 端（`commands.json`） |
| --- | --- | --- |
| 存的东西 | `允许远程执行的指令` | 谁能发什么、帮助文案、别名 |
| **安全边界** | ✅ **就是这里**。`CaiBotApi.cs` 里 `IsRemoteCommandAllowed` | ❌ **不是**。拿到 `secret` 的人可以绕过机器人直连 17779 发包 |
| 谁维护 | 服主，游戏里 `/cbl onebot exec` 改 | 机器人管理员，改 JSON |

远程执行跑在 `CaiBotPlayer`（固定超管身份）上，TShock 自身的权限检查对它无效，
所以**服务端那份白名单是唯一拦得住的地方**。机器人侧那份决定「群里谁可以发什么」，
纯粹是聊天层的策略。

> 为了不出现两份互相打架的名单，机器人**不自己抄**服务端的白名单。
> `plugin_list` 的响应里带回了 `allowed_commands`，机器人：
> 1. 在 `/泰拉 帮助` 里如实显示 `服务器当前允许远程执行: ...`；
> 2. 每次读到新名单都检查一遍——`commands.json` 里公开了服务端并不允许的指令时，
>    启动日志会直接警告，让你改其中一边，而不是让用户撞上一句「未被允许执行」。
>
> 所以**改白名单只改 TShock 端那一处**，机器人下次拉到名单就同步了。
> 想扩大普通人的范围，改 `commands.json` 的 `开放指令`，但它必须是服务端名单的子集。

### 改指令表

编辑 `commands.json`(或用 `CAIBOTLITE__COMMANDS_PATH` 指向你自己的副本):

```json
"进度": {
  "用法": "/泰拉 进度",             // 帮助里显示的写法
  "说明": "世界进度(已击败的 BOSS)", // 帮助里显示的说明
  "权限": "user",                  // user = 出现在帮助里
                                   // botadmin = 只有机器人管理员能用, 不显示
  "包": "progress",                // 发给 TShock 的数据包类型, 留空=本地处理(帮助)
  "参数": "",                      // 必填参数的名称, 留空=不需要参数
  "别名": ["查进度", "世界进度"],     // 同样能触发的写法
  "参数映射": { "金币": "货币" },     // 参数别名 -> 发给服务器的值
  "详情": ["多出来的一行说明"],       // 帮助里该指令下面的补充说明
  "开放指令": []                    // 见下
}
```

- `"权限": "user"` 的指令会**自动出现在 `/泰拉 帮助` 里**;
- `开放指令` 给公开指令加一层限制: 只放行名单里的第一个参数, 名单外的按
  `botadmin` 处理。`/泰拉 执行` 就靠它做到"普通人能调时间天气, 别的指令要管理员"。
  这份名单**会自动显示在帮助里**, 不会和代码脱节。
  它必须是服务端 `允许远程执行的指令` 的子集, 否则启动日志会警告——
  **要放行新指令请改 TShock 端, 不要只改这里**;
- 删掉一条指令, 对应说法就变成"未知指令";
- 下划线开头的键(`_说明`)当注释, 会被忽略。

### 已下线的功能

以下功能**两端都已经移除**, 服务端不再处理对应数据包, 群里发这些指令
只会得到"未知指令":

| 功能 | 原因 |
| --- | --- |
| 白名单管理(`添加白名单` `移除白名单` `白名单`) | 不需要 |
| 群黑名单(`拉黑` `解除拉黑`) | 不需要 |
| 设备授权(`登录`) | 登录不再经过机器人 |
| 群商店(`购买`) | 依赖 Economics 插件, 服务器上没装 |
| 自踢(`踢出`) | 不需要 |
| 角色绑定(`角色`) | 背包谁都能查, 不需要绑定 |

> **玩家登录已经完全不受机器人影响。** 服务端不再拦截 `ClientUUID` 数据包,
> 也不再有"机器人未连接就进不来"的情况——机器人挂掉只会让查询指令失效,
> 不会挡住任何人进游戏。登录一律走 TShock 自己的 `/login` `/register` 和 SSC。
> 旧的 `CaiBotLite.json` 里的 `白名单开关` 会被忽略, 并在下一次写配置时移除。

所有指令在群里和私聊里都能用, **回复只发到你发指令的那个会话**: 群里发就只回群,
私聊发就只回私聊, 不会两边都发。

### 私聊里怎么用

私聊里没有"群管理"这个身份, 所以:

- 把你自己的 QQ 填进 `CAIBOTLITE__ADMINS`(或 `CAIBOTLITE__SUPERUSERS`),
  之后私聊机器人就能用管理指令, **回复也只落在私聊里**, 不会打扰群;
- 没填的话, 私聊只能执行查询类指令(进度 / 在线 / 查背包 / 排行 / 帮助),
  管理类指令会提示需要配置 `ADMINS`;
- 私聊回复**不会**回退到群里。

## 开发与自测

```bash
python tests/test_bridge.py          # 桥接协议 + 渲染, 用模拟的 TShock 端跑通(含 server 模式与密钥校验)
python tests/test_commands.py        # 指令流程: 构造真实 OneBot v11 事件, 跑通权限/透传/查询
python tests/test_help.py            # /泰拉 帮助 的内容: 该有的有, 已下线的别有
python tests/test_config_fallback.py # 缺 pydantic-settings 时仍能加载并读对配置
python tests/dump_packages.py        # 导出实际发出的报文, 供 C# 端解析器校验
```

## 协议

桥接协议非常简单, 通道上流动的每一行都是一个信封:

```json
{"v": 1, "secret": "密钥", "package": "{\"version\":\"2025.7.18\",\"direction\":\"to_server\",\"type\":\"progress\",\"is_request\":true,\"request_id\":\"...\",\"payload\":{\"__bridge\":{\"group_id\":123,\"user_id\":456}}}"}
```

`package` 就是 TShock 端使用的原始 CaiBotLite 数据包, 业务字段与官方机器人的
协议完全一致, 因此本插件可以与官方 CaiBot 机器人共用同一个 TShock 端插件。

## 许可证

MIT

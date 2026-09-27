# CaiBotLitePlugin-nonebot

让 **TShock 服务器**和 **QQ 机器人**直接对话：群里发一条指令就能查世界进度、看在线玩家、
翻别人背包、拉排行榜，还能改时间、造天气。

```
QQ 群  ──OneBot v11──▶  nonebot 插件  ──WebSocket──▶  TShock 插件  ──▶  你的服务器
         (Lagrange / NapCat / LLOneBot)              (CaiBotLite.dll)
```

- **不经过官方 CaiBot 机器人**，数据全在自己手上，机器人实现随便换。
- **玩家登录完全不经过机器人**，进服走 TShock 自己的 `/login` `/register` 和 SSC。
  机器人挂掉只会让查询指令失效，**不会挡住任何人进游戏**。
- **不复用游戏端口，也不走 TShock REST API**，单独用 17779 端口，跨网络也能配通。

| 目录 | 内容 |
| --- | --- |
| [`tshock/`](tshock/) | TShock 端插件源码（C#，net9.0 / TShock 6.1） |
| [`release/`](release/) | 编译好的 `CaiBotLite.dll` + `linq2db.dll`，直接丢进 `ServerPlugins` 就能用 |
| [`nonebot/`](nonebot/) | NoneBot 端插件源码（Python） |

---

## 快速开始

### 1. TShock 端

把 [`release/`](release/) 里的 **两个** DLL 放进服务器的 `tshock/ServerPlugins/`：

```
CaiBotLite.dll
linq2db.dll        ← 必须一起, CaiBotLite 启动时要加载它
```

> ⚠️ **只放这两个。** 本仓库已经去掉了对 `BossLock` / `Economics.*` / `GenerateMap` /
> `ProgressControls` 的编译期依赖（改成运行期探测），所以编译产物里不会再混进
> `SixLabors.ImageSharp.dll`、`Jint.dll`、`Acornima.dll` 之类的东西。
> 就算你从上游仓库直接编译，**也绝不要**把那堆 DLL 一起复制进 `ServerPlugins`，
> 会让 TShock 启动即崩（`MissingFieldException: Terraria.Netplay.Disconnect`）。

装完重启服务器，进游戏执行 `/cbl onebot` 查看和修改配置：

```json5
{
  "OneBot": {
    "机器人连接方式": "onebot",        // 必须改成 onebot, 默认是官方机器人
    "OneBot通道": "auto",              // auto = 监听+主动连, 先通先用
    "本服监听地址": "http://0.0.0.0:17779/",
    "通讯密钥": "改成你自己的随机串",     // 机器人侧要填一样的
    "允许远程执行的指令": ["time", "clear", "wind", "worldevent", "version", "motd"]
  }
}
```

### 2. 机器人端

装一个 OneBot v11 实现（Lagrange.Core / NapCat / LLOneBot / go-cqhttp 都行），
然后把 [`nonebot/`](nonebot/) 装进你的 NoneBot：

```bash
nb plugin install nonebot-plugin-caibotlite
# 或直接把 nonebot/ 拷进 src/plugins/，再在 pyproject.toml 里声明
```

依赖（**这两个一定要装**，否则插件静默不工作）：

```bash
pip install pydantic-settings websockets
```

`.env` 里至少填这两项：

```dotenv
CAIBOTLITE__SECRET=改成和 TShock 端一样的密钥
CAIBOTLITE__MODE=auto
CAIBOTLITE__ADMINS=[你的QQ]      # 机器人管理员, 见下面的"权限"
```

### 3. 连通性

桥接是**双向**的，只要有一端有公网地址就能通。`auto` 模式下两边都会试，先连上的生效：

| 谁能被访问到 | TShock 端 `OneBot通道` | 机器人端 `MODE` | 谁去连谁 |
| --- | --- | --- | --- |
| **TShock** 有公网 IP/域名 | `server`（监听 17779） | `client`（主动连） | 机器人 → TShock |
| **机器人** 有公网 IP/域名 | `client`（主动连） | `server`（监听 8080） | TShock → 机器人 |
| 不确定 / 两边都行 | `auto` | `auto` | 两条都试，先通的生效 |

两端都没公网地址的话，任选一端做内网穿透（frp / tailscale / cloudflared），
恢复其中一条链路后按上表配即可。

---

## 指令

**所有指令都必须写成 `/泰拉 xxx`**：去掉 `/` 之后第一个词必须是根指令名
`泰拉`（或简写 `tl`）。`/进度` 这种少写根指令名的一律不认，也不会回复——
避免把普通聊天误当指令刷屏。

指令表在 [`nonebot/src/nonebot_plugin_caibotlite/commands.json`](nonebot/src/nonebot_plugin_caibotlite/commands.json)
里，**没有写死在代码中**，改 JSON 即生效。`/泰拉 帮助` 的内容也完全由它生成。

### 对所有人开放（会出现在帮助里）

| 指令 | 说明 |
| --- | --- |
| `/泰拉 帮助` | 指令列表 |
| `/泰拉 进度` | 世界进度、已击败 BOSS、进度锁 |
| `/泰拉 在线` | 在线人数与玩家列表 |
| `/泰拉 排行 <类型> [参数]` | `boss 史莱姆王`、`死亡`、`在线`、`钓鱼`、`货币 金币` |
| `/泰拉 查背包 <角色名>` | 背包 / 装备 / 存钱罐 / 状态 / 永久强化 / 经济，谁都能查 |
| `/泰拉 插件列表` | 服务器插件列表 |
| `/泰拉 执行 time` | 改时间（不接参数则查看当前时间） |
| `/泰拉 执行 clear` | 停雨停风 |
| `/泰拉 执行 wind <风速>` | 调风力 |
| `/泰拉 执行 worldevent <事件>` | 造天气，如 `rain` `meteor` `bloodmoon`；不带参数服务器会自己列 |

### 需要机器人管理员

`.env` 里 `CAIBOTLITE__ADMINS` / `CAIBOTLITE__SUPERUSERS` 中的 QQ 才能用。
**群管理身份不算，群的 owner / admin 也一样会被挡住。**
权限不足时机器人只回两个字 `无权限`——不解释原因，也不告诉对方怎么绕过。

| 指令 | 说明 |
| --- | --- |
| `/泰拉 执行 <其它指令>` | 除上面四个以外的服务器指令 |
| `/泰拉 上传地图` | 生成并发送地图图片（需服务器装 `GenerateMap`） |
| `/泰拉 下载地图` | 发送 `.map` 文件（需 `GenerateMap`） |
| `/泰拉 下载存档` | 发送 `.wld` 存档（需 `GenerateMap`） |

---

## 鉴权在哪一边

**只有 TShock 端是安全边界。** 这不是风格问题，是结构决定的：

远程执行跑在 `CaiBotPlayer`（**固定超管身份**）上，TShock 自身的权限检查对它完全无效。
所以服务端那份白名单是唯一拦得住东西的地方：

```csharp
// tshock/Common/CaiBotApi.cs
if (!Config.Settings.OneBot.IsRemoteCommandAllowed(command))   // ← 真正的安全边界
```

| | TShock 端（`CaiBotLite.json`） | NoneBot 端（`commands.json`） |
| --- | --- | --- |
| 存什么 | `允许远程执行的指令` | 谁能发什么、帮助文案、别名 |
| **安全边界** | ✅ **就是这里** | ❌ **不是** |
| 谁维护 | 服主，游戏里 `/cbl onebot exec` 改 | 机器人管理员，改 JSON |

NoneBot 侧那份**拦不住任何人**：拿到 `通讯密钥` 的人可以直接连 TShock 的 17779 端口发包，
完整绕过机器人。它只决定"群里谁可以发什么"，纯粹是聊天层的策略。

### 两份名单不会打架

曾经 `commands.json` 里抄了一份服务端的白名单，两份必然会漂移。现在没有了：

- `plugin_list` 的响应里带回了 `allowed_commands`，机器人读它；
- `/泰拉 帮助` 里如实显示 `服务器当前允许远程执行: ...`；
- 每次读到新名单都会校验一遍——`commands.json` 公开了服务端并不允许的指令时，
  **启动日志直接警告**，而不是让用户撞上一句莫名其妙的「未被允许执行」。

```powershell
[WARN] caibotlite | 指令表的『执行』把 wind 列在开放指令里，
                     但服务端(CaiBotLite.json -> 允许远程执行的指令)没有放行它,
                     普通人发这条会收到『未被允许执行』。请改其中一边。
```

**所以要放行新指令，只改 TShock 端一处**——游戏里 `/cbl onebot exec + <指令>`，
机器人下次拉到名单就同步了。`开放指令` 只是"公开子集"的选择器，必须是服务端名单的子集。

---

## 目录结构

```
.
├── README.md            ← 你正在看的
├── LICENSE              GPL-3.0（上游 TShockPlugin 同样是 GPL-3.0）
├── release/             编译产物，复制到 ServerPlugins/ 即可
│   ├── CaiBotLite.dll
│   └── linq2db.dll
├── tshock/              TShock 插件源码
│   ├── CaiBotLite.csproj
│   ├── template.targets
│   ├── CaiBotLite.cs    插件入口、/cbl 指令
│   ├── Config.cs        CaiBotLite.json
│   ├── Common/          协议处理、第三方插件集成
│   ├── OneBot/          OneBot v11 通道（WS / HTTP / 双向）
│   ├── Models/          数据包与虚拟玩家
│   ├── Enums/           数据包类型枚举
│   └── Shared/          上游共用的 I18n
└── nonebot/             NoneBot 插件源码
    ├── pyproject.toml
    ├── .env.example
    ├── src/nonebot_plugin_caibotlite/
    │   ├── commands.json   ← 指令表（改这里调整指令和权限）
    │   ├── commands.py     ← 指令表加载与权限判定
    │   ├── bridge.py       ← WebSocket / HTTP 桥接
    │   ├── plugin.py       ← 消息处理
    │   ├── render.py       ← 渲染
    │   └── config.py
    ├── tests/           5 套自测，不用连真服务器
    └── tools/           probe_server.py / ask_server.py
```

---

## 从源码编译

### TShock 插件

需要 [.NET 9 SDK](https://dotnet.microsoft.com/download/dotnet/9.0)：

```bash
cd tshock
dotnet build -c Release
# 产物在 tshock/bin/Release/，只需要 CaiBotLite.dll 和 linq2db.dll
```

本仓库是**独立精简版**：上游 `TShockPlugin` 里本项目还 `ProjectReference` 了
`BossLock` / `Economics.*` / `GenerateMap` / `ProgressControls` 来引用它们的类型，
这里全部改成了**运行期探测**（`Common/BossLockSupport.cs`、`Common/MapGeneratorSupport.cs` 等），
没装对应插件时自动降级。所以不会有人因为没装 Economics 就得把它的一堆 DLL 拖进 `ServerPlugins`。

### NoneBot 插件

```bash
cd nonebot
python -m compileall -q src tests tools

python tests/test_bridge.py          # 桥接协议 + 渲染，含 server 模式与密钥校验
python tests/test_commands.py        # 指令流程：权限 / 透传 / 查询
python tests/test_help.py            # 指令表加载与帮助生成
python tests/test_config_fallback.py # 缺 pydantic-settings 时仍能加载
python tests/test_render.py          # 渲染
```

`tools/probe_server.py` 可以对着真实服务器跑一遍连通性检查：

```bash
python tools/probe_server.py --host <TShock地址> --secret <通讯密钥> --skip-map
```

---

## 已下线 / 不再提供

| 功能 | 原因 |
| --- | --- |
| 白名单管理、群黑名单 | 不需要 |
| 设备授权（`/泰拉 登录`） | 登录不再经过机器人 |
| 群商店（`/泰拉 购买`） | 依赖 Economics 插件 |
| 自踢（`/泰拉 踢出`） | 不需要 |
| 角色绑定（`/泰拉 角色`） | 背包谁都能查 |

对应的数据包处理（`whitelist` / `self_kick` / `shop_buy` / `shop_condition`）
已从 TShock 端删除，收到的包直接忽略。

---

## 致谢与许可

TShock 端插件基于 [**UnrealMultiple/TShockPlugin**](https://github.com/UnrealMultiple/TShockPlugin)
的 `CaiBotLite` 改写，遵循 **GPL-3.0** 协议（本仓库同样以 GPL-3.0 发布）。
OneBot v11 协议来自 [OneBot 标准](https://github.com/botuniverse/onebot-11)。

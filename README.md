# CaiBotLitePlugin (TShock 端)

让 **TShock 服务器**和 **QQ 机器人**直接对话：群里发一条指令就能查世界进度、看在线玩家、
翻别人背包、拉排行榜，还能改时间、造天气。本仓库只包含 **TShock 端插件**；
QQ 机器人端（NoneBot 插件）已拆分到独立仓库。

```
QQ 群  ──OneBot v11──▶  NoneBot 插件  ──WebSocket──▶  TShock 插件  ──▶  你的服务器
         (Lagrange / NapCat / LLOneBot)              (CaiBotLite.dll)
        ↳ https://github.com/zlqd123/CaiBotLite-NoneBot
```

- **不经过官方 CaiBot 机器人**，数据全在自己手上，机器人实现随便换。
- **玩家登录完全不经过机器人**，进服走 TShock 自己的 `/login` `/register` 和 SSC。
  机器人挂掉只会让查询指令失效，**不会挡住任何人进游戏**。
- **不复用游戏端口，也不走 TShock REST API**，单独用 17779 端口，跨网络也能配通。

| 仓库 / 目录 | 内容 |
| --- | --- |
| [`tshock/`](tshock/) | TShock 端插件源码（C#，net9.0 / TShock 6.1） |
| [`release/`](release/) | 编译好的 `CaiBotLite.dll` + `linq2db.dll`，直接丢进 `ServerPlugins` 就能用 |
| [**CaiBotLite-NoneBot**](https://github.com/zlqd123/CaiBotLite-NoneBot) | **QQ 机器人端（NoneBot 插件），指令、配置、图片渲染都在那边** |
| [UnrealMultiple/TShockPlugin](https://github.com/UnrealMultiple/TShockPlugin) | 上游官方 TShock 插件仓库（本项目由其 `CaiBotLite` 改写） |
| [UnrealMultiple/CaiBotLite](https://github.com/UnrealMultiple/CaiBotLite) | 上游官方 QQ 机器人参考实现（Python） |

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

见 **[CaiBotLite-NoneBot](https://github.com/zlqd123/CaiBotLite-NoneBot)**：
安装步骤、全部 `CAIBOTLITE__*` 配置项、`/泰拉` 指令表、查背包图片渲染都在它的 README 里。

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

机器人群聊指令（`/泰拉 进度`、`/泰拉 查背包 <角色名>`、`/泰拉 执行 time` 等）
由机器人端插件提供，**完整指令表见
[CaiBotLite-NoneBot 的 README](https://github.com/zlqd123/CaiBotLite-NoneBot#指令)**。

---

## 鉴权在哪一边

**只有 TShock 端是安全边界。** 这不是风格问题，是结构决定的：

远程执行跑在 `CaiBotPlayer`（**固定超管身份**）上，TShock 自身的权限检查对它完全无效。
所以服务端那份白名单是唯一拦得住东西的地方：

```csharp
// tshock/Common/CaiBotApi.cs
if (!Config.Settings.OneBot.IsRemoteCommandAllowed(command))   // ← 真正的安全边界
```

| | TShock 端（`CaiBotLite.json`） | 机器人端（`commands.json`） |
| --- | --- | --- |
| 存什么 | `允许远程执行的指令` | 谁能发什么、帮助文案、别名 |
| **安全边界** | ✅ **就是这里** | ❌ **不是** |
| 谁维护 | 服主，游戏里 `/cbl onebot exec` 改 | 机器人管理员，改 JSON |

机器人侧那份**拦不住任何人**：拿到 `通讯密钥` 的人可以直接连 TShock 的 17779 端口发包，
完整绕过机器人。它只决定"群里谁可以发什么"，纯粹是聊天层的策略。

**所以要放行新指令，只改 TShock 端一处**——游戏里 `/cbl onebot exec + <指令>`，
机器人下次拉到名单（`plugin_list` 响应会带回 `allowed_commands`）就同步了。

---

## 目录结构

```
.
├── README.md            ← 你正在看的
├── LICENSE              GPL-3.0（上游 TShockPlugin 同样是 GPL-3.0）
├── release/             编译产物，复制到 ServerPlugins/ 即可
│   ├── CaiBotLite.dll
│   └── linq2db.dll
└── tshock/              TShock 插件源码
    ├── CaiBotLite.csproj
    ├── template.targets
    ├── CaiBotLite.cs    插件入口、/cbl 指令
    ├── Config.cs        CaiBotLite.json
    ├── Common/          协议处理、第三方插件集成
    ├── OneBot/          OneBot v11 通道（WS / HTTP / 双向）
    ├── Models/          数据包与虚拟玩家
    ├── Enums/           数据包类型枚举
    └── Shared/          上游共用的 I18n
```

---

## 从源码编译

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
QQ 机器人端插件见 [CaiBotLite-NoneBot](https://github.com/zlqd123/CaiBotLite-NoneBot)。

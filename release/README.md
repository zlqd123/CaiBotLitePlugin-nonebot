# release

编译好的 TShock 插件。**把这两个文件复制到服务器的 `tshock/ServerPlugins/` 目录，然后重启服务器。**

| 文件 | 说明 |
| --- | --- |
| `CaiBotLite.dll` | 插件本体 |
| `linq2db.dll` | 依赖库。**必须一起复制**，缺了插件启动时找不到它 |

游戏里执行 `/cbl onebot` 查看和修改配置。

## ⚠️ 不要复制其它 DLL

这个目录**只有**上面两个文件，是故意的。

如果你从上游 `UnrealMultiple/TShockPlugin` 仓库自己编译，产物目录里会混进
`GenerateMap.dll`、`Economics.*.dll`、`ProgressControls.dll`、`BossLock.dll`、
`SixLabors.ImageSharp.dll`、`Jint.dll`、`Acornima.dll` 等。

**把它们一起复制进 `ServerPlugins/` 会让 TShock 启动即崩**，典型报错：

```
MissingFieldException: 'Terraria.Netplay.Disconnect'
```

只复制 `CaiBotLite.dll` 和 `linq2db.dll` 就够了。需要地图/经济等功能时，
去 TShock 插件市场单独装对应插件即可——本插件会在运行期自动探测它们。

## 从源码重新编译

```bash
cd ../tshock
dotnet build -c Release
```

需要 [.NET 9 SDK](https://dotnet.microsoft.com/download/dotnet/9.0)。

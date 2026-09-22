# TShock AntiCheat

面向 Terraria **1.4.5.8** 的 TShock 服务端反作弊插件源码。

**项目状态：`paused_by_user`。** 这里公开的是开发源码，不是可直接部署的发行版。默认模式为 `ObserveOnly`；历史测试结果不能自动证明新构建可用于生产服。当前范围见 [M18 后总范围](anticheat-scope-handoff/AntiCheat-M18-PostScope-Master.md)，原任务的活动字段见 [台账](docs/m3-target-ledger.json)。

当前保留的有限任务包括普通 Butcher 高低伤、地图刷子、部分越权消息、9999 拿取与掉落物、简单锁血和少数召唤及物品规则。Windows/OS 防火墙、REST、旧版本兼容和 PvP 专项已退出当前范围；完整伤害、物品来源、液体电路与回滚模型等已冻结。源码中仍有旧阶段实现，不能把它们视为当前开发目标或生产资格。

## 构建

需要可构建 `net9.0` 的 .NET SDK，以及自行取得并审计过的 Terraria 1.4.5.8 对应 TShock/TSAPI/OTAPI 运行包。本仓库不分发这些程序集。运行包目录须包含 `ServerPlugins/TShockAPI.dll`、`bin/TerrariaServer.dll` 和 `bin/OTAPI.dll`。

```powershell
dotnet build src/AntiCheat.Plugin.TShock/AntiCheat.Plugin.TShock.csproj -c Release -p:TShockBaselineRoot="C:\path\to\audited-server"
```

独立 Core 测试可从 `tests/AntiCheat.Core.Tests` 运行。完整适配器测试还需要匹配的外部 TShock 源码补丁和隔离运行环境；本次开源整理未运行构建或测试。构建前应核对实际加载的运行时版本和程序集哈希。

本仓库只收录源码、测试、必要数据和少量范围记录。本地实验世界、数据库、日志、构建产物、原始证据及第三方工具副本均保留在本机，不属于公开源码。

## 许可

项目自有代码按 [GPL-3.0-or-later](LICENSE) 发布。改编数据与测试夹具涉及的第三方许可见 [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md)。

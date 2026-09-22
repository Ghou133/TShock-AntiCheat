# 构建与测试

[首页](../README.md) · [配置](CONFIGURATION.md) · [已知问题](REVIEW-2026-09.md)

## 两种环境，不能混用结论

| 环境 | 需要什么 | 能验证什么 |
| --- | --- | --- |
| 公开源码测试 | .NET 9 SDK、Python 3.11+、NuGet 访问 | Core / Rules / Persistence / Progression / RuleExtraction 与文档检查 |
| 原生插件与客户端测试 | 上述工具，加匹配的外部服务器运行包、补丁和隔离世界 | 实际程序集接口、原生 Hook、插件行为及客户端场景 |

`global.json` 从 9.0.300 起在 .NET 9 内选择已安装的较新功能带，不会自动选择 .NET 10。SDK 选择不等于宿主 CLR 资格。规则提取工具直接引用 SDK 的 Roslyn，不能任由机器上的最高 SDK 改变该依赖。

## 公开测试

在仓库根目录运行：

```powershell
dotnet --info
dotnet restore AntiCheat.Public.slnf --locked-mode
dotnet test AntiCheat.Public.slnf -c Release --no-restore
python tools/repository-checks/check_repository.py
```

定向测试示例：

```powershell
dotnet test tests/AntiCheat.Rules.Tests/AntiCheat.Rules.Tests.csproj `
  -c Release --filter FullyQualifiedName~ConfigurationBoundaryTests
```

锁文件发生变化时应单独审查，不要通过关闭 locked-mode 隐藏依赖漂移。Windows 命名管道及部分链接测试具有平台条件，跳过不是通过。`AntiCheat.DevMcp.Tests` 与 `AntiCheat.Adapter.Tests` 不在公共过滤器内，不把它们计入公共 CI 的成功范围。

## 原生插件的真实前提

当前 `TargetRuntime` 的接纳条件：Terraria `1.4.5.8`、协议 `326`、Windows x64、CLR `9.0.7`，并校验源码记录的程序集哈希与加载证据。**任意官方或自行编译的 1.4.5.8 运行包不自动匹配。** 原生接线还涉及外部源码补丁；公共仓库目前缺少完整的再现流程。

这是开发候选锁，不建议为了满足它而在公网长期使用过期补丁。更新 CLR、TShock、TSAPI 或 OTAPI 时必须先重建兼容性和规则验证，再更新锁定值。R1-01/R1-02 负责解决这一问题，不能简单把 `Verified` 改成 true。

编译引用所需布局：

```text
audited-server/
  ServerPlugins/TShockAPI.dll
  bin/
    TerrariaServer.dll
    OTAPI.dll
    HttpServer.dll
    ModFramework.dll
```

这是工程引用布局，不是完整运行包清单。服务器启动器及其其他依赖应来自同一套已核验运行包，不能按上述五个文件拼出一个新服务器。

```powershell
dotnet build src/AntiCheat.Plugin.TShock/AntiCheat.Plugin.TShock.csproj `
  -c Release -p:TShockBaselineRoot="C:\path\to\audited-server"
```

工程当前仍有本机实验目录的默认路径，外部开发请显式传入参数。编译引用检查只覆盖部分文件存在性，并不授予运行资格；缺失的依赖检查属于 R0-03。

## 构建产物与数据路径

默认 Release 输出位于 `src/AntiCheat.Plugin.TShock/bin/Release/net9.0/`。核心插件是 `AntiCheat.Plugin.TShock.dll`，其项目依赖包括 Core、Rules、Progression 和 Persistence。原生服务器依赖标记为不复制，不能从插件产物中取得完整 TShock 运行包。

只在服务器隔离副本中按照所用 TSAPI 的插件加载布局放置插件及依赖，不覆盖服务器原有程序集。当前没有经本轮验证的自动打包/部署脚本；不要将所有 DLL 不加区分地覆盖进 `ServerPlugins`。

运行代码从 `AppContext.BaseDirectory/data/progression/` 查找进度数据，**不是从插件 DLL 所在目录查找**。至少检查 `candidates.json` 与 `entity-candidates.json`；其他有限策略所需数据见对应源码。目录缺失或数据不匹配可能导致部分功能降级，不能把加载完成当作全部策略已启用。

## 原生测试与验收

Adapter 测试的工程配置及外部补丁必须对应运行包。先核对 `tests/AntiCheat.Adapter.Tests/AntiCheat.Adapter.Tests.csproj` 的引用，再在拥有匹配依赖的环境中运行：

```powershell
dotnet test tests/AntiCheat.Adapter.Tests/AntiCheat.Adapter.Tests.csproj `
  -c Release -p:TShockBaselineRoot="C:\path\to\audited-server"
```

此命令只是测试入口，不保证单个参数解决所有外部源码路径。已有历史记录指出部分测试缺少源码/数据目录，并有原生故障用例需要隔离进程；不要直接在真实游戏服或不受控进程中运行整套原生实验。先完成 R1 的依赖与故障用例清单。

记录源码 HEAD、工作树状态、插件 SHA256、服务器依赖哈希、CLR、配置、测试账号和合成场景。分别报告规则回归、原生接线、客户端正反例、处罚落盘、重连与重启结果。不得拿中间构建的结果给最终 DLL 背书。

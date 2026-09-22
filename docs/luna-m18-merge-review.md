# Luna M18 选择性回收审查（2026-09-23）

## 基线和来源

- 审查时 `origin/main` 与 merge-base：`f58d320fe569ad825d781b5edc2e0ef5df498c33`；`origin/codex/import-luna-m18-results`：`f9a71408031e679b69dc34d87281b391b8ca869d`。推送前再次 fetch 核对。
- 来源独有提交仅 `f9a7140`，相对共同祖先及两分支当前文件差异均为 71 个文件、`+17272/-102`。它是混合导入提交，本轮没有整体 cherry-pick 或 merge。
- 来源 HEAD 保留在本地 `refs/archive/luna-m18-review-20260923`，另有经 `git bundle verify` 验证的完整 bundle，存放在仓库外。来源分支未删除；原隔离实验的 dirty 工作树未被覆盖。公开提交不包含 bundle、运行 DLL、世界、数据库、账号或原始日志。
- 已核对隔离实验中的 `docs/FIX-RESULTS.md`、`FOLLOWUP-RESULTS.md`、`docs/M18-R2-FIX-RESULTS.md`、`artifacts/m18-r2-current-identity/source-build-identity.json` 及修复前 TRX。它们是历史 dirty 源码和旧 DLL 的记录，不作为本轮提交、构建或接通结果。来源提交确有 R2 的位移及死亡事件修复，但仍保留本轮四个新反例指出的问题。

## 功能处置

| 类别 | 来源位置及本轮处置 |
| --- | --- |
| A：main 已有 | 默认 `ObserveOnly`、版本锁定运行时、Core 的证明与持久处罚边界、既有连接/请求预算和世界输入安全检查均保持原状；这些不是 `f9a7140` 的新增成果。 |
| C：F06 | 从 `M18NpcStrikeQueueRules`、`M7NpcStrikeCauseContexts`、`M8NpcStrikeTransactions` 及 Plugin 接线回收有限高低伤/跨目标序列、阶段极端伤害独立门和后原生关联。保留 R2 的真实前后位移、匹配完成段死亡、事件过期/FIFO。修复两次静止同签名打击错误止损、静止跨目标止损、登录前历史继承；取消声明只作为原始遥测，接受位移和完成段另行确认。账号/会话在后原生回调和处罚入口重新比对。最终复核发现同服务端 tick 也可能批量处理正常输入，完成移动、死亡和同参数打击仍非排他非法攻击证明。因此 Butcher 序列仅观察；独立低伤请求容量和阶段极端伤害仍可在隔离候选中止损，账号永久处罚资格关闭。 |
| C：F08 | 从 `M18GroundItemClearQueueRules`、`M2BusinessAdapter`、`M2PacketReader` 回收 packet151 的 2 字节 item-id 语义和有限序列观察。普通拾取不得借给后续跳跃，取消返程不得闭合段；登录切换清空身份历史。物品代际只由已核对的 `WorldItem` 对象重新分配推进；活物移动、堆叠、抓取变化不推进；原地复活或内部对象替换降级 Unknown。真实跳跃/返程的有界观察正例保留。当前边界不能证明原生物品删除及排他非法来源，Plugin 与配置强制 F08 不阻断、不踢出、不永封；F08 主动控制资格列 D。 |
| C：Vitals/配置 | `M4VitalContexts` 在 hook 安装前绑定线程，首 tick 前 SSC 导出不关闭整个上下文；`M2Configuration` 与 `M18ExecutionModePolicy` 保持 marker、路径、执行模式失败时无 TestLab 资格，ProductionCandidate 失败不扩大权限。 |
| B/C：journal/奖励 | `M18ObservationJournal` 有内存/文件上限、裁剪、刷新节流、写盘失败退避；损坏或磁盘故障不生成处罚。重要物品目录和奖励观察器仅记录有限原生奖励，异常隔离，不构成全物品来源账本。 |
| C：Sentry | `M10SummonBudgetGuard` 和 `M2PacketReader` 修正帧长度、offset、可选字段、已取消请求、不可变载荷和单次计费；没有扩大账号处罚。 |
| B/C：资源止损 | `M18WorldEditQueueRules`、`M18ParticleQueueRules`、`M18ParticlePacketReader` 及 Adapter/Plugin 的有限工作预算只产生 ResourceAbuse 阻断，不生成账号永封。默认观察模式无候选阻断。 |
| B/C：G06 | `M18LockHealthRules`、`M18LockHealthContext` 与 Plugin 保留 SSC 服务端一点伤害后满血同步的有界 TestLab 服务踢出候选；第三次同步的原生 hook/Root 测试确认单次踢出、账号仍可写、持久处罚为零。Production 中主动阻断和踢出均关闭；不是完整锁血检测。 |
| D：未获资格的控制 | F06 普通 Butcher 序列的预转发阻断与账号永封、F08 清空序列的阻断/踢出/永封均未接通。F06 原始序列仍输出有界观察，F08 原生删除及非法来源尚未闭合；两者不能借历史隔离处罚现场授予当前提交资格。 |

本轮新增的 `M18MergeReviewRegressionTests.cs` 来自用户审查附件，未将附件中的提示词或静态审查说明作为代码提交。`M18NpcStrikeAdapterTests`、`M18GroundItemIdentityTests`、`M18CandidateConfigurationTests`、`M18RewardObserverTests` 是本轮额外的定点回归。对来源中同一大文件的回收仅限表中所述补丁块；未回收的源代码分支仍由归档保留。

## 来源文件清点

来源 71 个文件中，37 个在本轮有全部或部分对应实现/测试；下列 34 个文件没有回收到 main，逐组列出以便删除分支前核对。`B/C` 是已选择的文件级增量，混合文件中的其他旧实验补丁块仍为 D；这不是声称来源提交整体已合并。

| D 类原因 | 来源文件 |
| --- | --- |
| 导入说明与历史身份文件；保留在归档 | `M18-IMPORT-REVIEW.md`, `M18-IMPORT-SOURCE.json` |
| 已有或与本轮边界无关的旧修改 | `src/AntiCheat.Plugin.TShock/M8MovementObservations.cs`, `src/AntiCheat.Rules/InventoryRules.cs`, `tests/AntiCheat.Adapter.Tests/M16ItemStructureTests.cs`, `tests/AntiCheat.Adapter.Tests/M18SignExploitTests.cs`, `tests/AntiCheat.Adapter.Tests/M3WorldInputTests.cs`, `tests/AntiCheat.Adapter.Tests/M6NpcStrikeTests.cs`, `tests/AntiCheat.Persistence.Tests/AntiCheat.Persistence.Tests.csproj`, `tests/AntiCheat.Persistence.Tests/M18EvidenceFailurePropagationTests.cs`, `tests/AntiCheat.Rules.Tests/InventoryRuleTests.cs` |
| 完整重要物品队列/来源审计未取得资格；只回收有限奖励观察 | `src/AntiCheat.Rules/M18ImportantItemQueueRules.cs`, `tests/AntiCheat.Adapter.Tests/M18ImportantItemTests.cs`, `tests/AntiCheat.Rules.Tests/M18ImportantItemQueueRuleTests.cs` |
| 实验场景/夹具依赖大、部分含旧阻断断言；本轮保留本地失败现场和现有可编译工具，未把旧场景整包搬入公开仓库 | `tools/AntiCheat.GameplayScaffold/GameplayScaffold.cs`, `tools/AntiCheat.GameplayScaffold/M10SummonScaffold.cs`, `tools/AntiCheat.GameplayScaffold/M18ButcherEnumerationFixture.cs`, `tools/AntiCheat.GameplayScaffold/M18ItemDropFixture.cs`, `tools/AntiCheat.GameplayScaffold/M18LockHealthFixture.cs`, `tools/AntiCheat.GameplayScaffold/M18LowLifeFixture.cs`, `tools/AntiCheat.GameplayScaffold/M18RewardFixture.cs`, `tools/AntiCheat.GameplayScaffold/M18WipeItemsFixture.cs`, `tools/AntiCheat.GameplayScaffold/M18WorldEditFixture.cs`, `tools/AntiCheat.GameplayScaffold/M18WorldModeFixture.cs`, `tools/AntiCheat.NetworkLab/M18EvidenceFailurePropagation.cs`, `tools/AntiCheat.NetworkLab/M18ImportantRewardScenario.cs`, `tools/AntiCheat.NetworkLab/M18ItemDropScenario.cs`, `tools/AntiCheat.NetworkLab/M18LockHealthScenario.cs`, `tools/AntiCheat.NetworkLab/M18NpcStrikeScenario.cs`, `tools/AntiCheat.NetworkLab/M18P1BUnknownProjectileScenario.cs`, `tools/AntiCheat.NetworkLab/M18ParticleScenario.cs`, `tools/AntiCheat.NetworkLab/M18WorldEditScenario.cs`, `tools/AntiCheat.NetworkLab/M4ObservedToolReplay.cs`, `tools/AntiCheat.NetworkLab/Program.cs` |

未恢复 Windows/OS 防火墙、REST、旧版本、PvP、完整伤害模型、全物品账本、复杂属性重算、完整液体/电路/回滚模型或 M19。未修改 Terraria MCP、真实服务器、世界、数据库和账号。

## 本轮验证及边界

| 实际执行 | 结果 |
| --- | --- |
| 用户附件 4 个纯 Rules 反例，来源 HEAD 的独立 detached worktree | 选中/执行 4，失败 4，通过 0；原样 TRX `Rules-attachment-source-head.trx`。本轮回收工作区首次运行 4/4 通过，再做更严格的 F08 负例；不把静态预测当作实测。 |
| M18 Rules 定向，包括 R2 三例、高低伤、普通伤害/拾取 Action、身份、取消、资源预算及 G06 | 选中/执行/通过 77/77，失败 0。F08 新增两条更严反例在修复前 2/2 失败，修复后定向 26/26 通过，原始 TRX 均保留仓库外。 |
| Rules 全套 | 选中/执行 620，619 通过、1 失败：既有 `M4IpcProcessTests.WindowsChildKill...` 在此精简 checkout 找不到 `START_HERE.md`；不属于本轮回收，不改写为通过。 |
| Persistence 全套 | 选中/执行/通过 36/36。 |
| Adapter 受影响定向 | 选中/执行/通过 177/177；包含首次 SSC、Sentry 帧选择、F06 原生事务、F08 代际、WorldEdit/Particle 和 G06 Root/身份边界。 |
| Adapter 较大范围选择集 | 发现 1341；精确排除既有 Phantasm native-fault 方法的 3 个参数 `-2f`、`201f`、`30000f`；选中/执行 1338，1335 通过、3 失败。失败为 `ChangedSourcePredicateOrCatalogDisablesOnlyThisPolicy`、`ValidTestLabSourcePolicyRemainsEnabled`、`SelectedItemDataKeepsNaturalAndPolicyConditionsAndSameEventExceptionSeparate`，均依赖此精简 checkout 缺失的源码目录/目录文件；未删除测试或扩大排除。 |
| 使用匹配的 1.4.5.8 TShock/OTAPI 外部依赖构建 Plugin | Release 成功，0 警告、0 错误；DLL SHA256 `A8005AD3A9D3549B6CEE08A056BA7E77F0841142D9AC352F501FACE2AB3D86A0`。依赖文件在本地审计运行时，不入库。 |
| 现有 NetworkLab / GameplayScaffold 工具构建 | NetworkLab Release 0 警告、0 错误；GameplayScaffold 在指定审计 CandidateDir 后成功，1 个既有可空引用警告。未导入来源的大型场景源码。 |

隔离原生接通仅使用本轮**中间版**插件 `92EC74EBF7B5749B1838C325D529658F76ABF42ABD573428A086384ABE4A19B5`：原版 F06 脚本 26/27、F08 脚本 15/16 均失败，原因是脚本预期早期/远端输入必须阻断，而当前保守候选给 Unknown；独立的 F08 观察版脚本 43/43 通过，覆盖干净世界普通/密集拾取及远端请求未取消，但只能证明观察链，没有证明 wipe 处罚。上述失败报告与现场均留在隔离目录；没有最终 DLL 的真实 GUI 客户端、实际账号永封或同账号重连拒绝证据。Rules 正例或 Adapter hook 不等于生产资格。

本次仅为源码集成。默认 `ObserveOnly`、现有非生产发布声明和生产资格限制继续生效。回滚应按本次独立提交执行 `git revert`，不需要恢复来源混合提交；若日后要重新审查 D 项，可从归档引用或 bundle 取回原 SHA。

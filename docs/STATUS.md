# 功能状态与验证边界

[返回首页](../README.md) · [路线图](ROADMAP.md) · [配置](CONFIGURATION.md)

“已实现”只表示对应模块已有代码；“部分实现”表示仅覆盖有限类型、路径或上下文；“仅观察”不能宣传为已阻止破坏；“实验性”需要指定环境和补充验收。功能展示源是 `capabilities.json`，不得直接修改下方生成区。

源码事实基线记录在能力登记的 `reviewedSource`。涉及功能的后续变更必须核对该模块，更新字段与测试证据；这个提交号不是对所有未来代码的背书。

<!-- capabilities:start -->
| 模块 | 实现状态 | 当前边界 |
| --- | --- | --- |
| 会话与处罚恢复 | 已实现 | 有界会话、账号隔离、处罚意图与恢复；生产接通须单独验证 |
| 协议与输入安全 | 部分实现 | 有限身份、方向、数值和结构规则；不是完整协议权威 |
| NPC 伤害与批量击杀 | 部分实现 | 普通跨目标序列仅观察；有限候选预算独立止损，序列永封关闭 |
| 地面掉落物异常清除 | 仅观察 | 记录有限请求与目标代际；不阻断、不踢出、不永封 |
| 世界编辑与粒子请求 | 部分实现 | 候选模式下有限资源预算；不是完整世界回滚 |
| 生命同步异常 | 实验性 | 有限一点伤害与满血同步；仅隔离测试可服务踢出，无永封 |
| 召唤与哨兵数量 | 部分实现 | 仅有限类型的原生容量控制；不是全召唤物覆盖或超限即永封 |
| 物品结构与进度 | 部分实现 | 有限结构、生成条件与进度检查；不是全物品来源账本 |
| 观察日志与奖励记录 | 已实现 | 有界记录、裁剪与写盘退避；奖励记录不证明全资产来源 |
<!-- capabilities:end -->

<!-- evidence:start -->
| 模块 | 入口 / 测试 | 验证边界 | 下一任务 |
| --- | --- | --- | --- |
| 会话与处罚恢复 | [源码](../src/AntiCheat.Core/AntiCheatEngine.cs) / [测试](../tests/AntiCheat.Core.Tests/EnforcementTests.cs) | 公开单元测试可运行；真实封禁存储与重启须使用匹配运行包复验 | R4-01 |
| 协议与输入安全 | [源码](../src/AntiCheat.Plugin.TShock/M2RuleRegistry.cs) / [测试](../tests/AntiCheat.Adapter.Tests/M3ProductionQualificationTests.cs) | 代码内存在有限生产资格登记；关联的历史审计资料未全部公开，发布前须重建证据 | R1-02 |
| NPC 伤害与批量击杀 | [源码](../src/AntiCheat.Rules/M18NpcStrikeQueueRules.cs) / [测试](../tests/AntiCheat.Rules.Tests/M18NpcStrikeQueueRuleTests.cs) | 已有规则与适配器回归源码；没有当前最终 DLL 的公开客户端处罚闭环证据 | R3-01 |
| 地面掉落物异常清除 | [源码](../src/AntiCheat.Rules/M18GroundItemClearQueueRules.cs) / [测试](../tests/AntiCheat.Adapter.Tests/M18GroundItemIdentityTests.cs) | packet151 使用两字节物品编号；原生删除归因及排他非法来源尚未闭合 | R3-02 |
| 世界编辑与粒子请求 | [源码](../src/AntiCheat.Rules/M18WorldEditQueueRules.cs) / [测试](../tests/AntiCheat.Rules.Tests/M18WorldEditQueueRuleTests.cs) | 另有 M18ParticleQueueRules 及对应回归；资源拒绝不产生账号永封 | R3-03 |
| 生命同步异常 | [源码](../src/AntiCheat.Plugin.TShock/M18LockHealthContext.cs) / [测试](../tests/AntiCheat.Adapter.Tests/M18LockHealthRootTests.cs) | 不是通用锁血或永久闪避检测；Production 的主动阻断与服务踢出未启用 | R3-04 |
| 召唤与哨兵数量 | [源码](../src/AntiCheat.Plugin.TShock/M10SummonBudgetGuard.cs) / [测试](../tests/AntiCheat.Adapter.Tests/M10SummonNativeTests.cs) | 有限史莱姆召唤与冰霜九头蛇哨兵上下文；生成先于退休的瞬态需保留 | R3-05 |
| 物品结构与进度 | [源码](../src/AntiCheat.Plugin.TShock/M3InventoryContexts.cs) / [测试](../tests/AntiCheat.Rules.Tests/InventoryRuleTests.cs) | 候选数据不自动授予处罚资格；缺失目录或来源资料时部分规则降级 | R3-06 |
| 观察日志与奖励记录 | [源码](../src/AntiCheat.Persistence/M18ObservationJournal.cs) / [测试](../tests/AntiCheat.Persistence.Tests/M18ObservationJournalTests.cs) | 公开持久化测试可运行；记录不可用不能触发账号处罚 | R4-01 |
<!-- evidence:end -->

## 验证层级不能合并

| 层级 | 能证明什么 | 不能证明什么 |
| --- | --- | --- |
| 静态审查 | 代码路径、默认值、显式边界及明显缺失 | 可运行、没有误判或没有其他缺陷 |
| 公开单元回归 | 给定输入下核心、规则、日志与数据处理符合断言 | 游戏服务器已接线、真实客户端正常 |
| 原生适配器回归 | 指定运行包的 Hook、解析和上下文行为 | 最终客户端场景全部通过 |
| 隔离客户端场景 | 指定 DLL、账号、世界与具体操作的实际结果 | 全类型覆盖或其他运行包的同等资格 |
| 生产接纳 | 经审查批准的具体规则与具体运行包组合 | 所有规则、所有版本自动批准 |

历史报告中的执行数不作为本次 CI 结果。查看当前分支的 Actions 与对应提交；失败、跳过、未执行和外部依赖阻塞必须分别列出。

## 退出与冻结

退出开发目标：OS 防火墙联动、REST 防护、旧版本兼容、PvP 专项。**退出目标不等于所有旧源码已删除**；仍需 R2-03 检查调用和清理残留。

冻结：完整伤害与装备/Buff 全量重算、全物品来源账本、通用无限魔力与概率推断、完整液体/电路仿真、通用世界回滚。只有明确批准范围变更后才能重新启动。

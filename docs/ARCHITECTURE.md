# 技术架构与代码导航

[首页](../README.md) · [开发流程](DEVELOPMENT.md) · [模块化任务](ROADMAP.md)

## 当前实际结构

```text
src/
  AntiCheat.Core/              会话、判定资格、处罚意图、持久化接口
  AntiCheat.Rules/             业务规则、有限状态窗口、工作预算
  AntiCheat.Progression/       进度目录和条件模型
  AntiCheat.Persistence/       处罚文件日志、运行标记、有界观察日志
  AntiCheat.Plugin.TShock/     游戏适配、上下文、原生 Hook、账号存储
tests/                        对应项目回归及原生适配器测试
tools/                        数据提取、开发与隔离实验工具
data/progression/             候选数据与历史提取结果
reference/                    可分发夹具、许可和来源元数据
docs/                         当前公开说明及少量历史记录
```

顶层已有合理分层，问题主要发生在各层内部：按历史阶段而非功能域命名、插件入口集中接线、少数大状态机承担过多职责、实验工具反向进入插件编译。目标是渐进收敛，不是重新写一套框架。

## 依赖方向

```text
Plugin.TShock  ──→ Core + Rules + Persistence + 外部游戏运行包
Rules          ──→ Core + Progression
Persistence    ──→ Core
Core           ──→ 不引用其他项目或游戏运行包
Progression    ──→ 不引用其他项目或游戏运行包
```

箭头从调用者指向被依赖层。Plugin 使用的 Progression 由 Rules 传递引用。Rules/Persistence 不应反向依赖 Plugin；公开检查脚本核对这些工程边界，合法架构调整须同步修改约定和检查。

`AntiCheat.Public.slnf` 是公开逻辑与数据工具的工作集；`AntiCheat.sln` 还包含原生适配器与退役实验工具，不能把整个解决方案的构建条件当成公共测试条件。

## 数据经过哪些边界

1. **解析。** 适配器确认运行版本、完整帧、偏移、长度、索引和有限数值。客户端声明与服务端事实分别保存，不能覆盖来源标记。
2. **上下文。** 将请求关联到服务端连接、世界、账号及实体代际，观察原生方法执行前后。取消的请求不能作为已接受结果使用。
3. **规则。** 使用不可变输入产生 `BusinessRuleResult`；规则自身不选择处罚账号，不进行数据库或游戏对象写入。
4. **资格与隔离。** Core 核对运行指纹、规则版本、执行范围、身份和必要前提；完整证明才产生 `BanIntent`。先撤销同账号活动会话写入资格，再交给持久化泵。
5. **持久化。** 处罚意图写入独立文件日志，账号存储通过服务器线程调度执行。原样重试同一意图；故障不会把 Unknown 变成作弊。

观察日志是另一条支路，不是处罚累积分。日志容量、裁剪、写盘失败退避不能影响账号归罪。

## 核心概念

| 概念 | 代码入口 | 约束 |
| --- | --- | --- |
| 会话身份 | `Core/Contracts.cs` 的 `SessionKey` | 包含服务器运行 ID、世界 epoch、槽位和连接代际；不能只用槽位 |
| 处罚资格 | `Core/AntiCheatEngine.cs`、`Plugin/M2RuleRegistry.cs` | 执行模式、规则资格、运行指纹是不同维度 |
| 运行锁定 | `Plugin/TargetRuntime.cs` | 校验宿主、游戏版本、程序集及加载证据；不是对不可信本机管理员的沙箱 |
| 线程交接 | `Plugin/ServerThreadDispatcher.cs` | 游戏及 TShock 存储访问必须使用规定线程上下文 |
| 恢复 | `Persistence/FileEnforcementJournal.cs`、`RunSafetyGuard.cs` | 未完成意图不能过期或被容量淘汰；异常退出有独立恢复要求 |

表中的 `Core/`、`Plugin/`、`Persistence/` 分别是对应 `src/AntiCheat.*` 工程目录的简写，不是新增目录。

## 按功能找代码

| 功能域 | 现有主要入口 | 整理时的关键边界 |
| --- | --- | --- |
| 生命周期 / 接入 | `AntiCheatPlugin`、`NetworkControls`、连接 deadline/retirement 类 | 注册、取消、重连、世界切换、卸载必须成对 |
| 协议 | `M2PacketReader`、各 Protocol/PacketReader 类 | 帧读取与规则判断分离，保留取消顺序 |
| NPC 战斗 | `M18NpcStrikeQueueRules`、`M7NpcStrikeCauseContexts`、`M8NpcStrikeTransactions` | 声明、接受位移、原生完成与死亡事件不能混用 |
| 掉落物 | `M18GroundItemClearQueueRules`、`M2BusinessAdapter` | 物品编号不是稳定身份，必须处理代际及普通拾取反例 |
| 生命 | `M4VitalContexts`、`M18LockHealthContext` | 生命声明、原生伤害、SSC 上下文与服务踢出分开 |
| 物品 / 进度 | `M3InventoryContexts`、`M3ProgressionPolicy`、`M5ProgressionContexts` | 候选数据、权限、自然条件不等于来源完整 |
| 召唤 | `M10SummonBudgetGuard`、`M11SentryBudgetGuard` | 容量单位、瞬态、目标类型及自然退休顺序 |
| 公共世界资源 | World/Particle queue、world safety、paint/wiring/liquid guards | 定点输入保护与通用仿真/回滚分开 |

## 目标目录：尚未全部迁移

```text
src/AntiCheat.Plugin.TShock/
  Hosting/        初始化、生命周期、模式、版本和配置
  Protocol/       包读取与入口分发
  Sessions/       会话、账号绑定和连接生命周期
  Features/
    Combat/       NPC 打击与完成事件
    WorldItems/   掉落物观察
    Vitals/       生命同步
    Inventory/    物品、容器、进度上下文
    Summons/      召唤与哨兵
    World/        公共世界输入与资源控制
  Diagnostics/    插件自有诊断，不引用实验工具实现
  Enforcement/    主线程调度与账号处罚适配
```

Rules 采用同样的功能域分组，但不引入 TShock 类型。Persistence 只保留通用持久化职责。测试可按对应域整理，先保留现有命名空间、类名和序列化字段，再分开评估重命名。

**迁移方法：** R2-01 建立文件与调用图；R2-02 一次迁移一个领域；R2-03 删除或独立归档退役模块；R2-04 再拆入口与大型状态机。不得只建一套空目录后声称完成模块化。

## 当前明确的技术债

插件工程直接编译 `tools/AntiCheat.GameplayScaffold` 下四个诊断源文件；公开 Core 测试链接了旧开发工具的路径辅助类。前者属于需要清理的生产/工具耦合，后者是测试构建依赖，不应混为运行时依赖。

部分源码中的审计路径指向未公开材料，旧报告也包含本机环境假设。迁移不能把缺失报告替换成新写的空白“已审核”文件；对应运行资格须另行审查。

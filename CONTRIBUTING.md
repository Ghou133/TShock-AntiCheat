# 参与开发

项目接受人工与 AI 辅助贡献；验收看可检查的代码、测试和行为，不看生成方式。

先读 [开发入口](START_HERE.md)、[开发约定](AGENTS.md) 和 [路线图](docs/ROADMAP.md)。在新任务分支上记录目标、相关模块和验收条件。范围变更先更新路线，不恢复已退出的功能。

## 一个合格的变更

修复应包含失败原因、最小回归及合法反例。重构应保持行为，和规则阈值或资格改变分开提交。新增状态必须有容量、释放及会话切换策略。涉及原生 Hook 时说明注册顺序、线程假设和卸载路径。

运行 [公开检查](docs/BUILDING.md)。涉及插件运行时的变更还须运行匹配的 Adapter 测试；运行包不在仓库内，应明确列出阻塞，不能以桩替代实际程序集。CI 的平台跳过、失败和未执行应分别记录。

## 文档同步

功能状态由 `docs/capabilities.json` 维护，执行 `python tools/repository-checks/check_repository.py --write` 更新展示。代码入口移动后同步源码、测试、工程引用、工具路径及文档。README 只保留概要，长篇技术细节放在既有主题文档，不创建重复台账。

请勿提交运行 DLL、游戏世界、账号数据库、密钥、访问令牌、真实玩家日志或本机绝对路径。公共说明只描述本项目技术与行为，必要的第三方许可必须保留。

## 提交前

```powershell
git diff --check
python tools/repository-checks/check_repository.py
dotnet restore AntiCheat.Public.slnf --locked-mode
dotnet test AntiCheat.Public.slnf -c Release --no-restore
```

在 PR 中给出任务编号、真实测试结果、未验证部分和回滚方式。源码合入与正式部署是不同决定，公开检查通过不会自动批准生产规则或发布 DLL。

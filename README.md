# CairnStoryTracker

Cairn / 《孤山独影》的探索辅助 Mod（MelonLoader + CairnAPI）。

目标：帮助玩家在一次正常流程中更完整地发现 **叙事探索点**、**世界信息** 与 **特殊探索收集物**。它不是全物品地图，也不是作弊传送工具。

从 v0.4.0 起，Tracker 记录的是**玩家已经获得的探索知识**，而不是镜像当前游戏存档。

## 标记语义

```text
? = 叙事探索点
i = 世界信息
* = 独立探索收集物
```

**Marker 本身就是进度系统**：有 marker = 这里还有值得探索的内容；一个地点的全部内容完成后，marker 自动消失。不再有单独的 X/Y 数字进度面板。

说明：

- 同一个 Lore 地点只显示一个 marker
- 与 Collectible 属于同一底层内容的 Lore 阅读物会合并进 Lore marker（不会出现 `?` + `*` 这类重复标记）
- 一个地点内所有内容都完成后，marker 自动消失

## 永久探索记忆（v0.4.0）

探索状态永久、单调前进：`Unknown → Completed`。完成之后**不会**因为以下情况自动回退：

- 正常退出游戏后重新进入
- 游戏重新加载
- 加载较早的游戏保存状态
- 游戏世界状态倒退（收集物重新出现等）

数据保存在：

```text
<游戏目录>/UserData/CairnStoryTracker/progress.json
```

规则：

- 只保存"哪些内容已完成"（Lore 按成员、Collectible 按稳定 ID），不保存位置、分类或 marker 状态——这些每次从当前世界模型重新计算
- 游戏存档是完成证据之一，不是回退权威：存档回退不会删除 Tracker 的记忆
- 已在存档中明确完成的内容会在首次运行时自动吸收进探索记忆
- **不提供游戏内重置功能**。想从零开始：退出游戏后手动删除 `progress.json` 即可
- 若文件损坏或来自更高 schema 版本，Mod 会保留原文件并在本 session 禁用写入（日志中显示 `PERSISTENCE LOAD FAILED`），不会自动清空

v0.3.x → v0.4.0 首次迁移说明：旧版本从不保存"非持久化阅读物"的已读状态，这部分历史无法恢复——它们在第一次进入 v0.4.0 时可能重新出现一次，重新完成一次后即永久保存。存档本身可证明的持久化内容（persistent provider、已取得的收集物）会自动导入。

## 显示开关

通过 CairnModOptions（或 `UserData/MelonPreferences.cfg`）可分别开关：叙事探索点 / 世界信息 / 特殊收集物。

## 设计边界

- 不修改游戏存档
- 探索记忆只增不减（见上文"永久探索记忆"）
- 所有 marker 均为纯视觉辅助，不注册原生快速旅行点，不影响右侧地点列表

## 依赖

- Cairn
- MelonLoader 0.7.x（IL2CPP）
- CairnAPI（必须）
- CairnModOptions（可选：提供游戏内设置界面；没有它时使用默认配置）

## 构建

需要 .NET 6 SDK，并指向本机游戏安装目录（用于引用 MelonLoader 生成的 interop 程序集）：

```bash
dotnet build -c Release -p:GameDir="D:\Path\To\Cairn"
# 或设置一次环境变量后直接：
dotnet build -c Release
```

## 安装

把 `bin/Release/CairnStoryTracker.dll` 复制到游戏的 `Mods/` 目录（与 CairnAPI.dll 同位置）。

## 已知限制

- v0.3.x 期间完成过的**非持久化**阅读物（游戏本身不记忆的内容）无法迁移，升级后可能重新出现一次
- Lore 分类是保守的结构启发式（按 `*_Lore` 层级与成员数），不保证语义分类百分之百准确；所有内容都保留显示，不会因分类不确定而消失
- 打开 L1 时会进行一次模型刷新，可能产生短暂帧时间峰值（实测约 134ms 量级）
- `F8` 是重型诊断功能，按下时可能明显卡顿，仅供调试

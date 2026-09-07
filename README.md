# CairnStoryTracker

Cairn / 《孤山独影》的探索辅助 Mod（MelonLoader + CairnAPI）。

目标：帮助玩家在一次正常流程中更完整地发现 **叙事探索点**、**世界信息** 与 **特殊探索收集物**。它不是全物品地图，也不是作弊传送工具。

## 标记语义

```text
? = 叙事探索点
i = 世界信息
* = 独立探索收集物
```

说明：

- 同一个 Lore 地点只显示一个 marker
- 与 Collectible 属于同一底层内容的 Lore 阅读物会合并进 Lore marker（不会出现 `?` + `*` 这类重复标记）
- 一个地点内所有内容都完成后，marker 自动消失

## 进度面板

打开 L1（勘察岩壁）或鹰眼地图时显示：

```text
探索收集
>> 峭壁   X / Y
   拱岩   X / Y
```

`X/Y` 只统计可靠持久化的特殊 Collectible；Lore/readable 不进入 X/Y。

## 显示开关

通过 CairnModOptions（或 `UserData/MelonPreferences.cfg`）可分别开关：叙事探索点 / 世界信息 / 特殊收集物。

## 设计边界

- 不修改游戏存档
- 非持久化阅读物只记录**当前游戏 session** 的已读状态；退出游戏后这些 marker 会重新出现（设计行为，避免与游戏存档回档不同步）
- 所有 marker 均为纯视觉辅助，不注册原生快速旅行点，不影响右侧地点列表
- Collectible 的追踪 / remaining / X/Y 全部来自游戏自身数据，Mod 不保存进度

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

- 非持久化阅读物的已读状态只在当前 session 有效
- Lore 分类是保守的结构启发式（按 `*_Lore` 层级与成员数），不保证语义分类百分之百准确；所有内容都保留显示，不会因分类不确定而消失
- 打开 L1 时会进行一次模型刷新，可能产生短暂帧时间峰值（实测约 134ms 量级）
- `F8` 是重型诊断功能，按下时可能明显卡顿，仅供调试

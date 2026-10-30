# Dune: Imperium 逆向分析与离线 Mod 技术报告

**分析日期**：2026-10-30
**目标**：Steam 版《Dune: Imperium》（沙丘：帝国）数字版
**分析目的**：在用户自有合法副本上、仅针对离线对局增加玩法内容
**工具链**：UnityPy 1.25.4 + Python 3.13.14 + REA 6.7.0

> **使用边界**：本报告全部结论与产出仅适用于**本地离线对局**（对 AI、本地热座、自定义剧本）。
> 该游戏含在线排位赛（GameLift 服务器、ELO 计分、真实对手），任何改动均不得用于在线
> 排位或联机房间——那对其他真实玩家构成作弊。本分析不涉及、也不提供任何绕过授权校验、
> DRM、付费墙或联机优势的技术手段。

---

## 一、结论摘要

1. 游戏为 **Unity 6000.3.17f1 + IL2CPP + Addressables 2.9.1**，逻辑编译在 `GameAssembly.dll`（88.7 MB），内容是 411 个 LZ4HC 压缩的 AssetBundle（553 MB）。
2. 内容存在**清晰的两层边界**：
   - **数据层**：卡牌视图注册表（286 张卡）、卡牌预制体、剧本 JSON、存档 JSON —— 明文可读写，改造成本低。
   - **规则层**：卡牌费用与效果 —— **不在任何资源文件中**，编译进 IL2CPP 代码，按字符串常量分派。
3. 因此：
   - 改剧本参数、牌堆构成、起始状态、胜利条件 → **纯数据改写即可**，已交付可安装 mod。
   - 新增卡牌 / 新领袖（带全新效果） → **必须代码级 hook**（BepInEx 6 IL2CPP），当前受 .NET SDK 缺失阻塞。
4. 本报告附带的 mod 已通过静态回读校验，但**尚未做游戏内实测**。

---

## 二、目标基本信息

| 项目 | 值 |
|---|---|
| 安装路径 | `C:\Program Files (x86)\Steam\steamapps\common\Dune Imperium` |
| Steam AppID | 1689500 |
| Build ID | 25682137 |
| 游戏版本 | 4.1.2.1808（客户端内部标识 `worm`） |
| 引擎 | Unity 6000.3.17f1（Unity 6） |
| 脚本后端 | IL2CPP |
| 资源系统 | Addressables 2.9.1 |
| 占用空间 | 862 MB（Steam 记账 901,596,161 字节） |
| 已装内容包 | 基础包 + Rise of Ix + Immortality + Uprising（商店侧检测到三扩展） |
| 发行商 | Dire Wolf Digital |

### 关键文件

| 文件 | 大小 | 作用 |
|---|---|---|
| `GameAssembly.dll` | 88,717,624 B | IL2CPP 编译的游戏逻辑 |
| `UnityPlayer.dll` | 36,527,024 B | Unity 运行时 |
| `DuneImperium_Data/il2cpp_data/Metadata/global-metadata.dat` | 23,699,944 B | 类/方法/字符串常量元数据 |
| `DuneImperium_Data/StreamingAssets/aa/catalog.bin` | 827,061 B | Addressables 二进制目录 |
| `DuneImperium_Data/StreamingAssets/aa/StandaloneWindows64/*.bundle` | 411 个，约 553 MB | 全部内容资源 |

---

## 三、内容容器结构

### 3.1 AssetBundle 格式

- 头部签名 `UnityFS`，格式版本 8
- **411 个 bundle 全部使用 LZ4HC 压缩数据块**（`blocksInfo` 与数据块均压缩）

> **方法学教训（重要）**：由于全库压缩，直接用 `grep` 在 `.bundle` 原始字节上搜索字符串会
> 大幅漏检——只有恰好不可压缩的片段才漏出明文。本分析初期据此得出"卡牌预制体只有 3 个"
> 的错误结论。改用 UnityPy 逐对象解压后，实际数字是 **374 个**。
> 凡是涉及 bundle 内容的搜索，必须走解压路径。

### 3.2 Addressables 目录

- `catalog.bin` 为二进制目录：magic `0x0DE38942`、版本 2、头部 36 字节（含 6 个段偏移），
  段偏移分别指向字符串表与资源条目
- `settings.json` 声明 `m_AddressablesVersion: 2.9.1`，目录为本地内嵌（`m_IsLocalCatalogInBundle: false`）
- bundle 文件名为内容哈希（如 `b56aa8ad35f9c2e1694994f384e51bbe.bundle`），
  逻辑名到文件名由 catalog 映射

### 3.3 全库对象统计

扫描 411 个 bundle、**753,892 个对象**：

| 类型 | 数量 |
|---|---|
| MonoBehaviour | 219,740 |
| GameObject | 179,067 |
| RectTransform | 134,784 |
| CanvasRenderer | 85,730 |
| Transform | 44,283 |
| ParticleSystem / Renderer | 19,421 ×2 |
| Animator | 14,186 |
| Sprite | 2,085 |
| TextAsset | 13（仅 1 个 bundle） |

---

## 四、核心发现：数据层与规则层的边界

这是整个分析最重要的结论。游戏内容并非单一形态，而是分成可改性截然不同的两层。

### 4.1 数据层（可安全改写）

#### 卡牌视图注册表

- 位置：bundle `27dd4c246fcc9c0db70c3062bace07ce.bundle`（16 KB，单对象）
- 对象：`CardViewsLookup`，path_id = `-8400579711675427487`，字节大小 29,512
- 内容：869 个字符串、**286 个 GUID**，模式为三元组
  `UI_crd_<Type>_<CardName>` → `<Type>Playable` → `<GUID>`

卡牌类型分布：

| 类型标识 | 数量 |
|---|---|
| `ImperiumPlayable`（帝国牌） | 130 |
| `IntriguePlayable`（阴谋牌） | 62 |
| `ContractPlayable`（合约牌） | 28 |
| `ConflictPlayable`（冲突牌） | 23 |
| **合计** | **286** |

按预制体前缀统计（含非 Playable 资源）：

| 前缀 | 数量 |
|---|---|
| Imperium | 112 |
| Intrigue | 66 |
| Contract | 28 |
| Hagal | 25 |
| Conflict | 24 |
| Tleilaxu | 20 |
| TechTile | 18 |
| Fallback | 1 |

> 注：注册表中**没有 Leader 相关条目**，说明领袖系统不走卡牌视图注册表，
> 是独立的另一套机制（推测与代码内的领袖定义绑定）。

#### 卡牌预制体

- 命名规则：`UI_crd_<Category>_<CardName>`，例如 `UI_crd_Imperium_SardaukarInfantry`
- 每个预制体挂 21 个组件，绝大多数为 UI 行为（拖拽、提示、高亮、动画）
- 其中承载卡牌身份的数据组件，原始字节布局为：

```
[m_GameObject: PPtr 12B][m_Enabled: 4B][m_Script: PPtr 12B]
[int 4B][int 4B][string "<Type>Playable"][string "<GUID>"]
```

以「萨杜卡步兵」为例（组件 path_id `4128573215654528492`，100 字节）：

| 字段 | 值 |
|---|---|
| 整数 1 | `0` |
| 整数 2 | `2` |
| 类型串 | `ImperiumPlayable` |
| GUID | `06e9213a-c1b3-438c-86ef-d5e19818776a` |

该组件**只承载身份与视图类型，不含费用、效果、奖励任何数值**。

#### 剧本定义（DSL）

- 位置：bundle `b56aa8ad35f9c2e1694994f384e51bbe.bundle`，13 个 TextAsset
- 格式：**明文 JSON**，schema 引用 `WormMatchInitData.schema.json`
- 清单：`StandardSolo`、`Campaign01/14/16/103/109/113/205/221/222/223/306/312`、`Tutorial02`、`TutorialImmortality`

剧本 JSON 的可用字段（经实际解析确认）：

| 字段 | 语义 | 可改性 |
|---|---|---|
| `EndgameTriggerScore` | 胜利点阈值（默认 10） | 直接改数值 |
| `GamesToWin` | 需赢几局 | 直接改数值 |
| `sets` | 启用内容集（如 `BaseSet`） | 可换集 |
| `RuleModAbilities` | 规则修改器 GUID 数组 | 可挂已有修改器 |
| `LeaderAssignmentMode` | 领袖分配方式（`Random` / `CustomSolo`） | 可改 |
| `wormPlayers[]` | 各玩家初始配置 | 见下 |
| `wormPlayers[].StartingDeck` | 起始牌组，**卡牌名字符串数组** | 可增删改 |
| `wormPlayers[].StartingIntrigue` | 起始阴谋牌 | 可改 |
| `wormPlayers[].StartingAgents` | 起始特工数 | 可改 |
| `wormPlayers[].AllianceTokenVP` | 联盟标记胜利点 | 可改 |
| `wormPlayers[].PlayerMatchSoloOptions` | 起始资源与状态 | 见下 |
| `PlayerMatchSoloOptions.StartingSwordmaster` | 是否开局拥有剑术大师 | 布尔 |
| `PlayerMatchSoloOptions.StartingHighCouncil` | 是否开局进高议会 | 布尔 |
| `PlayerMatchSoloOptions.StartingSolari/Spice/Water` | 起始太阳币/香料/水 | 整数 |
| `PlayerMatchSoloOptions.StartingGarrison/Specimens` | 起始驻军/标本 | 整数 |
| `PlayerMatchSoloOptions.BonusStartingVP` | 起始额外胜利点 | 整数 |
| `MatchSoloOptions.ImperiumDeck` / `ImperiumDeckExclusions` | 帝国牌堆指定/排除 | 卡名数组 |
| `MatchSoloOptions.ConflictDeck` / `IntrigueDeck` | 冲突/阴谋牌堆指定 | 卡名数组 |
| `MatchSoloOptions.DoubleImperiumSets` | 帝国牌堆翻倍的内容集 | 集名数组 |
| `MatchSoloOptions.HagalChurn` / `HagalBrutalEscalation` | 哈加尔沙虫模式开关 | 布尔 |
| `ClientConfig` | 玩家人数上下限、是否可存档、排序 | 可改 |
| `RequireOwnership` | 需拥有的 DLC 列表 | 可改 |
| `ranked` | 是否排位 | 固定为 `false` |

这套 DSL 的表达力足以构造出与标准局显著不同的玩法，且**完全不需要碰代码**。

#### 存档

- 位置：`%LocalAppData%Low\Dire Wolf Digital\Dune_ Imperium\savedgames\`
- 格式：明文 JSON，单局可达 2.6 MB
- 顶层结构：`gameID`、`currentState`（含 `entities`、`playerAccounts`）、
  `activePlayer`、`playerStates`、`turnNumber`、`scenarioKey`、`matchType`、`eventReactor`
- 每玩家含 `displayActions`（实测单局 803 条动作历史）
- 附带 `_GameMetaData` 记录 `offlineIDs` 与各局 `options`（含 `scenario` 名）

存档结构说明规则引擎是高度数据驱动的，也意味着局面级修改（甚至自定义开局局面）在数据层可行。

### 4.2 规则层（不可数据改写）

**关键结论：卡牌的费用与效果不在任何资源文件中。**

三重验证：

**验证一 · 卡牌 GUID 字符串反查**

以 `Dagger`、`Crysknife`、`Diplomacy`、`SardaukarInfantry` 四张卡的 GUID 在全部 411 个
bundle 与 `_Data` 主资源中搜索（解压后），命中位置仅为：

| 卡 | 命中位置 |
|---|---|
| Dagger | 剧本包 ×3、`27dd4c24`（注册表）×1、`9b0d29d4`（预制体）×1 |
| Crysknife | 剧本包 ×3、`27dd4c24` ×1、`9b0d29d4` ×1 |
| Diplomacy | 剧本包 ×1、`27dd4c24` ×1、`9b0d29d4` ×1 |
| SardaukarInfantry | `27dd4c24` ×1、`9b0d29d4` ×1 |

即：GUID 只出现在「预制体自身」「注册表」「剧本」三处，**没有任何规则数据表**。

**验证二 · 卡牌 GUID 二进制反查**

Unity 常以 16 字节二进制存储 GUID 引用。对上述卡牌分别按 `.NET Guid.ToByteArray()`
（前三段小端）与规范字节序两种形式，在解压后的全库内容中搜索：

> **结果：四张卡、两种字节序，全部零命中。**

**验证三 · 效果标识符的归属**

选取游戏内效果与卡牌标识符，分别在 `global-metadata.dat`（元数据）与
`GameAssembly.dll`（代码）中统计出现次数：

| 标识符 | global-metadata.dat | GameAssembly.dll |
|---|---|---|
| `Crysknife` | 19 | 0 |
| `DeployTroop` | 14 | 0 |
| `ImperiumPlayable` | 5 | 0 |
| `AcquireCost` | 4 | 0 |
| `SardaukarInfantry` | 3 | 0 |

字符串常量全部落在元数据（IL2CPP 字符串字面量堆）中，代码段中为零——
这是 IL2CPP 的正常布局，说明这些标识符是**被 C# 代码引用的字面量**，
即卡牌效果由代码按字符串分派。

**综合结论**：

- 卡牌身份、视图、注册关系 → 数据驱动，可 patch
- 卡牌费用、效果、奖励结算 → 代码驱动，数据层无法触及
- 因此「改一张卡的费用」这样的简单需求，也已经是代码级改动

---

## 五、已交付的离线 Mod

### 5.1 改动内容

改写剧本 `Campaign01`（挑战模式「帝国恩泽」，`gameType: Challenge`）：

| 项目 | 原值 | 新值 |
|---|---|---|
| `EndgameTriggerScore`（胜利点阈值） | 10 | **15** |
| `StartingAgents`（起始特工） | 2 | **3** |
| `StartingSwordmaster`（开局剑术大师） | `false` | **`true`** |
| `StartingSolari`（起始太阳币） | 0 | **3** |
| `StartingSpice`（起始香料） | 0 | **2** |
| `StartingWater`（起始水） | 0 | **2** |
| `StartingDeck`（起始牌组） | 9 张 | **10 张**（增补 `Crysknife` 晶刃） |

设计意图：把原本的入门挑战改造成「资源充裕但胜利门槛更高的中盘对抗」，
同时用一张高价值卡（晶刃）改变早期节奏。

### 5.2 技术实现

1. 从 bundle `b56aa8ad…` 解出 `Campaign01` 的 TextAsset
2. 解析 JSON，应用上述字段改写
3. 写回 TextAsset，用 LZ4 重新打包整个 bundle
4. 回读校验：重新加载产出包，确认 `EndgameTriggerScore = 15`、
   起始牌组含 `Crysknife`、特工 3、剑术大师 `true`、资源 3/2/2 **全部读回正确**

产出包大小 53.6 KB（原包同量级），文件名保持不变，以免破坏 Addressables 目录的映射关系。

### 5.3 安装与回滚

- **安装**：以管理员身份运行 `install_mod.bat`（游戏位于 Program Files，需提权）。
  脚本会先把原包另存为 `.orig`，再覆盖。
- **回滚**：运行 `restore_mod.bat`，或使用 Steam「验证游戏文件完整性」。
- 原始 bundle 与存档均已备份于工作区 `backup/`。

### 5.4 验证状态

- ✅ 静态回读校验通过（补丁包内数据正确）
- ❌ **未做游戏内实测**
- ⚠️ 存在一项真实风险：若剧本定义由服务端下发，本地改写可能被覆盖。
  离线挑战模式应优先读取本地，但需实测确认。

---

## 六、新增卡牌 / 新领袖的路线（未实施）

### 6.1 为什么必须走代码

见第四节。数据层没有费用与效果字段，任何「新卡」若要具备新效果，
必须在运行时向卡牌分派逻辑注册新条目。

### 6.2 三步路线

| 步骤 | 内容 | 当前状态 |
|---|---|---|
| 1 | 安装 **.NET SDK**（BepInEx 插件编译与元数据工具依赖） | ❌ 本机 `dotnet` 不存在 |
| 2 | 安装 **BepInEx 6（IL2CPP / Unity 6 版）** 到游戏根目录，首次启动经 Il2CppInterop 生成可调用程序集 | 未开始 |
| 3 | 用 **Il2CppDumper** 处理 `GameAssembly.dll` + `global-metadata.dat`，定位 `CardViewsLookup` 对应 C# 类与卡牌效果分派方法，编写插件注入 | 未开始 |

### 6.3 已知风险

- Unity 6 + IL2CPP 对 BepInEx 而言属较新组合，兼容性需实测
- 注入构建必须限制在离线场景；建议在插件内加入运行环境检测，避免误入在线对局
- 服务端权威的在线对局部分，既无法也不应绕过

### 6.4 替代思路（无需代码，可立即尝试）

若目标是"新玩法"而非"新卡牌"，剧本 DSL 已足够表达大量设计：

- 超长局（胜利点 20+）、速攻局（胜利点 6）
- 资源倾斜开局（高太阳币/香料，逼迫快节奏买牌）
- 指定帝国牌堆与排除项 → 构造特定流派环境（如纯战斗流、纯经济流）
- `DoubleImperiumSets` → 牌堆翻倍，长线运营
- 沙虫模式（`HagalChurn` / `HagalBrutalEscalation`）组合
- 多局连战（`GamesToWin` > 1）+ 逐局递增难度

---

## 七、证据索引

所有中间产物位于工作区 `C:\Users\17789\DuneModding`。

| 结论 | 证据文件 |
|---|---|
| 411 个 bundle 全 LZ4HC | `scripts/bundle_header.py` 输出 |
| 全库对象类型与 753,892 对象扫描 | `dump/needle_hits.json`、`dump/bundle_inventory.json` |
| 286 张卡注册表 | `dump/card_registry.json` |
| 113 张卡名 → GUID 映射 | `dump/card_catalog.json` |
| 卡牌 GUID 本地反查仅 3 类位置 | `dump/guid_locations.json` |
| 二进制 GUID 零命中 | `dump/guid_binary_hits.json` |
| 13 个剧本 JSON 全字段 | `dump/textassets/*.txt` |
| 补丁后剧本可读版本 | `mod/Campaign01_modded.json` |
| 原始 bundle 备份 | `backup/bundles/b56aa8ad…bundle` |
| 存档备份 | `backup/savedgames_20261030/` |

### 分析脚本清单

| 脚本 | 用途 |
|---|---|
| `probe_bundle.py` | 抽样探测 bundle 可读性与对象类型 |
| `test_typetree.py` | 验证 MonoBehaviour typetree 可得性 |
| `scan_bundles.py` | 全量类型统计与 TextAsset 清点 |
| `dump_textassets.py` | 导出剧本 JSON |
| `bundle_header.py` | 解析 UnityFS 头，统计压缩格式 |
| `find_cards.py` | 卡牌对象定位（初版，性能不足已弃用） |
| `byte_scan.py` | 单包字节级内容搜索 |
| `dump_prefab.py` | 导出预制体组件结构与原始字节 |
| `scan_content.py` | 全量解压内容搜索（多目标） |
| `extract_cards.py` | 抽取卡牌注册索引 |
| `trace_guid.py` | 卡牌 GUID 字符串反查 |
| `trace_guid_binary.py` | 卡牌 GUID 二进制反查 |
| `build_mod.py` | 剧本改写与 bundle 重打包 |

---

## 八、局限与未知

1. **未做运行时验证**。mod 仅通过静态回读校验，未启动游戏确认生效；剧本是否由服务端覆盖未知。
2. **卡牌效果分派点未定位**。需要 .NET 工具链 dump 元数据才能看到具体类与方法，
   本报告只证明了"规则在代码里"这一事实，未展开到方法级。
3. **领袖系统未拆解**。注册表中无 Leader 条目，其数据与代码归属尚未分析。
4. **typytree 缺失**。游戏构建时剥离了自定义 typetree，因此无法用 UnityPy 直接反序列化
   MonoBehaviour 的业务字段，本报告对组件字段的解读基于原始字节布局推断，
   字段命名属推测，仅"类型 + GUID"两项有字符串实证。
5. **Addressables 目录未完整解析**。仅确认格式特征，未实现完整 catalog 解析，
   因此卡牌→bundle 的映射是通过内容扫描反推的，而非查表。
6. 结论基于静态推断，属"逻辑与行为的重建"，**不等于原始源码还原**。

---

## 九、方法论备注

三条值得记录的实践经验：

1. **不要用 grep 判断压缩容器的内容**。全库 LZ4HC 导致初期的字符串搜索严重漏检，
   曾误判卡牌预制体只有 3 个（实际 374 个）。正确做法是逐对象解压后搜索。
2. **`grep -c` 对二进制文件按"行"计数**，会给出恒为 1 的误导性结果，应使用 `grep -o | wc -l`。
3. **"数据驱动"要先证伪再证真**。发现卡牌预制体带 GUID 时，容易直接假设"规则表由 GUID 索引"；
   必须用字符串 + 二进制双形态反查，并结合元数据/代码段的字符串归属统计，
   才能确证规则位于代码层。

---

*本报告由 REA 逆向功能分析流程产出。所有分析均在本机执行，目标程序未上传；
分析结论经 AI 模型处理后生成。目标软件的合法副本由用户持有，改动仅限用户本机离线场景。*

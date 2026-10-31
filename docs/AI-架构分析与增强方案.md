# AI 架构分析与增强方案

**分析日期**：2026-10-31
**对象**：Steam 版《Dune: Imperium》4.1.2.1808（Unity 6 / IL2CPP）离线单人局
**范围**：仅本地离线对局。在线排位与联机房间不在讨论范围。

---

## 一、结论摘要

1. **这个游戏的 AI 不是搜索型 AI，是「加权评分 + 排序」的启发式 AI。**
   没有蒙特卡洛树、没有对局模拟、没有对手建模。它把每个可选动作交给一组评估器打分，再按分数排序取最优（多选时走背包算法）。这意味着**"变聪明"最短的路径不是改算法，而是改权重表**。

2. **权重表是 437 个具名参数（432 个 double + 5 个 int）**，打包在 `worm.canis.ai.AIConstantsRef` 里，按难度档分成三套：Easy / Medium / Hard。

3. **当前实际跑的是 Hard**（首局实测修正，见 3.4）。剧本里 `aiLevel` 写的 1 被大厅难度设置覆盖，
   注册表里账号级 `AIDifficulty = 2`。所以"切难度档"这条零成本路径不存在——AI 已经在最高档。

4. **真正让 AI "显得笨"的不是难度，而是规则改动与权重的失配**：权重表是按「10 分 / 一级到三级混合冲突 / 3 工人」调的，而现在的 mod 是「12 分 / 全二三级冲突 / 4 工人 / 每人双霍兹曼引擎 / 起始高等议会」。这些都在权重表的假设之外，且方向可预测（详见第五节）。

5. **运行期可以无损注入权重**：`AIConstants.SetInstance(int aiLevel, AIConstantsRef ai)` 是静态方法，三档 ref 也是静态属性，全部是普通 POCO（无实体/GUID/泛型容器），不触碰此前屡屡失败的卡牌注入链路。已交付探针插件完成导出与覆盖。

---

## 二、AI 的决策链路（静态分析结论）

### 2.1 类型骨架

| 类型 | 作用 |
|---|---|
| `Canis.entities.ai.AIProfileAbs` | AI 主体：`MakeChoice` 产出选择；`GetWeightMap` / `SelectionWithTargetsFunctionSorted` 负责打分排序 |
| `Canis.entities.ai.AISelectionEvaluator` | 单个能力（ability）的评估器，`Evaluate` 返回分值 |
| `Canis.ai.AIValuationFunction.Evaluate(PlayerEntity, Entity)` | 单张牌的估值，返回带"理由链"的 `AIValueSummer<double>` |
| `Canis.ai.value.AIValueSummer<T>` | 带运算与 `Reason` 的数值累加器，便于调试时输出"为什么这么打分" |
| `Canis.entities.ai.AIChoiceContext` | 一次选择上下文：`intents`（意图→适应度）、`rankedIntents` |
| `Canis.entities.ai.AIAlgorithmUtil` | `BestRankTimeout`（带超时的最优选择）、`KnapsackSolver`（多选预算） |
| `Canis.ai.value.AIValueSummerExtensions.InjectNoise` | 给评分注入随机扰动 |

### 2.2 决策流程

```
枚举候选 selection
  → 每个候选按 ability 找到 evaluator
  → 用 AIConstantsRef 里的权重逐项累加（资源/影响力/冲突姿态/特定卡牌修正）
  → AIValueSummer 汇总，InjectNoise 注入扰动
  → BestRankTimeout 排序（有超时保护）／多选时 KnapsackSolver
  → AIChoice.SendResponse
```

### 2.3 它没有的东西（决定了增强的天花板）

- **无前瞻**：不模拟"我这步之后对手会怎么走"。
- **无对手建模**：只有 `ConflictInterestOpponentsWithAgentsPenalty` 这类粗粒度的竞争人数惩罚。
- **无长期规划**：只有 Early / Mid / Late 三档相位（按回合数或进度切换），没有"这条路线几回合后能成型"的推演。
- **有超时**：`BestRankTimeout` 表明评估有时间预算，任何加重计算量的改造都可能触发降级。

**推论**：想让 AI 明显变强，优先做「让它的估值假设与当前规则对齐」，而不是给它加搜索——加搜索的工程量是前者的一个数量级，且容易撞超时。

---

## 三、难度档与权重表的来源

### 3.1 难度枚举

```
AIDifficultyLevels  Easy = 0
                    Medium = 1
                    Hard = 2
```

`worm.canis.ai.AIConstants` 持有三个静态引用 `EasyAIConstantsRef / MediumAIConstantsRef / HardAIConstantsRef`，
以及 `SetInstance(int aiLevel, AIConstantsRef ai)` 与 `loadedConstants : ConcurrentDictionary<int, AIConstantsRef>`。

### 3.2 `aiLevel` 的数据来源

剧本 JSON 的 `wormPlayers[i].aiLevel` 直接写进 `PlayerType.aiDifficulty`。人类固定 0。

官方剧本实测统计（97 个含 `aiLevel` 的 TextAsset）：

| 用法 | 代表剧本 |
|---|---|
| 1（Medium） | Campaign01 / 03 / 05 / 11 / 13 / 301 / 303 … |
| 2（Hard） | Campaign02 / 04 / 06 / 08 / 12 / 302 / 304 / 306 / 312 … |
| 0（人类） | 全部 |

结论：**1 和 2 都是官方在生产环境跑的配置**，切档不是"改坏游戏"。

### 3.3 当前 mod 的状态（改动前）

`mods/tools/build_presets.py` 中：

```python
"aiLevel": 0 if human else 1,
```

所有预设、所有 AI 座位都写成 1。

已改为 `--ai-level 0|1|2`（默认 1，保持现有行为；非 1 时产物加 `_aiN` 后缀避免覆盖）。
已空跑校验：`--ai-level 2` 生成后回读 bundle，`StandardSolo` 的 `aiLevel` 为 `0,2,2,2`。

### 3.4 实测修正：剧本里的 `aiLevel` 会被大厅设置覆盖

**2026-10-31 首局实测**（分阵营 Ultra，剧本 `aiLevel = 1`）：

- `profile/DireWolf/Dune Imperium/Player.log` 里三名 AI 全部注册为 **`Player AI（困难）`**；
- 注册表 `HKCU\Software\Dire Wolf Digital\Dune: Imperium` 中
  `3ec2f6ca-…AIDifficulty = 2`（账号级），`00000000-…AIDifficulty = 1`（全局默认）；
- 历史存档对照（`savedgames/_GameMetaData`）：Campaign203（`aiLevel=1`）当年显示「AI（中等）」，
  教程局显示「AI（简单）」。

即：**难度档取的是大厅里的难度设置，不是剧本里的 `aiLevel`**。中文名与档位一一对应
（简单/中等/困难 = 0/1/2），所以这局 AI 实际跑的是 **Hard**。

**结论**：`--ai-level` 对单人标准局无效，Tier 0（切难度档）没有收益——AI 本来就在 Hard。
真正的杠杆只剩权重（Tier 1）与动态调制（Tier 2）。要在运行时确实改变权重，
用探针的 `force_level.txt`（把目标档权重整体拷到实际使用的对象上），而不是改剧本。

**注意**：`mods/out/` 里已有的 170 个剧本包是旧脚本生成的，`aiLevel` 全部固化在 bundle 里。
要让既有预设生效，要么用 `--ai-level 2` 整体重生成，要么用探针的 `force_level.txt` 在运行时直接调档（后者不需要动任何 bundle，见第七节）。

---

## 四、权重表结构（437 项）

`AIConstantsRef` 的属性分组（按名字归类）：

| 分组 | 数量 | 代表参数 |
|---|---|---|
| 八大资源的分相位价值 | 24 | `VictoryPointValueEarly/Mid/Late`、`PersuasionValue*`、`StrengthValue*`、`SolariValue*`、`SpiceValue*`、`WaterValue*`、`TroopValue*`、`IntrigueValue*` |
| 相位动作价值 | 18 | `TrashCard*`、`Deploy*`、`GainSwordmaster*`、`GainHighCouncil*`、`GainFoldspace*`、`GainMentat*` |
| 冲突姿态 | 18 | `ConflictAverageValue`、`ConflictInterestHandSwordValue`、`ConflictPosture{No/Some/Many}Competitors{Lower,Upper}Bound`、`...Climax`、`ConflictPostureMidMod/HighMod` |
| 影响力 / 结盟 | 38 | `InfluenceValue`、`GainInfluenceAlliance*Mod`、`LoseInfluenceForfeit*` |
| 贫富判定阈值 | 39 | `Solari/Spice/Water/Troop/{Normal,Rich,Flooded}Threshold` |
| 卡牌 / 领袖 / 空间特化 | 177 | `OpulenceRevealBonus`、`SardaukarLegionRevealBonus`、`BaronVladimirHarkonnenGetTroopsBonus`、`HeighlinerSpaceBonus` … |

这套表的设计意图很清楚：**每个具体卡牌都有自己的修正项**，所以官方新卡上线时只需要加权重、不需要改算法。同一机制也解释了为什么"新增自定义卡牌"的 AI 表现会很差——没有对应权重，只能落到 `FallbackAIValuationFunction`。

---

## 五、为什么在这个 mod 里 AI 会显得笨（可验证假设）

以下每一条都是「权重表的默认假设」与「当前规则」的错配，按预期影响排序：

| # | 错配点 | 机制 | 可调参数 |
|---|---|---|---|
| 1 | 12 分制 vs 10 分制 | 后期 VP 权重按 10 分终点线性设计，12 分时需要更长的冲刺窗口 | `VictoryPointValueLate`、`MinimumAcquireValueLate` |
| 2 | 冲突全为二级/三级 | `ConflictAverageValue` 是按混合牌堆估的均值，实际每场收益都高于均值 → 前期低估战斗 | `ConflictAverageValue`、`ConflictInterest*` |
| 3 | 5 张二级 + 5 张三级固定 | 冲突强度曲线比标准模式更陡，`ConflictPosture*` 的分档边界偏保守 | `ConflictPostureManyCompetitors*` |
| 4 | 4 工人（Ultra） | 每轮行动数增加，工人本身的稀缺性下降，`Deploy*` 的相对价值偏高 | `DeployEarly/Mid/Late` |
| 5 | 起始双霍兹曼引擎 | 每回合多抽 2 张，抽牌与牌组质量的边际价值上升 | `CardDrawValue`、`TrashCard*` |
| 6 | 起始高等议会 | 已经拥有时 `GainHighCouncil*` 应为 0，否则会为无收益的空间付出代价 | `GainHighCouncilEarly/Mid/Late` |

> 以上是**假设**，不是实测结论。要证实需要先导出三档权重基线，再做 A/B。

---

## 五之一、实测基线（2026-10-31 抓取）

探针终于在 `RunAIResponses` 上抓到数据：`AILevel = 2 (Hard)`，三档 432 项全部导出，
原始数据在 `dump/ai_constants_baseline.json`。

### 三档难度其实几乎一样

| 项目 | 数值 |
|---|---|
| 432 项中三档有差异的 | **29 项**（其余 403 项三档完全相同） |
| `VictoryPointValueEarly/Mid/Late` | 6 / 6 / 9，**三档一致** |

也就是说 Hard 并不是"更会算分"，它只在 29 个行为参数上更凶：

| 参数 | Easy | Medium | Hard | 含义 |
|---|---|---|---|---|
| `ConflictPostureHighMod` | 0.85 | 1.05 | **1.8** | 高强度冲突的投入意愿 |
| `RichMod` | 1.15 | 1.0 | **0.5** | 资源"富余"后的折价 |
| `FloodedMod` | 1.15 | 0.85 | **0.25** | 资源"泛滥"后的折价 |
| `MinimumAcquireValueLate` | 0.5 | 0.75 | **2.2** | 后期买牌门槛（越高越挑） |
| `GainInfluenceStealAllianceMod` | 0.85 | 1.25 | **1.75** | 抢联盟的意愿 |

### 价格表（Hard 档，单位＝内部评分）

| 项目 | 价值 |
|---|---|
| 1 胜利点（后期） | **9** |
| 1 胜利点（前中期） | 6 |
| 1 阴谋牌 | 2.25–2.75 |
| 1 水 | 1.3–2.25 |
| 1 香料 | 1.5–2 |
| 1 影响力 | 2.25 |
| 1 兵 | 1.25 |
| 1 说服 | 0.66–1 |
| 1 太阳币（后期） | 0.25 |

换算下来 **1 分 ≈ 6 份香料**。所以"分不值钱"这个猜测可以排除了——

### 对"囤资源不拿分"的修正结论

VP 的定价是整张表里最高的，AI 并没有低估分。它的问题是**拿不到分**，机制有三条：

1. **冲突投入是绝对值，不跟收益挂钩。** `ConflictPosture*` 直接给"目标战力"区间
   （竞争多时 5–20，高潮期 10–15），而 `ConflictAverageValue` 固定为 **14**——
   这是照「一至三级混合牌堆」估的均值。你的牌堆全是二级/三级，每场收益都高于 14，
   于是按 14 算出来的投入目标系统性偏低，打不过就干脆不打。
2. **不打冲突的回合，它不会"空过投资"，而是花在当期正收益的资源动作上。** 这就是你看到的
   "抢了一堆没意义的资源"——不是它更喜欢资源，是它在拿不到分的回合里没有更好的事可做。
3. **后期买牌门槛 2.2 偏高。** 16 分制的后期比 10 分制长，门槛过高会让它后期几乎不补牌，
   牌组停止进化。

顺带两个在你这套 mod 下已经失效的参数：`GainSwordmasterEarly/Mid = 50`、
`GainHighCouncilEarly = 15`——四家开局就自带剑术大师/高等议会，这两项再也触发不了，
留着不碍事，但也说明这张表完全是按原版开局调的。

---

## 五之二、为什么它会「囤一堆没用的资源，却不拿分」

这不是错觉，是架构决定的。三个原因，按影响排序：

### 1. 它是贪心的，没有多回合投入

要赢一场高级冲突，通常要**提前 1–2 轮压兵**：这轮打兵牌、下轮揭示期才换成战力。
贪心评分看的是"这一步本身值多少"——打一张只给兵力的牌，当期收益是 `TroopValue`，成本是
少打一张有说服的牌。只要这一步算下来不赚，它就不做，于是兵一直囤在手里、冲突一直放。

**表现**：兵力/香料/太阳币数字很漂亮，冲突牌却常常只放 1–2 兵，VP 全靠买牌和结盟。

### 2. VP 只是一个价签，而且只有三档

`VictoryPointValueEarly/Mid/Late` 是"一分值多少"的定价，按 Early/Mid/Late 切换，
**不看你和胜利还差几分**。所以 14 分（离 16 差 2 分）和 6 分时，它的紧迫感完全一样。

### 3. 买牌门槛用的是资源估值，不是胜利路径

`MinimumAcquireValue*` 决定"这张牌够不够值"。这个"值"由资源估值累加而来，
所以一张只产香料/太阳币的牌很容易过线，而一张**两轮后才兑现的 VP 引擎**因为当期价值低而不过线。
结果是牌组越买越"富"，但产出全是资源。

> 这三条都指向同一个结论：**权重能缓解，架构不能根治**。第 1 条本质上需要前瞻，
> 权重只能通过"抬高兵的相对价值 + 抬高冲突姿态"去间接补偿。

---

## 六、增强路线（按性价比排序）

### Tier 0 · 切难度档（已作废）

原本打算用 `--ai-level 2`。首局实测证明剧本字段被大厅设置覆盖，而当前已经是 Hard，
所以这一层没有剩余空间。留着 `--ai-level` 只是为了让剧本数据自洽。

### Tier 1 · 按当前规则重标定权重（推荐，低成本）

用探针导出基线 → 只改下面几组参数 → 用 `ai_override.json` 注入，不改 bundle，随时可回滚。
预期收益：中高。这是唯一能针对「16 分 / 全高级冲突 / 4 工人」做定向修正的层级。

已备好一份实验档案 `mods/ai/profiles/vp_first.json`（复制为 `mods/ai/ai_override.json` 即生效）。
基线已拿到，这里用**绝对值**而不是倍数：

| 参数 | 原值 | 新值 | 意图 |
|---|---|---|---|
| `VictoryPointValueLate` | 9 | **12** | 16 分制后期更长，末期每分更该抢 |
| `ConflictInterestHandSwordValue` | 1 | **1.3** | 手牌当战力投入时更舍得 |
| `ConflictPostureNoCompetitorsUpperBound` | 5 | **7** | 无人竞争也别只放最低限度 |
| `ConflictPostureSomeCompetitorsUpperBound` | 12.5 | **17** | 有人争时把目标战力抬起来 |
| `ConflictPostureManyCompetitorsUpperBound` | 20 | **26** | 多人混战时肯压更多兵 |
| `ConflictPostureManyCompetitorsUpperBoundClimax` | 15 | **20** | 高潮期目标战力同步上调 |

只动了 6 项，全部是"投入强度"类参数（方向明确、不会反转语义）。
`ConflictAverageValue` 也有嫌疑（14 是按混合牌堆估的），但它的语义可能是"均值基准"也可能是"除数"，
方向不确定，暂不动——等 `[AIPROBE][ailog]` 里看到 AI 自述的冲突估值再决定。

### Tier 2 · 动态调制（中等成本，中等风险）

Harmony 补丁挂在 `AIConstantsRef` 的 getter 上，让权重随局面变化，而不是全程常数。
这是少数能真正补上「囤资源不拿分」第 1 条（无前瞻）的地方：

- **末期加压**：任一玩家距胜利 ≤ 3 分时，把 `VictoryPointValue*` 与冲突姿态上限按差距放大。
- **冲突前压兵**：下一场冲突奖励 ≥ 2VP 且自己落后时，临时抬高 `TroopValue*` / `ConflictInterest*`，
  等价于"提前两轮攒兵"的行为，但不需要搜索。
- **动态冲突均值**：按实际冲突牌堆改写 `ConflictAverageValue`，而不是用全局常数。
- **去噪**：把 `InjectNoise` 打空（或按难度分级），消除"明明能赢却走了次优"的随机失手。

实现难度低于重写评分逻辑，因为复用的仍是游戏自己的评估器；只需读 `PlayerEntity` / `Match` 的公开状态。

### Tier 3 · 换脑（不建议）

在 `AIProfileAbs.MakeChoice` 之后重排候选。问题：候选集与各自分值不外露，需要反射重建评分；`BestRankTimeout` 的超时约束会让重排极易降级。投入产出比最差。

---

## 七、已交付的改动

| 文件 | 内容 |
|---|---|
| `ai_probe/` | 新的 BepInEx 插件 `DuneAiProbe`，源码 + csproj，已编译并部署到 `game/BepInEx/plugins/` |
| `mods/tools/build_presets.py` | 新增 `--ai-level 0\|1\|2`；`meta.json` 记录 `ai_level` / `ai_label`；非 1 时产物加 `_aiN` 后缀 |
| `tools/TypeDump/Program.cs` | 输出方法静态标记、枚举字段的实际取值（本次靠它确认 `Easy=0/Medium=1/Hard=2`） |
| `mods/ai/ai_override.example.json` | 权重覆盖模板 |
| `dump/T_worm_ai.txt` | `AIConstantsRef` 全部 437 个属性的清单 |

探针 v2 的触发点改了三处，因为 v1 在 `SetupPlayer` 采集时三档 ref 全是 `null`——
AI 常量是**惰性初始化**的：

1. `StandardAIProfile..ctor`（AI 档案创建）
2. `AIProfileAbs.MakeChoice`（AI 第一次做决策）
3. `AIProfileAbs.AILog`（AI 自述日志，几乎每步都走）

哪个先拿到非空 ref 就在哪里落盘，只做一次。拿到后做四件事：

1. 打印 `AIConstants.FilePattern`、`persistentDataPath` / `dataPath` / `streamingAssetsPath` 下是否存在 `AIConstants*.json`；
2. 打印本局 AI 的 `PlayerEntity.AILevel`（实测应为 2）与前三档 ref 指针；
3. 把已建表的难度档权重导出到 `dump/ai_constants_runtime.json`；
4. 若存在 `mods/ai/ai_override.json` 或 `mods/ai/force_level.txt`，分别做逐条权重覆盖 / 整表拷贝。
   两个文件都不建时，探针只读不写，不改变任何难度。

另外还挂了一个 `AIConstants.SetInstance(aiLevel, ref)` 钩子。实测这一局**从未被调用**，
说明常量不是通过它注入的——它的用途仅剩"手动改写运行时表"，不能作为观察点。

---

## 八、验证协议

一次对局即可完成基线采集：

1. 双击 `启动mod版.bat`，进单人 → 标准剧本，随便打几回合后退出。
2. 取 `game/BepInEx/LogOutput.log` 中 `[AIPROBE]` 开头的行，以及 `dump/ai_constants_runtime.json`。
3. 关注三件事：
   - `[AIPROBE] first AI decision … AILevel=?` 打印的数字 → 与"困难"标签是否一致。
   - 三档权重是否都导出、彼此差多少 → 决定权重调优的起点。
   - `[AIPROBE][ailog]` 里的 AI 自述文本 → 直接看它为什么这么选。
   - `loadedConstants count` → 游戏实际创建了几套表。

A/B 建议：同一套预设、同一先手、同一冲突顺序（`--conflict fixed`），各打 3 局，比较 AI 的平均终局分数与冲突胜率。单局噪声很大，3 局只是下限。

---

## 九、边界与风险

- **仅限离线**：所有改动只作用于本地单人局，不涉及联机与排位。
- **超时风险**：不要试图在评估链路里加计算量；`BestRankTimeout` 会让 AI 退化为次优选择。
- **权重是耦合的**：单独放大 `StrengthValue` 会让 AI 无脑抢冲突、饿死经济。每次只动一组，且保留基线 JSON 以便 diff 回滚。
- **注入点尚未实机验证**：插件编译通过、钩子选的是已实证过的 `SetupPlayer`，但本次未运行游戏，`SetInstance` 钩子的参数绑定、`AIConstantsRef` 属性可写性都要靠第一局日志确认。

---

## 十、一句话回答

**能变聪明，而且性价比很高**：先把难度从 Medium 切到 Hard（一行数据），再按「12 分 + 全高级冲突 + 4 工人」重标定 6 组权重（一个 JSON 文件）。真要再进一步，才需要动代码去挂 getter 做动态调制。

# 接入 DeepSeek 让 AI 真人对战 · 可行性评估

**日期**：2026-10-31
**目标**：用 LLM 接管或辅助游戏 AI，与玩家对局
**方法**：协议层静态分析（IL2CPP / interop）+ 已有实测数据

---

## 一、结论

**技术上可行，但"每个选择都问一次 API"这条路不要走。** 推荐 **T2：策略层接管**——
每回合调用一次，LLM 只输出"这一轮怎么打"的策略参数，由游戏自己的评估器执行具体操作。

三个理由：

1. **延迟**：原生一次 AI 批次 0–406ms，API 往返 1–3s。一局约 150–250 次决策，
   全量接管会额外增加 5–10 分钟，且每次都要等。
2. **合法性**：LLM 直接输出操作，必然出现非法/不存在的目标。必须用游戏自己枚举出的候选集做约束，
   否则要靠反复重试，延迟再翻倍。
3. **错误代价**：非法的 `ISelectionCounter` 会累加 `PlayerEntity.ConsecutiveAITimeouts`，
   触到阈值即熔断；而循环卡死会抛 `AIInfiniteLoopException`，整局作废。

---

## 二、协议层事实（已核对）

| 项目 | 结论 |
|---|---|
| 待选列表可读 | `Canis.data.PendingSelection` 暴露 `Selection` / `Counter` / `ResponseParser` |
| 选项可枚举 | `ArchetypeCustomChoiceRequired.Buttons : List<SerializableAttributes>`，每个按钮带 `10140`(标题) / `920003`(图) / `940000`(费用) 等属性——游戏日志里已经是这个格式 |
| 响应类型 | `SelectionWithTargets` / `MultipleSelectionWithTargets` / `GameCustomChoice` / `SelectionFinished`（Networking 程序集） |
| 注入点 | `HasExecutionStack.ConsumeCounter(counter, inputMessage, player)`；AI 侧对应 `AIChoice.SendResponse()` |
| 稳定触发点 | `HasExecutionStack.RunAIResponses`（实测一定会触发）；`AIProfileAbs.MakeChoice` 实测**从未触发**，不可依赖 |
| 可控扩展点 | `AIProfileAbs.GetWeightMap(...)` 返回**可变** `Dictionary<EntityID,int>` |

> 关键：**选项文本不需要自己渲染**。游戏已经把每个按钮的标题/费用/属性放在 `Buttons` 里，
> 直接序列化即可构造 prompt，不需要解析 UI。

---

## 三、三档方案

| | T1 观察者 | **T2 策略层（推荐）** | T3 全接管 |
|---|---|---|---|
| LLM 输出 | 解说 + 建议（不接管） | 每回合一份策略 JSON | 每一次选择 |
| 调用频率 | 玩家回合一次 | 每回合一次（约 8–10 次/局） | 150–250 次/局 |
| 额外耗时 | 0（异步，不阻塞） | 8–10 × 2s ≈ 20s/局 | 5–10 分钟/局 |
| 非法操作风险 | 无 | 无（策略→参数，不碰协议） | 高，需校验 + 重试 |
| 实现量 | 小 | 中 | 大 |
| 可控性 | 只能看 | 高，随时回滚 | 低，出错整局崩 |

### T2 的具体形态

每回合开始时（或 AI 首次行动前）：

```
输入：局面摘要（四家分数/资源/影响力、当前冲突与奖励、AI 手牌、市场 5 张、可选空间）
      + 候选动作（取 GetWeightMap 排序后的 Top-K，带游戏自己算的分数）
输出：{"conflict": 2, "buy": "card.x", "resource_focus": "spice", "aggression": 0.8}
```

插件把这份 JSON 翻译成**权重覆盖**（`ai_override.json` 那一套）和**目标偏好**，
再交给原生评估器执行。好处：

- 每个具体操作仍然由游戏自己枚举和校验 → **不可能非法**；
- 一次调用换取整轮行为改变，延迟可接受；
- 失败（网络/超时/解析错）时原样回退到原生 AI，不中断对局。

### T1 的最小实现（建议作为第一步）

只做"读局面 → 发 API → 写日志"，不接管任何决策。
成本最低，能先把 API 链路、prompt 质量和 token 预算跑通，再决定要不要往 T2 走。

---

## 四、延迟这件事，可能没有想象中糟

游戏自带 `AIChoice.AIDecisionDelay` 与 `Match.AIDecisionDelayForSelection`——
**它本来就在故意让 AI「想一会儿」**。所以 1–2 秒的等待在观感上是"AI 在思考"，而不是"卡住了"。

前提是：这段时间**不能冻住 UI**。目前尚未确认 AI 是否在主线程同步执行
（元数据有 `Canis.matchThread` 命名空间和 `MatchPauseTypes.NonMainStack`，暗示按线程组织执行栈，
但公开 API 无线程类型）。这一点决定了 T3 能不能用、T2 会不会掉帧，需要实测。

---

## 五、工程注意

1. **API Key 不入库**：放 `mods/ai/deepseek_key.txt`，加入 `.gitignore`；插件启动读文件，不写死。
2. **超时必须自己设**：上一轮已确认没有墙钟看门狗，网络请求卡住 = 对局卡住。
   HttpClient 必须设 `Timeout`（建议 8s），并准备离线降级。
3. **失败即回退**：任何异常都退回原生 AI，宁可变笨也不能卡死。
4. **prompt 要小**：只发"当前可选 + 关键状态"，不要把整局历史塞进去；一局 10 次调用要控制成本。
5. **可复现**：把每次请求/响应对（脱敏后）落到 `dump/llm/`，便于复盘为什么它这么打。

---

## 六、不建议做的事

- 每次选择都调 API（T3）：延迟与非法风险都不划算。
- 让 LLM 直接生成"按钮索引"：索引会随版本变化，必须用稳定 ID（`ArchetypeID` / `EntityID`）。
- 在 AI 线程里同步等 HTTP：没有看门狗，一旦超时会表现为游戏假死。

---

## 七、建议的推进顺序

1. **T1 观察者**：跑通 API + 局面序列化，产出"AI 教练日志"。（1 次开发，零风险）
2. 用 T1 的日志校验 prompt 质量：它能否说清"我该抢哪场冲突"。
3. **T2 策略层**：把 LLM 输出映射成权重覆盖，A/B 对比原生 AI。
4. T3 只在确认线程与延迟可控后再评估。

---

## 八、T1 观察者实测记录（10-31）

### 注入点与数据源

- 唯一可靠观察点：`worm.canis.ai.WormAIProfile.MakeChoice(SelectionMessage, WormAIChoiceContext)`。
  `AIProfileAbs.MakeChoice` 那条从没触发（单人标准局的 AI 走 worm 自己的实现）。
- 待选文本直接取 `SelectionMessage.ToString()`。它是紧凑格式（约 400–1000 字符），不是 JSON：
  `[SelectionWithTargetsRequired] Prompt … TargetMap <[能力名(短ID): [XTargetInformation] 细节]>`。
  早期版本把序列化 JSON（6KB+）发出去，`Continuation`/`SerializedStack` 占了绝大部分篇幅，
  真正要看的目标信息反而被 6000 字截断挤掉。

### 三轮修正

1. **认错人**：`LatestRespondedPlayer` 是「刚答完的人」而不是「正在被问的人」，12/15 的请求其实是人类玩家的。
   改挂 `WormAIProfile.MakeChoice` 后，`__instance.Player` 天然就是被问者，问题消失。
2. **机器标识**：GUID / `worm.*` / `button.*` 全部翻成人话（`mods/ai/name_map.tsv`，2032 条，按内容哈希去重）。
3. **validTargets 不可读**（本轮）：目标给的是实体实例 ID，名称表里没有，模型只能说「无法判断」。
   解法链：`new EntityID(str)` → `match.TryGetEntity(id, out ent)` → `ent.EntityName` +
   `ent.GetArchetype().ArchetypeID`（原型 GUID）→ 名称表；能力类实体再兜一层 `button.<类名>`。

### 本轮结论（22:0x 一局，14 条样本）

- 名字全部解析成功（渗透 / 沙虫骑士 / 斯第尔格 / 印章戒指 / 匕首 / 家族原子弹…），
  单条待选从 6000 字符降到 300–800，延迟 1.9–2.9s，HTTP 全 200。
- 模型不再编造牌名，而会明确说「缺目标信息」——这是信息缺失，不是模型问题。
- 剩余缺口：目标信息类型只给数量（`ValidTargets (6)`）。
  `SelectionWithTargetsRequired.TargetMap` 里有 `EntityListTargetInformation.ValidTargets`，
  已加「目标明细」段把它逐条解析成名字（`Targets.Detail`），待下一局验证。
- 同名不同副本（两个「渗透」）目前只靠短 ID 区分，模型会要求「按目标区分」，属预期行为。

---

## 九、效果文本：从"这是什么"到"这能干什么"（10-31 深夜）

### 问题

补齐名称与目标后，模型仍有 11/30 的回答在说「没有具体效果文本，无法判断」。
它能看懂选项叫什么、能放哪儿，但不知道打出这张牌会发生什么——这恰好是唯一影响选牌的信息。

### 数据来源

本地化表（`StreamingAssets/Localization/win/localization`，zh_CN 段）里本来就有：

- 卡牌 `card.<set>.<image>.agent / .reveal / .text / .ability / .static`（含 `.text1`、`.text.1` 两种写法）
- 领袖 `worm.leader.<image>.staticability.desc / .signetability.desc`

`mods/tools/build_effect_map.py` 把同一个 image 的几段按「特使→展示→文本→能力」顺序拼成一句，
剥掉 `<br>`、`<sprite>`、`<size>`、`<voffset>` 等富文本并截到 70 字，产出 `mods/ai/effect_map.tsv`。

两张键：`GUID` 与 `name:<中文名>`。前者精确，后者兜底——很多卡有多个 GUID，只有其中一个带文本。

### 接入

`Targets.Detail` 给每个**选项**（targetMap 的键）附一句 `〔效果〕`；
`Resolve` 解析时顺手把「实体实例 ID → 原型 GUID」记进缓存，避免二次查库。
`ValidTargets` 里的目标（卡/空间）暂不附效果，否则 prompt 会膨胀。

### 覆盖率（如实记录）

469 条（224 GUID + 245 名字）。基础包（Imperium）的多数卡在 zh_CN 段里**只有 title、没有效果文本**，
所以 `沙虫骑士`、`外交`、`印章戒指` 这类仍然是空的；有文本的主要是起义/不朽/家族扩展。
插件侧对查不到的选项不写任何占位，模型看到的就是光牌名——比塞假信息好。

### 附带清理

`Coach.Humanize` 增加富文本清理：`<sprite name=DrawCard>` → `DrawCard`（保留图标语义）、
其余版式标签直接删。之前 `<sprite name=BuyTech_MinusOne> —或— …` 这种会原样进 prompt。

---

## 十、三个收尾项（10-31 深夜，build 2026-10-31g）

### 1. 基础包（Imperium）卡牌效果文本：本地化里不存在

逐项核对过：

- `Localization/win` 的 zh_CN 与 en_US 两份文本键集合完全相同（各 4784 条），
  `card.imperium.wormriders.*` 只有 `.title`，没有 `.agent` / `.reveal` / `.text`；
- 卡牌资源包 `9b0d29d4…bundle` 里，沙虫骑士预制体的 `locKey` 也只有 `card.imperium.wormriders.title`；
- `Localization/gamecore` 段（1866 条）同样没有。

结论：**基础包的效果是图标化表达的**（能力实体靠图标 + 属性渲染），没有可抽取的文本源。
要补只能走两条路：OCR 卡面贴图（375 张，准确率存疑）或对接公开中文卡表（需人工/联网录入）。
当前不做，宁缺勿造。

### 2. 同名实体：阵营轨道与部队

存档实测（三个存档一致）：`FactionTracks` 的四个子节点顺序固定为
`Emperor / SpacingGuild / BeneGesserit / Fremen`，属性 `900009` 也是这个枚举。
文本里原本只显示 `FactionTrack(28dcf898)`、`Troop(2ee0d4ac)`——同名字段，模型无法区分。

- 轨道：按 `PositionInParent` 取序号，映射到 `lookup.<faction>withicon` 得到 皇帝/宇航公会/贝尼·杰瑟里特/弗雷曼人，
  并打印 `[TARGETS] faction track idx=N -> 名字` 便于核对；
- 部队：彼此等价，直接写「部队」，不再带无信息的短 ID。

### 3. 能力类实体的效果

`FamilyAtomicsAbility` 这类实体的 `EntityName` 就是能力类名，中文名与文案都在
`worm.canis.abilities.<类名>` 下。之前只试过 `button.<类名>`，所以名字与效果都空着。

- 名字：仍走 `button.<类名>`（那是短名），拿不到再用类名；
- 效果：走 `worm.canis.abilities.<类名>`，例如家族原子弹 → 「移除并替换公共牌池？」。

`name_map.tsv` 同时把 `lookup.*` 纳入（+62 条），供阵营轨道取名用。

---

## 十一、对接公开卡表补齐基础包（build 2026-10-31g 数据侧，无需改 DLL）

第十节确认基础包效果文本在游戏内不存在，只能外部补。做法：

1. 取公开卡表（Dune Imperium Card Database：基础游戏 43 张帝国牌库 + 起始牌组 + 特殊牌），
   整理成 `mods/ai/card_text_extra.tsv`，三列：`英文卡名 <TAB> 中文效果 <TAB> 来源`。
2. **键用英文卡名**，由 `build_effect_map.py` 借 image 反查官方中文名：
   `en_US` 的 `card.*.<image>.title` → image → `zh_CN` 的同键 → 中文名。
   这样避免手写中文名对不上游戏内译名（第一版手写 26 条里有 20 条对不上，全部作废）。
3. 合并进 `effect_map.tsv` 的 `name:<中文名>` 键，已有条目一律不覆盖（扩展包文本优先用游戏自带）。

结果：`effect_map.tsv` 505 条，其中人工补充 36 条；基础包高频卡（外交/印章戒指/匕首/实验/
斯第尔格/奢靡/沙虫骑士）全部有文本。全卡覆盖率 274/436 ≈ 63%（此前 51%）。

仍缺的是起义（Uprising）、Ix、不朽等扩展里的同名新卡（走私/尽忠职守/技术交流/秘密/沙漠战术/
为了全人类/弗雷曼工具包/运送资源/无畏舰/伊克斯舰组/巨沙虫夏胡鲁…），公开卡表这次没覆盖到，
需要另找扩展卡表再补一轮。

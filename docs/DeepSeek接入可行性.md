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

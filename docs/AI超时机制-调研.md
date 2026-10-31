# AI 超时机制调研（只读）

**日期**：2026-10-31
**目的**：确认是否存在「AI 思考过久就报错」的机制，为「换脑」（自建 AI 决策器）评估风险
**方法**：IL2CPP 元数据 + interop 程序集静态分析；已有探针的实测日志对照

---

## 一、结论

1. **没有任何「思考太久」的墙钟看门狗。** 游戏不会因为你算得慢而抛异常、踢掉 AI 或终止对局。
2. 唯一与时间有关的入口是 `AIAlgorithmUtil.BestRankTimeout(elems, ranker, long timeout)`，
   它是**局部排序预算**：到点就返回当前最优（甚至返回 null），由调用方走兜底，
   属于"降级"而不是"报错"。
3. 真正会抛异常的是**迭代次数**保护，与耗时无关：

   ```
   HasExecutionStack.MaxRunIterations
   HasExecutionStack.CheckForInfiniteLoop(Int32 runIterations)
     → AIInfiniteLoopException
     → MatchErrors.INFINITE_LOOP_DETECTED
     → DBGNotifications.InfiniteAILoop
     → MessageAction.LogAIInfiniteLoop（记录 gameMessage / accountID）
   ```

   它数的是对局循环跑了多少轮，不是毫秒。**只有死循环会触发它，慢不会。**
4. 另有「连续失败」熔断：`PlayerEntity.ConsecutiveAITimeouts` 与 `ConsecutiveTimeoutThreshold`，
   配 `AINoResponse` / `AIUnexpectedSelection` 两种失败标记。这里的 timeout 指
   "AI 连续没给出合法响应"，不是"算得久"。**返回非法选择会累加，返回得慢不会。**

---

## 二、四类机制对照

| 机制 | 触发条件 | 结果 | 与耗时关系 |
|---|---|---|---|
| `BestRankTimeout` | 单次排序超过 timeout 参数 | 返回 best-so-far 或 null → 兜底评估器 | 直接相关，但**静默降级** |
| `MaxRunIterations` / `CheckForInfiniteLoop` | 对局循环迭代数超限 | `AIInfiniteLoopException` + 错误码 + 通知 | 无关 |
| `ConsecutiveAITimeouts` | 连续 N 次无合法响应 | 熔断（阈值 `ConsecutiveTimeoutThreshold`） | 间接（卡死才会发生） |
| `AIDecisionDelay` | 每次决策后 | 仅延迟发送，装样子 | 反向（人为变慢，不影响正确性） |

---

## 三、实测证据

| 证据 | 内容 |
|---|---|
| 泛型补丁实验 | `BestRankTimeout` 是泛型静态方法（2 个重载），Harmony 打补丁直接失败：`HarmonyException: IL Compile Error`，`ok=0 fail=2`。**无法通过补丁放宽超时。** |
| 批次耗时 | 一局开局阶段 4 次 `RunAIResponses`：`0ms / 406ms / 0ms / 0ms`。406ms 出现在首次决策（含类型初始化与领袖选择）。 |
| 无超时痕迹 | 全程日志里没有 `INFINITE_LOOP_DETECTED`、`InfiniteAILoop`、也没有 Fallback 相关记录。 |
| 兜底链存在 | `FallbackAIStrategy` / `FallbackAISelectionEvaluator` / `FallbackAIValuationFunction` 三类兜底都实现了，说明设计上预期"AI 可能拿不出结果"。 |

---

## 四、对「换脑」的三条硬约束

### 1. 不要指望绕过 `BestRankTimeout`

它是泛型静态方法，工具链打不上补丁（已实测）。任何依赖"给它更多毫秒"的方案都走不通。
如果你的脑子复用了它的排序入口，超时会**静默**退化成兜底结果——表现为"改了没反应"，
而不是报错。这是换脑时最容易误判的失败模式。

### 2. 必须自己保证终止

没有墙钟保护，只有迭代计数保护。**递归/循环没写好会真的把对局卡住**，
然后由 `CheckForInfiniteLoop` 抛 `AIInfiniteLoopException`，对局进入 `MatchPauseTypes.Error`。
这是唯一一条"会报错"的路径，代价是整局作废。

### 3. 线程归属尚未确认

元数据里有 `Canis.matchThread` 命名空间，`MatchPauseTypes` 里也有 `NonMainStack`，
暗示执行栈是按线程组织抽象的；但公开 API 里没有线程类型，**无法静态确认 AI 是否在主线程同步执行**。
若同步，重计算会掉帧（实测 406ms 那次没有明显卡顿反馈，但样本只有 1 次）。

---

## 五、接入点（基于已确认事实）

| 接入方式 | 位置 | 代价 | 说明 |
|---|---|---|---|
| **半换脑**（推荐） | `AIProfileAbs.GetWeightMap(swtr, func)` 的返回值 | 低 | 实测返回**可变** `Dictionary<EntityID,int>`，在其上加减即等价于外部重加权，不需要碰决策器 |
| 换评估器 | `SelectFromEvaluator.Evaluate(AIChoiceContext)` / `SelectFromTargetsEvaluator.Evaluate(...)` | 中 | 非泛型，可补丁；但需要自己产出 `AIChoice` |
| 全换脑 | `HasExecutionStack.RunAIResponses` 下游，自己产出 `ISelectionCounter` | 高 | 等于重写响应协议层，风险最高 |

补充：`AIProfileAbs.MakeChoice` 在实测中**从未触发**（开局阶段 AI 选领袖不走它），
所以它不能作为换脑的唯一入口；真正稳定触发的是 `HasExecutionStack.RunAIResponses`。

---

## 六、仍未确认的一项

**超时是否真的在发生**，目前无法观测——因为 `BestRankTimeout` 打不上补丁，拿不到它的实参和返回值。
可行的低成本验证：在非泛型的调用方（`SelectFromEvaluator.Evaluate`）上记时间戳，
对比"候选数量"与"耗时"，若耗时恒定贴着一个整数毫秒上限，那就是在撞超时。

在那之前，按现有证据：**超时是保险丝，不是思考预算。**

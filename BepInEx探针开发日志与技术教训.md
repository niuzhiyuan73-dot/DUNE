# BepInEx 代码 Mod 探针开发日志与技术教训

**项目**：Dune: Imperium 离线模组（桌面 `DUNE` 副本）
**周期**：2026-10-30，共 13 个插件迭代
**目标**：在单人标准模式中，给人类与 AI 的起始牌组各加入一张新卡
**当前状态**：环境与链路已验证可用；**注入尚未成功**，卡在一个具体的 API 缺口上

---

## 一、结论摘要

1. **BepInEx 在 Unity 6 上完全可用**——这是本轮最大的确定性收获，此前它是整条路线的最大风险。
2. 桩链路已验证：插件加载 → interop 类型可用 → Harmony 补丁在真实对局中命中。
3. 注入失败的原因**不是游戏不开放**，而是三个层面叠加：
   - 我的反射写法在 Il2Cpp 上有系统性缺陷（详见第四节，这是主要成本来源）
   - 单人模式的初始化链路与挑战模式不同
   - 「卡名 → 原型 ID」的映射入口在公开 API 中不存在
4. 建议转向**数据路线**（给 `StandardSolo` 补 `wormPlayers`）验证成本更低、成功率更高。

---

## 二、环境与技术栈（已确证）

| 项 | 值 |
|---|---|
| 引擎 | Unity 6000.3.17f1（Unity 6） |
| 脚本后端 | IL2CPP，运行时 .NET 6.0.7 |
| Mod 加载器 | BepInEx **6.0.0-be.791**（IL2CPP / win-x64），自带 .NET 6 运行时 |
| interop 程序集 | 132 MB，关键：`worm-canis.dll` 12 MB、`Canis.dll` 4.5 MB |
| .NET SDK | 8.0.427，用户目录 `C:\Users\17789\dotnet`（无需管理员） |
| 插件工程 | `DUNE\probe_plugin`（net6.0，直接引用 interop DLL） |
| 离线分析工具 | `DUNE\tools\ApiDump`（自写，解析 .NET 元数据导出 API 清单） |

### BepInEx 加载验证日志

```
[Info :   BepInEx] Running under Unity 6000.3.17f1
[Info :   BepInEx] Runtime version: 6.0.7
[Message:   BepInEx] Chainloader startup complete
[Info :Il2CppInterop] Registered mono type ... in il2cpp domain
```

无异常。唯一警告 `Class::Init signatures have been exhausted, using a substitute!` 属已知无害。

---

## 三、已确证的 API 事实（下次可直接复用）

### 3.1 初始化链路

**单人标准局与挑战局都走 `SetupPhase`**，执行顺序已被钩子实测确认：

```
BeginSetup → SetupPlayer → CreateDecks → CreateDecksExtra
```

`DeckSetupPhase`（BeginDeckSetup / CompleteDeckSetup / Selection）在**单人标准局完全不触发**——这是 v4 失败的原因。

### 3.2 玩家对象

| 结论 | 说明 |
|---|---|
| 类型 | `worm.canis.entities.WormPlayer` |
| **没有 `IsHuman`** | 判别字段是 `AIProfile`：人类为 `null`，AI 有值 |
| 捕获方式 | `SetupPlayer(WormPlayer player)` 的**方法参数**最可靠（`__args[0]`） |
| 玩家集合 | `WormMatch.PlayerTurnOrder` 在 `CreateDecks` 时为空，不可依赖 |

### 3.3 牌组容器

```
player.Deck  →  worm.canis.entities.WormPlayArea
继承链: WormPlayArea <- WormEntity <- Entity <- ObjectAttributes <- ...
```

- **不是列表**，是实体容器；卡牌作为子实体挂载
- 正确的添加方法：`AddDescendent(Entity)`，另有 `AddChild(Entity, Nullable<...>)`
- 容器内实体通过 `Deck.Children`（`List<...>`）访问
- **`CreateDecks` 时 `Deck.Children` 计数为 0** —— 此时尚未发牌

### 3.4 原型体系

| 项 | 结论 |
|---|---|
| 每张卡 | 是一个 C# 类：`worm.canis.archetypes.<类别>Archetypes.<内容集>.<卡名>`，如 `ImperiumArchetypes.BaseSet.Dagger`，含 `archID` 属性 |
| 规模 | `ImperiumArchetypes` 类型 870 个 |
| `WormArchetypes.Instance.archs` | 非公开属性，`Lazy<Il2CppReferenceArray<Archetype>>`，**长度 534** |
| **`archs` 内容** | 通用 `Archetype` 实例（实体类型级），**不含具体卡牌** |
| `WormArchetypes.CreateEntity` | 两个重载：`(Archetype, Match, PlayerEntity, Entity)` 与 `(ArchetypeID, Match, PlayerEntity, Entity)`，均返回 `Entity` |
| `Archetype.Get(ArchetypeID)` | 存在 |
| **`ArchetypeIDUtils.Lookup`** | **只接受 `ArchetypeID`**，不存在「卡名 → ID」的入口 |
| `WormArchetypes.AllArchetypes()` | 菜单阶段返回**空集**，不可用于启动时枚举 |

### 3.5 其他可用接口

- `WormArchetypes.Instance.Archs`（公开属性不存在，需反射取 `archs`）
- `Archetype.ConstructAbilities` / `Build` / `Deserialize` / `AbilityIDsAttribute`
- 游戏内建能力 `worm.canis.abilities.TriggeredAbilities.Skirmish.AddCardToStartingDeck`——是**触发器类**，需完整执行上下文，不是可直接调用的工具函数

---

## 四、踩过的坑（按代价排序）

### 坑 1：用反射操作 Il2Cpp 对象（代价最大，浪费 5 个版本）

**症状**：所有属性读出来是 `null`，所有集合枚举为空。

**根因**：
- Il2Cpp 集合**不实现** `System.Collections.IEnumerable`，也没有 `Count` / `Item`
- 接口方法用**显式实现命名**（`System_Collections_IEnumerator_MoveNext`），按 `"MoveNext"` 精确查找必然失败
- Il2Cpp 的 `IEnumerator<T>` 不含公开 `MoveNext`，**不能用 `foreach`**

**正确做法**：
```csharp
// 模糊匹配方法名，而非精确名
m.Name.IndexOf("MoveNext", StringComparison.Ordinal) >= 0
// 或直接用强类型调用（推荐）——interop 程序集已提供编译期类型
```

### 坑 2：`Convert.ToInt64(IntPtr)` 抛异常被吞

```csharp
Convert.ToInt64(p.GetValue(o))   // IntPtr → InvalidCastException → catch → 0
```
导致日志里**所有对象指针都是 0**，看起来像「拿不到对象」，实际对象是好的。直接 `(long)(IntPtr)value`。

### 坑 3：反射读 interop 属性返回的是接口对象

`Archetype.EntityName` 返回类型是 `Canis.entities.IEntityName`（接口），不是 `string`。
`ToString()` 得到的是**类型名**而非实际值。编译器早就提示过 `CS0029: 无法将 IEntityName 隐式转换为 string`。

### 坑 4：`grep` 在压缩资源上不可靠

411 个 AssetBundle 全部 LZ4HC 压缩。直接 `grep` 原始字节严重漏检，曾误判「卡牌预制体只有 3 个」（实际 374 个）。**必须先解压**。

补充：`grep -c` 对二进制文件按「行」计数，结果恒为 1，应使用 `grep -o | wc -l`。

### 坑 5：`dnfile` 解析 12 MB interop 超时

全表解析导致超时。改为自写 `ApiDump`（`System.Reflection.Metadata`）后，2361 个类型 / 24466 个方法瞬间完成。

### 坑 6：误判初始化阶段

v4 只挂 `DeckSetupPhase.CompleteDeckSetup`，在单人局中一次都没触发。**不同游戏模式的初始化链路可能完全不同**，应先做「全阶段打点」再定位。

### 坑 7：编译期错误信息被编码吞掉

PowerShell 输出中文乱码（GBK/UTF-8 混用），只能靠错误码（CS0246 / CS1061 / CS8917）判断问题类别。

---

## 五、迭代记录（v1 → v13）

| 版本 | 假设 | 结果 | 结论 |
|---|---|---|---|
| v1 | 能挂上 Harmony 即成功 | ✅ 补丁命中 | 桩链路可用 |
| v2 | 强类型枚举原型 | ❌ CS8917 类型推断失败 | 条件表达式在 interop 类型上不可用 |
| v3 | 反射打印签名 | ✅ 拿到 CreateEntity / AllArchetypes 签名 | 建立 API 地图 |
| v4 | 钩 `DeckSetupPhase` | ❌ 未触发 | 单人局不走该阶段 |
| v5 | 钩 `SetupPhase` 全阶段 | ✅ 4 个方法全触发 | 锁定 CreateDecks |
| v6 | `PlayerTurnOrder` 取玩家 | ❌ 玩家数 0 | Il2Cpp 集合枚举失败 |
| v7 | 从 `SetupPlayer` 参数抓玩家 | ⚠️ 抓到但 `IsHuman` 恒 false | `WormPlayer` 无此属性 |
| v8 | 用 `AIProfile` 判别 + 注入 | ❌ 牌组 -1，原型找不到 | `Deck` 未建、枚举仍失败 |
| v9 | 修 Il2Cpp 枚举（GetEnumerator） | ❌ 原型 0、指针 0 | 显式接口名 + IntPtr 转换双重问题 |
| v10 | 改强类型 | ❌ 编译期暴露 Deck 非列表 | `Deck` 是 `WormPlayArea` |
| v11 | 运行时打印 Deck 结构 | ✅ 拿到 `AddDescendent` | 找到正确添加方法 |
| v12 | `archs` 数组 + AddDescendent | ❌ 534 项中无 Dagger | `archs` 是实体类型原型 |
| v13 | 打印 `ArchetypeIDUtils` 签名 | ✅ 确认无「名字→ID」入口 | 缺口定位 |

---

## 六、未解问题

1. **卡名 → `ArchetypeID` 的映射入口在哪？**
   公开 API 中不存在。推测：每张卡的类在构造时自行向内部集合注册，`archID` 由代码赋值。

2. **卡牌实体真正创建（发牌）的时机？**
   `CreateDecks` 时 `Deck.Children` 为 0，说明发牌在更晚的阶段。需要继续打点 `DealCards` / `StartNewMatch` / 首次 `DrawCards`。

3. **`archs` 的 534 项到底是什么？**
   `EntityName` 返回 `IEntityName` 接口对象，`ToString` 得到类型名，实际值尚未取出。

---

## 七、建议路线

> **2026-10-30 补充：路线 A 已验证成功（见第十节）。代码路线暂停，数据路线成为主线。**

### 路线 A：数据路线（推荐先试，成本极低）

给 `StandardSolo` 剧本补上 `wormPlayers` 数组，用 `StartingDeck` 给人类与 AI 各加卡。

**依据**：官方 98 个剧本中，44 处 `LeaderOptions`、多处 AI 专属牌组（如 Campaign108 给 3 个 AI 各配 11 张显式牌组）证明该字段对 AI 有效。挑战模式已实测生效。

**风险**：`StandardSolo` 模板刻意不含 `wormPlayers`（玩家是运行时生成的），补上后是否被读取未知——但这是个纯数据实验，失败也能立刻回滚。

### 路线 B：代码路线（继续，但需再 2–3 轮打点）

1. 继续打点找到发牌时机（`DealCards` 等）
2. 在发牌前用 `CreateEntity` + `AddDescendent` 注入
3. 卡牌原型的获取：改为「遍历 `archs` 并按 `ArchetypeID` 匹配」或「从已存在的同名牌实体反查其 Archetype」

### 路线 C：混合

先用路线 A 验证「数据层能否给标准模式加卡」，若可行则完全不需要代码注入，本轮所有障碍自然消失。

---

## 八、可复用的工程资产

| 路径 | 用途 |
|---|---|
| `DUNE\probe_plugin\` | 插件工程（当前 v13 诊断版） |
| `DUNE\tools\ApiDump\` | .NET 元数据 → API 清单导出工具 |
| `DUNE\dump\api_worm_canis.txt` | 4729 类型 / 78346 方法 |
| `DUNE\dump\api_canis_full.txt` | 2361 类型 / 24466 方法 |
| `DUNE\dump\api_utils.txt` | 323 类型 / 2082 方法 |
| `DUNE\game\BepInEx\LogOutput.log` | 最近一次运行日志 |
| `DUNE\game\BepInEx\interop\` | 132 MB interop 程序集（可直接引用） |

---

## 九、方法论教训（最重要的三条）

1. **先打点，再动手**。每轮先让代码只观测、不修改，拿到真实类型和方法签名后再写逻辑。我前 4 版都是在猜 API，成本远高于一次打点。

2. **编译期错误是资产，不是障碍**。v10 的编译失败直接告诉我 `Deck` 是 `WormPlayArea` 而非列表，比运行时试错快十倍。应主动用强类型调用去「问」编译器。

3. **区分「工具错误」与「目标限制」**。这 13 轮里绝大多数失败源于我自己的反射写法错误，而不是游戏不开放。在得出「做不到」的结论前，必须先排除工具侧问题——否则会错误地放弃一条本可走通的路。

---

*本文档记录的是静态分析与运行时打点的实证结果，不含推测性结论；标注为「推测」的条目已明确说明。*

---

## 十、路线 A 验证成功（2026-10-30 补记）

### 实验设计

给 `StandardSolo` 剧本补上 `wormPlayers` 数组（1 名人类 + 3 名 AI），
每名玩家写入 10 张显式牌组（含「晶刃」）与 2 太阳币 / 1 香料 / 1 水。

补丁包：`mods/out/v3solo_b56aa8ad….bundle`（54,916 → 55,962 B），
回读校验通过后安装进副本，**无任何代码注入**。

### 实测结果

游戏内截图确认：

- 人类玩家起始手牌第 5 张为 **「晶刃」**
- 人类资源显示 **2 太阳币 / 1 香料 / 1 水**
- **三名 AI 同样获得 2 太阳币**，且牌组同为 10 张

### 结论

1. **`wormPlayers` 在单人标准局被完整读取**——尽管原版模板里该数组为空。
2. **`StartingDeck` 对 AI 同样生效**，人与 AI 可分别配置。
3. 因此：加卡、删卡、改牌组构成、调起始资源、限定领袖、控制科技池，
   **全部可以在数据层完成，不需要代码**。

### 对前 13 轮工作的重新定位

代码注入失败并非白费：

- BepInEx 在 Unity 6 上可用，这一结论让未来任何代码级需求（新卡效果、新机制）有了地基
- 已确证的 API 地图（第五、六节未解问题）在需要真·新卡时可以接着用
- 但**常规玩法改动不应再走代码路线**——数据层已证明足够，且风险与成本低一个数量级

### 仍需代码的情形

只有两类需求绕不开代码：

- **全新卡牌效果**（新 Ability 行为）——数据层的 AbilityID 只能组合既有能力
- **运行时动态修改**（例如对局中途改变规则）

其余一切优先走 JSON。

---

## 十一、第二轮：拦截创建（v14 → v19）全部失败

路线 A 成功后，目标转向「造新卡 / 改卡 / 改领袖」。这三件事共用同一个前提：
**拿到卡牌实体背后的 Archetype 或 Ability 定义**。第二轮就是围绕这个前提打的。

### 11.1 关键结论：拦截实体创建是死路

| 版本 | 钩子 | 结果 |
|---|---|---|
| v14–v16 | `SetupPhase.*` + `RoundPhase.DrawCards` | 触发正常，但手牌读数恒为 0 |
| v17 | `WormArchetypes.CreateEntity`（2 个重载） | **零调用** |
| v18 | 同族 6 个方法 + `AddDescendent` 兜底 | 闪退（见坑 8） |
| v19 | 12 个创建/反序列化方法（`DeclaredOnly`） | **全部零调用** |

零调用的原因：IL2CPP 把这类内部调用内联为 native 直连，托管层的 Harmony 补丁拦不到。
**这不是参数写错，是路径本身不可拦截。** 继续在这条路上投入不会有结果。

### 11.2 意外收获：崩溃日志吐出了真实签名

v18 崩溃时 HarmonyX 打印的警告里带着完整签名，这比我自己猜的准：

```
Canis.entities.Entity Archetypes::CreateEntity(
    Match m, ArchetypeID archetypeID, Entity parent,
    PlayerEntity player, Nullable<int> positionInParent)
```

**有 `parent` 和 `positionInParent`** —— 说明游戏提供了「把实体创建到指定容器的指定位置」的完整能力。
之前我只看到 4 参数简化版，那个版本从未被调用过。

### 11.3 v14–v19 的迭代记录

| # | 假设 | 结果 | 结论 |
|---|---|---|---|
| v14 | 卡牌在 Setup 阶段已存在 | 手牌恒为 0 | 发牌在更晚阶段 |
| v15 | 钩 `RoundPhase.DrawCards` | 触发 6 次，手牌仍 0 | 看错了容器 |
| v16 | 区分 `Hand.Children` 与 `HandCards()` | 两者皆 0 | 同上 |
| v17 | 钩 `CreateEntity` | 零调用 | 创建不可拦截 |
| v18 | 同族 6 方法 + `AddDescendent` | **崩溃** | 重复打补丁 → 栈溢出 |
| v19 | 12 个方法，`DeclaredOnly` | 零调用 | 确认 native 内联 |

---

## 十二、第三轮：实体树突破（v20 → v23）

### 12.1 转折点：不问「怎么创建」，问「现在在哪」

v20 放弃拦截，改为遍历 `WormMatch.WormPlaymat` 的实体树。一次就拿到了全貌：

```
WormPlaymat | Playmat | 子=25
  WormPlayer | Player | 子=16
    Entity | Hand    | 子=0      ← 一直是 0，我早期一直盯这里
    Entity | Deck    | 子=10     ← 卡牌实际在这里
    Entity | Supply  | 子=33
    Entity | Leader  | 子=2
```

**在此之前所有「手牌为 0」的困惑，根因就是看错了容器。**

### 12.2 卡牌实体的真实形态（重大修正）

```
卡牌实体类型: Canis.entities.Entity | 名称: ImperiumPlayable
继承链: Entity <- ObjectAttributes <- Object <- Il2CppObjectBase
```

**卡牌运行时不是 `WormImperiumPlayable`，而是通用 `Canis.entities.Entity`。**
身份只体现在 `EntityName` 这个字符串上。

这条修正推翻了此前一个错误预期：`WormImperiumPlayable::AcquireValue`、
`WormPlayable::AddChildAbility` 这些成员虽然真实存在于 interop 中，
但**不属于运行时卡牌对象**，对着卡牌实体调用必然失败。

### 12.3 领袖能力是以子实体形式存在的

```
Leader | 子=2
  Entity | AgentSupply | 子=2 → Agent, Agent
  Entity | Abilities   | 子=1 → ManipulateAbility
```

这说明游戏的整个对局状态是 **「通用实体 + 属性 + 子能力实体」的纯数据模型**，
而不是一堆硬编码的 C# 类。能力是一个叫 `ManipulateAbility` 的实体节点。

**这是造新卡 / 改卡 / 改领袖的共同基础**：三者都是「操作实体树 + 属性」，
不是「写新类」。

### 12.4 属性读取受阻（未解）

`MutableAttributes` 的 API 面（v22 实测）：

```
Void  StoreAttribute(IAttribute)
IMutableAttribute GetAttribute(IAttributeDefinition)
Boolean HasAttribute(Int32)
Void  SetAttribute(AttributeDefinition`1, V)
Void  ClearAttribute(Int32) / RemoveAttribute(Int32)
属性  data : ConcurrentDictionary`2      ← 真正存储
属性  AsReadOnly : ReadOnlyAttributes
```

两个卡点：

- **没有 `GetAttribute(int)` 重载**——必须传 `IAttributeDefinition` 对象，
  所以 `attr[10000]` 恒为 null，不能按整数 id 直接取值
- `MutableAttributes` 与 `data` 字典**枚举均返回 0 条**——
  与坑 1 同源：Il2Cpp 泛型接口未桥接到 .NET，`IEnumerator` 转换静默失败

### 12.5 v20–v23 迭代记录

| # | 目标 | 结果 |
|---|---|---|
| v20 | 遍历实体树 | ✅ 全貌拿到，定位 `Deck / Supply / Leader` |
| v21 | 解剖一张卡牌 | ✅ 实体为通用 `Entity`；有 `attributes` + `parentAttributes`；**无 `Archetype` 属性** |
| v22 | 读属性容器 API | ✅ 方法表完整；❌ 索引器与整数 id 均不可用 |
| v23 | 枚举属性 + 领袖子树 | ❌ 属性枚举 0 条；✅ 领袖能力为子实体 |

---

## 十三、修正后的架构模型（当前认知）

```
WormMatch
 └─ WormPlaymat（实体树根）
     ├─ WormPlayer
     │   ├─ Deck / Hand / Supply / Discard …（都是 Entity，EntityName 标识用途）
     │   ├─ Leader ──► AgentSupply / Abilities ──► ManipulateAbility …
     │   └─ 各类数值以属性存在（如 910014 = 牌组 GUID 列表）
     ├─ Supply（33 个子实体 = 市场上的卡）
     └─ …
```

要点三条：

1. **一切皆 `Entity`**：卡牌、容器、能力、领袖都是同一个类，靠 `EntityName` 区分
2. **身份在属性里**：卡牌是「哪张卡」由属性 + ArchetypeID 决定，不是由类型决定
3. **能力是节点**：`Abilities` 是容器实体，其子节点一个个都是能力

### 13.1 由此推出的可操作接口

| 需求 | 接口 | 来源 |
|---|---|---|
| 实体 → 原型 | `Entity.GetArchetype()` | `Canis.entities.Entity` 实例方法 |
| 实体 → 原型 ID | `IEntityExtensions.ArchID(IEntity)` | `Canis.attributes.IEntityExtensions` 静态扩展 |
| ID → 原型 | `Archetype.Get(ArchetypeID)` | `Canis.archetypes.Archetype` 静态 |
| 原型表生成 | `MakeArchetypes<TArchetype>()` | 扫描程序集，解释 `archs` 为何是 534 个通用原型 |
| 导出实体数据 | `Entity.Serialize()` | 绕开属性枚举的替代路径 |
| 改实体树 | `AddChild / AddDescendent / RemoveChild / MoveTo` | `Entity` 实例方法 |
| 造实体（带位置） | `CreateEntity(Match, ArchetypeID, Entity parent, PlayerEntity, int?)` | v18 崩溃日志实证 |

**关键判断**：既然存在 `GetArchetype()`，就不必再拦截创建。
「改卡 = 取原型改属性」，「造新卡 = 克隆原型换 ID」，
「改领袖 = 改其 `Abilities` 下的能力实体」——三条路都落在已确认的接口上。

---

## 十四、第二轮新增的坑

### 坑 8：同时给基类和派生类打补丁 → 栈溢出闪退

v18 里我对 `WormPlayArea.AddDescendent` 和其基类 `Entity.AddDescendent` 都打了补丁，
调用被重定向两次，直接栈溢出。HarmonyX 当时已有警告：

```
[Warning: HarmonyX] You should only patch implemented methods/constructors
```

**教训**：`GetMethods()` 会返回继承来的方法，打补丁前必须加 `BindingFlags.DeclaredOnly`，
且同一方法链上只打一个点。

### 坑 9：void 方法不能用带 `__result` 的后置补丁

`CreateEntities` 返回 void，我却用了 `__result`，报 `Cannot get result from void method`。
void 方法与有返回值方法必须拆成两个处理器。

### 坑 10：类型名对得上 ≠ 是同一个对象

interop 里 `WormImperiumPlayable`、`WormPlayable` 都有完整方法表，
但运行时卡牌是 `Canis.entities.Entity`。**interop 里的类齐全，不代表运行时会用到它们。**

**教训**：任何「这个类有什么方法」的结论，都必须先用 `GetType().FullName` 确认
运行时实例的真实类型，否则等于对着错误的类型写代码。

### 坑 11：`Count` / `Item` 也不是万能兜底

对 Il2Cpp 集合依次试过 `IEnumerable`、`GetEnumerator` 反射、`Count`+`Item` 三种方式，
`MutableAttributes` 与 `ConcurrentDictionary` 三者皆返回 0。
泛型容器在 interop 下可能完全不暴露可用的枚举路径。

**教训**：遇到泛型容器枚举失败，不要继续换遍历写法，应改用**非枚举路径**
（如 `Serialize()` 导出、`HasAttribute(int)` 逐项判断）。

### 坑 12：把「拦截」当成唯一入口

前 6 个版本都在找「谁创建了卡牌」，但更简单的问题是「它现在在哪」。
实体树遍历一次就解决了拦截 12 个方法都没解决的问题。

**教训**：在一个动态系统里，**读状态**通常比**拦事件**简单一个数量级。
先用读状态把结构摸清，再决定是否需要拦截。

---

## 十五、当前进度与下一步

### 已确证可用

- BepInEx 在 Unity 6 上稳定加载，Harmony 补丁真实命中（v1 已验证）
- 实体树可完整遍历，能定位玩家、牌组、市场、领袖
- `Entity.GetArchetype()` / `ArchID()` / `Archetype.Get()` 构成原型访问链
- `CreateEntity` 带 `parent` + `position` 的完整签名
- 数据层精通：`wormPlayers` 可在单人标准局生效（路线 A，已实测）

### 待验证（按优先级）

| # | 目标 | 验证方式 |
|---|---|---|
| 1 | `card.GetArchetype()` 能否返回具体卡原型 | 打印类型名是否形如 `…ImperiumArchetypes.BaseSet.Dagger` |
| 2 | `Entity.Serialize()` 能否导出卡牌全部属性 | 打印序列化结果的字段 |
| 3 | 改原型属性是否影响局内表现 | 改一张卡的数值，看卡面 |
| 4 | 克隆原型 + 新 ID 能否注册成功 | 加进牌组后能否正常打出 |
| 5 | 往 `Abilities` 加子实体是否加出新能力 | 观察卡牌行为变化 |

### 明确不做

- 不再尝试拦截 `CreateEntity` 一族（已证死路）
- 不再用反射枚举 Il2Cpp 泛型集合（改用 `Serialize` 等非枚举路径）

---

## 十六、静态分析路线：不依赖开游戏读代码（2026-10-30）

### 16.1 为什么要转静态

v24–v30 每验证一个假设都要启动一次游戏，一个来回十分钟，而且多次因为
「异常被 catch 吞掉」而拿到误导性结果。第七轮起改为**先静态读程序集**，
只把真正需要运行时数据的问题留给插件。

### 16.2 自建工具 TypeDump

`tools/TypeDump`（.NET 8 + System.Reflection.Metadata）可离线导出任意类型的
完整结构：基类、字段（含真实类型）、属性、方法完整签名（含参数名与返回类型）。

用法：

```
dotnet TypeDump.dll <interop程序集> <输出文件> <类型名子串...>
```

它会跳过 `NativeMethodInfoPtr_*` / `NativeFieldInfoPtr_*` 这类 interop 噪音字段。

### 16.3 由静态分析确认的关键契约

**卡牌原型（canis.archetypes.Archetype）**

```
.ctor(ArchetypeID archID)
.ctor(SerializableAttributes attributes)
.ctor(ArchetypeID archID, SerializableAttributes attributes)   ← 数据定义入口
Build(Match m, PlayerEntity owner, Entity parent) -> Entity    ← 原型 → 实体
Deserialize(SerializedEntity se, Match m, Entity parent) -> Entity
ConstructAbilities(Match, PlayerEntity, IHasAbilities)
Get(ArchetypeID) -> Archetype（静态）
ArchetypeID { get; set; } / EntityName { get; }
AbilityIDsAttribute : AttributeDefinition<List<AbilityID>>
```

**实体（Canis.entities.Entity）**

```
.ctor(Match, PlayerEntity, Archetype, string)   ← 构造时接收原型
GetArchetype() 是 virtual，无参数
MoveTo(Entity, int?) / ForceParent(Entity) / AddChild / RemoveChild
Serialize() -> SerializedEntity
```

**结论**：卡牌身份是构造实体时写入的 **native 字段**，不在 attributes 里。
所以「改 ArchID 让一张卡变成另一张卡」在属性层不可能实现（v30 已实证失败）。

**属性容器（Canis.attributes.MutableAttributes）**

```
.ctor(MutableAttributes other)      ← 拷贝构造
Clone() -> MutableAttributes        ← 克隆
data : ConcurrentDictionary<int, IMutableAttribute>
SetAttribute<T>(AttributeDefinition<T>, T)
GetAttribute<T>(AttributeDefinition<T>) -> MutableAttribute<T>
HasAttribute(int)
```

**属性定义（AttributeDefinitions）**

```
GetDefinition(int key) -> IAttributeDefinition
GetDefinition<T>(int key) -> AttributeDefinition<T>
```

**属性读写封装（AttributeDefinitionExtensions）**

```
GetFrom<T>(AttributeDefinition<T>, IHasAttributes) -> T
SetTo<T>(AttributeDefinition<T>, MutableAttributes, T)
SetFrom<T>(AttributeDefinition<T>, MutableAttributes, IHasAttributes source)
```

**ID 类型**

```
ArchetypeID(string) / ArchetypeID(Guid) / Empty
ArchetypeID._ID : Guid      ← 真实 GUID 存在这个字段
```

**SerializableAttributes**

```
.ctor(SerializableAttributes other) / Clone()
implicit operator SerializableAttributes(MutableAttributes)   ← 可直接转换
```

### 16.4 属性键的权威名称（由定义表读出，非猜测）

| key | Name | ValueType |
|---|---|---|
| 10000 | ArchID | `Canis.utils.ids.ArchetypeID` |
| 920003 | Image | `System.String` |
| 920004 | IconList | `List<worm.canis.data.enums.AgentIcons>` |
| 920005 | — | 阵营列表 |
| 920006 | **PersuasionCost** | `System.Nullable<System.Int32>` |
| 920009 | — | 分类（Starter / Main） |
| 900013 | WormAbilityIDs | `List<Canis.utils.ids.AbilityID>` |
| 900014 | — | 内容集（BaseSet / RiseOfIx …） |
| 900018 | — | 卡牌类别（Imperium / Intrigue / Conflict） |

### 16.5 由此得到的造新卡流水线

```
模板 = 某张已有卡的 Entity.GetArchetype()
克隆 = 模板.attributes.Clone()
改 ID = SetTo<ArchetypeID>(def10000, 克隆, 新ID)
转换 = SerializableAttributes.op_Implicit(克隆)
原型 = new Archetype(新ID, 转换后属性)
实体 = 原型.Build(match, player, deck)      ← parent 直接指定牌组
```

这条链路完全在托管层，不需要新增 C# 类，也不需要重新打包 AssetBundle。

### 16.6 静态分析纠正的三个错误认知

| 原认知 | 实际 |
|---|---|
| 改 attributes 的 10000 就能换卡 | ❌ 身份在 native 字段，属性层改不动 |
| IconList 是 `List<string>` | ❌ 是 `List<AgentIcons>` 枚举 |
| PersuasionCost 是 `int` | ❌ 是 `Nullable<int>` |

### 16.7 编译期错误也是免费的静态分析

把 interop 程序集加进引用后，**写错签名会直接编译失败**，报错里带着
正确的参数类型。这比开机跑一次快得多，也是确认「某方法是否存在」最可靠的手段。
本轮据此修正了：`ArchetypeID` 构造收 string、`SetAttribute` 只有 1 个泛型参数、
`MutableAttributes` 未桥接 `IHasAttributes` 接口。

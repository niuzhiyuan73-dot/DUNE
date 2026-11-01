# 只读信息导出插件

本插件只枚举已加载的原型注册表并序列化属性，不创建对局实体，不执行能力，不修改权重、牌组或存档，不调用网络服务。

## 已验证的能力

2026-11-01，本机游戏 4.1.2.1808：在主菜单阶段导出 **534 个原型、623 个属性定义**。不需要手动打完一局。

- `dump/current_mode/archetypes.json`：原型 GUID、原生类名、完整属性值，包括费用、内容集、图标、资源数值、能力 ID、文本键、条件属性等。
- `dump/current_mode/attribute_definitions.json`：每个数值字段编号的官方名称和类型。避免旧探针泛型/Nullable 读值失败造成费用全为空。
- 每进程成功导出一次；失败最多重试三次。主菜单轮询和 SetupPlayer 后置钩子均可触发。

## 稍后获取信息

1. 双击项目根目录 `启动mod版.bat`。主菜单停留约 10 秒。
2. 在 `game/BepInEx/LogOutput.log` 搜索 `[CATALOG] exported`。出现原型数和字段数，表示导出成功。
3. 如需补足当前玩法实际入池内容、能力实体名称和阶段，开始或读取一局离线标准剧本，并正常保存。导出器不要求通过 API 发请求；现有 AI 教练是另一个独立插件。
4. 执行：

```powershell
& 'C:/Users/17789/.workbuddy/binaries/python/versions/3.13.12/python.exe' -X utf8 -B mods/tools/build_current_mode_catalog.py
```

若换电脑，使用安装了 UnityPy 的 Python 即可。脚本只读游戏、原型导出和存档，写自己的数据表和报告。

输出：

- `mods/ai/current_mode_catalog.json`：无长度截断、按 GUID 区分版本、带来源与未解决项的结构化目录。
- `docs/当前玩法信息盘点.md`：开局配置、冲突顺序、覆盖统计和固定配置待核对项。
- `docs/当前玩法卡牌规则.md`：每项完整属性、文本和能力证据。
- `dump/current_mode/localization.json`：gamecore 与 win 的完整中英文本，包含旧脚本漏掉的冲突奖励键。
- `dump/current_mode/ability_observations.json`：仅从存档当前状态取得的能力证据，不混入撤销历史。

## “完整”的边界

原型属性可无损取得，但不能把它等同于完整自然语言规则。部分效果写在 IL2CPP 原生方法中，属性里只有能力 ID；一些本地化是选择提示或条件的半句。此插件不尝试通过执行能力来推测效果。

完整信息的后续核对流程：

1. 精确 GUID 对应原型、内容集和版本，不能只按中文名合并。
2. 从属性取得基础数值及费用，从本地化取得全文，从同一原型的存档能力子树取得能力身份/阶段。
3. 能力阶段字段是原生执行标记，未必就是卡面印刷的触发时机，例如购买能力可能借用 Reveal 阶段。需要结合卡面/规则上下文，不直接替换成“展示效果”。
4. 用 `ConflictRewardArchetypes` 连接起义冲突的三档奖励；基础版无对应奖励原型的冲突，仍需查规则/卡面。
5. 对本地仍没有完整语义的能力，用游戏实际渲染的卡牌详情或对应扩展的实体卡面核对；保留来源及核对状态。资源包中的纯插画不包含这些规则。
6. 原型属于启用内容集只说明是候选；具体入池受模式/扩展替换等规则影响，结合新局存档确认。

旧 `card_text_extra.tsv` 等表有已发现的不准确内容，生成器只保留为待核对证据，不自动覆盖原型属性。新目录暂未接入 Coach，不会把尚未核实的摘要自动传给模型。

## 编译与安装

```powershell
& 'C:/Users/17789/dotnet/dotnet.exe' build catalog_probe/DuneCatalogProbe.csproj -c Release
Copy-Item catalog_probe/bin/Release/DuneCatalogProbe.dll game/BepInEx/plugins/DuneCatalogProbe.dll
```

先退出 Mod 游戏再更新 DLL。无需覆盖 `DuneAiProbe.dll` 或 `DuneCardExport.dll`。停用时，在游戏退出后将 `DuneCatalogProbe.dll` 移出 plugins 目录即可。

## 验证

```powershell
& 'C:/Users/17789/.workbuddy/binaries/python/versions/3.13.12/python.exe' -X utf8 -B -m unittest discover -s mods/tools/tests -p test_current_mode_catalog.py
```

测试检查原型/字段唯一性、固定配置覆盖、官方奖励文本保留、旧错误摘要未覆盖游戏属性及长文本不截断。

using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text.RegularExpressions;
using BepInEx.Logging;
using Canis.entities;
using Canis.utils.ids;

namespace DuneAiProbe
{
    /// <summary>
    /// 把 selection.ToString() 里的机器标识换成人话，并砍掉与决策无关的大块。
    ///
    /// 两个已知问题（来自 10-31 的实测）：
    ///   1) validTargets 是"实体实例 ID"（运行时分配的 GUID），name_map.tsv 里没有，
    ///      模型只能说"无法判断"。
    ///      解法：EntityID 有 .ctor(String)，Match 有 TryGetEntity/GetEntity，
    ///      Entity 有 EntityName，GetArchetype().ArchetypeID 是原型 GUID，
    ///      原型 GUID 再查 name_map 得到中文名。
    ///   2) 一条 SelectionMessage 的 ToString() 有 6KB 以上，其中 Continuation /
    ///      SerializedStack 是执行栈快照，对"该选哪个"没有信息量，却把 targetMap 挤到截断之外。
    ///
    /// 线程：全部在游戏线程里调用（WormAIProfile.MakeChoice 的 prefix）。
    /// </summary>
    internal static class Targets
    {
        private static readonly Regex IdRe = new Regex(
            "[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}", RegexOptions.Compiled);

        private static readonly string[] NoiseKeys = { "Continuation", "SerializedStack", "OfferedAt" };

        // 同一局里实体 ID 不会变；换局（Match 指针变）才清空
        private static IntPtr _cacheMatch = IntPtr.Zero;
        private static readonly Dictionary<string, string> _cache =
            new Dictionary<string, string>(StringComparer.Ordinal);

        private static int _hit, _miss;
        private static ManualLogSource _log;
        private static readonly HashSet<int> _factionLogged = new HashSet<int>();

        // 实体实例 ID -> 效果键（原型 GUID，或 worm.canis.abilities.<类名>），Resolve 时顺手记下
        private static readonly Dictionary<string, string> _effKey =
            new Dictionary<string, string>(StringComparer.Ordinal);

        public static string Rewrite(Canis.Match match, string text, ManualLogSource log)
        {
            if (string.IsNullOrEmpty(text)) return text;
            _log = log;
            try
            {
                for (int i = 0; i < NoiseKeys.Length; i++) text = StripBlock(text, NoiseKeys[i]);

                IntPtr mp = IntPtr.Zero;
                try { if (match != null) mp = match.Pointer; } catch { }
                if (mp != _cacheMatch) { _cache.Clear(); _effKey.Clear(); _cacheMatch = mp; }

                _hit = 0;
                _miss = 0;
                string result = ReplaceAll(text, match);

                // 每次覆盖写一份，便于在游戏外用文本工具检查（不刷屏、不增长）
                try
                {
                    string dir = System.IO.Path.Combine(DuneAiProbe.Plugin.RootDir, "dump", "llm");
                    System.IO.Directory.CreateDirectory(dir);
                    System.IO.File.WriteAllText(System.IO.Path.Combine(dir, "last_pending.txt"),
                        result, new System.Text.UTF8Encoding(false));
                }
                catch { }

                log.LogInfo("[TARGETS] " + text.Length + " -> " + result.Length + " chars, resolved="
                            + _hit + " unresolved=" + _miss);
                return result;
            }
            catch (Exception e)
            {
                log.LogWarning("[TARGETS] rewrite failed: " + e.GetType().Name + ": " + e.Message);
                return text;
            }
        }

        /// 不用 Regex.Replace(lambda)：本工程引用的 Il2Cppmscorlib 缺 NullableAttribute，
        /// 编译器一生成 lambda 的闭包类型就会报 CS0656。
        private static string ReplaceAll(string text, Canis.Match match)
        {
            var ms = IdRe.Matches(text);
            if (ms == null || ms.Count == 0) return text;

            var sb = new System.Text.StringBuilder(text.Length);
            int pos = 0;
            for (int i = 0; i < ms.Count; i++)
            {
                Match m = ms[i];
                if (m.Index < pos) continue;
                sb.Append(text, pos, m.Index - pos);
                sb.Append(Replace(m.Value, match));
                pos = m.Index + m.Length;
            }
            sb.Append(text, pos, text.Length - pos);
            return sb.ToString();
        }

        private static string Replace(string id, Canis.Match match)
        {
            string hit;
            if (_cache.TryGetValue(id, out hit)) return hit;
            string v = Resolve(id, match);
            _cache[id] = v;
            return v;
        }

        /// <summary>
        /// SelectionWithTargetsRequired.TargetMap 里每个目标只给出 "ValidTargets (6)" 这样的计数，
        /// 真正可选的东西（6 个空间/6 张牌）看不到——而"放哪儿"恰恰是这类选择的关键。
        /// 这里把 ValidTargets 逐个解析成名字，拼成一段附加明细。
        ///
        /// 目标信息类型有十几种，不逐个建模：只认带 ValidTargets 属性的那几种，
        /// 其余的交给 ToString（Choices 之类它自己会打）。
        /// </summary>
        public static string Detail(Networking.selection.messages.SelectionMessage selection,
            Canis.Match match, ManualLogSource log)
        {
            if (selection == null) return null;
            try
            {
                var sw = selection.TryCast<Networking.selection.messages.SelectionWithTargetsRequired>();
                if (sw == null) return null;

                var map = sw.TargetMap;
                if (map == null || map.Count == 0) return null;

                // 两遍：先收集，再按「名字+效果+子目标」合并同名候选。
                // 实测 78% 的请求里同一张牌有 2 个实例（渗透×2），除 8 位短 ID 外完全相同；
                // 不合并时模型只能答"任选其一"（实测 21% 的答案是这种），决策信息被浪费。
                var order = new List<string>();
                var byKey = new Dictionary<string, Row>(StringComparer.Ordinal);
                int withTargets = 0, withEffect = 0, scanned = 0;
                int mapTotal = map.Count;
                foreach (var kv in map)
                {
                    if (scanned++ >= DetailScanMax) break;

                    string kid = null;
                    try { if (kv.Key != null) kid = kv.Key.ToString(); } catch { }
                    string label = kid != null ? Replace(kid, match) : "?";

                    // 选项侧补一句效果：模型反复说「没有效果文本，无法判断」，这一句就是给它判断依据
                    string eff = null;
                    string head = label;
                    if (kid != null)
                    {
                        eff = EffectFor(kid, match);
                        if (!string.IsNullOrEmpty(eff))
                        {
                            head = label + "〔" + eff + "〕";
                            withEffect++;
                            if (eff == AMBIGUOUS_MARK)
                            {
                                // 歧义不算"缺数据"，是"多版本待确认"，单独计数
                                RecordGap(GapKind.Ambiguous, label.Split('(')[0].Trim());
                            }
                        }
                        else
                        {
                            // 关键：沉默会被模型误读成"这牌没效果"，标注出来它就知道是数据缺失。
                            // 这里记录的是"尚未采集到"，不是"确认没有该效果"。
                            head = label + "〔效果待查〕";
                            // 记录缺口时先剥掉 <sprite name=...>：能力实体常以图标名作名字，
                            // 直接记会把整行变成标签，gaps.txt 不可读
                            RecordGap(GapKind.Unknown, StripTags(label.Split('(')[0].Trim()));
                        }
                    }

                    var list = kv.Value;
                    var parts = new List<string>();
                    for (int i = 0; list != null && i < list.Count; i++)
                    {
                        if (parts.Count >= SubTargetMax)
                        {
                            // 显式标记截断
                            parts.Add("〔另有 " + (list.Count - i) + " 个子目标未列出〕");
                            break;
                        }
                        string one = Describe(list[i], match, log);
                        if (!string.IsNullOrEmpty(one))
                        {
                            if (one.StartsWith("可选 ", StringComparison.Ordinal)) withTargets++;
                            parts.Add(one);
                        }
                    }

                    string sub;
                    if (list == null || list.Count == 0) sub = "（无子目标）";
                    else sub = parts.Count == 0 ? "（只能由按钮决定）" : string.Join("；", parts.ToArray());

                    // 判重键：名字去掉短 ID（同名不同实例视为同一项）+ 效果 + 子目标
                    string bas = ShortIdTail.Replace(label, "");
                    string gk = bas + "\u0001" + (eff ?? "") + "\u0001" + sub;
                    Row row;
                    if (!byKey.TryGetValue(gk, out row))
                    {
                        row = new Row { Head = head, Sub = sub };
                        byKey[gk] = row;
                        order.Add(gk);
                    }
                    else
                    {
                        row.Count++;
                    }
                }

                var sb = new System.Text.StringBuilder();
                int shown = 0;
                foreach (var gk in order)
                {
                    if (shown >= DetailRowMax) break;
                    Row row = byKey[gk];
                    shown++;
                    sb.Append("- ").Append(row.Head);
                    // 合并后必须说清"这是同一个东西的多个实例"，否则模型会以为少给了选项
                    if (row.Count > 1)
                        sb.Append(" ×").Append(row.Count).Append("（同名同效果，任选其一即可）");
                    sb.Append("：").Append(row.Sub).Append('\n');
                }
                if (order.Count > shown)
                    sb.Append("- 〔另有 ").Append(order.Count - shown).Append(" 组选项未列出〕\n");

                if (sb.Length == 0) return null;
                // 一行诊断：withTargets == 0 就说明 ValidTargets 还是没取到
                DumpGaps();
                log.LogInfo("[TARGETS] detail groups=" + shown + "/" + order.Count + " map=" + mapTotal
                            + " withTargets=" + withTargets
                            + " withEffect=" + withEffect + " gaps=" + _gapCount.Count);
                return "【目标明细】\n" + sb;
            }
            catch (Exception e)
            {
                log.LogInfo("[TARGETS] detail failed: " + e.GetType().Name + ": " + e.Message);
                return null;
            }
        }

        private static string Describe(object ti, Canis.Match match, ManualLogSource log)
        {
            if (ti == null) return null;
            string typeName = null;
            try { typeName = ti.GetType().Name; } catch { }

            // TargetMap 里的元素声明类型是基类 TargetInformation，GetType() 只会返回基类，
            // 反射 therefore 找不到 ValidTargets。IL2CPP 的真实类型要靠 TryCast 问原生侧。
            string byTargets = ValidTargetsOf(ti, match, log);
            if (byTargets != null) return byTargets;

            // 其余类型：ToString 已经带了 Choices / 提示，用它的结果
            try
            {
                string s = ti.ToString();
                if (string.IsNullOrEmpty(s)) return null;
                if (typeName != null && s.StartsWith("[" + typeName + "]")) s = s.Substring(typeName.Length + 2).Trim();
                // 基类没有重写 ToString 时会打出完整类型名，对模型是噪声
                if (s.StartsWith("Networking.selection.targetinformation.")) return null;
                return string.IsNullOrEmpty(s) ? null : s;
            }
            catch { return null; }
        }

        /// <summary>
        /// 带 ValidTargets 的目标类型，走 TryCast 拿到真正的派生类型包装器后反射取列表。
        /// 取不到（类型不符 / 列表为空）返回 null，交给调用方退回 ToString。
        ///
        /// 覆盖的类型（从 Networking.dll 实测枚举出 14 种，这里按"能拿到实体列表"的取）：
        ///   EntityListTargetInformation / EntityGroupingTargetInformation
        ///   KnapsackEntityListTargetInformation / RevealEntityListTargetInformation
        ///   RevealDetailedEntityListTargetInformation / ArchetypeCustomChoiceTargetInformation
        ///   CompositeTargetInformationListTargetInformation
        /// 其余（CustomChoiceTargetInformation / XTargetInformation / 各类 Orientation）
        /// 由 ToString 自己输出 Choices 等提示，不做展开。
        /// </summary>
        private static string ValidTargetsOf(object ti, Canis.Match match, ManualLogSource log)
        {
            object cast = null;
            try
            {
                var obj = ti as Il2CppInterop.Runtime.InteropTypes.Il2CppObjectBase;
                if (obj == null) return null;
                cast = TryCastAny(obj,
                    "EntityListTargetInformation",
                    "EntityGroupingTargetInformation",
                    "KnapsackEntityListTargetInformation",
                    "RevealEntityListTargetInformation",
                    "RevealDetailedEntityListTargetInformation",
                    "ArchetypeCustomChoiceTargetInformation",
                    "CompositeTargetInformationListTargetInformation");
            }
            catch { }
            if (cast == null) return null;

            try
            {
                var pi = cast.GetType().GetProperty("ValidTargets");
                if (pi == null) return null;
                var arr = pi.GetValue(cast) as System.Collections.IEnumerable;
                if (arr == null) return null;

                var names = new List<string>();
                int total = 0;
                int shown = 0;
                foreach (object o in arr)
                {
                    total++;
                    if (shown >= TargetListMax)
                    {
                        // 显式标记截断：不能静默省略，模型必须知道还有未列出的目标
                        names.Add("〔另有 " + (total - shown) + " 项未列出〕");
                        break;
                    }
                    string s = DescribeTarget(o, match, log);
                    if (!string.IsNullOrEmpty(s)) { names.Add(s); shown++; }
                }
                return names.Count == 0 ? null : "可选 " + string.Join("/", names.ToArray());
            }
            catch { return null; }
        }

        /// 目标列表最多列出多少项（超出部分显式标记"另有 N 项未列出"）。
        private const int TargetListMax = 20;

        /// 按类型名依次 TryCast，返回第一个成功的包装器。
        /// IL2CPP 的真实类型要靠原生侧 TryCast 问出来，反射只能看到基类。
        private static object TryCastAny(Il2CppInterop.Runtime.InteropTypes.Il2CppObjectBase obj,
                                         params string[] typeNames)
        {
            for (int i = 0; i < typeNames.Length; i++)
            {
                try
                {
                    var t = Type.GetType("Networking.selection.targetinformation." + typeNames[i] +
                                         ", Networking");
                    if (t == null) continue;
                    var mi = obj.GetType().GetMethod("TryCast", new Type[] { t });
                    if (mi == null) continue;
                    var res = mi.Invoke(obj, new object[] { null });
                    if (res != null) return res;
                }
                catch { }
            }
            return null;
        }

        /// <summary>
        /// 查某个实体的效果文本。
        ///
        /// 返回 null 表示"查不到"。调用方要区分三种情况，不能一律当成"没有效果"：
        ///   1. 查到了 —— 附上文本；
        ///   2. 查不到且该实体有歧义（同名多版本）—— 必须显式告诉模型"多版本，无法确定"；
        ///   3. 查不到且无歧义 —— 属于"尚未采集到"，标注待查。
        ///
        /// 按名兜底只在中文名唯一对应一个原型时允许，否则会把别的扩展的规则搬过来。
        /// </summary>
        private static string EffectFor(string idString, Canis.Match match)
        {
            string key = null;
            if (!_effKey.TryGetValue(idString, out key))
            {
                Replace(idString, match);          // 触发一次解析，把效果键补进缓存
                _effKey.TryGetValue(idString, out key);
            }
            if (string.IsNullOrEmpty(key)) return null;

            string e = Coach.EffectOf(key);        // key 可能是 GUID，也可能是能力本地化键
            if (string.IsNullOrEmpty(e) && IsGuidish(key))
            {
                string nm = Coach.NameOf(key);
                // 同名多版本时禁止按名兜底：Coach.EffectOf 内部已拦截，
                // 这里只负责把"歧义"这个事实带出去。
                if (!string.IsNullOrEmpty(nm))
                {
                    if (Coach.IsAmbiguousName(nm)) return AMBIGUOUS_MARK;
                    e = Coach.EffectOf(nm);
                }
            }
            else if (string.IsNullOrEmpty(e))
            {
                // worm.canis.abilities.<类名> 在 name_map 里存的就是效果描述
                e = Coach.NameOf(key);
            }
            return string.IsNullOrEmpty(e) ? null : e;
        }

        /// 同名多版本时的占位标记：明确告诉模型"这个实体有多个版本、规则不同、无法确定"。
        private const string AMBIGUOUS_MARK = "〔同名多版本·规则不同·无法确定，请按版本确认〕";

        private static bool _elemLogged;
        private static readonly Dictionary<string, int> _gapCount = new Dictionary<string, int>(StringComparer.Ordinal);

        /// 明细里最多列出多少个选项 / 每个选项多少个子目标。
        /// 超出部分一律显式标记"另有 N 项未列出"，不做静默截断。
        private const int DetailRowMax = 24;
        private const int SubTargetMax = 24;

        /// 防御：TargetMap 正常只有几十项；异常大的（整手牌选择）跳过其余项，
        /// 免得在 AI 决策线程里做过多反射。
        private const int DetailScanMax = 200;

        /// 行尾短 ID：Replace 会把实体写成「名字(1a2b3c4d)」，判重时要去掉，
        /// 否则同名不同实例永远不会被认成同一项。
        private static readonly Regex ShortIdTail = new Regex(@"\s*\([0-9a-f]{8}\)\s*$", RegexOptions.Compiled);

        /// 明细的一行（合并后）。Count = 命中同名同效果的实例数。
        private sealed class Row
        {
            public string Head;
            public string Sub;
            public int Count = 1;
        }

        /// 缺口的三种状态。区分"确认没有该效果"与"该效果尚未采集到"，
        /// 否则一张只提取到半张效果的卡会被误判成信息齐全。
        private enum GapKind
        {
            /// 尚未采集到：可能有该效果，但文本还没被提取/核对。
            Unknown,
            /// 同名多版本：不是缺数据，是无法确定该用哪个版本的规则。
            Ambiguous,
            /// 已确认没有：原型属性里确实不存在该效果。
            Absent,
        }

        private static readonly Dictionary<GapKind, Dictionary<string, int>> _gaps =
            new Dictionary<GapKind, Dictionary<string, int>>
            {
                { GapKind.Unknown, new Dictionary<string, int>(StringComparer.Ordinal) },
                { GapKind.Ambiguous, new Dictionary<string, int>(StringComparer.Ordinal) },
                { GapKind.Absent, new Dictionary<string, int>(StringComparer.Ordinal) },
            };

        private static void RecordGap(GapKind kind, string name)
        {
            if (string.IsNullOrEmpty(name)) return;
            var d = _gaps[kind];
            d[name] = d.TryGetValue(name, out int c) ? c + 1 : 1;
            _gapCount[name] = _gapCount.TryGetValue(name, out int g) ? g + 1 : 1;
        }

        /// 把「查不到文本」的实体按出现次数写到 dump/ai/gaps.txt：
        /// 洞从"感觉有"变成可枚举清单，补一条少一条。
        /// 三类分开统计：待采集 / 多版本歧义 / 已确认没有。
        private static void DumpGaps()
        {
            try
            {
                string dir = System.IO.Path.Combine(DuneAiProbe.Plugin.RootDir, "dump", "ai");
                System.IO.Directory.CreateDirectory(dir);
                var sb = new System.Text.StringBuilder();
                sb.Append("# 效果文本缺口清单（按状态分组，组内按出现次数降序）\n");
                sb.Append("# 说明：unknown=尚未采集到（可能有该效果）；ambiguous=同名多版本无法确定；");
                sb.Append("absent=已确认没有该效果\n");

                var groups = new List<KeyValuePair<string, Dictionary<string, int>>>
                {
                    new KeyValuePair<string, Dictionary<string, int>>("unknown", _gaps[GapKind.Unknown]),
                    new KeyValuePair<string, Dictionary<string, int>>("ambiguous", _gaps[GapKind.Ambiguous]),
                    new KeyValuePair<string, Dictionary<string, int>>("absent", _gaps[GapKind.Absent]),
                };
                for (int gi = 0; gi < groups.Count; gi++)
                {
                    var d = groups[gi].Value;
                    sb.Append("# [").Append(groups[gi].Key).Append("] 行数: ").Append(d.Count).Append('\n');
                    var list = new List<KeyValuePair<string, int>>(d);
                    list.Sort((a, b) => b.Value.CompareTo(a.Value));
                    for (int i = 0; i < list.Count; i++)
                        sb.Append(list[i].Value).Append('\t').Append(list[i].Key).Append('\n');
                }
                System.IO.File.WriteAllText(System.IO.Path.Combine(dir, "gaps.txt"), sb.ToString(),
                    new System.Text.UTF8Encoding(false));
            }
            catch { }
        }



        /// <summary>
        /// ValidTargets 的元素到底是什么类型，只有运行时才知道（可能是 EntityID、Entity、
        /// 或某种包装）。这里不赌：先看有没有 EntityName，再依次试 _ID / ID / EntityID / Value，
        /// 拿到真正的 GUID 才交给 Resolve；实在拿不到就返回 null，宁可不写也不要把类型名塞给模型。
        /// </summary>
        private static string DescribeTarget(object o, Canis.Match match, ManualLogSource log)
        {
            if (o == null) return null;

            Type t = null;
            try { t = o.GetType(); } catch { }
            if (t != null && !_elemLogged)
            {
                _elemLogged = true;
                try { log.LogInfo("[TARGETS] validTarget element type = " + t.FullName); } catch { }
            }

            string en = PropString(t, o, "EntityName");
            if (!string.IsNullOrEmpty(en)) return WithEffect(en, null, match);

            string id = PropString(t, o, "_ID")
                        ?? PropString(t, o, "ID")
                        ?? PropString(t, o, "EntityID")
                        ?? PropString(t, o, "Value");
            if (id == null)
            {
                string raw = null;
                try { raw = o.ToString(); } catch { }
                if (IsGuidish(raw)) id = raw;
            }
            if (id == null) return null;
            // 子目标也要附上自己的规则：从这些牌中弃一张/送入版图时，
            // 模型必须知道每个候选各自的作用，否则无从比较。
            return WithEffect(Replace(id, match), id, match);
        }

        /// 给一个已解析的名字附上〔效果〕。
        /// id 为实体实例 GUID 时用于取效果；查不到时按"尚未采集到"标注，不与"确认没有"混淆。
        private static string WithEffect(string label, string id, Canis.Match match)
        {
            if (string.IsNullOrEmpty(label)) return label;
            string eff = null;
            if (!string.IsNullOrEmpty(id))
            {
                try { eff = EffectFor(id, match); } catch { }
            }
            else
            {
                // 已经有 EntityName 的元素：先按名字查效果
                try { eff = Coach.EffectOf(label); } catch { }
                if (string.IsNullOrEmpty(eff))
                {
                    try
                    {
                        if (Coach.IsAmbiguousName(label)) eff = AMBIGUOUS_MARK;
                    }
                    catch { }
                }
            }
            if (string.IsNullOrEmpty(eff)) return label;
            return label + "〔" + eff + "〕";
        }

        private static string PropString(Type t, object o, string name)
        {
            if (t == null) return null;
            try
            {
                var pi = t.GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
                if (pi == null) return null;
                var v = pi.GetValue(o);
                if (v == null) return null;
                if (v is string s) return string.IsNullOrEmpty(s) ? null : s;
                var vs = v.ToString();
                return IsGuidish(vs) ? vs : null;
            }
            catch { return null; }
        }

        private static bool IsGuidish(string s)
        {
            if (string.IsNullOrEmpty(s)) return false;
            if (s.Length == 36) { int d = 0; for (int i = 0; i < s.Length; i++) if (s[i] == '-') d++; return d == 4; }
            return false;
        }

        private static string Resolve(string id, Canis.Match match)
        {
            // 不是 GUID 就原样返回（多半已经是人话），别按前 8 字符截成 "Canis.ut" 这种垃圾
            if (!IsGuidish(id)) return id;
            string shortId = id.Substring(0, 8);

            // 1) 原型 GUID（attributes 里的 10000 = archetypeID 就是这种）→ 查名称表即可
            string byArch = Coach.NameOf(id);
            if (byArch != null) { _effKey[id] = id; _hit++; return byArch + "(" + shortId + ")"; }

            if (match == null) { _miss++; return shortId; }

            // 2) 实体实例 → 实体名 / 原型 GUID
            try
            {
                var eid = new EntityID(id);
                Entity ent = null;
                bool ok;
                try { ok = match.TryGetEntity(eid, out ent); }
                catch
                {
                    ent = match.GetEntity(eid);
                    ok = ent != null;
                }
                if (!ok || ent == null) { _miss++; return shortId; }

                string en = null;
                try { en = ent.EntityName; } catch { }

                // 2a) 同名实体：阵营轨道与部队在存档里只有类名（FactionTrack / Troop），
                //     轨道靠兄弟顺序区分（实测三个存档顺序一致：皇帝/宇航公会/姐妹会/弗雷曼）
                string generic = GenericName(en, ent);
                if (generic != null) { _hit++; return generic; }

                string archGuid = null;
                try
                {
                    var a = ent.GetArchetype();
                    if (a != null && a.ArchetypeID != null) archGuid = a.ArchetypeID.ToString();
                }
                catch { }

                string human = Coach.NameOf(archGuid) ?? Coach.NameOf(en);

                // 2b) 能力类实体：entityName 是类名（FamilyAtomicsAbility），
                //     中文名与效果文本都在 worm.canis.abilities.<类名> 下
                string abilityKey = null;
                if (en != null && (Coach.NameOf(archGuid) == null || Coach.NameOf(en) == null))
                {
                    abilityKey = "worm.canis.abilities." + en;
                    if (Coach.NameOf(abilityKey) == null) abilityKey = null;
                }

                if (human == null && en != null) human = Coach.NameOf("button." + en);
                if (human == null) human = en;

                _effKey[id] = BestEffectKey(abilityKey ?? archGuid, ent, 3);
                if (string.IsNullOrEmpty(human)) { _miss++; return shortId; }

                _hit++;
                return human + "(" + shortId + ")";
            }
            catch { _miss++; return shortId; }
        }

        /// <summary>
        /// 能力实体往往没有独立文案（FamilyAtomicsAbility、DeployUnitsAbility…），
        /// 但它们挂在某张卡或某个领袖下面。这里向上找 1-3 层父实体，
        /// 谁有可用的效果文本就用谁的——纯游戏内数据，不依赖外部表。
        /// </summary>
        private static string BestEffectKey(string key, Entity ent, int maxUp)
        {
            if (!string.IsNullOrEmpty(key) && HasEffect(key)) return key;
            if (ent == null) return key;

            Entity cur = ent;
            for (int i = 0; i < maxUp && cur != null; i++)
            {
                Entity up = null;
                try { up = cur.Parent; } catch { }
                if (up == null) break;
                // 只回退到「可打出的卡」这一层：能力实体若挂在版图空间下（DeployUnitsAbility
                // 就挂在厄拉奇恩空间里），继续上溯会把**空间效果**当成能力效果，写进 prompt
                // 会误导模型（实测出现过「DeployUnitsAbility〔征兵1次并抽1张牌.控制厄拉奇恩…〕」）。
                string upName = null;
                try { upName = up.EntityName; } catch { }
                if (upName == null || upName.IndexOf("Playable") < 0) break;
                cur = up;

                string guid = null;
                try
                {
                    var a = cur.GetArchetype();
                    if (a != null && a.ArchetypeID != null) guid = a.ArchetypeID.ToString();
                }
                catch { }

                if (!string.IsNullOrEmpty(guid) && HasEffect(guid)) return guid;

                string nm = null;
                try { nm = cur.EntityName; } catch { }
                if (!string.IsNullOrEmpty(nm) && HasEffect("worm.canis.abilities." + nm))
                    return "worm.canis.abilities." + nm;
            }
            return key;
        }

        /// 该键查到东西才算数（GUID 还要试中文名兜底）
        private static bool HasEffect(string key)
        {
            if (string.IsNullOrEmpty(key)) return false;
            if (!string.IsNullOrEmpty(Coach.EffectOf(key))) return true;
            if (IsGuidish(key))
            {
                string nm = Coach.NameOf(key);
                if (!string.IsNullOrEmpty(nm) && !string.IsNullOrEmpty(Coach.EffectOf(nm))) return true;
            }
            else
            {
                string t = Coach.NameOf(key);
                if (!string.IsNullOrEmpty(t)) return true;
            }
            return false;
        }

        /// <summary>
        /// 存档里阵营轨道与部队都只存类名，没有可读名：
        ///   FactionTrack —— 四个兄弟节点顺序固定（皇帝/宇航公会/贝尼·杰瑟里特/弗雷曼，三个存档实测一致）；
        ///   Troop        —— 彼此等价，写成「部队」即可，短 ID 没有信息量。
        /// </summary>
        private static string GenericName(string en, Entity ent)
        {
            if (string.IsNullOrEmpty(en)) return null;

            if (en == "Troop") return "部队";

            if (en == "FactionTrack")
            {
                int idx = -1;
                try { idx = ent.PositionInParent; } catch { }
                string key = idx >= 0 && idx < FactionKeys.Length ? "lookup." + FactionKeys[idx] + "withicon" : null;
                // 这些键带前置图标标签（<sprite name="Emperor">皇帝），图标名对阵营名没有意义，去掉
                string nm = StripTags(key != null ? Coach.NameOf(key) : null);
                if (_factionLogged.Add(idx))
                    try { if (_log != null) _log.LogInfo("[TARGETS] faction track idx=" + idx + " -> " + (nm ?? "<null>")); } catch { }
                return string.IsNullOrEmpty(nm) ? "阵营轨道#" + idx : nm;
            }
            return null;
        }

        private static readonly string[] FactionKeys = { "emperor", "spacingguild", "benegesserit", "fremen" };

        /// 去掉 <sprite name=...> 这类富文本标签，保留标签外的文字
        private static string StripTags(string v)
        {
            if (string.IsNullOrEmpty(v)) return v;
            var sb = new System.Text.StringBuilder(v.Length);
            bool inTag = false;
            for (int i = 0; i < v.Length; i++)
            {
                char c = v[i];
                if (c == '<') { inTag = true; continue; }
                if (c == '>') { inTag = false; continue; }
                if (!inTag) sb.Append(c);
            }
            return sb.ToString().Trim();
        }

        // ---------- 文本清理 ----------

        /// 把 "key": {...} / "key": 123 整体替换成 "(略)"，其余内容原样保留。
        private static string StripBlock(string s, string key)
        {
            string pat = "\"" + key + "\":";
            int from = 0;
            while (true)
            {
                int i = s.IndexOf(pat, from, StringComparison.Ordinal);
                if (i < 0) return s;

                int v = i + pat.Length;
                while (v < s.Length && char.IsWhiteSpace(s[v])) v++;

                int end;
                if (v < s.Length && (s[v] == '{' || s[v] == '['))
                {
                    end = MatchClose(s, v);
                    if (end < 0) return s;
                    end++;   // 连同闭合括号一起吃掉
                }
                else
                {
                    end = v;
                    while (end < s.Length && s[end] != ',' && s[end] != '}' && s[end] != ']') end++;
                }

                s = s.Remove(v, end - v).Insert(v, "\"(略)\"");
                from = v + 5;
            }
        }

        /// 从 start（'{' 或 '['）找到匹配的闭合位置，字符串内的括号不计。
        private static int MatchClose(string s, int start)
        {
            char open = s[start];
            char close = open == '{' ? '}' : ']';
            int depth = 0;
            bool inStr = false;

            for (int i = start; i < s.Length; i++)
            {
                char c = s[i];
                if (inStr)
                {
                    if (c == '\\') i++;
                    else if (c == '"') inStr = false;
                    continue;
                }
                if (c == '"') { inStr = true; continue; }
                if (c == open) depth++;
                else if (c == close)
                {
                    depth--;
                    if (depth == 0) return i;
                }
            }
            return -1;
        }
    }
}

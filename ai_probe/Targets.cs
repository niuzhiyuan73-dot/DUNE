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

                var sb = new System.Text.StringBuilder();
                int rows = 0, withTargets = 0, withEffect = 0;
                foreach (var kv in map)
                {
                    if (rows++ >= 12) { sb.Append("- …\n"); break; }

                    string kid = null;
                    try { if (kv.Key != null) kid = kv.Key.ToString(); } catch { }
                    string key = kid != null ? Replace(kid, match) : "?";
                    // 选项侧补一句效果：模型反复说「没有效果文本，无法判断」，这一句就是给它判断依据
                    if (kid != null)
                    {
                        string eff = EffectFor(kid, match);
                        if (!string.IsNullOrEmpty(eff)) { key = key + "〔" + eff + "〕"; withEffect++; }
                        else
                        {
                            // 关键：沉默会被模型误读成"这牌没效果"，标注出来它就知道是数据缺失
                            key = key + "〔效果待查〕";
                            // 记录缺口时先剥掉 <sprite name=...>：能力实体常以图标名作名字，
                            // 直接记会把整行变成标签，gaps.txt 不可读
                            string gname = StripTags(key.Split('(')[0].Trim());
                            _gapCount[gname] = _gapCount.TryGetValue(gname, out int g) ? g + 1 : 1;
                        }
                    }
                    sb.Append("- ").Append(key);

                    var list = kv.Value;
                    if (list == null || list.Count == 0) { sb.Append("：（无子目标）\n"); continue; }

                    var parts = new List<string>();
                    for (int i = 0; i < list.Count && parts.Count < 12; i++)
                    {
                        string one = Describe(list[i], match, log);
                        if (!string.IsNullOrEmpty(one))
                        {
                            if (one.StartsWith("可选 ", StringComparison.Ordinal)) withTargets++;
                            parts.Add(one);
                        }
                    }
                    sb.Append("：").Append(parts.Count == 0 ? "（只能由按钮决定）" : string.Join("；", parts.ToArray()))
                      .Append('\n');
                }

                if (sb.Length == 0) return null;
                // 一行诊断：withTargets == 0 就说明 ValidTargets 还是没取到
                DumpGaps();
                log.LogInfo("[TARGETS] detail rows=" + rows + " withTargets=" + withTargets
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
        /// 两种带 ValidTargets 的目标类型，走 TryCast 拿到真正的派生类型包装器后反射取列表。
        /// 取不到（类型不符 / 列表为空）返回 null，交给调用方退回 ToString。
        /// </summary>
        private static string ValidTargetsOf(object ti, Canis.Match match, ManualLogSource log)
        {
            object cast = null;
            try
            {
                var obj = ti as Il2CppInterop.Runtime.InteropTypes.Il2CppObjectBase;
                if (obj == null) return null;
                try { cast = obj.TryCast<Networking.selection.targetinformation.EntityListTargetInformation>(); }
                catch { }
                if (cast == null)
                {
                    try { cast = obj.TryCast<Networking.selection.targetinformation.EntityGroupingTargetInformation>(); }
                    catch { }
                }
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
                foreach (object o in arr)
                {
                    if (names.Count >= 10) { names.Add("…"); break; }
                    string s = DescribeTarget(o, match, log);
                    if (!string.IsNullOrEmpty(s)) names.Add(s);
                }
                return names.Count == 0 ? null : "可选 " + string.Join("/", names.ToArray());
            }
            catch { return null; }
        }

        /// <summary>选项的效果摘要：优先用 Resolve 记下的效果键，GUID 查不到再按中文名回退。</summary>
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
            if (string.IsNullOrEmpty(e))
            {
                if (IsGuidish(key))
                {
                    string nm = Coach.NameOf(key);
                    if (!string.IsNullOrEmpty(nm)) e = Coach.EffectOf(nm);
                }
                else
                {
                    // worm.canis.abilities.<类名> 在 name_map 里存的就是效果描述
                    e = Coach.NameOf(key);
                }
            }
            return string.IsNullOrEmpty(e) ? null : e;
        }

        private static bool _elemLogged;
        private static readonly Dictionary<string, int> _gapCount = new Dictionary<string, int>(StringComparer.Ordinal);

        /// 把「查不到文本」的实体按出现次数写到 dump/ai/gaps.txt：
        /// 洞从"感觉有"变成可枚举清单，补一条少一条。
        private static void DumpGaps()
        {
            try
            {
                string dir = System.IO.Path.Combine(DuneAiProbe.Plugin.RootDir, "dump", "ai");
                System.IO.Directory.CreateDirectory(dir);
                var sb = new System.Text.StringBuilder();
                sb.Append("# 缺少效果文本的实体（按出现次数降序）\n# 行数: ").Append(_gapCount.Count).Append('\n');
                var list = new List<KeyValuePair<string, int>>(_gapCount);
                list.Sort((a, b) => b.Value.CompareTo(a.Value));
                foreach (var kv in list) sb.Append(kv.Value).Append('\t').Append(kv.Key).Append('\n');
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
            if (!string.IsNullOrEmpty(en)) return en;

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
            return id == null ? null : Replace(id, match);
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

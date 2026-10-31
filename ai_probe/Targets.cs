using System;
using System.Collections.Generic;
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

        public static string Rewrite(Canis.Match match, string text, ManualLogSource log)
        {
            if (string.IsNullOrEmpty(text)) return text;
            try
            {
                for (int i = 0; i < NoiseKeys.Length; i++) text = StripBlock(text, NoiseKeys[i]);

                IntPtr mp = IntPtr.Zero;
                try { if (match != null) mp = match.Pointer; } catch { }
                if (mp != _cacheMatch) { _cache.Clear(); _cacheMatch = mp; }

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
                int rows = 0;
                foreach (var kv in map)
                {
                    if (rows++ >= 12) { sb.Append("- …\n"); break; }

                    string key = "?";
                    try { if (kv.Key != null) key = Replace(kv.Key.ToString(), match); } catch { }
                    sb.Append("- ").Append(key);

                    var list = kv.Value;
                    if (list == null || list.Count == 0) { sb.Append("：（无子目标）\n"); continue; }

                    var parts = new List<string>();
                    for (int i = 0; i < list.Count && parts.Count < 12; i++)
                    {
                        string one = Describe(list[i], match);
                        if (!string.IsNullOrEmpty(one)) parts.Add(one);
                    }
                    sb.Append("：").Append(parts.Count == 0 ? "（只能由按钮决定）" : string.Join("；", parts.ToArray()))
                      .Append('\n');
                }

                if (sb.Length == 0) return null;
                return "【目标明细】\n" + sb;
            }
            catch (Exception e)
            {
                log.LogInfo("[TARGETS] detail failed: " + e.GetType().Name + ": " + e.Message);
                return null;
            }
        }

        private static string Describe(object ti, Canis.Match match)
        {
            if (ti == null) return null;
            string typeName = null;
            try { typeName = ti.GetType().Name; } catch { }

            // 带 ValidTargets 的实体类目标：把 ID 列表换成名字
            try
            {
                var pi = ti.GetType().GetProperty("ValidTargets");
                if (pi != null)
                {
                    object raw = pi.GetValue(ti);
                    var arr = raw as System.Collections.IEnumerable;
                    if (arr != null)
                    {
                        var names = new List<string>();
                        foreach (object o in arr)
                        {
                            if (names.Count >= 10) { names.Add("…"); break; }
                            string s = null;
                            try { if (o != null) s = Replace(o.ToString(), match); } catch { }
                            if (!string.IsNullOrEmpty(s)) names.Add(s);
                        }
                        if (names.Count > 0) return "可选 " + string.Join("/", names.ToArray());
                    }
                }
            }
            catch { }

            // 其余类型：ToString 已经带了 Choices / 提示，用它的结果
            try
            {
                string s = ti.ToString();
                if (string.IsNullOrEmpty(s)) return null;
                if (typeName != null && s.StartsWith("[" + typeName + "]")) s = s.Substring(typeName.Length + 2).Trim();
                return string.IsNullOrEmpty(s) ? null : s;
            }
            catch { return null; }
        }

        private static string Resolve(string id, Canis.Match match)
        {
            string shortId = id.Substring(0, 8);

            // 1) 原型 GUID（attributes 里的 10000 = archetypeID 就是这种）→ 查名称表即可
            string byArch = Coach.NameOf(id);
            if (byArch != null) { _hit++; return byArch + "(" + shortId + ")"; }

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

                string archGuid = null;
                try
                {
                    var a = ent.GetArchetype();
                    if (a != null && a.ArchetypeID != null) archGuid = a.ArchetypeID.ToString();
                }
                catch { }

                string human = Coach.NameOf(archGuid) ?? Coach.NameOf(en) ?? en;
                if (string.IsNullOrEmpty(human)) { _miss++; return shortId; }

                // 能力类实体：entityName 是类名（FreighterAgentAbility），中文名在 button.* 键下
                if (Coach.NameOf(archGuid) == null && Coach.NameOf(en) == null && en != null)
                {
                    string byButton = Coach.NameOf("button." + en);
                    if (byButton != null) human = byButton;
                }

                _hit++;
                return human + "(" + shortId + ")";
            }
            catch { _miss++; return shortId; }
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

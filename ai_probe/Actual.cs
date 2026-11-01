using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using BepInEx.Logging;

namespace DuneAiProbe
{
    /// <summary>
    /// 记录 AI 的**实际选择**，用于事后与教练建议比对（一致率）。
    ///
    /// 第一版失败过：直接把 MakeChoice 的返回值 ToString() 存下来，
    /// 拿到的只是接口名 `Networking.selection.messages.outgoing.ISelectionCounter`——
    /// 这个接口只有 GetCounter()，不含"选了哪个"。
    ///
    /// 正确的取值路径（类型转储 dump/T_net_sel.txt 已确认）：
    ///   SelectionWithTargets          → selection.entityID        （单选带目标）
    ///   MultipleSelectionWithTargets  → selection[].entityID      （多选）
    ///   GameCustomChoice              → Selection:int            （自定义序号）
    /// 这三种都实现 ISelectionCounter，所以返回值需要按具体类型 TryCast。
    ///
    /// 更关键的一点：entityID 就是 TargetMap 里的键，而我们在 Target 明细里
    /// 已经把这个键解析成了中文名（label）。所以只要在生成明细时顺手登记
    /// "键 → 名字"，事后就能把实选 entityID 还原成同一套名字，不必做模糊匹配。
    /// </summary>
    internal static class Actual
    {
        private static readonly object _lock = new object();
        private static readonly Dictionary<int, string> _results = new Dictionary<int, string>();
        // counter -> 有序的 (entityID, 名字) 列表
        private static readonly Dictionary<int, List<KeyValuePair<string, string>>> _options =
            new Dictionary<int, List<KeyValuePair<string, string>>>();
        private static int _recorded;
        private static ManualLogSource _log;

        [ThreadStatic] private static int _tlsCounter;

        public static void Init(ManualLogSource log) { _log = log; }

        /// <summary>prefix 里调用：登记本次决策的游戏序号。</summary>
        public static void Begin(int counter)
        {
            _tlsCounter = counter;
            lock (_lock)
            {
                if (!_options.ContainsKey(counter))
                    _options[counter] = new List<KeyValuePair<string, string>>();
            }
        }

        /// <summary>生成目标明细时调用：登记"实体ID → 中文名"，供事后还原实选。</summary>
        public static void NoteOption(int counter, string kid, string label)
        {
            if (counter <= 0 || string.IsNullOrEmpty(kid) || string.IsNullOrEmpty(label)) return;
            try
            {
                lock (_lock)
                {
                    List<KeyValuePair<string, string>> list;
                    if (!_options.TryGetValue(counter, out list))
                    {
                        list = new List<KeyValuePair<string, string>>();
                        _options[counter] = list;
                    }
                    for (int i = 0; i < list.Count; i++)
                        if (list[i].Key == kid) return;   // 已有，避免重复登记
                    list.Add(new KeyValuePair<string, string>(kid, label));
                }
            }
            catch { }
        }

        /// <summary>postfix 里调用：把实际选择记到对应序号上。</summary>
        public static void End(object result)
        {
            int counter = _tlsCounter;
            _tlsCounter = -1;
            if (counter < 0) return;
            try
            {
                string text = Resolve(counter, result);
                lock (_lock)
                {
                    _results[counter] = text;
                    Trim();
                    _recorded++;
                    if (_recorded <= 5 && _log != null)
                        _log.LogInfo("[ACTUAL] #" + counter + " -> " + Short(text, 120));
                }
            }
            catch (Exception e)
            {
                if (_log != null) _log.LogWarning("[ACTUAL] end: " + e.Message);
            }
        }

        /// <summary>落盘时调用：取该次决策的实际选择（取不到返回 null）。</summary>
        public static string ResultFor(int counter)
        {
            lock (_lock)
            {
                string v;
                return _results.TryGetValue(counter, out v) ? v : null;
            }
        }

        // ---------- 解析返回值 ----------

        private static string Resolve(int counter, object result)
        {
            if (result == null) return null;

            var bo = result as Il2CppInterop.Runtime.InteropTypes.Il2CppObjectBase;
            if (bo == null) return SafeToString(result);

            // 单选 + 目标：SelectionWithTargets
            try
            {
                var sw = bo.TryCast<Networking.selection.messages.outgoing.SelectionWithTargets>();
                if (sw != null)
                {
                    string kid = null;
                    try { if (sw.selection != null) kid = sw.selection.entityID != null ? sw.selection.entityID.ToString() : null; } catch { }
                    if (!string.IsNullOrEmpty(kid)) return LabelOf(counter, kid);
                }
            }
            catch { }

            // 多选：MultipleSelectionWithTargets
            try
            {
                var mw = bo.TryCast<Networking.selection.messages.outgoing.MultipleSelectionWithTargets>();
                if (mw != null && mw.selection != null)
                {
                    var names = new List<string>();
                    for (int i = 0; i < mw.selection.Count; i++)
                    {
                        string kid = null;
                        try
                        {
                            var item = mw.selection[i];
                            if (item != null && item.entityID != null) kid = item.entityID.ToString();
                        }
                        catch { }
                        if (!string.IsNullOrEmpty(kid)) names.Add(LabelOf(counter, kid));
                    }
                    if (names.Count > 0) return string.Join(" + ", names.ToArray());
                }
                else if (mw != null && (mw.selection == null || mw.selection.Count == 0))
                {
                    return "（空选择，可能跳过/放弃）";
                }
            }
            catch { }

            // 自定义选项：GameCustomChoice.Selection 是序号
            try
            {
                var gc = bo.TryCast<dwd.core.match.messages.outgoing.GameCustomChoice>();
                if (gc != null)
                {
                    string byIdx = LabelByIndex(counter, gc.Selection);
                    if (!string.IsNullOrEmpty(byIdx)) return byIdx;
                }
            }
            catch { }

            return SafeToString(result);
        }

        private static string LabelOf(int counter, string kid)
        {
            lock (_lock)
            {
                List<KeyValuePair<string, string>> list;
                if (_options.TryGetValue(counter, out list))
                {
                    for (int i = 0; i < list.Count; i++)
                        if (list[i].Key == kid) return list[i].Value;
                }
            }
            // 未登记（例如选项不是 TargetMap 里的键）：保留可回溯的短 ID
            return "?" + (kid.Length > 8 ? kid.Substring(0, 8) : kid);
        }

        private static string LabelByIndex(int counter, Il2CppSystem.Nullable<int> sel)
        {
            try
            {
                if (sel == null || !sel.HasValue) return null;
                int idx = sel.Value;
                lock (_lock)
                {
                    List<KeyValuePair<string, string>> list;
                    if (_options.TryGetValue(counter, out list) && idx >= 0 && idx < list.Count)
                        return list[idx].Value + "（序号 " + idx + "）";
                }
                return "序号 " + idx;
            }
            catch { return null; }
        }

        // ---------- 小工具 ----------

        private static void Trim()
        {
            if (_results.Count <= 200 && _options.Count <= 200) return;
            var keys = new List<int>(_results.Keys);
            keys.Sort();
            for (int i = 0; i < keys.Count - 200; i++)
            {
                _results.Remove(keys[i]);
                _options.Remove(keys[i]);
            }
        }

        private static string SafeToString(object o)
        {
            try
            {
                string s = o.ToString();
                // 接口代理的 ToString 只会给类型名，没有信息量，标明一下
                if (s != null && s.StartsWith("Networking.selection.messages.outgoing.", StringComparison.Ordinal))
                    return s + "（接口代理，未解析到具体类型）";
                return s;
            }
            catch { return null; }
        }

        private static string Short(string s, int n)
        {
            if (string.IsNullOrEmpty(s)) return "";
            s = s.Replace("\r", " ").Replace("\n", " ");
            return s.Length <= n ? s : s.Substring(0, n) + "…";
        }
    }
}

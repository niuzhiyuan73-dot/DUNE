using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using BepInEx.Logging;

namespace DuneAiProbe
{
    /// <summary>
    /// 记录 AI 的**实际选择**，用于事后与教练建议比对（采纳率/一致率）。
    ///
    /// 为什么只记原文、不在 C# 里判"选得对不对"：
    /// MakeChoice 的返回值是 ISelectionCounter，具体实现类没有类型转储，
    /// 字段名不可靠。与其猜结构，不如把可控的部分记全（ToString + 白名单属性），
    /// 判定放到离线脚本里做——那边改一行就能重跑，不用重新编译插件、不用再开一局。
    ///
    /// 配对方式：prefix 记录"本次决策的游戏序号"（线程静态），postfix 取 __result。
    /// AI 决策是同步调用（几百毫秒），所以 postfix 一定在 prefix 之后、下一条请求之前。
    /// </summary>
    internal static class Actual
    {
        private static readonly object _lock = new object();
        private static readonly Dictionary<int, string> _results = new Dictionary<int, string>();
        private static int _recorded;
        private static ManualLogSource _log;

        [ThreadStatic] private static int _tlsCounter;

        // 返回值里可能承载"选了什么"的属性名。逐个试，取第一个非空。
        private static readonly string[] CandidateProps =
        {
            "Selection", "Selected", "Choice", "Entity", "EntityID", "SelectionID",
            "Value", "Counter", "Targets", "Response",
        };

        public static void Init(ManualLogSource log)
        {
            _log = log;
        }

        /// <summary>prefix 里调用：登记本次决策的游戏序号。</summary>
        public static void Begin(int counter)
        {
            _tlsCounter = counter;
        }

        /// <summary>postfix 里调用：把实际选择记到对应序号上。失败只记原始文本，不抛。</summary>
        public static void End(object result)
        {
            try
            {
                int counter = _tlsCounter;
                _tlsCounter = -1;
                if (counter < 0) return;

                string text = Describe(result);
                lock (_lock)
                {
                    _results[counter] = text;
                    // 只保留最近 200 条，避免长局无限增长
                    if (_results.Count > 200)
                    {
                        var keys = new List<int>(_results.Keys);
                        keys.Sort();
                        for (int i = 0; i < keys.Count - 200; i++) _results.Remove(keys[i]);
                    }
                    _recorded++;
                    if (_recorded <= 5 && _log != null)
                        _log.LogInfo("[ACTUAL] #" + counter + " = " + Short(text, 160));
                }
            }
            catch (Exception e)
            {
                if (_log != null) _log.LogWarning("[ACTUAL] end: " + e.Message);
            }
        }

        /// <summary>落盘时调用：取该次决策的实际选择文本（取不到返回 null）。</summary>
        public static string ResultFor(int counter)
        {
            lock (_lock)
            {
                string v;
                return _results.TryGetValue(counter, out v) ? v : null;
            }
        }

        // ---------- 取值 ----------

        private static string Describe(object result)
        {
            if (result == null) return null;

            // 先试白名单属性：拿到实体/ID 比整段 ToString 更容易匹配
            try
            {
                var t = result.GetType();
                foreach (var name in CandidateProps)
                {
                    object v = null;
                    try
                    {
                        var pi = t.GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
                        if (pi != null) v = pi.GetValue(result);
                        if (v == null)
                        {
                            var fi = t.GetField(name, BindingFlags.Public | BindingFlags.Instance);
                            if (fi != null) v = fi.GetValue(result);
                        }
                    }
                    catch { }
                    if (v == null) continue;
                    string s = SafeStr(v);
                    if (!string.IsNullOrEmpty(s) && s != "null")
                        return "[" + name + "] " + s;
                }
            }
            catch { }

            string raw = null;
            try { raw = result.ToString(); } catch { }
            return raw;
        }

        private static string SafeStr(object v)
        {
            try
            {
                // 实体对象优先取 EntityName / ID，其次 ToString
                var t = v.GetType();
                foreach (var n in new[] { "EntityName", "Name", "ID", "Id" })
                {
                    try
                    {
                        var pi = t.GetProperty(n, BindingFlags.Public | BindingFlags.Instance);
                        if (pi == null) continue;
                        var x = pi.GetValue(v);
                        if (x != null) return x.ToString();
                    }
                    catch { }
                }
                return v.ToString();
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

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using BepInEx;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;
using UnityEngine;
using worm.canis.actions.phases;
using WormAIConstants = worm.canis.ai.AIConstants;
using WormAIConstantsRef = worm.canis.ai.AIConstantsRef;

namespace DuneAiProbe
{
    /// <summary>
    /// AI 探针：导出一局实际使用的 AI 权重表，可选覆盖。
    ///
    /// 产出：
    ///   dump/ai_constants_runtime.json   三档难度（Easy/Medium/Hard）的完整权重
    ///   日志 [AIPROBE] 行                常量加载路径、已初始化的难度档、覆盖明细
    ///
    /// 覆盖入口：DUNE/mods/ai/ai_override.json
    ///   { "Hard": { "VictoryPointValueLate": 14.0 } }
    ///   key 取 "Easy" / "Medium" / "Hard" / "all"
    ///
    /// 只在 SetupPlayer 触发一次，不影响对局流程。
    /// </summary>
    [BepInPlugin(GUID, "Dune AI Probe", "1.0.0")]
    public class Plugin : BasePlugin
    {
        public const string GUID = "local.duneaiprobe";

        private const string Root = @"C:/Users/17789/Desktop/DUNE";
        private const string DumpPath = Root + "/dump/ai_constants_runtime.json";
        private const string OverridePath = Root + "/mods/ai/ai_override.json";
        private const string ForceLevelPath = Root + "/mods/ai/force_level.txt";

        internal static ManualLogSource L;
        private static bool _done;

        public override void Load()
        {
            L = Log;
            L.LogInfo("========== [AIPROBE] load ==========");
            LogEnvironment();

            var h = new Harmony(GUID + ".harmony");
            var sp = typeof(SetupPhase).GetMethod("SetupPlayer",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly);
            if (sp != null)
            {
                h.Patch(sp, prefix: new HarmonyMethod(typeof(Plugin).GetMethod(nameof(OnSetup),
                    BindingFlags.Static | BindingFlags.NonPublic)));
                L.LogInfo("[AIPROBE] hooked SetupPhase.SetupPlayer");
            }
            else L.LogWarning("[AIPROBE] SetupPlayer not found");

            // 次要钩子：记录实际写进 AIConstants 的难度档（绑定失败不影响主流程）
            try
            {
                var si = typeof(WormAIConstants).GetMethod("SetInstance",
                    BindingFlags.Public | BindingFlags.Static, null,
                    new[] { typeof(int), typeof(WormAIConstantsRef) }, null);
                if (si != null)
                {
                    h.Patch(si, prefix: new HarmonyMethod(typeof(Plugin).GetMethod(nameof(OnSetInstance),
                        BindingFlags.Static | BindingFlags.NonPublic)));
                    L.LogInfo("[AIPROBE] hooked AIConstants.SetInstance");
                }
                else L.LogWarning("[AIPROBE] SetInstance(int, ref) not found");
            }
            catch (Exception e) { L.LogWarning("[AIPROBE] SetInstance hook: " + e.Message); }
        }

        private static void OnSetInstance(int aiLevel)
        {
            try { L.LogInfo("[AIPROBE] SetInstance(aiLevel=" + aiLevel + " -> " + LevelName(aiLevel) + ")"); }
            catch { }
        }

        private static void LogEnvironment()
        {
            try
            {
                var t = typeof(WormAIConstants);
                var fp = t.GetProperty("FilePattern", BindingFlags.Public | BindingFlags.Static);
                L.LogInfo("[AIPROBE] FilePattern = " + (fp?.GetValue(null)?.ToString() ?? "<null>"));
            }
            catch (Exception e) { L.LogWarning("[AIPROBE] FilePattern: " + e.Message); }

            var dirs = new List<string>();
            try
            {
                dirs.Add(Application.persistentDataPath);
                dirs.Add(Application.dataPath);
                dirs.Add(Application.streamingAssetsPath);
            }
            catch { }

            foreach (var d in dirs.Where(x => !string.IsNullOrEmpty(x)).Distinct())
            {
                L.LogInfo("[AIPROBE] dir = " + d);
                try
                {
                    if (!Directory.Exists(d)) { L.LogInfo("[AIPROBE]   (missing)"); continue; }
                    var hits = Directory.GetFiles(d, "AIConstants*.json", SearchOption.AllDirectories);
                    L.LogInfo("[AIPROBE]   AIConstants*.json = " + hits.Length);
                    foreach (var x in hits.Take(10)) L.LogInfo("[AIPROBE]     " + Path.GetFileName(x));
                }
                catch (Exception e) { L.LogInfo("[AIPROBE]   scan error: " + e.Message); }
            }
        }

        // ---------- 入口 ----------

        private static void OnSetup(SetupPhase __instance)
        {
            if (_done) return;
            _done = true;
            try { Inspect(); }
            catch (Exception e) { L.LogWarning("[AIPROBE] inspect: " + e.GetType().Name + ": " + e.Message); }
        }

        private static void Inspect()
        {
            var refs = new Dictionary<string, WormAIConstantsRef>();
            refs["Easy"] = GetRef(0);
            refs["Medium"] = GetRef(1);
            refs["Hard"] = GetRef(2);

            foreach (var kv in refs)
                L.LogInfo("[AIPROBE] " + kv.Key + " ref = " + (kv.Value == null ? "<null>" : Ptr(kv.Value)));

            // loadedConstants：Count + 逐档 ContainsKey，避免 IL2CPP 泛型枚举失败
            try
            {
                var lc = WormAIConstants.loadedConstants;
                if (lc != null)
                {
                    var present = new List<string>();
                    for (int lv = 0; lv <= 2; lv++)
                    {
                        bool has = false;
                        try { has = lc.ContainsKey(lv); } catch { }
                        if (has) present.Add(LevelName(lv));
                    }
                    L.LogInfo("[AIPROBE] loadedConstants count=" + lc.Count
                              + " present=[" + string.Join(", ", present) + "]");
                }
            }
            catch (Exception e) { L.LogWarning("[AIPROBE] loadedConstants: " + e.Message); }

            DumpAll(refs);
            ApplyOverrides(refs);
            ForceLevel(refs);
        }

        /// 读 mods/ai/force_level.txt（单行 0/1/2），无视剧本里的 aiLevel 强制该难度档。
        /// 用于在不重生成 156 套剧本包的前提下整体调档。
        private static void ForceLevel(Dictionary<string, WormAIConstantsRef> refs)
        {
            if (!File.Exists(ForceLevelPath))
            {
                L.LogInfo("[AIPROBE] no force_level.txt, keep scenario aiLevel");
                return;
            }

            string raw = File.ReadAllText(ForceLevelPath).Trim();
            if (!int.TryParse(raw, out int lv) || lv < 0 || lv > 2)
            {
                L.LogWarning("[AIPROBE] force_level.txt 内容无效: '" + raw + "'（应为 0/1/2）");
                return;
            }

            if (!refs.TryGetValue(LevelName(lv), out var r) || r == null)
            {
                L.LogWarning("[AIPROBE] force level " + lv + ": ref is null");
                return;
            }

            try
            {
                WormAIConstants.SetInstance(lv, r);
                L.LogWarning("[AIPROBE] forced AIConstants to " + LevelName(lv) + " (" + lv + ")");
            }
            catch (Exception e) { L.LogWarning("[AIPROBE] SetInstance: " + e.Message); }
        }

        private static string Ptr(WormAIConstantsRef r)
        {
            try
            {
                var p = r.GetType().GetProperty("Pointer", BindingFlags.Public | BindingFlags.Instance);
                if (p != null) return "0x" + Convert.ToInt64(p.GetValue(r)).ToString("X");
            }
            catch { }
            return r.ToString();
        }

        private static string LevelName(int lv)
        {
            if (lv == 0) return "Easy";
            if (lv == 1) return "Medium";
            if (lv == 2) return "Hard";
            return "Level" + lv;
        }

        private static WormAIConstantsRef GetRef(int lv)        {
            try
            {
                switch (lv)
                {
                    case 0: return WormAIConstants.EasyAIConstantsRef;
                    case 1: return WormAIConstants.MediumAIConstantsRef;
                    case 2: return WormAIConstants.HardAIConstantsRef;
                }
            }
            catch (Exception e) { L.LogWarning("[AIPROBE] GetRef(" + lv + "): " + e.Message); }
            return null;
        }

        private static List<PropertyInfo> DoubleProps()
        {
            return typeof(WormAIConstantsRef)
                .GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(p => p.PropertyType == typeof(double))
                .OrderBy(p => p.Name, StringComparer.Ordinal)
                .ToList();
        }

        private static string Read(WormAIConstantsRef r, PropertyInfo p)
        {
            try { return ((double)p.GetValue(r)).ToString("R", System.Globalization.CultureInfo.InvariantCulture); }
            catch { return null; }
        }

        // ---------- 导出 ----------

        private static void DumpAll(Dictionary<string, WormAIConstantsRef> refs)
        {
            var props = DoubleProps();
            var sb = new StringBuilder();
            sb.Append("{\n  \"_props\": ").Append(props.Count).Append(",\n");

            bool first = true;
            foreach (var kv in refs)
            {
                if (!first) sb.Append(",\n");
                first = false;
                sb.Append("  \"").Append(kv.Key).Append("\": ");
                if (kv.Value == null) { sb.Append("null"); continue; }

                sb.Append("{\n");
                for (int i = 0; i < props.Count; i++)
                {
                    var v = Read(kv.Value, props[i]);
                    sb.Append("    \"").Append(props[i].Name).Append("\": ").Append(v ?? "null");
                    if (i < props.Count - 1) sb.Append(",");
                    sb.Append("\n");
                }
                sb.Append("  }");
            }
            sb.Append("\n}\n");

            Directory.CreateDirectory(Path.GetDirectoryName(DumpPath));
            File.WriteAllText(DumpPath, sb.ToString(), new UTF8Encoding(false));
            L.LogWarning("[AIPROBE] >>> wrote " + DumpPath + " (" + props.Count + " props x " + refs.Count + " levels)");
        }

        // ---------- 覆盖 ----------

        private static void ApplyOverrides(Dictionary<string, WormAIConstantsRef> refs)
        {
            if (!File.Exists(OverridePath))
            {
                L.LogInfo("[AIPROBE] no override file, skip");
                return;
            }

            var table = MiniJson.ParseObject(File.ReadAllText(OverridePath, Encoding.UTF8));
            var props = new Dictionary<string, PropertyInfo>(StringComparer.Ordinal);
            foreach (var p in DoubleProps()) props[p.Name] = p;

            foreach (var kv in table)
            {
                var targets = new List<KeyValuePair<string, WormAIConstantsRef>>();
                if (kv.Key == "all")
                {
                    foreach (var r0 in refs)
                        if (r0.Value != null) targets.Add(r0);
                }
                else if (refs.TryGetValue(kv.Key, out var only) && only != null)
                    targets.Add(new KeyValuePair<string, WormAIConstantsRef>(kv.Key, only));
                else
                {
                    L.LogWarning("[AIPROBE] override: unknown level '" + kv.Key + "'");
                    continue;
                }

                foreach (var t in targets)
                {
                    int n = 0;
                    foreach (var pv in kv.Value)
                    {
                        if (!props.TryGetValue(pv.Key, out var p)) { L.LogWarning("[AIPROBE] unknown prop " + pv.Key); continue; }
                        if (!double.TryParse(pv.Value, System.Globalization.NumberStyles.Float,
                                System.Globalization.CultureInfo.InvariantCulture, out double v))
                        {
                            L.LogWarning("[AIPROBE] bad number " + pv.Key + "=" + pv.Value);
                            continue;
                        }
                        double old;
                        try { old = (double)p.GetValue(t.Value); } catch { continue; }
                        try
                        {
                            p.SetValue(t.Value, v);
                            if (Math.Abs(old - v) > 1e-9)
                                L.LogInfo("[AIPROBE] " + t.Key + "." + pv.Key + ": " + old + " -> " + v);
                            n++;
                        }
                        catch (Exception e) { L.LogWarning("[AIPROBE] set " + pv.Key + ": " + e.Message); }
                    }
                    L.LogWarning("[AIPROBE] applied " + n + " overrides to " + t.Key);
                }
            }
        }
    }

    /// 极简 JSON：只解析 {"level": {"prop": number}} 两层结构。
    internal static class MiniJson
    {
        public static Dictionary<string, Dictionary<string, string>> ParseObject(string s)
        {
            var outer = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
            int i = 0;
            while (i < s.Length)
            {
                i = SkipWs(s, i);
                if (i >= s.Length || s[i] == '}') break;
                if (s[i] != '"') { i++; continue; }
                string k = ReadString(s, ref i);
                i = SkipWs(s, i);
                if (i >= s.Length || s[i] != ':') break;
                i = SkipWs(s, i + 1);
                if (i < s.Length && s[i] == '{')
                {
                    i++;
                    var inner = new Dictionary<string, string>(StringComparer.Ordinal);
                    while (i < s.Length)
                    {
                        i = SkipWs(s, i);
                        if (i < s.Length && s[i] == '}') { i++; break; }
                        if (i >= s.Length || s[i] != '"') { i++; continue; }
                        string ik = ReadString(s, ref i);
                        i = SkipWs(s, i);
                        if (i >= s.Length || s[i] != ':') break;
                        i = SkipWs(s, i + 1);
                        int start = i;
                        while (i < s.Length && s[i] != ',' && s[i] != '}') i++;
                        inner[ik] = s.Substring(start, i - start).Trim().Trim('"');
                        if (i < s.Length && s[i] == ',') i++;
                    }
                    outer[k] = inner;
                }
                else
                {
                    while (i < s.Length && s[i] != ',' && s[i] != '}') i++;
                }
            }
            return outer;
        }

        private static int SkipWs(string s, int i)
        {
            while (i < s.Length && char.IsWhiteSpace(s[i])) i++;
            return i;
        }

        private static string ReadString(string s, ref int i)
        {
            if (i < s.Length && s[i] == '"') i++;
            var sb = new StringBuilder();
            while (i < s.Length && s[i] != '"')
            {
                if (s[i] == '\\' && i + 1 < s.Length) i++;
                sb.Append(s[i]); i++;
            }
            if (i < s.Length) i++;
            return sb.ToString();
        }
    }
}

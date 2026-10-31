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
using Canis.entities.ai;
using WormAIConstants = worm.canis.ai.AIConstants;
using WormAIConstantsRef = worm.canis.ai.AIConstantsRef;

namespace DuneAiProbe
{
    /// <summary>
    /// AI 探针 v2。
    ///
    /// 第一版在 SetupPlayer 采集，实测三档 ref 全为 null——AI 常量是惰性初始化的，
    /// 开局建玩家阶段还没建表。改为挂 AIProfileAbs.MakeChoice（AI 第一次做决策时必然已建表）。
    ///
    /// 产出：
    ///   dump/ai_constants_runtime.json   实际存在的难度档的完整权重
    ///   日志 [AIPROBE]                    实际使用的难度档、各档是否建表、覆盖明细
    ///
    /// 覆盖入口：
    ///   mods/ai/ai_override.json   { "Hard": { "VictoryPointValueLate": 14.0 } }
    ///   mods/ai/force_level.txt    单行 0/1/2，把该档权重整体拷到本局实际使用的档
    /// </summary>
    [BepInPlugin(GUID, "Dune AI Probe", "2.0.0")]
    public class Plugin : BasePlugin
    {
        public const string GUID = "local.duneaiprobe";

        private const string Root = @"C:/Users/17789/Desktop/DUNE";
        private const string DumpPath = Root + "/dump/ai_constants_runtime.json";
        private const string OverridePath = Root + "/mods/ai/ai_override.json";
        private const string ForceLevelPath = Root + "/mods/ai/force_level.txt";

        internal static ManualLogSource L;
        private static int _attempts;
        private static bool _finished;
        private static bool _loggedPlayerLevel;

        public override void Load()
        {
            L = Log;
            L.LogInfo("========== [AIPROBE] load ==========");
            LogEnvironment();

            var h = new Harmony(GUID + ".harmony");

            // 更早的触发点：AI 档案创建时（若此时常量已就绪，可省掉一次 AI 决策）
            try
            {
                var ctor = typeof(Canis.entities.ai.standard.StandardAIProfile).GetConstructor(
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance,
                    null, new[] { typeof(Canis.entities.PlayerEntity), typeof(Canis.Match) }, null);
                if (ctor != null)
                {
                    h.Patch(ctor, postfix: new HarmonyMethod(typeof(Plugin).GetMethod(nameof(OnProfileCtor),
                        BindingFlags.Static | BindingFlags.NonPublic)));
                    L.LogInfo("[AIPROBE] hooked StandardAIProfile..ctor");
                }
                else L.LogWarning("[AIPROBE] StandardAIProfile ctor not found");
            }
            catch (Exception e) { L.LogWarning("[AIPROBE] ctor hook: " + e.Message); }

            try
            {
                var mc = typeof(AIProfileAbs).GetMethod("MakeChoice",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (mc != null)
                {
                    h.Patch(mc, prefix: new HarmonyMethod(typeof(Plugin).GetMethod(nameof(OnMakeChoice),
                        BindingFlags.Static | BindingFlags.NonPublic)));
                    L.LogInfo("[AIPROBE] hooked AIProfileAbs.MakeChoice");
                }
                else L.LogWarning("[AIPROBE] MakeChoice not found");
            }
            catch (Exception e) { L.LogWarning("[AIPROBE] MakeChoice hook: " + e.Message); }

            // 兜底触发点：AI 自己的日志方法，几乎每次决策都会走
            try
            {
                var al = typeof(AIProfileAbs).GetMethod("AILog",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (al != null)
                {
                    h.Patch(al, prefix: new HarmonyMethod(typeof(Plugin).GetMethod(nameof(OnAiLog),
                        BindingFlags.Static | BindingFlags.NonPublic)));
                    L.LogInfo("[AIPROBE] hooked AIProfileAbs.AILog");
                }
                else L.LogWarning("[AIPROBE] AILog not found");
            }
            catch (Exception e) { L.LogWarning("[AIPROBE] AILog hook: " + e.Message); }

            try
            {
                var sp = typeof(SetupPhase).GetMethod("SetupPlayer",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly);
                if (sp != null)
                {
                    h.Patch(sp, prefix: new HarmonyMethod(typeof(Plugin).GetMethod(nameof(OnSetup),
                        BindingFlags.Static | BindingFlags.NonPublic)));
                    L.LogInfo("[AIPROBE] hooked SetupPhase.SetupPlayer (marker only)");
                }
            }
            catch (Exception e) { L.LogWarning("[AIPROBE] SetupPlayer hook: " + e.Message); }

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
            }
            catch (Exception e) { L.LogWarning("[AIPROBE] SetInstance hook: " + e.Message); }
        }

        // ---------- 钩子 ----------

        private static void OnSetup(SetupPhase __instance)
        {
            L.LogInfo("[AIPROBE] setup marker: player being set up");
        }

        private static void OnSetInstance(int aiLevel)
        {
            try { L.LogInfo("[AIPROBE] SetInstance(aiLevel=" + aiLevel + " -> " + LevelName(aiLevel) + ")"); }
            catch { }
        }

        private static void OnProfileCtor(AIProfileAbs __instance)
        {
            EnsureResolved(__instance, "AIProfileCtor");
        }

        private static int _aiLogLines;

        private static void OnAiLog(AIProfileAbs __instance, string str)
        {
            if (_aiLogLines < 40)
            {
                _aiLogLines++;
                try { L.LogInfo("[AIPROBE][ailog] " + (str ?? "")); } catch { }
            }
            EnsureResolved(__instance, "AILog");
        }

        private static void OnMakeChoice(AIProfileAbs __instance)
        {
            EnsureResolved(__instance, "MakeChoice");
        }

        private static void EnsureResolved(AIProfileAbs __instance, string source)
        {
            if (_finished) return;
            _attempts++;

            int used = -1;
            try
            {
                var p = __instance.Player;
                if (p != null)
                {
                    used = p.AILevel;
                    if (!_loggedPlayerLevel)
                    {
                        _loggedPlayerLevel = true;
                        L.LogInfo("[AIPROBE] first AI decision by player '" + Safe(() => p.PlayerName)
                                  + "' AILevel=" + used + " (" + LevelName(used) + ")");
                    }
                }
            }
            catch (Exception e) { if (_attempts <= 2) L.LogWarning("[AIPROBE] player: " + e.Message); }

            Dictionary<string, WormAIConstantsRef> refs;
            try { refs = ResolveAll(); }
            catch (Exception e) { L.LogWarning("[AIPROBE] resolve: " + e.Message); return; }

            int nonNull = 0;
            foreach (var kv in refs) if (kv.Value != null) nonNull++;

            if (nonNull == 0)
            {
                if (_attempts <= 3 || _attempts % 50 == 0)
                    L.LogInfo("[AIPROBE] attempt " + _attempts + ": all refs still null");
                return;
            }

            _finished = true;
            L.LogInfo("[AIPROBE] resolved " + nonNull + "/3 refs on attempt " + _attempts
                      + " via " + source + " (used level " + LevelName(used) + ")");

            DumpAll(refs);
            ApplyOverrides(refs, used);
        }

        // ---------- 解析 ----------

        private static WormAIConstantsRef Resolve(int lv)
        {
            // 优先取运行时字典里的实例（真正被 AI 使用的那个对象）
            try
            {
                var lc = WormAIConstants.loadedConstants;
                if (lc != null)
                {
                    WormAIConstantsRef r;
                    if (lc.TryGetValue(lv, out r) && r != null) return r;
                }
            }
            catch { }
            return GetRef(lv);
        }

        private static Dictionary<string, WormAIConstantsRef> ResolveAll()
        {
            var d = new Dictionary<string, WormAIConstantsRef>();
            for (int lv = 0; lv <= 2; lv++) d[LevelName(lv)] = Resolve(lv);
            return d;
        }

        private static string LevelName(int lv)
        {
            if (lv == 0) return "Easy";
            if (lv == 1) return "Medium";
            if (lv == 2) return "Hard";
            return "Level" + lv;
        }

        private static WormAIConstantsRef GetRef(int lv)
        {
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
                .Where(x => x.PropertyType == typeof(double))
                .OrderBy(x => x.Name, StringComparer.Ordinal)
                .ToList();
        }

        private static string Read(WormAIConstantsRef r, PropertyInfo p)
        {
            try { return ((double)p.GetValue(r)).ToString("R", System.Globalization.CultureInfo.InvariantCulture); }
            catch { return null; }
        }

        private static string Safe(Func<string> f)
        {
            try { return f() ?? ""; } catch { return "?"; }
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
                if (kv.Value == null) continue;
                if (!first) sb.Append(",\n");
                first = false;
                sb.Append("  \"").Append(kv.Key).Append("\": {\n");
                for (int i = 0; i < props.Count; i++)
                {
                    sb.Append("    \"").Append(props[i].Name).Append("\": ").Append(Read(kv.Value, props[i]) ?? "null");
                    if (i < props.Count - 1) sb.Append(",");
                    sb.Append("\n");
                }
                sb.Append("  }");
            }
            sb.Append("\n}\n");

            Directory.CreateDirectory(Path.GetDirectoryName(DumpPath));
            File.WriteAllText(DumpPath, sb.ToString(), new UTF8Encoding(false));
            L.LogWarning("[AIPROBE] >>> wrote " + DumpPath + " (" + props.Count + " props)");
        }

        // ---------- 调档 / 覆盖 ----------

        private static void ApplyOverrides(Dictionary<string, WormAIConstantsRef> refs, int usedLevel)
        {
            string usedName = LevelName(usedLevel);

            // 1) force_level：把指定档的权重整体拷到本局实际使用的档
            int force = ReadForceLevel();
            if (force >= 0 && force != usedLevel)
            {
                string srcName = LevelName(force);
                if (refs.TryGetValue(srcName, out var src) && src != null
                    && refs.TryGetValue(usedName, out var dst) && dst != null)
                {
                    int n = 0;
                    foreach (var p in DoubleProps())
                    {
                        try
                        {
                            double v = (double)p.GetValue(src);
                            p.SetValue(dst, v);
                            n++;
                        }
                        catch { }
                    }
                    L.LogWarning("[AIPROBE] forced " + usedName + " := " + srcName + " (" + n + " props copied)");
                }
                else L.LogWarning("[AIPROBE] force level " + force + ": source or target ref missing");
            }

            // 2) ai_override.json：按档覆盖
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
                    foreach (var r0 in refs) if (r0.Value != null) targets.Add(r0);
                }
                else if (refs.TryGetValue(kv.Key, out var only) && only != null)
                    targets.Add(new KeyValuePair<string, WormAIConstantsRef>(kv.Key, only));
                else { L.LogWarning("[AIPROBE] override: unknown level '" + kv.Key + "'"); continue; }

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

        private static int ReadForceLevel()
        {
            try
            {
                if (!File.Exists(ForceLevelPath)) return -1;
                string raw = File.ReadAllText(ForceLevelPath).Trim();
                if (int.TryParse(raw, out int lv) && lv >= 0 && lv <= 2) return lv;
                L.LogWarning("[AIPROBE] force_level.txt 内容无效: '" + raw + "'");
            }
            catch (Exception e) { L.LogWarning("[AIPROBE] force_level: " + e.Message); }
            return -1;
        }

        // ---------- 环境 ----------

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

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
using worm.canis.entities;
using Canis.entities;
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
        internal const string RootDir = Root;

        /// 每次改动手工递增：日志里能一眼确认加载的是哪份构建（DLL 被锁时会照旧版本跑）
        internal const string BuildTag = "2026-11-01b";
        private const string DumpPath = Root + "/dump/ai_constants_runtime.json";
        private const string OverridePath = Root + "/mods/ai/ai_override.json";
        private const string ForceLevelPath = Root + "/mods/ai/force_level.txt";
        private const string TimeoutPath = Root + "/mods/ai/extend_timeout.txt";

        private static double _timeoutFactor = 1.0;
        private static int _timeoutCalls;
        private static long _batchStart;
        private static int _batchCount;

        internal static ManualLogSource L;
        private static int _attempts;
        private static bool _finished;
        private static bool _loggedPlayerLevel;

        public override void Load()
        {
            L = Log;
            L.LogInfo("========== [AIPROBE] load ========== build " + BuildTag);
            LogEnvironment();

            var h = new Harmony(GUID + ".harmony");
            _harmony = h;
            ReadTimeoutFactor();
            SurveyTimeoutApi(h);
            Coach.Init(L, Root);

            // 最可靠的一处：所有 AI 响应都在这里生成（含开局选领袖）
            try
            {
                var rr = typeof(Canis.hasExecutionStack.HasExecutionStack).GetMethod("RunAIResponses",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (rr != null)
                {
                    h.Patch(rr,
                        prefix: new HarmonyMethod(typeof(Plugin).GetMethod(nameof(OnRunAIStart),
                            BindingFlags.Static | BindingFlags.NonPublic)),
                        postfix: new HarmonyMethod(typeof(Plugin).GetMethod(nameof(OnRunAIResponses),
                            BindingFlags.Static | BindingFlags.NonPublic)));
                    L.LogInfo("[AIPROBE] hooked HasExecutionStack.RunAIResponses");
                }
                else L.LogWarning("[AIPROBE] RunAIResponses not found");
            }
            catch (Exception e) { L.LogWarning("[AIPROBE] RunAIResponses hook: " + e.Message); }

            // 真正的 AI 决策入口：worm.canis.ai.WormAIProfile.MakeChoice（AIProfileAbs 那条从没触发过）
            // 注意：不能直接 GetMethod("MakeChoice")——派生类与基类同名同签名会让它抛 Ambiguous match。
            try
            {
                var cand = typeof(worm.canis.ai.WormAIProfile)
                    .GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                    .Where(m => m.Name == "MakeChoice")
                    .ToList();
                foreach (var m in cand)
                {
                    L.LogInfo("[AIPROBE] MakeChoice candidate: " + m.DeclaringType.FullName + "(" +
                              string.Join(", ", m.GetParameters().Select(p => p.ParameterType.Name).ToArray()) + ")");
                }

                var wm = cand.FirstOrDefault(m => m.DeclaringType == typeof(worm.canis.ai.WormAIProfile))
                         ?? cand.FirstOrDefault();
                if (wm != null)
                {
                    h.Patch(wm, prefix: new HarmonyMethod(typeof(Plugin).GetMethod(nameof(OnWormMakeChoice),
                        BindingFlags.Static | BindingFlags.NonPublic)));
                    L.LogInfo("[AIPROBE] hooked WormAIProfile.MakeChoice on " + wm.DeclaringType.FullName);
                }
                else L.LogWarning("[AIPROBE] WormAIProfile.MakeChoice not found");
            }
            catch (Exception e) { L.LogWarning("[AIPROBE] WormAIProfile hook: " + e.Message); }

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
            _setupCalls++;
            L.LogInfo("[AIPROBE] setup marker #" + _setupCalls);
            EnsureResolved(null, "SetupPlayer");
        }

        private static void OnSetInstance(int aiLevel)
        {
            try { L.LogInfo("[AIPROBE] SetInstance(aiLevel=" + aiLevel + " -> " + LevelName(aiLevel) + ")"); }
            catch { }
        }

        private static int _setupCalls;
        private static bool _runAiSeen;

        private static int _wormChoices;

        // 权重覆盖：属性只有 getter，所以走 getter 后置补丁（见 OnWeightGetter）
        private static Harmony _harmony;
        private static readonly Dictionary<string, double> _weightOverrides =
            new Dictionary<string, double>(StringComparer.Ordinal);
        private static readonly HashSet<string> _patchedGetters = new HashSet<string>(StringComparer.Ordinal);
        private static readonly HashSet<string> _getterHitLogged = new HashSet<string>(StringComparer.Ordinal);
        private static IntPtr _weightTarget = IntPtr.Zero;
        private static int _getterHitBudget = 24;

        /// WormAIProfile.MakeChoice 里能同时拿到"谁在被问"和"在选什么"，是目前唯一可靠的观察点。
        private static void OnWormMakeChoice(worm.canis.ai.WormAIProfile __instance,
            Networking.selection.messages.SelectionMessage selection)
        {
            _wormChoices++;

            // 诊断与教练配额无关：只要进过一次 AI 决策就把空间表导出来（只写一次）
            try
            {
                Canis.Match m0 = null;
                try { m0 = __instance.Match; } catch { }
                SpaceDiag.Dump(m0, L);
                // 卡牌 -> 效果能力：本地化里没有效果文本的卡，靠能力实体的中文文案还原
                CardDiag.Dump(m0, L);
            }
            catch { }

            if (!Coach.Enabled) return;
            try
            {
                PlayerEntity asker = null;
                try { asker = __instance.Player; } catch { }

                string pending = null;
                try { pending = selection != null ? selection.ToString() : null; } catch { }
                if (string.IsNullOrEmpty(pending)) return;

                // 实体实例 ID → 中文名；顺带砍掉执行栈快照（否则 targetMap 会被截断挤掉）
                try
                {
                    Canis.Match mt = null;
                    try { mt = __instance.Match; } catch { }
                    pending = Targets.Rewrite(mt, pending, L);

                    // ValidTargets 在原生 ToString 里只有数量，追加一份"可选目标"明细
                    string detail = Targets.Detail(selection, mt, L);
                    if (!string.IsNullOrEmpty(detail)) pending = pending + "\n" + detail;
                }
                catch (Exception e) { L.LogWarning("[TARGETS] " + e.Message); }

                string name = null;
                try { name = asker != null ? asker.PlayerName : null; } catch { }

                string stateBlock = null;
                try { stateBlock = State.Render(asker, L); }
                catch (Exception e) { L.LogInfo("[STATE] " + e.Message); }

                Coach.Observe(name, _wormChoices, stateBlock, pending);
            }
            catch (Exception e) { L.LogWarning("[COACH] worm: " + e.Message); }
        }

        private static void OnRunAIStart(Canis.hasExecutionStack.HasExecutionStack __instance)
        {
            _batchStart = Environment.TickCount64;
        }

        private static void OnRunAIResponses(Canis.hasExecutionStack.HasExecutionStack __instance)
        {
            if (!_runAiSeen)
            {
                _runAiSeen = true;
                L.LogInfo("[AIPROBE] first AI response batch (RunAIResponses fired)");
            }
            if (_batchCount < 20)
            {
                _batchCount++;
                L.LogInfo("[AIPROBE] AI batch #" + _batchCount + " took " + (Environment.TickCount64 - _batchStart) + " ms");
            }

            // 权重补丁只在游戏真的调用 getter 时才有意义；跑了几批还没命中就是被内联/直读了
            if (_batchCount == 6 && _weightOverrides.Count > 0 && _getterHitLogged.Count == 0)
            {
                L.LogWarning("[AIPROBE] weight getters never invoked after 6 batches"
                             + " -> patch ineffective, needs field-level write");
            }
            PlayerEntity p = null;
            try { p = __instance.LatestRespondedPlayer; } catch { }
            EnsureResolved(p, "RunAIResponses");


        }

        private static void OnProfileCtor(AIProfileAbs __instance)
        {
            PlayerEntity p = null;
            try { p = __instance.Player; } catch { }
            EnsureResolved(p, "AIProfileCtor");
        }

        private static int _aiLogLines;

        private static void OnAiLog(AIProfileAbs __instance, string str)
        {
            if (_aiLogLines < 40)
            {
                _aiLogLines++;
                try { L.LogInfo("[AIPROBE][ailog] " + (str ?? "")); } catch { }
            }
            PlayerEntity p = null;
            try { p = __instance.Player; } catch { }
            EnsureResolved(p, "AILog");
        }

        private static void OnMakeChoice(AIProfileAbs __instance)
        {
            PlayerEntity p = null;
            try { p = __instance.Player; } catch { }
            EnsureResolved(p, "MakeChoice");
        }

        private static void EnsureResolved(PlayerEntity player, string source)
        {
            if (_finished) return;
            _attempts++;

            int used = -1;
            try
            {
                if (player != null)
                {
                    used = player.AILevel;
                    if (!_loggedPlayerLevel)
                    {
                        _loggedPlayerLevel = true;
                        L.LogInfo("[AIPROBE] first AI touch via " + source + ": '" + Safe(() => player.PlayerName)
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
                if (_attempts <= 6 || _attempts % 200 == 0)
                    L.LogInfo("[AIPROBE] attempt " + _attempts + " (" + source + "): refs still null");
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
            WormAIConstantsRef usedRef = null;
            if (usedName != null) refs.TryGetValue(usedName, out usedRef);

            _weightOverrides.Clear();
            _weightTarget = IntPtr.Zero;
            try { if (usedRef != null) _weightTarget = usedRef.Pointer; } catch { }

            // 1) force_level：把指定档的整张表转成覆盖值，作用在本局实际使用的档上
            int force = ReadForceLevel();
            if (force >= 0 && force != usedLevel)
            {
                string srcName = LevelName(force);
                if (refs.TryGetValue(srcName, out var src) && src != null && usedRef != null)
                {
                    int n = 0;
                    foreach (var p in DoubleProps())
                    {
                        try { _weightOverrides[p.Name] = (double)p.GetValue(src); n++; } catch { }
                    }
                    L.LogWarning("[AIPROBE] force " + usedName + " := " + srcName + " (" + n + " props queued)");
                }
                else L.LogWarning("[AIPROBE] force level " + force + ": source or target ref missing");
            }

            // 2) ai_override.json：按档覆盖
            if (File.Exists(OverridePath))
            {
                var table = MiniJson.ParseObject(File.ReadAllText(OverridePath, Encoding.UTF8));
                var props = new Dictionary<string, PropertyInfo>(StringComparer.Ordinal);
                foreach (var p in DoubleProps()) props[p.Name] = p;

                foreach (var kv in table)
                {
                    if (kv.Key.StartsWith("_")) continue;   // 说明性字段
                    if (kv.Value == null) continue;
                    if (kv.Key != "all" && kv.Key != usedName)
                    {
                        L.LogInfo("[AIPROBE] override block '" + kv.Key + "' skipped (in use: " + usedName + ")");
                        continue;
                    }

                    foreach (var pv in kv.Value)
                    {
                        if (!props.TryGetValue(pv.Key, out var p)) { L.LogWarning("[AIPROBE] unknown prop " + pv.Key); continue; }
                        double old;
                        try { old = (double)p.GetValue(usedRef); }
                        catch (Exception e) { L.LogWarning("[AIPROBE] read " + pv.Key + ": " + e.Message); continue; }

                        // 支持三种写法：绝对 "3.5"、倍数 "*1.4"、增量 "+2" / "-0.5"
                        double v;
                        string raw = pv.Value;
                        if (raw.Length > 0 && (raw[0] == '*' || raw[0] == '+' || raw[0] == '-'))
                        {
                            if (!double.TryParse(raw.Substring(1), System.Globalization.NumberStyles.Float,
                                    System.Globalization.CultureInfo.InvariantCulture, out double k))
                            {
                                L.LogWarning("[AIPROBE] bad modifier " + pv.Key + "=" + raw);
                                continue;
                            }
                            v = raw[0] == '*' ? old * k : (raw[0] == '+' ? old + k : old - k);
                        }
                        else if (!double.TryParse(raw, System.Globalization.NumberStyles.Float,
                                     System.Globalization.CultureInfo.InvariantCulture, out v))
                        {
                            L.LogWarning("[AIPROBE] bad number " + pv.Key + "=" + raw);
                            continue;
                        }

                        _weightOverrides[pv.Key] = v;
                        if (Math.Abs(old - v) > 1e-9)
                            L.LogInfo("[AIPROBE] queue " + pv.Key + ": " + old + " -> " + v + "  (" + raw + ")");
                    }
                }
            }
            else L.LogInfo("[AIPROBE] no override file, skip");

            PatchWeightGetters();
        }

        private static void PatchWeightGetters()
        {
            if (_weightOverrides.Count == 0)
            {
                L.LogInfo("[AIPROBE] no weight overrides to apply");
                return;
            }
            if (_harmony == null) { L.LogWarning("[AIPROBE] harmony not ready, overrides dropped"); return; }

            var post = typeof(Plugin).GetMethod(nameof(OnWeightGetter),
                BindingFlags.Static | BindingFlags.NonPublic);
            int ok = 0, fail = 0, already = 0;

            foreach (var name in _weightOverrides.Keys.ToList())
            {
                if (_patchedGetters.Contains(name)) { already++; continue; }

                PropertyInfo prop = null;
                try
                {
                    prop = typeof(WormAIConstantsRef).GetProperty(name,
                        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                }
                catch (Exception e) { L.LogWarning("[AIPROBE] prop " + name + ": " + e.Message); }

                if (prop == null) { L.LogWarning("[AIPROBE] no property named " + name); fail++; continue; }
                var getter = prop.GetGetMethod(true);
                if (getter == null) { L.LogWarning("[AIPROBE] " + name + " has no getter"); fail++; continue; }
                if (getter.ReturnType != typeof(double))
                {
                    L.LogWarning("[AIPROBE] " + name + " returns " + getter.ReturnType.Name + ", only double supported");
                    fail++;
                    continue;
                }
                try
                {
                    _harmony.Patch(getter, postfix: new HarmonyMethod(post));
                    _patchedGetters.Add(name);
                    ok++;
                }
                catch (Exception e) { L.LogWarning("[AIPROBE] patch " + name + ": " + e.Message); fail++; }
            }

            L.LogWarning("[AIPROBE] weight getters: patched=" + ok + " failed=" + fail + " already=" + already
                         + " (target=" + (_weightTarget == IntPtr.Zero ? "all levels" : _weightTarget.ToString("X")) + ")");
        }

        /// <summary>
        /// 这些权重在 interop 里只有 getter（没有 setter，底层字段也没暴露），
        /// 所以唯一可行的改法是在 getter 上做后置补丁。getter 是虚方法，走 vtable，不会被内联。
        /// </summary>
        private static void OnWeightGetter(WormAIConstantsRef __instance, MethodBase __originalMethod, ref double __result)
        {
            if (_weightOverrides.Count == 0) return;
            if (_weightTarget != IntPtr.Zero)
            {
                IntPtr p = IntPtr.Zero;
                try { p = __instance.Pointer; } catch { }
                if (p != _weightTarget) return;
            }

            string name = __originalMethod != null ? __originalMethod.Name : null;
            if (string.IsNullOrEmpty(name)) return;
            if (name.StartsWith("get_", StringComparison.Ordinal)) name = name.Substring(4);

            double v;
            if (!_weightOverrides.TryGetValue(name, out v)) return;

            if (_getterHitBudget > 0 && _getterHitLogged.Add(name))
            {
                _getterHitBudget--;
                L.LogInfo("[AIPROBE] getter used: " + name + " " + __result + " -> " + v);
            }
            __result = v;
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

        // ---------- 超时研究与绕过 ----------

        private static void ReadTimeoutFactor()
        {
            try
            {
                if (!File.Exists(TimeoutPath))
                {
                    L.LogInfo("[AIPROBE] no extend_timeout.txt, timeout untouched (factor=1)");
                    return;
                }
                string raw = File.ReadAllText(TimeoutPath).Trim();
                if (double.TryParse(raw, System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out double f) && f >= 1.0 && f <= 1000.0)
                {
                    _timeoutFactor = f;
                    L.LogWarning("[AIPROBE] timeout factor = " + f);
                }
                else L.LogWarning("[AIPROBE] extend_timeout.txt 内容无效: '" + raw + "'（应为 1–1000 的倍数）");
            }
            catch (Exception e) { L.LogWarning("[AIPROBE] extend_timeout: " + e.Message); }
        }

        /// 枚举 AIAlgorithmUtil 的全部方法，并把 BestRankTimeout 打上补丁。
        /// 它是泛型静态方法：能否一次打中所有实例化，正是这次实验要回答的问题。
        private static void SurveyTimeoutApi(Harmony h)
        {
            try
            {
                var t = typeof(AIAlgorithmUtil);
                L.LogInfo("[AIPROBE] AIAlgorithmUtil methods:");
                foreach (var mi in t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static))
                {
                    if (mi.DeclaringType != t) continue;
                    var ps = string.Join(", ", mi.GetParameters().Select(x => x.ParameterType.Name + " " + x.Name));
                    L.LogInfo("[AIPROBE]   " + mi.Name + "<" + mi.GetGenericArguments().Length + "> genericDef="
                              + mi.IsGenericMethodDefinition + " (" + ps + ")");
                }

                int ok = 0, fail = 0;
                foreach (var mi in t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static))
                {
                    if (mi.DeclaringType != t || mi.Name != "BestRankTimeout") continue;
                    try
                    {
                        h.Patch(mi, prefix: new HarmonyMethod(typeof(Plugin).GetMethod(nameof(OnBestRankTimeout),
                            BindingFlags.Static | BindingFlags.NonPublic)));
                        ok++;
                        L.LogInfo("[AIPROBE]   patched " + mi.Name + " (genericDef=" + mi.IsGenericMethodDefinition + ")");
                    }
                    catch (Exception e)
                    {
                        fail++;
                        L.LogWarning("[AIPROBE]   patch failed: " + mi.Name + " -> " + e.GetType().Name + ": " + e.Message);
                    }
                }
                L.LogInfo("[AIPROBE] BestRankTimeout patch result: ok=" + ok + " fail=" + fail);
            }
            catch (Exception e) { L.LogWarning("[AIPROBE] survey: " + e.GetType().Name + ": " + e.Message); }
        }

        private static void OnBestRankTimeout(ref long timeout)
        {
            if (_timeoutCalls < 20)
            {
                _timeoutCalls++;
                L.LogInfo("[AIPROBE] BestRankTimeout called, timeout=" + timeout + ", factor=" + _timeoutFactor);
            }
            if (_timeoutFactor > 1.0 && timeout > 0)
            {
                double v = timeout * _timeoutFactor;
                timeout = v >= long.MaxValue / 2 ? long.MaxValue / 2 : (long)v;
            }
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

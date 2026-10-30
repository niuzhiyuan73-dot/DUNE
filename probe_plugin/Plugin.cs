using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using BepInEx;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;

namespace DuneProbe
{
    [BepInPlugin(GUID, "Dune Probe", "0.5.0")]
    public class Plugin : BasePlugin
    {
        public const string GUID = "local.duneprobe";

        internal static ManualLogSource Logger;
        private static readonly HashSet<string> Fired = new HashSet<string>();
        private static bool _injected;

        public override void Load()
        {
            Logger = Log;
            Logger.LogInfo("========== [PROBE v5] 诊断开始 ==========");

            var harmony = new Harmony("local.duneprobe.harmony");
            var postfix = new HarmonyMethod(typeof(Plugin).GetMethod(nameof(OnAnyPhase),
                BindingFlags.Static | BindingFlags.NonPublic));

            var targets = new (string TypeName, string Method)[]
            {
                ("worm.canis.actions.phases.SetupPhase", "CreateDecks"),
                ("worm.canis.actions.phases.SetupPhase", "CreateDecksExtra"),
                ("worm.canis.actions.phases.SetupPhase", "BeginSetup"),
                ("worm.canis.actions.phases.SetupPhase", "SetupPlayer"),
                ("worm.canis.actions.phases.DeckSetupPhase", "BeginDeckSetup"),
                ("worm.canis.actions.phases.DeckSetupPhase", "CompleteDeckSetup"),
                ("worm.canis.actions.phases.DeckSetupPhase", "Selection"),
            };

            foreach (var (tn, mn) in targets)
            {
                try
                {
                    var t = Type.GetType(tn + ", worm-canis");
                    if (t == null)
                    {
                        foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
                        {
                            t = a.GetType(tn);
                            if (t != null) break;
                        }
                    }
                    if (t == null) { Logger.LogInfo($"[PROBE] 类型未找到: {tn}"); continue; }

                    var mi = t.GetMethod(mn, BindingFlags.Public | BindingFlags.NonPublic |
                                             BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly);
                    if (mi == null) { Logger.LogInfo($"[PROBE] 方法未找到: {tn}.{mn}"); continue; }

                    harmony.Patch(mi, postfix: postfix);
                    Logger.LogInfo($"[PROBE] 已挂载: {t.Name}.{mn}");
                }
                catch (Exception e)
                {
                    Logger.LogWarning($"[PROBE] 挂载 {tn}.{mn} 失败: {e.GetType().Name}: {e.Message}");
                }
            }

            Logger.LogInfo("========== [PROBE v5] 就绪 ==========");
        }

        private static void OnAnyPhase(MethodBase __originalMethod, object __instance)
        {
            var name = __originalMethod.DeclaringType?.Name + "." + __originalMethod.Name;
            if (Fired.Contains(name)) return;
            Fired.Add(name);
            Logger.LogWarning($"[PROBE] >>> 阶段触发: {name}");

            try { DumpProperties(__instance); }
            catch (Exception e) { Logger.LogWarning("[PROBE] 属性枚举失败: " + e.GetType().Name); }

            if (name.EndsWith("CreateDecks") || name.EndsWith("CompleteDeckSetup"))
                TryInject(__instance);
        }

        private static void DumpProperties(object o)
        {
            if (o == null) { Logger.LogInfo("[PROBE]   (实例为 null)"); return; }
            var t = o.GetType();
            var props = t.GetProperties(BindingFlags.Public | BindingFlags.Instance);
            var sb = "";
            foreach (var p in props)
            {
                try
                {
                    var pt = p.PropertyType.Name;
                    sb += $" {p.Name}:{pt}";
                }
                catch { }
            }
            Logger.LogInfo($"[PROBE]   {t.Name} 属性:{sb}");
        }

        private static void TryInject(object phase)
        {
            if (_injected) return;
            try
            {
                object match = GetProp(phase, "wormMatch") ?? GetProp(phase, "WormMatch") ?? GetProp(phase, "match");
                if (match == null) { Logger.LogWarning("[PROBE] 未找到 match 引用"); return; }

                object players = GetProp(match, "PlayerTurnOrder");
                if (!(players is IEnumerable pe)) { Logger.LogWarning("[PROBE] PlayerTurnOrder 不可枚举"); return; }

                foreach (object p in pe)
                {
                    if (p == null) continue;
                    bool human;
                    try { human = Convert.ToBoolean(GetProp(p, "IsHuman") ?? false); } catch { human = false; }
                    Logger.LogInfo($"[PROBE]   玩家 {SafeStr(GetProp(p, "EntityName"))} IsHuman={human} 牌组={Count(GetProp(p, "Deck"))}");
                    if (!human) continue;

                    InjectInto(p);
                    _injected = true;
                    break;
                }
            }
            catch (Exception e)
            {
                Logger.LogWarning("[PROBE] 注入失败: " + e.GetType().Name + ": " + e.Message);
            }
        }

        private static void InjectInto(object player)
        {
            try
            {
                var deck = GetProp(player, "Deck");
                if (deck == null) { Logger.LogWarning("[PROBE] Deck 为 null"); return; }
                int before = Count(deck);

                var archType = FindType("worm.canis.archetypes.WormArchetypes");
                var archetypeType = FindType("canis.archetypes.Archetype");
                if (archType == null || archetypeType == null) { Logger.LogWarning("[PROBE] 未找到 Archetype 类型"); return; }

                object inst = archType.GetProperty("Instance").GetValue(null);
                object all = archType.GetMethod("AllArchetypes").Invoke(inst, null);

                object dagger = null;
                foreach (object a in (IEnumerable)all)
                {
                    if (a == null) continue;
                    if (a.GetType().Name == "Dagger") { dagger = a; break; }
                }
                if (dagger == null) { Logger.LogWarning("[PROBE] 未找到 Dagger 原型"); return; }
                Logger.LogInfo($"[PROBE] 原型: {dagger.GetType().FullName}");

                object match = GetProp(player, "WormMatch") ?? GetProp(player, "Match");
                Logger.LogInfo($"[PROBE] player.WormMatch = {(match == null ? "null" : match.GetType().Name)}");

                Logger.LogWarning($"[PROBE] 牌组数量 {before}，Deck 类型 {deck.GetType().Name}");
            }
            catch (Exception e)
            {
                Logger.LogWarning("[PROBE] InjectInto 异常: " + e.GetType().Name + ": " + e.Message);
            }
        }

        private static Type FindType(string full)
        {
            foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
            {
                var t = a.GetType(full);
                if (t != null) return t;
            }
            return null;
        }

        private static object GetProp(object o, string name)
        {
            if (o == null) return null;
            try
            {
                var p = o.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
                return p?.GetValue(o);
            }
            catch { return null; }
        }

        private static string SafeStr(object o)
        {
            if (o == null) return "null";
            try { return o.ToString(); } catch { return "<err>"; }
        }

        private static int Count(object coll)
        {
            if (coll == null) return -1;
            try
            {
                var c = coll.GetType().GetProperty("Count");
                return c != null ? Convert.ToInt32(c.GetValue(coll)) : -1;
            }
            catch { return -1; }
        }
    }
}

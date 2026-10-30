using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using BepInEx;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;
using canis.archetypes;
using worm.canis;
using worm.canis.archetypes;
using worm.canis.entities;
using worm.canis.actions.phases;

namespace DuneProbe
{
    [BepInPlugin(GUID, "Dune Probe", "1.3.0")]
    public class Plugin : BasePlugin
    {
        public const string GUID = "local.duneprobe";

        internal static ManualLogSource Logger;
        private static readonly List<WormPlayer> Players = new List<WormPlayer>();
        private static WormMatch _match;
        private static bool _done;

        public override void Load()
        {
            Logger = Log;
            Logger.LogInfo("========== [PROBE v13] 诊断 ==========");
            var h = new Harmony("local.duneprobe.harmony");
            Patch(h, "SetupPlayer", nameof(OnSetupPlayer));
            Patch(h, "CreateDecks", nameof(OnAfter));
            Logger.LogInfo("========== [PROBE v13] 就绪 ==========");
        }

        private static void Patch(Harmony h, string name, string handler)
        {
            try
            {
                var mi = typeof(SetupPhase).GetMethod(name, BindingFlags.Public | BindingFlags.NonPublic |
                                                             BindingFlags.Instance | BindingFlags.DeclaredOnly);
                if (mi == null) { Logger.LogWarning($"[PROBE] {name} 未找到"); return; }
                h.Patch(mi, postfix: new HarmonyMethod(typeof(Plugin).GetMethod(handler,
                    BindingFlags.Static | BindingFlags.NonPublic)));
                Logger.LogInfo($"[PROBE] 已挂载 {name}");
            }
            catch (Exception e) { Logger.LogWarning($"[PROBE] 挂载 {name} 失败: {e.GetType().Name}"); }
        }

        private static void OnSetupPlayer(SetupPhase __instance, object[] __args)
        {
            try
            {
                if (_match == null && __instance.wormMatch != null) _match = __instance.wormMatch;
                var p = __args != null && __args.Length > 0 ? __args[0] as WormPlayer : null;
                if (p == null) return;
                if (Players.Any(x => x.Pointer == p.Pointer)) return;
                Players.Add(p);
            }
            catch { }
        }

        private static void OnAfter(SetupPhase __instance)
        {
            if (_done) return;
            _done = true;
            try
            {
                if (_match == null && __instance.wormMatch != null) _match = __instance.wormMatch;
                Logger.LogInfo($"[PROBE] 玩家数: {Players.Count}, 对局: {_match != null}");

                DumpSigs();
                DumpArchs();
                DumpDeckInfo();
            }
            catch (Exception e) { Logger.LogWarning("[PROBE] 异常: " + e.GetType().Name + ": " + e.Message); }
        }

        private static void DumpSigs()
        {
            Logger.LogInfo("[PROBE] ---- ArchetypeIDUtils 签名 ----");
            foreach (var t in FindAllTypes("ArchetypeIDUtils"))
            {
                Logger.LogInfo("[PROBE]  类型: " + t.FullName);
                foreach (var m in t.GetMethods(BindingFlags.Public | BindingFlags.Static))
                {
                    if (m.DeclaringType != t) continue;
                    var ps = string.Join(", ", m.GetParameters().Select(x => x.ParameterType.Name + " " + x.Name));
                    Logger.LogInfo($"[PROBE]    {m.ReturnType.Name} {m.Name}({ps})");
                }
            }

            Logger.LogInfo("[PROBE] ---- Archetype 静态方法 ----");
            var at = typeof(Archetype);
            foreach (var m in at.GetMethods(BindingFlags.Public | BindingFlags.Static))
            {
                var ps = string.Join(", ", m.GetParameters().Select(x => x.ParameterType.Name + " " + x.Name));
                Logger.LogInfo($"[PROBE]    {m.ReturnType.Name} {m.Name}({ps})");
            }
        }

        private static void DumpArchs()
        {
            try
            {
                var inst = WormArchetypes.Instance;
                var pi = typeof(WormArchetypes).GetProperty("archs",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                object lazy = pi?.GetValue(inst);
                object arr = lazy?.GetType().GetProperty("Value")?.GetValue(lazy);
                if (arr == null) { Logger.LogWarning("[PROBE] archs 为 null"); return; }

                var lenProp = arr.GetType().GetProperty("Length");
                int len = Convert.ToInt32(lenProp.GetValue(arr));
                var item = arr.GetType().GetProperty("Item");
                Logger.LogInfo($"[PROBE] archs 长度 {len}，前 8 个：");
                for (int i = 0; i < Math.Min(8, len); i++)
                {
                    var a = item.GetValue(arr, new object[] { i });
                    if (a == null) { Logger.LogInfo($"[PROBE]   #{i} null"); continue; }
                    string name = "?", id = "?";
                    try { name = a.GetType().GetProperty("EntityName")?.GetValue(a)?.ToString(); } catch { }
                    try { id = a.GetType().GetProperty("ArchetypeID")?.GetValue(a)?.ToString(); } catch { }
                    Logger.LogInfo($"[PROBE]   #{i} {a.GetType().Name} | {name} | {id}");
                }
            }
            catch (Exception e) { Logger.LogWarning("[PROBE] archs 转储失败: " + e.GetType().Name + ": " + e.Message); }
        }

        private static void DumpDeckInfo()
        {
            try
            {
                if (Players.Count == 0) return;
                var deck = Players[0].Deck;
                if (deck == null) { Logger.LogWarning("[PROBE] Deck null"); return; }

                Logger.LogInfo($"[PROBE] Deck 类型: {deck.GetType().FullName}");
                foreach (var p in deck.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
                {
                    if (p.Name.IndexOf("Child", StringComparison.OrdinalIgnoreCase) < 0 &&
                        p.Name.IndexOf("Card", StringComparison.OrdinalIgnoreCase) < 0 &&
                        p.Name.IndexOf("Count", StringComparison.OrdinalIgnoreCase) < 0) continue;
                    object v = null;
                    try { v = p.GetValue(deck); } catch { }
                    Logger.LogInfo($"[PROBE]   Deck.{p.Name} : {p.PropertyType.Name} = {SafeLen(v)}");
                }
            }
            catch (Exception e) { Logger.LogWarning("[PROBE] Deck 转储失败: " + e.GetType().Name); }
        }

        private static string SafeLen(object v)
        {
            if (v == null) return "null";
            try
            {
                var len = v.GetType().GetProperty("Length");
                if (len != null) return "len=" + len.GetValue(v);
                var cnt = v.GetType().GetProperty("Count");
                if (cnt != null) return "count=" + cnt.GetValue(v);
                return v.ToString();
            }
            catch { return "<err>"; }
        }

        private static IEnumerable<Type> FindAllTypes(string simpleName)
        {
            foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type[] ts;
                try { ts = a.GetTypes(); } catch { continue; }
                foreach (var t in ts)
                    if (t.Name == simpleName) yield return t;
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
    }
}

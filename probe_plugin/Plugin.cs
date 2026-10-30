using System;
using System.Reflection;
using BepInEx;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;
using canis.archetypes;
using worm.canis.archetypes;
using worm.canis.actions.phases;

namespace DuneProbe
{
    [BepInPlugin(GUID, "Dune Probe", "0.3.0")]
    public class Plugin : BasePlugin
    {
        public const string GUID = "local.duneprobe";

        internal static ManualLogSource Logger;

        public override void Load()
        {
            Logger = Log;
            Logger.LogInfo("========== [PROBE v3] 开始 ==========");

            DumpType(typeof(WormArchetypes), "WormArchetypes");
            DumpType(typeof(Archetype), "Archetype");
            PatchDeckSetup();

            Logger.LogInfo("========== [PROBE v3] 完成 ==========");
        }

        private static void DumpType(Type t, string label)
        {
            Logger.LogInfo($"[PROBE] ---- {label} ({t.FullName}) ----");
            const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic |
                                       BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly;
            MemberInfo[] members;
            try { members = t.GetMembers(flags); }
            catch (Exception e)
            {
                Logger.LogWarning($"[PROBE] 取成员失败: {e.GetType().Name}: {e.Message}");
                return;
            }

            int shown = 0;
            foreach (var m in members)
            {
                try
                {
                    var s = m.ToString();
                    if (s == null) continue;
                    if (s.IndexOf("rchetype", StringComparison.OrdinalIgnoreCase) < 0 &&
                        s.IndexOf("Deck", StringComparison.OrdinalIgnoreCase) < 0 &&
                        s.IndexOf("Ability", StringComparison.OrdinalIgnoreCase) < 0) continue;
                    Logger.LogInfo($"[PROBE]   {m.MemberType}: {s}");
                    if (++shown >= 40) break;
                }
                catch { }
            }
        }

        private static void PatchDeckSetup()
        {
            Harmony harmony;
            HarmonyMethod postfix;
            try
            {
                harmony = new Harmony("local.duneprobe.harmony");
                postfix = new HarmonyMethod(typeof(Plugin).GetMethod(nameof(OnDeckSetup),
                    BindingFlags.Static | BindingFlags.NonPublic));
            }
            catch (Exception e)
            {
                Logger.LogWarning("[PROBE] Harmony 初始化失败: " + e.GetType().Name);
                return;
            }

            foreach (var name in new[] { "BeginDeckSetup", "CompleteDeckSetup" })
            {
                MethodInfo mi = null;
                try
                {
                    mi = typeof(DeckSetupPhase).GetMethod(name,
                        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly);
                }
                catch (Exception e) { Logger.LogWarning($"[PROBE] 查找 {name} 异常: {e.GetType().Name}"); }

                if (mi == null) { Logger.LogInfo($"[PROBE] {name} 未找到"); continue; }

                try
                {
                    harmony.Patch(mi, postfix: postfix);
                    Logger.LogInfo($"[PROBE] {name} 已挂 postfix");
                }
                catch (Exception e)
                {
                    Logger.LogWarning($"[PROBE] {name} 挂载失败: {e.GetType().Name}: {e.Message}");
                }
            }
        }

        private static void OnDeckSetup(DeckSetupPhase __instance)
        {
            try
            {
                object player = __instance.player;
                string pname = "<null>";
                if (player != null)
                {
                    var pi = player.GetType().GetProperty("EntityName");
                    pname = pi != null ? pi.GetValue(player)?.ToString() : player.GetType().Name;
                }
                Logger.LogWarning($"[PROBE] >>> DeckSetup 触发, player={pname}");
            }
            catch (Exception e)
            {
                Logger.LogWarning("[PROBE] DeckSetup 日志失败: " + e.GetType().Name + ": " + e.Message);
            }
        }
    }
}

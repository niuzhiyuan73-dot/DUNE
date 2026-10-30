using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using BepInEx;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;
using canis.archetypes;
using Canis.attributes;
using Canis.utils.ids;
using worm.canis;
using worm.canis.actions.phases;
using Entity = Canis.entities.Entity;

namespace DuneProbe
{
    [BepInPlugin(GUID, "Dune Probe", "3.1.0")]
    public class Plugin : BasePlugin
    {
        public const string GUID = "local.duneprobe";

        private const int A_ARCHID = 10000;
        private const int A_COST = 920006;

        internal static ManualLogSource Logger;
        private static WormMatch _match;
        private static bool _done;

        public override void Load()
        {
            Logger = Log;
            Logger.LogInfo("========== [PROBE v31] 造新卡流水线 ==========");
            var h = new Harmony("local.duneprobe.harmony");
            Patch(h, nameof(OnSetupPlayer), typeof(SetupPhase), "SetupPlayer");
            Patch(h, nameof(OnDraw), typeof(RoundPhase), "DrawCards");
            Logger.LogInfo("========== [PROBE v31] 就绪 ==========");
        }

        private static void Patch(Harmony h, string handler, Type t, string method)
        {
            var mi = t.GetMethod(method, BindingFlags.Public | BindingFlags.NonPublic |
                                        BindingFlags.Instance | BindingFlags.DeclaredOnly);
            if (mi == null) { Logger.LogWarning($"[PROBE] {method} 未找到"); return; }
            h.Patch(mi, postfix: new HarmonyMethod(typeof(Plugin).GetMethod(handler,
                BindingFlags.Static | BindingFlags.NonPublic)));
            Logger.LogInfo($"[PROBE] 挂载 {method}");
        }

        private static void OnSetupPlayer(SetupPhase __instance, object[] __args)
        {
            try { if (_match == null && __instance.wormMatch != null) _match = __instance.wormMatch; } catch { }
        }

        private static void OnDraw(object __instance)
        {
            if (_done) return;
            _done = true;
            try
            {
                var playmat = _match?.WormPlaymat;
                var player = Children(playmat).FirstOrDefault(x => x?.GetType().Name == "WormPlayer");
                if (player == null) { Logger.LogWarning("[PROBE] 无 Player"); return; }
                var deck = Children(player).FirstOrDefault(x => Name(x) == "Deck");
                var cards = Children(deck);
                Logger.LogWarning($"[PROBE] 牌组原有 {cards.Count} 张");
                if (cards.Count == 0) return;

                var template = ((Entity)cards[0]).GetArchetype();
                var playerEntity = (Canis.entities.PlayerEntity)player;
                var deckEntity = (Entity)deck;
                Logger.LogInfo($"[PROBE] 模板原型 = {template?.GetType().FullName}");

                // ---------- 策略一：克隆属性 → 新 Archetype → Build ----------
                Logger.LogWarning("[PROBE] ==== 策略一：clone + new Archetype + Build ====");
                TryStrategyA(template, playerEntity, deckEntity, deck);

                // ---------- 策略二：只给 ID，逐个 SetTo 复制 ----------
                if (!_success)
                {
                    Logger.LogWarning("[PROBE] ==== 策略二：new Archetype(id) + 逐个复制属性 ====");
                    TryStrategyB(template, playerEntity, deckEntity, deck);
                }

                // ---------- 策略三：直接 new Entity(模板) + MoveTo ----------
                if (!_success)
                {
                    Logger.LogWarning("[PROBE] ==== 策略三：new Entity(模板原型) + MoveTo ====");
                    TryStrategyC(template, playerEntity, deckEntity, deck);
                }

                Logger.LogWarning($"[PROBE] 最终牌组数量 = {Children(deck).Count}（原 {cards.Count}）");
            }
            catch (Exception e) { Logger.LogWarning("[PROBE] 异常: " + e.GetType().Name + ": " + e.Message); }
        }

        private static bool _success;

        // ================= 策略一 =================
        private static void TryStrategyA(Archetype template, Canis.entities.PlayerEntity player, Entity deck, object deckRaw)
        {
            try
            {
                if (template == null) { Logger.LogWarning("[PROBE]   模板为空"); return; }

                var srcAttrs = template.GetType().GetProperty("attributes")?.GetValue(template) as MutableAttributes;
                if (srcAttrs == null) { Logger.LogWarning("[PROBE]   取不到模板 attributes"); return; }

                MutableAttributes cloned;
                try { cloned = srcAttrs.Clone(); }
                catch (Exception e) { Logger.LogWarning("[PROBE]   Clone 失败: " + (e.InnerException?.Message ?? e.Message)); return; }
                Logger.LogInfo($"[PROBE]   克隆属性集成功，data 条目 = {AttrCount(cloned)}");

                // 换新 ID
                var newId = new ArchetypeID(Guid.NewGuid().ToString());
                try
                {
                    var defId = DefTyped<ArchetypeID>(A_ARCHID);
                    if (defId != null) AttributeDefinitionExtensions.SetTo<ArchetypeID>(defId, cloned, newId);
                }
                catch (Exception e) { Logger.LogInfo("[PROBE]   写新 ArchID 失败: " + (e.InnerException?.Message ?? e.Message)); }

                // 成本设成 1，便于识别
                try
                {
                    var defCost = DefTyped<int?>(A_COST);
                    if (defCost != null) AttributeDefinitionExtensions.SetTo<int?>(defCost, cloned, (int?)1);
                }
                catch (Exception e) { Logger.LogInfo("[PROBE]   写成本失败: " + (e.InnerException?.Message ?? e.Message)); }

                // MutableAttributes -> SerializableAttributes（隐式转换）
                SerializableAttributes ser = null;
                try
                {
                    var op = typeof(SerializableAttributes).GetMethods(BindingFlags.Public | BindingFlags.Static)
                        .FirstOrDefault(m => m.Name == "op_Implicit"
                                          && m.GetParameters().Length == 1
                                          && m.GetParameters()[0].ParameterType == typeof(MutableAttributes));
                    if (op == null) { Logger.LogWarning("[PROBE]   无 implicit(MutableAttributes)"); return; }
                    ser = (SerializableAttributes)op.Invoke(null, new object[] { cloned });
                    Logger.LogInfo($"[PROBE]   转 SerializableAttributes 成功: {(ser != null)}");
                }
                catch (Exception e) { Logger.LogWarning("[PROBE]   转换失败: " + (e.InnerException?.Message ?? e.Message)); return; }

                Archetype newArch;
                try { newArch = new Archetype(newId, ser); }
                catch (Exception e) { Logger.LogWarning("[PROBE]   new Archetype 失败: " + (e.InnerException?.Message ?? e.Message)); return; }
                Logger.LogInfo($"[PROBE]   新原型 = {(newArch == null ? "null" : newArch.GetType().FullName)}");

                var ent = TryBuild(newArch, player, deck);
                if (ent != null) _success = true;
            }
            catch (Exception e) { Logger.LogWarning("[PROBE]   策略一异常: " + e.GetType().Name + ": " + e.Message); }
        }

        // ================= 策略二 =================
        private static void TryStrategyB(Archetype template, Canis.entities.PlayerEntity player, Entity deck, object deckRaw)
        {
            try
            {
                var newId = new ArchetypeID(Guid.NewGuid().ToString());
                Archetype newArch;
                try { newArch = new Archetype(newId); }
                catch (Exception e) { Logger.LogWarning("[PROBE]   new Archetype(id) 失败: " + (e.InnerException?.Message ?? e.Message)); return; }
                if (newArch == null) { Logger.LogWarning("[PROBE]   新原型为 null"); return; }
                Logger.LogInfo("[PROBE]   new Archetype(id) 成功");

                var dst = newArch.GetType().GetProperty("attributes")?.GetValue(newArch) as MutableAttributes;
                var src = template?.GetType().GetProperty("attributes")?.GetValue(template) as MutableAttributes;
                Logger.LogInfo($"[PROBE]   目标属性容器 = {(dst == null ? "null" : "OK")}");

                if (dst != null && src != null)
                {
                    // 逐个复制已知类型的属性
                    CopyGeneric<ArchetypeID>(A_ARCHID, src, dst, newId);
                    CopyGeneric<string>(920003, src, dst, null);      // Image
                    CopyGeneric<string>(900018, src, dst, null);      // Kind
                    CopyGeneric<int?>(A_COST, src, dst, null);
                    CopyGeneric<string>(920009, src, dst, null);      // Category
                }

                var ent = TryBuild(newArch, player, deck);
                if (ent != null) _success = true;
            }
            catch (Exception e) { Logger.LogWarning("[PROBE]   策略二异常: " + e.GetType().Name + ": " + e.Message); }
        }

        // ================= 策略三 =================
        private static void TryStrategyC(Archetype template, Canis.entities.PlayerEntity player, Entity deck, object deckRaw)
        {
            try
            {
                Entity ent;
                try { ent = new Entity(_match, player, template, "ImperiumPlayable"); }
                catch (Exception e) { Logger.LogWarning("[PROBE]   new Entity 失败: " + (e.InnerException?.Message ?? e.Message)); return; }
                if (ent == null) { Logger.LogWarning("[PROBE]   实体为 null"); return; }
                Logger.LogInfo($"[PROBE]   新实体 = {ent.GetType().FullName} | 名称={Name(ent)}");

                Attach(ent, deck, player);
                _success = true;
            }
            catch (Exception e) { Logger.LogWarning("[PROBE]   策略三异常: " + e.GetType().Name + ": " + e.Message); }
        }

        // ---------- Build 与挂载 ----------
        private static Entity TryBuild(Archetype arch, Canis.entities.PlayerEntity player, Entity deck)
        {
            Entity ent = null;
            try
            {
                ent = arch.Build(_match, player, deck);
                Logger.LogWarning($"[PROBE]   Build 返回: {(ent == null ? "null" : ent.GetType().Name + " | " + Name(ent))}");
            }
            catch (Exception e) { Logger.LogWarning("[PROBE]   Build 失败: " + (e.InnerException?.Message ?? e.Message)); }

            if (ent == null)
            {
                try
                {
                    ent = new Entity(_match, player, arch, "ImperiumPlayable");
                    Logger.LogInfo("[PROBE]   改用 new Entity(arch) 构造成功");
                }
                catch (Exception e) { Logger.LogWarning("[PROBE]   new Entity(arch) 失败: " + (e.InnerException?.Message ?? e.Message)); }
            }

            if (ent == null) return null;
            Attach(ent, deck, player);
            return ent;
        }

        private static void Attach(Entity ent, Entity deck, object player)
        {
            try
            {
                var mv = typeof(Entity).GetMethods(BindingFlags.Public | BindingFlags.Instance)
                                       .FirstOrDefault(m => m.Name == "MoveTo");
                if (mv != null)
                {
                    var ps = mv.GetParameters();
                    object pos = null;
                    if (ps.Length == 2) pos = Activator.CreateInstance(ps[1].ParameterType);
                    mv.Invoke(ent, ps.Length == 2 ? new object[] { deck, pos } : new object[] { deck });
                    Logger.LogInfo("[PROBE]   MoveTo(deck) 已调用");
                }
            }
            catch (Exception e) { Logger.LogWarning("[PROBE]   MoveTo 失败: " + (e.InnerException?.Message ?? e.Message)); }

            try
            {
                var fp = typeof(Entity).GetMethod("ForceParent", BindingFlags.Public | BindingFlags.Instance);
                if (fp != null) { fp.Invoke(ent, new object[] { deck }); Logger.LogInfo("[PROBE]   ForceParent(deck) 已调用"); }
            }
            catch (Exception e) { Logger.LogWarning("[PROBE]   ForceParent 失败: " + (e.InnerException?.Message ?? e.Message)); }
        }

        // ---------- 属性工具 ----------
        private static int AttrCount(MutableAttributes a)
        {
            try
            {
                var d = a.GetType().GetProperty("data")?.GetValue(a);
                var c = d?.GetType().GetProperty("Count")?.GetValue(d);
                return c == null ? -1 : Convert.ToInt32(c);
            }
            catch { return -1; }
        }

        private static AttributeDefinition<T> DefTyped<T>(int key)
        {
            try
            {
                var m = typeof(AttributeDefinitions).GetMethods(BindingFlags.Public | BindingFlags.Static)
                    .First(x => x.Name == "GetDefinition" && x.IsGenericMethod
                             && x.GetParameters().Length == 1
                             && x.GetParameters()[0].ParameterType == typeof(int));
                return m.MakeGenericMethod(typeof(T)).Invoke(null, new object[] { key }) as AttributeDefinition<T>;
            }
            catch (Exception e)
            {
                Logger.LogInfo($"[PROBE]   GetDefinition<{typeof(T).Name}>({key}) 失败: {(e.InnerException?.Message ?? e.Message)}");
                return null;
            }
        }

        private static void CopyGeneric<T>(int key, MutableAttributes src, MutableAttributes dst, T overrideValue)
        {
            try
            {
                var def = DefTyped<T>(key);
                if (def == null) return;
                T v;
                if (overrideValue != null) v = overrideValue;
                else
                {
                    try
                    {
                        var attr = src.GetAttribute(def);
                        v = attr != null ? attr.Value : default;
                    }
                    catch { return; }
                }
                if (v == null) return;
                AttributeDefinitionExtensions.SetTo<T>(def, dst, v);
                Logger.LogInfo($"[PROBE]   复制 {key} = {v}");
            }
            catch (Exception e) { Logger.LogInfo($"[PROBE]   复制 {key} 失败: {(e.InnerException?.Message ?? e.Message)}"); }
        }

        // ---------- 通用工具 ----------
        private static string Name(object e)
        {
            if (e == null) return "null";
            try
            {
                var v = e.GetType().GetProperty("EntityName")?.GetValue(e);
                if (v == null) return "null";
                var np = v.GetType().GetProperty("Name");
                return np != null ? (np.GetValue(v)?.ToString() ?? "null") : v.ToString();
            }
            catch { return "?"; }
        }

        private static List<object> Children(object e)
        {
            var r = new List<object>();
            if (e == null) return r;
            try
            {
                var list = e.GetType().GetProperty("Children")?.GetValue(e);
                if (list == null) return r;
                var cnt = list.GetType().GetProperty("Count");
                int n = cnt != null ? Convert.ToInt32(cnt.GetValue(list)) : 0;
                var item = list.GetType().GetProperty("Item");
                if (item == null) return r;
                for (int i = 0; i < n; i++) { try { r.Add(item.GetValue(list, new object[] { i })); } catch { } }
            }
            catch { }
            return r;
        }
    }
}

using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json.Nodes;
using Canis;
using Canis.entities;
using Canis.utils.ids;
using Networking.selection.messages;
using worm.canis;
using worm.canis.entities;
using worm.canis.data.enums;

namespace DuneTrainingRecorder;

internal static class Capture
{
    private static readonly Dictionary<string, string> Names = new();
    private static readonly Dictionary<string, JsonObject> Rules = new();
    internal static void Init(string root)
    {
        var p = Path.Combine(root, "mods", "ai", "name_map.tsv");
        if (File.Exists(p)) foreach (var line in File.ReadLines(p))
        { int tab = line.IndexOf('\t'); if (tab > 0) Names[line[..tab]] = line[(tab + 1)..]; }
        foreach (var v in new[] { "effect_map_v3.tsv", "effect_map_v2.tsv" })
        {
            p = Path.Combine(root, "mods", "ai", v);
            if (!File.Exists(p)) continue;
            foreach (var line in File.ReadLines(p))
            {
                var c = line.Split('\t'); if (c.Length < 6) continue;
                Rules[c[0]] = new JsonObject { ["text"] = c[5], ["source"] = c[1],
                    ["priority"] = c[2], ["content_set"] = c[3], ["ambiguous_name"] = c[4],
                    ["verification"] = "reference_text_not_training_label" };
            }
            break;
        }
    }
    internal static string Name(string id) => id != null && Names.TryGetValue(id, out var name) ? name : id;
    internal static string LocalText(object v)
    {
        var raw = Native.Json(v);
        if (raw is JsonObject o && o["id"] != null) return Name(o["id"].ToString());
        return Name(Native.Str(v));
    }
    internal static JsonObject EntityInfo(Entity e)
    {
        if (e == null) return null;
        var r = new JsonObject { ["entity_id"] = Native.Str(e.EntityID), ["entity_name"] = e.EntityName,
            ["owner_id"] = Native.Str(e.OwningPlayerID), ["type"] = Native.TypeName(e) };
        string arch = null;
        try { arch = Native.Str(e.GetArchetype()?.ArchetypeID); } catch { }
        r["archetype_id"] = arch;
        r["name"] = Name(arch) ?? Name(e.EntityName);
        return r;
    }
    internal static JsonObject Resolve(Match m, string id, bool withRules = true)
    {
        Entity e = null;
        try { if (Guid.TryParse(id, out _)) m.TryGetEntity(new EntityID(id), out e); } catch { }
        var r = e != null ? EntityInfo(e) : new JsonObject { ["entity_id"] = id, ["name"] = Name(id) };
        if (!withRules) return r;
        var attrs = e != null ? Native.Attributes(e) : new JsonObject();
        // A primitive archetype ID (e.g. leader selection) may not be an instantiated entity.
        r["attributes"] = attrs;
        string arch = r["archetype_id"]?.ToString() ?? id;
        JsonObject rule = null;
        if (!Rules.TryGetValue(arch ?? "", out rule))
        {
            var nm = r["name"]?.ToString();
            if (nm != null && Rules.TryGetValue("name:" + nm, out var nr)
                && string.IsNullOrEmpty(nr["ambiguous_name"]?.ToString())) rule = nr;
        }
        r["rules"] = Native.Copy(rule);
        r["features"] = Features(attrs);
        return r;
    }
    // Typed numerical evidence only. Never interpret a generic Chinese "费用" as action cost.
    internal static JsonObject Features(JsonObject attrs)
    {
        var type = attrs["900018"]?.ToString();
        var purchase = new JsonObject(); var space = new JsonObject(); var activation = new JsonObject();
        void Put(JsonObject to, string label, string key)
        { if (attrs[key] != null) to[label] = new JsonObject { ["value"] = Native.Copy(attrs[key]), ["attribute_id"] = key }; }
        if (type == "Imperium" || type == "Tleilaxu" || type == "TechTile")
        { Put(purchase, "persuasion", "920006"); Put(purchase, "tech_spice", "960004"); }
        if (type == "Space")
        { Put(space, "spice", "900010"); Put(space, "water", "900011"); Put(space, "solari", "900012"); }
        if (type == "Intrigue")
        { Put(activation, "spice", "900010"); Put(activation, "water", "900011"); Put(activation, "solari", "900012"); }
        var printed = new JsonObject();
        foreach (var item in new[] { ("spice", "900003"), ("water", "900004"), ("solari", "900005"),
            ("troops", "900006"), ("persuasion", "900007"), ("strength", "900008") })
            Put(printed, item.Item1, item.Item2);
        return new JsonObject { ["card_type"] = type, ["purchase_cost"] = purchase,
            ["space_cost"] = space, ["activation_cost"] = activation,
            ["cost_status"] = "printed_attributes_only_final_payment_may_differ",
            ["printed_values"] = printed,
            ["immediate_result"] = null, ["immediate_result_status"] = "unknown_until_phase_and_conditions_resolved" };
    }

    internal static JsonObject Selection(Match m, SelectionMessage s)
    {
        var candidates = new JsonArray();
        var r = new JsonObject { ["native_counter"] = s.Counter, ["type"] = Native.TypeName(s),
            ["prompt"] = LocalText(s.Prompt), ["prompt_raw"] = Native.Json(s.Prompt),
            ["forced"] = s.isForced, ["candidates"] = candidates };
        var kind = s.TryCast<Networking.selection.messages.outgoing.IHasKind>();
        if (kind != null) r["kind"] = kind.Kind;
        var sw = s.TryCast<SelectionWithTargetsRequired>();
        if (sw != null)
        {
            r["source_id"] = Native.Str(sw.SourceID); r["target_type"] = sw.TargetType;
            r["selection_params"] = Native.Json(sw.SelectionParams);
            foreach (var kv in sw.TargetMap)
            {
                string id = kv.Key.ToString(); var c = Resolve(m, id);
                c["candidate_id"] = id; c["candidate_kind"] = "entity_option";
                var targets = new JsonArray();
                foreach (var ti in kv.Value)
                {
                    object obj = Native.Concrete(ti);
                    var g = new JsonObject { ["type"] = Native.TypeName(ti), ["raw"] = Native.Json(ti) };
                    foreach (var key in new[] { "NumberToSelect", "MinimumToSelect", "Forced", "TargetWeight", "Minimum", "Maximum", "Min", "Max" })
                    { var val = Native.Get(obj, key); if (val != null) g[key] = Native.Json(val); }
                    var valid = Native.Get(obj, "ValidTargets");
                    if (valid != null)
                    {
                        var list = new JsonArray();
                        foreach (var target in Native.Items(valid))
                        {
                            var key = Native.Get(target, "Key");
                            var id2 = Native.Str(key ?? target);
                            var d = Resolve(m, id2);
                            if (key != null) d["weight"] = Native.Json(Native.Get(target, "Value"));
                            list.Add(d);
                        }
                        g["valid_targets"] = list;
                    }
                    var choices = Native.Get(obj, "Choices");
                    if (choices != null)
                    {
                        var list = new JsonArray(); int index = 0;
                        foreach (var choice in Native.Items(choices)) list.Add(new JsonObject {
                            ["index"] = index++, ["raw"] = Native.Json(choice), ["name"] = LocalText(choice) });
                        g["choices"] = list;
                    }
                    targets.Add(g);
                }
                c["target_groups"] = targets;
                candidates.Add(c);
            }
            r["capture_status"] = "structured_target_map";
        }
        else
        {
            object actual = Native.Concrete(s);
            var buttons = Native.Get(actual, "Buttons") ?? Native.Get(actual, "Choices");
            if (buttons != null)
            {
                int index = 0;
                foreach (var b in Native.Items(buttons))
                { candidates.Add(new JsonObject { ["candidate_id"] = "choice:" + index, ["choice_index"] = index++,
                    ["candidate_kind"] = "custom_choice", ["name"] = LocalText(b), ["attributes"] = Native.Json(b) }); }
                r["capture_status"] = "structured_buttons";
            }
            else { r["capture_status"] = "unsupported_selection_type"; r["raw"] = Native.Json(s); }
        }
        r["root_candidate_count"] = candidates.Count;
        r["omitted_candidates"] = 0;
        return r;
    }

    internal static JsonObject Response(Il2CppSystem.Object input)
    {
        var r = new JsonObject { ["type"] = Native.TypeName(input), ["raw"] = Native.Json(input) };
        var selected = new JsonArray(); r["selected"] = selected;
        var sw = input.TryCast<Networking.selection.messages.outgoing.SelectionWithTargets>();
        if (sw != null && sw.selection != null)
        {
            var targets = new JsonArray();
            foreach (var t in sw.selection.targetResponses)
                targets.Add(new JsonObject { ["type"] = Native.TypeName(t), ["value"] = Native.Json(t) });
            selected.Add(new JsonObject { ["candidate_id"] = Native.Str(sw.selection.entityID), ["targets"] = targets });
            r["status"] = "parsed"; return r;
        }
        var multi = input.TryCast<Networking.selection.messages.outgoing.MultipleSelectionWithTargets>();
        if (multi != null)
        {
            if (multi.selection != null) foreach (var s in multi.selection)
                selected.Add(new JsonObject { ["candidate_id"] = Native.Str(s.entityID), ["entity_targets"] = Native.Array(s.targets) });
            r["status"] = "parsed"; return r;
        }
        var cc = input.TryCast<dwd.core.match.messages.outgoing.GameCustomChoice>();
        if (cc != null)
        { var index = cc.Selection.HasValue ? (int?)cc.Selection.Value : null;
            r["choice_index"] = index; if (index != null) selected.Add(new JsonObject { ["candidate_id"] = "choice:" + index });
            r["status"] = "parsed"; return r; }
        if (input.TryCast<Networking.selection.messages.outgoing.SelectionFinished>() != null)
        { r["status"] = "finished_or_passed"; return r; }
        r["status"] = "raw_only"; return r;
    }

    private static void Value(JsonObject o, string key, Func<int> read)
    { try { o[key] = read(); } catch { o[key] = null; } }
    private static JsonArray Cards(object collection)
    {
        var a = new JsonArray();
        foreach (var item in Native.Items(collection))
        {
            var e = item as Entity;
            if (e == null && item is Il2CppInterop.Runtime.InteropTypes.Il2CppObjectBase b) e = b.TryCast<Entity>();
            if (e != null) a.Add(EntityInfo(e));
        }
        return a;
    }
    internal static JsonObject Snapshot(Match m, PlayerEntity human)
    {
        var result = new JsonObject { ["perspective_player_id"] = Native.Str(human.AccountID),
            ["visibility"] = "human_hand_and_public_state_no_opponent_hands_or_deck_order",
            ["captured_at"] = DateTimeOffset.UtcNow.ToString("O") };
        var players = new JsonArray(); result["players"] = players;
        foreach (var p in m.players)
        {
            bool me = p.AccountID.ToString() == human.AccountID.ToString();
            var row = new JsonObject { ["player_id"] = p.AccountID.ToString(), ["name"] = p.PlayerName, ["is_ai"] = p.IsAI };
            players.Add(row);
            Value(row, "vp", () => WormEntityExtensions.VictoryPoints(p));
            Value(row, "solari", () => WormEntityExtensions.Solari(p));
            Value(row, "spice", () => WormEntityExtensions.Spice(p));
            Value(row, "water", () => WormEntityExtensions.Water(p));
            Value(row, "strength", () => WormEntityExtensions.Strength(p));
            var wp = p.TryCast<WormPlayer>(); if (wp == null) continue;
            row["leader"] = EntityInfo(wp.Leader);
            Value(row, "garrison_troops", () => wp.GarrisonTroops); Value(row, "conflict_troops", () => wp.ConflictTroops);
            Value(row, "garrison_dreadnoughts", () => wp.GarrisonDreadnoughts); Value(row, "conflict_dreadnoughts", () => wp.ConflictDreadnoughts);
            Value(row, "hand_count", () => wp.HandCount); Value(row, "deck_count", () => wp.DeckCount);
            Value(row, "intrigue_count", () => wp.IntrigueHandCount);
            Value(row, "remaining_agents", () => Native.Items(wp.RemainingAgents).Count);
            var influence = new JsonObject(); row["influence"] = influence;
            var alliances = new JsonObject(); row["alliances"] = alliances;
            foreach (var faction in new[] { Factions.Emperor, Factions.SpacingGuild, Factions.BeneGesserit, Factions.Fremen })
            { Value(influence, faction.ToString(), () => wp.GetFactionInfluence(faction));
                try { alliances[faction.ToString()] = wp.InAlliance(faction); } catch { alliances[faction.ToString()] = null; } }
            row["discard"] = Cards(wp.Discard.Children);
            row["play_area"] = Cards(wp.PlayArea.Children);
            row["tech_tiles"] = Cards(wp.TechTileArea.Children);
            if (me)
            { row["hand"] = Cards(wp.Hand.Children); row["intrigue_hand"] = Cards(wp.IntrigueHand.Children);
                row["supply"] = Cards(wp.Supply.Children); }
        }
        var wm = m.TryCast<WormMatch>();
        if (wm != null)
        {
            var board = wm.WormPlaymat.Board;
            result["current_conflict"] = board.CurrentConflict == null ? null : Resolve(m, board.CurrentConflict.EntityID.ToString());
            var spaces = new JsonArray(); result["spaces"] = spaces;
            foreach (var e in board.Descendents)
            {
                var space = e.TryCast<WormSpace>(); if (space == null) continue;
                var r = EntityInfo(space); r["agents"] = Cards(space.Agents);
                r["controller"] = Native.Str(space.ControllingPlayer?.AccountID);
                r["attributes"] = Native.Attributes(space); spaces.Add(r);
            }
            var publicAttrs = Native.Attributes(wm.WormPlaymat);
            var attrs = new JsonObject();
            foreach (var key in new[] { "900021", "100368", "950001", "900033", "900038" }) attrs[key] = Native.Copy(publicAttrs[key]);
            result["round_number"] = Native.Copy(publicAttrs["900021"]);
            result["round_phase"] = Native.Copy(publicAttrs["950001"]);
            result["mode_attributes"] = attrs;
            var market = new JsonObject(); result["public_zones"] = market;
            // Only public zones; do not archive future conflict or market-deck order.
            foreach (var e in wm.WormPlaymat.Descendents)
                if (e.EntityName == "ImperiumRow" || e.EntityName == "TechTileRow" || e.EntityName == "TleilaxuRow"
                    || e.EntityName == "Reserve" || e.EntityName == "ContractRow") market[e.EntityName] = Cards(e.Children);
        }
        result["game_cleaned_up"] = m.GameCleanedUp;
        return result;
    }
    internal static JsonObject Mode(Match m)
    {
        var mid = m.matchInitData;
        return new JsonObject { ["game_id"] = Native.Str(m.GameID), ["match_type"] = m.MatchType,
            ["scenario_name"] = mid?.scenarioName, ["game_type"] = mid?.gameType, ["difficulty"] = mid?.difficulty,
            ["game_options"] = Native.Json(m.GameOptions), ["init_options"] = Native.Json(mid?.options),
            ["scenario_description"] = mid?.description, ["random_seed"] = mid?.randomSeed,
            ["source"] = "live_match_configuration" };
    }
}

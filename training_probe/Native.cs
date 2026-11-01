using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Text.Json.Nodes;
using Il2CppInterop.Runtime.InteropTypes;
using Canis.entities;

namespace DuneTrainingRecorder;

internal static class Native
{
    private static readonly Dictionary<string, Type> Types = new();
    internal static string TypeName(object v)
    {
        try { return v is Il2CppObjectBase b ? b.TryCast<Il2CppSystem.Object>()?.GetIl2CppType().FullName ?? v.GetType().FullName : v?.GetType().FullName; }
        catch { return v?.GetType().FullName; }
    }
    internal static object Concrete(object v)
    {
        if (v is not Il2CppObjectBase b) return v;
        var name = TypeName(v);
        try
        {
            if (name == v.GetType().FullName) return v;
            if (!Types.TryGetValue(name, out var type))
            {
                foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
                { type = a.GetType(name); if (type != null) break; }
                if (type != null) Types[name] = type;
            }
            if (type != null && typeof(Il2CppObjectBase).IsAssignableFrom(type))
                return typeof(Il2CppObjectBase).GetMethod("TryCast").MakeGenericMethod(type).Invoke(b, null) ?? v;
        }
        catch { }
        return v;
    }
    internal static object Get(object obj, string name)
    {
        if (obj == null) return null;
        obj = Concrete(obj);
        try { return obj.GetType().GetProperty(name)?.GetValue(obj); } catch { return null; }
    }
    internal static string Str(object o) { try { return o?.ToString(); } catch { return null; } }
    internal static List<object> Items(object obj)
    {
        var result = new List<object>();
        if (obj == null) return result;
        obj = Concrete(obj);
        // IL2CPP generic collections are NOT System.Collections.IEnumerable.
        if (obj is IEnumerable managed)
        { foreach (var v in managed) result.Add(v); return result; }
        if (obj is Il2CppObjectBase b)
        {
            var native = b.TryCast<Il2CppSystem.Collections.IEnumerable>();
            if (native != null)
            {
                var iter = native.GetEnumerator();
                while (iter.MoveNext()) result.Add(Concrete(iter.Current));
                return result;
            }
        }
        var count = Get(obj, "Count") ?? Get(obj, "Length");
        var item = obj.GetType().GetProperty("Item");
        if (count != null && item != null)
            for (int i = 0; i < Convert.ToInt32(count); i++) result.Add(item.GetValue(obj, new object[] { i }));
        return result;
    }
    internal static JsonNode Json(object obj)
    {
        if (obj == null) return null;
        try
        {
            if (obj is Il2CppSystem.Object n)
                return JsonNode.Parse(Canis.json.JSON.ToJSON(n, new Canis.json.JSON.Options(false, "")));
            if (obj is string s) return JsonValue.Create(s);
            if (obj.GetType().IsPrimitive || obj is decimal) return System.Text.Json.JsonSerializer.SerializeToNode(obj);
        }
        catch (Exception e) { return new JsonObject { ["capture_error"] = e.GetType().Name, ["type"] = TypeName(obj) }; }
        return new JsonObject { ["type"] = TypeName(obj), ["unparsed"] = true };
    }
    internal static JsonNode Copy(JsonNode n) => n == null ? null : JsonNode.Parse(n.ToJsonString());
    internal static JsonArray Array(object o)
    { var a = new JsonArray(); foreach (var v in Items(o)) a.Add(Json(v)); return a; }
    internal static JsonObject Attributes(Entity e)
    {
        var result = new JsonObject();
        try
        {
            var raw = Json(e.SerializeAttributes());
            if (raw is JsonArray array)
                foreach (var v in array)
                { var k = v?["name"]?.ToString(); if (k != null) result[k] = Copy(v["value"]); }
        }
        catch (Exception ex) { result["capture_error"] = ex.Message; }
        return result;
    }
}

using System.Reflection;
using Bang;
using Murder;
using Murder.Core;
using Road.Services;

namespace NeverwayMod.DevTools.Core
{

/// <summary>
/// Runtime-discovered catalog of calendar events.
/// 
/// Calendar events are assets in Neverway.dll: <c>Road.Assets.CalendarEventAsset</c>.
/// We enumerate them from the game's asset database so the list stays in sync with
/// the current game build.
/// </summary>
internal static class CalendarEventCatalog
{
    private static (Guid guid, string name)[]? _cache;
    private static float _cacheTime;

    public static (Guid guid, string name)[] GetAllCalendarEventsCached(float refreshSeconds = 10f)
    {
        float now = Game.Now;
        if (_cache is not null && now - _cacheTime < refreshSeconds)
            return _cache;

        _cache = GetAllCalendarEventsUncached();
        _cacheTime = now;
        return _cache;
    }

    private static (Guid guid, string name)[] GetAllCalendarEventsUncached()
    {
        try
        {
            var t = ResolveCalendarEventAssetType();
            if (t is null)
                return [];

            var all = Game.Data.FilterAllAssets(t);
            var list = new List<(Guid guid, string name)>(all.Count);
            foreach (var kv in all)
            {
                string? name = kv.Value?.Name;
                if (string.IsNullOrWhiteSpace(name))
                    name = kv.Key.ToString()[..8];

                list.Add((kv.Key, name));
            }

            list.Sort(static (a, b) => string.Compare(a.name, b.name, StringComparison.OrdinalIgnoreCase));
            return list.ToArray();
        }
        catch
        {
            return [];
        }
    }

    private static Type? ResolveCalendarEventAssetType()
    {
        const string fqName = "Road.Assets.CalendarEventAsset";

        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            try
            {
                var t = asm.GetType(fqName, throwOnError: false, ignoreCase: false);
                if (t is not null)
                    return t;
            }
            catch
            {
                // ignore
            }
        }

        return null;
    }
}

/// <summary>
/// Runtime-discovered catalog of NPC profiles.
/// 
/// NPCs are assets in Neverway.dll: <c>Road.Assets.NpcProfileAsset</c>. We enumerate them from
/// the asset database so we can list birthdays on the calendar.
/// </summary>
internal static class NpcCatalog
{
    private static (Guid guid, string name)[]? _cache;
    private static float _cacheTime;

    public static (Guid guid, string name)[] GetAllNpcProfilesCached(float refreshSeconds = 30f)
    {
        float now = Game.Now;
        if (_cache is not null && now - _cacheTime < refreshSeconds)
            return _cache;

        _cache = GetAllNpcProfilesUncached();
        _cacheTime = now;
        return _cache;
    }

    private static (Guid guid, string name)[] GetAllNpcProfilesUncached()
    {
        try
        {
            var t = ResolveNpcProfileAssetType();
            if (t is null)
                return [];

            var all = Game.Data.FilterAllAssets(t);
            var list = new List<(Guid guid, string name)>(all.Count);
            foreach (var kv in all)
            {
                string? name = kv.Value?.Name;
                if (string.IsNullOrWhiteSpace(name))
                    name = kv.Key.ToString()[..8];
                list.Add((kv.Key, name));
            }

            list.Sort(static (a, b) => string.Compare(a.name, b.name, StringComparison.OrdinalIgnoreCase));
            return list.ToArray();
        }
        catch
        {
            return [];
        }
    }

    private static Type? ResolveNpcProfileAssetType()
    {
        const string fqName = "Road.Assets.NpcProfileAsset";

        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            try
            {
                var t = asm.GetType(fqName, throwOnError: false, ignoreCase: false);
                if (t is not null)
                    return t;
            }
            catch
            {
                // ignore
            }
        }

        return null;
    }
}

}

namespace DevTools.Core
{

/// <summary>
/// Reflection-based access to Neverway's RoadSaveData calendar + birthday state.
/// </summary>
internal static class CalendarSaveController
{
    private static bool _loaded;

    private static Type? _saveType;
    private static FieldInfo? _oneTimeEventsField;
    private static FieldInfo? _recurringEventsField;
    private static MethodInfo? _addCalendarEventWorldGuid;
    private static MethodInfo? _removeCalendarEventGuid;
    private static MethodInfo? _afterModifiedCalendar;
    private static MethodInfo? _checkCalendar;

    private static FieldInfo? _npcBirthdaysField;
    private static MethodInfo? _unlockBirthday;

    private static string _lastStatus = string.Empty;
    public static string LastStatus => _lastStatus;

    public static bool EnsureLoaded()
    {
        if (_loaded)
            return _saveType is not null;

        _loaded = true;

        try
        {
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                try { _saveType = asm.GetType("Road.Assets.RoadSaveData", throwOnError: false, ignoreCase: false); }
                catch { continue; }
                if (_saveType is null) continue;
                break;
            }

            if (_saveType is null)
            {
                _lastStatus = "RoadSaveData type not found.";
                return false;
            }

            var flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

            _oneTimeEventsField = _saveType.GetField("_oneTimeCalendarEvents", flags);
            _recurringEventsField = _saveType.GetField("_recurringCalendarEvents", flags);
            _npcBirthdaysField = _saveType.GetField("_npcBirthdays", flags);

            _removeCalendarEventGuid = _saveType.GetMethod("RemoveCalendarEvent", flags, [typeof(Guid)]);
            _afterModifiedCalendar = _saveType.GetMethod("AfterModifiedCalendar", flags, Type.EmptyTypes);
            _checkCalendar = _saveType.GetMethod("CheckCalendar", flags);

            // RoadSaveData.AddCalendarEvent(World, Guid)
            foreach (var m in _saveType.GetMethods(flags))
            {
                if (!string.Equals(m.Name, "AddCalendarEvent", StringComparison.Ordinal))
                    continue;

                var p = m.GetParameters();
                if (p.Length == 2 && p[0].ParameterType == typeof(World) && p[1].ParameterType == typeof(Guid))
                {
                    _addCalendarEventWorldGuid = m;
                    break;
                }
            }

            // RoadSaveData.UnlockBirthday(NpcId)
            foreach (var m in _saveType.GetMethods(flags))
            {
                if (!string.Equals(m.Name, "UnlockBirthday", StringComparison.Ordinal))
                    continue;

                var p = m.GetParameters();
                if (p.Length == 1)
                {
                    _unlockBirthday = m;
                    break;
                }
            }

            return true;
        }
        catch (Exception ex)
        {
            _lastStatus = $"CalendarSaveController init failed: {ex.Message}";
            return false;
        }
    }

    public static object? GetSaveOrNull()
    {
        try { return SaveServices.GetOrCreateSave(); }
        catch { return null; }
    }

    public static bool IsCalendarEventEnabled(object save, Guid eventGuid)
    {
        if (!EnsureLoaded())
            return false;

        try
        {
            if (_oneTimeEventsField?.GetValue(save) is System.Collections.IDictionary oneTime && oneTime.Contains(eventGuid))
                return true;
            if (_recurringEventsField?.GetValue(save) is System.Collections.IDictionary recurring && recurring.Contains(eventGuid))
                return true;
        }
        catch { }

        return false;
    }

    public static bool SetCalendarEventEnabled(MonoWorld world, object save, Guid eventGuid, bool enabled)
    {
        if (!EnsureLoaded())
            return false;

        try
        {
            bool changed;
            if (enabled)
            {
                if (_addCalendarEventWorldGuid is null)
                {
                    _lastStatus = "AddCalendarEvent(World, Guid) not found.";
                    return false;
                }

                changed = _addCalendarEventWorldGuid.Invoke(save, [world, eventGuid]) is bool b && b;
            }
            else
            {
                if (_removeCalendarEventGuid is null)
                {
                    _lastStatus = "RemoveCalendarEvent(Guid) not found.";
                    return false;
                }

                _removeCalendarEventGuid.Invoke(save, [eventGuid]);
                changed = true;
            }

            // Nudge any cached calendar info.
            try { _afterModifiedCalendar?.Invoke(save, null); } catch { }
            try { _checkCalendar?.Invoke(save, [world]); } catch { }

            _lastStatus = changed
                ? $"{(enabled ? "Enabled" : "Disabled")}: {eventGuid.ToString()[..8]}"
                : "No change.";

            return true;
        }
        catch (TargetInvocationException tie)
        {
            _lastStatus = tie.InnerException?.Message ?? tie.Message;
            return false;
        }
        catch (Exception ex)
        {
            _lastStatus = ex.Message;
            return false;
        }
    }

    public static bool IsNpcBirthdayUnlocked(object save, object npcIdValue)
    {
        if (!EnsureLoaded())
            return false;

        try
        {
            // _npcBirthdays: Dictionary<int, NpcId>
            if (_npcBirthdaysField?.GetValue(save) is System.Collections.IDictionary dict)
            {
                foreach (var v in dict.Values)
                {
                    if (v is not null && v.Equals(npcIdValue))
                        return true;
                }
            }
        }
        catch { }

        return false;
    }

    public static bool SetNpcBirthdayUnlocked(object save, object npcIdValue, bool unlocked)
    {
        if (!EnsureLoaded())
            return false;

        try
        {
            if (unlocked)
            {
                if (_unlockBirthday is null)
                {
                    _lastStatus = "UnlockBirthday(NpcId) not found.";
                    return false;
                }

                _unlockBirthday.Invoke(save, [npcIdValue]);
                _lastStatus = "Birthday unlocked.";
                return true;
            }

            // There's no public 'lock' method; we remove from the underlying dictionary.
            if (_npcBirthdaysField?.GetValue(save) is System.Collections.IDictionary dict)
            {
                var toRemove = new List<object>();
                foreach (var key in dict.Keys)
                {
                    try
                    {
                        var v = dict[key];
                        if (v is not null && v.Equals(npcIdValue))
                            toRemove.Add(key);
                    }
                    catch { }
                }

                foreach (var k in toRemove)
                    dict.Remove(k);

                _lastStatus = toRemove.Count > 0 ? "Birthday locked." : "No change.";
                return true;
            }

            _lastStatus = "_npcBirthdays not accessible.";
            return false;
        }
        catch (TargetInvocationException tie)
        {
            _lastStatus = tie.InnerException?.Message ?? tie.Message;
            return false;
        }
        catch (Exception ex)
        {
            _lastStatus = ex.Message;
            return false;
        }
    }
}

}

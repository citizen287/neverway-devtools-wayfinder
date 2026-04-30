using System.Reflection;
using Bang;
using ImGuiNET;
using Murder.Core;
using Murder.Core.Dialogs;
using NeverwayMod.DevTools.Core;
using Road.Services;

namespace DevTools.UI;

/// <summary>
/// Blackboard inspector/editor.
/// 
/// This enumerates blackboard types at runtime (via <see cref="IBlackboard"/> + <c>[Blackboard]</c>
/// attribute), then uses <see cref="Road.Services.SaveServices.GetOrCreateSave"/> and the underlying
/// <c>BlackboardTracker</c> to read/write variable values.
/// </summary>
public static class BlackboardPanel
{
    private sealed record BlackboardDef(string Name, Type Type, bool IsCharacter);

    private static bool _loaded;
    private static List<BlackboardDef> _defs = [];
    private static string _search = string.Empty;

    // Character-scoped blackboards (e.g. "Self") need a Guid. We let the user supply one.
    private static string _characterGuidText = string.Empty;
    private static Guid? _characterGuid;

    // Per-variable edit buffers.
    private static readonly Dictionary<string, int> _intEdits = new();
    private static readonly Dictionary<string, float> _floatEdits = new();
    private static readonly Dictionary<string, double> _doubleEdits = new();
    private static readonly Dictionary<string, string> _stringEdits = new();

    private static string _status = string.Empty;
    private static System.Numerics.Vector4 _statusColor = UIColors.Text;

    public static void Render()
    {
        EnsureLoaded();

        var world = GameHelper.GetWorld();
        if (world is not MonoWorld)
        {
            ImGui.TextColored(UIColors.Error, "No active MonoWorld.");
            return;
        }

        var save = SaveServices.GetOrCreateSave();
        if (save is null)
        {
            ImGui.TextColored(UIColors.Error, "No save loaded.");
            return;
        }

        var tracker = save.BlackboardTracker;
        if (tracker is null)
        {
            ImGui.TextColored(UIColors.Error, "Save has no BlackboardTracker.");
            return;
        }

        // Header
        ImGui.TextDisabled($"Blackboards found: {_defs.Count}");
        if (!string.IsNullOrWhiteSpace(_status))
        {
            ImGui.SameLine();
            ImGui.TextColored(_statusColor, _status);
        }

        ImGui.SameLine();
        if (ImGui.SmallButton("Refresh##bb"))
            Reload();

        ImGui.Separator();

        ImGui.SetNextItemWidth(-1);
        ImGui.InputTextWithHint("##bb_search", "Search blackboards/vars...", ref _search, 128);

        ImGui.Spacing();
        ImGui.TextDisabled("Character GUID (for character blackboards like 'Self'):");
        ImGui.SetNextItemWidth(260);
        if (ImGui.InputText("##bb_char_guid", ref _characterGuidText, 64))
        {
            if (Guid.TryParse(_characterGuidText, out var parsed))
                _characterGuid = parsed;
            else
                _characterGuid = null;
        }

        if (_characterGuid is null && _characterGuidText.Length > 0)
        {
            ImGui.SameLine();
            ImGui.TextColored(UIColors.Warning, "(invalid guid)");
        }

        ImGui.Separator();

        // Render all blackboards
        foreach (var def in _defs)
        {
            // Search filter
            if (!PassesSearch(def.Name))
            {
                // Still show blackboard if any of its fields match search (we'll check in RenderBlackboard)
                // so we can't early-continue here.
            }

            Guid? contextGuid = def.IsCharacter ? _characterGuid : null;
            bool canOpen = !def.IsCharacter || contextGuid is not null;

            // Grey-out character blackboards when no guid is provided.
            if (!canOpen)
                ImGui.BeginDisabled();

            string headerLabel = def.IsCharacter
                ? $"{def.Name}  (character)"
                : def.Name;

            bool open = ImGui.CollapsingHeader(headerLabel, ImGuiTreeNodeFlags.DefaultOpen);
            if (open)
                RenderBlackboard(tracker, def, contextGuid);

            if (!canOpen)
            {
                ImGui.EndDisabled();
                ImGui.TextColored(UIColors.Warning, "Provide a Character GUID above to inspect/edit this blackboard.");
                ImGui.Spacing();
            }
        }
    }

    private static void RenderBlackboard(object tracker, BlackboardDef def, Guid? blackboardGuid)
    {
        if (!TryGetBlackboardInstance(tracker, def.Name, blackboardGuid, out var instance, out var error))
        {
            ImGui.TextColored(UIColors.Error, error);
            return;
        }

        if (instance is null)
        {
            ImGui.TextColored(UIColors.Error, "Blackboard instance is null.");
            return;
        }

        var fields = def.Type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        if (fields.Length == 0)
        {
            ImGui.TextDisabled("(no fields)");
            return;
        }

        // Table
        if (!ImGui.BeginTable($"bb_tbl_{def.Name}_{blackboardGuid}", 5,
                ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.BordersInnerH | ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingStretchProp))
        {
            return;
        }

        ImGui.TableSetupColumn("Name", ImGuiTableColumnFlags.WidthStretch, 0.35f);
        ImGui.TableSetupColumn("Type", ImGuiTableColumnFlags.WidthFixed, 110);
        ImGui.TableSetupColumn("Current", ImGuiTableColumnFlags.WidthStretch, 0.25f);
        ImGui.TableSetupColumn("Edit", ImGuiTableColumnFlags.WidthStretch, 0.30f);
        ImGui.TableSetupColumn("Ops", ImGuiTableColumnFlags.WidthFixed, 150);
        ImGui.TableHeadersRow();

        foreach (var field in fields.OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase))
        {
            if (field.Name.Contains("k__BackingField"))
                continue;

            if (!PassesSearch(def.Name, field.Name))
                continue;

            object? current;
            try
            {
                current = field.GetValue(instance);
            }
            catch (Exception ex)
            {
                current = $"<err: {ex.Message}>";
            }

            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(field.Name);

            ImGui.TableNextColumn();
            ImGui.TextDisabled(PrettyTypeName(field.FieldType));

            ImGui.TableNextColumn();
            ImGui.TextUnformatted(current?.ToString() ?? "null");

            ImGui.TableNextColumn();
            RenderEditWidget(tracker, def.Name, blackboardGuid, field, current);

            ImGui.TableNextColumn();
            RenderOps(tracker, def.Name, blackboardGuid, field, current);
        }

        ImGui.EndTable();
        ImGui.Spacing();
    }

    private static void RenderEditWidget(object tracker, string blackboardName, Guid? blackboardGuid, FieldInfo field, object? current)
    {
        string key = MakeKey(blackboardName, blackboardGuid, field.Name);

        if (field.FieldType == typeof(bool))
        {
            bool v = current is bool b && b;
            if (ImGui.Checkbox($"##bb_edit_{key}", ref v))
                TryApply(tracker, blackboardName, blackboardGuid, field, v, ApplyMode.Set);
            return;
        }

        // For other types we keep a staged edit value.
        if (field.FieldType == typeof(int))
        {
            int v = GetOrInit(_intEdits, key, current is int i ? i : 0);
            ImGui.SetNextItemWidth(-1);
            if (ImGui.InputInt($"##bb_edit_{key}", ref v))
                _intEdits[key] = v;
            return;
        }

        if (field.FieldType == typeof(float))
        {
            float v = GetOrInit(_floatEdits, key, current is float f ? f : 0f);
            ImGui.SetNextItemWidth(-1);
            if (ImGui.InputFloat($"##bb_edit_{key}", ref v))
                _floatEdits[key] = v;
            return;
        }

        if (field.FieldType == typeof(double))
        {
            double v = GetOrInit(_doubleEdits, key, current is double d ? d : 0.0);
            ImGui.SetNextItemWidth(-1);
            if (ImGui.InputDouble($"##bb_edit_{key}", ref v))
                _doubleEdits[key] = v;
            return;
        }

        if (field.FieldType == typeof(string))
        {
            string v = GetOrInit(_stringEdits, key, current as string ?? string.Empty);
            ImGui.SetNextItemWidth(-1);
            if (ImGui.InputText($"##bb_edit_{key}", ref v, 256))
                _stringEdits[key] = v;
            return;
        }

        if (field.FieldType.IsEnum)
        {
            var names = Enum.GetNames(field.FieldType);
            var values = Enum.GetValues(field.FieldType);
            int idx = current is not null ? Array.IndexOf(values, current) : 0;
            if (idx < 0) idx = 0;

            ImGui.SetNextItemWidth(-1);
            if (ImGui.Combo($"##bb_edit_{key}", ref idx, names, names.Length))
            {
                object? selected = values.GetValue(idx);
                if (selected is not null)
                    TryApply(tracker, blackboardName, blackboardGuid, field, selected, ApplyMode.Set);
            }
            return;
        }

        ImGui.TextDisabled("(unsupported)");
    }

    private static void RenderOps(object tracker, string blackboardName, Guid? blackboardGuid, FieldInfo field, object? current)
    {
        string key = MakeKey(blackboardName, blackboardGuid, field.Name);

        // bool applies immediately from the checkbox.
        if (field.FieldType == typeof(bool) || field.FieldType.IsEnum)
        {
            ImGui.TextDisabled("-");
            return;
        }

        object? staged = GetStagedValueForField(field, key);
        if (staged is null)
        {
            ImGui.TextDisabled("-");
            return;
        }

        bool isNumeric = field.FieldType == typeof(int)
            || field.FieldType == typeof(float)
            || field.FieldType == typeof(double);

        if (ImGui.SmallButton($"Set##bb_set_{key}"))
            TryApply(tracker, blackboardName, blackboardGuid, field, staged, ApplyMode.Set);

        if (isNumeric)
        {
            ImGui.SameLine();
            if (ImGui.SmallButton($"Min##bb_min_{key}"))
                TryApply(tracker, blackboardName, blackboardGuid, field, staged, ApplyMode.Min);

            ImGui.SameLine();
            if (ImGui.SmallButton($"Max##bb_max_{key}"))
                TryApply(tracker, blackboardName, blackboardGuid, field, staged, ApplyMode.Max);
        }
    }

    private enum ApplyMode { Set, Min, Max }

    private enum PrimitiveKind
    {
        Unknown,
        Bool,
        Int,
        Float,
        Double,
        String
    }

    private static void TryApply(object tracker, string blackboardName, Guid? blackboardGuid, FieldInfo field, object staged, ApplyMode mode)
    {
        object valueToSet = staged;

        // If the runtime supports BlackboardActionKind.Min/Max, try to use it first.
        // This keeps behavior consistent with any special tracker logic.
        if (mode == ApplyMode.Min || mode == ApplyMode.Max)
        {
            if (InvokeMinMaxIfSupported(
                tracker,
                blackboardName,
                blackboardGuid,
                field,
                staged,
                isMin: mode == ApplyMode.Min,
                out var minMaxErr))
            {
                _status = $"{blackboardName}.{field.Name} updated";
                _statusColor = UIColors.Active;
                return;
            }
            else if (!string.IsNullOrWhiteSpace(minMaxErr))
            {
                // If we attempted Min/Max and it errored, surface that instead of silently falling back.
                _status = minMaxErr;
                _statusColor = UIColors.Error;
                return;
            }
        }

        if (mode != ApplyMode.Set)
        {
            // Implement Min/Max even if the underlying tracker doesn't support it as an action kind.
            object? current = null;
            if (TryGetBlackboardInstance(tracker, blackboardName, blackboardGuid, out var instance, out _))
                current = instance is null ? null : field.GetValue(instance);

            if (field.FieldType == typeof(int))
            {
                int cur = current is int ci ? ci : 0;
                int s = (int)staged;
                valueToSet = mode == ApplyMode.Min ? Math.Min(cur, s) : Math.Max(cur, s);
            }
            else if (field.FieldType == typeof(float))
            {
                float cur = current is float cf ? cf : 0f;
                float s = (float)staged;
                valueToSet = mode == ApplyMode.Min ? MathF.Min(cur, s) : MathF.Max(cur, s);
            }
            else if (field.FieldType == typeof(double))
            {
                double cur = current is double cd ? cd : 0.0;
                double s = (double)staged;
                valueToSet = mode == ApplyMode.Min ? Math.Min(cur, s) : Math.Max(cur, s);
            }
            else
            {
                // Not a numeric type; ignore Min/Max.
                valueToSet = staged;
            }
        }

        if (TrySetFieldValue(tracker, blackboardName, blackboardGuid, field, valueToSet, out var error))
        {
            _status = $"{blackboardName}.{field.Name} updated";
            _statusColor = UIColors.Active;
        }
        else
        {
            _status = error;
            _statusColor = UIColors.Error;
        }
    }

    private static object? GetStagedValueForField(FieldInfo field, string key)
    {
        if (field.FieldType == typeof(int) && _intEdits.TryGetValue(key, out var i)) return i;
        if (field.FieldType == typeof(float) && _floatEdits.TryGetValue(key, out var f)) return f;
        if (field.FieldType == typeof(double) && _doubleEdits.TryGetValue(key, out var d)) return d;
        if (field.FieldType == typeof(string) && _stringEdits.TryGetValue(key, out var s)) return s;
        return null;
    }

    private static bool TrySetFieldValue(object tracker, string blackboardName, Guid? blackboardGuid, FieldInfo field, object value, out string error)
    {
        error = string.Empty;

        try
        {
            if (field.FieldType == typeof(bool))
                return InvokeSet(tracker, "SetBool", new[] { typeof(string), typeof(string), typeof(BlackboardActionKind), typeof(bool) },
                    new object?[] { blackboardName, field.Name, BlackboardActionKind.Set, (bool)value }, blackboardGuid, out error);

            if (field.FieldType == typeof(int))
                return InvokeSet(tracker, "SetInt", new[] { typeof(string), typeof(string), typeof(BlackboardActionKind), typeof(int) },
                    new object?[] { blackboardName, field.Name, BlackboardActionKind.Set, (int)value }, blackboardGuid, out error);

            if (field.FieldType == typeof(string))
                return InvokeSet(tracker, "SetString", new[] { typeof(string), typeof(string), typeof(string) },
                    new object?[] { blackboardName, field.Name, (string)value }, blackboardGuid, out error);

            // Generic path.
            return InvokeSetValueGeneric(tracker, blackboardName, blackboardGuid, field, value, out error);
        }
        catch (Exception ex)
        {
            error = ex.InnerException?.Message ?? ex.Message;
            return false;
        }
    }

    private static bool InvokeMinMaxIfSupported(
        object tracker,
        string blackboardName,
        Guid? blackboardGuid,
        FieldInfo field,
        object value,
        bool isMin,
        out string? invokedError)
    {
        invokedError = null;

        // Some builds might support BlackboardActionKind.Min/Max and honor it in SetInt/SetBool/etc.
        // We don't have compile-time access to the enum values, so we try to resolve by name.
        if (!TryGetBlackboardActionKindByName(isMin ? "Min" : "Max", out var actionKindValue))
            return false;

        var kind = GetPrimitiveKind(field.FieldType);
        if (kind == PrimitiveKind.Unknown)
            return false;

        try
        {
            switch (kind)
            {
                case PrimitiveKind.Bool:
                    // Min/Max doesn't make sense for bool.
                    return false;

                case PrimitiveKind.Int:
                    return InvokeSet(tracker, "SetInt",
                        new[] { typeof(string), typeof(string), typeof(BlackboardActionKind), typeof(int) },
                        new object?[] { blackboardName, field.Name, actionKindValue, (int)value },
                        blackboardGuid, out invokedError);

                case PrimitiveKind.Float:
                case PrimitiveKind.Double:
                    // Usually floats/doubles go through SetValue<T>
                    // We try generic SetValue<T>(name, field, value, guid?) and if it supports Min/Max
                    // it'll likely have an overload with BlackboardActionKind. We'll probe for that.
                    return InvokeSetValueWithActionKindIfExists(tracker, blackboardName, blackboardGuid, field, value, actionKindValue, out invokedError);

                default:
                    return false;
            }
        }
        catch (Exception ex)
        {
            invokedError = ex.InnerException?.Message ?? ex.Message;
            return false;
        }
    }

    private static bool TryGetBlackboardActionKindByName(string name, out BlackboardActionKind kind)
    {
        kind = default;
        try
        {
            // Enum.TryParse exists even if the named value doesn't.
            if (Enum.TryParse(typeof(BlackboardActionKind), name, ignoreCase: true, out var boxed) && boxed is BlackboardActionKind parsed)
            {
                kind = parsed;
                return true;
            }
        }
        catch { }

        return false;
    }

    private static PrimitiveKind GetPrimitiveKind(Type t)
    {
        if (t == typeof(bool)) return PrimitiveKind.Bool;
        if (t == typeof(int)) return PrimitiveKind.Int;
        if (t == typeof(float)) return PrimitiveKind.Float;
        if (t == typeof(double)) return PrimitiveKind.Double;
        if (t == typeof(string)) return PrimitiveKind.String;
        return PrimitiveKind.Unknown;
    }

    private static bool InvokeSetValueWithActionKindIfExists(
        object tracker,
        string blackboardName,
        Guid? blackboardGuid,
        FieldInfo field,
        object value,
        BlackboardActionKind actionKind,
        out string error)
    {
        error = string.Empty;

        var trackerType = tracker.GetType();
        var methods = trackerType.GetMethods(BindingFlags.Instance | BindingFlags.Public)
            .Where(m => m.Name == "SetValue" && m.IsGenericMethodDefinition)
            .ToArray();

        foreach (var m in methods)
        {
            var p = m.GetParameters();

            // Look for: (string bb, string field, BlackboardActionKind kind, T value [, Guid?])
            if (p.Length is not (4 or 5))
                continue;

            if (p[0].ParameterType != typeof(string) || p[1].ParameterType != typeof(string) || p[2].ParameterType != typeof(BlackboardActionKind))
                continue;

            var closed = m.MakeGenericMethod(field.FieldType);
            var cp = closed.GetParameters();

            object?[] args;
            if (cp.Length == 4)
            {
                args = [blackboardName, field.Name, actionKind, value];
            }
            else
            {
                args = [blackboardName, field.Name, actionKind, value, CoerceGuidArg(cp[^1].ParameterType, blackboardGuid)];
            }

            closed.Invoke(tracker, args);
            return true;
        }

        return false;
    }

    private static bool InvokeSetValueGeneric(object tracker, string blackboardName, Guid? blackboardGuid, FieldInfo field, object value, out string error)
    {
        error = string.Empty;

        var trackerType = tracker.GetType();
        var methods = trackerType.GetMethods(BindingFlags.Instance | BindingFlags.Public)
            .Where(m => m.Name == "SetValue" && m.IsGenericMethodDefinition)
            .ToArray();

        if (methods.Length == 0)
        {
            error = "BlackboardTracker.SetValue<T>() not found.";
            return false;
        }

        // Prefer (string, string, T) or (string, string, T, Guid?)
        MethodInfo? chosen = null;
        foreach (var m in methods)
        {
            var p = m.GetParameters();
            if (p.Length is 3 or 4 && p[0].ParameterType == typeof(string) && p[1].ParameterType == typeof(string))
            {
                chosen = m;
                if (p.Length == 3) break;
            }
        }

        if (chosen is null)
        {
            error = "No compatible BlackboardTracker.SetValue<T> overload found.";
            return false;
        }

        var closed = chosen.MakeGenericMethod(field.FieldType);
        var parms = closed.GetParameters();

        object?[] args;
        if (parms.Length == 3)
        {
            args = [blackboardName, field.Name, value];
        }
        else
        {
            args = [blackboardName, field.Name, value, CoerceGuidArg(parms[^1].ParameterType, blackboardGuid)];
        }

        closed.Invoke(tracker, args);
        return true;
    }

    /// <summary>
    /// Invokes tracker.SetX(...) and tries to match optional Guid/Guid? overloads.
    /// </summary>
    private static bool InvokeSet(object tracker, string methodName, Type[] prefixParamTypes, object?[] prefixArgs, Guid? blackboardGuid, out string error)
    {
        error = string.Empty;
        var trackerType = tracker.GetType();

        var candidates = trackerType.GetMethods(BindingFlags.Instance | BindingFlags.Public)
            .Where(m => m.Name == methodName)
            .ToArray();

        foreach (var m in candidates)
        {
            var p = m.GetParameters();
            if (p.Length != prefixParamTypes.Length && p.Length != prefixParamTypes.Length + 1)
                continue;

            bool prefixMatches = true;
            for (int i = 0; i < prefixParamTypes.Length; i++)
            {
                if (p[i].ParameterType != prefixParamTypes[i])
                {
                    prefixMatches = false;
                    break;
                }
            }

            if (!prefixMatches)
                continue;

            object?[] args;
            if (p.Length == prefixParamTypes.Length)
            {
                args = prefixArgs;
            }
            else
            {
                args = new object?[prefixArgs.Length + 1];
                Array.Copy(prefixArgs, args, prefixArgs.Length);
                args[^1] = CoerceGuidArg(p[^1].ParameterType, blackboardGuid);
            }

            m.Invoke(tracker, args);
            return true;
        }

        error = $"{methodName} overload not found (expected {prefixParamTypes.Length} or {prefixParamTypes.Length + 1} params).";
        return false;
    }

    private static object? CoerceGuidArg(Type parameterType, Guid? guid)
    {
        if (parameterType == typeof(Guid?)) return guid;
        if (parameterType == typeof(Guid)) return guid ?? Guid.Empty;

        // Some versions might use Nullable<Guid> but not the exact Guid? alias.
        if (parameterType.IsGenericType && parameterType.GetGenericTypeDefinition() == typeof(Nullable<>))
        {
            var inner = Nullable.GetUnderlyingType(parameterType);
            if (inner == typeof(Guid)) return guid;
        }

        // Fallback.
        return guid;
    }

    private static bool TryGetBlackboardInstance(object tracker, string blackboardName, Guid? blackboardGuid, out object? instance, out string error)
    {
        instance = null;
        error = string.Empty;

        try
        {
            var trackerType = tracker.GetType();
            var find = trackerType.GetMethods(BindingFlags.Instance | BindingFlags.Public)
                .FirstOrDefault(m => m.Name == "FindBlackboard" && m.GetParameters().Length == 2);

            if (find is null)
            {
                error = "BlackboardTracker.FindBlackboard(name, guid) not found.";
                return false;
            }

            object? info = find.Invoke(tracker, [blackboardName, blackboardGuid]);
            if (info is null)
            {
                error = $"FindBlackboard returned null for '{blackboardName}'.";
                return false;
            }

            var infoType = info.GetType();
            var bbProp = infoType.GetProperty("Blackboard", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (bbProp is not null)
            {
                instance = bbProp.GetValue(info);
                return true;
            }

            var bbField = infoType.GetField("Blackboard", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (bbField is not null)
            {
                instance = bbField.GetValue(info);
                return true;
            }

            error = $"FindBlackboard return type '{infoType.Name}' has no Blackboard property/field.";
            return false;
        }
        catch (Exception ex)
        {
            error = ex.InnerException?.Message ?? ex.Message;
            return false;
        }
    }

    private static void EnsureLoaded()
    {
        if (_loaded) return;
        Reload();
    }

    private static void Reload()
    {
        _loaded = true;
        _defs = EnumerateBlackboards();
        _status = string.Empty;
    }

    private static List<BlackboardDef> EnumerateBlackboards()
    {
        var results = new List<BlackboardDef>();

        Type iBlackboard = typeof(IBlackboard);
        Type? iCharacter = Type.GetType("Murder.Core.Dialogs.ICharacterBlackboard, Murder", throwOnError: false);

        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            Type[] types;
            try { types = asm.GetTypes(); }
            catch { continue; }

            foreach (var t in types)
            {
                if (t.IsAbstract || t.IsInterface) continue;
                if (!iBlackboard.IsAssignableFrom(t)) continue;

                if (!TryGetBlackboardName(t, out string? name))
                    continue;

                if (string.IsNullOrWhiteSpace(name))
                    continue;

                bool isCharacter = iCharacter is not null && iCharacter.IsAssignableFrom(t);

                // Deduplicate by name (prefer first seen).
                if (results.Any(r => r.Name == name))
                    continue;

                results.Add(new BlackboardDef(name, t, isCharacter));
            }
        }

        results.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        return results;
    }

    private static bool TryGetBlackboardName(Type blackboardType, out string? name)
    {
        name = null;

        // Prefer attribute data so we don't need to know exact attribute property names.
        foreach (var cad in CustomAttributeData.GetCustomAttributes(blackboardType))
        {
            if (cad.AttributeType.Name is not ("BlackboardAttribute" or "Blackboard"))
                continue;

            if (cad.ConstructorArguments.Count > 0)
            {
                var arg0 = cad.ConstructorArguments[0].Value as string;
                if (!string.IsNullOrWhiteSpace(arg0))
                {
                    name = arg0;
                    return true;
                }
            }

            // Fallback: try named arg "Name".
            foreach (var na in cad.NamedArguments)
            {
                if (na.MemberName.Equals("Name", StringComparison.OrdinalIgnoreCase)
                    && na.TypedValue.Value is string s
                    && !string.IsNullOrWhiteSpace(s))
                {
                    name = s;
                    return true;
                }
            }
        }

        // Last resort: public const string Name.
        var constName = blackboardType.GetField("Name", BindingFlags.Public | BindingFlags.Static);
        if (constName is not null && constName.FieldType == typeof(string))
        {
            try
            {
                name = constName.GetRawConstantValue() as string;
                return !string.IsNullOrWhiteSpace(name);
            }
            catch { }
        }

        return false;
    }

    private static bool PassesSearch(string blackboardName, string? fieldName = null)
    {
        if (string.IsNullOrWhiteSpace(_search))
            return true;

        string haystack = fieldName is null
            ? blackboardName
            : $"{blackboardName}.{fieldName}";

        return haystack.Contains(_search, StringComparison.OrdinalIgnoreCase);
    }

    private static string MakeKey(string blackboardName, Guid? guid, string fieldName)
        => guid is null ? $"{blackboardName}:{fieldName}" : $"{blackboardName}:{guid}:{fieldName}";

    private static T GetOrInit<T>(Dictionary<string, T> dict, string key, T defaultValue)
    {
        if (!dict.TryGetValue(key, out var v))
        {
            dict[key] = defaultValue;
            return defaultValue;
        }

        return v;
    }

    private static string PrettyTypeName(Type t)
    {
        if (t == typeof(int)) return "int";
        if (t == typeof(float)) return "float";
        if (t == typeof(double)) return "double";
        if (t == typeof(bool)) return "bool";
        if (t == typeof(string)) return "string";
        if (t.IsEnum) return $"enum {t.Name}";
        return t.Name;
    }
}

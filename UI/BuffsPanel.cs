using System.Reflection;
using Bang;
using DevTools.Core;
using ImGuiNET;
using Murder.Core;
using NeverwayMod.DevTools.Core;
using Road.Services;

namespace DevTools.UI;

/// <summary>
/// Buff toggler panel.
/// 
/// We enumerate all <c>Road.Assets.BuffAsset</c> from the asset database, and then use
/// <c>RoadSaveData.HasBuff/AddBuff/RemoveBuff</c> (invoked by reflection) to toggle them.
/// </summary>
public static class BuffsPanel
{
    private static bool _loaded;
    private static List<(Guid Guid, string Name)> _all = [];
    private static List<(Guid Guid, string Name)> _filtered = [];

    private static string _search = string.Empty;
    private static int _selected;

    private static string _lastStatus = string.Empty;
    private static System.Numerics.Vector4 _lastStatusColor = UIColors.Text;

    private static MethodInfo? _hasBuffGuidMethod;
    private static MethodInfo? _addBuffGuidMethod;
    private static MethodInfo? _removeBuffGuidMethod;
    private static MethodInfo? _removeAllTimeSensitiveBuffsMethod;
    private static MethodInfo? _removeAllTimeSensitivePermanentBuffsMethod;

    public static void Render()
    {
        EnsureLoaded();

        var world = GameHelper.GetWorld();
        if (world is not MonoWorld monoWorld)
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

        ImGui.TextDisabled($"Loaded: {_all.Count} buffs");
        if (!string.IsNullOrWhiteSpace(_lastStatus))
        {
            ImGui.SameLine();
            ImGui.TextColored(_lastStatusColor, _lastStatus);
        }

        if (_hasBuffGuidMethod is null || _addBuffGuidMethod is null || _removeBuffGuidMethod is null)
        {
            ImGui.TextColored(UIColors.Warning, "Backend not found: RoadSaveData.HasBuff/AddBuff/RemoveBuff (Guid)");
        }
        else
        {
            ImGui.TextDisabled($"Backend: {_hasBuffGuidMethod.DeclaringType?.Name}.{_hasBuffGuidMethod.Name}() (reflection)");
        }

        ImGui.Separator();

        // Search
        ImGui.SetNextItemWidth(-1);
        if (ImGui.InputTextWithHint("##buff_search", "Search buffs...", ref _search, 128))
            Filter();

        // Bulk actions
        ImGui.Spacing();
        if (ImGui.Button("Enable ALL (filtered)"))
            TrySetAll(monoWorld, save, enabled: true);
        ImGui.SameLine();
        if (ImGui.Button("Disable ALL (filtered)"))
            TrySetAll(monoWorld, save, enabled: false);

        ImGui.SameLine();
        if (ImGui.Button("Clear temporary buffs"))
            TryClearTemporaryBuffs(monoWorld, save);
        ImGui.SameLine();
        if (ImGui.Button("Clear time-sensitive permanent buffs"))
            TryClearTimeSensitivePermanentBuffs(monoWorld, save);

        ImGui.Separator();

        // List
        var listHeight = ImGui.GetTextLineHeightWithSpacing() * 14;
        if (ImGui.BeginChild("BuffsList", new System.Numerics.Vector2(0, listHeight), ImGuiChildFlags.None))
        {
            for (int i = 0; i < _filtered.Count; i++)
            {
                bool isSelected = i == _selected;
                if (ImGui.Selectable(_filtered[i].Name, isSelected))
                    _selected = i;
            }
        }
        ImGui.EndChild();

        if (_filtered.Count > 0)
            _selected = Math.Clamp(_selected, 0, _filtered.Count - 1);
        else
            _selected = 0;

        // Selected actions
        ImGui.Spacing();
        bool hasSelection = _filtered.Count > 0;
        if (!hasSelection)
            ImGui.BeginDisabled();

        Guid selectedGuid = hasSelection ? _filtered[_selected].Guid : Guid.Empty;
        string selectedName = hasSelection ? _filtered[_selected].Name : "";
        bool isEnabled = hasSelection && HasBuff(save, selectedGuid);

        if (ImGui.Checkbox($"Enabled##buff_{selectedGuid}", ref isEnabled) && hasSelection)
        {
            TrySetSingle(monoWorld, save, selectedGuid, selectedName, isEnabled);
        }

        ImGui.SameLine();
        if (ImGui.Button("Copy GUID") && hasSelection)
        {
            try { ImGui.SetClipboardText(selectedGuid.ToString()); } catch { }
        }

        if (!hasSelection)
            ImGui.EndDisabled();

        ImGui.Spacing();
        ImGui.TextDisabled("Note: This modifies save buffs and applies them to the current player entity.");
    }

    private static void EnsureLoaded()
    {
        if (_loaded) return;
        _loaded = true;

        try
        {
            LoadBackendReflection();

            _all = LoadBuffsFromAssets();
            Filter();
        }
        catch (Exception ex)
        {
            DevToolsMod.LogError($"BuffsPanel init failed: {ex}");
            _all = [];
            _filtered = [];
        }
    }

    private static void LoadBackendReflection()
    {
        // Find Road.Assets.RoadSaveData methods. We use reflection to avoid hard dependencies.
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            Type? saveType;
            try { saveType = asm.GetType("Road.Assets.RoadSaveData", throwOnError: false, ignoreCase: false); }
            catch { continue; }
            if (saveType is null) continue;

            foreach (var m in saveType.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
            {
                if (string.Equals(m.Name, "HasBuff", StringComparison.Ordinal))
                {
                    var p = m.GetParameters();
                    if (p.Length == 1 && p[0].ParameterType == typeof(Guid) && m.ReturnType == typeof(bool))
                        _hasBuffGuidMethod = m;
                }
                else if (string.Equals(m.Name, "AddBuff", StringComparison.Ordinal))
                {
                    // RoadSaveData.AddBuff(World, Entity, Guid, bool)
                    var p = m.GetParameters();
                    if (p.Length == 4 && p[2].ParameterType == typeof(Guid) && p[3].ParameterType == typeof(bool))
                        _addBuffGuidMethod = m;
                }
                else if (string.Equals(m.Name, "RemoveBuff", StringComparison.Ordinal))
                {
                    // RoadSaveData.RemoveBuff(World, Entity, Guid, bool)
                    var p = m.GetParameters();
                    if (p.Length == 4 && p[2].ParameterType == typeof(Guid) && p[3].ParameterType == typeof(bool))
                        _removeBuffGuidMethod = m;
                }
                else if (string.Equals(m.Name, "RemoveAllTimeSensitiveBuffs", StringComparison.Ordinal))
                {
                    // RoadSaveData.RemoveAllTimeSensitiveBuffs(World, bool)
                    var p = m.GetParameters();
                    if (p.Length == 2 && p[0].ParameterType == typeof(World) && p[1].ParameterType == typeof(bool))
                        _removeAllTimeSensitiveBuffsMethod = m;
                }
                else if (string.Equals(m.Name, "RemoveAllTimeSensitivePermanentBuffs", StringComparison.Ordinal))
                {
                    // RoadSaveData.RemoveAllTimeSensitivePermanentBuffs(World)
                    var p = m.GetParameters();
                    if (p.Length == 1 && p[0].ParameterType == typeof(World))
                        _removeAllTimeSensitivePermanentBuffsMethod = m;
                }
            }

            break;
        }
    }

    private static List<(Guid Guid, string Name)> LoadBuffsFromAssets()
    {
        var all = BuffCatalog.GetAllBuffsCached(refreshSeconds: 10f);
        var list = new List<(Guid Guid, string Name)>(capacity: all.Length);
        foreach (var (guid, name) in all)
            list.Add((guid, name));
        return list;
    }

    private static void Filter()
    {
        _filtered = string.IsNullOrWhiteSpace(_search)
            ? new List<(Guid Guid, string Name)>(_all)
            : _all.Where(e => e.Name.Contains(_search, StringComparison.OrdinalIgnoreCase)).ToList();

        if (_filtered.Count > 0)
            _selected = Math.Clamp(_selected, 0, _filtered.Count - 1);
        else
            _selected = 0;
    }

    private static bool HasBuff(object save, Guid buffGuid)
    {
        try
        {
            if (_hasBuffGuidMethod is null)
                return false;

            return _hasBuffGuidMethod.Invoke(save, [buffGuid]) is bool b && b;
        }
        catch
        {
            return false;
        }
    }

    private static void TrySetSingle(MonoWorld world, object save, Guid buffGuid, string name, bool enabled)
    {
        try
        {
            if (_addBuffGuidMethod is null || _removeBuffGuidMethod is null)
            {
                SetStatus("Backend not found.", UIColors.Error);
                return;
            }

            var player = world.TryGetUniqueEntityPlayer();
            if (player is null)
            {
                SetStatus("No player entity found.", UIColors.Error);
                return;
            }

            if (enabled)
                _addBuffGuidMethod.Invoke(save, [world, player, buffGuid, /*silent*/ false]);
            else
                _removeBuffGuidMethod.Invoke(save, [world, player, buffGuid, /*silent*/ false]);

            SetStatus($"{(enabled ? "Enabled" : "Disabled")}: {name}", UIColors.Success);
        }
        catch (TargetInvocationException tie)
        {
            SetStatus($"Buff error: {tie.InnerException?.Message ?? tie.Message}", UIColors.Error);
            DevToolsMod.LogError($"BuffsPanel toggle invocation failed: {tie}");
        }
        catch (Exception ex)
        {
            SetStatus($"Buff error: {ex.Message}", UIColors.Error);
            DevToolsMod.LogError($"BuffsPanel toggle failed: {ex}");
        }
    }

    private static void TrySetAll(MonoWorld world, object save, bool enabled)
    {
        if (_filtered.Count == 0)
            return;

        var player = world.TryGetUniqueEntityPlayer();
        if (player is null)
        {
            SetStatus("No player entity found.", UIColors.Error);
            return;
        }

        int changed = 0;
        foreach (var b in _filtered)
        {
            bool already = HasBuff(save, b.Guid);
            if (enabled && already)
                continue;
            if (!enabled && !already)
                continue;

            try
            {
                if (enabled)
                    _addBuffGuidMethod?.Invoke(save, [world, player, b.Guid, /*silent*/ false]);
                else
                    _removeBuffGuidMethod?.Invoke(save, [world, player, b.Guid, /*silent*/ false]);

                changed++;
            }
            catch
            {
                // ignore individual failures; we'll report summary
            }
        }

        SetStatus($"{(enabled ? "Enabled" : "Disabled")} {changed} buffs", UIColors.Success);
    }

    private static void TryClearTemporaryBuffs(MonoWorld world, object save)
    {
        try
        {
            if (_removeAllTimeSensitiveBuffsMethod is null)
            {
                SetStatus("Clear method not found.", UIColors.Warning);
                return;
            }

            _removeAllTimeSensitiveBuffsMethod.Invoke(save, [world, /*silent*/ false]);
            SetStatus("Cleared temporary buffs", UIColors.Success);
        }
        catch (Exception ex)
        {
            SetStatus($"Clear error: {ex.Message}", UIColors.Error);
        }
    }

    private static void TryClearTimeSensitivePermanentBuffs(MonoWorld world, object save)
    {
        try
        {
            if (_removeAllTimeSensitivePermanentBuffsMethod is null)
            {
                SetStatus("Clear method not found.", UIColors.Warning);
                return;
            }

            _removeAllTimeSensitivePermanentBuffsMethod.Invoke(save, [world]);
            SetStatus("Cleared time-sensitive permanent buffs", UIColors.Success);
        }
        catch (Exception ex)
        {
            SetStatus($"Clear error: {ex.Message}", UIColors.Error);
        }
    }

    private static void SetStatus(string status, System.Numerics.Vector4 color)
    {
        _lastStatus = status;
        _lastStatusColor = color;
        ConsoleEngine.AddInfo($"Buffs: {status}");
    }
}

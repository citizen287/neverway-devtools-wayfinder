using System.Reflection;
using DevTools.Core;
using ImGuiNET;
using Murder.Core;
using NeverwayMod.DevTools.Core;
using Road.Services;

namespace DevTools.UI;

/// <summary>
/// Pet spawner/adopter panel.
///
/// Neverway exposes pet acquisition via save data: <c>Road.Assets.RoadSaveData.Adopt(PetKind, string)</c>.
/// We call it by reflection (to avoid compile-time coupling) and then optionally try to apply pets immediately.
/// </summary>
public static class PetsPanel
{
    private static bool _loaded;
    private static List<(object Value, string Name)> _all = [];
    private static List<(object Value, string Name)> _filtered = [];

    private static string _search = string.Empty;
    private static int _selected;

    private static string _petName = string.Empty;

    private static string _lastStatus = string.Empty;
    private static System.Numerics.Vector4 _lastStatusColor = UIColors.Text;

    private static MethodInfo? _adoptMethod;
    private static MethodInfo? _applyPetsMethod;

    public static void Render()
    {
        EnsureLoaded();

        var world = GameHelper.GetWorld();
        if (world is not MonoWorld monoWorld)
        {
            ImGui.TextColored(UIColors.Error, "No active MonoWorld.");
            return;
        }

        ImGui.TextDisabled($"Loaded: {_all.Count} pet kinds");
        if (!string.IsNullOrWhiteSpace(_lastStatus))
        {
            ImGui.SameLine();
            ImGui.TextColored(_lastStatusColor, _lastStatus);
        }

        if (_adoptMethod is null)
            ImGui.TextColored(UIColors.Warning, "Backend not found: RoadSaveData.Adopt(PetKind, string)");
        else
            ImGui.TextDisabled($"Backend: {_adoptMethod.DeclaringType?.Name}.{_adoptMethod.Name}() (reflection)");

        ImGui.Separator();

        ImGui.SetNextItemWidth(-1);
        if (ImGui.InputTextWithHint("##pet_search", "Search pets...", ref _search, 128))
            Filter();

        var listHeight = ImGui.GetTextLineHeightWithSpacing() * 12;
        if (ImGui.BeginChild("PetsList", new System.Numerics.Vector2(0, listHeight), ImGuiChildFlags.None))
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

        ImGui.Spacing();
        ImGui.SetNextItemWidth(-1);
        ImGui.InputTextWithHint("##pet_name", "Optional pet name...", ref _petName, 64);

        ImGui.Spacing();

        bool canAdopt = _adoptMethod is not null && _filtered.Count > 0;
        if (!canAdopt)
            ImGui.BeginDisabled();

        if (ImGui.Button("Spawn/Adopt"))
            TryAdoptAndMaybeApply(monoWorld, _filtered[_selected].Value, _petName);

        ImGui.SameLine();
        if (ImGui.Button("Apply pets now"))
            TryApplyPets(monoWorld);

        if (!canAdopt)
            ImGui.EndDisabled();

        ImGui.Spacing();
        ImGui.TextDisabled("Note: this modifies save data. If pets don’t appear immediately, sleep or click 'Apply pets now'.");
    }

    private static void EnsureLoaded()
    {
        if (_loaded) return;
        _loaded = true;

        try
        {
            LoadBackendReflection();

            _all = LoadPetsFromEnum();
            Filter();
        }
        catch (Exception ex)
        {
            DevToolsMod.LogError($"PetsPanel init failed: {ex}");
            _all = [];
            _filtered = [];
        }
    }

    private static void LoadBackendReflection()
    {
        // Find Road.Assets.RoadSaveData.Adopt(PetKind, string)
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            Type? saveType;
            try { saveType = asm.GetType("Road.Assets.RoadSaveData", throwOnError: false, ignoreCase: false); }
            catch { continue; }
            if (saveType is null) continue;

            foreach (var m in saveType.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
            {
                if (!string.Equals(m.Name, "Adopt", StringComparison.Ordinal))
                    continue;

                var p = m.GetParameters();
                if (p.Length == 2 && p[1].ParameterType == typeof(string) && p[0].ParameterType.IsEnum)
                {
                    _adoptMethod = m;
                    break;
                }
            }

            _applyPetsMethod = saveType.GetMethod("ApplyPetsOnEndOfDay", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

            if (_adoptMethod is not null)
                break;
        }
    }

    private static List<(object Value, string Name)> LoadPetsFromEnum()
    {
        var all = PetCatalog.GetAllPetKindsCached(refreshSeconds: 30f);
        var list = new List<(object Value, string Name)>(capacity: all.Length);
        foreach (var (value, name) in all)
            list.Add((value, name));
        return list;
    }

    private static void Filter()
    {
        _filtered = string.IsNullOrWhiteSpace(_search)
            ? new List<(object Value, string Name)>(_all)
            : _all.Where(e => e.Name.Contains(_search, StringComparison.OrdinalIgnoreCase)).ToList();

        if (_filtered.Count > 0)
            _selected = Math.Clamp(_selected, 0, _filtered.Count - 1);
        else
            _selected = 0;
    }

    private static void TryAdoptAndMaybeApply(MonoWorld world, object petKindValue, string petName)
    {
        try
        {
            var save = SaveServices.GetOrCreateSave();
            if (save is null)
            {
                SetStatus("No save loaded.", UIColors.Error);
                return;
            }

            if (_adoptMethod is null)
            {
                SetStatus("Adopt() not found.", UIColors.Error);
                return;
            }

            petName ??= string.Empty;

            _adoptMethod.Invoke(save, [petKindValue, petName]);
            SetStatus("Adopted", UIColors.Success);

            // Best-effort apply.
            TryApplyPets(world);
        }
        catch (TargetInvocationException tie)
        {
            SetStatus($"Adopt error: {tie.InnerException?.Message ?? tie.Message}", UIColors.Error);
            DevToolsMod.LogError($"PetsPanel Adopt invocation failed: {tie}");
        }
        catch (Exception ex)
        {
            SetStatus($"Adopt error: {ex.Message}", UIColors.Error);
            DevToolsMod.LogError($"PetsPanel Adopt failed: {ex}");
        }
    }

    private static void TryApplyPets(MonoWorld world)
    {
        try
        {
            var save = SaveServices.GetOrCreateSave();
            if (save is null)
                return;

            if (_applyPetsMethod is null)
            {
                // Not fatal; some builds may not expose this.
                SetStatus("Adopted (apply method not found)", UIColors.Warning);
                return;
            }

            _applyPetsMethod.Invoke(save, null);
            SetStatus("Pets applied", UIColors.Success);
        }
        catch (Exception ex)
        {
            DevToolsMod.LogWarning($"ApplyPetsOnEndOfDay failed: {ex.Message}");
        }
    }

    private static void SetStatus(string status, System.Numerics.Vector4 color)
    {
        _lastStatus = status;
        _lastStatusColor = color;
        ConsoleEngine.AddInfo($"Pets: {status}");
    }
}

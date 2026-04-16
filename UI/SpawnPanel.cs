using System.Reflection;
using System.Text.Json;
using Bang;
using Bang.Components;
using DevTools.Core;
using ImGuiNET;
using Murder.Core;
using Murder.Services;
using NeverwayMod.DevTools.Core;

namespace DevTools.UI;

/// <summary>
/// ImGui-based spawner panel, adapted from SpawnModGui.
///
/// It loads <c>entities.json</c> from this assembly's embedded resources and
/// allows spawning an entry near the player.
/// </summary>
public static class SpawnPanel
{
    private static bool _loaded;
    private static List<(string Name, Guid Guid)> _all = [];
    private static List<(string Name, Guid Guid)> _filtered = [];

    private static string _search = string.Empty;
    private static int _selected;

    private static string _lastSpawnStatus = string.Empty;
    private static System.Numerics.Vector4 _lastSpawnStatusColor = UIColors.Text;

    public static void Render()
    {
        EnsureLoaded();

        var world = GameHelper.GetWorld();
        if (world is not MonoWorld monoWorld)
        {
            ImGui.TextColored(UIColors.Error, "No active MonoWorld.");
            return;
        }

        ImGui.TextDisabled($"Loaded: {_all.Count} entries");

        if (!string.IsNullOrWhiteSpace(_lastSpawnStatus))
        {
            ImGui.SameLine();
            ImGui.TextColored(_lastSpawnStatusColor, _lastSpawnStatus);
        }

        ImGui.SetNextItemWidth(-1);
        if (ImGui.InputTextWithHint("##spawn_search", "Search...", ref _search, 128))
            Filter();

        ImGui.Separator();

        // List
        var listHeight = ImGui.GetTextLineHeightWithSpacing() * 16;
        if (ImGui.BeginChild("SpawnList", new System.Numerics.Vector2(0, listHeight), ImGuiChildFlags.None))
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

        // Actions
        ImGui.Spacing();
        bool canSpawn = _filtered.Count > 0;
        if (!canSpawn)
            ImGui.BeginDisabled();

        if (ImGui.Button("Spawn##spawn_btn"))
            TrySpawn(monoWorld, _filtered[_selected].Guid);

        ImGui.SameLine();
        if (ImGui.Button("Copy GUID"))
        {
            try
            {
                ImGui.SetClipboardText(_filtered[_selected].Guid.ToString());
            }
            catch { }
        }

        if (!canSpawn)
            ImGui.EndDisabled();
    }

    private static void EnsureLoaded()
    {
        if (_loaded) return;
        _loaded = true;

        try
        {
            _all = LoadEntitiesFromEmbeddedJson();
            Filter();
        }
        catch (Exception ex)
        {
            DevToolsMod.LogError($"SpawnPanel failed to load entities.json: {ex.Message}");
            _all = [];
            _filtered = [];
        }
    }

    private static List<(string Name, Guid Guid)> LoadEntitiesFromEmbeddedJson()
    {
        var asm = Assembly.GetExecutingAssembly();
        var resName = asm.GetManifestResourceNames().FirstOrDefault(n => n.EndsWith("entities.json", StringComparison.OrdinalIgnoreCase));
        if (resName is null)
            throw new InvalidOperationException("entities.json not found as an embedded resource.");

        using var stream = asm.GetManifestResourceStream(resName);
        if (stream is null)
            throw new InvalidOperationException($"Failed to open embedded resource '{resName}'.");

        using var doc = JsonDocument.Parse(stream);

        var list = new List<(string Name, Guid Guid)>(capacity: 4096);
        foreach (var prop in doc.RootElement.EnumerateObject())
        {
            if (!Guid.TryParse(prop.Name, out var guid))
                continue;

            // Schema: { "<guid>": [ { "text": "Some/Name" } ] }
            var first = prop.Value.ValueKind == JsonValueKind.Array
                ? prop.Value.EnumerateArray().FirstOrDefault()
                : default;

            if (first.ValueKind != JsonValueKind.Object)
                continue;

            if (!first.TryGetProperty("text", out var textElem))
                continue;

            var name = textElem.GetString();
            if (string.IsNullOrWhiteSpace(name))
                continue;

            list.Add((name!, guid));
        }

        list.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        return list;
    }

    private static void Filter()
    {
        _filtered = string.IsNullOrWhiteSpace(_search)
            ? new List<(string Name, Guid Guid)>(_all)
            : _all.Where(e => e.Name.Contains(_search, StringComparison.OrdinalIgnoreCase)).ToList();

        if (_filtered.Count > 0)
            _selected = Math.Clamp(_selected, 0, _filtered.Count - 1);
        else
            _selected = 0;
    }

    private static void TrySpawn(MonoWorld world, Guid guid)
    {
        var player = world.TryGetUniqueEntityPlayer();
        if (player == null)
            return;

        var pos = player.GetPosition();

        var spawnPos = new System.Numerics.Vector2(pos.X, pos.Y);

        try
        {
            // Murder's helper tries to find a collision-free position by querying the entity's collider.
            // Some prefabs don't have a ColliderComponent, which makes GetCollider throw.
            EntityServices.Spawn(world, spawnPos, guid, 1, 0f);

            _lastSpawnStatus = "Spawned";
            _lastSpawnStatusColor = UIColors.Success;
        }
        catch (KeyNotFoundException)
        {
            // Fallback path: create the prefab directly and place it at the target location.
            // This skips the collision-resolution step that requires a collider.
            if (TrySpawnWithoutCollider(world, spawnPos, guid))
            {
                _lastSpawnStatus = "Spawned (no collider)";
                _lastSpawnStatusColor = UIColors.Warning;
            }
            else
            {
                _lastSpawnStatus = "Spawn failed";
                _lastSpawnStatusColor = UIColors.Error;
            }
        }
        catch (Exception ex)
        {
            DevToolsMod.LogError($"Spawn failed: {ex}");
            ConsoleEngine.AddInfo($"Spawn error: {ex.Message}");
            _lastSpawnStatus = "Spawn error";
            _lastSpawnStatusColor = UIColors.Error;
        }
    }

    private static bool TrySpawnWithoutCollider(MonoWorld world, System.Numerics.Vector2 spawnPos, Guid guid)
    {
        try
        {
            // AssetServices is in Murder.dll but (surprisingly) in the global namespace.
            var e = AssetServices.TryCreate(world, guid);
            if (e is null)
            {
                DevToolsMod.LogWarning($"AssetServices.TryCreate returned null for {guid}");
                return false;
            }

            // Ensure it has a position at the requested location.
            e.AddOrReplaceComponent(new PositionComponent(spawnPos), typeof(PositionComponent));

            DevToolsMod.LogWarning($"Spawned {guid} using fallback path (no ColliderComponent)");
            return true;
        }
        catch (Exception ex)
        {
            DevToolsMod.LogError($"Fallback spawn failed for {guid}: {ex}");
            return false;
        }
    }
}

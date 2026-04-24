using Bang;
using Bang.Components;
using DevTools.Core;
using ImGuiNET;
using Murder;
using Murder.Assets.Graphics;
using Murder.Components;
using Murder.Core;
using NeverwayMod.DevTools.Core;

namespace DevTools.UI;

/// <summary>
/// Sprite spawner panel.
/// 
/// Enumerates all <see cref="SpriteAsset"/> at runtime and allows spawning a simple entity
/// that renders the sprite and (by default) cycles through all animations.
/// </summary>
public static class SpritePanel
{
    private static bool _loaded;
    private static List<(string Name, Guid Guid)> _all = [];
    private static List<(string Name, Guid Guid)> _filtered = [];

    private static string _search = string.Empty;
    private static int _selected;

    private static bool _spawnAtPlayer = true;
    private static float _yOffset;

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

        ImGui.TextDisabled($"Loaded: {_all.Count} sprites");
        if (!string.IsNullOrWhiteSpace(_lastSpawnStatus))
        {
            ImGui.SameLine();
            ImGui.TextColored(_lastSpawnStatusColor, _lastSpawnStatus);
        }

        ImGui.SetNextItemWidth(-1);
        if (ImGui.InputTextWithHint("##sprite_search", "Search sprites...", ref _search, 128))
            Filter();

        ImGui.Separator();

        // List
        var listHeight = ImGui.GetTextLineHeightWithSpacing() * 16;
        if (ImGui.BeginChild("SpriteList", new System.Numerics.Vector2(0, listHeight), ImGuiChildFlags.None))
        {
            for (int i = 0; i < _filtered.Count; i++)
            {
                bool isSelected = i == _selected;
                if (ImGui.Selectable(_filtered[i].Name, isSelected))
                    _selected = i;

                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip(_filtered[i].Guid.ToString());
            }
        }
        ImGui.EndChild();

        if (_filtered.Count > 0)
            _selected = Math.Clamp(_selected, 0, _filtered.Count - 1);
        else
            _selected = 0;

        // Options
        ImGui.Spacing();
        ImGui.Checkbox("Spawn at player", ref _spawnAtPlayer);
        ImGui.SetNextItemWidth(180);
        ImGui.DragFloat("Y offset", ref _yOffset, 1f, -256f, 256f);

        // Actions
        ImGui.Spacing();
        bool canSpawn = _filtered.Count > 0;
        if (!canSpawn)
            ImGui.BeginDisabled();

        if (ImGui.Button("Spawn##sprite_spawn"))
            TrySpawn(monoWorld, _filtered[_selected].Guid);

        ImGui.SameLine();
        if (ImGui.Button("Copy GUID##sprite_copy"))
        {
            try { ImGui.SetClipboardText(_filtered[_selected].Guid.ToString()); }
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
            _all = LoadSpritesFromGameAssets();
            Filter();
        }
        catch (Exception ex)
        {
            DevToolsMod.LogError($"SpritePanel failed to enumerate sprite assets: {ex.Message}");
            _all = [];
            _filtered = [];
        }
    }

    private static List<(string Name, Guid Guid)> LoadSpritesFromGameAssets()
    {
        var all = SpriteCatalog.GetAllSpritesCached(refreshSeconds: 10f);
        var list = new List<(string Name, Guid Guid)>(capacity: all.Length);
        foreach (var (guid, name) in all)
            list.Add((name, guid));
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

    private static void TrySpawn(MonoWorld world, Guid spriteGuid)
    {
        var player = world.TryGetUniqueEntityPlayer();
        if (_spawnAtPlayer && player is null)
        {
            _lastSpawnStatus = "No player";
            _lastSpawnStatusColor = UIColors.Warning;
            return;
        }

        // Determine spawn position.
        var pos = player?.GetPosition();
        var spawnPos = _spawnAtPlayer && pos is not null
            ? new System.Numerics.Vector2(pos.Value.X, pos.Value.Y)
            : System.Numerics.Vector2.Zero;

        spawnPos.Y += _yOffset;

        try
        {
            // Build a minimal entity that can be rendered by Murder's sprite render system.
            // We'll use the first animation of the sprite asset (if any).
            var firstAnim = TryGetFirstAnimation(spriteGuid);

            var components = new List<IComponent>(capacity: 4)
            {
                new PositionComponent(spawnPos),
            };

            if (firstAnim is not null)
            {
                var portrait = new Murder.Core.Portrait(spriteGuid, firstAnim);
                components.Add(new SpriteComponent(portrait));
            }
            else
            {
                // If the sprite asset has no animations, try to show frame 0.
                // (This is common for single-frame sprites.)
                components.Add(new SpriteComponent(spriteGuid));
                components.Add(new Murder.Components.Graphics.SpriteFrameComponent("", 0, spawnPos.Y));
            }

            var entity = world.AddEntity(components.ToArray());

            _lastSpawnStatus = "Spawned";
            _lastSpawnStatusColor = UIColors.Success;
        }
        catch (Exception ex)
        {
            DevToolsMod.LogError($"Sprite spawn failed: {ex}");
            ConsoleEngine.AddInfo($"Sprite spawn error: {ex.Message}");
            _lastSpawnStatus = "Spawn error";
            _lastSpawnStatusColor = UIColors.Error;
        }
    }

    private static string? TryGetFirstAnimation(Guid spriteGuid)
    {
        try
        {
            var asset = Game.Data.TryGetAsset(spriteGuid) as SpriteAsset;
            if (asset is null)
                return null;

            if (asset.Animations is { Count: > 0 })
                return asset.Animations.Keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase).FirstOrDefault();

            return null;
        }
        catch
        {
            return null;
        }
    }
}

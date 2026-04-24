using DevTools.Core;
using ImGuiNET;
using Murder.Core;
using NeverwayMod.DevTools.Core;

namespace DevTools.UI;

/// <summary>
/// Calendar tab (intentionally spelled "Calender" to match the requested tab name).
/// Provides a toggle for CalendarEvents systems.
/// </summary>
public static class CalenderPanel
{
    private static string _eventSearch = string.Empty;
    private static string _npcSearch = string.Empty;

    private static int _selectedEvent;
    private static int _selectedNpc;

    public static void Render()
    {
        var world = GameHelper.GetMonoWorld();

        ImGui.TextDisabled("Calendar screen toggles (events + birthdays). This modifies save data.");
        ImGui.Spacing();

        if (world == null)
        {
            ImGui.TextColored(UIColors.Error, "No active world.");
            return;
        }

        var save = CalendarSaveController.GetSaveOrNull();
        if (save is null)
        {
            ImGui.TextColored(UIColors.Error, "No save loaded.");
            return;
        }

        if (!CalendarSaveController.EnsureLoaded())
        {
            ImGui.TextColored(UIColors.Error, "Calendar backend not found.");
            if (!string.IsNullOrWhiteSpace(CalendarSaveController.LastStatus))
                ImGui.TextDisabled(CalendarSaveController.LastStatus);
            return;
        }

        // Keep the old system-level toggle as a separate section, since it may still be useful.
        if (ImGui.CollapsingHeader("Systems (advanced)", ImGuiTreeNodeFlags.DefaultOpen))
        {
            bool sysEnabled = CalendarEventsController.Enabled;
            if (ImGui.Checkbox("CalendarEvents systems enabled", ref sysEnabled))
                CalendarEventsController.SetEnabled(world, sysEnabled);

            if (!string.IsNullOrWhiteSpace(CalendarEventsController.LastStatus))
                ImGui.TextDisabled($"Status: {CalendarEventsController.LastStatus}");

            ImGui.Separator();
        }

        if (ImGui.BeginTabBar("CalenderTabs"))
        {
            if (ImGui.BeginTabItem("Events"))
            {
                RenderEvents(world, save);
                ImGui.EndTabItem();
            }

            if (ImGui.BeginTabItem("Birthdays"))
            {
                RenderBirthdays(world, save);
                ImGui.EndTabItem();
            }

            ImGui.EndTabBar();
        }

        if (!string.IsNullOrWhiteSpace(CalendarSaveController.LastStatus))
        {
            ImGui.Spacing();
            ImGui.TextDisabled($"Save status: {CalendarSaveController.LastStatus}");
        }
    }

    private static void RenderEvents(MonoWorld world, object save)
    {
        var all = CalendarEventCatalog.GetAllCalendarEventsCached(refreshSeconds: 10f);
        ImGui.TextDisabled($"Loaded: {all.Length} calendar events");

        ImGui.SetNextItemWidth(-1);
        ImGui.InputTextWithHint("##event_search", "Search events...", ref _eventSearch, 128);

        // Bulk actions
        ImGui.Spacing();
        if (ImGui.Button("Enable ALL (filtered)"))
            SetAllEvents(world, save, all, enabled: true);
        ImGui.SameLine();
        if (ImGui.Button("Disable ALL (filtered)"))
            SetAllEvents(world, save, all, enabled: false);

        ImGui.Separator();

        // List
        var listHeight = ImGui.GetTextLineHeightWithSpacing() * 14;
        if (ImGui.BeginChild("CalendarEventsList", new System.Numerics.Vector2(0, listHeight), ImGuiChildFlags.None))
        {
            int visibleIndex = 0;
            for (int i = 0; i < all.Length; i++)
            {
                var (guid, name) = all[i];
                if (!PassFilter(name, guid, _eventSearch))
                    continue;

                bool isSelected = visibleIndex == _selectedEvent;
                if (ImGui.Selectable(name, isSelected))
                    _selectedEvent = visibleIndex;

                visibleIndex++;
            }
        }
        ImGui.EndChild();

        // Selected toggle
        var filtered = all.Where(e => PassFilter(e.name, e.guid, _eventSearch)).ToArray();
        if (filtered.Length == 0)
        {
            _selectedEvent = 0;
            ImGui.TextDisabled("No events match your search.");
            return;
        }

        _selectedEvent = Math.Clamp(_selectedEvent, 0, filtered.Length - 1);
        var selected = filtered[_selectedEvent];

        bool enabled = CalendarSaveController.IsCalendarEventEnabled(save, selected.guid);
        if (ImGui.Checkbox($"Enabled##event_{selected.guid}", ref enabled))
        {
            CalendarSaveController.SetCalendarEventEnabled(world, save, selected.guid, enabled);
        }

        ImGui.SameLine();
        if (ImGui.Button("Copy GUID"))
        {
            try { ImGui.SetClipboardText(selected.guid.ToString()); } catch { }
        }
    }

    private static void SetAllEvents(MonoWorld world, object save, (Guid guid, string name)[] all, bool enabled)
    {
        int changed = 0;
        foreach (var e in all)
        {
            if (!PassFilter(e.name, e.guid, _eventSearch))
                continue;

            bool already = CalendarSaveController.IsCalendarEventEnabled(save, e.guid);
            if (enabled && already) continue;
            if (!enabled && !already) continue;

            if (CalendarSaveController.SetCalendarEventEnabled(world, save, e.guid, enabled))
                changed++;
        }

        ConsoleEngine.AddInfo($"Calendar events: {(enabled ? "enabled" : "disabled")} {changed} event(s)");
    }

    private static void RenderBirthdays(MonoWorld world, object save)
    {
        var allNpcs = NpcCatalog.GetAllNpcProfilesCached(refreshSeconds: 30f);
        ImGui.TextDisabled($"Loaded: {allNpcs.Length} NPC profiles");

        ImGui.SetNextItemWidth(-1);
        ImGui.InputTextWithHint("##npc_search", "Search NPCs...", ref _npcSearch, 128);

        ImGui.Spacing();
        if (ImGui.Button("Unlock ALL birthdays (filtered)"))
            SetAllBirthdays(world, save, allNpcs, unlocked: true);
        ImGui.SameLine();
        if (ImGui.Button("Lock ALL birthdays (filtered)"))
            SetAllBirthdays(world, save, allNpcs, unlocked: false);

        ImGui.Separator();

        var listHeight = ImGui.GetTextLineHeightWithSpacing() * 14;
        if (ImGui.BeginChild("BirthdayList", new System.Numerics.Vector2(0, listHeight), ImGuiChildFlags.None))
        {
            int visibleIndex = 0;
            for (int i = 0; i < allNpcs.Length; i++)
            {
                var guid = allNpcs[i].guid;
                var name = allNpcs[i].name;
                if (!PassFilter(name, guid, _npcSearch))
                    continue;

                bool isSelected = visibleIndex == _selectedNpc;
                if (ImGui.Selectable(name, isSelected))
                    _selectedNpc = visibleIndex;
                visibleIndex++;
            }
        }
        ImGui.EndChild();

        var filtered = allNpcs.Where(n => PassFilter(n.name, n.guid, _npcSearch)).ToArray();
        if (filtered.Length == 0)
        {
            _selectedNpc = 0;
            ImGui.TextDisabled("No NPCs match your search.");
            return;
        }

        _selectedNpc = Math.Clamp(_selectedNpc, 0, filtered.Length - 1);
        var selected = filtered[_selectedNpc];

        // For birthdays, RoadSaveData works with NpcId (not NPC asset guid).
        // We'll resolve NpcId by reflecting the NpcProfileAsset instance.
        if (!TryGetNpcIdFromProfileGuid(selected.guid, out var npcIdValue))
        {
            ImGui.TextColored(UIColors.Warning, "Could not resolve NpcId for selected NPC (reflection)." );
            return;
        }

        bool unlocked = CalendarSaveController.IsNpcBirthdayUnlocked(save, npcIdValue);
        if (ImGui.Checkbox($"Unlocked##bday_{selected.guid}", ref unlocked))
        {
            CalendarSaveController.SetNpcBirthdayUnlocked(save, npcIdValue, unlocked);
        }
    }

    private static void SetAllBirthdays(MonoWorld world, object save, (Guid guid, string name)[] allNpcs, bool unlocked)
    {
        int changed = 0;
        foreach (var n in allNpcs)
        {
            if (!PassFilter(n.name, n.guid, _npcSearch))
                continue;

            if (!TryGetNpcIdFromProfileGuid(n.guid, out var npcIdValue))
                continue;

            bool already = CalendarSaveController.IsNpcBirthdayUnlocked(save, npcIdValue);
            if (unlocked && already) continue;
            if (!unlocked && !already) continue;

            if (CalendarSaveController.SetNpcBirthdayUnlocked(save, npcIdValue, unlocked))
                changed++;
        }

        ConsoleEngine.AddInfo($"Birthdays: {(unlocked ? "unlocked" : "locked")} {changed} NPC(s)");
    }

    private static bool PassFilter(string name, Guid guid, string search)
    {
        if (string.IsNullOrWhiteSpace(search))
            return true;

        return name.Contains(search, StringComparison.OrdinalIgnoreCase) ||
               guid.ToString().Contains(search, StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryGetNpcIdFromProfileGuid(Guid npcProfileGuid, out object npcIdValue)
    {
        npcIdValue = null!;

        try
        {
            // Resolve NPC profile asset type and load the asset instance.
            Type? npcType = null;
            const string fqName = "Road.Assets.NpcProfileAsset";
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                npcType = asm.GetType(fqName, throwOnError: false, ignoreCase: false);
                if (npcType != null) break;
            }
            if (npcType is null)
                return false;

            var asset = Murder.Game.Data.TryGetAsset(npcProfileGuid);
            if (asset is null || !npcType.IsInstanceOfType(asset))
                return false;

            // NpcProfileAsset has field 'Id' of type Road.Core.NpcId.
            var idField = npcType.GetField("Id", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
            if (idField is null)
                return false;

            var value = idField.GetValue(asset);
            if (value is null)
                return false;

            npcIdValue = value;
            return true;
        }
        catch
        {
            return false;
        }
    }
}

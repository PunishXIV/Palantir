#if DEBUG
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;

namespace Palantir.Windows;

public sealed class DebugWindow(Configuration config, Network network, Storage storage, DeepDungeon dungeon)
    : Window("Palantir Debug##debug")
{
    public override void Draw()
    {
        var (floor, hoards, layout, pomanders) = dungeon.Instance();

        ImGui.TextUnformatted($"Floor {floor} · hoards collected {hoards} · in deep dungeon {dungeon.InDeepDungeon}");
        ImGui.TextUnformatted($"Layout {layout}");

        ImGui.TextUnformatted($"Hub {network.State} · storage {(storage.Available ? "ready" : "unavailable")}");
        ImGui.TextUnformatted(
            $"{dungeon.Visible.Count} known · {dungeon.Revealed.Count} revealed · {dungeon.Coffers.Count} coffers");
        ImGui.TextUnformatted(
            $"Thresholds: trap {config.Traps.Integrity} · hoard {config.Hoards.Integrity}");

        if (ImGui.Button("Mark player position"))
            dungeon.MarkPlayerPosition();

        ImGui.Separator();
        ImGui.TextUnformatted("Pomander slots");

        using (var table = ImRaii.Table("##pomanders", 4, ImGuiTableFlags.Borders | ImGuiTableFlags.SizingFixedFit))
        {
            if (table.Success)
            {
                ImGui.TableSetupColumn("Slot");
                ImGui.TableSetupColumn("ItemId");
                ImGui.TableSetupColumn("Count");
                ImGui.TableSetupColumn("Active");
                ImGui.TableHeadersRow();

                for (var slot = 0; slot < pomanders.Length; slot++)
                {
                    var (item, count, active) = pomanders[slot];
                    if (item == 0)
                        continue;

                    ImGui.TableNextColumn(); ImGui.TextUnformatted(slot.ToString());
                    ImGui.TableNextColumn(); ImGui.TextUnformatted(item.ToString());
                    ImGui.TableNextColumn(); ImGui.TextUnformatted(count.ToString());
                    ImGui.TableNextColumn(); ImGui.TextUnformatted(active ? "yes" : "");
                }
            }
        }

        ImGui.Separator();
        ImGui.TextUnformatted("Nearby objects");
        
        using var objects = ImRaii.Table("##objects", 9,
            ImGuiTableFlags.Borders | ImGuiTableFlags.SizingFixedFit | ImGuiTableFlags.ScrollY | ImGuiTableFlags.ScrollX,
            new Vector2(0, 260));

        if (!objects.Success)
            return;

        ImGui.TableSetupColumn("BaseId");
        ImGui.TableSetupColumn("Kind");
        ImGui.TableSetupColumn("Model");
        ImGui.TableSetupColumn("Targ");
        ImGui.TableSetupColumn("Event");
        ImGui.TableSetupColumn("Vis");
        ImGui.TableSetupColumn("Render");
        ImGui.TableSetupColumn("Name");
        ImGui.TableSetupColumn("Distance");
        ImGui.TableHeadersRow();

        foreach (var o in dungeon.NearbyObjects())
        {
            ImGui.TableNextColumn(); ImGui.TextUnformatted(o.BaseId.ToString());
            ImGui.TableNextColumn(); ImGui.TextUnformatted(o.Kind.ToString());

            ImGui.TableNextColumn();
            if (o.HasModel) ImGui.TextUnformatted("yes");
            else ImGui.TextColored(Palette.Amber, "no");

            ImGui.TableNextColumn(); ImGui.TextUnformatted(o.Targetable.ToString());
            ImGui.TableNextColumn(); ImGui.TextUnformatted(o.EventState.ToString());
            ImGui.TableNextColumn(); ImGui.TextUnformatted(o.Visibility.ToString());
            ImGui.TableNextColumn(); ImGui.TextUnformatted(o.RenderFlags.ToString());
            ImGui.TableNextColumn(); ImGui.TextUnformatted(o.Name);
            ImGui.TableNextColumn(); ImGui.TextUnformatted($"{o.Distance:F1}");
        }
    }
}
#endif

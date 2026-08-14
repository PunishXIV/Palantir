using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Components;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin.Services;

namespace Palantir.Windows;

public sealed class ConfigWindow : Window
{
    private static readonly Vector2 WindowSize = new(720, 450);

    private readonly Configuration _config;
    private readonly Network _network;
    private readonly IFramework _framework;

    private const string DistanceHelp =
        "How far from you markers are drawn. Large values cost frame time.";

    private static readonly RenderMode[] RenderModes = Enum.GetValues<RenderMode>();

    private string _newServer = "";
    private string? _addError;
    private bool _adding;

    public ConfigWindow(Configuration config, Network network, IFramework framework)
        : base("Palantir Settings##config", ImGuiWindowFlags.NoResize)
    {
        _config = config;
        _network = network;
        _framework = framework;

        Size = WindowSize;
        SizeCondition = ImGuiCond.Always;
    }

    public override void Draw()
    {
        using var tabs = ImRaii.TabBar("##tabs");
        if (!tabs.Success)
            return;

        DrawRendering();
        DrawServers();
    }

    private void DrawRendering()
    {
        using var tab = ImRaii.TabItem("Rendering");
        if (!tab.Success)
            return;

        using (var markers = ImRaii.Header("Traps / Accursed Hoard", ImGuiTreeNodeFlags.DefaultOpen))
        {
            if (markers.Success)
                DrawMarkerSection();
        }

        using (var coffers = ImRaii.Header("Treasure Coffers", ImGuiTreeNodeFlags.None))
        {
            if (coffers.Success)
                DrawCofferSection();
        }

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        Check("Draw Additional Debug Information", _config.DrawDebugInfo, v => _config.DrawDebugInfo = v);

        ImGuiComponents.HelpMarker(
            "Draws verbose debug information below trap markers etc. Used to report obviously fake " +
            "or incorrect trap submissions to the server administrator for investigation.");
    }

    private void DrawMarkerSection()
    {
        using (var table = ImRaii.Table("##markers", 8, ImGuiTableFlags.SizingFixedFit))
        {
            if (table.Success)
            {
                ImGui.TableSetupColumn("Marker", ImGuiTableColumnFlags.WidthFixed, NameWidth);
                ImGui.TableSetupColumn("On", ImGuiTableColumnFlags.WidthFixed, Fit("On"));
                ImGui.TableSetupColumn("Label", ImGuiTableColumnFlags.WidthFixed, Fit("Label"));
                ImGui.TableSetupColumn("Colour", ImGuiTableColumnFlags.WidthFixed, Fit("Colour"));
                ImGui.TableSetupColumn("Fill", ImGuiTableColumnFlags.WidthFixed, Fit("Fill", icon: true));
                ImGui.TableSetupColumn("Integrity", ImGuiTableColumnFlags.WidthFixed,
                    Math.Max(Fit("Integrity", icon: true), 100 * ImGuiHelpers.GlobalScale));

                ImGui.TableSetupColumn("Render Distance", ImGuiTableColumnFlags.WidthStretch);
                ImGui.TableSetupColumn("Mode", ImGuiTableColumnFlags.WidthFixed,
                    Math.Max(Fit("Mode", icon: true), 90 * ImGuiHelpers.GlobalScale));

                ImGui.TableNextRow(ImGuiTableRowFlags.Headers);
                Header("Marker");
                Header("On");
                Header("Label");
                Header("Colour");
                Header("Fill",
                    "Fills the marker with a faded version of its colour. Off leaves the outer ring " +
                    "only, so you can still see the ground underneath.\n\n" +
                    "DirectX render mode only. VFX omens are the game's own filled effects.");
                Header("Integrity",
                    "How many players have to confirm the same spot before Palantir shows it to you.\n\n" +
                    "Traps and hoards you find yourself always show up, however low the number.\n\n" +
                    "0 shows everything, even a spot only one person has ever reported, mistakes " +
                    "included.\n\nHigher numbers are more trustworthy, but markers take longer to appear " +
                    "and rarely discovered ones may never show up at all.\n\n" +
                    "Recommended: 4 for traps, 2 for the Accursed Hoard. Traps are in nearly every " +
                    "room, so they reach 4 confirmations quickly. There is only one potential hoard per floor, so these " +
                    "accumulate confirmations much less quickly.");
                Header("Render Distance", DistanceHelp);
                Header("Mode",
                    "DirectX is the default. Markers are drawn over the game, so they stay visible " +
                    "through walls, enemies and other players.\n\n" +
                    "VFX uses the game's own omen effects. They look native and sit on the ground, but " +
                    "anything in front of them hides them, and they are easy to mistake for an enemy's " +
                    "attack telegraph.");

                DrawRow("Traps", _config.Traps, crowdSourced: true);
                DrawRow("Accursed Hoard", _config.Hoards, crowdSourced: true);
            }
        }

        ImGui.Spacing();
        DrawMergeToggle();
    }

    private void DrawRow(string label, RenderCategory category, bool crowdSourced)
    {
        using var _ = ImRaii.PushId(label);

        NameCell(label);

        ImGui.TableNextColumn();
        Check("##on", category.Enabled, v => category.Enabled = v);

        using var disabled = ImRaii.Disabled(!category.Enabled);

        ImGui.TableNextColumn();
        Check("##label", category.Label, v => category.Label = v);

        ImGui.TableNextColumn();
        DrawColour(category);

        if (crowdSourced)
        {
            ImGui.TableNextColumn();
            using (ImRaii.Disabled(category.Mode != RenderMode.DirectX))
                Check("##fill", category.Fill, v => category.Fill = v);

            if (category.Mode != RenderMode.DirectX && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                ImGui.SetTooltip("Only applies to the DirectX render mode. VFX omens are the game's own filled effects.");

            ImGui.TableNextColumn();
            ImGui.SetNextItemWidth(ImGui.GetContentRegionAvail().X);
            var integrity = category.Integrity;
            if (ImGui.SliderInt("##integrity", ref integrity, 0, 10))
                category.Integrity = integrity;
            SaveOnRelease();
        }

        ImGui.TableNextColumn();
        ImGui.SetNextItemWidth(ImGui.GetContentRegionAvail().X);
        DistanceSlider(category);

        if (!crowdSourced)
            return;

        ImGui.TableNextColumn();
        ImGui.SetNextItemWidth(ImGui.GetContentRegionAvail().X);
        
        using var mode = ImRaii.Combo("##mode", category.Mode.ToString());
        if (!mode.Success)
            return;

        foreach (var option in RenderModes)
        {
            if (!ImGui.Selectable(option.ToString(), category.Mode == option))
                continue;

            category.Mode = option;
            _config.Save();
        }
    }

    private void DrawMergeToggle()
    {
        var traps = _config.Traps;
        var hoards = _config.Hoards;

        var blocker = (traps.Enabled, hoards.Enabled) switch
        {
            (false, _) or (_, false) => "Enable both traps and the Accursed Hoard to merge them.",
            _ when traps.Mode != RenderMode.DirectX || hoards.Mode != RenderMode.DirectX =>
                "Both categories must use the DirectX render mode. VFX omens are separate game " +
                "objects and cannot be blended into one marker.",
            _ when !traps.Fill => "Traps must be filled to merge. The merged marker uses the trap " +
                                  "colour as its body and the hoard colour as its outline.",
            _ => null,
        };

        using (ImRaii.Disabled(blocker is not null))
            Check("Merge Trap / Accursed Hoard Markers", _config.MergeTrapHoard, v => _config.MergeTrapHoard = v);

        if (blocker is not null && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(blocker);
        else
            ImGuiComponents.HelpMarker(
                "A trap and a hoard can share the same spot. Rather than draw two rings fighting " +
                "over the same ground, draw one with the trap's body colour and the hoard's outline.");
    }

    private void DrawCofferSection()
    {
        using var table = ImRaii.Table("##coffers", 5, ImGuiTableFlags.SizingFixedFit);
        if (!table.Success)
            return;

        ImGui.TableSetupColumn("Coffer", ImGuiTableColumnFlags.WidthFixed, NameWidth);
        ImGui.TableSetupColumn("On", ImGuiTableColumnFlags.WidthFixed, Fit("On"));
        ImGui.TableSetupColumn("Label", ImGuiTableColumnFlags.WidthFixed, Fit("Label"));
        ImGui.TableSetupColumn("Colour", ImGuiTableColumnFlags.WidthFixed, Fit("Colour"));
        ImGui.TableSetupColumn("Render Distance", ImGuiTableColumnFlags.WidthStretch);

        ImGui.TableNextRow(ImGuiTableRowFlags.Headers);
        Header("Coffer");
        Header("On");
        Header("Label");
        Header("Colour");
        Header("Render Distance", DistanceHelp);

        DrawRow("Bronze", _config.BronzeCoffers, crowdSourced: false);
        DrawRow("Silver", _config.SilverCoffers, crowdSourced: false);
        DrawRow("Gold", _config.GoldCoffers, crowdSourced: false);
        DrawMimicRow();
    }

    private void DrawMimicRow()
    {
        using var _ = ImRaii.PushId("Mimic");

        NameCell("Mimic",
            "Only found in Palace of the Dead, on floor 49 and below. Drawn with " +
            "the trap colour and distance above, and always drawn in DirectX mode.");

        ImGui.TableNextColumn();
        Check("##on", _config.MimicCoffers, v => _config.MimicCoffers = v);

        using var disabled = ImRaii.Disabled(!_config.MimicCoffers);

        ImGui.TableNextColumn();
        Check("##label", _config.MimicLabel, v => _config.MimicLabel = v);
        
        ImGui.TableNextColumn();

        ImGui.TableNextColumn();
        ImGui.AlignTextToFramePadding();
        ImGui.TextDisabled("uses display settings of traps");
    }
    
    private static float Fit(string header, bool icon = false) =>
        Math.Max(
            ImGui.CalcTextSize(header).X + (icon ? ImGui.GetStyle().ItemSpacing.X + ImGui.GetFrameHeight() : 0),
            ImGui.GetFrameHeight());

    private static float NameWidth => Math.Max(Fit("Accursed Hoard"), Fit("Mimic", icon: true));
    
    private static void Header(string label, string? help = null)
    {
        ImGui.TableNextColumn();
        ImGui.TextUnformatted(label);

        if (help is not null)
            ImGuiComponents.HelpMarker(help);
    }

    private static void NameCell(string label, string? help = null)
    {
        ImGui.TableNextColumn();
        ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted(label);

        if (help is not null)
            ImGuiComponents.HelpMarker(help);
    }

    private void Check(string id, bool value, Action<bool> set)
    {
        if (!ImGui.Checkbox(id, ref value))
            return;

        set(value);
        _config.Save();
    }

    private void DistanceSlider(RenderCategory category)
    {
        var distance = category.Distance;
        if (ImGui.SliderInt("##distance", ref distance, 20, 250))
            category.Distance = distance;
        SaveOnRelease();
    }

    private void DrawColour(RenderCategory category)
    {
        var colour = category.Colour;
        if (ImGui.ColorEdit3("##colour", ref colour, ImGuiColorEditFlags.NoInputs))
            category.Colour = colour;
        SaveOnRelease();
    }

    private void DrawServers()
    {
        using var tab = ImRaii.TabItem("Servers");
        if (!tab.Success)
            return;

        using (ImRaii.Disabled(_network.IsEnabled))
        {
            Field("Server");
            using (var combo = ImRaii.Combo("##server", _config.SelectedServer))
            {
                if (combo.Success)
                {
                    foreach (var server in _config.AllServers)
                    {
                        if (!ImGui.Selectable(server, server == _config.SelectedServer))
                            continue;

                        _config.SelectedServer = server;
                        _config.Save();
                    }
                }
            }

            ImGui.Spacing();

            var style = ImGui.GetStyle();
            var button = ImGui.CalcTextSize("Add").X + (style.FramePadding.X * 2);
            ImGui.SetNextItemWidth(ImGui.GetContentRegionAvail().X - button - style.ItemSpacing.X);
            ImGui.InputText("##newServer", ref _newServer, 512);
            ImGui.SameLine();

            using (ImRaii.Disabled(_adding || _newServer.Length == 0))
            {
                if (ImGui.Button("Add"))
                    _ = AddAsync(_newServer.Trim());
            }

            var pinned = Configuration.Pinned.Contains(_config.SelectedServer);
            using (ImRaii.Disabled(pinned))
            {
                if (ImGui.Button("Remove selected"))
                    Remove(_config.SelectedServer);
            }

            if (pinned && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                ImGui.SetTooltip("Built-in servers cannot be removed.");
        }

        Check("Connect on startup", _config.AutoConnect, v => _config.AutoConnect = v);

        if (_network.IsEnabled)
            ImGui.TextDisabled("Disconnect to change servers.");

        if (_addError is { } error)
            Palette.TextWrapped(Palette.Red, error);
    }

    private static void Field(string label)
    {
        ImGui.Spacing();
        ImGui.TextUnformatted(label);
        ImGui.SetNextItemWidth(ImGui.GetContentRegionAvail().X);
    }

    private void SaveOnRelease()
    {
        if (ImGui.IsItemDeactivatedAfterEdit())
            _config.Save();
    }

    private async Task AddAsync(string url)
    {
        _adding = true;

        string? error = null;
        try
        {
            await _network.MetadataAsync(url);
        }
        catch (Exception ex)
        {
            error = ex.Message;
        }
        
        await _framework.RunOnTick(() =>
        {
            _adding = false;
            _addError = error;

            if (error is not null)
                return;

            var server = url.TrimEnd('/');
            if (!_config.AllServers.Contains(server))
                _config.Servers.Add(server);

            _config.SelectedServer = server;
            _config.Save();
            _newServer = "";
        });
    }

    private void Remove(string server)
    {
        if (Configuration.Pinned.Contains(server))
            return;

        _config.Servers.Remove(server);
        _config.Accounts.Remove(server);
        _config.SelectedServer = Configuration.DefaultServer;
        _config.Save();
    }
}

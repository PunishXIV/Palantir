using System.IO;
using System.Numerics;
using System.Text.Json;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Components;
using Dalamud.Interface.ImGuiFileDialog;
using Dalamud.Interface.Textures;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin.Services;
using Palantir.Common;

namespace Palantir.Windows;

public sealed class ConfigWindow : Window
{
    private static readonly Vector2 WindowSize = new(720, 450);

    private readonly Configuration _config;
    private readonly Network _network;
    private readonly IFramework _framework;
    private readonly ITextureProvider _texture;
    private readonly Storage _storage;
    private readonly DeepDungeon _dungeon;

    private readonly FileDialogManager _files = new();

    private static readonly JsonSerializerOptions Json =
        new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private static readonly (string Label, byte? Type)[] TypeFilters =
    [
        ("Everything", null),
        ("Traps only", MarkerType.Trap),
        ("Accursed Hoard only", MarkerType.Hoard),
    ];

    private static readonly string[] Groups =
        [.. Enum.GetValues<ETerritoryType>().Select(t => t.ToString().Split('_')[0]).Distinct()];

    private const uint ChestBronze = 60911; // also the mimic
    private const uint ChestSilver = 60912;
    private const uint ChestGold = 60913;

    private const string DistanceHelp =
        "How far from you markers are drawn. Large values cost frame time.";

    private static readonly RenderMode[] RenderModes = Enum.GetValues<RenderMode>();

    private string _newServer = "";
    private string? _addError;
    private bool _adding;

    private readonly HashSet<string> _selected = [.. Groups];
    private int _typeFilter;
    private bool _trustImported;
    private bool _busy;
    private string? _dataMessage;
    private bool _dataFailed;

    public ConfigWindow(
        Configuration config,
        Network network,
        IFramework framework,
        ITextureProvider texture,
        Storage storage,
        DeepDungeon dungeon)
        : base("Palantir Settings##config", ImGuiWindowFlags.NoResize)
    {
        _config = config;
        _network = network;
        _framework = framework;
        _texture = texture;
        _storage = storage;
        _dungeon = dungeon;

        Size = WindowSize;
        SizeCondition = ImGuiCond.Always;
    }

    public override void Draw()
    {
        using (var tabs = ImRaii.TabBar("##tabs"))
        {
            if (tabs.Success)
            {
                DrawRendering();
                DrawServers();
                DrawData();
            }
        }

        _files.Draw();
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

    private void DrawRow(string label, RenderCategory category, bool crowdSourced, uint? icon = null)
    {
        using var _ = ImRaii.PushId(label);

        NameCell(label, icon: icon);

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

        DrawRow("Bronze", _config.BronzeCoffers, crowdSourced: false, ChestBronze);
        DrawRow("Silver", _config.SilverCoffers, crowdSourced: false, ChestSilver);
        DrawRow("Gold", _config.GoldCoffers, crowdSourced: false, ChestGold);
        DrawMimicRow();
    }

    private void DrawMimicRow()
    {
        using var _ = ImRaii.PushId("Mimic");

        NameCell("Mimic",
            "Only found in Palace of the Dead, on floor 49 and below. Drawn with " +
            "the trap colour and distance above, and always drawn in DirectX mode.",
            ChestBronze);

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

    private static float IconSpace => ImGui.GetFrameHeight() + ImGui.GetStyle().ItemSpacing.X;

    private static float NameWidth => Math.Max(
        Fit("Accursed Hoard"),
        IconSpace + Math.Max(Fit("Bronze"), Fit("Mimic", icon: true)));
    
    private static void Header(string label, string? help = null)
    {
        ImGui.TableNextColumn();
        ImGui.TextUnformatted(label);

        if (help is not null)
            ImGuiComponents.HelpMarker(help);
    }

    private void NameCell(string label, string? help = null, uint? icon = null)
    {
        ImGui.TableNextColumn();
        ImGui.AlignTextToFramePadding();

        if (icon is { } id)
        {
            var size = ImGui.GetFrameHeight();
            ImGui.Image(_texture.GetFromGameIcon(new GameIconLookup(id)).GetWrapOrEmpty().Handle, new Vector2(size));
            ImGui.SameLine();
        }

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

    private void DrawData()
    {
        using var tab = ImRaii.TabItem("Data");
        if (!tab.Success)
            return;

        if (!_storage.Available)
        {
            Palette.TextWrapped(Palette.Red, "No local cache. Palantir could not open its database.");
            return;
        }

        ImGui.TextWrapped(
            "You can export your local cache to share them with another player. An export is a " +
            "JSON file with a .orb extension, so you can open it and see exactly what you are " +
            "sending. Useful for periods of server downtime or in the case your chosen server " +
            "is shut down permanently.");

        ImGui.TextWrapped("Imported markers are never uploaded to a Palantir server.");

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        ImGui.TextUnformatted("Export");
        ImGui.SetNextItemWidth(220 * ImGuiHelpers.GlobalScale);

        using (var combo = ImRaii.Combo("##exportType", TypeFilters[_typeFilter].Label))
        {
            if (combo.Success)
            {
                for (var i = 0; i < TypeFilters.Length; i++)
                    if (ImGui.Selectable(TypeFilters[i].Label, i == _typeFilter))
                        _typeFilter = i;
            }
        }

        foreach (var group in Groups)
        {
            var on = _selected.Contains(group);
            if (ImGui.Checkbox(GroupName(group), ref on))
                _ = on ? _selected.Add(group) : _selected.Remove(group);
        }

        using (ImRaii.Disabled(_busy || _selected.Count == 0))
        {
            if (ImGui.Button("Export..."))
                _files.SaveFileDialog("Export markers", "Palantir Orb{.orb}",
                    $"palantir-export-{DateTime.Now:yyyyMMdd-HHmmss}.orb", ".orb",
                    (ok, path) => { if (ok) _ = ExportAsync(Orb(path)); });
        }

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        ImGui.TextUnformatted("Import");
        ImGui.Checkbox("Trust imported markers as my own discoveries", ref _trustImported);

        ImGuiComponents.HelpMarker(
            "Imported markers arrive with only a single confirmation, so they stay hidden until other players " +
            "confirm them or you lower your integrity threshold. Tick this to show them straight away, as though you " +
            "had found them yourself.\n\n" +
            "Imported markers are never uploaded to a Palantir server either way.");

        var blocker = _dungeon.InDeepDungeon
            ? "Must not be already in a deep dungeon to import. Imported markers only load when you enter a floorset."
            : null;

        using (ImRaii.Disabled(_busy || blocker is not null))
        {
            if (ImGui.Button("Import..."))
                _files.OpenFileDialog("Import markers", "Palantir Orb{.orb},.*",
                    (ok, paths) => { if (ok && paths.Count > 0) _ = ImportAsync(paths[0]); }, 1);
        }

        if (blocker is not null && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(blocker);

        if (_dataMessage is { } message)
            Palette.TextWrapped(_dataFailed ? Palette.Red : Palette.Green, message);
    }

    private static string GroupName(string group) => group switch
    {
        "Palace" => "Palace of the Dead",
        "HeavenOnHigh" => "Heaven-on-High",
        "EurekaOrthos" => "Eureka Orthos",
        "PilgrimsTraverse" => "Pilgrim's Traverse",
        _ => group,
    };

    private static string Orb(string path) =>
        path.EndsWith(".orb", StringComparison.OrdinalIgnoreCase) ? path : path + ".orb";

    private async Task ExportAsync(string path)
    {
        _busy = true;

        string message;
        var failed = false;
        try
        {
            var type = TypeFilters[_typeFilter].Type;
            var markers = (await _storage.All())
                .Where(r => (type is null || r.Type == type)
                            && _selected.Contains(Group(r.Territory))
                            && MarkerId.IsValid(r.X, r.Y, r.Z))
                .Select(r => r.ToMarker())
                .ToArray();

            var export = new MarkerExport(Storage.ExportVersion, DateTime.UtcNow, markers);
            await File.WriteAllTextAsync(path, JsonSerializer.Serialize(export, Json));

            message = $"Exported {markers.Length} marker(s).";
        }
        catch (Exception ex)
        {
            message = ex.Message;
            failed = true;
        }

        await _framework.RunOnTick(() => Done(message, failed));
    }

    private async Task ImportAsync(string path)
    {
        _busy = true;

        string message;
        var failed = false;
        try
        {
            var export = JsonSerializer.Deserialize<MarkerExport>(await File.ReadAllTextAsync(path), Json)
                         ?? throw new InvalidOperationException("That file is empty.");

            if (export.Version != Storage.ExportVersion)
                throw new InvalidOperationException(
                    $"That file is export v{export.Version}; this plugin reads v{Storage.ExportVersion}.");

            var markers = (export.Markers ?? [])
                .Where(m => Territories.IsDeepDungeon(m.Territory)
                            && MarkerType.IsPersistable(m.Type)
                            && MarkerId.IsValid(m.X, m.Y, m.Z))
                .Select(m => m with { Id = MarkerId.For(m.Territory, m.Type, m.X, m.Y, m.Z) })
                .DistinctBy(m => m.Id)
                .ToArray();

            var (added, known) = await _storage.Import(markers, _trustImported);

            message = $"{added} imported, {known} already known, " +
                      $"{(export.Markers?.Length ?? 0) - markers.Length} rejected.";
        }
        catch (Exception ex)
        {
            message = ex.Message;
            failed = true;
        }

        await _framework.RunOnTick(() => Done(message, failed));
    }

    private void Done(string message, bool failed)
    {
        _busy = false;
        _dataMessage = message;
        _dataFailed = failed;
    }

    private static string Group(ushort territory) =>
        Enum.GetName((ETerritoryType)territory)?.Split('_')[0] ?? "";

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

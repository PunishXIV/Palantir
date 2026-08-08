using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;

namespace Palantir.Windows;

internal static class Palette
{
    public static readonly Vector4 Green = new(0.2f, 0.85f, 0.2f, 1f);
    public static readonly Vector4 Amber = new(1f, 0.8f, 0f, 1f);
    public static readonly Vector4 Red = new(1f, 0.4f, 0.4f, 1f);

    public static void TextWrapped(Vector4 colour, string text)
    {
        using (ImRaii.PushColor(ImGuiCol.Text, colour))
            ImGui.TextWrapped(text);
    }
}

public sealed class MainWindow : Window
{
    private const float Width = 350;

    private readonly Configuration _config;
    private readonly Network _network;
    private readonly Storage _storage;
    private readonly DeepDungeon _dungeon;
    private readonly ConfigWindow _settings;
#if DEBUG
    private readonly DebugWindow _debug;
#endif

    private volatile bool _busy;

    public MainWindow(
        Configuration config,
        Network network,
        Storage storage,
        DeepDungeon dungeon,
        ConfigWindow settings
#if DEBUG
        , DebugWindow debug
#endif
    ) : base("Palantir##main", ImGuiWindowFlags.AlwaysAutoResize)
    {
        _config = config;
        _network = network;
        _storage = storage;
        _dungeon = dungeon;
        _settings = settings;
#if DEBUG
        _debug = debug;
#endif
        
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(Width, 0),
            MaximumSize = new Vector2(Width, float.MaxValue),
        };
    }

    public override void Draw()
    {
        var connected = _network.IsConnected;
        var enabled = _network.IsEnabled;

        using (ImRaii.Disabled(_busy || _config.SelectedServer.Length == 0))
        {
            if (ImGui.Button(enabled ? "Disconnect" : "Connect", new Vector2(110, 0)))
                _ = ToggleAsync(enabled);
        }

        ImGui.SameLine();
        ImGui.AlignTextToFramePadding();
        DrawStatus(connected, enabled);

        if (!connected && _network.LastError is { } error)
            Palette.TextWrapped(Palette.Red, error);

        ImGui.Separator();

        var presence = _network.Presence;
        ImGui.TextUnformatted($"{presence.Online} online, {presence.InDungeon} in a deep dungeon");

        if (_dungeon.InDeepDungeon)
        {
            ImGui.TextUnformatted($"Floor {_dungeon.Floor} — {_dungeon.Visible.Count} markers known");

            if (!connected)
                Palette.TextWrapped(Palette.Amber, _storage.Available
                    ? "Discoveries are saved locally and upload when you reconnect."
                    : "Discoveries upload only if you reconnect before leaving this floorset.");
        }

        ImGui.Separator();

        if (ImGui.Button("Settings"))
            _settings.Toggle();

#if DEBUG
        ImGui.SameLine();
        if (ImGui.Button("Debug"))
            _debug.Toggle();
#endif
    }

    private void DrawStatus(bool connected, bool enabled)
    {
        var cache = _storage.Available;

        var (wifi, wifiTip) = connected
            ? (Palette.Green, $"Connected to {_config.SelectedServer}!")
            : enabled
                ? (Palette.Amber, _network.IsRetrying
                    ? "Reconnecting..."
                    : "Connecting...")
                : (Palette.Red, "Not connected. Only markers already in your local cache will be displayed.");

        var (database, databaseTip) = cache
            ? (Palette.Green, "Local cache active.")
            : connected
                ? (Palette.Amber, "No local cache. Discoveries upload as you find them, but are lost " +
                                  "if the connection drops and you leave the floorset.")
                : (Palette.Red, "No local cache and no server. Discoveries are lost when you leave this floorset.");

        var (hook, hookTip) = _dungeon.HookActive
            ? (Palette.Green, "ActionEffectHandler hook initialized.")
            : cache || connected
                ? (Palette.Amber, "ActionEffectHandler hook failed. Markers revealed by a Pomander " +
                                  "of Sight, the Accursed Hoard and markers already in your local cache still function.")
                : (Palette.Red, "ActionEffectHandler hook failed, with no local cache and no server to fall back on.");

        Icon(FontAwesomeIcon.Wifi, wifi, wifiTip);
        ImGui.SameLine();
        Icon(FontAwesomeIcon.Database, database, databaseTip);
        ImGui.SameLine();
        Icon(FontAwesomeIcon.PuzzlePiece, hook, hookTip);
    }

    private static void Icon(FontAwesomeIcon icon, Vector4 colour, string tooltip)
    {
        using (ImRaii.PushFont(UiBuilder.IconFont))
            ImGui.TextColored(colour, icon.ToIconString());

        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(tooltip);
    }

    private async Task ToggleAsync(bool enabled)
    {
        _busy = true;
        try
        {
            if (enabled)
                await _network.DisableAsync();
            else
                await _network.EnableAsync(_config.SelectedServer);
        }
        finally
        {
            _busy = false;
        }
    }
}

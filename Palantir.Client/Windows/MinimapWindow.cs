using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Textures;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin.Services;
using RoomFlags = FFXIVClientStructs.FFXIV.Client.Game.InstanceContent.InstanceContentDeepDungeon.RoomFlags;

namespace Palantir.Windows;

public sealed class MinimapWindow : Window
{
    private const ImGuiWindowFlags Borderless =
        ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoScrollbar |
        ImGuiWindowFlags.NoScrollWithMouse | ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoCollapse |
        ImGuiWindowFlags.NoFocusOnAppearing | ImGuiWindowFlags.NoNav;

    private const string RoomsTexture = "ui/uld/DeepDungeonNaviMap_Rooms_hr1.tex";
    private const string NaviTexture = "ui/uld/DeepDungeonNaviMap_hr1.tex";

    public const int Columns = 5;
    private const float Unit = 55;
    private const ushort Votive = 0x100; // PT votive candle thingy, not in CS?

    private const uint White = 0xFFFFFFFF;
    private const uint Dim = 0x99999999;
    private const uint Member = 0xFFFFA64D;
    private const uint Outline = 0xFF000000;

    private static readonly uint[] Chests = [GameIcon.ChestBronze, GameIcon.ChestSilver, GameIcon.ChestGold];

    private readonly Configuration _config;
    private readonly DeepDungeon _dungeon;
    private readonly ITextureProvider _texture;

    private FloorMap _map;
    private IDisposable? _style;

    public MinimapWindow(Configuration config, DeepDungeon dungeon, ITextureProvider texture)
        : base("Palantir Minimap##minimap", Borderless)
    {
        _config = config;
        _dungeon = dungeon;
        _texture = texture;

        IsOpen = true;
        RespectCloseHotkey = false;
        DisableWindowSounds = true;
        AllowPinning = false;
        AllowClickthrough = false;
    }

    public FloorMap? Current() =>
        _config.Minimap.Enabled && _dungeon.InDeepDungeon ? _dungeon.Map() : null;

    public override bool DrawConditions()
    {
        if (!_config.Minimap.Detached || Current() is not { } map)
            return false;

        _map = map;
        return true;
    }

    public override void PreDraw()
    {
        var settings = _config.Minimap;

        Flags = Borderless
                | (settings.Locked || settings.ClickThrough ? ImGuiWindowFlags.NoMove : 0)
                | (settings.ClickThrough ? ImGuiWindowFlags.NoInputs : 0);
        BgAlpha = settings.Opacity;

        _style = ImRaii.PushStyle(ImGuiStyleVar.WindowBorderSize, 0f)
            .Push(ImGuiStyleVar.WindowPadding, new Vector2(4 * ImGuiHelpers.GlobalScale));
    }

    public override void PostDraw()
    {
        _style?.Dispose();
        _style = null;
    }

    public override void Draw() => DrawMap(_map, Unit * ImGuiHelpers.GlobalScale * _config.Minimap.Scale);

    public void DrawMap(in FloorMap map, float room, bool centre = false)
    {
        var active = new bool[map.Rooms.Length];
        var (minCol, maxCol, minRow, maxRow) = (Columns, -1, Columns, -1);

        for (var i = 0; i < active.Length; i++)
        {
            active[i] = ((int)map.Rooms[i] & 0xF) != 0 || i == map.Player || map.Party.Contains(i);
            if (!active[i])
                continue;

            (minCol, maxCol) = (Math.Min(minCol, i % Columns), Math.Max(maxCol, i % Columns));
            (minRow, maxRow) = (Math.Min(minRow, i / Columns), Math.Max(maxRow, i / Columns));
        }

        var crop = _config.Minimap.ActiveOnly && maxCol >= 0;
        if (!crop)
            (minCol, maxCol, minRow, maxRow) = (0, Columns - 1, 0, (map.Rooms.Length - 1) / Columns);

        var size = new Vector2(maxCol - minCol + 1, maxRow - minRow + 1) * room;

        var draw = ImGui.GetWindowDrawList();
        var origin = ImGui.GetCursorScreenPos();
        var k = room / Unit;

        if (centre)
            origin.X += (ImGui.GetContentRegionAvail().X - size.X) / 2;

        var rooms = _texture.GetFromGame(RoomsTexture).GetWrapOrEmpty().Handle;
        var navi = _texture.GetFromGame(NaviTexture).GetWrapOrEmpty().Handle;
        var passage = Icon(map.PassageOpen ? GameIcon.PassageOpen : GameIcon.PassageClosed);
        var @return = Icon(map.ReturnOpen ? GameIcon.ReturnOpen : GameIcon.ReturnClosed);

        for (var i = 0; i < map.Rooms.Length; i++)
        {
            if (crop && !active[i])
                continue;

            var flags = map.Rooms[i];
            var cell = origin + new Vector2(i % Columns - minCol, i / Columns - minRow) * room;
            var middle = cell + new Vector2(room / 2);

            var tile = (int)flags & 0xF;
            var uv = new Vector2(0.0104f + tile % 4 * 0.25f, 0.0104f + tile / 4 * 0.25f);
            draw.AddImage(rooms, cell, cell + new Vector2(room), uv, uv + new Vector2(0.2292f), tile > 0 ? White : Dim);

            if (i == map.Player)
                Centred(draw, navi, middle, 40 * k, new(0.2424f, 0.4571f), new(0.4848f, 0.6857f));

            if (flags.HasFlag(RoomFlags.Home))
                Centred(draw, navi, middle, 40 * k, new(0.4848f, 0.4571f), new(0.7272f, 0.6657f));

            var poi = cell + new Vector2(17.5f, 27.5f) * k;
            if (flags.HasFlag(RoomFlags.Passage))
                Image(draw, passage, poi, 20 * k);
            if (flags.HasFlag(RoomFlags.Return))
                Image(draw, @return, poi, 20 * k);
            if (((ushort)flags & Votive) != 0)
                Image(draw, Icon(GameIcon.Votive), poi, 20 * k);

            for (var c = 0; c < Chests.Length; c++)
                if ((map.Chests[i] & (1 << c)) != 0)
                    Image(draw, Icon(Chests[c]), cell + new Vector2(1.25f + 11.25f * c, 1.25f) * k, 30 * k);

            if (_config.Minimap.Party)
                DrawParty(draw, map.Party, i, cell, room);

            if (i == map.Player)
                DrawPlayer(draw, navi, middle, map.Rotation, k);
        }

        ImGui.Dummy(size);
    }

    private ImTextureID Icon(uint id) => _texture.GetFromGameIcon(new GameIconLookup(id)).GetWrapOrEmpty().Handle;

    private static void Image(ImDrawListPtr draw, ImTextureID texture, Vector2 at, float size) =>
        draw.AddImage(texture, at, at + new Vector2(size), Vector2.Zero, Vector2.One, White);

    private static void Centred(ImDrawListPtr draw, ImTextureID texture, Vector2 centre, float size, Vector2 uv0, Vector2 uv1) =>
        draw.AddImage(texture, centre - new Vector2(size / 2), centre + new Vector2(size / 2), uv0, uv1, White);

    private static void DrawParty(ImDrawListPtr draw, int[] party, int room, Vector2 cell, float size)
    {
        var radius = size * 0.055f;
        var stacked = 0;

        foreach (var member in party)
        {
            if (member != room)
                continue;

            var dot = cell + new Vector2(0.88f, 0.9f - 0.13f * stacked++) * size;
            draw.AddCircleFilled(dot, radius, Member);
            draw.AddCircle(dot, radius, Outline);
        }
    }

    private static void DrawPlayer(ImDrawListPtr draw, ImTextureID texture, Vector2 centre, float rotation, float k)
    {
        var cos = -MathF.Cos(rotation);
        var sin = MathF.Sin(rotation);
        var (width, front, back) = (20 * k, 23.4375f * k, 16.5625f * k);

        Vector2 At(float x, float y) => centre + new Vector2(x * cos - y * sin, x * sin + y * cos);

        draw.AddImageQuad(texture,
            At(-width, -front), At(width, -front), At(width, back), At(-width, back),
            new(0f, 0.4571f), new(0.2424f, 0.4571f), new(0.2424f, 0.6857f), new(0f, 0.6857f),
            White);
    }
}

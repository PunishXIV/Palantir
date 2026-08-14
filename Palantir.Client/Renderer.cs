using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using Palantir.Common;
using Pictomancy;

namespace Palantir;

public sealed class Renderer(
    Configuration config,
    DeepDungeon dungeon,
    IObjectTable objects,
    IGameGui gui,
    IDalamudPluginInterface plugin,
    IPluginLog log) : IDisposable
{
    private const float Radius = 1.6f;

    private const float BodyAlpha = 0.4f;

    private static readonly Vector3 LabelOffset = new(0, -1.05f, 0);

    private static readonly Vector2 LabelPadding = new(4f, 2f);

    private const float BackdropAlpha = 0.55f;

    private bool _failed;

    private readonly HashSet<(int, int, int)> _trapCells = [];
    private readonly HashSet<(int, int, int)> _hoardCells = [];
    
    private readonly Dictionary<Guid, string> _vfxKeys = [];

    public void Start() => plugin.UiBuilder.Draw += Draw;

    public void Dispose() => plugin.UiBuilder.Draw -= Draw;

    private void Draw()
    {
        try
        {
            DrawMarkers();
            _failed = false;
        }
        catch (Exception ex)
        {
            if (_failed) return;
            _failed = true;
            log.Error(ex, "Rendering failed. Report this to Pictomancy dev?");
        }
    }

    private void DrawMarkers()
    {
        if (objects.LocalPlayer is not { } player)
            return;

        using var draw = PctService.Draw();
        if (draw == null)
            return;

        DrawMarkerLayer(draw, player.Position);
        DrawCofferLayer(draw, player.Position);
        DrawLabels(player.Position);
    }

    private void DrawMarkerLayer(PctDrawList draw, Vector3 eye)
    {
        var traps = config.Traps;
        var hoards = config.Hoards;
        
        var visible = dungeon.Visible;
        
        var merging = config.MergeTrapHoard
                      && traps is { Enabled: true, Mode: RenderMode.DirectX, Fill: true }
                      && hoards is { Enabled: true, Mode: RenderMode.DirectX }
                      && !dungeon.SafetyActive;

        _trapCells.Clear();
        _hoardCells.Clear();

        if (merging)
        {
            foreach (var m in visible)
                (m.Type == MarkerType.Hoard ? _hoardCells : _trapCells).Add(MarkerId.Normalize(m.X, m.Y, m.Z));
        }

        foreach (var marker in visible)
        {
            var category = marker.Type switch
            {
                MarkerType.Trap => traps,
                MarkerType.Hoard => hoards,
                _ => null,
            };

            if (marker.Type != MarkerType.Debug && category is not { Enabled: true })
                continue;

            if (marker.Type == MarkerType.Trap && dungeon.SafetyActive)
                continue;

            var position = new Vector3(marker.X, marker.Y, marker.Z);
            var range = category?.Distance ?? traps.Distance;
            if (Fade(eye, position, range) is not { } alpha)
                continue;

            var merged = merging && category is not null
                         && (marker.Type == MarkerType.Trap ? _hoardCells : _trapCells)
                             .Contains(MarkerId.Normalize(marker.X, marker.Y, marker.Z));
            
            if (merged && marker.Type == MarkerType.Hoard)
                continue;

            var (body, ring) = (marker.Type, merged) switch
            {
                (MarkerType.Debug, _) => (new Vector3(0f, 1f, 0f), Vector3.One),
                (MarkerType.Trap, true) => (traps.Colour, hoards.Colour),
                _ => (category!.Colour, category!.Colour),
            };

            if (category is { Mode: RenderMode.VFX })
                PctService.VfxRenderer.AddCircle(VfxKey(marker.Id), position, Radius, new Vector4(body, alpha));
            else
                Ring(draw, position, body, ring, alpha, category?.Fill ?? true);

            if (config.DrawDebugInfo)
            {
                var suffix = marker.Local ? " (L)" : "";

                draw.AddText(
                    position + new Vector3(0, -1.4f, 0),
                    Pack(new Vector4(1f, 1f, 1f, alpha)),
                    $"{marker.Id}\nTerritoryType {marker.Territory}\nX {marker.X}\nY {marker.Y}\nZ {marker.Z}\nIntegrity: {marker.Confirmations}{suffix}",
                    1f);
            }
        }
    }

    private (RenderCategory Category, bool Enabled, bool Label, string Name) Settings(CofferKind kind) =>
        kind switch
        {
            CofferKind.Bronze => (config.BronzeCoffers, config.BronzeCoffers.Enabled, config.BronzeCoffers.Label, "Bronze Coffer"),
            CofferKind.Silver => (config.SilverCoffers, config.SilverCoffers.Enabled, config.SilverCoffers.Label, "Silver Coffer"),
            CofferKind.Gold => (config.GoldCoffers, config.GoldCoffers.Enabled, config.GoldCoffers.Label, "Gold Coffer"),
            _ => (config.Traps, config.MimicCoffers, config.MimicLabel, "Mimic"),
        };

    private void DrawCofferLayer(PctDrawList draw, Vector3 eye)
    {
        foreach (var coffer in dungeon.Coffers)
        {
            var (category, enabled, _, _) = Settings(coffer.Kind);
            if (!enabled)
                continue;

            if (Fade(eye, coffer.Position, category.Distance) is not { } alpha)
                continue;

            Ring(draw, coffer.Position, category.Colour, category.Colour, alpha);
        }
    }

    private void DrawLabels(Vector3 eye)
    {
        var traps = config.Traps;

        if (traps is { Enabled: true, Label: true } && !dungeon.SafetyActive)
        {
            foreach (var revealed in dungeon.Revealed)
                if (Fade(eye, revealed.Position, traps.Distance) is { } alpha)
                    Label(revealed.Position, revealed.Name, alpha);
        }

        foreach (var coffer in dungeon.Coffers)
        {
            var (category, enabled, label, name) = Settings(coffer.Kind);
            if (!enabled || !label)
                continue;

            if (Fade(eye, coffer.Position, category.Distance) is { } alpha)
                Label(coffer.Position, name, alpha);
        }

        if (config.Hoards is { Enabled: true, Label: true })
        {
            foreach (var hoard in dungeon.Hoards)
                if (Fade(eye, hoard, config.Hoards.Distance) is { } alpha)
                    Label(hoard, "Accursed Hoard", alpha);
        }
    }

    private void Label(Vector3 world, string text, float alpha)
    {
        if (!gui.WorldToScreen(world + LabelOffset, out var screen))
            return;

        var draw = ImGui.GetBackgroundDrawList();
        var size = ImGui.CalcTextSize(text);
        var origin = new Vector2(screen.X - (size.X / 2f), screen.Y);

        draw.AddRectFilled(
            origin - LabelPadding,
            origin + size + LabelPadding,
            Pack(new Vector4(0f, 0f, 0f, alpha * BackdropAlpha)),
            3f);

        draw.AddText(origin, Pack(new Vector4(1f, 1f, 1f, alpha)), text);
    }
    
    private static float? Fade(Vector3 eye, Vector3 target, int range)
    {
        var distance = Vector3.Distance(eye, target);
        return !(distance <= range) ? null : Math.Clamp((range - distance) / 10f, 0f, 1f);
    }

    private string VfxKey(Guid id) =>
        _vfxKeys.TryGetValue(id, out var key) ? key : _vfxKeys[id] = id.ToString();

    private static void Ring(PctDrawList draw, Vector3 position, Vector3 body, Vector3 ring, float alpha, bool fill = true)
    {
        if (fill)
            draw.AddCircleFilled(position, Radius, Pack(new Vector4(body, alpha * BodyAlpha)));

        draw.AddCircle(position, Radius, Pack(new Vector4(ring, alpha)));
    }

    // stolen from Ice, packs RGBA into 0xAABBGGRR
    private static uint Pack(Vector4 color) =>
        ((uint)(Math.Clamp(color.W, 0f, 1f) * 255) << 24) |
        ((uint)(Math.Clamp(color.Z, 0f, 1f) * 255) << 16) |
        ((uint)(Math.Clamp(color.Y, 0f, 1f) * 255) << 8) |
        (uint)(Math.Clamp(color.X, 0f, 1f) * 255);
}

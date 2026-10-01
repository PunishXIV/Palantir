using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using Lumina.Data.Parsing.Scd;
using Palantir.Common;
using Palantir.MobInformation;
using Pictomancy;
using System.Numerics;
using System.Reflection;
using static Palantir.MobInformation.MobDatabase;

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
            MobDatabase.RegisterMobInfo();
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
        DrawLandmarks(draw, player.Position);
        DrawMobs(draw, player.Position);
        DrawCofferLayer(draw, player.Position);
        DrawLabels(player.Position);
    }

    private void DrawMarkerLayer(PctDrawList draw, Vector3 eye)
    {
        var traps = config.Traps;
        var hoards = config.Hoards;
        
        var visible = dungeon.Visible;

        var merging = config.MergeTrapHoard
                      && traps is { Enabled: true, Fill: true }
                      && hoards is { Enabled: true }
                      && traps.Mode == hoards.Mode
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
            {
                // string pulse_white = "m0238_white_o0x1";
                string solid_light = "k5d1_omen_o01pg";
                // string sandy_effect = "m0506en_o0f";

                if (merged)
                {

                    // Donut sizing things, it needs to scale properly to render
                    // All of these are currently based on the ring itself being 1.6f, if we end up changing it, inner donut will need to be adjusted
                    // have some values below in case you want to test it out yourself in the future to see if it would look better...

                    // If you want something bigger - 1.073
                    // Best / most "fitting" - 1.214 
                    // Small - 1.43

                    PctService.VfxRenderer.AddOmen(VfxKey(marker.Id), solid_light, position, new(1.6f), 0, new Vector4(CompensateVfxColor(body), alpha));
                    PctService.VfxRenderer.AddDonut($"{VfxKey(marker.Id)}_Donut", position, 1.214f, 1.6f, new Vector4(ring, alpha));
                }
                else
                {
                    PctService.VfxRenderer.AddOmen(VfxKey(marker.Id), solid_light, position, new(Radius), 0, new Vector4(CompensateVfxColor(body), alpha));
                }
            }
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

    private (RenderCategory Category, bool Enabled, bool Label, string Name) Settings(LandmarkKind kind) =>
        kind switch
        {
            LandmarkKind.Passage => (config.Passage, config.Passage.Enabled, config.Passage.Label, "Passage"),
            LandmarkKind.Return => (config.Return, config.Return.Enabled, config.Return.Label, "Return"),
            LandmarkKind.Votife => (config.Votife, config.Votife.Enabled, config.Votife.Label, "Candelabra"),
            _ => (config.Passage, false, false, "???")
        };

    private (MobCategory Category, bool Enabled, bool Label) Settings(AggroType type)
        => type switch
        {
            AggroType.Sight => (config.SightMobs, config.SightMobs.Enabled, config.SightMobs.Label),
            AggroType.Sound => (config.SoundMobs, config.SoundMobs.Enabled, config.SoundMobs.Label),
            AggroType.Proximity => (config.ProximityMobs, config.ProximityMobs.Enabled, config.ProximityMobs.Label),
            _ => (config.SightMobs, false, false)
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
    private void DrawLandmarks(PctDrawList draw, Vector3 eye)
    {
        foreach (var landmark in dungeon.Landmarks)
        {
            var (category, enabled, _, _) = Settings(landmark.Kind);
            if (!enabled)
                continue;

            if (Fade(eye, landmark.position, category.Distance) is not { } alpha)
                continue;

            Ring(draw, landmark.position, category.Colour, category.Colour, alpha);
        }
    }
    private void DrawMobs(PctDrawList draw, Vector3 eye)
    {
        var territory = Plugin.ClientState.TerritoryType;
        foreach (var mob in dungeon.Mobs)
        {
            if (mob.inCombat)
                continue;

            if (MobDatabase.MobInformation.TryGetValue(mob.BnpcId, out var mobInfo))
            {
                // This ONLY matters for mimics... annoyingly. They have a different aggro range in PotD than any other DD. AND ONLY THEM
                HashSet<uint> PalaceOfTheDeadMapIds = new()
                {
                    561, 562, 563, 564, 565, 593, 594, 595, 596, 597, 598, 599, 600, 601, 602, 603, 604, 605, 606, 607
                };

                var aggroInfo = mobInfo.GetAggroInfo(territory);
                var aggroType = aggroInfo.AggroType;
                var enemyType = mobInfo.MobType;

                float aggroRange = enemyType is ESPType.Mimic && PalaceOfTheDeadMapIds.Contains(territory) ? 14 : 10;
                var hitbox = mob.hitbox;
                var position = mob.position;
                float size = hitbox + aggroRange;

                if (aggroType is AggroType.Sight && config.SightMobs.Enabled)
                {
                    var sightInfo = config.SightMobs;
                    const float zoneRadian = 1.571f;
                    var rotation = -mob.rotation;
                    float halfAngle = zoneRadian / 2f;
                    float minAngle = rotation - halfAngle;
                    float maxAngle = rotation + halfAngle;

                    if (Fade(eye, mob.position, sightInfo.Distance, sightInfo.Colour) is not { } colour)
                        continue;

                    if (sightInfo.Mode is RenderMode.DirectX)
                        draw.AddConeFilled(mob.position, size, rotation, zoneRadian, ImGui.ColorConvertFloat4ToU32(colour));
                    else
                        PctService.VfxRenderer.AddFan($"{mob.entityId}_{mob.baseId}", mob.position, 0f, size, minAngle, maxAngle, colour);
                }
                else if (aggroType is AggroType.Sound && config.SoundMobs.Enabled)
                {
                    var soundInfo = config.SoundMobs;

                    if (Fade(eye, mob.position, soundInfo.Distance, soundInfo.Colour) is not { } colour)
                        continue;

                    if (soundInfo.Mode is RenderMode.DirectX)
                        draw.AddCircleFilled(mob.position, size, ImGui.ColorConvertFloat4ToU32(colour));
                    else if (soundInfo.Mode is RenderMode.VFX)
                    {
                        if (soundInfo.Type is VfxType.Pulse)
                        {
                            PctService.VfxRenderer.AddCircle($"{mob.baseId}_{mob.entityId}", mob.position, size, color: colour);
                        }
                        else if (soundInfo.Type is VfxType.Static)
                        {
                            string solid_light = "k5d1_omen_o01pg";
                            PctService.VfxRenderer.AddOmen($"{mob.baseId}_{mob.entityId}", solid_light, position, new(size), 0, CompensateVfxColor(colour));
                        }
                        else if (soundInfo.Type is VfxType.Light_Pulse)
                        {
                            string light_pulse = "m0531_light_o0v";
                            PctService.VfxRenderer.AddOmen($"{mob.baseId}_{mob.entityId}", light_pulse, position, new(size), 0, colour);
                        }
                    }
                }
                else if (aggroType is AggroType.Proximity && config.ProximityMobs.Enabled)
                {
                    var proxyInfo = config.ProximityMobs;

                    if (Fade(eye, mob.position, proxyInfo.Distance, proxyInfo.Colour) is not { } colour)
                        continue;

                    if (proxyInfo.Mode is RenderMode.DirectX)
                        draw.AddCircleFilled(mob.position, size, ImGui.ColorConvertFloat4ToU32(colour));
                    else if (proxyInfo.Mode is RenderMode.VFX)
                    {
                        if (proxyInfo.Type is VfxType.Pulse)
                        {
                            PctService.VfxRenderer.AddCircle($"{mob.baseId}_{mob.entityId}", mob.position, size, color: colour);
                        }
                        else if (proxyInfo.Type is VfxType.Static)
                        {
                            string solid_light = "k5d1_omen_o01pg";
                            PctService.VfxRenderer.AddOmen($"{mob.baseId}_{mob.entityId}", solid_light, position, new(size), 0, CompensateVfxColor(colour));
                        }
                        else if (proxyInfo.Type is VfxType.Light_Pulse)
                        {
                            string light_pulse = "m0531_light_o0v";
                            PctService.VfxRenderer.AddOmen($"{mob.baseId}_{mob.entityId}", light_pulse, position, new(size), 0, colour);
                        }
                    }
                }

                bool patrolMob = (aggroInfo.Patrol is { } patrol && patrol);

                if (patrolMob && config.PatrolMobs.Enabled)
                {
                    Vector3 TransformFlat(Vector3 origin, float rotation, float localX, float localZ)
                    {
                        float cos = MathF.Cos(rotation);
                        float sin = MathF.Sin(-rotation);
                        return origin + new Vector3(
                            localX * cos - localZ * sin,
                            0f,
                            localX * sin + localZ * cos
                        );
                    }

                    var color = ImGui.ColorConvertFloat4ToU32(config.PatrolMobs.Colour);

                    float shaftLength = 3;
                    float shaftWidth = 1.2f;
                    float headLength = 1.5f;
                    float headWidth = 3;
                    float thickness = 2;

                    float halfShaftW = shaftWidth / 2f;
                    float halfShaftL = shaftLength / 2f;
                    float halfHeadW = headWidth / 2f;

                    Vector3 T(float x, float z) => TransformFlat(position, mob.rotation, x, z);

                    // 7 points, walked around the perimeter in order (starting at back-left, going clockwise in local space).
                    var backLeft = T(-halfShaftW, -halfShaftL);
                    var backRight = T(halfShaftW, -halfShaftL);
                    var shoulderR = T(halfShaftW, halfShaftL);   // where shaft meets head on the right
                    var headBaseR = T(halfHeadW, halfShaftL);    // head's wide base corner, right
                    var tip = T(0f, halfShaftL + headLength);
                    var headBaseL = T(-halfHeadW, halfShaftL);   // head's wide base corner, left
                    var shoulderL = T(-halfShaftW, halfShaftL);  // where shaft meets head on the left


                    draw.PathLineTo(backLeft);
                    draw.PathLineTo(backRight);
                    draw.PathLineTo(shoulderR);
                    draw.PathLineTo(headBaseR);
                    draw.PathLineTo(tip);
                    draw.PathLineTo(headBaseL);
                    draw.PathLineTo(shoulderL);
                    draw.PathStroke(color, PctStrokeFlags.Closed, thickness);
                }
            }
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

        foreach (var landmark in dungeon.Landmarks)
        {
            var (category, enabled, label, name) = Settings(landmark.Kind);
            if (!enabled || !label)
                continue;

            if (Fade(eye, landmark.position, category.Distance) is { } alpha)
                Label(landmark.position, name, alpha);
        }

        foreach (var mob in dungeon.Mobs)
        {
            if (mob.inCombat)
                continue;

            if (MobDatabase.MobInformation.TryGetValue(mob.BnpcId, out var mobInfo))
            {
                var territory = Plugin.ClientState.TerritoryType;
                var aggroType = mobInfo.GetAggroInfo(territory).AggroType;
                var isPatrol = mobInfo.GetAggroInfo(territory).Patrol;

                var (category, enabled, label) = Settings(aggroType);

                bool patrolMob = config.PatrolMobs.Enabled && config.PatrolMobs.Label && isPatrol;

                if (!enabled || !label)
                    continue;

                if (Fade(eye, mob.position, category.Distance) is { } alpha)
                {
                    string name = config.PatrolMobs.Label && patrolMob ? $"\uE05E {mob.name}" : $"{mob.name}";
                    Label(mob.position, name, alpha);
                }
            }
            else
                continue;
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

    private static Vector4? Fade(Vector3 eye, Vector3 target, int range, Vector4 color)
    {
        var distance = Vector3.Distance(eye, target);
        if (distance > range)
            return null;

        var fade = Math.Clamp((range - distance) / 10f, 0f, 1f);
        color.W *= fade;
        return color;
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

    // The Vfx color trap is heavily blue color (no base red unfort) but it's the best one we got
    // Using this to boost the red value if there ever is any. That way we get a nicer red spectrum vs the muddy red it was originally
    // 3 seems to be the best point? 
    private const float RedBoost = 3.0f;
    private static Vector3 CompensateVfxColor(Vector3 color)
    {
        if (color.X <= 0f)
            return color;

        var dominance = color.X / MathF.Max(color.X, MathF.Max(color.Y, color.Z));
        var boost = 1f + (RedBoost - 1f) * dominance;

        return color with { X = color.X * boost };
    }
    private static Vector4 CompensateVfxColor(Vector4 color)
    {
        if (color.X <= 0f)
            return color;

        var dominance = color.X / MathF.Max(color.X, MathF.Max(color.Y, color.Z));
        var boost = 1f + (RedBoost - 1f) * dominance;

        return color with { X = color.X * boost };
    }
}

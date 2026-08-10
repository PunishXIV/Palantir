using System.Numerics;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Game.Event;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using Palantir.Common;
using ObjectKind = Dalamud.Game.ClientState.Objects.Enums.ObjectKind;
using Vector3 = System.Numerics.Vector3;

namespace Palantir;

public sealed partial class DeepDungeon
{
    private static readonly HashSet<uint> TrapActions =
    [
        6275,  // Landmine
        6276,  // Luring Trap
        6277,  // Enfeebling Trap
        6278,  // Impeding Trap
        6279,  // Toading Trap
        11287, // Odder Trap (HoH)
        32375, // Owling Trap (EO)
        44038, // Fae Trap (PT)
    ];

    private const uint SafetyAction = 6259;
    private const uint SightAction = 6260;
    private const uint IntuitionAction = 6870;

    private unsafe delegate void ActionEffect(uint casterId, Character* caster, Vector3* position,
        ActionEffectHandler.Header* header, ActionEffectHandler.TargetEffects* effects, GameObjectId* targets);
    
    private unsafe void InstallHook()
    {
        try
        {
            _hook = interop.HookFromAddress<ActionEffect>(ActionEffectHandler.Addresses.Receive.Value, Detour);
            _hook.Enable();
            log.Debug("ActionEffectHandler hook initialized.");
        }
        catch (Exception ex)
        {
            _hook?.Dispose();
            _hook = null;

            log.Error(ex, "Could not hook action effects. Palantir will continue to run in a degraded state.");
        }
    }

    public void MarkPlayerPosition() =>
        Detach(Task.Run(async () =>
        {
            var position = await framework.RunOnFrameworkThread(() => objects.LocalPlayer?.Position);
            if (position is { } p) await ReportAsync(p, MarkerType.Debug, Discovery.Marked);
        }));

    public unsafe (byte Floor, byte Hoards, byte Layout, (byte Item, byte Count, bool Active)[] Pomanders) Instance()
    {
        var dungeon = EventFramework.Instance()->GetInstanceContentDeepDungeon();
        if (dungeon == null) return (0, 0, 0, []);

        var pomanders = new (byte, byte, bool)[16];
        for (var i = 0; i < pomanders.Length; i++)
        {
            var item = dungeon->Items[i];
            pomanders[i] = (item.ItemId, item.Count, item.IsActive);
        }

        return (dungeon->Floor, dungeon->HoardCount, dungeon->LayoutInitializationType, pomanders);
    }

#if DEBUG
    public readonly record struct NearbyObject(
        uint BaseId, ObjectKind Kind, string Name, float Distance,
        bool HasModel, byte Targetable, byte EventState, byte Visibility, ulong RenderFlags);
    
    public unsafe IEnumerable<NearbyObject> NearbyObjects()
    {
        var player = objects.LocalPlayer?.Position ?? Vector3.Zero;

        return objects
            .Select(o =>
            {
                var game = (GameObject*)o.Address;
                return new NearbyObject(
                    game->BaseId,
                    o.ObjectKind,
                    o.Name.TextValue,
                    Vector3.Distance(player, o.Position),
                    game->DrawObject != null,
                    (byte)game->TargetableStatus,
                    game->EventState,
                    game->Visibility,
                    (ulong)game->RenderFlags);
            })
            .Where(o => o.Distance < 50f)
            .OrderBy(o => o.Distance);
    }
#endif

    private void OnUpdate(IFramework _)
    {
        if (!condition[ConditionFlag.InDeepDungeon])
            return;

        var floor = ReadFloor();

        if (floor != 0 && floor != _floor)
        {
            _floor = floor;
            ResetFloor();
        }

        if (++_tick % 8 == 0)
            Scan();
    }

    private unsafe void Scan()
    {
        List<LiveCoffer> coffers = [];
        List<Vector3> hoards = [];
        var revealedChanged = false;

        foreach (var obj in objects)
        {
            var game = (GameObject*)obj.Address;
            var baseId = game->BaseId;
            var isHoard = DungeonObjects.HoardObjects.Contains(baseId);

            if (CofferAt(obj, baseId) is { } coffer)
                coffers.Add(new LiveCoffer(obj.Position, coffer));
            else if (isHoard)
                hoards.Add(obj.Position);

            var cell = MarkerId.Normalize(obj.Position.X, obj.Position.Y, obj.Position.Z);
            var key = (baseId, cell.X, cell.Y, cell.Z);

            if (_scanned.Contains(key))
                continue;

            if (_sight && DungeonObjects.TrapVfx.TryGetValue(baseId, out var name))
            {
                _scanned.Add(key);
                _revealedWork[cell] = new RevealedTrap(obj.Position, name);
                revealedChanged = true;
                Detach(ReportAsync(obj.Position, MarkerType.Trap, Discovery.Revealed));
            }
            else if (isHoard)
            {
                _scanned.Add(key);
                Detach(ReportAsync(obj.Position, MarkerType.Hoard, Discovery.Revealed));
            }
        }

        _coffers = [.. coffers];
        _hoards = [.. hoards];

        if (revealedChanged)
        {
            _revealed = [.. _revealedWork.Values];
            Invalidate();
        }
    }

    private static CofferKind? CofferAt(IGameObject obj, uint baseId) => baseId switch
    {
        DungeonObjects.MimicCoffer => CofferKind.Mimic,
        DungeonObjects.GoldCoffer => CofferKind.Gold,
        DungeonObjects.SilverCoffer => CofferKind.Silver,
        _ when obj.ObjectKind == ObjectKind.Treasure && DungeonObjects.BronzeCoffers.Contains(baseId)
            => CofferKind.Bronze,
        _ => null,
    };

    private unsafe void Detour(uint casterId, Character* caster, Vector3* position,
        ActionEffectHandler.Header* header, ActionEffectHandler.TargetEffects* effects, GameObjectId* targets)
    {
        try
        {
            if (header != null && condition[ConditionFlag.InDeepDungeon])
            {
                var action = header->ActionId;

                if (TrapActions.Contains(action))
                {
                    Vector3 source = caster != null ? caster->GameObject.Position : Vector3.Zero;
                    if (source == Vector3.Zero && position != null)
                        source = *position;

                    if (source != Vector3.Zero)
                        Detach(ReportAsync(source, MarkerType.Trap, Discovery.Triggered));
                }
                else if (action == SafetyAction)
                {
                    _safety = true;
                    Invalidate();
                }
                else if (action == SightAction)
                {
                    _sight = true;
                    Invalidate();
                }
            }
        }
        catch (Exception ex)
        {
            log.Error(ex, "Action effect handling failed.");
        }

        _hook!.Original(casterId, caster, position, header, effects, targets);
    }

    private unsafe byte ReadFloor()
    {
        if (!condition[ConditionFlag.InDeepDungeon])
            return 0;

        var dungeon = EventFramework.Instance()->GetInstanceContentDeepDungeon();
        return dungeon != null ? dungeon->Floor : (byte)0;
    }

    private void ResetFloor()
    {
        _safety = false;
        _sight = false;
        _revealedWork.Clear();
        _scanned.Clear();
        _revealed = [];
        _coffers = [];
        _hoards = [];
        Invalidate();
    }
}

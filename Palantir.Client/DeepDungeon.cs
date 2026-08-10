using System.Numerics;
using System.Runtime.CompilerServices;
using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using Palantir.Common;
using Vector3 = System.Numerics.Vector3;

namespace Palantir;

public readonly record struct RevealedTrap(Vector3 Position, string Name);

public enum CofferKind { Bronze, Silver, Gold, Mimic }

public readonly record struct LiveCoffer(Vector3 Position, CofferKind Kind);

public enum Discovery
{
    Triggered,
    Revealed,
    Marked,
}

public sealed partial class DeepDungeon(
    Configuration config,
    Storage storage,
    Network network,
    IClientState clientState,
    ICondition condition,
    IObjectTable objects,
    IFramework framework,
    IGameInteropProvider interop,
    IPluginLog log) : IDisposable
{
    private readonly Dictionary<Guid, MarkerRow> _markers = [];

    private readonly Dictionary<(int X, int Y, int Z), RevealedTrap> _revealedWork = [];
    private readonly HashSet<(uint BaseId, int X, int Y, int Z)> _scanned = [];

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly CancellationTokenSource _shutdown = new();

    private Hook<ActionEffect>? _hook;
    private CancellationTokenSource? _transition;
    private MarkerRow[] _visible = [];
    private (int Trap, int Hoard) _thresholds = (-1, -1);
    private volatile bool _stale = true;
    private volatile ushort _territory;

    private volatile RevealedTrap[] _revealed = [];
    private volatile LiveCoffer[] _coffers = [];
    private volatile Vector3[] _hoards = [];

    private byte _floor;
    private bool _safety;
    private bool _sight;
    private int _tick;
    private bool _started;

    public byte Floor => _floor;
    public bool SafetyActive => _safety;
    public bool HookActive => _hook is not null;
    public bool InDeepDungeon => Territories.IsDeepDungeon(_territory);
    public IReadOnlyList<RevealedTrap> Revealed => _revealed;
    public IReadOnlyList<LiveCoffer> Coffers => _coffers;

    public IReadOnlyList<Vector3> Hoards => _hoards;

    public IReadOnlyList<MarkerRow> Visible
    {
        get
        {
            lock (_markers)
            {
                var thresholds = (Trap: config.Traps.Integrity, Hoard: config.Hoards.Integrity);

                if (!_stale && _thresholds == thresholds)
                    return _visible;

                _thresholds = thresholds;
                _stale = false;

                IEnumerable<MarkerRow> query = _markers.Values.Where(m => m.Local ||
                    m.Confirmations >= (m.Type == MarkerType.Hoard ? thresholds.Hoard : thresholds.Trap));
                
                var published = _revealed;

                if (_sight && published.Length > 0)
                {
                    var revealed = published
                        .Select(r => MarkerId.For(_territory, MarkerType.Trap, r.Position.X, r.Position.Y, r.Position.Z))
                        .ToHashSet();

                    query = query.Where(m => m.Type != MarkerType.Trap || revealed.Contains(m.Id));
                }

                return _visible = query.ToArray();
            }
        }
    }

    public void Start()
    {
        if (_started) return;
        _started = true;

        InstallHook();

        network.MarkerReceived += OnMarkerReceived;
        network.OnConnected = OnConnectedAsync;
        clientState.TerritoryChanged += OnTerritoryChanged;
        framework.Update += OnUpdate;

        if (clientState.IsLoggedIn)
            OnTerritoryChanged(clientState.TerritoryType);
    }

    public void Stop()
    {
        if (!_started) return;
        _started = false;

        _hook?.Disable();
        network.MarkerReceived -= OnMarkerReceived;
        network.OnConnected = null;
        clientState.TerritoryChanged -= OnTerritoryChanged;
        framework.Update -= OnUpdate;
    }

    public void Dispose()
    {
        Stop();
        _hook?.Dispose();

        try { _shutdown.Cancel(); } catch (ObjectDisposedException) { }
        _shutdown.Dispose();
        _gate.Dispose();
    }

    private void OnTerritoryChanged(uint territory) => Detach(TransitionAsync((ushort)territory));

    private async Task TransitionAsync(ushort territory)
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token);
        try { Interlocked.Exchange(ref _transition, cts)?.Cancel(); } catch (ObjectDisposedException) { }

        try
        {
            await _gate.WaitAsync(cts.Token);
            try
            {
                _territory = territory;
                await framework.RunOnFrameworkThread(() =>
                {
                    _floor = ReadFloor();
                    ResetFloor();
                });

                lock (_markers)
                {
                    _markers.Clear();
                    Invalidate();
                }

                await Tell(() => network.SetInDungeon(InDeepDungeon));

                if (!InDeepDungeon)
                {
                    await Tell(network.Unsubscribe);
                    return;
                }

                foreach (var row in await storage.ForTerritory(territory))
                    Store(row);

                await SyncAsync(territory, cts.Token);
            }
            finally
            {
                _gate.Release();
            }
        }
        catch (OperationCanceledException)
        {
            // superseded
        }
        finally
        {
            Interlocked.CompareExchange(ref _transition, null, cts);
            cts.Dispose();
        }
    }

    private async Task SyncAsync(ushort territory, CancellationToken ct)
    {
        if (!network.IsConnected || !Territories.IsDeepDungeon(territory))
            return;

        try
        {
            var markers = await network.Subscribe(territory);
            if (_territory != territory)
                return;

            var rows = new List<MarkerRow>();
            var added = 0;

            foreach (var marker in markers)
            {
                var merged = Merge(marker, local: false, synced: true);
                if (merged.Added) added++;
                if (merged.Changed) rows.Add(merged.Row);
            }

            log.Information("Received {Count} marker(s) for territory {Territory}: {New} new, {Known} already known.",
                markers.Length, territory, added, markers.Length - added);

            await storage.Save(rows);
        }
        catch (Exception ex)
        {
            log.Error(ex, "Could not fetch markers for territory {Territory}.", territory);
            return;
        }

        await FlushAsync(territory, ct);
    }

    private async Task FlushAsync(ushort territory, CancellationToken ct)
    {
        var pending = await storage.Unsynced(territory);
        var known = pending.Select(r => r.Id).ToHashSet();
        pending.AddRange(UnsyncedInMemory(territory).Where(m => !known.Contains(m.Id)));

        if (pending.Count == 0)
            return;

        var sent = new List<MarkerRow>();
        foreach (var row in pending)
        {
            if (ct.IsCancellationRequested || !network.IsConnected)
                break;

            try
            {
                var stored = await network.Report(row.ToMarker());

                sent.Add(Merge(stored, local: true, synced: true).Row);
            }
            catch (Exception ex)
            {
                var (qx, qy, qz) = MarkerId.Normalize(row.X, row.Y, row.Z);
                log.Warning(ex, "Could not upload {Kind:l} in territory {Territory} at {X},{Y},{Z}; leaving it queued.",
                    MarkerType.Name(row.Type), row.Territory, qx, qy, qz);
                break;
            }
        }

        if (sent.Count > 0)
        {
            log.Information("Uploaded {Count} queued discovery(s) for territory {Territory}.", sent.Count, territory);
            await storage.Save(sent);
        }
    }

    private List<MarkerRow> UnsyncedInMemory(ushort territory)
    {
        lock (_markers)
            return _markers.Values.Where(m => !m.Synced && m.Territory == territory).ToList();
    }

    private async Task ReportAsync(Vector3 position, byte type, Discovery discovery)
    {
        var territory = _territory;
        if (type != MarkerType.Debug && !Territories.IsDeepDungeon(territory))
            return;

        if (!MarkerId.IsValid(position.X, position.Y, position.Z))
        {
            log.Warning("Ignoring {Kind:l} at unusable position {X},{Y},{Z} in territory {Territory}.",
                MarkerType.Name(type), position.X, position.Y, position.Z, territory);
            return;
        }

        var id = MarkerId.For(territory, type, position.X, position.Y, position.Z);
        var (nx, ny, nz) = MarkerId.Normalize(position.X, position.Y, position.Z);
        var kind = MarkerType.Name(type);
        var verb = discovery.ToString().ToLowerInvariant();

        bool known;
        lock (_markers)
        {
            known = _markers.TryGetValue(id, out var existing);
            if (known && existing is { Local: true, Synced: true })
            {
                log.Debug("Already-reported {Kind:l} {Verb:l} in territory {Territory} at {X},{Y},{Z}.",
                    kind, verb, territory, nx, ny, nz);
                return;
            }
        }

        var marker = new Marker(id, territory, type, position.X, position.Y, position.Z);
        var uploaded = false;

        if (MarkerType.IsPersistable(type))
        {
            if (network.IsConnected)
            {
                try
                {
                    marker = await network.Report(marker);
                    uploaded = true;
                }
                catch (Exception ex)
                {
                    log.Warning(ex, "Could not upload {Kind:l} in territory {Territory} at {X},{Y},{Z}; queued for retry.",
                        kind, territory, nx, ny, nz);
                }
            }

            var state = known ? "Existing" : "New";

            if (uploaded)
                log.Information(
                    "{State:l} {Kind:l} {Verb:l} in territory {Territory} at {X},{Y},{Z}; submitted, {Confirmations} confirmation(s).",
                    state, kind, verb, territory, nx, ny, nz, marker.Confirmations);
            else if (storage.Available)
                log.Information(
                    "{State:l} {Kind:l} {Verb:l} in territory {Territory} at {X},{Y},{Z}; queued until the next connection.",
                    state, kind, verb, territory, nx, ny, nz);
            else
                log.Information(
                    "{State:l} {Kind:l} {Verb:l} in territory {Territory} at {X},{Y},{Z}; held in memory, and lost if you leave this floorset before reconnecting.",
                    state, kind, verb, territory, nx, ny, nz);
        }

        var merged = Merge(marker, local: true, synced: uploaded);

        if (type != MarkerType.Debug && merged.Changed)
            await storage.Save([merged.Row]);
    }

    private void OnMarkerReceived(Marker marker)
    {
        if (marker.Territory != _territory)
            return;

        var merged = Merge(marker, local: false, synced: true);
        if (merged.Changed)
            Detach(storage.Save([merged.Row]));
    }

    private async Task OnConnectedAsync()
    {
        await _gate.WaitAsync(_shutdown.Token);
        try
        {
            await Tell(() => network.SetInDungeon(InDeepDungeon));
            await SyncAsync(_territory, _shutdown.Token);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task Tell(Func<Task> call)
    {
        if (!network.IsConnected)
            return;

        try
        {
            await call();
        }
        catch (Exception ex)
        {
            log.Debug("Server call failed: {Error:l}", ex.Message);
        }
    }

    private readonly record struct Merged(MarkerRow Row, bool Added, bool Changed);

    private Merged Merge(Marker marker, bool local, bool synced)
    {
        lock (_markers)
        {
            if (marker.Territory != _territory)
                return new Merged(Row(marker, local, synced), false, true);

            if (!_markers.TryGetValue(marker.Id, out var row))
            {
                _markers[marker.Id] = row = Row(marker, local, synced);
                Invalidate();
                return new Merged(row, true, true);
            }

            var changed = marker.Confirmations > row.Confirmations
                          || (local && (!row.Local || row.Synced != synced));

            row.Confirmations = Math.Max(row.Confirmations, marker.Confirmations);
            row.Local |= local;

            if (local) row.Synced = synced;

            if (changed) Invalidate();
            return new Merged(row, false, changed);
        }
    }

    private static MarkerRow Row(Marker marker, bool local, bool synced) => new()
    {
        Id = marker.Id,
        Territory = marker.Territory,
        Type = marker.Type,
        X = marker.X, Y = marker.Y, Z = marker.Z,
        Confirmations = marker.Confirmations,
        Local = local,
        Synced = !local || synced,
    };

    private void Store(MarkerRow row)
    {
        lock (_markers)
        {
            _markers[row.Id] = row;
            Invalidate();
        }
    }

    private void Invalidate() => _stale = true;

    private void Detach(Task task, [CallerMemberName] string origin = "") =>
        _ = task.ContinueWith(
            faulted => log.Error(faulted.Exception!, "Unhandled failure in {Origin:l}.", origin),
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
}

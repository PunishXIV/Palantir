using Dalamud.Plugin.Services;
using Microsoft.EntityFrameworkCore;
using Palantir.Common;

namespace Palantir;

public class MarkerRow
{
    public Guid Id { get; set; }
    public ushort Territory { get; set; }
    public byte Type { get; set; }
    public float X { get; set; }
    public float Y { get; set; }
    public float Z { get; set; }
    public int Confirmations { get; set; }
    
    public bool Local { get; set; }
    public bool Synced { get; set; }

    public Marker ToMarker() => new(Id, Territory, Type, X, Y, Z, Confirmations);
}

public sealed class Storage(string path, IPluginLog log) : IDisposable
{
    private readonly DbContextOptions<MarkerDb> _options =
        new DbContextOptionsBuilder<MarkerDb>().UseSqlite($"Data Source={path}").Options;

    private readonly SemaphoreSlim _write = new(1, 1);

    public bool Available { get; private set; }

    public void Initialize()
    {
        try
        {
            using var db = new MarkerDb(_options);
            db.Database.EnsureCreated();
            db.Database.ExecuteSqlRaw("PRAGMA journal_mode=WAL;");
            Available = true;
        }
        catch (Exception ex)
        {
            log.Error(ex, "Could not open the local marker database; running without a local cache.");
        }
    }

    public async Task<List<MarkerRow>> ForTerritory(ushort territory)
    {
        if (!Available) return [];
        await using var db = new MarkerDb(_options);
        return await db.Markers.Where(m => m.Territory == territory).ToListAsync();
    }

    public async Task<List<MarkerRow>> Unsynced(ushort territory)
    {
        if (!Available) return [];
        await using var db = new MarkerDb(_options);
        return await db.Markers.Where(m => !m.Synced && m.Territory == territory).ToListAsync();
    }

    public async Task Save(IReadOnlyCollection<MarkerRow> rows)
    {
        if (!Available || rows.Count == 0) return;

        await _write.WaitAsync();
        try
        {
            await using var db = new MarkerDb(_options);
            var ids = rows.Select(r => r.Id).ToList();
            var existing = await db.Markers.Where(m => ids.Contains(m.Id)).ToDictionaryAsync(m => m.Id);

            foreach (var row in rows)
            {
                if (existing.TryGetValue(row.Id, out var stored))
                    db.Entry(stored).CurrentValues.SetValues(row);
                else
                    db.Markers.Add(row);
            }

            await db.SaveChangesAsync();
        }
        catch (Exception ex)
        {
            log.Error(ex, "Failed to save {Count} markers.", rows.Count);
        }
        finally
        {
            _write.Release();
        }
    }

    public void Dispose() => _write.Dispose();

    internal class MarkerDb(DbContextOptions<MarkerDb> options) : DbContext(options)
    {
        public DbSet<MarkerRow> Markers => Set<MarkerRow>();

        protected override void OnModelCreating(ModelBuilder model) =>
            model.Entity<MarkerRow>().HasIndex(m => m.Territory);
    }
}

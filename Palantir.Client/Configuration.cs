using System.Numerics;
using System.Security.Cryptography;
using Dalamud.Configuration;
using Dalamud.Plugin;
using Newtonsoft.Json;

namespace Palantir;

public enum RenderMode { DirectX, VFX }
public enum VfxType { Pulse, Light_Pulse, Static }

public class RenderCategory
{
    public bool Enabled { get; set; }
    public int Distance { get; set; } = 100;
    public Vector3 Colour { get; set; }
    public RenderMode Mode { get; set; }

    public int Integrity { get; set; } = 4;

    public bool Label { get; set; }

    public bool Fill { get; set; } = true;
}

public class MobCategory
{
    public bool Enabled { get; set; }
    public int Distance { get; set; } = 60;
    public Vector4 Colour { get; set; }
    public RenderMode Mode { get; set; } = RenderMode.DirectX;
    public bool Label { get; set; } = true;
    public VfxType Type { get; set; } = VfxType.Pulse;
}

public class Configuration : IPluginConfiguration
{
    public const string DefaultServer = "https://palantir.puni.sh";

    public static IReadOnlyList<string> Pinned { get; } =
#if DEBUG
        [DefaultServer, "http://127.0.0.1:4308"];
#else
        [DefaultServer];
#endif

    private const string Unprotected = "plain:";

    public int Version { get; set; } = 1;

    public List<string> Servers { get; set; } = [];
    public string SelectedServer { get; set; } = DefaultServer;
    public bool AutoConnect { get; set; }

    public bool DrawDebugInfo { get; set; }
    
    public RenderCategory Traps { get; set; } =
        new() { Enabled = true, Colour = new Vector3(1f, 0.16f, 0.16f), Integrity = 4, Label = true }; // #FF2929
    
    public RenderCategory Hoards { get; set; } =
        new() { Colour = new Vector3(1f, 0.84f, 0f), Integrity = 2, Label = true }; // #FFD600

    public RenderCategory Passage { get; set; } = new() { Colour = new Vector3(0.55f, 0.75f, 1.00f), Label = true }; // #8CBFFF
    public RenderCategory Return { get; set; } = new() { Colour = new Vector3(0.60f, 0.90f, 0.60f), Label = true }; // #99E699
    public RenderCategory Votife { get; set; } = new() { Colour = new Vector3(0.75f, 0.60f, 0.95f), Label = true }; // #BF99F2

    public RenderCategory BronzeCoffers { get; set; } = new() { Colour = new Vector3(0.72f, 0.45f, 0.20f) }; // #B87333
    public RenderCategory SilverCoffers { get; set; } = new() { Colour = new Vector3(0.75f, 0.78f, 0.82f) }; // #BFC7D1
    public RenderCategory GoldCoffers { get; set; } = new() { Colour = new Vector3(1f, 0.88f, 0.35f) };      // #FFE059

    public MobCategory SightMobs { get; set; } = new() { Colour = new Vector4(1f, 0f, 0f, 0.5f) };
    public MobCategory ProximityMobs { get; set; } = new() { Colour = new(1f, 0.5333f, 0f, 0.502f) };
    public MobCategory SoundMobs { get; set; } = new() { Colour = new Vector4(0f, 1f, 0.6f, 0.502f) };
    public MobCategory PatrolMobs { get; set; } = new() { Colour = new Vector4(1f, 0.9569f, 0f, 1f), Distance = 100 };

    public bool MimicCoffers { get; set; }

    public bool MimicLabel { get; set; } = true;

    public bool MergeTrapHoard { get; set; } = true;

    public Dictionary<string, string> Accounts { get; set; } = [];

    [JsonIgnore]
    public IReadOnlyList<string> AllServers => [.. Pinned, .. Servers];

    [JsonIgnore] private IDalamudPluginInterface _plugin = null!;
    [JsonIgnore] private readonly Lock _save = new();

    public static Configuration Load(IDalamudPluginInterface plugin)
    {
        Configuration config;
        try
        {
            config = plugin.GetPluginConfig() as Configuration ?? new Configuration();
        }
        catch
        {
            config = new Configuration();
        }

        config._plugin = plugin;

        config.Servers = config.Servers.Where(s => !Pinned.Contains(s)).Distinct().ToList();

        if (!config.AllServers.Contains(config.SelectedServer))
            config.SelectedServer = DefaultServer;

        return config;
    }

    public void Save()
    {
        lock (_save)
            _plugin.SavePluginConfig(this);
    }

    public Guid? GetAccount(string server)
    {
        if (!Accounts.TryGetValue(server, out var stored))
            return null;

        try
        {
            return stored.StartsWith(Unprotected)
                ? Guid.Parse(stored[Unprotected.Length..])
                : new Guid(ProtectedData.Unprotect(Convert.FromBase64String(stored), null, DataProtectionScope.CurrentUser));
        }
        catch
        {
            return null;
        }
    }

    public void SetAccount(string server, Guid account)
    {
        try
        {
            Accounts[server] = Convert.ToBase64String(
                ProtectedData.Protect(account.ToByteArray(), null, DataProtectionScope.CurrentUser));
        }
        catch (PlatformNotSupportedException)
        {
            // no DPAPI under Linux/WINE
            Accounts[server] = Unprotected + account;
        }

        Save();
    }
}

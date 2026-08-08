using System.IO;
using Dalamud.Game.Command;
using Dalamud.Interface.Windowing;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using Palantir.Windows;
using Pictomancy;

namespace Palantir;

public sealed class Plugin : IDalamudPlugin
{
    [PluginService] internal static IDalamudPluginInterface PluginInterface { get; private set; } = null!;
    [PluginService] internal static ICommandManager CommandManager { get; private set; } = null!;
    [PluginService] internal static IClientState ClientState { get; private set; } = null!;
    [PluginService] internal static ICondition Condition { get; private set; } = null!;
    [PluginService] internal static IObjectTable ObjectTable { get; private set; } = null!;
    [PluginService] internal static IGameGui GameGui { get; private set; } = null!;
    [PluginService] internal static IFramework Framework { get; private set; } = null!;
    [PluginService] internal static IGameInteropProvider GameInterop { get; private set; } = null!;
    [PluginService] internal static IPluginLog Log { get; private set; } = null!;

    private const string Command = "/palantir";
    
    private static readonly TimeSpan DisposeBudget = TimeSpan.FromSeconds(5);

    private readonly WindowSystem _windows = new("Palantir");
    private readonly Configuration _config;
    private readonly Storage _storage;
    private readonly Network _network;
    private readonly DeepDungeon _dungeon;
    private readonly Renderer _renderer;
    private readonly MainWindow _main;
    private readonly ConfigWindow _settings;

    public Plugin()
    {
        PctService.Initialize(PluginInterface);

        _config = Configuration.Load(PluginInterface);

        _storage = new Storage(Path.Combine(PluginInterface.ConfigDirectory.FullName, "palantir.db"), Log);
        _storage.Initialize();

        _network = new Network(_config, Log);
        _dungeon = new DeepDungeon(_config, _storage, _network, ClientState, Condition, ObjectTable, Framework, GameInterop, Log);
        _renderer = new Renderer(_config, _dungeon, ObjectTable, GameGui, PluginInterface, Log);

        _settings = new ConfigWindow(_config, _network, Framework);
#if DEBUG
        var debug = new DebugWindow(_config, _network, _storage, _dungeon);
        _main = new MainWindow(_config, _network, _storage, _dungeon, _settings, debug);
        _windows.AddWindow(debug);
#else
        _main = new MainWindow(_config, _network, _storage, _dungeon, _settings);
#endif
        _windows.AddWindow(_settings);
        _windows.AddWindow(_main);

        _dungeon.Start();
        _renderer.Start();
        
        if (_config.AutoConnect)
            _ = _network.EnableAsync(_config.SelectedServer);

        CommandManager.AddHandler(Command, new CommandInfo((_, _) => _main.Toggle())
        {
            HelpMessage = "Opens the Palantir window",
        });

        PluginInterface.UiBuilder.Draw += _windows.Draw;
        PluginInterface.UiBuilder.OpenMainUi += _main.Toggle;
        PluginInterface.UiBuilder.OpenConfigUi += _settings.Toggle;
    }

    public void Dispose()
    {
        PluginInterface.UiBuilder.Draw -= _windows.Draw;
        PluginInterface.UiBuilder.OpenMainUi -= _main.Toggle;
        PluginInterface.UiBuilder.OpenConfigUi -= _settings.Toggle;
        _windows.RemoveAllWindows();
        CommandManager.RemoveHandler(Command);
        
        _renderer.Dispose();
        _dungeon.Dispose();

        if (!_network.DisposeAsync().AsTask().Wait(DisposeBudget))
            Log.Error("didn't dispose properly within {Seconds}s; reload may leak the connection?",
                DisposeBudget.TotalSeconds);

        _storage.Dispose();
        PctService.Dispose();
    }
}

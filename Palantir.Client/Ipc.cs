using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;
using System.Numerics;

namespace Palantir;

public sealed class Ipc(IDalamudPluginInterface plugin, Storage storage) : IDisposable
{
	private readonly ICallGateProvider<ushort, List<Vector3>> _getTraps = plugin.GetIpcProvider<ushort, List<Vector3>>("Palantir.GetTraps");
	private readonly ICallGateProvider<List<Vector3>, object> _updateTrapsEvent = plugin.GetIpcProvider<List<Vector3>, object>("Palantir.WatchTraps");

	public void Start() => _getTraps.RegisterFunc(storage.Traps);

	public void Dispose() => _getTraps.UnregisterFunc();

	public void OnUpdated(List<Vector3> positions) => _updateTrapsEvent.SendMessage(positions);
}

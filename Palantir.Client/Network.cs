using System.Net.Http.Json;
using System.Text.Json;
using Dalamud.Plugin.Services;
using Microsoft.AspNetCore.SignalR.Client;
using Palantir.Common;

namespace Palantir;

public sealed class Network(Configuration config, IPluginLog log) : IAsyncDisposable
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(10) };
    private readonly SemaphoreSlim _gate = new(1, 1);
    private HubConnection? _hub;
    private CancellationTokenSource? _intent;
    private string _server = "";
    private string? _token;
    private DateTime _expires;
    private int _supervising;
    public event Action<Marker>? MarkerReceived;
    public Func<Task>? OnConnected { get; set; }

    public HubConnectionState State => _hub?.State ?? HubConnectionState.Disconnected;
    public bool IsConnected => State == HubConnectionState.Connected;
    public bool IsEnabled => _intent is { IsCancellationRequested: false };
    public bool IsRetrying => _supervising == 1;
    public PresenceStats Presence { get; private set; } = new(0, 0);
    public string? LastError { get; private set; }

    public async Task<ServerMetadata> MetadataAsync(string server, CancellationToken ct = default)
    {
        var metadata = await _http.GetFromJsonAsync<ServerMetadata>($"{Root(server)}/metadata", Json, ct)
                       ?? throw new InvalidOperationException("Server returned no metadata.");

        if (metadata.ProtocolVersion != Protocol.Version)
            throw new InvalidOperationException(
                $"Server speaks protocol v{metadata.ProtocolVersion}, this plugin speaks v{Protocol.Version}. " +
                (metadata.ProtocolVersion > Protocol.Version ? "Update Palantir." : "The server needs updating."));

        return metadata;
    }

    public async Task<string?> EnableAsync(string server)
    {
        await DisableAsync();
        await _gate.WaitAsync();
        try
        {
            _server = server;
            _intent = new CancellationTokenSource();

            await ConnectAsync(_intent.Token);
            return null;
        }
        catch (Exception ex)
        {
            LastError = ex.Message;

            if (IsEnabled) Supervise();
            return ex.Message;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task DisableAsync()
    {
        await _gate.WaitAsync();
        try
        {
            if (_intent is { } intent)
            {
                await intent.CancelAsync();
                intent.Dispose();
                _intent = null;
            }

            if (_hub is { } hub)
            {
                _hub = null;
                await hub.DisposeAsync();
                log.Information("Disconnected from {Server:l}.", _server);
            }

            _token = null;
            Presence = new PresenceStats(0, 0);
        }
        finally
        {
            _gate.Release();
        }
    }

    public Task<Marker[]> Subscribe(ushort territory) => Ready().InvokeAsync<Marker[]>("Subscribe", territory);
    public Task Unsubscribe() => Ready().InvokeAsync("Unsubscribe");
    public Task<Marker> Report(Marker marker) => Ready().InvokeAsync<Marker>("Report", marker);
    public Task SetInDungeon(bool inDungeon) => Ready().InvokeAsync("SetInDungeon", inDungeon);

    public async ValueTask DisposeAsync()
    {
        await DisableAsync();
        _http.Dispose();
        _gate.Dispose();
    }

    private static string Root(string server) => server.TrimEnd('/');

    private HubConnection Ready() => _hub is { State: HubConnectionState.Connected } hub
        ? hub
        : throw new InvalidOperationException("Not connected to a Palantir server.");

    private HubConnection Build(string server)
    {
        var hub = new HubConnectionBuilder()
            .WithUrl($"{Root(server)}/hub", options => options.AccessTokenProvider = TokenAsync)
            .WithAutomaticReconnect(new Forever())
            .Build();

        hub.On<Marker>("ReceiveMarker", marker => MarkerReceived?.Invoke(marker));
        hub.On<PresenceStats>("ReceivePresence", stats => Presence = stats);

        hub.Reconnected += async _ => await AfterConnect();

        hub.Reconnecting += _ =>
        {
            log.Information("Connection to {Server:l} lost; reconnecting.", _server);
            return Task.CompletedTask;
        };

        hub.Closed += _ =>
        {
            if (IsEnabled)
            {
                log.Information("Connection to {Server:l} closed; retrying.", _server);
                Supervise();
            }

            return Task.CompletedTask;
        };

        return hub;
    }

    private async Task ConnectAsync(CancellationToken ct)
    {
        await MetadataAsync(_server, ct);
        _hub ??= Build(_server);
        await _hub.StartAsync(ct);
        await AfterConnect();
    }

    private async Task AfterConnect()
    {
        LastError = null;
        log.Information("Connected to {Server:l}.", _server);
        Presence = await Ready().InvokeAsync<PresenceStats>("GetPresence");

        if (OnConnected is { } handler)
            await handler();
    }

    private void Supervise()
    {
        if (Interlocked.Exchange(ref _supervising, 1) == 0)
            _ = SuperviseAsync();
    }

    private async Task SuperviseAsync()
    {
        var token = _intent?.Token ?? CancellationToken.None;
        try
        {
            for (var attempt = 0; !token.IsCancellationRequested; attempt++)
            {
                await Task.Delay(Backoff(attempt), token);

                try
                {
                    await ConnectAsync(token);
                    return;
                }
                catch (Exception ex) when (!token.IsCancellationRequested)
                {
                    LastError = ex.Message;
                    log.Debug("Connect attempt {Attempt} to {Server:l} failed: {Error:l}", attempt + 1, _server, ex.Message);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // intent was cleared. an explicit disconnect is never undone
        }
        finally
        {
            Interlocked.Exchange(ref _supervising, 0);
        }
    }

    private async Task<string?> TokenAsync()
    {
        // refreshed five minutes early, or an all-but-expired token gets handed to a reconnect
        // and fails the very handshake it was fetched for
        if (_token is not null && DateTime.UtcNow < _expires - TimeSpan.FromMinutes(5))
            return _token;

        var response = await _http.PostAsJsonAsync($"{Root(_server)}/auth", new AuthRequest(config.GetAccount(_server)), Json);

        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException(
                $"Authentication failed ({(int)response.StatusCode}): " +
                (Detail(await response.Content.ReadAsStringAsync()) ?? response.ReasonPhrase));

        var auth = await response.Content.ReadFromJsonAsync<AuthResponse>(Json)
                   ?? throw new InvalidOperationException("Authentication returned no token.");

        config.SetAccount(_server, auth.AccountId);
        _token = auth.Token;
        _expires = Expiry(auth.Token);
        return _token;
    }

    private static string? Detail(string body)
    {
        try
        {
            return JsonDocument.Parse(body).RootElement.TryGetProperty("detail", out var detail)
                ? detail.GetString()
                : null;
        }
        catch
        {
            return null;
        }
    }

    private static DateTime Expiry(string jwt)
    {
        var payload = jwt.Split('.')[1].Replace('-', '+').Replace('_', '/');
        var json = Convert.FromBase64String(payload.PadRight(payload.Length + (4 - payload.Length % 4) % 4, '='));
        return DateTimeOffset.FromUnixTimeSeconds(JsonDocument.Parse(json).RootElement.GetProperty("exp").GetInt64()).UtcDateTime;
    }

    private static TimeSpan Backoff(int attempt) =>
        TimeSpan.FromSeconds(Math.Min(30, Math.Pow(2, Math.Min(attempt, 5)))) * (0.8 + Random.Shared.NextDouble() * 0.4);

    // I need to fix this in the future because the default SignalR reconnect stuff is a bit fucked, it's not elegant
    // maybe I can steal whatever Mare did because it handled retry/reconnect much better
    private sealed class Forever : IRetryPolicy
    {
        public TimeSpan? NextRetryDelay(RetryContext context) => Backoff((int)context.PreviousRetryCount);
    }
}

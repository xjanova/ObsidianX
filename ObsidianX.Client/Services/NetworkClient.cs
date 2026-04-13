using Microsoft.AspNetCore.SignalR.Client;
using ObsidianX.Core.Models;

namespace ObsidianX.Client.Services;

public class NetworkClient : IAsyncDisposable
{
    private HubConnection? _hub;
    private string _serverUrl = "http://localhost:5142/brain-hub";

    public bool IsConnected => _hub?.State == HubConnectionState.Connected;
    public string ServerUrl => _serverUrl;

    // Events
    public event Action<PeerInfo>? PeerJoined;
    public event Action<string>? PeerLeft;
    public event Action<ShareRequest>? ShareRequested;
    public event Action<string, bool, string>? ShareResponseReceived; // fromAddr, accepted, title
    public event Action<string>? StatusChanged;
    public event Action<int>? PeerCountChanged;

    private readonly List<PeerInfo> _peers = [];
    public IReadOnlyList<PeerInfo> Peers => _peers;

    public async Task<bool> ConnectAsync(string serverUrl, PeerInfo myInfo)
    {
        _serverUrl = serverUrl;

        try
        {
            _hub = new HubConnectionBuilder()
                .WithUrl(_serverUrl)
                .WithAutomaticReconnect([TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10)])
                .Build();

            // Register event handlers
            _hub.On<PeerInfo>("PeerJoined", peer =>
            {
                lock (_peers)
                {
                    _peers.RemoveAll(p => p.BrainAddress == peer.BrainAddress);
                    _peers.Add(peer);
                }
                PeerJoined?.Invoke(peer);
                PeerCountChanged?.Invoke(_peers.Count);
            });

            _hub.On<string>("PeerLeft", address =>
            {
                lock (_peers) { _peers.RemoveAll(p => p.BrainAddress == address); }
                PeerLeft?.Invoke(address);
                PeerCountChanged?.Invoke(_peers.Count);
            });

            _hub.On<ShareRequest>("ShareRequested", request =>
            {
                ShareRequested?.Invoke(request);
            });

            _hub.On<dynamic>("ShareResponse", response =>
            {
                string from = response.FromAddress;
                bool accepted = response.Accepted;
                string title = response.NodeTitle;
                ShareResponseReceived?.Invoke(from, accepted, title);
            });

            _hub.Reconnecting += (ex) =>
            {
                StatusChanged?.Invoke("Reconnecting...");
                return Task.CompletedTask;
            };

            _hub.Reconnected += (id) =>
            {
                StatusChanged?.Invoke("Reconnected");
                return Task.CompletedTask;
            };

            _hub.Closed += (ex) =>
            {
                StatusChanged?.Invoke("Disconnected");
                PeerCountChanged?.Invoke(0);
                return Task.CompletedTask;
            };

            await _hub.StartAsync();

            // Register our brain
            await _hub.InvokeAsync("RegisterBrain", myInfo);

            StatusChanged?.Invoke("Connected");
            return true;
        }
        catch (Exception ex)
        {
            StatusChanged?.Invoke($"Connection failed: {ex.Message}");
            return false;
        }
    }

    public async Task DisconnectAsync()
    {
        if (_hub != null)
        {
            await _hub.StopAsync();
            await _hub.DisposeAsync();
            _hub = null;
        }
        lock (_peers) { _peers.Clear(); }
        StatusChanged?.Invoke("Disconnected");
        PeerCountChanged?.Invoke(0);
    }

    public async Task<List<MatchResult>> FindExpertsAsync(MatchRequest request)
    {
        if (_hub == null || !IsConnected) return [];
        return await _hub.InvokeAsync<List<MatchResult>>("FindExperts", request);
    }

    public async Task RequestShareAsync(ShareRequest request)
    {
        if (_hub == null || !IsConnected) return;
        await _hub.InvokeAsync("RequestShare", request);
    }

    public async Task RespondToShareAsync(string fromAddress, bool accepted)
    {
        if (_hub == null || !IsConnected) return;
        await _hub.InvokeAsync("RespondToShare", fromAddress, accepted);
    }

    public async Task<object?> GetNetworkStatsAsync()
    {
        if (_hub == null || !IsConnected) return null;
        return await _hub.InvokeAsync<object>("GetNetworkStats");
    }

    public async ValueTask DisposeAsync()
    {
        if (_hub != null)
        {
            await _hub.DisposeAsync();
        }
    }
}

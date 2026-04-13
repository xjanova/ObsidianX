using Microsoft.AspNetCore.SignalR;
using ObsidianX.Core.Models;

namespace ObsidianX.Server.Hubs;

public class BrainHub : Hub
{
    private static readonly Dictionary<string, PeerInfo> ConnectedPeers = new();
    private static readonly Dictionary<string, string> EndpointToAddress = new();
    private static readonly List<ShareRequest> PendingRequests = [];
    private static readonly List<string> ActivityLog = [];
    private static readonly DateTime StartTime = DateTime.UtcNow;
    private static int _totalShareRequests;

    private const int MaxPendingRequests = 10_000;
    private const int MaxKeywords = 50;
    private const int MaxResults = 100;
    private const int MaxDisplayNameLength = 100;

    public static object GetPeersSnapshot()
    {
        lock (ConnectedPeers)
        {
            return ConnectedPeers.Values.Select(p => new
            {
                p.BrainAddress,
                p.DisplayName,
                Status = p.Status.ToString(),
                p.TotalKnowledgeNodes,
                p.TotalWords,
                TopExpertise = p.ExpertiseScores
                    .OrderByDescending(kv => kv.Value)
                    .Take(3)
                    .Select(kv => new { Category = kv.Key.ToString(), Score = kv.Value })
            }).ToList();
        }
    }

    public static object GetStatsSnapshot()
    {
        List<string> recentActivity;
        lock (ActivityLog)
        {
            recentActivity = ActivityLog.TakeLast(20).Reverse().ToList();
        }

        lock (ConnectedPeers)
        {
            return new
            {
                TotalPeers = ConnectedPeers.Count,
                OnlinePeers = ConnectedPeers.Values.Count(p => p.Status == PeerStatus.Online),
                TotalKnowledge = ConnectedPeers.Values.Sum(p => p.TotalKnowledgeNodes),
                TotalWords = ConnectedPeers.Values.Sum(p => p.TotalWords),
                TotalShareRequests = _totalShareRequests,
                Uptime = (DateTime.UtcNow - StartTime).TotalSeconds,
                RecentActivity = recentActivity
            };
        }
    }

    private static void LogActivity(string message)
    {
        lock (ActivityLog)
        {
            ActivityLog.Add($"[{DateTime.UtcNow:HH:mm:ss}] {message}");
            if (ActivityLog.Count > 100) ActivityLog.RemoveAt(0);
        }
    }

    private static string Sanitize(string? input, int maxLen = 200)
    {
        if (string.IsNullOrWhiteSpace(input)) return "";
        return input.Length > maxLen ? input[..maxLen] : input;
    }

    public async Task RegisterBrain(PeerInfo? peerInfo)
    {
        if (peerInfo == null
            || string.IsNullOrWhiteSpace(peerInfo.BrainAddress)
            || string.IsNullOrWhiteSpace(peerInfo.DisplayName))
        {
            await Clients.Caller.SendAsync("Error", "Invalid peer info: address and name required");
            return;
        }

        peerInfo.DisplayName = Sanitize(peerInfo.DisplayName, MaxDisplayNameLength);
        peerInfo.Status = PeerStatus.Online;
        peerInfo.LastSeen = DateTime.UtcNow;
        peerInfo.Endpoint = Context.ConnectionId;

        int totalPeers;
        lock (ConnectedPeers)
        {
            ConnectedPeers[peerInfo.BrainAddress] = peerInfo;
            EndpointToAddress[Context.ConnectionId] = peerInfo.BrainAddress;
            totalPeers = ConnectedPeers.Count;
        }

        await Clients.All.SendAsync("PeerJoined", peerInfo);
        await Clients.Caller.SendAsync("Registered", new
        {
            Success = true,
            YourAddress = peerInfo.BrainAddress,
            TotalPeers = totalPeers,
            Message = $"Welcome to ObsidianX Network! {totalPeers} brains connected."
        });

        var shortAddr = peerInfo.BrainAddress.Length > 18 ? peerInfo.BrainAddress[..18] + "..." : peerInfo.BrainAddress;
        LogActivity($"Brain joined: {peerInfo.DisplayName} ({shortAddr})");
        Console.WriteLine($"[+] Brain registered: {peerInfo.BrainAddress} ({peerInfo.DisplayName})");
    }

    public async Task<List<MatchResult>> FindExperts(MatchRequest? request)
    {
        if (request == null || string.IsNullOrWhiteSpace(request.RequesterAddress))
            throw new HubException("Invalid match request");

        request.Keywords ??= [];
        if (request.Keywords.Count > MaxKeywords)
            request.Keywords = request.Keywords.Take(MaxKeywords).ToList();
        if (request.MaxResults is < 1 or > MaxResults)
            request.MaxResults = 20;

        List<PeerInfo> peers;
        lock (ConnectedPeers)
        {
            peers = ConnectedPeers.Values
                .Where(p => p.BrainAddress != request.RequesterAddress && p.Status == PeerStatus.Online)
                .ToList();
        }

        var results = peers
            .Select(peer =>
            {
                double score = 0;
                if (peer.ExpertiseScores.TryGetValue(request.DesiredCategory, out var expertise))
                    score = expertise;

                foreach (var keyword in request.Keywords)
                {
                    if (!string.IsNullOrEmpty(keyword) &&
                        peer.DisplayName.Contains(keyword, StringComparison.OrdinalIgnoreCase))
                        score += 0.1;
                }

                return new MatchResult
                {
                    Peer = peer,
                    MatchScore = score,
                    MatchedCategory = request.DesiredCategory,
                    MatchReason = score > 0.7 ? "Expert" : score > 0.4 ? "Knowledgeable" : "Learning"
                };
            })
            .Where(r => r.MatchScore >= request.MinExpertiseScore)
            .OrderByDescending(r => r.MatchScore)
            .Take(request.MaxResults)
            .ToList();

        LogActivity($"Match request: {request.DesiredCategory} — found {results.Count} experts");
        Console.WriteLine($"[?] Match request from {request.RequesterAddress}: " +
                          $"seeking {request.DesiredCategory}, found {results.Count} matches");

        return results;
    }

    public async Task RequestShare(ShareRequest? request)
    {
        if (request == null
            || string.IsNullOrWhiteSpace(request.FromAddress)
            || string.IsNullOrWhiteSpace(request.ToAddress))
        {
            throw new HubException("Invalid share request");
        }

        lock (PendingRequests)
        {
            // Cap pending requests to prevent memory exhaustion
            if (PendingRequests.Count >= MaxPendingRequests)
                PendingRequests.RemoveAll(r => r.Status != ShareStatus.Pending);
            PendingRequests.Add(request);
        }

        Interlocked.Increment(ref _totalShareRequests);

        PeerInfo? target;
        lock (ConnectedPeers)
        {
            ConnectedPeers.TryGetValue(request.ToAddress, out target);
        }

        if (target != null)
        {
            await Clients.Client(target.Endpoint).SendAsync("ShareRequested", request);
            var shortAddr = request.ToAddress.Length > 18 ? request.ToAddress[..18] + "..." : request.ToAddress;
            LogActivity($"Share request: {Sanitize(request.NodeTitle, 60)} → {shortAddr}");
            Console.WriteLine($"[>] Share request: {request.FromAddress} -> {request.ToAddress} ({request.NodeTitle})");
        }
    }

    public async Task RespondToShare(string? fromAddress, bool accepted)
    {
        if (string.IsNullOrWhiteSpace(fromAddress)) return;

        ShareRequest? request;
        lock (PendingRequests)
        {
            request = PendingRequests.FirstOrDefault(r =>
                r.FromAddress == fromAddress && r.Status == ShareStatus.Pending);
            if (request != null)
                request.Status = accepted ? ShareStatus.Accepted : ShareStatus.Rejected;
        }

        if (request != null)
        {
            PeerInfo? requester;
            lock (ConnectedPeers)
            {
                ConnectedPeers.TryGetValue(request.FromAddress, out requester);
            }
            if (requester != null)
            {
                await Clients.Client(requester.Endpoint).SendAsync("ShareResponse", new
                {
                    request.FromAddress,
                    request.ToAddress,
                    Accepted = accepted,
                    request.NodeTitle
                });
            }
        }
    }

    public Task<object> GetNetworkStats()
    {
        lock (ConnectedPeers)
        {
            var categoryStats = ConnectedPeers.Values
                .SelectMany(p => p.ExpertiseScores)
                .GroupBy(kv => kv.Key)
                .ToDictionary(g => g.Key.ToString(), g => new
                {
                    TotalExperts = g.Count(),
                    AvgScore = g.Average(x => x.Value),
                    MaxScore = g.Max(x => x.Value)
                });

            return Task.FromResult<object>(new
            {
                TotalPeers = ConnectedPeers.Count,
                OnlinePeers = ConnectedPeers.Values.Count(p => p.Status == PeerStatus.Online),
                TotalKnowledge = ConnectedPeers.Values.Sum(p => p.TotalKnowledgeNodes),
                TotalWords = ConnectedPeers.Values.Sum(p => p.TotalWords),
                Categories = categoryStats
            });
        }
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        var disconnected = string.Empty;
        lock (ConnectedPeers)
        {
            if (EndpointToAddress.TryGetValue(Context.ConnectionId, out var address))
            {
                EndpointToAddress.Remove(Context.ConnectionId);
                if (ConnectedPeers.TryGetValue(address, out var peer))
                {
                    peer.Status = PeerStatus.Offline;
                    disconnected = address;
                    ConnectedPeers.Remove(address);
                }
            }
        }

        if (!string.IsNullOrEmpty(disconnected))
        {
            await Clients.All.SendAsync("PeerLeft", disconnected);
            var shortAddr = disconnected.Length > 18 ? disconnected[..18] + "..." : disconnected;
            LogActivity($"Brain left: {shortAddr}");
            Console.WriteLine($"[-] Brain disconnected: {disconnected}");
        }

        await base.OnDisconnectedAsync(exception);
    }
}

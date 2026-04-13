using Microsoft.AspNetCore.SignalR;
using ObsidianX.Core.Models;

namespace ObsidianX.Server.Hubs;

public class BrainHub : Hub
{
    private static readonly Dictionary<string, PeerInfo> ConnectedPeers = new();
    private static readonly List<ShareRequest> PendingRequests = [];
    private static readonly List<string> ActivityLog = [];
    private static DateTime _startTime = DateTime.UtcNow;
    private static int _totalShareRequests;

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
        lock (ConnectedPeers)
        {
            return new
            {
                TotalPeers = ConnectedPeers.Count,
                OnlinePeers = ConnectedPeers.Values.Count(p => p.Status == PeerStatus.Online),
                TotalKnowledge = ConnectedPeers.Values.Sum(p => p.TotalKnowledgeNodes),
                TotalWords = ConnectedPeers.Values.Sum(p => p.TotalWords),
                TotalShareRequests = _totalShareRequests,
                Uptime = (DateTime.UtcNow - _startTime).TotalSeconds,
                RecentActivity = ActivityLog.TakeLast(20).Reverse().ToList()
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

    public async Task RegisterBrain(PeerInfo peerInfo)
    {
        peerInfo.Status = PeerStatus.Online;
        peerInfo.LastSeen = DateTime.UtcNow;
        peerInfo.Endpoint = Context.ConnectionId;

        lock (ConnectedPeers)
        {
            ConnectedPeers[peerInfo.BrainAddress] = peerInfo;
        }

        await Clients.All.SendAsync("PeerJoined", peerInfo);
        await Clients.Caller.SendAsync("Registered", new
        {
            Success = true,
            YourAddress = peerInfo.BrainAddress,
            TotalPeers = ConnectedPeers.Count,
            Message = $"Welcome to ObsidianX Network! {ConnectedPeers.Count} brains connected."
        });

        LogActivity($"Brain joined: {peerInfo.DisplayName} ({peerInfo.BrainAddress[..18]}...)");
        Console.WriteLine($"[+] Brain registered: {peerInfo.BrainAddress} ({peerInfo.DisplayName})");
    }

    public async Task<List<MatchResult>> FindExperts(MatchRequest request)
    {
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

                // Keyword bonus
                foreach (var keyword in request.Keywords)
                {
                    if (peer.DisplayName.Contains(keyword, StringComparison.OrdinalIgnoreCase))
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

    public async Task RequestShare(ShareRequest request)
    {
        lock (PendingRequests)
        {
            PendingRequests.Add(request);
        }

        PeerInfo? target;
        lock (ConnectedPeers)
        {
            ConnectedPeers.TryGetValue(request.ToAddress, out target);
        }

        _totalShareRequests++;
        if (target != null)
        {
            await Clients.Client(target.Endpoint).SendAsync("ShareRequested", request);
            LogActivity($"Share request: {request.NodeTitle} → {request.ToAddress[..18]}...");
            Console.WriteLine($"[>] Share request: {request.FromAddress} -> {request.ToAddress} ({request.NodeTitle})");
        }
    }

    public async Task RespondToShare(string fromAddress, bool accepted)
    {
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
            var peer = ConnectedPeers.Values.FirstOrDefault(p => p.Endpoint == Context.ConnectionId);
            if (peer != null)
            {
                peer.Status = PeerStatus.Offline;
                disconnected = peer.BrainAddress;
                ConnectedPeers.Remove(peer.BrainAddress);
            }
        }

        if (!string.IsNullOrEmpty(disconnected))
        {
            await Clients.All.SendAsync("PeerLeft", disconnected);
            LogActivity($"Brain left: {disconnected[..18]}...");
            Console.WriteLine($"[-] Brain disconnected: {disconnected}");
        }

        await base.OnDisconnectedAsync(exception);
    }
}

namespace ObsidianX.Core.Models;

public class PeerInfo
{
    public string BrainAddress { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string PublicKey { get; set; } = string.Empty;
    public string Endpoint { get; set; } = string.Empty;
    public PeerStatus Status { get; set; } = PeerStatus.Offline;
    public Dictionary<KnowledgeCategory, double> ExpertiseScores { get; set; } = new();
    public long TotalKnowledgeNodes { get; set; }
    public long TotalWords { get; set; }
    public DateTime LastSeen { get; set; }
    public DateTime JoinedAt { get; set; }
    public double ReputationScore { get; set; } = 0.5;
    public int SharedCount { get; set; }
    public int ReceivedCount { get; set; }
}

public enum PeerStatus
{
    Online,
    Offline,
    Busy,
    Sharing,
    Requesting
}

public class MatchRequest
{
    public string RequesterAddress { get; set; } = string.Empty;
    public KnowledgeCategory DesiredCategory { get; set; }
    public List<string> Keywords { get; set; } = [];
    public double MinExpertiseScore { get; set; } = 0.3;
    public int MaxResults { get; set; } = 10;
}

public class MatchResult
{
    public PeerInfo Peer { get; set; } = new();
    public double MatchScore { get; set; }
    public KnowledgeCategory MatchedCategory { get; set; }
    public string MatchReason { get; set; } = string.Empty;
}

public class ShareRequest
{
    public string FromAddress { get; set; } = string.Empty;
    public string ToAddress { get; set; } = string.Empty;
    public string NodeTitle { get; set; } = string.Empty;
    public KnowledgeCategory Category { get; set; }
    public int WordCount { get; set; }
    public string Signature { get; set; } = string.Empty;
    public DateTime RequestedAt { get; set; } = DateTime.UtcNow;
    public ShareStatus Status { get; set; } = ShareStatus.Pending;
}

public enum ShareStatus
{
    Pending,
    Accepted,
    Rejected,
    Completed,
    Cancelled
}

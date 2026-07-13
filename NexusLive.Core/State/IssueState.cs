using System;
using System.Collections.Generic;

namespace NexusLive.Core.State
{
    public enum IssueStatus
    {
        Pending,
        Resolved
    }

    public class IssueInfo
    {
        public string Id { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public IssueStatus Status { get; set; } = IssueStatus.Pending;
        public string Category { get; set; } = string.Empty;
        public DateTime CreatedTimestamp { get; set; } = DateTime.UtcNow;
        public DateTime LastUpdatedTimestamp { get; set; } = DateTime.UtcNow;
        public string? ResolutionSummary { get; set; }
    }

    public class MeetingIssueState
    {
        public string MeetingId { get; set; } = Guid.NewGuid().ToString();
        public DateTime LastUpdated { get; set; } = DateTime.UtcNow;
        public List<IssueInfo> Issues { get; set; } = new();
    }
}

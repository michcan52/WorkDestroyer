using System;

namespace NexusLive.Core.State
{
    public class MeetingHistoryItem
    {
        public string Id { get; set; } = string.Empty;
        public DateTime Date { get; set; }
        public string Summary { get; set; } = string.Empty;
        public string Transcript { get; set; } = string.Empty;
    }
}

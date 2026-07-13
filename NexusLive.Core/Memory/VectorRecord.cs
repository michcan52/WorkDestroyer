using System.Collections.Generic;

namespace NexusLive.Core.Memory
{
    public class VectorRecord
    {
        public string Id { get; set; } = string.Empty;
        public string Text { get; set; } = string.Empty;
        public double Distance { get; set; }
        public Dictionary<string, object> Metadata { get; set; } = new();
    }
}

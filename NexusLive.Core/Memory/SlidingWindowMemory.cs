using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using NexusLive.Core.Audio;

namespace NexusLive.Core.Memory
{
    public class SlidingWindowMemory
    {
        private readonly List<TranscriptionSegment> _segments = new();
        private readonly object _lock = new();
        private readonly TimeSpan _windowDuration;

        public TimeSpan WindowDuration => _windowDuration;

        public SlidingWindowMemory(TimeSpan? windowDuration = null)
        {
            _windowDuration = windowDuration ?? TimeSpan.FromMinutes(10);
        }

        public void AddSegment(TranscriptionSegment segment)
        {
            if (segment == null) throw new ArgumentNullException(nameof(segment));

            lock (_lock)
            {
                _segments.Add(segment);
                PruneOldSegments();
            }
        }

        public List<TranscriptionSegment> GetActiveSegments()
        {
            lock (_lock)
            {
                PruneOldSegments();
                // Return a copy to ensure thread safety
                return _segments.ToList();
            }
        }

        public string GetFormattedContext()
        {
            lock (_lock)
            {
                PruneOldSegments();
                if (_segments.Count == 0)
                {
                    return string.Empty;
                }

                var sb = new StringBuilder();
                foreach (var segment in _segments)
                {
                    // Format segment timestamp relative to start or as time strings
                    sb.AppendLine($"[{segment.Timestamp:HH:mm:ss}] {segment.Text}");
                }
                return sb.ToString().TrimEnd();
            }
        }

        public void Clear()
        {
            lock (_lock)
            {
                _segments.Clear();
            }
        }

        private void PruneOldSegments()
        {
            DateTime threshold = DateTime.UtcNow - _windowDuration;
            _segments.RemoveAll(s => s.Timestamp < threshold);
        }
    }
}

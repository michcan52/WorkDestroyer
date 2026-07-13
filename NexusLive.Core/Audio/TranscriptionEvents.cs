using System;

namespace NexusLive.Core.Audio
{
    public class TranscriptionSegment
    {
        public string Text { get; set; } = string.Empty;
        public TimeSpan Start { get; set; }
        public TimeSpan End { get; set; }
        public double Confidence { get; set; }
        public DateTime Timestamp { get; set; } = DateTime.UtcNow;
    }

    public class TranscriptionSegmentEventArgs : EventArgs
    {
        public TranscriptionSegment Segment { get; }

        public TranscriptionSegmentEventArgs(TranscriptionSegment segment)
        {
            Segment = segment ?? throw new ArgumentNullException(nameof(segment));
        }
    }

    public class AudioDataEventArgs : EventArgs
    {
        public float[] PcmData { get; }

        public AudioDataEventArgs(float[] pcmData)
        {
            PcmData = pcmData ?? throw new ArgumentNullException(nameof(pcmData));
        }
    }
}

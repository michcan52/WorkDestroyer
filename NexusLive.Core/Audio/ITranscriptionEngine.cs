using System;
using System.Threading;
using System.Threading.Tasks;

namespace NexusLive.Core.Audio
{
    public interface ITranscriptionEngine
    {
        event EventHandler<TranscriptionSegmentEventArgs> SegmentTranscribed;
        Task InitializeAsync(string modelPath, CancellationToken cancellationToken);
        Task ProcessAudioAsync(float[] pcmData, CancellationToken cancellationToken);
    }
}

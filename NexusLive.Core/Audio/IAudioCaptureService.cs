using System;

namespace NexusLive.Core.Audio
{
    public interface IAudioCaptureService
    {
        event EventHandler<AudioDataEventArgs> AudioDataAvailable;
        void StartCapture();
        void StopCapture();
        bool IsCapturing { get; }
    }
}

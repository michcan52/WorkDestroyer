using System;
using System.Threading;
using System.Threading.Tasks;
using NexusLive.Core.Audio;
using Xunit;

namespace NexusLive.Tests
{
    public class WhisperTranscriptionEngineTests
    {
        [Fact]
        public async Task ProcessAudioAsync_ShouldEnqueueAndTriggerSegmentTranscribed()
        {
            // Arrange
            using var engine = new WhisperTranscriptionEngine();
            var tcs = new TaskCompletionSource<TranscriptionSegmentEventArgs>();
            
            engine.SegmentTranscribed += (sender, e) =>
            {
                tcs.TrySetResult(e);
            };

            await engine.InitializeAsync("dummy_model_path.bin", CancellationToken.None);

            // Act
            float[] pcm = new float[160]; // 10ms frame at 16kHz
            pcm[0] = 0.5f; // Signal to trigger heartbeat activity
            await engine.ProcessAudioAsync(pcm, CancellationToken.None);

            // Assert
            var result = await Task.WhenAny(tcs.Task, Task.Delay(2000));
            Assert.Same(tcs.Task, result); // Verify the event was raised within 2 seconds
            
            var eventArgs = await tcs.Task;
            Assert.NotNull(eventArgs.Segment);
            Assert.Contains("simulated", eventArgs.Segment.Text);
        }

        [Fact]
        public async Task ProcessAudioAsync_ShouldReuseEventArgsInstance_ZeroAllocation()
        {
            // Arrange
            using var engine = new WhisperTranscriptionEngine();
            TranscriptionSegmentEventArgs? firstArgs = null;
            TranscriptionSegmentEventArgs? secondArgs = null;
            int eventCount = 0;
            var sem = new SemaphoreSlim(0);

            engine.SegmentTranscribed += (sender, e) =>
            {
                eventCount++;
                if (eventCount == 1)
                {
                    firstArgs = e;
                    sem.Release();
                }
                else if (eventCount == 2)
                {
                    secondArgs = e;
                    sem.Release();
                }
            };

            await engine.InitializeAsync("dummy_model_path.bin", CancellationToken.None);

            // Act
            float[] pcm = new float[160];
            pcm[0] = 0.8f;
            
            await engine.ProcessAudioAsync(pcm, CancellationToken.None);
            await sem.WaitAsync(); // Wait for 1st event

            await engine.ProcessAudioAsync(pcm, CancellationToken.None);
            await sem.WaitAsync(); // Wait for 2nd event

            // Assert
            Assert.NotNull(firstArgs);
            Assert.NotNull(secondArgs);
            Assert.Same(firstArgs, secondArgs); // Reused EventArgs object!
        }
    }
}

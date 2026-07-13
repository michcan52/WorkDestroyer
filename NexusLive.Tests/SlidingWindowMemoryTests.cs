using System;
using System.Threading;
using NexusLive.Core.Audio;
using NexusLive.Core.Memory;
using Xunit;

namespace NexusLive.Tests
{
    public class SlidingWindowMemoryTests
    {
        [Fact]
        public void AddSegment_ShouldAddAndReturnActiveSegments()
        {
            // Arrange
            var memory = new SlidingWindowMemory(TimeSpan.FromMinutes(10));
            var segment = new TranscriptionSegment
            {
                Text = "Hello, world",
                Timestamp = DateTime.UtcNow
            };

            // Act
            memory.AddSegment(segment);
            var active = memory.GetActiveSegments();

            // Assert
            Assert.Single(active);
            Assert.Equal("Hello, world", active[0].Text);
        }

        [Fact]
        public void PruneOldSegments_ShouldRemoveSegmentsOutsideWindow()
        {
            // Arrange
            var memory = new SlidingWindowMemory(TimeSpan.FromSeconds(2));
            var oldSegment = new TranscriptionSegment
            {
                Text = "I am old",
                Timestamp = DateTime.UtcNow - TimeSpan.FromSeconds(5)
            };
            var newSegment = new TranscriptionSegment
            {
                Text = "I am new",
                Timestamp = DateTime.UtcNow
            };

            // Act
            memory.AddSegment(oldSegment);
            memory.AddSegment(newSegment);
            var active = memory.GetActiveSegments();

            // Assert
            Assert.Single(active);
            Assert.Equal("I am new", active[0].Text);
        }

        [Fact]
        public void GetFormattedContext_ShouldConcatSegmentTexts()
        {
            // Arrange
            var memory = new SlidingWindowMemory(TimeSpan.FromMinutes(10));
            var time1 = DateTime.UtcNow - TimeSpan.FromMinutes(2);
            var time2 = DateTime.UtcNow - TimeSpan.FromMinutes(1);

            var s1 = new TranscriptionSegment { Text = "First point.", Timestamp = time1 };
            var s2 = new TranscriptionSegment { Text = "Second point.", Timestamp = time2 };

            // Act
            memory.AddSegment(s1);
            memory.AddSegment(s2);
            var formatted = memory.GetFormattedContext();

            // Assert
            // Checking content ignoring specific local time zone conversions if any (since we use formatting pattern :HH:mm:ss)
            Assert.Contains("First point.", formatted);
            Assert.Contains("Second point.", formatted);
        }
    }
}

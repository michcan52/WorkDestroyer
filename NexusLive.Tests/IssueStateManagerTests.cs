using System;
using System.IO;
using System.Threading.Tasks;
using NexusLive.Core.State;
using Xunit;

namespace NexusLive.Tests
{
    public class IssueStateManagerTests : IDisposable
    {
        private readonly string _tempFilePath;

        public IssueStateManagerTests()
        {
            // Use unique temp file inside the test directory
            _tempFilePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, $"temp_issue_state_{Guid.NewGuid()}.json");
        }

        [Fact]
        public async Task LoadFromFile_NoFile_ShouldInitializeEmpty()
        {
            // Arrange
            var manager = new IssueStateManager();

            // Act
            await manager.LoadFromFileAsync(_tempFilePath);

            // Assert
            Assert.NotNull(manager.CurrentState);
            Assert.Empty(manager.GetAllIssues());
        }

        [Fact]
        public async Task SaveAndLoad_ShouldPersistStateCorrectly()
        {
            // Arrange
            var manager = new IssueStateManager();
            var issue = new IssueInfo
            {
                Id = "ISS-100",
                Description = "Fix database index performance",
                Status = IssueStatus.Pending,
                Category = "Database"
            };

            // Act
            manager.AddOrUpdateIssue(issue);
            await manager.SaveToFileAsync(_tempFilePath);

            var newManager = new IssueStateManager();
            await newManager.LoadFromFileAsync(_tempFilePath);

            // Assert
            var loadedIssue = newManager.GetIssue("ISS-100");
            Assert.NotNull(loadedIssue);
            Assert.Equal("Fix database index performance", loadedIssue.Description);
            Assert.Equal(IssueStatus.Pending, loadedIssue.Status);
            Assert.Equal("Database", loadedIssue.Category);
        }

        [Fact]
        public void UpdateExistingIssue_ShouldModifyFields()
        {
            // Arrange
            var manager = new IssueStateManager();
            var issue = new IssueInfo
            {
                Id = "ISS-101",
                Description = "Old Description",
                Status = IssueStatus.Pending
            };
            manager.AddOrUpdateIssue(issue);

            // Act
            var updated = new IssueInfo
            {
                Id = "ISS-101",
                Description = "New Description",
                Status = IssueStatus.Resolved,
                ResolutionSummary = "Fixed by optimization"
            };
            manager.AddOrUpdateIssue(updated);

            // Assert
            var result = manager.GetIssue("ISS-101");
            Assert.NotNull(result);
            Assert.Equal("New Description", result.Description);
            Assert.Equal(IssueStatus.Resolved, result.Status);
            Assert.Equal("Fixed by optimization", result.ResolutionSummary);
        }

        public void Dispose()
        {
            if (File.Exists(_tempFilePath))
            {
                try
                {
                    File.Delete(_tempFilePath);
                }
                catch
                {
                    // Ignore disposal errors
                }
            }
            GC.SuppressFinalize(this);
        }
    }
}

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace NexusLive.Core.State
{
    public class IssueStateManager
    {
        private MeetingIssueState _currentState = new();
        private readonly object _lock = new();
        private readonly JsonSerializerOptions _jsonOptions;

        public MeetingIssueState CurrentState
        {
            get
            {
                lock (_lock)
                {
                    return _currentState;
                }
            }
        }

        public IssueStateManager()
        {
            _jsonOptions = new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                WriteIndented = true
            };
            // Serialize Enums as strings (Pending/Resolved) rather than integers
            _jsonOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        }

        public async Task LoadFromFileAsync(string filePath, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(filePath)) throw new ArgumentException("File path cannot be null or empty.", nameof(filePath));

            if (!File.Exists(filePath))
            {
                lock (_lock)
                {
                    _currentState = new MeetingIssueState();
                }
                return;
            }

            try
            {
                using FileStream openStream = File.OpenRead(filePath);
                var loadedState = await JsonSerializer.DeserializeAsync<MeetingIssueState>(openStream, _jsonOptions, cancellationToken).ConfigureAwait(false);
                
                lock (_lock)
                {
                    _currentState = loadedState ?? new MeetingIssueState();
                }
            }
            catch (JsonException ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to parse Issue State JSON: {ex.Message}");
                // Initialize with fresh state on failure
                lock (_lock)
                {
                    _currentState = new MeetingIssueState();
                }
            }
        }

        public async Task SaveToFileAsync(string filePath, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(filePath)) throw new ArgumentException("File path cannot be null or empty.", nameof(filePath));

            string? directory = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }

            MeetingIssueState stateCopy;
            lock (_lock)
            {
                _currentState.LastUpdated = DateTime.UtcNow;
                stateCopy = _currentState; // We assume state object shape is stable for serialization
            }

            using FileStream writeStream = File.Create(filePath);
            await JsonSerializer.SerializeAsync(writeStream, stateCopy, _jsonOptions, cancellationToken).ConfigureAwait(false);
        }

        public void AddOrUpdateIssue(IssueInfo issue)
        {
            if (issue == null) throw new ArgumentNullException(nameof(issue));
            if (string.IsNullOrWhiteSpace(issue.Id)) throw new ArgumentException("Issue ID cannot be empty.", nameof(issue.Id));

            lock (_lock)
            {
                var existing = _currentState.Issues.FirstOrDefault(i => i.Id.Equals(issue.Id, StringComparison.OrdinalIgnoreCase));
                if (existing != null)
                {
                    existing.Description = issue.Description;
                    existing.Status = issue.Status;
                    existing.Category = issue.Category;
                    existing.LastUpdatedTimestamp = DateTime.UtcNow;
                    existing.ResolutionSummary = issue.ResolutionSummary;
                }
                else
                {
                    issue.CreatedTimestamp = DateTime.UtcNow;
                    issue.LastUpdatedTimestamp = DateTime.UtcNow;
                    _currentState.Issues.Add(issue);
                }
            }
        }

        public IssueInfo? GetIssue(string id)
        {
            lock (_lock)
            {
                return _currentState.Issues.FirstOrDefault(i => i.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
            }
        }

        public List<IssueInfo> GetActiveIssues()
        {
            lock (_lock)
            {
                return _currentState.Issues.Where(i => i.Status == IssueStatus.Pending).ToList();
            }
        }

        public List<IssueInfo> GetAllIssues()
        {
            lock (_lock)
            {
                return _currentState.Issues.ToList();
            }
        }

        public string ToJsonString()
        {
            lock (_lock)
            {
                return JsonSerializer.Serialize(_currentState, _jsonOptions);
            }
        }
    }
}

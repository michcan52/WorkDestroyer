using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;

namespace NexusLive.Core.State
{
    public class MeetingHistoryService
    {
        private readonly string _storageDir;

        public MeetingHistoryService(string storageDir)
        {
            _storageDir = storageDir ?? throw new ArgumentNullException(nameof(storageDir));
            if (!Directory.Exists(_storageDir))
            {
                Directory.CreateDirectory(_storageDir);
            }
        }

        public async Task SaveMeetingAsync(string summary, string transcript)
        {
            var item = new MeetingHistoryItem
            {
                Id = $"MTG-{DateTime.UtcNow:yyyyMMdd-HHmmss}",
                Date = DateTime.UtcNow,
                Summary = summary ?? string.Empty,
                Transcript = transcript ?? string.Empty
            };

            string filePath = Path.Combine(_storageDir, $"{item.Id}.json");
            var options = new JsonSerializerOptions { WriteIndented = true };
            string json = JsonSerializer.Serialize(item, options);
            await File.WriteAllTextAsync(filePath, json).ConfigureAwait(false);
            Console.WriteLine($"[DIAGNOSTIC] Saved past meeting log to: {filePath}");
        }

        public List<MeetingHistoryItem> GetAllMeetings()
        {
            var meetings = new List<MeetingHistoryItem>();
            if (!Directory.Exists(_storageDir)) return meetings;

            foreach (var file in Directory.GetFiles(_storageDir, "MTG-*.json"))
            {
                try
                {
                    string json = File.ReadAllText(file);
                    var item = JsonSerializer.Deserialize<MeetingHistoryItem>(json);
                    if (item != null)
                    {
                        meetings.Add(item);
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[DIAGNOSTIC] Error reading meeting history file {file}: {ex.Message}");
                }
            }

            // Sort by Date descending (newest first)
            meetings.Sort((a, b) => b.Date.CompareTo(a.Date));
            return meetings;
        }
    }
}

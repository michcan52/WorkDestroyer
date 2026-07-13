using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace NexusLive.Core.Memory
{
    public class ChromaDbMockService : IVectorDbService
    {
        private readonly List<VectorRecord> _inMemoryDb = new();
        private readonly object _lock = new();
        private string? _activeCollection;

        public Task InitializeCollectionAsync(string collectionName, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(collectionName))
            {
                throw new ArgumentException("Collection name cannot be empty.", nameof(collectionName));
            }

            _activeCollection = collectionName;
            return Task.CompletedTask;
        }

        public Task AddRecordAsync(string id, string text, float[]? vector, Dictionary<string, object>? metadata, CancellationToken cancellationToken)
        {
            lock (_lock)
            {
                // Remove existing if it matches ID to act like upsert
                _inMemoryDb.RemoveAll(r => r.Id == id);
                
                _inMemoryDb.Add(new VectorRecord
                {
                    Id = id,
                    Text = text,
                    Distance = 0.0,
                    Metadata = metadata ?? new Dictionary<string, object>()
                });
            }

            return Task.CompletedTask;
        }

        public Task<List<VectorRecord>> QuerySimilarityAsync(string queryText, int limit, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(queryText))
            {
                return Task.FromResult(new List<VectorRecord>());
            }

            lock (_lock)
            {
                // A simple word-overlap keyword matching search as a mock vector database search
                var queryWords = queryText.Split(new[] { ' ', ',', '.', '?' }, StringSplitOptions.RemoveEmptyEntries)
                                           .Select(w => w.ToLowerInvariant())
                                           .ToHashSet();

                var results = _inMemoryDb
                    .Select(record =>
                    {
                        var recordWords = record.Text.Split(new[] { ' ', ',', '.', '?' }, StringSplitOptions.RemoveEmptyEntries)
                                                     .Select(w => w.ToLowerInvariant());

                        int matches = recordWords.Count(w => queryWords.Contains(w));
                        double similarity = matches > 0 ? (double)matches / queryWords.Count : 0.0;

                        return new VectorRecord
                        {
                            Id = record.Id,
                            Text = record.Text,
                            Distance = 1.0 - similarity, // Lower distance means more similar
                            Metadata = record.Metadata
                        };
                    })
                    .Where(r => r.Distance < 1.0)
                    .OrderBy(r => r.Distance)
                    .Take(limit)
                    .ToList();

                // If no direct keyword matches, return a default helpful mock RAG result for test/demo
                if (results.Count == 0 && _inMemoryDb.Count > 0)
                {
                    results = _inMemoryDb
                        .Take(limit)
                        .Select(r => new VectorRecord
                        {
                            Id = r.Id,
                            Text = r.Text,
                            Distance = 0.8, // standard low similarity
                            Metadata = r.Metadata
                        })
                        .ToList();
                }

                return Task.FromResult(results);
            }
        }
    }
}

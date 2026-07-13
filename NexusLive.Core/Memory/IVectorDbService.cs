using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace NexusLive.Core.Memory
{
    public interface IVectorDbService
    {
        Task InitializeCollectionAsync(string collectionName, CancellationToken cancellationToken);
        Task AddRecordAsync(string id, string text, float[]? vector, Dictionary<string, object>? metadata, CancellationToken cancellationToken);
        Task<List<VectorRecord>> QuerySimilarityAsync(string queryText, int limit, CancellationToken cancellationToken);
    }
}

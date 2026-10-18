using System.Text.Json;
using OhMyStudy.Domain;

namespace OhMyStudy.Infrastructure.Storage;

public sealed partial class LibraryRepository
{
    public Task<List<JsonElement>> ListJobModelCallsAsync(string jobId, CancellationToken cancellationToken = default)
    {
        ValidId(jobId);
        return Run(() => Query("SELECT details_json FROM model_calls WHERE job_id=$p0 ORDER BY ordinal", r => DomainJson.Deserialize<JsonElement>(r.GetString(0)), jobId), cancellationToken);
    }
}

using System.Threading;
using System.Threading.Tasks;
using AiUsageTray.Models;

namespace AiUsageTray.Services;

public interface IUsageProvider
{
    string Name { get; }

    Task<ProviderUsage> FetchAsync(CancellationToken ct);
}

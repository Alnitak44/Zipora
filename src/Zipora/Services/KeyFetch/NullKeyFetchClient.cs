namespace Zipora.Services.KeyFetch;

/// <summary>
/// 未配置服务器地址时的空实现：激活失败、查询无结果、贡献为空操作。
/// </summary>
public sealed class NullKeyFetchClient : IKeyFetchClient
{
    public static readonly NullKeyFetchClient Instance = new();

    public bool IsConfigured => false;

    public Task<FetchResult> FetchPasswordAsync(string md5, long size, CancellationToken ct = default)
        => Task.FromResult(new FetchResult(FetchStatus.NotFound, null, null));

    public Task<ContributeResult> ContributeAsync(string md5, long size, string password, CancellationToken ct = default)
        => Task.FromResult(new ContributeResult(ContributeStatus.Ok, null));
}

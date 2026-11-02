namespace Zipora.Services.KeyFetch;

/// <summary>KeyFetch public cloud-query API.</summary>
public interface IKeyFetchClient
{
    bool IsConfigured { get; }

    Task<FetchResult> FetchPasswordAsync(string md5, long size, CancellationToken ct = default);

    Task<ContributeResult> ContributeAsync(string md5, long size, string password, CancellationToken ct = default);
}

public enum FetchStatus { Ok, NotFound, QuotaExceeded, Error }

public record FetchResult(FetchStatus Status, string? Password, string? Message);

public enum ContributeStatus { Ok, QuotaExceeded, Error }

public record ContributeResult(ContributeStatus Status, string? Message);

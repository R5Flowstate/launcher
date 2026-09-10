namespace R5Flowstate.Content;

public interface IHttpFetcher
{
    Task DownloadAsync(
        string url,
        string destPath,
        string? expectedSha256 = null,
        IProgress<ContentInstallProgress>? progress = null,
        CancellationToken cancel = default);
}

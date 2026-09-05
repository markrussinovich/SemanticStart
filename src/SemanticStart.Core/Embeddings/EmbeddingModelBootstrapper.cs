using System.Net;
using SemanticStart.Core;

namespace SemanticStart.Core.Embeddings;

public sealed class EmbeddingModelBootstrapper
{
    public const string DefaultModelUrl = "https://huggingface.co/sentence-transformers/all-MiniLM-L6-v2/resolve/main/onnx/model.onnx";
    public const string DefaultVocabUrl = "https://huggingface.co/sentence-transformers/all-MiniLM-L6-v2/resolve/main/vocab.txt";

    private const long MinimumModelBytes = 1_000_000;
    private const long MinimumVocabBytes = 100_000;

    private readonly HttpClient _httpClient;
    private readonly string _modelsDirectory;
    private readonly string _modelUrl;
    private readonly string _vocabUrl;

    public EmbeddingModelBootstrapper(
        HttpClient? httpClient = null,
        string? modelsDirectory = null,
        string modelUrl = DefaultModelUrl,
        string vocabUrl = DefaultVocabUrl)
    {
        _httpClient = httpClient ?? new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        _modelsDirectory = modelsDirectory ?? AppPaths.ModelsDirectory;
        _modelUrl = modelUrl;
        _vocabUrl = vocabUrl;
    }

    public string ModelPath => Path.Combine(_modelsDirectory, "all-MiniLM-L6-v2.onnx");

    public string VocabPath => Path.Combine(_modelsDirectory, "all-MiniLM-L6-v2-vocab.txt");

    public async Task<string> EnsureModelAsync(CancellationToken cancellationToken = default)
    {
        AppPaths.EnsureCreated();
        Directory.CreateDirectory(_modelsDirectory);
        await DownloadIfNeededAsync(_modelUrl, ModelPath, MinimumModelBytes, null, cancellationToken).ConfigureAwait(false);
        return ModelPath;
    }

    public async Task<string> EnsureVocabAsync(CancellationToken cancellationToken = default)
    {
        AppPaths.EnsureCreated();
        Directory.CreateDirectory(_modelsDirectory);
        await DownloadIfNeededAsync(_vocabUrl, VocabPath, MinimumVocabBytes, null, cancellationToken).ConfigureAwait(false);
        return VocabPath;
    }

    public async Task<EmbeddingModelFiles> EnsureAsync(
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        AppPaths.EnsureCreated();
        Directory.CreateDirectory(_modelsDirectory);

        await DownloadIfNeededAsync(_modelUrl, ModelPath, MinimumModelBytes, ScaleProgress(progress, 0.0, 0.95), cancellationToken)
            .ConfigureAwait(false);
        await DownloadIfNeededAsync(_vocabUrl, VocabPath, MinimumVocabBytes, ScaleProgress(progress, 0.95, 1.0), cancellationToken)
            .ConfigureAwait(false);
        progress?.Report(1.0);

        return new EmbeddingModelFiles(ModelPath, VocabPath);
    }

    private static IProgress<double>? ScaleProgress(IProgress<double>? progress, double start, double end)
    {
        return progress is null
            ? null
            : new Progress<double>(value => progress.Report(start + ((end - start) * Math.Clamp(value, 0.0, 1.0))));
    }

    private async Task DownloadIfNeededAsync(
        string url,
        string path,
        long minimumBytes,
        IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        if (IsUsableFile(path, minimumBytes))
        {
            progress?.Report(1.0);
            return;
        }

        string tempPath = path + ".tmp";
        try
        {
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }

            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            using HttpResponseMessage response = await _httpClient.SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead,
                    cancellationToken)
                .ConfigureAwait(false);

            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                throw new EmbeddingModelDownloadException($"Embedding model file was not found at {url}.");
            }

            response.EnsureSuccessStatusCode();

            long? contentLength = response.Content.Headers.ContentLength;
            await using Stream source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            await using var destination = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1024 * 1024, useAsync: true);

            var buffer = new byte[1024 * 1024];
            long totalRead = 0;
            int read;
            while ((read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
            {
                await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                totalRead += read;
                if (contentLength is > 0)
                {
                    progress?.Report((double)totalRead / contentLength.Value);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or OperationCanceledException or InvalidOperationException)
        {
            throw new EmbeddingModelDownloadException(
                $"Could not download embedding model file from {url}. Check network connectivity or pre-place the file at {path}.",
                ex);
        }

        if (!IsUsableFile(tempPath, minimumBytes))
        {
            TryDelete(tempPath);
            throw new EmbeddingModelDownloadException(
                $"Downloaded embedding model file from {url} was too small or empty. Delete the cache and try again: {path}");
        }

        File.Move(tempPath, path, overwrite: true);
        progress?.Report(1.0);
    }

    private static bool IsUsableFile(string path, long minimumBytes)
    {
        return File.Exists(path) && new FileInfo(path).Length >= minimumBytes;
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}

public sealed record EmbeddingModelFiles(string ModelPath, string VocabPath);

public sealed class EmbeddingModelDownloadException : Exception
{
    public EmbeddingModelDownloadException(string message)
        : base(message)
    {
    }

    public EmbeddingModelDownloadException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

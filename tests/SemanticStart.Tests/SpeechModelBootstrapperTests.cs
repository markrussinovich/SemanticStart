using SemanticStart.Core.Speech;

namespace SemanticStart.Tests;

/// <summary>
/// Only the part of the bootstrapper that can be answered from disk: where the files go, and
/// whether they are all there. That question is asked at startup to decide whether dictation can
/// be loaded without first asking the user to authorise a ~665 MB download, so a wrong answer either
/// downloads without consent or reports a model missing that is sitting right there.
///
/// <para>
/// Nothing here reaches the network. The bootstrapper takes its models directory, so the files are
/// faked in a scratch directory at exactly the sizes the size floor is checking for - the floor
/// exists to reject a truncated download, and a zero-byte placeholder has to fail it.
/// </para>
/// </summary>
public sealed class SpeechModelBootstrapperTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ss-speech-" + Guid.NewGuid().ToString("N"));

    public SpeechModelBootstrapperTests() => Directory.CreateDirectory(_dir);

    /// <summary>
    /// The size floors are deliberately set well below the model files' actual published sizes -
    /// see the remarks on <see cref="SpeechModelOptions.MinimumEncoderBytes"/> - so this pins the
    /// relationships that make that a safe choice rather than the exact numbers, which are free to
    /// move a little with a future export without becoming a test failure: every floor must still
    /// be positive, small enough to be clearly below a genuine download, and ordered the same way
    /// the files themselves are ordered by size (encoder &gt;&gt; decoder &gt; joiner &gt; tokens).
    /// </summary>
    [Fact]
    public void SizeFloorsAreOrderedAndStrictlyBelowTheApproximateTotal()
    {
        var options = SpeechModelOptions.Default;

        Assert.True(options.MinimumEncoderBytes > 0);
        Assert.True(options.MinimumDecoderBytes > 0);
        Assert.True(options.MinimumJoinerBytes > 0);
        Assert.True(options.MinimumTokensBytes > 0);
        Assert.True(options.MinimumVadBytes > 0);

        Assert.True(options.MinimumEncoderBytes > options.MinimumDecoderBytes);
        Assert.True(options.MinimumDecoderBytes > options.MinimumJoinerBytes);
        Assert.True(options.MinimumJoinerBytes > options.MinimumTokensBytes);

        // The floors exist to catch a truncated download, not to pin the real size, so every
        // floor - including their sum - must still leave headroom below what the user is told to
        // expect, or a download that is genuinely complete but a little smaller than today's
        // published files would be rejected as truncated.
        var sumOfFloors = options.MinimumEncoderBytes + options.MinimumDecoderBytes
            + options.MinimumJoinerBytes + options.MinimumTokensBytes + options.MinimumVadBytes;
        Assert.True(sumOfFloors < options.ApproximateDownloadBytes);
    }

    [Fact]
    public void NothingOnDiskMeansNothingIsDownloaded()
    {
        Assert.False(new SpeechModelBootstrapper(modelsDirectory: _dir).IsDownloaded);
    }

    [Fact]
    public void EveryFilePresentAndFullSizeMeansDownloaded()
    {
        var bootstrapper = CreateBootstrapper();
        WriteFullSizeModel(bootstrapper);

        Assert.True(bootstrapper.IsDownloaded);
    }

    [Theory]
    [InlineData("license")]
    [InlineData("notice")]
    public void UnreadableLegalFileReportsReadinessFailureAndMeansNotDownloaded(string file)
    {
        var bootstrapper = CreateBootstrapper();
        WriteFullSizeModel(bootstrapper);
        SpeechDiagnosticEvent? diagnostic = null;
        void Capture(SpeechDiagnosticEvent report) => diagnostic = report;
        SpeechDiagnostics.Reported += Capture;

        try
        {
            using var lockedFile = File.Open(PathFor(bootstrapper, file), FileMode.Open, FileAccess.Read, FileShare.None);

            Assert.False(bootstrapper.IsDownloaded);
            Assert.NotNull(diagnostic);
            Assert.Equal("model.readiness", diagnostic.Operation);
            Assert.True(diagnostic.IsError);
            Assert.IsAssignableFrom<IOException>(diagnostic.Exception);
        }
        finally
        {
            SpeechDiagnostics.Reported -= Capture;
        }
    }

    /// <summary>
    /// One missing file is a half-downloaded model, which loads as an error deep inside the ONNX
    /// runtime rather than as "the model is not here".
    /// </summary>
    [Theory]
    [InlineData("encoder")]
    [InlineData("decoder")]
    [InlineData("joiner")]
    [InlineData("tokens")]
    [InlineData("vad")]
    [InlineData("license")]
    [InlineData("notice")]
    public void OneMissingFileMeansNotDownloaded(string missing)
    {
        var bootstrapper = CreateBootstrapper();
        WriteFullSizeModel(bootstrapper);
        File.Delete(PathFor(bootstrapper, missing));

        Assert.False(bootstrapper.IsDownloaded);
    }

    /// <summary>
    /// The failure this guards is an interrupted download left in place: a file that exists, is
    /// far too small, and would otherwise be taken for a complete model forever.
    /// </summary>
    [Theory]
    [InlineData("encoder")]
    [InlineData("vad")]
    public void ATruncatedFileMeansNotDownloaded(string truncated)
    {
        var bootstrapper = CreateBootstrapper();
        WriteFullSizeModel(bootstrapper);
        WriteFile(PathFor(bootstrapper, truncated), 1_024);

        Assert.False(bootstrapper.IsDownloaded);
    }

    /// <summary>
    /// The files live in the directory the caller named, under names carrying the model id, so two
    /// models can share the one models directory the embedding model already uses.
    /// </summary>
    [Fact]
    public void ModelFilesArePlacedInTheGivenDirectory()
    {
        var bootstrapper = new SpeechModelBootstrapper(modelsDirectory: _dir);

        foreach (var path in AllPaths(bootstrapper))
            Assert.Equal(_dir, Path.GetDirectoryName(path));

        Assert.StartsWith(bootstrapper.ModelId, Path.GetFileName(bootstrapper.EncoderPath), StringComparison.Ordinal);
        Assert.Equal("silero_vad.onnx", Path.GetFileName(bootstrapper.VadPath));

        // Distinct names, or one download overwrites the last.
        Assert.Equal(7, AllPaths(bootstrapper).Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public void CleanupRemovesSpeechFilesAndPartialsButKeepsOtherModels()
    {
        var bootstrapper = new SpeechModelBootstrapper(modelsDirectory: _dir);
        foreach (var path in AllPaths(bootstrapper))
            File.WriteAllBytes(path, [1, 2, 3]);
        File.WriteAllText(bootstrapper.EncoderPath + ".tmp", "partial");
        File.WriteAllText(bootstrapper.LicensePath + ".tmp", "partial");
        var otherModel = Path.Combine(_dir, "embedding.onnx");
        File.WriteAllText(otherModel, "keep");

        Assert.Equal(21, bootstrapper.InstalledBytes);
        bootstrapper.DeleteModelFiles();

        Assert.Equal(0, bootstrapper.InstalledBytes);
        Assert.All(AllPaths(bootstrapper), path => Assert.False(File.Exists(path)));
        Assert.False(File.Exists(bootstrapper.EncoderPath + ".tmp"));
        Assert.False(File.Exists(bootstrapper.LicensePath + ".tmp"));
        Assert.True(File.Exists(otherModel));
    }

    [Fact]
    public void CleanupRejectsModelIdsThatEscapeTheModelsDirectory()
    {
        var outside = Path.Combine(Path.GetDirectoryName(_dir)!, "outside-encoder.int8.onnx");
        File.WriteAllText(outside, "keep");
        try
        {
            var options = new SpeechModelOptions { ModelId = "../outside" };
            var bootstrapper = new SpeechModelBootstrapper(modelsDirectory: _dir, options: options);

            Assert.Throws<InvalidOperationException>(bootstrapper.DeleteModelFiles);
            Assert.True(File.Exists(outside));
        }
        finally
        {
            File.Delete(outside);
        }
    }

    /// <summary>
    /// The VAD model is the one file fetched from a raw file URL rather than release hosting, and
    /// the checksum is what makes that safe, so the comparison itself is worth pinning.
    /// </summary>
    [Fact]
    public void TheChecksumComparisonAcceptsOnlyTheExpectedContent()
    {
        var path = Path.Combine(_dir, "checksum.bin");
        File.WriteAllText(path, "semanticstart");

        // Not the hash of anything: the point is that a file whose contents do not match is
        // rejected rather than accepted because it was the right length.
        const string wrong = "0000000000000000000000000000000000000000000000000000000000000000";

        Assert.False(SpeechModelBootstrapper.MatchesSha256(path, wrong));

        // Spelled in lower case as well, because the published hash is lower case while
        // Convert.ToHexString produces upper.
        Assert.True(SpeechModelBootstrapper.MatchesSha256(path, Sha256Of(path)));
        Assert.True(SpeechModelBootstrapper.MatchesSha256(path, Sha256Of(path).ToLowerInvariant()));
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private static string Sha256Of(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(stream));
    }

    private static string[] AllPaths(SpeechModelBootstrapper bootstrapper) =>
    [
        bootstrapper.EncoderPath,
        bootstrapper.DecoderPath,
        bootstrapper.JoinerPath,
        bootstrapper.TokensPath,
        bootstrapper.VadPath,
        bootstrapper.LicensePath,
        bootstrapper.NoticePath,
    ];

    /// <summary>
    /// An override file is the whole point of making the sources parametric - a mirror, an
    /// internal host or a newer model revision has to be reachable without a rebuild - so the
    /// round trip from JSON to the paths and URLs the bootstrapper actually uses is pinned.
    /// </summary>
    [Fact]
    public void OverrideFileReplacesTheModelSources()
    {
        File.WriteAllText(
            Path.Combine(_dir, SpeechModelOptions.FileName),
            """
            {
              "modelId": "mirrored-parakeet",
              "encoderUrl": "https://mirror.internal/encoder.onnx",
              "minimumEncoderBytes": 1234,
              "vadSha256": null
            }
            """);

        var options = SpeechModelOptions.Load(_dir);
        var bootstrapper = new SpeechModelBootstrapper(modelsDirectory: _dir, options: options);

        Assert.Equal("https://mirror.internal/encoder.onnx", options.EncoderUrl);
        Assert.Equal(1234, options.MinimumEncoderBytes);
        Assert.Null(options.VadSha256);
        Assert.StartsWith("mirrored-parakeet", Path.GetFileName(bootstrapper.EncoderPath), StringComparison.Ordinal);

        // Unspecified members keep the upstream defaults rather than resetting to null or zero.
        Assert.Equal(SpeechModelOptions.Default.DecoderUrl, options.DecoderUrl);
        Assert.Equal(SpeechModelOptions.Default.MinimumVadBytes, options.MinimumVadBytes);
    }

    /// <summary>
    /// No override file is the normal case and must not be an error, or every clean install would
    /// fail to start dictation.
    /// </summary>
    [Fact]
    public void MissingOverrideFileYieldsTheDefaults()
    {
        Assert.Same(SpeechModelOptions.Default, SpeechModelOptions.Load(_dir));
    }

    /// <summary>
    /// A malformed override is reported instead of being ignored: downloading from the upstream
    /// default when an operator has asked for a mirror would quietly defeat the reason they wrote
    /// the file.
    /// </summary>
    [Fact]
    public void MalformedOverrideFileIsReported()
    {
        File.WriteAllText(Path.Combine(_dir, SpeechModelOptions.FileName), "{ not json");

        Assert.Throws<SpeechModelDownloadException>(() => SpeechModelOptions.Load(_dir));
    }

    private static string PathFor(SpeechModelBootstrapper bootstrapper, string file) => file switch
    {
        "encoder" => bootstrapper.EncoderPath,
        "decoder" => bootstrapper.DecoderPath,
        "joiner" => bootstrapper.JoinerPath,
        "tokens" => bootstrapper.TokensPath,
        "vad" => bootstrapper.VadPath,
        "license" => bootstrapper.LicensePath,
        "notice" => bootstrapper.NoticePath,
        _ => throw new ArgumentOutOfRangeException(nameof(file), file, "Unknown model file."),
    };

    /// <summary>
    /// Files at the size the bootstrapper's floors demand. Written by length rather than by
    /// content: the encoder floor is 400 MB, and setting the length leaves the filesystem to
    /// account for the space instead of this test writing it a megabyte at a time.
    /// </summary>
    private static void WriteFullSizeModel(SpeechModelBootstrapper bootstrapper)
    {
        WriteFile(bootstrapper.EncoderPath, 400_000_000);
        WriteFile(bootstrapper.DecoderPath, 3_000_000);
        WriteFile(bootstrapper.JoinerPath, 500_000);
        WriteFile(bootstrapper.TokensPath, 2_000);
        WriteFile(bootstrapper.VadPath, 1_000_000);
        File.WriteAllText(bootstrapper.LicensePath, new string('L', 8_000));
        File.WriteAllText(bootstrapper.NoticePath, bootstrapper.Options.LicenseNotice + Environment.NewLine);
    }

    private SpeechModelBootstrapper CreateBootstrapper(HttpClient? client = null) =>
        new(client, _dir, SpeechModelOptions.Default with { LicenseSha256 = null });

    [Fact]
    public async Task FirstDownloadIncludesLegalFilesAndUsesConfiguredChecksum()
    {
        const string license = "Complete custom model agreement for this test.";
        var requests = new List<string>();
        var options = SpeechModelOptions.Default with
        {
            ModelId = "custom-model",
            LicenseUrl = "https://mirror.internal/LICENSE.txt",
            LicenseSha256 = Convert.ToHexString(
                System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(license))),
            LicenseNotice = "Custom model attribution",
            MinimumLicenseBytes = 1,
            MinimumEncoderBytes = 1,
            MinimumDecoderBytes = 1,
            MinimumJoinerBytes = 1,
            MinimumTokensBytes = 1,
            MinimumVadBytes = 1,
            VadSha256 = null,
        };
        using var client = new HttpClient(new StubHandler((request, _) =>
        {
            var url = request.RequestUri!.AbsoluteUri;
            requests.Add(url);
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(url == options.LicenseUrl ? license : "model"),
            });
        }));
        var bootstrapper = new SpeechModelBootstrapper(client, _dir, options);

        await bootstrapper.EnsureAsync();

        Assert.Equal(options.LicenseUrl, requests[0]);
        Assert.Equal(6, requests.Count);
        Assert.True(bootstrapper.IsDownloaded);
        Assert.Equal(license, File.ReadAllText(bootstrapper.LicensePath));
        Assert.Equal(options.LicenseNotice + Environment.NewLine, File.ReadAllText(bootstrapper.NoticePath));
        File.WriteAllText(bootstrapper.LicensePath, "tampered agreement");
        Assert.False(bootstrapper.IsDownloaded);
    }

    [Fact]
    public async Task CachedWeightsBackfillOnlyTheLegalFiles()
    {
        var requests = new List<string>();
        var license = new string('L', 8_000);
        using var client = new HttpClient(new StubHandler((request, _) =>
        {
            requests.Add(request.RequestUri!.AbsoluteUri);
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(license),
            });
        }));
        var bootstrapper = CreateBootstrapper(client);
        WriteFullSizeModel(bootstrapper);
        File.Delete(bootstrapper.LicensePath);
        File.Delete(bootstrapper.NoticePath);

        Assert.False(bootstrapper.IsDownloaded);
        await bootstrapper.EnsureAsync();
        await bootstrapper.EnsureAsync();

        Assert.Equal([bootstrapper.Options.LicenseUrl], requests);
        Assert.Equal(license, File.ReadAllText(bootstrapper.LicensePath));
        Assert.Equal("Licensed by NVIDIA Corporation under the NVIDIA Open Model License" + Environment.NewLine,
            File.ReadAllText(bootstrapper.NoticePath));
        Assert.True(bootstrapper.IsDownloaded);
        Assert.Empty(Directory.GetFiles(_dir, "*.tmp"));
    }

    [Fact]
    public async Task IncorrectNoticeIsRepairedWithoutNetworkAccess()
    {
        using var client = new HttpClient(new StubHandler((_, _) =>
            throw new InvalidOperationException("No downloads should be needed.")));
        var bootstrapper = CreateBootstrapper(client);
        WriteFullSizeModel(bootstrapper);
        File.WriteAllText(bootstrapper.NoticePath, "wrong attribution");

        Assert.False(bootstrapper.IsDownloaded);
        await bootstrapper.EnsureAsync();
        Assert.True(bootstrapper.IsDownloaded);
    }

    [Theory]
    [InlineData("html")]
    [InlineData("truncated")]
    [InlineData("not-found")]
    [InlineData("checksum")]
    public async Task InvalidLicenseFailsReadinessAndCleansTempFile(string failure)
    {
        using var client = new HttpClient(new StubHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage(failure == "not-found"
                ? System.Net.HttpStatusCode.NotFound : System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(failure switch
                {
                    "html" => "<html>" + new string('L', 8_000) + "</html>",
                    "truncated" => "short",
                    _ => new string('L', 8_000),
                }),
            })));
        var bootstrapper = failure == "checksum"
            ? new SpeechModelBootstrapper(client, _dir)
            : CreateBootstrapper(client);
        WriteFullSizeModel(bootstrapper);
        File.Delete(bootstrapper.LicensePath);

        await Assert.ThrowsAsync<SpeechModelDownloadException>(() => bootstrapper.EnsureAsync());

        Assert.False(bootstrapper.IsDownloaded);
        Assert.False(File.Exists(bootstrapper.LicensePath));
        Assert.False(File.Exists(bootstrapper.LicensePath + ".tmp"));
        Assert.Equal(400_000_000, new FileInfo(bootstrapper.EncoderPath).Length);
    }

    [Fact]
    public async Task CancelledLegalDownloadLeavesCachedWeightsUntouched()
    {
        using var cancellation = new CancellationTokenSource();
        using var client = new HttpClient(new StubHandler((_, token) =>
        {
            cancellation.Cancel();
            token.ThrowIfCancellationRequested();
            throw new InvalidOperationException("Cancellation should have been propagated.");
        }));
        var bootstrapper = CreateBootstrapper(client);
        WriteFullSizeModel(bootstrapper);
        File.Delete(bootstrapper.LicensePath);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            bootstrapper.EnsureAsync(cancellationToken: cancellation.Token));

        Assert.False(bootstrapper.IsDownloaded);
        Assert.Empty(Directory.GetFiles(_dir, "*.tmp"));
        Assert.Equal(400_000_000, new FileInfo(bootstrapper.EncoderPath).Length);
    }

    [Fact]
    public void OverridesIncludeModelSpecificLegalSourcesAndAttribution()
    {
        File.WriteAllText(Path.Combine(_dir, SpeechModelOptions.FileName),
            """
            {
              "licenseUrl": "https://mirror.internal/LICENSE.txt",
              "licenseSha256": null,
              "minimumLicenseBytes": 100,
              "licenseNotice": "Custom model attribution"
            }
            """);
        var options = SpeechModelOptions.Load(_dir);

        Assert.Equal("https://mirror.internal/LICENSE.txt", options.LicenseUrl);
        Assert.Null(options.LicenseSha256);
        Assert.Equal(100, options.MinimumLicenseBytes);
        Assert.Equal("Custom model attribution", options.LicenseNotice);
    }

    private sealed class StubHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) => send(request, cancellationToken);
    }

    private static void WriteFile(string path, long length)
    {
        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        stream.SetLength(length);
    }
}

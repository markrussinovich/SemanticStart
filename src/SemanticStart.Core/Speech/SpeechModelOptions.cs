using System.Text.Json;

namespace SemanticStart.Core.Speech;

/// <summary>
/// Where the speech models come from and how small a file is too small to be one of them.
///
/// <para>
/// Separated from <see cref="SpeechModelBootstrapper"/> so the download logic holds no literals:
/// a different mirror, an air-gapped internal host, or a newer model revision is then a data
/// change rather than a code change. The defaults are the published upstream locations, so the
/// ordinary case needs no file at all.
/// </para>
/// <para>
/// Overrides are read from <c>speech-model.json</c> in the app's data directory with
/// <see cref="JsonSerializer"/>, which is the same mechanism <c>settings.json</c> already uses.
/// <c>Microsoft.Extensions.Configuration</c> and <c>IOptions&lt;T&gt;</c> are deliberately not used
/// here: both are resolved from a host's service provider, and this app has no container - it is
/// composed by hand in <c>App.OnStartup</c> - so they would add a dependency without adding the
/// lifetime management that is the reason to take one.
/// </para>
/// <para>
/// <b>Licensing.</b> The code that reads these files (sherpa-onnx) is Apache-2.0, but the encoder,
/// decoder and joiner weights at <see cref="EncoderUrl"/>/<see cref="DecoderUrl"/>/
/// <see cref="JoinerUrl"/> are exported from NVIDIA's <c>nvidia/parakeet-unified-en-0.6b</c> and are
/// governed by the separate NVIDIA Open Model License
/// (<see href="https://www.nvidia.com/en-us/agreements/enterprise-software/nvidia-open-model-license/"/>,
/// model card at <see href="https://huggingface.co/nvidia/parakeet-unified-en-0.6b"/>), not by
/// sherpa's license. A user enabling dictation is agreeing to that license for the model weights,
/// which is why the download only happens after the Settings consent gate - see
/// <c>SherpaOnnxSpeechProvider</c>. The complete agreement and required Notice attribution are
/// retained beside the weights; <c>README.md</c> documents their locations.
/// </para>
/// </summary>
public sealed record SpeechModelOptions
{
    /// <summary>The file read by <see cref="Load"/> when it exists.</summary>
    public const string FileName = "speech-model.json";

    // The sherpa-onnx project publishes this model as a single .tar.bz2 GitHub release asset (in
    // the "asr-models" release on k2-fsa/sherpa-onnx), which is not fetchable file-by-file over
    // plain HTTP and would need a bzip2/tar dependency this app does not otherwise have. The same
    // four files are mirrored individually - unarchived - on Hugging Face under csukuangfj2's
    // account, which is how every other model this bootstrapper fetches is hosted, so one URL
    // scheme (plain HTTPS GET, no archive extraction) covers all of them.
    private const string ParakeetBaseUrl =
        "https://huggingface.co/csukuangfj2/sherpa-onnx-nemo-parakeet-unified-en-0.6b-int8-streaming-560ms/resolve/main/";

    /// <summary>
    /// Identifies the model on disk and in traces. Part of every downloaded file's name, so two
    /// revisions can sit side by side and switching back does not re-download.
    /// </summary>
    public string ModelId { get; init; } = "sherpa-onnx-nemo-parakeet-unified-en-0.6b-int8-streaming-560ms";

    public string EncoderUrl { get; init; } = ParakeetBaseUrl + "encoder.int8.onnx";

    public string DecoderUrl { get; init; } = ParakeetBaseUrl + "decoder.int8.onnx";

    public string JoinerUrl { get; init; } = ParakeetBaseUrl + "joiner.int8.onnx";

    public string TokensUrl { get; init; } = ParakeetBaseUrl + "tokens.txt";

    /// <summary>
    /// Complete plain-text agreement from NVIDIA's NVlabs repository, pinned to a revision.
    /// The Parakeet model repository links the agreement but does not host a LICENSE file.
    /// Mirrors must also supply the agreement, not the HTML agreement page.
    /// </summary>
    public string LicenseUrl { get; init; } =
        "https://raw.githubusercontent.com/NVlabs/GRAIL/8b9afa5c0b10e8d26ea066c8475893eec8ea9165/imports/SONIC/decoupled_wbc/sim2mujoco/resources/robots/g1/policy/NVIDIA%20Open%20Model%20License";

    public string? LicenseSha256 { get; init; } =
        "a0c4f6a35dd2c858c86069d84140052831820e78f3074a2fb758bb4156b66994";

    public long MinimumLicenseBytes { get; init; } = 8_000;

    /// <summary>
    /// Section 3.1 requires this attribution in a Notice text file alongside the weights.
    /// Override together with the license when configuring a differently licensed model.
    /// </summary>
    public string LicenseNotice { get; init; } =
        "Licensed by NVIDIA Corporation under the NVIDIA Open Model License";

    /// <summary>Silero VAD v5.1, pinned to a tag so a released build's model cannot change.</summary>
    public string VadUrl { get; init; } =
        "https://raw.githubusercontent.com/snakers4/silero-vad/v5.1/src/silero_vad/data/silero_vad.onnx";

    /// <summary>
    /// SHA-256 of silero_vad.onnx at tag v5.1. Verified because the file is small enough that
    /// hashing costs nothing and it is fetched from a raw file URL rather than a release asset.
    /// Null disables the check, which is what pointing <see cref="VadUrl"/> at another build means.
    /// </summary>
    public string? VadSha256 { get; init; } = "2623a2953f6ff3d2c1e61740c6cdb7168133479b267dfef114a4a3cc5bdd788f";

    /// <summary>
    /// Size floors, in bytes. A truncated download is the failure these catch: it leaves a file
    /// that exists, so existence alone would be taken as "already downloaded" forever. The model
    /// files are checked by size rather than by hash because upstream publishes no hashes for them,
    /// and one recorded here would be a claim about one download rather than the model.
    ///
    /// <para>
    /// Floors are set well below the files' actual published sizes - encoder.int8.onnx is
    /// 654,046,389 bytes, decoder.int8.onnx is 7,257,777 bytes, joiner.int8.onnx is 1,735,860
    /// bytes and tokens.txt is 8,952 bytes (total 663,048,978 bytes before the separate Silero
    /// VAD), measured directly from the sherpa-onnx project's <c>asr-models</c> release archive
    /// for this model, whose contents the Hugging Face mirror at <see cref="EncoderUrl"/> serves
    /// unarchived and unchanged - rather than at the real sizes, so a future patch release that
    /// changes the exact byte count by a few percent does not turn into a false "truncated
    /// download" rejection.
    /// </para>
    /// </summary>
    public long MinimumEncoderBytes { get; init; } = 400_000_000;

    public long MinimumDecoderBytes { get; init; } = 3_000_000;

    public long MinimumJoinerBytes { get; init; } = 500_000;

    public long MinimumTokensBytes { get; init; } = 2_000;

    public long MinimumVadBytes { get; init; } = 1_000_000;

    /// <summary>
    /// What the user is told they are about to download, before it starts: the measured
    /// encoder+decoder+joiner+tokens total of 663,048,978 bytes above, plus the Silero VAD
    /// (~2.2 MB, unchanged by this model swap), rounded to a number comfortable to show in a
    /// sentence - about 665 MB (decimal) / 634 MiB - rather than the compressed size of the
    /// upstream .tar.bz2 archive, which this app never downloads as a unit. About nine times the
    /// ~75 MB streaming Zipformer it replaces, because the 0.6B-parameter Conformer encoder is
    /// roughly nine times the parameter count of the small streaming Zipformer.
    /// </summary>
    public long ApproximateDownloadBytes { get; init; } = 665_000_000L;

    /// <summary>The upstream defaults, used whenever no override file is present.</summary>
    public static SpeechModelOptions Default { get; } = new();

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>
    /// Reads <see cref="FileName"/> from <paramref name="directory"/>, falling back to
    /// <see cref="Default"/> when it is absent. Absent is the normal case, so it is not an error;
    /// a file that is present and unreadable is, because silently downloading from somewhere other
    /// than the operator asked for is worse than refusing to start.
    /// </summary>
    public static SpeechModelOptions Load(string? directory = null)
    {
        var path = Path.Combine(directory ?? AppPaths.Root, FileName);
        if (!File.Exists(path))
            return Default;

        try
        {
            return JsonSerializer.Deserialize<SpeechModelOptions>(File.ReadAllText(path), JsonOptions) ?? Default;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            throw new SpeechModelDownloadException($"Could not read the speech model configuration at {path}.", ex);
        }
    }
}

namespace SemanticStart.Core.Speech;

public sealed class SpeechModelDownloadException : Exception
{
    public SpeechModelDownloadException(string message)
        : base(message)
    {
    }

    public SpeechModelDownloadException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

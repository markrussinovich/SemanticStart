namespace SemanticStart.Core.Speech;

/// <summary>Something dictation handled internally, on its way to the application's log.</summary>
/// <param name="Operation">A stable identifier for the step, such as <c>capture.stop</c>.</param>
/// <param name="Message">What happened, in the terms the log reader needs.</param>
/// <param name="Exception">The failure, when there was one.</param>
/// <param name="IsError">Whether this is a failure rather than an observation.</param>
public sealed record SpeechDiagnosticEvent(string Operation, string Message, Exception? Exception, bool IsError);

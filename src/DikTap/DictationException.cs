namespace DikTap;

public enum ErrorKind
{
    Generic,
    Audio,
    Network,
    Auth,
    Api,
    Clipboard,
    NoSpeech,
    Configuration,
}

// Short, actionable message for the user; never contains keys, audio or dictated text.
internal sealed class DictationException : Exception
{
    public ErrorKind Kind { get; }

    // The capture device disappeared or stopped delivering audio; what was already recorded can still be used.
    public bool DeviceLost { get; init; }

    public DictationException(string userMessage, ErrorKind kind = ErrorKind.Generic, Exception? inner = null)
        : base(userMessage, inner)
    {
        Kind = kind;
    }
}

namespace PunchedApi.Application.Notifications;

public sealed class PermanentChannelException : Exception
{
    public PermanentChannelException(string message) : base(message) { }
}

public sealed class RetryableChannelException : Exception
{
    public RetryableChannelException(string message, Exception? innerException = null)
        : base(message, innerException) { }
}
namespace DeezSpoTag.Services.Download;

public sealed class NullActivityLogWriter : IActivityLogWriter
{
    public void Info(string message)
    {
    }

    public void Warn(string message)
    {
    }

    public void Error(string message)
    {
    }
}

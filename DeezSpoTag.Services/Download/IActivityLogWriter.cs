namespace DeezSpoTag.Services.Download;

public interface IActivityLogWriter
{
    void Info(string message);
    void Warn(string message);
    void Error(string message);
}

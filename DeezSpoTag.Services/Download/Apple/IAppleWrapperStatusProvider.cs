namespace DeezSpoTag.Services.Download.Apple;

public interface IAppleWrapperStatusProvider
{
    AppleWrapperStatusSnapshot GetStatus();
}

public sealed record AppleWrapperStatusSnapshot(
    string Status,
    string Message,
    bool NeedsTwoFactor,
    bool WrapperReady)
{
    public static AppleWrapperStatusSnapshot Missing =>
        new("missing", "Wrapper not started.", false, false);
}

public sealed class NullAppleWrapperStatusProvider : IAppleWrapperStatusProvider
{
    public AppleWrapperStatusSnapshot GetStatus()
    {
        return AppleWrapperStatusSnapshot.Missing;
    }
}

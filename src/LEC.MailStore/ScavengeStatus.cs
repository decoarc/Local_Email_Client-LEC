namespace LEC.MailStore;

public enum ScavengeState { NeverScanned, Scanned }

public sealed record ScavengeStatus(ScavengeState State, int RecoveredCount, bool IsVisible)
{
    public static readonly ScavengeStatus NotScanned = new(ScavengeState.NeverScanned, 0, false);
}

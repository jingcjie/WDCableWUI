namespace WDCableWUI.Services;

public enum SessionRole
{
    GroupOwner,
    Client
}

public static class SessionRoleExtensions
{
    public static string GetEventName(this SessionRole role)
    {
        return role switch
        {
            SessionRole.GroupOwner => "groupOwner",
            SessionRole.Client => "client",
            _ => role.ToString()
        };
    }
}

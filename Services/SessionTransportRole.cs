namespace WDCableWUI.Services;

public enum SessionTransportRole
{
    Listener,
    Connector
}

public static class SessionTransportRoleExtensions
{
    public static string GetEventName(this SessionTransportRole role)
    {
        return role switch
        {
            SessionTransportRole.Listener => "listener",
            SessionTransportRole.Connector => "connector",
            _ => role.ToString()
        };
    }

    public static SessionTransportRole GetTransportRole(this SessionRole role)
    {
        return role switch
        {
            SessionRole.Client => SessionTransportRole.Listener,
            SessionRole.GroupOwner => SessionTransportRole.Connector,
            _ => SessionTransportRole.Connector
        };
    }

    public static SessionTransportRole GetPeerRole(this SessionTransportRole role)
    {
        return role switch
        {
            SessionTransportRole.Listener => SessionTransportRole.Connector,
            SessionTransportRole.Connector => SessionTransportRole.Listener,
            _ => role
        };
    }
}

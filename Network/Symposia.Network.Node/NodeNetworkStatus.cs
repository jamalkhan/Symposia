namespace Symposia.Network.Node;

/// <summary>
/// How far this node has got joining the network, for its readiness endpoint (BlobStorage #1 FR-25):
/// whether its on-chain registration is confirmed and whether a discovery cycle has completed. A step
/// this host doesn't run (registration or discovery disabled, or no blockchain gateway) counts as done.
/// </summary>
public sealed class NodeNetworkStatus
{
    private volatile bool _registrationConfirmed;
    private volatile bool _networkJoined;

    public bool RegistrationConfirmed => _registrationConfirmed;

    public bool NetworkJoined => _networkJoined;

    public void ConfirmRegistration() => _registrationConfirmed = true;

    public void MarkNetworkJoined() => _networkJoined = true;
}

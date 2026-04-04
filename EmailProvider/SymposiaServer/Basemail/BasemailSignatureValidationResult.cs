namespace NativeSmtpReceiver;

public sealed record BasemailSignatureValidationResult(
    bool Succeeded,
    BasemailPeerRecord? Peer,
    string? ErrorMessage);

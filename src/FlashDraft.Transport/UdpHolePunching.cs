using System.Net;
using System.Net.Sockets;
using System.Text;
using FlashDraft.Core;

namespace FlashDraft.Transport;

public enum PunchState
{
    Idle,
    StunResolved,
    Probing,
    Succeeded,
    Failed,
}

/// <summary>
/// WAN UDP hole punching. A public STUN server supplies the reflexive address,
/// which is carried inside the manually exchanged connection code. No TURN relay
/// is operated: on failure the caller shows a clear message and re-negotiates a
/// new connection code (or falls back to LAN).
/// </summary>
public sealed class UdpHolePunchingService : IHolePunchingService
{
    public PunchState State { get; private set; } = PunchState.Idle;

    public async Task<HolePunchResult> PunchAsync(
        TransferSession session,
        string remoteEndpoint,
        CancellationToken cancellationToken)
    {
        // Adjustable: a real STUN binding request to a public server belongs here.
        // The reflexive address is already embedded in session.StunEndpoint by the caller.
        State = PunchState.StunResolved;
        (string host, int port) = ParseEndpoint(remoteEndpoint);

        using var client = new UdpClient(AddressFamily.InterNetwork);
        State = PunchState.Probing;
        byte[] probe = Encoding.UTF8.GetBytes($"PROBE:{session.SessionId:D}");

        for (int i = 0; i < ProtocolConstants.HolePunchProbeCount; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await client.SendAsync(probe, host, port).ConfigureAwait(false);
            await Task.Delay(ProtocolConstants.HolePunchProbeIntervalMs, cancellationToken).ConfigureAwait(false);
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(ProtocolConstants.HolePunchTimeoutMs);
        try
        {
            UdpReceiveResult reply = await client.ReceiveAsync(timeout.Token).ConfigureAwait(false);
            if (Encoding.UTF8.GetString(reply.Buffer).StartsWith($"PROBE-ACK:{session.SessionId:D}", StringComparison.Ordinal))
            {
                State = PunchState.Succeeded;
                return new HolePunchResult(true, null, reply.RemoteEndPoint.ToString());
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // Timeout is an expected outcome for symmetric NAT.
        }

        State = PunchState.Failed;
        return new HolePunchResult(
            false,
            "Hole punching failed (possible symmetric NAT). Use LAN, manual port mapping, or exchange a new connection code. TURN relay is not implemented.",
            null);
    }

    private static (string Host, int Port) ParseEndpoint(string endpoint)
    {
        int separator = endpoint.LastIndexOf(':');
        if (separator < 0)
        {
            throw new FormatException("Endpoint must be host:port.");
        }

        return (endpoint.Substring(0, separator), int.Parse(endpoint.Substring(separator + 1)));
    }
}

using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using FlashDraft.Core;
using FlashDraft.Transport;
using Xunit;

namespace FlashDraft.Core.Tests;

/// <summary>
/// Headless coverage for dual-stack STUN parsing, v2 offer codes, endpoint
/// ordering, NAT typing, bilingual diagnostics and two-peer punching. No test
/// touches the public Internet.
/// </summary>
public class HolePunchingTests
{
    [Fact]
    public void Stun_ParsesIpv6XorMappedAddress()
    {
        uint cookie = ProtocolConstants.StunMagicCookie;
        byte[] transaction = RandomNumberGenerator.GetBytes(12);
        byte[] packet = new byte[20 + 4 + 20];
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(0, 2), 0x0101);
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(2, 2), 20);
        BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(4, 4), cookie);
        transaction.CopyTo(packet.AsSpan(8, 12));
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(20, 2), 0x0020);
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(22, 2), 20);

        int xorPort = 51000 ^ (int)(cookie >> 16);
        packet[24] = 0;
        packet[25] = 0x02;
        packet[26] = (byte)(xorPort >> 8);
        packet[27] = (byte)xorPort;

        IPAddress v6 = IPAddress.Parse("2001:db8::1");
        byte[] raw = v6.GetAddressBytes();
        byte[] mask = new byte[16];
        BinaryPrimitives.WriteUInt32BigEndian(mask.AsSpan(0, 4), cookie);
        transaction.CopyTo(mask.AsSpan(4, 12));
        for (int i = 0; i < 16; i++) packet[28 + i] = (byte)(raw[i] ^ mask[i]);

        string? parsed = StunClient.ParseMappedAddress(packet);
        Assert.Equal("[2001:db8::1]:51000", parsed);
        Assert.Equal("[2001:db8::1]:51000", StunClient.ParseBindingResponse(packet, transaction));
    }

    [Fact]
    public void Stun_RejectsForgedTransactionId()
    {
        byte[] transaction = RandomNumberGenerator.GetBytes(12);
        byte[] packet = new byte[32];
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(0, 2), 0x0101);
        BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(4, 4), ProtocolConstants.StunMagicCookie);
        transaction.CopyTo(packet.AsSpan(8, 12));
        byte[] wrong = (byte[])transaction.Clone();
        wrong[0] ^= 0xFF;
        Assert.Null(StunClient.ParseBindingResponse(packet, wrong));
    }

    [Fact]
    public void Stun_RejectsWrongMessageType()
    {
        byte[] transaction = RandomNumberGenerator.GetBytes(12);
        byte[] packet = new byte[32];
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(0, 2), 0x0001);
        BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(4, 4), ProtocolConstants.StunMagicCookie);
        transaction.CopyTo(packet.AsSpan(8, 12));
        Assert.Null(StunClient.ParseBindingResponse(packet, transaction));
    }

    private static SessionOffer MakeOffer(bool v2, DateTimeOffset now)
    {
        byte[] publicKey = RandomNumberGenerator.GetBytes(32);
        byte[] token = RandomNumberGenerator.GetBytes(ProtocolConstants.TokenSize);
        var lan = new[] { "192.168.1.20:45791" };
        if (!v2)
        {
            return new SessionOffer(Guid.NewGuid(), publicKey, now.AddMinutes(5), token,
                lan, "203.0.113.9:51000");
        }

        return new SessionOffer(Guid.NewGuid(), publicKey, now.AddMinutes(5), token,
            lan, "203.0.113.9:51000")
        {
            Endpoints = new[]
            {
                "[2001:db8::1]:51000",
                "203.0.113.9:51000",
                "192.168.1.20:45791",
            },
            NatTypeHint = NatType.PortRestrictedCone.ToString(),
        };
    }

    [Fact]
    public void OfferCode_V2_RoundTrip_PreservesEndpointsAndNatHint()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        SessionOffer offer = MakeOffer(v2: true, now);
        string code = ConnectionCode.EncodeOffer(offer);
        SessionOffer decoded = ConnectionCode.DecodeOffer(code, now);

        Assert.Equal(offer.Endpoints, decoded.Endpoints);
        Assert.Equal(offer.NatTypeHint, decoded.NatTypeHint);
        Assert.Equal(offer.ReflexiveEndpoint, decoded.ReflexiveEndpoint);
        Assert.Equal(3, EndpointSet.FromOffer(decoded).Count);
    }

    [Fact]
    public void OfferCode_V1_StillDecodes_WithEmptyEndpoints()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        SessionOffer offer = MakeOffer(v2: false, now);
        SessionOffer decoded = ConnectionCode.DecodeOffer(ConnectionCode.EncodeOffer(offer), now);

        Assert.Empty(decoded.Endpoints);
        Assert.Equal("203.0.113.9:51000", decoded.ReflexiveEndpoint);
        Assert.Equal(2, EndpointSet.FromOffer(decoded).Count);
    }

    [Fact]
    public void EndpointSet_OrdersIpv6ThenIpv4ThenLan_AndSkipsLinkLocal()
    {
        var set = EndpointSet.Build(
            "[2001:db8::5]:5000",
            "203.0.113.9:51000",
            new[] { "192.168.1.20:45791", "[fe80::1]:5000", "127.0.0.1:45791" });

        Assert.Equal(
            new[] { "[2001:db8::5]:5000", "203.0.113.9:51000", "192.168.1.20:45791" },
            set.Ordered);
        Assert.Equal(AddressFamily.InterNetworkV6, EndpointSet.FamilyOf(set.Ordered[0]));
    }

    [Fact]
    public void NatClassifier_VaryingPorts_MeansSymmetric()
    {
        Assert.Equal(NatType.Symmetric, NatClassifier.FromSamples(new[] { 51000, 51001, 51002 }));
        Assert.Equal(NatType.PortRestrictedCone, NatClassifier.FromSamples(new[] { 51000, 51000 }));
        Assert.Equal(NatType.UdpBlocked, NatClassifier.FromSamples(Array.Empty<int>()));
        Assert.Equal(NatType.Unknown, NatClassifier.FromSamples(new[] { 51000 }));
    }

    [Fact]
    public void Diagnostics_SymmetricNat_ProducesDetailedBilingualFailure()
    {
        PunchFailure failure = PunchDiagnostics.Build(
            NatType.Symmetric, NatType.PortRestrictedCone, "probe");

        Assert.Equal("NET-SYM-PRC-02", failure.Code);
        Assert.Contains("Symmetric", failure.ReasonEn);
        Assert.Contains("对称型", failure.ReasonZh);
        Assert.Equal(4, failure.Suggestions.Count);
        var exception = new PunchFailedException(failure);
        Assert.Contains("NET-SYM-PRC-02", exception.Message);
    }

    [Fact]
    public void PunchWire_RejectsTamperedAndOversizedDatagrams()
    {
        Guid sid = Guid.NewGuid();
        byte[] frame = System.Text.Encoding.UTF8.GetBytes(
            PunchWire.Frame("PROBE", sid, "local:5000"));
        Assert.NotNull(PunchWire.Parse(frame, sid));

        byte[] tampered = (byte[])frame.Clone();
        tampered[3] ^= 0x01;
        Assert.Null(PunchWire.Parse(tampered, sid));
        Assert.Null(PunchWire.Parse(frame, Guid.NewGuid()));
        Assert.Null(PunchWire.Parse(new byte[ProtocolConstants.MaxPunchPacketBytes + 1], sid));
    }

    [Fact]
    public async Task WanPunch_TwoPeers_Loopback_Succeeds()
    {
        Guid sid = Guid.NewGuid();
        byte[] token = RandomNumberGenerator.GetBytes(ProtocolConstants.TokenSize);
        int portA = ReserveUdpPort();
        int portB = ReserveUdpPort();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        var responder = new WanPunchService();
        var initiator = new WanPunchService();

        Task<PunchOutcome> b = responder.PunchAsync(
            new PunchOptions(sid, token, new[] { "127.0.0.1:" + portA },
                LocalPort: portB, EnableIpv6: false, Window: TimeSpan.FromSeconds(12)),
            cts.Token);
        await Task.Delay(120, cts.Token);
        Task<PunchOutcome> a = initiator.PunchAsync(
            new PunchOptions(sid, token, new[] { "127.0.0.1:" + portB },
                LocalPort: portA, EnableIpv6: false, IsInitiator: true,
                Window: TimeSpan.FromSeconds(12)),
            cts.Token);

        PunchOutcome[] results = await Task.WhenAll(a, b).WaitAsync(TimeSpan.FromSeconds(18));
        Assert.True(results[0].Succeeded, "initiator did not punch through");
        Assert.True(results[1].Succeeded, "responder did not punch through");
        Assert.NotNull(results[0].Endpoint);
        Assert.NotNull(results[1].Endpoint);
        results[0].Endpoint!.Client.Dispose();
        results[1].Endpoint!.Client.Dispose();
    }

    [Fact]
    public void StunBackoff_HasFiveAscendingSteps()
    {
        Assert.Equal(ProtocolConstants.StunMaxAttempts, ProtocolConstants.StunBackoffMs.Length);
        for (int i = 1; i < ProtocolConstants.StunBackoffMs.Length; i++)
        {
            Assert.True(ProtocolConstants.StunBackoffMs[i] > ProtocolConstants.StunBackoffMs[i - 1]);
        }

        Assert.Equal(4, ProtocolConstants.StunServerEndpoints.Length);
    }

    private static int ReserveUdpPort()
    {
        using var probe = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        return ((IPEndPoint)probe.Client.LocalEndPoint!).Port;
    }
}

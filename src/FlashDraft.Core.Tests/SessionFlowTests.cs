using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using FlashDraft.Crypto;
using FlashDraft.Transport;
using Xunit;

namespace FlashDraft.Core.Tests;

/// <summary>
/// End-to-end session tests: offer exchange, key binding, LAN transfer and
/// WAN UDP transfer. All runs are headless and use loopback sockets only.
/// </summary>
public class SessionFlowTests
{
    [Fact]
    public void OfferCode_RoundTrip_CarriesReflexiveEndpoint()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        var offer = new SessionOffer(
            Guid.NewGuid(),
            RandomNumberGenerator.GetBytes(32),
            now.AddMinutes(5),
            RandomNumberGenerator.GetBytes(ProtocolConstants.TokenSize),
            new[] { "192.168.1.20:45791" },
            "203.0.113.9:51000");

        string code = ConnectionCode.EncodeOffer(offer);
        SessionOffer decoded = ConnectionCode.DecodeOffer(code, now);

        Assert.StartsWith("flashdraft://", code);
        Assert.Equal(offer.SessionId, decoded.SessionId);
        Assert.Equal(offer.OffererPublicKey, decoded.OffererPublicKey);
        Assert.Equal(offer.OneTimeToken, decoded.OneTimeToken);
        Assert.Equal(offer.ReflexiveEndpoint, decoded.ReflexiveEndpoint);
        Assert.Equal(offer.LanCandidates, decoded.LanCandidates);
    }

    [Fact]
    public void ExpiredOffer_IsRejected()
    {
        DateTimeOffset past = DateTimeOffset.UtcNow.AddMinutes(-10);
        var offer = new SessionOffer(
            Guid.NewGuid(), RandomNumberGenerator.GetBytes(32),
            past.AddMinutes(1), RandomNumberGenerator.GetBytes(ProtocolConstants.TokenSize),
            Array.Empty<string>(), null);

        string code = ConnectionCode.EncodeOffer(offer);
        Assert.Throws<InvalidOperationException>(
            () => ConnectionCode.DecodeOffer(code, DateTimeOffset.UtcNow));
    }

    [Fact]
    public void BothPeers_DeriveTheSameSessionKeys()
    {
        var aKeys = new EphemeralKeyPair();
        var bKeys = new EphemeralKeyPair();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        var offerA = MakeOffer(aKeys.PublicKey, now);
        var offerB = MakeOffer(bKeys.PublicKey, now);

        SessionKeys a = SecureSession.Bind(aKeys, offerA, offerB, out Guid sidA);
        SessionKeys b = SecureSession.Bind(bKeys, offerB, offerA, out Guid sidB);

        Assert.Equal(sidA, sidB);
        Assert.Equal(a.SendKey, b.ReceiveKey);
        Assert.Equal(a.ReceiveKey, b.SendKey);
        Assert.Equal(a.SessionPrefix, b.SessionPrefix);
    }

    [Fact]
    public void Stun_ParsesXorMappedAddress()
    {
        uint cookie = ProtocolConstants.StunMagicCookie;
        byte[] packet = new byte[32];
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(0, 2), 0x0101);
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(2, 2), 8);
        BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(4, 4), cookie);
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(20, 2), 0x0020);
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(22, 2), 8);
        packet[24] = 0;
        packet[25] = 0x01;
        int xorPort = 51000 ^ (int)(cookie >> 16);
        packet[26] = (byte)(xorPort >> 8);
        packet[27] = (byte)xorPort;
        uint addr = 0x01020304u ^ cookie;
        BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(28, 4), addr);

        Assert.Equal("1.2.3.4:51000", StunClient.ParseMappedAddress(packet));
    }

    [Fact]
    public async Task Lan_FullSession_TransfersAFile()
    {
        int port = ReserveTcpPort();
        string root = Path.Combine(Path.GetTempPath(), "fd-lan-" + Guid.NewGuid().ToString("N"));
        string src = Path.Combine(root, "src");
        string dst = Path.Combine(root, "dst");
        Directory.CreateDirectory(src);
        Directory.CreateDirectory(dst);
        try
        {
            byte[] content = RandomNumberGenerator.GetBytes(ProtocolConstants.ChunkSize * 2 + 137);
            string sourcePath = Path.Combine(src, "sample.bin");
            File.WriteAllBytes(sourcePath, content);
            var descriptor = MakeDescriptor(content, "sample.bin", ProtocolConstants.ChunkSize);
            var progress = new ChannelProgressReporter<ProgressSnapshot>();

            var a = new SessionOrchestrator(new NullStunClient(), lanPort: port);
            var b = new SessionOrchestrator(new NullStunClient(), lanPort: port);
            DateTimeOffset now = DateTimeOffset.UtcNow;
            var offerA = new SessionOffer(Guid.NewGuid(), a.LocalPublicKey,
                now.AddMinutes(5), RandomNumberGenerator.GetBytes(ProtocolConstants.TokenSize),
                new[] { "127.0.0.1:" + port }, null);
            var offerB = new SessionOffer(Guid.NewGuid(), b.LocalPublicKey,
                now.AddMinutes(5), RandomNumberGenerator.GetBytes(ProtocolConstants.TokenSize),
                Array.Empty<string>(), null);

            var (keysA, sidA) = a.Bind(offerA, offerB);
            var (keysB, sidB) = b.Bind(offerB, offerA);

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(25));
            Task receive = a.ReceiveAsync(offerA, sidA, keysA, dst, progress, false, cts.Token);
            await Task.Delay(150, cts.Token);
            Task send = b.SendAsync(offerA, sidB, keysB,
                new[] { descriptor }, new[] { sourcePath }, progress, false, cts.Token);
            await Task.WhenAll(receive, send).WaitAsync(TimeSpan.FromSeconds(30));
            progress.Complete();

            Assert.Equal(content, File.ReadAllBytes(Path.Combine(dst, "sample.bin")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Wan_UdpLoopback_TransfersAFile()
    {
        int portA = ReserveUdpPort();
        int portB = ReserveUdpPort();
        string root = Path.Combine(Path.GetTempPath(), "fd-wan-" + Guid.NewGuid().ToString("N"));
        string src = Path.Combine(root, "src");
        string dst = Path.Combine(root, "dst");
        Directory.CreateDirectory(src);
        Directory.CreateDirectory(dst);
        try
        {
            byte[] content = RandomNumberGenerator.GetBytes(ProtocolConstants.WanChunkSize * 2 + 91);
            string sourcePath = Path.Combine(src, "wan.bin");
            File.WriteAllBytes(sourcePath, content);
            var descriptor = MakeDescriptor(content, "wan.bin", ProtocolConstants.WanChunkSize);
            var progress = new ChannelProgressReporter<ProgressSnapshot>();

            var a = new SessionOrchestrator(new NullStunClient(), wanPort: portA);
            var b = new SessionOrchestrator(new NullStunClient(), wanPort: portB);
            DateTimeOffset now = DateTimeOffset.UtcNow;
            var offerA = new SessionOffer(Guid.NewGuid(), a.LocalPublicKey,
                now.AddMinutes(5), RandomNumberGenerator.GetBytes(ProtocolConstants.TokenSize),
                Array.Empty<string>(), "127.0.0.1:" + portA);
            var offerB = new SessionOffer(Guid.NewGuid(), b.LocalPublicKey,
                now.AddMinutes(5), RandomNumberGenerator.GetBytes(ProtocolConstants.TokenSize),
                Array.Empty<string>(), null);

            var (keysA, sidA) = a.Bind(offerA, offerB);
            var (keysB, sidB) = b.Bind(offerB, offerA);

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(25));
            Task receive = a.ReceiveAsync(offerA, sidA, keysA, dst, progress, true, cts.Token);
            await Task.Delay(120, cts.Token);
            Task send = b.SendAsync(offerA, sidB, keysB,
                new[] { descriptor }, new[] { sourcePath }, progress, true, cts.Token);
            await Task.WhenAll(receive, send).WaitAsync(TimeSpan.FromSeconds(30));
            progress.Complete();

            Assert.Equal(content, File.ReadAllBytes(Path.Combine(dst, "wan.bin")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static SessionOffer MakeOffer(byte[] publicKey, DateTimeOffset now) =>
        new(Guid.NewGuid(), publicKey, now.AddMinutes(5),
            RandomNumberGenerator.GetBytes(ProtocolConstants.TokenSize),
            Array.Empty<string>(), null);

    private static FileDescriptor MakeDescriptor(byte[] content, string name, int chunkSize)
    {
        uint total = (uint)((content.Length + chunkSize - 1) / chunkSize);
        return new FileDescriptor(0, name, content.Length, total, SHA256.HashData(content))
        {
            ChunkSize = chunkSize,
        };
    }

    private static int ReserveTcpPort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        int port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    private static int ReserveUdpPort()
    {
        using var probe = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        return ((IPEndPoint)probe.Client.LocalEndPoint!).Port;
    }
}

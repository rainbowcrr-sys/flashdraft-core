using System.Security.Cryptography;
using FlashDraft.Crypto;
using Xunit;

namespace FlashDraft.Core.Tests;

public class ProtocolTests
{
    [Fact]
    public void FrameHeader_RoundTrip_PreservesFields()
    {
        byte[] hash = SHA256.HashData(new byte[] { 1, 2, 3 });
        var original = new FrameHeader
        {
            Type = FrameType.Data,
            SessionId = Guid.NewGuid(),
            FileIndex = 7,
            ChunkIndex = 42,
            PayloadLength = 256 * 1024 + 16,
            ChunkHash = hash,
        };

        byte[] buffer = new byte[ProtocolConstants.FrameHeaderSize];
        original.WriteTo(buffer);
        FrameHeader parsed = FrameHeader.Read(buffer);

        Assert.Equal(original.SessionId, parsed.SessionId);
        Assert.Equal(original.Type, parsed.Type);
        Assert.Equal(original.FileIndex, parsed.FileIndex);
        Assert.Equal(original.ChunkIndex, parsed.ChunkIndex);
        Assert.Equal(original.PayloadLength, parsed.PayloadLength);
        Assert.Equal(hash, parsed.ChunkHash);
    }

    [Fact]
    public void ConnectionCode_RoundTrip_PreservesSession()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        var session = new TransferSession(
            Guid.NewGuid(),
            RandomNumberGenerator.GetBytes(32),
            now,
            now.AddMinutes(5),
            RandomNumberGenerator.GetBytes(ProtocolConstants.TokenSize),
            new[] { "192.168.1.20:45791" },
            "203.0.113.9:51000");

        string code = ConnectionCode.Encode(session);
        TransferSession decoded = ConnectionCode.Decode(code, now);

        Assert.StartsWith("flashdraft://", code);
        Assert.Equal(session.SessionId, decoded.SessionId);
        Assert.Equal(session.EphemeralPublicKey, decoded.EphemeralPublicKey);
        Assert.Equal(session.OneTimeToken, decoded.OneTimeToken);
        Assert.Equal(session.StunEndpoint, decoded.StunEndpoint);
        Assert.Equal(session.LanCandidates, decoded.LanCandidates);
    }

    [Fact]
    public void ConnectionCode_Expired_Throws()
    {
        DateTimeOffset past = DateTimeOffset.UtcNow.AddMinutes(-10);
        var session = new TransferSession(
            Guid.NewGuid(),
            RandomNumberGenerator.GetBytes(32),
            past,
            past.AddMinutes(1),
            RandomNumberGenerator.GetBytes(ProtocolConstants.TokenSize),
            Array.Empty<string>(),
            null);

        string code = ConnectionCode.Encode(session);
        Assert.Throws<InvalidOperationException>(() => ConnectionCode.Decode(code, DateTimeOffset.UtcNow));
    }

    [Fact]
    public void Nonce_DiffersPerChunk_AndIsStableForSameChunk()
    {
        byte[] prefix = RandomNumberGenerator.GetBytes(4);
        byte[] a = NonceBuilder.Build(prefix, 3, 11);
        byte[] b = NonceBuilder.Build(prefix, 3, 12);
        byte[] c = NonceBuilder.Build(prefix, 3, 11);

        Assert.Equal(ProtocolConstants.NonceSize, a.Length);
        Assert.NotEqual(a, b);
        Assert.Equal(a, c);
    }

    [Fact]
    public void AesGcm_RoundTrip_AndAadTamperFails()
    {
        byte[] key = RandomNumberGenerator.GetBytes(ProtocolConstants.KeySize);
        byte[] nonce = NonceBuilder.Build(RandomNumberGenerator.GetBytes(4), 0, 0);
        byte[] aad = new byte[ProtocolConstants.FrameHeaderSize];
        new FrameHeader { SessionId = Guid.NewGuid() }.WriteTo(aad);
        byte[] plaintext = RandomNumberGenerator.GetBytes(4096);

        byte[] cipher = AesGcmChunkCipher.Seal(key, nonce, plaintext, aad);
        Assert.Equal(plaintext, AesGcmChunkCipher.Open(key, nonce, cipher, aad));

        byte[] tampered = (byte[])aad.Clone();
        tampered[24] ^= 0xFF;
        // AuthenticationTagMismatchException derives from CryptographicException.
        Assert.ThrowsAny<CryptographicException>(() => AesGcmChunkCipher.Open(key, nonce, cipher, tampered));
    }

    [Fact]
    public void ChunkBitmap_TracksCompleteness()
    {
        var bitmap = new ChunkBitmap(10);
        Assert.False(bitmap.IsComplete);
        for (uint i = 0; i < 10; i++)
        {
            bitmap.Set(i);
        }

        Assert.True(bitmap.IsComplete);
        Assert.True(ChunkBitmap.FromBase64(10, bitmap.ToBase64()).IsComplete);
    }

    [Fact]
    public void Pin_HasSixDigits()
    {
        string pin = ConnectionCode.DerivePin(RandomNumberGenerator.GetBytes(ProtocolConstants.TokenSize));
        Assert.Equal(ProtocolConstants.PinLength, pin.Length);
        Assert.All(pin, c => Assert.Contains(c, "0123456789"));
    }
}

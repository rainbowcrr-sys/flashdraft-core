using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using FlashDraft.Crypto;
using FlashDraft.Transport;
using Xunit;

namespace FlashDraft.Core.Tests;

/// <summary>
/// Two-endpoint loopback integration test on 127.0.0.1. It exercises key
/// agreement, frame framing, AES-GCM, chunk verification, resume metadata and
/// final file commit without any external service.
/// </summary>
public class LoopbackTransferTests
{
    [Fact]
    public async Task TwoEndpoints_TransferFile_AndVerify()
    {
        Guid sessionId = Guid.NewGuid();
        var initiator = new EphemeralKeyPair();
        var responder = new EphemeralKeyPair();

        byte[] sharedInitiator = initiator.CalculateSharedSecret(responder.PublicKey);
        byte[] sharedResponder = responder.CalculateSharedSecret(initiator.PublicKey);
        SessionKeys senderKeys = KeyDerivation.Derive(
            sharedInitiator, sessionId, initiator.PublicKey, responder.PublicKey, isInitiator: true);
        SessionKeys receiverKeys = KeyDerivation.Derive(
            sharedResponder, sessionId, responder.PublicKey, initiator.PublicKey, isInitiator: false);

        Assert.Equal(senderKeys.SendKey, receiverKeys.ReceiveKey);
        Assert.Equal(senderKeys.ReceiveKey, receiverKeys.SendKey);
        Assert.Equal(senderKeys.SessionPrefix, receiverKeys.SessionPrefix);

        string root = Path.Combine(Path.GetTempPath(), "flashdraft-" + Guid.NewGuid().ToString("N"));
        string sourceDirectory = Path.Combine(root, "src");
        string destinationDirectory = Path.Combine(root, "dst");
        Directory.CreateDirectory(sourceDirectory);
        Directory.CreateDirectory(destinationDirectory);
        try
        {
            byte[] content = RandomNumberGenerator.GetBytes(ProtocolConstants.ChunkSize * 2 + 137);
            string sourcePath = Path.Combine(sourceDirectory, "sample.bin");
            File.WriteAllBytes(sourcePath, content);
            uint totalChunks = (uint)((content.Length + ProtocolConstants.ChunkSize - 1) / ProtocolConstants.ChunkSize);
            var descriptor = new FileDescriptor(
                0, "sample.bin", content.Length, totalChunks, SHA256.HashData(content));

            DateTimeOffset now = DateTimeOffset.UtcNow;
            var session = new TransferSession(
                sessionId,
                initiator.PublicKey,
                now,
                now.AddMinutes(5),
                RandomNumberGenerator.GetBytes(ProtocolConstants.TokenSize),
                Array.Empty<string>(),
                null);

            int port = ReserveLoopbackPort();
            var progress = new ChannelProgressReporter<ProgressSnapshot>();
            var sender = new LanTcpTransferService(senderKeys, port);
            var receiver = new LanTcpTransferService(receiverKeys, port);

            Task receiveTask = receiver.ReceiveAsync(
                session, destinationDirectory, progress, CancellationToken.None);
            await Task.Delay(100);
            Task sendTask = sender.SendAsync(
                session,
                new[] { descriptor },
                new[] { sourcePath },
                $"127.0.0.1:{port}",
                progress,
                CancellationToken.None);

            await Task.WhenAll(sendTask, receiveTask);
            progress.Complete();

            string destinationPath = Path.Combine(destinationDirectory, "sample.bin");
            Assert.True(File.Exists(destinationPath));
            Assert.Equal(content, File.ReadAllBytes(destinationPath));
            Assert.False(File.Exists(destinationPath + ".part"));
            Assert.False(File.Exists(destinationPath + ".meta"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static int ReserveLoopbackPort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        int port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }
}

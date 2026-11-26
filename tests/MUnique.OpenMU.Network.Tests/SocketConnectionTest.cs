// <copyright file="SocketConnectionTest.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.Network.Tests;

using System.Buffers;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using Microsoft.Extensions.Logging.Abstractions;
using MUnique.OpenMU.Network.SimpleModulus;
using MUnique.OpenMU.Network.Xor;
using Pipelines.Sockets.Unofficial;

/// <summary>
/// Tests the connection implementation over real loopback sockets.
/// </summary>
[TestFixture]
public class SocketConnectionTest
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(15);

    /// <summary>
    /// Tests ordered, byte-exact delivery of C1 and C2 packets.
    /// </summary>
    /// <returns>The async task.</returns>
    [Test]
    public async Task TestReceivePipelinedAsync()
    {
        await this.TestReceivePipelinedAsync(false).ConfigureAwait(false);
    }

    /// <summary>
    /// Tests ordered, byte-exact delivery of C3 and C4 packets through client encryption and server decryption.
    /// </summary>
    /// <returns>The async task.</returns>
    [Test]
    public async Task TestReceivePipelinedWithEncryptionAsync()
    {
        await this.TestReceivePipelinedAsync(true).ConfigureAwait(false);
    }

    /// <summary>
    /// Tests that an invalid outgoing header closes the outgoing stream and marks the connection disconnected.
    /// </summary>
    /// <returns>The async task.</returns>
    [Test]
    public async Task TestDisconnectOnInvalidHeaderSentAsync()
    {
        await RunWithSocketsAsync(async (serverSocket, clientSocket, cancellationToken) =>
        {
            using var connection = CreateServerConnection(serverSocket, true);
            var disconnected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            connection.Disconnected += () =>
            {
                disconnected.TrySetResult();
                return ValueTask.CompletedTask;
            };

            var receiveTask = connection.BeginReceiveAsync();
            try
            {
                await connection.Output.WriteAsync(new byte[] { 0xDE, 0xAD, 0xBE, 0xEF }, cancellationToken).ConfigureAwait(false);
                Assert.That(await clientSocket.ReceiveAsync(new byte[1], SocketFlags.None, cancellationToken).ConfigureAwait(false), Is.Zero);
                Assert.That(connection.Connected, Is.False);

                // A failed outgoing pipe closes the sending half. Finish the peer's sending
                // half as well before waiting for the incoming loop and disconnect event.
                clientSocket.Shutdown(SocketShutdown.Send);
                await disconnected.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
                await receiveTask.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                serverSocket.Dispose();
                clientSocket.Dispose();
                await receiveTask.WaitAsync(TestTimeout).ConfigureAwait(false);
            }
        }).ConfigureAwait(false);
    }

    /// <summary>
    /// Tests that an invalid incoming header disconnects the connection without dispatching a packet.
    /// </summary>
    /// <returns>The async task.</returns>
    [Test]
    public async Task TestDisconnectOnInvalidHeaderReceivedAsync()
    {
        await RunWithSocketsAsync(async (serverSocket, clientSocket, cancellationToken) =>
        {
            using var connection = CreateServerConnection(serverSocket, true);
            var receivedPackets = 0;
            connection.PacketReceived += _ =>
            {
                receivedPackets++;
                return ValueTask.CompletedTask;
            };

            var receiveTask = connection.BeginReceiveAsync();
            try
            {
                using var stream = new NetworkStream(clientSocket, ownsSocket: false);
                await stream.WriteAsync(new byte[] { 0xDE, 0xAD, 0xBE, 0xEF }, cancellationToken).ConfigureAwait(false);
                await receiveTask.WaitAsync(cancellationToken).ConfigureAwait(false);
                Assert.That(connection.Connected, Is.False);
                Assert.That(receivedPackets, Is.Zero);
                Assert.That(await stream.ReadAsync(new byte[1], cancellationToken).ConfigureAwait(false), Is.Zero);
            }
            finally
            {
                serverSocket.Dispose();
                clientSocket.Dispose();
                await receiveTask.WaitAsync(TestTimeout).ConfigureAwait(false);
            }
        }).ConfigureAwait(false);
    }

    private static Connection CreateServerConnection(Socket socket, bool encrypted)
    {
        var pipe = SocketConnection.Create(socket);
        return new Connection(pipe, encrypted ? new PipelinedDecryptor(pipe.Input) : null, encrypted ? new PipelinedEncryptor(pipe.Output) : null, NullLogger<Connection>.Instance);
    }

    private static async Task RunWithSocketsAsync(Func<Socket, Socket, CancellationToken, Task> test)
    {
        using var cancellation = new CancellationTokenSource(TestTimeout);
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var clientSocket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        await clientSocket.ConnectAsync(listener.LocalEndpoint, cancellation.Token).ConfigureAwait(false);
        using var serverSocket = await listener.AcceptSocketAsync(cancellation.Token).ConfigureAwait(false);
        await test(serverSocket, clientSocket, cancellation.Token).ConfigureAwait(false);
    }

    private async Task TestReceivePipelinedAsync(bool encrypted)
    {
        const int maximumPacketCount = 1000;

        await RunWithSocketsAsync(async (serverSocket, clientSocket, cancellationToken) =>
        {
            using var receiver = CreateServerConnection(serverSocket, encrypted);
            var clientPipe = SocketConnection.Create(clientSocket);
            IPipelinedEncryptor? encryptor = encrypted
                ? new PipelinedXor32Encryptor(new PipelinedSimpleModulusEncryptor(clientPipe.Output, PipelinedSimpleModulusEncryptor.DefaultClientKey).Writer)
                : null;
            using var sender = new Connection(clientPipe, null, encryptor, NullLogger<Connection>.Instance);
            var received = new List<byte[]>();
            var expected = new List<byte[]>();
            var allReceived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            receiver.PacketReceived += packet =>
            {
                received.Add(packet.ToArray());
                if (received.Count == maximumPacketCount)
                {
                    allReceived.TrySetResult();
                }

                return ValueTask.CompletedTask;
            };

            var receiveTask = receiver.BeginReceiveAsync();
            try
            {
                for (var i = 0; i < maximumPacketCount; i++)
                {
                    var headerSize = i % 2 == 0 ? 2 : 3;
                    var packet = new byte[12];
                    packet[0] = (byte)((headerSize == 2 ? 0xC1 : 0xC2) + (encrypted ? 2 : 0));
                    packet[headerSize - 1] = (byte)packet.Length;
                    packet[headerSize] = 0xF1;
                    BitConverter.TryWriteBytes(packet.AsSpan(headerSize + 1), i);
                    expected.Add(packet);
                    await sender.Output.WriteAsync(packet, cancellationToken).ConfigureAwait(false);
                }

                await allReceived.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
                clientSocket.Shutdown(SocketShutdown.Send);
                await receiveTask.WaitAsync(cancellationToken).ConfigureAwait(false);
                Assert.That(received, Has.Count.EqualTo(maximumPacketCount));
                for (var i = 0; i < maximumPacketCount; i++)
                {
                    Assert.That(received[i], Is.EqualTo(expected[i]), $"Packet {i} must preserve its bytes and order.");
                }
            }
            finally
            {
                serverSocket.Dispose();
                clientSocket.Dispose();
                await receiveTask.WaitAsync(TestTimeout).ConfigureAwait(false);
            }
        }).ConfigureAwait(false);
    }
}

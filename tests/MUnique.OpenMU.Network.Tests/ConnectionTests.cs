// <copyright file="ConnectionTests.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.Network.Tests;

using System.IO.Pipelines;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

/// <summary>
/// Tests for <see cref="Connection"/>.
/// </summary>
[TestFixture]
public class ConnectionTests
{
    /// <summary>
    /// Tests if disconnecting inside a packet handler stops reading the current buffer without
    /// accessing it after the pipe reader completed or dispatching another buffered packet.
    /// </summary>
    /// <param name="completeInput">Whether the producer completed before the reader started.</param>
    /// <returns>The async task.</returns>
    [TestCase(false)]
    [TestCase(true)]
    public async Task DisconnectingInsidePacketHandlerStopsReadingAsync(bool completeInput)
    {
        var duplexPipe = new DuplexPipe();
        var logger = new RecordingConnectionLogger();
        using var connection = new Connection(duplexPipe, null, null, logger);
        var packetCount = 0;
        var disconnectedCount = 0;
        connection.PacketReceived += async packet =>
        {
            packetCount++;
            await connection.DisconnectAsync().ConfigureAwait(false);
        };
        connection.Disconnected += () =>
        {
            disconnectedCount++;
            return ValueTask.CompletedTask;
        };

        // Queue two complete packets before reading so that continuing after the first handler
        // would either dispatch the second packet or access memory released during disconnect.
        var packets = new byte[] { 0xC1, 0x04, 0xF1, 0x00, 0xC1, 0x04, 0xF1, 0x01 };
        await duplexPipe.ReceivePipe.Writer.WriteAsync(packets).ConfigureAwait(false);
        if (completeInput)
        {
            await duplexPipe.ReceivePipe.Writer.CompleteAsync().ConfigureAwait(false);
        }

        await connection.BeginReceiveAsync().WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        await duplexPipe.ReceivePipe.Writer.CompleteAsync().ConfigureAwait(false);

        Assert.Multiple(() =>
        {
            Assert.That(connection.Connected, Is.False);
            Assert.That(packetCount, Is.EqualTo(1), "A buffered packet must not reach a disconnected session.");
            Assert.That(disconnectedCount, Is.EqualTo(1));
            Assert.That(logger.Exceptions, Is.Empty, "The receive loop handles exceptions internally, so awaiting it alone does not verify a clean shutdown.");
        });
    }

    /// <summary>
    /// Tests if the connection is disconnected after a malformed packet was received.
    /// </summary>
    /// <returns>The async task.</returns>
    [Test]
    public async Task DisconnectedByMalformedPacketReceivedAsync()
    {
        var malformedData = new byte[] { 0xDE, 0xAD, 0xBE, 0xEF };
        var duplexPipe = new DuplexPipe();
        using var connection = new Connection(duplexPipe, null, null, new NullLogger<Connection>());
        var disconnected = false;
        connection.Disconnected += async () => disconnected = true;
        _ = connection.BeginReceiveAsync();
        try
        {
            await duplexPipe.ReceivePipe.Writer.WriteAsync(malformedData).ConfigureAwait(false);
        }
        catch
        {
            // we need to swallow the exception for this test, so we can check the connected flag afterwards.
        }

        for (int i = 0; i < 10 && !disconnected; i++)
        {
            await Task.Delay(10).ConfigureAwait(false);
        }

        Assert.That(connection.Connected, Is.False);
    }

    /// <summary>
    /// Tests if the reader (e.g. SocketConnection) gets an exception when it reads a malformed packet which leads to an exception.
    /// The consumer (e.g. SocketConnection) will take care to call <see cref="PipeReader.Complete"/> or <see cref="PipeReader.CompleteAsync"/>.
    /// </summary>
    /// <returns>The async task.</returns>
    [Test]
    public async Task ExceptionWhenFailingToEncryptSentPacketAsync()
    {
        var malformedData = new byte[] { 0xDE, 0xAD, 0xBE, 0xEF };
        var duplexPipe = new DuplexPipe();
        using var connection = new Connection(duplexPipe, null, new Xor.PipelinedXor32Encryptor(duplexPipe.Output), new NullLogger<Connection>());

        _ = connection.BeginReceiveAsync();
        await connection.Output.WriteAsync(malformedData).ConfigureAwait(false);

        Assert.That(
            async () => await duplexPipe.SendPipe.Reader.ReadAsync().ConfigureAwait(false),
            Throws.TypeOf<InvalidPacketHeaderException>());
    }

    /// <summary>
    /// Tests if the connection is initially connected.
    /// </summary>
    [Test]
    public void InitiallyConnected()
    {
        var duplexPipe = new DuplexPipe();
        using var connection = new Connection(duplexPipe, null, null, new NullLogger<Connection>());
        Assert.That(connection.Connected, Is.True);
    }

    private sealed class RecordingConnectionLogger : ILogger<Connection>
    {
        public IList<Exception> Exceptions { get; } = new List<Exception>();

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (exception is not null)
            {
                this.Exceptions.Add(exception);
            }
        }
    }
}

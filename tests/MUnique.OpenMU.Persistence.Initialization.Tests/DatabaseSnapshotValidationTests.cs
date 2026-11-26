// <copyright file="DatabaseSnapshotValidationTests.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.Persistence.Initialization.Tests;

using System.Buffers.Binary;
using System.IO;
using System.IO.Compression;
using System.Text.Json.Nodes;
using System.Threading;
using Microsoft.EntityFrameworkCore;
using MUnique.OpenMU.Persistence.EntityFramework;

/// <summary>
/// Checks snapshot rejection without requiring or modifying any database.
/// </summary>
[TestFixture]
[NonParallelizable]
public class DatabaseSnapshotValidationTests
{
    private IDisposable? _settings;

    /// <summary>
    /// Uses an unreachable socket so an accidentally reached restore path cannot modify a database.
    /// </summary>
    [SetUp]
    public void SetUp() => this._settings = ConnectionConfigurator.UseTemporarily(new UnreachableDatabaseSettings());

    /// <summary>
    /// Restores the connection configuration used by other tests.
    /// </summary>
    [TearDown]
    public void TearDown() => this._settings?.Dispose();

    /// <summary>
    /// Both entry points must reject malformed snapshots before database access.
    /// </summary>
    /// <param name="kind">The malformed snapshot variant.</param>
    [TestCase("missing-payload")]
    [TestCase("duplicate-payload")]
    [TestCase("duplicate-manifest")]
    [TestCase("duplicate-table")]
    [TestCase("unexpected-payload")]
    [TestCase("malformed-manifest")]
    [TestCase("missing-manifest")]
    [TestCase("null-manifest")]
    [TestCase("null-tables")]
    [TestCase("null-migrations")]
    [TestCase("null-admin-migrations")]
    [TestCase("null-table-name")]
    [TestCase("invalid-table-name")]
    [TestCase("foreign-schema")]
    [TestCase("wrong-format")]
    [TestCase("invalid-copy-header")]
    [TestCase("truncated-copy-header")]
    [TestCase("negative-extension")]
    [TestCase("truncated-extension")]
    [TestCase("unsupported-copy-flags")]
    [TestCase("truncated-row")]
    [TestCase("negative-field-length")]
    [TestCase("inconsistent-field-count")]
    [TestCase("missing-copy-trailer")]
    [TestCase("trailing-copy-data")]
    [TestCase("huge-field-length")]
    public async Task MalformedSnapshotsAreRejectedBeforeRestoreAsync(string kind)
    {
        using var snapshot = CreateSnapshot(kind);
        var service = new DatabaseSnapshotService();
        Assert.That(await service.GetRestoreBlockingReasonAsync(snapshot).ConfigureAwait(false), Is.Not.Null.And.Not.Empty);
        Assert.That(snapshot.Position, Is.Zero, "Preflight must preserve the caller's stream position.");
        Assert.ThrowsAsync<InvalidDataException>(async () => await service.RestoreSnapshotAsync(snapshot).ConfigureAwait(false));
    }

    /// <summary>
    /// Empty and non-empty COPY data remain valid, including null and empty values.
    /// </summary>
    /// <param name="kind">A valid binary COPY variant.</param>
    [TestCase("valid-empty")]
    [TestCase("valid-row")]
    [TestCase("valid-null-empty")]
    [TestCase("valid-extension")]
    [TestCase("valid-large-field")]
    public async Task ValidCopyDataIsAcceptedAsync(string kind)
    {
        using var snapshot = CreateSnapshot(kind);
        Assert.That(await new DatabaseSnapshotService().GetRestoreBlockingReasonAsync(snapshot).ConfigureAwait(false), Is.Null);
        Assert.That(snapshot.Position, Is.Zero);
    }

    /// <summary>
    /// Preflight restores a non-zero caller position on success and failure.
    /// </summary>
    /// <param name="kind">A valid or invalid archive.</param>
    [TestCase("valid-row")]
    [TestCase("malformed-manifest")]
    public async Task PreflightPreservesNonZeroPositionAsync(string kind)
    {
        using var snapshot = CreateSnapshot(kind);
        snapshot.Position = 7;
        _ = await new DatabaseSnapshotService().GetRestoreBlockingReasonAsync(snapshot).ConfigureAwait(false);
        Assert.That(snapshot.Position, Is.EqualTo(7));
    }

    /// <summary>
    /// Cancellation does not turn into a misleading invalid-file result or start restoration.
    /// </summary>
    [Test]
    public void CanceledValidationDoesNotRestoreAsync()
    {
        using var snapshot = CreateSnapshot("valid-row");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var service = new DatabaseSnapshotService();
        Assert.That(async () => await service.GetRestoreBlockingReasonAsync(snapshot, cancellation.Token).ConfigureAwait(false), Throws.InstanceOf<OperationCanceledException>());
        Assert.That(snapshot.Position, Is.Zero);
        Assert.That(async () => await service.RestoreSnapshotAsync(snapshot, cancellation.Token).ConfigureAwait(false), Throws.InstanceOf<OperationCanceledException>());
    }

    private static MemoryStream CreateSnapshot(string kind)
    {
        var result = new MemoryStream();
        using (var archive = new ZipArchive(result, ZipArchiveMode.Create, leaveOpen: true))
        {
            var table = kind == "foreign-schema" ? "public.Account" : "data.Account";
            var manifest = new JsonObject
            {
                ["FormatVersion"] = kind == "wrong-format" ? 999 : 2,
                ["CreatedAt"] = DateTime.UtcNow,
                ["Migrations"] = new JsonArray(),
                ["AdminPanelMigrations"] = new JsonArray(),
                ["Tables"] = new JsonArray(table),
            };
            switch (kind)
            {
                case "duplicate-table": manifest["Tables"] = new JsonArray(table, table); break;
                case "null-tables": manifest["Tables"] = null; break;
                case "null-migrations": manifest["Migrations"] = null; break;
                case "null-admin-migrations": manifest["AdminPanelMigrations"] = null; break;
                case "null-table-name": manifest["Tables"] = new JsonArray((JsonNode?)null); break;
                case "invalid-table-name": manifest["Tables"] = new JsonArray("data."); break;
            }

            if (kind != "missing-manifest")
            {
                var text = kind == "malformed-manifest" ? "{" : kind == "null-manifest" ? "null" : manifest.ToJsonString();
                WriteEntry(archive, "manifest.json", Encoding.UTF8.GetBytes(text));
                if (kind == "duplicate-manifest")
                {
                    WriteEntry(archive, "manifest.json", Encoding.UTF8.GetBytes(text));
                }
            }

            if (kind != "missing-payload")
            {
                WriteEntry(archive, table + ".bin", CreateCopyData(kind));
                if (kind == "duplicate-payload")
                {
                    WriteEntry(archive, table + ".bin", CreateCopyData(kind));
                }
            }

            if (kind == "unexpected-payload")
            {
                WriteEntry(archive, "data.Unlisted.bin", CreateCopyData("valid-empty"));
            }
        }

        result.Position = 0;
        return result;
    }

    private static void WriteEntry(ZipArchive archive, string name, byte[] data)
    {
        using var stream = archive.CreateEntry(name).Open();
        stream.Write(data);
    }

    private static byte[] CreateCopyData(string kind)
    {
        using var stream = new MemoryStream();
        stream.Write(Convert.FromHexString("5047434F50590AFF0D0A00"));
        WriteInt32(kind == "unsupported-copy-flags" ? 1 << 16 : 0);
        WriteInt32(kind == "negative-extension" ? -1 : kind is "valid-extension" or "truncated-extension" ? 4 : 0);
        if (kind == "valid-extension")
        {
            WriteInt32(42);
        }

        if (kind is "valid-row" or "truncated-row" or "negative-field-length" or "inconsistent-field-count" or "huge-field-length")
        {
            WriteInt16(1);
            WriteInt32(kind == "negative-field-length" ? -2 : kind == "huge-field-length" ? int.MaxValue : 4);
            WriteInt32(42);
            if (kind == "inconsistent-field-count")
            {
                WriteInt16(0);
            }
        }
        else if (kind == "valid-null-empty")
        {
            WriteInt16(2);
            WriteInt32(-1);
            WriteInt32(0);
        }
        else if (kind == "valid-large-field")
        {
            WriteInt16(1);
            WriteInt32(200000);
            stream.Write(new byte[200000]);
        }

        if (kind != "missing-copy-trailer")
        {
            WriteInt16(-1);
        }

        if (kind == "trailing-copy-data")
        {
            stream.WriteByte(42);
        }

        var data = stream.ToArray();
        if (kind == "invalid-copy-header")
        {
            data[0] = 0;
        }

        return kind == "truncated-copy-header" ? data[..10] : kind == "truncated-row" ? data[..26] : data;

        void WriteInt16(short value)
        {
            Span<byte> bytes = stackalloc byte[2];
            BinaryPrimitives.WriteInt16BigEndian(bytes, value);
            stream.Write(bytes);
        }

        void WriteInt32(int value)
        {
            Span<byte> bytes = stackalloc byte[4];
            BinaryPrimitives.WriteInt32BigEndian(bytes, value);
            stream.Write(bytes);
        }
    }

    private sealed class UnreachableDatabaseSettings : IDatabaseConnectionSettingProvider
    {
        public Task? Initialization => null;

        public Task InitializeAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public ConnectionSetting GetConnectionSetting<TContextType>()
            where TContextType : DbContext => this.GetConnectionSetting(typeof(TContextType));

        public ConnectionSetting GetConnectionSetting(Type contextType) => new()
        {
            DatabaseEngine = DatabaseEngine.Npgsql,
            ConnectionString = "Host=/tmp/rea-no-such-snapshot-test;Database=snapshot_validation;Username=postgres;Timeout=1;Pooling=false",
        };
    }
}

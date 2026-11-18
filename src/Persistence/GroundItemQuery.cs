// <copyright file="GroundItemQuery.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.Persistence;

/// <summary>
/// Limits one ground delivery recovery page to an actual host, map, instance and time range.
/// </summary>
public sealed class GroundItemQuery
{
    /// <summary>
    /// The largest recovery page, bounding deserialization and per-pass memory use.
    /// </summary>
    public const int MaximumPageSize = 256;

    /// <summary>
    /// Gets the persistent definition identity of the actual map host.
    /// </summary>
    public required Guid HostServerId { get; init; }

    /// <summary>
    /// Gets the map definition, or <c>null</c> to scan the host's maps.
    /// </summary>
    public Guid? MapDefinitionId { get; init; }

    /// <summary>
    /// Gets the exact instance, or <c>null</c> for ordinary maps.
    /// </summary>
    public Guid? MapInstanceId { get; init; }

    /// <summary>
    /// Gets a value indicating whether every instance is included, for host expiry scans.
    /// </summary>
    public bool IncludeAllInstances { get; init; }

    /// <summary>
    /// Gets the exclusive lower expiry bound, in UTC, or <c>null</c> for no lower bound.
    /// </summary>
    public DateTime? ExpiresAfterUtc { get; init; }

    /// <summary>
    /// Gets the inclusive upper expiry bound, in UTC, or <c>null</c> for no upper bound.
    /// </summary>
    public DateTime? ExpiresBeforeUtc { get; init; }

    /// <summary>
    /// Gets the expiry time of the last scanned delivery, in UTC.
    /// Must be supplied together with <see cref="AfterDeliveryId"/>.
    /// </summary>
    public DateTime? AfterExpiresAtUtc { get; init; }

    /// <summary>
    /// Gets the last scanned delivery identity, used after its expiry time for keyset ordering.
    /// </summary>
    public Guid? AfterDeliveryId { get; init; }

    /// <summary>
    /// Gets the maximum number of item aggregates to load in this page.
    /// </summary>
    public int Limit { get; init; } = MaximumPageSize;

    /// <summary>
    /// Rejects ambiguous cursors, unbounded pages and non-UTC deadlines before querying.
    /// </summary>
    public void Validate()
    {
        if (this.HostServerId == Guid.Empty || this.MapDefinitionId == Guid.Empty || this.MapInstanceId == Guid.Empty
            || this.AfterDeliveryId == Guid.Empty || this.Limit is < 1 or > MaximumPageSize)
        {
            throw new ArgumentException("Recovery requires nonempty identities and a bounded page.");
        }

        if (this.IncludeAllInstances && this.MapInstanceId is not null)
        {
            throw new ArgumentException("Select either one instance or all instances.");
        }

        if ((this.AfterDeliveryId is null) != (this.AfterExpiresAtUtc is null))
        {
            throw new ArgumentException("Both delivery cursor fields are required together.");
        }

        foreach (var timestamp in new[] { this.ExpiresAfterUtc, this.ExpiresBeforeUtc, this.AfterExpiresAtUtc })
        {
            if (timestamp is { Kind: not DateTimeKind.Utc })
            {
                throw new ArgumentException("Recovery deadlines and cursors must be UTC.");
            }
        }
    }
}

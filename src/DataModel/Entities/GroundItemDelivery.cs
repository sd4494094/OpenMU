// <copyright file="GroundItemDelivery.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.DataModel.Entities;

using MUnique.OpenMU.Annotations;

/// <summary>
/// The durable address and original deadlines of an outstanding ground item delivery.
/// </summary>
/// <remarks>
/// The identifiers here address a host, map and owner across processes and persistence
/// contexts; they deliberately do not attach those object graphs to the item aggregate.
/// A null instance identifier denotes an ordinary map. A non-null instance identifier
/// must never be resolved to an ordinary map with the same map definition.
/// </remarks>
[Cloneable]
public partial class GroundItemDelivery
{
    /// <summary>
    /// Gets or sets the persistent definition identifier of the actual map host.
    /// This is not the connection server number or a transient map identifier.
    /// </summary>
    public Guid HostServerId { get; set; }

    /// <summary>
    /// Gets or sets the persistent map definition identifier, including its variant.
    /// </summary>
    public Guid MapDefinitionId { get; set; }

    /// <summary>
    /// Gets or sets the instance identifier, or <c>null</c> for an ordinary map.
    /// </summary>
    public Guid? MapInstanceId { get; set; }

    /// <summary>
    /// Gets or sets the original owner's persistent character identifier.
    /// </summary>
    public Guid OwnerCharacterId { get; set; }

    /// <summary>
    /// Gets or sets the horizontal coordinate of the drop.
    /// </summary>
    public byte PositionX { get; set; }

    /// <summary>
    /// Gets or sets the vertical coordinate of the drop.
    /// </summary>
    public byte PositionY { get; set; }

    /// <summary>
    /// Gets or sets the UTC time at which the delivery was first staged.
    /// </summary>
    public DateTime DroppedAt { get; set; }

    /// <summary>
    /// Gets or sets the original UTC deadline for exclusive owner pickup.
    /// </summary>
    public DateTime OwnerPriorityUntil { get; set; }

    /// <summary>
    /// Gets or sets the original UTC expiry deadline, which recovery must not extend.
    /// </summary>
    public DateTime ExpiresAt { get; set; }
}

// <copyright file="AriaMessage.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.Startup.Aria;

/// <summary>A detached input message sent to the model.</summary>
/// <param name="Role">The message role.</param>
/// <param name="Content">The message content.</param>
public sealed record AriaMessage(string Role, string Content);

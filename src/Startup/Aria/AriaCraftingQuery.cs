// <copyright file="AriaCraftingQuery.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.Startup.Aria;

using System.Text.Json;

/// <summary>Resolves common crafting questions for a lookup before answer generation.</summary>
internal static class AriaCraftingQuery
{
    private static readonly string[] RecipeCues = ["合成", "配方", "材料", "成功率", "craft", "recipe", "mix", "success rate"];
    private static readonly string[] AcquisitionCues = ["掉", "爆", "获取", "获得", "哪里", "怎么拿", "drop", "obtain", "get"];

    /// <summary>Extracts a recipe or output alias without embedding any recipe data in the model client.</summary>
    /// <param name="messages">The bounded conversation.</param>
    /// <returns>The arguments for a crafting lookup, when the latest question is explicit.</returns>
    internal static JsonElement? CreateArguments(IReadOnlyList<AriaMessage> messages)
    {
        var question = messages.LastOrDefault(message => message.Role == "user")?.Content;
        if (question is null || question.StartsWith("以下JSON仅为玩家状态和历史记忆数据", StringComparison.Ordinal))
        {
            return null;
        }

        var asksRecipe = RecipeCues.Any(cue => question.Contains(cue, StringComparison.OrdinalIgnoreCase));
        if (asksRecipe && (question.Contains("三代翅膀", StringComparison.Ordinal)
            || question.Contains("3rd Level Wings", StringComparison.OrdinalIgnoreCase)))
        {
            return JsonSerializer.SerializeToElement(new { text = "三代翅膀" });
        }

        if ((question.Contains("神鹰之羽", StringComparison.Ordinal) || question.Contains("Feather of Condor", StringComparison.OrdinalIgnoreCase))
            && (asksRecipe || AcquisitionCues.Any(cue => question.Contains(cue, StringComparison.OrdinalIgnoreCase))))
        {
            return JsonSerializer.SerializeToElement(new { text = "神鹰之羽" });
        }

        return null;
    }
}

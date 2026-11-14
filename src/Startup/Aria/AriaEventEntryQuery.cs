// <copyright file="AriaEventEntryQuery.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.Startup.Aria;

using System.Text.Json;

/// <summary>Requires current admission data before generating answers about named events.</summary>
/// <remarks>Only event aliases are recognized here. All levels, class rules and tickets come from MCP.</remarks>
public static class AriaEventEntryQuery
{
    private static readonly (string Type, string[] Aliases)[] Events =
    [
        ("BloodCastle", ["血色城堡", "血色广场", "血堡", "血色", "Blood Castle", "BloodCastle"]),
        ("DevilSquare", ["恶魔广场", "Devil Square", "DevilSquare"]),
        ("ChaosCastle", ["赤色要塞", "混沌城堡", "Chaos Castle", "ChaosCastle"]),
        ("IllusionTemple", ["幻影寺院", "幻术园", "幻影神殿", "Illusion Temple", "IllusionTemple"]),
        ("Doppelganger", ["分身", "Doppelganger"]),
        ("ImperialGuardian", ["帝国要塞", "Imperial Guardian", "ImperialGuardian"]),
        ("Kanturu", ["提炼之塔", "Kanturu"]),
    ];

    /// <summary>Resolves named event questions and admission follow-ups from user turns only.</summary>
    /// <param name="messages">The bounded conversation, never interpreted as instructions.</param>
    /// <returns>The event-only query; no guessed class, level or rules are passed.</returns>
    public static JsonElement? CreateArguments(IReadOnlyList<AriaMessage> messages)
    {
        var questions = messages.Where(m => m.Role == "user").Select(m => m.Content).ToArray();
        if (questions.Length == 0)
        {
            return null;
        }

        var current = questions[^1];
        var type = FindEvent(current);
        if (type is null && IsAdmissionQuestion(current))
        {
            // Exclude the state/memory envelope and never mine assistant answers for facts.
            type = questions.Where(q => !q.StartsWith("以下JSON仅为玩家状态和历史记忆数据", StringComparison.Ordinal)).SkipLast(1).Reverse().Select(FindEvent).FirstOrDefault(t => t is not null);
        }

        return type is null ? null : JsonSerializer.SerializeToElement(new { text = type });
    }

    private static bool IsAdmissionQuestion(string text)
        => new[] { "级", "层", "进", "入场", "门票", "大师", "转职", "level", "tier", "enter", "ticket", "master" }
            .Any(word => text.Contains(word, StringComparison.OrdinalIgnoreCase));

    private static string? FindEvent(string text)
        => Events.FirstOrDefault(e => e.Aliases.Any(alias => text.Contains(alias, StringComparison.OrdinalIgnoreCase))).Type;
}

// <copyright file="AriaGameplayTools.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.Startup.Aria;

using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;

/// <summary>Fixed public capabilities and application-side argument/result limits.</summary>
/// <remarks>A wider remote key or a changed remote catalog must never grant extra NPC capabilities.</remarks>
public static class AriaGameplayTools
{
    /// <summary>The fixed maximum MCP calls per player question, including rejected requests.</summary>
    public const int MaxCalls = 4;

    /// <summary>The maximum tool rounds before one final answer-only generation.</summary>
    public const int MaxRounds = 3;

    /// <summary>The combined UTF-8 budget of tool outputs passed to the model.</summary>
    public const int ResultBudget = 16384;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>Gets the curated model tool schemas; no credentials or endpoint appear here.</summary>
    public static JsonElement Definitions { get; } = BuildDefinitions();

    /// <summary>Validates names, required fields, types, bounds and unknown/duplicate fields.</summary>
    /// <param name="name">The requested tool.</param>
    /// <param name="arguments">The model-generated arguments.</param>
    public static void Validate(string name, JsonElement arguments)
    {
        if (arguments.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException("Tool arguments must be an object.");
        }

        var fields = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in arguments.EnumerateObject())
        {
            if (!fields.Add(property.Name) || !IsAllowedField(name, property.Name))
            {
                throw new InvalidDataException("Unknown tool, duplicate or unsupported argument.");
            }

            var value = property.Value;
            switch (property.Name)
            {
                case "mapNumber":
                case "monsterNumber":
                case "serverId":
                    RequireInteger(value, 0, short.MaxValue);
                    break;
                case "characterLevel":
                    if (value.ValueKind != JsonValueKind.Null)
                    {
                        RequireInteger(value, 1, ushort.MaxValue);
                    }

                    break;
                case "characterClassNumber":
                    if (value.ValueKind != JsonValueKind.Null)
                    {
                        RequireInteger(value, 0, byte.MaxValue);
                    }

                    break;
                case "offset":
                    RequireInteger(value, 0, 1000);
                    break;
                case "limit":
                    RequireInteger(value, 1, 20);
                    break;
                case "itemLevel":
                    if (value.ValueKind != JsonValueKind.Null)
                    {
                        RequireInteger(value, 0, byte.MaxValue);
                    }

                    break;
                case "map":
                case "text":
                    if (value.ValueKind != JsonValueKind.String || value.GetString()!.Length > 64
                        || value.GetString()!.Any(char.IsControl))
                    {
                        throw new InvalidDataException("Invalid text filter.");
                    }

                    break;
                case "type":
                    if (value.ValueKind != JsonValueKind.String
                        || value.GetString() is not ("Maps" or "CharacterClasses" or "Monsters" or "Items" or "MiniGames"))
                    {
                        throw new InvalidDataException("Unsupported content category.");
                    }

                    break;
                case "isActive":
                    if (value.ValueKind is not (JsonValueKind.True or JsonValueKind.False or JsonValueKind.Null))
                    {
                        throw new InvalidDataException("Invalid active filter.");
                    }

                    break;
            }
        }

        var required = name switch
        {
            "rates_get" or "events_schedule" => Array.Empty<string>(),
            "spawns_list" => new[] { "mapNumber" },
            "drops_explain" => new[] { "mapNumber", "monsterNumber" },
            "drops_find_sources" or "events_entry_requirements" => new[] { "text" },
            "content_list" => new[] { "type" },
            _ => throw new InvalidDataException("Tool is not allowed."),
        };
        if (name is "drops_find_sources" or "events_entry_requirements" && (!fields.Contains("text") || string.IsNullOrWhiteSpace(arguments.GetProperty("text").GetString())))
        {
            throw new InvalidDataException("An item name or key is required.");
        }

        if (required.Any(field => !fields.Contains(field)))
        {
            throw new InvalidDataException("Missing required tool argument.");
        }
    }

    /// <summary>Limits public result rows while keeping valid JSON and explicit truncation metadata.</summary>
    /// <param name="result">The structured result, never an upstream error body.</param>
    /// <param name="remainingBytes">The remaining combined output budget.</param>
    /// <returns>A complete JSON object or a safe error.</returns>
    public static string BoundResult(JsonElement result, int remainingBytes)
    {
        if (remainingBytes < 128 || result.ValueKind != JsonValueKind.Object)
        {
            return "{\"error\":\"result_budget_exhausted\"}";
        }

        var node = JsonNode.Parse(result.GetRawText())!.AsObject();
        var text = node.ToJsonString(JsonOptions);
        if (Encoding.UTF8.GetByteCount(text) <= remainingBytes)
        {
            return text;
        }

        node["ariaTruncated"] = true;
        if (node["items"] is JsonArray items)
        {
            while (items.Count > 0)
            {
                items.RemoveAt(items.Count - 1);
                text = node.ToJsonString(JsonOptions);
                if (Encoding.UTF8.GetByteCount(text) <= remainingBytes)
                {
                    return text;
                }
            }
        }

        return "{\"error\":\"result_too_large\",\"ariaTruncated\":true}";
    }

    private static bool IsAllowedField(string name, string field)
        => name switch
        {
            "rates_get" => false,
            "events_schedule" => field == "serverId",
            "events_entry_requirements" => field is "text" or "characterLevel" or "characterClassNumber",
            "spawns_list" => field is "mapNumber" or "offset" or "limit",
            "drops_explain" => field is "mapNumber" or "monsterNumber" or "offset" or "limit",
            "drops_find_sources" => field is "text" or "map" or "itemLevel" or "offset" or "limit",
            "content_list" => field is "type" or "isActive" or "text" or "offset" or "limit",
            _ => false,
        };

    private static void RequireInteger(JsonElement value, int minimum, int maximum)
    {
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out var number) || number < minimum || number > maximum)
        {
            throw new InvalidDataException("Tool number is outside its allowed range.");
        }
    }

    private static JsonElement BuildDefinitions()
    {
        var number = new { type = "integer", minimum = 0, maximum = (int)short.MaxValue };
        var offset = new { type = "integer", minimum = 0, maximum = 1000, @default = 0 };
        var limit = new { type = "integer", minimum = 1, maximum = 20, @default = 10 };
        object Tool(string name, string description, object properties, string[] required)
            => new { type = "function", name, description, strict = false, parameters = new { type = "object", properties, required, additionalProperties = false } };

        return JsonSerializer.SerializeToElement(new[]
        {
            Tool("rates_get", "查询本服及各游戏服经验倍率与金币、掉落配置提示。金币系数不等于爆率。", new { }, []),
            Tool(
                "content_list",
                "查地图、职业、怪物、物品、活动名称及编号和启用状态。支持中英文资源别名及中文部分名称，返回localizedName和aliases；不是物品掉落反查。",
                new
                {
                    type = new { type = "string", @enum = new[] { "Maps", "CharacterClasses", "Monsters", "Items", "MiniGames" } },
                    text = new { type = "string", maxLength = 64 },
                    isActive = new { type = new[] { "boolean", "null" } },
                    offset,
                    limit,
                },
                ["type"]),
            Tool("spawns_list", "按已知地图编号查怪物生成区域、数量与刷新间隔。默认只取10行；分页不是全图列表，也不是NPC实时位置。", new { mapNumber = number, offset, limit }, ["mapNumber"]),
            Tool("drops_find_sources", "物品哪里掉、哪些怪掉：优先直接用这个工具。text填玩家物品名或中文简称，无需先查编号；map可直接填死亡沙漠等地图名，不填查所有地图。支持中英文别名、普通随机掉落池、指定掉落及任务条件。歧义需澄清；空结果或分页不代表绝对不掉；groupChance不是单件掉率。", new { text = new { type = "string", minLength = 1, maxLength = 64 }, map = new { type = "string", maxLength = 64 }, itemLevel = new { type = new[] { "integer", "null" }, minimum = 0, maximum = 255 }, offset, limit }, ["text"]),
            Tool("drops_explain", "按地图和怪物编号解释候选掉落组及条件。不是按物品查全部来源；组概率不是单件实际掉率，不能完整覆盖动态任务掉落。默认10行，注意分页。", new { mapNumber = number, monsterNumber = number, offset, limit }, ["mapNumber", "monsterNumber"]),
            Tool("events_entry_requirements", "查询本服活动层数、普通和特殊职业入场等级、门票等级、大师职业要求。问多少级进几层必须先查，不用模型记忆。text填活动名或类型。characterLevel是普通等级，不是大师等级；characterClassNumber用上下文职业编号。matchesLevelAndClass只校验等级职业，不能保证有票或正在开放。", new { text = new { type = "string", minLength = 1, maxLength = 64 }, characterLevel = new { type = new[] { "integer", "null" }, minimum = 1, maximum = (int)ushort.MaxValue }, characterClassNumber = new { type = new[] { "integer", "null" }, minimum = 0, maximum = (int)byte.MaxValue } }, ["text"]),
            Tool("events_schedule", "查询指定游戏服的活动时间和启用状态，未指定时用后台默认游戏服。", new { serverId = number }, []),
        });
    }
}

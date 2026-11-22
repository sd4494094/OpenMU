---
sidebar_position: 31
---

# Aria AI conversation

When enabled by the server operator, the creator goddess Aria offers private
conversation and help through these equivalent commands:

```text
/Aria your question
/aria your question
/艾莉雅 你的问题
/小助理 你的问题
```

Use normal chat mode and a space after the command. Clicking Aria also explains
how to start a conversation. You may ask from other maps.

All aliases share the same conversation. Your submitted question is echoed only
to you, and only you receive Aria's answer. The model transport requests a complete JSON response, avoiding proxy streams
which omit their final completion event. She waits until the complete answer
is ready, then sends it together as short lines that fit the game chat panel; another question must wait
until the current request finishes. If the service is busy or unavailable, try
again later.

Aria remembers recent conversation and a stored summary for your character.
Memory remains separate from other players and NPCs. To clear your character's
history and summary with Aria, enter `/Aria forget` or `/Aria 忘记我`.
Server operators control raw conversation retention, which defaults to 30 days.
The server sends relevant conversation and character context to its configured
AI provider.

Aria cannot grant items, change attributes, teleport you or perform GM actions.
She can answer general MU Season 6 Episode 3 questions from model knowledge.
Verified server information takes precedence over general rules; she must not
present an unverified rule as a confirmed setting of this server. Her answers
remain limited to 80 characters in total. Completed replies use wider lines
(up to 26 plain Chinese characters within the existing packet byte budget).
Punctuation is preferred only near a full line's end, so an early comma does not
create an unnecessarily short line. Player question echoes use the same wrapping.
This is server-side formatting; the existing client chat system is unchanged.
Actual visible width still depends on the client's font and window size.

Transient connection failures and HTTP 500/502/503/504 responses are retried
once after a short randomized delay, within the original request deadline
(default 30 seconds). Authentication, billing/rate-limit errors and explicit
failed/incomplete model output are not retried. A retry never publishes partial
text or repeats the player echo, conversation storage or final reply.

Operators can find configuration, migration and recovery instructions in
[`deploy/aria/README.md`](https://github.com/sd4494094/OpenMU/blob/dev/deploy/aria/README.md).
This feature does not enable or reconfigure AI player bots.


## Optional server gameplay queries

When the operator enables Aria's dedicated MCP connection, she can query current
experience multipliers, spawn configuration, content activation and event schedules,
and explain candidate drop groups for a specified monster and map. She uses only
seven read-only gameplay tools. Account data, other players, generic configuration,
logs and administration operations are not available to her.

Ask “袭风刺技能石有哪些怪掉” or follow up with a map such as “死亡沙漠”.
The item-source query resolves Chinese partial names, skill-orb aliases, English
names and invariant item keys in one call. It searches stored translations and
exact built-in resource aliases, including English-only legacy databases, without
renaming persisted items. Ambiguous names require clarification.

Sources include ordinary random pools with the actual monster-level window,
explicit map/monster groups and configured quest requirement groups. Quest and
event conditions remain visible. Detached global group catalogs are not reported
as active sources. A group's configured chance is not a final item probability.
Player-specific groups, custom drop generators and runtime quest/party/event
eligibility are outside the query; a page or truncated result never proves an
item cannot drop. General monster-group explanations remain available separately.
If a query fails, Aria distinguishes missing server information from general
Season 6 knowledge.

Queries share the existing conversation deadline, with at most four calls over
three tool rounds, followed by an answer-only turn. Each page is at most 20 rows
(default 10), and tool output totals at most 16 KiB. Tool results are treated as
data and are not added to long-term player summaries. Only the final short answer
is sent and stored as part of the normal conversation; intermediate tool outputs
and model preludes are not displayed. Casual conversation makes no MCP calls.

Named event questions trigger a current \`events_entry_requirements\` lookup before
answer generation, including level/tier follow-ups. If the lookup is unavailable
or truncated, no numerical admission advice is generated. The tool reads actual
configuration instead of a fixed season table, separates ordinary and special
class ranges and treats master class as an additional requirement. Context
includes the configured class number and special/master flags. Optional class
and normal level inputs evaluate the same range rule as live entry; this is not
a guarantee of tickets, fees, opening time or other runtime eligibility.
Aria can look up crafting materials and results by recipe or output name. For example, \`三代翅膀\` finds both third-wing stages and \`神鹰之羽\` finds the recipe that produces it.

The Aria API key allowlist must include \`crafting_find_recipes\` and \`events_entry_requirements\` alongside the
six existing gameplay tools, plus `equipment_get_details` for equipment values. Old conversation answers are not admission facts. Explicit third-wing crafting and Feather of Condor acquisition questions load the corresponding recipe before answer generation.

General Season 6 equipment comparisons and weapon progression advice can be answered directly. Current server drops, rates, recipes and event requirements are queried before reporting server-specific rules.

### 装备数值查询

Aria 可用只读的 `equipment_get_details` 按中英文装备名或物品编号查询攻击区间、计算后的穿戴属性要求、独立等级要求和适用职业。默认普通 +0，也可指定强化等级；需求系数不是最终穿戴要求。等级要求为 0 表示没有独立等级限制。

查询使用已保存配置和游戏现有计算逻辑，按满耐久计算，不含追加、卓越、套装、镶嵌、和谐及人物属性；不读取玩家背包。歧义、非装备、停用物品或缺少公式会明确返回状态。Aria 专用 Viewer 密钥需在工具白名单中加入此工具，无需授予通用配置读取权限。

### 按职业检索武器与推荐依据

只读工具 `equipment_search` 可按职业中文名、英文名或职业编号，以及武器类型、单手/双手筛选；弓弩单独标为 Ranged，避免与近战双手武器混淆。返回正式名称、同一强化等级下普通满耐久武器的攻击和攻速贡献、配置镶嵌上限、属性要求与适用职业。搜索支持名称缩小范围、分页和强化等级（0–15），只返回启用武器，默认每页 8 件，最多 20 件。Viewer 密钥只需额外开放 `equipment_search`，不需要通用配置权限。

结果按最大物理攻击、最小物理攻击降序排列，不是综合战力排名。镶嵌上限不保证实际孔数，也不包含镶嵌效果；启用不保证能通过当前掉落获得。职业符合不代表人物属性已足够。具体装备推荐先查名称与职业；通用配装思路仍直接回答。比较“更强”必须区分同条件基础攻击与强化、卓越、镶嵌后的综合效果，不能编造固定升级链。名称无匹配时纠正后检索真实候选，再继续回答来源与属性。

回归需覆盖“更高单手武器→还有哪些候选→候选掉落及普通+0属性”，另验证编造名称后的纠错、跨职业武器排除、单/双手区分、分页与无结果，以及通用配装问题不强制查询。

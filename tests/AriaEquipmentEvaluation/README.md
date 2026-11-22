# Aria equipment recommendation evaluation

This opt-in evaluation uses the real Responses model and a restricted MCP Viewer key. It never sends game chat or writes player memory. Run it against a deployment of the matching commit, with Season Six equipment data; it is deliberately separate from offline NUnit tests.

Supply the existing `Aria__Endpoint`, `Aria__ApiKey`, `Aria__Model`, `Aria__ReasoningEffort`, `Aria__TimeoutSeconds`, `Aria__MaxOutputTokens` and `Aria__Mcp__*` settings through the environment. Keep credentials out of command text, logs and committed files. TLS validation uses `Aria__Mcp__RootCertificateFile`. The Viewer allowlist must contain `equipment_search`, `equipment_get_details` and `drops_find_sources`.

On dev, build once with the same SDK as the server:

```sh
dotnet build tests/AriaEquipmentEvaluation -c Release -p:ci=true
dotnet run --project tests/AriaEquipmentEvaluation -c Release --no-build
```

In a matching deployment image, the compiled evaluator may use `OPENMU_ARIA_EVALUATION_APP=/app` to load the deployed assemblies and prompt. It runs at the actual configured 30-second conversation and 10-second MCP deadlines; do not extend them to conceal latency failures.

The evaluator checks class, weapon kind and hand filters, +15 calculated attack, and two complete repetitions of screenshot 17's recommendation → candidate follow-up → mixed source/+0 attribute conversation. Each recommendation must query candidates, name actual compatible weapons and avoid the fabricated sword. The mixed question must query both drops and details. Separate checks cover recovery after a fabricated historical recommendation and answering general gearing principles without queries. Output includes exact tool arguments, results, final answers and timings, with no credentials. Assertions fail the process rather than treating a model refusal as a successful evaluation.

Candidate ordering is maximum physical attack, not overall power. The checks intentionally do not require one particular recommended weapon or claim that a socket weapon is stronger. A production rollout should also check server health and MCP role isolation independently. Archive the JSON result with the deployment record; game-client text layout still needs human verification.

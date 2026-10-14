# Observability and evaluation

WhyLine's promise is "how we know". Two things back it up beyond the live Verifier: every moment is traced end to end
with OpenTelemetry, and the published cards are scored offline by independent evaluators.

## Tracing

Each detected moment becomes **one trace**:

```
newsroom Goal                                   WhyLine.Newsroom   (moment id, kind, minute, importance)
├─ decide whyline-director                      approved, reason, audience size
├─ brief whyline-analyst                        headline
│  └─ invoke_agent whyline-analyst              Agent Framework (GenAI conventions: model, tokens, prompt/response)
├─ write whyline-narrator      ×1 per viewer    persona, language, attempt
│  └─ invoke_agent whyline-narrator
├─ verify whyline-verifier                      passed, feedback when rejected
├─ write whyline-localizer                      (viewers in another language)
├─ verify whyline-verifier
└─ publish whyline-publisher                    card title, latency
```

With the workflow orchestrator, Agent Framework also emits workflow, executor and message spans (source
`WhyLine.Workflow`). Metrics (meter `WhyLine.Newsroom`):

| Metric | Meaning |
|---|---|
| `whyline.moments.directed` | Moments the Director decided on, tagged `approved` and `kind` |
| `whyline.cards.published` | Verified cards, tagged `stage` and `language` |
| `whyline.verifier.rejections` | Drafts sent back for an untraceable claim, tagged `agent` |
| `whyline.fallbacks` | Model calls replaced by the template voice, tagged `agent` |
| `whyline.card.latency` | Milliseconds from the Director's decision to a published card |

Code: `NewsroomTelemetry` (spans and metrics), `FoundryAgentFactory` (`UseOpenTelemetry` on every agent),
`NewsroomWorkflow` (`WithOpenTelemetry`), `TelemetrySetup` (exporters).

### See the traces in Microsoft Foundry

1. In the Foundry portal, open your project → **Tracing** and connect an Application Insights resource (create one if
   needed).
2. Copy its connection string (Application Insights → Overview → Connection String).
3. Add it to `src/WhyLine.Web/appsettings.Local.json` (or user secrets):

```json
"Telemetry": { "ApplicationInsightsConnectionString": "InstrumentationKey=...;IngestionEndpoint=..." }
```

4. Run the app (`dotnet run --project src/WhyLine.Web --launch-profile "WhyLine (Foundry agents)"`). The startup log says `Telemetry: Application Insights`. Traces appear in Foundry → Tracing
   and in Application Insights → Transaction search within a couple of minutes.

Prompts and completions are recorded on the model-call spans (`Agents:Foundry:RecordContentInTraces`, on by default
because WhyLine only sends synthetic match data). Set it to `false` to keep only timings and token counts.

### Or locally, with the .NET Aspire dashboard

```bash
docker run --rm -it -p 18888:18888 -p 4317:18889 mcr.microsoft.com/dotnet/aspire-dashboard:latest
```

Set `"Telemetry": { "OtlpEndpoint": "http://localhost:4317" }`, run the app and open http://localhost:18888 (the login
token is printed in the container log). Both exporters can be on at once.

## Evaluation

```bash
dotnet run --project tools/WhyLine.Evaluate                                      # all fixtures, several minutes
dotnet run --project tools/WhyLine.Evaluate -- --matches comeback --moments 2    # one fixture, about two minutes
dotnet tool restore
dotnet tool run aieval report --path eval/store --output eval/report.html        # HTML report
```

The evaluation plays every fixture, runs the key moments (all goals, the most important other moments and full time)
through the Foundry newsroom for three viewers (English analyst, English casual fan, Urdu casual fan), then scores every
published card. Options: `--matches`, `--moments`, `--safety`, `--judge <deployment>` and `--out <folder>`.

| Metric | Source | What it checks |
|---|---|---|
| Fact trace | WhyLine `ClaimVerifier`, run again independently | Every number and player name traces to a computed fact |
| Groundedness | Microsoft.Extensions.AI.Evaluation (LLM judge) | Claims are supported by the stats engine's facts only, not by the Analyst's prose |
| Relevance | LLM judge | The card answers "what happened and why it matters" for that viewer |
| Coherence | LLM judge | The card reads logically and in the right order |
| Fluency | LLM judge | Natural language, including Urdu |
| Violence, Hate and unfairness, Protected material | Microsoft Foundry evaluation service (`--safety`, needs `az login`) | Content safety |
| First-draft acceptance | Newsroom traces | Share of cards the Verifier approved without sending a draft back |

The judge is your Reasoning deployment (override with `--judge <deployment>`). Outputs: `eval/summary.md`,
`eval/results.json` and `eval/report.html` (the Microsoft.Extensions.AI.Evaluation report, with each score's reasoning).
Judge responses are cached for 14 days, so re-running unchanged cards is free.

Options: `--matches comeback,thriller`, `--moments 3`, `--safety`, `--judge <deployment>`, `--out eval`.

### Evaluation-driven improvement

The evaluation found real problems, and each fix was re-measured. All runs use seed 7, `gpt-5.5` / `gpt-5.4-mini` with
low reasoning effort, and `gpt-5.5` as judge. Judge scores vary by about ±0.1 between identical runs.

**1. Casual cards said what happened but not why it mattered.** The first run (comeback fixture, 9 cards) scored
relevance 5.0 for the English analyst but 3.0 for the casual fan; the judge's reasons all said "does not explain why it
matters". The Casual persona guide asked for "one simple reason", and the model gave *how* the goal was built instead.
The guide now requires a sentence on what the moment changes for the match or the viewer's club, and the Recap agent
must tell key moments in match order. (The judge's prompt also stopped mentioning the minute, which the card shows next
to the clock anyway.)

| Comeback fixture, 9 cards | Relevance | Coherence | Groundedness | Cards flagged |
|---|---|---|---|---|
| Before | 3.78 | 4.11 | 4.44 | 6 |
| After | 4.33 | 4.33 | 4.56 | 1 |

**2. Urdu sometimes came back in Roman script.** The full run (5 fixtures, 60 cards) showed 3 of 20 Urdu cards written
as Roman Urdu ("Juric ne Saltmere ko aage kar diya"), and Urdu relevance trailing English. The Localizer and Recap
instructions now require Urdu and Arabic script, and the Verifier rejects a draft that is not mostly Arabic script once
names are removed (`ClaimVerifier.MostlyArabicScript`), sending it back with that feedback.

| Full run, Urdu casual fan (20 cards) | Roman-script cards | Groundedness | Relevance | Coherence | Fluency |
|---|---|---|---|---|---|
| Before | 3 | 4.10 | 3.80 | 4.05 | 3.95 |
| After | 0 | 4.35 | 4.00 | 3.95 | 4.00 |

| Full run, all 60 cards | Fact trace | Groundedness | Relevance | Coherence | Fluency |
|---|---|---|---|---|---|
| After both fixes | 100% | 4.57 | 4.27 | 4.25 | 3.92 |

**3. Still open: meaning, not numbers.** The remaining flags are semantic slips the Verifier cannot see, because it
checks numbers and names rather than meaning. One Urdu card said a team "leads" when the score was level; one English
card misspelled a club ("Eastmere" for Eastmarsh). Next steps: reject near-miss club names deterministically, and add a
small model-based semantic check (who scored, who leads) to the Verifier loop, measured with this same harness.

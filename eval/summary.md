# WhyLine evaluation

Run `20261014T101052`. Agents: `gpt-5.5` (Analyst, Recap) and `gpt-5.4-mini` (Narrator, Localizer) on Microsoft Foundry. Judge: `gpt-5.5`. 60 published cards across 5 fixtures, written for an English analyst, an English casual fan and an Urdu casual fan.

Quality scores are 1 (poor) to 5 (excellent) from Microsoft.Extensions.AI.Evaluation's LLM-as-judge evaluators. Groundedness is judged against the stats engine's computed facts only, not the Analyst's prose. Fact trace is WhyLine's deterministic Verifier run again independently.

## Overall

| Metric | Score | Failed |
|---|---|---|
| Fact trace | 100% pass | 0/60 |
| Groundedness | 4.57 | 4/60 |
| Relevance | 4.27 | 6/60 |
| Coherence | 4.25 | 2/60 |
| Fluency | 3.92 | 5/60 |

First-draft acceptance: 100% (0 drafts sent back by the Verifier). Template fallbacks: 1.

## By fixture

| fixture | Cards | Fact trace | Groundedness | Relevance | Coherence | Fluency |
|---|---|---|---|---|---|---|
| comeback | 12 | 100% pass | 4.42 | 3.92 | 4.17 | 3.92 |
| press-flip | 12 | 100% pass | 4.67 | 4.58 | 4.33 | 3.92 |
| smash-and-grab | 12 | 100% pass | 4.83 | 4.50 | 4.42 | 3.83 |
| stalemate | 12 | 100% pass | 4.50 | 4.33 | 4.17 | 4.00 |
| thriller | 12 | 100% pass | 4.42 | 4.00 | 4.17 | 3.92 |

## By viewer

| viewer | Cards | Fact trace | Groundedness | Relevance | Coherence | Fluency |
|---|---|---|---|---|---|---|
| Analyst-en | 20 | 100% pass | 4.60 | 4.65 | 4.45 | 3.90 |
| Casual-en-Home | 20 | 100% pass | 4.75 | 4.15 | 4.35 | 3.85 |
| Casual-ur-Away | 20 | 100% pass | 4.35 | 4.00 | 3.95 | 4.00 |

## Flagged cards

- **comeback 15' Goal, Casual-ur-Away**, Groundedness 3: "Florin Juric نے Saltmere Athletic کو 0-1 کی برتری دلا دی" — Mostly answers the query, but includes an incorrect team name and unsupported phrasing, so it is not fully grounded.
- **comeback 15' Goal, Casual-ur-Away**, Relevance 2: "Florin Juric نے Saltmere Athletic کو 0-1 کی برتری دلا دی" — The response is relevant in format and intent, but includes incorrect/unsupported match details, especially the wrong team name.
- **comeback 38' Goal, Casual-en-Home**, Relevance 3: "Saltmere extend their lead" — The response is relevant and useful, but somewhat incomplete because it lacks details about what actually happened in the goal.
- **comeback 90' Goal, Analyst-en**, Fluency 3: "Abara finishes rapid Ironvale move" — The response is coherent and readable with minor awkwardness, showing competent control of language but limited stylistic range.
- **comeback 91' FullTime, Analyst-en**, Coherence 3: "Full-time recap: Ironvale Rovers 3-2 Saltmere Athletic" — The response is understandable and logically ordered, but it lacks smooth tactical analysis and has a slight temporal inconsistency.
- **comeback 91' FullTime, Analyst-en**, Relevance 3: "Full-time recap: Ironvale Rovers 3-2 Saltmere Athletic" — The response is relevant and mostly accurate as a match summary, but it is incomplete because it lacks meaningful tactical analysis and includes an odd live-match phrasing.
- **press-flip 87' Goal, Analyst-en**, Fluency 3: "Rana turns regain into lead" — The response is clear overall but contains noticeable awkward phrasing and minor grammatical errors.
- **smash-and-grab 28' BigChance, Casual-en-Home**, Fluency 3: "Baptiste comes close" — The response is clear and readable with only a minor grammatical issue, but it lacks the complexity and polish of higher fluency levels.
- **smash-and-grab 39' BigChance, Casual-en-Home**, Fluency 3: "Corvane waste a big opening" — The response clearly conveys the event but contains a few awkward or grammatically imperfect phrases, so it is competent rather than highly polished.
- **stalemate 19' BigChance, Analyst-en**, Groundedness 3: "Quaresma tests Eastmere control" — The response is mostly relevant and uses several correct match facts, but it includes incorrect/unsupported details, especially the wrong team name and unsupported control attribution.
- **stalemate 19' BigChance, Casual-en-Home**, Relevance 3: "Quaresma denied for Larkhaven" — The response mostly addresses the request with relevant, concise information, but lacks specific detail about what the chance was and has slightly awkward phrasing.
- **stalemate 91' FullTime, Analyst-en**, Groundedness 3: "Larkhaven Dominance Ends Goalless" — The recap is mostly grounded in the match data but includes some inaccurate or unsupported tactical interpretation, so it is not fully correct.
- **thriller 22' Goal, Casual-en-Home**, Relevance 3: "Marrow Bay level it up" — The response is relevant and mostly complete, explaining the scorer, score impact, and match significance, but it omits details of the goal itself.
- **thriller 22' Goal, Casual-en-Home**, Fluency 3: "Marrow Bay level it up" — The response clearly communicates the match situation, but awkward phrasing and limited sentence variety keep it from higher fluency.
- **thriller 22' Goal, Casual-ur-Away**, Coherence 3: "Marrow Bay نے برابری کر لی ہے" — The response is mostly coherent and relevant, but the statement that Marrow Bay has taken the lead after making it 1-1 creates confusion.
- **thriller 22' Goal, Casual-ur-Away**, Groundedness 3: "Marrow Bay نے برابری کر لی ہے" — The response is mostly relevant and includes correct goal details, but it contains an incorrect claim that Marrow Bay now has the lead instead of being level.
- **thriller 22' Goal, Casual-ur-Away**, Relevance 2: "Marrow Bay نے برابری کر لی ہے" — The response is relevant in format and intent, but contains a key factual/internal error about the match situation.

## Sample cards

- **comeback 15' Goal, Analyst-en**: **Juric sparks Saltmere's opener** Florin Juric won it back with an interception in the middle third and Saltmere Athletic scored 8 s later. The finish came from a 0.42 xG chance, 19 m out, and Saltmere lead 0-1 at 15.
- **press-flip 23' Goal, Analyst-en**: **Brandt punishes the regain** Elias Brandt won it back with an interception in the middle third, then scored 6 s later from a 0.38 xG chance. Northbridge City lead 1-0 at 23, so the game has shifted from balanced to their lead-state. Harbour now need to protect central possession better, or Northbridge City can keep attacking quickly after regains.
- **smash-and-grab 88' Goal, Analyst-en**: **Fenwick punishes the regain** Duskmoor United lead 0-1 in the 88th minute after Joaquin Galvao’s middle-third interception sparked a 1-pass move finished by Ezra Fenwick 9 s later. The shot came from 18 m with 0.24 xG and 95 km/h, showing how quickly the regain turned into a decisive transition.
- **stalemate 91' FullTime, Analyst-en**: **Larkhaven Dominance Ends Goalless** Larkhaven FC 0-0 Eastmarsh Albion, with the turning point Eastmarsh surviving the 53-minute spell and then seeing Emre Whitlock’s 68-minute chance saved despite Larkhaven’s 3.73 xG and 25 shots. At 14, Hassan Quaresma had a 0.38 xG chance from 2 m saved, the first sign Larkhaven were getting into high-value areas. At 19, Quaresma was denied again from 20 m on a 0.45 xG chance, then at 33 he put an 8 m opportunity worth 0.35 xG wide. Eastmarsh’s clearest opening came at 53, when Tariq Farouk missed wide from 5 m with a 0.52 xG chance, and the momentum briefly tilted their way as Larkhaven’s last-five-minute possession dropped to 30%. At 68, Whitlock forced another save from an 18 m chance worth 0.52 xG, leaving Larkhaven with control, volume and territory, but no finish.
- **thriller 10' Goal, Analyst-en**: **Esposito punishes the regain** Halden Forge lead 1-0 at 10' after Viktor Esposito turned a regain into a finish from 2 m. The move took only 3 passes and ended 13 s after Yannick Delacroix won the ball by tackle in the middle third. It was a 0.41 xG chance, so the quick transition created the opening rather than long possession.

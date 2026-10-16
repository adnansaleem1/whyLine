# Demo video (3 minutes)

A shot list and voiceover for a 3-minute hackathon video, built on **demo mode** (*Demo: jump to…* in the Studio
header). Every chapter starts 30 seconds before its moment and plays at 4×. The fixture is scripted and seeded, so
every take plays the same match, and a bad take is redone by picking the chapter again.

## Before recording

1. Check the Foundry deployments: both model deployments (`gpt-5.5` and `gpt-5.4-mini` by default) must exist. If the
   startup log or the newsroom panel shows `DeploymentNotFound` or "used rule-based brief", the reasoning tier is
   missing and the video would show template briefs.
2. Run with Foundry agents:
   `dotnet run --project src/WhyLine.Web --launch-profile "WhyLine (Foundry agents)"`.
   Check the log for `Speech: Azure AI Speech (<region>)`.
3. Browser at 1920×1080, zoom 90–100%, full screen (F11), no other tabs or bookmarks bar.
4. Leave Viewer A on English Analyst (live) and Viewer B on Urdu Casual, Harbour United fan, 30 s behind (the defaults).
5. Turn on **Speak insights** for Viewer B only, so two voices never overlap. It stays on across jumps.
6. Do one full dry run of every chapter. Speech is cached per card, so the second take starts speaking faster.
7. Record the screen with system audio (OBS, or Windows Snipping Tool's screen recording) so the neural voice is on the
   recording. Record the voiceover separately and lay it over the video in the edit.

## Shot list

Times are positions in the finished video. Record each scene as its own clip and cut the waiting in the edit.

| Time | Scene | On screen | Voiceover |
|---|---|---|---|
| 0:00–0:15 | **Hook** | Studio at kick-off, then a slow zoom on the tagline | "Match AI tells you what happened. WhyLine tells you why it mattered, and proves it. Every number, every name, traced back to the data." |
| 0:15–0:35 | **The setup** | Point out Viewer A, Viewer B (Urdu, 30 s delay), the agent newsroom panel header "Microsoft Foundry agents · Agent Framework workflow" | "Two fans, one match. An English analyst watching live, and a casual Harbour United fan in Urdu, thirty seconds behind. Six agents work as a newsroom: an Analyst, a Narrator, a Localizer and a Recap writer on Microsoft Foundry models, with a rule-based Director and Verifier keeping them honest." |
| 0:35–1:05 | **Spoiler-safe goal** · chapter *22' Goal · NBC* | Goal on Viewer A; card appears. Viewer B's "insights held" counter; 30 s later the Urdu card appears and is read aloud by the neural voice | "Northbridge score. The analyst gets the why straight away. Viewer B hasn't seen it yet, so their card is held until their own stream gets there, and then it arrives in Urdu, read aloud by an Azure AI Speech neural voice." |
| 1:05–1:20 | **Why line** | Click a sentence in Viewer A's card; the evidence events are drawn on the pitch | "Every sentence is a citation. Click it and the events that prove it are drawn on the pitch. That's the why line." |
| 1:20–1:50 | **Explained momentum** · chapter *52' Tactical switch · HAR* | The *Momentum, explained* chart turns; a yellow marker appears; the newsroom panel shows Director, Analyst, drafts and Verifier approvals | "Harbour United switch to a high press. The momentum chart turns, and the agents explain why, from computed facts like pressures per minute and high regains. You can watch the newsroom work: the Director decides it matters, the Analyst briefs, the Narrators draft, and the Verifier checks every number before anything is published." |
| 1:50–2:15 | **The payoff and personalization** · chapter *60' Goal · HAR* | Goal; switch Viewer A's mode to Audio Description before the goal; both cards side by side | "The press pays off. Same goal, two stories: for the Harbour fan, in Urdu, and an audio description that says where on the pitch the action is, for blind and low-vision fans." |
| 2:15–2:30 | **Catch me up** | Press *Catch me up* on Viewer B | "Joined late? Catch me up recaps only what this viewer has already seen, never beyond it." |
| 2:30–2:45 | **Trust, measured** | Cut to the evaluation table (README or `eval/report.html`), then Foundry Tracing with one trace per moment | "We measure it. On sixty cards: one hundred percent of numbers and names trace to computed facts, groundedness 4.57 out of 5, judged on Foundry. Every moment is one OpenTelemetry trace in Foundry." |
| 2:45–3:00 | **Close** | Architecture slide, then the Studio at full time (chapter *90' Full time and recap*) and the repo URL | "WhyLine: explained, verified, personalized match intelligence, built on Microsoft Foundry and Agent Framework. Not just what happened. Why it mattered, and how we know." |

## Tips

- **Pace:** about 150 spoken words a minute. The voiceover above is about 300 words (two minutes), which leaves about
  a minute for the neural voice and the visuals to breathe. Duck the voiceover under the Urdu voice in scene 3.
- **Waiting:** at 4× a chapter takes 30–60 seconds of real time before its card lands. Keep recording and cut the
  wait, or speed it up 2× in the edit with a small "4× replay" label.
- **Mouse:** move slowly and only when it matters; zoom in (in the edit) on the card, the held counter and the
  newsroom panel when the voiceover mentions them.
- **Captions:** add them. Judges often watch muted, and the Urdu card needs an English subtitle.
- **Fallback:** if Foundry is slow or down while recording, the template agents
  (`--launch-profile "WhyLine (Template agents)"`) play the same match through the same Verifier and UI, but say so
  in the video if you use them.

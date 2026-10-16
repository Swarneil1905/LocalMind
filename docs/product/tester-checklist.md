# Tester preparation checklist

Prepared on October 14, 2026. **Recruitment and independent native-platform access are not arranged. No invitations have been sent.** This checklist is the Day 1 deliverable; recruiting people and booking devices remain pending work.

Use the scenarios in [demo journeys](demo-journeys.md). The current text-chat and manual task screens can be observed. Source-change repair and voice are future targets and must not be presented to testers as finished capabilities.

## Recruit three participants without recording personal identities here

| Slot | Useful perspective | Intended observation | Recruitment | Session |
| --- | --- | --- | --- | --- |
| T01 | A curious learner who uses chat tools occasionally | Understand an explanation and correct its context | Unarranged | Not booked |
| T02 | Someone who organizes a small personal or work project | Find project/task controls and verify a saved result | Unarranged | Not booked |
| T03 | Someone who prefers speaking or keyboard navigation | Assess text interaction now; voice when an implemented pilot is available | Unarranged | Not booked |

These are recruitment targets, not claims about existing people. Store any contact details outside this repository in the user's chosen contact system. Record feedback here using the slot only. Do not ask for private messages, documents or account credentials.

## Arrange native platform coverage early

| Platform | Owner | Independent access | Build to test | Evidence status |
| --- | --- | --- | --- | --- |
| Windows x64 | Unassigned | Unarranged; a developer machine does not fill the independent tester slot | Pending | Not run |
| Linux x64 | Unassigned | Unarranged | Pending | Not run |
| macOS Intel | Unassigned | Unarranged | Pending | Not run |
| macOS ARM64 | Unassigned | Unarranged | Pending | Not run |

One person may help with more than one platform; three usability participants do not automatically cover four platform targets. Ask device owners for the OS version, architecture and approximate RAM/GPU capability, not serial numbers or personal identifiers. Packaging success in CI does not prove that a native install launches, displays correctly, accesses a microphone or shuts down cleanly.

- [ ] Identify a willing owner for each native target, or record the coverage gap.
- [ ] Agree a session date and a way to receive the exact candidate installer.
- [ ] Record the build/commit and checksum supplied to each tester.
- [ ] Prepare an install/launch/quit check on a synthetic profile.
- [ ] Later, arrange an upgrade test using a disposable 0.7.0 profile containing synthetic conversations and tasks.
- [ ] Keep a platform unreleased if its required native acceptance evidence is unavailable.

## Invitation draft for Swarneil to send

> I'm testing LocalMind, a desktop app that runs a local AI model. Would you be willing to try a 10-minute session and tell me where it is useful or confusing? You would use fictional prompts and a test project, so no personal files or connected accounts are needed. I'm testing the product, not your ability to use it. Text notes are enough; a screen or audio recording would happen only if you agree. You can stop at any time. Let me know your desktop operating system and a time that works for you.

This is a draft only. Agree recording and data retention separately before the session; accepting an invitation is not recording consent.

## Moderator preparation

- [ ] Confirm the participant slot, time and platform without adding contact details to this document.
- [ ] Record the exact build, model digest and profile. Confirm the tested binary corresponds to the stated build.
- [ ] Use a disposable test profile or clearly isolated synthetic project. Do not erase an existing user profile.
- [ ] Preload the selected model so downloads do not consume the 10-minute usability session. Measure first-run/download behavior separately.
- [ ] Prepare **Demo Learning** with no tasks titled **Compare one boosting round**. This is a manual setup step until fixture tooling exists.
- [ ] Use Web, Knowledge and Memory off for the controlled conversation baseline and no connected accounts. Record any deviation.
- [ ] Check that the app launches and the local model responds. A readiness problem is a finding; do not conceal it by swapping in a different app.
- [ ] Prepare a timer and the observation table below. Prefer written notes unless recording is explicitly agreed.
- [ ] Explain which capabilities are currently available. Skip future-only journeys and mark them **Blocked: not implemented**.

## Ten-minute uncoached session for the current build

Give the participant the goal text below without naming button locations. Observe their first attempt. If they ask for help, first ask what they expected to happen. Do not guide them through the interface during the uncoached attempt. If they cannot continue, record the block and offer to end that task or continue with assistance; mark assisted completion separately.

| Time | Goal or moderator action | What to observe |
| --- | --- | --- |
| 0:00–0:30 | Confirm consent for notes and any separately agreed recording. Say: “We are testing the app, not you. Please tell me what you expect as you go.” | Consent, starting build and readiness. |
| 0:30–3:30 | Goal: “Use LocalMind to understand how another small tree can improve a prediction in XGBoost. Ask for one concrete example and then a short explanation of anything unclear.” | First useful response, relevance, follow-up continuity, readable math/code and whether they can find the composer. |
| 3:30–5:30 | Provide two messages in order: “In this fictional scenario, my learning-project submission is due October 18, 2026 at 6 p.m. America/Chicago. I have 30 minutes to practise.” Then: “Correction: it is due October 16 at the same time, and 5 of those minutes are needed for setup. Update the plan.” | Does the final reply keep the corrected deadline and fit 30 minutes? Does it introduce invented context or claim an action happened? |
| 5:30–7:30 | Goal: “Using the app's task screens, save a task called Compare one boosting round in Demo Learning. Check that it was saved, then mark it done.” | Finding the right project, task creation, persisted visible result and status change. This is a manual-task baseline, not chat-to-task automation. |
| 7:30–10:00 | Ask the feedback questions below. Let the participant finish explaining. | Confusion, usefulness, a specific repeat-use situation and the main obstacle. |

If generation consumes a time window, record the elapsed time and incomplete task rather than rushing the participant or pretending completion. The observer may stop after 10 minutes; later troubleshooting is a separate session. The more detailed arithmetic and failure checks in the demo document belong in developer evaluation and do not have to fit this short user session.

### Feedback questions

1. What did you expect the app to do that it did not do?
2. Which answer or action was most useful, and what made it useful?
3. Where did you hesitate or feel unsure whether something had worked?
4. Is there a specific task you would return to LocalMind for during the next week? “None” is a useful answer.
5. What would stop you from using it again?

Avoid asking only whether they “liked it.” Ask about a concrete workflow and preserve negative feedback. Three short sessions are formative evidence, not a market-demand or retention result.

## Later sessions for the planned pilots

Do not add these to the current 10-minute session until the corresponding build has passed developer checks.

| Pilot | Uncoached user goal | Required evidence before inviting a tester |
| --- | --- | --- |
| Reviewed project repair | “The workshop date changed. Find affected work, update the summary and briefing task, and leave the public note unchanged.” | Synthetic fixtures; safe staging; selective review; stale-preview rejection; interrupted-apply recovery. |
| Voice-to-task | “Speak a task request, correct its transcript, save it, confirm it appears in Tasks, then stop the spoken response.” | Visible mic lifecycle; editable transcription; actual task receipt; duplicate protection; reliable Stop; accessible text fallback. |

Use the same 10-minute structure: 30 seconds of consent/readiness, up to 7 minutes on one pilot goal, then 2.5 minutes of feedback. If the pilot is unavailable or disabled, record that status instead of substituting a mockup and counting it as completion.

## Observation and outcome record

For each task, classify the first attempt as **Unassisted completion**, **Assisted completion**, **Not completed**, or **Blocked by unavailable feature**. Do not count the last category as an attempted working-feature success. Record a technical failure separately from whether the participant found the controls.

| Field | Record |
| --- | --- |
| Participant slot / date / platform | Pending |
| Build / commit / model digest / profile | Pending |
| Journey and exact synthetic prompt/goal | Pending |
| Initial availability and model warm/cold state | Pending |
| Outcome for each task | Not run |
| Start / first visible response / finish times | Pending |
| Corrected deadline and timing constraints retained? | Not checked |
| Task persisted and status changed? | Not checked |
| Help requests / points of hesitation | Pending |
| Error text and recovery attempted | Pending |
| Useful result in the participant's own words | Pending |
| Specific workflow they would repeat, or none | Pending |
| Main reason they would not return | Pending |
| Consent and reference for any retained recording | None recorded |

Summarize counts as `unassisted / attempted available tasks`, with assisted and failed counts alongside. Report latency observations with their build, device and model; do not turn a few sessions into a general performance claim. Retain screenshots or recordings only under the agreed consent and retention terms, and keep personal data out of the repository.

## Follow-up decisions

- [ ] Group findings into answer quality, navigation/state clarity, correctness, responsiveness and recovery.
- [ ] Save a reproducible synthetic example for each significant defect.
- [ ] Prioritize data loss, false completion claims and blocked core workflows before cosmetic polish.
- [ ] Add actual evidence to the appropriate backlog/gate item; do not mark recruitment complete until participants agree.
- [ ] Re-test changed behavior on the final candidate build. Prior results do not establish that later binaries pass.
- [ ] Keep the four strategic directions in scope review: consistent workspace and rehearsal are the first pilots; portable procedures and private coordination remain later research.


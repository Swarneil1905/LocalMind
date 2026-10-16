# Product demo journeys

Prepared on October 14, 2026 for Day 1 of the six-week product plan. These are repeatable synthetic scenarios and acceptance targets, not a report that the journeys have passed.

## What the current code supports

| Surface | Present in the inspected code | Boundary for this demo |
| --- | --- | --- |
| Text chat | Streaming replies, conversation history, optional web search, memory and knowledge context | Response usefulness and correction handling need measurement. The service keeps the last 20 history messages and uses a fixed model configuration. |
| Projects and tasks | Project creation, summaries, conversation association, manual task creation and status changes | A task can be created from the Tasks or Projects screen. Chat has no task tool dispatcher. The current task form does not expose a due-date input. |
| Knowledge | Document search and source file paths | The desktop chat request does not pass a project ID to retrieval. Association with a project does not prove retrieval isolation. |
| Source-change repair | No implemented dependency graph, impact list or prepare/review/apply flow found | Journey 2 is a future target. Existing memory links and database migration staging do not implement this workflow. |
| Voice | An unavailable preview with a link back to text chat | Journey 3 is a future target. The current Voice screen explicitly says it captures no microphone audio. |
| Answer formatting | Markdown with GitHub-flavored Markdown support | Math rendering is not configured in the inspected message component. Record raw formula text as a presentation defect. |

Source basis: [desktop chat and task commands](../../apps/desktop/src-tauri/src/lib.rs), [chat service](../../services/ai/app/api/routes/chat.py), [Tasks screen](../../apps/desktop/src/components/TasksPage.tsx), [Projects screen](../../apps/desktop/src/components/ProjectsPage.tsx), [Voice preview](../../apps/desktop/src/components/BuddyPage.tsx), and [answer renderer](../../apps/desktop/src/components/MessageList.tsx). This is a source review; an installed application may differ from the checkout. Record the tested build before any demonstration.

## Preparation and evidence rules

- Use a disposable test profile or dedicated synthetic conversations and projects. Do not reset a real profile or connect personal accounts to prepare these fixtures.
- Record the app version/build, commit when known, operating system, model name and digest, context/sampling settings, and whether the model was already loaded.
- For Journey 1, turn off Web, Knowledge and Memory in the test chat to isolate conversational behavior. Use a profile with no connected accounts. If testing another configuration, record it as a separate run.
- Record the full prompts and replies, first visible response time, total response time and any errors. Do not replace a failed answer in a recording with an answer from a different model or run.
- Use `Not run`, `Pass`, `Fail`, `Blocked: not implemented`, or `Assisted` as outcomes. A service test and a native app demonstration are different evidence types.
- The fixture text below is a specification to create in a dedicated test folder when that journey is implemented. This document does not create those files or modify application data.

## Journey 1: understand a concept and adapt to a correction

**Current phase:** runnable as a text-chat baseline. Desired answer quality is an acceptance target; it is not established by the presence of a chat box. Related packages: `BASE-01`, `EVAL-01`, `CHAT-01`, `CHAT-02`, `MODEL-01`, `UX-01` and `GATE-01`.

### Starting fixture

Create a fresh synthetic conversation named **Demo: learning a boosting round**. The fictional learner knows basic Python and wants intuition, a numerical example and a short practice plan. No files, external facts, package installation or actual training job are needed.

### User steps

Send these prompts in the same conversation, waiting for each reply before continuing:

1. **Concept:** “Help me understand XGBoost for a learning project. I know basic Python. Explain why adding another small tree can improve predictions. Start with intuition and one tiny example, rather than an installation checklist.”
2. **Concrete follow-up:** “Use targets 3, 5 and 7 with initial predictions 4, 4 and 4. For this simplified squared-error illustration, suppose the next tree perfectly predicts the residuals. With a learning rate of 0.5, show the residuals and updated predictions. Explain what this illustration leaves out of real XGBoost.”
3. **Plan:** “In this fictional scenario today is October 15, 2026. My learning-project submission is due October 18 at 6 p.m. America/Chicago. I have 30 minutes this evening. Turn the example into a practical learning session with a short self-check.”
4. **Correction:** “Correction: the submission is due October 16 at 6 p.m. America/Chicago, not October 18. I still have 30 minutes and need 5 minutes for setup. Replan the session, state the corrected deadline and keep the focus on understanding one boosting round.”

### Success checks

- The explanation connects successive trees with correcting the current prediction under a loss function; it does not merely list installation and training commands.
- The simplified residuals are `-1, 1, 3`, and the updated predictions are `3.5, 4.5, 5.5`. The reply identifies the perfect residual fit as a simplifying assumption, not a guarantee about real XGBoost.
- The later replies retain the learning topic and latest deadline: October 16, 2026 at 6 p.m. America/Chicago.
- The final plan totals at most 30 minutes, includes 5 minutes for setup, and gives a concrete self-check. It does not introduce an unmentioned team or claim to have created a task, trained a model or edited files.
- The user can read any math/code, copy an answer and remain at their chosen scroll position while the answer streams.

### Failure and recovery

If the model repeats the old deadline, gives incorrect arithmetic or claims an action happened, save that exact answer as a failed case before retrying. If generation stalls, use the available Stop control and record whether it responds. Retry in a fresh conversation with the same prompts and profile as a separate run; do not silently switch models or remove the correction turn. Record raw LaTeX or clipping independently from answer correctness.

### Required evidence

**Native app:** one complete transcript or consented screen recording, build/model details, timings and the completed checklist above. A passing service response cannot establish readable math, usable controls or correct scroll behavior.

**Service test:** the same ordered conversation history with saved request settings and raw responses. This can measure model behavior and later routing regressions, but cannot prove that the installed desktop passes the journey.

## Journey 2: review the impact of a changed project fact

**Current phase:** future pilot; mark **Blocked: not implemented** today. Projects and manually managed tasks are present, but source dependencies, stale-work detection and reviewed file repair are not. Related packages: `KNOW-01`, `KNOW-02`, `GRAPH-01`, `GRAPH-02`, `SAFE-01`, `SAFE-02`, `SAFE-03` and `GATE-02`.

### Starting fixtures for the future pilot

Use a dedicated folder named **LocalMind Demo Workshop** containing only these synthetic Markdown files:

| File | Initial content |
| --- | --- |
| `workshop-brief.md` | `Workshop date: October 24, 2026.` followed by `Prepare the briefing one day before the workshop, at 5 p.m. America/Chicago.` |
| `team-summary.md` | `The workshop is on October 24, 2026. The briefing is due October 23 at 5 p.m. America/Chicago.` |
| `public-note.md` | `Workshop: October 24, 2026. Publication has not been approved.` |
| `supplies.md` | `Bring 12 notebooks. This quantity is independent of the workshop date.` |

Create the synthetic project **Demo Workshop** and one task **Prepare the briefing**. The future test fixture sets its due time to `2026-10-23T17:00:00-05:00`; this is not currently available through the task-creation form. Capture the returned project/task IDs rather than assuming fixed IDs.

The future dependency interface must let the user confirm links from the workshop date and relative briefing rule to `team-summary.md`, `public-note.md` and that task. `supplies.md` has no such link. Record initial hashes, text and task fields before preparing any repair.

### User steps once the pilot exists

1. Select only the synthetic folder and confirm the described links. Inspect the source fact and linked targets.
2. Change the source date in `workshop-brief.md` to **October 25, 2026**, leaving the one-day-before rule unchanged. Request a rescan.
3. Inspect the affected-work list. The two linked text artifacts and briefing task should be stale; `supplies.md` should be unchanged.
4. Ask LocalMind to prepare repairs. Inspect the staged diff and the reason for every proposed change. Confirm that originals remain intact at this point.
5. Accept the `team-summary.md` change and task due-date change. Reject the `public-note.md` change because publication remains unapproved. Apply only the selected changes.
6. Verify the summary says October 25 and the task is due October 24 at 5 p.m. America/Chicago. Confirm that the public note retains its old text and remains visibly stale.
7. Exercise the supported restore operation for the changed text artifact and inspect the result. Do not imply that restoring a file also restores task state unless the implemented recovery contract does so.

### Success checks

- The impact list is based on confirmed project links and includes the originating fact, not an unsupported model guess about unrelated files.
- Preparing or rejecting repairs writes no original file and changes no task. Staging is confined to an app-owned area.
- Only selected edits are applied. The rejected public note is not labeled consistent or silently fixed. Unrelated supplies remain byte-identical.
- The task update produces an actual execution result; the model saying “updated” is insufficient. Replaying the same operation does not duplicate a task or apply the edit twice.
- A stale preview is rejected if the original changes after preparation. Interrupted operations retain enough journal/backup evidence for the supported recovery behavior.

### Failure and recovery

Run a second synthetic case in which `team-summary.md` is edited after preview and before apply. The app must refuse the stale edit and offer a fresh preparation rather than overwrite the user's new content. Inject a failure between writes in a service fixture and inspect the journal. On restart, recover or clearly report partial completion; never present an incomplete operation as successful. Stop the pilot if these checks fail and retain the fixture for diagnosis.

### Required evidence

**Native app:** selected-folder scope, confirmed links, affected-work list, before/after review, selected versus rejected edits, task state after refresh, and recovery outcome. A rendered mockup or manual file edit is not an executed product journey.

**Service test:** fixture hashes and task records before preparation, before apply and after recovery; explicit expected affected IDs; stale-preview, interruption and replay cases. These tests establish data behavior, not the usability of the review screen.

## Journey 3: turn a spoken request into a verified local task

**Current phase:** future pilot; mark **Blocked: not implemented** today. Voice capture, transcription, speech output and chat-to-task dispatch are not connected. A manual task added through the existing task form is useful baseline evidence, but is not a voice success. Related packages: `ACT-01`, `VOICE-01` through `VOICE-07`, `UX-03` and `GATE-03`.

### Starting fixtures for the future pilot

Use the synthetic project **Demo Learning** with zero tasks whose title begins **Compare one boosting round**. Record its ID and initial task count. Download the selected licensed speech/model assets before measuring a warm session. Use one stock voice; voice cloning is outside this journey. Begin with the microphone inactive and no personal accounts connected.

### User steps once the pilot exists

1. Deliberately start push-to-talk and, when the operating system asks, grant microphone access. Confirm the visible recording state.
2. Say: “Create a task called Practice one boosting round in Demo Learning. Do not set a due date.” Stop recording.
3. Inspect the editable transcript. Deliberately change **Practice** to **Compare** before sending. This tests editing without assuming that recognition will make a particular mistake.
4. Send the corrected request. Inspect the action result and open the Tasks screen. Refresh and verify exactly one **Compare one boosting round** task in **Demo Learning**, with no due date.
5. Hear a spoken confirmation backed by the same successful task receipt. Check that the matching text remains available.
6. Interrupt a subsequent spoken explanation using the supported Stop control, then continue with another text or voice turn. The previous turn must not resume or attach late audio to the new turn.

### Success checks

- No capture occurs before an explicit start. The transcript can be corrected or discarded before submission; cancelled audio creates no task.
- The corrected text, not the original transcript, becomes the submitted message and task title.
- An actual task ID and result are returned and agree with the persisted task shown after refresh. Replaying the same operation after a lost acknowledgement creates no duplicate. A separate intentional new request is not that replay test.
- Spoken confirmation does not precede a successful action receipt. Errors are explained in text and do not claim completion.
- Stop releases playback and cancels the matching generation; late events cannot affect another turn. Keep interruption turn-based for this pilot rather than claiming native full-duplex speech.

### Failure and recovery

Test denied microphone permission, silence, device removal and model unavailability with synthetic input. Each should preserve any existing typed draft and offer a clear recovery route. If transcription or speech output fails, continue in text without claiming voice passed. Test a lost task acknowledgement in the service harness using one operation ID. Verify retry against actual task count, not just a repeated success sentence. Temporary recordings should follow the implemented deletion policy.

### Required evidence

**Native app:** visible microphone state, editable transcript before and after correction, submitted text, action receipt, persisted task after refresh, audible output and interruption behavior. Record audio only with the tester's explicit consent; written observation is an alternative.

**Service test:** fixed audio fixtures, transcript-to-request mapping, turn IDs, typed task arguments, execution receipt, duplicate protection and cleanup results. A mocked speech engine can test state transitions but cannot establish microphone access, recognition quality or playback on a real device.

## Relationship to the four product directions

| Direction | Demonstrated here | Current phase |
| --- | --- | --- |
| Consistent workspace | Journey 2 links a changed fact to affected text and a local task. | Planned narrow pilot using user-confirmed links. |
| Rehearsal before action | Journey 2 prepares on copies, reviews and applies selected edits. | Planned narrow pilot; no universal desktop undo. |
| Portable proven procedures | Later, export a passing synthetic repair recipe and rerun its checks on a fresh profile. | Research after the local repair path works; not part of today's demo. |
| Private coordination | Later, test an explicit availability exchange between two synthetic participants. | Research simulation; no live multi-organization or cryptographic privacy claim. |

Journey 1 establishes conversational usefulness. Journey 3 supplies an accessible way to use the same action path. Neither substitutes for the consistency and recovery work.

## Run record

Copy this table for each attempt. Attach only synthetic, consented evidence.

| Field | Result |
| --- | --- |
| Journey / attempt ID | Not run |
| Date / build / commit / platform | Pending |
| Model digest / profile / warm or cold | Pending |
| Native UI, service, or mock evidence | Pending |
| Fixture revision and settings | Pending |
| Outcome and completed checks | Pending |
| First visible response / total elapsed time | Pending |
| Failure or user confusion | Pending |
| Recovery attempted and result | Pending |
| Transcript, recording or test-output reference | Pending |


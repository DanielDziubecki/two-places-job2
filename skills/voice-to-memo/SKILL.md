---
name: voice-to-memo
description: Turn the tram desk's voice memo from the night tram-3's feed refresh stalled into notes the next reader can use, and hand them in. Use it for job 2 of the course challenge, on that job's MCP link.
metadata:
  version: "1"
---

# voice-to-memo

Job 2: the tram desk's end-of-shift voice memo (1 Nov 2026), as one note per finding.

Tools on the job's MCP link: `transcribe`, `memo_write`, `memo_search`, `submit`. Every tool but `transcribe` takes the step token from your last call on this job. If you lose it, start again from step 1.

## Procedure

1. **transcribe** — `transcribe` the clip. Its URL comes with the job.
2. **notes** — one `memo_write` per finding. A finding is anything that happened on a line: a fault, a freeze or stall, a hold, a reset — every time it happened, even when it repeats. The talk around it is not a finding. Write each note as `<what happened> @m:ss`, where `@m:ss` is the segment's offset in the clip. One finding per note, one `@m:ss` per note.
3. **confirm** — `memo_search` with no query. Check that every finding has its note, each with its `@m:ss`. Write what is missing, then search again.
4. **submit** — `submit` the notes, one per line as you wrote them, with `skillVersion: "1"`.

Keep the times said in the clip as they are said.

# Report Answers by Voice - Implementation Plan

## Overview
Two features adding dictation and custom answers to dev reports.

## Feature 1: Dictation in Every Text Box
### Client-side (dev-report-notes.js):
- Add microphone button to each comment textarea and "Something else" box
- States: idle, recording (red, timer, level bars), transcribing, done, failed
- One box records at a time (mutual exclusion)
- Transcript lands in the box for editing

### Protocol (protocol.ts and CONTRACT.md):
- New message: `dictation-start` - host starts recording a field
- New message: `dictation-result` - host returns transcript to page
- New message: `dictation-failed` - host reports failure
- Handles transcription through existing Gateway path

### Host-side (frameHost.ts):
- Receives dictation-start with fieldId
- Uses existing recorder and transcription
- Posts result back with transcript

## Feature 2: "Something else" Option
### Client-side (dev-report-notes.js):
- Auto-add "Something else" radio to each question
- Shows text box with microphone when selected
- Queue answer button disabled until box has text
- Special optionValue for own-answer (reserved value)

### Gateway:
- Accept answer with reserved own-answer value
- Store owner's custom text with answer
- Display as "OWN ANSWER" in cc-dev-reports

## Files to modify:
1. packages/client-core/src/devreports/dev-report-notes.js - add UI and logic
2. packages/client-core/src/devreports/protocol.ts - add dictation messages
3. packages/client-core/src/devreports/CONTRACT.md - document new messages
4. packages/client-core/src/devreports/frameHost.ts - handle dictation requests
5. src/CcDirector.Gateway/Api/DevReportEndpoints.cs - accept own-answer value
6. src/CcDirector.Gateway/Api/TeamQuestionEndpoints.cs - accept own-answer value

## Testing:
- devReportNotes.test.ts - own-answer logic
- frameHost.test.ts - dictation protocol
- Browser proof with screenshots

## Proof artifacts:
- Cockpit screen: microphone recording, transcript landing
- Phone screen: same
- cc-dev-reports answers output: own-answer shown as "OWN ANSWER (not one of your options)"

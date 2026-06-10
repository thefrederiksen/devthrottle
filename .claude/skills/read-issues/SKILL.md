---
name: read-issues
description: List GitHub issues, let the user pick one, and download all content including images so an agent can work on it. Use this when the user wants to browse, review, or select issues to work on.
disable-model-invocation: true
argument-hint: "[optional: issue number, label filter, or search query]"
---

# Read GitHub Issues

Browse, filter, and select GitHub issues, then download all content and images so an agent can fully understand and work on the selected issue.

## Input: $ARGUMENTS

## Instructions

### Step 1: List Issues

If a specific issue number is provided in `$ARGUMENTS`, skip to Step 2.

If a search query or label filter is provided, use it:
```bash
# By label
gh issue list --label "bug" --state open

# By search
gh issue list --search "QUERY" --state open

# All open issues
gh issue list --state open --limit 30
```

If no filter is provided, list all open issues:
```bash
gh issue list --state open --limit 30
```

Display the issues in a clear format showing:
- Issue number
- Title
- Labels
- Assignee
- Created date

### Step 2: Select an Issue

- If `$ARGUMENTS` contains a specific issue number, use that directly
- Otherwise, use AskUserQuestion to let the user pick which issue to work on
- Present the top issues as options with their titles

### Step 3: Download Full Issue Content

Once an issue is selected, fetch everything:

```bash
# Get full issue details including body
gh issue view ISSUE_NUMBER

# Get all comments
gh issue view ISSUE_NUMBER --comments

# Get issue metadata as JSON for structured parsing
gh issue view ISSUE_NUMBER --json title,body,labels,assignees,comments,state,milestone,createdAt,updatedAt,author
```

### Step 4: Extract and Download Images

1. Parse the issue body and all comments for image URLs
2. Look for patterns like:
   - `![alt](https://...)`  — Markdown images
   - `https://github.com/user-attachments/...` — GitHub uploaded images
   - `https://user-images.githubusercontent.com/...` — Legacy GitHub images
3. For each image found:
   - Download it to a temporary location using `curl` or `Invoke-WebRequest`
   - Save to a temp directory like `$env:TEMP/gh-issues/ISSUE_NUMBER/`
   - Read the image using the Read tool so Claude can see it
4. If no images are found, note that and continue

### Step 5: Present Complete Issue Context

Provide a comprehensive summary including:

```markdown
## Issue #NUMBER: TITLE

**State:** open/closed
**Labels:** label1, label2
**Assignee:** @username
**Created:** date
**Author:** @username

### Description
[Full issue body]

### Images
[Display all downloaded images]

### Comments (N total)
[All comments with authors and dates]

### Key Requirements
- [Extracted actionable items from the description]
- [Any acceptance criteria mentioned]

### Related Files (if mentioned)
- [Any file paths or code references from the issue]
```

### Step 6: Offer Next Steps

After presenting the issue, ask the user:
- Would you like to implement this issue? (suggest using `/developer-agent NUMBER`, or `/implementation-loop NUMBER` to drive it through Dev+QA to merged)
- Would you like to assign it to yourself?
- Would you like to add a comment?

## Important Notes
- Always use `gh` CLI for all GitHub operations
- Download images so Claude can actually see and understand visual bug reports
- Parse both the issue body AND all comments for complete context
- If the repo has many issues, default to showing only open ones

"""Fill one team so it can be shown as it is - or empty it again (owner, 8 Oct 2026).

The content is the approved team showcase mockups (devthrottle_internal docs/teams/2026-10-08-team-showcase-mockups.html,
Screens 1, 2, 4, 5, 6 and 7): the team named "DevThrottle"; Peter Hansen (Manager), Mary Olsen and Mark Berg
(Developers) and Lisa Jensen (Collaborator), who are made-up members, not accounts - they cannot sign in, hold no paid
seat, have no Directors and are sent no email; the Mentor's blocks about them for the last closed week, and the Owner's
own Mentor page; two questions; four requests with their history; and the reports.

Every row is written with ONE tag, and --remove deletes every row that carries it, and nothing else. All times are
counted back from the moment the Gateway runs the write, so the week shown is recent.

Run it, in the foreground, with the hosted Gateway's administrator service token in ADMIN_SERVICE_TOKEN:

    cc-secrets run admin-service-token -- python scripts/showcase/team-showcase.py --gateway https://<gateway> --team <team id>
    cc-secrets run admin-service-token -- python scripts/showcase/team-showcase.py --gateway https://<gateway> --team <team id> --remove

--print shows the content that would be sent, and sends nothing.

Not included, and why: the waiting invitation for anders@devthrottle.com. Writing one sends nothing, but the Team page
offers "Resend" on every waiting invitation, and one click would mail an address that is not a real person. The
factory report ("Weekly sales numbers") is not included either: a factory's report is not a team report, and there is
no team row it could be written as.
"""

import argparse
import json
import os
import sys
import urllib.error
import urllib.request

TOKEN_VAR = "ADMIN_SERVICE_TOKEN"
DEFAULT_TAG = "showcase-2026-10"

PETER, MARY, MARK, LISA, OWNER = "peter", "mary", "mark", "lisa", "owner"

CONTENT = {
    "teamName": "DevThrottle",
    "members": [
        {"key": PETER, "name": "Peter Hansen", "email": "peter@devthrottle.com", "role": "Manager", "joinedDaysAgo": 62},
        {"key": MARY, "name": "Mary Olsen", "email": "mary@devthrottle.com", "role": "Developer", "joinedDaysAgo": 48},
        {"key": MARK, "name": "Mark Berg", "email": "mark@devthrottle.com", "role": "Developer", "joinedDaysAgo": 41},
        {"key": LISA, "name": "Lisa Jensen", "email": "lisa@devthrottle.com", "role": "Collaborator", "joinedDaysAgo": 30},
    ],
    # Screen 1: the Owner's own Mentor page.
    "ownerPersonal": {
        "person": OWNER,
        "tone": "mixed",
        "workedOn": "41 sessions on 6 repositories. Most time: the Teams first version (18 sessions), the website (9), "
                    "the Reddit tool (6).",
        "howItWent": "23 pull requests merged, median 3 hours from start to merge. 5 sessions stopped on a usage limit "
                     "and waited 40 minutes on average.",
        "wentBadlyAndWhy": "On Tuesday you restarted the Cockpit menu task three times. The first instruction did not say "
                           "which file holds the menu, so each session searched for it again. The fourth attempt named "
                           "the file and the sections and finished in 40 minutes.",
        "quote": "fix the menu, it's too long",
        "oneThingToTry": "Start a task by naming the file or page and what \"done\" looks like. Your sessions that did "
                         "this finished 2.4 times faster this week.",
    },
    # Screen 4: the team's Mentor page.
    "teamMentor": [
        {
            "person": MARY,
            "tone": "good",
            "workedOn": "The invoice export and two bug fixes. 14 sessions, 6 pull requests merged, median 2 hours to merge.",
            "howItWent": "Every task started from an issue with the file named and a test to pass. Her reviews caught two "
                         "real defects in Mark's work.",
            "wentBadlyAndWhy": None,
            "quote": None,
            "oneThingToTry": "She ran one session at a time all week; two in parallel on the bug fixes would have halved "
                             "the waiting.",
        },
        {
            "person": MARK,
            "tone": "hard",
            "workedOn": "The login rewrite. 22 sessions, 1 pull request merged, 2 abandoned.",
            "howItWent": None,
            "wentBadlyAndWhy": "Eleven sessions were stopped and restarted with almost the same instruction, because none "
                               "said what \"finished\" means. The session that worked said which tests had to pass and "
                               "which file not to touch.",
            "quote": "make login work with the new provider",
            "oneThingToTry": "One sentence of \"done means\" in every first instruction. Mary's team skill \"Task brief\" "
                             "does this.",
        },
        {
            "person": PETER,
            "tone": "mixed",
            "workedOn": "Reviewing (19 pull requests) and the release.",
            "howItWent": None,
            "wentBadlyAndWhy": None,
            "quote": None,
            "oneThingToTry": "His reviews waited 9 hours on average; the agent review switched on Monday should take the "
                             "first pass.",
        },
    ],
    # Screens 5 and 7: reports sent to members; two carry the questions.
    "reports": [
        {
            "from": MARY,
            "title": "Invoice export is ready to try",
            "summary": "The invoice export is built and its tests pass. One question decides which orders it includes.",
            "hoursAgo": 3,
            "sentTo": [OWNER, PETER, LISA],
            "readBy": [],
            "question": {
                "id": "cancelled-orders",
                "text": "Should the invoice export include cancelled orders?",
                "options": [
                    {"value": "paid-only", "label": "No - only orders that were paid (matches what the accountants "
                                                    "asked for in the request)", "recommended": True},
                    {"value": "with-cancelled", "label": "Yes - with a \"cancelled\" column", "recommended": False},
                ],
            },
        },
        {
            "from": PETER,
            "title": "Pricing page: who sees it first",
            "summary": "The new pricing page is ready. Before it goes live, one decision: who sees it first.",
            "hoursAgo": 26,
            "sentTo": [OWNER, LISA],
            "readBy": [],
            "question": {
                "id": "pricing-first",
                "text": "Which customers get the new pricing page first?",
                "options": [
                    {"value": "new-signups", "label": "Everyone signing up after Monday", "recommended": True},
                    {"value": "everyone", "label": "Existing customers too, with an email", "recommended": False},
                ],
            },
        },
        {
            "from": MARK,
            "title": "Login rewrite: where it stands",
            "summary": "Three things are left before the new login provider can ship, and one of them needs Peter.",
            "hoursAgo": 22,
            "sentTo": [OWNER, PETER],
            "readBy": [],
            "question": None,
        },
        {
            "from": PETER,
            "title": "Release 2.18 checklist passed",
            "summary": "Every step of the release checklist passed. The release is ready to tag.",
            "hoursAgo": 50,
            "sentTo": [OWNER],
            "readBy": [OWNER],
            "question": None,
        },
    ],
    # Screen 6: requests, with what happened to each.
    "requests": [
        {"from": LISA, "text": "Add a CSV download to the customer list", "daysAgo": 2,
         "steps": [{"state": "accepted", "by": MARK, "daysAgo": 1.5, "reason": None}]},
        {"from": LISA, "text": "The welcome email has the old logo", "daysAgo": 5,
         "steps": [{"state": "accepted", "by": MARY, "daysAgo": 4.8, "reason": None},
                   {"state": "done", "by": MARY, "daysAgo": 4.2, "reason": None}]},
        {"from": PETER, "text": "Show the trial end date on the account page", "daysAgo": 0.2, "steps": []},
        {"from": LISA, "text": "Dark mode for the reports", "daysAgo": 9,
         "steps": [{"state": "declined", "by": OWNER, "daysAgo": 8, "reason": "Not this month."}]},
    ],
}


def post(gateway, path, body, token):
    request = urllib.request.Request(
        gateway.rstrip("/") + path,
        data=json.dumps(body).encode("utf-8"),
        headers={"Content-Type": "application/json", "Authorization": f"Bearer {token}"},
        method="POST",
    )
    try:
        with urllib.request.urlopen(request, timeout=60) as response:
            return response.status, json.loads(response.read().decode("utf-8"))
    except urllib.error.HTTPError as error:
        text = error.read().decode("utf-8", "replace")
        try:
            return error.code, json.loads(text)
        except json.JSONDecodeError:
            return error.code, {"error": text[:500]}


def main():
    parser = argparse.ArgumentParser(description="Fill one team so it can be shown as it is, or empty it again.")
    parser.add_argument("--gateway", help="The Gateway's address, for example https://gateway.example.com")
    parser.add_argument("--team", help="The team's id")
    parser.add_argument("--tag", default=DEFAULT_TAG, help=f"The tag every row carries (default {DEFAULT_TAG})")
    parser.add_argument("--actor", default="the Teams Architect", help="Who is running this, for the Gateway's log")
    parser.add_argument("--reason", default="The owner's team, shown as it is (owner ruling, 8 Oct 2026)",
                        help="Why, for the Gateway's log")
    parser.add_argument("--remove", action="store_true", help="Remove every row that carries the tag, and nothing else")
    parser.add_argument("--print", dest="print_only", action="store_true", help="Print the content and send nothing")
    args = parser.parse_args()

    if args.print_only:
        print(json.dumps(CONTENT, indent=2))
        return 0
    if not args.gateway or not args.team:
        parser.error("--gateway and --team are required (or --print)")

    token = os.environ.get(TOKEN_VAR, "")
    if not token:
        print(f"ERROR: {TOKEN_VAR} is not set. It is the Gateway's administrator service token. Run:\n"
              f"  cc-secrets run admin-service-token -- python {sys.argv[0]} --gateway {args.gateway} --team {args.team}"
              f"{' --remove' if args.remove else ''}", file=sys.stderr)
        return 2

    body = {"team": args.team, "tag": args.tag, "actor": args.actor, "reason": args.reason}
    if args.remove:
        status, answer = post(args.gateway, "/gateway/admin/team-showcase/remove", body, token)
    else:
        body["content"] = CONTENT
        status, answer = post(args.gateway, "/gateway/admin/team-showcase", body, token)

    print(f"HTTP {status}")
    print(json.dumps(answer, indent=2))
    if status != 200:
        print("ERROR: the Gateway did not do it. Its answer is above.", file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main())

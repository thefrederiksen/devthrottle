"""Build the Factories screen QA report (one self-contained HTML file) from the live screenshots in qa-live/."""
import base64
import pathlib

HERE = pathlib.Path(__file__).parent
SHOTS = HERE / "qa-live"


def img(name: str, caption: str) -> str:
    data = base64.b64encode((SHOTS / name).read_bytes()).decode("ascii")
    return (f'<figure><img src="data:image/png;base64,{data}" alt="{caption}">'
            f"<figcaption>{caption}</figcaption></figure>")


STYLE = """
body{font-family:Segoe UI,Arial,sans-serif;max-width:980px;margin:0 auto;padding:16px;color:#1b1f24;background:#fff;line-height:1.5}
h1{font-size:26px;margin:0 0 4px} h2{font-size:20px;margin-top:28px;border-bottom:1px solid #ddd;padding-bottom:4px}
.meta{color:#555;font-size:14px} .ok{color:#116329;font-weight:600} .wait{color:#9a6700;font-weight:600}
table{border-collapse:collapse;width:100%;font-size:14px} td,th{border:1px solid #ddd;padding:6px 8px;text-align:left;vertical-align:top}
figure{margin:12px 0} img{max-width:100%;border:1px solid #ccc} figcaption{font-size:13px;color:#555}
.phones{display:flex;gap:12px;flex-wrap:wrap} .phones figure{flex:1 1 280px}
label{display:block;margin:6px 0} textarea{width:100%;min-height:50px}
"""

HTML = f"""<!doctype html><html><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
<title>Factories Screen QA</title><style>{STYLE}</style></head><body>

<section data-dev-report="header" data-dev-report-status="waiting-on-you">
<h1>Factories screen - QA report</h1>
<p class="meta">Implementation Lead, 6 October 2026. Live on the hosted Gateway at commit ec2f995. Version 2 adds round 2 (your feedback on the list).</p>
</section>

<section data-dev-report="summary">
<p><b>Round 2, your feedback, is live:</b> every FAILING, NEEDS YOU and PAUSED row now says why; a failure stops counting once it is over; waiting items can be handled one by one or in bulk; a factory can be archived and restored, and Tallyhand is archived. Center Consulting offers Talk to Ruth Calder. At your word I marked the stale waiting items handled: 44 on Website Business and 1 on Machine Care, each recorded as your act; 19 newer decisions on Website Business stay, the Gmail sign-in among them. One question remains below: the Director update.</p>
<p><b>The Factories screen is live and you can use it now.</b> The sidebar says Factories. The list shows all ten
factories as one row each - name, what is waiting on you, one status word, and a Talk button for the CEO - worst
first. Each factory has its own page with the goal, the goal number, what waits on you, the CEO's latest work, the
last talk with you, and a Seats tab where every seat has its own Talk button and computer. It works at phone
width.</p>
<p><b>One thing is waiting on you: the Director on SOREN_NORTH is version 2.12.0</b> (running since 1 October), older
than the version that can put a session into a factory. Pressing Talk opened the right session on the first live
test, but in no factory, so the agent could not read or write the factory's memory. The screen now refuses with a
plain sentence instead of opening a half-working talk. The same old Director is also why none of the factories'
scheduled runs are in their factory today. Once you update it, I re-run the Talk test and finish this report.</p>
</section>

<section data-dev-report="questions">
<div data-dev-report-question="update-director" data-dev-report-question-text="May the SOREN_NORTH Director be updated so Talk can finish?">
<h3>The SOREN_NORTH Director is 2.12.0; its launcher already holds 2.16.0. How should it be updated?</h3>
<label><input type="radio" name="update-director" value="you-now" data-recommended> You run File, Smart Restart on it when it suits you, and tell me - it restarts your sessions there, so it should be your moment. I then finish the Talk proof the same day.</label>
<label><input type="radio" name="update-director" value="lead-tonight"> I ask the Director to Smart Restart itself tonight while you are away, and finish the proof in the morning.</label>
<label><input type="radio" name="update-director" value="later"> Leave it for now; close the mission with Talk proved only up to the refusal.</label>
<textarea data-dev-report-comment placeholder="Anything to add (optional)"></textarea>
</div>
</section>

<section data-dev-report="detail">
<h2>Round 2 - your feedback on the live list (7 October)</h2>
<p>You could not tell why Website Business failed, how to clear what Machine Care needed, or why Tallyhand was paused, and you wanted a way to remove a factory. All of it is live now (commit ec2f995):</p>
{img("r2-01-list-before-archive.png", "Every non-RUNNING row now says why under its word: Website Business FAILING because the CEO's git pull failed today at 01:13; Machine Care NEEDS YOU for its 23 September disk alert; Tallyhand PAUSED - Nothing scheduled. Center Consulting offers Talk to Ruth Calder, its CFO.")}
<ul>
<li><b>A failure clears when it is over.</b> Yesterday's four keep-page 404s (12:02) no longer count: the same seat later succeeded for the same businesses. A failure that names no subject (for example "no CEO run had started") clears only when you press Handled - the review caught that such failures were vanishing three seconds later on the seat's own "I emailed the owner" row.</li>
<li><b>Waiting on you is actionable:</b> each item with its text, time, seat, link and Handled; decisions first, newest first. The Gmail sign-in item is now second from the top on Website Business instead of buried.</li>
<li><b>Bulk clear</b>, owner only, with a confirm that counts on the Gateway. On your answer I used it on 7 October at 12:05 UTC: Website Business 44 items (one more had passed the 7-day line since I first opened it), Machine Care 1. Each item got its handled row and one row records your act and the count. Website Business keeps 19 newer decisions. Machine Care now shows FAILING for a new, real failure: its cleaner hit its one-hour run limit at 04:54 today.</li>
<li><b>Archive a factory</b>, owner only. I archived Tallyhand as its first use, as asked: it is off the list, under Show archived with Restore, nothing deleted, recorded as your act. mindzie AI Reports now reads Talk to Max Ridley, since the name is no longer shared.</li>
</ul>
{img("r2-07-bulk-clear-confirm.png", "The bulk clear's confirm on Website Business (first opened and cancelled; used on your answer).")}
{img("r2-11-list-after-bulk.png", "The list after the bulk clear: Website Business 19 decisions, Machine Care failing for today's cleaner run.")}
{img("r2-02-archive-confirm.png", "Archive's confirm for Tallyhand: exactly what happens, before it happens.")}
{img("r2-05-show-archived.png", "Tallyhand under Show archived, with Restore.")}
<p><b>An incident on the way:</b> the first round 2 deploy broke the Factories area for about 25 minutes - the archive change added its database column for the local database only, not for the hosted one. I rolled production back with the rollback workflow, a fix added the hosted database's migration (proved on a real PostgreSQL), and it was redeployed. A guard for exactly this exists but sits in a test suite the default run skips; the fix's pull request proposes moving it.</p>
<p>Follow-ups filed: Restore could switch on a schedule you switched off by hand after an archive (devthrottle#3609), and the scheduler can overwrite a pause made during a run (devthrottle#3610). The account-wide Waiting screen still lists an archived factory's items, if it has any (Tallyhand has none).</p>
</section>

<section data-dev-report="detail">
<h2>What you can use now</h2>
{img("live-01-list.png", "The list, live: ten factories, worst first. Website Business is FAILING (a failed run in the last day), Machine Care NEEDS YOU, Tallyhand PAUSED (none of its seats has a schedule), the rest RUNNING. No CEO where a factory has none.")}
{img("live-02-warmforward-page.png", "WarmForward's page: CEO, seat count, computer, Talk to Nora Hale; goal, goal number, waiting on you, latest from the CEO, last talk with you. No factory has a GOAL.md yet, so it says No goal set yet.")}
{img("live-11-seats-one-zone.png", "The Seats tab: the four seats the factory defines, when each runs, its last run in the same time zone, its computer, and Talk. The computer's change control is marked coming and is not a button.")}
<div class="phones">
{img("live-05-phone-list.png", "Phone width (390 pixels): the list becomes cards, no sideways scroll.")}
{img("live-06-phone-seats.png", "Phone width: the Seats tab as cards.")}
</div>
{img("live-12-talk-refused-old-director.png", "The failure case, live: pressing Talk on a seat while the old Director runs. Nothing is started, and the sentence says why and what to do.")}
{img("live-08-old-address-redirect.png", "An old Factory Agents address (the agents tab of WarmForward) lands on the new Seats tab.")}

<h2>What Talk does, and how far it is proved</h2>
<table>
<tr><th>What the mission asked</th><th>Live result</th></tr>
<tr><td>A new top-level session owned by you, never a child</td><td class="ok">Proved: the first live talk (Nora Hale) had no owner session and no parent, started by a person from the Cockpit.</td></tr>
<tr><td>On the agent's computer, in its factory's folder, named Factory - Agent - talk with the owner</td><td class="ok">Proved: SOREN_NORTH, D:\\ReposFred\\cc-consult\\ideas\\warmforward-factory, "WarmForward - Nora Hale - talk with the owner".</td></tr>
<tr><td>Seated as the agent: its brief, the goal, memory, recent runs</td><td class="wait">Partly: Nora read her brief, the factory files and her journal and opened with what happened and what she needed from you. She could not read the factory memory - the old Director put the session in no factory.</td></tr>
<tr><td>Leaves an activity line and a memory note; the page shows Talked with you</td><td class="wait">Not proved live yet - needs the Director update. Proved in tests: a talked line written through the real activity record shows on the page.</td></tr>
<tr><td>A goal number posted by the command and shown on the page</td><td class="wait">Not proved live yet. The command is deployed; I did not post a number in a CEO's name myself, because the number must be the CEO's. I will ask Nora to post hers in the re-test talk.</td></tr>
</table>
<p>I closed the test talk as soon as the defect showed. A second talk you may have opened yourself (DevThrottle - Ada Brennan) is still open; I did not touch it, and it is in no factory for the same reason.</p>

<h2>Decisions I made, inside the mandate</h2>
<ul>
<li><b>A factory registry on the Gateway.</b> The Gateway knew no list of factories, CEOs or seats. A new command, <code>cc-devthrottle factory register</code>, records each factory's folder, computer, CEO and seats. The briefs stay on disk; this is an index, not a move of the definitions.</li>
<li><b>The ten factories are registered</b> from manifests written from the live schedules and each factory's own files (kept in the mission folder). Five factories had never written an activity row, so their ids come from their folders: warmforward, tallyhand, devthrottle, business-research and cc-factory (Center Consulting). Center Consulting's CFO is not called its head, so it shows No CEO.</li>
<li><b>The goal number command</b> is <code>cc-devthrottle factory goal-number post</code> (value, unit, date, link to how it was measured) and <code>goal-number show</code>.</li>
<li><b>Status words:</b> FAILING is any failed run the factory recorded in the last 24 hours, seat or not (ClickFunnels' runner scripts count). PAUSED includes a factory with nothing scheduled at all (Tallyhand).</li>
<li><b>Changing a seat's computer ships read-only</b>, marked coming. Moving a schedule to another computer is changing a factory's schedule, which the mission kept out of scope.</li>
<li><b>Website Business shows 63 decisions waiting.</b> That is true data: escalations since 21 September that were never marked handled. Each has an I have handled it button on the factory page.</li>
</ul>

<h2>Follow-ups</h2>
<ul>
<li>The old Factory Agents list view and map are still served by the Gateway with no reader: filed to remove (devthrottle#3596).</li>
<li>At phone width with the Cockpit's sidebar left open, every page's content is squeezed, this one included; collapsed, it is right. That is the Cockpit's sidebar, not this screen.</li>
</ul>
</section>

<section data-dev-report="evidence">
<h2>Evidence, and what is not proved</h2>
<ul>
<li>Merged to main, each after a separate review that approved it (review files are in the mission folder): the factory registry and goal number (#3588), the views (#3592), Talk (#3590), the Cockpit (#3595), and the two live-QA fixes - the refusal for an old Director and one time zone per seat row (#3597).</li>
<li>Deployed three times through the deploy workflow; measured outages 4.4, 4.6 and 4.6 seconds. Live commit checked on the health endpoint: 3e7d7f8.</li>
<li>Root cause of the factory-less talk: read from the Director's own log and version (2.12.0, factory membership arrived in 2.13.0); the Gateway sent the factory correctly.</li>
<li>Screenshots were taken in your signed-in Cockpit (soren@centerconsulting.com) on the live Gateway; phone width is a 390-pixel browser window, not a real phone.</li>
<li><b>Not proved:</b> a talk that writes its activity line and memory note live; a goal number posted by a CEO and shown live; a talk from a seat (only its refusal is proved live). All three wait on the Director update. The full parked Gateway suite was not run on every pull request because the machine was short of memory; each pull request ran its own area's tests and the default local gate.</li>
<li>The first review agent reached its usage limit partway through; later reviews were done by the next agent in the reviewer order.</li>
</ul>
</section>
</body></html>
"""

out = HERE / "QA-REPORT.html"
out.write_text(HTML, encoding="utf-8")
print(out, len(HTML))

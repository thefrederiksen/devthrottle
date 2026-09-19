# Phase 2: the decisions taken while building it

Written by the Delivery Lead, 19 September 2026, which built this phase under the owner's override
recorded in `handover-delivery-lead-2.md`. Everything here is a judgement the mandate did not settle
and that a reader of the code would otherwise have to reconstruct. A Reviewer from a different agent
family reads the phase before it merges; these are the places to look hardest.

## 1. Gradle has no proof, so there is no Gradle rule

**The mission's table of first rules names five package caches: npm, pip, uv, NuGet and Gradle.
Four are built. Gradle is not.**

The proof kind those rules rest on is that the tool which wrote the data ships a command that clears
it, and we run that command rather than deleting inside somebody else's store. Gradle ships no such
command. It cleans its own caches on a schedule of its own, and its guidance for clearing them by
hand is to delete the folder - which is precisely the reasoning this tool exists to refuse. A path
somebody else's product wrote is not a proof.

Where the mission's own sections disagree, the one that states the idea wins over the one that lists
examples: section 5's "The one idea" says a rule may recommend removal only when it holds one of
exactly three proofs, and that anything else is reported and never offered. The table in the same
section is ordered by measured value, not by obligation.

Nothing is lost by leaving it out. Gradle's cache is still reported by `scan` and `report` like
anything else of its size; it is simply never offered for removal. If Gradle grows a real cleanup
command, the rule is four lines in `WindowsRuleSet`.

## 2. The NuGet rule points at the package folder, and its command clears more than that

`dotnet nuget locals all --clear` clears several NuGet caches, of which the packages folder is the
largest. The rule measures that folder, because that is the one whose size can be stated as a
measurement, and names the command that clears it. The command clearing more than the rule measured
makes the reported number a floor rather than an overstatement, which is the safe direction: the
reader is never told more will be freed than actually will be.

## 3. The whole cache is offered as one item, not its files one at a time

A cache rule's command clears the whole cache. Offering the files inside it individually would
describe an act nobody can perform - there is no way to ask npm to clear one entry - so each cache
is one candidate, at the cache folder's own path, carrying the whole measured size.

## 4. Five rules, not one, for the package caches

The mission's table shows the package caches as one row. They are built as four separate rules
because each has its own command, its own "what is lost", and its own controls, and a caller can act
on one without the others. A single rule covering all of them would have to report a broken instrument
for all four when one folder would not be listed, and would give the reader one command line that
does not exist.

## 5. A rule declares where it looks, and `recommend` runs only the rules that look inside

A rule examines one known place on the machine: the package cache, a cache folder, the temporary
folder. A caller asking about one folder must not be handed gigabytes found somewhere else entirely -
the bytes would not be inside what was asked about, and the unclassified count, which is a
subtraction of one from the other, would be meaningless. This was found by running the tool: a
recommendation for a five kilobyte scratch folder reported 51.9 gigabytes reclaimable and nought
unclassified.

So every rule carries `LooksIn`, and `RuleSelection` splits the machine's rules into the ones whose
folder sits inside the folder asked about and the ones that do not. **The ones left out are NAMED,
with where they look.** A tool that quietly ran fewer rules than it has reports less to remove and
reads exactly like a cleaner disk.

The consequence, which is correct and worth stating because it looks harsh: asking about a folder no
rule looks inside gives verdict BROKEN and exit code 1, with all of the machine's rules named
underneath. It is not exit 0 and "nothing to remove", because that is the answer a genuinely clean
disk gives and the two must never look the same.

## 6. The reach lines are carried as a list, not matched out of the report's text

The recommendations must repeat what the scan beneath them could and could not see. The first
version picked those lines back out of the scan report by matching how each one starts. That is a
check whose pass condition is finding something: reword a line in the scan report and the
recommendations silently lose their reach line, with nothing failing anywhere.

`ScanReport` now carries `ReachLines` as its own list, written once in `ScanReportBuilder` and spliced
into the report's own lines from there. One rendering, two readers. A test requires every one of them
to appear in the recommendations.

## 7. An unreadable folder inside a cache makes the rule broken

A folder inside a cache that will not be listed means the measured size is short by an unknown
amount. The mission requires measured bytes, never an estimate presented as a fact, so the rule says
it could not do its work rather than printing a smaller number. The same reasoning applies to a
scratch folder whose contents will not be listed: it cannot be measured or checked for open files, so
it is counted and left alone rather than offered on an incomplete reading.

## 8. The scratch folder names, and the one that is deliberately absent

Sixteen names. Each was found by reading where it is created in this repository, and each is created
only inside a project whose name ends in Tests. **The bare name `cc-director` is deliberately not on
the list** although it appears in the temporary folder: product code creates it too, in
`CcDirector.AgentBrain` and `CcDirector.Avalonia`, so it fails the proof. A rule that matched it would
offer a running Director's own folder.

`cc-reclaim-tests`, which this suite's own fixtures create, is also absent: those fixtures delete
their trees when they finish, so it is not a leak and there is nothing there to offer.

The age gate is judged on the newest write anywhere inside a folder, not on the folder's own stamp,
because a folder whose own stamp is old can still hold a file written a minute ago. On top of the age
gate, every file in a match is asked whether anything holds it open, and a match with an open file is
left alone whatever its age says.

## 9. The one substitution in this phase, and why it is there

`IInstallerRecordSource` exists so the installer rule can be tested. The records live in the
machine's registry and there is no way to build a fixture registry the way a fixture tree is built on
disk, so the one thing that cannot be faked is put behind an interface and everything else - the
package folder, the file sizes, the ages, the comparison itself - is tested for real against a tree
the tests build. It is the only substitution in this phase, and it is here because it is genuinely
needed rather than out of habit.

## 10. What this phase deliberately does not contain

No removal code of any kind: no delete, no move, no holding folder, not even unreachable, not even
behind a flag that is never passed. Nothing runs anybody else's cleanup command - the rules print
them. Nothing raises itself to administrator; the installer rule says an administrator is needed and
prints what the owner runs.

Nothing was removed from the owner's machine by this phase. The only thing run against it is a
read-only scan and a read-only recommendation, and the evidence for both is in `phase-2-proof.md`.

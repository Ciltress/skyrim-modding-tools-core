# Record files are moved and deleted by tools

A record file written where the build does not read it — outside every project, in a folder that is no group,
under a name that does not end in .yaml — has to be moved, or deleted once the right one exists. OpenClaw gives a
model no tool that moves or deletes a file. So the build named each such file and where it belonged, and e4b
wrote the record again there: the first copy stayed. Eval round 66 had it in 6 runs. One failed on an escape
copied into the copy left outside the project. Another ended on a warning about a copy in `Notes/`, which no call
it had could clear. Before that, the way out was to write the file empty, which the build leaves out (eval probe
16), and a refusal that only a delete could satisfy trapped a model (round 10).

`moveRecord` moves one record file into a project's plugin folder; `deleteRecord` deletes one record file. Both
take a file the way `editRecord` does — its path in the plugin folder, or the record's EditorID — or by its full
path for a file elsewhere in the projects folder, and touch nothing outside that folder, nor a project's
`RecordData.yaml`. `moveRecord` refuses a target that is there already and names the `deleteRecord` call
instead.

Each refusal or warning about a misplaced or left-over file writes out the call that fixes it, as the warning
for a renamed copy writes its `editRecord` call — made as written 3 times of 3 (rounds 65–66). A file outside the
plugin folder is named by its full path with forward slashes: a backslash reached the model escaped once per layer
of JSON, four times through OpenClaw (round 65).

## Consequences

- Two more tools in every session's prompt (ADR 0001), and in the `core` profile too: its build answers name
  them.
- OpenClaw's `apply_patch` can delete and move files too, but only in OpenClaw, by a patch text a small model
  writes badly, and only inside its workspace unless that boundary is lifted for every file on the disk. These
  tools serve every client and stop at the projects folder.
- A file written empty is still left out of the build, but no answer suggests it any more.
- A copy outside every project that has the name of one in the plugin folder is a warning now, with its
  `deleteRecord` call; it used to go unmentioned once the project had records.

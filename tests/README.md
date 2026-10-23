# Validation commands

```powershell
dotnet run --project tests/Stransfer.Core.Tests -c Release
dotnet run --project tests/Stransfer.App.SmokeTests -c Release
dotnet run --project tests/Stransfer.Benchmarks -c Release -- benchmark.json
```

The core console harness uses unique temporary directories and known test data. It covers content verification, copy/move, native same-volume file identity/streams, copy-delete safety, keep-both naming, overlapping selections, cancellation, source changes, missing partials, corrupted records, volume mismatch, preflight capacity and FAT32 limits, queued names/capacity, parallel copies and real child-process termination during both sequential and parallel transfers.

The FAT32 limit test creates a sparse file with a real logical length above the limit and injects only the filesystem/capacity response. It does not write 4 GiB of physical data. Capacity tests inject free-space responses without filling a real drive.

The WPF harness loads real resources and views in an isolated profile, uses real button automation peers/commands, resumes a seeded job, verifies files and history, visits Settings/Tools, checks binding errors and shuts down through the normal application lifecycle. `STRANSFER_PREVIEW_DIR` optionally writes render previews. It does not register Explorer commands, alter startup, install tools or touch user clipboard data.

`--test "name fragment"` filters core cases for diagnosis. Windows physical unplug/reconnect, power loss, UAC, installers and Explorer keyboard/tab integration require the manual matrix in `docs/VALIDATION.md`.

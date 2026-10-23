# Performance measurements

## Actual Camera folder

Tested the user's Desktop/Camera folder read-only: **313 files, 11,731,399,487 bytes (10.93 GiB)**, C: to D: on the same MSI M371 SSD. The baseline uses the actual Windows Shell IFileOperation copy engine. Each comparison ran twice, reversing engine order in round two. Times include Stransfer scanning, preflight, journaling and final durable publication.

| Selected implementation | Round 1 | Round 2 | Mean |
|---|---:|---:|---:|
| Windows Shell | 26.359 s | 26.319 s | 26.339 s |
| Stransfer Fast | 31.185 s | 29.611 s | 30.398 s |

Fast remained **15.4% slower than Windows Shell** in this comparison. Its elapsed time decreased from 37.713 s in the earlier buffered trial to 30.398 s, but these separate trials were not controlled for cache, temperature or SSD/background activity. The initial Windows samples varied from 12.810 s to 27.078 s, so the session does not establish a universal speedup.

The selected implementation batches photo-size files below 8 MiB on non-seeking local drives, avoids batch-timer waits in the sequential phase, and uses unbuffered native I/O for files at least 256 MiB. A two-worker large-file trial averaged 31.974 s and was rejected; large files remain sequential. Durable pause/checkpoint and publication rules are retained.

Every run checked output counts and lengths. SHA-256 of all 313 source files matched both engines' final outputs after each comparison, outside timing. Source sizes/modification times remained unchanged. Only newly created GUID-named benchmark outputs and journals were removed. No original Camera files or existing user destinations were modified.

Windows Shell can complete with cached writes; Stransfer flushes before durable publication. The OS cache was not cleared and background activity was uncontrolled. These are sequential Shell-engine measurements, not the concurrent Explorer dialog screenshot.

[Selected raw samples](benchmark-camera.json), [buffered trial](benchmark-camera-buffered.json), [unbuffered-only trial](benchmark-camera-unbuffered.json), [selected photo batching trial](benchmark-camera-parallel-photos.json), [rejected large-file parallel trial](benchmark-camera-parallel-large.json).

Reproduce with read-only source files and sufficient free space for four outputs (plus 1 GiB):

```powershell
dotnet run --project tests/Stransfer.Benchmarks -c Release -- benchmark-camera.json --source "$env:USERPROFILE\Desktop\Camera" --destination-root D:\
```

## Native checkpoint update

The focused 512 MiB C: → D: test after removing the per-file initial 32 MiB flush measured **0.524 s** for File.Copy + flush, **0.563 s** for Stransfer Fast, and **1.304 s** for Verified (three rotated rounds, medians, warm cache). Fast remained about 7% slower than this native baseline. [Raw focused samples](benchmark-native-checkpoints.json) retain all runs. This is not a timing of the user's concurrent Explorer copies. Fast/Verified is now visible on each compact transfer, since changing Settings does not change an existing job's saved mode.

Measured on 2026-10-23, Windows x64, MSI M371 NVMe SSD. Source C: and destination D: are partitions on the same physical disk. Three rotated rounds per mode, warm cache; medians below. No other Stransfer build/test ran during the final run. System background activity was not controlled.

| Workload | Windows File.Copy + flush | Stransfer Fast | Stransfer Verified |
|---|---:|---:|---:|
| 1,000 x 1 KiB | 2.753 s | 2.383 s | 2.526 s |
| 200 x 64 KiB | 3.111 s | 0.566 s | 0.570 s |
| 1 x 512 MiB | 0.602 s | 0.703 s | 1.284 s |

Fast improved both small-file workloads; the single large file remained about 17% slower than the native baseline. Verified large-file copying improved from 3.181 s before this change to 1.284 s, while still adding checksum work compared with ordinary Windows copying. These results do not establish that Stransfer always beats Explorer.

The baseline uses native File.Copy followed by Flush(true), with no checksum pass. Stransfer includes scanning, preflight, durable journals and final publish. Fast skips full checksum readback; Verified includes it. Actual Explorer completion can have different cache/flush behavior. Physical USB/HDD pairs, cold-cache performance, antivirus effects and other workloads can differ.

[Raw final samples](benchmark-crossdrive-final.json), [before optimization](benchmark-crossdrive-before.json), and [original local results](benchmark-local.json) are retained. Run on the intended drives:

```powershell
dotnet run --project tests/Stransfer.Benchmarks -c Release -- benchmark.json --destination-root D:\ --compare-verification
```

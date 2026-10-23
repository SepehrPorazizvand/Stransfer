# Local performance results

Final local run: 2026-10-22, Windows x64. Three rounds per case; median shown. Warm filesystem cache, one local temporary directory, no concurrently running Stransfer tests/builds. The host's background activity is not controlled.

**Copy readback verification was disabled for this run.** The app normally enables it. The baseline is `File.Copy` followed by a disk flush for each file; it does not have Stransfer's journal, recovery and preflight. This is not a direct measurement of the Explorer UI, USB drives or physical HDD/SSD pairs.

| Workload | File.Copy + flush | Stransfer, 1 worker | Stransfer, 4 workers |
|---|---:|---:|---:|
| 1,000 × 1 KiB | 2.996 s | 4.183 s | 3.543 s |
| 200 × 64 KiB | 3.597 s | 1.152 s | 0.759 s |
| 1 × 128 MiB | 0.140 s | 0.188 s | 0.193 s |

Four workers improved the 64 KiB workload substantially. The 1 KiB workload and single large file remained slower than the baseline. A higher worker setting is therefore not a universal speed guarantee. The app does not claim that the many-tiny-files problem has disappeared.

The raw samples and exact medians are in [benchmark-local.json](benchmark-local.json). Run the benchmark on the actual drives and with the intended verification setting before making hardware-specific performance claims.

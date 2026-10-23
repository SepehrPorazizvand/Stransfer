# Performance measurements

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

# Validation status

Validated on the development Windows x64 environment; update/release work on 2026-10-24.

| Check | Result |
|---|---|
| Core integration suite | 34/34 passed, including native accelerated copy pause/checkpoint/restart, large-file batch recovery and transient manifest/journal sharing locks |
| Native pause before the periodic checkpoint | Passed: flushes the native destination handle before stopping, reloads the exact saved offset and resumes to identical contents |
| Large unbuffered native copies | Passed: non-sector-aligned final block, read-only source attribute retention, early pause and restart; Fast large-file batches retain their sources and resume to identical SHA-256 contents |
| Actual Desktop/Camera C: → D: comparison | Passed: 313 files / 10.93 GiB, two reversed-order rounds per trial; final Windows and Stransfer outputs matched all source SHA-256 hashes, originals unchanged, isolated outputs cleaned. Selected Fast mean 30.398 s versus Shell 26.339 s; see PERFORMANCE.md |
| Sequential abrupt process termination and recovery | Passed with real child-process termination |
| Parallel abrupt process termination and recovery | Passed; also repeated eight times after fixing transient lock handling |
| Real WPF resources, layout and bindings | Passed |
| Permission-based update discovery and validation | Passed: checks request metadata only; stable version gates, manifest matching, SHA-256 failures, ZIP traversal rejection, file replacement and rollback preserve settings |
| Full EXE update from 1.1.3 to 1.2.0 | Passed in a disposable isolated profile: helper readiness, graceful parent exit, executable replacement, startup acknowledgement and staging/backup cleanup; unrelated files retained |
| Resume button, copied content and completed history | Passed |
| Settings/Tools/Shortcuts views, toggle persistence and registration status bindings | Passed |
| Ctrl+Shift+V registration and WM_HOTKEY dispatch | Passed with a real test-owned HWND; Ctrl+V messages rejected |
| Shortcut conflict/retry/disable, clipboard/focus changes, modal guard, duplicate suppression and composer fallback | Passed with injected platform inputs |
| Shell namespace selection without FileDrop | Passed through Stransfer IStream reads/writes: PC/Shell in both directions, Shell-to-Shell, content verification, empty directories and keep-both |
| Namespace restart/resume | Passed: paused durable byte checkpoint, reloaded records, corrupt-prefix rejection and verified completion |
| Physical Samsung S24 FE | Passed two 128 KiB PC → MTP → PC round trips with content verification and empty directories; successful test fixtures removed |
| Installed-tool actions | Passed: SDK version detection, Install/Remove labels, external refresh and exact-package command generation; no real tools removed in tests |
| Dashboard without the manual New Transfer form | Passed: queue/history remain visible; transfer scheduling still checks capacity; shortcut opens compact progress |
| Compact shortcut transfer window | Passed: 480 × 330 layout, hidden dashboard during preparation/transfer/errors, real Pause/Resume, completion and closing the window while transfer continues |
| Completed history removal with the original drive absent | Passed; retained both source and destination |
| Current-user named-pipe command delivery and shutdown | Passed |
| Full published EXE: background start and second-instance `--exit` | Passed in an isolated profile |
| Slim published EXE: background start and second-instance `--exit` | Passed using the local Desktop Runtime |
| Optional package installations | Not run; installation remains a user action |
| Live Explorer Ctrl+Shift+V | Passed: installed full app accepted one job into the focused disposable folder; SHA-256 contents matched and source remained |
| Live Explorer Ctrl+V | Passed: native Windows Replace or Skip Files dialog appeared; Stransfer job count did not increase; dialog cancelled |
| Multiple Windows 11 tabs, elevated windows and text-field matrix | Not tested in the live check |
| Actual per-user Explorer registry commands | Not enabled on the development profile; command construction compiled |
| Physical USB detach, HDD/SSD performance, hardware power loss | Not performed; volume identity and forced process loss are covered by automated tests |
| ARM64 | Publish target available; not hardware-tested |

Published-process checks used `STRANSFER_STATE_DIR` and changed no normal startup/menu registration. The slim process used `DOTNET_ROOT_X64` pointing at the local SDK's runtime. Neither build installed tools or transferred user data. The builds are unsigned.

Automated namespace tests use real Shell filesystem providers with FileDrop hidden. A separate opt-in hardware test also passed against the connected Samsung phone. Physical USB unplug/replug and power-loss tests remain unverified; the phone became unavailable before the larger physical pause/resume test could run. Some uniquely named Stransfer-test-* fixtures from failed development checks may remain on the disconnected phone; the opt-in --device-test-cleanup test command removes only matching local test records and verified fixture contents. Installed SDK and 7-Zip were retained.

## Manual Explorer matrix before treating shortcut integration as stable

Use disposable files and empty destination folders. Enable only the integration option under test.

1. Copy and cut to a normal Explorer folder with Ctrl+Shift+V. Confirm exactly one Stransfer job, correct destination and source retention/removal. Confirm Ctrl+V and Shift+Insert use Windows without creating Stransfer jobs.
2. Repeat in each of two Windows 11 tabs and two Explorer windows. Navigate immediately before/after Paste; a changed target must stop acceptance, never send files to a different folder.
3. Paste text with Ctrl+V into the address bar, search field and rename editor. Normal text behavior must remain. Ctrl+Shift+V outside a confidently resolved Explorer folder should open destination selection.
4. Test folder-background and selected-folder “Paste with Stransfer,” including drive roots, UNC paths, spaces and long names. With Stransfer closed, the command must launch it; with it open, it must reuse the process.
5. Disable shortcut/menu settings and exit the tray app. Native paste behavior must remain available. Explorer native Paste menus and drag/drop remain native even when shortcuts are enabled.
6. Pause, close, reopen and resume disposable transfers. Reconnect the same test drive under a different letter. A different drive at the old letter must not be accepted.

Use a disposable VM for forced operating-system loss tests. Do not infer physical-drive durability from a process-kill test alone.

## Performance report

`benchmark-local.json` is generated by `tests/Stransfer.Benchmarks`. It retains each sample, verification mode and methodology. The final run follows the build/test work; no other Stransfer test or build runs concurrently. The host itself is not isolated from all background activity. Results are local warm-cache measurements and cannot establish universal Explorer/USB/HDD speed claims.

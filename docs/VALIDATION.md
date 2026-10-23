# Validation status

Validated on the development Windows x64 environment; shortcut update on 2026-10-23.

| Check | Result |
|---|---|
| Core integration suite | 33/33 passed, including native accelerated copy pause/checkpoint/restart and transient manifest/journal sharing locks |
| Sequential abrupt process termination and recovery | Passed with real child-process termination |
| Parallel abrupt process termination and recovery | Passed; also repeated eight times after fixing transient lock handling |
| Real WPF resources, layout and bindings | Passed |
| Resume button, copied content and completed history | Passed |
| Settings/Tools/Shortcuts views, toggle persistence and registration status bindings | Passed |
| Ctrl+Shift+V registration and WM_HOTKEY dispatch | Passed with a real test-owned HWND; Ctrl+V messages rejected |
| Shortcut conflict/retry/disable, clipboard/focus changes, modal guard, duplicate suppression and composer fallback | Passed with injected platform inputs |
| Shell namespace selection without FileDrop | Passed through Stransfer IStream reads/writes: PC/Shell in both directions, Shell-to-Shell, content verification, empty directories and keep-both |
| Namespace restart/resume | Passed: paused durable byte checkpoint, reloaded records, corrupt-prefix rejection and verified completion |
| Physical Samsung S24 FE | Passed two 128 KiB PC → MTP → PC round trips with content verification and empty directories; successful test fixtures removed |
| Installed-tool actions | Passed: SDK version detection, Install/Remove labels, external refresh and exact-package command generation; no real tools removed in tests |
| New transfer through the composer with capacity display | Passed |
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

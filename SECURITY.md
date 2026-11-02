# Security

Stransfer does not require API keys or a GitHub token in the app. Public release checks and downloads are anonymous. Never add keys, tokens, passwords, private signing keys or local credential files to source, release assets or examples. Ignored credential files are also excluded from source ZIPs. If a real credential is exposed, revoke/rotate it immediately; deleting the latest file does not remove it from Git history.

CI runs `scripts/check-secrets.ps1 -History -SelfTest`. It checks common credential formats and suspicious literal assignments without printing secret values. This is a guard against common mistakes, not a guarantee that every kind of secret will be detected.

## GitHub permissions

- CI and release builds have only `contents: read`; checkout does not persist credentials.
- Build/test steps receive no token environment variable.
- Only the isolated publish job has `contents: write`. It downloads build artifacts and runs fixed release commands; it does not check out or execute project code or binaries.
- `GH_TOKEN` is the temporary, repository-scoped GitHub Actions token and is passed only to the individual steps that need it. There is no personal access token in source or application binaries.
- Tokens configured externally are outside this repository's control. If you use a PAT manually, restrict it to this repository, use only Contents read/write as needed, set a short expiration and revoke it when finished. No PAT is needed for normal CI releases.

## Dependency versions

`global.json` pins the stable .NET SDK to 10.0.402 with automatic roll-forward and previews disabled. GitHub Actions are pinned to verified full commit hashes. NuGet sources are restricted to nuget.org; package lock files are committed and CI uses locked restore. High/critical NuGet vulnerability findings fail the build. There are no third-party application packages; Microsoft's implicit single-file SDK tooling is locked separately for each publish architecture.

Dependabot opens weekly reviewable updates for Actions and NuGet dependencies; updates are not merged automatically. Review the SDK version and its supported runtime/security patches regularly and update `global.json` deliberately. Regenerate normal locks with `dotnet restore Stransfer.slnx --force-evaluate`. Regenerate publish locks for both App and Installer with `dotnet restore <project> -p:RuntimeIdentifier=win-x64 -p:PublishSingleFile=true --force-evaluate` (also repeat for win-arm64). Review changes and run CI. Keep `NuGetAudit` enabled.

Do not publish credentials in a public issue. Report a security issue privately through GitHub's private vulnerability reporting if it is available on the repository.

# Security model and update boundary

## Local data

- User data resides under `%LOCALAPPDATA%\ClipboardManager`: SQLite history (`history.db`), images, logs, migration inputs and rollback backups.
- The update manifest rejects user-data paths. The worker receives an explicit user-data root only to reject overlap; it does not enumerate, read, or write database, image, setting, JSON migration, or log contents.
- Clipboard payloads are not sent in update requests and are not serialized in app/worker diagnostics.

## Update trust and integrity

- The release client accepts only the configured public GitHub repository, HTTPS release metadata and assets, and the supported `winui3` package identity.
- Each compressed attachment and expanded program file is checked against declared size and SHA-256. The installer ZIP has a separate SHA-256 sidecar.
- These checks detect truncation, transfer corruption and mismatch between an asset and its manifest. They do **not** protect against an attacker who can replace both the GitHub release manifest and its files, or alter the updater/application source.
- Application and worker packages are not code-signed. Windows may show an unknown-publisher warning. Users should verify the repository and published SHA-256 and must not disable Windows security protections to run the app.

## Worker and rollback

- The worker is self-contained, x64, and independent of WinUI, WPF and WinForms.
- Its allowed transaction root is a GUID workspace under the installation directory on the same volume. It validates path containment, rejects reparse points, rechecks staged hashes, and only modifies paths present in the validated manifests.
- A journal is written before replacement. The previous manifest is committed last; file backups remain until the new application reports its transaction-bound startup health marker. A failed apply or health timeout attempts rollback and retains recovery state when rollback cannot finish.
- The worker waits for the exact old process identity and the application single-instance mutex. It does not accept installation/workspace parameters derived from unchecked path data for diagnostic writes.

## First WPF-to-WinUI migration

The old WPF updater manifest is incompatible with the WinUI manifest. Until a complete synthetic-data migration passes on a disposable Windows account, the supported first upgrade is: exit the old app and run the full per-user installer over the existing install directory. Do not advertise legacy in-app incremental upgrade. The installer preserves the separate LocalAppData data directory; runtime behavior of legacy JSON/theme/image migration remains pending end-to-end installation verification.

## Credentials and release workflow

- Local restore, build, tests and packaging require no PAT, personal GitHub token, signing certificate, private key or credential-store entry.
- The release workflow gives read-only `contents` access to validation jobs. Only the final publish job receives the repository-provided short-lived `GITHUB_TOKEN` with `contents: write`; no user-owned token is required.
- Secret scanning runs as a CI gate. Any genuine credential discovered in history must be revoked/rotated; deleting it from the working tree alone is not sufficient.

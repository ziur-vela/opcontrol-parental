# Contributing to Parental OpControl

Thanks for your interest. You can help by reporting issues, proposing changes
and sending pull requests.

## Reporting issues

Open an issue at <https://github.com/ziur-vela/parental-opcontrol/issues> and
include:

- What you expected to happen and what happened instead.
- Steps to reproduce.
- Product version (visible in the dashboard sidebar or `/version`), operating
  system and, for client issues, the client and helper versions.
- Logs with secrets and personal data removed.

Please do not publish security vulnerabilities in a public issue. Report them
privately to `hendry.ruiz@opcontrol.net`.

## Pull requests

1. Fork the repository and create a branch from `main`.
2. Keep changes focused; describe what changed and why.
3. Build and run the tests before opening the pull request:
   `dotnet test OpControl.Parental.slnx -c Release`.
4. Add or update tests for behaviour changes.
5. New user-facing strings must be added in English and es-MX.
6. Follow the existing code style; do not commit generated files, build output
   or secrets.

## Contribution license

Parental OpControl is licensed under the PolyForm Noncommercial License 1.0.0.

By sending a contribution (a pull request, patch, or any other change) you
agree that:

- your contribution may be distributed under the PolyForm Noncommercial
  License 1.0.0, and
- your contribution may also be distributed under any future license chosen by
  the copyright holder, including AGPL-3.0.

You confirm that you have the right to grant these permissions for the work you
submit.

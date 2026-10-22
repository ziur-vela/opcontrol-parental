# Third-party notices

Parental OpControl is distributed by OpControl IT Solutions. The following
third-party components are used by or bundled with the product. Each component
remains under its own license; nothing in this file changes those licenses.

## NuGet packages

| Package | Used by | License |
|---|---|---|
| Microsoft.AspNetCore.Identity.EntityFrameworkCore | Server | MIT |
| Microsoft.EntityFrameworkCore.Design | Server (build) | MIT |
| Npgsql.EntityFrameworkCore.PostgreSQL | Server | PostgreSQL License |
| QRCoder | Server | MIT |
| Microsoft.Extensions.Hosting | Client | MIT |
| Microsoft.Extensions.Hosting.WindowsServices | Client | MIT |
| Microsoft.AspNetCore.SignalR.Client | Client | MIT |
| Microsoft.Extensions.Http | Client | MIT |
| System.Management | Client, Setup | MIT |
| OpenCvSharp4 | Helper | Apache-2.0 |
| OpenCvSharp4.runtime.win | Helper | Apache-2.0 |
| Microsoft.NET.Test.Sdk | Tests (build) | MIT |
| xunit | Tests (build) | Apache-2.0 |
| xunit.runner.visualstudio | Tests (build) | Apache-2.0 |
| Microsoft.EntityFrameworkCore.InMemory | Tests (build) | MIT |

Transitive NuGet dependencies are covered by the licenses of their respective
packages. Copyright notices for those packages are available in the packages
themselves and in the .NET runtime distribution.

## Fonts

| Font | Files | License |
|---|---|---|
| IBM Plex Mono | `IBMPlexMono-*.ttf`, `ibm-plex-mono-*-normal.woff2` | SIL Open Font License 1.1 |
| Manrope | `Manrope-*.ttf`, `manrope-*-normal.woff2` | SIL Open Font License 1.1 |

The full OFL texts are included at `src/OpControl.Parental.Server/wwwroot/fonts/`,
`src/OpControl.Parental.Client.Windows.Helper/fonts/` and
`src/OpControl.Parental.Setup/fonts/`.

## Data sources

| Data | Source | License |
|---|---|---|
| Adult, gambling, social and fake-news host lists | [StevenBlack/hosts](https://github.com/StevenBlack/hosts) | MIT |
| Public Suffix List | [publicsuffix.org](https://publicsuffix.org/) | Mozilla Public License 2.0 |

## Runtime images and platforms

| Component | License |
|---|---|
| .NET 10 runtime and ASP.NET Core | MIT |
| PostgreSQL 17 (Docker image `postgres:17.6-bookworm`) | PostgreSQL License |

## Brand assets

The OpControl and Parental OpControl names, logos and other brand assets in
`src/**/brand`, `src/OpControl.Parental.Server/wwwroot/brand` and
`assets/screenshots` are copyright OpControl IT Solutions and are not covered
by the PolyForm Noncommercial license for trademark use.

# Macys Product Feed CLI

A .NET CLI tool that scrapes macys.com product listings and generates a CSV product feed (issue #127).

## Prerequisites

This tool uses [Playwright](https://playwright.dev/dotnet/) to render JavaScript-hydrated listing pages. Before first use, install its headless Chromium browser:

```
dotnet build
pwsh bin/Debug/net8.0/playwright.ps1 install chromium
```

(If `pwsh` isn't available, install PowerShell first, or run the equivalent `playwright install chromium` via the generated `playwright.ps1`/`playwright.sh` script in the build output.)

## Usage

```
dotnet run -- --url https://www.macys.com/shop/some-category --output feed.csv
```

### Options

| Option | Description | Default |
|---|---|---|
| `--url` | A macys.com category/listing URL. Repeatable. | — |
| `--url-file` | Path to a newline-delimited file of URLs. Combines with `--url` (union of both). | — |
| `--output` | Output CSV path. Parent directory is created if missing. | `./macys-product-feed.csv` |
| `--max-pages` | Maximum pages to follow per listing URL. | unlimited |
| `--delay-ms` | Delay between page requests, to avoid hammering macys.com. | `1000` |

At least one of `--url` or `--url-file` is required.

## Exit codes

- `0` — at least one product was written (even if some pages/tiles failed along the way).
- Non-zero — invalid CLI input, or zero products were produced across all input URLs.

## Notes

- Processing is strictly sequential; `--delay-ms` is the sole request-pacing mechanism.
- macys.com's page structure can change without notice — see the architectural plan on issue #127 for the parsing strategy (JSON network-capture preferred, DOM fallback) and known risks (anti-bot defenses, pagination-mechanism assumptions).
- Compliance with macys.com's Terms of Use for any production/commercial use is outside this tool's scope — use conservatively.

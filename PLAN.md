# POSscan build plan

The design is in `spec.md`. This file tracks what's built and what's next.

## Status

| Part | State | Evidence |
|---|---|---|
| Windows app: home, review, finished, and settings screens | Built | Driven end to end on the real receipt in automated tests, with screenshots checked by eye |
| Reading PDFs, OCR, and barcodes | Built | Real receipt, on Linux and on the Windows build under Wine |
| Superstore receipt parser | Built for four receipts | Three of the four match their printed subtotals once reviewed. The fourth is cut off and stays blocked. |
| PDFs holding several receipts | Built | Splits, turns upside-down pages, and levels slanted scans, on the real three-receipt batch |
| Totals checks and suggested fixes | Built | Example matches exactly once the one flagged total is confirmed |
| Saved items, price history, and audit log | Built | 22 workflow tests |
| Crash recovery, duplicate detection, and daily backups | Built | Automated tests |
| Scanner folder | Built | Not covered by tests yet |
| RAS CSV reader and writer | Built | Reads all 203 real rows. Writing is atomic with a read-back check. |
| Manual Confirmation popup | Built | Driven with real keypresses on the real batch, with screens checked by eye |
| Estream test files | Ready | `milestone0/`, awaiting a test on a real System 5 |
| One-click installer (unsigned) | Built | Install, update, update refused while open, and uninstall with data kept, all under Wine. OCR on the real receipt using the bundled Microsoft runtime. |

## Not proven yet

- **Estream import:** no POSscan file has been imported into System 5. See `milestone0/README.md`.
- **Real Windows 11:** tested under Wine only. The installer, drag and drop, the file pickers, and Open folder haven't been exercised on Windows.
- **Other receipts:** the parser has only seen four. Expect tuning when more are available.
- **Misread digits in item codes:** a misread digit can still look like a valid code, and totals can't catch it. Two of 71 codes in the batch did this, so checking codes in review matters until items are set up.
- **Receipts with no printed date:** the date isn't read from handwriting, so these use the import date in price history and the file name.
- **Receipt-to-UPC rule:** the check digit is calculated, and hasn't been compared with barcodes on real products.

## Not built yet

- Importing an item list from Estream, which would make first-time setup quicker (needs an Estream export to work from)
- Restoring a backup from inside the app (backups exist, but restoring is manual)
- A support bundle button, and a Test CSV tool in the app (the command-line tool has `check-csv`)
- Templates for other suppliers
- Signing the installer (needs a code-signing certificate). Until then, Windows may show a SmartScreen warning, and Smart App Control would block the app if it's turned on.

## Next steps

1. **Store:** run `dist/POSscan-Setup-1.1.0.exe` on the store PC.
2. **Store:** run `milestone0/README.md` on a backed-up System 5.
3. **Store:** save Superstore receipts as they come in. Once there are about 30, run the accuracy test in spec section 12.
4. Confirm the receipt-to-UPC rule against about 10 real products.
5. Adjust the app using the results, then run the store pilot.

## Building

Requires the .NET 10 SDK.

```
dotnet test tests/POSscan.Tests
dotnet test tests/POSscan.UiTests
dotnet run --project src/POSscan.App
packaging/build-installer.sh
```

The script writes `dist/POSscan-Setup-<version>.exe`, taking the version from `POSscan.App.csproj`. It also needs Wine and cabextract, and downloads NSIS and Microsoft's Visual C++ runtime on first use. `onnxruntime.dll` needs that runtime, and the .NET publish doesn't include it. Wine supplies its own copy, so testing a missing runtime requires `WINEDLLOVERRIDES` set to force native DLLs.

When testing the installer under Wine, replace the prefix's `Desktop` and `Documents` links with real folders and set `winemenubuilder.exe=d`, or it writes to your real home folder. Don't disable `mscoree`, because the app won't start without it.

To try a whole PDF without the app, run `dotnet run --project src/POSscan.Cli -- batch <file.pdf> <folder> [wrong=right ...]`. It uses a throwaway database, accepts new items as read, and writes a CSV for each receipt that passes. Its CSVs have had no human review, so don't import them into a live Estream.

Set `POSSCAN_SHOTS` to a folder before running the UI tests to save a screenshot of each screen. Set `POSSCAN_DATA` to run the app against a separate data folder.

## Layout

| Path | Contents |
|---|---|
| `src/POSscan.App` | The Windows app |
| `src/POSscan.Core` | Parser, totals checks, review rules, UPC rules, and CSV, with no third-party dependencies |
| `src/POSscan.Data` | SQLite storage and the import, review, and export workflow |
| `src/POSscan.Ocr` | PDF rendering, OCR, and barcode reading |
| `src/POSscan.Cli` | Developer command-line tool |
| `tests/POSscan.Tests` | Logic and workflow tests. Fixtures come from the real files, with payment and staff lines removed. |
| `tests/POSscan.UiTests` | Runs the real screens on the real receipt |
| `milestone0` | Estream test files and checklist |
| `packaging` | Installer script and build |

## How cost is decided

Set by the store on 2026-11-02:

- A sale price is the cost, even a clearance price.
- Coupons and printed discounts are ignored, so the cost is the price before them.
- If units of an item were bought at more than one price, the highest applies to all of them. When every unit was at a multi-buy deal, the cost is the deal's price per unit, since that's the only amount paid.
- Each receipt gets its own CSV.

## Manual Confirmation

A toggle in the review screen's header. Turning it on opens a popup that goes down the receipt in order: each item, its fees and discounts, multi-buy totals, then the subtotal, tax, total, and item count. Each line shows its part of the scan and one text box, such as `06870011200  4 @ 5.05`.

- Enter saves the line and moves on. Esc closes the popup. Lines already confirmed stay saved.
- Confirming a line counts as checking it, and sets up a new item if its code gives a UPC. In-store and produce codes still need a SKU on the main screen.
- The line total is worked out from the quantity and price, and a warning shows first if the receipt printed something else.
- The toggle is remembered, so each receipt opens the popup until it's been finished once. Turn the toggle off and on to go through a receipt again.
- Lines that couldn't be read aren't in the popup. They stay on the main screen.

## Where the app keeps data

`%LOCALAPPDATA%\POSscan` holds the database, archived receipts, daily backups, and logs. Price files go to `Documents\POSscan` unless changed in Settings. The program itself installs to `%LOCALAPPDATA%\Programs\POSscan`, which is replaced on every update. Updating and uninstalling leave the data folders alone.

## Dependency note

SkiaSharp is pinned to 3.119.4 in `POSscan.Ocr.csproj`, and PDFtoImage (5.2.1) and the ZXing SkiaSharp binding (0.16.22) are held at versions that work with it. Avalonia and RapidOcrNet both use SkiaSharp 3.x, and newer versions of those two packages move to 4.x, which crashes at startup. Upgrade them together.

# POSscan

Turns scanned Real Canadian Superstore receipts into `.csv` price files that Estream System 5 can import. It runs on Windows 11, works offline, and nothing leaves the PC.

## Install

1. Download `POSscan-Setup-1.3.0.exe` from the [latest release](https://github.com/Malverma/Receipt2CSV/releases/latest).
2. Double-click the file. POSscan installs and opens by itself, with no admin password needed.

The installer isn't code-signed yet, so Windows may show "Windows protected your PC". Click **More info**, then **Run anyway**.

To update, run the newer installer. Your receipts and settings are kept.

## Use

1. Drop a scanned receipt PDF onto POSscan. One PDF can hold several receipts.
2. Check anything POSscan flags, and check new item codes against the receipt. To go down the receipt one line at a time, turn on **Manual Confirmation**, correct anything in the box, and press Enter for the next line. You can create the file at any point, and POSscan tells you what went in unchecked.
3. Click **Create price file**. Each receipt gets its own CSV in `Documents\POSscan`.
4. Import that file into Estream.

The Estream import hasn't been proven on a real System 5 yet. Try it on a backed-up system first, using `milestone0/README.md`.

## Development

See `PLAN.md` for status and build commands, and `spec.md` for the design.

Real receipts and price lists aren't in this repository, so `tests/POSscan.Tests` and `tests/POSscan.UiTests` need local copies of those files to run.

# POSscan: Product and Technical Specification

| | |
|---|---|
| **Status** | Draft 3, updated with the working app, 2026-11-01 |
| **Platform** | Windows 11 desktop (x64) |
| **Target system** | Estream System 5 point of sale |
| **Reference files** | `superstore reciept.pdf`, `RAS Price Change List - Wholesale-en-us-815 - THE SQUIRREL COVE GENERAL STORE` (`.pdf` and `.csv`) |

## 1. Summary

POSscan turns a scanned PDF of a supplier receipt into a `.csv` price batch file that Estream System 5 can import. This is the same route the store already uses for RAS price change lists.

Receipt scans are hard to read, and a wrong price or barcode can reach the live POS. POSscan is therefore built around one rule:

> **POSscan never exports a value it cannot justify.** Every price must either reconcile against the receipt's printed totals or be confirmed by a person.

### 1.1 Design decisions

| Decision | Choice | Reason |
|---|---|---|
| Link to Estream | Exchange files only: POSscan writes a CSV and staff import it | Uses a route the store already relies on. POSscan never touches the Estream database, so it can't corrupt it and Estream updates can't break it through a schema change. |
| Output format | Configurable CSV profile that mirrors the real RAS CSV | How Estream reads that file isn't confirmed yet (section 2). The layout can be corrected without a code change. |
| Reading receipts | Local OCR, fully offline in v1 | Works during internet outages, has no running cost, and keeps receipts on the store's PC. Must pass the accuracy gate in section 12. |
| Meaning of a receipt price | The store's wholesale cost, written to **New Price** | The RAS list's prices are wholesale. POSscan doesn't set shelf prices. |
| Human review | Required for every batch | Scan quality makes unattended export unsafe. Reconciliation keeps the review short. |
| Supplier support | Superstore in v1, with a template for each supplier | Each chain prints differently. Templates allow new suppliers without changing the core. |

## 2. What is known about the Estream format

**Known**
- The store reports that the RAS `.csv` uploads to Estream as is and communicates batch price information.
- The sample file is ASCII with CRLF line endings, no BOM, and quotes only where a value contains a comma.
- Its 203 item rows match the RAS PDF on every code, name, and price. Its section totals (114, 75, and 14) match too.

**How the real file is laid out**

The file looks like a table conversion of the PDF rather than a clean data export. Each page repeats a report header, and these details matter to POSscan:

| Feature | Example from the file |
|---|---|
| 21 lines of report text before the first table | `Next RAS: 815 THE SQUIRREL COVE GENERAL STORE` |
| Section title and a two-line column header on each page | `Listing,SKU,UPC,Brand Name,Container,New,Old,Diff`, then `Code,,,,Size,Price,Price,` |
| Category rows | `Spirits - Whisky,,,,,,,,` |
| 139 clean item rows with 8 columns | `G,15628,80915134694,APRICOT BRANDY BOLS,0.75,13.34,12.38,0.96` |
| 64 rows with 9 columns, where **prices shift position** | `G,125,80686821311,CANADIAN CLUB - PREMIUM,1.14,,27.4,26.64,0.76` and `G,893,48415000307,J. P. WISERS DELUXE,,0.75,19.38,17.88,1.5` |
| Names split across two cells | `G,7617,80432400395,CHIVAS,REGAL 12 YR. OLD,0.75,41.54,38.54,3` |
| A name's overflow on its own row | `,,,SAUVIGNON,,,,` |
| Numbers with trailing zeros removed, except negatives | `27.4`, `2`, `-1.00` |
| Blank UPC on 2 rows | `G,579037,,BODACIOUS - SMOOTH WHITE,3,25.05,23.05,2` |

**Not yet confirmed**

Prices don't sit in a fixed column, so a simple importer would misread 64 of the 203 rows. Estream may read the file by pattern, or it may read some rows wrongly without saying so. The file alone can't show which. These remain unknown:

- Whether Estream imports the shifted rows correctly
- Whether it needs the report text and page headers, or only the item rows
- Which field it uses to match an item: SKU, UPC, or either
- Whether it accepts items with no BC Liquor Distribution Branch (LDB) SKU, Listing Code, or Container Size
- Which Estream price the import changes: cost, retail, or both
- Whether unknown items are rejected, skipped, or created
- Whether Old Price and Diff are read or ignored

**The largest project risk:** the RAS import may be specific to liquor products and reject grocery items such as those on a Superstore receipt. Milestone 0 (section 13) tests this using the files and checklist in `milestone0/`.

## 3. Goals and scope

### 3.1 Goals
1. Produce an importable batch from a Superstore receipt scan in under 5 minutes, including review.
2. Let no unverified price or item code reach an exported file.
3. Reduce work over time by remembering how receipt items map to Estream items.
4. Run reliably on one Windows 11 PC with no internet connection.

### 3.2 In scope for v1
- Importing PDF receipts by file picker, drag and drop, or a watched scanner folder
- Superstore receipts in the layout of the example
- Multi-page PDFs, single long-page scans, and PDFs holding several receipts, each reviewed and exported on its own
- Review and correction, item mapping, price history, batch export, and an audit trail

### 3.3 Out of scope for v1
- Writing directly to the Estream database or automating its screens
- Calculating or setting retail prices
- Inventory or receiving quantities
- Other suppliers' templates (the template system is in v1, the additional templates are not)
- Photos from phones, and reading handwriting
- Converting RAS PDFs, since RAS files already import directly
- Multi-user or multi-store use

## 4. Users and workflow

The user is a store owner or staff member who is comfortable with the POS but isn't technical.

1. Scan the receipt to PDF.
2. Open POSscan and drop in the PDF, or let the watched folder pick it up.
3. POSscan reads the receipt and checks it against its printed totals.
4. Review the screen, which shows only what needs attention, and correct or confirm those lines.
5. Click **Export**. POSscan writes the CSV.
6. Import the CSV in Estream System 5.
7. Mark the batch as imported in POSscan.

## 5. Findings from the example receipt

The example is a single 1275 × 5922 pixel, 300 DPI, 1-bit CCITT image with no text layer. The items below were read by eye from the scan, so some characters are uncertain.

| Finding | Evidence | Requirement |
|---|---|---|
| Scan quality is poor | Black-and-white only, with broken characters. `PCO MUSH SLI 227` and `HMG2D MILK` are hard to read even for a person. | Image cleanup, field-level confidence, and the source image beside each line in review |
| Printed totals catch misreads | Lines read by eye add up to **$179.77**, against a printed subtotal of **$179.79**. The 2¢ gap means at least one line was misread. The mushroom item looks like `3.96` to the eye, while the POSscan OCR run read `3.98`, which closes the gap exactly. | Reconciliation is mandatory, and a gap blocks export until it is resolved |
| Other printed checks work | Item count of **45** matches when the $0 `PHY STAMP EARN` line is excluded. Taxable lines total **$53.00**, and 5% of that is the printed GST of **$2.65**. | Check item count and tax as well as the subtotal |
| Receipt codes have no check digit | Codes such as `06870011200` have 11 digits and no check digit, so a misread digit still looks valid. | A code is trusted only after matching a known item or being confirmed by a person |
| Four kinds of item code | 11-digit codes, 7-digit in-store codes (`2439450`), produce codes (`4556`, `740`), and loyalty lines (`24632`) | Classify each code. Only a confirmed mapping can link in-store and produce codes to Estream items. |
| Several price formats | Single line, `3 @ $5.05`, weighed `2.540 kg @ $2.16/kg`, and grouped `11 @ 2/$6.00` | Parse all of these (section 7.3) |
| Discounts don't change the cost | `PM BOGO Set Price -0.50` lines leave the cost at $3.50. The 2/$6.00 group's 11 items total $33.00, which is $3.00 each. | Sale prices are the cost. Coupons and printed discounts are ignored. When units were bought at more than one price, the highest applies to all of them, with the regular price kept as a note |
| Fees are separate lines | `RECYCLING FEE` and `DEPOSIT 1` follow the milk lines | Fees reconcile but never become part of the item's cost or a separate item |
| Non-item content | Declined card section, payment lines, loyalty text, a survey, and a coupon. The declined card block also prints $182.44. | Only the item section is parsed. Other amounts can't be mistaken for items. |
| Names are cut off | `LR TRUFFLE ROYAL`, `STRAW CHEESECAKE` | Receipt names are never used to match automatically. Estream's item name is shown once mapped. |
| Transaction ID available | Barcode digits `99152802690720261027134915`, which appear to contain the store (1528), lane, transaction (6907), date, and time | Use for duplicate detection, after confirming the layout on more receipts |

### Findings from the RAS report

- 203 item rows in three sections: 114 increases, 75 decreases, and 14 pending de-list. All 203 rows were accounted for.
- Prices have two decimals, and Diff equals New minus Old on all rows that were arithmetic-checked (201).
- All 201 populated UPCs have a valid check digit once left-padded, so RAS UPCs include the check digit and drop leading zeros. Their lengths run from 9 to 13 digits.
- **UPC can be blank:** 2 rows have none (SKUs 579037 and 606285), so Estream may match on SKU.
- **SKU can repeat:** the same SKU appears on more than one row, for example for different container sizes or in two sections.
- **Diff can be zero:** one row is `7.10 / 7.10 / 0.00`.

## 6. Architecture

```
 Scanned PDF
      |
      v
 1 Intake --> 2 Image cleanup --> 3 OCR --> 4 Receipt parser --> 5 Reconciliation
                                               (template)              |
                                                                       v
 Estream System 5 <-- 8 CSV export <-- 7 Review screen <-- 6 Item matching
   (staff import)        |                   |                    |
                         v                   v                    v
                 Batch archive        SQLite: receipts, item map, price history, audit log
```

### 6.1 Technology

| Layer | Choice | Reason |
|---|---|---|
| Runtime | C#, .NET 10 LTS, self-contained | Long-term support, and no separate runtime to install |
| UI | Avalonia 12 | Builds a normal Windows 11 program, needs no browser engine, and every screen can be run and tested automatically during development. It replaces WPF, which can't run on the Linux development machine. |
| PDF rendering | PDFium, through PDFtoImage | Reliable, and handles CCITT scans |
| OCR | RapidOcrNet with PP-OCRv5 models, running locally through ONNX Runtime | Works on the example scan and has no native install step. Tesseract hasn't been compared. The final choice still depends on the wider test in section 12. |
| Barcode reading | ZXing.Net | Deterministic transaction ID |
| Database | SQLite in WAL mode | Single file, needs no server, and recovers from crashes |
| Logs | Plain daily text files | Support diagnostics, with no extra dependency |
| Delivery | One-click per-user installer, unsigned for now | Runs with no prerequisites or administrator rights. Signing it needs a code-signing certificate. |

### 6.2 Stability principles

- **No network dependency.** All features work offline, and POSscan makes no network calls in v1.
- **Each stage saves its results.** A crash loses at most the correction being typed, and the receipt reopens where it stopped.
- **Atomic output.** The CSV is written to a temporary file, read back and validated, then renamed. Estream can never see a partial file.
- **Money is `decimal`.** Floating point is never used for money, and rounding is explicit.
- **Originals are read-only.** The source PDF is copied to the archive with its SHA-256 hash and never changed.
- **Failures are contained.** A bad PDF fails that one receipt with a clear message and doesn't stop the app or the folder watcher.
- **Versioned behaviour.** Templates and CSV profiles carry version numbers, and each batch records the versions it used.
- **Safe upgrades.** The database is backed up automatically before any schema migration.

## 7. Functional requirements

### 7.1 Intake
- **FR-1** Accept PDFs by file picker, drag and drop, and a configurable watched folder.
- **FR-2** Wait for a watched file to finish writing before opening it, since scanners write slowly.
- **FR-3** Reject encrypted, corrupt, or empty PDFs with a plain-language message.
- **FR-4** Warn on a duplicate receipt, identified by matching file hash or transaction ID. Staff may override this with a recorded reason.

### 7.2 Image cleanup and OCR
- **FR-5** Render each page at a minimum of 300 DPI, using embedded image resolution where possible.
- **FR-6** Correct skew, remove scanner edge lines, and split very long receipts into overlapping strips.
- **FR-7** Use the embedded text layer if a PDF has one, but still apply all checks.
- **FR-8** Save the text, position, and confidence of each OCR result so that review can show the matching image region.

### 7.3 Parsing
- **FR-9** Select a template by matching the supplier's header. If none matches, stop and ask rather than guess.
- **FR-10** Parse only between the template's item start and `SUBTOTAL`.
- **FR-11** Extract date, store, transaction ID, subtotal, tax, total, and item count.
- **FR-12** Parse these line types:

| Type | Example | Result |
|---|---|---|
| Single | `06038314170 PCO MUSH SLI 227 MRJ 3.50` | Qty 1, unit price 3.50 |
| Quantity | `(3)06870011200 HOMO MILK` + `3 @ $5.05 15.15` | Qty 3, unit price 5.05 |
| Weighed | `4556 CABBAGE GREEN` + `2.540 kg @ $2.16/kg 5.49` | 2.540 kg, price 2.16 per kg |
| Multi-buy group | Four coded lines + `$3.50 ea or 2/$6.00` + `11 @ 2/$6.00 33.00` | 11 units across lines, 33.00 ÷ 11 = 3.00 each, regular price 3.50 |
| Discount | `PM BOGO Set Price -0.50` | Counts toward the subtotal, but the cost stays 3.50, the price before the discount |
| Fee | `RECYCLING FEE`, `DEPOSIT 1` | Stored against the item above, excluded from cost |
| Department | `22-DAIRY` | Stored as a category note |
| Non-sale | `(8)24632 PHY STAMP EARN`, `8 @ $0.00` | Ignored, and not counted in the item total |

- **FR-13** Keep any line that can't be parsed and flag it for review. A line must never disappear silently.

### 7.4 Reconciliation

- **FR-14** Run these checks:

| Check | Tolerance | On failure |
|---|---|---|
| Sum of item, discount, and fee lines = printed subtotal | Exact | Blocks export |
| Quantity × unit price = line total | ±$0.01 | Flags line |
| Kg × price per kg = line total | ±$0.01 | Flags line |
| Sum of quantities = printed item count | Exact | Blocks export |
| Taxable lines × tax rate = printed tax | ±$0.01 | Warning |
| Subtotal + tax = total | Exact | Blocks export |

- **FR-15** When the subtotal is off, rank the likely culprits by OCR confidence and whether changing a look-alike digit closes the gap. POSscan **suggests** these fixes and never applies them on its own.
- **FR-16** If a receipt can't be reconciled (for example, part of it is cut off), a person can export only by entering a reason and confirming each line. The reason is recorded in the audit log.

### 7.5 Item matching

- **FR-17** Normalise each code by type. An 11-digit receipt code is converted to a 12-digit UPC-A by adding the calculated check digit, and leading zeros are handled to match RAS style. This rule must be confirmed against real product barcodes in Milestone 1.
- **FR-18** Match in this order, and only on exact matches:
  1. Saved mapping (supplier + receipt code → Estream item)
  2. Normalised UPC in the imported Estream catalog
- **FR-19** Show similar names only as suggestions. A name match is never automatic.
- **FR-20** Save a new mapping only after a person confirms it.
- **FR-21** Let staff mark an item as **Not for resale** (remembered) or **Skip this time**.
- **FR-22** Allow Estream catalog import from a CSV export, if System 5 provides one. Without a catalog, matching relies on saved mappings, and first-time receipts take longer to review.

### 7.6 Review screen

- **FR-23** Show the receipt image and parsed lines side by side, and highlight the image region for the selected line.
- **FR-24** Sort lines needing attention first, and collapse lines that passed every check.
- **FR-25** Mark each line's status with both text and colour: **OK**, **Check**, or **Blocked**.
- **FR-26** Re-run reconciliation immediately after every edit.
- **FR-27** Support keyboard-only review, with Enter to confirm and move to the next line.
- **FR-28** Hold a line for confirmation if its cost changed by more than a configurable amount (default ±25%), or if it has no price history.
- **FR-29** Show old cost, new cost, and difference for each line.

### 7.7 Export

- **FR-30** Enable **Export** only when no blocking issue remains.
- **FR-31** Write one CSV per batch, using the active profile.
- **FR-32** Name files `POSscan_<supplier>_<receipt date>_<batch ID>.csv`.
- **FR-33** When the same item appears more than once on a batch at different costs, use the highest and note it on the review screen. Never average them.
- **FR-34** Show a pre-export summary: number of items, increases, decreases, and skipped lines.
- **FR-35** Track batch status: Draft, Exported, Imported, or Voided. Warn before re-exporting a batch.
- **FR-36** Add a reminder on-screen not to open the CSV in Excel before import. Excel can strip leading zeros and change values such as `.750`.

### 7.8 Settings and administration

- **FR-37** Manage folder paths, price change threshold, CSV profile, and templates.
- **FR-38** Provide a **Test CSV** tool that validates a file against the active profile before import.
- **FR-39** Provide **Create support bundle**, which zips logs and versions and excludes receipts unless the user opts in.
- **FR-40** Back up the database daily, keep 30 days of backups, and offer a restore option.

## 8. CSV output profile

### 8.1 Default profile

POSscan writes the structure of the real RAS CSV, using only its clean 8-column row shape. The first 20 lines match the real file byte for byte, apart from the store and date values, in case the importer skips a fixed number of lines. A second `rows-only` profile leaves out everything except item rows. Milestone 0 decides which is used.

| # | Column | Source for a receipt item | Example |
|---|---|---|---|
| 1 | Listing Code | Item map; blank if none | `G` |
| 2 | SKU | Estream item number from the item map | `125` |
| 3 | UPC | RAS-style UPC: check digit included, leading zeros dropped | `80686821311` |
| 4 | Brand Name | Estream item name, or receipt name if unavailable | `CANADIAN CLUB - PREMIUM` |
| 5 | Container Size | Item map; blank if none | `1.14` or `6 x .355` |
| 6 | New Price | Actual cost per unit paid on the receipt | `27.4` |
| 7 | Old Price | Last cost in POSscan or the catalog | `26.64` |
| 8 | Diff | New Price − Old Price | `0.76` |

The example values come from the RAS file. They show the format, not a Superstore item.

### 8.2 Rules

- Numbers follow the real file: no currency symbol, trailing zeros removed from positive values (`27.4`, `2`, `0`), and two decimals on negatives (`-1.50`). Single container sizes use the same format (`0.75`), and pack sizes stay as text (`6 x .355`).
- Files are ASCII with CRLF line endings and no BOM. Accented letters are converted to plain ones (`CRÈME` becomes `CREME`).
- POSscan rows always use 8 columns. It doesn't reproduce the shifted rows, since those look like conversion artifacts rather than a requirement.
- UPC and SKU are text, so their digits are never reformatted.
- Fields containing commas, quotes, or line breaks follow RFC 4180 quoting.
- **Old Price when unknown:** the default is to set it equal to New Price with a Diff of `0.00`, a form the RAS report already contains. The review screen labels these rows as having no history. If Milestone 0 shows that Estream accepts blanks, blank becomes the default, since it is more honest.
- **Weighed items:** exported per kg only if Milestone 0 shows that Estream handles this correctly. Otherwise they are excluded from the CSV and listed on the summary for manual entry.
- Every export is checked by reading it back against the profile before it is released.

### 8.3 Configurable settings

Each profile is a versioned JSON file that sets the following: column names and order, header on or off, delimiter, quote style, encoding and BOM, line ending, decimal format, UPC padding, match key, and rules for blank values. Changing the Estream format doesn't require a new program build.

## 9. Data model

| Table | Key contents |
|---|---|
| `receipts` | Supplier, date, store, transaction ID, file hash, archive path, parsed totals, template version, status |
| `receipt_lines` | Raw text, image region, confidence, code and type, description, quantity, unit, regular price, actual cost, fees, status |
| `item_map` | Supplier + receipt code → Estream SKU, UPC, name, Listing Code, Container Size, Not for resale flag, confirmed by, confirmed date |
| `catalog_items` | Imported Estream items, if an export is available |
| `price_history` | Item, supplier, cost, date, source receipt |
| `batches` | Profile version, file path and hash, row count, status, timestamps |
| `audit_log` | Append-only record of edits, overrides, exports, and status changes |

## 10. Privacy and security

- All data stays on the PC. POSscan makes no network calls in v1.
- Payment, loyalty, and cashier details aren't parsed or stored in database fields. The example contains a masked card number, a card status, a loyalty balance, and staff names.
- Archived PDFs are kept as scanned, so the data folder uses per-user Windows permissions.
- Receipts are retained for a configurable period, by default 7 years to support typical business record-keeping. The store should confirm this with its accountant.
- The installer and program files are code-signed.
- POSscan runs without administrator rights.

## 11. Non-functional requirements

| Area | Requirement |
|---|---|
| Speed | Under 30 seconds from PDF to review screen for a typical receipt, on a 4-core PC with 8 GB of RAM |
| Reliability | No data loss when power fails mid-operation, verified by testing |
| Compatibility | Windows 11 x64, 1366 × 768 minimum, 100–200% display scaling |
| Accessibility | Full keyboard use, and status is never shown by colour alone |
| Install | Under 2 minutes with no separate prerequisites |
| Updates | Manual installer in v1, with no automatic updates that could change behaviour unexpectedly |
| Diagnostics | Plain-language errors, each with a code, and detail in the logs |

## 12. Testing and acceptance

### 12.1 Receipt test set
Collect at least **30 real Superstore receipts**, including faded, creased, and very long ones. Hand-check their contents to create verified answer files. Store them outside any public repository, as they are real business records.

### 12.2 OCR selection gate
Run the OCR engine on the test set. A v1 engine must meet the following:

| Measure | Target |
|---|---|
| Item code read correctly, per line | ≥ 90% |
| Price read correctly, per line | ≥ 95% |
| Receipts reconciling with no corrections | ≥ 60% |
| **Wrong values exported after review** | **0** |

If no local engine meets these targets, consider an optional cloud check that is off by default and sends only the item region of the receipt. Adding it would need a separate decision, since it changes the offline and privacy design. These targets are starting estimates.

**First result, from one receipt only:** this isn't enough to pass or fail the gate.

| Measure | Result on the example receipt |
|---|---|
| Item codes read correctly | 21 of 25 (84%), below target |
| Line amounts read correctly | 23 of 24 (96%) |
| Reconciled with no corrections | No: blocked until one misread total was confirmed |
| After confirming that total | Subtotal, item count, and tax base all match exactly |
| Time | About 17 seconds on the development PC |

Three of the four bad codes are flagged automatically. The fourth, `06036314170` for `06038314170`, looks like a valid code, which is why FR-18 accepts only exact matches to confirmed items and treats a one-digit difference as a question for staff. These figures were compared with a hand reading of the scan.

### 12.3 Automated tests
- **Parser:** every line type in section 7.3, using saved OCR output
- **Reconciliation:** including the 2¢ gap in the example, seeded digit errors, and a cut-off receipt
- **Money and UPC:** rounding, check digit calculation, and leading zeros, using the 201 RAS UPCs as known-good data
- **CSV:** a byte-for-byte comparison with an approved reference file, once a real sample exists
- **Failure injection:** power loss during export, a full disk, locked files, and corrupt PDFs

### 12.4 Acceptance criteria
v1 is accepted when all of these are met:
1. A POSscan batch imports into System 5 with no errors, and each changed price is verified on screen in Estream.
2. The example receipt goes from PDF to reviewed export and matches its printed totals.
3. A deliberately altered price or code can't be exported without a recorded confirmation.
4. A misread receipt can be corrected and exported in under 5 minutes.
5. Two weeks of real use pass with no incorrect price reaching the POS.

## 13. Delivery plan

| Milestone | Work | Done when |
|---|---|---|
| **0. Format proof** | Run the test imports in `milestone0/README.md` on a backed-up system. | Section 2's unknowns are answered and the profile is finalized |
| **1. OCR gate** | Collect test receipts and compare engines. Confirm the UPC rule on real products. | The gate in section 12.2 is passed |
| **2. Core** | Parser, reconciliation, database, and archive | The test set passes automatically |
| **3. Application** | Review screen, mapping, export, and installer | The example receipt runs end to end |
| **4. Pilot** | Run alongside the current process at the store | Section 12.4 is met |

Build status is tracked in `PLAN.md`. The app was built ahead of Milestones 0 and 1 at the store's request. **Don't rely on it for real price changes until Milestone 0 passes.** If Estream rejects non-liquor items, the output design must change, though most of the app stays as it is. The options would be to look for a different System 5 import path, or to ask Estream support for one.

## 14. Risks

| Risk | Likelihood | Impact | Mitigation |
|---|---|---|---|
| RAS import rejects non-liquor items | Unknown | Critical | Milestone 0 comes first, and support is contacted if it fails |
| Estream misreads RAS rows with shifted columns | Unknown | High | POSscan writes only clean rows, and Milestone 0 checks shifted rows in Estream |
| Import changes a different price than expected | Unknown | High | Verify in Milestone 0 on a test setup or with a backup |
| OCR misreads a digit | High | High | Reconciliation, confidence flags, and required review |
| Misread code that resolves to a different real item | Low | High | Saved mappings, and the receipt name shown beside the Estream name |
| Superstore changes its receipt layout | Medium | Medium | Template match fails safely and stops, and templates are versioned |
| Staff confirm lines without checking | Medium | High | Totals must still reconcile, and large price changes need specific confirmation |
| CSV edited in Excel before import | Medium | Medium | On-screen warning and the Test CSV tool |
| Estream upgrade changes the import | Low | Medium | Profile can be changed without a new build |

## 15. Open questions

| # | Question | Who can answer | Blocks | Default until answered |
|---|---|---|---|---|
| 1 | Does Estream read the RAS CSV's shifted rows correctly, and does it need the report text? | Test import | Milestone 0 | Mirror the real structure, with clean rows only |
| 2 | Will the import accept non-liquor items, and does it match on SKU or UPC? | Test import or Estream support | Milestone 0 | None; must be tested |
| 3 | Does the import change cost, retail, or both? | Test import or Estream support | Milestone 0 | Treated as wholesale cost |
| 4 | Should the store want suggested retail prices added in a later version? | Store | Nothing in v1 | Cost only |
| 5 | Can System 5 export its item list to CSV? | Store or Estream support | Quicker first-time mapping | Saved mappings only |
| 6 | Is there a test environment, or a way to back up and restore System 5 before a test import? | Store or Estream support | Milestone 0 safety | Back up before any test |
| 7 | Which other suppliers matter most after Superstore? | Store | Later templates | Superstore only |
| 8 | How should a weighed item be represented in Estream? | Test import | Weighed export | Manual entry |

# Milestone 0: Estream test imports

These tests answer the questions only Estream can answer. Nothing else in POSscan should be finished until they're done.

> **Warning: these files can change real prices.** Back up System 5 first, and use a test setup if you have one. Restore the backup afterwards. The liquor files use the 2017 prices from your RAS list, and the grocery files use prices from one receipt.

## Before you start

1. Back up System 5 and confirm you know how to restore it.
2. Write down the current cost and retail price of each item you'll check.
3. Don't open or save these CSVs in Excel. It can change the values. Notepad is safe for viewing.

## Test 1: How does Estream read the original RAS file?

Import your original `RAS Price Change List ... .csv` on the backed-up system, then look up these items:

| SKU | Item | Why it matters | Correct New Price | Actual price in Estream |
|---|---|---|---|---|
| 15628 | Apricot Brandy Bols | Clean row (control) | 13.34 | |
| 125 | Canadian Club Premium | Prices shifted one column | 27.40 | |
| 893 | J. P. Wisers Deluxe | Size and prices shifted one column | 19.38 | |
| 7617 | Chivas Regal 12 Yr | Name split across two cells | 41.54 | |
| 579037 | Bodacious Smooth White | No UPC, so it can only match on SKU | 25.05 | |

Also note:
- Did the price change appear as **cost**, **retail**, or both?
- Did Estream report how many rows it imported, or any errors?

Restore the backup before continuing.

## Test 2: Do POSscan's files import?

Import each file, check the result, and restore the backup between files.

| File | What it tests | Check | Result |
|---|---|---|---|
| `probe-a-liquor-wrapped.csv` | POSscan's full layout | SKU 125 = 27.40, SKU 3558 = 19.92 | |
| `probe-b-liquor-rows-only.csv` | Are report text and headers needed? | Same as above | |
| `probe-c-grocery-wrapped.csv` | **Non-liquor items, matched by UPC only, with no SKU** | Milk = 5.05, basil = 4.00, if you stock them | |
| `probe-d-weighed-wrapped.csv` | Item sold by weight | Green cabbage (4556) = 2.16 | |

If you don't stock the milk or basil, edit a copy in Notepad and put in the UPC and price of a grocery item you do stock. Use the UPC as Estream shows it, and tell me how it's written there, because that also tells us the format it expects.

## What to send back

- The filled-in tables above, plus any error messages, screenshots, or import logs
- What happened to items Estream didn't recognise: rejected, skipped, or created?
- How Estream displays the UPC of one grocery item, including any leading zeros

## What each result means

| Result | Next step |
|---|---|
| Probes A and C both work | Build the review screen with this format |
| A works but C fails | Grocery items need their Estream SKU. Retest C with a real SKU in column 2, and otherwise contact Estream support about another import route. |
| Test 1 shows wrong prices on shifted rows | Your current RAS process has a problem to fix, separately from POSscan |
| A fails | Compare with the original file. The layout profile can be corrected without a rebuild. |

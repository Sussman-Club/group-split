# Receipt importer preview

Load this folder as an unpacked extension in Chrome or Edge. Open the
GroupSplit receipts inbox and select **Import from a store**. Choose Amazon
or Costco, select Connect store, and sign in yourself. Open your orders;
for Costco select In-Warehouse. Return to the importer tab,
choose dates, and find receipts.
Review the list before saving receipts as PDFs and uploading them to the
inbox. Keep the GroupSplit tab open and signed in.

The inbox shortcut requires this extension to be reloaded after updates and
the receipts page to be refreshed. If it cannot detect the extension, it
shows installation guidance and leaves the normal file upload available.

This early preview checks available Costco warehouse periods that may overlap
the requested dates, and pages through each period. Costco PDFs are printouts
of the displayed receipt and can contain membership or masked payment details.
Period labels it cannot interpret are checked conservatively.
For Amazon it checks each year in the chosen date range and follows the
available order-history pages, reporting orders without a usable invoice.
The optional recipient filter uses Amazon's “Ship to” name on the current
orders page. Orders with no readable recipient remain unknown; recipient
names are used locally and are not copied into the receipt or sent to GroupSplit.
It creates a new private summary locally from the date, item names, quantities,
prices and totals. The original invoice, customer name, address, payment
details and order number are not copied. Item titles may themselves contain
personal text; the importer shows them for review before saving or uploading.
Shipping is separate when
present. A truncated hash
of the order number distinguishes otherwise identical purchases. The totals
must reconcile or the import stops without saving. New inbox receipts receive
the summary details automatically; existing duplicates are left unchanged.
It does not yet handle Costco online orders.
Neither retailer nor the automatic upload path has
been validated against a live account. It stops if it cannot identify a
receipt unambiguously.
The extension accesses a retailer only after the user connects it and clicks Find.
The browser's debugging permission is required to print a Costco receipt to PDF.
No retailer credentials are requested or stored. The extension does not navigate
to or click purchase, payment, return, cancellation, or print controls.

The preview sends receipt PDFs only to the GroupSplit inbox tab it was
opened from, restricted to the official GroupSplit domain or a local app.
It saves a copy in Downloads/GroupSplit; Costco PDFs can contain membership or
masked payment information. The Amazon summary contains only the allowlisted
fields described above. Protect and remove
downloaded copies when you no longer need them. No separate analytics or external
sync service is configured in the extension.

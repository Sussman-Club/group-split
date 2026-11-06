# Receipt importer preview

Load this folder as an unpacked extension in Chrome or Edge. Open the
GroupSplit receipts inbox and select **Import from a store**. Choose Amazon
or Costco and select Connect store. The store opens in a background tab. If
you need to sign in, go to that tab, sign in, and return to the importer.
For Costco, the importer selects the chosen warehouse or online view when
you find receipts.
Review the list before importing receipts as PDFs to the
inbox. Keep the GroupSplit tab open and signed in.

The inbox shortcut requires this extension to be reloaded after updates and
the receipts page to be refreshed. If it cannot detect the extension, it
shows installation guidance and leaves the normal file upload available.
The AppHost parameter `receipt-importer-extension-ids` lists permitted browser
extension IDs, separated by commas. Add the ID for each browser store or local
unpacked extension you use; IDs can differ between installations.

This early preview checks available Costco warehouse periods that may overlap
the requested dates, and pages through each period. Costco PDFs are printouts
of the displayed receipt and can contain membership or masked payment details.
Period labels it cannot interpret are checked conservatively.
For Amazon it checks each year in the chosen date range and follows the
available order-history pages, reporting orders without a usable invoice.
Select Check recipients to scan Amazon order pages for the chosen dates and
populate the optional “Ship to” filter. Orders with no readable recipient remain unknown.
Amazon verifies the order number on the printable receipt, then renders the
full page as a PDF. It may include addresses, payment details and order
identifiers. Use **Review printable receipt** to check the source before importing.
GroupSplit automatically reads new attachments that arrive without extracted
details. If reading fails, the source file stays available in the inbox.
Costco Online scans available Costco periods that may overlap the selected dates. It creates a private summary using the order date, item names, quantities, prices and totals. Review item names before importing.
Neither retailer nor the automatic upload path has
been validated against a live account. It stops if it cannot identify a
receipt unambiguously.
The extension accesses a retailer only after the user connects it and clicks Find.
The browser's debugging permission is required to render Amazon and Costco warehouse receipts to PDF.
No retailer credentials are requested or stored. The extension does not navigate
to or click purchase, payment, return, cancellation, or print controls.

The preview sends receipt PDFs only to the GroupSplit inbox tab it was
opened from, restricted to the official GroupSplit domain or a local app.
The extension does not download a copy to your device. Costco PDFs can contain membership or
masked payment information. No separate analytics or external
sync service is configured in the extension.

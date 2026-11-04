# GroupSplit receipt importer — privacy policy draft

This draft describes the current preview and needs a public address and
publisher contact before submission.

The GroupSplit receipt importer helps you copy selected purchases from your
signed-in Amazon or Costco session into your GroupSplit receipts inbox. It
accesses a retailer only after you connect that retailer and initiate an import.
You sign in directly with the retailer; the extension does not ask for or store
your retailer password.

For Amazon, the extension reads order dates, ship-to recipient names for the
optional filter, and invoice item names, quantities, prices, shipping, tax and
totals. It uses the order identifier to create a reference for duplicate
detection. The generated summary omits the original invoice's customer,
address and payment sections. Item names can themselves contain personal text;
review the preview before importing. Recipient names are used for filtering and
are not copied into the generated receipt or sent to GroupSplit.

For Costco warehouse purchases, the extension reads receipt listings and
prints the displayed receipt into a PDF. These PDFs can include customer,
membership or masked payment information. Costco online orders are not
currently supported.

The extension saves a copy of each PDF in your Downloads/GroupSplit folder and
uploads it to the signed-in GroupSplit inbox when you select the import action.
Amazon's extracted item and total details are also sent to GroupSplit for new
receipts. GroupSplit stores uploaded receipts under your account; its own
privacy and retention policies apply to that stored data. You can delete local
copies from your downloads folder.

The extension uses your existing browser session to access the retailer. Its
current code does not configure analytics or send retailer data to an additional
third-party service. The browser and retailer process their own requests under
their respective policies.

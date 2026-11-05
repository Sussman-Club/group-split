# Edge Add-ons submission notes

## Listing description

Import receipts into your GroupSplit inbox from Amazon and Costco. Connect a
store using your own signed-in browser session, choose a date range, and review
the results before importing them. Amazon imports use the full printable receipt.
Costco supports in-warehouse receipts and online order summaries.

The importer needs a signed-in GroupSplit inbox. Retailer access is requested
when you connect the store. A copy of each imported PDF is saved to your
Downloads/GroupSplit folder. Amazon and Costco warehouse PDFs may contain customer or payment
information; review them before sharing.

## Permissions

- `activeTab` and `scripting`: interact with the selected retailer and the
  GroupSplit inbox during import.
- Optional Amazon and Costco site access: read the signed-in retailer pages
  after the user connects a store.
- `debugger`: render Amazon and Costco warehouse receipts to PDF.
- `downloads`: save a copy of each generated receipt.
- GroupSplit site access: offer the inbox shortcut and send approved receipts
  to the signed-in inbox.

## Before submitting

- Enroll and complete verification in the Microsoft Edge developer program.
- Publish an accurate privacy policy at a publicly accessible HTTPS URL and
  provide that URL in Partner Center. `PRIVACY-DRAFT.md` is a starting point,
  not a published policy.
- Confirm the GroupSplit production domain and privacy/contact details.
- Verify the Amazon and Costco flows in Edge using suitable test accounts.
- Complete Partner Center's data-use declarations and reviewer instructions.

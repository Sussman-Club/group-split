using System.Net.Http.Json;
using GroupSplit.API.Test.Base;
using GroupSplit.Shared;

namespace GroupSplit.API.Test.Endpoints;

public sealed class ReceiptObservedFingerprintEndpointTest
{
    [Fact]
    public async Task Matching_observed_fingerprints_are_private_suggestions_even_when_details_differ()
    {
        await using var host = await ApiEndpointHost.StartAsync();
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        var foreign = Guid.NewGuid();
        var fingerprint = new string('a', 64);
        async Task Save(HttpClient client, Guid id, string merchant) =>
            (await client.PutAsJsonAsync($"/receipts/inbox/{id}", new SaveReceiptInboxRequest
            {
                Receipt = new SaveReceiptRequest
                {
                    MerchantName = merchant,
                    ObservedFingerprint = fingerprint,
                    Total = 1,
                    Subtotal = 1,
                    Items = [new ReceiptItemInput { Name = "Item", UnitPrice = 1, TotalPrice = 1 }]
                }
            }, TestContext.Current.CancellationToken)).EnsureSuccessStatusCode();
        await Save(host.Client, first, "First");
        await Save(host.Client, second, "Different");
        using var other = host.ClientForAnotherUser();
        await Save(other, foreign, "Foreign");
        var result = await host.Client.GetFromJsonAsync<ReceiptInboxResponse>($"/receipts/inbox/{first}", TestContext.Current.CancellationToken);
        Assert.Equal(second, Assert.Single(result!.PossibleDuplicates).Id);
    }
    [Fact]
    public async Task A_read_fingerprint_suggests_an_extension_import_for_review()
    {
        await using var host = await ApiEndpointHost.StartAsync();
        var imported = Guid.NewGuid();
        var uploaded = Guid.NewGuid();
        var fingerprint = new string('b', 64);
        using var form = new MultipartFormDataContent();
        var file = new ByteArrayContent([1, 2, 3]);
        file.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/pdf");
        form.Add(file, "file", "receipt.pdf");
        form.Add(new StringContent(fingerprint), "importFingerprint");
        (await host.Client.PostAsync($"/receipts/inbox/{imported}/files", form, TestContext.Current.CancellationToken)).EnsureSuccessStatusCode();
        var details = new SaveReceiptInboxRequest
        {
            Receipt = new SaveReceiptRequest
            {
                MerchantName = "Warehouse",
                ObservedFingerprint = fingerprint,
                Subtotal = 1,
                Total = 1,
                Items = [new ReceiptItemInput { Name = "Item", UnitPrice = 1, TotalPrice = 1 }]
            }
        };
        (await host.Client.PutAsJsonAsync($"/receipts/inbox/{uploaded}", details, TestContext.Current.CancellationToken)).EnsureSuccessStatusCode();
        var row = await host.Client.GetFromJsonAsync<ReceiptInboxResponse>($"/receipts/inbox/{uploaded}", TestContext.Current.CancellationToken);
        Assert.Equal(imported, Assert.Single(row!.PossibleDuplicates).Id);
    }

}

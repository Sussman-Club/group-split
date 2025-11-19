using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace GroupSplit.AppHost;

public static class Extensions
{

    extension<T>(IResourceBuilder<T> builder) where T : IResourceWithEndpoints
    {
        public IResourceBuilder<T> WithScalarUrl()
        {
            return builder
                .WithUrls(ctx =>
                {
                    foreach (var url in ctx.Urls.Where(x => x.Endpoint?.EndpointName is "http" or "https"))
                    {
                        url.DisplayLocation = UrlDisplayLocation.DetailsOnly;
                    }
                })
                .WithUrlForEndpoint("http", _ => new ResourceUrlAnnotation
                {
                    Url = "/scalar/v1",
                    DisplayLocation = UrlDisplayLocation.SummaryAndDetails
                });
        }
    }

    extension(IResourceBuilder<ProjectResource> resourceBuilder)
    {
        public IResourceBuilder<ProjectResource> WithDataPopulationCommand()
        {
            return resourceBuilder.WithCommand("seed-data", "Seed database",
                async context =>
                {
                    await SeedIdentityUsersAsync(resourceBuilder, context);
                    return new ExecuteCommandResult { Success = true };
                }, new CommandOptions
                {
                    IconName = "FoodGrains"
                });
        }

        private static async Task SeedIdentityUsersAsync(IResourceBuilder<ProjectResource> identityApi,
            ExecuteCommandContext context)
        {
            var cancellationToken = context.CancellationToken;
            var logger = context.ServiceProvider.GetRequiredService<ResourceLoggerService>()
                .GetLogger(identityApi.Resource);

            logger.LogInformation("🔐 Starting Identity user seeding with Bogus...");

            using var httpClient = new HttpClient();

            var httpEndpoint = identityApi.GetEndpoint("http");
            var baseUrl = await httpEndpoint.GetValueAsync(cancellationToken);

            var faker = new Bogus.Faker();

            var total = 25;
            var success = 0;
            var errors = 0;

            logger.LogInformation("📦 Generating {total} fake Identity users...", total);

            for (var i = 0; i < total; i++)
            {
                try
                {
                    var first = faker.Name.FirstName();
                    var last = faker.Name.LastName();
                    var email = faker.Internet.Email(first, last);

                    var payload = new
                    {
                        email,
                        password = "Password123!",
                        firstName = first,
                        lastName = last
                    };

                    var json = JsonSerializer.Serialize(payload);
                    var content = new StringContent(json, Encoding.UTF8, "application/json");

                    var response = await httpClient.PostAsync(
                        $"{baseUrl}/users/register",
                        content,
                        cancellationToken);

                    if (response.IsSuccessStatusCode)
                    {
                        success++;
                        logger.LogInformation("✅ Created identity user {Email}", email);
                    }
                    else
                    {
                        errors++;
                        var body = await response.Content.ReadAsStringAsync(cancellationToken);
                        logger.LogWarning("⚠️ Failed creating {Email}: {Status} - {Body}",
                            email, response.StatusCode, body);
                    }
                }
                catch (Exception ex)
                {
                    errors++;
                    logger.LogError("💥 Exception on user {i}: {Message}", i, ex.Message);
                }

                await Task.Delay(50, cancellationToken);
            }

            logger.LogInformation("🎉 Identity user seeding complete! {success} created, {errors} errors.",
                success, errors);
        }
    }
}
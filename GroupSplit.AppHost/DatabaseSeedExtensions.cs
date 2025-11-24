using Microsoft.Extensions.DependencyInjection;

namespace GroupSplit.AppHost;

#pragma warning disable ASPIREINTERACTION001

public static class DatabaseSeedExtensions
{
    extension<TResource>(IResourceBuilder<TResource> dbResourceBuilder) where TResource : IResource
    {
        public IResourceBuilder<TResource> WithTestCommand(string commandName = "test")
        {
            return dbResourceBuilder.WithCommand(commandName, "Test input", async context =>
            {
                var interactionService = context.ServiceProvider.GetRequiredService<IInteractionService>();

                var input = new InteractionInput
                {
                    Name = "AllowCustomInput",
                    Label = "Favorite food?",
                    InputType = InputType.Choice,
                    Options = [KeyValuePair.Create("pizza", "Pizza"), KeyValuePair.Create("burger", "Burger")],
                    AllowCustomChoice = true
                };

                var result = await interactionService.PromptInputAsync("What is your favorite food?",
                    "Select your favorite food.", input);

                if (result.Data is null || result.Canceled)
                {
                    return new ExecuteCommandResult
                        { Success = false, ErrorMessage = "Command canceled by user." };
                }

                return new ExecuteCommandResult { Success = true };
            }, new CommandOptions { IconName = "Test" });
        }
    }
}
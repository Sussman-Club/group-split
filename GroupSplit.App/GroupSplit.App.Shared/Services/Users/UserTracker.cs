using GroupSplit.Shared;
using Microsoft.AspNetCore.Components;

namespace GroupSplit.App.Shared.Services.Users;

public class UserTracker
{
    [PersistentState]
    public UserInfo? User { get; set; }
}
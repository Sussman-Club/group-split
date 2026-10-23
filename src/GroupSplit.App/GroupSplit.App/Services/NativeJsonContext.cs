using System.Text.Json.Serialization;

namespace GroupSplit.App.Services;

[JsonSerializable(typeof(NativeSession.Tokens))]
[JsonSerializable(typeof(NativeSession.TokenResponse))]
[JsonSerializable(typeof(NativeSession.OAuthError))]
[JsonSerializable(typeof(GroupSplit.Shared.PushDeviceRequest))]
internal partial class NativeJsonContext : JsonSerializerContext;

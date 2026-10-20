using System.Text.Json.Serialization;

namespace GroupSplit.App.Services;

[JsonSerializable(typeof(NativeSession.Tokens))]
[JsonSerializable(typeof(NativeSession.TokenResponse))]
[JsonSerializable(typeof(NativeSession.OAuthError))]
internal partial class NativeJsonContext : JsonSerializerContext;

using System.Text.Json;
using GroupSplit.Cli.Configuration;

namespace GroupSplit.Cli.Auth;

/// <summary>
/// Credentials on disk, keyed by realm and client so several servers can be signed in at
/// once. The file is written 0600 on Unix; there is no OS keychain here because the CLI
/// has to work over SSH and in containers, where there is no keyring to talk to.
/// </summary>
public sealed class TokenStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    public string Path => ConfigPaths.CredentialsFile;

    public StoredCredential? Get(Uri authority, string clientId)
        => Load().Credentials.GetValueOrDefault(Key(authority, clientId));

    public void Save(Uri authority, string clientId, StoredCredential credential)
    {
        var file = Load();
        file.Credentials[Key(authority, clientId)] = credential;
        Write(file);
    }

    public bool Remove(Uri authority, string clientId)
    {
        var file = Load();

        if (!file.Credentials.Remove(Key(authority, clientId)))
        {
            return false;
        }

        Write(file);
        return true;
    }

    private static string Key(Uri authority, string clientId)
        => $"{authority.ToString().TrimEnd('/')}|{clientId}";

    private CredentialFile Load()
    {
        if (!File.Exists(Path))
        {
            return new CredentialFile();
        }

        try
        {
            return JsonSerializer.Deserialize<CredentialFile>(File.ReadAllText(Path), Options)
                   ?? new CredentialFile();
        }
        catch (JsonException)
        {
            // A corrupt credential file is not worth failing a command over: the worst
            // case is one more sign-in, and the file is rewritten below.
            return new CredentialFile();
        }
    }

    private void Write(CredentialFile file)
    {
        Directory.CreateDirectory(ConfigPaths.DataDirectory);

        // Create the file before writing so the mode is narrowed before any token is in
        // it -- writing first would leave a readable window on a shared machine.
        if (!File.Exists(Path))
        {
            using (File.Create(Path))
            {
            }
        }

        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(Path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }

        File.WriteAllText(Path, JsonSerializer.Serialize(file, Options));
    }
}

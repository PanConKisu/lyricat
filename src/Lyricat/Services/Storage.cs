using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace LyricsStatus.Services;

public static class Mascots
{
    public const string Concept = "NinuConcept";
    public const string Dracani = "NinuByDracani";
}

public static class Paths
{
    public const string AppName = "Lyricat";
    public const string Publisher = "Kiisum!";
    public const string LegacyAppName = "SpotifyLyricsStatus";

    static string Roaming => Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
    static string LocalRoot => Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

    public static string Data => Directory.CreateDirectory(Path.Combine(Roaming, Publisher, AppName)).FullName;

    public static string LegacyData => Path.Combine(Roaming, LegacyAppName);

    public static string Local => Directory.CreateDirectory(Path.Combine(LocalRoot, Publisher, AppName)).FullName;

    public static void MoveOldData()
    {
        var oldData = Path.Combine(Roaming, AppName);
        if (Directory.Exists(oldData))
        {
            foreach (var file in Directory.GetFiles(oldData))
            {
                var target = Path.Combine(Data, Path.GetFileName(file));
                try { if (!File.Exists(target)) File.Move(file, target); } catch (IOException) { }
            }
            try { Directory.Delete(oldData); } catch (IOException) { /* not empty: leave it */ }
        }
        var oldLocal = Path.Combine(LocalRoot, AppName);
        if (Directory.Exists(oldLocal))
            try { Directory.Delete(oldLocal, recursive: true); } catch (Exception) { /* in use: next time */ }
    }

    public static string Config => Path.Combine(Data, "config.json");
    public static string WebViewData => Path.Combine(Local, "WebView2");
}

public sealed class Settings
{
    [JsonPropertyName("status_format")] public string StatusFormat { get; set; } = "{line}";
    [JsonPropertyName("emoji")] public string Emoji { get; set; } = "";
    [JsonPropertyName("no_lyrics_text")] public string NoLyricsText { get; set; } = "meow";
    [JsonPropertyName("pause_text")] public string PauseText { get; set; } = "♪";
    [JsonPropertyName("paused_text")] public string PausedText { get; set; } = "";
    [JsonPropertyName("update_interval")] public double UpdateInterval { get; set; } = 4.0;
    [JsonPropertyName("stop_on_rate_limit")] public bool StopOnRateLimit { get; set; } = true;
    [JsonPropertyName("lyrics_offset")] public double LyricsOffset { get; set; } = 0.3;
    [JsonPropertyName("check_updates")] public bool CheckUpdates { get; set; } = true;
    [JsonPropertyName("join_lines")] public bool JoinFastLines { get; set; }
    [JsonPropertyName("join_separator")] public string JoinSeparator { get; set; } = "-";
    [JsonPropertyName("mascot")] public string Mascot { get; set; } = Mascots.Concept;
    [JsonPropertyName("ears_dance")] public bool EarsDance { get; set; } = true;
    [JsonPropertyName("reduce_motion")] public bool ReduceMotion { get; set; }
    [JsonPropertyName("accepted_disclaimer")] public bool AcceptedDisclaimer { get; set; }

    public const double MinInterval = 1.0, MaxInterval = 10.0;

    static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
    };

    public static Settings Load()
    {
        try
        {
            Paths.MoveOldData();
            var legacy = Path.Combine(Paths.LegacyData, "config.json");
            if (!File.Exists(Paths.Config) && File.Exists(legacy)) File.Copy(legacy, Paths.Config);

            if (File.Exists(Paths.Config))
                return (JsonSerializer.Deserialize<Settings>(File.ReadAllText(Paths.Config), Options) ?? new()).Normalized();
        }
        catch (Exception) { /* broken file: fall back to defaults */ }
        return new Settings();
    }

    public void Save()
    {
        Normalized();
        var tmp = Paths.Config + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(this, Options));
        File.Move(tmp, Paths.Config, overwrite: true);
    }

    public Settings Normalized()
    {
        UpdateInterval = Math.Round(Math.Clamp(UpdateInterval, MinInterval, MaxInterval) * 2) / 2;
        LyricsOffset = Math.Round(Math.Clamp(LyricsOffset, -3, 3), 1);
        StatusFormat = string.IsNullOrWhiteSpace(StatusFormat) ? "{line}" : StatusFormat;
        Emoji ??= ""; NoLyricsText ??= ""; PauseText ??= ""; PausedText ??= ""; JoinSeparator ??= "-";
        if (Mascot is not (Mascots.Concept or Mascots.Dracani)) Mascot = Mascots.Concept;
        return this;
    }

    public Settings Clone() => (Settings)MemberwiseClone();
}

public enum Risk { Safe, Questionable, Dangerous }

public static class RiskLevel
{
    public static Risk For(double interval) =>
        interval >= 4 ? Risk.Safe : interval >= 2 ? Risk.Questionable : Risk.Dangerous;
}

public static class Secrets
{
    public const string DiscordToken = "discord_token";
    public const string SpDc = "sp_dc";
    public const string ClientId = "spotify_client_id";
    public const string ClientSecret = "spotify_client_secret";
    public const string RefreshToken = "spotify_refresh_token";

    public static readonly string[] UserFields = [DiscordToken, SpDc, ClientId, ClientSecret];

    const int CRED_TYPE_GENERIC = 1;
    const int CRED_PERSIST_LOCAL_MACHINE = 2;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct CREDENTIAL
    {
        public int Flags;
        public int Type;
        public string TargetName;
        public string? Comment;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
        public int CredentialBlobSize;
        public IntPtr CredentialBlob;
        public int Persist;
        public int AttributeCount;
        public IntPtr Attributes;
        public string? TargetAlias;
        public string? UserName;
    }

    [DllImport("advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool CredRead(string target, int type, int flags, out IntPtr credential);

    [DllImport("advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool CredWrite(ref CREDENTIAL credential, int flags);

    [DllImport("advapi32.dll", EntryPoint = "CredDeleteW", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool CredDelete(string target, int type, int flags);

    [DllImport("advapi32.dll")]
    static extern void CredFree(IntPtr buffer);

    static string Target(string name) => $"{Paths.AppName}:{name}";

    static (string? User, string? Secret) ReadRaw(string target)
    {
        if (!CredRead(target, CRED_TYPE_GENERIC, 0, out var ptr)) return (null, null);
        try
        {
            var cred = Marshal.PtrToStructure<CREDENTIAL>(ptr);
            string? secret = null;
            if (cred.CredentialBlobSize > 0)
            {
                var bytes = new byte[cred.CredentialBlobSize];
                Marshal.Copy(cred.CredentialBlob, bytes, 0, bytes.Length);
                secret = Encoding.Unicode.GetString(bytes).TrimEnd('\0');
            }
            return (cred.UserName, secret);
        }
        finally { CredFree(ptr); }
    }

    public static string Get(string name) => ReadRaw(Target(name)).Secret?.Trim() ?? "";

    public static void Set(string name, string? value)
    {
        value = value?.Trim().Trim('"', '\'');
        if (string.IsNullOrEmpty(value))
        {
            CredDelete(Target(name), CRED_TYPE_GENERIC, 0);
            return;
        }
        var bytes = Encoding.Unicode.GetBytes(value);
        var blob = Marshal.AllocHGlobal(bytes.Length);
        try
        {
            Marshal.Copy(bytes, 0, blob, bytes.Length);
            var cred = new CREDENTIAL
            {
                Type = CRED_TYPE_GENERIC,
                TargetName = Target(name),
                UserName = name,
                CredentialBlob = blob,
                CredentialBlobSize = bytes.Length,
                Persist = CRED_PERSIST_LOCAL_MACHINE,
            };
            if (!CredWrite(ref cred, 0)) throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        finally
        {
            Marshal.Copy(new byte[bytes.Length], 0, blob, bytes.Length);
            Marshal.FreeHGlobal(blob);
            Array.Clear(bytes);
        }
    }

    public static bool HasAll() => UserFields.All(f => Get(f).Length > 0);

    public static void MigrateLegacy()
    {
        var old = Paths.LegacyAppName;
        foreach (var name in UserFields.Append(RefreshToken))
        {
            if (Get(name).Length > 0) continue;
            string? secret = null, legacyTarget = null;
            foreach (var target in new[] { $"{old}:{name}", $"{name}@{old}" })
            {
                secret = ReadRaw(target).Secret;
                if (!string.IsNullOrEmpty(secret)) { legacyTarget = target; break; }
            }
            if (string.IsNullOrEmpty(secret))
            {
                var (user, bare) = ReadRaw(old);
                if (user == name) { secret = bare; legacyTarget = old; }
            }
            if (string.IsNullOrEmpty(secret) || legacyTarget is null) continue;
            Set(name, secret);
            if (Get(name) == secret.Trim()) CredDelete(legacyTarget, CRED_TYPE_GENERIC, 0);
        }

        foreach (var file in new[] { Path.Combine(Paths.LegacyData, ".spotify_oauth.json") })
        {
            try
            {
                if (!File.Exists(file)) continue;
                using var doc = JsonDocument.Parse(File.ReadAllText(file));
                if (Get(RefreshToken).Length == 0 &&
                    doc.RootElement.TryGetProperty("refresh_token", out var rt) && rt.GetString() is { Length: > 0 } token)
                    Set(RefreshToken, token);
                if (Get(RefreshToken).Length > 0) File.Delete(file);
            }
            catch (Exception) { /* leave it */ }
        }
    }
}

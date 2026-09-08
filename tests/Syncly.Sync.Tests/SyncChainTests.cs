using System.Security.Cryptography;
using Syncly.Security;
using Syncly.Sync;

namespace Syncly.Sync.Tests;

public class SyncChainTests
{
    [Fact]
    public void Round_trips_24_words_and_uri()
    {
        var chain = SyncChain.Create();
        Assert.Equal(24, chain.Words.Split(' ').Length);

        var fromWords = SyncChain.Parse(chain.Words);
        Assert.Equal(chain.ChainId, fromWords.ChainId);
        Assert.Equal(chain.Uri, fromWords.Uri);

        var fromUri = SyncChain.Parse(chain.Uri);
        Assert.Equal(chain.Secret, fromUri.Secret);
    }

    [Fact]
    public void Rejects_a_typo_in_the_checksum()
    {
        var words = SyncChain.Create().Words.Split(' ');
        var original = words[^1];
        // The last word carries only an 8-bit checksum, so a single fixed replacement
        // (e.g. "zoo") is valid about once in 256 random phrases. Search for a typo
        // that actually breaks validation.
        string? broken = null;
        foreach (var candidate in Bip39.WordList)
        {
            if (candidate == original)
                continue;

            words[^1] = candidate;
            var phrase = string.Join(' ', words);
            if (!Bip39.TryDecode(phrase, out _))
            {
                broken = phrase;
                break;
            }
        }

        Assert.NotNull(broken);
        var ex = Assert.Throws<FormatException>(() => Bip39.Decode(broken));
        Assert.Contains("checksum", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Rejects_an_unknown_word()
    {
        var words = SyncChain.Create().Words.Split(' ').ToList();
        words[0] = "notaword";
        Assert.Throws<FormatException>(() => Bip39.Decode(string.Join(' ', words)));
    }

    [Fact]
    public void Vault_round_trip_preserves_the_secret()
    {
        var chain = SyncChain.Create();
        var restored = SyncChain.FromVault(chain.ToVault());
        Assert.Equal(chain.Secret, restored.Secret);
    }
}

public class SyncInviteTests
{
    [Fact]
    public void Invite_round_trips_chain_and_proton_mailbox()
    {
        var chain = SyncChain.Create();
        var prefs = new Syncly.Model.SyncPreferences
        {
            Backend = Syncly.Model.SyncBackendKind.Cloud,
            ProtonShareUrl = "https://drive.proton.me/urls/TOKEN#secret12ab",
            ProtonSharePassword = "extra-pass",
        };

        var uri = SyncInvite.From(chain, prefs).ToUri();
        Assert.StartsWith(SyncInvite.UriPrefix, uri);

        var parsed = SyncInvite.Parse(uri);
        Assert.Equal(chain.Uri, parsed.ChainUri);
        Assert.Equal(Syncly.Model.SyncBackendKind.Cloud, parsed.Backend);
        Assert.Equal(prefs.ProtonShareUrl, parsed.ProtonShareUrl);
        Assert.Equal(prefs.ProtonSharePassword, parsed.ProtonSharePassword);

        var applied = new Syncly.Model.SyncPreferences();
        parsed.ApplyTo(applied);
        Assert.Equal(Syncly.Model.SyncBackendKind.Cloud, applied.Backend);
        Assert.Equal(prefs.ProtonShareUrl, applied.ProtonShareUrl);
        Assert.Equal(prefs.ProtonSharePassword, applied.ProtonSharePassword);
    }

    [Fact]
    public void ApplyTo_strips_folder_when_host_does_not_support_it()
    {
        var chain = SyncChain.Create();
        var invite = SyncInvite.From(chain, new Syncly.Model.SyncPreferences
        {
            Backend = Syncly.Model.SyncBackendKind.Folder,
            FolderPath = @"C:\SynclyMailbox",
        });

        var applied = new Syncly.Model.SyncPreferences();
        invite.ApplyTo(applied, supportsLocalFolder: false);
        Assert.Equal(Syncly.Model.SyncBackendKind.None, applied.Backend);
        Assert.Null(applied.FolderPath);
    }

    [Fact]
    public void Chain_only_uri_is_not_an_invite()
    {
        Assert.False(SyncInvite.TryParse(SyncChain.Create().Uri, out _));
    }
}

public class DeviceIdentityTests
{
    private sealed class MemoryVault : IKeyVault
    {
        private readonly Dictionary<string, string> _values = new(StringComparer.Ordinal);

        public Task<string?> ReadAsync(string key, CancellationToken ct = default) =>
            Task.FromResult(_values.TryGetValue(key, out var value) ? value : null);

        public Task WriteAsync(string key, string value, CancellationToken ct = default)
        {
            _values[key] = value;
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task Rename_persists_in_the_vault()
    {
        var vault = new MemoryVault();
        var identity = await DeviceIdentity.LoadOrCreateAsync(vault, "Phone");
        await identity.RenameAsync(vault, "Kitchen tablet");

        Assert.Equal("Kitchen tablet", identity.DisplayName);

        identity.Dispose();
        var reloaded = await DeviceIdentity.LoadOrCreateAsync(vault, "ignored");
        Assert.Equal("Kitchen tablet", reloaded.DisplayName);
        reloaded.Dispose();
    }
}

public class BlobCipherTests
{
    [Fact]
    public void Encrypt_then_decrypt_recovers_the_pack()
    {
        var chain = SyncChain.Create();
        var payload = "hello mailbox"u8.ToArray();
        var blob = BlobCipher.Encrypt(chain, payload);
        Assert.True(BlobCipher.LooksLikePack(blob));
        Assert.Equal(payload, BlobCipher.Decrypt(chain, blob));
    }

    [Fact]
    public void Wrong_chain_cannot_read_the_blob()
    {
        var blob = BlobCipher.Encrypt(SyncChain.Create(), "secret"u8.ToArray());
        Assert.ThrowsAny<CryptographicException>(() => BlobCipher.Decrypt(SyncChain.Create(), blob));
    }

    [Fact]
    public void Device_pack_round_trips_through_the_chain()
    {
        var chain = SyncChain.Create();
        var pack = new DevicePack("abc", "Phone", "abc=3", []);
        var opened = DevicePackCodec.Open(DevicePackCodec.Seal(pack, chain), chain);
        Assert.Equal(pack.DeviceId, opened.DeviceId);
        Assert.Equal(pack.DisplayName, opened.DisplayName);
        Assert.Equal(pack.Version, opened.Version);
        Assert.Empty(opened.Ops);
    }
}

public class LocalFolderBackendTests
{
    [Fact]
    public async Task Writes_are_visible_to_another_backend_on_the_same_folder()
    {
        var path = Path.Combine(Path.GetTempPath(), "syncly-folder", Guid.NewGuid().ToString("N"));
        var a = new LocalFolderBackend(path);
        var b = new LocalFolderBackend(path);

        await a.WriteAsync("hello.syncly", "abc"u8.ToArray());
        var names = await b.ListAsync();
        Assert.Contains("hello.syncly", names);
        Assert.Equal("abc"u8.ToArray(), await b.ReadAsync("hello.syncly"));
    }
}

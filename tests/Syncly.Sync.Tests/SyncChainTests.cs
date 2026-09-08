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
        words[23] = words[23] == "zoo" ? "zone" : "zoo";
        Assert.Throws<FormatException>(() => Bip39.Decode(string.Join(' ', words)));
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

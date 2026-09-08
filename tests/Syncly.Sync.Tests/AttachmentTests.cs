using Syncly.App;
using Syncly.Model;
using Syncly.Security;
using Syncly.Sync;

namespace Syncly.Sync.Tests;

public class AttachmentTests
{
    [Theory]
    [InlineData("image/png", FilePreviewKind.Image)]
    [InlineData("video/mp4", FilePreviewKind.Video)]
    [InlineData("application/pdf", FilePreviewKind.Pdf)]
    [InlineData("application/zip", FilePreviewKind.Card)]
    [InlineData(null, FilePreviewKind.Card)]
    public void Preview_kind_follows_mime(string? mime, FilePreviewKind kind) =>
        Assert.Equal(kind, FilePreview.ForMime(mime));

    [Fact]
    public async Task BlobStore_round_trips_plaintext()
    {
        var dir = Path.Combine(Path.GetTempPath(), "syncly-blobs", Guid.NewGuid().ToString("N"));
        var store = new BlobStore(dir);
        var chain = SyncChain.Create();
        var payload = "hello attachment"u8.ToArray();

        await using var input = new MemoryStream(payload);
        await store.PutAsync("fl_test", input, chain);

        var plain = await store.TryGetPlainAsync("fl_test", chain);
        Assert.Equal(payload, plain);
        Assert.Equal(payload.Length, store.LastPutBytes);
    }

    [Fact]
    public async Task BlobStore_rejects_oversized_files()
    {
        var dir = Path.Combine(Path.GetTempPath(), "syncly-blobs", Guid.NewGuid().ToString("N"));
        var store = new BlobStore(dir);
        var chain = SyncChain.Create();
        var oversized = new byte[BlobStore.MaxBytes + 1];

        await using var input = new MemoryStream(oversized);
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => store.PutAsync("fl_big", input, chain));
        Assert.Contains("MB", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Attachments_sync_as_mailbox_sidecars()
    {
        var folder = Path.Combine(Path.GetTempPath(), "syncly-mailbox", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var chain = SyncChain.Create();

        await using var a = await TestNode.CreateAsync("alpha");
        await using var b = await TestNode.CreateAsync("bravo");
        await a.UseMailboxAsync(folder, chain);
        await b.UseMailboxAsync(folder, chain);

        const string fileId = "fl_photo";
        var payload = new byte[] { 1, 2, 3, 4, 5, 9 };

        await using (var stream = new MemoryStream(payload))
            await a.Blobs.PutAsync(fileId, stream, chain);

        await a.AuthorAsync(x =>
        {
            x.CreateObject("page-1", "Shared");
            x.UpsertBlock("page-1", "b-file", null, Crdt.FracIndex.Middle, BlockKind.File);
            x.SetProp("page-1", "b-file", PropKeys.FileId, fileId);
            x.SetProp("page-1", "b-file", PropKeys.Mime, "image/png");
            x.SetProp("page-1", "b-file", PropKeys.FileName, "shot.png");
            x.SetProp("page-1", "b-file", PropKeys.ByteSize, payload.Length.ToString());
        });

        await a.Engine.SyncNowAsync();
        await b.Engine.SyncNowAsync();

        Assert.True(File.Exists(Path.Combine(folder, MailboxFiles.BlobName(fileId))));
        Assert.True(b.Blobs.Has(fileId));

        var plain = await b.Blobs.TryGetPlainAsync(fileId, chain);
        Assert.Equal(payload, plain);

        var block = b.Replica.Snapshot("page-1").Flatten().Single(bl => bl.Id == "b-file");
        Assert.Equal(BlockKind.File, block.Kind);
        Assert.Equal(fileId, block.FileId);
        Assert.Equal("image/png", block.Mime);
    }

    [Fact]
    public async Task Attach_requires_a_sync_chain()
    {
        await using var app = await SynclyApp.StartAsync(new SynclyOptions
        {
            DataDirectory = Path.Combine(Path.GetTempPath(), "syncly-attach", Guid.NewGuid().ToString("N")),
            DisplayName = "test",
        });

        var pageId = await app.Workspace.CreatePageAsync(null, "Notes");
        await using var stream = new MemoryStream([1, 2, 3]);
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => app.Workspace.AttachFileAsync(pageId, stream, "a.bin"));
    }
}

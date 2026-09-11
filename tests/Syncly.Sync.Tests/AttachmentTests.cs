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
    [InlineData("application/pdf", FilePreviewKind.Card)]
    [InlineData("application/zip", FilePreviewKind.Card)]
    [InlineData(null, FilePreviewKind.Card)]
    public void Preview_kind_follows_mime(string? mime, FilePreviewKind kind) =>
        Assert.Equal(kind, FilePreview.ForMime(mime));

    [Theory]
    [InlineData("application/pdf", "doc.pdf", "📄")]
    [InlineData("application/zip", "a.zip", "📦")]
    [InlineData("text/plain", "notes.txt", "📝")]
    [InlineData("application/octet-stream", "blob.bin", "📎")]
    [InlineData(null, "file.pdf", "📄")]
    public void Card_icon_follows_mime_or_extension(string? mime, string fileName, string icon) =>
        Assert.Equal(icon, FilePreview.CardIcon(mime, fileName));

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
    public async Task Engine_finds_file_ids_on_file_blocks()
    {
        await using var a = await TestNode.CreateAsync("alpha");
        await a.AuthorAsync(x =>
        {
            x.CreateObject("page-1", "Shared");
            x.UpsertBlock("page-1", "b-file", null, Crdt.FracIndex.Middle, BlockKind.File);
            x.SetProp("page-1", "b-file", PropKeys.FileId, "fl_photo");
        });

        Assert.Equal(["fl_photo"], SyncEngine.ReferencedBlobIds(a.Replica));
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

    [Fact]
    public async Task Replace_file_keeps_the_block_and_swaps_the_blob()
    {
        await using var app = await SynclyApp.StartAsync(new SynclyOptions
        {
            DataDirectory = Path.Combine(Path.GetTempPath(), "syncly-replace", Guid.NewGuid().ToString("N")),
            DisplayName = "test",
        });

        await app.Sync.CreateChainAsync();
        var pageId = await app.Workspace.CreatePageAsync(null, "Notes");

        await using (var first = new MemoryStream("one"u8.ToArray()))
        {
            var blockId = await app.Workspace.AttachFileAsync(pageId, first, "a.txt", "text/plain");
            var original = app.Workspace.Tree(pageId).Find(blockId);
            Assert.NotNull(original);
            var oldFileId = original.FileId;

            await using var second = new MemoryStream("two"u8.ToArray());
            await app.Workspace.ReplaceFileAsync(pageId, blockId, second, "b.txt", "text/plain");

            var replaced = app.Workspace.Tree(pageId).Find(blockId);
            Assert.NotNull(replaced);
            Assert.Equal(BlockKind.File, replaced.Kind);
            Assert.Equal("b.txt", replaced.FileName);
            Assert.Equal("text/plain", replaced.Mime);
            Assert.NotEqual(oldFileId, replaced.FileId);

            var plain = await app.Blobs.TryGetPlainAsync(replaced.FileId!, app.Sync.Chain!);
            Assert.Equal("two"u8.ToArray(), plain);
        }
    }

    [Fact]
    public async Task Delete_block_removes_a_file_from_the_page()
    {
        await using var app = await SynclyApp.StartAsync(new SynclyOptions
        {
            DataDirectory = Path.Combine(Path.GetTempPath(), "syncly-delete-file", Guid.NewGuid().ToString("N")),
            DisplayName = "test",
        });

        await app.Sync.CreateChainAsync();
        var pageId = await app.Workspace.CreatePageAsync(null, "Notes");
        await using var stream = new MemoryStream("keep"u8.ToArray());
        var blockId = await app.Workspace.AttachFileAsync(pageId, stream, "keep.txt", "text/plain");

        await app.Workspace.DeleteBlockAsync(pageId, blockId);

        Assert.Null(app.Workspace.Tree(pageId).Find(blockId));
        Assert.DoesNotContain(app.Workspace.Open(pageId).Flatten(), b => b.Id == blockId);
    }
}

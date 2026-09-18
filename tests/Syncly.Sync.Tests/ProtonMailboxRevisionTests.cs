using System.Text.Json;
using Syncly.Backend.ProtonDrive;

namespace Syncly.Sync.Tests;

public class ProtonMailboxRevisionTests
{
    [Fact]
    public void Draft_conflict_2500_exposes_the_existing_revision_id()
    {
        using var doc = JsonDocument.Parse(
            """
            {
              "Code": 2500,
              "Details": {
                "ConflictLinkID": "link",
                "ConflictRevisionID": null,
                "ConflictDraftRevisionID": "draft-rev",
                "ConflictDraftClientUID": null,
                "RevisionID": "draft-rev"
              },
              "Error": "Draft revision already exists for this link"
            }
            """);

        Assert.True(ProtonMailbox.TryReadDraftRevisionId(doc.RootElement, out var draftId));
        Assert.Equal("draft-rev", draftId);
        Assert.False(ProtonMailbox.TryReadCreatedRevision(doc.RootElement, out _));
    }

    [Fact]
    public void Created_revision_payload_is_not_a_draft_conflict()
    {
        using var doc = JsonDocument.Parse(
            """
            {
              "Code": 1000,
              "Revision": { "ID": "new-rev" }
            }
            """);

        Assert.True(ProtonMailbox.TryReadCreatedRevision(doc.RootElement, out var revisionId));
        Assert.Equal("new-rev", revisionId);
        Assert.False(ProtonMailbox.TryReadDraftRevisionId(doc.RootElement, out _));
    }
}

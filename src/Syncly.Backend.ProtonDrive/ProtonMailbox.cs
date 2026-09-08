using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Org.BouncyCastle.Bcpg.OpenPgp;

namespace Syncly.Backend.ProtonDrive;

/// <summary>
/// After SRP, the share private key unlocks the folder key. File names and contents use that
/// folder key, then each file's own node key, matching Proton Drive's public-link upload.
/// </summary>
internal sealed class ProtonMailbox
{
    private readonly ProtonKeySet _shareKeys;
    private HttpClient? _http;
    private string _token = "";
    private string _volumeId = "";
    private string _rootLinkId = "";
    private ProtonKeySet? _folderKeys;
    private byte[]? _hashKey;
    private readonly Dictionary<string, string> _links = new(StringComparer.Ordinal);

    private ProtonMailbox(ProtonKeySet keys)
    {
        _shareKeys = keys;
    }

    public static ProtonMailbox Unlock(ProtonShareDto share, string urlPassword)
    {
        if (string.IsNullOrWhiteSpace(share.SharePassphrase) || string.IsNullOrWhiteSpace(share.ShareKey))
            throw new ProtonDriveException("Proton Drive did not return a share key for this link.");

        byte[] salt;
        try
        {
            salt = Convert.FromBase64String(share.SharePasswordSalt);
        }
        catch (FormatException ex)
        {
            throw new ProtonDriveException("Proton Drive share salt is not valid.", ex);
        }

        var password = Encoding.UTF8.GetBytes(urlPassword);
        var candidates = KeyPasswordCandidates(password, salt);

        Exception? last = null;
        foreach (var candidate in candidates)
        {
            try
            {
                var passphrase = ProtonPgp.DecryptWithPassword(share.SharePassphrase, candidate);
                var keys = ProtonPgp.UnlockPrivateKey(share.ShareKey, passphrase);
                return new ProtonMailbox(keys);
            }
            catch (Exception ex) when (ex is PgpException or ProtonDriveException or InvalidOperationException)
            {
                last = ex;
            }
        }

        throw new ProtonDriveException(
            "Could not decrypt the Proton Drive share passphrase. Check that the link still has Editor access.",
            last);
    }

    internal static IEnumerable<byte[]> KeyPasswordCandidates(byte[] password, byte[] salt)
    {
        yield return ProtonSrp.DeriveKeyPassphrase(password, salt);

        var salt16 = salt.Length == 16 ? salt : ProtonSrp.BcryptSalt16(salt);
        var full = Org.BouncyCastle.Crypto.Generators.OpenBsdBCrypt.Generate(
            "2y", Encoding.Latin1.GetChars(password), salt16, 10);
        yield return Encoding.ASCII.GetBytes(full);
        yield return password;
    }

    public void Bind(HttpClient http, string token, string uid, string accessToken, string volumeId, string linkId)
    {
        _http = http;
        _token = token;
        _volumeId = volumeId;
        _rootLinkId = linkId;
        _ = uid;
        _ = accessToken;
    }

    public async Task<IReadOnlyList<string>> ListAsync(CancellationToken ct)
    {
        await RefreshAsync(ct);
        return _links.Keys.ToList();
    }

    public async Task<byte[]?> ReadAsync(string name, CancellationToken ct)
    {
        await RefreshAsync(ct);
        if (!_links.TryGetValue(name, out var linkId))
            return null;

        var http = RequireHttp();
        var linkDoc = await http.GetFromJsonAsync<JsonElement>(
            ProtonDriveBackend.DataPath($"v2/volumes/{_volumeId}/links/{linkId}"), cancellationToken: ct);

        if (!TryGetLink(linkDoc, out var node))
            return null;

        if (!TryUnlockFile(node, out _, out var sessionKey))
            throw new ProtonDriveException($"Could not download “{name}” from Proton Drive.");

        if (!TryReadBlockTargets(node, out var blocks))
            blocks = await LoadRevisionBlocksAsync(linkId, ReadActiveRevisionId(node), ct);

        if (blocks.Count == 0)
            throw new ProtonDriveException($"Could not download “{name}” from Proton Drive.");

        using var buffer = new MemoryStream();
        foreach (var (url, token) in blocks)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            if (!string.IsNullOrWhiteSpace(token))
                request.Headers.TryAddWithoutValidation("pm-storage-token", token);

            using var response = await http.SendAsync(request, ct);
            response.EnsureSuccessStatusCode();
            var raw = await response.Content.ReadAsByteArrayAsync(ct);
            await buffer.WriteAsync(DecryptFileBlock(raw, sessionKey), ct);
        }

        return buffer.ToArray();
    }

    public async Task WriteAsync(string name, ReadOnlyMemory<byte> data, CancellationToken ct)
    {
        await RefreshAsync(ct);

        if (_links.TryGetValue(name, out var existing))
        {
            await UploadRevisionAsync(existing, data, ct);
            return;
        }

        var folder = RequireFolder();
        var draft = ProtonPgp.CreateFileDraft(name, _rootLinkId, folder.Keys, folder.HashKey);
        var created = await CreateFileAsync(draft.Body, ct);
        if (created is null)
        {
            await RefreshAsync(ct);
            if (_links.TryGetValue(name, out existing))
            {
                await UploadRevisionAsync(existing, data, ct);
                return;
            }

            throw new ProtonDriveException("Proton Drive refused the upload. Confirm the link has Editor access.");
        }

        try
        {
            await UploadBlocksAndCommitAsync(created.Value.LinkId, created.Value.RevisionId, draft.FileKeys, draft.SessionKey, data, ct);
            _links[name] = created.Value.LinkId;
        }
        catch
        {
            await TryDeleteLinkAsync(created.Value.LinkId, ct);
            throw;
        }
    }

    private async Task<(string LinkId, string RevisionId)?> CreateFileAsync(
        Dictionary<string, object?> body,
        CancellationToken ct)
    {
        var http = RequireHttp();
        var paths = new[]
        {
            ProtonDriveBackend.DataPath($"v2/volumes/{_volumeId}/files"),
            ProtonDriveBackend.DataPath($"v2/volumes/{_volumeId}/folders/{_rootLinkId}/files"),
        };

        string? lastError = null;
        foreach (var path in paths)
        {
            using var response = await http.PostAsJsonAsync(path, body, ct);
            var text = await response.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(text) ? "{}" : text);
            var root = doc.RootElement;
            var code = ReadCode(root);

            if (code is 0 or 1000 && TryReadCreatedFile(root, out var created))
                return created;

            if (code is 2500 or 2501)
                return null;

            lastError = text;
            if (response.IsSuccessStatusCode)
                continue;

            if ((int)response.StatusCode is 404 or 405)
                continue;

            throw new ProtonDriveException(
                "Proton Drive refused the upload. Confirm the link has Editor access. " + text);
        }

        throw new ProtonDriveException(
            "Proton Drive refused the upload. Confirm the link has Editor access. " + lastError);
    }

    private async Task UploadRevisionAsync(string linkId, ReadOnlyMemory<byte> data, CancellationToken ct)
    {
        var http = RequireHttp();
        var linkDoc = await http.GetFromJsonAsync<JsonElement>(
            ProtonDriveBackend.DataPath($"v2/volumes/{_volumeId}/links/{linkId}"), cancellationToken: ct);
        if (!TryGetLink(linkDoc, out var node) || !TryUnlockFile(node, out var fileKeys, out var sessionKey))
            throw new ProtonDriveException("Could not unlock the existing Proton Drive file.");

        var currentRevision = ReadActiveRevisionId(node);
        var body = new Dictionary<string, object?>();
        if (!string.IsNullOrWhiteSpace(currentRevision))
            body["CurrentRevisionID"] = currentRevision;

        using var response = await http.PostAsJsonAsync(
            ProtonDriveBackend.DataPath($"v2/volumes/{_volumeId}/files/{linkId}/revisions"),
            body,
            ct);
        var text = await response.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(text) ? "{}" : text);
        if (!TryReadCreatedRevision(doc.RootElement, out var revisionId))
        {
            throw new ProtonDriveException("Could not update the file on Proton Drive. " + text);
        }

        await UploadBlocksAndCommitAsync(linkId, revisionId, fileKeys, sessionKey, data, ct);
    }

    private async Task UploadBlocksAndCommitAsync(
        string linkId,
        string revisionId,
        ProtonKeySet fileKeys,
        byte[] sessionKey,
        ReadOnlyMemory<byte> data,
        CancellationToken ct)
    {
        var http = RequireHttp();
        var plaintext = data.ToArray();
        var encrypted = ProtonPgp.EncryptWithSessionKey(plaintext, sessionKey);
        var hash = Convert.ToBase64String(SHA256.HashData(encrypted));
        var encSignature = ProtonPgp.EncryptToKey(ProtonPgp.SignDetached(plaintext, fileKeys), fileKeys.EncryptionPublic);
        var verifier = await TryVerificationTokenAsync(linkId, revisionId, encrypted, ct);

        var block = new Dictionary<string, object?>
        {
            ["Index"] = 1,
            ["Size"] = encrypted.Length,
            ["Hash"] = hash,
            ["EncSignature"] = encSignature,
        };
        if (verifier is not null)
            block["Verifier"] = new Dictionary<string, object?> { ["Token"] = Convert.ToBase64String(verifier) };

        var request = new Dictionary<string, object?>
        {
            ["VolumeID"] = _volumeId,
            ["LinkID"] = linkId,
            ["RevisionID"] = revisionId,
            ["BlockList"] = new object[] { block },
            ["ThumbnailList"] = Array.Empty<object>(),
        };

        using var prepare = await http.PostAsJsonAsync(ProtonDriveBackend.DataPath("blocks"), request, ct);
        var prepareText = await prepare.Content.ReadAsStringAsync(ct);
        using var prepareDoc = JsonDocument.Parse(string.IsNullOrWhiteSpace(prepareText) ? "{}" : prepareText);
        if (!TryReadUploadTarget(prepareDoc.RootElement, out var bareUrl, out var token))
            throw new ProtonDriveException("Proton Drive did not accept the file block. " + prepareText);

        await UploadBlockBytesAsync(http, bareUrl, token, encrypted, ct);

        var commit = new Dictionary<string, object?>
        {
            ["State"] = 1,
            ["ManifestSignature"] = ProtonPgp.SignDetachedArmored(SHA256.HashData(encrypted), fileKeys),
            ["BlockList"] = new object[]
            {
                new Dictionary<string, object?> { ["Index"] = 1, ["Token"] = token },
            },
        };

        using var sealedRevision = await http.PutAsJsonAsync(
            ProtonDriveBackend.DataPath($"v2/volumes/{_volumeId}/files/{linkId}/revisions/{revisionId}"),
            commit,
            ct);
        if (!sealedRevision.IsSuccessStatusCode)
        {
            var detail = await sealedRevision.Content.ReadAsStringAsync(ct);
            throw new ProtonDriveException("Proton Drive could not finish the upload. " + detail);
        }
    }

    private async Task<byte[]?> TryVerificationTokenAsync(
        string linkId,
        string revisionId,
        byte[] encrypted,
        CancellationToken ct)
    {
        var http = RequireHttp();
        using var response = await http.GetAsync(
            ProtonDriveBackend.DataPath($"v2/volumes/{_volumeId}/links/{linkId}/revisions/{revisionId}/verification"),
            ct);
        if (!response.IsSuccessStatusCode)
            return null;

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        if (!doc.RootElement.TryGetProperty("VerificationCode", out var codeEl))
            return null;

        var raw = codeEl.GetString();
        if (string.IsNullOrWhiteSpace(raw))
            return null;

        try
        {
            return ProtonPgp.VerificationToken(Convert.FromBase64String(raw), encrypted);
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private static async Task UploadBlockBytesAsync(
        HttpClient http,
        string bareUrl,
        string token,
        byte[] encrypted,
        CancellationToken ct)
    {
        using var raw = new HttpRequestMessage(HttpMethod.Post, bareUrl);
        raw.Headers.TryAddWithoutValidation("pm-storage-token", token);
        raw.Content = new ByteArrayContent(encrypted);
        raw.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        using var rawResponse = await http.SendAsync(raw, ct);
        if (rawResponse.IsSuccessStatusCode)
            return;

        using var multipart = new MultipartFormDataContent();
        var part = new ByteArrayContent(encrypted);
        part.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        multipart.Add(part, "Block", "blob");
        using var form = new HttpRequestMessage(HttpMethod.Post, bareUrl);
        form.Headers.TryAddWithoutValidation("pm-storage-token", token);
        form.Content = multipart;
        using var formResponse = await http.SendAsync(form, ct);
        if (!formResponse.IsSuccessStatusCode)
        {
            var detail = await formResponse.Content.ReadAsStringAsync(ct);
            throw new ProtonDriveException("Proton Drive storage refused the file block. " + detail);
        }
    }

    private async Task TryDeleteLinkAsync(string linkId, CancellationToken ct)
    {
        try
        {
            var http = RequireHttp();
            await http.DeleteAsync(
                ProtonDriveBackend.DataPath($"v2/volumes/{_volumeId}/links/{linkId}"),
                ct);
        }
        catch
        {
            // Best-effort cleanup of a failed draft.
        }
    }

    private async Task RefreshAsync(CancellationToken ct)
    {
        await EnsureFolderAsync(ct);
        var http = RequireHttp();
        var ids = new List<string>();
        string? anchor = null;

        while (true)
        {
            var path = ProtonDriveBackend.DataPath($"v2/volumes/{_volumeId}/folders/{_rootLinkId}/children");
            if (!string.IsNullOrEmpty(anchor))
                path += "?AnchorID=" + Uri.EscapeDataString(anchor);

            using var response = await http.GetAsync(path, ct);
            if (!response.IsSuccessStatusCode)
            {
                using var retry = await http.GetAsync($"urls/{_token}/files", ct);
                if (!retry.IsSuccessStatusCode)
                {
                    var detail = await response.Content.ReadAsStringAsync(ct);
                    throw new ProtonDriveException("Could not list the Proton Drive folder. " + detail);
                }

                await LoadChildrenAsync(retry, ct);
                return;
            }

            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            var root = doc.RootElement;
            if (HasLinkObjects(root))
            {
                ApplyLinks(root);
                return;
            }

            foreach (var id in ReadLinkIds(root))
                ids.Add(id);

            var more = root.TryGetProperty("More", out var moreEl) && moreEl.ValueKind is JsonValueKind.True;
            anchor = root.TryGetProperty("AnchorID", out var anchorEl) ? anchorEl.GetString() : null;
            if (!more || string.IsNullOrEmpty(anchor))
                break;
        }

        _links.Clear();
        if (ids.Count == 0)
            return;

        await LoadLinkDetailsAsync(ids, ct);
    }

    private async Task EnsureFolderAsync(CancellationToken ct)
    {
        if (_folderKeys is not null && _hashKey is not null)
            return;

        var http = RequireHttp();
        var doc = await http.GetFromJsonAsync<JsonElement>(
            ProtonDriveBackend.DataPath($"v2/volumes/{_volumeId}/links/{_rootLinkId}"),
            cancellationToken: ct);

        if (!TryGetLink(doc, out var link))
            throw new ProtonDriveException("Proton Drive did not return the shared folder.");

        var passphrase = ReadString(link, "NodePassphrase");
        var nodeKey = ReadString(link, "NodeKey");
        if (string.IsNullOrWhiteSpace(passphrase) || string.IsNullOrWhiteSpace(nodeKey))
            throw new ProtonDriveException("Proton Drive folder is missing its encryption key.");

        var unlocked = ProtonPgp.DecryptWithPrivateKey(passphrase, _shareKeys);
        _folderKeys = ProtonPgp.UnlockPrivateKey(nodeKey, unlocked);

        var hashArmored = ReadNestedString(link, "FolderProperties", "NodeHashKey")
                          ?? ReadNestedString(link, "Folder", "NodeHashKey")
                          ?? ReadString(link, "NodeHashKey");
        if (string.IsNullOrWhiteSpace(hashArmored))
            throw new ProtonDriveException("Proton Drive folder is missing its name hash key.");

        _hashKey = ProtonPgp.DecryptWithPrivateKey(hashArmored, _folderKeys);
    }

    private async Task LoadLinkDetailsAsync(IReadOnlyList<string> ids, CancellationToken ct)
    {
        var http = RequireHttp();
        const int batch = 150;
        for (var i = 0; i < ids.Count; i += batch)
        {
            var chunk = ids.Skip(i).Take(batch).ToArray();
            using var response = await http.PostAsJsonAsync(
                ProtonDriveBackend.DataPath($"v2/volumes/{_volumeId}/links"),
                new { LinkIDs = chunk },
                ct);

            if (!response.IsSuccessStatusCode)
            {
                var detail = await response.Content.ReadAsStringAsync(ct);
                throw new ProtonDriveException("Could not read Proton Drive folder entries. " + detail);
            }

            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            ApplyLinks(doc.RootElement, clear: i == 0);
        }
    }

    private async Task LoadChildrenAsync(HttpResponseMessage response, CancellationToken ct)
    {
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        ApplyLinks(doc.RootElement);
    }

    private void ApplyLinks(JsonElement root, bool clear = true)
    {
        if (clear)
            _links.Clear();

        foreach (var link in EnumerateLinks(root))
        {
            if (!link.TryGetProperty("LinkID", out var idEl) && !link.TryGetProperty("linkId", out idEl))
                continue;

            var id = idEl.GetString();
            if (string.IsNullOrWhiteSpace(id))
                continue;

            var name = DecryptName(link);
            if (name is null)
                continue;

            _links[name] = id;
        }
    }

    private static bool HasLinkObjects(JsonElement root) =>
        EnumerateLinks(root).Any(link =>
            link.TryGetProperty("Name", out _)
            || (link.TryGetProperty("LinkID", out _) && link.TryGetProperty("MIMEType", out _)));

    private static IEnumerable<string> ReadLinkIds(JsonElement root)
    {
        foreach (var name in new[] { "LinkIDs", "linkIDs", "LinkIds" })
        {
            if (!root.TryGetProperty(name, out var arr) || arr.ValueKind != JsonValueKind.Array)
                continue;

            foreach (var item in arr.EnumerateArray())
            {
                var id = item.ValueKind == JsonValueKind.String ? item.GetString() : null;
                if (!string.IsNullOrWhiteSpace(id))
                    yield return id;
            }

            yield break;
        }
    }

    private string? DecryptName(JsonElement link)
    {
        if (!link.TryGetProperty("Name", out var nameEl))
            return null;

        var raw = nameEl.GetString();
        if (string.IsNullOrWhiteSpace(raw))
            return null;

        if (!raw.Contains("BEGIN PGP", StringComparison.Ordinal))
            return raw;

        foreach (var keys in NameKeys())
        {
            try
            {
                return Encoding.UTF8.GetString(ProtonPgp.DecryptWithPrivateKey(raw, keys));
            }
            catch
            {
                // Try the share key if the folder key is not the recipient.
            }
        }

        return null;
    }

    private IEnumerable<ProtonKeySet> NameKeys()
    {
        if (_folderKeys is not null)
            yield return _folderKeys;
        yield return _shareKeys;
    }

    private async Task<List<(string Url, string? Token)>> LoadRevisionBlocksAsync(
        string linkId,
        string? revisionId,
        CancellationToken ct)
    {
        var blocks = new List<(string Url, string? Token)>();
        if (string.IsNullOrWhiteSpace(revisionId))
            return blocks;

        var http = RequireHttp();
        var path = ProtonDriveBackend.DataPath(
            $"v2/volumes/{_volumeId}/files/{linkId}/revisions/{revisionId}?FromBlockIndex=1&PageSize=50");
        using var response = await http.GetAsync(path, ct);
        if (!response.IsSuccessStatusCode)
            return blocks;

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        if (doc.RootElement.TryGetProperty("Revision", out var revision)
            && revision.TryGetProperty("Blocks", out var blockEl)
            && blockEl.ValueKind == JsonValueKind.Array)
            ReadBlockTargets(blockEl, blocks);

        return blocks;
    }

    private static bool TryReadBlockTargets(JsonElement node, out List<(string Url, string? Token)> blocks)
    {
        blocks = [];
        if (!TryGetFileProperties(node, out var file))
            return false;

        if (file.TryGetProperty("ActiveRevision", out var revision)
            && revision.TryGetProperty("Blocks", out var blockEl)
            && blockEl.ValueKind == JsonValueKind.Array)
            return ReadBlockTargets(blockEl, blocks);

        return false;
    }

    private bool TryUnlockFile(JsonElement node, out ProtonKeySet fileKeys, out byte[] sessionKey)
    {
        fileKeys = _shareKeys;
        sessionKey = [];

        var passphrase = ReadString(node, "NodePassphrase");
        var nodeKey = ReadString(node, "NodeKey");
        if (string.IsNullOrWhiteSpace(passphrase) || string.IsNullOrWhiteSpace(nodeKey))
            return false;

        var parent = _folderKeys ?? _shareKeys;
        byte[] unlocked;
        try
        {
            unlocked = ProtonPgp.DecryptWithPrivateKey(passphrase, parent);
        }
        catch
        {
            return false;
        }

        try
        {
            fileKeys = ProtonPgp.UnlockPrivateKey(nodeKey, unlocked);
        }
        catch
        {
            return false;
        }

        if (!TryGetFileProperties(node, out var file))
            return false;

        var packet = ReadString(file, "ContentKeyPacket");
        if (string.IsNullOrWhiteSpace(packet))
            return false;

        try
        {
            sessionKey = ProtonPgp.DecryptSessionKey(Convert.FromBase64String(packet), fileKeys.EncryptionPrivate);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static byte[] DecryptFileBlock(byte[] raw, byte[] sessionKey)
    {
        try
        {
            return ProtonPgp.DecryptWithSessionKey(raw, sessionKey);
        }
        catch (Exception ex) when (ex is not ProtonDriveException)
        {
            throw new ProtonDriveException("Could not decrypt a Proton Drive file block.", ex);
        }
    }

    private FolderSecrets RequireFolder()
    {
        if (_folderKeys is null || _hashKey is null)
            throw new ProtonDriveException("Proton Drive folder keys are not ready.");

        return new FolderSecrets(_folderKeys, _hashKey);
    }

    private HttpClient RequireHttp() =>
        _http ?? throw new ProtonDriveException("Proton Drive session is not connected.");

    private static bool TryGetLink(JsonElement root, out JsonElement link)
    {
        if (root.TryGetProperty("Link", out link))
            return true;

        if (root.TryGetProperty("link", out link))
            return true;

        link = root;
        return root.ValueKind == JsonValueKind.Object;
    }

    private static IEnumerable<JsonElement> EnumerateLinks(JsonElement root)
    {
        foreach (var name in new[] { "Links", "links", "Files", "files", "Children", "children" })
        {
            if (root.TryGetProperty(name, out var arr) && arr.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in arr.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.Object
                        && item.TryGetProperty("Link", out var nested)
                        && nested.ValueKind == JsonValueKind.Object)
                        yield return nested;
                    else if (item.ValueKind == JsonValueKind.Object)
                        yield return item;
                }

                yield break;
            }
        }

        if (root.TryGetProperty("Link", out var single))
            yield return single;
    }

    private static bool TryGetFileProperties(JsonElement node, out JsonElement file)
    {
        if (node.TryGetProperty("FileProperties", out file) || node.TryGetProperty("File", out file))
            return file.ValueKind == JsonValueKind.Object;

        file = default;
        return false;
    }

    private static bool ReadBlockTargets(JsonElement blocks, List<(string Url, string? Token)> output)
    {
        foreach (var block in blocks.EnumerateArray())
        {
            if (!block.TryGetProperty("BareUrl", out var urlEl)
                && !block.TryGetProperty("BareURL", out urlEl)
                && !block.TryGetProperty("URL", out urlEl)
                && !block.TryGetProperty("Url", out urlEl))
                continue;

            var url = urlEl.GetString();
            if (string.IsNullOrWhiteSpace(url))
                continue;

            var token = ReadString(block, "Token");
            output.Add((url, token));
        }

        return output.Count > 0;
    }

    private static bool TryReadCreatedFile(JsonElement root, out (string LinkId, string RevisionId) created)
    {
        created = default;
        if (!root.TryGetProperty("File", out var file) && !root.TryGetProperty("file", out file))
            return false;

        var linkId = ReadString(file, "ID") ?? ReadString(file, "LinkID");
        var revisionId = ReadString(file, "RevisionID") ?? ReadString(file, "RevisionId");
        if (string.IsNullOrWhiteSpace(linkId) || string.IsNullOrWhiteSpace(revisionId))
            return false;

        created = (linkId, revisionId);
        return true;
    }

    private static bool TryReadCreatedRevision(JsonElement root, out string revisionId)
    {
        revisionId = "";
        if (root.TryGetProperty("Revision", out var revision))
        {
            revisionId = ReadString(revision, "ID") ?? ReadString(revision, "RevisionID") ?? "";
            return !string.IsNullOrWhiteSpace(revisionId);
        }

        revisionId = ReadString(root, "ID") ?? ReadString(root, "RevisionID") ?? "";
        return !string.IsNullOrWhiteSpace(revisionId);
    }

    private static bool TryReadUploadTarget(JsonElement root, out string bareUrl, out string token)
    {
        bareUrl = "";
        token = "";
        if (!root.TryGetProperty("UploadLinks", out var links) || links.ValueKind != JsonValueKind.Array)
            return false;

        foreach (var link in links.EnumerateArray())
        {
            bareUrl = ReadString(link, "BareURL") ?? ReadString(link, "BareUrl") ?? "";
            token = ReadString(link, "Token") ?? "";
            if (!string.IsNullOrWhiteSpace(bareUrl) && !string.IsNullOrWhiteSpace(token))
                return true;
        }

        return false;
    }

    private static string? ReadActiveRevisionId(JsonElement node)
    {
        if (!TryGetFileProperties(node, out var file))
            return null;

        if (file.TryGetProperty("ActiveRevision", out var revision))
            return ReadString(revision, "ID")
                   ?? ReadString(revision, "RevisionID")
                   ?? ReadString(revision, "ActiveRevisionID");

        return ReadString(file, "ActiveRevisionID");
    }

    private static int ReadCode(JsonElement root) =>
        root.TryGetProperty("Code", out var code) && code.TryGetInt32(out var value) ? value : -1;

    private static string? ReadString(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var el) ? el.GetString() : null;

    private static string? ReadNestedString(JsonElement obj, string parent, string name)
    {
        if (!obj.TryGetProperty(parent, out var child) || child.ValueKind != JsonValueKind.Object)
            return null;

        return ReadString(child, name);
    }

    private readonly record struct FolderSecrets(ProtonKeySet Keys, byte[] HashKey);
}

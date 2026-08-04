using System.ComponentModel;
using System.Text.Json;
using BeeMemoryBank.Core.Services;
using ModelContextProtocol.Server;

namespace BeeMemoryBank.Api.McpTools;

[McpServerToolType]
public class BeeUploadTools(
    ArticleService articleService,
    MediaService mediaService,
    SessionService session,
    McpResponseManager responseManager)
{
    private const long MaxUploadBytes = 20 * 1024 * 1024;

    private static readonly Dictionary<string, string> ExtensionToContentType = new(StringComparer.OrdinalIgnoreCase)
    {
        [".png"] = "image/png", [".jpg"] = "image/jpeg", [".jpeg"] = "image/jpeg",
        [".gif"] = "image/gif", [".webp"] = "image/webp", [".svg"] = "image/svg+xml"
    };

    [McpServerTool(Name = "bee_get_upload_script")]
    [Description(
        "Get a Python script for uploading files from disk to BeeMemoryBank without reading them into context.\n" +
        "The script talks to the MCP endpoint directly via JSON-RPC — no REST API access required.\n" +
        "Call once, save the script to disk, remember the path. Never call again.\n" +
        "The script uses only Python stdlib (no pip install needed). Also supports uploading images " +
        "(upload-media command) as an unlinked media record you can then reference from an article.")]
    public string GetUploadScript()
    {
        return UploadScript;
    }

    [McpServerTool(Name = "bee_save_media")]
    [Description(
        "Upload a new image into the vault from base64-encoded bytes. Supported types: PNG, JPEG, GIF, " +
        "WEBP, SVG (by file extension in fileName). Max 20 MB decoded size.\n" +
        "Returns JSON: { mediaId }. Paste \"![](/api/media/{mediaId})\" into an article's markdown to embed it.\n" +
        "Optional articleId links the upload to that article immediately (subject to the same access " +
        "rules as other tools); omit it to create an unlinked upload that is deleted automatically after " +
        "24 hours if never referenced in a saved article body.\n" +
        "For large images or when avoiding base64 in your own context matters, prefer the script from " +
        "bee_get_upload_script instead (its upload-media command does the base64 encoding locally, outside your context).")]
    public async Task<string> SaveMedia(
        [Description("File name, used only to determine the image type by extension (.png/.jpg/.jpeg/.gif/.webp/.svg). Not used as a display name.")] string fileName,
        [Description("Base64-encoded file content. A \"data:image/...;base64,\" prefix is tolerated and stripped automatically. Max 20 MB decoded.")] string contentBase64,
        [Description("Optional article ID (GUID) to link this upload to immediately. Omit to upload unlinked; it will be auto-deleted after 24h if never referenced by a saved article.")] Guid? articleId = null)
    {
        if (!session.IsUnlocked)
            return "Error: session is locked. Unlock first.";

        if (contentBase64.StartsWith("data:", StringComparison.Ordinal))
        {
            contentBase64 = contentBase64[(contentBase64.IndexOf(',') + 1)..];
        }

        // Cheap pre-check before allocating a potentially huge byte array: base64 expands ~4:3,
        // so decoded size ≈ encoded length * 3 / 4.
        var estimatedDecodedSize = (long)contentBase64.Length * 3 / 4;
        if (estimatedDecodedSize > MaxUploadBytes)
            return "Error: input exceeds 20 MB limit.";

        byte[] bytes;
        try
        {
            bytes = Convert.FromBase64String(contentBase64);
        }
        catch (FormatException)
        {
            return "Error: contentBase64 is not valid base64.";
        }

        // Precise check — the pre-check above is an estimate; base64 padding can shift it slightly.
        if (bytes.Length > MaxUploadBytes)
            return $"Error: file exceeds 20 MB limit (decoded size {bytes.Length} bytes).";

        var ext = Path.GetExtension(fileName);
        if (!ExtensionToContentType.TryGetValue(ext, out var contentType))
            return $"Error: unsupported file extension '{ext}'. Supported: .png, .jpg, .jpeg, .gif, .webp, .svg";

        if (articleId.HasValue)
        {
            var article = await articleService.GetMetadataAsync(articleId.Value);
            if (article == null)
                return $"Error: article {articleId} not found";
            if (article.Protected)
                return "Error: this article is password-protected (second-layer encryption); agents cannot attach media to it.";
        }

        try
        {
            var media = await mediaService.CreateAsync(fileName, contentType, bytes, articleId);
            var json = JsonSerializer.Serialize(new { mediaId = media.Id });
            return responseManager.ProcessResponse(json);
        }
        catch (ArgumentException ex)
        {
            return $"Error: {ex.Message}";
        }
    }

    private const string UploadScript = """"
# BeeMemoryBank File Upload — uploads files directly from disk, bypassing LLM context.
# Uses the MCP protocol directly (JSON-RPC over HTTP). No REST API access required.
#
# Usage:
#   python bmb-upload.py --url https://bmb.example.com/mcp --bearer bee_xxx \
#       create <file> <title> <treePath> [--tags tag1,tag2]
#   python bmb-upload.py --url https://bmb.example.com/mcp --bearer bee_xxx \
#       update <file> <articleId> [--tags tag1,tag2]
#   python bmb-upload.py --url https://bmb.example.com/mcp --bearer bee_xxx \
#       upload-media <imageFile> [--article-id <articleId>]
#
# --url: the MCP endpoint URL (same one you use in your MCP client config).
# The file content goes straight from disk to the server — never through your context window.
# Requires: Python 3.6+ (stdlib only, no pip install needed).

import json, sys, urllib.request, urllib.error, io, argparse, base64, os

if sys.platform == "win32":
    sys.stdout = io.TextIOWrapper(sys.stdout.buffer, encoding="utf-8", errors="replace")
    sys.stderr = io.TextIOWrapper(sys.stderr.buffer, encoding="utf-8", errors="replace")

def die(msg, code=1):
    print(msg, file=sys.stderr)
    sys.exit(code)

def mcp_post(url, headers, payload):
    """POST a JSON-RPC message. Returns (response_matching_request_id, session_id).
    For notifications (no id in payload) returns (None, session_id)."""
    expected_id = payload.get("id")
    data = json.dumps(payload, ensure_ascii=False).encode("utf-8")
    req = urllib.request.Request(url, data=data, method="POST")
    for k, v in headers.items():
        req.add_header(k, v)
    try:
        with urllib.request.urlopen(req) as resp:
            content_type = (resp.headers.get("Content-Type") or "").lower()
            body = resp.read().decode("utf-8")
            session_id = resp.headers.get("Mcp-Session-Id")
    except urllib.error.HTTPError as e:
        body = e.read().decode("utf-8", errors="replace")
        die(f"HTTP {e.code} from MCP: {body[:500]}")
    except urllib.error.URLError as e:
        die(f"Connection error: {e.reason}")

    candidates = []
    if "text/event-stream" in content_type:
        for line in body.splitlines():
            if line.startswith("data:"):
                chunk = line[5:].lstrip()
                if chunk:
                    try:
                        candidates.append(json.loads(chunk))
                    except Exception:
                        pass
    elif body.strip():
        try:
            candidates.append(json.loads(body))
        except Exception:
            pass

    if expected_id is None:
        return None, session_id

    # Match by id (compare as strings to handle int/string mismatch).
    for c in candidates:
        if "id" in c and str(c.get("id")) == str(expected_id):
            return c, session_id
    # Fallback: any response-shaped message.
    for c in reversed(candidates):
        if "id" in c:
            return c, session_id
    return None, session_id

def mcp_tool_call(mcp_url, bearer, tool_name, arguments):
    headers = {
        "Authorization": f"Bearer {bearer}",
        "Content-Type": "application/json",
        "Accept": "application/json, text/event-stream",
    }

    init_resp, session_id = mcp_post(mcp_url, headers, {
        "jsonrpc": "2.0", "id": 1, "method": "initialize",
        "params": {
            "protocolVersion": "2025-03-26",
            "capabilities": {},
            "clientInfo": {"name": "bmb-upload", "version": "2.0"}
        }
    })
    if init_resp is None:
        die("No response to MCP initialize.")
    if "error" in init_resp:
        err = init_resp["error"]
        msg = err.get("message", str(err)) if isinstance(err, dict) else str(err)
        die(f"MCP init error: {msg}")
    if session_id:
        headers["Mcp-Session-Id"] = session_id

    mcp_post(mcp_url, headers, {"jsonrpc": "2.0", "method": "notifications/initialized"})

    resp, _ = mcp_post(mcp_url, headers, {
        "jsonrpc": "2.0", "id": 2, "method": "tools/call",
        "params": {"name": tool_name, "arguments": arguments}
    })
    return resp

def print_mcp_result(resp):
    if resp is None:
        die("No response from server.")
    if "error" in resp:
        err = resp["error"]
        msg = err.get("message", str(err)) if isinstance(err, dict) else str(err)
        die(f"Error: {msg}")
    result = resp.get("result", {})
    if not isinstance(result, dict):
        print(result); return
    is_error = bool(result.get("isError"))
    out = sys.stderr if is_error else sys.stdout
    for item in result.get("content", []):
        if item.get("type") == "text":
            print(item.get("text", ""), file=out)
    if is_error:
        sys.exit(1)

def main():
    parser = argparse.ArgumentParser(description="BeeMemoryBank file upload via MCP")
    parser.add_argument("--url", required=True, help="MCP endpoint URL (e.g. https://bmb.example.com/mcp)")
    parser.add_argument("--bearer", required=True, help="Agent bearer token (bee_xxx)")
    sub = parser.add_subparsers(dest="action")

    cr = sub.add_parser("create", help="Create new article from file")
    cr.add_argument("file")
    cr.add_argument("title")
    cr.add_argument("treePath")
    cr.add_argument("--tags", default="", dest="tags")

    up = sub.add_parser("update", help="Update article content from file")
    up.add_argument("file")
    up.add_argument("articleId")
    up.add_argument("--tags", default=None, dest="tags",
                    help="Replaces all tags. Omit to keep current. Use '' to clear.")

    md = sub.add_parser("upload-media", help="Upload an image file (base64-encoded locally, never sent through the LLM's context)")
    md.add_argument("file")
    md.add_argument("--article-id", default=None, dest="article_id", help="Optional article ID to link the upload to immediately.")

    args = parser.parse_args()
    if not args.action:
        parser.print_help(); sys.exit(1)

    mcp_url = args.url.rstrip("/")

    if args.action == "upload-media":
        try:
            with open(args.file, "rb") as f:
                data = f.read()
        except OSError as e:
            die(f"Error reading file: {e}")
        encoded = base64.b64encode(data).decode("ascii")
        arguments = {"fileName": os.path.basename(args.file), "contentBase64": encoded}
        if args.article_id:
            arguments["articleId"] = args.article_id
        print_mcp_result(mcp_tool_call(mcp_url, args.bearer, "bee_save_media", arguments))
        return

    try:
        with open(args.file, "r", encoding="utf-8-sig") as f:
            content = f.read()
    except (OSError, UnicodeDecodeError) as e:
        die(f"Error reading file: {e}")

    if args.action == "create":
        tags = [t.strip() for t in args.tags.split(",") if t.strip()] if args.tags else []
        arguments = {"title": args.title, "treePath": args.treePath, "content": content}
        if tags:
            arguments["tags"] = tags
        print_mcp_result(mcp_tool_call(mcp_url, args.bearer, "bee_save_article", arguments))
    elif args.action == "update":
        arguments = {"id": args.articleId, "content": content}
        if args.tags is not None:
            arguments["tags"] = [t.strip() for t in args.tags.split(",") if t.strip()]
        print_mcp_result(mcp_tool_call(mcp_url, args.bearer, "bee_update_article", arguments))

if __name__ == "__main__":
    main()
"""";
}

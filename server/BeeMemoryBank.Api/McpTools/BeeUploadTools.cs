using System.ComponentModel;
using ModelContextProtocol.Server;

namespace BeeMemoryBank.Api.McpTools;

[McpServerToolType]
public class BeeUploadTools
{
    [McpServerTool(Name = "bee_get_upload_script")]
    [Description(
        "Get a Python script for uploading files from disk to BeeMemoryBank without reading them into context.\n" +
        "Call once, save the script to disk, remember the path. Never call again.\n" +
        "The script uses only Python stdlib (no pip install needed).")]
    public string GetUploadScript()
    {
        return UploadScript;
    }

    private const string UploadScript = """
# BeeMemoryBank File Upload — uploads files directly from disk, bypassing LLM context.
#
# IMPORTANT: --url must be the BASE domain of the server, NOT the MCP endpoint.
# Take the MCP URL you use to connect (e.g. https://bmb.example.com/mcp),
# strip the path, and use only the base: https://bmb.example.com
#
# Usage:
#   python bmb-upload.py --url https://bmb.example.com --bearer bee_xxx create <file> <title> <treePath> [--tags tag1,tag2]
#   python bmb-upload.py --url https://bmb.example.com --bearer bee_xxx update <file> <articleId>
#
# The script appends /api/articles to the base URL automatically.
# Save this script once. Use it whenever you need to upload files from disk.
# The file content goes straight from disk to the server — never through your context window.
# Requires: Python 3.6+ (stdlib only, no pip install needed).

import json, sys, urllib.request, urllib.error, io, os, argparse

if sys.platform == "win32":
    sys.stdout = io.TextIOWrapper(sys.stdout.buffer, encoding="utf-8", errors="replace")
    sys.stderr = io.TextIOWrapper(sys.stderr.buffer, encoding="utf-8", errors="replace")

def api_call(method, url, bearer, payload):
    data = json.dumps(payload, ensure_ascii=False).encode("utf-8")
    req = urllib.request.Request(url, data=data, method=method)
    req.add_header("Authorization", f"Bearer {bearer}")
    req.add_header("Content-Type", "application/json")
    try:
        with urllib.request.urlopen(req) as resp:
            body = resp.read().decode("utf-8")
            print(f"OK ({resp.status}): {body[:500]}")
    except urllib.error.HTTPError as e:
        print(f"HTTP {e.code}: {e.read().decode('utf-8', errors='replace')}", file=sys.stderr)
        sys.exit(1)

def main():
    parser = argparse.ArgumentParser(description="BeeMemoryBank file upload")
    parser.add_argument("--url", required=True, help="BMB server URL (e.g. https://bmb.example.com)")
    parser.add_argument("--bearer", required=True, help="Bearer token (bee_xxx)")
    sub = parser.add_subparsers(dest="action")

    cr = sub.add_parser("create", help="Create new article from file")
    cr.add_argument("file", help="Path to file")
    cr.add_argument("title", help="Article title")
    cr.add_argument("treePath", help="Tree path (e.g. /Docs/Architecture)")
    cr.add_argument("--tags", default="", help="Comma-separated tags")

    up = sub.add_parser("update", help="Update article content from file")
    up.add_argument("file", help="Path to file")
    up.add_argument("articleId", help="Article GUID")

    args = parser.parse_args()
    if not args.action:
        parser.print_help()
        sys.exit(1)

    with open(args.file, "r", encoding="utf-8") as f:
        content = f.read()

    base = args.url.rstrip("/")
    # Resulting URLs:
    #   POST {base}/api/articles           ← create
    #   PUT  {base}/api/articles/{id}      ← update

    if args.action == "create":
        tags = [t.strip() for t in args.tags.split(",") if t.strip()] if args.tags else []
        api_call("POST", f"{base}/api/articles", args.bearer,
                 {"title": args.title, "treePath": args.treePath, "tags": tags, "content": content})

    elif args.action == "update":
        api_call("PUT", f"{base}/api/articles/{args.articleId}", args.bearer,
                 {"content": content})

if __name__ == "__main__":
    main()
""";
}

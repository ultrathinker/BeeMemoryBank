using System.ComponentModel;
using ModelContextProtocol.Server;

namespace BeeMemoryBank.Api.McpTools;

[McpServerToolType]
public class BeeUploadTools
{
    [McpServerTool(Name = "bee_get_upload_script")]
    [Description(
        "Get a Python script for uploading files from disk to BeeMemoryBank without reading them into context.\n" +
        "The script talks to the MCP endpoint directly via JSON-RPC — no REST API access required.\n" +
        "Call once, save the script to disk, remember the path. Never call again.\n" +
        "The script uses only Python stdlib (no pip install needed).")]
    public string GetUploadScript()
    {
        return UploadScript;
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
#
# --url: the MCP endpoint URL (same one you use in your MCP client config).
# The file content goes straight from disk to the server — never through your context window.
# Requires: Python 3.6+ (stdlib only, no pip install needed).

import json, sys, urllib.request, urllib.error, io, argparse

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

    args = parser.parse_args()
    if not args.action:
        parser.print_help(); sys.exit(1)

    try:
        with open(args.file, "r", encoding="utf-8-sig") as f:
            content = f.read()
    except (OSError, UnicodeDecodeError) as e:
        die(f"Error reading file: {e}")

    mcp_url = args.url.rstrip("/")

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

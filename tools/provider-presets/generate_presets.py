#!/usr/bin/env python3
"""Turns a downloaded copy of the models.dev catalogue into the provider presets shipped inside the product.

The product never downloads anything for this: a person (or the release orchestrator) downloads https://models.dev/api.json
(MIT, https://github.com/anomalyco/models.dev), runs this script over it, reviews the diff of the output and commits it.

    python tools/provider-presets/generate_presets.py --catalog api.json
    python tools/provider-presets/generate_presets.py --catalog api.json --check   # only says what would change

What comes from where:
  * overlay.json (this folder, curated by hand) decides WHICH providers are offered, in which order, under which name, at which base URL
    (from the providers' own documentation), with which protocol, wire options and note, and which models are always listed ("pinned").
  * the catalogue adds the chat models it knows for each provider (id, name, context window, vision, tools, reasoning), newest first,
    up to the overlay's maxModels, leaving out what the overlay's excludeModels patterns name; for an aggregator the overlay's "vendors"
    and "perVendor" pick the newest models of each named vendor instead. Where the catalogue has the provider it is also the authority on
    the pinned models: their facts come from it, and a pinned model it marks deprecated or no longer lists is left out with a warning.
    A provider the catalogue does not have keeps its pinned models as written.

Python 3.9+, standard library only. Reads the catalogue as data: nothing in it is executed.
"""
import argparse
import json
import os
import re
import sys
from datetime import date

HERE = os.path.dirname(os.path.abspath(__file__))
REPO = os.path.normpath(os.path.join(HERE, "..", ".."))
DEFAULT_OUT = os.path.join(REPO, "server", "BeeMemoryBank.Api", "Services", "Providers", "provider-presets.json")
DEFAULT_OVERLAY = os.path.join(HERE, "overlay.json")

PROTOCOLS = {"openai-chat", "anthropic-messages", "gemini"}
# The reasoning styles a provider of each protocol may use (ReasoningStyles.For in ProviderModels.cs).
REASONING = {
    "openai-chat": {"openrouter", "openai", "zai", "none"},
    "anthropic-messages": {"anthropic", "none"},
    "gemini": {"gemini", "none"},
}
MAX_TOKENS_FIELDS = {"max_tokens", "max_completion_tokens"}
MODEL_ID = re.compile(r"^\S{1,200}$")


def fail(message):
    print("error: " + message, file=sys.stderr)
    sys.exit(2)


def load_json(path, what):
    try:
        with open(path, encoding="utf-8") as f:
            return json.load(f)
    except (OSError, ValueError) as e:
        fail(f"cannot read the {what} {path}: {e}")


def check_preset(p):
    for field in ("id", "name", "protocol", "baseUrl", "options"):
        if field not in p:
            fail(f"overlay preset {p.get('id', '?')} has no '{field}'")
    if p["protocol"] not in PROTOCOLS:
        fail(f"overlay preset {p['id']}: unknown protocol {p['protocol']}")
    url = p["baseUrl"]
    if not (url.startswith("https://") or (url.startswith("http://") and p.get("local"))):
        fail(f"overlay preset {p['id']}: the base URL must be https:// (http:// only for a local preset)")
    o = p["options"]
    if o.get("reasoning", "none") not in REASONING[p["protocol"]] or o.get("maxTokensField", "max_tokens") not in MAX_TOKENS_FIELDS:
        fail(f"overlay preset {p['id']}: invalid wire options {o}")
    if o.get("imageGeneration") and p["protocol"] != "openai-chat":
        fail(f"overlay preset {p['id']}: image generation is only offered through the openai-chat protocol")


def is_deprecated(entry):
    return str(entry.get("status", "")).lower() == "deprecated"


def as_model(entry):
    """One catalogue model as the preset file names it, or None when it is not a chat model the product can use: it takes text and answers
    with text only (no image, audio or video output, no speech-to-text), and is not deprecated."""
    mid = entry.get("id")
    if not isinstance(mid, str) or not MODEL_ID.match(mid):
        return None
    if is_deprecated(entry):
        return None
    modalities = entry.get("modalities") or {}
    outputs = modalities.get("output") or ["text"]
    if set(outputs) != {"text"}:
        return None
    inputs = modalities.get("input") or ["text"]
    if "text" not in inputs:
        return None
    limit = entry.get("limit") or {}
    context = limit.get("context")
    return {
        "id": mid,
        "name": entry.get("name") if isinstance(entry.get("name"), str) else None,
        "contextWindow": context if isinstance(context, int) and context > 0 else None,
        "vision": "image" in inputs or bool(entry.get("attachment") and "image" in inputs),
        "tools": bool(entry.get("tool_call")),
        "reasoning": bool(entry.get("reasoning")),
        "_date": str(entry.get("last_updated") or entry.get("release_date") or ""),
    }


def catalogue_entry(models, mid):
    """The catalogue's entry for a model id (keyed by id, or found by its id field)."""
    entry = models.get(mid)
    if isinstance(entry, dict):
        return entry
    return next((e for e in models.values() if isinstance(e, dict) and e.get("id") == mid), None)


def pinned_for(preset, models, known_provider):
    """The pinned models of a preset. Where the catalogue has the provider it is the authority: a pinned model it marks deprecated or no
    longer lists is left out (with a warning, so the overlay gets cleaned), and the catalogue's facts (name, context window, vision, tools,
    reasoning) replace the overlay's."""
    pinned = []
    for m in preset.get("pinnedModels", []):
        if not known_provider:
            pinned.append(dict(m))
            continue
        entry = catalogue_entry(models, m["id"])
        if entry is None or is_deprecated(entry):
            why = "is deprecated in" if entry is not None else "is not in"
            print(f"warning: pinned model {m['id']} of preset {preset['id']} {why} the catalogue; left out", file=sys.stderr)
            continue
        facts = as_model(entry)
        merged = dict(m)
        if facts is not None:
            for k in ("name", "contextWindow", "vision", "tools", "reasoning"):
                if facts.get(k) is not None:
                    merged[k] = facts[k]
        pinned.append(merged)
    return pinned


def vendor_of(mid):
    return mid.split("/", 1)[0] if "/" in mid else ""


def models_for(preset, catalog):
    source = preset.get("source")
    provider = catalog.get(source) if source else None
    if source and provider is None:
        print(f"warning: the catalogue has no provider '{source}' (preset {preset['id']}); only its pinned models are listed", file=sys.stderr)
    models = ((provider or {}).get("models") or {})
    pinned = pinned_for(preset, models, provider is not None)
    seen = {m["id"] for m in pinned}
    found = []
    for entry in models.values():
        if not isinstance(entry, dict):
            continue
        m = as_model(entry)
        if m is None or m["id"] in seen:
            continue
        if preset.get("toolsOnly", True) and not m["tools"]:
            continue
        if any(re.search(x, m["id"]) for x in preset.get("excludeModels", [])):
            continue
        found.append(m)
    found.sort(key=lambda m: (m["_date"], m["id"]), reverse=True)

    # An aggregator (OpenRouter) lists hundreds of models: "newest first" would be whatever was added last. With "vendors" the preset
    # takes the newest "perVendor" models of each named vendor ("openai/...", "anthropic/..."), in the overlay's order.
    vendors = preset.get("vendors")
    if vendors:
        per = int(preset.get("perVendor", 1))
        picked = []
        for v in vendors:
            picked += [m for m in found if vendor_of(m["id"]) == v][:per]
        found = picked

    room = max(0, int(preset.get("maxModels", 8)) - len(pinned))
    chosen = pinned + found[:room]
    for m in chosen:
        m.pop("_date", None)
    return chosen


def build(catalog, overlay, catalog_name):
    presets = []
    ids = set()
    for p in overlay.get("presets", []):
        check_preset(p)
        if p["id"] in ids:
            fail(f"overlay preset {p['id']} is listed twice")
        ids.add(p["id"])
        options = {
            "reasoning": p["options"].get("reasoning", "none"),
            "streamUsage": bool(p["options"].get("streamUsage", True)),
            "maxTokensField": p["options"].get("maxTokensField", "max_tokens"),
            "imageGeneration": bool(p["options"].get("imageGeneration", False)),
        }
        if p["protocol"] == "anthropic-messages":
            options["promptCaching"] = bool(p["options"].get("promptCaching", True))
        presets.append({
            "id": p["id"],
            "name": p["name"],
            "protocol": p["protocol"],
            "baseUrl": p["baseUrl"],
            "local": bool(p.get("local")),
            "docs": p.get("docs"),
            "note": p.get("note"),
            "options": options,
            "models": models_for(p, catalog),
        })
    return {
        "format": 1,
        "source": "Generated by tools/provider-presets/generate_presets.py from the models.dev catalogue "
                  "(MIT, https://github.com/anomalyco/models.dev) and tools/provider-presets/overlay.json.",
        "generatedFrom": f"{catalog_name} on {date.today().isoformat()}",
        "presets": presets,
    }


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--catalog", required=True, help="a downloaded copy of https://models.dev/api.json")
    ap.add_argument("--overlay", default=DEFAULT_OVERLAY, help="the curated list (default: overlay.json beside this script)")
    ap.add_argument("--out", default=DEFAULT_OUT, help="the preset file of the product")
    ap.add_argument("--check", action="store_true", help="write nothing; exit 1 when the output would change")
    args = ap.parse_args()

    catalog = load_json(args.catalog, "catalogue")
    if not isinstance(catalog, dict):
        fail("the catalogue is not an object of providers")
    overlay = load_json(args.overlay, "overlay")
    result = build(catalog, overlay, os.path.basename(args.catalog))
    text = json.dumps(result, indent=2, ensure_ascii=True) + "\n"

    old = None
    if os.path.exists(args.out):
        with open(args.out, encoding="utf-8") as f:
            old = f.read()
    # The date line changes on every run; compare without it.
    strip = lambda s: re.sub(r'"generatedFrom": [^\n]*', "", s or "")
    changed = strip(old) != strip(text)
    if args.check:
        print("the presets would change" if changed else "the presets are up to date")
        sys.exit(1 if changed else 0)
    with open(args.out, "w", encoding="utf-8", newline="\n") as f:
        f.write(text)
    print(f"wrote {args.out}: {len(result['presets'])} presets, {sum(len(p['models']) for p in result['presets'])} models")


if __name__ == "__main__":
    main()

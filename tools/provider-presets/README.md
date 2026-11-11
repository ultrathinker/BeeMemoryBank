# Provider presets

The list of model providers offered under **Admin → AI / Chat → Add provider** ships inside the product as
`server/BeeMemoryBank.Api/Services/Providers/provider-presets.json` (an embedded resource). The product never downloads it, at build
time or at run time. This folder makes it:

| File | What it is |
|---|---|
| `overlay.json` | Curated by hand: which providers are offered, in which order, under which name, at which base URL (taken from the provider's own documentation, never from the catalogue), with which protocol, wire options and note (the Z.ai Coding Plan warning lives here), which catalogue models to leave out (`excludeModels`, regular expressions: dated snapshots, speech, image and safety models, other vendors' models a provider resells), and which models are always listed (`pinnedModels`). For an aggregator, `vendors` and `perVendor` name whose models to list. |
| `generate_presets.py` | Reads a downloaded copy of the open [models.dev](https://github.com/anomalyco/models.dev) catalogue (MIT; see `THIRD-PARTY-NOTICES.txt`) and the overlay, and writes the preset file: for each preset with a `source` it adds the chat models the catalogue knows (text in and text out only, tool calling, not deprecated), newest first, up to `maxModels`, after the pinned ones; with `vendors`, the newest `perVendor` models of each named vendor instead. Where the catalogue has the provider it is the authority on the pinned models too: their facts come from it, and a pinned model it marks deprecated or no longer lists is left out with a warning (clean the overlay when you see one). |

## Regenerating

```bash
curl -o /tmp/models-dev-api.json https://models.dev/api.json      # by hand, outside the product
python -I tools/provider-presets/generate_presets.py --catalog /tmp/models-dev-api.json --check   # says whether it would change
python -I tools/provider-presets/generate_presets.py --catalog /tmp/models-dev-api.json
git diff server/BeeMemoryBank.Api/Services/Providers/provider-presets.json                      # review, then commit
```

Python 3.9 or later, standard library only. The catalogue is read as data.

After regenerating, run `ProviderRegistryTests` and `ProviderPresetsTests` (Integration tests): the product drops any entry whose
protocol, base URL or options it does not accept, and the tests pin the two Z.ai endpoints and the local servers.

## Adding a provider

Add an entry to `overlay.json` (`id`, `name`, `source` = the provider's key in the catalogue or `null`, `protocol`, `baseUrl`, `local`,
`docs`, `note`, `options`, `maxModels`, `pinnedModels`) and regenerate. The wire options are described in `docs/ai-providers.md`. A preset
with a protocol the node has no client for is not offered until a client for it is registered.

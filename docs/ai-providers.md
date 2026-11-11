# Model providers: where the AI chat and Dream send their requests

The built-in AI chat and Dream's model step talk to a model through a **provider**: OpenRouter, Z.ai, Anthropic, Google, OpenAI,
DeepSeek, a server on your own network such as Ollama, or any service that speaks the OpenAI chat format, Anthropic's Messages format or
Google's Gemini format at an address you type in.
A superadmin sets them up under **Admin → AI / Chat**. Nothing is sent to a provider until a superadmin has **approved** it, and the
approval names the exact host.

Embeddings (semantic search) are computed locally by the ONNX model and are not affected by any of this.

## What a provider is

| Field | Meaning |
|---|---|
| Name | Shown everywhere a model is named ("model - provider"). |
| Protocol | The wire format: `openai-chat` (OpenAI's `POST {base}/chat/completions`), `anthropic-messages` (Anthropic's `POST {base}/messages`) or `gemini` (Google's `POST {base}/models/{model}:generateContent`); see [the native protocols](#the-native-protocols-anthropic-and-gemini). The API refuses to create a provider with a protocol this node has no client for (a blind node has none). |
| Base URL | Where its endpoints are, e.g. `https://api.z.ai/api/coding/paas/v4`. Stored in one normalized form (lower-case scheme and host, no default port, no trailing slash). |
| Local | The provider runs on this computer or on your own network: plain `http://` and loopback/private addresses are allowed for it. |
| Approval | Which host a superadmin approved, by whom and when. |
| Wire options | What differs between providers of the same format (below). |

Each provider has its own **keys** (several per provider, with the priority, enable switch, cooldown and last-error display the chat
always had) and its own **models** (text / vision / image generation, context window). A local provider with no key is called without
an `Authorization` header (Ollama, LM Studio and llama.cpp need none).

### The built-in list

Pick a provider from a searchable list or add a custom one. The list ships inside the product
(`server/BeeMemoryBank.Api/Services/Providers/provider-presets.json`, an embedded resource); the product never downloads it. A preset
carries the base URL, the wire options the provider needs, a note to show beside it, and a few known models to tick. **Fetch models**
asks the provider itself (`GET {base}/models`, through the same egress guard, only for an approved provider and only on a click).

| Preset | Base URL | Notes |
|---|---|---|
| OpenRouter | `https://openrouter.ai/api/v1` | Image generation through chat/completions. |
| Z.ai (pay as you go) | `https://api.z.ai/api/paas/v4` | |
| Z.ai Coding Plan | `https://api.z.ai/api/coding/paas/v4` | Shown with: *Z.ai's terms allow the Coding Plan quota only in supported coding tools; using it elsewhere may get the account throttled or frozen.* |
| Anthropic | `https://api.anthropic.com/v1` | Native Messages format (`anthropic-messages`): thinking kept across a tool loop, prompt caching. |
| Google Gemini | `https://generativelanguage.googleapis.com/v1beta` | Native Gemini format (`gemini`): thought signatures kept across a tool loop. |
| OpenAI, DeepSeek, Moonshot, Mistral, Groq, xAI | their `/v1` endpoints | |
| Ollama, LM Studio, llama.cpp server | `http://localhost:11434/v1`, `http://localhost:1234/v1`, `http://localhost:8080/v1` | Local. |

The list is generated from the open [models.dev](https://github.com/anomalyco/models.dev) catalogue (MIT) by
`tools/provider-presets/generate_presets.py`; see `tools/provider-presets/README.md`.

## The approval

Only a superadmin, in a session that is not an agent's, with the vault unlocked, can add a provider, change its base URL, protocol or
local flag, or approve it. The page asks: **"Notes and chat content will be sent to <host>. Approve?"**; the request carries that host and
is refused if the provider points elsewhere by the time it arrives. A new base URL, protocol or local flag **withdraws** the approval
until it is confirmed again; a name or the wire options do not. Adding, approving, re-addressing, withdrawing and deleting a provider
write an audit line.

The approval is a stamp - the protocol, the normalized base URL and the local flag - **sealed under the node's chat key** (AES-256-GCM, the
provider's id in the AAD). A provider is approved only while that stamp opens and says exactly what the provider row says now. So
somebody who can write `chat.db` but does not hold the vault password can neither approve a provider nor point an approved one at
another host. While the vault is locked an approval cannot be checked; nothing is sent then anyway.

The **built-in OpenRouter provider** of a node that used the chat before (below) needs no stamp: its address is the one 2.9.0 had
compiled in, it cannot be changed, and a row that says anything else is not approved.

Agents (agent keys, MCP) can neither read nor change providers or keys: every provider and key route is superadmin-only and refuses an
agent, even one owned by a superadmin.

## The egress guard

Every request to every provider - whatever its protocol - leaves through one class, `ProviderEgress`, which refuses before anything is
sent:

- **HTTPS only**; plain HTTP only for a provider marked local.
- The request goes to the **approved host and port** and nowhere else. A key is only ever put on a request for its own provider's
  approved address (a provider key is sealed with its provider's id inside it, so a key row moved to another provider does not open).
- **No redirect** is followed: a 3xx is an answer like any other error. A redirect would re-send the whole conversation, decrypted note
  text included, wherever it pointed.
- **No system proxy**: a proxy would resolve and connect on the node's behalf, and the address check would judge the proxy.
- For a provider **not** marked local the socket is opened **only to a public address**, judged where the socket is opened, on the
  address the name resolved to (`PeerSocketConnector` with `HostAddressPolicy.IsPublic`): loopback, private, link-local, cloud-metadata,
  multicast, the shared address space `100.64.0.0/10` (carrier NAT, Tailscale), the documentation and benchmarking ranges, the IETF
  protocol blocks (`192.0.0.0/24`, `2001::/23` with Teredo), IPv6 outside `2000::/3`, and the IPv4-in-IPv6 spellings of all of those
  (IPv4-mapped, NAT64 `64:ff9b::/96` and `64:ff9b:1::/48`, 6to4) are refused, so DNS rebinding cannot move a request onto your own
  network. The same address literals are refused when such a provider is added. A local provider may use loopback, private and shared
  (`100.64.x.x`, a Tailscale address of a home Ollama) addresses, never link-local or a cloud metadata address.

**Nothing a provider answered is logged.** A failed request is logged with the provider's name, the HTTP status, a short error word
(`invalid_request_error`, `RESOURCE_EXHAUSTED`) and the provider's request id when it is a plain token; never the response body or
its message, and never an answer's text, because a provider can quote what it was sent - decrypted notes, tool results, write
arguments, thinking. This holds for every protocol, the chat and Dream alike.

A request the guard refuses is not a key's fault: the model is skipped (the next one of the fallback chain is tried) and the reason is
shown.

## Wire options

| Option | Values | Used for |
|---|---|---|
| Reasoning effort | OpenAI format: `openrouter` (`reasoning: {effort}`), `openai` (`reasoning_effort`), `zai` (`thinking: {type}`: a low effort turns thinking off), `none`. Anthropic: `anthropic` (`output_config: {effort}`) or `none`. Gemini: `gemini` (`generationConfig.thinkingConfig.thinkingLevel`, sent to Gemini 3 and later models only) or `none` | Dream asks reasoning models to think little. The chat sends none. |
| Stream usage | on / off (OpenAI format) | Whether a streamed answer asks for a final usage chunk (`stream_options.include_usage`). A provider that reports no usage breaks nothing; the token counts stay empty. |
| Output limit field | `max_tokens`, `max_completion_tokens` (OpenAI format) | Dream's output limit (OpenAI's newer models want the second). |
| Image generation | on / off (OpenAI format) | OpenRouter only in this version. Elsewhere a model cannot be marked for image generation, the image-generation default lists no such model, and `generate_image` answers with a clear "not available through this provider". |
| Prompt caching | on / off (Anthropic) | `cache_control: {type: ephemeral}` on the last system block and the last tool, so the chat's system prompt and tool list are read from Anthropic's cache on every round of a tool loop. |

The presets set them; the provider's Edit dialog shows and changes the ones of the provider's protocol, and the API refuses a reasoning style
of another protocol. OpenAI comes with the reasoning effort off, because its non-reasoning models refuse the field; switch it to `openai`
for a provider you use with reasoning models only.

## The native protocols: Anthropic and Gemini

Both clients are written by hand, like the OpenAI one: every request is built in `AnthropicMessagesClient` / `GeminiClient` and leaves through
the egress guard above (no SDK: an SDK brings its own HTTP client, past the guard; `ProviderEgressArchitectureTests` keeps it so). A
provider created with `openai-chat` at Anthropic's or Google's OpenAI-compatible address keeps working as it is.

**Anthropic Messages** (`anthropic-messages`, base `https://api.anthropic.com/v1`): `POST {base}/messages`, `GET {base}/models` (paged by
`after_id`), the key in `x-api-key`, `anthropic-version: 2026-10-21`.

- The leading system messages are the top-level `system` blocks; a system message later in the conversation (the image manifest, a vision
  model's description) stays in place as text of its user turn. Turns of one role in a row are merged; tool calls are `tool_use` blocks,
  the results of one round one user turn of `tool_result` blocks; a call left without a result (the loop paused on a write before it) gets a
  "not run" result, since the API refuses an unanswered `tool_use`. Images are base64 image blocks.
- No `temperature`, `top_p` or `top_k` is ever sent: this API version refuses them on every model, so Dream's temperature 0 is dropped.
  `max_tokens` is required: Dream's limit, else 16000 for a chat round (the thinking of a thinking model included).
- Thinking is adaptive on current models: the chat sends nothing (the model's default), Dream its low effort as `output_config.effort`; no
  `thinking` field is sent (`budget_tokens` and a disabled thinking are refused by current models).
- Usage: the prompt is `input_tokens + cache_creation_input_tokens + cache_read_input_tokens`, the completion `output_tokens`. Stop
  `max_tokens` is the chat's cut-off; `refusal` is said plainly to the person and not retried.
- Errors: the HTTP status as for every provider (401 disables the key, 429 pauses it, 529 "overloaded" and 5xx try the next key, a request
  error such as `invalid_request_error` goes to the chain's next model); an `error`
  event inside the stream counts with the status of its type.

**Gemini** (`gemini`, base `https://generativelanguage.googleapis.com/v1beta`): `POST {base}/models/{model}:generateContent` and
`:streamGenerateContent?alt=sse`, `GET {base}/models` (paged by `pageToken`, only models that support `generateContent`). The key goes in
the `x-goog-api-key` header, **never** in the address (`?key=` would put it into any log that records URLs).

- `systemInstruction`, roles `user` / `model`, text, `inlineData` images, `functionCall` / `functionResponse` parts. Gemini may give a call
  no id: one is made up (`bmb_call_...`), kept in the transcript and never sent to Gemini; a result is matched to its call by name. A call
  without a result gets a "not run" response.
- The tools' JSON schemas are cut down to the subset Gemini accepts (`additionalProperties`, `$schema`, `default` ... left out, a format
  Gemini does not know, such as `uuid`, said in the description instead).
- The chat sends no `generationConfig`; Dream sends `maxOutputTokens`, `temperature` 0 and, on Gemini 3 and later, `thinkingLevel: low`.
- Usage: `promptTokenCount`; the completion is `candidatesTokenCount + thoughtsTokenCount`. `MAX_TOKENS` is the cut-off; `SAFETY`,
  `RECITATION`, `BLOCKLIST`, `PROHIBITED_CONTENT` ... and a prompt blocked by `promptFeedback.blockReason` are said plainly and not retried.
- Errors: `RESOURCE_EXHAUSTED` arrives as 429 (pauses the key); an invalid key arrives as 400 `API_KEY_INVALID` and is treated as 401
  (disables it).

**Thinking across a tool loop.** An Anthropic answer that thought carries `thinking` / `redacted_thinking` blocks with signatures, and
Gemini 3 signs its parts (`thoughtSignature`); both providers refuse the next round of a tool loop if those do not come back unmodified. The
client returns them as the turn's provider state (the turn's content as the provider wrote it), the tool loop keeps it with the assistant
turn, and the next round sends it back verbatim - **only to the same provider and model**. When the fallback chain moves to another model
the state is dropped and the turn rebuilt from its text and tool calls (Gemini 3: the call of the current round is marked with Google's
`skip_thought_signature_validator`, the value for history another model wrote). A turn paused on a write stores the state with its row
(`chat_message.provider_state_ciphertext`, sealed under the chat key and bound to the row's id), so `/confirm` resumes with it. Thinking is
never shown to the person and never sent to the browser.

Not in this version: image generation through either protocol. No preset for Z.ai's Anthropic-format endpoint either: it would need its own
base path, authorization header and API version, which is exactly the special casing this client avoids; it can be tried as a custom
provider.

## The fallback chain

The default text model gets an ordered list of **fallback models**, which may belong to other providers (Admin → AI / Chat, "Fallback
chain of the text model"). For each model the keys of its provider are tried in priority order as before (a 401 disables a key, a 402
or 429 pauses it for five minutes, any other failure tries the next key; so does a provider that does not answer in time - the HTTP
client's timeout while the user is still there, noted on the key as "The provider did not answer in time." without pausing it - whereas
a closed browser or Dream's Stop ends the call and nothing more is tried). A **request error** - 400, 404, 413 or 422: a request the
provider refuses as such (a schema it does not take, a model it does not know, a request too long) - is not a key's: every key would get the
same answer, so no other key of that provider is tried and the key is neither paused, disabled nor noted. (Google's invalid key arrives as 400
`API_KEY_INVALID` and is treated as 401.) When every key of the model's provider failed, a request error came back, or the provider cannot
be called (not approved, no usable key, refused by the egress guard), the **next model of the chain** is tried; when there is none, the
error is shown. A streamed answer
fails over **only before its first word**: once text has reached the user, another stream is not spliced in and the error is shown.
The chat stores which model of which provider answered (`chat_message.model` and `chat_message.provider`) and shows it in the message's
tooltip.

Dream uses its own model (or the chat's default text model) followed by the **same** fallback list. Every attempt, fallback attempts
included, takes a call slot of the run's budget and is charged as before (see [dream.md](dream.md)); an attempt that timed out has an
unknown outcome and is charged its whole upper estimate.

## Storage, upgrade and rollback

Everything lives in `chat.db` (node-local, never synced, never in a snapshot); all schema changes are additive.

- `chat_provider` - the registry (the approval stamp in `approval_ciphertext`, sealed under the chat key).
- `chat_provider_key` - the keys of every provider **except** the built-in OpenRouter one (secret and prefix sealed under the chat key).
- `chat_provider_model` - the models of every provider except the built-in one.
- `chat_settings.fallback_text_model_ids`, `chat_message.provider` - new columns.
- `chat_message.provider_state_ciphertext` (with `_iv`, `_key_v`) - the provider state of a tool-call turn (thinking blocks, thought
  signatures), sealed under the chat key from its first write, bound to the message's id.

**The built-in OpenRouter provider keeps its keys in `chat_api_key` and its models in `chat_model`**, exactly as 2.9.0 wrote them. A node
upgraded from 2.9.0 has the provider "OpenRouter" (approved, `https://openrouter.ai/api/v1`) with all its keys and models and its
defaults, with nobody doing anything: nothing is copied and the upgrade needs no unlocked vault. A node that never used the chat gets no
provider until one is added.

**Rollback to 2.9.0.** 2.9.0 sends every row of `chat_api_key` to openrouter.ai, so the rule is: **no key of any other provider is ever
written to `chat_api_key`** (not even a second OpenRouter account, which is a provider of its own), and a key deleted here is deleted from
whichever table holds it, `chat_api_key` included. Deleting the built-in provider deletes its rows from `chat_api_key` and `chat_model`. A
node rolled back to 2.9.0 therefore sees exactly the OpenRouter keys and models it would have had; a default pointing at another provider's
model is unknown to it and falls back to its oldest model, as 2.9.0 always did for a missing default. 2.9.0 opens the database unchanged
(it ignores the new tables and columns). Going forward again after a rollback, keys and models 2.9.0 added are the built-in provider's.
One thing 2.9.0 does not do over such a database: its offline re-key refuses a `chat.db` with tables it does not list (the new three), so
it stops on its working copy before the live databases are changed; run the re-key with this version or later.

Every new sealed column (`chat_provider.approval_ciphertext`, `chat_provider_key.ciphertext`, `chat_provider_key.key_prefix_ciphertext`,
`chat_message.provider_state_ciphertext`)
is re-sealed by the offline re-key (`libs/BeeMemoryBank.Rekey`, `ChatColumns`) and carried by a DEK rotation like the rest of chat.db (it
is sealed under the chat key, which the rotation re-wraps). A node reset deletes all keys, the built-in provider's and the others'.

## For developers: adding a protocol

The chat and Dream call `IModelProtocolClient` (complete, complete with tools, stream with tools, image generation, list models), picked
by `ModelClientRegistry` from the provider's protocol. A new protocol is one class implementing the interface, sending only through
`ProviderEgress.SendAsync` with the `ProviderEgressTarget` it is given, mapping its answers into the chat's shapes
(`ChatCompleteResponse`, `ToolCompletionResult` with OpenAI-style tool calls, `ImageGenResult`) and throwing `ProviderHttpException` for
an HTTP status (`ProviderRefusalException` for a refusal), returning what its next round needs beyond text and tool calls as
`ToolCompletionResult.ProviderState` (used again only for the same provider and model), and one line in `ApiServices.cs`
(`AddSingleton<IModelProtocolClient, YourClient>()`). The registry then accepts providers with that protocol; nothing else changes.

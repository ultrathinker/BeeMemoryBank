# bmb-seedgen

A command-line tool that seeds a fresh BeeMemoryBank data directory with a synthetic corpus of
articles, folders, and tags — going entirely through the normal service layer
(`InitializationService` → `SessionService` → `FolderService` → `ArticleService`), so every body
is encrypted exactly like real usage. Built to produce a reproducible dataset for large-scale
search benchmarking.

## Usage

```
bmb-seedgen --data-path <dir> --articles <N> --folders <M> [options]
```

| Flag              | Default   | Description                                                                 |
| ----------------- | --------- | --------------------------------------------------------------------------- |
| `--data-path`     | required  | Target data directory (created if missing). **Do not point this at a real vault.** |
| `--articles`      | required  | Number of articles to generate.                                             |
| `--folders`       | required  | Number of leaf folders to generate (depth 3–5; ancestor stubs are created implicitly). |
| `--seed`          | `42`      | Determinism seed.                                                           |
| `--locale`        | `ru,en`   | Comma-separated subset of `en`, `ru`. Assigns each article one locale.      |
| `--password`      | `test1234`| Vault password (used to initialize the node and to unlock).                 |
| `--force`         | off       | Allow seeding onto a directory that already contains a node/folders.        |

### Examples

```bash
# small smoke corpus
bmb-seedgen --data-path ./scratch/seed --articles 500 --folders 30

# full-scale benchmark corpus
bmb-seedgen --data-path ./scratch/big --articles 100000 --folders 2000 --seed 42
```

## What gets generated

- **Folders**: `M` leaf paths, each 3–5 segments deep. A small fixed set of "major" top-level
  categories is sampled with a Zipf (power-law) distribution so a few folders absorb most articles
  while a long tail of small folders remains — realistic, not uniform.
- **Articles**: titles from topic/prose templates; bodies are Zipf-sampled prose (English words
  drawn from the BERT `vocab.txt`; Russian from a curated word list) assembled into paragraphs,
  sentences, headings, and bullet lists. ~85% of bodies are 500 B–4 KB; ~15% are 10–50 KB to
  stress long-body paths.
- **Tags**: 0–6 per article, drawn Zipf-style from a fixed pool of ~200 tag names.
- **Protected articles**: ~1% are wrapped with a fixed passphrase (`seedgen-protected-1234`) so
  later search work can verify protected bodies are excluded from indexing.
- **Locales**: each article is assigned `en` or `ru` (per `--locale`) and its body is drawn from
  the matching word pool.

## Determinism

Given identical `--seed`, `--articles`, `--folders`, and `--locale`, the generator reproduces the
**same content** across runs: same folder paths, same titles, same tags, same body text, same
protected-article selection.

What "content" does and does **not** cover:

- **Covered** (deterministic): folder tree, article titles, tree-path assignment, tags, body
  prose, protected-article flag.
- **Not covered** (encryption-layer randomness, intentionally non-deterministic — same category as
  the article `Id` GUIDs that `ArticleService` itself mints): per-article AES-GCM IVs/DEKs and the
  random salt embedded in each protected article's `BMBENC1` blob. So two runs produce databases
  with identical article counts, titles, tree, tags, and underlying plaintext, but the on-disk
  ciphertext bytes differ. This matches how a real node behaves and is sufficient for benchmark
  reproducibility.

## Idempotency / safety

If `--data-path` already holds an initialised node or any folders, the tool **refuses to run**
unless `--force` is passed (which seeds additively onto the existing vault). This guards against
accidentally reseeding or corrupting a real directory.

## Notes

- The generator does **not** produce embeddings — that is the background processor's job on a real
  node. It only creates articles via `ArticleService`.
- The ONNX model is referenced only because `ConceptTagService` (an `ArticleService` dependency)
  takes an embedding generator; the model file is gitignored and downloaded separately in CI (see
  `AGENTS.md`). The build does not depend on it being present.

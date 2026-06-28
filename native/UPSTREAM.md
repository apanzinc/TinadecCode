# Codex Rust Upstream

TinadecOffice uses Codex Rust as a mature upstream capability source while keeping TinadecCore as the product-level agent runtime and state owner.

## Layering Rule

- Core owns the portable agent framework, runtime state, orchestration, approvals, capability discovery, and tool policy.
- Codex Rust is the preferred upstream implementation source for mature programming-domain primitives such as search, patch, sandbox, shell, watcher, tests, and review formatting.
- Glue adapters translate between stable Core contracts and upstream Codex Rust crates or processes. Rust is an implementation source, not a product layering rule.

## Upstream Source

- Repository: `https://github.com/openai/codex`
- Local checkout used by this workspace: `D:\github\CodeX Rust`
- Codex Rust workspace path: `D:\github\CodeX Rust\codex-rs`
- Current local upstream commit: `14953023471159aaed89f360c0f3da2346cb4bc0`
- Optional vendored subtree path: `native/codex-src`
- Pinning rule: record the exact upstream commit before replacing any stub implementation.

The first native Codex glue integration uses a local path dependency on
`codex-file-search`, keeping the Core-facing JSON contract stable while the
implementation moves to Codex Rust. If the upstream checkout is relocated, update
the path dependency in `native/glue/code-native/Cargo.toml` or vendor the exact
commit with:

```powershell
git subtree add --prefix native/codex-src https://github.com/openai/codex.git <commit-sha> --squash
```

Then replace the implementations under `native/glue/*` incrementally, keeping the public Core contracts stable.

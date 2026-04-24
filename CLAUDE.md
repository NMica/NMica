# CLAUDE.md

Start with `README.md` — it's the source of truth for what this project is, how it's
built, and how to run tests. Everything else you need is there.

## One thing worth flagging

**`external/dotnet-sdk/` is a sparse+shallow submodule of `dotnet/sdk`, not NMica code.**

Its files are compiled into `NMica.dll` via `<Compile Include>` in the csproj so we stay
in sync with upstream. They are read-only reference — do not edit them (changes won't
survive `submodule update`), and don't explore the tree to "understand the project." The
NMica-specific code all lives under `src/NMica/`.

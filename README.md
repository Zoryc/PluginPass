# PluginPass

A Virt-A-Mate **session plugin** that decides which plugins a scene is allowed to load,
and transparently **redirects plugin references to your own builds** — without editing a
single scene file or `.var` package.

The point: keep *one* optimised copy of a plugin instead of babysitting every version a
downloaded scene happens to ask for, and never re-do that work after the next download.

---

## How it works

When a scene or subscene finishes loading, PluginPass walks every atom's plugin list,
matches each plugin url against your rules, and removes or re-points the ones that
match. It runs on the scene-loaded event and does nothing per frame.

All of it is public VaM API — `MVRPluginManager.GetJSON` to see what loaded,
`RemovePluginWithUID` and `CreatePlugin` to act on it.

### Why it can't intercept *before* the load

The honest version, because it shapes what this tool can and cannot do.

VaM compiles plugin scripts at runtime and then runs the result through
`DynamicCSharp.ScriptDomain.SecurityCheckAssembly`, which rejects any assembly
referencing a restricted namespace. **`System.Reflection` is on that list.** The match
is `String.Compare` against `TypeReference.Namespace` — exact, so `System.Reflection`
is banned while `MVR.FileManagementSecure` is fine.

No reflection means no Harmony, and no Harmony means a plugin script cannot patch
`MVRPluginManager.LateRestoreFromJSON` to strip entries before VaM reads them. That is
a ceiling of the plugin sandbox, not an implementation gap.

The consequence: **a blocked plugin is still compiled once, and its `Init()` still
runs**, before it is removed. What you get back is everything after that — `Update`,
coroutines, physics callbacks, its share of every frame for the rest of the session.
For the "I maintain one optimised fork instead of six versions" goal, that is the part
that mattered anyway.

If you want genuine pre-load interception, it has to be a **BepInEx plugin** — a
compiled DLL loaded outside the sandbox, where Harmony is available. That is a
different project, not a setting.

---

## Install

**From the package** — drop `VexMech.PluginPass.<n>.var` into `AddonPackages/`.

**From source** — copy the `Custom` folder over your VaM install; the repository is laid
out exactly like the inside of the package, so it lands in the right place.

```bash
Copy-Item "Custom" "D:\MeshedVR\" -Recurse -Force
```

Either way, add it as a **Session Plugin**
(`User Preferences → Plugins → Session Plugins → Add Plugin`) and save it into your
session plugin preset so it loads on startup.

Rules live in `Saves/PluginData/PluginPass/rules.json`. The right-hand panel is a live
editor for that file — edit, press **Save rules to file**. Changes apply to the next
scene load; **Apply rules to the current scene** applies them retroactively.

---

## Writing VaM plugins

[`docs/VAM_PLUGIN_API.md`](docs/VAM_PLUGIN_API.md) is a full reference for the VaM
plugin API — the sandbox rules that decide whether a plugin loads at all, the lifecycle,
plugin url and storable id formats, UI recipes, SimpleJSON traps, `.var` packaging, and
a generated dump of every public member of 52 core types.

Part 2 is generated straight from `Assembly-CSharp.dll`, so it cannot drift from the
game. Regenerate for a new VaM version with:

```bash
pwsh ./build/gen-api-doc.ps1
```

## Repository layout

```
meta.json                                    package manifest
Custom/Scripts/VexMech/PluginPass/           the plugin, at its in-package path
docs/VAM_PLUGIN_API.md                       VaM plugin API reference
build/build.ps1                              packages the repo into a .var
build/verify.ps1                             compiles + checks against VaM's own rules
build/syntaxcheck/                           C# 6 dialect gate (build-time only)
build/gen-api-doc.ps1                        regenerates docs/VAM_PLUGIN_API.md
build/api-doc-guide.md                       the hand-written half of that doc
build/canary/                                20-line plugin to test if VaM can compile
.github/workflows/build.yml                  CI
```

## Build

```bash
pwsh ./build/build.ps1 -Version 1
```

Writes `dist/VexMech.PluginPass.1.var`. Runs on Windows PowerShell 5.1 and PowerShell 7+.
The version is the integer in the package name — it lives only in the filename, never in
`meta.json`. Version `0` means "development build".

The script refuses to produce a package when `meta.json`'s `contentList` and the files on
disk disagree, in either direction, so a stale manifest is a build failure rather than a
broken package. It also writes zip entries with forward slashes and emits directory
entries, which VaM requires and which `Compress-Archive` does not reliably do.

## Release

CI builds on every push and PR (as version `0`) and uploads the package as a workflow
artifact. To cut a release, push a tag:

```bash
git tag v1 && git push origin v1
```

That builds `VexMech.PluginPass.1.var` and attaches it to a GitHub release. `v2` → `.2`,
and so on. A manual **Run workflow** with an explicit version also works.

Before CI packages anything it runs `build/syntaxcheck`, which binds the sources twice —
once as C# 6, once as latest — and fails on anything that only errors under C# 6. VaM's
runtime compiler (`mcs.dll`) tops out at C# 6, so this catches tuples, pattern matching,
`out var` and friends here rather than as a red line in the VaM message log. It is a
dialect gate, not a full compile: VaM's assemblies cannot be redistributed to CI, so
loading the plugin in VaM remains the authoritative check.

---

## What it touches

VaM has three separate plugin lists. PluginPass is deliberately scoped:

| List | Lives on | Touched? |
| --- | --- | --- |
| **Atom plugins** | each atom's `PluginManager` storable | **yes** |
| **Scene plugins** | the `CoreControl` atom | yes — toggle, on by default |
| **Session plugins** | its own manager, not an atom at all | **never** |

Session plugins are unreachable by design, not by a filter: the sweep walks atoms, and
the session plugin manager isn't on one. That's the same reason loading a scene doesn't
wipe your session plugins.

So if you run Embody as a session plugin and want every scene's bundled copy gone: pick
Embody in the builder, scope **Every version of this package**, action **Block**. Your
session copy is untouched; every atom copy in every scene is removed on load.

The picker only lists what the engine can actually act on — session plugins are left out
so you can't build a rule that silently never fires.

### Cost

One pass per scene load, nothing per frame: one dictionary lookup per atom, and a plugin
list read only for atoms that actually have a plugin manager. With no rules loaded it
does nothing at all. The status panel reports what the last sweep actually read.

---

## Making a rule

You don't write JSON. The left-hand column is a six-step builder:

1. **Rescan scene for plugins** — fills the picker with everything loaded right now
2. **Plugin** — a filterable list, shown as `MacGruber.PostMagic   PostMagic.cs`
3. **Applies to** — every version of this package / this version only / this script file
   anywhere / this exact url
4. **Do what** — block, redirect, or allow (an exception to a broader rule)
5. **Choose my replacement script** — opens VaM's own file browser, filtered to
   `.cs`/`.cslist`/`.dll` under `Custom/Scripts`, so a redirect target is picked, never typed
6. **Add this rule**

Above the button, a live preview spells the rule back in English and tells you **how many
plugins in the current scene it would match** — so you find out a rule is too broad, or
matches nothing, before you commit it.

The right-hand column lists your rules in priority order with enable/disable, move-up and
delete. First match wins, so ordering matters and `allow` rules belong above broad blocks.

Everything is written to `Saves/PluginData/PluginPass/rules.json`, which is still a plain
file you can hand-edit in the Advanced box at the bottom — the format is below.

---

## Rule format

```json
{
  "rules": [
    {
      "enabled": true,
      "name": "every version of this plugin goes to my build",
      "package": "SomeAuthor.SomePlugin",
      "action": "redirect",
      "to": "Me.MyFork.latest:{path}"
    },
    {
      "enabled": true,
      "name": "drop one heavy version everywhere",
      "package": "SomeAuthor.HeavyPlugin.7",
      "action": "block"
    }
  ]
}
```

**First rule that matches wins.**

### `package`

| value | matches |
| --- | --- |
| `Author.Name` | **every version** of that package |
| `Author.Name.7` | that one version only |
| `Author.*` | wildcards — `*` and `?` |
| `"*"` | any package, *and* loose `Custom/Scripts` files |
| omitted | the package is not part of the match |

`Author.Name` matching every version is the whole point — `Author.Name.7`,
`Author.Name.12` and `Author.Name.latest` all reduce to `Author.Name`. Sibling packages
do **not** match: a rule for `Author.Thing` leaves `Author.ThingExtra.1` alone.

### `script`

| value | matches |
| --- | --- |
| `Heavy.cs` | the **file name** only (no slash in the pattern) |
| `*/Custom/Scripts/Old/*.cs` | the **whole path** (any pattern containing `/` or `\`) |
| omitted | any script in the package |

Matching is case-insensitive and `/` and `\` are interchangeable.

### `action`

- `block` — the plugin is dropped.
- `redirect` — the plugin is replaced by `to`.
- `allow` — explicit pass-through. Put an `allow` rule *above* a broad `block` to carve
  out an exception.

### `to` (redirect targets)

A full url (`Me.MyFork.latest:/Custom/Scripts/Me/Fork.cs`) or a loose path
(`/Custom/Scripts/Me/Fork.cs`). Three tokens are substituted from the original url:

| token | example |
| --- | --- |
| `{path}` | `/Custom/Scripts/SomeAuthor/Thing.cs` |
| `{file}` | `Thing.cs` |
| `{package}` | `SomeAuthor.SomePlugin.7` |

So `"to": "Me.MyFork.latest:{path}"` keeps the folder layout and swaps only the package —
handy when your fork is a repackaged copy of the original tree.

A rule must set `package` and/or `script`. A rule with neither would swallow every plugin
in the game, so it is rejected and reported in the status panel.

---

## The one thing that decides whether a redirect is seamless

VaM names a plugin's storable `<slot>_<Namespace.ClassName>` — e.g.
`plugin#0_MacGruber.PostMagic`. The scene's saved plugin settings and every trigger that
points at that plugin are keyed on that exact string.

A redirect removes the original plugin and creates a replacement in the slot it just
freed. VaM hands out the lowest free slot, so the replacement normally lands back on
`plugin#0` — PluginPass does all redirects before any blocks precisely so that stays
true, and tells you in the activity log on the rare occasion it doesn't.

What that buys you:

- **Your fork keeps the same `namespace` + class name** → the storable id is identical,
  so **triggers aimed at that plugin still resolve**. Rename the class and they won't.
  So when you fork a plugin to optimise it, resist renaming the class.
- **Its scene-saved settings do not carry over.** They were restored onto the original
  instance before PluginPass ran, and the replacement starts at its own defaults. If
  your fork needs configuring, configure it in your fork.

Blocking has the matching consequence: orphaned settings and triggers for the blocked
plugin will log warnings on load. That is VaM telling you the truth, not a fault in
PluginPass.

---

## Options

| Toggle | Default | Effect |
| --- | --- | --- |
| PluginPass enabled | on | master switch |
| Keep original if redirect target is missing | on | a typo'd or uninstalled target leaves the scene working instead of silently losing a plugin |
| Log every action to the message log | off | one message per block/redirect |

**List the plugins in this scene** dumps every plugin url currently loaded, with counts —
the quickest way to copy an exact package id into a rule.

---

## Notes and limits

- **Re-saving a scene bakes in the redirect.** PluginPass changes what loads, not what is
  on disk — but if you load a redirected scene and then *save* it, VaM writes out the
  live plugin list, redirect included.
- Rules are applied to *every* plugin manager: atoms, scene plugins and session plugins.
  PluginPass always excludes itself.
- Manually adding a plugin through the UI is a deliberate act and is not filtered.
- Add PluginPass **once**. A second copy re-applies the same rules; same-frame duplicate
  work is guarded, but there is no reason to run two.
- The rules file is the single source of truth — it is not stored in the session preset.

## Verified against

VaM 1.x, `Assembly-CSharp` as shipped in `VaM_Data/Managed`.

`build/verify.ps1` compiles the plugin against a real VaM install and then checks the
emitted assembly the way VaM itself would: no restricted namespace referenced (that is
what rejects `System.Reflection`), and no enum nested inside the `MVRScript` class — the
one construct with no precedent in ~3,900 working plugin files, and the one VaM's
compiler crashed on here. Earlier versions of this check also rejected `out` parameters
and nested types in general; both turned out to be fine in VaM and are no longer flagged.
`build.ps1` runs it before packaging and skips it on CI, where VaM's assemblies aren't
available.

Every VaM API the plugin touches was checked against the shipped assembly,
and the matching engine has a passing test suite (54 cases covering globbing, version
grouping, url splitting and rule selection).

## Licence

MIT. The package declares `CC BY`, which is VaM's nearest equivalent — VaM only accepts
Creative Commons values in `licenseType`, and `CC BY` carries the same terms that matter:
use, modify and redistribute freely, including commercially, with credit.

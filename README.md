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

## Two engines

PluginPass ships in two halves, and the **Settings** tab has a dropdown — *Rules are
applied by* — that decides which one does the work.

| Mode | Behaviour |
| --- | --- |
| **Auto** (default) | Use the BepInEx engine when it is loaded, otherwise the in-game sweep |
| **BepInEx engine only** | Never sweep. If the engine is missing, say so loudly rather than quietly doing half the job |
| **Standard** | Always use the in-game sweep, even if the engine is loaded |

Both read the same `rules.json`. A rule means the same thing either way — there is a
differential test (`engine/tools/parity.ps1`) that drives 4,420 inputs through both
matching implementations and fails the build if they ever disagree.

### The engine (`BepInEx/plugins/PluginPassEngine.dll`)

Three Harmony patches, applied outside the sandbox:

1. **`MVRPluginManager.LateRestoreFromJSON`** — rewrites `jc["plugins"]` before VaM
   reads it. The original plugin is **never compiled, never constructed, never runs a
   frame**. The replacement is simply what the scene asked for all along.
2. **`Atom.RestoreFromLast`** — skipped for slots the engine rewrote, so your build
   keeps its own configuration instead of being hard-defaulted. (See *Scene-saved
   settings* below for why that branch is dangerous.)
3. **`MVRPluginManager.GivePluginNewUID`** — a merge restore renumbers slots *after*
   the rewrite loop. Without this, a swap whose package still needs approval would come
   back under a different slot name, the suppression in (2) would miss it, and the fork
   would be hard-defaulted anyway — which is the common case, because plugin approval
   is granted per package *version*, so a freshly rebuilt fork is always unapproved.

### The in-game sweep (the session plugin)

VaM compiles plugin scripts at runtime and then runs the result through
`DynamicCSharp.ScriptDomain.SecurityCheckAssembly`, which rejects any assembly
referencing a restricted namespace. **`System.Reflection` is on that list.** The match
is `String.Compare` against `TypeReference.Namespace` — exact, so `System.Reflection`
is banned while `MVR.FileManagementSecure` is fine.

No reflection means no Harmony, so a plugin script cannot get in front of the load.
The sweep runs on the scene-loaded event instead, which means **a blocked plugin is
still compiled once and its `Init()` still runs** before it is removed. What you get
back is everything after that — `Update`, coroutines, physics callbacks, its share of
every frame for the rest of the session.

That is a ceiling of the plugin sandbox, not an implementation gap — and it is exactly
why the engine exists. The session plugin remains the rule editor in both modes, and
the fallback whenever the engine is not loaded.

### How they find each other

The sandbox forbids reflection, so the script cannot ask whether the engine assembly is
loaded, reference a type from it, or call into it. The engine therefore plants a
`GameObject` named `PluginPassEngine` with one child whose **name** is a status line:

```
v=1.0.0;rules=3;swap=5;block=0;keep=1;scene=1
```

`GameObject.Find` is ordinary UnityEngine, so that one channel works across the
boundary. The engine plants the object only after its patches actually applied, so
finding it is proof the interception is live — not just that the DLL is present.

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

**The engine (optional, recommended)** — needs BepInEx 5.4 already installed:

```bash
pwsh ./engine/build.ps1 -Deploy
```

That drops `PluginPassEngine.dll` into `BepInEx/plugins/`. Restart VaM; the Settings
tab will show *BepInEx engine: loaded*. Its own options live in
`BepInEx/config/vexmech.pluginpass.engine.cfg`. Without it everything still works —
the session plugin just falls back to sweeping after each scene load.

Rules live in `Saves/PluginData/PluginPass/rules.json`. The right-hand panel is a live
editor for that file — edit, press **Save rules to file**. Changes apply to the next
scene load; **Apply rules to the current scene** applies them retroactively.

---

## Writing VaM plugins

The VaM plugin API reference that was written alongside this plugin - sandbox rules,
lifecycle, load pipeline, UI recipes, packaging, a post-mortem, and a generated dump of
every public member of the core types - lives in its own repository, **VAM_PLUGIN_API**
(a sibling of this one). Its `README.md` is the whole document.
## Repository layout

```
meta.json                                    package manifest
Custom/Scripts/VexMech/PluginPass/           the session plugin, at its in-package path
build/build.ps1                              packages the repo into a .var
build/verify.ps1                             compiles + checks against VaM's own rules
build/syntaxcheck/                           C# 6 dialect gate (build-time only)
build/canary/                                20-line plugin to test if VaM can compile
engine/src/                                  the BepInEx engine (a normal DLL)
engine/build.ps1                             builds it against VaM's own assemblies
engine/tools/verify-targets.ps1              the Harmony targets still exist in VaM
engine/tools/parity.ps1                      engine and script decide identically
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

**The engine is not built by CI and is not inside the `.var`.** It links against VaM's
own `mscorlib 2.0.0.0` and `Assembly-CSharp`, which cannot be redistributed, and a
BepInEx plugin cannot load out of a package anyway. Build it locally with
`engine/build.ps1 -Deploy`. `engine/tools/verify-targets.ps1` and
`engine/tools/parity.ps1` need a real VaM install for the same reason.

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

The id is `Type.ToString()`, so it carries the namespace: a fork declared as
`VexMech.PlotFinder` is `plugin#0_VexMech.PlotFinder`, not `plugin#0_DiviningRod`.

**Keep your fork's `namespace` + class name identical to the original.** The package name
is free — `VexMech.PlotFinder` as a *package* holding a `ToumeiHitsuji.DiviningRod` class
is exactly right. It is the type name that the scene format keys on.

When the names match:

- **Triggers aimed at the plugin resolve.** `TriggerActionDiscrete.Trigger` calls
  `CheckMissingReceiver()` as its first instruction, so triggers re-resolve lazily on
  every fire — a storable registered under the expected id repairs them whenever it
  appears.
When the names **don't** match, triggers aimed at the original go quiet. Nothing is
logged — `CheckMissingReceiver` fails silently, and `Atom.Restore` skips a settings block
whose id matches no storable without a word — so the only symptom is a button in the
scene that stops doing anything.

### Scene-saved settings are not carried into a redirect

By design, and it is the behaviour you want when the replacement is *your* build.

`CreateScriptController` ends with `RegisterAdditionalStorable`, then calls
`Atom.RestoreFromLast` **only if the plugin manager's `insideRestore` flag is set**:

```
IL_031d: ldfld    JSONStorable::insideRestore
IL_0322: brfalse.s IL_0331          ← false ⇒ skip the restore
IL_032c: callvirt Atom::RestoreFromLast(JSONStorable)
```

That flag is set while VaM is loading a scene, and again in
`MVRPluginManager.UserConfirmDenyComplete` when a deferred Allow prompt resumes a load.
It is **not** set when PluginPass creates the replacement in its own sweep — so the
replacement never has the scene's settings applied to it, and comes up on whatever its
own `Init()` establishes. A fork with a defaults profile (Embody calls
`LoadFromDefaults()` at the end of `Init`) therefore comes up on *your* defaults, not on
hard defaults and not on the scene author's.

The one exception: if the replacement package still needs the Allow prompt, the load
resumes through `UserConfirmDenyComplete` with `insideRestore = true`, and a replacement
whose class name matches the original **will** be handed the scene's saved settings that
once. Trust the package (or turn on *Always allow plugins*) to make it deterministic.

A mismatched name has one more consequence in that path: `RestoreFromLast` finds no block
under the new id, and its no-match branch builds an empty `JSONClass` and calls
`RestoreFromJSON(..., setMissingToDefault: true)` — an active wipe to hard defaults,
logged nowhere.

Blocking has the matching consequence: orphaned settings and triggers for the blocked
plugin will log warnings on load. That is VaM telling you the truth, not a fault in
PluginPass.

---

## Options

| Toggle | Default | Effect |
| --- | --- | --- |
| PluginPass enabled | on | master switch |
| Rules are applied by | Auto | engine when loaded, otherwise the in-game sweep — see **Two engines** |
| Also apply to scene plugins | on | include the `CoreControl` atom's plugin list |
| Keep original if redirect target is missing | on | a typo'd or uninstalled target leaves the scene working instead of silently losing a plugin |
| Log every action to the message log | off | one message per block/redirect |

The engine's own settings are in `BepInEx/config/vexmech.pluginpass.engine.cfg`:

| Key | Default | Effect |
| --- | --- | --- |
| `Enabled` | true | false plants no handshake object, so the session plugin falls back |
| `KeepMySettings` | true | skip `RestoreFromLast` on swapped slots — your build keeps its own config |
| `ScenePlugins` | true | also act on the `CoreControl` atom |
| `KeepOriginalIfMissing` | true | as above, for the engine |
| `RulesPath` | *(empty)* | override the rules file location |
| `Verbose` | false | log every suppressed restore |

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

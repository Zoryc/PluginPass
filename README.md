# PluginPass

[![Build](https://github.com/Zoryc/PluginPass/actions/workflows/build.yml/badge.svg)](https://github.com/Zoryc/PluginPass/actions/workflows/build.yml)
[![Release](https://img.shields.io/github/v/release/Zoryc/PluginPass)](https://github.com/Zoryc/PluginPass/releases)

A **Virt-A-Mate session plugin** that swaps or blocks the plugins a scene loads — so downloaded
scenes use **your** optimised builds instead of whatever version they shipped with.

No scene files or `.var` packages are edited. Set a rule once, and every future download follows it.

## Features

- 🔁 **Swap** any plugin for your own build — every version, one version, or a single script
- 🚫 **Block** plugins you never want loaded
- ✅ **Allow** exceptions to a broader rule
- 🧩 **Point-and-click rule builder** — no JSON required
- ⚡ **Optional BepInEx engine** — swaps plugins *before* VaM compiles them
- 📦 Follows `.latest`, so your rules keep working as you rebuild your forks

## How it works

PluginPass comes in two parts. Both read the same rules file.

|  | Session plugin | BepInEx engine *(optional)* |
| --- | --- | --- |
| Runs | after the scene loads | before VaM compiles anything |
| Replaced plugin | compiles once, then is removed | never compiles at all |
| Your fork's settings | its own | its own |
| Install | `.var` package | DLL in `BepInEx/plugins` |

The **Settings** tab chooses which one does the work: **Auto** (engine when installed — the
default), **BepInEx engine only**, or **Standard** (session plugin only).

## Install

1. Drop `VexMech.PluginPass.<n>.var` from [Releases](https://github.com/Zoryc/PluginPass/releases) into `AddonPackages/`.
2. In VaM: **User Preferences → Plugins → Session Plugins → Add Plugin**, pick PluginPass.
3. Save your session plugin preset so it loads on startup.

### Optional: the BepInEx engine

Requires [BepInEx 5.4](https://github.com/BepInEx/BepInEx). The engine links against VaM's own
assemblies, so it is built locally rather than shipped:

```powershell
./engine/build.ps1 -VamPath "<your VaM folder>" -Deploy
```

Restart VaM. The Settings tab should say **BepInEx engine: loaded**.

## Making a rule

Open PluginPass with a scene loaded:

1. **Rescan scene for plugins**
2. Pick the **plugin**
3. Choose what it **applies to** — every version, this version, this script, or this exact file
4. Choose the **action** — block, replace, or allow
5. For a replacement, **choose your script** (VaM's file browser, already filtered)
6. **Add this rule**

A live preview shows how many plugins in the current scene the rule would match.
Rules are checked top to bottom, and **the first match wins**.

## Rule format

Rules are saved to `Saves/PluginData/PluginPass/rules.json` and can be edited by hand:

```json
{
  "rules": [
    {
      "name": "Use my build of SomePlugin",
      "package": "SomeAuthor.SomePlugin",
      "action": "redirect",
      "to": "Me.MyFork.latest:/Custom/Scripts/Me/MyFork.cs"
    },
    {
      "name": "Block one heavy version",
      "package": "SomeAuthor.HeavyPlugin.7",
      "action": "block"
    }
  ]
}
```

| Field | Values |
| --- | --- |
| `package` | `Author.Name` (every version) · `Author.Name.7` (one version) · wildcards `*` `?` |
| `script` | `File.cs` (file name) · `*/Folder/*.cs` (whole path) |
| `action` | `block` · `redirect` · `allow` |
| `to` | target url — supports `{path}`, `{file}` and `{package}` from the original |
| `enabled` | `true` / `false` |

A rule needs `package` and/or `script`. Matching is case-insensitive.

## Settings

| Setting | Default | |
| --- | --- | --- |
| PluginPass enabled | on | Master switch |
| Rules are applied by | Auto | Engine when installed, otherwise the session plugin |
| Also apply to scene plugins | on | Include plugins on the `CoreControl` atom |
| Keep original if replacement is missing | on | A missing fork never removes a working plugin |
| Log to VaM's message log | off | One line per swap or block |

Session plugins are **never** touched.

<details>
<summary><b>Engine settings</b> — <code>BepInEx/config/vexmech.pluginpass.engine.cfg</code></summary>

| Key | Default | |
| --- | --- | --- |
| `Enabled` | `true` | `false` hands everything back to the session plugin |
| `KeepMySettings` | `true` | Your fork keeps its own settings instead of being reset |
| `ScenePlugins` | `true` | Also act on the `CoreControl` atom |
| `KeepOriginalIfMissing` | `true` | Same as the in-game setting |
| `RulesPath` | *(empty)* | Use a different rules file |
| `Verbose` | `false` | Extra logging |

</details>

## Good to know

- **Triggers follow the class name.** VaM saves plugins as `plugin#0_Namespace.ClassName`.
  If your fork's class name differs from the original's, scene buttons and triggers aimed at
  the original will silently stop working. Settings are unaffected — your fork keeps its own.
- **Re-saving a scene keeps the swap.** VaM saves what is loaded, so a redirected scene saved
  again will reference your build.
- **Approve new fork versions.** VaM asks to allow plugins once per package *version*. Without
  the engine, an unapproved fork can come up on default settings the first time — approve it in
  the Package Manager after each rebuild.

<details>
<summary><b>Technical details</b></summary>

**Why two engines?** VaM's plugin sandbox rejects any script that references
`System.Reflection`, so a plugin script cannot use Harmony to get in front of the load. The
session plugin therefore acts after the scene loads. The engine is a normal BepInEx DLL with
three Harmony patches:

| Patch | Purpose |
| --- | --- |
| `MVRPluginManager.LateRestoreFromJSON` | Rewrites the plugin list before VaM reads it |
| `Atom.RestoreFromLast` | Stops VaM resetting a swapped plugin to hard defaults |
| `MVRPluginManager.GivePluginNewUID` | Keeps track of a swap when a merge load renumbers slots |

**How they talk.** The sandbox allows no shared types, so the engine creates a GameObject
named `PluginPassEngine` whose child's name is a status line (`v=1.0.0;rules=3;swap=5;…`).
The session plugin reads it with `GameObject.Find`.

**Cost.** One pass per scene load, nothing per frame.

</details>

## Building from source

```powershell
./build/build.ps1 -Version 1      # → dist/VexMech.PluginPass.1.var
```

Pushing a tag such as `v1` builds the package and attaches it to a GitHub release.

<details>
<summary><b>Checks and project layout</b></summary>

| Script | Checks |
| --- | --- |
| `build/verify.ps1` | Compiles against VaM and applies VaM's own sandbox rules |
| `build/syntaxcheck/` | Rejects anything newer than C# 6 (runs in CI) |
| `engine/tools/verify-targets.ps1` | The engine's Harmony targets still exist in VaM |
| `engine/tools/parity.ps1` | The engine and session plugin match rules identically |

These need a local VaM install (pass `-VamPath`), since VaM's assemblies can't be
redistributed — CI runs the syntax check only.

```
Custom/Scripts/VexMech/PluginPass/   session plugin
engine/                              BepInEx engine
build/                               packaging and checks
```

</details>

## License

MIT. The package declares `CC BY`, VaM's closest equivalent.

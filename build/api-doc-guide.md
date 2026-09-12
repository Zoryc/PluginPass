# Virt-A-Mate plugin API reference

Everything here was read out of `VaM_Data/Managed/Assembly-CSharp.dll` and
`resources.assets` with Mono.Cecil, or measured against the plugin corpus in
`AddonPackages`. It is not written from memory, and Part 2 is generated, so it
cannot drift from the game.

**Regenerate:** `pwsh ./build/gen-api-doc.ps1` (set `VAM_PATH` if VaM isn't at `D:\MeshedVR`).

### What "complete" means here

- **Part 1** is the hand-written guide: the sandbox rules that decide whether a plugin
  loads at all, lifecycle, url and id formats, UI, files, JSON traps, packaging, and a
  post-mortem of every mistake made writing the first plugin (section 10).
- **Part 2** expands the core types in full - every **constructor**, field, property and
  method that is `public` *or* `protected`, plus nested enums and delegates and the
  interfaces each type implements. Protected members are included because plugins
  subclass `MVRScript`, so its protected surface is part of the contract.
- **The appendix** lists *every* public type in `Assembly-CSharp` by name, grouped by
  namespace. Nothing is hidden - anything not expanded above is one line away.

To expand a type that is only in the appendix, add its name to `$groups` in
`build/gen-api-doc.ps1` and regenerate. That is the intended workflow; the set expanded
by default is the set real plugins actually use, measured in section 8.

---

# Part 1 - The guide

## 1. Read this first: the sandbox

VaM compiles plugin scripts **at runtime** with the Mono compiler in
`VaM_Data/Managed/mcs.dll`, then runs the resulting assembly through
`DynamicCSharp.ScriptDomain.SecurityCheckAssembly`. Both stages can reject code that
a normal C# compiler accepts, and both only report **inside the game**.

### 1.1 C# 6 is the ceiling

`Mono.CSharp.LanguageVersion` in `mcs.dll` stops at `V_6`, and `Default = 6`.

Anything from C# 7 on fails to compile in VaM while working perfectly in your IDE:

| Not available | Use instead |
| --- | --- |
| tuples `(int a, int b)` | a small class, or two calls |
| pattern matching `o is int n` | `is` + cast |
| `out var` | declare the variable first (but see 1.2 - avoid `out` entirely) |
| local functions | a private method |
| `$"interpolation"` | `string.Format` or `+` |
| `?.` null-conditional | explicit `if` |
| `nameof` | a string literal |
| expression-bodied members | a normal body |

### 1.2 Restricted namespaces

`DynamicCSharp.Security.NamespaceRestriction.Verify` walks
`ModuleDefinition.GetTypeReferences()` and compares `TypeReference.Namespace` with
`String.Compare` - **an exact match, not a prefix**. So `MVR.FileManagement` is banned
but `MVR.FileManagementSecure` is fine.

Banned namespaces, with their per-type exceptions:

| Namespace | Exceptions |
| --- | --- |
| `System.IO` | `System.IO.Stream`, `System.IO.IOException` |
| `System.Reflection` | none |
| `System.Runtime` | none |
| `System.Runtime.InteropServices` | `GuidAttribute`, `ComVisibleAttribute` |
| `System.Security` | none |
| `System.Configuration` | none |
| `System.Data` | none |
| `System.Deployment` | none |
| `System.Dynamic` | none |
| `System.Management` | none |
| `System.Resources` | none |
| `System.Net.Mail` | none |
| `MVR.FileManagement` | none |
| `DynamicCSharp` | none |
| `Battlehub.RTSaveLoad` (and sub-namespaces) | none |
| `ZenFulcrum.EmbeddedBrowser` | none |

`DynamicCSharp.Security.AssemblyChecker` additionally rejects **unmanaged DLL
references** (P/Invoke). Inclusive (whitelist) mode exists in the enum but throws
"Inclusive security checking is not implemented" - only the blacklist is live.

Three consequences that are easy to walk into:

1. **No reflection means no Harmony.** A plugin script can never patch a VaM method.
   If you need to intercept something before VaM does it, that has to be a **BepInEx
   plugin** - a compiled DLL loaded outside the sandbox. This is a hard architectural
   boundary, not a workaround waiting to be found.

2. **No `out` parameters.** C# compiles an `out` parameter with a
   `System.Runtime.InteropServices.OutAttribute`, which is restricted. Declaring
   `void Split(string s, out string a, out string b)` gets your plugin rejected.
   Calling someone else's `out` method (`dict.TryGetValue(k, out v)`) is fine - the
   attribute lives on their declaration, not your call.

3. **No `System.IO`.** Use `MVR.FileManagementSecure.FileManagerSecure` (Part 2), which
   is the sanctioned wrapper and handles VaM's user-confirm prompts.

### 1.3 Nested types can hard-crash the compiler

On some installs VaM dies with a `mono.dll` access violation
(`mono_class_from_typeref`, null read at `+0x8`) inside
`TypeBuilder.CreateType()` while `Mono.CSharp` closes a **nested** type container.
The game closes; there is no error message.

Symptom in the crash log (`%LOCALAPPDATA%\Temp\MeshedVR\VaM\Crashes\*\output_log.txt`)
- an extra `TypeDefinition:CloseContainer` frame:

```
mono_class_from_typeref
mono_reflection_get_custom_attrs_blob
TypeBuilder:create_runtime_class
TypeDefinition:CloseContainer
TypeContainer:CloseContainer
TypeDefinition:CloseContainer     <- a type inside a type
...
McsDriver:Compile
```

**Keep types flat.** Put helper classes at namespace scope, not nested in your
`MVRScript`. Watch for the ones the compiler generates for you:

- a nested `enum` - use `const int` instead
- iterators (`yield return`) compile to a nested state machine - use
  `Invoke("Method", 0f)` or `InvokeRepeating` when a one-frame delay is all you need
- lambdas that capture more than `this` compile to a `<>c__DisplayClass`; a lambda
  capturing only `this` does not, so `delegate { SomeMethod(); }` is safe

Verify with Mono.Cecil rather than by eye - see `build/verify.ps1` in this repo.

### 1.4 Compiler references

`DynamicCSharp.Settings.assemblyReferences` (in `resources.assets`) is:

```
Assembly-CSharp.dll
UnityEngine.dll + UnityEngine.*Module.dll (Core, UI, UIModule, TextRendering,
IMGUI, Input, Physics, Animation, Audio, JSONSerialize, Video, VR, XR, Timeline,
Terrain, Cloth, AI, AR, Accessibility, ParticleSystem, ImageConversion, Wind,
UnityWebRequest, UIElements)
```

plus whatever mcs references by default (mscorlib, System, System.Core). `System.Xml`
is not referenced. `System.Linq` works and is used by ~1360 of the 3808 plugin files
in the corpus.

### 1.5 `JSONStorable.Type` shadows `System.Type`

`JSONStorable` declares a nested enum called `Type`. Every class deriving from
`MVRScript` inherits it, and inherited member lookup beats namespace lookup, so a bare
`Type` inside your plugin resolves to `JSONStorable.Type`, not `System.Type`:

```csharp
Type t = typeof(Foo);              // error CS0029
System.Type t = typeof(Foo);       // correct - always spell it out
```

A `using Type = System.Type;` alias does **not** fix this; member lookup still wins.

---

## 2. Lifecycle

```csharp
public class MyPlugin : MVRScript
{
    public override void Init() { }     // VaM calls this; build UI here
    private void Start() { }            // Unity, next frame
    private void Update() { }           // Unity, every frame
    public void OnDestroy() { }         // unsubscribe EVERYTHING here
}
```

`Init()` runs **synchronously inside `MVRPluginManager.LateRestoreFromJSON`**, deep in
the scene-restore call stack. That has real consequences:

- The scene is **not** finished loading. Other atoms may not exist yet. If you need a
  loaded scene, hook `SuperController.singleton.onSceneLoadedHandlers`.
- Anything that must not happen on that stack (heavy work, anything reentrant) should
  be deferred with `Invoke("Method", 0f)`.

`JSONStorable` declares `Awake` as `virtual`; `MVRScript` hides it with its own
`protected void Awake()`. Neither declares `OnDestroy`, so you can declare your own
freely.

**Always unsubscribe in `OnDestroy`.** Plugin reload creates a new assembly while the
old one stays in memory; a leaked handler keeps calling into the dead copy and throws
on every scene load for the rest of the session.

```csharp
public void OnDestroy()
{
    SuperController sc = SuperController.singleton;
    if (sc != null)
    {
        sc.onSceneLoadedHandlers -= OnSceneLoaded;
        sc.onSubSceneLoadedHandlers -= OnSubSceneLoaded;
        sc.onAtomAddedHandlers -= OnAtomAdded;
    }
}
```

### Session plugin vs scene plugin

| | scene / atom plugin | session plugin |
| --- | --- | --- |
| `containingAtom` | the atom | null - guard for it |
| lifetime | the scene | the whole VaM session |
| added via | atom's Plugins tab | User Preferences > Plugins > Session Plugins |

---

## 3. Plugin URLs and storable ids

A plugin reference in a scene looks like one of:

```
Author.Package.7:/Custom/Scripts/Author/Thing.cs     versioned package
Author.Package.latest:/Custom/Scripts/Author/Thing.cs
SELF:/Custom/Scripts/Author/Thing.cs                 inside its own package
/Custom/Scripts/Old/Heavy.cs                         loose file, no package
```

`Author.Package` is the *group*; the trailing numeric (or `latest`) segment is the
version. Split on the first `:`; an index of 1 means a Windows drive letter, not a
package.

In the scene JSON they arrive as one block on the atom's `PluginManager` storable:

```json
"plugins": {
  "plugin#0": "SomeAuthor.SomePlugin.7:/Custom/Scripts/SomeAuthor/Thing.cs",
  "plugin#1": "/Custom/Scripts/Old/Heavy.cs"
}
```

### Storable ids

`MVRPluginManager.CreateScriptController` names the script's storable:

```
<pluginUid> + "_" + scriptType.ToString()
```

so `plugin#0_Author.ClassName` - the **namespace-qualified** type name. Every saved
plugin setting and every trigger in the scene is keyed on that exact string. Rename
your class or change the slot, and triggers stop resolving.

### uid allocation

`CreatePluginUID` returns the **lowest free** `plugin#N` (scanning 0..999, checking the
`pluginUIDs` dictionary). `RemovePlugin` frees the uid. So removing `plugin#0` and
immediately calling `CreatePlugin()` gives you `plugin#0` back - but if you freed a
*lower* uid first, the new plugin takes that one instead. Order matters when you are
rewriting several slots.

---

## 4. UI

All of these are `MVRScript` methods; the `rightSide` bool picks the column.

```csharp
JSONStorableBool b = new JSONStorableBool("enabled", true);
b.setCallbackFunction = delegate { Refresh(); };   // param-less delegate matches any signature
RegisterBool(b);                                   // makes it save/load with the scene
CreateToggle(b, false).label = "Enabled";

CreateSlider(new JSONStorableFloat("amount", 1f, 0f, 10f), false);
CreateButton("Do it", false).button.onClick.AddListener(delegate { Go(); });
CreateSpacer(false).height = 15f;
CreateScrollablePopup(chooser, true);
```

`Register*` is what persists a value. A storable you never register is UI-only.

### Multi-line editable text field

`CreateTextField` is read-only. The standard trick:

```csharp
JSONStorableString js = new JSONStorableString("text", "");
UIDynamicTextField field = CreateTextField(js, true);
field.height = 600f;
InputField input = field.gameObject.AddComponent<InputField>();
input.textComponent = field.UItext;
input.lineType = InputField.LineType.MultiLineNewline;
input.targetGraphic = field.backgroundImage;
js.inputField = input;
```

Rich text works in these fields: `<b>`, `<i>`, `<color=#1b5e20>`.

---

## 5. Files

`MVR.FileManagementSecure.FileManagerSecure` is the only file API a plugin may touch.
It understands package paths (`Author.Pkg.3:/Custom/...`) as well as normal ones.

```csharp
if (FileManagerSecure.FileExists(path, false)) {
    string text = FileManagerSecure.ReadAllText(path);
}
FileManagerSecure.CreateDirectory("Saves/PluginData/MyPlugin");
FileManagerSecure.WriteAllText(path, text);   // may prompt the user the first time
```

`MVRScript.SaveJSON(jc, path)` / `LoadJSON(path)` delegate to `SuperController` and are
the easy route for plugin config. Convention for plugin data is
`Saves/PluginData/<YourPlugin>/`.

Writes can throw if the user denies the prompt - always wrap them.

---

## 6. SimpleJSON gotchas

VaM ships a modified SimpleJSON. Two behaviours will bite you:

**1. A missing key returns a lazy creator, not null.** `JSONNode` overloads `==`, so
comparing against `null` works, but only through the operator:

```csharp
JSONNode n = jc["plugins"];
if (n == null) return;                    // correct - the operator handles it
if (ReferenceEquals(n, null)) return;     // WRONG - never true
```

Merely reading a missing key does not insert it; only assignment does.

**2. `AsBool` on a missing key is `false`.** So a missing flag can never default to
true unless you check first:

```csharp
bool enabled = !o.HasKey("enabled") || o["enabled"].AsBool;
```

**3. Snapshot keys before mutating.** Mutating the backing dictionary while enumerating
`Keys` invalidates the enumerator:

```csharp
List<string> keys = new List<string>(obj.Count);
foreach (string k in obj.Keys) keys.Add(k);
for (int i = 0; i < keys.Count; i++) { /* now safe to Remove / assign */ }
```

Parse with `JSONNode.Parse(text)`. `JSONClass` has `HasKey`, `Remove`, `Add`, `Keys`,
`Count`; assigning a `string` to an index works via an implicit conversion.

---

## 7. Packaging a .var

A `.var` is a zip:

- `meta.json` at the **root**
- content beside it, mirroring the VaM folder layout (`Custom/...`, `Saves/...`)
- zip entry names use **forward slashes**, and directory entries are included.
  `Compress-Archive` on Windows PowerShell 5.1 can write `\` separators, which VaM
  cannot read - build the zip explicitly.
- the filename carries the version: `Creator.Package.<N>.var`, N an integer. The
  version is **not** in meta.json.

Fields `MVR.FileManagement.VarPackage.LoadMetaData` actually reads:

```
licenseType (required; "MISSING" if absent)   secondaryLicenseType
EAEndYear / EAEndMonth / EAEndDay             standardReferenceVersionOption
scriptReferenceVersionOption                  description / credits / instructions
promotionalLink / patreonLink                 contentList        dependencies
customOptions                                 hadReferenceIssues
```

`contentList` paths use **backslashes**. Keep it in sync with what you ship - VaM uses
it for the package browser.

`licenseType` accepts: `CC BY`, `CC BY-SA`, `CC BY-ND`, `CC BY-NC`, `CC BY-NC-SA`,
`CC BY-NC-ND`, plus the paid/FC variants. There is no MIT option; `CC BY` is the
closest permissive equivalent.

Neither `creatorName` nor `packageName` may contain a `.` - VaM splits the package uid
on dots as `Creator.Package.Version`.

### Blocking a package's plugins without any code

`AddonPackagesUserPrefs/<Creator.Package.Version>.prefs`:

```json
{ "pluginsAlwaysEnabled" : "false", "pluginsAlwaysDisabled" : "true",
  "ignoreMissingDependencyErrors" : "false" }
```

Per package **version**, and all-or-nothing for that package - but zero runtime cost.

---

## 8. What real plugins actually use

Measured over **3,808 plugin `.cs` files** in 786 packages: number of files mentioning
each type. Useful for judging what is idiomatic versus what is exotic.

| Type | Files | | Type | Files |
| --- | ---: | --- | --- | ---: |
| `SuperController` | 1340 | | `JSONStorableAction` | 275 |
| `MVRScript` | 932 | | `JSONArray` | 270 |
| `JSONStorableBool` | 904 | | `FreeControllerV3` | 264 |
| `JSONStorableFloat` | 898 | | `JSONStorableParam` | 217 |
| `JSONClass` | 782 | | `JSONStorableColor` | 169 |
| `JSONStorableString` | 712 | | `FileManagerSecure` | 168 |
| `Atom` | 661 | | `DAZCharacterSelector` | 161 |
| `JSONStorableStringChooser` | 618 | | `UIDynamicColorPicker` | 145 |
| `UIDynamicButton` | 567 | | `HSVColorPicker` | 141 |
| `UIDynamic` | 419 | | `DAZMorph` | 136 |
| `UIDynamicTextField` | 385 | | `JSONNode` | 134 |
| `UIDynamicSlider` | 375 | | `JSONStorableUrl` | 119 |
| `Trigger` | 347 | | `TriggerHandler` | 89 |
| `UIDynamicPopup` | 335 | | `AssetBundleManager` | 90 |
| `UIDynamicToggle` | 324 | | | |

Exotic by comparison - **1 file each** across the whole corpus (and that file was mine):
`AppDomain.CurrentDomain`, `Activator.CreateInstance`, `BindingFlags.NonPublic`,
`HarmonyLib`, and a nested `enum`. If you find yourself reaching for those, that is a
signal, not a coincidence.

---

## 9. Debugging

- `SuperController.LogMessage(string)` and `SuperController.LogError(string)` - the
  in-game message log. There is also a `(string, bool logToFile)` overload.
- Unity player log: `%USERPROFILE%\AppData\LocalLow\MeshedVR\VaM\output_log.txt`
- Crash dumps: `%LOCALAPPDATA%\Temp\MeshedVR\VaM\Crashes\<timestamp>\`
  containing `error.log` (faulting module + register state) and `output_log.txt`
  (the managed stack). A crash *while compiling* means the compiler died, not your
  code - your code never ran.
- `MVR.FileManagement.FileManager.AssertNotCalledFromUnityMessaging()` guards some VaM
  calls. It only trips when the immediate caller is `SendMessage`,
  `SendMessageUpwards` or `BroadcastMessage` - normal callbacks and UnityEvents are
  fine.

### A checklist before you load a plugin in VaM

1. Does it compile against the real `Assembly-CSharp.dll`? (`csc /langversion:5`)
2. Does it parse as C# 6?
3. Does the emitted assembly reference a restricted namespace? (section 1.2)
4. Does it emit any nested type? (section 1.3)

`build/verify.ps1` in this repo does 1, 3 and 4; `build/syntaxcheck` does 2.

---

## 10. Post-mortem: every mistake made writing the first plugin

These are real failures from building PluginPass, in the order they happened. Each one
looked like something else at first, which is why they are worth writing down. If you
are an assistant reading this before writing a VaM plugin: **read section 1 and run the
checklist above before writing a line of code.** Almost everything below was avoidable.

| # | Symptom | Actual cause | Rule |
| --- | --- | --- | --- |
| 1 | 22 compile errors: "cannot convert `System.Type` to `JSONStorable.Type`" | `JSONStorable` has a nested enum named `Type` that shadows `System.Type` in every `MVRScript` | Write `System.Type` explicitly (1.5) |
| 2 | **VaM closed with no message.** `mono.dll` access violation | Nested `class` + nested `enum` in the plugin; VaM's compiler faults closing a nested type container | Keep types flat (1.3) |
| 3 | Still crashing after the first fix | The `yield return` coroutine added in the fix compiled to a *nested* state machine | `Invoke("M", 0f)` instead of a coroutine |
| 4 | "The namespace 'System.Reflection' is prohibited" | The whole Harmony interception engine was impossible from inside the sandbox | No reflection, ever (1.2) |
| 5 | Nearly shipped: `out` parameters | They compile to `System.Runtime.InteropServices.OutAttribute`, a restricted namespace | Return a value or an index instead (1.2) |
| 6 | A UTF-8 BOM appeared in the source | PowerShell 5.1 `Set-Content -Encoding UTF8` writes a BOM | `[System.IO.File]::WriteAllText($p,$s,(New-Object System.Text.UTF8Encoding($false)))` |
| 7 | Latent crash, never fired | The Harmony patch was installed from `Init()`, which runs *inside* the method being patched | Defer anything reentrant off that stack (section 2) |
| 8 | A CI check that always passed | Roslyn reports language-version errors while **binding**, not parsing, so parsing C# 7 as "C# 6" succeeds | Bind twice (C# 6 vs latest) and diff; and always test a check against input that should fail it |
| 9 | `ZipArchiveMode` not found | `ZipFile` is in `System.IO.Compression.FileSystem`, `ZipArchiveMode` in `System.IO.Compression` | Load both |
| 10 | StackOverflow loading a built DLL | A recursive `AssemblyResolve` handler | Pre-load dependencies instead |
| 11 | Redirect pointed at the wrong plugin slot | `RemovePlugin` frees the uid and `CreatePluginUID` takes the **lowest free** one, so an earlier block stole it | Do slot-preserving work before anything that frees a lower uid (section 3) |

### Two diagnostic lessons

**A crash inside the compiler is not your plugin's logic.** If the managed stack shows
`McsDriver:Compile` / `TypeBuilder:CreateType`, your code never ran - the compiler died
turning it into an assembly. Look at the *shape* of your types, not their behaviour.

**Compare crash stacks before blaming the environment.** This install also had
unrelated `mono.dll` crashes from before the plugin existed, which made "it's your
BepInEx GC patcher" look convincing. The tell was frame depth: the pre-existing crashes
closed a *top-level* type, the plugin's crashes closed a *nested* one. Same faulting
address, different cause. One extra stack frame was the whole diagnosis.

### The check that ends this class of bug

Compile against the real `Assembly-CSharp.dll` and inspect the emitted assembly with
Mono.Cecil before ever launching VaM. `build/verify.ps1` does exactly that and would
have caught mistakes 1, 2, 3, 4 and 5 in under a second each, on the desk, instead of
across several game crashes.

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

---

# Part 2 - Generated API reference

Read straight out of `Assembly-CSharp.dll`. Public members only.
Regenerate with `pwsh ./build/gen-api-doc.ps1`.

## Your plugin

### MVRScript

extends `JSONStorable`

```csharp
// constructors
new MVRScript()

// fields
protected Dictionary<UIDynamicColorPicker, JSONStorableColor> colorPickerToJSONStorableColor
JSONStorableBool enabledJSON
protected RectTransform leftUIContent
protected List<Transform> leftUIElements
MVRPluginManager manager
JSONStorableString pluginLabelJSON
protected Dictionary<UIDynamicPopup, JSONStorableStringChooser> popupToJSONStorableStringChooser
protected RectTransform rightUIContent
protected List<Transform> rightUIElements
protected Dictionary<UIDynamicSlider, JSONStorableFloat> sliderToJSONStorableFloat
protected Dictionary<UIDynamicTextField, JSONStorableString> textFieldToJSONStorableString
protected Dictionary<UIDynamicToggle, JSONStorableBool> toggleToJSONStorableBool

// methods
protected void Awake()
UIDynamicButton CreateButton(string label, bool rightSide)
UIDynamicColorPicker CreateColorPicker(JSONStorableColor jsc, bool rightSide)
UIDynamicPopup CreateFilterablePopup(JSONStorableStringChooser jsc, bool rightSide)
UIDynamicPopup CreatePopup(JSONStorableStringChooser jsc, bool rightSide)
UIDynamicPopup CreateScrollablePopup(JSONStorableStringChooser jsc, bool rightSide)
UIDynamicSlider CreateSlider(JSONStorableFloat jsf, bool rightSide)
UIDynamic CreateSpacer(bool rightSide)
UIDynamicTextField CreateTextField(JSONStorableString jss, bool rightSide)
UIDynamicToggle CreateToggle(JSONStorableBool jsb, bool rightSide)
protected Transform CreateUIElement(Transform prefab, bool rightSide)
void ForceAwake()
FreeControllerV3[] GetAllControllers()
Atom GetAtomById(string uid)
List<string> GetAtomUIDs()
Atom GetContainingAtom()
bool GetLeftGrabToggle()
bool GetLeftPointerShow()
bool GetLeftQuickGrab()
bool GetLeftQuickRelease()
bool GetLeftSelect()
float GetLeftTriggerVal()
FreeControllerV3 GetMainController()
bool GetRightGrabToggle()
bool GetRightPointerShow()
bool GetRightQuickGrab()
bool GetRightQuickRelease()
bool GetRightSelect()
float GetRightTriggerVal()
List<Atom> GetSceneAtoms()
List<string> GetVisibleAtomUIDs()
void Init()
protected void InitInternal()
void InitUI()
void InitUIAlt()
JSONNode LoadJSON(string saveName)
void RemoveButton(UIDynamicButton button)
void RemoveColorPicker(JSONStorableColor jsc)
void RemoveColorPicker(UIDynamicColorPicker dcolor)
void RemovePopup(UIDynamicPopup dpopup)
void RemovePopup(JSONStorableStringChooser jsc)
void RemoveSlider(UIDynamicSlider dslider)
void RemoveSlider(JSONStorableFloat jsf)
void RemoveSpacer(UIDynamic spacer)
void RemoveTextField(UIDynamicTextField dtext)
void RemoveTextField(JSONStorableString jss)
void RemoveToggle(UIDynamicToggle dtoggle)
void RemoveToggle(JSONStorableBool jsb)
void SaveJSON(JSONClass jc, string saveName, UserActionCallback confirmCallback, UserActionCallback denyCallback, ExceptionCallback exceptionCallback)
void SaveJSON(JSONClass jc, string saveName)
void SelectController(FreeControllerV3 fc, bool alignView)
bool ShouldIgnore()
protected void SyncEnabled(bool b)
protected void SyncLabel(string s)

```

### JSONStorable

extends `MonoBehaviour`

```csharp
// constructors
new JSONStorable()

// fields
protected bool _isPresetRestore
protected bool _isPresetStore
protected bool _JSONWasInit
protected string _subScenePrefix
protected List<string> actionNames
protected Dictionary<string, JSONStorableAction> actions
protected List<string> allFloatAndColorParamNames
protected List<string> allParamAndActionNames
protected Dictionary<string, JSONStorableParam> allParams
protected Dictionary<string, JSONStorableParam> allParamsByAltName
protected HashSet<string> appearanceLocks
protected List<string> audioClipActionNames
protected Dictionary<string, JSONStorableActionAudioClip> audioClipActions
protected bool awakecalled
protected List<string> boolParamNames
protected Dictionary<string, JSONStorableBool> boolParams
protected List<string> colorParamNames
protected Dictionary<string, JSONStorableColor> colorParams
Atom containingAtom
protected static JSONClass copyStore1
protected static JSONClass copyStore2
protected static JSONClass copyStore3
protected Dictionary<string, HashSet<string>> customAppearanceParamLocks
protected Dictionary<string, HashSet<string>> customPhysicalParamLocks
bool exclude
protected List<string> floatParamNames
protected List<string> floatParamNamesIncludingHidden
protected Dictionary<string, JSONStorableFloat> floatParams
protected Dictionary<string, JSONStorableFloat> floatParamsByAltName
protected HSVColor hsvc
protected bool insideRestore
bool needsStore
bool onlyStoreIfActive
string overrideId
protected HashSet<string> physicalLocks
protected List<string> presetFilePathActionNames
protected Dictionary<string, JSONStorableActionPresetFilePath> presetFilePathActions
JSONStorableAction restoreAllFromDefaultsAction
JSONStorableAction restoreAllFromStore1Action
JSONStorableAction restoreAllFromStore2Action
JSONStorableAction restoreAllFromStore3Action
JSONStorableAction restoreAppearanceFromDefaultsAction
JSONStorableAction restoreAppearanceFromStore1Action
JSONStorableAction restoreAppearanceFromStore2Action
JSONStorableAction restoreAppearanceFromStore3Action
JSONStorableAction restorePhysicalFromDefaultsAction
JSONStorableAction restorePhysicalFromStore1Action
JSONStorableAction restorePhysicalFromStore2Action
JSONStorableAction restorePhysicalFromStore3Action
JSONStorableAction saveToStore1Action
JSONStorableAction saveToStore2Action
JSONStorableAction saveToStore3Action
protected List<string> sceneFilePathActionNames
protected Dictionary<string, JSONStorableActionSceneFilePath> sceneFilePathActions
protected List<string> stringChooserActionNames
protected Dictionary<string, JSONStorableActionStringChooser> stringChooserActions
protected List<string> stringChooserParamNames
protected Dictionary<string, JSONStorableStringChooser> stringChooserParams
protected List<string> stringParamNames
protected Dictionary<string, JSONStorableString> stringParams
Transform UITransform
Transform UITransformAlt
protected List<string> urlParamNames
protected Dictionary<string, JSONStorableUrl> urlParams
protected List<string> vector3ParamNames
protected Dictionary<string, JSONStorableVector3> vector3Params

// properties
protected bool appearanceLocked { get; set; }
bool isPresetRestore { get; set; }
bool isPresetStore { get; set; }
bool mergeRestore { get; set; }
protected bool physicalLocked { get; set; }
string storeId { get; }
string subScenePrefix { get; set; }

// methods
string AtomUidToStoreAtomUid(string atomUid)
protected void Awake()
void CallAction(string actionName)
void CallAction(string actionName, string path)
void CallAction(string actionName, NamedAudioClip nac)
void CallPresetFileAction(string actionName, string path)
void CallStringChooserAction(string actionName, string choice)
void ClearAllLocks()
void ClearAppearanceLock(string appearanceLockUid)
void ClearCustomAppearanceParamLock(string paramName, string lockUid)
void ClearCustomPhysicalParamLock(string paramName, string lockUid)
void ClearPhysicalLock(string physicalLockUid)
void DeregisterAction(JSONStorableAction action)
void DeregisterAudioClipAction(JSONStorableActionAudioClip action)
void DeregisterBool(JSONStorableBool param)
void DeregisterColor(JSONStorableColor param)
void DeregisterFloat(JSONStorableFloat param)
void DeregisterPresetFilePathAction(JSONStorableActionPresetFilePath action)
void DeregisterSceneFilePathAction(JSONStorableActionSceneFilePath action)
void DeregisterString(JSONStorableString param)
void DeregisterStringChooser(JSONStorableStringChooser param)
void DeregisterStringChooserAction(JSONStorableActionStringChooser action)
void DeregisterUrl(JSONStorableUrl param)
void DeregisterVector3(JSONStorableVector3 param)
JSONStorableAction GetAction(string name)
List<string> GetActionNames()
List<string> GetAllFloatAndColorParamNames()
List<string> GetAllParamAndActionNames()
JSONStorableActionAudioClip GetAudioClipAction(string name)
List<string> GetAudioClipActionNames()
JSONStorableBool GetBoolJSONParam(string name)
List<string> GetBoolParamNames()
bool GetBoolParamValue(string param)
JSONStorableColor GetColorJSONParam(string name)
List<string> GetColorParamNames()
HSVColor GetColorParamValue(string param)
string[] GetCustomParamNames()
JSONStorableFloat GetFloatJSONParam(string name)
float GetFloatJSONParamMaxValue(string param)
float GetFloatJSONParamMinValue(string param)
List<string> GetFloatParamNames()
float GetFloatParamValue(string param)
JSONClass GetJSON(bool includePhysical, bool includeAppearance, bool forceStore)
JSONStorableParam GetParam(string name)
Type GetParamOrActionType(string name)
JSONStorableActionPresetFilePath GetPresetFilePathAction(string name)
List<string> GetPresetFilePathActionNames()
JSONStorableActionSceneFilePath GetSceneFilePathAction(string name)
List<string> GetSceneFilePathActionNames()
JSONStorableActionStringChooser GetStringChooserAction(string name)
List<string> GetStringChooserActionNames()
JSONStorableStringChooser GetStringChooserJSONParam(string name)
List<string> GetStringChooserJSONParamChoices(string param)
List<string> GetStringChooserParamNames()
string GetStringChooserParamValue(string param)
JSONStorableString GetStringJSONParam(string name)
List<string> GetStringParamNames()
string GetStringParamValue(string param)
JSONStorableUrl GetUrlJSONParam(string name)
List<string> GetUrlParamNames()
string GetUrlParamValue(string param)
JSONStorableVector3 GetVector3JSONParam(string name)
Vector3 GetVector3JSONParamMaxValue(string param)
Vector3 GetVector3JSONParamMinValue(string param)
List<string> GetVector3ParamNames()
Vector3 GetVector3ParamValue(string param)
bool HasParamsOrActions()
protected void InitActions()
protected void InitAudioClipActions()
protected void InitBoolParams()
protected void InitColorParams()
protected void InitFloatParams()
protected void InitJSONStorable()
protected void InitPresetFilePathActions()
protected void InitSceneFilePathActions()
protected void InitStringChooserActions()
protected void InitStringChooserParams()
protected void InitStringParams()
protected void InitUI(Transform t, bool isAlt)
void InitUI()
void InitUIAlt()
protected void InitUrlParams()
protected void InitVector3Params()
bool IsAction(string name)
bool IsAudioClipAction(string name)
bool IsBoolJSONParam(string name)
bool IsColorJSONParam(string name)
bool IsCustomAppearanceParamLocked(string paramName)
bool IsCustomPhysicalParamLocked(string paramName)
bool IsFloatJSONParam(string name)
bool IsPresetFilePathAction(string name)
bool IsSceneFilePathAction(string name)
bool IsStringChooserAction(string name)
bool IsStringChooserJSONParam(string name)
bool IsStringJSONParam(string name)
bool IsUrlJSONParam(string name)
bool IsVector3JSONParam(string name)
void LateRestoreFromJSON(JSONClass jc, bool restorePhysical, bool restoreAppearance, bool setMissingToDefault)
void MassDeregister(HashSet<string> paramNamesToDeregister)
void PostRestore(bool restorePhysical, bool restoreAppearance)
void PostRestore()
void PreRemove()
void PreRestore()
void PreRestore(bool restorePhysical, bool restoreAppearance)
void RegisterAction(JSONStorableAction action)
void RegisterAudioClipAction(JSONStorableActionAudioClip action)
void RegisterBool(JSONStorableBool param)
void RegisterColor(JSONStorableColor param)
void RegisterFloat(JSONStorableFloat param)
void RegisterPresetFilePathAction(JSONStorableActionPresetFilePath action)
void RegisterSceneFilePathAction(JSONStorableActionSceneFilePath action)
void RegisterString(JSONStorableString param)
void RegisterStringChooser(JSONStorableStringChooser param)
void RegisterStringChooserAction(JSONStorableActionStringChooser action)
void RegisterUrl(JSONStorableUrl param)
void RegisterVector3(JSONStorableVector3 param)
void Remove()
void RestoreAllFromDefaults()
void RestoreAllFromStore1()
void RestoreAllFromStore2()
void RestoreAllFromStore3()
void RestoreAppearanceFromDefaults()
void RestoreAppearanceFromStore1()
void RestoreAppearanceFromStore2()
void RestoreAppearanceFromStore3()
void RestoreFromJSON(JSONClass jc, bool restorePhysical, bool restoreAppearance, JSONArray presetAtoms, bool setMissingToDefault)
void RestoreFromStore1(bool restorePhysical, bool restoreAppearance)
void RestoreFromStore2(bool restorePhysical, bool restoreAppearance)
void RestoreFromStore3(bool restorePhysical, bool restoreAppearance)
void RestorePhysicalFromDefaults()
void RestorePhysicalFromStore1()
void RestorePhysicalFromStore2()
void RestorePhysicalFromStore3()
void SaveToStore1()
void SaveToStore2()
void SaveToStore3()
void SetAppearanceLock(string appearanceLockUid)
void SetBoolParamValue(string param, bool value)
void SetColorParamValue(string param, HSVColor value)
void SetCustomAppearanceParamLock(string paramName, string lockUid)
void SetCustomPhysicalParamLock(string paramName, string lockUid)
void SetDefaultsFromCurrent()
void SetFloatParamValue(string param, float value)
void SetPhysicalLock(string physicalLockUid)
void SetStringChooserParamValue(string param, string value)
void SetStringParamValue(string param, string value)
void SetUI(Transform t)
void SetUIAlt(Transform t)
void SetUrlParamValue(string param, string value)
void SetVector3ParamValue(string param, Vector3 value)
string StoredAtomUidToAtomUid(string storedAtomUid)
void Validate()

// nested types
enum Type { None, Bool, Float, Vector3, String, Url, StringChooser, Color, Action, AudioClipAction, SceneFilePathAction, PresetFilePathAction, StringChooserAction }

```

### MVRScriptAbstract

extends `JSONStorable`

```csharp
// constructors
protected new MVRScriptAbstract()

// methods
FreeControllerV3 GetMainAtomController()

```

## Storable parameters (the plugin data model)

### JSONStorableParam

extends `object`

```csharp
// constructors
protected new JSONStorableParam()

// fields
protected Button _defaultButton
protected Button _defaultButtonAlt
string altName
bool hidden
bool isRestorable
bool isStorable
protected HashSet<string> locks
string name
bool registeredAltName
RestoreTime restoreTime
JSONStorable storable
StoreType storeType
Type type

// properties
Button defaultButton { get; set; }
Button defaultButtonAlt { get; set; }
protected bool locked { get; set; }

// methods
void ClearAllLocks()
void ClearLock(string lockUid)
void LateRestoreFromJSON(JSONClass jc, bool restorePhysical, bool restoreAppearance, bool setMissingToDefault)
bool NeedsLateRestore(JSONClass jc, bool restorePhysical, bool restoreAppearance)
bool NeedsRestore(JSONClass jc, bool restorePhysical, bool restoreAppearance)
protected bool NeedsStore(JSONClass jc, bool includePhysical, bool includeAppearance)
void RegisterDefaultButtton(Button b, bool isAlt)
void RestoreFromJSON(JSONClass jc, bool restorePhysical, bool restoreAppearance, bool setMissingToDefault)
void SetDefaultFromCurrent()
void SetLock(string lockUid)
void SetValToDefault()
bool StoreJSON(JSONClass jc, bool includePhysical, bool includeAppearance, bool forceStore)

// nested types
enum RestoreTime { Normal, Late }
enum StoreType { Appearance, Physical, Any, Full }

```

### JSONStorableBool

extends `JSONStorableParam`

```csharp
// constructors
new JSONStorableBool(string paramName, bool startingValue)
new JSONStorableBool(string paramName, bool startingValue, SetJSONBoolCallback callback)
new JSONStorableBool(string paramName, bool startingValue, SetBoolCallback callback)

// fields
protected bool _defaultVal
protected GameObject _indicator
protected GameObject _indicatorAlt
protected GameObject _negativeIndicator
protected GameObject _negativeIndicatorAlt
protected Toggle _toggle
protected Toggle _toggleAlt
protected bool _val
SetBoolCallback setCallbackFunction
SetJSONBoolCallback setJSONCallbackFunction

// properties
bool defaultVal { get; set; }
GameObject indicator { get; set; }
GameObject indicatorAlt { get; set; }
GameObject negativeIndicator { get; set; }
GameObject negativeIndicatorAlt { get; set; }
Toggle toggle { get; set; }
Toggle toggleAlt { get; set; }
bool val { get; set; }
bool valNoCallback { get; set; }

// methods
protected void InternalSetVal(bool b, bool doCallback)
void LateRestoreFromJSON(JSONClass jc, bool restorePhysical, bool restoreAppearance, bool setMissingToDefault)
void RegisterIndicator(GameObject go, bool isAlt)
void RegisterNegativeIndicator(GameObject go, bool isAlt)
void RegisterToggle(Toggle t, bool isAlt)
void RestoreFromJSON(JSONClass jc, bool restorePhysical, bool restoreAppearance, bool setMissingToDefault)
void SetDefaultFromCurrent()
void SetVal(bool value)
void SetValToDefault()
bool StoreJSON(JSONClass jc, bool includePhysical, bool includeAppearance, bool forceStore)

// nested types
delegate SetBoolCallback(bool val)
delegate SetJSONBoolCallback(JSONStorableBool jb)

```

### JSONStorableFloat

extends `JSONStorableParam`

```csharp
// constructors
new JSONStorableFloat(string paramName, float startingValue, float minValue, float maxValue, bool constrain, bool interactable)
new JSONStorableFloat(string paramName, float startingValue, SetJSONFloatCallback callback, float minValue, float maxValue, bool constrain, bool interactable)
new JSONStorableFloat(string paramName, float startingValue, SetFloatCallback callback, float minValue, float maxValue, bool constrain, bool interactable)

// fields
protected bool _constrained
protected float _defaultVal
protected bool _interactable
protected float _max
protected float _min
protected Slider _slider
protected Slider _sliderAlt
protected SliderControl _sliderControl
protected SliderControl _sliderControlAlt
protected float _val
SetFloatCallback setCallbackFunction
SetJSONFloatCallback setJSONCallbackFunction

// properties
bool constrained { get; set; }
float defaultVal { get; set; }
float max { get; set; }
float min { get; set; }
Slider slider { get; set; }
Slider sliderAlt { get; set; }
float val { get; set; }
float valNoCallback { get; set; }

// methods
protected void InternalSetVal(float f, bool doCallback)
void LateRestoreFromJSON(JSONClass jc, bool restorePhysical, bool restoreAppearance, bool setMissingToDefault)
void RegisterSlider(Slider s, bool isAlt)
void RestoreFromJSON(JSONClass jc, bool restorePhysical, bool restoreAppearance, bool setMissingToDefault)
void SetDefaultFromCurrent()
void SetInteractble(bool b)
void SetVal(float value)
void SetValToDefault()
bool StoreJSON(JSONClass jc, bool includePhysical, bool includeAppearance, bool forceStore)

// nested types
delegate SetJSONFloatCallback(JSONStorableFloat jf)
delegate SetFloatCallback(float val)

```

### JSONStorableString

extends `JSONStorableParam`

```csharp
// constructors
new JSONStorableString(string paramName, string startingValue)
new JSONStorableString(string paramName, string startingValue, SetJSONStringCallback callback)
new JSONStorableString(string paramName, string startingValue, SetStringCallback callback)

// fields
protected Button _clearValButton
protected Button _clearValButtonAlt
protected UIDynamicTextField _dynamicText
protected UIDynamicTextField _dynamicTextAlt
protected InputField _inputField
protected InputFieldAction _inputFieldAction
protected InputFieldAction _inputFieldActionAlt
protected InputField _inputFieldAlt
protected bool _interactable
protected Button _setValToInputFieldButton
protected Button _setValToInputFieldButtonAlt
protected Text _text
protected Text _textAlt
protected string _val
string defaultVal
bool disableOnEndEdit
bool enableOnChange
bool representsAtomUid
SetStringCallback setCallbackFunction
SetJSONStringCallback setJSONCallbackFunction

// properties
Button clearValButton { get; set; }
Button clearValButtonAlt { get; set; }
UIDynamicTextField dynamicText { get; set; }
UIDynamicTextField dynamicTextAlt { get; set; }
InputField inputField { get; set; }
InputFieldAction inputFieldAction { get; set; }
InputFieldAction inputFieldActionAlt { get; set; }
InputField inputFieldAlt { get; set; }
bool interactable { get; set; }
Button setValToInputFieldButton { get; set; }
Button setValToInputFieldButtonAlt { get; set; }
Text text { get; set; }
Text textAlt { get; set; }
string val { get; set; }
string valNoCallback { get; set; }

// methods
void ClearVal()
protected void InternalSetVal(string s, bool doCallback)
void LateRestoreFromJSON(JSONClass jc, bool restorePhysical, bool restoreAppearance, bool setMissingToDefault)
void RegisterClearValButton(Button but, bool isAlt)
void RegisterInputField(InputField inf, bool isAlt)
void RegisterInputFieldAction(InputFieldAction infa, bool isAlt)
void RegisterSetValToInputFieldButton(Button but, bool isAlt)
void RegisterText(Text t, bool isAlt)
void RestoreFromJSON(JSONClass jc, bool restorePhysical, bool restoreAppearance, bool setMissingToDefault)
void SetDefaultFromCurrent()
void SetVal(string value)
void SetValToDefault()
void SetValToInputField()
void SetValToInputFieldAlt()
bool StoreJSON(JSONClass jc, bool includePhysical, bool includeAppearance, bool forceStore)

// nested types
delegate SetJSONStringCallback(JSONStorableString js)
delegate SetStringCallback(string val)

```

### JSONStorableStringChooser

extends `JSONStorableParam`

```csharp
// constructors
new JSONStorableStringChooser(string paramName, List<string> choicesList, string startingValue, string displayName)
new JSONStorableStringChooser(string paramName, List<string> choicesList, List<string> displayChoicesList, string startingValue, string displayName)
new JSONStorableStringChooser(string paramName, List<string> choicesList, string startingValue, string displayName, SetJSONStringCallback callback)
new JSONStorableStringChooser(string paramName, List<string> choicesList, string startingValue, string displayName, SetStringCallback callback)
new JSONStorableStringChooser(string paramName, List<string> choicesList, List<string> displayChoicesList, string startingValue, string displayName, SetJSONStringCallback callback)
new JSONStorableStringChooser(string paramName, List<string> choicesList, List<string> displayChoicesList, string startingValue, string displayName, SetStringCallback callback)

// fields
protected List<string> _choices
protected List<string> _displayChoices
protected string _label
protected UIPopup _popup
protected UIPopup _popupAlt
protected ToggleGroupValue _toggleGroupValue
protected ToggleGroupValue _toggleGroupValueAlt
protected string _val
protected bool _valueSetFromUI
string defaultVal
PopupOpenCallback popupOpenCallback
bool representsAtomUid
SetStringCallback setCallbackFunction
SetJSONStringCallback setJSONCallbackFunction
protected bool useDifferentDisplayChoices

// properties
List<string> choices { get; set; }
List<string> displayChoices { get; set; }
string label { get; set; }
UIPopup popup { get; set; }
UIPopup popupAlt { get; set; }
ToggleGroupValue toggleGroupValue { get; set; }
ToggleGroupValue toggleGroupValueAlt { get; set; }
string val { get; set; }
string valNoCallback { get; set; }
bool valueSetFromUI { get; }

// methods
protected void InternalSetVal(string s, bool doCallback)
void LateRestoreFromJSON(JSONClass jc, bool restorePhysical, bool restoreAppearance, bool setMissingToDefault)
void PopupOpen()
void RegisterPopup(UIPopup p, bool isAlt)
void RegisterToggleGroupValue(ToggleGroupValue tgv, bool isAlt)
void RestoreFromJSON(JSONClass jc, bool restorePhysical, bool restoreAppearance, bool setMissingToDefault)
void SetDefaultFromCurrent()
void SetVal(string value)
protected void SetValFromUI(string value)
void SetValToDefault()
bool StoreJSON(JSONClass jc, bool includePhysical, bool includeAppearance, bool forceStore)
protected void SyncPopup()
protected void SyncPopupAlt()

// nested types
delegate PopupOpenCallback()
delegate SetStringCallback(string val)
delegate SetJSONStringCallback(JSONStorableStringChooser js)

```

### JSONStorableUrl

extends `JSONStorableString`

```csharp
// constructors
new JSONStorableUrl(string paramName, string startingValue)
new JSONStorableUrl(string paramName, string startingValue, string filter)
new JSONStorableUrl(string paramName, string startingValue, SetJSONStringCallback callback)
new JSONStorableUrl(string paramName, string startingValue, SetStringCallback callback)
new JSONStorableUrl(string paramName, string startingValue, string filter, string suggestPath)
new JSONStorableUrl(string paramName, string startingValue, SetJSONStringCallback callback, string filter)
new JSONStorableUrl(string paramName, string startingValue, SetStringCallback callback, string filter)
new JSONStorableUrl(string paramName, string startingValue, SetStringCallback callback, string filter, bool forceCallbackOnSet)
new JSONStorableUrl(string paramName, string startingValue, SetJSONStringCallback callback, string filter, bool forceCallbackOnSet)
new JSONStorableUrl(string paramName, string startingValue, SetStringCallback callback, string filter, string suggestPath)
new JSONStorableUrl(string paramName, string startingValue, SetJSONStringCallback callback, string filter, string suggestPath)
new JSONStorableUrl(string paramName, string startingValue, SetJSONStringCallback callback, string filter, string suggestPath, bool forceCallbackOnSet)
new JSONStorableUrl(string paramName, string startingValue, SetStringCallback callback, string filter, string suggestPath, bool forceCallbackOnSet)

// fields
protected string _activeSuggestedPath
protected bool _allowBrowseAboveSuggestPath
protected Button _clearButton
protected Button _clearButtonAlt
protected Button _copyFromClipboardButton
protected Button _copyFromClipboardButtonAlt
protected Button _copyToClipboardButton
protected Button _copyToClipboardButtonAlt
protected Button _fileBrowseButton
protected Button _fileBrowseButtonAlt
protected string _filter
protected bool _forceCallbackOnSet
protected string _normalizedSuggestedPath
protected Button _reloadButton
protected Button _reloadButtonAlt
protected string _suggestedPath
protected string _suggestedPathGroup
protected bool _valueSetFromBrowse
bool allowFullComputerBrowse
BeginBrowseCallback beginBrowseCallback
BeginBrowseWithObjectCallback beginBrowseWithObjectCallback
EndBrowseCallback endBrowseCallback
EndBrowseWithObjectCallback endBrowseWithObjectCallback
string fileRemovePrefix
bool hideExtension
List<ShortCut> shortCuts
bool showDirs
protected static Dictionary<string, string> suggestedPathGroups

// properties
bool allowBrowseAboveSuggestedPath { get; set; }
Button clearButton { get; set; }
Button clearButtonAlt { get; set; }
Button copyFromClipboardButton { get; set; }
Button copyFromClipboardButtonAlt { get; set; }
Button copyToClipboardButton { get; set; }
Button copyToClipboardButtonAlt { get; set; }
Button fileBrowseButton { get; set; }
Button fileBrowseButtonAlt { get; set; }
Button reloadButton { get; set; }
Button reloadButtonAlt { get; set; }
string suggestedPath { get; set; }
string suggestedPathGroup { get; set; }
string val { get; set; }
bool valueSetFromBrowse { get; }

// methods
void Clear()
void CopyFromClipboard()
void CopyToClipboard()
void FileBrowse()
void LateRestoreFromJSON(JSONClass jc, bool restorePhysical, bool restoreAppearance, bool setMissingToDefault)
void RegisterClearButton(Button b, bool isAlt)
void RegisterCopyFromClipboardButton(Button b, bool isAlt)
void RegisterCopyToClipboardButton(Button b, bool isAlt)
void RegisterFileBrowseButton(Button b, bool isAlt)
void RegisterReloadButton(Button b, bool isAlt)
void Reload()
protected void ReplaceSuggestedPathGroupPathWithActivePath()
protected void ResetActiveSuggestedPath()
void RestoreFromJSON(JSONClass jc, bool restorePhysical, bool restoreAppearance, bool setMissingToDefault)
void SetFilePath(string path)
protected void SetFilePathBrowseCallback(string path, bool didClose)
static void SetSuggestedPathGroupPath(string group, string path)
bool StoreJSON(JSONClass jc, bool includePhysical, bool includeAppearance, bool forceStore)

// nested types
delegate BeginBrowseCallback()
delegate EndBrowseCallback()
delegate BeginBrowseWithObjectCallback(JSONStorableUrl jsurl)
delegate EndBrowseWithObjectCallback(JSONStorableUrl jsurl)

```

### JSONStorableColor

extends `JSONStorableParam`

```csharp
// constructors
new JSONStorableColor(string paramName, HSVColor startingColor)
new JSONStorableColor(string paramName, HSVColor startingColor, SetJSONColorCallback callback)
new JSONStorableColor(string paramName, HSVColor startingColor, SetHSVColorCallback callback)

// fields
protected HSVColorPicker _colorPicker
protected HSVColorPicker _colorPickerAlt
protected HSVColor _defaultVal
protected HSVColor _val
SetHSVColorCallback setCallbackFunction
SetJSONColorCallback setJSONCallbackFunction

// properties
HSVColorPicker colorPicker { get; set; }
HSVColorPicker colorPickerAlt { get; set; }
HSVColor defaultVal { get; set; }
HSVColor val { get; set; }
HSVColor valNoCallback { get; set; }

// methods
protected void InternalSetVal(float h, float s, float v, bool doCallback)
void LateRestoreFromJSON(JSONClass jc, bool restorePhysical, bool restoreAppearance, bool setMissingToDefault)
void RegisterColorPicker(HSVColorPicker cp, bool isAlt)
protected void Restore(JSONClass jc, bool setMissingToDefault)
void RestoreFromJSON(JSONClass jc, bool restorePhysical, bool restoreAppearance, bool setMissingToDefault)
void SetColor(Color c)
void SetDefaultFromCurrent()
void SetVal(float h, float s, float v)
void SetValToDefault()
bool StoreJSON(JSONClass jc, bool includePhysical, bool includeAppearance, bool forceStore)

// nested types
delegate SetHSVColorCallback(float h, float s, float v)
delegate SetJSONColorCallback(JSONStorableColor jc)

```

### JSONStorableVector3

extends `JSONStorableParam`

```csharp
// constructors
new JSONStorableVector3(string paramName, Vector3 startingValue, Vector3 minValue, Vector3 maxValue, bool constrain, bool interactable)
new JSONStorableVector3(string paramName, Vector3 startingValue, SetJSONVector3Callback callback, Vector3 minValue, Vector3 maxValue, bool constrain, bool interactable)
new JSONStorableVector3(string paramName, Vector3 startingValue, SetVector3Callback callback, Vector3 minValue, Vector3 maxValue, bool constrain, bool interactable)

// fields
protected bool _constrained
protected Vector3 _defaultVal
protected bool _interactable
protected Vector3 _max
protected Vector3 _min
protected Slider _sliderX
protected Slider _sliderXAlt
protected SliderControl _sliderXControl
protected SliderControl _sliderXControlAlt
protected Slider _sliderY
protected Slider _sliderYAlt
protected SliderControl _sliderYControl
protected SliderControl _sliderYControlAlt
protected Slider _sliderZ
protected Slider _sliderZAlt
protected SliderControl _sliderZControl
protected SliderControl _sliderZControlAlt
protected Vector3 _val
SetVector3Callback setCallbackFunction
SetJSONVector3Callback setJSONCallbackFunction

// properties
bool constrained { get; set; }
Vector3 defaultVal { get; set; }
Vector3 max { get; set; }
Vector3 min { get; set; }
Slider sliderX { get; set; }
Slider sliderXAlt { get; set; }
Slider sliderY { get; set; }
Slider sliderYAlt { get; set; }
Slider sliderZ { get; set; }
Slider sliderZAlt { get; set; }
Vector3 val { get; set; }
Vector3 valNoCallback { get; set; }

// methods
protected void InternalSetVal(Vector3 v, bool doCallback)
void LateRestoreFromJSON(JSONClass jc, bool restorePhysical, bool restoreAppearance, bool setMissingToDefault)
void RegisterSliderX(Slider s, bool isAlt)
void RegisterSliderY(Slider s, bool isAlt)
void RegisterSliderZ(Slider s, bool isAlt)
void RestoreFromJSON(JSONClass jc, bool restorePhysical, bool restoreAppearance, bool setMissingToDefault)
void SetDefaultFromCurrent()
void SetVal(Vector3 value)
void SetValToDefault()
void SetXVal(float f)
void SetYVal(float f)
void SetZVal(float f)
bool StoreJSON(JSONClass jc, bool includePhysical, bool includeAppearance, bool forceStore)

// nested types
delegate SetJSONVector3Callback(JSONStorableVector3 jf)
delegate SetVector3Callback(Vector3 v)

```

### JSONStorableAngle

extends `JSONStorableFloat`

```csharp
// constructors
new JSONStorableAngle(string n, float v, SetFloatCallback callback)

// properties
float val { get; set; }

```

### JSONStorableAction

extends `object`

```csharp
// constructors
new JSONStorableAction(string n, ActionCallback callback)

// fields
protected Button _button
protected Button _buttonAlt
protected UIDynamicButton _dynamicButton
protected UIDynamicButton _dynamicButtonAlt
protected bool _interactable
ActionCallback actionCallback
string name
JSONStorable storable

// properties
Button button { get; set; }
Button buttonAlt { get; set; }
UIDynamicButton dynamicButton { get; set; }
UIDynamicButton dynamicButtonAlt { get; set; }
bool interactable { get; set; }

// methods
protected void DoCallback()
void RegisterButton(Button b, bool isAlt)
void RegisterButton(UIDynamicButton b, bool isAlt)

// nested types
delegate ActionCallback()

```

### JSONStorableActionStringChooser

extends `object`

```csharp
// constructors
new JSONStorableActionStringChooser(string n, StringChoiceActionCallback callback, JSONStorableStringChooser chooser)

// fields
protected Button _button
protected Button _buttonAlt
protected JSONStorableStringChooser _chooser
protected UIDynamicButton _dynamicButton
protected UIDynamicButton _dynamicButtonAlt
StringChoiceActionCallback actionCallback
string name
JSONStorable storable

// properties
Button button { get; set; }
Button buttonAlt { get; set; }
string choice { get; }
List<string> choices { get; }
List<string> displayChoices { get; }
UIDynamicButton dynamicButton { get; set; }
UIDynamicButton dynamicButtonAlt { get; set; }

// methods
protected void DoCallback()
void RegisterButton(Button b, bool isAlt)
void RegisterButton(UIDynamicButton b, bool isAlt)

// nested types
delegate StringChoiceActionCallback(string choice)

```

### JSONStorableActionAudioClip

extends `object`

```csharp
// constructors
new JSONStorableActionAudioClip(string n, AudioClipActionCallback callback)

// fields
AudioClipActionCallback actionCallback
string name
JSONStorable storable

// nested types
delegate AudioClipActionCallback(NamedAudioClip nac)

```

### JSONStorableTriggerHandler

extends `JSONStorable`

implements `TriggerHandler`

```csharp
// constructors
new JSONStorableTriggerHandler()

// fields
RectTransform triggerActionDiscretePrefab
RectTransform triggerActionMiniPrefab
RectTransform triggerActionsPrefab
RectTransform triggerActionTransitionPrefab

// methods
RectTransform CreateTriggerActionDiscreteUI()
RectTransform CreateTriggerActionMiniUI()
RectTransform CreateTriggerActionsUI()
RectTransform CreateTriggerActionTransitionUI()
void DuplicateTrigger(Trigger trigger)
void RemoveTrigger(Trigger trigger)
void RemoveTriggerActionUI(RectTransform rt)

```

### JSONStorableDynamic

extends `MonoBehaviour`

```csharp
// constructors
new JSONStorableDynamic()

// fields
string assetBundleName
string assetName
protected AsyncOperation async
Atom containingAtom
protected Canvas[] cvs
protected bool didRegisterBundle
protected bool didStartLoadFromBundleAsync
protected Transform instance
string instanceName
protected bool isRegistered
protected JSONStorable[] jss
protected AsyncFlag loadFlag
protected AssetBundleLoadAssetOperation loadRequest
bool nameInstance
bool needsPostLoadJSONRestore
bool neverUnloadOnDisable
OnLoaded onLoadedHandlers
OnLoaded onPreloadedHandlers
Transform prefab
protected PhysicsSimulatorJSONStorable[] psjs
protected PhysicsSimulator[] pss
protected RenderSuspend[] rss
string sceneName
protected ScaleChangeReceiverJSONStorable[] scrjss
protected ScaleChangeReceiver[] scrs
bool unloadOnDisable

// properties
protected bool ready { get; set; }

// methods
protected void ClearLoadFlag()
protected void Connect()
protected void InitInstance()
protected IEnumerator LoadFromBundleAsync()
protected void LoadFromPrefab()
protected IEnumerator LoadFromPrefabAsync()
protected void OnDestroy()
protected void OnDisable()
protected void OnEnable()
protected void OnLoadComplete(bool skipJSONRestore)
protected void OnPreloadComplete()
void PostLoadJSONRestore()
protected void RegisterDynamicObjs()
void ResetUnregisteredInstance()
void UnloadIfInactive()
void UnloadIfNotEnabled()
protected void UnloadInstance()
protected void UnregisterDynamicObjs()

// nested types
delegate OnLoaded()

```

## UI widgets

### UIDynamic

extends `MonoBehaviour`

```csharp
// constructors
new UIDynamic()

// properties
float height { get; set; }

```

### UIDynamicButton

extends `UIDynamic`

```csharp
// constructors
new UIDynamicButton()

// fields
Button button
Image buttonImage
Text buttonText

// properties
Color buttonColor { get; set; }
string label { get; set; }
Color textColor { get; set; }

```

### UIDynamicToggle

extends `UIDynamic`

```csharp
// constructors
new UIDynamicToggle()

// fields
Image backgroundImage
Text labelText
Toggle toggle

// properties
Color backgroundColor { get; set; }
string label { get; set; }
Color textColor { get; set; }

```

### UIDynamicSlider

extends `UIDynamic`

```csharp
// constructors
new UIDynamicSlider()

// fields
protected bool _autoSetQuickButtons
protected bool _defaultButtonEnabled
protected bool _quickButtonsEnabled
protected bool _rangeAdjustEnabled
Button defaultButton
Text labelText
Button quickButtonM1
Button quickButtonM2
Button quickButtonM3
Button quickButtonM4
Button quickButtonP1
Button quickButtonP2
Button quickButtonP3
Button quickButtonP4
RectTransform quickButtonsGroup
RectTransform rangeAdjustGroup
Slider slider
SliderControl sliderControl
SetTextFromFloat sliderValueTextFromFloat

// properties
bool autoSetQuickButtons { get; set; }
bool defaultButtonEnabled { get; set; }
float defaultValue { get; set; }
string label { get; set; }
bool quickButtonsEnabled { get; set; }
bool rangeAdjustEnabled { get; set; }
string valueFormat { get; set; }

// methods
void AutoSetButtons()
void Configure(string labelString, float min, float max, float defaultValue, bool clamp, string valFormat, bool showQuickButtons, bool showRangeAdjust)
void ConfigureQuickButton(Button b, float qv)
void ConfigureQuickButton(Button b, float qv, float sv)
void ConfigureQuickButtons(float m1v, float m2v, float m3v, float m4v, float p1v, float p2v, float p3v, float p4v)
float GetQuickButtonValue(Button b)
protected void Init()
protected void InitQuickButton(Button b)
protected void InitQuickButtons()
protected void SyncDefaultButton()
protected void SyncQuickButtonsGroup()
protected void SyncRangeAdjustGroup()

```

### UIDynamicTextField

extends `UIDynamic`

```csharp
// constructors
new UIDynamicTextField()

// fields
Image backgroundImage
Text UItext

// properties
Color backgroundColor { get; set; }
string text { get; set; }
Color textColor { get; set; }

```

### UIDynamicPopup

extends `UIDynamic`

```csharp
// constructors
new UIDynamicPopup()

// fields
float labelSpacingRight
UIPopup popup

// properties
string label { get; set; }
Color labelTextColor { get; set; }
float labelWidth { get; set; }
float popupPanelHeight { get; set; }

```

### UIDynamicColorPicker

extends `UIDynamic`

```csharp
// constructors
new UIDynamicColorPicker()

// fields
protected bool _showLabel
HSVColorPicker colorPicker
Text labelText
RectTransform pickerContainer

// properties
string label { get; set; }
bool showLabel { get; set; }

// methods
protected void SyncShowLabel()

```

## The world

### SuperController

extends `MonoBehaviour`

```csharp
// constructors
new SuperController()

// fields
protected AlignRotationOffset _alignRotationOffset
protected bool _allowGrabPlusTriggerHandToggle
protected bool _alwaysUseAlternateHands
protected bool _autoFreezeAnimationOnSwitchToEditMode
protected bool _autoSimulation
protected string _creatorKey
protected bool _disableCollisionForAtomsNotInIsolatedAtom
protected bool _disableCollisionForAtomsNotInIsolatedSubScene
protected bool _disableRenderForAtomsNotInIsolatedAtom
protected bool _disableRenderForAtomsNotInIsolatedSubScene
protected string _entertainerKey
protected int _errorCount
protected string _freeKey
protected bool _freezePhysicsForAtomsNotInIsolatedAtom
protected bool _freezePhysicsForAtomsNotInIsolatedSubScene
protected bool _generateDepthTexture
protected Color _helpColor
protected string _helpText
protected bool _hideTrackers
protected bool _isLoading
protected Atom _isolatedAtom
protected SubScene _isolatedSubScene
protected string _keyFilePath
protected bool _leapHandLeftConnected
protected bool _leapHandRightConnected
protected bool _leapMotionEnabled
protected string _legacySteamKeyFilePath
protected bool _monitorUIVisible
protected int _msgCount
protected string _nsteamKey
protected ThumbstickFunction _oculusThumbstickFunction
protected bool _onStartupSkipStartScreen
protected bool _pauseAutoSimulation
protected bool _pauseRender
protected bool _render
protected bool _resetSimulation
protected string _retailKey
protected List<Atom> _saveQueue
protected bool _showHiddenAtoms
protected string _steamKey
protected string _teaserKey
protected bool _toggleMonitorSaveMainHUDVisible
protected bool _tracker1Visible
protected bool _tracker2Visible
protected bool _tracker3Visible
protected bool _tracker4Visible
protected bool _tracker5Visible
protected bool _tracker6Visible
protected bool _tracker7Visible
protected bool _tracker8Visible
protected Side _UISide
protected bool _useLegacyWorldScaleChange
protected bool _useMonitorRigAudioListenerWhenActive
protected bool _useSceneLoadPosition
protected bool _vamXInstalled
protected bool _vamXWasInstalled
protected float _worldUIMonitorAnchorDistance
protected float _worldUIMonitorAnchorHeight
protected bool _worldUIShowOverlaySky
protected float _worldUIVRAnchorDistance
protected float _worldUIVRAnchorHeight
protected LinkedList<AudioListener> additionalAudioListeners
Transform[] advancedSceneEditButtons
Transform[] advancedSceneEditDisabledButtons
Transform[] advancedSceneEditDisabledEditModeTransforms
Transform[] advancedSceneEditOnlyEditModeTransforms
UIPopup alignRotationOffsetPopup
Text allErrorsCountText
Text allErrorsCountText2
InputField allErrorsInputField
InputField allErrorsInputField2
Text allMessagesCountText
Text allMessagesCountText2
InputField allMessagesInputField
InputField allMessagesInputField2
Toggle allowGrabPlusTriggerHandToggleToggle
Toggle allowHeadPossessMousePanAndZoomToggle
Toggle allowPossessSpringAdjustmentToggle
protected List<FreeControllerV3> alreadyDisplayed
protected Dictionary<string, bool> alreadyPackaged
HandModelControl alternateControllerHandModelControl
Transform alternateCustomUI
bool alwaysEnablePointers
Toggle alwaysUseAlternateHandsToggle
protected Dictionary<string, int> assetBundleAssetNameRefCounts
protected Dictionary<string, GameObject> assetBundleAssetNameToPrefab
bool assetManagerSimulateInEditor
protected Dictionary<string, AtomAsset> atomAssetByType
AtomAsset[] atomAssets
string atomAssetsFile
protected List<string> atomCategories
string atomCategory
UIPopup atomCategoryPopup
protected Dictionary<string, List<string>> atomCategoryToAtomTypes
Transform atomContainer
Transform atomPoolContainer
protected Dictionary<string, Atom> atomPrefabByType
UIPopup atomPrefabPopup
Atom[] atomPrefabs
protected HashSet<Atom> atomsInSubSceneHash
protected List<string> atomTypes
Toggle autoFreezeAnimationOnSwitchToEditModeToggle
protected Transform bootstrapPluginContainer
protected ScriptDomain bootstrapPluginDomain
protected Dictionary<string, List<MVRScriptController>> bootstrapPluginScriptControllers
string buttonCycleSelection
string buttonSelect
string buttonToggleRotateMode
string buttonToggleTargets
string buttonUnselect
CameraTarget centerCameraTarget
GameObject[] centerCameraTargetDisableWhenMonitor
HandModelControl commonHandModelControl
Slider controllerScaleSlider
Transform creatorKeyTransform
protected MotionAnimationMaster currentAnimationMaster
protected AudioListener currentAudioListener
protected bool cursorLockedLastFrame
SteamVR_Action_Boolean cycleEngageAction
SteamVR_Action_Vector2 cycleUsingXAxisAction
SteamVR_Action_Vector2 cycleUsingYAxisAction
string[] demoPackagePrefixes
string demoScenesDir
FileBrowser directoryBrowserUI
bool disableAdvancedSceneEdit
Toggle disableAllNavigationToggle
bool disableBrowse
Toggle disableCollisionForAtomsNotInIsolatedAtomToggle
Toggle disableCollisionForAtomsNotInIsolatedSubSceneToggle
bool disableCustomUI
Toggle disableGrabNavigationToggle
bool disableHub
bool disableInternalKeyBindings
bool disableInternalNavigationKeyBindings
bool disableKeyInformation
bool disableLeap
bool disableLoadSceneButton
bool disableNavigation
bool disablePackages
bool disablePromotional
Toggle disableRenderForAtomsNotInIsolatedAtomToggle
Toggle disableRenderForAtomsNotInIsolatedSubSceneToggle
bool disableSaveSceneButton
Toggle disableTeleportDuringPossessToggle
Toggle disableTeleportToggle
bool disableTermsOfUse
bool disableUI
bool disableVR
RectTransform dynamicButtonPrefab
RectTransform dynamicSliderPrefab
RectTransform dynamicTogglePrefab
Transform[] editModeOnlyTransforms
Toggle editModeToggle
KeyType editorMimicHighestKey
string editorMimicVersion
string[] editorSceneList
Transform embeddedSceneUI
bool enableStartScene
Button endIsolateEditAtomButton
Button endIsolateEditSubSceneButton
Transform entertainerKeyTransform
protected string errorLog
protected bool errorLogDirty
Transform errorLogPanel
protected float errorSplashTime
protected float errorSplashTimeRemaining
Transform errorSplashTransform
FileBrowser fileBrowserUI
FileBrowser fileBrowserWorldUI
protected Dictionary<string, string> filesToMigrateMap
float fixedMonitorUIScale
Toggle focusAtomOnAddToggle
float focusDistance
protected bool foundVersion
Transform freeKeyTransform
SteamVR_Action_Vector2 freeModeMoveAction
GameObject freeMouseMoveModeIndicator
SteamVR_Action_Vector2 freeMoveAction
Toggle freeMoveFollowFloorToggle
Toggle freeMoveFollowFloorToggleAlt
Slider freeMoveMultiplierSlider
Toggle freezeAnimationToggle
Toggle freezeAnimationToggleAlt
Toggle freezePhysicsForAtomsNotInIsolatedAtomToggle
Toggle freezePhysicsForAtomsNotInIsolatedSubSceneToggle
Toggle generateDepthTextureToggle
SteamVR_Action_Boolean grabAction
SteamVR_Action_Boolean grabNavigateAction
Slider grabNavigationPositionMultiplierSlider
Slider grabNavigationRotationMultiplierSlider
SteamVR_Action_Single grabValAction
Transform handsContainer
SteamVR_Action_Vibration hapticAction
protected bool hasPendingErrorSplash
Transform headPossessedActivateTransform
Text headPossessedText
Transform heightAdjustTransform
Text helpHUDText
Transform helpOverlayOVR
Transform helpOverlayVive
Toggle helpToggle
Toggle helpToggleAlt
protected bool hiddenReset
protected bool hideWaitTransform
Camera hiResScreenshotCamera
Slider hiResScreenShotCameraFOVSlider
Transform hiResScreenshotPreview
SteamVR_Action_Boolean holdGrabAction
protected List<AsyncFlag> holdLoadCompleteFlags
Slider hologridTransparencySlider
HubBrowse hubBrowser
Transform[] hubDisabledEnableTransforms
Transform[] hubDisabledTransforms
protected bool hubOpenedFromWorld
AtomAsset[] indirectAtomAssets
Atom[] indirectAtomPrefabs
bool invertAxis1
bool invertAxis2
bool invertAxis3
bool invertNavigationForwardAxis
bool invertNavigationSideAxis
bool invertNavigationTurnAxis
bool invertNavigationUpAxis
RectTransform isolatedAtomEditControlPanel
Text isolatedAtomLabel
RectTransform isolatedSceneEditControlPanel
Text isolatedSubSceneLabel
bool isOpenVR
bool isOVR
Text keyEntryStatus
Transform[] keyInformationTransforms
InputField keyInputField
InputFieldAction keyInputFieldAction
Transform keySyncingIndicator
protected Atom lastAddedAtom
protected string lastBrowseDir
protected string lastCycleSelectAtomType
protected string lastCycleSelectAtomUid
protected string lastLoadDir
protected string lastLoadedvamXBootstrapPath
protected string lastMediaDir
protected string lastScenePathDir
Transform leapHandLeft
LeapHandModelControl leapHandModelControl
Transform leapHandMountLeft
Transform leapHandMountRight
Transform leapHandRight
Toggle leapMotionEnabledToggle
Transform LeapRig
LeapXRServiceProvider[] LeapServiceProviders
Camera leftControllerCamera
Transform leftHand
Transform leftHandAlternate
Transform leftSelectionHUDTransform
Button loadConfirmButton
Transform loadConfirmPanel
Text loadConfirmPathText
protected string loadedName
Transform loadingGeometry
Transform loadingIcon
protected List<AsyncFlag> loadingIconFlags
Slider loadingProgressSlider
Slider loadingProgressSliderAlt
Text loadingTextStatus
Text loadingTextStatusAlt
Transform loadingUI
Transform loadingUIAlt
JSONNode loadJson
Transform[] loadSceneButtons
Transform[] loadSceneDisabledButtons
protected bool loadSceneWorldDialogActivatedFromWorld
protected bool loadTemplateWorldDialogActivatedFromWorld
Toggle lockHeightDuringNavigateToggle
Toggle lockHeightDuringNavigateToggleAlt
LayerMask lookAtTriggerMask
Camera lookCamera
Slider loResScreenShotCameraFOVSlider
Transform mainHUD
Transform mainHUDAttachPoint
Transform mainHUDPivot
float mainHUDPivotXRotationMonitor
float mainHUDPivotXRotationVR
UITabSelector mainMenuTabSelector
Transform mainMenuUI
float maxAngularVelocity
float maxDepenetrationVelocity
protected int maxLength
protected MD5CryptoServiceProvider md5
FileBrowser mediaFileBrowserUI
SteamVR_Action_Boolean menuAction
Transform migratePathsPanel
Slider monitorCameraFOVSlider
Camera MonitorCenterCamera
Vector3 MonitorCenterCameraOffset
Transform MonitorModeAuxUI
Button MonitorModeButton
Transform MonitorRig
protected AudioListener monitorRigAudioListener
Transform MonitorUI
Transform MonitorUIAnchor
Transform MonitorUIAttachPoint
Slider monitorUIScaleSlider
Slider monitorUIYOffsetSlider
MotionAnimationMaster motionAnimationMaster
GameObject mouseCrosshair
RectTransform mouseCrosshairPointer
Transform mouseGrab
SelectionHUD mouseSelectionHUD
protected string msgLog
protected bool msgLogDirty
Transform msgLogPanel
protected float msgSplashTime
protected float msgSplashTimeRemaining
Transform msgSplashTransform
MultiButtonPanel multiButtonPanel
Transform navigationCamera
LayerMask navigationColliderMask
CubicBezierCurve navigationCurve
float navigationDistance
Axis navigationForwardAxis
Transform navigationHologrid
protected float navigationHologridShowTime
protected float navigationHologridTransparencyMultiplier
protected bool navigationHologridVisible
Transform navigationPlayArea
Transform navigationPlayer
Transform navigationRig
Transform navigationRigParent
Axis navigationSideAxis
Axis navigationTurnAxis
Axis navigationUpAxis
protected HashSet<Atom> nestedAtomsInSubSceneHash
JSONEmbed newJSONEmbedScene
Text newPathsText
string newSceneAltName
string newSceneName
Transform normalAlertRoot
protected string[] obsoleteDirectories
Transform obsoletePathsPanel
Text obsoletePathsText
UIPopup oculusThumbstickFunctionPopup
GameObject okAlertPrefab
GameObject okAndCancelAlertPrefab
int oldestMajorVersion
Text oldPathsText
OnAtomAdded onAtomAddedHandlers
OnAtomParentChanged onAtomParentChangedHandlers
OnAtomRemoved onAtomRemovedHandlers
OnAtomSubSceneChanged onAtomSubSceneChangedHandlers
OnAtomUIDRename onAtomUIDRenameHandlers
OnAtomUIDsChanged onAtomUIDsChangedHandlers
OnAtomUIDsWithForceProducersChanged onAtomUIDsWithForceProducersChangedHandlers
OnAtomUIDsWithForceReceiversChanged onAtomUIDsWithForceReceiversChangedHandlers
OnAtomUIDsWithFreeControllersChanged onAtomUIDsWithFreeControllersChangedHandlers
OnAtomUIDsWithRigidbodiesChanged onAtomUIDsWithRigidbodiesChangedHandlers
OnAudioSourceControlNamesChanged onAudioSourceControlNamesChangedHandlers
OnSceneLoaded onBeforeSceneSaveHandlers
OnForceProducerNamesChanged onForceProducerNamesChangedHandlers
OnForceReceiverNamesChanged onForceReceiverNamesChangedHandlers
OnFreeControllerNamesChanged onFreeControllerNamesChangedHandlers
OnGameModeChanged onGameModeChangedHandlers
VRWebBrowser onlineBrowser
Transform onlineBrowserUI
Transform[] onlineBrowseSceneButtons
Transform[] onlineBrowseSceneDisabledButtons
OnRhythmControllerNamesChanged onRhythmControllerNamesChangedHandlers
OnRigidbodyNamesChanged onRigidbodyNamesChangedHandlers
OnSceneLoaded onSceneLoadedHandlers
OnSceneLoaded onSceneSavedHandlers
Toggle onStartupSkipStartScreenToggle
OnSubSceneLoaded onSubSceneLoadedHandlers
Toggle openMainHUDOnErrorToggle
protected AudioListener openVRRigAudioListener
protected Collider[] overlappingControls
protected List<FreeControllerV3> overlappingFcs
protected AudioListener overrideAudioListener
bool overrideCanvasSortingLayer
string overrideCanvasSortingLayerName
Button overwriteConfirmButton
Transform overwriteConfirmPanel
Text overwriteConfirmPathText
Camera OVRCenterCamera
OVRHandInput ovrHandInputLeft
OVRHandInput ovrHandInputRight
Transform OVRRig
protected AudioListener ovrRigAudioListener
PackageBuilder packageBuilder
Transform packageBuilderUI
HubDownloader packageDownloader
PackageBuilder packageManager
Transform packageManagerUI
bool packageMode
protected Dictionary<string, string> pathMigrationMappings
protected AsyncFlag pauseAutoSimulationFlag
Toggle pauseAutoSimulationToggle
protected int pauseFrames
protected AsyncFlag pauseRenderFlag
Toggle pauseRenderToggle
Slider playerHeightAdjustSlider
Slider playerHeightAdjustSliderAlt
Transform[] playModeOnlyTransforms
Toggle playModeToggle
Slider possessPositionSpringSlider
Slider possessRotationSpringSlider
Transform[] promotionalTransforms
SteamVR_Action_Vector2 pushPullAction
Button quickReloadIsolatedSubSceneButton
Button quickSaveIsolatedSubSceneButton
Toggle quickSelectAlignToggle
Toggle quickSelectMoveToggle
Toggle quickSelectOpenUIToggle
protected RaycastHit[] raycastHits
LineRenderer rayLineLeft
Material rayLineMaterialLeft
Material rayLineMaterialRight
LineRenderer rayLineRight
float rayLineWidth
protected bool readyToResumeSimulation
protected HashSet<string> referencedVarPackages
Transform regularPlayArea
SteamVR_Action_Boolean remoteGrabAction
SteamVR_Action_Boolean remoteHoldGrabAction
protected bool remoteHoldGrabDisabled
protected List<AsyncFlag> removeAutoSimulationFlags
protected List<AsyncFlag> removeHoldFlags
protected List<AsyncFlag> removeLoadFlags
protected List<AsyncFlag> removeRenderFlags
protected List<AsyncFlag> removeSimulationFlags
protected AsyncFlag resetSimulationTimerFlag
protected string resolvedVersion
protected List<string> resolvedVersionDefines
Camera rightControllerCamera
Transform rightHand
Transform rightHandAlternate
Transform rightSelectionHUDTransform
Transform[] saveSceneButtons
Transform[] saveSceneDisabledButtons
string savesDir
string savesDirEditor
protected string savingName
Transform sceneControlUI
Transform sceneControlUIAlt
protected Vector3 sceneLoadMonitorCameraLocalRotation
protected float sceneLoadPlayerHeightAdjust
protected Vector3 sceneLoadPosition
protected Quaternion sceneLoadRotation
protected ScreenShotCallback screenShotCallback
Camera screenshotCamera
Transform screenshotPreview
SteamVR_Action_Boolean selectAction
Toggle selectAtomOnAddToggle
UIPopup selectAtomPopup
LayerMask selectColliderMask
UIPopup selectControllerPopup
Text selectedControllerNameDisplay
FCPositionHandle selectedControllerPositionHandle
FCRotationHandle selectedControllerRotationHandle
Transform selectionHUDTransform
Button selectIsolatedAtomButton
Button selectIsolatedSubSceneButton
Transform selectPrefab
Mesh selectPrefabStandardMesh
float selectPrefabStandardScale
PresetManagerControl sessionPresetManagerControl
Toggle showHiddenAtomsToggle
Toggle showHiddenAtomsToggleAlt
bool showMainHUDOnStart
Toggle showNavigationHologridToggle
bool sortAtomUIDs
string[] specificMilestoneVersionDefines
JSONEmbed startJSONEmbedScene
string startSceneAltName
string startSceneName
SteamVRHandInput steamVRHandInputLeft
SteamVRHandInput steamVRHandInputRight
SteamVR_Action_Boolean swapFreeMoveAxis
float targetAlpha
LayerMask targetColliderMask
SteamVR_Action_Boolean targetShowAction
Transform teaserKeyTransform
SteamVR_Action_Boolean teleportAction
Toggle teleportAllowRotationToggle
SteamVR_Action_Boolean teleportShowAction
protected string tempHelpText
FileBrowser templatesFileBrowserWorldUI
Transform[] termsOfUseTransforms
SteamVR_Action_Boolean toggleHandAction
Transform topWorldUI
Transform touchCenterHandLeft
Transform touchCenterHandRight
Transform touchHandMountLeft
Transform touchHandMountRight
Transform touchObjectLeft
protected MeshRenderer[] touchObjectLeftMeshRenderers
Transform touchObjectRight
protected MeshRenderer[] touchObjectRightMeshRenderers
Material twoStageLineMaterial
protected Dictionary<string, List<Atom>> typeToAtomPool
SteamVR_Action_Boolean UIInteractAction
UIPopup UISidePopup
Toggle useLegacyWorldScaleChangeToggle
bool useLookForNavigation
bool useLookSelect
Toggle useMonitorRigAudioListenerWhenActiveToggle
UITabSelector userPrefsTabSelector
Toggle useSceneLoadPositionToggle
protected string vamXBootstrapPluginPath
Button vamXButton
Text vamXButtonText
GameObject[] vamXDisabledAndAdvancedSceneEditDisabledGameObjects
GameObject[] vamXDisabledAndAdvancedSceneEditGameObjects
GameObject[] vamXDisabledGameObjects
GameObject[] vamXEnabledAndAdvancedSceneEditGameObjects
GameObject[] vamXEnabledGameObjects
protected string vamXMainScene
RectTransform vamXPanel
Button vamXTutorialButton
protected string vamXTutorialScene
protected int vamXVersion
string version
Text versionText
Camera ViveCenterCamera
Transform viveCenterHandLeft
Transform viveCenterHandRight
Transform viveHandMountLeft
Transform viveHandMountRight
Transform viveObjectLeft
Transform viveObjectRight
Transform ViveRig
Transform viveTracker1
SteamVR_RenderModel viveTracker1Model
Transform viveTracker2
SteamVR_RenderModel viveTracker2Model
Transform viveTracker3
SteamVR_RenderModel viveTracker3Model
Transform viveTracker4
SteamVR_RenderModel viveTracker4Model
Transform viveTracker5
SteamVR_RenderModel viveTracker5Model
Transform viveTracker6
SteamVR_RenderModel viveTracker6Model
Transform viveTracker7
SteamVR_RenderModel viveTracker7Model
Transform viveTracker8
SteamVR_RenderModel viveTracker8Model
Text[] waitReasonTexts
protected List<AsyncFlag> waitResumeAutoSimulationFlags
protected List<AsyncFlag> waitResumeRenderFlags
protected List<AsyncFlag> waitResumeSimulationFlags
Transform waitTransform
protected Dictionary<FreeControllerV3, bool> wasHitFC
protected Dictionary<SelectTarget, bool> wasHitST
Transform wizardWorldUI
Transform worldAlertRoot
Slider worldScaleSlider
Slider worldScaleSliderAlt
Transform worldScaleTransform
Transform worldUI
protected AsyncFlag worldUIActiveFlag
HUDAnchor worldUIAnchor
Toggle worldUIShowOverlaySkyToggle
Slider worldUIVRAnchorDistanceSlider
Slider worldUIVRAnchorHeightSlider
protected ZipOutputStream zos

// properties
ActiveUI activeUI { get; set; }
protected bool advancedSceneEditDisabled { get; }
AlignRotationOffset alignRotationOffset { get; set; }
bool allowGrabPlusTriggerHandToggle { get; set; }
bool allowHeadPossessMousePanAndZoom { get; set; }
bool allowPossessSpringAdjustment { get; set; }
bool alwaysUseAlternateHands { get; set; }
bool assetManagerReady { get; }
protected Transform atomContainerTransform { get; }
string[] audioSourceControlNames { get; }
bool autoFreezeAnimationOnSwitchToEditMode { get; set; }
protected bool autoSimulation { get; set; }
protected bool browseDisabled { get; }
float controllerScale { get; set; }
string creatorKey { get; }
AudioListener CurrentAudioListener { get; }
string currentLoadDir { get; set; }
string currentSaveDir { get; set; }
SelectMode currentSelectMode { get; }
protected bool customUIDisabled { get; }
bool disableAllNavigation { get; set; }
bool disableCollisionForAtomsNotInIsolatedAtom { get; set; }
bool disableCollisionForAtomsNotInIsolatedSubScene { get; set; }
bool disableGrabNavigation { get; set; }
bool disableRenderForAtomsNotInIsolatedAtom { get; set; }
bool disableRenderForAtomsNotInIsolatedSubScene { get; set; }
bool disableTeleport { get; set; }
bool disableTeleportDuringPossess { get; set; }
protected JSONEmbed embeddedJSONScene { get; }
string entertainerKey { get; }
int errorCount { get; set; }
string[] forceProducerNames { get; }
string[] forceReceiverNames { get; }
string[] freeControllerNames { get; }
string freeKey { get; }
bool freeMoveFollowFloor { get; set; }
float freeMoveMultiplier { get; set; }
bool freezeAnimation { get; }
bool freezePhysicsForAtomsNotInIsolatedAtom { get; set; }
bool freezePhysicsForAtomsNotInIsolatedSubScene { get; set; }
GameMode gameMode { get; set; }
bool generateDepthTexture { get; set; }
float grabNavigationPositionMultiplier { get; set; }
float grabNavigationRotationMultiplier { get; set; }
Color helpColor { get; set; }
bool helpOverlayOn { get; set; }
string helpText { get; set; }
bool hideTrackers { get; set; }
float hiResScreenShotCameraFOV { get; set; }
float hologridTransparency { get; set; }
bool hubDisabled { get; }
bool HubOpen { get; }
bool isLoading { get; }
bool IsMonitorOnly { get; }
bool IsMonitorRigActive { get; }
protected Atom isolatedAtom { get; set; }
protected SubScene isolatedSubScene { get; set; }
string keyFilePath { get; }
bool keyInformationDisabled { get; }
ActiveUI lastActiveUI { get; }
protected bool leapDisabled { get; }
bool leapMotionEnabled { get; set; }
FreeControllerV3 LeftFullGrabbedController { get; }
FreeControllerV3 LeftGrabbedController { get; }
string legacySteamKeyFilePath { get; }
string LoadedSceneName { get; }
protected bool loadSceneButtonDisabled { get; }
bool lockHeightDuringNavigate { get; set; }
float loResScreenShotCameraFOV { get; set; }
bool MainHUDAnchoredOnMonitor { get; }
bool MainHUDVisible { get; }
float monitorCameraFOV { get; set; }
float monitorUIScale { get; set; }
float monitorUIYOffset { get; set; }
int msgCount { get; set; }
bool navigationDisabled { get; }
string nsteamKey { get; }
ThumbstickFunction oculusThumbstickFunction { get; set; }
bool onStartupSkipStartScreen { get; set; }
bool openMainHUDOnError { get; set; }
protected bool packagesDisabled { get; }
bool pauseAutoSimulation { get; set; }
bool pauseRender { get; set; }
float playerHeightAdjust { get; set; }
protected bool pointersAlwaysEnabled { get; }
float possessPositionSpring { get; set; }
float possessRotationSpring { get; set; }
bool promotionalDisabled { get; }
protected bool render { get; set; }
protected bool resetSimulation { get; set; }
string retailKey { get; }
string[] rhythmControllerNames { get; }
FreeControllerV3 RightFullGrabbedController { get; }
FreeControllerV3 RightGrabbedController { get; }
string[] rigidbodyNames { get; }
protected bool saveSceneButtonDisabled { get; }
string savesDirResolved { get; }
bool showHiddenAtoms { get; set; }
bool showNavigationHologrid { get; set; }
SuperController singleton { get; }
int solverIterations { get; set; }
float startingMonitorCameraFOV { get; }
protected bool startSceneEnabled { get; }
string steamKey { get; }
string teaserKey { get; }
bool teleportAllowRotation { get; set; }
bool termsOfUseDisabled { get; }
protected bool tracker1Visible { get; set; }
protected bool tracker2Visible { get; set; }
protected bool tracker3Visible { get; set; }
protected bool tracker4Visible { get; set; }
protected bool tracker5Visible { get; set; }
protected bool tracker6Visible { get; set; }
protected bool tracker7Visible { get; set; }
protected bool tracker8Visible { get; set; }
bool UIDisabled { get; }
Side UISide { get; set; }
bool useLegacyWorldScaleChange { get; set; }
bool useMonitorRigAudioListenerWhenActive { get; set; }
bool useSceneLoadPosition { get; set; }
bool vamXIntalled { get; }
protected bool VRDisabled { get; }
float worldScale { get; set; }
bool worldUIActivated { get; }
bool worldUIShowOverlaySky { get; set; }
float worldUIVRAnchorDistance { get; set; }
float worldUIVRAnchorHeight { get; set; }

// methods
void ActivateWorldUI()
Transform AddAtom(Atom atom, string useuid, bool userInvoked, bool forceSelect, bool forceFocus, bool instantiate)
void AddAtomByPopupValue()
void AddAtomByType(string type, bool userInvoked, bool forceSelect, bool forceFocus)
IEnumerator AddAtomByType(string type, string useuid, bool userInvoked, bool forceSelect, bool forceFocus)
void AddAtomByTypeForceSelect(string type)
void AddCanvas(Canvas c)
string AddFileToPackage(string path, string packagepath)
void AddKey()
void AddVarPackageRefToVacPackage(string packageUid)
void AdjustNavigationRigHeight()
void Alert(string alertMessage, UnityAction okAlertCallback, DisplayUIChoice displayUIChoice)
void Alert(string alertMessage, UnityAction okAlertCallback, UnityAction cancelAlertCallback, DisplayUIChoice displayUIChoice)
static void AlertUser(string alert, UnityAction okCallback, UnityAction cancelCallback, DisplayUIChoice displayUIChoice)
static void AlertUser(string alert, UnityAction okCallback, DisplayUIChoice displayUIChoice)
void AlignRigFacingController(FreeControllerV3 controller, bool rotationOnly)
void AlignRigFacingSelectedController(bool rotationOnly)
protected void AllocateOverlappingControls()
protected void AllocateRaycastHits()
void ArmAllControlledControllersForRecord(ICollection<MotionAnimationControl> filteredMacs)
static IEnumerator AssetManagerReady()
void AtomParentChanged(Atom atom, Atom newParent)
void AtomSubSceneChanged(Atom atom, SubScene newSubScene)
AudioSourceControl AudioSourceControlrNameToAudioSourceControl(string controllerName)
protected bool BuildFilesToMigrateMap()
protected void BuildMigrationMappings()
void CancelMigrateFiles()
void CancelRemoveObsoleteFiles()
protected void CheckAutoConnectLeapHands()
protected bool CheckHoldLoad()
protected bool CheckIfControllerLinkedToMotionControl(Transform t, FreeControllerV3 fc)
protected bool CheckLoadingIcon()
protected void CheckMessageAndErrorQueue()
protected void CheckResumeAutoSimulation()
protected void CheckResumeRender()
protected void CheckResumeSimulation()
void ClearAtomPool()
void ClearErrors()
protected void ClearFileBrowsersCurrentPath()
void ClearHeadPossess()
void ClearMessages()
void ClearPossess(bool excludeHeadClear, FreeControllerV3 specificController)
void ClearPossess(bool excludeHeadClear)
void ClearPossess()
void ClearScene()
void ClearSelection(bool syncSelectedUI)
void CloseAllWorldUIPanels()
void CloseErrorLogPanel()
void CloseErrorSplash()
void CloseHub()
void CloseMessageLogPanel()
void CloseMessageSplash()
void CloseTemplateWorldDialog()
protected void CompleteTwoStagePossess()
protected void ConnectLeapHandLeft()
protected void ConnectLeapHandRight()
UIDynamicButton CreateDynamicButton(RectTransform parent)
UIDynamicSlider CreateDynamicSlider(RectTransform parent)
UIDynamicToggle CreateDynamicToggle(RectTransform parent)
protected RectTransform CreateDynamicUIElement(RectTransform parent, RectTransform prefab)
protected MVRScriptController CreateScriptController(string scriptUid, string url, ScriptType type)
void CycleSelectAtomOfType(string type)
void DeactivateWorldUI()
protected void DestroyScriptController(MVRScriptController mvrsc)
protected void DetermineAtomsInAtom(Atom startingAtom, Atom atom, HashSet<Atom> atomsHash, HashSet<Atom> nestedSubSceneAtomsHash, bool isInNestedSubScene)
protected void DetermineVRRig()
void DisableRemoteHoldGrab()
protected void DisconnectLeapHandLeft()
protected void DisconnectLeapHandRight()
void DoSaveScreenshot(string saveName, ScreenShotCallback callback)
void EnableRemoteHoldGrab()
void EndIsolateEditAtom()
void EndIsolateEditSubScene()
void Error(string err, bool logToFile, bool splash)
void FocusOnController(FreeControllerV3 controller, bool rotationOnly, bool alignUpDown)
void FocusOnSelectedController()
void FocusOnSelectedController(bool rotationOnly, bool alignUpDown)
FreeControllerV3 FreeControllerNameToFreeController(string controllerName)
void GarbageCollect()
List<AnimationPattern> GetAllAnimationPatterns()
List<AnimationStep> GetAllAnimationSteps()
List<FreeControllerV3> GetAllFreeControllers()
Atom GetAtomByUid(string uid)
List<string> GetAtomCategories()
protected Atom GetAtomOfTypeFromPool(string atomType)
List<Atom> GetAtoms()
List<string> GetAtomUIDs()
List<string> GetAtomUIDsWithAudioSourceControls()
List<string> GetAtomUIDsWithForceProducers()
List<string> GetAtomUIDsWithForceReceivers()
List<string> GetAtomUIDsWithFreeControllers()
List<string> GetAtomUIDsWithRhythmControllers()
List<string> GetAtomUIDsWithRigidbodies()
List<string> GetAudioSourceControlNamesInAtom(string atomUID)
GameObject GetCachedPrefab(string assetBundleName, string assetName)
bool GetCancel()
string[] GetDirectoriesAtPath(string path, string pattern)
void GetDirectoryPathDialog(FileBrowserCallback callback, string suggestedFolder, List<ShortCut> shortCuts, bool fullComputerBrowse)
string[] GetFilesAtPath(string path, string pattern)
List<string> GetForceProducerNamesInAtom(string atomUID)
List<string> GetForceReceiverNamesInAtom(string atomUID)
List<string> GetFreeControllerNamesInAtom(string atomUID)
Vector4 GetFreeNavigateVector(SteamVR_Action_Vector2 moveAction, bool ignoreDisable)
bool GetGrabNavigateLeft()
bool GetGrabNavigateRight()
bool GetGrabNavigateStartLeft()
bool GetGrabNavigateStartRight()
bool GetLeftCancel()
bool GetLeftGrab()
bool GetLeftGrabRelease()
float GetLeftGrabVal()
bool GetLeftHoldGrab()
bool GetLeftRemoteGrab()
bool GetLeftRemoteGrabRelease()
bool GetLeftRemoteHoldGrab()
bool GetLeftSelect()
bool GetLeftToggleHand()
bool GetLeftUIPointerShow()
string GetMD5Hash(byte[] bytes)
void GetMediaPathDialog(FileBrowserCallback callback, string filter, string suggestedFolder, bool fullComputerBrowse, bool showDirs, bool showKeepOpt, string fileRemovePrefix, bool hideExtenstion, List<ShortCut> shortCuts, bool browseVarFilesAsDirectories, bool showInstallFolderInDirectoryList)
void GetMediaPathDialog(FileBrowserFullCallback callback, string filter, string suggestedFolder, bool fullComputerBrowse, bool showDirs, bool showKeepOpt, string fileRemovePrefix, bool hideExtenstion, List<ShortCut> shortCuts, bool browseVarFilesAsDirectories, bool showInstallFolderInDirectoryList)
protected void GetMediaPathDialogInternal(string filter, string suggestedFolder, bool fullComputerBrowse, bool showDirs, bool showKeepOpt, string fileRemovePrefix, bool hideExtension, List<ShortCut> shortCuts, bool browseVarFilesAsDirectories, bool showInstallFolderInDirectoryList)
bool GetMenuMoveLeft()
bool GetMenuMoveRight()
bool GetMenuShow()
bool GetMouseRelease()
bool GetMouseSelect()
List<FreeControllerV3> GetOverlappingTargets(Transform processFrom, float overlapRadius)
IEnumerable<string> GetResolvedVersionDefines()
List<string> GetRhythmControllerNamesInAtom(string atomUID)
bool GetRightCancel()
bool GetRightGrab()
bool GetRightGrabRelease()
float GetRightGrabVal()
bool GetRightHoldGrab()
bool GetRightRemoteGrab()
bool GetRightRemoteGrabRelease()
bool GetRightRemoteHoldGrab()
bool GetRightSelect()
bool GetRightToggleHand()
bool GetRightUIPointerShow()
List<string> GetRigidbodyNamesInAtom(string atomUID)
JSONClass GetSaveJSON(Atom specificAtom, bool includePhysical, bool includeAppearance)
void GetScenePathDialog(FileBrowserCallback callback)
Atom GetSelectedAtom()
FreeControllerV3 GetSelectedController()
protected static string GetSha256Hash(SHA256 shaHash, string input, int length)
bool GetTargetShow()
bool GetTeleportFinish(bool inTeleportMode)
bool GetTeleportFinishLeft(bool inTeleportMode)
bool GetTeleportFinishRight(bool inTeleportMode)
bool GetTeleportShow(bool inTeleportMode)
bool GetTeleportShowLeft(bool inTeleportMode)
bool GetTeleportShowRight(bool inTeleportMode)
bool GetTeleportStart(bool inTeleportMode)
bool GetTeleportStartLeft(bool inTeleportMode)
bool GetTeleportStartRight(bool inTeleportMode)
string GetTempUID()
string GetVersion()
List<string> GetVisibleAtomUIDs()
void HardReset()
void HideMainHUD()
void HideMonitorUI()
void HideTempHelp()
void HoldLoadComplete(AsyncFlag af)
protected void IncrementLoadingSlider()
protected void InitAssetBundleDictionaries()
protected void InitAtomPool()
protected void InitAudioListeners()
bool IsSimulationResetting()
protected KeyType IsValidKey(string key)
void Load(string saveName)
protected IEnumerator LoadAtomFromBundleAsync(AtomAsset aa, string useuid, bool userInvoked, bool forceSelect, bool forceFocus)
protected void LoadBootstrapPlugin(string url)
protected IEnumerator LoadCo(bool clearOnly, bool loadMerge)
protected void LoadDialog(string lastPath, bool resetFileFormat)
void LoadForEdit(string saveName)
protected void LoadForEditFromSceneDialog(string saveName)
void LoadFromJSONEmbed(JSONEmbed je, bool loadMerge, bool editMode)
protected void LoadFromSceneDialog(string saveName)
protected void LoadFromSceneWorldDialog(string saveName)
protected void LoadFromTemplateWorldDialog(string saveName)
protected void LoadInternal(string saveName, bool loadMerge, bool editMode)
JSONNode LoadJSON(string saveName)
void LoadMerge(string saveName)
protected void LoadMergeFromSceneDialog(string saveName)
void LoadMergeSceneDialog()
void LoadRestoreUI(string saveName)
void LoadSceneDialog()
void LoadSceneForEditDialog()
void LoadSceneWorldDialog(bool fromWorld)
void LoadSceneWorldDialogWithPath(string path)
void LoadTemplateWorldDialog(bool fromWorld)
static void LogError(string err, bool logToFile)
static void LogError(string err, bool logToFile, bool splash)
static void LogError(string err)
static void LogMessage(string msg, bool logToFile)
static void LogMessage(string msg, bool logToFile, bool splash)
static void LogMessage(string msg)
void Message(string msg, bool logToFile, bool splash)
void MigrateFilesInMigrateMap()
protected bool MotionControlPossess(Transform motionController, FreeControllerV3 controllerToPossess, bool usePossessorSnapPoint, bool adjustSpring)
void MoveMainHUD(Vector3 v)
void MoveMainHUD(Transform t)
void MoveToSceneLoadPosition()
void NewScene()
void NewScenePlayMode()
string NormalizeDirectoryPath(string path)
string NormalizeLoadPath(string path)
string NormalizeMediaPath(string path)
string NormalizePath(string path)
string NormalizeSavePath(string path)
string NormalizeScenePath(string path)
void NotifySubSceneLoad(SubScene subScene)
protected void OnPackageRefresh()
void OpenAddAtomUIToAtomType(string type)
void OpenErrorLogPanel()
void OpenFolderInExplorer(string path)
void OpenHub()
void OpenHubFromWorldUI()
void OpenLinkInBrowser(string url)
void OpenMessageLogPanel()
void OpenPackageBuilder()
void OpenPackageDownloader()
void OpenPackageInManager(string packageUid)
void OpenPackageManager()
void OpenSteamVRBindingsInBrowser()
protected void OpenTopWorldUI()
static void OpenURL(string url)
protected static void OpenURLDirect(string url)
void OpenVamXMainScene()
void OpenVamXTutorialScene()
void OpenWizard()
void OutputEncryptedKey(string keyval)
void PauseAutoSimulation(AsyncFlag af)
void PauseRender(AsyncFlag af)
void PauseSimulation(int numFrames, string pauseName, bool hidden)
void PauseSimulation(AsyncFlag af, bool hidden)
void PauseSimulation(int numFrames, string pauseName)
void PauseSyncAtomLists()
void playerHeightAdjustAdjust(float val)
void PopOverrideLoadDir()
void ProcessAnimationRecord()
bool ProcessControllerTargetHighlight(SelectionHUD sh, Transform processFromPointer, Transform processFromOverlap, bool ptrMode, List<FreeControllerV3> highlightedControllers, bool uihit, FreeControllerV3 excludeController, out bool isOverlap, float overlapRadius)
void ProcessTargetSelectionCycleSelectMouse()
ForceProducerV2 ProducerNameToForceProducer(string producerName)
void PurgeImageCache()
void PushAudioListener(AudioListener audioListener)
void PushOverrideLoadDirFromFilePath(string path)
protected void PutAtomBackInPool(Atom a)
void QuickReloadIsolatedSubScene()
void QuickSaveIsolatedSubScene()
void Quit()
string ReadFileIntoString(string path)
ForceReceiver ReceiverNameToForceReceiver(string receiverName)
void RegisterCharacterRun(DAZCharacterRun dAZCharacterRun)
void RegisterPrefab(string assetBundleName, string assetName, GameObject prefab)
void ReleaseTempUID(string uid)
void RemoveAtom(Atom atom, bool syncList)
void RemoveAtom(Atom atom)
void RemoveAudioListener(AudioListener audioListener)
void RemoveCanvas(Canvas c)
protected void RemoveLegacyDirectories(StreamWriter log)
protected void RemoveNonStartingAtoms()
void RemoveObsoleteDirectories()
void RenameAtom(Atom atom, string requestedID)
void ReportLoadedAssetBundles()
void RescanPackages()
void ResetFocusPoint()
void ResetMonitorCenterCamera()
void ResetNavigationRigPositionRotation()
void ResetSimulation(AsyncFlag af, bool hidden)
void ResetSimulation(int numFrames, string pauseName, bool hidden)
void ResumeSyncAtomLists()
RhythmController RhythmControllerrNameToRhythmController(string controllerName)
Rigidbody RigidbodyNameToRigidbody(string rigidbodyName)
void Save(string saveName)
void Save(string saveName, Atom specificAtom, bool includePhysical, bool includeAppearance, ScreenShotCallback callback, bool isOverwrite)
void SaveAddDependency(Atom saveAtom)
protected void SaveAndAddToCurrentPackage(string saveName, bool isOverwrite)
protected void SaveAndAddToCurrentPackageFromDialog(string saveName)
protected void SaveAndAddToNewPackage(string saveName, bool isOverwrite)
protected void SaveAndAddToNewPackageFromDialog(string saveName)
void SaveConfirm(string option)
void SaveFromAtom(string saveName, Atom specificAtom, bool includePhysical, bool includeAppearance, ScreenShotCallback callback, bool isOverwrite)
protected void SaveFromDialog(string saveName)
void SaveJSON(JSONClass jc, string saveName)
void SaveJSON(JSONClass jc, string saveName, UserActionCallback confirmCallback, UserActionCallback denyCallback, ExceptionCallback exceptionCallback)
protected void SaveLegacyPackageFromDialog(string saveName)
void SaveSceneCurrentAddonPackageDialog()
void SaveSceneDialog()
void SaveSceneDialog(FileBrowserCallback callback)
void SaveSceneLegacyPackageDialog()
void SaveSceneNewAddonPackageDialog()
void SaveStringIntoFile(string path, string contents)
void SelectController(string atomName, string controllerName, bool alignView, bool alignRotationOnly, bool alignUpDown, bool openUI)
void SelectController(FreeControllerV3 controller, bool alignView, bool alignRotationOnly, bool alignUpDown, bool openUI)
void SelectIsolatedAtom()
void SelectIsolatedSubScene()
void SelectLastAddedAtom()
void SelectModeAnimationRecord(MotionAnimationMaster animationMaster)
void SelectModeAnimationRecord()
void SelectModeArmedForRecord(IEnumerable<MotionAnimationControl> macsToChooseFrom)
void SelectModeArmedForRecord()
void SelectModeAtom(SelectAtomCallback sac)
void SelectModeControllers(SelectControllerCallback scc)
void SelectModeCustom(string helpText)
void SelectModeCustomWithTargetControl(string helpText)
void SelectModeCustomWithVRTargetControl(string helpText)
void SelectModeForceProducers(SelectForceProducerCallback sfpc)
void SelectModeForceReceivers(SelectForceReceiverCallback sfrc)
void SelectModeFreeMove()
void SelectModeFreeMoveMouse()
void SelectModeOff()
void SelectModeOffAndShowMainHUDAuto()
void SelectModePossess(bool excludeHeadClear)
void SelectModePossessAndAlign()
void SelectModeRigidbody(SelectRigidbodyCallback srbc)
void SelectModeScreenshot()
void SelectModeTeleport()
void SelectModeTwoStagePossess()
void SelectModeTwoStagePossessNoClear()
void SelectModeUnpossess()
void SetActiveUI(string uiName)
protected void SetAddAtomAtomPopupValues(string category)
void SetAlignRotationOffset(string type)
void SetAtomAssetsFromFile()
void SetCustomUI(Transform cui)
void SetFreezeAnimation(bool freeze)
void SetHelpHUDText(string txt)
void SetLeftSelect()
void SetLoadDirFromFilePath(string path)
void SetLoadFlag()
void SetLoadingIconFlag(AsyncFlag af)
void SetMainMenuTab(string tabName)
protected void SetMonitorRig()
void SetNullSaveDir()
protected void SetOculusRig()
void SetOculusThumbstickFunctionFromString(string str)
void SetOnlyShowControllers(HashSet<FreeControllerV3> onlyControllers)
protected void SetOpenVRRig()
protected void SetPauseAutoSimulation(bool b)
protected void SetPauseRender(bool b)
void SetRightSelect()
void SetSaveDirFromFilePath(string path)
protected void SetSavesDirFromCommandline()
void SetSceneLoadPosition()
protected void SetSelectionHUDHeader(SelectionHUD sh, string txt)
void SetToLastActiveUI()
void SetUISide(string side)
void SetUserPrefsTab(string tabName)
void SetUseSceneLoadPosition(bool b)
void ShowMainHUD(bool setAnchors, bool forceMonitor)
void ShowMainHUDAuto()
void ShowMainHUDMonitor()
void ShowMonitorUI()
void ShowTempHelp(string text)
protected void SplashNavigationHologrid(float seconds)
void StartIsolateEditAtom(Atom atom)
void StartIsolateEditSubScene(SubScene subScene)
void StartPlayback()
void StartScene()
void StartSceneForEdit()
void StopPlayback()
void StopRecording()
protected void SyncActiveHands()
protected void SyncAdvancedSceneEditModeTransforms()
protected void SyncAllErrorsInputFields()
protected void SyncAllMessagesInputFields()
protected void SyncAudioListener()
protected void SyncAutoFreezeAnimationOnSwitchToEditMode(bool b)
protected void SyncCursor()
protected void SyncFreezeAnimation()
protected void SyncGenerateDepthTexture()
protected void SyncHelpText()
void SyncHiddenAtoms()
protected void SyncIsolatedAtom()
protected void SyncIsolatedSubScene()
protected void SyncLeapEnabled()
void SyncMonitorRigPosition()
protected void SyncNavigationHologridVisibility()
protected void SyncOnStartupSkipStartScreen(bool b)
protected Transform SyncToDisplayChoice(DisplayUIChoice displayUIChoice)
protected void SyncToKeyFile(bool userInvoked)
protected IEnumerator SyncToKeyFilePackageRefresh()
void SyncTracker1Visibility()
void SyncTracker2Visibility()
void SyncTracker3Visibility()
void SyncTracker4Visibility()
void SyncTracker5Visibility()
void SyncTracker6Visibility()
void SyncTracker7Visibility()
void SyncTracker8Visibility()
protected void SyncTrackerVisibility()
protected void SyncUISide()
void SyncUIToUnlockLevel()
protected void SyncVamX()
protected void SyncVersion()
void SyncVersionText()
void SyncVisibility()
protected void SyncWorldUIAnchor()
protected void SyncWorldUIOverlaySky()
protected void SyncWorldUIVRAnchorDistance(float f)
protected void SyncWorldUIVRAnchorHeight(float f)
protected void TestDirectoryCallback(string dir)
void TestDirectoryPathBrowse()
void ToggleDisableAllNavigation()
void ToggleMainHUD()
void ToggleMainHUDAuto()
void ToggleMainHUDMonitor()
void ToggleMainMonitor()
void ToggleModeFreeMoveMouse()
void ToggleMonitorUI()
void ToggleRotationMode()
void ToggleShowHiddenAtoms()
void ToggleTargetsOnWithButton()
protected void UnloadAllBootstrapPlugins()
protected void UnloadBootstrapPlugin(string url)
void UnloadUnusedResources()
protected void UnregisterAllPrefabsFromAtoms()
void UnregisterCharacterRun(DAZCharacterRun dAZCharacterRun)
void UnregisterPrefab(string assetBundleName, string assetName)
protected void UpdateLoadingStatus(string txt)
void ValidateAllAtoms()
protected void VerifyPossess()

// nested types
enum KeyType { Invalid, Free, Teaser, Entertainer, NSteam, Steam, Retail, Creator }
delegate ScreenShotCallback(string imgPath)
enum GameMode { Edit, Play }
enum DisplayUIChoice { Auto, Normal, World }
enum AlignRotationOffset { None, Right, Left }
enum ActiveUI { None, MainMenu, MainMenuOnly, SelectedOptions, MultiButtonPanel, EmbeddedScenePanel, OnlineBrowser, PackageBuilder, PackageManager, PackageDownloader, Custom }
enum SelectMode { Off, FilteredTargets, Targets, Controller, ForceReceiver, ForceProducer, Rigidbody, Atom, Possess, TwoStagePossess, PossessAndAlign, Unpossess, AnimationRecord, ArmedForRecord, Teleport, FreeMove, FreeMoveMouse, SaveScreenshot, Screenshot, Custom, CustomWithTargetControl, CustomWithVRTargetControl }
delegate SelectControllerCallback(FreeControllerV3 fc)
delegate SelectForceProducerCallback(ForceProducerV2 fp)
delegate SelectForceReceiverCallback(ForceReceiver fr)
delegate SelectRigidbodyCallback(Rigidbody rb)
delegate SelectAtomCallback(Atom a)
delegate OnForceReceiverNamesChanged(string[] receiverNames)
delegate OnForceProducerNamesChanged(string[] producerNames)
delegate OnRhythmControllerNamesChanged(string[] controllerNames)
delegate OnAudioSourceControlNamesChanged(string[] controlNames)
delegate OnFreeControllerNamesChanged(string[] controllerNames)
delegate OnRigidbodyNamesChanged(string[] rigidbodyNames)
class AtomAsset
delegate OnAtomUIDsChanged(List<string> atomUIDs)
delegate OnAtomUIDsWithForceReceiversChanged(List<string> atomUIDs)
delegate OnAtomUIDsWithForceProducersChanged(List<string> atomUIDs)
delegate OnAtomUIDsWithFreeControllersChanged(List<string> atomUIDs)
delegate OnAtomUIDsWithRigidbodiesChanged(List<string> atomUIDs)
delegate OnAtomUIDRename(string oldName, string newName)
delegate OnAtomParentChanged(Atom atom, Atom newParent)
delegate OnAtomSubSceneChanged(Atom atom, SubScene newSubScene)
delegate OnAtomAdded(Atom atom)
delegate OnAtomRemoved(Atom atom)
delegate OnGameModeChanged(GameMode gameMode)
delegate OnSceneLoaded()
delegate OnSubSceneLoaded(SubScene subScene)
delegate OnBeforeSceneSave()
delegate OnSceneSaved()
enum ThumbstickFunction { GrabWorld, SwapAxis, Both }

```

### Atom

extends `JSONStorable`

```csharp
// constructors
new Atom()

// fields
protected bool _collisionEnabled
protected SubScene _containingSubScene
protected float _currentScale
protected bool _freezePhysics
protected bool _globalDisableRender
protected bool _grabFreezePhysics
protected bool _hidden
protected bool _insideSyncOnToggleObjects
protected bool _isPhysicsFrozen
protected bool _isResettingPhysics
protected bool _isResettingPhysicsFull
protected bool _keepParamLocksWhenPuttingBackInPool
protected bool _on
protected bool _resetPhysicsDisableCollision
protected bool _resetSimulation
protected string _subScenePath
protected bool _tempDisableCollision
protected bool _tempDisableRender
protected bool _tempFreezePhysics
protected bool _tempHidden
protected bool _tempOff
protected bool _useRigidbodyInterpolation
bool alwaysShowExtents
bool canBeParented
string category
Transform childAtomContainer
JSONStorableBool collisionEnabledJSON
Text descriptionText
Text descriptionTextAlt
bool destroyed
bool doNotZeroReParentObject
bool excludeFromGlobalDisableRender
bool excludeFromTempDisableCollision
bool excludeFromTempDisableRender
bool excludeFromTempFreezePhysics
float extentPadding
JSONStorableBool freezePhysicsJSON
protected AsyncFlag grabFreezePhysicsFlag
protected JSONStorableBool hiddenJSON
InputField idText
InputFieldAction idTextAction
InputFieldAction idTextActionAlt
InputField idTextAlt
bool inPool
protected JSONStorableAction IsolateEditAtomAction
bool isPoolable
bool isSubSceneType
JSONStorableBool keepParamLocksWhenPuttingBackInPoolJSON
protected string lastLoadAppearanceDir
protected string lastLoadPhysicalDir
protected string lastLoadPresetDir
protected bool lastRestoreAppearance
protected JSONClass lastRestoredData
protected bool lastRestorePhysical
protected string loadedAppearanceName
bool loadedFromBundle
protected string loadedName
protected string loadedPhysicalName
FreeControllerV3 mainController
PresetManagerControl mainPresetControl
protected JSONStorableBool onJSON
Transform[] onToggleObjects
UIPopup parentAtomSelectionPopup
protected AsyncFlag pauseAutoSimulationFlag
Transform reParentObject
protected AsyncFlag resetPhysicsFlag
protected JSONStorableAction ResetPhysicsJSONAction
protected JSONStorableFloat resetPhysicsProgressJSON
protected Coroutine resetRoutine
protected bool saveIncludeAppearance
protected bool saveIncludePhysical
protected Dictionary<Rigidbody, RigidbodyConstraints> saveRigidbodyContraints
Button selectAtomParentFromSceneButton
protected JSONStorableAction SelectContainingSubSceneAction
protected JSONStorableAction toggleOnJSON
string type
protected List<AsyncFlag> waitResumeSimulationFlags

// properties
AnimationPattern[] animationPatterns { get; }
AnimationStep[] animationSteps { get; }
Animator[] animators { get; }
AudioSourceControl[] audioSourceControls { get; }
AutoColliderBatchUpdater[] autoColliderBatchUpdaters { get; }
List<Canvas> canvases { get; }
bool collisionEnabled { get; set; }
protected SubScene containingSubScene { get; set; }
string description { get; set; }
ForceProducerV2[] forceProducers { get; }
ForceReceiver[] forceReceivers { get; }
FreeControllerV3[] freeControllers { get; }
bool globalDisableRender { get; set; }
bool grabFreezePhysics { get; set; }
GrabPoint[] grabPoints { get; }
bool hidden { get; set; }
bool hiddenNoCallback { get; set; }
bool isPhysicsFrozen { get; }
protected bool isPreparingToPutBackInPool { get; set; }
bool isSubSceneRestore { get; set; }
bool keepParamLocksWhenPuttingBackInPool { get; }
Rigidbody[] linkableRigidbodies { get; }
FreeControllerV3 masterController { get; set; }
MotionAnimationControl[] motionAnimationControls { get; }
bool on { get; }
Atom parentAtom { get; set; }
PhysicsSimulator[] physicsSimulators { get; }
PhysicsSimulatorJSONStorable[] physicsSimulatorsStorable { get; }
PlayerNavCollider[] playerNavColliders { get; }
protected List<PresetManagerControl> presetManagerControls { get; set; }
Rigidbody[] realRigidbodies { get; }
protected bool resetPhysicsDisableCollision { get; set; }
protected bool resetSimulation { get; set; }
RhythmController[] rhythmControllers { get; }
Rigidbody[] rigidbodies { get; }
ScaleChangeReceiverJSONStorable[] scaleChangeReceiverJSONStorables { get; }
ScaleChangeReceiver[] scaleChangeReceivers { get; }
protected SubScene subSceneComponent { get; set; }
protected string subScenePath { get; set; }
bool tempDisableCollision { get; set; }
bool tempDisableRender { get; set; }
bool tempFreezePhysics { get; set; }
bool tempHidden { get; set; }
bool tempOff { get; set; }
string uid { get; set; }
string uidWithoutSubScenePath { get; }
bool useRigidbodyInterpolation { get; set; }

// methods
void AddCanvas(Canvas c)
void AddChild(Atom a)
void AlertPhysicsCorruption(string type)
protected void Awake()
protected void CheckResumeSimulation()
void ClearParentAtom()
protected void ClearPauseAutoSimulationFlag()
protected void ClearResetPhysics()
void DeregisterDynamicMeshRenderer(MeshRenderer mr)
void DeregisterDynamicPhysicsSimulator(PhysicsSimulator ps)
void DeregisterDynamicPhysicsSimulatorJSONStorable(PhysicsSimulatorJSONStorable ps)
void DeregisterDynamicRenderSuspend(RenderSuspend rs)
void DeregisterDynamicScaleChangeReceiver(ScaleChangeReceiver scr)
void DeregisterDynamicScaleChangeReceiverJSONStorable(ScaleChangeReceiverJSONStorable scr)
HashSet<Atom> GetChildren()
JSONStorable GetStorableByID(string storeid)
List<string> GetStorableIDs()
void InitUI()
void InitUIAlt()
protected void IsolateEditAtom()
protected bool IsOnInAtomHierarchy(Atom currentAtom)
void LateRestore(JSONClass jc, bool restorePhysical, bool restoreAppearance, bool restoreCore, bool isSubSceneRestore, bool setMissingToDefault, bool isTheSubSceneAtom)
void LoadAppearancePreset(string saveName)
void LoadAppearancePresetDialog()
void LoadPhysicalPreset(string saveName)
void LoadPhysicalPresetDialog()
void LoadPreset(string saveName)
void LoadPresetDialog()
protected void OnAtomRenamed(string oldid, string newid)
protected void OnAtomSubSceneChanged(Atom atom, SubScene subScene)
void OnPreRemove()
void OnRemove()
void PostRestore(bool restorePhysical, bool restoreAppearance)
void PostRestore()
void PrepareToPutBackInPool()
void PreRestore(bool restorePhysical, bool restoreAppearance)
void PreRestore()
void PreRestoreForSubScene()
void RecalculateSubScenePath()
bool RegisterAdditionalStorable(JSONStorable js)
void RegisterDynamicMeshRenderer(MeshRenderer mr)
void RegisterDynamicPhysicsSimulator(PhysicsSimulator ps)
void RegisterDynamicPhysicsSimulatorJSONStorable(PhysicsSimulatorJSONStorable ps)
void RegisterDynamicRenderSuspend(RenderSuspend rs)
void RegisterDynamicScaleChangeReceiver(ScaleChangeReceiver scr)
void RegisterDynamicScaleChangeReceiverJSONStorable(ScaleChangeReceiverJSONStorable scr)
void Remove()
void RemoveAllChildAtoms()
void RemoveCanvas(Canvas c)
void RemoveChild(Atom a)
void Reset(bool resetSimulation)
void ResetAppearance()
void ResetPhysical()
protected void ResetPhysics()
void ResetPhysics(bool fullReset, bool fullResetControls)
protected IEnumerator ResetPhysicsCo(bool full, bool fullResetControls)
void ResetRigidbodies()
void ResetSimulation(AsyncFlag af)
void Restore(JSONClass jc, bool restorePhysical, bool restoreAppearance, bool restoreCore, JSONArray presetAtoms, bool isClear, bool isSubSceneRestore, bool setMissingToDefault, bool isTheSubSceneAtom)
void RestoreForceInitialize()
void RestoreFromLast(JSONStorable js)
void RestoreParentAtom(JSONClass jc)
void RestoreStartingOn()
void RestoreTransform(JSONClass jc, bool setUnlistedToDefault)
void SavePreset(string saveName)
void SavePresetDialog(bool includePhysical, bool includeAppearance)
void ScaleChanged(float sc)
void SelectAtomParent(Atom a)
void SelectAtomParentFromScene()
protected void SelectContainingSubScene()
void SetCollisionEnabled(bool b)
void SetFreezePhysics(bool b)
void SetLastRestoredData(JSONClass jc, bool isAppearance, bool isPhysical)
void SetOn(bool b)
void SetParentAtom(Atom a)
void SetParentAtom(string atomUID)
void SetParentAtomSelectPopupValues()
void SetUID(string val)
protected void SetUIDToInputField()
protected void SetUIDToInputFieldAlt()
void Store(JSONArray atoms, bool includePhysical, bool includeAppearance)
void StoreForSubScene(JSONClass jc, bool isTheSubSceneAtom)
protected void SyncCollisionEnabled(bool b)
protected void SyncDescriptionText()
protected void SyncFreezePhysics(bool b)
protected void SyncFreezePhysics()
protected void SyncHidden(bool b)
protected void SyncIdText()
protected void SyncKeepParamLocksWhenPuttingBackInPool(bool b)
protected void SyncOn(bool b)
protected void SyncOnToggleObjects()
protected void SyncRenderSuspend()
protected void SyncRigidbodyInterpolation()
protected void SyncSelectContainingSubSceneButton()
void ToggleOn()
void UnregisterAdditionalStorable(JSONStorable js)
void Validate()
protected void WalkAndRecalculateSubScenePath(Atom atom)
protected void ZeroReParentObjectTransform()

```

### SubScene

extends `JSONStorable`

```csharp
// constructors
new SubScene()

// fields
protected HashSet<Atom> _atomsInSubScene
protected bool _drawContainedAtomsLines
protected bool _loadOnRestoreFromOtherSubScene
protected JSONStorableAction AddLooseAtomsToSubSceneAction
protected JSONStorableBool autoSetSubSceneUIDToSignatureOnBrowseLoadJSON
protected JSONStorableUrl browsePathJSON
protected JSONStorableAction ClearPackageUidAction
protected JSONStorableAction ClearSubSceneAction
protected JSONStorableString creatorNameJSON
protected JSONStorableBool drawContainedAtomsLinesJSON
bool isIsolateEditing
protected JSONStorableAction IsolateEditSubSceneAction
protected LineDrawer lineDrawer
Material lineMaterial
protected JSONStorableBool loadOnRestoreFromOtherSubSceneJSON
protected JSONStorableAction LoadSubSceneAction
protected JSONStorableActionPresetFilePath loadSubSceneWithPathJSON
protected JSONStorableUrl loadSubSceneWithPathUrlJSON
MotionAnimationMaster motionAnimationMaster
protected JSONStorableString packageUidJSON
protected JSONStorableAction SetToYourCreatorNameAction
protected JSONStorableString signatureJSON
protected JSONStorableString storedCreatorNameJSON
protected JSONStorableString storeNameJSON
protected JSONStorableUrl storePathJSON
protected string storeRoot
protected Dictionary<string, string> storeRootMap
protected JSONStorableAction StoreSubSceneAction
protected JSONStorableAction UnparentAllAtomsAction

// properties
IEnumerable<Atom> atomsInSubScene { get; }

// methods
protected void AddAtomToSubScene(Atom atom)
protected void AddLooseAtomsToSubScene()
protected void Awake()
protected void BeginBrowse(JSONStorableUrl jsurl)
bool CheckExistance()
bool CheckReadyForStore()
protected void ClearPackageUid()
protected void ClearSubScene()
protected string GetStorePath(bool includePackage)
protected void Init()
protected void InitUI(Transform t, bool isAlt)
protected bool IsAtomInThisSubScene(Atom atom)
protected void IsolateEditSubScene()
void LoadSubScene()
protected IEnumerator LoadSubSceneCo(JSONClass inputJSON)
protected void LoadSubSceneWithPath(string p)
protected void OnAtomParentChanged(Atom atom, Atom newParent)
protected void OnDestroy()
void PostRestore()
void PreRemove()
protected void RemoveAtomFromSubScene(Atom atom)
protected void SetNamesFromFilePath(string fpath, bool noCallback)
protected void SetStorePathFromParts()
protected void SetToYourCreatorName()
void StoreSubScene()
protected void SyncBrowsePath(string url)
protected void SyncCreatorName(string s)
protected void SyncDrawContainedAtomsLines(bool b)
protected void SyncLoadButton()
protected void SyncLoadOnRestoreFromOtherSubScene(bool b)
protected void SyncPackageUid(string s)
protected void SyncSignature(string s)
protected void SyncStoreButton()
protected void SyncStoreName(string s)
protected void SyncStorePath(string url)
protected void UnparentAllAtoms()
protected void Update()

```

### FreeControllerV3

extends `PhysicsSimulatorJSONStorable`

```csharp
// constructors
new FreeControllerV3()

// fields
protected bool _forceStorePositionRotationAsLocal
protected bool _isGrabbing
float _RBLockPositionMaxForce
float _RBLockRotationMaxForce
protected List<object> allocatedObjects
Transform alsoMoveWhenInactive
Transform alsoMoveWhenInactiveAlternate
Transform alsoMoveWhenInactiveParentWhenActive
Transform alsoMoveWhenInactiveParentWhenInactive
protected JSONStorableBool canGrabPositionJSON
protected JSONStorableBool canGrabRotationJSON
protected int complyPauseFrames
protected float complyPositionThreshold
protected JSONStorableFloat complyPositionThresholdJSON
protected float complyRotationThreshold
protected JSONStorableFloat complyRotationThresholdJSON
protected float complySpeed
protected JSONStorableFloat complySpeedJSON
Transform control
bool controlsCollisionEnabled
bool controlsOn
protected FreeControllerV3UI currentFCUI
protected FreeControllerV3UI currentFCUIAlt
protected string[] customParamNames
protected JSONStorableBool deactivateOtherControlsOnPossessJSON
bool debug
Mesh deselectedMesh
float deselectedMeshScale
float distanceHolder
DrawAxisnames DrawForwardAxis
bool drawMesh
bool drawMeshWhenDeselected
bool drawSnapshot
DrawAxisnames DrawUpAxis
bool enableSelectRoot
Transform focusPoint
Transform follow
Color followingColor
Mesh followingPositionMesh
Mesh followingRotationMesh
Transform followWhenOff
float forceFactor
bool freezeAtomPhysicsWhenGrabbed
protected JSONStorableBool freezeAtomPhysicsWhenGrabbedJSON
bool GUIalwaysVisibleWhenSelected
Color highlightColor
float highlightedScale
Color holdColor
Mesh holdPositionMesh
Mesh holdRotationMesh
protected JSONStorableBool interactableInPlayModeJSON
Material linkLineMaterial
UIPopup linkToAtomSelectionPopup
protected string linkToAtomUID
UIPopup linkToSelectionPopup
Color lockColor
Mesh lockPositionMesh
Mesh lockRotationMesh
Transform lookAt
Color lookAtColor
Mesh lookAtRotationMesh
Material material
DrawAxisnames MeshForwardAxis
float meshScale
DrawAxisnames MeshUpAxis
MoveAxisnames MoveAxis1
MoveAxisnames MoveAxis2
MoveAxisnames MoveAxis3
float moveFactor
Mesh moveModeOverlayMesh
RotationOrder naturalJointDriveRotationOrder
Color offColor
Mesh offPositionMesh
Mesh offRotationMesh
Color onColor
OnGrabEnd onGrabEndHandlers
OnGrabStart onGrabStartHandlers
protected JSONStorableBool onJSON
OnMovement onMovementHandlers
OnPositionChange onPositionChangeHandlers
Mesh onPositionMesh
FreeControllerV3[] onPossessDeactiveList
OnPossessEnd onPossessEndHandlers
OnPossessStart onPossessStartHandlers
OnPositionChange onRotationChangeHandlers
Mesh onRotationMesh
Color overlayColor
protected JSONStorableBool possessableJSON
DrawAxisnames PossessForwardAxis
Transform possessPoint
DrawAxisnames PossessUpAxis
protected JSONStorableAction resetAction
Rigidbody[] rigidbodySlavesForMass
RotateAxisnames RotateAxis1
RotateAxisnames RotateAxis2
RotateAxisnames RotateAxis3
float rotateFactor
Mesh rotateModeOverlayMesh
protected float scalePow
Color selectedColor
float selectedScale
bool startedPossess
Rigidbody startingLinkToRigidbody
Vector3 startingLocalPosition
Quaternion startingLocalRotation
Vector3 startingPosition
PositionState startingPositionState
Quaternion startingRotation
RotationState startingRotationState
bool stateCanBeModified
bool storePositionRotationAsLocal
static float targetAlpha
float torqueFactor
Text UIDText
Text UIDTextAlt
Transform[] UITransforms
Transform[] UITransformsPlayMode
float unhighlightedScale
bool useContainedMeshRenderers
bool useForceWhenOff
InputField xLocalPositionInputField
SetTextFromFloat xLocalPositionText
InputField xLocalRotationInputField
SetTextFromFloat xLocalRotationText
InputField xPositionInputField
SetTextFromFloat xPositionText
InputField xRotationInputField
SetTextFromFloat xRotationText
InputField yLocalPositionInputField
SetTextFromFloat yLocalPositionText
InputField yLocalRotationInputField
SetTextFromFloat yLocalRotationText
InputField yPositionInputField
SetTextFromFloat yPositionText
InputField yRotationInputField
SetTextFromFloat yRotationText
InputField zLocalPositionInputField
SetTextFromFloat zLocalPositionText
InputField zLocalRotationInputField
SetTextFromFloat zLocalRotationText
InputField zPositionInputField
SetTextFromFloat zPositionText
InputField zRotationInputField
SetTextFromFloat zRotationText

// properties
bool canGrabPosition { get; set; }
bool canGrabRotation { get; set; }
bool collisionEnabled { get; set; }
ControlMode controlMode { get; set; }
Transform ControlParentToUse { get; }
Color currentPositionColor { get; }
PositionState currentPositionState { get; set; }
Color currentRotationColor { get; }
RotationState currentRotationState { get; set; }
bool deactivateOtherControlsOnPossess { get; set; }
Rigidbody followWhenOffRB { get; set; }
bool forceStorePositionRotationAsLocal { get; set; }
bool globalCollisionEnabled { get; set; }
bool guihidden { get; set; }
bool hidden { get; set; }
bool highlighted { get; set; }
bool interactableInPlayMode { get; set; }
bool isGrabbing { get; set; }
bool isPositionOn { get; }
bool isRotationOn { get; }
float jointRotationDriveDamper { get; set; }
float jointRotationDriveMaxForce { get; set; }
float jointRotationDriveSpring { get; set; }
float jointRotationDriveXTarget { get; set; }
float jointRotationDriveXTargetAdditional { get; set; }
float jointRotationDriveYTarget { get; set; }
float jointRotationDriveYTargetAdditional { get; set; }
float jointRotationDriveZTarget { get; set; }
float jointRotationDriveZTargetAdditional { get; set; }
Rigidbody linkToRB { get; set; }
bool on { get; set; }
bool physicsEnabled { get; set; }
float positionGrid { get; set; }
GridMode positionGridMode { get; set; }
bool possessable { get; set; }
bool possessed { get; set; }
float RBAngularDrag { get; set; }
float RBComplyJointRotationDriveSpring { get; set; }
float RBComplyPositionDamper { get; set; }
float RBComplyPositionSpring { get; set; }
float RBComplyRotationDamper { get; set; }
float RBComplyRotationSpring { get; set; }
float RBDrag { get; set; }
float RBHoldPositionDamper { get; set; }
float RBHoldPositionMaxForce { get; set; }
float RBHoldPositionSpring { get; set; }
float RBHoldRotationDamper { get; set; }
float RBHoldRotationMaxForce { get; set; }
float RBHoldRotationSpring { get; set; }
float RBLinkPositionDamper { get; set; }
float RBLinkPositionMaxForce { get; set; }
float RBLinkPositionSpring { get; set; }
float RBLinkRotationDamper { get; set; }
float RBLinkRotationMaxForce { get; set; }
float RBLinkRotationSpring { get; set; }
float RBLockPositionDamper { get; set; }
float RBLockPositionMaxForce { get; set; }
float RBLockPositionSpring { get; set; }
float RBLockRotationDamper { get; set; }
float RBLockRotationMaxForce { get; set; }
float RBLockRotationSpring { get; set; }
float RBMass { get; set; }
float RBMaxVelocity { get; set; }
bool RBMaxVelocityEnable { get; set; }
float rotationGrid { get; set; }
GridMode rotationGridMode { get; set; }
bool selected { get; set; }
Vector3 selectedPosition { get; }
bool useGravityOnRBWhenOff { get; set; }
bool xLocalLock { get; set; }
bool xLock { get; set; }
bool xRotLock { get; set; }
bool yLocalLock { get; set; }
bool yLock { get; set; }
bool yRotLock { get; set; }
bool zLocalLock { get; set; }
bool zLock { get; set; }
bool zRotLock { get; set; }

// methods
void AlignTo(Transform t, bool alsoRotateRB)
protected void Awake()
void ControlAxis1(float val)
void ControlAxis2(float val)
void ControlAxis3(float val)
protected void DeregisterFCUI(FreeControllerV3UI fcui)
protected void DestroyAllocatedObjects()
string[] GetCustomParamNames()
Vector3 GetForwardPossessAxis()
JSONClass GetJSON(bool includePhysical, bool includeAppearance, bool forceStore)
Vector3 GetLocalEulerAngles()
Vector3 GetLocalPosition()
Vector3 GetUpPossessAxis()
void HideGUI()
void InitUI()
void InitUIAlt()
void LateRestoreFromJSON(JSONClass jc, bool restorePhysical, bool restoreAppearance, bool setMissingToDefault)
void MoveAbsoluteNoForce(Vector3 direction)
void MoveAbsoluteNoForce(float x, float y, float z)
void MoveAxis(MoveAxisnames man, float val)
void MoveControl(Vector3 newPosition, bool callHandlers)
void MoveControl(Vector3 newPosition)
void MoveControlRelatve(Vector3 move)
void MoveLinkConnectorTowards(Transform t, float moveDistance)
void MovePlayerTo()
void MovePlayerToAndControl()
void MoveRelativeNoForce(float x, float y, float z)
void MoveRelativeNoForce(Vector3 direction)
void MoveTo(Vector3 pos, bool alsoMoveRB)
void MoveTo(float x, float y, float z)
void MoveXPositionRelativeNoForce(string val)
void MoveXPositionRelativeNoForce(float f)
void MoveYPositionRelativeNoForce(string val)
void MoveYPositionRelativeNoForce(float f)
void MoveZPositionRelativeNoForce(string val)
void MoveZPositionRelativeNoForce(float f)
void NextControlMode()
protected void OnAtomUIDRename(string fromid, string toid)
protected void onLinkToRigidbodyNamesChanged(List<string> rbNames)
void PauseComply(int numFrames)
void PossessMoveAndAlignTo(Transform t)
protected void RegisterAllocatedObject(object o)
protected void RegisterFCUI(FreeControllerV3UI fcui, bool isAlt)
void Reset()
void ResetAppliedForces()
void ResetControl()
void RestoreFromJSON(JSONClass jc, bool restorePhysical, bool restoreAppearance, JSONArray presetAtoms, bool setMissingToDefault)
void RestorePreLinkState()
void RotateAxis(RotateAxisnames ran, float val)
void RotateControl(Vector3 newWorldRotation, bool callHandlers)
void RotateControl(Vector3 newWorldRotation)
void RotateControl(Vector3 axis, float angle, bool callHandlers)
void RotateControl(Vector3 axis, float angle)
void RotateSelfRelativeNoForce(Vector3 rotation)
void RotateTo(Quaternion q)
void RotateWorldX(float val, bool absolute)
void RotateWorldY(float val, bool absolute)
void RotateWorldZ(float val, bool absolute)
void RotateX(float val)
void RotateXSelfRelativeNoForce(string val)
void RotateXSelfRelativeNoForce(float f)
void RotateY(float val)
void RotateYSelfRelativeNoForce(string val)
void RotateYSelfRelativeNoForce(float f)
void RotateZ(float val)
void RotateZSelfRelativeNoForce(string val)
void RotateZSelfRelativeNoForce(float f)
void ScaleChanged(float scale)
void SelectAlignToRigidbody(Rigidbody rb)
void SelectAlignToRigidbodyFromScene()
void SelectLinkToRigidbody(Rigidbody rb)
void SelectLinkToRigidbody(Rigidbody rb, SelectLinkState linkState, bool usePhysicalLink, bool modifyState)
void SelectLinkToRigidbodyFromScene()
void SelectRoot()
void SetHoldPositionDamperMax()
void SetHoldPositionDamperMin()
void SetHoldPositionDamperPercent(float percent)
void SetHoldPositionSpringMax()
void SetHoldPositionSpringMin()
void SetHoldPositionSpringPercent(float percent)
void SetHoldRotationDamperMax()
void SetHoldRotationDamperMin()
void SetHoldRotationDamperPercent(float percent)
void SetHoldRotationSpringMax()
void SetHoldRotationSpringMin()
void SetHoldRotationSpringPercent(float percent)
void SetLinkToAtom(string atomUID)
protected void SetLinkToAtomNames()
void SetLinkToRigidbody(string rigidbodyName)
void SetLinkToRigidbodyObject(string objectName)
void SetLocalEulerAngles(Vector3 eulerAngles)
void SetLocalPosition(Vector3 localPosition)
void SetLocalPositionNoForce(Vector3 position)
void SetLocalRotationNoForce(Vector3 rotation)
void SetPositionGridModeFromString(string gridModeString)
void SetPositionNoForce(Vector3 position)
void SetPositionStateFromString(string state)
void SetRotationGridModeFromString(string gridModeString)
void SetRotationNoForce(Vector3 rotation)
void SetRotationStateFromString(string state)
void SetXLocalPositionNoForce(float f)
void SetXLocalRotationNoForce(float f)
void SetXPositionNoForce(float f)
void SetXRotationNoForce(float f)
void SetYLocalPositionNoForce(float f)
void SetYLocalRotationNoForce(float f)
void SetYPositionNoForce(float f)
void SetYRotationNoForce(float f)
void SetZLocalPositionNoForce(float f)
void SetZLocalRotationNoForce(float f)
void SetZPositionNoForce(float f)
void SetZRotationNoForce(float f)
void ShowGUI()
protected void SyncAtomUID()
protected void SyncCanGrabPosition(bool b)
protected void SyncCanGrabRotation(bool b)
protected void SyncComplyPositionThreshold(float f)
protected void SyncComplyRotationThreshold(float f)
protected void SyncComplySpeed(float f)
protected void SyncDeactivateOtherControlsOnPossess(bool b)
protected void SyncFreezeAtomPhysicsWhenGrabbed(bool b)
protected void SyncGrabFreezePhysics()
protected void SyncInteractableInPlayMode(bool b)
protected void SyncOn(bool b)
protected void SyncPossessable(bool b)
void TakeSnapshot()
protected void Update()
void XLocalPositionSnapPoint1()
void XLocalRotation0()
void XLocalRotationAdd(float a)
void XLocalRotationSnap1()
void XPositionSnapPoint1()
void XRotation0()
void XRotationAdd(float a)
void XRotationSnap1()
void YLocalPositionSnapPoint1()
void YLocalRotation0()
void YLocalRotationAdd(float a)
void YLocalRotationSnap1()
void YPositionSnapPoint1()
void YRotation0()
void YRotationAdd(float a)
void YRotationSnap1()
void ZLocalPositionSnapPoint1()
void ZLocalRotation0()
void ZLocalRotationAdd(float a)
void ZLocalRotationSnap1()
void ZPositionSnapPoint1()
void ZRotation0()
void ZRotationAdd(float a)
void ZRotationSnap1()

// nested types
enum SelectLinkState { PositionAndRotation, Position, Rotation }
enum PositionState { On, Off, Following, Hold, Lock, ParentLink, PhysicsLink, Comply }
enum RotationState { On, Off, Following, Hold, Lock, LookAt, ParentLink, PhysicsLink, Comply }
enum GridMode { None, Local, Global }
enum MoveAxisnames { X, Y, Z, CameraRight, CameraUp, CameraForward, CameraRightNoY, CameraForwardNoY, None }
enum RotateAxisnames { X, Y, Z, NegX, NegY, NegZ, WorldY, None }
enum DrawAxisnames { X, Y, Z, NegX, NegY, NegZ }
enum ControlMode { Off, Position, Rotation }
delegate OnPositionChange(FreeControllerV3 fcv3)
delegate OnRotationChange(FreeControllerV3 fcv3)
delegate OnMovement(FreeControllerV3 fcv3)
delegate OnGrabStart(FreeControllerV3 fcv3)
delegate OnGrabEnd(FreeControllerV3 fcv3)
delegate OnPossessStart(FreeControllerV3 fcv3)
delegate OnPossessEnd(FreeControllerV3 fcv3)

```

## Plugin hosting internals

### MVRPluginManager

extends `JSONStorable`

```csharp
// constructors
new MVRPluginManager()

// fields
Transform configurableButtonPrefab
Transform configurableColorPickerPrefab
Transform configurableFilterablePopupPrefab
Transform configurablePopupPrefab
Transform configurableScrollablePopupPrefab
Transform configurableSliderPrefab
Transform configurableSpacerPrefab
Transform configurableTextFieldPrefab
Transform configurableTogglePrefab
protected JSONStorableAction CreatePluginAction
protected ScriptDomain domain
protected MD5CryptoServiceProvider md5
Transform pluginContainer
Transform pluginListPanel
protected Color pluginPanelErrorColor
Transform pluginPanelPrefab
protected Color pluginPanelValidColor
protected Color pluginPanelWarningColor
protected List<MVRPlugin> plugins
protected Dictionary<string, bool> pluginUIDs
Transform scriptControllerPanelPrefab
RectTransform scriptUIParent
Transform scriptUIPrefab

// methods
protected void AlertUserToNetworkRestriction(string url)
protected void Awake()
protected void BeginBrowse(JSONStorableUrl jsurl)
MVRPlugin CreatePlugin()
protected void CreatePluginCallback()
protected string CreatePluginUID(bool isTempName)
protected MVRPlugin CreatePluginWithId(string pluginUID)
protected MVRScriptController CreateScriptController(MVRPlugin mvrp, ScriptType type)
protected void DestroyScriptController(MVRScriptController mvrsc)
JSONClass GetJSON(bool includePhysical, bool includeAppearance, bool forceStore)
string GetMD5Hash(byte[] bytes)
protected void GivePluginNewUID(MVRPlugin mvrp, bool isTempName)
protected void GivePluginsNewUIDs(List<MVRPlugin> pluginsList, bool isTemp)
protected void Init()
void InitUI()
void InitUIAlt()
void LateRestoreFromJSON(JSONClass jc, bool restorePhysical, bool restoreAppearance, bool setMissingToDefault)
protected void OnDestroy()
void PreRestore(bool restorePhysical, bool restoreAppearance)
void ReloadPluginWithUID(string uid)
void RemoveAllPlugins()
protected void RemovePlugin(MVRPlugin mvrp)
protected void RemovePluginScriptControllers(MVRPlugin mvrp)
void RemovePluginWithUID(string uid)
protected void SetPluginPanelColor(MVRPlugin mvrp, Color c)
protected void SetPluginPanelErrorColor(MVRPlugin mvrp)
protected void SetPluginPanelValidColor(MVRPlugin mvrp)
protected void SetPluginPanelWarningColor(MVRPlugin mvrp)
protected void SyncPluginUrl(MVRPlugin mvrp)
protected void SyncPluginUrlInternal(MVRPlugin mvrp, bool isFromConfirmDenyResponse)
protected void UserConfirmDenyComplete(MVRPlugin mvrp, bool didConfirm)

```

### MVRPlugin

extends `object`

```csharp
// constructors
new MVRPlugin()

// fields
Transform configUI
protected HashSet<VarPackage> confirmedOncePackages
JSONStorableUrl pluginURLJSON
protected HashSet<VarPackage> requestedPackages
RectTransform scriptControllerContent
List<MVRScriptController> scriptControllers
string uid
protected UserConfirmDenyComplete userConfirmDenyCompleteCallback
protected bool userConfirmedAPackage

// properties
protected bool HasRequestedPackages { get; set; }

// methods
void AddRequestedPackage(VarPackage vp)
protected void AutoConfirmVarPackage(VarPackage vp)
protected void AutoDenyVarPackage(VarPackage vp)
protected void CheckUserConfirmDenyComplete()
protected string GetPrompt(VarPackage vp)
bool IsVarPackageConfirmed(VarPackage vp)
void Reload()
void SetupUserConfirmDeny(UserConfirmDenyComplete callback)
void UserConfirm()
protected void UserConfirmStickyVarPackage(VarPackage vp)
protected void UserConfirmVarPackage(VarPackage vp)
protected void UserDenyStickyVarPackage(VarPackage vp)
protected void UserDenyVarPackage(VarPackage vp)

// nested types
delegate UserConfirmDenyComplete(MVRPlugin mvrp, bool didConfirm)

```

### MVRScriptController

extends `object`

```csharp
// constructors
new MVRScriptController()

// fields
Transform configUI
Transform customUI
GameObject gameObject
MVRScript script

// methods
void CloseUI()
void OpenUI()

```

## Files (the only sanctioned IO)

### MVR.FileManagementSecure.FileManagerSecure

extends `object`

```csharp
// constructors
new FileManagerSecure()

// methods
static void CopyFile(string oldPath, string newPath, UserActionCallback confirmCallback, UserActionCallback denyCallback, ExceptionCallback exceptionCallback)
static void CopyFile(string oldPath, string newPath)
static void CreateDirectory(string path)
static void CreateDirectory(string path, UserActionCallback confirmCallback, UserActionCallback denyCallback, ExceptionCallback exceptionCallback)
static void DeleteFile(string path)
static void DeleteFile(string path, UserActionCallback confirmCallback, UserActionCallback denyCallback, ExceptionCallback exceptionCallback)
static DateTime DirectoryCreationTime(string path, bool onlySystemDirectories)
static bool DirectoryExists(string path, bool onlySystemDirectories)
static DateTime DirectoryLastWriteTime(string path, bool onlySystemDirectories)
static DateTime FileCreationTime(string path, bool onlySystemFiles)
static bool FileExists(string path, bool onlySystemFiles)
static DateTime FileLastWriteTime(string path, bool onlySystemFiles)
static string[] GetDirectories(string dir, string pattern)
static string GetDirectoryName(string path, bool returnSlashPath)
static string GetFileName(string path)
static string[] GetFiles(string dir, string pattern)
static string GetFullPath(string path)
static int GetPackageVersion(string packageUid)
static List<ShortCut> GetShortCutsForDirectory(string dir, bool allowNavigationAboveRegularDirectories, bool useFullPaths, bool generateAllFlattenedShortcut, bool includeRegularDirsInFlattenedShortcut)
static bool IsDirectoryInPackage(string path)
static bool IsFileInPackage(string path)
static void MoveFile(string oldPath, string newPath, UserActionCallback confirmCallback, UserActionCallback denyCallback, ExceptionCallback exceptionCallback, bool overwrite)
static void MoveFile(string oldPath, string newPath, bool overwrite)
static string NormalizeLoadPath(string path)
static string NormalizePath(string path)
static bool PackageExists(string packageUid)
static byte[] ReadAllBytes(string path)
static string ReadAllText(string path)
static void RegisterRefreshHandler(OnRefresh refreshHandler)
static void UnregisterRefreshHandler(OnRefresh refreshHandler)
static void WriteAllBytes(string path, byte[] bytes)
static void WriteAllBytes(string path, byte[] bytes, UserActionCallback confirmCallback, UserActionCallback denyCallback, ExceptionCallback exceptionCallback)
static void WriteAllText(string path, string text, UserActionCallback confirmCallback, UserActionCallback denyCallback, ExceptionCallback exceptionCallback)
static void WriteAllText(string path, string text)

```

## JSON

### SimpleJSON.JSONNode

extends `object`

```csharp
// constructors
new JSONNode()

// properties
JSONArray AsArray { get; }
bool AsBool { get; set; }
double AsDouble { get; set; }
float AsFloat { get; set; }
int AsInt { get; set; }
JSONClass AsObject { get; }
IEnumerable<JSONNode> Childs { get; }
int Count { get; }
IEnumerable<JSONNode> DeepChilds { get; }
JSONNode Item { get; set; }
JSONNode Item { get; set; }
string Value { get; set; }

// methods
void Add(JSONNode aItem)
void Add(string aKey, JSONNode aItem)
static JSONNode Deserialize(BinaryReader aReader)
bool Equals(object obj)
int GetHashCode()
static JSONNode LoadFromBase64(string aBase64)
static JSONNode LoadFromCompressedBase64(string aBase64)
static JSONNode LoadFromCompressedFile(string aFileName)
static JSONNode LoadFromCompressedStream(Stream aData)
static JSONNode LoadFromFile(string aFileName)
static JSONNode LoadFromStream(Stream aData)
static bool op_Equality(JSONNode a, object b)
static string op_Implicit(JSONNode d)
static JSONNode op_Implicit(string s)
static bool op_Inequality(JSONNode a, object b)
static JSONNode Parse(string aJSON)
JSONNode Remove(string aKey)
JSONNode Remove(JSONNode aNode)
JSONNode Remove(int aIndex)
string SaveToBase64()
string SaveToCompressedBase64()
void SaveToCompressedFile(string aFileName)
void SaveToCompressedStream(Stream aData)
void SaveToFile(string aFileName)
void SaveToStream(Stream aData)
void Serialize(BinaryWriter aWriter)
string ToString(string aPrefix)
void ToString(string aPrefix, StringBuilder sb)
string ToString()

```

### SimpleJSON.JSONClass

extends `JSONNode`

implements `IEnumerable`

```csharp
// constructors
new JSONClass()

// properties
IEnumerable<JSONNode> Childs { get; }
int Count { get; }
JSONNode Item { get; set; }
JSONNode Item { get; set; }
IEnumerable<string> Keys { get; }

// methods
void Add(string aKey, JSONNode aItem)
IEnumerator GetEnumerator()
bool HasKey(string aKey)
JSONNode Remove(JSONNode aNode)
JSONNode Remove(string aKey)
JSONNode Remove(int aIndex)
void Serialize(BinaryWriter aWriter)
string ToString()
void ToString(string aPrefix, StringBuilder sb)
string ToString(string aPrefix)

```

### SimpleJSON.JSONArray

extends `JSONNode`

implements `IEnumerable`

```csharp
// constructors
new JSONArray()

// properties
IEnumerable<JSONNode> Childs { get; }
int Count { get; }
JSONNode Item { get; set; }
JSONNode Item { get; set; }

// methods
void Add(string aKey, JSONNode aItem)
IEnumerator GetEnumerator()
JSONNode Remove(int aIndex)
JSONNode Remove(JSONNode aNode)
void Serialize(BinaryWriter aWriter)
string ToString()
void ToString(string aPrefix, StringBuilder sb)
string ToString(string aPrefix)

```

### SimpleJSON.JSONData

extends `JSONNode`

```csharp
// constructors
new JSONData(bool aData)
new JSONData(int aData)
new JSONData(double aData)
new JSONData(string aData)
new JSONData(float aData)

// properties
string Value { get; set; }

// methods
void Serialize(BinaryWriter aWriter)
string ToString()
void ToString(string aPrefix, StringBuilder sb)
string ToString(string aPrefix)

```

### SimpleJSON.JSON

extends `object`

```csharp
// methods
static JSONNode Parse(string aJSON)

```

## Triggers

### Trigger

extends `object`

implements `TriggerActionHandler`

```csharp
// constructors
new Trigger()

// fields
protected bool _active
protected float _transitionInterpValue
protected HashSet<TriggerActionDiscrete> actionsWithActiveTimers
protected Toggle activeToggle
protected Button addDiscreteActionEndButton
protected Button addDiscreteActionStartButton
protected Button addTransitionActionButton
protected Button clearActionsButton
protected Button closeTriggerActionsPanelButton
protected Button copyDiscreteActionsEndButton
protected Button copyDiscreteActionsStartButton
static List<TriggerActionDiscrete> copyOfDiscreteActions
static List<TriggerActionTransition> copyOfTransitionActions
protected Button copyTransitionActionsButton
protected ScrollRectContentManager discreteActionEndContentManager
protected List<TriggerActionDiscrete> discreteActionsEnd
protected List<TriggerActionDiscrete> discreteActionsStart
protected ScrollRectContentManager discreteActionStartContentManager
protected JSONStorableString displayNameJSON
bool doActionsInReverse
protected Button duplicateButton
TriggerHandler handler
OnCloseTriggerActionsPanel onCloseTriggerActionsPanel
OnOpenTriggerActionsPanel onOpenTriggerActionsPanel
protected Button pasteDiscreteActionsEndButton
protected Button pasteDiscreteActionsStartButton
protected Button pasteTransitionActionsButton
protected JSONStorableString previewTextJSON
protected Button removeButton
bool reverse
protected List<TriggerActionTransition> transitionActions
protected ScrollRectContentManager transitionActionsContentManager
protected Slider transitionInterpValueSlider
protected Button triggerActionsButton
Transform triggerActionsPanel
Transform triggerActionsParent
Transform triggerPanel

// properties
IEnumerable<TriggerActionDiscrete> ActionsWithActiveTimers { get; }
bool active { get; set; }
IEnumerable<TriggerActionDiscrete> DiscreteActionsEnd { get; }
IEnumerable<TriggerActionDiscrete> DiscreteActionsStart { get; }
string displayName { get; set; }
bool HasActionsWithActiveTimers { get; }
string previewText { get; set; }
IEnumerable<TriggerActionTransition> TransitionActions { get; }
float transitionInterpValue { get; set; }

// methods
protected void AutoSetDisplayName()
protected void AutoSetPreviewText()
protected void AutoSetPreviewTextAndDisplayName()
protected void ClearActions()
void CloseTriggerActionsPanel()
void CopyDiscreteActionsEnd()
void CopyDiscreteActionsStart()
void CopyTransitionActions()
void CreateDiscreteActionEnd()
TriggerActionDiscrete CreateDiscreteActionEndInternal(int index)
void CreateDiscreteActionStart()
TriggerActionDiscrete CreateDiscreteActionStartInternal(int index)
void CreateTransitionAction()
TriggerActionTransition CreateTransitionActionInternal(int index)
RectTransform CreateTriggerActionDiscreteUI()
protected void CreateTriggerActionMiniUI(ScrollRectContentManager contentManager, TriggerAction ta, int index, int totalCount)
protected void CreateTriggerActionsPanel()
RectTransform CreateTriggerActionTransitionUI()
void DeregisterUI()
void Duplicate()
void DuplicateTriggerAction(TriggerAction ta)
void ForceTransitionsActive()
void ForceTransitionsInactive()
JSONClass GetJSON()
JSONClass GetJSON(string subScenePrefix)
bool HasActions()
void InitTriggerActionsUI()
void InitTriggerUI()
void OpenTriggerActionsPanel()
void PasteDiscreteActionsEnd()
void PasteDiscreteActionsStart()
void PasteTransitionActions()
void Remove()
void RemoveTriggerAction(TriggerAction ta)
void Reset()
void RestoreFromJSON(JSONClass jc, string subScenePrefix, bool isMerge)
void RestoreFromJSON(JSONClass jc)
void SetHasActiveTimer(TriggerActionDiscrete tad, bool hasTimer)
void SyncAtomNames()
protected void SyncDisplayName(string n)
protected void SyncPreviewText(string n)
void TriggerActionNameChange(TriggerAction ta)
void Update()
void Validate()

// nested types
delegate OnOpenTriggerActionsPanel()
delegate OnCloseTriggerActionsPanel()

```

### TriggerHandler

extends `-`

```csharp
// methods
RectTransform CreateTriggerActionDiscreteUI()
RectTransform CreateTriggerActionMiniUI()
RectTransform CreateTriggerActionsUI()
RectTransform CreateTriggerActionTransitionUI()
void DuplicateTrigger(Trigger t)
void RemoveTrigger(Trigger t)
void RemoveTriggerActionUI(RectTransform rt)

```

### TriggerAction

extends `object`

```csharp
// constructors
new TriggerAction()

// fields
protected string _missingReceiverStoreid
protected JSONStorable _receiver
protected Atom _receiverAtom
protected string _receiverStoreId
protected string _receiverTargetName
Button closeDetailPanelButton
Button duplicateButton
protected JSONStorableBool enabledJSON
TriggerActionHandler handler
protected JSONStorableString nameJSON
Button openDetailPanelButton
protected bool paramContrained
protected float paramDefault
protected float paramMax
protected float paramMin
protected JSONStorableString previewTextJSON
UIPopup receiverAtomPopup
UIPopup receiverPopup
protected bool receiverSetFromPopup
protected JSONStorableFloat receiverTargetFloat
UIPopup receiverTargetNamePopup
protected List<string> receiverTargetNames
Button removeButton
RectTransform triggerActionMiniPanel
RectTransform triggerActionPanel

// properties
protected bool detailPanelOpen { get; set; }
bool enabled { get; set; }
string name { get; set; }
string previewText { get; set; }
JSONStorable receiver { get; set; }
Atom receiverAtom { get; set; }
string receiverTargetName { get; set; }

// methods
protected void AutoSetName()
protected void AutoSetPreviewText()
protected void AutoSetPreviewTextAndName()
protected void CheckMissingReceiver()
void CloseDetailPanel()
protected void CreateTriggerActionPanel()
void DeregisterUI()
void Duplicate()
JSONClass GetJSON()
JSONClass GetJSON(string subScenePrefix)
void InitTriggerActionMiniPanelUI()
void InitTriggerActionPanelUI()
void OpenDetailPanel()
void Remove()
void RestoreFromJSON(JSONClass jc)
void RestoreFromJSON(JSONClass jc, string subScenePrefix)
protected void SetInitialParamsFromReceiverTarget()
void SetReceiver(string storeid)
void SetReceiverAtom(string uid)
protected void SetReceiverAtomPopupValues()
protected void SetReceiverPopupValues()
void SetReceiverTargetName(string targetName)
void SetReceiverTargetNameAndSetInitialParams(string targetName)
protected void SetReceiverTargetPopupNames()
void SyncAtomName()
protected void SyncEnabled(bool b)
protected void SyncFromReceiverTarget()
protected void SyncName(string n)
protected void SyncPreviewText(string n)
protected void SyncTargetPopupNames()
void Validate()

```

### TriggerActionDiscrete

extends `TriggerAction`

```csharp
// constructors
new TriggerActionDiscrete()

// fields
protected Type _actionType
protected NamedAudioClip _audioClip
protected string _audioClipCategory
protected AudioClipType _audioClipType
protected bool _boolValue
protected float _curveSecondPointLocation
protected float _curveSecondPointValue
protected float _curveTimerLength
protected TimerType _curveTimerType
protected bool _curveUseSecondPoint
protected float _curveValueEnd
protected float _curveValueStart
protected float _floatValue
protected float _HSVColorH
protected float _HSVColorS
protected float _HSVColorV
protected float _oneOverTimerLength
protected string _presetFilePath
protected string _sceneFilePath
protected float _secondTimerPointCurveLocation
protected float _secondTimerPointValue
protected string _stringChooserActionChoice
protected string _stringChooserValue
protected string _stringValue
protected float _timerLength
protected TimerType _timerType
protected bool _useSecondTimerPoint
protected bool _useTimer
protected UIPopup audioClipCategoryPopup
protected UIPopup audioClipPopup
protected RectTransform audioClipPopupsContainer
protected UIPopup audioClipTypePopup
protected bool beforeTestBoolValue
protected float beforeTestFloatValue
protected bool beforeTestStateCaptured
protected string beforeTestStringChooserValue
protected string beforeTestStringValue
protected HSVColor beforeTestTriggerColor
protected Toggle boolValueToggle
protected Button choosePresetFilePathButton
protected Button chooseSceneFilePathButton
protected HSVColorPicker colorPicker
protected RectTransform colorPickerContainer
protected Button copyButton
static JSONClass copyOfData
protected RectTransform curveContainer
protected UILineRenderer curveLineRenderer
bool doActionsInReverse
protected UIDynamicSlider floatValueDynamicSlider
protected Slider floatValueSlider
protected Button pasteButton
protected Text presetFilePathText
protected Button resetTestButton
protected Text sceneFilePathText
protected Slider secondTimerPointCurveLocationSlider
protected UIDynamicSlider secondTimerPointValueDynamicSlider
protected Slider secondTimerPointValueSlider
protected UIPopup stringChooserActionPopup
protected UIPopup stringChooserValuePopup
protected InputField stringValueField
protected InputFieldAction stringValueFieldAction
protected Button testButton
protected Slider timerLengthSlider
protected UIPopup timerTypePopup
protected HSVColor triggerColor
protected Toggle useSecondTimerPointToggle
protected Toggle useTimerToggle

// properties
Type actionType { get; set; }
NamedAudioClip audioClip { get; set; }
string audioClipCategory { get; set; }
AudioClipType audioClipType { get; set; }
bool boolValue { get; set; }
protected float currentTimerTime { get; set; }
protected AnimationCurve floatCurve { get; set; }
float floatValue { get; set; }
string presetFilePath { get; set; }
string sceneFilePath { get; set; }
float secondTimerPointCurveLocation { get; set; }
float secondTimerPointValue { get; set; }
string stringChooserActionChoice { get; set; }
string stringChooserValue { get; set; }
string stringValue { get; set; }
protected bool timerActive { get; set; }
float timerLength { get; set; }
TimerType timerType { get; set; }
bool useSecondTimerPoint { get; set; }
bool useTimer { get; set; }

// methods
protected void AutoSetName()
protected void AutoSetNameFromAudioClipParam()
protected void AutoSetNameFromBoolParam()
protected void AutoSetNameFromColorParam()
protected void AutoSetNameFromFloatParam()
protected void AutoSetNameFromStringChooserParam()
protected void AutoSetNameFromStringOrUrlParam()
protected void AutoSetPreviewText()
protected void AutoSetPreviewTextFromAudioClipParam()
protected void AutoSetPreviewTextFromBoolParam()
protected void AutoSetPreviewTextFromColorParam()
protected void AutoSetPreviewTextFromFloatParam()
protected void AutoSetPreviewTextFromStringChooserParam()
protected void AutoSetPreviewTextFromStringOrUrlParam()
protected void CheckMissingReceiver()
protected void ConfigureFloatCurve(TimerType type, float length, float startValue, float endValue, bool useSecondPoint, float secondPointValue, float secondPointLocation)
void CopyData()
protected void CreateTriggerActionPanel()
void DeregisterUI()
JSONClass GetJSON(string subScenePrefix)
protected void GetPresetFilePath()
protected void GetSceneFilePath()
void InitTriggerActionPanelUI()
void OpenDetailPanel()
void PasteData()
void ResetTest()
void RestoreFromJSON(JSONClass jc, string subScenePrefix)
void SetAudioClip(string clipUID)
void SetAudioClipCategory(string cat)
void SetAudioClipType(string type)
protected void SetClipCategoryPopupValues()
protected void SetClipPopupValues()
protected void SetColorFromHSV(float h, float s, float v)
void SetFloatValue(float f)
protected void SetInitialParamsFromReceiverTarget()
void SetPresetFilePath(string path)
protected void SetReceiverTargetPopupNames()
void SetSceneFilePath(string path)
void SetSecondTimerFloatValue(float f)
void SetSecondTimerPointCurveLocation(float f)
protected void SetStringChooserActionChoice(string choice)
protected void SetStringChooserActionPopupValues()
protected void SetStringChooserValue(string v)
protected void SetStringValue(string v)
protected void SetStringValueToField()
protected void SetTimerLength(float f)
protected void SetTimerType(string type)
protected void SetUseSecondTimerPoint(bool b)
protected void SetUseTimer(bool b)
protected void SyncFloatCurve(bool useCurrentCurveStart)
protected void SyncFloatCurveRender()
protected void SyncFromReceiverTarget()
protected void SyncStringChooserPopup()
protected void SyncTimerTypePopup()
protected void SyncType()
void Test()
void Trigger(bool reverse, bool force)
void Update()
void Validate()

// nested types
enum AudioClipType { Embedded, URL }
enum TimerType { EaseInOut, Linear }

```

### TriggerActionTransition

extends `TriggerAction`

```csharp
// constructors
new TriggerActionTransition()

// fields
protected Type _actionType
protected float _currentInterp
protected float _endHSVColorH
protected float _endHSVColorS
protected float _endHSVColorV
protected float _endVal
protected float _startHSVColorH
protected float _startHSVColorS
protected float _startHSVColorV
protected float _startVal
protected bool _startWithCurrentVal
bool active
protected Button copyButton
static JSONClass copyOfData
protected bool disableTriggerInterp
protected HSVColorPicker endColorPicker
protected RectTransform endColorPickerContainer
protected UIDynamicSlider endValDynamicSlider
protected Slider endValSlider
protected HSVColor interpColor
protected Button pasteButton
protected HSVColorPicker startColorPicker
protected RectTransform startColorPickerContainer
protected UIDynamicSlider startValDynamicSlider
protected Slider startValSlider
protected Toggle startWithCurrentValToggle
protected Slider testTransitionSlider

// properties
Type actionType { get; set; }
float endVal { get; set; }
float startVal { get; set; }
bool startWithCurrentVal { get; set; }

// methods
protected void AutoSetName()
protected void AutoSetNameFromColorParam()
protected void AutoSetNameFromFloatParam()
protected void AutoSetPreviewText()
protected void AutoSetPreviewTextFromColorParam()
protected void AutoSetPreviewTextFromFloatParam()
protected void CheckEndValConstraint(bool skipSync)
protected void CheckMissingReceiver()
protected void CheckStartValConstraint(bool skipSync)
void CopyData()
protected void CreateTriggerActionPanel()
void DeregisterUI()
JSONClass GetJSON(string subScenePrefix)
void InitTriggerActionPanelUI()
void InitTriggerStart()
void PasteData()
void RestoreFromJSON(JSONClass jc, string subScenePrefix)
protected void SetEndColorFromHSV(float h, float s, float v)
protected void SetEndColorFromHSV(float h, float s, float v, bool force)
void SetEndVal(float f)
protected void SetInitialParamsFromReceiverTarget()
void SetReceiverTargetNameAndSetInitialParams(string targetName)
protected void SetReceiverTargetPopupNames()
protected void SetStartColorFromHSV(float h, float s, float v)
void SetStartVal(float f)
void SetStartWithCurrentVal(bool b)
protected void SetTestTransition(float f)
protected void SyncEndValDynamicSlider()
protected void SyncFromReceiverTarget()
protected void SyncStartValDynamicSlider()
protected void SyncType()
void TriggerInterp(float interp, bool force)
void Validate()

```

## Characters and morphs

### DAZCharacterSelector

extends `JSONStorable`

```csharp
// constructors
new DAZCharacterSelector()

// fields
protected Dictionary<string, DAZClothingItem> _clothingItemByBackupId
protected Dictionary<string, DAZClothingItem> _clothingItemById
protected bool _disableAnatomy
protected DAZClothingItem[] _femaleClothingItems
protected DAZHairGroup[] _femaleHairItems
protected Gender _gender
protected Dictionary<string, DAZHairGroup> _hairItemByBackupId
protected Dictionary<string, DAZHairGroup> _hairItemById
protected DAZCharacter _loadedCharacter
protected bool _loadedGenderChange
protected DAZClothingItem[] _maleClothingItems
protected DAZHairGroup[] _maleHairItems
protected bool _onlyShowFavoriteMorphsInParameterLists
protected bool _unloadCharactersWhenSwitching
protected bool _useAdvancedColliders
protected bool _useAuxBreastColliders
protected bool _useFemaleMorphsOnMale
protected bool _useMaleMorphsOnFemale
Transform[] advancedCollidersFemale
Transform[] advancedCollidersMale
protected HashSet<string> alreadyReportedDuplicates
Collider[] auxBreastColliders
protected JSONStorableStringChooser characterChooserJSON
GenerateDAZCharacterSelectorUI characterSelectorUI
GenerateDAZCharacterSelectorUI characterSelectorUIAlt
DAZCharacterTextureControlUI characterTextureUI
DAZCharacterTextureControlUI characterTextureUIAlt
Transform characterTextureUITab
Transform characterTextureUITabAlt
DAZBone chestBone
protected Dictionary<string, JSONStorableBool> clothingItemJSONs
protected List<JSONStorableAction> clothingItemToggleJSONs
GenerateDAZClothingSelectorUI clothingSelectorFemaleUI
GenerateDAZClothingSelectorUI clothingSelectorMaleUI
RectTransform color1Container
Text color1DisplayNameText
HSVColorPicker color1Picker
RectTransform color2Container
Text color2DisplayNameText
HSVColorPicker color2Picker
RectTransform color3Container
Text color3DisplayNameText
HSVColorPicker color3Picker
DAZCharacterMaterialOptions copyUIFrom
protected string[] customParamNames
Transform customUIBucket
protected AsyncFlag delayResumeFlag
protected JSONStorableBool disableAnatomyJSON
Transform dynamicClothingItemPrefab
Transform dynamicHairItemPrefab
protected DAZSkinV2 exportSkin
int[] femaleAnatomyOffMaterialSlots
int[] femaleAnatomyOnMaterialSlots
AdjustJoints femaleBreastAdjustJoints
Transform femaleCharactersContainer
Transform femaleCharactersPrefab
Transform femaleClothingContainer
DAZClothingItem femaleClothingCreatorItem
Transform femaleClothingPrefab
DAZCharacterMaterialOptions femaleEyelashMaterialOptions
AdjustJoints femaleGluteAdjustJoints
Transform femaleHairContainer
DAZHairGroup femaleHairCreatorItem
Transform femaleHairPrefab
DAZMorphBank femaleMorphBank1
Transform femaleMorphBank1Prefab
DAZMorphBank femaleMorphBank2
Transform femaleMorphBank2Prefab
DAZMorphBank femaleMorphBank3
Transform femaleMorphBank3Prefab
DAZCharacter femalePlaceholderCharacter
Transform[] femaleTransforms
protected Dictionary<string, JSONStorableBool> hairItemJSONs
protected List<JSONStorableAction> hairItemToggleJSONs
GenerateDAZHairSelectorUI hairSelectorFemaleUI
GenerateDAZHairSelectorUI hairSelectorFemaleUIAlt
GenerateDAZHairSelectorUI hairSelectorMaleUI
GenerateDAZHairSelectorUI hairSelectorMaleUIAlt
DAZBone headBone
DAZBone hipBone
DAZBone leftFootBone
FreeControllerV3 leftFootController
DAZBone leftHandBone
BoxCollider leftShoeCollider
FreeControllerV3 leftToeController
int[] maleAnatomyOffMaterialSlots
int[] maleAnatomyOnMaterialSlots
Transform maleCharactersContainer
Transform maleCharactersPrefab
Transform maleClothingContainer
DAZClothingItem maleClothingCreatorItem
Transform maleClothingPrefab
DAZCharacterMaterialOptions maleEyelashMaterialOptions
Transform maleHairContainer
DAZHairGroup maleHairCreatorItem
Transform maleHairPrefab
DAZMorphBank maleMorphBank1
Transform maleMorphBank1Prefab
DAZMorphBank maleMorphBank2
Transform maleMorphBank2Prefab
DAZMorphBank maleMorphBank3
Transform maleMorphBank3Prefab
DAZCharacter malePlaceholderCharacter
Transform[] maleTransforms
Transform morphBankContainer
GenerateDAZMorphsControlUI morphsControlFemaleUI
GenerateDAZMorphsControlUI morphsControlFemaleUIAlt
GenerateDAZMorphsControlUI morphsControlMaleUI
GenerateDAZMorphsControlUI morphsControlMaleUIAlt
protected AsyncFlag onCharacterLoadedFlag
protected JSONStorableBool onlyShowFavoriteMorphsInParametersListsJSON
Text param10DisplayNameText
Text param10DisplayNameTextAlt
Slider param10Slider
Slider param10SliderAlt
Text param1DisplayNameText
Text param1DisplayNameTextAlt
Slider param1Slider
Slider param1SliderAlt
Text param2DisplayNameText
Text param2DisplayNameTextAlt
Slider param2Slider
Slider param2SliderAlt
Text param3DisplayNameText
Text param3DisplayNameTextAlt
Slider param3Slider
Slider param3SliderAlt
Text param4DisplayNameText
Text param4DisplayNameTextAlt
Slider param4Slider
Slider param4SliderAlt
Text param5DisplayNameText
Text param5DisplayNameTextAlt
Slider param5Slider
Slider param5SliderAlt
Text param6DisplayNameText
Text param6DisplayNameTextAlt
Slider param6Slider
Slider param6SliderAlt
Text param7DisplayNameText
Text param7DisplayNameTextAlt
Slider param7Slider
Slider param7SliderAlt
Text param8DisplayNameText
Text param8DisplayNameTextAlt
Slider param8Slider
Slider param8SliderAlt
Text param9DisplayNameText
Text param9DisplayNameTextAlt
Slider param9Slider
Slider param9SliderAlt
DAZBone pelvisBone
protected Coroutine refreshCoroutine
protected HashSet<string> registeredMorphNames
Transform[] regularColliders
Transform[] regularCollidersFemale
Transform[] regularCollidersMale
protected JSONStorableAction removeAllClothingAction
protected JSONStorableAction removeAllRealClothingAction
Button restoreMaterialFromDefaultsButton
Button restoreMaterialFromDefaultsButtonAlt
Button restoreMaterialFromStore1Button
Button restoreMaterialFromStore1ButtonAlt
Button restoreMaterialFromStore2Button
Button restoreMaterialFromStore2ButtonAlt
Button restoreMaterialFromStore3Button
Button restoreMaterialFromStore3ButtonAlt
DAZBone rightFootBone
FreeControllerV3 rightFootController
DAZBone rightHandBone
BoxCollider rightShoeCollider
FreeControllerV3 rightToeController
DAZBones rootBones
string rootBonesName
string rootBonesNameFemale
string rootBonesNameMale
Button saveMaterialToStore1Button
Button saveMaterialToStore1ButtonAlt
Button saveMaterialToStore2Button
Button saveMaterialToStore2ButtonAlt
Button saveMaterialToStore3Button
Button saveMaterialToStore3ButtonAlt
protected SkinMethod saveSkinMethod
string startingCharacterName
UIPopup textureGroup1Popup
UIPopup textureGroup1PopupAlt
Text textureGroup1Text
Text textureGroup1TextAlt
UIPopup textureGroup2Popup
UIPopup textureGroup2PopupAlt
Text textureGroup2Text
Text textureGroup2TextAlt
UIPopup textureGroup3Popup
UIPopup textureGroup3PopupAlt
Text textureGroup3Text
Text textureGroup3TextAlt
UIPopup textureGroup4Popup
UIPopup textureGroup4PopupAlt
Text textureGroup4Text
Text textureGroup4TextAlt
UIPopup textureGroup5Popup
UIPopup textureGroup5PopupAlt
Text textureGroup5Text
Text textureGroup5TextAlt
Transform UIBucketForDynamicItems
protected JSONStorableAction undressAllClothingAction
protected JSONStorableBool unloadCharactersWhenSwitchingJSON
protected JSONStorableAction unloadInactiveObjectsJSONAction
protected JSONStorableBool useAdvancedCollidersJSON
protected JSONStorableBool useAuxBreastCollidersJSON
protected JSONStorableBool useFemaleMorphsOnMaleJSON
protected JSONStorableBool useMaleMorphsOnFemaleJSON

// properties
DAZCharacter[] characters { get; }
DAZClothingItem[] clothingItems { get; }
GenerateDAZClothingSelectorUI clothingSelectorUI { get; }
DAZCharacter[] femaleCharacters { get; }
DAZClothingItem[] femaleClothingItems { get; }
DAZHairGroup[] femaleHairItems { get; }
Gender gender { get; set; }
DAZHairGroup[] hairItems { get; }
GenerateDAZHairSelectorUI hairSelectorUI { get; }
GenerateDAZHairSelectorUI hairSelectorUIAlt { get; }
DAZCharacter[] maleCharacters { get; }
DAZClothingItem[] maleClothingItems { get; }
DAZHairGroup[] maleHairItems { get; }
DAZMorphBank morphBank1 { get; }
DAZMorphBank morphBank1OtherGender { get; }
DAZMorphBank morphBank2 { get; }
DAZMorphBank morphBank3 { get; }
GenerateDAZMorphsControlUI morphsControlUI { get; }
GenerateDAZMorphsControlUI morphsControlUIAlt { get; }
GenerateDAZMorphsControlUI morphsControlUIOtherGender { get; }
DAZCharacter selectedCharacter { get; set; }
bool useAdvancedColliders { get; set; }
bool useAuxBreastColliders { get; set; }

// methods
protected void Awake()
bool CleanDemandActivatedMorphs()
protected void ConnectClothingItem(DAZClothingItem dci)
protected void ConnectDynamicItem(DAZDynamicItem di)
protected void ConnectSkin()
void CopyUI()
protected void EarlyInit()
protected void EarlyInitCharacters()
protected void EarlyInitClothingItems()
protected void EarlyInitHairItems()
void EnableUndressAllClothingItems()
void EnableUndressOnClothingItem(DAZClothingItem clothingItem)
void ExportCurrentCharacterOBJ()
DAZClothingItem GetClothingItem(string itemId)
HashSet<string> GetClothingOtherTags()
string[] GetCustomParamNames()
DAZHairGroup GetHairItem(string itemId)
HashSet<string> GetHairOtherTags()
JSONClass GetJSON(bool includePhysical, bool includeAppearance, bool forceStore)
int GetRuntimeMorphDeltasLoadedCount()
void Init(bool genderChange)
void InitBones()
void InitCharacters()
void InitClothingItems()
void InitComponents()
void InitHairItems()
protected void InitJSONParams()
protected void InitMorphBanks()
void InitUI()
void InvalidateDynamicClothingItemThumbnail(string thumbPath)
void InvalidateDynamicHairItemThumbnail(string thumbPath)
bool IsClothingUIDAvailable(string uid)
bool IsHairUIDAvailable(string uid)
void LoadDynamicCreatorItem(DAZDynamicItem item, DAZDynamic dd)
void LoadFemaleClothingCreatorItem(DAZDynamic source)
void LoadFemaleHairCreatorItem(DAZDynamic source)
void LoadMaleClothingCreatorItem(DAZDynamic source)
void LoadMaleHairCreatorItem(DAZDynamic source)
void LockActiveClothing()
void LockActiveHair()
protected void MorphFavoriteChanged(DAZMorph dm)
protected void OnCharacterLoaded()
protected void OnCharacterPreloaded()
protected void OptimizeMemory()
void RefreshDynamicClothes()
void RefreshDynamicClothingThumbnails()
void RefreshDynamicHair()
void RefreshDynamicHairThumbnails()
void RefreshDynamicItems()
bool RefreshPackageMorphs()
bool RefreshRuntimeMorphs()
protected IEnumerator RefreshWhenHubClosed()
void RemoveAllClothing()
void RemoveAllHair()
void RemoveAllRealClothing()
protected void ReportMemory(List<string> reports)
protected void ResetClothing(bool clearAll)
protected void ResetHair(bool clearAll)
protected void ResetMorphBanks()
void ResetMorphsOtherGender(bool resetPhysical, bool resetAppearance)
protected void ResetMorphsToDefault(bool resetPhysical, bool resetAppearance)
void RestoreFromJSON(JSONClass jc, bool restorePhysical, bool restoreAppearance, JSONArray presetAtoms, bool setMissingToDefault)
protected void ResyncMorphRegistry()
protected void ResyncMorphs(ResyncMorphsOption resyncOption)
void SelectCharacterByName(string characterName, bool fromRestore)
void SetActiveClothingItem(DAZClothingItem item, bool active, bool fromRestore, bool skipSyncAnatomy)
void SetActiveClothingItem(string itemId, bool active, bool fromRestore)
void SetActiveDynamicItem(DAZDynamicItem item, bool active, bool fromRestore)
void SetActiveHairItem(string itemId, bool active, bool fromRestore)
void SetActiveHairItem(DAZHairGroup item, bool active, bool fromRestore, bool skipSyncAnatomy)
void SetClothingItemLock(DAZClothingItem item, bool locked)
void SetMorphAnimatable(DAZMorph dm)
protected void SyncCharacterChoice(string choice)
void SyncClothingAdjustments()
protected void SyncClothingItem(JSONStorableBool clothingItemJSON)
protected void SyncColliders()
protected void SyncCustomItems(Gender gender, DAZDynamicItem[] existingItems, string searchPath, Transform dynamicItemPrefab, Transform container)
protected void SyncDisableAnatomy(bool b)
protected void SyncGender()
void SyncHairAdjustments()
protected void SyncHairItem(JSONStorableBool hairItemJSON)
protected void SyncOnlyShowFavoriteMorphsInParameterLists(bool b)
protected void SyncUnloadCharactersWhenSwitching()
protected void SyncUnloadCharactersWhenSwitching(bool b)
protected void SyncUseAdvancedColliders(bool b)
protected void SyncUseAuxBreastColliders(bool b)
protected void SyncUseFemaleMorphsOnMale(bool b)
protected void SyncUseMaleMorphsOnFemale(bool b)
protected void SyncUseOtherGenderMorphs()
void ToggleClothingItem(DAZClothingItem item)
void ToggleHairItem(DAZHairGroup item)
void UnloadDemandActivatedMorphs()
void UnloadDisabledCharacters()
void UnloadDisabledClothingItems()
void UnloadDisabledHairItems()
void UnloadInactiveCharacters()
void UnloadInactiveClothingItems()
void UnloadInactiveHairItems()
void UnloadInactiveObjects()
void UnloadRuntimeMorphDeltas()
protected IEnumerator UnloadUnusedAssetsDelayed()
void UnlockAllClothing()
void UnlockAllHair()
protected void ValidateDynamicClothesSkin()
protected void ValidateDynamicHairSkin()

// nested types
enum Gender { None, Male, Female, Both }

```

### DAZMorph

extends `object`

```csharp
// constructors
new DAZMorph()
new DAZMorph(DAZMorph copyFrom)

// fields
protected RectTransform _animatableWarningIndicator
protected RectTransform _animatableWarningIndicatorAlt
float _appliedValue
protected Button _copyUidButton
protected string _drivenBy
protected bool _favorite
protected Toggle _favoriteToggle
protected string _formulasString
protected bool _hasBoneModificationFormulas
protected bool _hasBoneRotationFormulas
protected bool _hasFormulas
protected bool _hasMCMFormulas
protected bool _hasMorphValueFormulas
protected Button _increaseRangeButton
protected Button _increaseRangeButtonAlt
protected bool _isDriven
protected string _lowerCaseResolvedDisplayName
protected DAZMorphBank _morphBank
protected DAZMorphSubBank _morphSubBank
protected Button _openInPackageManagerButton
protected Button _openInPackageManagerButtonAlt
protected Text _packageLicenseText
protected Text _packageLicenseTextAlt
protected Text _packageUidText
protected Text _packageUidTextAlt
protected Image _panelImage
protected Button _resetRangeButton
protected Button _resetRangeButtonAlt
protected string _uid
protected Text _versionText
protected Text _versionTextAlt
protected bool _wasZeroedKeepChildValues
protected Button _zeroKeepChildValuesButton
bool active
DAZMorphVertex[] deltas
DAZMorphVertexFast[] deltasFast
bool deltasLoaded
string deltasLoadPath
bool disable
string displayName
protected Text drivenByText
protected RectTransform drivenIndicator
DAZMorphFormula[] formulas
string group
protected static Color highCostColor
float importValue
bool isDemandActivated
bool isDemandLoaded
bool isInPackage
bool isLatestVersion
bool isPoseControl
bool isRuntime
bool isTransient
JSONStorableFloat jsonFloat
float max
string metaJSONFile
string metaLoadPath
float min
string morphName
protected static Color normalColor
int numDeltas
string overrideName
string overrideRegion
protected static Color packageColor
string packageLicense
string packageUid
bool preserveValueOnReimport
string region
protected static Color runtimeColor
protected float startingMax
protected float startingMin
float startValue
protected static Color transientColor
bool triggerNormalRecalc
bool triggerTangentRecalc
static string uidCopy
string version
bool visible
bool wasTouched

// properties
protected bool activeImmediate { get; set; }
bool animatable { get; set; }
RectTransform animatableWarningIndicator { get; set; }
RectTransform animatableWarningIndicatorAlt { get; set; }
float appliedValue { get; set; }
Button copyUidButton { get; set; }
string drivenBy { get; }
bool favorite { get; set; }
Toggle favoriteToggle { get; set; }
string formulasString { get; }
bool hasBoneModificationFormulas { get; }
bool hasBoneRotationFormulas { get; }
bool hasFormulas { get; }
bool hasMCMFormulas { get; }
bool hasMorphValueFormulas { get; }
Button increaseRangeButton { get; set; }
Button increaseRangeButtonAlt { get; set; }
bool isDriven { get; }
string lowerCaseResolvedDisplayName { get; }
DAZMorphBank morphBank { get; set; }
DAZMorphSubBank morphSubBank { get; set; }
float morphValue { get; set; }
float morphValueAdjustLimits { get; set; }
Button openInPackageManagerButton { get; set; }
Button openInPackageManagerButtonAlt { get; set; }
Text packageLicenseText { get; set; }
Text packageLicenseTextAlt { get; set; }
Text packageUidText { get; set; }
Text packageUidTextAlt { get; set; }
Image panelImage { get; set; }
Button resetRangeButton { get; set; }
Button resetRangeButtonAlt { get; set; }
string resolvedDisplayName { get; }
string resolvedRegionName { get; }
string uid { get; set; }
Text versionText { get; set; }
Text versionTextAlt { get; set; }
bool wasZeroedKeepChildValues { get; set; }
Button zeroKeepChildValuesButton { get; set; }

// methods
protected void AdjustRange(float v)
void CopyParameters(DAZMorph copyFrom, bool setValue)
void CopyUid()
void DeregisterUI()
void DeregisterUIAlt()
JSONClass GetDeltasJSON()
JSONClass GetMetaJSON()
void Import(JSONNode mn)
void IncreaseRange()
void Init()
void InitUI(Transform UITransform)
void InitUIAlt(Transform UITransform)
void LoadDeltas()
void LoadDeltasFromBinaryFile(string path)
void LoadDeltasFromJSON(JSONNode deltan)
void LoadMeta()
void LoadMetaFromJSON(JSONNode metan)
protected void OpenInPackageManager()
void Reset()
void ResetRange()
void RestoreFromJSON(JSONClass jc)
void SaveDeltasToBinaryFile(string path)
void SetDefaultValue()
bool SetDriven(bool b, string by, bool syncUI)
protected void SetFavorite(bool b)
bool SetValue(float v)
protected void SetValueInternal(float v)
bool SetValueThreadSafe(float v)
bool StoreJSON(JSONClass jc, bool forceStore)
void SyncDrivenUI()
void SyncJSON()
void UnloadDeltas()
protected void ZeroKeepChildValues()

```

### DAZMorphBank

extends `MonoBehaviour`

```csharp
// constructors
new DAZMorphBank()

// fields
protected bool _applyPerformancePatches
protected Dictionary<string, DAZMorph> _builtInMorphsByName
protected Dictionary<string, DAZMorph> _builtInMorphsByUid
protected DAZMesh _connectedMesh
protected List<DAZMorph> _morphs
protected Dictionary<string, DAZMorph> _morphsByDisplayName
protected Dictionary<string, DAZMorph> _morphsByName
protected Dictionary<string, DAZMorph> _morphsByUid
protected Dictionary<string, DAZMorphSubBank> _morphSubBanksByRegion
protected Dictionary<string, string> _morphToRegionName
protected Vector3[] _threadedMorphedUVVertices
bool _threadedNormalsDirtyThisFrame
bool _threadedTangentsDirtyThisFrame
protected Vector3[] _threadedVisibleMorphedUVVertices
protected bool _threadsRunning
protected List<DAZMorph> _touchedMorphs
protected List<DAZMorph> _unactivatedMorphs
protected DAZMorphTaskInfo applyMorphsTask
string autoImportFolder
string autoImportFolderLegacy
protected HashSet<string> badMorphNames
Dictionary<DAZBone, bool> boneRotationsDirty
bool bonesDirty
protected bool checkAllThreadedVertices
string conflictingFolder
protected HashSet<string> currentLoadedMorphPackageUids
protected HashSet<string> currentPreloadMorphPackageUids
protected bool demandActivatedMorphsDirty
protected List<DAZMorph> dirtyMorphs
bool enableMCMMorphs
string geometryId
protected int missedFrameCount
DAZBones morphBones
DAZBones morphBones2
protected int numMaxThreadedChangedVertices
protected int numThreadedChangedVertices
MorphFavoriteChanged onMorphFavoriteChangedHandlers
protected int[] threadedChangedVertices
protected bool threadedVerticesChanged
protected int totalFrameCount
protected bool triggerThreadResetMorphs
bool updateEnabled
bool useThreadedMorphing
protected bool visibleNonPoseThreadedVerticesChanged
bool visibleNonPoseVerticesChanged
protected bool wasInit

// properties
DAZMesh connectedMesh { get; set; }
List<DAZMorph> morphs { get; }
int numMorphs { get; }

// methods
void AddMorphAsTouched(DAZMorph morph)
bool AddMorphUsingSubBanks(DAZMorph dm)
protected void ApplyAllBoneMorphs()
protected void ApplyBoneMorphs(DAZBones bones, DAZMorph morph, bool zero)
void ApplyMorphs(bool force)
void ApplyMorphsImmediate()
protected void ApplyMorphsInternal(bool force)
void ApplyMorphsThreaded()
bool ApplyMorphsThreadedFast(Vector3[] verts, Vector3[] visibleNonPoseVerts, DAZBones bones)
void ApplyMorphsThreadedFastFinish()
bool ApplyMorphsThreadedFastOld(Vector3[] verts, Vector3[] visibleNonPoseVerts, DAZBones bones)
void ApplyMorphsThreadedFinish()
void ApplyMorphsThreadedStart()
protected void BuildMorphsByNameAndUid()
protected void BuildMorphsList()
protected void BuildMorphSubBanksByRegion()
protected void BuildMorphToRegionName()
bool CleanDemandActivatedMorphs()
protected void CleanDirtyMorphs()
protected void ClearDirtyMorphs()
void ClearPackageMorphs()
void ClearTransientMorphs()
protected void DemandActivateMorph(DAZMorph dm)
protected void EnableInit()
DAZMorph GetBuiltInMorph(string morphName)
DAZMorph GetBuiltInMorphByUid(string morphName)
DAZMorph GetMorph(string morphName)
DAZMorph GetMorphByDisplayName(string morphDisplayName)
DAZMorph GetMorphByUid(string uid)
string GetMorphRegionName(string morphName)
int GetRuntimeMorphDeltasLoadedCount()
void Init()
protected void InitMorphs()
bool IsBadMorph(string name)
bool LoadTransientMorphs(string dir)
protected void MTTask(object info)
void NotifyMorphFavoriteChanged(DAZMorph morph)
protected void OnApplicationQuit()
protected void OnDestroy()
void PrepMorphsThreadedFast()
void RebuildAllLookups()
bool RefreshPackageMorphs()
bool RefreshRuntimeMorphs()
void ReInit()
void ResetMorphs()
void ResetMorphsFast(bool resetBones)
protected bool RuntimeImportFromDir(string path, bool isTransient, bool isDemandLoaded, bool forceReload)
protected bool RuntimeImportFromDir(DirectoryEntry de, bool isTransient, bool isDemandLoaded, bool forceReload)
protected bool RuntimeImportMorph(FileEntry morphInfFile, bool isTransient, bool isDemandLoaded, bool forceReload)
protected void StartThreads()
protected void StopThreads()
bool UnloadDemandActivatedMorphs()
void UnloadRuntimeMorphDeltas()
protected void ZeroAllBoneMorphs()

// nested types
delegate MorphFavoriteChanged(DAZMorph morph)

```

### GenerateDAZMorphsControlUI

extends `GenerateTabbedUI`

```csharp
// constructors
new GenerateDAZMorphsControlUI()

// fields
protected string _filter
protected bool _forceCategoryRefresh
protected string _lowerCaseFilter
protected DAZMorphBank _morphBank1
protected DAZMorphBank _morphBank2
protected DAZMorphBank _morphBank3
protected bool _onlyShowActive
protected bool _onlyShowFavorites
protected bool _onlyShowLatest
protected SecondaryShowType _secondaryShowType
protected ShowType _showType
UIPopup categoryPopup
protected Dictionary<string, List<DAZMorph>> categoryToMorphs
protected string currentCategory
protected List<DAZMorph> currentDisplayedMorphs
UIPopup DefaultPopup
protected List<DAZMorph> filteredMorphs
InputField filterField
bool isAltUI
protected string[] morphCategories
protected Dictionary<string, DAZMorph> morphDisplayNameToMorph
protected List<DAZMorph> morphs
protected Dictionary<string, DAZMorph> morphUidToMorph
Toggle onlyShowActiveToggle
Toggle onlyShowFavoritesToggle
Toggle onlyShowLatestToggle
UIPopup secondaryShowTypePopup
UIPopup showTypePopup
UIPopup ZeroPopup

// properties
string filter { get; set; }
DAZMorphBank morphBank1 { get; set; }
DAZMorphBank morphBank2 { get; set; }
DAZMorphBank morphBank3 { get; set; }
bool onlyShowActive { get; set; }
bool onlyShowFavorites { get; set; }
bool onlyShowLatest { get; set; }
SecondaryShowType secondaryShowType { get; set; }
ShowType showType { get; set; }

// methods
bool CleanDemandActivatedMorphs()
static int CustomMorphSort(DAZMorph m1, DAZMorph m2)
void DefaultAll()
void DefaultMorph()
void DefaultPose()
void DefaultShown()
protected IEnumerator DelayedCategoryRefresh()
void DoDefaultCommand(string command)
void DoZeroCommand(string command)
void ForceCategoryRefresh()
protected void Generate()
DAZMorph GetMorphByDisplayName(string morphDisplayName)
DAZMorph GetMorphByUid(string morphUid)
List<string> GetMorphDisplayNames()
List<DAZMorph> GetMorphs()
List<string> GetMorphUids()
protected Transform InstantiateControl(Transform parent, int index)
bool IsBadMorph(string morphDisplayName)
void ResyncMorphs()
void ResyncUI()
void SetCategoryFromString(string val)
void SetFilter(string f)
protected void SetOnlyShowActive(bool b)
protected void SetOnlyShowFavorites(bool b)
protected void SetOnlyShowLatest(bool b)
protected void SetSecondaryShowType(string t)
protected void SetShowType(string t)
void TabChange(string name, bool on)
protected void UnregisterCurrentDisplayedMorphsUI()
void ZeroAll()
void ZeroMorph()
void ZeroNearZero()
void ZeroNearZeroMore()
void ZeroPose()
void ZeroShown()

// nested types
enum ShowType { MorphAndPose, Morph, Pose }
enum SecondaryShowType { All, BuiltIn, CustomAll, CustomPackage, CustomLocal, Transient, Formula }

```

### DAZSkinV2

extends `PreCalcMeshProvider`

implements `RenderSuspend`

```csharp
// constructors
new DAZSkinV2()

// fields
protected bool _applyPerformancePatches
protected int _calcChangeMatricesKernel
protected int _copyKernel
protected int _copyTangentsKernel
protected bool _hasGeneralWeights
protected int _initKernel
protected string[] _materialNames
protected ComputeBuffer _matricesBuffer
protected ComputeBuffer _normalsBuffer
protected int _nullVertexIndex
protected int _numBones
protected int _numMaterials
protected int _numSubThreads
protected ComputeBuffer _originalNormalsBuffer
protected ComputeBuffer _originalSurfaceNormalsBuffer
protected ComputeBuffer _originalTangentsBuffer
protected ComputeBuffer _originalVerticesBuffer
protected PhysicsType _physicsType
protected int _postSkinMorphKernel
protected bool _renderSuspend
protected bool _singleThreadPostVerts
protected int _skinFinishKernel
protected int _skinGeneralKernel
protected int _skinXYZKernel
protected int _skinXZYKernel
protected int _skinYXZKernel
protected int _skinYZXKernel
protected int _skinZXYKernel
protected int _skinZYXKernel
protected bool _subThreadsRunning
protected ComputeBuffer _surfaceNormalsBuffer
protected ComputeBuffer _surfaceNormalsRawBuffer
protected ComputeBuffer _tangentsBuffer
protected bool _threadsRunning
protected bool _updateDrawDisabled
protected bool _useGeneralWeights
ComputeBuffer _verticesBuffer1
ComputeBuffer _verticesBuffer2
protected bool _wasInit
protected int _zeroKernel
protected bool accumlationStarted
protected List<object> allocatedObjects
bool allowPostSkinMorph
protected bool alreadyCheckedForMeshComponents
protected int[] baseVertIndices
protected DAZVertexMap[] baseVertsToUVVertsCopy
protected static int baseVertToUVVertGroupSize
float bloat
protected ComputeBuffer[] boneBuffer
protected Matrix4x4[] boneChangeFromOriginalMatrices
protected Dictionary<string, Dictionary<int, DAZSkinV2GeneralVertexWeights>> boneGeneralWeightsMap
protected Matrix4x4[] boneLocalToWorldMatrices
protected Dictionary<string, int> boneNameToIndexMap
protected Vector3[] boneRotationAngles
protected Dictionary<string, Dictionary<int, DAZSkinV2VertexWeights>> boneWeightsMap
protected Matrix4x4[] boneWorldToLocalMatrices
float bulgeScale
protected DAZBone[] dazBones
DAZMesh dazMesh
float debugStartTime
float debugStopTime
float debugTime
int debugVertex
bool delayDisplayOneFrame
ComputeBuffer delayedNormalsBuffer
ComputeBuffer delayedTangentsBuffer
ComputeBuffer delayedVertsBuffer
bool draw
Vector3[] drawNormals
Vector3 drawOffset
Vector3[] drawSurfaceNormals
Vector4[] drawTangents
Vector3[] drawVerts
protected float f
protected ComputeBuffer[] fullWeightsBuffer
protected ComputeBuffer[] generalWeightsBuffer
int GPUAutoSwapCopyNum
bool GPUAutoSwapShader
Material[] GPUmaterials
ComputeShader GPUMeshCompute
Material GPUsimpleMaterial
ComputeShader GPUSkinner
bool GPUuseSimpleMaterial
protected List<DAZSkinV2Node> importNodes
protected bool[] isBaseVert
float laplacianSmoothBeta
int laplacianSmoothPasses
float lastFrameSkinStartTime
protected DAZSkinTaskInfo mainSkinTask
float mainThreadSkinStartTime
float mainThreadSkinStopTime
float mainThreadSkinTime
float mainThreadSmoothCorrectionStartTime
float mainThreadSmoothCorrectionStopTime
float mainThreadSmoothCorrectionTime
float mainThreadSmoothStartTime
float mainThreadSmoothStopTime
float mainThreadSmoothTime
protected MapVerticesGPU mapVerticesGPU
float mass
protected Dictionary<int, ComputeBuffer> materialNormalsBuffers
bool[] materialsEnabled
bool[] materialsShadowCastEnabled
protected Dictionary<int, ComputeBuffer> materialTangentsBuffers
protected Dictionary<int, ComputeBuffer> materialVertsBuffers
protected Mesh mesh
protected MeshCollider meshCollider
protected MeshFilter meshFilter
protected MeshRenderer meshRenderer
protected MeshSmooth meshSmooth
protected MeshSmoothGPU meshSmoothGPU
protected bool meshWasInit
protected int missedFrameCount
protected bool needsDispatch
protected bool needsPostSkinNormals
DAZSkinV2Node[] nodes
protected DAZSkinTaskInfo normalTangentTask
protected int numBaseVerts
protected int[] numGeneralSkinThreadGroups
protected int[] numSkinFinishThreadGroups
protected int[] numSkinThreadGroups
protected int numUVOnlyVerts
protected int numUVVerts
protected int numVertThreadGroups
protected RecalculateNormalsGPU originalRecalcNormalsGPU
protected RecalculateTangentsGPU originalRecalcTangentsGPU
protected bool[] postSkinBones
Vector3[] postSkinMorphs
protected ComputeBuffer postSkinMorphsBuffer
protected Vector3[] postSkinMorphsThreaded
protected DAZSkinTaskInfo postSkinMorphTask
protected int[] postSkinNeededTriangleIndices
protected int[] postSkinNeededTriangles
protected bool[] postSkinNeededVerts
protected int[] postSkinNeededVertsList
Vector3[] postSkinNormals
bool[] postSkinNormalVerts
protected RecalculateNormals postSkinRecalcNormals
bool[] postSkinVerts
bool postSkinVertsChanged
bool postSkinVertsChangedThreaded
bool[] postSkinVertsReady
protected Vector3[] postSkinWorkingNormals
protected Vector3[] postSkinWorkingSurfaceNormals
protected ComputeBuffer preCalcVertsBuffer
Vector3[] rawSkinnedVerts
Vector3[] rawSkinnedWorkingVerts
ComputeBuffer rawVertsBuffer
protected Rigidbody RB
protected RecalculateNormals recalcNormals
protected RecalculateNormalsGPU recalcNormalsGPU
protected RecalculateNormalsGPU recalcNormalsGPURaw
protected RecalculateTangents recalcTangents
protected RecalculateTangentsGPU recalcTangentsGPU
DAZBones root
string sceneGeometryId
bool showMaterials
bool showNodes
bool skin
protected static int skinGroupSize
string skinId
SkinMethod skinMethod
float skinStartTime
float skinStopTime
float skinTime
string skinUrl
protected Vector3[] smoothedVerts
ComputeBuffer smoothedVertsBuffer
int smoothOuterLoops
float springSmoothFactor
int springSmoothPasses
protected Vector3[] startNormals
protected Vector4[] startTangents
protected Vector3[] startVerts
protected ComputeBuffer startVertsBuffer
protected Vector3[] startVertsCopy
static bool staticDraw
protected Stopwatch stopwatch
protected float[] strongestBoneWeight
DAZBone[] strongestDAZBone
protected DAZSkinTaskInfo[] tasks
float threadMainSkinStartTime
float threadMainSkinStopTime
float threadMainSkinTime
float threadRecalcNormalTangentStartTime
float threadRecalcNormalTangentStopTime
float threadRecalcNormalTangentTime
float[] threadSkinStartTime
float[] threadSkinStopTime
float[] threadSkinTime
int[] threadSkinVertsCount
float[] threadSmoothCorrectionStartTime
float[] threadSmoothCorrectionStopTime
float[] threadSmoothCorrectionTime
float[] threadSmoothStartTime
float[] threadSmoothStopTime
float[] threadSmoothTime
protected int totalFrameCount
protected Vector3[] unsmoothedVerts
float updateStartTime
float updateStopTime
float updateTime
bool useAsynchronousNormalTangentRecalc
bool useAsynchronousThreadedSkinning
bool useEarlyFinish
bool useMultithreadedSkinning
bool useMultithreadedSmoothing
bool useSmoothVertsForNormalTangentRecalc
bool useThreadControlNumThreads
protected Dictionary<int, bool> vertexDoneAccumulating
protected static int vertGroupSize
protected ComputeBuffer[] weightsBuffer
protected Vector3[] workingNormals
protected Vector3[] workingSurfaceNormals
protected Vector4[] workingTangents
protected Vector3[] workingVerts
protected Vector3[] workingVerts1
protected Vector3[] workingVerts2

// properties
Mesh BaseMesh { get; }
bool hasGeneralWeights { get; }
string[] materialNames { get; }
Mesh Mesh { get; }
Mesh MeshForImport { get; }
ComputeBuffer normalsBuffer { get; }
protected GpuBuffer<Vector3> NormalsBuffer { get; set; }
int numBones { get; }
int numMaterials { get; }
int numSubThreads { get; set; }
PhysicsType physicsType { get; set; }
Vector3[] postSkinNormalsThreaded { get; }
protected GpuBuffer<Vector3> PreCalculatedVerticesBuffer { get; set; }
bool recalculateNormals { get; set; }
bool recalculateTangents { get; set; }
bool renderSuspend { get; set; }
ComputeBuffer surfaceNormalsBuffer { get; }
ComputeBuffer surfaceNormalsRawBuffer { get; }
ComputeBuffer tangentsBuffer { get; }
bool threadsRunning { get; }
GpuBuffer<Matrix4x4> ToWorldMatricesBuffer { get; }
bool useGeneralWeights { get; set; }
bool useSmoothing { get; set; }
Color[] VertexSimColors { get; }
bool wasInit { get; }

// methods
protected void CalculateStrongestWeights()
void CheckGeneralWeights()
void CopyMaterials()
protected void CreateBoneWeightsArray()
protected void DestroyAllocatedObjects()
protected bool[] DetermineUsedBonesForVerts(bool[] usedVerts)
void Dispatch()
void Dispose()
protected void DrawMesh()
void DrawMeshGPU()
protected void DrawMeshNative()
protected DAZSkinV2Node FindNodeById(string id)
protected DAZSkinV2Node FindNodeByName(string name)
protected DAZSkinV2Node FindNodeByUrl(string url)
void FlushBuffers()
Mesh GetMesh()
void Import(JSONNode jn)
void ImportNode(JSONNode jn, string url)
void ImportStart()
void Init()
protected void InitBones()
protected void InitMesh()
void InitPhysicsObjects()
protected void InitRecalcNormalsTangents()
void InitSkinTimes()
protected void InitSmoothing()
protected void MTTask(object info)
protected void NormalTangentRecalc()
protected void OnApplicationQuit()
void PostProcessDispatch(ComputeBuffer finalVerts)
void ReassignDAZMesh()
Vector3[] RecalcAndGetAllNormals(Vector3[] vertsToUse)
void RecalcBones()
protected void RecalculatePostSkinNeededVertsAndTriangles()
protected void RegisterAllocatedObject(object o)
void ResetPostSkinMorphs()
void SetNumSubThreads(int num)
void SetWorkingVerts(Vector3[] verts)
void SkinMesh(bool forceSynchronous)
protected void SkinMeshCPUandGPU(bool forceSynchronous)
void SkinMeshCPUandGPUApplyPostSkinMorphsFast()
protected void SkinMeshCPUandGPUEarlyFinish()
protected void SkinMeshCPUandGPUFinishFrame()
void SkinMeshCPUandGPUFinishFrameFast()
void SkinMeshCPUandGPUStartFrameFast()
protected void SkinMeshFinishFrame()
protected void SkinMeshGPU()
protected void SkinMeshGPUCleanup()
protected void SkinMeshGPUDelay()
protected void SkinMeshGPUFrame()
protected void SkinMeshGPUInit()
protected void SkinMeshGPUMaterialInit()
void SkinMeshGPUPostSkinVerts()
void SkinMeshGPUPostSkinVertsFinish()
void SkinMeshGPUPostSkinVertsStart()
void SkinMeshGPUPostSkinVertsThreaded()
protected void SkinMeshPart(int startIndex, int stopIndex, bool[] onlyVerts, bool[] onlyBones)
void SkinMeshPostVertsThreadedFast(Vector3[] verts)
protected void SkinMeshStartFrame(bool forceSynchronous)
protected void SkinMeshThreaded(bool forceSynchronous)
void SkinMeshThreadedFast(Vector3[] verts, bool forceSynchronous)
void SmoothAllVertices(Vector3[] inVerts, Vector3[] outVerts)
void StartSubThreads()
protected void StartThreads()
void Stop()
protected void StopSubThreads()
protected void StopThreads()
protected Dictionary<int, DAZSkinV2VertexWeights> WalkBonesAndAccumulateWeights(Transform bone)
protected Dictionary<int, DAZSkinV2VertexWeights> WalkBonesAndAccumulateWeightsRecursive(Transform bone)

// nested types
enum PhysicsType { None, Static, Rigidbody, KinematicRigidbody, Ignore }
enum SkinMethod { CPU, GPU, CPUAndGPU }

```

### DAZBone

extends `JSONStorable`

```csharp
// constructors
new DAZBone()

// fields
protected Vector3 _baseJointRotation
protected bool _jointRotationDisabled
protected Quaternion _localRotation
protected Matrix4x4 _localToWorldMatrix
protected bool _rotationMorphsEnabled
Matrix4x4 changeFromOriginalMatrix
bool childDirty
Vector3 currentAngles
Vector3 currentAnglesRadians
protected string[] customParamNames
DAZBones dazBones
protected bool detectedPhysicsCorruptionOnThread
protected bool didDetachJoint
bool disableMorph
protected static float geoScale
bool isRoot
RotationOrder jointDriveTargetRotationOrder
DAZBone parentBone
DAZBone parentForMorphOffsets
protected string physicsCorruptionType
Vector3 presetLocalRotation
Vector3 presetLocalTranslation
bool transformDirty
bool useCustomJointMap
bool useUnityEulerOrientation
protected bool wasInit
Matrix4x4 worldToLocalMatrix
JointTarget xJointMap
JointTarget yJointMap
JointTarget zJointMap

// properties
Vector3 baseJointRotation { get; set; }
FreeControllerV3 control { get; }
string id { get; set; }
Vector3 importWorldOrientation { get; }
Vector3 importWorldPosition { get; }
Quaternion inverseStartingLocalRotation { get; }
bool jointRotationDisabled { get; set; }
Vector3 maleWorldOrientation { get; }
Vector3 maleWorldPosition { get; }
Matrix4x4 morphedLocalToWorldMatrix { get; }
Vector3 morphedWorldOrientation { get; }
Vector3 morphedWorldPosition { get; }
Matrix4x4 morphedWorldToLocalMatrix { get; }
Dictionary<string, Vector3> morphOffsets { get; }
Dictionary<string, Vector3> morphOrientationOffsets { get; }
Dictionary<string, Vector3> morphRotations { get; }
Matrix4x4 nonMorphedLocalToWorldMatrix { get; }
Matrix4x4 nonMorphedWorldToLocalMatrix { get; }
bool rotationMorphsEnabled { get; set; }
RotationOrder rotationOrder { get; }
Vector3 startingLocalPosition { get; }
Quaternion startingLocalRotation { get; }
Quaternion startingRotationRelativeToRoot { get; }
Vector3 worldOrientation { get; }
Vector3 worldPosition { get; }

// methods
void ApplyOffsetTransform()
void ApplyPresetLocalTransforms()
void AttachJoint()
protected void Awake()
void DetachJoint()
void FinishThreadUpdate()
void ForceClearMorphs()
Vector3 GetAngles()
Vector3 GetAnglesDegrees()
string[] GetCustomParamNames()
JSONClass GetJSON(bool includePhysical, bool includeAppearance, bool forceStore)
void ImportNode(JSONNode jn, bool isMale)
void Init()
void PrepThreadUpdate()
void RepairJoint()
void ResetScale()
void ResetToStartingLocalPositionRotation()
void RestoreFromJSON(JSONClass jc, bool restorePhysical, bool restoreAppearance, JSONArray presetAtoms, bool setMissingToDefault)
void RestoreParent()
void RestoreTransform()
void Rotate(Vector3 rotationToUse)
void SaveAndDetachParent()
void SaveTransform()
void SetBoneOrientationXOffset(string morphName, float xoffset)
void SetBoneOrientationYOffset(string morphName, float yoffset)
void SetBoneOrientationZOffset(string morphName, float zoffset)
void SetBoneScaleX(string morphName, float xscale)
void SetBoneXOffset(string morphName, float xoffset)
void SetBoneXRotation(string morphName, float rot)
void SetBoneYOffset(string morphName, float yoffset)
void SetBoneYRotation(string morphName, float rot)
void SetBoneZOffset(string morphName, float zoffset)
void SetBoneZRotation(string morphName, float rot)
void SetImportValuesToTransform()
protected void SetJointDriveTargetRotation(ConfigurableJoint cj, Vector3 r)
void SetMorphedTransform(bool useScale, float globalScale)
void SetResetVelocity()
void SetTransformToImportValues()
void SetTransformToMorphPositionAndRotation(bool useScale, float globalScale)
void SyncMorphBoneRotations(bool force)
void ThreadsafeUpdate()

// nested types
enum JointTarget { X, NegX, Y, NegY, Z, NegZ }

```

## Audio

### AudioSourceControl

extends `VariableTrigger`

```csharp
// constructors
new AudioSourceControl()

// fields
protected AudioRolloffMode _audioRolloffMode
protected float _delayBetweenQueuedClips
protected bool _equalizeVolume
protected bool _loop
protected float _maxDistance
protected float _minDistance
protected float _pitch
protected NamedAudioClip _playingClip
protected float _spatialBlend
protected bool _spatialize
protected float _stereoPan
protected float _stereoSpread
protected float _volume
protected float _volumeTriggerMultiplier
protected float _volumeTriggerQuickness
protected JSONStorableStringChooser audioRolloffModeJSON
AudioSource audioSource
protected JSONStorableAction clearQueueJSONAction
float clipLoudness
protected JSONStorableFloat delayBetweenQueuedClipsJSON
protected float delayTimer
protected JSONStorableAction endMicrophoneInputAction
protected JSONStorableBool equalizeVolumeJSON
protected bool isPaused
protected JSONStorableBool loopJSON
protected JSONStorableFloat maxDistanceJSON
protected string micDevice
protected JSONStorableFloat minDistanceJSON
OnMicStart onMicStartHandlers
OnMicStop onMicStopHandlers
protected JSONStorableAction pauseJSONAction
protected JSONStorableFloat pitchJSON
protected JSONStorableActionAudioClip playIfClearJSONAction
protected Text playingClipNameText
protected Text playingClipNameTextAlt
protected JSONStorableActionAudioClip playNextClearQueueJSONAction
protected JSONStorableActionAudioClip playNextJSONAction
protected JSONStorableActionAudioClip playNowClearQueueJSONAction
protected JSONStorableActionAudioClip playNowJSONAction
protected JSONStorableActionAudioClip playNowLoopJSONAction
protected LinkedList<NamedAudioClip> queue
protected JSONStorableActionAudioClip queueClipJSONAction
float recentMaxLoudness
protected JSONStorableFloat recentMaxVolumeJSON
int sampleDataLength
float smoothedClipLoudness
protected JSONStorableFloat spatialBlendJSON
protected JSONStorableBool spatializeJSON
protected JSONStorableAction startMicrophoneInputAction
protected JSONStorableFloat stereoPanJSON
protected JSONStorableFloat stereoSpreadJSON
protected JSONStorableAction stopAndClearQueueJSONAction
protected JSONStorableAction stopJSONAction
protected JSONStorableAction stopLoopJSONAction
protected float timeSinceClipFinished
protected JSONStorableAction togglePauseJSONAction
protected JSONStorableAction unpauseJSONAction
protected JSONStorableFloat volumeJSON
protected JSONStorableFloat volumeTriggerMultiplierJSON
protected JSONStorableFloat volumeTriggerQuicknessJSON

// properties
AudioRolloffMode audioRolloffMode { get; set; }
float delayBetweenQueuedClips { get; set; }
bool equalizeVolume { get; set; }
bool loop { get; set; }
float maxDistance { get; set; }
protected bool MicActive { get; set; }
float minDistance { get; set; }
float pitch { get; set; }
NamedAudioClip playingClip { get; }
float spatialBlend { get; set; }
bool spatialize { get; set; }
float stereoPan { get; set; }
float stereoSpread { get; set; }
float volume { get; set; }
float volumeTriggerMultiplier { get; set; }
float volumeTriggerQuickness { get; set; }

// methods
protected void ClearPlayingClip()
void ClearQueue()
protected void CreateFloatJSON()
protected void EndMicrophoneInput()
protected void Init()
void InitUI()
void InitUIAlt()
void Pause()
protected void PlayClip(NamedAudioClip nac, bool loopClip)
void PlayIfClear(NamedAudioClip nac)
void PlayNext(NamedAudioClip nac)
void PlayNextClearQueue(NamedAudioClip nac)
void PlayNow(NamedAudioClip nac)
void PlayNowClearQueue(NamedAudioClip nac)
void PlayNowLoop(NamedAudioClip nac)
void QueueClip(NamedAudioClip nac)
void RestoreFromJSON(JSONClass jc, bool restorePhysical, bool restoreAppearance, JSONArray presetAtoms, bool setMissingToDefault)
protected void StartMicrophoneInput()
void Stop()
void StopAndClearQueue()
void StopLoop()
protected void SyncAudioRolloffMode(string s)
protected void SyncDelayBetweenQueuedClips(float f)
protected void SyncEqualizeVolume(bool b)
protected void SyncLoop(bool b)
protected void SyncMaxDistance(float f)
protected void SyncMinDistance(float f)
protected void SyncPitch(float f)
protected void SyncRolloffToUI()
protected void SyncSpatialBlend(float f)
protected void SyncSpatialize(bool b)
protected void SyncStereoPan(float f)
protected void SyncStereoSpread(float f)
protected void SyncVolume(float f)
protected void SyncVolumeTriggerMultiplier(float f)
protected void SyncVolumeTriggerQuickness(float f)
void TogglePause()
void UnPause()
protected void Update()

// nested types
delegate OnMicStart()
delegate OnMicStop()

```

### NamedAudioClip

extends `object`

```csharp
// constructors
new NamedAudioClip()

// fields
protected string _category
protected string _displayName
protected string _uid
InputField categoryField
bool destroyed
InputField displayNameField
AudioClipManager manager
AudioClip sourceClip
Button testButton
Text testButtonText
Text uidText

// properties
string category { get; set; }
AudioClip clipToPlay { get; }
string displayName { get; set; }
string uid { get; set; }

// methods
void InitUI()
protected void SetCategory(string c)
protected void SetDisplayName(string v)
void Test()

```

### URLAudioClip

extends `NamedAudioClip`

```csharp
// constructors
new URLAudioClip()

// fields
protected string _errorMsg
protected bool _ready
protected bool _removed
bool error
bool fromRestore
Slider loadProgressSlider
Toggle readyToggle
Button removeButton
Text sizeText
RectTransform UIpanel
string url

// properties
string errorMsg { get; set; }
bool ready { get; set; }
bool removed { get; }

// methods
void InitUI()
void Remove()

```

## Colour

### HSVColor

extends `ValueType`

```csharp
// fields
float H
float S
float V

```

### HSVColorPicker

extends `TwoDPicker`

```csharp
// constructors
new HSVColorPicker()

// fields
Slider blueSlider
static HSVColor clipboardColor
Transform colorObject
Image colorSample
Color currentColor
HSVColor currentHSVColor
float defaultCvalue
float defaultHue
float defaultSaturation
Slider greenSlider
Image hueImage
Slider hueSlider
OnColorChanged onColorChangedHandlers
OnHSVColorChanged onHSVColorChangedHandlers
Slider redSlider
Image saturationImage

// properties
float blue { get; set; }
int blue255 { get; set; }
string blue255string { get; set; }
float cvalue { get; set; }
float cvalueNoCallback { get; set; }
float green { get; set; }
int green255 { get; set; }
string green255string { get; set; }
float hue { get; set; }
float hueNoCallback { get; set; }
float red { get; set; }
int red255 { get; set; }
string red255string { get; set; }
float saturation { get; set; }
float saturationNoCallback { get; set; }

// methods
void Awake()
void CopyToClipboard()
static Color HSVToRGB(HSVColor hsvColor)
static Color HSVToRGB(float H, float S, float V)
void PasteFromClipboard()
void RegenerateSVImage()
void Reset()
static HSVColor RGBToHSV(float r, float g, float b)
void SetBlueFromSlider255(float b)
void SetGreenFromSlider255(float g)
void SetHSV(HSVColor hc, bool noCallback)
void SetHSV(float h, float s, float v, bool noCallback)
void SetRedFromSlider255(float r)
protected void SetXYValFromSelectorPosition()

// nested types
delegate OnColorChanged(Color color)
delegate OnHSVColorChanged(float hue, float saturation, float value)

```

## Assets, images, presets, preferences

### MeshVR.AssetLoader

extends `MonoBehaviour`

```csharp
// constructors
new AssetLoader()

// fields
protected List<AssetBundleFromFileRequest> assetBundleFromFileQueue
protected Dictionary<string, int> assetBundleReferenceCounts
protected Dictionary<string, AssetBundle> pathToAssetBundle
protected List<SceneLoadIntoTransformRequest> sceneLoadIntoTransformQueue
protected static AssetLoader singleton

// methods
protected IEnumerator AssetBundleFromFileQueueProcessor()
static void DoneWithAssetBundleFromFile(string path)
protected IEnumerator LoadBundleFileAsync(AssetBundleFromFileRequest abffr)
protected IEnumerator LoadSceneIntoTransformAsync(SceneLoadIntoTransformRequest slr)
static void QueueLoadAssetBundleFromFile(AssetBundleFromFileRequest abffr)
static void QueueLoadSceneIntoTransform(SceneLoadIntoTransformRequest slr)
protected IEnumerator SceneLoadIntoTransfromQueueProcessor()

// nested types
delegate LoadBundleFromFileCallback(AssetBundleFromFileRequest abffr)
class AssetBundleFromFileRequest
delegate LoadSceneIntoTransformCallback(SceneLoadIntoTransformRequest slr)
class SceneLoadIntoTransformRequest

```

### AssetBundles.AssetBundleManager

extends `MonoBehaviour`

```csharp
// constructors
new AssetBundleManager()

// fields
protected List<string> keysToRemove

// properties
string[] ActiveVariants { get; set; }
AssetBundleManifest AssetBundleManifestObject { set; }
string BaseDownloadingURL { get; set; }
LogMode logMode { get; set; }

// methods
static LoadedAssetBundle GetLoadedAssetBundle(string assetBundleName, out string error)
static int GetNumberOfLoadedAssetBundles()
static AssetBundleLoadManifestOperation Initialize()
static AssetBundleLoadManifestOperation Initialize(string manifestAssetBundleName)
static AssetBundleLoadAssetOperation LoadAssetAsync(string assetBundleName, string assetName, Type type)
protected static void LoadAssetBundle(string assetBundleName, bool isLoadingAssetBundleManifest)
protected static bool LoadAssetBundleInternal(string assetBundleName, bool isLoadingAssetBundleManifest)
protected static void LoadDependencies(string assetBundleName)
static AssetBundleLoadOperation LoadLevelAsync(string assetBundleName, string levelName, bool isAdditive)
static void RegisterAssetBundleAdditionalUse(string assetBundleName)
protected static string RemapVariantName(string assetBundleName)
static void ReportLoadedAssetBundles()
static void SetDevelopmentAssetBundleServer()
static void SetSourceAssetBundleDirectory(string relativePath)
static void SetSourceAssetBundleURL(string absolutePath)
static void UnloadAssetBundle(string assetBundleName)
protected static bool UnloadAssetBundleInternal(string assetBundleName)
protected static void UnloadDependencies(string assetBundleName)

// nested types
enum LogMode { All, JustErrors }
enum LogType { Info, Warning, Error }

```

### AssetBundles.AssetBundleLoadAssetOperation

extends `AssetBundleLoadOperation`

```csharp
// constructors
protected new AssetBundleLoadAssetOperation()

// methods
T GetAsset()

```

### ImageLoaderThreaded

extends `MonoBehaviour`

```csharp
// constructors
new ImageLoaderThreaded()

// fields
protected bool _threadsRunning
protected ImageLoaderTaskInfo imageLoaderTask
protected Dictionary<string, Texture2D> immediateTextureCache
protected AsyncFlag loadFlag
protected int numRealQueuedImages
protected int progress
GameObject progressHUD
protected int progressMax
Slider progressSlider
Text progressText
protected LinkedList modreq(System.Runtime.CompilerServices.IsVolatile) queuedImages
static ImageLoaderThreaded singleton
protected Dictionary<string, Texture2D> textureCache
protected Dictionary<Texture2D, bool> textureTrackedCache
protected Dictionary<Texture2D, int> textureUseCount
protected Dictionary<string, Texture2D> thumbnailCache

// methods
void ClearCacheThumbnail(string imgPath)
bool DeregisterTextureUse(Texture2D tex)
Texture2D GetCachedThumbnail(string path)
protected void MTTask(object info)
protected void OnApplicationQuit()
protected void OnDestroy()
void OptimizeMemory()
protected void PostProcessImageQueue()
protected void PreprocessImageQueue()
void ProcessImageImmediate(QueuedImage qi)
protected void ProcessImageQueueThreaded()
void PurgeAllImmediateTextures()
void PurgeAllTextures()
void PurgeAllThumbnailTextures()
void QueueImage(QueuedImage qi)
void QueueThumbnail(QueuedImage qi)
void QueueThumbnailImmediate(QueuedImage qi)
bool RegisterTextureUse(Texture2D tex)
protected void RemoveCanceledImages()
protected void ReportMemory(List<string> reports)
void ReportOnTextures()
protected void StartThreads()
protected void StopThreads()
protected void UseCachedTex(QueuedImage qi, Texture2D tex)

// nested types
delegate ImageLoaderCallback(QueuedImage qi)
class QueuedImage

```

### MeshVR.PresetManager

extends `MonoBehaviour`

```csharp
// constructors
new PresetManager()

// fields
protected bool _paramsLocked
protected string _presetName
protected bool _tempUnlockParams
string[] conditionalFlagsToStore
ConditionalLoadEvent[] conditionalLoadEvents
string creatorName
string customPath
protected List<Storable> dynamicStorables
Transform[] dynamicStorablesBuckets
JSONClass filteredJSON
bool ignoreExclude
bool includeAppearance
bool includeChildrenMaterialOptions
bool includeOptional
bool includeOptional2
bool includeOptional3
bool includePhysical
protected bool isMergeRestore
ItemType itemType
JSONClass lastLoadedJSON
bool neverSetUnlistedParamsToDefault
bool onlyCallPostLoadEventIfIncludeOptional
bool optionalFirst
SpecificStorable[] optionalSpecificStorables
SpecificStorable[] optionalSpecificStorables2
SpecificStorable[] optionalSpecificStorables3
protected List<Storable> optionalStorables
protected List<Storable> optionalStorables2
protected List<Storable> optionalStorables3
string package
UnityEvent postLoadEvent
UnityEvent postLoadOptimizeEvent
protected string presetPackageName
protected string presetPackagePath
protected string presetSubName
protected string presetSubPath
protected Dictionary<JSONStorable, bool> regularStorables
bool setConditionalFlagsOnLoadDefaults
bool setOptional2ToDefaultOnRestore
bool setOptional3ToDefaultOnRestore
bool setOptionalToDefaultOnRestore
protected bool setUnlistedDynamicStorableParamsToDefault
protected bool setUnlistedParamsToDefault
protected Dictionary<string, bool> specificKeyStorables
SpecificStorable[] specificStorables
protected List<Storable> storables
bool storeConditionalFlagsAlways
bool storeConditionalFlagsWhenStoreOptional
bool storeConditionalFlagsWhenStoreOptional2
bool storeConditionalFlagsWhenStoreOptional3
string storedCreatorName
string storeFolderName
string storeName
bool storeOptionalStorables
bool storeOptionalStorables2
bool storeOptionalStorables3
bool storePresetBinary
protected string storeRoot
bool useTransformAndChildren

// properties
bool paramsLocked { get; set; }
string presetName { get; set; }
protected bool tempUnlockParams { get; set; }

// methods
protected void Awake()
bool CheckPresetExistance()
bool CheckPresetReadyForStore()
protected void ClearLockStorablesInList(List<Storable> sts)
void CreateStoreFolderPath()
void DeletePreset()
protected void FilterStorables(JSONClass inputjc, JSONClass outputjc, List<Storable> sts)
List<string> FindFavoriteNames()
string GetFavoriteStorePath()
string GetPresetNameFromFilePath(string fpath)
string GetStoreFolderPath(bool includePackage)
string GetStorePathBase()
string GetStoreRootPath(bool includePackage)
bool IsFavorite()
bool IsInPackage()
bool IsPresetInPackage()
protected void LateRestore(JSONClass jc)
bool LoadDefaultsPre()
protected void LoadDefaultsPreInternal()
protected void LoadDefaultsPreProcessStorables(List<Storable> sts, JSONArray outputStorables)
bool LoadPreset()
void LoadPresetFromJSON(JSONClass inputJSON, bool isMerge)
bool LoadPresetPost()
bool LoadPresetPre(bool isMerge)
protected void LoadPresetPreFromJSON(JSONClass inputJSON, bool isMerge)
protected void LockStorablesInList(List<Storable> sts)
bool MergeLoadPreset()
string[] PathToNames(string inpath)
protected void PostRestore()
protected void PostRestoreStorable(JSONStorable js)
protected void PreRestore()
protected void PreRestoreStorable(JSONStorable js)
protected void RefreshDynamicStorables()
protected void RefreshSpecificStorables(SpecificStorable[] spStorables, List<Storable> outputStorables)
void RefreshStorables()
protected void Restore(JSONClass jc)
void RestorePresetBinary()
protected void RestoreStorables(JSONClass jc)
void SetFavorite(bool b)
void SetNamesFromPath(string path)
bool StorePreset(bool storeAll, bool doScreenshot)
bool StorePreset(bool doScreenshot)
protected void StorePresetBinary()
protected void StoreStorables(JSONClass jc, bool storeAll)
protected void StoreStorablesInList(JSONClass jc, List<Storable> sts, bool storeAll)
void SyncMaterialOptions()
void SyncParamsLocked()

// nested types
class SpecificStorable
class Storable
enum ItemType { None, Custom, ClothingFemale, ClothingMale, ClothingNeutral, Atom, HairFemale, HairMale, HairNeutral }
class ConditionalLoadEvent

```

### MeshVR.PresetManagerControl

extends `JSONStorable`

```csharp
// constructors
new PresetManagerControl()

// fields
protected JSONStorableAction autoTypeLoadPresetAction
protected JSONStorableAction clearUserDefaultsAction
PresetManagerControlOverview controlOverview
protected JSONStorableBool favoriteJSON
protected JSONStorableStringChooser favoriteSelectionJSON
protected bool forceLoadOnPresetBrowsePathSync
protected bool forceUseMergeLoad
protected bool forceUseRegularLoad
protected JSONStorableBool includeAppearanceJSON
protected JSONStorableBool includeOptional2JSON
protected JSONStorableBool includeOptional3JSON
protected JSONStorableBool includeOptionalJSON
protected JSONStorableBool includePhysicalJSON
protected bool isLoadingPreset
protected JSONStorableAction loadDefaultsAction
protected JSONStorableAction loadPresetAction
protected JSONStorableBool loadPresetOnSelectJSON
protected JSONStorableString loadPresetWithNameJSON
protected JSONStorableActionPresetFilePath loadPresetWithPathJSON
protected JSONStorableUrl loadPresetWithPathUrlJSON
protected JSONStorableAction loadUserDefaultsAction
protected JSONStorableBool lockParamsJSON
protected JSONStorableAction mergeLoadPresetAction
protected JSONStorableString mergeLoadPresetWithNameJSON
protected JSONStorableActionPresetFilePath mergeLoadPresetWithPathJSON
protected JSONStorableUrl mergeLoadPresetWithPathUrlJSON
protected JSONStorableAction openPresetBrowsePathInExplorerAction
protected PresetManager pm
protected JSONStorableUrl presetBrowsePathJSON
protected JSONStorableString presetNameJSON
bool setMaterialOptionsTexturePaths
bool showMergeLoad
protected Text statusText
protected Text statusTextAlt
protected JSONStorableBool storeOptional2JSON
protected JSONStorableBool storeOptional3JSON
protected JSONStorableBool storeOptionalJSON
protected JSONStorableAction storeOverlayPresetAction
protected JSONStorableAction storeOverlayPresetWithScreenshotAction
protected JSONStorableAction storePresetAction
protected JSONStorableBool storePresetBinaryJSON
protected JSONStorableBool storePresetNameJSON
protected JSONStorableString storePresetWithNameJSON
protected JSONStorableAction storePresetWithScreenshotAction
protected JSONStorableAction storeUserDefaultsAction
protected JSONStorableBool useMergeLoadJSON

// properties
bool lockParams { get; set; }

// methods
protected void AutoTypeLoadPreset()
protected void Awake()
protected void BeginBrowse(JSONStorableUrl jsurl)
void ClearUserDefaults()
protected void EndBrowse(JSONStorableUrl jsurl)
protected void Init()
protected void InitUI(Transform t, bool isAlt)
protected void LoadDefaults()
protected void LoadPreset()
protected void LoadPresetWithPath(string p)
void LoadUserDefaults()
protected void MergeLoadPreset()
protected void MergeLoadPresetWithPath(string p)
void OpenPresetBrowsePathInExplorer()
void PreRestore()
protected void RefreshFavoriteNames()
void RestoreFromJSON(JSONClass jc, bool restorePhysical, bool restoreAppearance, JSONArray presetAtoms, bool setMissingToDefault)
protected void SetStatus(string status)
protected void StoreOverlayPreset()
protected void StoreOverlayPreset(bool skipSync)
protected void StoreOverlayPresetWithScreenshot()
protected void StoreOverlayPresetWithScreenshot(bool skipSync)
protected void StorePreset()
protected void StorePreset(bool skipSync)
protected void StorePresetWithScreenshot(bool skipSync)
protected void StorePresetWithScreenshot()
void StoreUserDefaults()
protected void SyncFavorite(bool b)
protected void SyncFavoriteSelection(string s)
protected void SyncIncludeAppearance(bool b)
protected void SyncIncludeOptional(bool b)
protected void SyncIncludeOptional2(bool b)
protected void SyncIncludeOptional3(bool b)
protected void SyncIncludePhysical(bool b)
protected void SyncLoadPresetWithName(string s)
protected void SyncLockParams(bool b)
protected void SyncMergeLoadPresetWithName(string s)
protected void SyncPresetBrowsePath(string url)
protected void SyncPresetLoadButton()
protected void SyncPresetName(string s)
protected void SyncPresetStoreButton()
void SyncPresetUI()
protected void SyncStoreOptional(bool b)
protected void SyncStoreOptional2(bool b)
protected void SyncStoreOptional3(bool b)
protected void SyncStorePresetBinary(bool b)
protected void SyncStorePresetName(bool b)
protected void SyncStorePresetWithName(string s)

```

### UserPreferences

extends `MonoBehaviour`

```csharp
// constructors
new UserPreferences()

// fields
protected DirectoryOption _fileBrowserDirectoryOption
protected SortBy _fileBrowserSortBy
Toggle allowNonWhitelistDomainsToggle
Toggle allowNonWhitelistDomainsToggleAlt
Toggle allowNonWhitelistDomainsToggleAlt2
Toggle allowNonWhitelistDomainsToggleAlt3
HandModelControl alternateMotionHandModelControl
Toggle alwaysAllowPluginsDownloadedFromHubToggle
Toggle alwaysAllowPluginsDownloadedFromHubToggleAlt
Toggle alwaysShowPointersOnTouchToggle
Text autoPhysicsRateText
Button browseCacheFolderButton
Button browseDAZDefaultContentFolderButton
Button browseDAZExtraLibraryFolderButton
Text cacheFolderText
Button clearDAZDefaultContentFolderButton
Button clearDAZExtraLibraryFolderButton
Toggle closeObjectBlurToggle
Toggle confirmLoadToggle
InputField creatorNameInputField
Image crosshair
Slider crosshairAlphaSlider
Toggle customQualityToggle
Text DAZDefaultContentFolderText
Text DAZExtraLibraryFolderText
float defaultFingerBendOffset
float defaultFingerInputFactor
float defaultFingerSpreadOffset
float defaultSteamVRPointerAngle
float defaultThumbBendOffset
float defaultThumbInputFactor
float defaultThumbSpreadOffset
Shader defaultUIShader
Toggle desktopVsyncToggle
protected List<MKGlow> dynamicGlow
Toggle enableCachingToggle
Toggle enableHubDownloaderToggle
Toggle enableHubDownloaderToggleAlt
Toggle enableHubToggle
Toggle enableHubToggleAlt
Toggle enablePluginsToggle
Toggle enablePluginsToggleAlt
Toggle enableWebBrowserProfileToggle
Toggle enableWebBrowserProfileToggleAlt
Toggle enableWebBrowserToggle
Toggle enableWebBrowserToggleAlt
Toggle enableWebMiscToggle
Toggle enableWebMiscToggleAlt
RectTransform enableWhenHideInactiveTargets
RectTransform enableWhenShowInactiveTargets
GameObject exitButton
Slider fingerBendOffsetSlider
Slider fingerInputFactorSlider
Slider fingerSpreadOffsetSlider
GameObject[] firstTimeUserDisableGameObjects
GameObject[] firstTimeUserEnableGameObjects
Toggle flipToolbarToggle
UIPopup glowEffectsPopup
protected int glowObjectCount
MKGlow[] glowObjects
Transform headCollider
Toggle hideDisabledWebMessagesToggle
Toggle hideExitButtonToggle
Toggle hideInactiveTargetsToggle
Toggle highQualityToggle
LeapHandModelControl leapHandModelControl
bool loadPrefsFileOnStart
Toggle lowQualityToggle
Toggle maxQualityToggle
Toggle midQualityToggle
Camera mirrorReflectionCamera1
Camera mirrorReflectionCamera2
Toggle mirrorReflectionsToggle
Toggle mirrorToggle
HandModelControl motionHandModelControl
UIPopup msaaPopup
Camera normalCamera
Toggle oculusDisableFreeMoveToggle
Toggle oculusSwapGrabAndTriggerToggle
Toggle optimizeMemoryOnPresetLoadToggle
Toggle optimizeMemoryOnSceneLoadToggle
Shader overlayUIShader
Toggle overlayUIToggle
OVRHandInput ovrLeftHandInput
OVRHandInput ovrRightHandInput
Image panelForUIMaterial
Toggle physicsHighQualityToggle
UIPopup physicsRatePopup
UIPopup physicsUpdateCapPopup
UIPopup pixelLightCountPopup
Slider pointLightShadowBiasBaseSlider
Slider pointLightShadowBlurSlider
Toggle realtimeReflectionProbesToggle
Slider renderScaleSlider
Button resetCacheFolderButton
Button reviewTermsButton
UIPopup shaderLODPopup
Slider shadowFilterLevelSlider
Toggle showControllersMenuOnlyToggle
Toggle showTargetsMenuOnlyToggle
static UserPreferences singleton
UIPopup smoothPassesPopup
Toggle softPhysicsToggle
SteamVR_RenderModel steamVRLeftControllerModel
HandInput steamVRLeftHandInput
Transform steamVRLeftHandPointer
SteamVR_Behaviour_Skeleton steamVRLeftHandSkeleton
Slider steamVRPointerAngleSlider
SteamVR_RenderModel steamVRRightControllerModel
HandInput steamVRRightHandInput
Transform steamVRRightHandPointer
SteamVR_Behaviour_Skeleton steamVRRightHandSkeleton
Toggle steamVRShowControllersToggle
Toggle steamVRUseControllerHandPoseToggle
Slider targetAlphaSlider
Button termsAndSettingsAcceptedButton
GameObject termsNotAcceptedGameObject
Toggle termsOfUseAcceptedToggle
Slider thumbBendOffsetSlider
Slider thumbInputFactorSlider
Slider thumbSpreadOffsetSlider
ChildOrderFlip toolbarFlipper
Toggle ultraLowQualityToggle
Toggle ultraQualityToggle
Toggle useHeadColliderToggle
Toggle useMonitorViewOffsetWhenUIOpenToggle
protected string[] whitelistDomainPaths
protected HashSet<string> whitelistDomains

// properties
bool allowNonWhitelistDomains { get; set; }
bool allowPluginsNetworkAccess { get; }
bool alwaysAllowPluginsDownloadedFromHub { get; set; }
bool alwaysShowPointersOnTouch { get; set; }
string cacheFolder { get; }
bool closeObjectBlur { get; set; }
bool confirmLoad { get; set; }
string creatorName { get; set; }
float crosshairAlpha { get; set; }
string DAZDefaultContentFolder { get; set; }
string DAZExtraLibraryFolder { get; set; }
bool desktopVsync { get; set; }
bool enableCaching { get; set; }
bool enableHub { get; set; }
bool enableHubDownloader { get; set; }
bool enablePlugins { get; set; }
bool enableWebBrowser { get; set; }
bool enableWebBrowserProfile { get; set; }
bool enableWebMisc { get; set; }
DirectoryOption fileBrowserDirectoryOption { get; set; }
SortBy fileBrowserSortBy { get; set; }
float fingerBendOffset { get; set; }
float fingerInputFactor { get; set; }
float fingerSpreadOffset { get; set; }
bool firstTimeUser { get; set; }
bool flipToolbar { get; set; }
GlowEffectsLevel glowEffects { get; set; }
bool hideDisabledWebMessages { get; set; }
bool hideExitButton { get; set; }
bool hideInactiveTargets { get; set; }
bool mirrorReflections { get; set; }
bool mirrorToDisplay { get; set; }
int msaaLevel { get; set; }
bool oculusDisableFreeMove { get; set; }
bool oculusSwapGrabAndTrigger { get; set; }
bool optimizeMemoryOnPresetLoad { get; set; }
bool optimizeMemoryOnSceneLoad { get; set; }
bool overlayUI { get; set; }
bool pauseGlow { get; set; }
bool physicsHighQuality { get; set; }
PhysicsRate physicsRate { get; set; }
int physicsUpdateCap { get; set; }
int pixelLightCount { get; set; }
float pointLightShadowBiasBase { get; set; }
float pointLightShadowBlur { get; set; }
bool realtimeReflectionProbes { get; set; }
float renderScale { get; set; }
ShaderLOD shaderLOD { get; set; }
float shadowFilterLevel { get; set; }
bool shouldLoadPrefsFileOnStart { get; }
bool showControllersMenuOnly { get; set; }
bool showTargetsMenuOnly { get; set; }
int smoothPasses { get; set; }
bool softPhysics { get; set; }
float steamVRPointerAngle { get; set; }
bool steamVRShowControllers { get; set; }
bool steamVRUseControllerHandPose { get; set; }
float targetAlpha { get; set; }
bool termsOfUseAccepted { get; set; }
float thumbBendOffset { get; set; }
float thumbInputFactor { get; set; }
float thumbSpreadOffset { get; set; }
bool useHeadCollider { get; set; }
bool useMonitorViewOffsetWhenUIOpen { get; set; }

// methods
void BrowseCacheFolder()
void BrowseDAZDefaultContentFolder()
void BrowseDAZExtraLibraryFolder()
bool CheckWhitelistDomain(string url)
void ClearDAZDefaultContentFolder()
void ClearDAZExtraLibraryFolder()
void DeregisterGlowCamera(MKGlow mkg)
void DeregisterGlowObject()
void DisableFirstTimeUser()
protected string ExtractTopDomain(string url)
void HideInactiveTargets()
void RegisterGlowCamera(MKGlow mkg)
void RegisterGlowObject()
void ResetCacheFolder()
void ResetPreferences()
void RestorePreferences()
void ReviewTerms()
void SavePreferences()
protected void SetDAZDefaultContentFolder(string folder)
protected void SetDAZExtraLibraryFolder(string folder)
void SetFileBrowserDirectoryOption(string dirOptionString)
void SetFileBrowserSortBy(string sortByString)
void SetGlowEffectsFromString(string ge)
void SetMsaaFromString(string levelString)
void SetPhysicsRateFromString(string pr)
void SetPhysicsUpdateCapFromString(string pr)
void SetPixelLightCountFromString(string countString)
void SetQuality(string qualityName)
void SetShaderLODFromString(string lod)
void SetSmoothPassesFromString(string levelString)
void ShowInactiveTargets()
protected void SyncEnableHub()
protected void SyncEnableHubDownloader()
protected void SyncEnableWebBrowser()
protected void SyncEnableWebBrowserProfile()
protected void SyncFirstTimeUser()
void SyncWhitelistDomains()
protected void TermsAndSettingsAcceptedPressed()

// nested types
enum ShaderLOD { Low, Medium, High }
enum GlowEffectsLevel { Off, Low, High }
enum PhysicsRate { Auto, _45, _60, _72, _80, _90, _120, _144, _240, _288 }
enum SortBy { AtoZ, ZtoA, NewToOld, OldToNew, NewToOldPackage, OldToNewPackage }
enum DirectoryOption { ShowFirst, ShowLast, Intermix, Hide }

```

### DAZClothingItem

extends `DAZDynamicItem`

```csharp
// constructors
new DAZClothingItem()

// fields
protected bool _jointAdjustEnabled
bool adjustFemaleBreastJointSpringAndDamper
bool adjustFemaleGluteJointSpringAndDamper
float breastJointSpringAndDamperMultiplier
protected DAZClothingItemControl[] clothingItemControls
Vector3 colliderDimensions
BoxCollider colliderLeft
Vector3 colliderLeftCenter
Vector3 colliderLeftRotation
BoxCollider colliderRight
Vector3 colliderRightCenter
Vector3 colliderRightRotation
ColliderType colliderTypeLeft
ColliderType colliderTypeRight
float drive2XAngleTarget
FreeControllerV3 drive2XAngleTargetController1
ControllerType drive2XAngleTargetController1Type
FreeControllerV3 drive2XAngleTargetController2
ControllerType drive2XAngleTargetController2Type
float driveXAngleTarget
FreeControllerV3 driveXAngleTargetController1
ControllerType driveXAngleTargetController1Type
FreeControllerV3 driveXAngleTargetController2
ControllerType driveXAngleTargetController2Type
ExclusiveRegion exclusiveRegion
float gluteJointSpringAndDamperMultiplier

// properties
bool jointAdjustEnabled { get; set; }

// methods
HashSet<string> GetAllClothingOtherTags()
protected void InitInstance()
bool IsClothingUIDAvailable(string uid)
protected void OnDisable()
protected void OnEnable()
void PartialResetPhysics()
void RefreshClothingItems()
void RefreshClothingItemThumbnail(string thumbPath)
void ResetPhysics()
protected void SetHidePath()
void SetLocked(bool b)
protected void SetUserPrefsPath()
void SyncClothingAdjustments()
protected void SyncOtherTags()

// nested types
enum ExclusiveRegion { None, UnderHip, UnderChest, Hip, Chest, Shoes, Glasses, Gloves, Hat, Legs }
enum ColliderType { None, Shoe }
enum ControllerType { None, LeftFoot, RightFoot, LeftToe, RightToe }

```

### DAZHairGroup

extends `DAZDynamicItem`

```csharp
// constructors
new DAZHairGroup()

// fields
CapsuleCollider[] hairColliders
protected DAZHairGroupControl[] hairGroupControls
DAZBones rootBonesForSkinning

// methods
protected void Connect()
HashSet<string> GetAllHairOtherTags()
protected void InitInstance()
bool IsHairUIDAvailable(string uid)
void PartialResetPhysics()
void RefreshHairItems()
void RefreshHairItemThumbnail(string thumbPath)
void ResetPhysics()
protected void SetHidePath()
void SetLocked(bool b)
protected void SetUserPrefsPath()
void SyncHairAdjustments()
protected void SyncOtherTags()

```

### CameraTarget

extends `MonoBehaviour`

```csharp
// constructors
new CameraTarget()

// fields
CameraLocation cameraLocation
static CameraTarget centerTarget
bool isMonitorCamera
static CameraTarget leftTarget
Matrix4x4 projectionMatrix
static CameraTarget rightTarget
Camera targetCamera
Matrix4x4 worldToCameraMatrix

// methods
void FindCamera()

// nested types
enum CameraLocation { None, Left, Right, Center }

```

### AsyncFlag

extends `object`

```csharp
// constructors
new AsyncFlag(string name)

// properties
string Name { get; set; }
protected bool Raised { get; set; }

// methods
void Lower()
void Raise()

```

### MVR.FileManagementSecure.ShortCut

extends `object`

```csharp
// constructors
new ShortCut()

// fields
string creator
DirectoryEntry directoryEntry
string displayName
bool flatten
bool includeRegularDirsInFlatten
bool isLatest
string package
string packageFilter
string path

// properties
bool isHidden { get; }

// methods
bool IsSameAs(ShortCut otherShortCut)

```

### UITabSelector

extends `MonoBehaviour`

```csharp
// constructors
new UITabSelector()

// fields
Color activeColor
protected bool activeTabChanged
static UITabSelector activeTabSelector
Color normalColor
string startingTabName
Image tabBackground
Transform toggleContainer

// properties
protected string activeTabName { get; set; }
string alternateStartingTabName { get; set; }

// methods
void ActiveTabChanged()
protected void ActiveTabChangedProcess()
bool HasTab(string name)
void SelectNextTab()
void SelectPreviousTab()
void SetActiveTab(string name)

```

### MotionAnimationControl

extends `JSONStorable`

```csharp
// constructors
new MotionAnimationControl()

// fields
protected MotionAnimationMaster _animationMaster
protected JSONStorableBool armedForRecordJSON
MotionAnimationClip clip
FreeControllerV3 controller
protected string[] customParamNames
protected JSONStorableBool drawPathJSON
bool drawPathOpt
Material material
protected Mesh mesh
protected JSONStorableBool playbackEnabledJSON
bool suspendPositionPlayback
bool suspendRotationPlayback

// properties
MotionAnimationMaster animationMaster { get; set; }
bool armedForRecord { get; set; }
bool drawPath { get; set; }
bool playbackEnabled { get; set; }

// methods
protected void ApplyStep(MotionAnimationStep step, bool forceAlignControlled)
protected void Awake()
void ClearAnimation()
void FinalizeRecord()
string[] GetCustomParamNames()
JSONClass GetJSON(bool includePhysical, bool includeAppearance, bool forceStore)
protected void Init()
void InitUI()
void InitUIAlt()
void LoopbackStep(float percent, float toTimeStep)
void PlaybackStep(float playbackCounter)
void PlaybackStepForceAlign(float playbackCounter)
void PrepareRecord(int recordCounter)
void RecordStep(float recordCounter, bool forceRecord)
void RestoreFromJSON(JSONClass jc, bool restorePhysical, bool restoreAppearance, JSONArray presetAtoms, bool setMissingToDefault)
void TrimClip(float startStep, float stopStep)
protected void Update()

```

---

## Appendix - every public type in Assembly-CSharp

Not documented in full above. To dump one, add it to `\System.Collections.Specialized.OrderedDictionary` in
`build/gen-api-doc.ps1` and regenerate.

**(global)** (836)

```
AdjustJointCOG                    AdjustJointDamper                 AdjustJointHardPositionTargets    AdjustJointPositionDrive
AdjustJointPositionTarget         AdjustJointPositionTargets        AdjustJointPositionTargetsAndSetDAZMorphAdjustJoints
AdjustJointSpring                 AdjustJointSpringOnCollision      AdjustJointSprings                AdjustJointSpringsControl
AdjustJointSpringsControlUI       AdjustJointsUI                    AdjustJointTarget                 AdjustJointTargetAndCOG
AdjustJointTargetAndSpring        AdjustJointTargets                AdjustJointXDrive                 AdjustJointXDriveUI
AdjustLightV2                     AdjustLightV2UI                   AdjustMaterialFromLight           AdjustRotationTarget
AlertUI                           AlignTransform                    AllJointsController               AllJointsControllerUI
AmbienceEmitter                   Analysis                          AnalysisListAttribute             AnimatedDAZBoneDirectDrive
AnimatedDAZBoneDirectDrives       AnimatedDAZBoneDirectDrivesUI     AnimatedDAZBoneMoveProducer       AnimationPattern
AnimationPatternUI                AnimationPose                     AnimationPoseJoint                AnimationSequence
AnimationSequenceClip             AnimationSequenceClipUI           AnimationStep                     AnimationStepTriggerUI
AnimationStepUI                   AnimationStepUIConnector          AnimationTimelineTrigger          AnimationTimelineTriggerHandler
AnimationTimelineTriggerUI        AQUAS_BubbleBehaviour             AQUAS_BubbleMorph                 AQUAS_Buoyancy
AQUAS_Camera                      AQUAS_Caustics                    AQUAS_LensEffects                 AQUAS_Parameters
AQUAS_Reflection                  AQUAS_RenderQueueEditor           AQUAS_SmallBubbleBehaviour        AssertHelper
AssetHolder                       assetLoadedCallback               AsyncFlag                         Atom
AtomUI                            AtomUIConnector                   AudioClipManager                  AudioManager
AudioSourceControl                AudioSourceControlUI              AudioSourceControlUIConnector     AutoCollider
AutoColliderBatchUpdater          AutoColliderGroup                 AvatarLayer                       BakedSkinMesh
BaseStarDataRenderer              BasicLookInputModule              BlendList                         Blinker
BoatSimulator                     BufferedAudioStream               Buoyancy                          ButtonControl
CameraControl                     CameraControlUI                   CameraTarget                      CanvasSizeControl
CanvasSizeControlUI               CanvasSizeControlUIConnector      CapsuleToolControl                CapsuleToolControlUI
CausticDecal                      CenterTransformOnCam              CharacterControl2D                CharacterPoseSnapRestore
CharacterPoseSnapRestoreUI        ChildOrderFlip                    ClothSimControl                   ClothSimControlUI
CodeExample                       ColliderHighlighter               CollisionDebug                    CollisionDebugs
CollisionEventHandler             CollisionScreenFade               CollisionTrigger                  CollisionTriggerEventHandler
CollisionTriggerGroup             CollisionTriggerGroupUI           CollisionTriggerUI                ColorChangedEvent
CommandLineChecker                ConfigurableJointReconnector      ConfigurableJointScaler           CopyToClipboard
CPUStarDataRenderer               CraneController                   CubeGenerator                     CubicBezierControlPoint
CubicBezierCurve                  CubicBezierPoint                  CursorController                  CustomUnityAssetLoader
CustomUnityAssetLoaderUI          CycleForceProducerV2              CycleForceProducerV2UI            CycleForceProducerV2UIConnector
CycleHandPairs                    DAZBone                           DAZBones                          DAZBulgeFactors
DAZCharacter                      DAZCharacterMaterialOptions       DAZCharacterRun                   DAZCharacterRunMetricsUI
DAZCharacterSelector              DAZCharacterSelectorUI            DAZCharacterTextureControl        DAZCharacterTextureControlUI
DAZClothingItem                   DAZClothingItemControl            DAZClothingItemControlUI          DAZDynamicItem
DAZDynamicItemLabel               DAZDynamicItemLabelUI             DAZDynamicItemUI                  DAZHairGroup
DAZHairGroupControl               DAZHairGroupControlUI             DAZHairMesh                       DAZHairMeshMaterialOptions
DAZMaleAnatomy                    DAZMaleAnatomyControl             DAZMergedMesh                     DAZMergedSkin
DAZMergedSkinV2                   DAZMesh                           DAZMeshData                       DAZMeshDrawOffsetControl
DAZMeshDrawOffsetControlUI        DAZMeshEyelidControl              DAZMeshEyelidControlUI            DAZMeshGraft
DAZMeshGraftVertexPair            DAZMeshMaterialOptions            DAZMeshSelection                  DAZMeshSelectionDebug
DAZMeshVertexWeights              DAZMorph                          DAZMorphBank                      DAZMorphFormula
DAZMorphFormulaTargetType         DAZMorphSet                       DAZMorphSplit                     DAZMorphSubBank
DAZMorphTaskInfo                  DAZMorphTaskType                  DAZMorphUI                        DAZMorphVertex
DAZMorphVertexFast                DAZMorphVertexFastBatch           DAZNode                           DAZPhysicsCharacter
DAZPhysicsMesh                    DAZPhysicsMeshCapsuleCollider     DAZPhysicsMeshColliderGroup       DAZPhysicsMeshEarlyUpdate
DAZPhysicsMeshesEnable            DAZPhysicsMeshesEnableUI          DAZPhysicsMeshHardVerticesGroup   DAZPhysicsMeshSelection
DAZPhysicsMeshSoftVerticesGroup   DAZPhysicsMeshSoftVerticesSet     DAZPhysicsMeshUI                  DAZSkin
DAZSkinControl                    DAZSkinMeshSelection              DAZSkinTaskInfo                   DAZSkinTaskType
DAZSkinV2                         DAZSkinV2BulgeFactors             DAZSkinV2GeneralVertexWeights     DAZSkinV2MeshSelection
DAZSkinV2Node                     DAZSkinV2VertexWeights            DAZSkinWrap                       DAZSkinWrapControl
DAZSkinWrapControlUI              DAZSkinWrapMaterialOptions        DAZSkinWrapMaterialOptionsUI      DAZSkinWrapStore
DAZSkinWrapSwitcher               DAZSkinWrapSwitcherUI             DAZTongueControl                  DAZTongueControlUI
DAZUVMap                          DAZUVPatch                        DAZVertexMap                      DebugHUD
DebugJoint                        DebugJoints                       DebugJointsControl                DebugJointsControlUI
DebugMatrix4x4                    DebugQuaternion2Angles            DebugSMR                          DebugWorldPosition
DelayedMeshRenderer               DelayedSkinMeshRenderer           DelayFrameForTesting              DelayTest
DemoBehaviour                     DemoControl                       DemoGUIWater                      DepthFix
DictionaryExt                     directory_example                 DynamicGeometry                   DynamicGeometryUI
DynamicLoadedUI                   EmbeddedAudioClipManager          EmitterChannel                    EnableLink
EnableSwitch                      ExampleWheelController            ExtendedCapsuleCollider           EyeCenter
EyesControl                       EyesControlUI                     Fade                              FastList`1
FCPositionHandle                  FCRotationHandle                  fire_wiggle                       FirstPersonLauncher
flickering                        FlickeringLight                   Floor_PlanarReflection            Floor_ReflectionScriptCamera
FollowPhysicallySingle            FollowRotationSelective           ForceProducerV2                   ForceProducerV2UI
ForceProducerV2UIConnector        ForceReceiver                     FPS                               Frame
FreeCamera                        FreeControllerV3                  FreeControllerV3GUI               FreeControllerV3Link
FreeControllerV3UI                FreeControllerV3UIConnector       FreqHint                          GenerateDAZCharacterSelectorUI
GenerateDAZClothingSelectorUI     GenerateDAZDynamicSelectorUI      GenerateDAZHairSelectorUI         GenerateDAZMorphsControlJSONStorable
GenerateDAZMorphsControlUI        GenerateEmbeddedSceneSelectorUI   GenerateTabbedUI                  generic_example
GlobalStopwatch                   GPUCollidersConsumer              GPUCollidersManager               GPUCollidersManagerInit
GpuGrabSphere                     GPUToolsClothColliderReceiver     GPUToolsColliderProvider          GPUToolsColliderReceiver
GPUToolsHairColliderReceiver      GrabPoint                         GrabPointUI                       GrapplingHook
GravityControl                    GridControl                       GridControlUI                     gui
GUIDrawing                        GUIState                          HairEditingCamera                 HairGlobalSettings
HairSimControl                    HairSimControlTools               HairSimControlUI                  HairSimScalpMaskToolControl
HairSimScalpMaskToolControlUI     HairSimStyleControl               HairSimStyleControlUI             HairSimStyleToolControl
HairSimStyleToolControlUI         HairStripV2                       HandAnimatorManager               HandAnimatorManagerVR
HandControl                       HandControlLink                   HandControlUI                     HierarchicalAssignUIMaterial
HSVChangedEvent                   HSVColor                          HSVColorPicker                    HUDAnchor
HUDText                           IconSpin                          IEnumerableExtension              IgnoreCanvas
IgnoreChildColliders              IgnoreRaycast                     IgnoreSelect                      ImageControl
ImageControlUI                    ImageControlUIConnector           ImageLoaderThreaded               InputFieldAction
InputFieldAutoSizing              InspectorButtonAttribute          InspectorCommentAttribute         InspectorNoteAttribute
InteractionControl                InterpolateRigidbodies            JointPositionHardLimit            JoystickControl
JSONEmbed                         JSONSceneLoader                   JSONStorable                      JSONStorableAction
JSONStorableActionAudioClip       JSONStorableActionPresetFilePath  JSONStorableActionSceneFilePath   JSONStorableActionStringChooser
JSONStorableAngle                 JSONStorableBool                  JSONStorableColor                 JSONStorableDynamic
JSONStorableFloat                 JSONStorableParam                 JSONStorableString                JSONStorableStringChooser
JSONStorableTriggerHandler        JSONStorableUrl                   JSONStorableVector3               KeyEventHandler
Launcher                          LayoutMaxSize                     LeapMotionGrabber                 LeapMotionProviderSwitcher
LeverControll                     LightMapData                      LightMapDataContainerObject       LightmappingManager
LightScript                       LineDrawer                        LinkedPoint                       LipSyncDemo_SetCurrentTarget
Localizatron                      LocalRiggedFinger                 LomontFFT                         lookAt
LookAtCamera                      LookAtTarget                      LookAtTrigger                     LookAtTriggerUI
LookAtWithLimits                  LookAtWithLimitsControl           LookAtWithLimitsControlUI         LookInputModule
MapVerticesGPU                    MaterialOptions                   MaterialOptionsIgnore             MaterialOptionsUI
MaterialOptionsUIConnector        MaterialOptionTextureGroup        MaterialOptionTextureSet          MaterialOptionType
MemoryOptimizer                   MeshGPU                           MeshPoly                          MeshRendererExample
MeshSimplify                      MeshSmooth                        MeshSmoothGPU                     MessengerHelper
MinMaxAttribute                   MirrorCopyPhysicsComponents       MirrorReflection                  MirrorReflectionUI
MirrorReflectionUIConnector       MixerSnapshot                     MKGlowCamera                      MKGlowObject
MoonManager                       MopedDemo                         MotionAnimationClip               MotionAnimationClipBank
MotionAnimationControl            MotionAnimationControlUI          MotionAnimationMaster             MotionAnimationMasterUI
MotionAnimationPattern            MotionAnimationStep               MotionAnimator                    MouseLook
MoveAndRotateAs                   MoveAndRotateAsBatch              MoveAndRotateAsHUDAnchor          MoveAndRotateAsJSONStorable
MoveAndRotateAsJSONStorableUI     MoveAsOtherAndLookAt              MoveCameraToCharacter             MoveProducer
MoveProducerUI                    MoveProducerUIConnector           MoveReceiver                      MoveWaterToCamera
Mp3Importer                       MPGImport                         MultiButtonPanel                  MVRDownloadManager
MVRPlugin                         MVRPluginManager                  MVRPluginManagerUI                MVRPluginUI
MVRScript                         MVRScriptAbstract                 MVRScriptController               MVRScriptControllerUI
MVRScriptUI                       MyDebug                           NamedAudioClip                    NAudioPlayer
NGSS_ContactShadows               NGSS_Directional                  NonConvexMeshCollider             ObiActorTeleport
ObjectChoice                      ObjectChooser                     ObjectChooserUI                   ObjectDragger
ObjectLimit                       OBJExporter                       OBJLoader                         OculusSpatializerUnity
Oni                               ONSPAmbisonicsNative              ONSPAudioSource                   ONSPProfiler
ONSPReflectionZone                ONSPVersion                       OrientAxis                        Oscillate
OSPProps                          OvrAvatar                         OvrAvatarAsset                    OvrAvatarAssetMaterial
OvrAvatarAssetMesh                OvrAvatarAssetTexture             ovrAvatarAssetType                OvrAvatarBase
ovrAvatarBaseComponent            OvrAvatarBody                     ovrAvatarBodyComponent            ovrAvatarBodyPartType
ovrAvatarButton                   ovrAvatarCapabilities             ovrAvatarComponent                OvrAvatarComponent
ovrAvatarControllerComponent      ovrAvatarControllerType           OvrAvatarDriver                   OvrAvatarHand
ovrAvatarHandComponent            ovrAvatarHandGesture              ovrAvatarHandInputState           OvrAvatarLocalDriver
ovrAvatarMaterialLayerBlendMode   ovrAvatarMaterialLayerSampleMode  ovrAvatarMaterialLayerState       ovrAvatarMaterialMaskType
ovrAvatarMaterialState            ovrAvatarMeshAssetData            OvrAvatarMeshInstance             ovrAvatarMeshVertex
ovrAvatarMessage_AssetLoaded      ovrAvatarMessage_AvatarSpecificationovrAvatarMessageType              OvrAvatarPacket
ovrAvatarPBSMaterialState         OvrAvatarProjectorRenderComponent OvrAvatarRemoteDriver             OvrAvatarRenderComponent
ovrAvatarRenderPart_ProjectorRenderovrAvatarRenderPart_SkinnedMeshRenderovrAvatarRenderPart_SkinnedMeshRenderPBSovrAvatarRenderPart_SkinnedMeshRenderPBS_V2
ovrAvatarRenderPartType           OvrAvatarSDKManager               OvrAvatarSettings                 OvrAvatarSkinnedMeshPBSV2RenderComponent
ovrAvatarSkinnedMeshPose          OvrAvatarSkinnedMeshRenderComponentOvrAvatarSkinnedMeshRenderPBSComponentovrAvatarTextureAssetData
ovrAvatarTextureFormat            ovrAvatarTouch                    OvrAvatarTouchController          ovrAvatarTransform
ovrAvatarVisibilityFlags          OVRBoundary                       OVRCallback                       OVRCallback`1
OVRCallback`2                     OVRCallback`3                     OVRCameraComposition              OVRCameraRig
OVRChromaticAberration            OVRComposition                    OVRCubemapCapture                 OVRDebugConsole
OVRDebugHeadController            OVRDebugInfo                      OVRDirectComposition              OVRDisplay
OVRExtensions                     OVRExternalComposition            OVRGazePointer                    OVRGearVrControllerTest
OVRGrabbable                      OVRGrabber                        OVRGridCube                       OVRHaptics
OVRHapticsClip                    OVRInput                          OVRLayerAttribute                 OVRLipSync
OVRLipSyncContext                 OVRLipSyncContextBase             OVRLipSyncContextCanned           OVRLipSyncContextMorphTarget
OVRLipSyncContextTextureFlip      OVRLipSyncDebugConsole            OVRLipSyncMicInput                OVRLipSyncSequence
OVRLipSyncTestAudio               OVRManager                        OVRMicInput                       OVRMixedRealityCaptureSettings
OVRModeParms                      OVRMonoscopic                     OVRNativeBuffer                   OVROverlay
OVRPlatformMenu                   OVRPlayerController               OVRPose                           OVRProfile
OVRProgressIndicator              OVRRaycaster                      OVRResetOrientation               OVRSandwichComposition
OVRSceneSampleController          OVRScreenFade                     OVRTouchpadHelper                 OVRTrackedRemote
OVRTracker                        OVRVoiceMod                       OVRVoiceModContext                OVRWaitCursor
P2PManager                        PacketRecordSettings              ParticleSystemControl             ParticleSystemControlUI
PhysicalRiggedFinger              PhysicsMaterialControl            PhysicsMaterialControlUI          PhysicsResetter
PhysicsSimulator                  PhysicsSimulatorJSONStorable      PixelationControl                 PixelationControlUI
PlatformManager                   PlayerController                  PlayerNavCollider                 PlayerTransform
PlayModeUI                        PointerEnterExitAction            PoseEditHelper                    PossessControl
Possessee                         Possessor                         PreloadSounds                     ProbeDemo
ProcessControl                    ProgressBar                       ProjectorMatrix                   QualityControl
Quaternion2Angles                 RandomLook                        Range                             Raycast
RebindSMR                         RecalculateNormals                RecalculateNormalsGPU             RecalculateTangents
RecalculateTangentsGPU            RectTransformInfo                 ReflectionCamera                  ReflectionSnapshot
RemoteLoopbackManager             RemotePlayer                      RenderQueueControl                RenderQueueControlUI
RenderSuspend                     ReplacePrefab                     ReportCenterOfMass                RhythmAudioSourceControl
RhythmController                  RhythmForceProducerV2             RhythmForceProducerV2UI           RhythmForceProducerV2UIConnector
RhythmTool                        RigidbodyAttributes               RippleCreator                     RobotArmController
RoomManager                       Rotate                            RotateHologram                    RuntimeMeshSimplifier
ScaleChangeReceiver               ScaleChangeReceiverJoint          ScaleChangeReceiverJSONStorable   ScaleChangeReceiverMeshCollider
SceneLoader                       ScrollRectContentManager          SelectableSelect                  SelectableUnselect
SelectionHUD                      SelectTarget                      SetAnchorFromVertex               SetCenterOfGravity
SetDAZMorph                       SetDAZMorphControl                SetDAZMorphControlUI              SetDAZMorphFromAverageBoneAngle
SetDAZMorphFromBoneAngle          SetDAZMorphFromBoneAngleToggler   SetDAZMorphFromDistance           SetDAZMorphFromLocalDistance
SetDAZMorphFromUISlider           SetDAZMorphRandom                 SetMorphFromAngle                 SetMorphFromDistance
SetMorphFromHighestAngle          SetMorphFromHighestDistance       SetTextFromFloat                  SetTransformScale
SetTransformScaleIndependent      SetTransformScaleIndependentUI    SetTransformScaleUI               SetTransformScaleUIConnector
SimpleMoveProducer                SimplifyMeshPreview               SimulateMovement                  Singleton`1
SkyshopLightController            SkyshopLightControllerUI          SkySwab                           SlaveBlendShapes
SlaveConfigurableJointDrive       SlaveTransformHier                SliderControl                     SliderTrack
SlowmoToggler                     SmoothSkinMeshRenderer            SoundEmitter                      SoundFX
SoundFXNext                       SoundFXRef                        SoundGroup                        SoundPriority
specificationCallback             SpeechBlend                       SpeechBlendControl                SpeechBlendControlUI
SpeechBlendMicrophoneInput        SpeechBlendVisemeUI               SpeechBubbleControl               SpeechBubbleControlUI
SphereUtility                     SpinPlanet                        StarPoint                         StateModel
SteamVRFrustumAdjust              SubAtom                           SubScene                          SubSceneUI
SuperController                   SyncForceProducerV2               SyncForceProducerV2UI             SyncForceProducerV2UIConnector
SyncMaterialParameters            TabbedUIBuilder                   TestCompression                   TestScript
TextStorable                      TextStorableUI                    TextureLoader                     TextureScale
ThreadControl                     TimeControl                       ToggleGroupValue                  Torchelight
TorchLightController              TorchLightControllerUI            TorchLightControllerUIConnector   TorchSlider
Torchslider2                      TransformControl                  TransformControlUI                TransformPresets
TransformResetter                 Trigger                           TriggerAction                     TriggerActionDiscrete
TriggerActionDiscreteUI           TriggerActionHandler              TriggerActionMiniUI               TriggerActionsPanelUI
TriggerActionTransition           TriggerActionTransitionUI         TriggerActionUI                   TriggerHandler
TriggerUI                         TwoDPicker                        UIButtonTrigger                   UIButtonTriggerUI
UIColorScheme                     UIConnector                       UIConnectorMaster                 UIDynamic
UIDynamicButton                   UIDynamicColorPicker              UIDynamicPopup                    UIDynamicSlider
UIDynamicTextField                UIDynamicToggle                   UIHoverPopup                      UIHoverTextNotifier
UIImageControl                    UIImageControlUI                  UIMultiConnector                  UIPopup
UIPopupButton                     UIPopupPanel                      UIPopupTrack                      UIProvider
UIRectTransformControl            UIRectTransformControlUI          UIRenderQueueControl              UIRenderQueueControlIgnore
UIRenderQueueControlUI            UISideAlign                       UISliderTrigger                   UISliderTriggerUI
UIStyle                           UIStyleButton                     UIStyleButtonMaster               UIStyleImage
UIStyleImageMaster                UIStyleSlider                     UIStyleSliderMaster               UIStyleText
UIStyleTextMaster                 UIStyleToggle                     UIStyleToggleMaster               UITab
UITabSelector                     UITextControl                     UITextControlUI                   UIToggleTrigger
UIToggleTriggerUI                 UIVisibility                      UIVisibilityControl               UIVisibilityControlUI
Underwater                        UnderwaterPostEffects             UnityAnimatorControl              UnityAnimatorControlUI
UnityThreadHelper                 UpdateTime                        URLAudioClip                      URLAudioClipManager
URLAudioClipUI                    UserPreferences                   Util                              VariableTrigger
VariableTriggerUI                 VertexMap                         VideoReadyTriggerUI               VideoStartedTriggerUI
VideoStoppedTriggerUI             ViveTrackedControllers            VoiceModDemo_Logic                VoipManager
VRKeyboardLink                    VRWebBrowser                      VRWebBrowserUI                    VRWebBrowserUIConnector
Water_DistortionAndBloom          WaterRipples                      WAV                               WebClientSecure
WebClientWithTimeout              WFX_SunShafts                     wind_rot                          ZeroMorphs
```

**AQUAS** (2)

```
AQUAS_Look                        AQUAS_Walk
```

**AssetBundles** (9)

```
AssetBundleLoadAssetOperation     AssetBundleLoadAssetOperationFull AssetBundleLoadAssetOperationSimulationAssetBundleLoadLevelOperation
AssetBundleLoadManifestOperation  AssetBundleLoadOperation          AssetBundleManager                LoadedAssetBundle
Utility
```

**Assets.OVR.Scripts** (4)

```
FixMethodDelegate                 FixRecord                         RangedRecord                      Record
```

**Battlehub** (3)

```
BHPath                            Reflection                        SerializeIgnore
```

**Battlehub.Cubeman** (2)

```
CubemanCharacter                  CubemanUserControl
```

**Battlehub.RTCommon** (46)

```
ApplyCallback                     BeginDragEventHandler             BoundsType                        DragDrop
DropEventHandler                  ExposeToEditor                    ExposeToEditorChangeEvent`1       ExposeToEditorEvent
ExposeToEditorObjectType          ExposeToEditorUnityEvent          Game                              GameCamera
GameCameraEvent                   GLCamera                          GLRenderer                        HierarchyItem
IGL                               InputController                   LockAxes                          LockObject
MouseOrbit                        PurgeCallback                     Record                            RTLayer
RuntimeEditorApplication          RuntimeEditorEvent                RuntimeEditorWindow               RuntimeGraphics
RuntimeGraphicsLayer              RuntimePivotMode                  RuntimePivotRotation              RuntimeSelection
RuntimeSelectionChanged           RuntimeTool                       RuntimeTools                      RuntimeToolsEvent
RuntimeUndo                       RuntimeUndoComponent              RuntimeUndoEventHandler           RuntimeWindowType
SnappingMode                      SpawnPrefabChanged                SpriteGizmo                       SpriteGizmoManager
Test                              UndoStack`1
```

**Battlehub.RTGizmos** (16)

```
AudioReverbZoneGizmo              AudioSourceGizmo                  BaseGizmo                         BoxColliderGizmo
BoxGizmo                          CapsuleColliderGizmo              CapsuleGizmo                      ConeGizmo
DirectionalLightGizmo             LightGizmo                        PointLightGizmo                   RuntimeGizmos
SkinnedMeshRendererGizmo          SphereColliderGizmo               SphereGizmo                       SpotlightGizmo
```

**Battlehub.RTHandles** (33)

```
BaseHandle                        BaseHandleModel                   BoxSelection                      BoxSelectionMethod
CubemenGame                       EditorCharacter                   EditorDemo                        EditorHint
FilteringArgs                     GameCharacter                     GameFerry                         GameFerryBehavior
GameFerryButton                   GameGoal                          Grid                              InstantiatePrefab
PositionHandle                    PositionHandleModel               RotationHandle                    RotationHandleModel
RTHandlesDemoSmoothFollow         RTHColors                         RuntimeHandleAxis                 RuntimeHandles
RuntimeSceneView                  RuntimeSelectionComponent         RuntimeToolsComponent             ScaleHandle
ScaleHandleModel                  SceneGizmo                        SelectionGizmo                    UnityEditorToolChanged
UnityEditorToolsListener
```

**Battlehub.RTSaveLoad** (76)

```
AddResourcesTest                  AssetBundleDescriptor             AssetBundleEventHandler           AssetBundleLoader
AssetTypeHint                     BinarySerializer                  Binder                            BundleResourceMap
CreateAssetBundles                DataContract                      Dependencies                      ExtraSceneData
FileSystemStorage                 FolderTemplate                    GameObjectExtension               IAssetBundleLoader
ID                                IdentifiersMap                    IJob                              IntArray
IProject                          IProjectManager                   IRTSerializable                   IRuntimeShaderUtil
ISceneManager                     ISerializer                       IStorage                          Job
NilContainer                      NoneItem                          ObjectExt                         ObjectToID
PathHelper                        PersistentArgumentCache           PersistentData                    PersistentDescriptor
PersistentIgnore                  PersistentPersistentCall          PersistentScene                   PersistentScript
PersistentUnityEventBase          PrimitiveContract                 PrimitiveContract`1               Project
ProjectData                       ProjectEventHandler               ProjectEventHandler`1             ProjectItem
ProjectItemData                   ProjectItemMeta                   ProjectItemObjectPair             ProjectItemTypes
ProjectItemWrapper                ProjectManager                    ProjectManagerCallback            ProjectManagerCallback`1
ProjectManagerEventArgs           ProjectMeta                       ProjectPayload                    ProjectPayload`1
ProjectRoot                       ProtobufSerializer                ResourceGroup                     ResourceMap
RTShaderPropertyType              RuntimeSceneManager               RuntimeShaderInfo                 RuntimeShaderUtil
SaveLoadConfig                    SerializationSurrogates           Serializer                        StorageEventHandler`1
StorageEventHandler`2             StoragePayload`1                  StoragePayload`2                  TypeModelCreator
```

**Battlehub.RTSaveLoad.PersistentObjects** (170)

```
PersistentAnchoredJoint2D         PersistentAnimation               PersistentAnimationClip           PersistentAnimationCurve
PersistentAnimator                PersistentAnimatorOverrideControllerPersistentAreaEffector2D          PersistentAssetBundle
PersistentAssetBundleManifest     PersistentAudioBehaviour          PersistentAudioChorusFilter       PersistentAudioClip
PersistentAudioDistortionFilter   PersistentAudioEchoFilter         PersistentAudioHighPassFilter     PersistentAudioListener
PersistentAudioLowPassFilter      PersistentAudioReverbFilter       PersistentAudioReverbZone         PersistentAudioSource
PersistentAvatar                  PersistentAvatarMask              PersistentBehaviour               PersistentBillboardAsset
PersistentBillboardRenderer       PersistentBoxCollider             PersistentBoxCollider2D           PersistentBuoyancyEffector2D
PersistentBurst                   PersistentCamera                  PersistentCanvas                  PersistentCanvasGroup
PersistentCanvasRenderer          PersistentCapsuleCollider         PersistentCapsuleCollider2D       PersistentCharacterController
PersistentCharacterJoint          PersistentCircleCollider2D        PersistentCloth                   PersistentCollider
PersistentCollider2D              PersistentCollisionModule         PersistentColorBySpeedModule      PersistentColorOverLifetimeModule
PersistentComponent               PersistentCompositeCollider2D     PersistentComputeShader           PersistentConfigurableJoint
PersistentConstantForce           PersistentConstantForce2D         PersistentCubemap                 PersistentCubemapArray
PersistentCustomDataModule        PersistentDetailPrototype         PersistentDistanceJoint2D         PersistentEdgeCollider2D
PersistentEffector2D              PersistentEmissionModule          PersistentEmitParams              PersistentExternalForcesModule
PersistentFixedJoint              PersistentFixedJoint2D            PersistentFlare                   PersistentFlareLayer
PersistentFont                    PersistentForceOverLifetimeModule PersistentFrictionJoint2D         PersistentGameObject
PersistentGradient                PersistentGUIElement              PersistentGUILayer                PersistentGUISkin
PersistentGUIStyle                PersistentGUIStyleState           PersistentHingeJoint              PersistentHingeJoint2D
PersistentInheritVelocityModule   PersistentJoint                   PersistentJoint2D                 PersistentKeyframe
PersistentLensFlare               PersistentLight                   PersistentLightmapSettings        PersistentLightProbeGroup
PersistentLightProbeProxyVolume   PersistentLightProbes             PersistentLightsModule            PersistentLimitVelocityOverLifetimeModule
PersistentLineRenderer            PersistentLODGroup                PersistentMainModule              PersistentMaterial
PersistentMesh                    PersistentMeshCollider            PersistentMeshFilter              PersistentMeshRenderer
PersistentMinMaxCurve             PersistentMinMaxGradient          PersistentMonoBehaviour           PersistentMotion
PersistentMovieTexture            PersistentNetworkView             PersistentNoiseModule             PersistentObject
PersistentOcclusionArea           PersistentOcclusionPortal         PersistentParticle                PersistentParticleSystem
PersistentParticleSystemRenderer  PersistentPhysicMaterial          PersistentPhysicsMaterial2D       PersistentPhysicsUpdateBehaviour2D
PersistentPlatformEffector2D      PersistentPointEffector2D         PersistentPolygonCollider2D       PersistentProceduralTexture
PersistentProjector               PersistentQualitySettings         PersistentRectTransform           PersistentReflectionProbe
PersistentRelativeJoint2D         PersistentRenderer                PersistentRenderSettings          PersistentRenderTexture
PersistentRigidbody               PersistentRigidbody2D             PersistentRotationBySpeedModule   PersistentRotationOverLifetimeModule
PersistentRuntimeAnimatorControllerPersistentScriptableObject        PersistentShader                  PersistentShaderVariantCollection
PersistentShapeModule             PersistentSizeBySpeedModule       PersistentSizeOverLifetimeModule  PersistentSkinnedMeshRenderer
PersistentSkybox                  PersistentSliderJoint2D           PersistentSparseTexture           PersistentSphereCollider
PersistentSplatPrototype          PersistentSpringJoint             PersistentSpringJoint2D           PersistentSprite
PersistentSpriteRenderer          PersistentStateMachineBehaviour   PersistentSubEmittersModule       PersistentSurfaceEffector2D
PersistentTargetJoint2D           PersistentTerrain                 PersistentTerrainCollider         PersistentTerrainData
PersistentTextAsset               PersistentTextMesh                PersistentTexture                 PersistentTexture2D
PersistentTexture2DArray          PersistentTexture3D               PersistentTextureSheetAnimationModulePersistentTrailModule
PersistentTrailRenderer           PersistentTransform               PersistentTree                    PersistentTreePrototype
PersistentTriggerModule           PersistentVelocityOverLifetimeModulePersistentWebCamTexture           PersistentWheelCollider
PersistentWheelJoint2D            PersistentWindZone
```

**Battlehub.RTSaveLoad.PersistentObjects.AI** (4)

```
PersistentNavMeshAgent            PersistentNavMeshData             PersistentNavMeshObstacle         PersistentOffMeshLink
```

**Battlehub.RTSaveLoad.PersistentObjects.Audio** (3)

```
PersistentAudioMixer              PersistentAudioMixerGroup         PersistentAudioMixerSnapshot
```

**Battlehub.RTSaveLoad.PersistentObjects.EventSystems** (10)

```
PersistentBaseInput               PersistentBaseInputModule         PersistentBaseRaycaster           PersistentEventSystem
PersistentEventTrigger            PersistentPhysics2DRaycaster      PersistentPhysicsRaycaster        PersistentPointerInputModule
PersistentStandaloneInputModule   PersistentUIBehaviour
```

**Battlehub.RTSaveLoad.PersistentObjects.Experimental.Rendering** (1)

```
PersistentRenderPipelineAsset
```

**Battlehub.RTSaveLoad.PersistentObjects.Networking.Match** (1)

```
PersistentNetworkMatch
```

**Battlehub.RTSaveLoad.PersistentObjects.Networking.PlayerConnection** (1)

```
PersistentPlayerConnection
```

**Battlehub.RTSaveLoad.PersistentObjects.Rendering** (2)

```
PersistentGraphicsSettings        PersistentSortingGroup
```

**Battlehub.RTSaveLoad.PersistentObjects.UI** (33)

```
PersistentAspectRatioFitter       PersistentBaseMeshEffect          PersistentButton                  PersistentCanvasScaler
PersistentContentSizeFitter       PersistentDropdown                PersistentGraphic                 PersistentGraphicRaycaster
PersistentGridLayoutGroup         PersistentHorizontalLayoutGroup   PersistentHorizontalOrVerticalLayoutGroupPersistentImage
PersistentInputField              PersistentLayoutElement           PersistentLayoutGroup             PersistentMask
PersistentMaskableGraphic         PersistentNavigation              PersistentOptionData              PersistentOutline
PersistentPositionAsUV1           PersistentRawImage                PersistentRectMask2D              PersistentScrollbar
PersistentScrollRect              PersistentSelectable              PersistentShadow                  PersistentSlider
PersistentSpriteState             PersistentText                    PersistentToggle                  PersistentToggleGroup
PersistentVerticalLayoutGroup
```

**Battlehub.RTSaveLoad.PersistentObjects.Video** (2)

```
PersistentVideoClip               PersistentVideoPlayer
```

**Battlehub.RTSaveLoad.PersistentObjects.VR.WSA** (1)

```
PersistentWorldAnchor
```

**Battlehub.RTSaveLoad.UnityEngineNS** (31)

```
BoneWeightSurrogate               BoundsSurrogate                   CharacterInfoSurrogate            ClothSkinningCoefficientSurrogate
Color32Surrogate                  ColorSurrogate                    GradientAlphaKeySurrogate         GradientColorKeySurrogate
JointAngleLimits2DSurrogate       JointDriveSurrogate               JointLimitsSurrogate              JointMotor2DSurrogate
JointMotorSurrogate               JointSpringSurrogate              JointSuspension2DSurrogate        JointTranslationLimits2DSurrogate
LayerMaskSurrogate                Matrix4x4Surrogate                NetworkPlayerSurrogate            NetworkViewIDSurrogate
QuaternionSurrogate               RectOffsetSurrogate               RectSurrogate                     RenderBufferSurrogate
SoftJointLimitSpringSurrogate     SoftJointLimitSurrogate           TreeInstanceSurrogate             Vector2Surrogate
Vector3Surrogate                  Vector4Surrogate                  WheelFrictionCurveSurrogate
```

**Battlehub.RTSaveLoad.UnityEngineNS.AINS** (2)

```
NavMeshPathSurrogate              OffMeshLinkDataSurrogate
```

**Battlehub.RTSaveLoad.UnityEngineNS.ExperimentalNS.DirectorNS** (1)

```
PlayableGraphSurrogate
```

**Battlehub.RTSaveLoad.UnityEngineNS.SceneManagementNS** (1)

```
SceneSurrogate
```

**Battlehub.RTSaveLoad.UnityEngineNS.UINS** (2)

```
AnimationTriggersSurrogate        ColorBlockSurrogate
```

**Battlehub.UIControls** (53)

```
Breadcrumb                        DataBindAction                    ExternalDragItem                  ItemArgs
ItemContainer                     ItemContainerData                 ItemDataBindingArgs               ItemDropAction
ItemDropArgs                      ItemDropCancelArgs                ItemDropMarker                    ItemEventHandler
ItemExpandingArgs                 ItemsCancelArgs                   ItemsControl                      ItemsControl`1
ItemsRemovedArgs                  LayoutElementResizer              ListBox                           ListBoxDemo
ListBoxItem                       ParentChangedEventArgs            PopupWindow                       PopupWindowAction
PopupWindowArgs                   PopupWindowEvent                  ProgressBar                       RectTransformChanged
RectTransformChangeListener       Rotation                          SelectionChangedArgs              TreeView
TreeViewDemo                      TreeViewDropMarker                TreeViewExpander                  TreeViewItem
TreeViewItemContainerData         TreeViewItemDataBindingArgs       VirtualizingExternalDragItem      VirtualizingItemContainer
VirtualizingItemDropMarker        VirtualizingItemEventHandler      VirtualizingItemsControl          VirtualizingItemsControl`1
VirtualizingListBox               VirtualizingListBoxItem           VirtualizingMode                  VirtualizingParentChangedEventArgs
VirtualizingScrollRect            VirtualizingTreeView              VirtualizingTreeViewDemo          VirtualizingTreeViewDropMarker
VirtualizingTreeViewItem
```

**Battlehub.Utils** (20)

```
AnimationCallback`2               AnimationInfo`2                   BtnRepeat                         CursorHelper
EditorStopwatch                   EnumFlagsAttribute                ExposePropertyAttribute           FloatAnimationInfo
GameObjectExtensions              IAnimationInfo                    ObjectToTexture                   PsAutoDestroy
QuaternionAnimationInfo           ReadOnlyAttribute                 Run                               Strong
TakeSnapshot                      TextureExtensions                 Vector3AnimationInfo              Vector3SlerpAnimationInfo
```

**DynamicCSharp** (14)

```
AsyncCompileLoadOperation         DynamicCSharp                     FieldProxy                        IMemberProxy
PropertyProxy                     RestrictionMode                   RuntimeRestrictions               ScriptAssembly
ScriptDomain                      ScriptEvaluator                   ScriptProxy                       ScriptType
Variable                          Variable`1
```

**DynamicCSharp.Compiler** (2)

```
AsyncCompileOperation             ScriptCompiler
```

**DynamicCSharp.Demo** (14)

```
CodeUI                            CreateDomainExample               IExampleInterface                 LoadAssemblyExample
LoadScriptExample                 ScriptCoroutineExample            ScriptEvaluatorExample            ScriptInterfaceExample
ScriptProxyBehaviourExample       ScriptProxyExample                TankController                    TankManager
TankManagerAsync                  TankShell
```

**DynamicCSharp.Security** (7)

```
AssemblySecurityError             MemberRestriction                 NamespaceRestriction              ReferenceRestriction
Restriction                       RuntimeNamespaceRestriction       TypeRestriction
```

**Example** (4)

```
AnotherGreetingLogger             DefaultGreetingLogger             ExampleBehaviour                  IGreetingLogger
```

**GPUTools.Cloth.Scripts** (2)

```
ClothEditorType                   ClothSettings
```

**GPUTools.Cloth.Scripts.Geometry** (1)

```
ClothGeometryImporter
```

**GPUTools.Cloth.Scripts.Geometry.Data** (1)

```
ClothGeometryData
```

**GPUTools.Cloth.Scripts.Geometry.DebugDraw** (1)

```
ClothDebugDraw
```

**GPUTools.Cloth.Scripts.Geometry.Passes** (9)

```
BuildPhysicsBlend                 BuildPhysicsStrength              CommonJobsPass                    JointsPass
NearbyJointsPass                  NeighborsPass                     NeighborsPass2                    PhysicsVerticesPass
StiffnessJointsPass
```

**GPUTools.Cloth.Scripts.Geometry.Tools** (1)

```
ClothSphereBrash
```

**GPUTools.Cloth.Scripts.Runtime** (1)

```
BuildRuntimeCloth
```

**GPUTools.Cloth.Scripts.Runtime.Commands** (12)

```
BuildClothAccessories             BuildDistanceJoints               BuildGrabSpheres                  BuildLineSpheres
BuildNearbyJoints                 BuildParticles                    BuildPlanes                       BuildPointJoints
BuildSpheres                      BuildStiffnessJoints              BuildVertexData                   BuildWind
```

**GPUTools.Cloth.Scripts.Runtime.Data** (2)

```
ClothDataFacade                   RuntimeData
```

**GPUTools.Cloth.Scripts.Runtime.Kernels** (3)

```
CreateVertexDataKernel            CreateVertexOnlyDataKernel        ParticleNeiborsCollisionKernel
```

**GPUTools.Cloth.Scripts.Runtime.Physics** (2)

```
ClothPhysics                      ClothPhysicsWorld
```

**GPUTools.Cloth.Scripts.Runtime.Render** (1)

```
ClothRender
```

**GPUTools.Cloth.Scripts.Types** (3)

```
ClothVertex                       Int2                              Int2ListContainer
```

**GPUTools.ClothDemo.Scripts** (3)

```
OpenSceneButton                   TestKernels                       TestPrimitive
```

**GPUTools.Common.Scripts.PL.Abstract** (4)

```
IBufferWrapper                    IPass                             KernelBase                        PrimitiveBase
```

**GPUTools.Common.Scripts.PL.Attributes** (1)

```
GpuData
```

**GPUTools.Common.Scripts.PL.Config** (1)

```
GpuConfig
```

**GPUTools.Common.Scripts.PL.Tools** (8)

```
ComputeUtils                      ExecuteAsPass                     GpuBuffer`1                       GpuMatrix4x4
GpuValue`1                        GroupData                         GroupedData`1                     IGroupItem
```

**GPUTools.Common.Scripts.Tools** (7)

```
CacheProvider`1                   FixedList`1                       FloatSmoother                     FpsCounter
RenderSetup                       SerializableDictionary`2          TextureSize
```

**GPUTools.Common.Scripts.Tools.Commands** (4)

```
BuildChainCommand                 CacheChainCommand                 IBuildCommand                     ICacheCommand
```

**GPUTools.Common.Scripts.Tools.Debug** (7)

```
DebugTexture                      DebugVertices                     ExecuteTimer                      LoggerUtil
MeshDebuger                       TargetFramerate                   Validator
```

**GPUTools.Common.Scripts.Tools.Demo** (1)

```
DemoCamera
```

**GPUTools.Common.Scripts.Tools.Kernels** (6)

```
GPUMatrixCopyPaster               GPUMatrixMultiplier               GPUMatrixPointMultiplier          GPUMatrixSelector
GPUPointsSelector                 GPUVector3CopyPaster
```

**GPUTools.Common.Scripts.Tools.Ranges** (1)

```
FloatRange
```

**GPUTools.Common.Scripts.Tools.SpatialSearch** (2)

```
SearchVoxel                       SpatialSearcher
```

**GPUTools.Common.Scripts.Utils** (6)

```
CollectionUtils                   ConvertUtils                      CurveUtils                        MathSearchUtils
TransformUtils                    TriangleUtils
```

**GPUTools.Demo.Common.Scripts** (1)

```
PlayImageEffects
```

**GPUTools.Hair.Scripts** (1)

```
HairSettings
```

**GPUTools.Hair.Scripts.Geometry.Abstract** (1)

```
GeometryProviderBase
```

**GPUTools.Hair.Scripts.Geometry.Constrains** (1)

```
HairJointArea
```

**GPUTools.Hair.Scripts.Geometry.Create** (8)

```
CreatorGeometry                   GeometryBrush                     GeometryBrushBehaviour            GeometryGroupData
GeometryGroupHistory              HairGeometryCreator               RuntimeHairGeometryCreator        ScalpRendererType
```

**GPUTools.Hair.Scripts.Geometry.Create.Kernels** (1)

```
HairEditorKernel
```

**GPUTools.Hair.Scripts.Geometry.Import** (2)

```
HairGeometryImporter              HairGroupsProvider
```

**GPUTools.Hair.Scripts.Geometry.MayaImport** (2)

```
CacheMayaHairData                 MayaHairGeometryImporter
```

**GPUTools.Hair.Scripts.Geometry.MayaImport.Commands** (4)

```
AssignHairLinesToTriangles        ComputeStandsToScalpVerticesMap   MoveHairLinesToScalpVertices      StripsToLinesMayaPass
```

**GPUTools.Hair.Scripts.Geometry.MayaImport.Data** (1)

```
MayaHairData
```

**GPUTools.Hair.Scripts.Geometry.MayaImport.Debug** (1)

```
MayaImporterDebugDraw
```

**GPUTools.Hair.Scripts.Geometry.Procedural** (2)

```
CurveGrid                         ProceduralScalp
```

**GPUTools.Hair.Scripts.Geometry.Tools** (2)

```
MeshUtils                         ScalpProcessingTools
```

**GPUTools.Hair.Scripts.Runtime.Commands** (2)

```
BuildPlanes                       BuildRuntimeHair
```

**GPUTools.Hair.Scripts.Runtime.Commands.Physics** (10)

```
BuildAccessories                  BuildCompressionJoints            BuildDistanceJoints               BuildEditLineSpheres
BuildLineSpheres                  BuildNearbyDistanceJoints         BuildParticles                    BuildPointJoints
BuildSpheres                      BuildWind
```

**GPUTools.Hair.Scripts.Runtime.Commands.Render** (3)

```
BuildBarycentrics                 BuildParticlesData                BuildTesselation
```

**GPUTools.Hair.Scripts.Runtime.Data** (2)

```
HairDataFacade                    RuntimeData
```

**GPUTools.Hair.Scripts.Runtime.Kernels** (3)

```
TesselateKernel                   TesselateWithNormalsKernel        TesselateWithNormalsRenderRigidityKernel
```

**GPUTools.Hair.Scripts.Runtime.Physics** (2)

```
GPHairPhysics                     HairPhysicsWorld
```

**GPUTools.Hair.Scripts.Runtime.Render** (3)

```
HairRender                        RenderParticle                    TessRenderParticle
```

**GPUTools.Hair.Scripts.Settings** (7)

```
ColorProviderType                 HairHeadCenterType                HairLODSettings                   HairPhysicsSettings
HairRenderSettings                HairShadowSettings                HairStandsSettings
```

**GPUTools.Hair.Scripts.Settings.Abstract** (1)

```
HairSettingsBase
```

**GPUTools.Hair.Scripts.Settings.Colors** (4)

```
GeometryColorProvider             IColorProvider                    ListColorProvider                 RootTipColorProvider
```

**GPUTools.Hair.Scripts.Types** (1)

```
Vector4ListContainer
```

**GPUTools.Hair.Scripts.Utils** (1)

```
GeometryUtils
```

**GPUTools.HairDemo.Scripts** (2)

```
ConstantRotation                  DemoView
```

**GPUTools.HairDemo.Scripts.BallsDemo** (1)

```
BallController
```

**GPUTools.HairDemo.Scripts.BlendShapesDemo** (1)

```
TestBlendShapes
```

**GPUTools.HairDemo.Scripts.Tess** (1)

```
TessDemo
```

**GPUTools.Painter.Scripts** (4)

```
ColorBrush                        ColorChannel                      GPUPaint                          PainterSettings
```

**GPUTools.Painter.Scripts.Kernels** (1)

```
PaintKernel
```

**GPUTools.Physics.Scripts.Behaviours** (16)

```
CapsuleLineSphereCollider         GpuBrushCapsule                   GpuCutCapsule                     GpuEditCapsule
GpuGrabCapsule                    GpuGrowCapsule                    GpuHoldCapsule                    GpuPullCapsule
GpuPushCapsule                    GpuRigidityDecreaseCapsule        GpuRigidityIncreaseCapsule        GpuRigiditySetCapsule
GpuSphereCollider                 LineSphereCollider                PlaneCollider                     SkinnedMeshCollider
```

**GPUTools.Physics.Scripts.DebugDraw** (1)

```
GPDebugDraw
```

**GPUTools.Physics.Scripts.Kernels** (49)

```
CompressionJointsKernel           CopySpecificParticlesKernel       DistanceJointsAdjustKernel        DistanceJointsKernel
DistanceJointsUniformRigidityKernelDistanceJointsWithParticleHoldKernelGrabJointsKernel                  IntegrateIterKernel
IntegrateIterWithParticleHoldKernelIntegrateKernel                   IntegrateVelocityInnerKernel      IntegrateVelocityKernel
MovePointJointsToParticlesKernel  NearbyDistanceJointsKernel        ParticleCollisionResetKernel      ParticleLineSphereBrushKernel
ParticleLineSphereCollisionHoldKernelParticleLineSphereCollisionKernel ParticleLineSphereCopyKernel      ParticleLineSphereCutKernel
ParticleLineSphereGrabKernel      ParticleLineSphereGrowKernel      ParticleLineSphereHoldKernel      ParticleLineSphereHoldResetKernel
ParticleLineSphereProcessKernel   ParticleLineSpherePullKernel      ParticleLineSpherePushKernel      ParticleLineSphereRigidityDecreaseKernel
ParticleLineSphereRigidityIncreaseKernelParticleLineSphereRigiditySetKernelParticlePlaneCollisionKernel      ParticleSphereCollisionHoldKernel
ParticleSphereCollisionKernel     ParticleSphereCopyKernel          ParticleSphereProcessKernel       PointJointsFinalKernel
PointJointsFixedRigidityKernel    PointJointsKernel                 PointJointsPreCalculatedFinalKernelPointJointsPreCalculatedKernel
ResetKernel                       ResetToPointJointsKernel          ResetToPointJointsPreCalculatedKernelSplineJointsAdjustKernel
SplineJointsKernel                SplineJointsReverseKernel         SplineJointsReverseWithParticleHoldKernelSplineJointsWithParticleHoldKernel
StiffnessJointsKernel
```

**GPUTools.Physics.Scripts.Tools** (2)

```
BoneCollidersCopyPaster           BoneCollidersPlacer
```

**GPUTools.Physics.Scripts.Types.Dynamic** (1)

```
GPParticle
```

**GPUTools.Physics.Scripts.Types.Joints** (2)

```
GPDistanceJoint                   GPPointJoint
```

**GPUTools.Physics.Scripts.Types.Shapes** (6)

```
GPGrabSphere                      GPLineSphere                      GPLineSphereWithDelta             GPLineSphereWithMatrixDelta
GPSphere                          GPSphereWithDelta
```

**GPUTools.Physics.Scripts.Wind** (3)

```
NoiseOctave                       Perlin2D                          WindReceiver
```

**GPUTools.Skinner.Scripts** (1)

```
SkinnerSettings
```

**GPUTools.Skinner.Scripts.Abstract** (1)

```
IMeshProvider
```

**GPUTools.Skinner.Scripts.Commands** (1)

```
SkinnerCommand
```

**GPUTools.Skinner.Scripts.Kernels** (4)

```
GPUBlendShapePlayer               GPUSkinner                        GPUSkinnerPro                     Weight
```

**GPUTools.Skinner.Scripts.Providers** (6)

```
MeshProvider                      PreCalcMeshProvider               PreCalcMeshProviderHolder         ScalpMeshType
SkinnedMeshProvider               StaticMeshProvider
```

**GPUTools.Skinner.Scripts.Utils** (1)

```
MeshSkinUtils
```

**HelloMeow** (5)

```
AudioImporter                     BassImporter                      DecoderImporter                   ImportOperation
MobileImporter
```

**Leap** (47)

```
Arm                               BeginProfilingBlockArgs           BeginProfilingForThreadArgs       Bone
Config                            ConfigChangeEventArgs             ConnectionEventArgs               ConnectionLostEventArgs
Constants                         Controller                        CopyFromOtherExtensions           CSharpExtensions
Device                            DeviceEventArgs                   DeviceFailureEventArgs            DeviceList
DistortionData                    DistortionEventArgs               DroppedFrameEventArgs             EndProfilingBlockArgs
EndProfilingForThreadArgs         FailedDevice                      FailedDeviceList                  Finger
Frame                             FrameEventArgs                    Hand                              HeadPoseEventArgs
IController                       Image                             ImageEventArgs                    InternalFrameEventArgs
LeapEvent                         LeapEventArgs                     LeapQuaternion                    LeapTestProviderExtensions
LeapTransform                     LogEventArgs                      Matrix                            MessageSeverity
PointMapping                      PointMappingChangeEventArgs       PolicyEventArgs                   SetConfigResponseEventArgs
TestHandFactory                   TransformExtensions               Vector
```

**Leap.Unity** (148)

```
AbstractHoldDetector              AnimationCurveUtil                ArrayPool`1                       AssetFolder
AutoValueProxy                    BitConverterNonAlloc              BoxedIndexableStruct`2            BoxedIndexableStructExtensions
CapsuleHand                       Chirality                         Comment                           ConnectionMonitor
DebugHand                         DefaultCurve                      DelayBuffer`1                     DeltaBuffer
DeltaBuffer`2                     DeltaFloatBuffer                  DeltaQuaternionBuffer             Deque`1
Detector                          DetectorLogicGate                 DisconnectionNotice               Either`2
EnableDepthBuffer                 Enum`1                            EnumEventTable                    ExecuteAfterAttribute
ExecuteAfterDefault               ExecuteBeforeAttribute            ExecuteBeforeDefault              ExtendedFingerDetector
FingerDirectionDetector           FingerModel                       FpsLabel                          FrameRateControls
GuiRectUtil                       HandDrop                          HandEnableDisable                 HandModel
HandModelBase                     HandModelManager                  HandRepresentation                Hands
HandTransitionBehavior            HandUtils                         Hash                              ICanReportDuplicateInformation
IIndexable`1                      IIndexableExtensions              IIndexableStruct`2                IMinHeapNode
IndexableEnumerator`1             IndexableStructEnumerator`2       InternalUtility                   IPoolable
IProgressView                     ISerializableDictionary           IValueProxy                       KeyEnableBehaviors
KeyEnableGameObjects              LeapColor                         LeapEyeDislocator                 LeapHandsAutoRig
LeapImageRetriever                LeapPreferences                   LeapProfiling                     LeapProjectCheckAttribute
LeapProjectChecks                 LeapProvider                      LeapProviderExtensions            LeapServiceProvider
LeapTestProvider                  LeapXRServiceProvider             ListAndArrayExtensions            LogicType
Maybe                             Maybe`1                           MinHeap`1                         MinimalHand
ModelType                         MultiTypedList                    MultiTypedList`1                  MultiTypedList`3
MultiTypedList`4                  MultiTypedList`5                  MultiTypedList`6                  MultiTypedList`7
MultiTypedList`8                  MultiTypedList`9                  MultiTypedListUtil                MultiTypedReference`1
MultiTypedReference`3             MultiTypedReference`4             MultiTypedReference`5             MultiTypedReferenceUtil
PalmDirectionDetector             PinchDetector                     PointingState                     PointingType
PolyFinger                        PolyHand                          Pool`1                            Pose
PoseExtensions                    PostProcessProvider               ProduceConsumeBuffer`1            ProfilerSample
ProgressBar                       ProximityDetector                 ProximityEvent                    ReadonlyHashSet`1
ReadonlyList`1                    ReadonlySlice`1                   ReadonlySliceExtensions           RiggedFinger
RiggedHand                        RigidFinger                       RigidHand                         RingBuffer`1
SDictionaryAttribute              SerializableDictionary`2          SerializableDictionaryBase        SerializableHashSet`1
SerializableHashSetBase           SerializableType                  SHashSetAttribute                 SingleLayer
SkeletalFinger                    SkeletalHand                      Slice`1                           SliceExtensions
SlidingMax                        SmoothedFloat                     SmoothedQuaternion                SmoothedVector3
StationaryTestLeapProvider        StreamingAsset                    StreamingFolder                   TransformHistory
TransformUtil                     Type`1                            UnityMatrixExtension              UnityQuaternionExtension
UnityVectorExtension              Utils                             XRHeightOffset                    XRSupportUtil
```

**Leap.Unity.Animation** (9)

```
Direction                         Ease                              HermiteSpline                     HermiteSpline2
HermiteSpline3                    IInterpolator                     SmoothType                        TransformTweenBehaviour
Tween
```

**Leap.Unity.Animation.Internal** (12)

```
ColorInterpolatorBase`1           FloatInterpolatorBase`1           GradientInterpolatorBase          InterpolatorBase`2
MaterialSelector                  QuaternionInterpolatorBase`1      TransformSelector                 TweenInstance
TweenRunner                       Vector2InterpolatorBase`1         Vector3InterpolatorBase`1         Vector4InterpolatorBase`1
```

**Leap.Unity.Attachments** (6)

```
AttachmentHand                    AttachmentHandEnableDisable       AttachmentHands                   AttachmentPointBehaviour
AttachmentPointFlags              AttachmentPointFlagsExtensions
```

**Leap.Unity.Attributes** (32)

```
AutoFindAttribute                 AutoFindLocations                 CombinablePropertyAttribute       CurveBoundsAttribute
DisableAttribute                  DisableIf                         DisableIfAll                      DisableIfAny
DisableIfBase                     EditTimeOnly                      EnumFlags                         IAdditiveDrawer
IAfterFieldAdditiveDrawer         IAfterLabelAdditiveDrawer         IBeforeFieldAdditiveDrawer        IBeforeLabelAdditiveDrawer
IFullPropertyDrawer               ImplementsInterfaceAttribute      ImplementsTypeNameDropdownAttributeIncrementable
InspectorNameAttribute            IPropertyConstrainer              IPropertyDisabler                 ISupportDragAndDrop
MaxValue                          MinMax                            MinValue                          OnEditorChangeAttribute
QuickButtonAttribute              RunTimeOnlyAttribute              UnitCurveAttribute                UnitsAttribute
```

**Leap.Unity.Encoding** (2)

```
VectorHand                        VectorHandExtensions
```

**Leap.Unity.Examples** (2)

```
InertiaPostProcessProvider        ProjectionPostProcessProvider
```

**Leap.Unity.Infix** (1)

```
Infix
```

**Leap.Unity.Query** (5)

```
Query`1                           QueryCollapseExtensions           QueryConversionExtensions         QueryOperatorExtensions
Values
```

**Leap.Unity.RuntimeGizmos** (5)

```
IRuntimeGizmoComponent            RuntimeColliderGizmos             RuntimeGizmoDrawer                RuntimeGizmoManager
RuntimeGizmoToggle
```

**Leap.Unity.Space** (10)

```
IdentityTransformer               IRadialTransformer                ISpaceComponent                   ITransformer
ITransformerExtensions            LeapCylindricalSpace              LeapRadialSpace                   LeapSpace
LeapSpaceAnchor                   LeapSphericalSpace
```

**Leap.Unity.Swizzle** (1)

```
Swizzle
```

**LeapInternal** (64)

```
Allocate                          CircularObjectBuffer`1            Connection                        CopyFromLeapCExtensions
Deallocate                        eDistortionMatrixType             eLeapAllocatorType                eLeapConnectionStatus
eLeapDeviceCaps                   eLeapDeviceFlag                   eLeapDeviceStatus                 eLeapDeviceType
eLeapDroppedFrameType             eLeapEventType                    eLeapHandType                     eLeapImageFormat
eLeapImageType                    eLeapLogSeverity                  eLeapPerspectiveType              eLeapPolicyFlag
eLeapRS                           eLeapServiceDisposition           eLeapValueType                    ImageData
LEAP_ALLOCATOR                    LEAP_BONE                         LEAP_CONFIG_CHANGE_EVENT          LEAP_CONFIG_RESPONSE_EVENT
LEAP_CONFIG_RESPONSE_EVENT_WITH_REF_TYPELEAP_CONNECTION_CONFIG            LEAP_CONNECTION_EVENT             LEAP_CONNECTION_INFO
LEAP_CONNECTION_LOST_EVENT        LEAP_CONNECTION_MESSAGE           LEAP_DEVICE_EVENT                 LEAP_DEVICE_FAILURE_EVENT
LEAP_DEVICE_INFO                  LEAP_DEVICE_REF                   LEAP_DIGIT                        LEAP_DISCONNECTION_EVENT
LEAP_DROPPED_FRAME_EVENT          LEAP_FRAME_HEADER                 LEAP_HAND                         LEAP_HEAD_POSE_EVENT
LEAP_IMAGE                        LEAP_IMAGE_EVENT                  LEAP_IMAGE_PROPERTIES             LEAP_LOG_EVENT
LEAP_MATRIX_3x3                   LEAP_PALM                         LEAP_POINT_MAPPING                LEAP_POINT_MAPPING_CHANGE_EVENT
LEAP_POLICY_EVENT                 LEAP_QUATERNION                   LEAP_TELEMETRY_DATA               LEAP_TIP
LEAP_TRACKING_EVENT               LEAP_VARIANT_REF_TYPE             LEAP_VARIANT_VALUE_TYPE           LEAP_VECTOR
LeapC                             Logger                            MemoryManager                     StructMarshal`1
```

**MeshVR** (42)

```
AssetLoader                       CubicBezierCurveCompact           CubicBezierPointCompact           DAZClothingItemOpenInCreatorControl
DAZClothingItemOpenInCreatorControlUIDAZClothSettingsSimTextureReloaderDAZDynamic                        DAZDynamicDeleter
DAZDynamicDeleterUI               DAZDynamicItemOpenInCreatorControlDAZDynamicItemOpenInCreatorControlUIDAZDynamicReloader
DAZDynamicReloaderUI              DAZImport                         DAZImportMaterial                 DAZRuntimeCreator
DAZRuntimeCreatorUI               FPS                               GlobalLightingManager             GlobalSceneOptions
GpuProximityGrab                  GpuTriggerGrab                    HandModelControl                  HandModelControlUI
IBinaryStorable                   LeapHandModelControl              LeapHandModelControlUI            PerfMon
PerfMonCamera                     PerfMonPre                        PresetManager                     PresetManagerControl
PresetManagerControlOverview      PresetManagerControlOverviewUI    PresetManagerControlUI            ScreenUICamera
ScreenUICameraConnector           Selectable                        Selector                          SteamVRActivator
WindControl                       WindControlUI
```

**MeshVR.AnimationPatternV2** (3)

```
AnimationPatternV2                Step                              Track
```

**MeshVR.Hands** (8)

```
Finger                            FingerInput                       FingerOutput                      HandInput
HandInputInitializer              HandOutput                        OVRHandInput                      SteamVRHandInput
```

**MHLab** (1)

```
Example
```

**MHLab.PATCH.Settings** (1)

```
Settings
```

**MK.Glow** (2)

```
GlowType                          MKGlow
```

**mset** (24)

```
ColorMode                         ColorModeLabel                    Corner                            CubeBuffer
FreeProbe                         Gamma                             GLUtil                            Logo
QPow                              Quality                           RGB                               ShaderIDs
SHEncoding                        SHEncodingFile                    SHUtil                            Sky
SkyAnchor                         SkyApplicator                     SkyBlender                        SkyDebug
SkyManager                        SkyProbe                          TexSize                           Util
```

**MVR** (5)

```
Metric                            MetricUI                          NaNUtils                          ObjectAllocator
TransformRotator
```

**MVR.FileManagement** (27)

```
CacheManager                      CacheSizeUI                       DirectoryEntry                    FileChecker
FileEntry                         FileEntryStream                   FileEntryStreamReader             FileManager
PackageBuilder                    PackageBuilderContentItem         PackageBuilderPackageItem         PackageBuilderReferenceItem
PackageBuilderUI                  PackageInfo                       PackageInfoUI                     SystemDirectoryEntry
SystemFileEntry                   SystemFileEntryStream             SystemFileEntryStreamReader       UserConfirmPanel
VarDirectoryEntry                 VarFileEntry                      VarFileEntryStream                VarFileEntryStreamReader
VarPackage                        VarPackageCustomOption            VarPackageGroup
```

**MVR.FileManagementSecure** (5)

```
ExceptionCallback                 FileManagerSecure                 OnRefresh                         ShortCut
UserActionCallback
```

**MVR.Hub** (11)

```
HubBrowse                         HubBrowseUI                       HubDownloader                     HubDownloaderUI
HubResourceCreatorSupportUI       HubResourceItem                   HubResourceItemDetail             HubResourceItemDetailUI
HubResourceItemUI                 HubResourcePackage                HubResourcePackageUI
```

**NativeLibrary** (1)

```
Linked
```

**NativeLibraryDoorstop** (1)

```
Doorstop
```

**Obi** (66)

```
Constants                         CoroutineJob                      EditorCoroutine                   IObiSolverClient
ObiActor                          ObiAerodynamicConstraintBatch     ObiAerodynamicConstraints         ObiAmbientForceZone
ObiAnimatorController             ObiArbiter                        ObiBatchedConstraints             ObiBendConstraintBatch
ObiBendingConstraints             ObiBezierCurve                    ObiBone                           ObiBoxShapeTracker
ObiBoxShapeTracker2D              ObiCapsuleShapeTracker            ObiCapsuleShapeTracker2D          ObiCatmullRomCurve
ObiCircleShapeTracker2D           ObiCollider                       ObiCollider2D                     ObiColliderBase
ObiCollisionMaterial              ObiConstraintBatch                ObiCurve                          ObiDistanceConstraintBatch
ObiDistanceConstraints            ObiEdgeShapeTracker2D             ObiEmitter                        ObiEmitterMaterial
ObiEmitterMaterialFluid           ObiEmitterMaterialGranular        ObiEmitterShape                   ObiExternalForce
ObiMeshShapeTracker               ObiParticleBaker                  ObiParticleCache                  ObiParticleGridDebugger
ObiParticleHandle                 ObiParticleRenderer               ObiPinConstraintBatch             ObiPinConstraints
ObiProfiler                       ObiRigidbody                      ObiRope                           ObiRopeCursor
ObiRopeHelper                     ObiRopeSection                    ObiShapeTracker                   ObiSkinConstraintBatch
ObiSkinConstraints                ObiSolver                         ObiSolverData                     ObiSphereShapeTracker
ObiSphericalForceZone             ObiStitcher                       ObiTerrainShapeTracker            ObiTetherConstraintBatch
ObiTetherConstraints              ObiUtils                          ObiVolumeConstraintBatch          ObiVolumeConstraints
ParticleData                      ShadowmapExposer
```

**Obi.Characters.ThirdPerson** (2)

```
ThirdPersonCharacter              ThirdPersonUserControl
```

**Obi.CrossPlatformInput** (9)

```
AxisTouchButton                   ButtonHandler                     CrossPlatformInputManager         InputAxisScrollbar
Joystick                          MobileControlRig                  TiltInput                         TouchPad
VirtualInput
```

**Obi.CrossPlatformInput.PlatformSpecific** (2)

```
MobileInput                       StandaloneInput
```

**Oculus.Avatar** (1)

```
CAPI
```

**Oculus.Platform** (129)

```
Achievements                      AchievementType                   AndroidPlatform                   Application
ApplicationLifecycle              ApplicationOptions                AssetFile                         Avatar
Callback                          CallbackRunner                    CAPI                              CloudStorage
CloudStorageDataStatus            CloudStorageUpdateStatus          Core                              Entitlements
GraphAPI                          HTTP                              IAP                               IMicrophone
IVoipPCMSource                    KeyValuePairType                  LaunchType                        LeaderboardFilterType
Leaderboards                      LeaderboardStartAt                Livestreaming                     LivestreamingAudience
LivestreamingMicrophoneStatus     LivestreamingStartStatus          Matchmaking                       MatchmakingCriterionImportance
MatchmakingOptions                MatchmakingStatApproach           Media                             MediaContentType
Message                           Message`1                         MessageWithAchievementDefinitions MessageWithAchievementProgressList
MessageWithAchievementUpdate      MessageWithApplicationVersion     MessageWithAssetFileDeleteResult  MessageWithAssetFileDownloadCancelResult
MessageWithAssetFileDownloadResultMessageWithAssetFileDownloadUpdateMessageWithCloudStorageConflictMetadataMessageWithCloudStorageData
MessageWithCloudStorageMetadataListMessageWithCloudStorageMetadataUnderLocalMessageWithCloudStorageUpdateResponseMessageWithHttpTransferUpdate
MessageWithInstalledApplicationListMessageWithLeaderboardDidUpdate   MessageWithLeaderboardEntryList   MessageWithLivestreamingApplicationStatus
MessageWithLivestreamingStartResultMessageWithLivestreamingStatus    MessageWithLivestreamingVideoStatsMessageWithMatchmakingAdminSnapshot
MessageWithMatchmakingBrowseResultMessageWithMatchmakingEnqueueResultMessageWithMatchmakingEnqueueResultAndRoomMessageWithMatchmakingNotification
MessageWithMatchmakingStatsUnderMatchmakingStatsMessageWithNetworkingPeer         MessageWithOrgScopedID            MessageWithParty
MessageWithPartyID                MessageWithPartyUnderCurrentParty MessageWithPidList                MessageWithPingResult
MessageWithPlatformInitialize     MessageWithProductList            MessageWithPurchase               MessageWithPurchaseList
MessageWithRoom                   MessageWithRoomInviteNotification MessageWithRoomInviteNotificationListMessageWithRoomList
MessageWithRoomUnderCurrentRoom   MessageWithRoomUnderViewerRoom    MessageWithSdkAccountList         MessageWithShareMediaResult
MessageWithString                 MessageWithSystemPermission       MessageWithSystemVoipState        MessageWithUser
MessageWithUserAndRoomList        MessageWithUserList               MessageWithUserProof              MessageWithUserReportID
MicrophoneInput                   MicrophoneInputNative             Net                               Notifications
Packet                            Parties                           PeerConnectionState               PermissionGrantStatus
PermissionType                    PlatformInitializeResult          PlatformInternal                  PlatformSettings
Request                           Request`1                         RoomJoinability                   RoomJoinPolicy
RoomMembershipLockStatus          RoomOptions                       Rooms                             RoomType
SdkAccountType                    SendPolicy                        ShareMediaStatus                  StandalonePlatform
StandalonePlatformSettings        SystemVoipStatus                  TimeWindow                        UserOptions
UserOrdering                      UserPresenceStatus                Users                             Voip
VoipAudioSourceHiLevel            VoipMuteState                     VoipPCMSourceNative               VoipSampleRate
WindowsPlatform
```

**Oculus.Platform.Models** (63)

```
AchievementDefinition             AchievementDefinitionList         AchievementProgress               AchievementProgressList
AchievementUpdate                 ApplicationVersion                AssetFileDeleteResult             AssetFileDownloadCancelResult
AssetFileDownloadResult           AssetFileDownloadUpdate           CloudStorageConflictMetadata      CloudStorageData
CloudStorageMetadata              CloudStorageMetadataList          CloudStorageUpdateResponse        DeserializableList`1
Error                             HttpTransferUpdate                InstalledApplication              InstalledApplicationList
LaunchDetails                     LeaderboardEntry                  LeaderboardEntryList              LivestreamingApplicationStatus
LivestreamingStartResult          LivestreamingStatus               LivestreamingVideoStats           MatchmakingAdminSnapshot
MatchmakingAdminSnapshotCandidate MatchmakingAdminSnapshotCandidateListMatchmakingBrowseResult           MatchmakingEnqueuedUser
MatchmakingEnqueuedUserList       MatchmakingEnqueueResult          MatchmakingEnqueueResultAndRoom   MatchmakingStats
NetworkingPeer                    OrgScopedID                       Party                             PartyID
Pid                               PidList                           PingResult                        PlatformInitialize
Product                           ProductList                       Purchase                          PurchaseList
Room                              RoomInviteNotification            RoomInviteNotificationList        RoomList
SdkAccount                        SdkAccountList                    ShareMediaResult                  SystemPermission
SystemVoipState                   User                              UserAndRoom                       UserAndRoomList
UserList                          UserProof                         UserReportID
```

**OldMoatGames** (4)

```
AnimatedGifPlayer                 GifDecoder                        GifPath                           GifPlayerState
```

**PrefabEvolution** (11)

```
BaseExposedData                   EvolvePrefab                      ExposedProperty                   ExposedPropertyGroup
GameObjectWrapper                 PEExposedProperties               PELinkage                         PEModifications
PEPrefabScript                    Utils                             Wrappers
```

**SimpleJSON** (6)

```
JSON                              JSONArray                         JSONBinaryTag                     JSONClass
JSONData                          JSONNode
```

**SpeechBlendEngine** (3)

```
ExtractFeatures                   SpeechUtil                        VoiceProfile
```

**Technie.PhysicsCreator** (7)

```
Hull                              HullData                          HullPainter                       HullType
PaintingData                      Sphere                            SphereUtils
```

**Technie.PhysicsCreator.QHull** (9)

```
Face                              FaceList                          HalfEdge                          InternalErrorException
Point3d                           QuickHull3D                       SimpleExample                     Vector3d
Vertex
```

**TypeReferences** (5)

```
ClassExtendsAttribute             ClassGrouping                     ClassImplementsAttribute          ClassTypeConstraintAttribute
ClassTypeReference
```

**uFileBrowser** (7)

```
DirectoryButton                   FileBrowser                       FileBrowserCallback               FileBrowserFullCallback
FileButton                        FileIcon                          ShortCutButton
```

**UltimateGameTools.MeshSimplifier** (3)

```
MeshUniqueVertices                RelevanceSphere                   Simplifier
```

**UnityEngine.EventSystems** (5)

```
GamePadInputModule                OVRInputModule                    OVRPhysicsRaycaster               OVRPointerEventData
PointerEventDataExtension
```

**UnityEngine.EventSystems.Extensions** (1)

```
AimerInputModule
```

**UnityEngine.PostProcessing** (47)

```
AmbientOcclusionComponent         AmbientOcclusionModel             AntialiasingModel                 BloomComponent
BloomModel                        BuiltinDebugViewsComponent        BuiltinDebugViewsModel            ChromaticAberrationComponent
ChromaticAberrationModel          ColorGradingComponent             ColorGradingCurve                 ColorGradingModel
DepthOfFieldComponent             DepthOfFieldModel                 DitheringComponent                DitheringModel
EyeAdaptationComponent            EyeAdaptationModel                FogComponent                      FogModel
FxaaComponent                     GetSetAttribute                   GrainComponent                    GrainModel
GraphicsUtils                     MaterialFactory                   MinAttribute                      MotionBlurComponent
MotionBlurModel                   PostProcessingBehaviour           PostProcessingComponent`1         PostProcessingComponentBase
PostProcessingComponentCommandBuffer`1PostProcessingComponentRenderTexture`1PostProcessingContext             PostProcessingModel
PostProcessingProfile             RenderTextureFactory              ScreenSpaceReflectionComponent    ScreenSpaceReflectionModel
TaaComponent                      TrackballAttribute                TrackballGroupAttribute           UserLutComponent
UserLutModel                      VignetteComponent                 VignetteModel
```

**UnityEngine.UI** (3)

```
ExtensionsToggle                  ExtensionsToggleGroup             ReturnKeyTriggersButton
```

**UnityEngine.UI.Extensions** (123)

```
Accordion                         AccordionElement                  AutoCompleteComboBox              AutoCompleteSearchType
BestFitOutline                    BezierPath                        BoundTooltipItem                  BoundTooltipTrigger
BoxSlider                         CableCurve                        Circle                            CLZF2
ComboBox                          CooldownButton                    CUIBezierCurve                    CUIGraphic
CUIImage                          CUIText                           CurvedLayout                      CurvedText
CylinderText                      DiamondGraph                      DragCorrector                     DropDownList
DropDownListButton                DropDownListItem                  ExampleSelectable                 FancyScrollView`1
FancyScrollView`2                 FancyScrollViewCell`1             FancyScrollViewCell`2             FancyScrollViewNullContext
FlowLayoutGroup                   Gradient                          Gradient2                         GradientDir
GradientMode                      HorizontalScrollSnap              HoverTooltip                      IBoxSelectable
InputFieldEnterSubmit             InputFocus                        KnobFloatValueEvent               LetterSpacing
Menu                              Menu`1                            MenuManager                       MonoSpacing
MultiTouchScrollRect              NavigationMode                    NicerOutline                      NonDrawingGraphic
PaginationManager                 PPIViewer                         RadialLayout                      RadialSlider
RaycastMask                       ReadOnlyAttribute                 RectTransformExtension            ReorderableList
ReorderableListContent            ReorderableListDebug              ReorderableListElement            RescaleDragPanel
RescalePanel                      ResizePanel                       ResolutionMode                    ScrollConflictManager
ScrollPositionController          ScrollRectEx                      ScrollRectExtensions              ScrollRectLinker
ScrollRectTweener                 ScrollSnap                        ScrollSnapBase                    ScrollSnapScrollbarHelper
Segment                           SegmentedControl                  SelectableScaler                  SelectionBox
ShineEffect                       ShineEffector                     SimpleMenu`1                      SoftMaskScript
Stepper                           StepperSide                       TableLayoutGroup                  TabNavigationHelper
TextPic                           TileSizeFitter                    TiltWindow                        ToolTip
UI_InfiniteScroll                 UI_Knob                           UI_ScrollRectOcclusion            UI_TweenScale
UIAdditiveEffect                  UICircle                          UICornerCut                       UIExtensionMethods
UIFlippable                       UIGridRenderer                    UIHighlightable                   UIImageCrop
UILinearDodgeEffect               UILineConnector                   UILineRenderer                    UILineTextureRenderer
UIMultiplyEffect                  UIParticleSystem                  UIPolygon                         UIPrimitiveBase
UIScreenEffect                    UIScrollToSelection               UIScrollToSelectionXY             UISelectableExtension
UISoftAdditiveEffect              UIVerticalScroller                UIWindowBase                      Vector3_Array2D
VerticalScrollSnap                VRCursor                          VRInputModule
```

**UnityEngine.UI.Extensions.ColorPicker** (12)

```
ColorImage                        ColorLabel                        ColorPickerControl                ColorPickerPresets
ColorPickerTester                 ColorSlider                       ColorSliderImage                  ColorValues
HexColorField                     HsvColor                          HSVUtil                           SVBoxSlider
```

**UnityEngine.UI.Extensions.Examples** (30)

```
AnimateEffects                    AwesomeMenu                       ComboBoxChanged                   CooldownEffect_Image
CooldownEffect_SAUIM              Example01CellDto                  Example01Scene                    Example01ScrollView
Example01ScrollViewCell           Example02CellDto                  Example02Scene                    Example02ScrollView
Example02ScrollViewCell           Example02ScrollViewContext        Example03CellDto                  Example03Scene
Example03ScrollView               Example03ScrollViewCell           Example03ScrollViewContext        GameMenu
LineRendererOrbit                 MainMenu                          OptionsMenu                       PaginationScript
PauseMenu                         ScrollingCalendar                 TestAddingPoints                  testHref
UpdateRadialValue                 UpdateScrollSnap
```

**UnityEngine.UI.Extensions.Tweens** (1)

```
FloatTween
```

**UnityStandardAssets.CinematicEffects** (6)

```
AntiAliasing                      DepthOfField                      ImageEffectHelper                 RenderTexureUtility
ScreenSpaceReflection             TonemappingColorGrading
```

**UnityStandardAssets.ImageEffects** (37)

```
AAMode                            Antialiasing                      Bloom                             BloomAndFlares
BloomOptimized                    BloomScreenBlendMode              Blur                              BlurOptimized
CameraMotionBlur                  ColorCorrectionCurves             ColorCorrectionLookup             ColorCorrectionRamp
ContrastEnhance                   ContrastStretch                   CreaseShading                     DepthOfField
DepthOfFieldDeprecated            EdgeDetection                     Fisheye                           Grayscale
HDRBloomMode                      ImageEffectBase                   ImageEffects                      LensflareStyle34
MotionBlur                        NoiseAndGrain                     NoiseAndScratches                 PostEffectsBase
ScreenOverlay                     ScreenSpaceAmbientOcclusion       SepiaTone                         SunShafts
Tonemapping                       TweakMode34                       Twirl                             VignetteAndChromaticAberration
Vortex
```

**UnityStandardAssets.Utility** (1)

```
AutoMoveAndRotate
```

**UnityStandardAssets.Water** (1)

```
Water
```

**UnityStandardAssets.WaterVR** (1)

```
Water
```

**UnityThreading** (21)

```
ActionExtension                   ActionThread                      Channel                           Channel`1
Dispatcher                        DispatcherBase                    EnumerableExtension               EnumeratableActionThread
EnumeratorExtension               NullDispatcher                    ObjectExtension                   SwitchTo
Task                              Task`1                            TaskDistributor                   TaskEndedEventHandler
TaskExtension                     TaskSortingSystem                 ThreadBase                        TickThread
WaitOneExtension
```

**VikingCrewDevelopment** (1)

```
RandomMovementBehaviour
```

**VikingCrewDevelopment.Demos** (2)

```
SayRandomThingsBehaviour          UISpeechController
```

**VikingCrewTools** (1)

```
MouseOrbitImproved
```

**VikingCrewTools.UI** (4)

```
RatioLayoutFitter                 ScriptedDialogueBehaviour         SpeechBubbleBehaviour             SpeechBubbleManager
```

**Weelco** (2)

```
MouseCameraRotation               VRKeyboardDemo
```

**Weelco.VRInput** (12)

```
IUIHitPointer                     IUILaserPointer                   IUIPointer                        OVRUIHitPointer
OVRUILaserPointer                 UIGazePointer                     ViveUILaserPointer                VRGazeInputModule
VRHitInputModule                  VRInputEventData                  VRInputModule                     VRInputSettings
```

**Weelco.VRKeyboard** (4)

```
VRKeyboardBase                    VRKeyboardButton                  VRKeyboardData                    VRKeyboardFull
```

**Wilberforce.VAO** (2)

```
VAOEffect                         VAOEffectCommandBuffer
```

**ZenFulcrum.EmbeddedBrowser** (17)

```
ActionTimer                       BallBrowserSpawner                CoinPickup                        DemoList
Door                              ExplainUnzip                      ForcedCooperation                 HUDManager
PlayerInventory                   SayWordsOnTouch                   SimpleController                  SimpleFPSController
SimpleScripting                   TestDownloadServer                VRBrowserPanel                    VRMainControlPanel
VRMode
```


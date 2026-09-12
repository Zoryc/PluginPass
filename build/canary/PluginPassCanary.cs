//
// PluginPassCanary - not part of PluginPass. A ~20 line session plugin whose only
// job is to answer one question: can this VaM install compile a plugin script at all?
//
// Some installs crash inside VaM's runtime compiler (mono.dll access violation in
// mono_class_from_typeref, under TypeBuilder.CreateType). When that happens the game
// dies while compiling, before a single line of plugin code runs, so it looks like
// "plugin X crashes VaM" when in fact any plugin might.
//
// Add this as a Session Plugin.
//   * It prints a message to the log  -> compiling works, the problem is elsewhere.
//   * VaM dies adding it              -> the install cannot compile plugins. Nothing
//                                        written in C# can fix that; the environment
//                                        needs fixing (start by disabling BepInEx
//                                        plugins that patch mono, e.g. GC patchers).
//
using UnityEngine;

namespace VexMech
{
    public class PluginPassCanary : MVRScript
    {
        public override void Init()
        {
            SuperController.LogMessage("PluginPassCanary: compiled and initialised OK.");
            JSONStorableString status = new JSONStorableString("status",
                "<b>Canary alive.</b>\n\nThis install can compile plugin scripts.");
            CreateTextField(status, false).height = 200f;
        }
    }
}

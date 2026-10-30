"""Writes Plugins/Shared/RynthUi/RynthIcons.cs from RynthCore's ImGui/PhosphorIcons.cs.

Only the icons in the engine's Baked list are in the shared font atlas, so only those are
written: a plugin can't name an icon that would draw as nothing. Run after the engine adds
icons to Baked (Tools/RynthUi.Tests checks the two still match):

    python Tools/gen-rynthui-icons.py [path to RynthCore]     (default: the sibling ..\\RynthCore)
"""
import os, re, sys

here = os.path.dirname(os.path.abspath(__file__))
suite = os.path.dirname(here)
core = os.path.abspath(sys.argv[1]) if len(sys.argv) > 1 else os.path.join(os.path.dirname(suite), "RynthCore")
src = os.path.join(core, "src", "RynthCore.Engine", "ImGui", "PhosphorIcons.cs")
text = open(src, encoding="utf-8").read()

consts = dict(re.findall(r'public const string (\w+) = "\\u([0-9A-Fa-f]{4})";', text))
baked_expr = re.search(r"public const string Baked =(.*?);", text, re.S).group(1)
baked = re.findall(r"[A-Z]\w*", baked_expr)
missing = [n for n in baked if n not in consts]
if missing:
    sys.exit("Baked names without a constant: " + ", ".join(missing))

names = sorted(set(baked))
lines = [
    "// ============================================================================",
    "//  RynthSuite - Plugins/Shared/RynthUi/RynthIcons.cs   (GENERATED: do not edit)",
    "//  The Phosphor icons (MIT) baked into the engine's font atlas, by Phosphor's",
    "//  names: put one in a label or a button. Written by Tools/gen-rynthui-icons.py",
    "//  from RynthCore's ImGui/PhosphorIcons.cs (its Baked list); an icon that isn't",
    "//  baked would draw as nothing, so it isn't here. Tools/RynthUi.Tests checks",
    "//  this file against the engine's.",
    "// ============================================================================",
    "",
    "namespace RynthCore.Plugin.Shared.RynthUi;",
    "",
    "internal static class RynthIcons",
    "{",
]
for n in names:
    lines.append(f'    public const string {n} = "\\u{consts[n].upper()}";')
lines += ["}", ""]
out = os.path.join(suite, "Plugins", "Shared", "RynthUi", "RynthIcons.cs")
os.makedirs(os.path.dirname(out), exist_ok=True)
with open(out, "w", encoding="utf-8", newline="\n") as f:
    f.write("\n".join(lines))
print(f"{len(names)} icons -> {out}")

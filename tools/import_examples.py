"""
Imports the Csound example collection into the app (src/CsoundVisual/Examples) and builds the
index the «Ejemplos» browser reads (Examples/index.json).

Sources:
  1. The official Csound manual (https://github.com/csound/manual): docs/examples/*.csd
     (one or more per opcode, ~1500) plus the sound/data files they use, and docs/opcodes/*.md,
     whose header gives each opcode's category ("Signal Generators:Basic Oscillators") and whose
     first paragraph is a one-line description. Example ↔ opcode comes from the
     `--8<-- "examples/<file>.csd"` include lines of the opcode pages.
  2. The examples that ship with the Csound 7 installation (@Source/Csound7/examples/*.csd).

Usage:
  python tools/import_examples.py <manual-repo-folder> [<csound7-folder>]
Re-running replaces Examples/Manual and Examples/Csound7 and rewrites index.json.

<csound7-folder> defaults to @Source/Csound7 in the repository and must contain examples/.
The curated galleries (Examples/Showcase, Examples/AudioFX, Examples/Cabbage) are written by
hand and are never modified here; only their metadata files (showcase.json, audiofx.json,
cabbage.json, each ``{"pieces": [...]}``) are read to put them at the top of the index.

Output (Examples/index.json), read by the app's examples browser:
  {"generatedFrom": ..., "count": N, "examples": [entry, ...]}
  entry = {"file": path relative to Examples/ with "/" separators, "title", "name" (file name
           without extension), "category", "description", "source": "manual" | "csound7" |
           "showcase" | "audiofx" | "cabbage"}; curated entries also carry "titleEs",
           "descriptionEs" and "live" (true for a live effect meant for the «Csound Visual FX»
           VST3 plug-in, which processes the DAW track's input; the browser labels it so).
The JSON shape must match ViewModels/ExampleCatalog.cs in the app.
Order: curated galleries first (in their metadata order), then everything else sorted by
category, title and file name.

The script runs top to bottom at import time (no functions, no ``if __name__`` guard); it is a
maintenance tool run by hand, never by the app or the build.
"""
import json, os, re, shutil, sys

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))  # repository root
DEST = os.path.join(ROOT, "src", "CsoundVisual", "Examples")        # copied next to the exe by the csproj

# Command line: argv[1] is required, argv[2] is optional. Without argv[1] the script used to
# die with an IndexError traceback; now it prints the usage and exits with code 2 (the usual
# "bad command line" status).
if len(sys.argv) < 2 or sys.argv[1] in ("-h", "--help"):
    print("usage: python tools/import_examples.py <manual-repo-folder> [<csound7-folder>]",
          file=sys.stderr)
    sys.exit(2)
manual = sys.argv[1]
csound7 = sys.argv[2] if len(sys.argv) > 2 else os.path.join(ROOT, "@Source", "Csound7")
ex_src = os.path.join(manual, "docs", "examples")
op_src = os.path.join(manual, "docs", "opcodes")
# Validate the sources BEFORE anything is deleted below: the copy step starts by wiping
# Examples/Manual and Examples/Csound7, so a mistyped path would otherwise destroy the current
# examples and only then fail.
for folder in (ex_src, op_src, os.path.join(csound7, "examples")):
    if not os.path.isdir(folder):
        print("error: folder not found: " + folder, file=sys.stderr)
        sys.exit(2)

# --- copy files --------------------------------------------------------------------------------
# The two imported folders are wiped and recopied wholesale, so files removed upstream disappear.
# docs/examples is copied completely (not only *.csd) because the examples open sound files,
# MIDI files and data files that sit next to them.
for sub in ("Manual", "Csound7"):
    shutil.rmtree(os.path.join(DEST, sub), ignore_errors=True)
shutil.copytree(ex_src, os.path.join(DEST, "Manual"))
# Licence note of the manual examples (GFDL-1.2-or-later), kept next to them.
shutil.copy2(os.path.join(ROOT, "tools", "manual-examples-LICENSE.md"), os.path.join(DEST, "Manual", "LICENSE.md"))
os.makedirs(os.path.join(DEST, "Csound7"), exist_ok=True)
# From the Csound 7 installation only the top-level .csd files are taken (flat folder).
for name in sorted(os.listdir(os.path.join(csound7, "examples"))):
    if name.lower().endswith(".csd"):
        shutil.copy2(os.path.join(csound7, "examples", name), os.path.join(DEST, "Csound7", name))

# --- opcode pages: category, description, which examples they include ---------------------------
by_example = {}   # "oscil.csd" -> (opcode, category, description)
# MkDocs "snippets" include syntax used by the manual pages to embed an example file.
include = re.compile(r'--8<--\s*"examples/([^"]+\.csd)"')
for page in sorted(os.listdir(op_src)):
    if not page.endswith(".md"):
        continue
    text = open(os.path.join(op_src, page), encoding="utf8", errors="replace").read()
    # Front-matter fields ("id:", "category:") and the first "# " heading. The heading is the
    # preferred display name, then the id, then the file name.
    m_id = re.search(r"^id:(.+)$", text, re.M)
    m_cat = re.search(r"^category:(.+)$", text, re.M)
    m_title = re.search(r"^# (.+)$", text, re.M)
    opcode = (m_title.group(1) if m_title else (m_id.group(1) if m_id else page[:-3])).strip()
    category = m_cat.group(1).strip() if m_cat else ""
    # Description = first prose line after the heading: skip headings, images, HTML, admonitions
    # ("===" / "!!!") and code fences, then strip Markdown emphasis/code markers.
    desc = ""
    if m_title:
        for line in text[m_title.end():].splitlines():
            line = line.strip()
            if line and not line.startswith(("#", "!", "<", "=", "`")):
                desc = re.sub(r"[_*`]", "", line)
                break
    # An example included by several pages keeps the first page in alphabetical order.
    for ex in include.findall(text):
        by_example.setdefault(os.path.basename(ex), (opcode, category, desc))

# --- index entries for the imported examples ------------------------------------------------------
# Every .csd under Examples/Manual (recursively); examples not referenced by any opcode page get
# a fallback category based on their folder. Matching is by bare file name, so two .csd files with
# the same name in different sub-folders share the same opcode information.
entries = []
for dirpath, _, files in os.walk(os.path.join(DEST, "Manual")):
    for f in sorted(files):
        if not f.lower().endswith(".csd"):
            continue
        rel = os.path.relpath(os.path.join(dirpath, f), DEST).replace("\\", "/")
        opcode, category, desc = by_example.get(f, ("", "", ""))
        if not category:
            category = "Musical Examples" if "/musical/" in rel else "Other"
        entries.append({"file": rel, "title": opcode or f[:-4], "name": f[:-4], "category": category,
                         "description": desc, "source": "manual"})
for f in sorted(os.listdir(os.path.join(DEST, "Csound7"))):
    entries.append({"file": "Csound7/" + f, "title": f[:-4], "name": f[:-4], "category": "Csound 7 (installation)",
                    "description": "", "source": "csound7"})

entries.sort(key=lambda e: (e["category"], e["title"].lower(), e["name"].lower()))

# The curated galleries (written for this app, each in its own Examples/<folder> with a metadata
# file) go first, in their intended order, with Spanish and English texts. A gallery whose
# metadata file is missing is skipped silently. Their "file" values are taken verbatim from the
# metadata (paths relative to Examples/).
showcase = []
# Curated galleries, in this order: (folder, metadata file, category).
for folder, meta_name, category in [("Showcase", "showcase.json", "Showcase"), ("AudioFX", "audiofx.json", "AudioFX"),
                                    ("Cabbage", "cabbage.json", "Cabbage")]:
    meta = os.path.join(DEST, folder, meta_name)
    if not os.path.exists(meta):
        continue
    for p in json.load(open(meta, encoding="utf8"))["pieces"]:
        showcase.append({"file": p["file"], "title": p["title"], "titleEs": p.get("titleEs", ""),
                         "name": os.path.splitext(os.path.basename(p["file"]))[0], "category": category,
                         "description": p.get("description", ""), "descriptionEs": p.get("descriptionEs", ""),
                         "live": bool(p.get("live", False)), "source": category.lower()})
entries = showcase + entries
# ensure_ascii=False keeps «», accents and em dashes readable in the JSON; indent=1 keeps the
# file diff-friendly without making it much larger.
json.dump({"generatedFrom": "csound/manual docs/examples + Csound 7 installation examples", "count": len(entries), "examples": entries},
          open(os.path.join(DEST, "index.json"), "w", encoding="utf8"), ensure_ascii=False, indent=1)
print("examples:", len(entries), "· categories:", len({e["category"] for e in entries}),
      "· with opcode info:", sum(1 for e in entries if e["description"]))

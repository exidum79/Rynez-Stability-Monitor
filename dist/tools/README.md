# Put y-cruncher here

This tool uses **y-cruncher** as its stress engine, but does **not** include it
(y-cruncher has its own license and is not redistributed here).

You must supply it yourself:

1. Download y-cruncher from the official site:
   http://www.numberworld.org/y-cruncher/
   (Use the **latest version** — that is what this tool was developed and tested against.)
2. Extract the archive.
3. Copy its entire contents into **this folder** (`dist/tools/`).

When it is in place, this folder should contain at least:

```
dist/tools/
  y-cruncher.exe        <-- required, the launcher looks for exactly this path
  Binaries/             <-- the per-architecture stress binaries
  ...
```

The monitor checks for `dist/tools/y-cruncher.exe` on startup and will refuse to
run (with a download hint) if it is missing.

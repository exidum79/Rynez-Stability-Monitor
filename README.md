# Rynez Stability Monitor

[![Download](https://img.shields.io/github/v/release/exidum79/Rynez-Stability-Monitor?label=Download&logo=github&sort=semver)](https://github.com/exidum79/Rynez-Stability-Monitor/releases/latest)
[![Downloads](https://img.shields.io/github/downloads/exidum79/Rynez-Stability-Monitor/total?logo=github)](https://github.com/exidum79/Rynez-Stability-Monitor/releases)
[![License: MIT](https://img.shields.io/badge/License-MIT-green.svg)](LICENSE)
[![Platform](https://img.shields.io/badge/platform-Windows%20x64-blue)](#requirements)

> **▶ [Download the latest build (ready-to-run .exe, no .NET install needed)](https://github.com/exidum79/Rynez-Stability-Monitor/releases/latest)** — then drop in your own y-cruncher (see [Setup](#y-cruncher-is-not-included)).

A small, focused Windows tool for **diagnosing which CPU core is unstable** when
you are tuning an AMD Ryzen with **Curve Optimizer / PBO** (and similar
overclock / undervolt setups).

It drives [y-cruncher](http://www.numberworld.org/y-cruncher/) as the stress
engine, wraps it with a micro-freeze detector and a reboot-surviving crash
breadcrumb, times every run to spot silent slowdowns, **stops the moment a
problem is found, and points at the offending core** — then writes everything to
a permanent CSV log so the failing core is recorded even when Windows logs
nothing.

> It is a **diagnostic** tool. It tells you *which* core misbehaved. **It does
> not change any BIOS / Curve Optimizer / voltage setting for you** — what you do
> with that information is your decision (and your risk). See
> [Disclaimer](#disclaimer).

---

## Why this exists

When you push Curve Optimizer too far, the failure is often:

- **Silent** — a wrong calculation with no blue screen, or
- **Catastrophic** — an instant reboot that leaves **nothing** in the Windows
  Event Log.

In both cases the standard question "*which core was it?*" goes unanswered. This
tool's whole job is to **be the recorder Windows isn't**, and to attribute the
failure to a specific physical core.

---

## How it works

### Two test modes

| Mode | What it does | What it catches |
|------|--------------|-----------------|
| **All-core** (default) | y-cruncher self-pins across **every** core (heavy AVX-512 + large memory). | Load-line / Vdroop and thermal-regime instability. On an error, the failing logical core from y-cruncher's own message is mapped to a physical core. |
| **Single-core** (`--single`) | y-cruncher is confined to **one physical core at a time** via a Windows **Job Object affinity mask**, so that core boosts to its **single-core ceiling**. | Curve Optimizer instability in the **high-boost / light-load** regime that all-core testing hides (see "[My experience](#my-experience-author-notes)"). The core under test is known because *we* pinned it, so any error or freeze is blamed on it. |

Single-core confinement is done purely by a **Job Object affinity limit** (plus a
process-affinity hint). y-cruncher's `stress` command has no per-core option and
rejects engine flags, so the launcher is assigned to the job **before** it spawns
its architecture child binary — the child inherits the affinity and stays
confined.

### The detectors

1. **y-cruncher self-check** — y-cruncher verifies its own math; a mismatch is a
   real computation error. The tool reads *which logical core* it names and maps
   it to a physical core via the **actual CPU topology mask** (not a `÷2` guess).
2. **Micro-freeze monitor** (on by default) — a high-priority thread wakes every
   ~1 ms and measures how long it *actually* slept. A sudden e.g. 30 ms gap means
   the system failed to schedule **anything** for 30 ms = a stall/stutter the user
   feels. In single-core mode this is blamed on the pinned core; in all-core mode
   it is informational. Tune with `--hitch-ms N`, disable with `--no-hitch`.
3. **Sensor-free slowdown detection** — a completed run is time-bounded, so its
   wall-clock time should be near-constant. If a run takes noticeably longer than
   the running average, the machine spent time **not executing** (clock
   stretching / throttling / stalls) — recorded as a `SLOWDOWN` event. No sensors
   required.
4. **Reboot-surviving crash breadcrumb** — every second the tool overwrites a tiny
   `lastalive.txt` with the timestamp and the core currently under test. If an
   uncorrectable error reboots the machine instantly, after reboot that file holds
   the state from ~1 second before death = **the core that was running when it
   died**. On the next launch the tool reads it and flags the prime suspect.
5. **Permanent CSV log** — every event is appended to
   `dist/logs/rynez_<timestamp>.csv` (timestamp, source, core, detail, …). The
   Windows Event Log rotates and gets purged; this file stays.

### Safety / cleanup

- A **kill-on-close Job Object** ensures y-cruncher dies if the monitor window is
  closed (X), or on logoff/shutdown — no orphaned full-load process.
- The run **stops on the first problem** by default (`--stop-on N`) and prints a
  per-core **instability scoreboard** plus a final summary naming the suspect
  core.

---

## Requirements

- **Windows** (x64), with the **.NET 8 Desktop Runtime** to run the prebuilt
  binary, or the **.NET 8 SDK** to build it yourself.
- An **AMD Ryzen** (the workflow is written around Curve Optimizer / PBO, but the
  stress + per-core attribution is generally useful).
- **y-cruncher** — **you must supply it yourself** (see below). Run as
  **Administrator** (the included `.bat` launchers request elevation; processor
  affinity / the monitor need it).

### y-cruncher is not included

This tool does **not** bundle y-cruncher — it has its own license and is not
redistributed here. Download it yourself from the official site and drop it into
`dist/tools/`:

1. Get y-cruncher: **http://www.numberworld.org/y-cruncher/**
   Use the **latest version** — that is the version this tool was developed and
   tested against.
2. Copy its contents into `dist/tools/` so that **`dist/tools/y-cruncher.exe`**
   exists (see [`dist/tools/README.md`](dist/tools/README.md)).

The monitor refuses to start (with a download hint) if `y-cruncher.exe` is
missing.

---

## Build

```sh
cd src/ycruncher-monitor
dotnet publish ycruncher-monitor.csproj -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o ../../dist
```

This produces `dist/ycruncher-monitor.exe`, which the `.bat` launchers expect.
For a no-runtime-needed build, add `--self-contained true` (larger output).

## Run

The easiest way is the two batch files (they auto-request Administrator):

- **`y-cruncher-monitor (all-core).bat`** — all-core diagnosis.
- **`core-cycler (single-core).bat`** — single-core (high-boost) diagnosis.

Or run the executable directly:

```sh
dist\ycruncher-monitor.exe                       # all-core, loop until you stop / first error
dist\ycruncher-monitor.exe --single              # single-core sweep over every core
```

### Options

| Option | Default | Meaning |
|--------|---------|---------|
| `--single` | off | Single-core mode: pin one core at a time (high-boost CO testing). |
| `--seconds N` | `120` | Seconds per individual test (internally capped to 60 s/test). One run = a full pass of every test. |
| `--cycles N` | `0` | Passes (all-core: number of runs; single: number of full sweeps over every core). `0` = infinite. |
| `--stop-on N` | `1` | Stop after N problem events. `0` = never stop. |
| `--yc-tests "BKT FFTv4 N63 VT3"` | as shown | y-cruncher tests tuned for Curve Optimizer hunting (see below). |
| `--yc-mem 1.2G` | auto | Memory size for y-cruncher. Empty = let it auto-size (good for all-core). |
| `--no-hitch` | off | Turn the micro-freeze monitor off. |
| `--hitch-ms N` | `15` | Micro-freeze threshold in milliseconds. |

**Default test selection** (`BKT FFTv4 N63 VT3`) is chosen to expose CO problems
from several angles: `BKT` is the lightest (→ highest boost, exposes too-aggressive
CO at high frequency), `FFTv4` is the heaviest AVX-512 (→ max current/heat, load
Vdroop), `N63` is an NTT integer path (→ silent errors FFT misses), `VT3` is
memory-coupled. Valid tokens: `BKT BBP SFTv4 SNT SVT FFTv4 NTT63 N63 VSTv3 VT3`.

### Output & logs (in `dist/logs/`)

- `rynez_<timestamp>.csv` — the permanent, append-only event log.
- `lastalive.txt` — the 1-second crash breadcrumb (read on the next launch).

Core numbers are **0-based, OS order**. Cross-check them against Ryzen Master /
BIOS before changing a per-core Curve Optimizer value.

---

## My experience (author notes)

This is what the tool was built to chase, in case it helps you read your own
results:

- I started at **CO −30** and ran **all-core** mode, bumping the suspect core's
  offset **+1 at a time**.
- All-core ran **10+ minutes with no error** — looked stable. But switching to
  **single-core** mode, it **caught an error**.
- My read: in all-core, per-core **power limits**, **thermal contention** and
  **power/current contention** keep clocks from boosting high, so the instability
  that **only shows up at high single-core boost** stays hidden. Single-core mode
  lets one core boost to its ceiling and surfaces it.
- Earlier, on a **tuned Windows + RAM overclock**, I couldn't tell *what* was
  actually at fault — raising the CO value still produced errors.
- Now on a **clean/stock Windows with no overclock**, things seem **much more
  stable at lower CO values**.
- Caveat: I haven't yet done real-world light-load usage (e.g. lighter games), so
  **real-world stability is still unproven** — a stress pass is a strong signal,
  not a guarantee.

Takeaway: **don't trust an all-core "pass" alone.** Re-test in single-core mode,
and isolate variables (overclock vs. RAM OC vs. CO) one at a time.

---

## Disclaimer

**Use this software, and change any overclocking / undervolting setting, entirely
at your own risk.**

- Overclocking and undervolting — including AMD **Curve Optimizer / PBO** and
  **memory overclocking** — can cause system instability, **data loss**, and
  **permanent hardware damage**, and may **void your warranty**.
- This tool only **reports** which core looked unstable. **Deciding what to change
  in BIOS, and applying it, is 100% your responsibility.** The author does not and
  cannot know your hardware, cooling, or limits.
- The software is provided **"as is"**, without warranty of any kind. The author
  accepts **no liability** for any damage, data loss, or other harm of any kind
  arising from its use. See [`LICENSE`](LICENSE).
- Always have backups, watch your temperatures and voltages, and make changes in
  small steps.

## License

[MIT](LICENSE) — free to use, modify, and distribute. y-cruncher is **not**
covered by this license and is **not** included; it is a separate program by
Alexander J. Yee with its own license.

# Rynez Stability Monitor

[![Download](https://img.shields.io/github/v/release/exidum79/Rynez-Stability-Monitor?label=Download&logo=github&sort=semver)](https://github.com/exidum79/Rynez-Stability-Monitor/releases/latest)
[![Downloads](https://img.shields.io/github/downloads/exidum79/Rynez-Stability-Monitor/total?logo=github)](https://github.com/exidum79/Rynez-Stability-Monitor/releases)
[![License: MIT](https://img.shields.io/badge/License-MIT-green.svg)](LICENSE)
[![Platform](https://img.shields.io/badge/platform-Windows%20x64-blue)](#requirements)

> **▶ [Download the latest release](https://github.com/exidum79/Rynez-Stability-Monitor/releases/latest)** — grab the **`...-win-x64.zip`** (ready-to-use: exe + launchers + `tools\` folder, no .NET install needed), extract it, then drop in your own y-cruncher (see [Quick start](#quick-start-download)).

A small, focused Windows tool for **diagnosing which CPU core is unstable** when
you are tuning an AMD Ryzen with **Curve Optimizer / PBO** (and similar
overclock / undervolt setups).

It drives [y-cruncher](http://www.numberworld.org/y-cruncher/) as the stress
engine, wraps it with a micro-freeze detector and a reboot-surviving crash
breadcrumb, times every run to spot silent slowdowns, **stops the moment a
problem is found, and points at the offending core** — then writes everything to
a permanent CSV log so the failing core is recorded even when Windows logs
nothing.

> It is a **diagnostic** tool. It tells you *which loaded core* misbehaved — that is
> *where* a fault surfaced, not proof of the root cause. It **cannot** separate RAM
> vs. memory controller vs. CPU core on its own; that needs isolating one variable
> at a time (see [Attribution & limitations](#attribution--limitations-read-this)).
> **⚠️ The micro-freeze detection and the "problem core" result can be
> false-positived by a RAM overclock or an unstable memory controller (IMC). A fault
> is attributable to the CPU core (CO) only when RAM and the IMC are 100% stable** —
> prove memory first, then trust the core label.
> **It does not change any BIOS / Curve Optimizer / voltage setting for you** — what
> you do with that information is your decision (and your risk). See
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

> The single-core mode is the same idea as the well-known
> [**CoreCycler**](https://github.com/sp00n/corecycler) (per-core load to expose
> Curve Optimizer errors at high boost). What this tool adds on top is the
> combined all-core mode, the micro-freeze monitor, the reboot-surviving crash
> breadcrumb, the sensor-free slowdown detection, and the permanent CSV log.

### The detectors

1. **y-cruncher self-check** — y-cruncher verifies its own math; a mismatch is a
   real computation error. The tool reads *which logical core* it names and maps
   it to a physical core via the **actual CPU topology mask** (not a `÷2` guess).
2. **Micro-freeze monitor** (on by default) — a high-priority thread wakes every
   ~1 ms and measures how long it *actually* slept. A sudden e.g. 30 ms gap means
   the system failed to schedule **anything** for 30 ms = a stall/stutter the user
   feels. In single-core mode this is blamed on the pinned core; in all-core mode
   it is informational. Tune with `--hitch-ms N`, disable with `--no-hitch`.
   ⚠️ **A RAM overclock or an unstable memory controller (IMC) can trigger
   micro-freezes too** — the per-core blame here is only valid once memory/IMC is
   proven stable (see [Attribution & limitations](#attribution--limitations-read-this)).
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

## Attribution & limitations (read this)

This tool reports **which loaded core** an error or micro-freeze happened on. That
is **where** it surfaced — not proof of the **root cause**. Be honest about what it
can and cannot tell you:

- **It cannot separate RAM vs. memory controller (IMC) vs. CPU core.** Even in
  single-core mode, the pinned core still runs through the **IMC and RAM**. If your
  memory/IMC is unstable, a single loaded core can error or stall — and the tool
  will label it "core N." The per-core blame (especially the **micro-freeze**
  signal) is **correlation, not a verdict**.
- **The core label is only trustworthy when memory is already proven stable** —
  i.e. when you are tuning **Curve Optimizer alone**. With a **RAM overclock + CO +
  SoC/voltage** all changed at once, a single mixed run **cannot** attribute the
  fault to one component. No tool can.
- **Both signals — the micro-freeze detector and the "problem core N" label — can
  be false-positived by a RAM overclock or an unstable memory controller (IMC).**
  An error or stall on a pinned core may actually be the memory/IMC, not the core.
  **Only when RAM and the IMC are 100% stable can a fault be read as a CPU-core (CO)
  problem.** Prove memory first (step 1 below), then trust the core label.

**The only way to make attribution possible is to change one variable at a time:**

| Step | Setup | Test | If it fails, suspect |
|------|-------|------|----------------------|
| 0 | Full **stock** (DDR JEDEC, CO 0, SoC auto) | quick sanity | platform / hardware |
| 1 | **RAM OC only** (CO off) | a dedicated memory tester (TM5/Karhu/MemTest86) + memory-heavy y-cruncher tests, for hours | **RAM or IMC** |
| 2 | **CO only** (RAM at stock or a proven profile) | this tool, **single-core / `--core`** | **CPU core CO** |
| 3 | **Both combined** | both of the above | an **interaction** (shared SoC/VDDIO, power/thermal budget) |

Within step 1, to tell **RAM** from **IMC** apart: lowering the **memory clock**
(MCLK/FCLK) fixing it points at the **IMC**; loosening **timings** at the same
clock points at the **DRAM**; `VSOC`/`VDDG` helping points at the IMC, `VDD`/`VDDQ`
helping points at the DRAM. A **CO** fault, by contrast, is independent of memory
clock and goes away when you make the offset **less negative**.

Use the test type as a probe: **compute-heavy / low-memory** tests isolate the
**core/CO**; **memory-heavy** tests isolate **RAM/IMC**.

> Bottom line: this tool is most trustworthy for **CO-only** tuning. If a RAM
> overclock is also in play, treat the core label as a hint, not a conclusion, and
> isolate variables first.

---

## Quick start (download)

1. **Download** the **`Rynez-Stability-Monitor-...-win-x64.zip`** from the
   [latest release](https://github.com/exidum79/Rynez-Stability-Monitor/releases/latest)
   and extract it anywhere. You get this layout:
   ```
   Rynez-Stability-Monitor\
     ycruncher-monitor.exe
     y-cruncher-monitor (all-core).bat
     core-cycler (single-core).bat
     core-cycler (pick cores).bat
     tools\            <- put y-cruncher here
   ```
2. **Add y-cruncher** (not bundled — see below) so that `tools\y-cruncher.exe`
   exists.
3. **Run** a launcher (it auto-requests Administrator), e.g. double-click
   `core-cycler (single-core).bat`.

The release exe is **self-contained** — no .NET install is required to run it.

## Requirements

- **Windows** (x64). The released binary is self-contained (**no .NET runtime
  needed**); the **.NET 8 SDK** is only needed to [build](#build) it yourself.
- An **AMD Ryzen** (the workflow is written around Curve Optimizer / PBO, but the
  stress + per-core attribution is generally useful).
- **y-cruncher** — **you must supply it yourself** (see below). Run as
  **Administrator** (the included `.bat` launchers request elevation; processor
  affinity / the monitor need it).

### y-cruncher is not included

This tool does **not** bundle y-cruncher — it has its own license and is not
redistributed here. Download it yourself from the official site:

1. Get y-cruncher: **http://www.numberworld.org/y-cruncher/**
   Use the **latest version** — that is the version this tool was developed and
   tested against.
2. Put its contents in the **`tools`** folder **next to `ycruncher-monitor.exe`**
   so that **`tools\y-cruncher.exe`** exists. (If you built from source instead,
   that folder is `dist/tools/` — see [`dist/tools/README.md`](dist/tools/README.md).)

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

The easiest way is the batch files (they auto-request Administrator):

- **`y-cruncher-monitor (all-core).bat`** — all-core diagnosis.
- **`core-cycler (single-core).bat`** — single-core (high-boost) diagnosis, sweeps every core.
- **`core-cycler (pick cores).bat`** — single-core on **only the core(s) you choose**.
  Open it in Notepad and edit one line near the top:
  ```bat
  set "CORES=0"        rem one core
  set "CORES=0,2,5"    rem several cores (comma, no spaces)
  set "CORES="         rem blank = sweep every core
  ```
  Use this to **soak one suspect core continuously** (e.g. the core that failed
  before) instead of splitting the night across all cores.

Or run the executable directly (path depends on your layout — the package root
for a downloaded release, or `dist\` for a source build):

```sh
ycruncher-monitor.exe                            # all-core, loop until you stop / first error
ycruncher-monitor.exe --single                   # single-core sweep over every core
ycruncher-monitor.exe --core 0                   # single-core on ONLY core 0 (continuous soak)
ycruncher-monitor.exe --cores 0,2,5              # single-core on ONLY cores 0, 2 and 5
```

The `.bat` launchers find `ycruncher-monitor.exe` whether it sits next to them or
in a `dist\` subfolder, so both layouts work.

### Options

| Option | Default | Meaning |
|--------|---------|---------|
| `--single` | off | Single-core mode: pin one core at a time (high-boost CO testing). |
| `--core N` | — | Single-core on **only** physical core N (implies `--single`). Continuous soak of one suspect core. |
| `--cores 0,2,5` | — | Single-core on **only** the listed physical cores (comma-separated; implies `--single`). |
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

### How long to run

Curve Optimizer instability is **intermittent**, so run time matters — but the
two outcomes are not symmetric:

- **A short FAIL is conclusive.** If a core errors or freezes within minutes,
  that core *is* unstable — done. (This is why single-core mode is useful: it
  surfaces a real fault fast by letting one core boost high.)
- **A short PASS is not.** A 10-minute clean run tells you almost nothing. Before
  you *trust* a configuration, run for **hours, ideally overnight**.

The launchers default to `--cycles 0` (loop until an error or you stop), so they
are built for long runs — just leave them going. Use a short pass only to move on
to the next variable, never as a final "stable" verdict.

**Single-core sweep divides your time across cores.** A full sweep tests one core
at a time, so an overnight sweep gives each core only about `total ÷ core count`
of cumulative high-boost time (e.g. 8 h on a 6-core ≈ 80 min per core, in
rotating bursts). That rotation is the accepted CoreCycler-style method and adds
useful boost/idle thermal cycling — but to give **one** core a true continuous
soak, use `--core N` (or `core-cycler (pick cores).bat`) and let just that core
run all night. The complementary **all-core** run instead soaks **every** core
the whole time, in the low-frequency / high-current corner.

### Output & logs (in the `logs/` folder next to the exe)

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

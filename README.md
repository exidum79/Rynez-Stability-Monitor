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
breadcrumb, times every run to spot silent slowdowns, **reads WHEA hardware
errors to tag RAM/IMC vs CPU-core machine checks**, **stops the moment a problem
is found, and points at the offending core** — then writes everything to a
permanent CSV log so the failing core is recorded even when Windows logs nothing.

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

> **🔎 Hardware-level detection (WHEA / MCA):** it also reads the CPU's machine-check
> events from Windows and tags each hardware error **RAM/IMC vs CPU-core vs PCIe** at the
> hardware level (see [detector #5](#how-it-works) and
> [Hardware-level attribution](#hardware-level-attribution-whea--mca)). **But a DRAM/memory
> error is only *reportable* with ECC memory on an ECC-reporting board** — an **ECC UDIMM on
> a supporting AM5 board**, or server **RDIMMs**. Consumer **non-ECC DDR5** has only silent
> on-die ECC and **cannot be tracked**, so the absence of WHEA memory events never clears RAM.

> **🖱️ Micro-freeze false positives — handled for you:** the stutter detector flags **any**
> scheduling stall, so normal activity (app switching, screensavers, display power changes, video
> on a weaker CPU) can look like a hitch. By **default the tool keeps the display on and blocks
> sleep/screensaver for the run** (no power-plan change, reverted on exit — you can still switch the
> monitor off physically), and it **auto-ignores hitches within ~2 s of keyboard/mouse input**. So
> no manual setup each time; just don't run heavy foreground apps/video during the test. Opt out
> with `--allow-sleep`; tune with `--hitch-ms` / `--no-hitch`
> (see [Micro-freeze false positives](#micro-freeze-hitch-false-positives)).

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

## Why one tool can't catch every instability — what the research shows

Large-scale field studies of CPUs land on the same uncomfortable result: a defective
("mercurial") core miscomputes **only under specific conditions** — a particular instruction
sequence, voltage, frequency, or temperature — and is silent the rest of the time. Google's
*"Cores that don't count"* and Meta's *"Silent Data Corruptions at Scale"* both document errors
that one workload triggers and another never does. There is **no single test pattern that
exercises every fault**, because the faults themselves are pattern-dependent.

Two consequences for desktop CO / PBO tuning:

- **No one tool is "enough."** A memory tester, a compute self-checker (y-cruncher), a power-virus
  (Prime95), and a boost-swing / transient probe each cover a **different regime**. Coverage comes
  from **layering** them, not from crowning a winner — which is exactly why forum "tool X vs tool Y"
  fights never resolve: they compare tools that test different things. (The table below maps which
  tool covers which regime.)
- **Clean root-cause needs hardware, not software.** IBM's z990 RAS design got trustworthy
  attribution only via **lockstep dual-execution in silicon**. A software tool on a consumer board
  can observe *which loaded core* misbehaved, but **cannot prove** RAM vs IMC vs core by itself — so
  this tool is deliberately honest about being a *diagnostic*, not a verdict (see
  [Attribution & limitations](#attribution--limitations-read-this)).

On AMD specifically, Curve Optimizer / PBO instability is a **per-core, condition-dependent undervolt
margin** problem (per AMD's own Curve Optimizer / Precision Boost Overdrive documentation) — which is
why this tool hunts **per core** and across multiple regimes (steady single-core, all-core, and
transient boost-swing) instead of with one blanket load.

Full citations are in [References](#references).

---

## Which stress engine — and is y-cruncher "enough"?

The community argument ("**y-cruncher** is best" vs "no, **Prime95**" vs "**AIDA64**
is more realistic") asks the wrong question. These tools are **not competitors that
rank**; they each stress a **different instability regime**, and each catches faults
the others miss. That is *why* people disagree — they are testing different things.

| Engine | What it actually stresses | What it's best at | Blind spot |
|--------|---------------------------|-------------------|------------|
| **y-cruncher** | Heavy AVX-512 + large-memory math **with a built-in result self-check** | **Silent compute errors** — it *proves* the math came out wrong, not just "it crashed." On Zen 4/5 the AVX-512 path also pulls near-max current/heat. The standard for **Curve Optimizer / per-core** hunting. | Not a dedicated memory tester; absolute worst-case power is marginally below Prime95 Small-FFT. |
| **Prime95 (Small FFT)** | A **power-virus** — maximum sustained current and heat | **Thermals, Vdroop, VRM / power-delivery** limits — the hottest, highest-current corner. | Doesn't *verify* results the way y-cruncher does, so a marginal silent error can slip; weak per-core attribution. |
| **AIDA64** | A lighter **combined** CPU+FPU+cache+memory load | A "closer to real-world", run-it-for-hours soak; quick combined sanity. | The **loosest** of the three — marginal CO/undervolt faults often pass it. Not conclusive for tight tuning. |

### Why this tool drives y-cruncher

The failure mode this tool exists to chase — **Curve Optimizer / PBO undervolt
instability** — is most often a **silent wrong calculation**, not a clean crash.
Only y-cruncher **checks its own result every run**, so it is the one engine that
turns "the machine *might* be miscomputing" into a logged, attributable error. On
Zen 4/5 its AVX-512 worker also drives current and heat hard enough to expose load
Vdroop, so for **CO/PBO core hunting it is the right engine, not a compromise.**

So for **what this tool is for, y-cruncher is enough — and arguably the best choice.**
What the tool *adds on top* is the part y-cruncher alone doesn't give you: **per-core
attribution**, the **micro-freeze monitor**, the **reboot-surviving crash breadcrumb**,
**WHEA/MCA** hardware tagging, and the **permanent CSV log** (see [How it works](#how-it-works)).

### Where y-cruncher (and therefore this tool) is *not* enough

Being honest about the gaps — these are real, and they are **regimes y-cruncher was
never meant to own**, not weaknesses of this tool:

- **Deep RAM stability.** y-cruncher's memory-coupled tests pressure the **IMC** well,
  but it is **not a dedicated memory tester**. Pair it with **TM5 / Karhu / MemTest86**
  for real DRAM coverage — and remember consumer **non-ECC DDR5 can't even be tracked
  by WHEA** (see [Attribution & limitations](#attribution--limitations-read-this)).
  This is already called out in the `mem-test` / `full-test` launchers.
- **Absolute worst-case power/thermal.** If your question is specifically "does my VRM /
  cooling survive the hottest possible load," a **Prime95 Small-FFT** pass pushes a touch
  higher than y-cruncher. Use it as a one-off thermal ceiling check.
- **Sub-millisecond transient.** The `--transient` mode adds boost-swing stress, but
  Windows timing is ~0.5–2 ms — not a Linux sub-ms tool (see
  [Transient / boost-cycling mode](#transient--boost-cycling-mode)).

> **Bottom line for the forum debate:** don't pick one — **layer them**. Use a dedicated
> memory tester (TM5/Karhu) to **prove RAM/IMC first**, this tool's **y-cruncher single-core
> mode** to hunt the unstable **Curve Optimizer core**, and (optionally) a short **Prime95
> Small-FFT** run for the thermal/power ceiling. They're not rivals; they cover different
> ground. For the job this tool does — *which core is unstable under CO/PBO* — y-cruncher
> is the correct engine.

---

## How it works

### Test modes

| Mode | What it does | What it catches |
|------|--------------|-----------------|
| **All-core** (default) | y-cruncher self-pins across **every** core (heavy AVX-512 + large memory). | Load-line / Vdroop and thermal-regime instability. On an error, the failing logical core from y-cruncher's own message is mapped to a physical core. |
| **Single-core** (`--single`) | y-cruncher is confined to **one physical core at a time** via a Windows **Job Object affinity mask**, so that core boosts to its **single-core ceiling**. | Curve Optimizer instability in the **high-boost / light-load** regime that all-core testing hides (see "[My experience](#my-experience-author-notes)"). The core under test is known because *we* pinned it, so any error or freeze is blamed on it. |
| **Transient** (`--transient`) | Single-core **plus** the worker is **duty-cycled** — suspended for `--idle-ms`, resumed for `--burst-ms`, repeatedly — so the pinned core **ramps idle→load over and over** instead of holding a steady clock. | Curve Optimizer faults that only show on **rapid boost-clock swings (the transient regime)**, which a steady 100% load never triggers. y-cruncher's own math self-check still runs, so silent compute errors are still caught. **Honest limit:** Windows timer granularity is ~0.5–2 ms (**not** sub-ms), so it adds real transient exposure over steady load but does **not** match a Linux sub-ms tool — treat it as a *complement* to the other two modes (see [Transient / boost-cycling mode](#transient--boost-cycling-mode)). |

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

### Transient / boost-cycling mode

A fair criticism of *any* steady-load core tester (this one included, and CoreCycler):
**pegging a core at a constant 100% holds it at one operating point.** A meaningful
class of Curve Optimizer faults on Zen instead shows up in the **transient** regime —
the rapid **idle→load boost ramps** where the requested clock jumps faster than the
voltage rail settles, so Vmin is momentarily violated. Peak clock matters less than
**how fast the boost clock swings**.

`--transient` targets that. It runs the **single-core** pin (so the core boosts to its
ceiling) **and** duty-cycles the worker on top: a high-priority thread **suspends** the
y-cruncher worker for `--idle-ms` (the core drops its clock), then **resumes** it for
`--burst-ms` (the core ramps back to boost), over and over. Suspend/resume is transparent
to y-cruncher, so its **own math self-check still runs** — a silent compute error is still
caught; you just also get the boost-swing stress a steady load can't produce.

> **Honest limitation — read this.** Windows scheduler/timer granularity is **~0.5–2 ms**,
> **not** sub-millisecond. So this **cannot** reproduce the sub-1 ms boost residency a
> dedicated Linux tool (e.g. *Threadstepper*) can. It is **not** a sub-ms transient tester.
> What it honestly is: **meaningful boost-cycling that exposes more transient behaviour than
> steady load**, within what Windows timing allows. Use it as a **complement** to the steady
> single-core and all-core runs, not a replacement — and if the worker child can't be found
> or suspended, the run simply falls back to steady single-core load (still a valid test).

Tune the duty cycle with `--burst-ms` / `--idle-ms` (defaults `5` / `5`). Lower values pack
in more idle→load swings per second but get jittery as they approach the timer floor; the
idle gap should stay long enough (a few ms) for the core to actually drop its clock between
bursts. Launch it with **`core-cycler (transient boost).bat`**.

> **Needs the Balanced power plan.** The swing only develops if the clock is *allowed to
> drop* during the idle gap. On **High performance** (or any plan with **Minimum processor
> state = 100%**) the core stays pinned near its ceiling, so the suspend/resume cycles do
> almost nothing — there is no idle→boost transition left to stress. Use **Balanced** with
> **Minimum processor state ≈ 5%** so the clock falls in the idle gap and gets yanked back
> to full boost on each burst. This matters most for the **max-shake** launcher below.

For a one-click setup there are two **core-0** transient launchers (no editing needed):
**`core-cycler (core0 transient).bat`** (default `5/5` duty, pinned to core 0) and
**`core-cycler (core0 max-shake).bat`** (`2/3` duty — the idle gap is long enough for the
clock+voltage to genuinely drop, then the short burst yanks it to full boost, giving the
**deepest idle→full-boost swing** that pops CO undervolts). Both require the **Balanced**
plan above to do anything; going below ~1–2 ms is pointless (Windows `Sleep` floors at
~1 ms and the clock/voltage ramp itself takes ~1–2 ms, so shorter just blurs into a
mid-clock instead of a full swing).

#### Real-world random mode (`--random`)

A fixed `burst/idle` metronome holds the core at one *average* load — over Task Manager's
~1 s sample window the hundreds of ms-scale cycles average out, so the utilisation graph
sits at a flat band and looks nothing like real use. **`--random`** fixes that: instead of a
metronome it runs random **phases** — each a random **80–2000 ms** stretch at a random
**0–100 % target load** (delivered by fast ~10 ms micro-duty). Because a phase is on the
order of (or longer than) the sample window, the reported utilisation actually **wanders the
full 0→100 % range like real-world use**: idle stretches (the clock fully drops), full-load
stretches, and partial stretches with fast idle→boost edges. As a bonus, a phase boundary
from a ~0 % idle stretch straight into a ~100 % stretch is the **deepest possible
idle→full-load step**, the hardest Vmin transient of all. It exercises CO faults across many
load levels and transition timings in a single run rather than betting on one fixed swing
(`--burst-ms` / `--idle-ms` are ignored in this mode). Launch it with
**`core-cycler (transient random).bat`** (edit `CORES` to target specific cores, or leave it
blank to sweep all). Same **Balanced** power-plan requirement applies.

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
5. **WHEA hardware-error attribution** (on by default) — reads the CPU's **Machine
   Check Architecture (MCA)** via the Windows event log (`Microsoft-Windows-WHEA-Logger`)
   and classifies each hardware error by domain: **memory/IMC** vs **CPU-core** vs
   **PCIe/IO**. Classification is driven by the **UEFI CPER error record's section-type
   GUIDs** (parsed from the raw event), *not* by hardcoded bank numbers — so it adapts
   to any core / channel / slot count; the MCA bank is only a cosmetic unit hint. This
   is the only signal here that separates RAM/IMC from the core at the **hardware**
   level. Disable with `--no-whea`.
   ⚠️ **Non-ECC DDR5 caveat:** a DRAM error only reaches WHEA when **reportable ECC** is
   active. Consumer DDR5 only has silent **on-die ECC**, so a RAM-overclock fault is
   corrected invisibly or just corrupts/reboots and **never appears here** — *absence of
   WHEA memory events does not clear memory.* Reportable memory attribution needs **ECC
   DIMMs on an ECC-reporting board** (an ECC UDIMM on a supporting AM5 board, or server
   RDIMMs); plain consumer non-ECC kits cannot be tracked.
6. **Permanent CSV log** — every event is appended to
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

### Hardware-level attribution (WHEA / MCA)

The closest thing to *true* separation is reading the CPU's **Machine Check
Architecture** directly. Windows surfaces MCA data through **WHEA**
(`Microsoft-Windows-WHEA-Logger`). This tool reads each event's raw **UEFI CPER**
record and classifies it by the **section-type GUID** (memory / processor / PCIe) —
a vendor- and machine-independent identifier — so the `RAM/IMC` vs `CPU-CORE` vs
`PCIe/IO` tag does **not** depend on hardcoded bank numbers and adapts to any
core / memory-channel / slot count. The MCA bank (e.g. UMC vs LS/L2/L3) is shown only
as a cosmetic unit hint. Events with no CPER blob fall back to the OS-defined event ID.

Two hard limits keep this from being a silver bullet — and they are the industry
consensus, not this tool's shortcomings:

- **Non-ECC DDR5:** MCA only reports a DRAM error when **reportable ECC** is active.
  Consumer DDR5 ships with mandatory **on-die ECC**, but that corrects silently *inside
  the chip* and is never reported to the host — so a RAM-OC fault is fixed invisibly or
  just corrupts/reboots and never reaches WHEA. Reportable attribution needs **ECC DIMMs
  on an ECC-reporting board** (ECC UDIMM on a supporting AM5 board, or server RDIMMs);
  a plain consumer non-ECC kit cannot be tracked.
- **APIC-ID → core mapping** for a processor WHEA event is best-effort; the reliable
  part is the **domain** (memory vs core), not the exact core number.

> Portability note: the WHEA **event IDs, field names, and CPER section GUIDs come from
> the Windows OS / UEFI spec**, so they are the same on every x64 machine and do **not**
> change with your CPU / RAM-bank / PCIe-slot count — only the field *values* do. Because
> classification is now GUID-driven (not bank-number-driven), it works across vendors and
> platforms; the bank number is shown only as a cosmetic hint.

Industry research reaches the same place — without a vendor's internal test structures
you can observe *which* core miscomputed but generally **cannot prove root cause** from
one tool. See [References](#references) for the full list.

### References

The same body of research backs both points made up top — **faults are condition-dependent**
(so no single tool covers them all) and **clean attribution needs hardware**, not a software stress
test:

- Hochschild et al., *“Cores that don't count”*, **HotOS 2021** (Google) — mercurial cores miscompute only under specific conditions.
- Dixit et al., *“Silent Data Corruptions at Scale”*, **2021** (Meta), arXiv:2102.11245 — SDC is workload- and condition-dependent at fleet scale.
- Fair et al., *“RAS of the IBM eServer z990”*, **IBM J. R&D 2004** — lockstep / dual-execution = how clean attribution is actually done, in hardware.
- Intel, *“Data Center Silent Data Errors”* (2024); Inkley & Mishaeli, *“Finding Faulty Components in a Live Fleet Environment”* (Intel, 2024).
- Schroeder, Pinheiro & Weber, *“DRAM Errors in the Wild”*, **SIGMETRICS 2009** (Google field data).
- AMD, **Precision Boost Overdrive & Curve Optimizer** documentation (AMD Ryzen / Ryzen Master technical docs) — defines CO as a **per-core, condition-dependent** voltage-offset margin; the vendor reference for *why* per-core, multi-regime hunting is the right approach (no public AMD SDC paper exists — this is vendor documentation, not a study).

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
     core-cycler (transient boost).bat
     core-cycler (core0 transient).bat
     core-cycler (core0 max-shake).bat
     core-cycler (transient random).bat
     mem-test (RAM-IMC).bat
     full-test (RAM-IMC + CPU-CO).bat
     tools\            <- put y-cruncher here
   ```
2. **Add y-cruncher** (not bundled — separate license). Download the **latest** from
   **http://www.numberworld.org/y-cruncher/** and extract its **WHOLE folder** into
   `tools\`. When done, `tools\` must look like this:
   ```
   tools\
     y-cruncher.exe          <- the launcher
     Binaries\               <- REQUIRED: the worker .exe's (e.g. "24-ZN5 ~ Komari.exe")
     (Custom Formulas\, *.dll, *.txt, ...)   <- everything else from the y-cruncher folder
   ```
   > ⚠️ **The #1 setup mistake: copying only `y-cruncher.exe`.** That file is *just a
   > launcher* — the real stress runs in a **child process inside `tools\Binaries\`**. If
   > you copy only the launcher, the monitor **refuses to start** and tells you the worker
   > binaries are missing. So copy the **entire** y-cruncher folder, not just the one .exe.
   > (Don't worry if `y-cruncher.exe` shows **0%** in Task Manager later — that's normal,
   > the busy process is the `…Binaries\… .exe` child.)
3. **Run** a launcher (it auto-requests Administrator), e.g. double-click
   `core-cycler (single-core).bat`. A correct setup prints `y-cruncher.exe + Binaries\ found.`

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
2. Extract its **whole folder** into **`tools`** (next to `ycruncher-monitor.exe`) so that
   **both `tools\y-cruncher.exe` AND `tools\Binaries\` exist** — `Binaries\` holds the
   per-architecture worker `.exe`s (e.g. `24-ZN5 ~ Komari.exe`) that do the real work.
   **Do not copy only `y-cruncher.exe`** — by itself it is just a launcher and cannot
   stress-test. (If you built from source, that folder is `dist/tools/` — see
   [`dist/tools/README.md`](dist/tools/README.md).)

The monitor refuses to start if `y-cruncher.exe` is missing, **or if `tools\Binaries\` is
missing** (the "you extracted only the launcher" case) — with a clear message either way.

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
- **`core-cycler (transient boost).bat`** — **transient / boost-cycling** single-core:
  duty-cycles the pinned worker so the core ramps **idle→load repeatedly**, exposing CO
  faults that only show on rapid boost swings (steady load misses them). Edit `BURST` /
  `IDLE` (ms) and optionally `CORES` at the top. **Honest limit:** Windows timing is
  ~0.5–2 ms (not sub-ms) — a complement to the steady runs, not a sub-ms tester (see
  [Transient / boost-cycling mode](#transient--boost-cycling-mode)).
- **`core-cycler (core0 transient).bat`** — the same transient mode **hard-pinned to core 0**
  (default `5/5` duty), so you can soak core 0 with no editing.
- **`core-cycler (core0 max-shake).bat`** — core 0 transient tuned for the **biggest clock
  swing** (`2/3` duty: the deepest idle→full-boost transition, the one that pops CO
  undervolts). ⚠️ **Only works on the Balanced power plan** (Minimum processor state ≈ 5%);
  on High performance / min-state 100% the clock never drops in the idle gap, so there is no
  swing to stress (see [Transient / boost-cycling mode](#transient--boost-cycling-mode)).
- **`core-cycler (transient random).bat`** — **real-world random** transient: random
  80–2000 ms phases at random 0–100% load, so the core's utilisation **wanders the full
  0→100% range like real use** (not a flat metronome band) and hits many load levels /
  transition timings in one run. Edit `CORES` at the top (blank = sweep all). Same Balanced
  plan requirement (see [Real-world random mode](#real-world-random-mode---random)).
- **`mem-test (RAM-IMC).bat`** — **RAM / IMC only**: all-core, memory-coupled tests
  with a large memory footprint, looping. Edit `MEM` at the top to most of your free
  RAM for the heaviest memory stress. WHEA tags a memory fault `RAM/IMC`. (Still pair
  with TM5 / Karhu / MemTest86 — y-cruncher is not a dedicated memory tester.)
- **`full-test (RAM-IMC + CPU-CO).bat`** — the **full battery** in one go:
  Phase 1 all-core memory-coupled load (**RAM / IMC** + load vdroop), then Phase 2
  single-core high-boost sweep (**CPU core / CO**). WHEA tags each error RAM/IMC vs
  CPU-core as it runs; the battery **stops on the first detected problem**. Cycles per
  phase are editable at the top. (y-cruncher is not a substitute for a dedicated RAM
  tester — pair with TM5 / Karhu / MemTest86 for deep memory coverage.)

Or run the executable directly (path depends on your layout — the package root
for a downloaded release, or `dist\` for a source build):

```sh
ycruncher-monitor.exe                            # all-core, loop until you stop / first error
ycruncher-monitor.exe --single                   # single-core sweep over every core
ycruncher-monitor.exe --core 0                   # single-core on ONLY core 0 (continuous soak)
ycruncher-monitor.exe --cores 0,2,5              # single-core on ONLY cores 0, 2 and 5
ycruncher-monitor.exe --transient                # transient boost-cycling sweep (idle->load swings)
ycruncher-monitor.exe --transient --core 0 --burst-ms 5 --idle-ms 5   # transient soak of one core
ycruncher-monitor.exe --transient --random       # real-world random load (utilisation wanders 0->100%)
ycruncher-monitor.exe --transient --random --core 0   # real-world random soak of one core
```

The `.bat` launchers find `ycruncher-monitor.exe` whether it sits next to them or
in a `dist\` subfolder, so both layouts work.

### Options

| Option | Default | Meaning |
|--------|---------|---------|
| `--single` | off | Single-core mode: pin one core at a time (high-boost CO testing). |
| `--core N` | — | Single-core on **only** physical core N (implies `--single`). Continuous soak of one suspect core. |
| `--cores 0,2,5` | — | Single-core on **only** the listed physical cores (comma-separated; implies `--single`). |
| `--transient` | off | Transient / boost-cycling: single-core **plus** duty-cycling the worker (suspend/resume) so the core ramps idle→load repeatedly — exposes CO faults that only show on rapid boost swings. Implies `--single`. ~0.5–2 ms granularity (not sub-ms). |
| `--burst-ms N` | `5` | Transient load-burst length (ms) — how long the worker runs each cycle. Ignored with `--random`. |
| `--idle-ms N` | `5` | Transient idle-gap length (ms) — how long the worker is suspended each cycle (lets the core drop its clock). Ignored with `--random`. |
| `--random` | off | Real-world random load (with `--transient`): random 80–2000 ms phases at random 0–100% target load, so utilisation **wanders the full 0→100% range** like real use instead of a flat metronome band. Sweeps many load levels / idle→boost timings in one run. |
| `--seconds N` | `120` | Seconds per individual test (internally capped to 60 s/test). One run = a full pass of every test. |
| `--cycles N` | `0` | Passes (all-core: number of runs; single: number of full sweeps over every core). `0` = infinite. |
| `--stop-on N` | `1` | Stop after N problem events. `0` = never stop. |
| `--yc-tests "BKT FFTv4 N63 VT3"` | as shown | y-cruncher tests tuned for Curve Optimizer hunting (see below). |
| `--yc-mem 1.2G` | auto | Memory size for y-cruncher. Empty = let it auto-size (good for all-core). |
| `--no-hitch` | off | Turn the micro-freeze monitor off. |
| `--hitch-ms N` | `15` | Micro-freeze threshold in milliseconds. |
| `--no-whea` | off | Turn the WHEA hardware-error reader off (it is on by default). |
| `--allow-sleep` | off | Allow system sleep / screensaver during the run. By default the tool keeps the **display on** and blocks sleep/screensaver (no power-plan change, reverted on exit). |

**Default test selection** (`BKT FFTv4 N63 VT3`) is chosen to expose CO problems
from several angles: `BKT` is the lightest (→ highest boost, exposes too-aggressive
CO at high frequency), `FFTv4` is the heaviest AVX-512 (→ max current/heat, load
Vdroop), `N63` is an NTT integer path (→ silent errors FFT misses), `VT3` is
memory-coupled. Valid tokens: `BKT BBP SFTv4 SNT SVT FFTv4 NTT63 N63 VSTv3 VT3`.

### "It printed the start line then nothing happens / y-cruncher.exe is at 0%"

That is normal — it is almost certainly **running**, not stuck:

- **`y-cruncher.exe` is only a launcher** and correctly sits at **~0%**. The real load runs
  in a **child process** named for your CPU (e.g. `24-ZN5 ~ Komari.exe` on Zen 5). Look for
  *that* process in Task Manager → **Details** — it should be pegging the cores.
- Each run is **silent except a progress tick every ~15 s**; a full all-core pass is a few
  minutes (default `4 tests × 60 s = ~240 s`). Wait for the ticks / the result.
- Sanity-check `…/tools/yc_stress.log` — if `Iteration … Total Elapsed Time` keeps growing,
  it is working.
- If the child worker never appears, you likely extracted **only `y-cruncher.exe`**. Extract
  the **whole** y-cruncher folder so `tools\Binaries\` (the per-architecture worker `.exe`s)
  is present.

### Micro-freeze (`[HITCH]`) false positives

The micro-freeze monitor measures scheduling latency, so it flags **any** momentary stall —
including ones caused by normal OS activity, not your CO:

- **App switching, opening a browser, dismissing a screensaver** → a 15–30 ms blip.
- **Display power on/off and animated screensavers** (GPU + compositor work) → larger blips.
- Weaker CPUs/iGPUs hit this more easily (e.g. browser + YouTube) because there is less
  scheduling headroom; a strong CPU may never see it from the same activity.

The tool handles most of this for you:

- **By default it keeps the display on and blocks sleep + the screensaver for the whole run**
  (via `SetThreadExecutionState` — **no power-plan change**, reverted on exit). That removes the
  screensaver / display-off false positives at the source. You can still switch the monitor
  **off physically**. Opt out with `--allow-sleep`.
- It also **ignores any hitch within ~2 s of keyboard/mouse input** (logged as *environmental*,
  never blamed on a core) — so alt-tabbing or touching the PC won't be counted.

So you no longer need to edit your power plan or screensaver settings each time. Remaining advice:

- **Don't run heavy foreground apps / video** during a single-core test on a weak CPU, and don't
  interact with it — let it sit idle under load.
- A real CO fault shows as a hitch while the machine is **left idle under load**, or as a
  y-cruncher error / WHEA event / hard freeze — not a one-off blip when you touch the PC.
- Still too noisy? Raise the threshold: `--hitch-ms 25` (or `30`), or disable it with `--no-hitch`.

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
- My memory is plain consumer DDR5 with only **on-die ECC** (which corrects silently and
  is never reported to WHEA), so a RAM/IMC fault could never be cleanly attributed here.
  Because of that I'm **keeping RAM at stock — no memory overclock.** That takes memory
  out of the equation and keeps the per-core Curve Optimizer result trustworthy. If I ever
  want real RAM/IMC attribution, that needs ECC memory on an ECC-reporting board.

Takeaway: **don't trust an all-core "pass" alone.** Re-test in single-core mode,
and isolate variables (overclock vs. RAM OC vs. CO) one at a time.

---

## What's been added (feedback-driven changelog)

This tool has shipped continuously since **v1.0** — most of it shaped by community
feedback and real-world use (CO testing methodology, setup pitfalls, and false
positives). Newest first:

| Version | What was added | Feedback / reason it exists |
|---------|----------------|-----------------------------|
| **v1.4.0** | **Transient / boost-cycling mode** (`--transient`): pins one core *and* duty-cycles the worker (suspend/resume) so it ramps **idle→load repeatedly**, exposing CO faults that only show on rapid boost swings. | *"A steady 100% load won't find low-load / transient instability."* Correct — so this targets the transient regime directly. Honest about the limit: Windows timing is ~0.5–2 ms (**not** sub-ms), so it's a **complement** to the steady runs, not a Linux sub-ms tool. |
| **v1.3.1** | Keeps the **display on + blocks sleep/screensaver** during a test (no power-plan change, reverted on exit). | Screensaver / display-off were a source of micro-freeze false positives — removed at the source. |
| **v1.3.0** | **Ignores micro-freezes within ~2 s of keyboard/mouse input** (logged as environmental, never blamed on a core). | Normal interaction (alt-tab, dismissing a screensaver) was being flagged as a hitch. |
| **v1.2.3** | Detects a **launcher-only y-cruncher install** and explains the fix. | Users copied only `y-cruncher.exe` (not `Binaries\`) and it looked broken. |
| **v1.2.2** | **Live progress heartbeat** every ~15 s. | Silent runs "looked frozen" (y-cruncher.exe sits at ~0%; the child does the work). |
| **v1.2.1** | Dedicated **RAM/IMC launcher** (`mem-test`). | Make memory-only testing a one-click path, separate from CO testing. |
| **v1.2.0** | **GUID-driven WHEA classification** + the **full-test battery**. | Classify hardware errors by UEFI CPER section-type GUID, not hardcoded bank numbers — adapts to any core / channel / slot count. |
| **v1.1.0** | **WHEA / MCA hardware-error attribution** (RAM/IMC vs CPU-core vs PCIe). | The only signal here that separates memory from core at the **hardware** level. |
| **v1.0.2** | **Per-core targeting** (`--core` / `--cores`). | Soak one suspect core continuously instead of splitting the night across all cores. |
| **v1.0.1** | Launcher path fix. | — |
| **v1.0.0** | Initial release: all-core + single-core (CoreCycler-style) diagnosis, micro-freeze monitor, reboot-surviving crash breadcrumb, sensor-free slowdown detection, permanent CSV log. | — |

> Keeping this honest matters more than looking finished: where a method has a real
> limit (non-ECC DDR5 can't be WHEA-tracked; Windows timing isn't sub-ms; a short
> *pass* proves nothing), that limit is stated in plain text rather than glossed over.

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

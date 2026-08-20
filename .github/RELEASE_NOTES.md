A fix release for problems reported against v2.0.0: a crash on every run, and a dark theme
that left several controls unreadable.

## Fixed

**The application no longer reports a crash on every run.** The engine raises run-state
changes from whichever thread advanced the run, and the window updated its buttons directly
from that callback — but WPF's command plumbing may only be touched from the UI thread. The
ramp-up transition is raised from a fire-and-forget task, so the resulting error was never
observed and resurfaced later as *"A Task's exception(s) were not observed"*, once per run.
The window now marshals engine callbacks onto its own thread, and the engine notifies
listeners one at a time and contains any that throw, so a misbehaving listener can no longer
end an unattended run.

**Dark mode is readable.** Most controls were only re-coloured, which leaves WPF's stock
templates painting their own light-theme chrome underneath — most visibly on grid column
headers and combo box drop-downs, where near-white text landed on a near-white background.
Combo boxes, list and grid rows, column headers, scroll bars, check boxes, sliders, tooltips
and text fields are now fully re-templated, against a palette that defines every colour in
both themes.

**The sliders have been rebuilt** as a rounded track with the travelled portion filled in the
accent colour and a circular thumb that responds to hover and drag, replacing the stock tab
over a sunken groove.

**A virtual user could pin a CPU core.** If operations completed synchronously — which is
what happens when a server refuses connections outright — the user loop never returned to the
scheduler. That starved the timers that end the run and report ramp-up, so a failing target
looked like a hung application. Users now yield periodically.

**Background faults no longer interrupt you.** Unobserved task exceptions were each raised as
a modal dialog. They are reported long after the work was abandoned and tend to repeat, so
they are now written to the log only; dialogs are kept for faults worth acting on.

## Also

The window renders with pixel snapping and display-mode text, and the panels have been given
more consistent spacing.

A new test suite enforces on every push what compiling cannot: that both palettes define the
same keys, that every resource reference resolves, and that the controls whose stock
templates are light-only still carry one. A resource key missing from a theme resolves to
nothing at runtime rather than failing the build, which is precisely how the unreadable
controls arose.

## Downloads

- **DBTickler.exe** — the desktop app. Portable, self-contained, no .NET installation needed.
- **dbtickler-cli.exe** — the command-line tool.

Both are Windows x64. The core library and CLI are platform-neutral if you build from source.

**Full changelog**: https://github.com/jakemorgangit/DBTickler/compare/v2.0.0...v2.0.1

// One question, asked of the browser instead of guessed at: has this player asked for motion to stop?
//
// The stylesheet already answers it for anything that MOVES — every animation in app.css is switched off
// under `prefers-reduced-motion: reduce`. What a stylesheet cannot switch off is TIME. The dungeon crawls
// are sequenced in C# with awaits, so under reduced motion a player would still be made to sit through
// several silent seconds while a rail they cannot see moving fills itself in. This lets those sequences
// skip to the resolved state instead, which is what "reduce" actually asks for.
//
// The C# side (Motion) invokes this by name. There is no compiler between the two, so a rename here breaks
// the interop at runtime rather than at build time — MotionInteropTests pins the name.

export function prefersReducedMotion() {
    return typeof window !== 'undefined'
        && typeof window.matchMedia === 'function'
        && window.matchMedia('(prefers-reduced-motion: reduce)').matches === true;
}

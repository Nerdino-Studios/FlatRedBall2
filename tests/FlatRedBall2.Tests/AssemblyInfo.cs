using Xunit;

// Gum Forms keeps its cursor, keyboard, focused control and roots in statics, and every engine that
// ticks with automation active installs its own cursor and keyboard into them. With xunit's default
// of running collections in parallel, a test in one collection constructing a Gum control can see a
// cursor another collection nulled or swapped mid-frame. Gum's own suite disables parallelism for
// the same reason; this suite completes in a few seconds either way.
[assembly: CollectionBehavior(DisableTestParallelization = true)]

// MewStage-authored; preserved by third_party/tools/vendor.py (never overwritten). See third_party/NOTICE.
// MewVG's renderer contract (INVGRenderer, the paint and vertex structs the GL renderer consumes) is internal:
// the MewStage NanoVG renderer implements it exactly as upstream's MewVG.GL does, through this grant.
using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("MewStage.Ui.Backend")]
[assembly: InternalsVisibleTo("MewStage.Ui.Tests")]

// MewStage-authored; preserved by third_party/tools/vendor.py (never overwritten). See third_party/NOTICE.
// MewUI has no public third-party backend API: a host that implements IWindowBackend / IGraphicsFactory /
// the backend text contract compiles against internals, exactly as upstream's own backends do (AssemblyInfo.cs).
// A separate file instead of a patch to AssemblyInfo.cs, so an upstream change there can never conflict.
using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("MewStage.Ui.Backend")]      // the MewUI backend (the only MewStage library on internals)
[assembly: InternalsVisibleTo("MewStage.Ui.Tests")]        // the backend's host contract, sink and allocation gates
[assembly: InternalsVisibleTo("MewStage.Demo.Tests")]      // the offscreen journeys through the real host

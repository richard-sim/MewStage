// Analyzer suppressions for MewStage.Rendering, each with its reason. Everything else in the project builds under the
// full standard (.editorconfig at the repository root).

using System.Diagnostics.CodeAnalysis;

// The two GPU backends map one device contract onto one native API each. Each is a single class by design (one owner of
// the native device, one registry of resources, one place every handle is translated), so its coupling to the API's type
// surface is the size of that API, not a sign of tangled responsibilities. Splitting it would scatter the handle
// registry across files without removing a single dependency.
[assembly: SuppressMessage("Maintainability", "CA1506:Avoid excessive class coupling", Justification = "A GPU backend maps the device contract onto a native API; its coupling is the API's size.", Scope = "type", Target = "~T:MewStage.Rendering.Backends.Dx12.Dx12Device")]
[assembly: SuppressMessage("Maintainability", "CA1506:Avoid excessive class coupling", Justification = "A GPU backend maps the device contract onto a native API; its coupling is the API's size.", Scope = "type", Target = "~T:MewStage.Rendering.Backends.Vulkan.VulkanDevice")]
[assembly: SuppressMessage("Maintainability", "CA1506:Avoid excessive class coupling", Justification = "Device bring-up touches every create-info structure of the API once, in order.", Scope = "member", Target = "~M:MewStage.Rendering.Backends.Vulkan.VulkanDevice.Init")]
[assembly: SuppressMessage("Maintainability", "CA1502:Avoid excessive complexity", Justification = "Device bring-up is one linear sequence of feature checks; splitting it hides the order they must run in.", Scope = "member", Target = "~M:MewStage.Rendering.Backends.Vulkan.VulkanDevice.Init")]
[assembly: SuppressMessage("Maintainability", "CA1506:Avoid excessive class coupling", Justification = "A graphics pipeline description touches every fixed-function create-info structure once.", Scope = "member", Target = "~M:MewStage.Rendering.Backends.Vulkan.VkRenderPipeline.#ctor(MewStage.Rendering.Backends.Vulkan.VulkanDevice,MewStage.Graphics.RenderPipelineDesc@)")]
[assembly: SuppressMessage("Maintainability", "CA1502:Avoid excessive complexity", Justification = "A graphics pipeline description maps every fixed-function enum of the contract; each branch is one mapping.", Scope = "member", Target = "~M:MewStage.Rendering.Backends.Vulkan.VkRenderPipeline.#ctor(MewStage.Rendering.Backends.Vulkan.VulkanDevice,MewStage.Graphics.RenderPipelineDesc@)")]
[assembly: SuppressMessage("Maintainability", "CA1506:Avoid excessive class coupling", Justification = "The offscreen proof walks the whole Vulkan bring-up, render and readback path in one place on purpose.", Scope = "member", Target = "~M:MewStage.Rendering.Backends.Vulkan.VulkanOffscreen.ClearAndReadback(System.Int32,System.Int32,System.Single,System.Single,System.Single,System.Single)~MewStage.Rendering.Backends.Vulkan.OffscreenImage")]

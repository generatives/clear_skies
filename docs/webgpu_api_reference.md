# Silk.NET.WebGPU 2.23.0 — API Reference (reflected)

Captured by reflecting over the installed assemblies. All handles are raw pointers (`Instance*`, `Device*`, …). Obtain the API with `WebGPU.GetApi()` (static).

## Lifecycle / setup methods (on `WebGPU` instance)

```
static WebGPU GetApi()

Instance* CreateInstance(InstanceDescriptor* descriptor)
void      InstanceRequestAdapter(Instance*, RequestAdapterOptions*, PfnRequestAdapterCallback, void* userdata)
void      AdapterRequestDevice(Adapter*, DeviceDescriptor*, PfnRequestDeviceCallback, void* userdata)
Queue*    DeviceGetQueue(Device*)
Surface*  InstanceCreateSurface(Instance*, SurfaceDescriptor*)
void      DeviceSetUncapturedErrorCallback(Device*, PfnErrorCallback, void* userdata)
```

### Surface from a Silk window (extension)
```
// namespace Silk.NET.WebGPU — extension on INativeWindowSource (IWindow implements it)
Surface* WebGPUSurface.CreateWebGPUSurface(INativeWindowSource view, WebGPU wgpu, Instance* instance)
// usage: Surface* s = window.CreateWebGPUSurface(wgpu, instance);
```

### Callback delegates (use `Pfn*.From(managedDelegate)`)
```
RequestAdapterCallback : void (RequestAdapterStatus status, Adapter* adapter, byte* message, void* userdata)
RequestDeviceCallback  : void (RequestDeviceStatus  status, Device*  device,  byte* message, void* userdata)
ErrorCallback          : void (ErrorType type, byte* message, void* userdata)
// wgpu-native invokes the adapter/device request callbacks SYNCHRONOUSLY during the request call.
```

## Resources / pipeline

```
ShaderModule*     DeviceCreateShaderModule(Device*, ShaderModuleDescriptor*)
Buffer*           DeviceCreateBuffer(Device*, BufferDescriptor*)
void              QueueWriteBuffer(Queue*, Buffer*, ulong bufferOffset, void* data, nuint size)
Texture*          DeviceCreateTexture(Device*, TextureDescriptor*)
TextureView*      TextureCreateView(Texture*, TextureViewDescriptor*)   // pass null desc for default view
BindGroupLayout*  DeviceCreateBindGroupLayout(Device*, BindGroupLayoutDescriptor*)
PipelineLayout*   DeviceCreatePipelineLayout(Device*, PipelineLayoutDescriptor*)
BindGroup*        DeviceCreateBindGroup(Device*, BindGroupDescriptor*)
RenderPipeline*   DeviceCreateRenderPipeline(Device*, RenderPipelineDescriptor*)
```

## Per-frame

```
void          SurfaceConfigure(Surface*, SurfaceConfiguration*)
void          SurfaceGetCurrentTexture(Surface*, SurfaceTexture*)   // out via pointer
void          SurfaceGetCapabilities(Surface*, Adapter*, SurfaceCapabilities*)
CommandEncoder* DeviceCreateCommandEncoder(Device*, CommandEncoderDescriptor*)
RenderPassEncoder* CommandEncoderBeginRenderPass(CommandEncoder*, RenderPassDescriptor*)
void RenderPassEncoderSetPipeline(RenderPassEncoder*, RenderPipeline*)
void RenderPassEncoderSetBindGroup(RenderPassEncoder*, uint groupIndex, BindGroup*, nuint dynamicOffsetCount, uint* dynamicOffsets)
void RenderPassEncoderSetVertexBuffer(RenderPassEncoder*, uint slot, Buffer*, ulong offset, ulong size)
void RenderPassEncoderSetIndexBuffer(RenderPassEncoder*, Buffer*, IndexFormat, ulong offset, ulong size)
void RenderPassEncoderDrawIndexed(RenderPassEncoder*, uint indexCount, uint instanceCount, uint firstIndex, int baseVertex, uint firstInstance)
void RenderPassEncoderEnd(RenderPassEncoder*)
CommandBuffer* CommandEncoderFinish(CommandEncoder*, CommandBufferDescriptor*)
void QueueSubmit(Queue*, nuint commandCount, CommandBuffer** commands)
void SurfacePresent(Surface*)         // in CORE WebGPU (not the WGPU extension)
```

Release functions exist per type: `InstanceRelease`, `TextureRelease`, `TextureViewRelease`,
`CommandEncoderRelease`, `CommandBufferRelease`, `RenderPassEncoderRelease`, etc.

## WGPU extension (`Silk.NET.WebGPU.Extensions.WGPU.Wgpu`, ctor takes `INativeContext`)
```
Bool32 DevicePoll(Device*, Bool32 wait, WrappedSubmissionIndex*)   // present is in core, only poll here
```

## Key structs (field order matters for initialization)

```
InstanceDescriptor      { ChainedStruct* NextInChain }
RequestAdapterOptions   { ChainedStruct* NextInChain; Surface* CompatibleSurface; PowerPreference PowerPreference; BackendType BackendType; Bool32 ForceFallbackAdapter }
DeviceDescriptor        { ChainedStruct* NextInChain; byte* Label; nuint RequiredFeatureCount; FeatureName* RequiredFeatures; RequiredLimits* RequiredLimits; QueueDescriptor DefaultQueue; PfnDeviceLostCallback DeviceLostCallback; void* DeviceLostUserdata }
SurfaceConfiguration    { ChainedStruct* NextInChain; Device* Device; TextureFormat Format; TextureUsage Usage; nuint ViewFormatCount; TextureFormat* ViewFormats; CompositeAlphaMode AlphaMode; uint Width; uint Height; PresentMode PresentMode }
SurfaceTexture          { Texture* Texture; Bool32 Suboptimal; SurfaceGetCurrentTextureStatus Status }
SurfaceCapabilities     { ChainedStructOut* NextInChain; nuint FormatCount; TextureFormat* Formats; nuint PresentModeCount; PresentMode* PresentModes; nuint AlphaModeCount; CompositeAlphaMode* AlphaModes }

ShaderModuleDescriptor      { ChainedStruct* NextInChain; byte* Label; nuint HintCount; ShaderModuleCompilationHint* Hints }
ShaderModuleWGSLDescriptor  { ChainedStruct Chain; byte* Code }   // chain into ShaderModuleDescriptor.NextInChain; Chain.SType = SType.ShaderModuleWgslDescriptor

BufferDescriptor        { ChainedStruct* NextInChain; byte* Label; BufferUsage Usage; ulong Size; Bool32 MappedAtCreation }
TextureDescriptor       { ChainedStruct* NextInChain; byte* Label; TextureUsage Usage; TextureDimension Dimension; Extent3D Size; TextureFormat Format; uint MipLevelCount; uint SampleCount; nuint ViewFormatCount; TextureFormat* ViewFormats }
TextureViewDescriptor   { ...; TextureFormat Format; TextureViewDimension Dimension; uint BaseMipLevel; uint MipLevelCount; uint BaseArrayLayer; uint ArrayLayerCount; TextureAspect Aspect }
Extent3D                { uint Width; uint Height; uint DepthOrArrayLayers }

VertexAttribute         { VertexFormat Format; ulong Offset; uint ShaderLocation }
VertexBufferLayout      { ulong ArrayStride; VertexStepMode StepMode; nuint AttributeCount; VertexAttribute* Attributes }
VertexState             { ...; ShaderModule* Module; byte* EntryPoint; nuint ConstantCount; ConstantEntry* Constants; nuint BufferCount; VertexBufferLayout* Buffers }
FragmentState           { ...; ShaderModule* Module; byte* EntryPoint; ...; nuint TargetCount; ColorTargetState* Targets }
ColorTargetState        { ChainedStruct* NextInChain; TextureFormat Format; BlendState* Blend; ColorWriteMask WriteMask }
PrimitiveState          { ...; PrimitiveTopology Topology; IndexFormat StripIndexFormat; FrontFace FrontFace; CullMode CullMode }
DepthStencilState       { ...; TextureFormat Format; Bool32 DepthWriteEnabled; CompareFunction DepthCompare; StencilFaceState StencilFront/Back; uint StencilReadMask/WriteMask; int DepthBias; float DepthBiasSlopeScale/Clamp }
MultisampleState        { ...; uint Count; uint Mask; Bool32 AlphaToCoverageEnabled }
RenderPipelineDescriptor{ ...; byte* Label; PipelineLayout* Layout; VertexState Vertex; PrimitiveState Primitive; DepthStencilState* DepthStencil; MultisampleState Multisample; FragmentState* Fragment }

BindGroupLayoutEntry    { ...; uint Binding; ShaderStage Visibility; BufferBindingLayout Buffer; Sampler...; Texture...; StorageTexture... }
BufferBindingLayout     { ChainedStruct* NextInChain; BufferBindingType Type; Bool32 HasDynamicOffset; ulong MinBindingSize }
BindGroupLayoutDescriptor { ...; byte* Label; nuint EntryCount; BindGroupLayoutEntry* Entries }
BindGroupEntry          { ...; uint Binding; Buffer* Buffer; ulong Offset; ulong Size; Sampler* Sampler; TextureView* TextureView }
BindGroupDescriptor     { ...; byte* Label; BindGroupLayout* Layout; nuint EntryCount; BindGroupEntry* Entries }
PipelineLayoutDescriptor{ ...; byte* Label; nuint BindGroupLayoutCount; BindGroupLayout** BindGroupLayouts }

RenderPassColorAttachment { ChainedStruct* NextInChain; TextureView* View; uint DepthSlice; TextureView* ResolveTarget; LoadOp LoadOp; StoreOp StoreOp; Color ClearValue }
RenderPassDepthStencilAttachment { TextureView* View; LoadOp DepthLoadOp; StoreOp DepthStoreOp; float DepthClearValue; Bool32 DepthReadOnly; LoadOp StencilLoadOp; StoreOp StencilStoreOp; uint StencilClearValue; Bool32 StencilReadOnly }
RenderPassDescriptor    { ...; byte* Label; nuint ColorAttachmentCount; RenderPassColorAttachment* ColorAttachments; RenderPassDepthStencilAttachment* DepthStencilAttachment; ... }
Color                   { double R, G, B, A }
ChainedStruct           { ChainedStruct* Next; SType SType }
QueueDescriptor         { ChainedStruct* NextInChain; byte* Label }
```

## Enums (relevant members)

```
PresentMode        : Fifo, FifoRelaxed, Immediate, Mailbox
CompositeAlphaMode : Auto, Opaque, Premultiplied, Unpremultiplied, Inherit
TextureUsage       : None, CopySrc, CopyDst, TextureBinding, StorageBinding, RenderAttachment
LoadOp             : Undefined, Clear, Load
StoreOp            : Undefined, Store, Discard
BufferUsage        : None, MapRead, MapWrite, CopySrc, CopyDst, Index, Vertex, Uniform, Storage, Indirect, QueryResolve
VertexFormat       : Float32x2, Float32x3, Float32x4, ...   (use Float32x3)
ShaderStage        : None, Vertex, Fragment, Compute
SType              : ShaderModuleWgslDescriptor, ...
SurfaceGetCurrentTextureStatus : Success, Timeout, Outdated, Lost, OutOfMemory, DeviceLost
RequestAdapterStatus / RequestDeviceStatus : Success, Error, Unknown, ...
BackendType        : Undefined, Null, WebGpu, D3D11, D3D12, Metal, Vulkan, OpenGL, OpenGles
CompareFunction    : Undefined, Never, Less, LessEqual, Greater, GreaterEqual, Equal, NotEqual, Always
PrimitiveTopology  : PointList, LineList, LineStrip, TriangleList, TriangleStrip
CullMode           : None, Front, Back
FrontFace          : Ccw, CW
VertexStepMode     : Vertex, Instance, VertexBufferNotUsed
BufferBindingType  : Undefined, Uniform, Storage, ReadOnlyStorage
ColorWriteMask     : None, Red, Green, Blue, Alpha, All
TextureDimension   : Dimension1D, Dimension2D, Dimension3D    (note: also aliased TextureDimension2D)
IndexFormat        : Undefined, Uint16, Uint32
ErrorType          : NoError, Validation, OutOfMemory, Internal, Unknown, DeviceLost
```

## Implementation notes
- **Window:** create with `GraphicsAPI.None` so Silk doesn't make a GL context; `window.Initialize()` (or use the `Load` event) before `CreateWebGPUSurface`.
- **Strings:** entry points / WGSL code / labels are null-terminated UTF-8 `byte*`. Use `SilkMarshal.StringToPtr(s, NativeStringEncoding.UTF8)` → `(byte*)`, free with `SilkMarshal.Free(ptr)` after the call (or keep alive for the lifetime where required, e.g. WGSL code only needed during CreateShaderModule).
- **Clip space:** WebGPU NDC is **Y-up, depth [0,1]** (D3D-like). Build a `[0,1]`-depth perspective; **no Vulkan Y-flip**.
- **Matrices:** to avoid row/column convention ambiguity, matrices are built column-major (column-vector convention) by hand and uploaded directly; WGSL computes `proj * view * model * vec4(pos,1)`.
- **RenderPassColorAttachment.DepthSlice:** set to `WGPU_DEPTH_SLICE_UNDEFINED` (`uint.MaxValue`) for 2D color attachments.
- **Async:** adapter/device callbacks fire synchronously under wgpu-native, so creation can be written synchronously (no real async needed).

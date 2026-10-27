# gltfpack 1.3 (meshoptimizer)

Official Windows x64 release: https://github.com/zeux/meshoptimizer/releases/tag/v1.3
Binary: gltfpack-windows.zip / gltfpack.exe
SHA256: A082A0DB2332908133100332CAEEDDAA304AFCF5E8094ADC755EBDEA8C720984
Source: https://github.com/zeux/meshoptimizer/tree/v1.3
Build recipe: .github/workflows/release.yml (static MSVC runtime).
Main license: MIT. Upstream also bundles cgltf, fast_obj, sdefl, Basis Universal and libwebp. Original header notices plus Basis Universal Apache-2.0 license and libwebp COPYING/PATENTS accompany this build.

Invoked locally with -slb (border lock), -se 0.002 and -noq. No compression extensions, remote resource loading, or texture re-encoding is requested. Unsupported input is rejected. Pin is checked before execution.

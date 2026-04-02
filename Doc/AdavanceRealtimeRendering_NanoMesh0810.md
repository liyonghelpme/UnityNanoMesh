Hello everyone, I'm Shun from Tencent Games. My topic is seamless rendering on
mobile.

1

We hope this rendering pipeline can simplify developers' work of customizing and
tailoring mesh resources for different platforms, especially mobile, and reduce errors
caused by manual operations. Of course, we also aim to ensure game graphics
quality and interactive experience. It means refined rendering in areas that attract
players' attention and no visual jumps or lags due to dynamic resource loading during
gameplay.

2

Talking alone might be abstract, so let's demonstrate with a test scenario. This is a
rendering scene with 80 million triangles achievable on mobile. We only used high-
precision assets in production, but during rendering, we control the granularity of
clusters based on projection area and distance from the camera at the cluster level,
ensuring optimal GPU resources for suitable effects.

3

However, compared to Desktop and console, mobile mainstream devices lack many
hardware and software features necessary for seamless rendering, like rendering
pipeline limitations, support for mesh shader and bindless features. Bandwidth and
IO impacts on performance are also concerns.In addition, the mobile platform has its
own tiled base GPU architecture.

4

So, how do we achieve seamless cluster rendering under these limitations? We aim
for simplicity and efficiency, simplifying parts heavily reliant on hardware and
bandwidth as much as possible.

5

Before diving into technical details, let's quickly review the background knowledge.
First, Cluster-based Rendering: Occlusion culling is common in games, solving
overdraw issues caused by direct object occlusion. But large objects often get fully
rendered even if only part is visible. By dividing original meshes into small clusters
and culling at the cluster level, GPUs can skip many invalid triangles. With fewer
triangles per cluster, we can reduce vertex index precision, like using 8 bits. We can
also load clusters on demand based on bounding boxes, improving GPU memory
utilization.

6

The second technology is Visbuffer. When rendering fine triangles, Visbuffer provides
higher Quad Utilization than forward and deferred pipelines, with just 32 bits of extra
overhead. John Hable will talk about vis buffer further in this 2024 advances.

7

Thanks to Drobot‘s presentation in Advances, as well as Brian Karis’ talk,and
Wolf.gang Engel‘s vis buffer original deck, also Sebastian’s SIGGRAPH 2015 talk .
we can get more detail about those technologies . And John Hable will talk
about vis buffer further in this 2024 advances

8

After introducing the background technologies, let's move on to the rendering
pipeline architecture. The pipeline has two parts: offline and runtime. The offline
stage imports and generates custom cluster data, while the runtime part includes
streaming, culling, rasterization, and shading.

9

In the offline part, before anything else, we redesign the mesh storage structure.
Instead of traditional multi-level LODs or simple clusters, we split meshes into many
clusters and continuously recombine them into new, coarser-level clusters, iterating
similarly. It's like UE5's Nanite, but we add a merge coefficient function to ensure
controllability of assets and manually generated mesh LODs. This makes multi-level
cluster data smaller, supporting data structures for culling and streaming. For each
cluster, we store its bounding box and normal cone for efficient occlusion and
backface culling. Each cluster has up to 128 triangles, so we can store indices with
just 8 bits, saving space. For non-leaf nodes, we record errors from merging.

10

Next is the streaming and culling stages. Streaming starts with the roughest clusters
from each instance, then loads on demand based on culling pass output.

11

In culling, we let the GPU handle cluster control and selection. Objects are stored in
an instance buffer, clusters in a cluster buffer, and IDs of successfully culled clusters
in a visible cluster buffer. The cluster buffer loads dynamically based on GPU
selection. Culling starts with fast instance-level culling using HZB, then applies various
techniques like frustum culling, occlusion culling, and normal cone-based backface
culling. We use LOD error similar to UE5's Nanite for cluster LOD culling, but we
design a distance curve for LodFactor selection, ensuring enough rendering precision
nearby and rougher triangles far away, mimicking manual mesh LOD factor control.

12

During rasterization, to reduce power consumption and GPU usage, we store
intermediate rendering results in a 32-bit visbuffer. Since all triangles are in clusters,
ideally, one draw call renders them all to the visbuffer. For GPU parallelism, we
categorize objects: type 1 records cluster and triangle IDs in the visbuffer in the
vertex shader, skipping pixel shader computation. Type 2, like skinned meshes,
recalculates vertex positions with bone info in the vertex shader. Type 3, like masked
vegetation, filters visbuffer generation based on masked textures in the pixel shader.
We avoid soft rasterization due to extra scene depth passes, lack of atomic64
support, and higher bandwidth from 64-bit visbuffers. Since we render by clusters,
not instances, 7 bits for triangle indices suffice, leaving space for clusters.

13

Since bindless is not yet supported on mobile devices, to achieve material shading,
we need to render each material separately with a drawcall. Since it's rare for a single
material to cover the entire screen, we divide the screen into many small tiles, and
each material has its own list of tiles. This way, during rendering, we don't need to
render each material across the entire screen. For each tile's rendering, we extract
the materialID from the cluster information in the visbuffer and compare it with the
current shading materialID. Only when they are equal will the material shading
proceed.

14

Rendering deformable meshes is inevitable in games. Take skinned meshes as an
example. During animation, each cluster's bounds keep changing, making pre-
calculated bounds for LOD and culling inaccurate. Real-time vertex-based bounds
calculation is too time-consuming. So, we need special handling for skinned meshes.
The approach is dynamic bound box calculation and culling based on clusters in the
main bone space.

15

Offline, we compute each cluster's main bone, normal cone, and a conservative
bounding box in the main bone space. The main bone is the one with the highest
vertex weight in the cluster. The normal cone is the average normal of all cluster
triangles, with a cone angle covering them all. The bound box covers the cluster's
maximum range in the main bone space across all animations. Runtime, the CPU
sends bone transform data to the GPU. During cluster culling, it reads the bone
transform, transforms the conservative bound box to mesh space, then proceeds
with normal cluster culling. Skinning rasterization follows normal skeletal animation
calculations.

16

Seamless rendering, due to its inherent characteristics, employs special
implementation methods and optimization techniques in lighting calculations. For
instance, when calculating global illumination, we consider whether lightmaps can be
utilized and whether their precision can cover pixel-level triangles. These factors
significantly impact memory and storage. Hence, we've implemented a fully dynamic
GI algorithm.

17

The GI algorithm utilizes a hierarchical voxel-based approach. It works by dividing the
scene into blocks, voxelizing each block, and leveraging these blocks and refined
voxels for rapid raymarching. BrickTexture stores BrickData offset in
BrickMappingAtlas.BrickMappingAtlas stores voxels offset in VisFaceAtlas

18

Here are the default config variables.

19

Here is the Pseudocode how we get lighting data from brick and
visfacelightingatlas.When performing ray tracing, the first step involves quickly
querying brickgroupbitmask to identify which bricks within a group are valid. This
involves a single uint64 sampling operation. If the mask is not zero, it indicates that
the current brick group contains valid bricks, and we can determine which specific
bricks are valid by checking which bits in the mask are set.
After locating the brickindex through the combination of the bitmask and the
sampleposition, it is straightforward to convert this index into bricktexcoord,
allowing us to query the actual bias stored for the brick in the bricktexture.
Subsequently, we utilize both the brickbitmask and the sampleposition to continue
with a rapid ray marching process, which identifies the valid voxelcoord within the
brick. Then, in the brickmappingatlas, we locate the offset of the voxel within the
visfaceatlas, and proceed to read the relevant lightingdata.

20

Here is the Pseudocode how we get valid brick from brickmask.

21

Let's compare the performance between brickgi and lightmap on mobile devices. In
this test, we added over 100 objects, each with a lightmap resolution of 1024x1024,
within a scene area of 512x512 meters. Our findings show that lightmap requires at
least 160MB of memory overhead, whereas brickgi only needs 30MB. Additionally,
while the GPU time for brickgi is slightly higher than that of lightmap, 3ms is still
considered an acceptable latency for most mobile games.

22

This table shows the details of 30MB memory allocated to cover an area within 512
meters. Moreover, high-precision clusters aren't necessary for voxelization
calculations, keeping the overall voxelization cost low.

23

Moreover, high-precision clusters aren't necessary for voxelization calculations,
keeping the overall voxelization cost low. Voxelization inherently has limited
precision, and clusters with lower precision are more easily voxelized through
rasterization

24

Regarding direct lighting calculations, our tests indicate that high-precision clusters
for generating shadow depths aren't essential. Using coarser approximations has
minimal impact on visual quality but significantly enhances performance. For
example, the upper image uses a more detailed rendering requiring over 200
drawcalls, while the lower image achieves similar results with around 100 drawcalls.

25

The upper image here shows the effect with 200 drawcalls, and the lower image
displays the shadowmap with 100 drawcalls. The difference is barely noticeable.
Additionally, one of the most effective methods is shadow caching.

26

Let's look at seamless rendering's performance, comparing effects, framerates,
power consumption, package size, and production efficiency.

27

Quality-wise, we can render 80 million triangles simultaneously, unimaginable on
mobile, especially retail devices. With this many triangles, we maintain stable
framerates and acceptable power consumption. We also save on package size. Plus,
we don't need to manually create LODs; just LOD0 assets suffice.

28

Here's the framerate on a mobile device. Blue and green  lines show the rendering
effect after using seamless mobile rendering. Notice the blue curve, maintaining
around 60fps with minimal fluctuations.

29

Take a look at the detailed performance data on different level mobile device. On
high-end device ,it only take about 3ms gpu time,while on low-end mobile device
which is 5 years ago,it will be about 20ms.

30

We can also take a look at the detailed performance data on high-end mobile device.
In the table above, I've broken down each rendering phase and monitored the
changes in GPU time consumption step by step. If enable instance culling,it will take
0.11 ms, and then turn on cluster culling,it take 0.07ms , and then turn on binning
pass ,it take 0.2 ms ,then rasterization pass which take 0.72ms ,then material classify
pass take 0.92ms ,the last pass is material shading which take 0.87ms. We can
observe that the time consumption for culling is not significant, and in this scenario,
rasterization and material shading is the more time-consuming part. The table below
shows the time consumption of IO Streaming, and we can see that the GPU's IO
performance is also acceptable.

31

We can also take a look at the detailed performance data on low-end mobile device
which is 5 year ago. We can observe that the time consumption for culling is not
significant, and in this scenario,  material shading is the most time-consuming part.

32

This is power consumption over 10 minutes on a mobile device. Seamless rendering
on mobile (blue) performs well. We found that after enabling this, the power
consumption is significantly lower than that of traditional mesh rendering mode.

33

These are our research and validation results, but there's much room for
improvement. This is just the beginning.

34

We'll continue optimizing performance and iterating features based on
advancements in rendering technology and hardware capabilities.such as memory
usage,software vsr with buffer,io optimization,1.5ms is still a bit little heavy. For
features,We are not supporting raytracing now.And with mobile hardware
capabilities development,we will add meshshader and bindless material shading
support.

35

Thank you for your time. Feel free to reach out to me if you have any further
questions.

36


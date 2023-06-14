using System.Collections.Generic;
using Latios.Authoring;
using Latios.Authoring.Systems;
using Latios.Transforms;
using Latios.Unsafe;
using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Entities;
using Unity.Entities.Exposed;
using Unity.Entities.LowLevel.Unsafe;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Jobs;

namespace Latios.Kinemation.Authoring
{
    /// <summary>
    /// Defines a skeleton animation clip as well as its compression settings
    /// </summary>
    public struct SkeletonClipConfig
    {
        /// <summary>
        /// The clip which should be played on the animator and baked into the blob
        /// </summary>
        public UnityObjectRef<AnimationClip> clip;
        /// <summary>
        /// The compression settings used for compressing the clip.
        /// </summary>
        public SkeletonClipCompressionSettings settings;
        /// <summary>
        /// An array of events to be attached to the clip blob. The order will be sorted during blob creation.
        /// An array can be auto-generated by calling AnimationClip.ExtractKinemationClipEvents().
        /// </summary>
        public NativeArray<ClipEvent> events;
    }

    /// <summary>
    /// Compression settings for a skeleton animation clip.
    /// </summary>
    public struct SkeletonClipCompressionSettings
    {
        /// <summary>
        /// Higher levels lead to longer compression time but more compressed clips.
        /// Values range from 0 to 4. Typical default is 2.
        /// </summary>
        public short compressionLevel;
        /// <summary>
        /// The maximum distance a point sampled some distance away from the bone can
        /// deviate from the original authored animation due to lossy compression.
        /// Typical default is one ten-thousandth of a Unity unit.
        /// Warning! This is measured when the character root is animated with initial
        /// local scale of 1f!
        /// </summary>
        public float maxDistanceError;
        /// <summary>
        /// How far away from the bone points are sampled when evaluating distance error.
        /// Defaults to 3% of a Unity unit.
        /// Warning! This is measured when the character root is animated with initial
        /// local scale of 1f!
        /// </summary>
        public float sampledErrorDistanceFromBone;
        /// <summary>
        /// The max uniform scale value error. The underlying compression library requires
        /// the uniform scale values to be compressed first independently currently.
        /// Defaults to 1 / 100_000.
        /// </summary>
        public float maxUniformScaleError;

        /// <summary>
        /// Looping clips must have matching start and end poses.
        /// If the source clip does not have this, setting this value to true can correct clip.
        /// </summary>
        public bool copyFirstKeyAtEnd;

        /// <summary>
        /// Default animation clip compression settings. These provide relative fast compression,
        /// decently small clip sizes, and typically acceptable accuracy.
        /// (The accuracy is way higher than Unity's default animation compression)
        /// </summary>
        public static readonly SkeletonClipCompressionSettings kDefaultSettings = new SkeletonClipCompressionSettings
        {
            compressionLevel             = 2,
            maxDistanceError             = 0.0001f,
            sampledErrorDistanceFromBone = 0.03f,
            maxUniformScaleError         = 0.00001f,
            copyFirstKeyAtEnd            = false
        };
    }

    public static class SkeletonClipSetBlobberAPIExtensions
    {
        /// <summary>
        /// Requests the creation of a SkeletonClipSetBlob Blob Asset
        /// </summary>
        /// <param name="animator">An animator on which to sample the animations (a clone will be temporarily created).
        /// If the animator is not structurally identical to the one used to generate a skeleton
        /// that will play this clip at runtime, results are undefined.</param>
        /// <param name="clips">An array of clips along with their events and compression settings which should be compressed into the blob asset.
        /// This array can be temp-allocated.</param>
        public static SmartBlobberHandle<SkeletonClipSetBlob> RequestCreateBlobAsset(this IBaker baker, Animator animator, NativeArray<SkeletonClipConfig> clips)
        {
            return baker.RequestCreateBlobAsset<SkeletonClipSetBlob, SkeletonClipSetBakeData>(new SkeletonClipSetBakeData
            {
                animator = animator,
                clips    = clips
            });
        }
    }
    /// <summary>
    /// Input for the SkeletonClipSetBlob Smart Blobber
    /// </summary>
    public struct SkeletonClipSetBakeData : ISmartBlobberRequestFilter<SkeletonClipSetBlob>
    {
        /// <summary>
        /// The UnityEngine.Animator that should sample this clip.
        /// The converted clip will only work correctly with that GameObject's converted skeleton entity
        /// or another skeleton entity with an identical hierarchy.
        /// </summary>
        public Animator animator;
        /// <summary>
        /// The list of clips and their compression settings which should be baked into the clip set.
        /// </summary>
        public NativeArray<SkeletonClipConfig> clips;

        public bool Filter(IBaker baker, Entity blobBakingEntity)
        {
            if (animator == null)
            {
                Debug.LogError($"Kinemation failed to bake clip set on animator {animator.gameObject.name}. The Animator was null.");
                return false;
            }
            if (!clips.IsCreated)
            {
                Debug.LogError($"Kinemation failed to bake clip set on animator {animator.gameObject.name}. The clips array was not allocated.");
                return false;
            }

            int i = 0;
            foreach (var clip in clips)
            {
                if (clip.clip.GetHashCode() == 0)
                {
                    Debug.LogError($"Kinemation failed to bake clip set on animator {animator.gameObject.name}. Clip at index {i} was null.");
                }
                i++;
            }

            baker.DependsOn(animator.avatar);
            baker.AddComponent(blobBakingEntity, new ShadowHierarchyRequest
            {
                animatorToBuildShadowFor = animator
            });
            var clipEventsBuffer = baker.AddBuffer<ClipEventToBake>(blobBakingEntity).Reinterpret<ClipEvent>();
            var clipsBuffer      = baker.AddBuffer<SkeletonClipToBake>(blobBakingEntity);
            baker.AddBuffer<SampledBoneTransform>(          blobBakingEntity);
            baker.AddBuffer<SkeletonClipSetBoneParentIndex>(blobBakingEntity);
            foreach (var clip in clips)
            {
                var clipValue = clip.clip.Value;
                baker.DependsOn(clipValue);
                clipsBuffer.Add(new SkeletonClipToBake
                {
                    clip        = clip.clip,
                    settings    = clip.settings,
                    eventsStart = clipEventsBuffer.Length,
                    eventsCount = clip.events.Length,
                    clipName    = clipValue.name,
                    sampleRate  = clipValue.frameRate
                });
                if (clip.events.Length > 0)
                    clipEventsBuffer.AddRange(clip.events);
            }

            return true;
        }
    }

    [TemporaryBakingType]
    internal struct SampledBoneTransform : IBufferElementData
    {
        public TransformQvvs boneTransform;
    }

    [TemporaryBakingType]
    internal struct SkeletonClipSetBoneParentIndex : IBufferElementData
    {
        public int parentIndex;
    }

    [TemporaryBakingType]
    internal struct SkeletonClipToBake : IBufferElementData
    {
        public UnityObjectRef<AnimationClip>   clip;
        public FixedString128Bytes             clipName;
        public float                           sampleRate;
        public SkeletonClipCompressionSettings settings;
        public int                             eventsStart;
        public int                             eventsCount;
        public int                             boneTransformStart;
        public int                             boneTransformCount;
    }
}

namespace Latios.Kinemation.Authoring.Systems
{
    [RequireMatchingQueriesForUpdate]
    [DisableAutoCreation]
    public partial class SkeletonClipSetSmartBlobberSystem : SystemBase
    {
        Queue<(Transform, int)> m_breadthQueue;
        List<Transform>         m_transformsCache;

        protected override void OnCreate()
        {
            new SmartBlobberTools<SkeletonClipSetBlob>().Register(World);
        }

        protected override void OnUpdate()
        {
            if (m_breadthQueue == null)
                m_breadthQueue = new Queue<(Transform, int)>();
            if (m_transformsCache == null)
                m_transformsCache = new List<Transform>();

            Entities.ForEach((ref DynamicBuffer<SkeletonClipSetBoneParentIndex> parentIndices,
                              ref DynamicBuffer<SampledBoneTransform> sampledBoneTransforms,
                              ref DynamicBuffer<SkeletonClipToBake> clipsToBake,
                              in ShadowHierarchyReference shadowRef) =>
            {
                var shadow = shadowRef.shadowHierarchyRoot.Value;
                var taa    = FetchParentsAndTransformAccessArray(ref parentIndices, shadow);

                for (int i = 0; i < clipsToBake.Length; i++)
                {
                    ref var clip            = ref clipsToBake.ElementAt(i);
                    var     startAndCount   = SampleClip(ref sampledBoneTransforms, taa, clip.clip, clip.settings.copyFirstKeyAtEnd);
                    clip.boneTransformStart = startAndCount.x;
                    clip.boneTransformCount = startAndCount.y;
                }

                taa.Dispose();
            }).WithEntityQueryOptions(EntityQueryOptions.IncludePrefab | EntityQueryOptions.IncludeDisabledEntities).WithoutBurst().Run();

            m_breadthQueue.Clear();
            m_transformsCache.Clear();

            new CompressClipsAndBuildBlobJob().ScheduleParallel();
        }

        // Unlike in 0.5, this version assumes the breadth-first layout skeleton that the shadow hierarchy builds matches the runtime skeleton layout.
        TransformAccessArray FetchParentsAndTransformAccessArray(ref DynamicBuffer<SkeletonClipSetBoneParentIndex> parentIndices, GameObject shadow)
        {
            m_breadthQueue.Clear();
            m_transformsCache.Clear();

            m_breadthQueue.Enqueue((shadow.transform, -1));

            while (m_breadthQueue.Count > 0)
            {
                var (bone, parentIndex)                                            = m_breadthQueue.Dequeue();
                int currentIndex                                                   = parentIndices.Length;
                parentIndices.Add(new SkeletonClipSetBoneParentIndex { parentIndex = parentIndex });
                m_transformsCache.Add(bone);

                for (int i = 0; i < bone.childCount; i++)
                {
                    var child = bone.GetChild(i);
                    m_breadthQueue.Enqueue((child, currentIndex));
                }
            }

            var taa = new TransformAccessArray(m_transformsCache.Count);
            foreach (var tf in m_transformsCache)
                taa.Add(tf);
            return taa;
        }

        int2 SampleClip(ref DynamicBuffer<SampledBoneTransform> appendNewSamplesToThis, TransformAccessArray shadowHierarchy, AnimationClip clip, bool copyFirstPose)
        {
            int requiredSamples    = Mathf.CeilToInt(clip.frameRate * clip.length) + (copyFirstPose ? 1 : 0);
            int requiredTransforms = requiredSamples * shadowHierarchy.length;
            int startIndex         = appendNewSamplesToThis.Length;
            appendNewSamplesToThis.ResizeUninitialized(requiredTransforms + appendNewSamplesToThis.Length);

            var boneTransforms = appendNewSamplesToThis.Reinterpret<TransformQvvs>().AsNativeArray().GetSubArray(startIndex, requiredTransforms);

            var oldWrapMode = clip.wrapMode;
            clip.wrapMode   = WrapMode.Clamp;
            var   root      = shadowHierarchy[0].gameObject;
            float timestep  = math.rcp(clip.frameRate);
            var   job       = new CaptureBoneSamplesJob
            {
                boneTransforms = boneTransforms,
                samplesPerBone = requiredSamples,
                currentSample  = 0
            };

            if (copyFirstPose)
                requiredSamples--;

            for (int i = 0; i < requiredSamples; i++)
            {
                clip.SampleAnimation(root, timestep * i);
                job.currentSample = i;
                job.RunReadOnly(shadowHierarchy);
            }

            if (copyFirstPose)
            {
                clip.SampleAnimation(root, 0f);
                job.currentSample = requiredSamples;
                job.RunReadOnly(shadowHierarchy);
            }

            clip.wrapMode = oldWrapMode;

            return new int2(startIndex, requiredTransforms);
        }

        [BurstCompile]
        partial struct CaptureBoneSamplesJob : IJobParallelForTransform
        {
            [NativeDisableParallelForRestriction]  // Why is this necessary when we are using RunReadOnly()?
            public NativeArray<TransformQvvs> boneTransforms;
            public int                        samplesPerBone;
            public int                        currentSample;

            public void Execute(int index, TransformAccess transform)
            {
                int target             = index * samplesPerBone + currentSample;
                boneTransforms[target] = new TransformQvvs(transform.localPosition, transform.localRotation, 1f, transform.localScale);
            }
        }

        [WithOptions(EntityQueryOptions.IncludeDisabledEntities | EntityQueryOptions.IncludePrefab)]
        [BurstCompile]
        partial struct CompressClipsAndBuildBlobJob : IJobEntity
        {
            public unsafe void Execute(ref SmartBlobberResult result,
                                       ref DynamicBuffer<ClipEventToBake>               clipEventsBuffer,  // Ref so it can be sorted
                                       in DynamicBuffer<SkeletonClipToBake>             clipsBuffer,
                                       in DynamicBuffer<SkeletonClipSetBoneParentIndex> parentsBuffer,
                                       in DynamicBuffer<SampledBoneTransform>           boneSamplesBuffer)
            {
                var parents = parentsBuffer.Reinterpret<int>().AsNativeArray();

                var     builder = new BlobBuilder(Allocator.Temp);
                ref var root    = ref builder.ConstructRoot<SkeletonClipSetBlob>();
                root.boneCount  = (short)parents.Length;
                var blobClips   = builder.Allocate(ref root.clips, clipsBuffer.Length);

                // Step 1: Patch parent hierarchy for ACL
                var parentIndices = new NativeArray<short>(parents.Length, Allocator.Temp);
                for (short i = 0; i < parents.Length; i++)
                {
                    short index = (short)parents[i];
                    if (index < 0)
                        index        = i;
                    parentIndices[i] = index;
                }

                int clipIndex = 0;
                foreach (var srcClip in clipsBuffer)
                {
                    // Step 2: Convert settings
                    var aclSettings = new AclUnity.Compression.SkeletonCompressionSettings
                    {
                        compressionLevel             = srcClip.settings.compressionLevel,
                        maxDistanceError             = srcClip.settings.maxDistanceError,
                        maxUniformScaleError         = srcClip.settings.maxUniformScaleError,
                        sampledErrorDistanceFromBone = srcClip.settings.sampledErrorDistanceFromBone
                    };

                    // Step 3: Encode bone samples into QVV array
                    var qvvArray = boneSamplesBuffer.Reinterpret<AclUnity.Qvvs>().AsNativeArray().GetSubArray(srcClip.boneTransformStart, srcClip.boneTransformCount);

                    // Step 4: Compress
                    var compressedClip = AclUnity.Compression.CompressSkeletonClip(parentIndices, qvvArray, srcClip.sampleRate, aclSettings);

                    // Step 5: Build blob clip
                    blobClips[clipIndex]      = default;
                    blobClips[clipIndex].name = srcClip.clipName;
                    var events                = clipEventsBuffer.Reinterpret<ClipEvent>().AsNativeArray().GetSubArray(srcClip.eventsStart, srcClip.eventsCount);
                    ClipEventsBlobHelpers.Convert(ref blobClips[clipIndex].events, ref builder, events);

                    var compressedData = builder.Allocate(ref blobClips[clipIndex].compressedClipDataAligned16, compressedClip.sizeInBytes, 16);
                    compressedClip.CopyTo((byte*)compressedData.GetUnsafePtr());

                    // Step 6: Dispose ACL memory and safety
                    compressedClip.Dispose();

                    clipIndex++;
                }

                result.blob = UnsafeUntypedBlobAssetReference.Create(builder.CreateBlobAssetReference<SkeletonClipSetBlob>(Allocator.Persistent));
            }
        }
    }
}


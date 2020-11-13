﻿using Unity.Mathematics;

//Todo: Metadata Ids which are collider specific? Use case: compound and mesh colliders for querying UVs or primitive index.
//Todo: Do we need distance in the distance queries? Should we have them for Raycasts?
namespace Latios.PhysicsEngine
{
    //In World Space
    public struct PointDistanceResult
    {
        public float3 hitpoint;
        public float  distance;
        public float3 normal;
    }

    public struct ColliderDistanceResult
    {
        public float3 hitpointA;
        public float3 hitpointB;
        public float3 normalA;
        public float3 normalB;
        public float  distance;
        public int    subColliderIndexA;
        public int    subColliderIndexB;
    }

    public struct RaycastResult
    {
        public float3 position;
        public float  distance;
        public float3 normal;
    }
}


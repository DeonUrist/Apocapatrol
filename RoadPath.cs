using UnityEngine;
using System;
using System.Runtime.CompilerServices;

namespace Apocapatrol
{
    internal static class RoadPath
    {
        private sealed class Metrics
        {
            internal readonly float[] Distance, Corner;
            internal Metrics(Vector3[] route)
            {
                Distance = new float[route.Length]; Corner = new float[route.Length];
                for (int i = 1; i < route.Length; i++) Distance[i] = Distance[i-1] + TrafficLocations.FlatDistance(route[i-1],route[i]);
                for (int i = 0; i < route.Length; i++) Corner[i] = float.PositiveInfinity;
                for (int i = 1; i < route.Length-1; i++)
                {
                    var a=route[i]-route[i-1];var b=route[i+1]-route[i];a.y=b.y=0f;
                    float angle=Vector3.Angle(a,b)*Mathf.Deg2Rad;
                    if(angle<0.03f)continue;
                    float radius=Mathf.Min(a.magnitude,b.magnitude)/Mathf.Max(0.02f,2f*Mathf.Sin(angle*0.5f));
                    Corner[i]=Mathf.Max(3f,Mathf.Sqrt(radius*2.5f));
                }
            }
        }
        private static readonly ConditionalWeakTable<Vector3[], Metrics> Cache = new ConditionalWeakTable<Vector3[], Metrics>();
        private static readonly ConditionalWeakTable<Vector3[], Metrics>.CreateValueCallback Create = r => new Metrics(r);
        internal static void Prepare(Vector3[] route) { Cache.GetValue(route,Create); }

        internal static float Progress(Vector3[] route, int leg, Vector3 actual)
        {
            var metrics=Cache.GetValue(route,Create);
            float best = float.MaxValue, progress = 0f;
            for (int i = Mathf.Max(1,leg-64); i < Mathf.Min(route.Length,leg+7); i++)
            {
                var d = route[i] - route[i-1]; d.y = 0f; float length = d.magnitude;
                    float t = length > 0.001f ? Mathf.Clamp01(Vector3.Dot(actual - route[i-1], d) / (length * length)) : 0f;
                    float distance = TrafficLocations.FlatDistance(actual, Vector3.Lerp(route[i-1], route[i], t));
                    if (distance < best) { best = distance; progress = metrics.Distance[i-1] + length * t; }
            }
            return progress;
        }
        internal static Vector3 Point(Vector3[] route, float distance)
        {
            if (route.Length == 0) return Vector3.zero;
            if (distance < 0f && route.Length > 1)
            { var d = route[1] - route[0]; d.y = 0f; return route[0] + d.normalized * distance; }
            var metrics=Cache.GetValue(route,Create);
            int index=Array.BinarySearch(metrics.Distance,distance);
            if(index>=0)return route[index]; index=~index;
            if(index>=route.Length)return route[route.Length-1];
            float length=metrics.Distance[index]-metrics.Distance[index-1];
            return Vector3.Lerp(route[index-1],route[index],length>0.001f?(distance-metrics.Distance[index-1])/length:0f);
        }
        internal static Vector3 Target(Vector3[] route, int leg, Vector3 actual, float speed)
        {
            float ahead = Mathf.Clamp(6f + speed * 0.45f, 6f, 16f);
            // Reduce the chord through a sharp bend so the vehicle follows the road instead of cutting the corner.
            float safe = Speed(route, leg, actual, speed);
            ahead = Mathf.Min(ahead, 6f + safe * 0.45f);
            return Point(route, Progress(route, leg, actual) + ahead);
        }
        internal static float Speed(Vector3[] route, int leg, Vector3 actual, float cruise)
        {
            if (route.Length < 3) return cruise;
            var metrics=Cache.GetValue(route,Create);
            float progress = Progress(route, leg, actual), result = cruise;
            int start=Array.BinarySearch(metrics.Distance,progress-2f);if(start<0)start=~start;
            for (int i = Mathf.Max(1,start); i < route.Length - 1; i++)
            {
                float distance=metrics.Distance[i]-progress;
                if(distance>cruise*3f+20f)break;
                float corner=metrics.Corner[i];
                result = Mathf.Min(result, Mathf.Sqrt(corner * corner + 6f * Mathf.Max(0f, distance)));
            }
            return result;
        }
    }
}

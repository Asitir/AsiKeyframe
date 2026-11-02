using UnityEngine;

namespace AsitirTool.Editor
{
    /// <summary>外形几何。外形空间：+Z 沿骨骼，XY 为截面，offset 为整体平移。</summary>
    public static class AsiKeyShapes
    {
        public const int RingSegments = 48;

        public static readonly int[][] BoxFaces =
        {
            new[] { 0, 1, 3, 2 },
            new[] { 4, 5, 7, 6 },
            new[] { 0, 2, 6, 4 },
            new[] { 1, 3, 7, 5 },
            new[] { 0, 1, 5, 4 },
            new[] { 2, 3, 7, 6 },
        };

        public static readonly int[] BoxEdges = { 0, 1, 2, 3, 4, 5, 6, 7, 0, 2, 1, 3, 4, 6, 5, 7, 0, 4, 1, 5, 2, 6, 3, 7 };

        /// <summary>八面体：0 为关节，1–4 为腰部一圈，5 为指向子关节的尖端。</summary>
        public static readonly int[][] OctaFaces =
        {
            new[] { 0, 1, 2 }, new[] { 0, 2, 3 }, new[] { 0, 3, 4 }, new[] { 0, 4, 1 },
            new[] { 5, 2, 1 }, new[] { 5, 3, 2 }, new[] { 5, 4, 3 }, new[] { 5, 1, 4 },
        };

        public static readonly int[] OctaEdges = { 0, 1, 0, 2, 0, 3, 0, 4, 5, 1, 5, 2, 5, 3, 5, 4, 1, 2, 2, 3, 3, 4, 4, 1 };

        public static Matrix4x4 ShapeMatrix(AsiKeyBone b, Transform control, float scale)
        {
            Matrix4x4 c = Matrix4x4.TRS(control.position, control.rotation, control.localScale);
            Quaternion r = b.frame * Quaternion.Euler(b.shape.euler);
            return c * Matrix4x4.TRS(Vector3.zero, r, Vector3.one * Mathf.Max(1e-4f, scale));
        }

        /// <summary>骨头样式（八面体、棍状）的坐标系：关节在原点，+Z 沿骨骼指向子关节，不带外形的偏移、旋转和缩放。</summary>
        public static Matrix4x4 BoneMatrix(AsiKeyBone b, Transform control)
        {
            Matrix4x4 c = Matrix4x4.TRS(control.position, control.rotation, control.localScale);
            return c * Matrix4x4.TRS(Vector3.zero, b.frame, Vector3.one);
        }

        /// <summary>八面体腰部在骨长 10% 处，半宽为骨长的 10%（与 Blender 一致），外形大小只放大宽度。</summary>
        public static void OctaCorners(AsiKeyBone b, float scale, Vector3[] o)
        {
            float len = b.boneLength;
            float r = Mathf.Max(Mathf.Abs(len) * 0.1f, 1e-4f) * Mathf.Max(1e-4f, scale);
            float z = len * 0.1f;
            o[0] = Vector3.zero;
            o[1] = new Vector3(r, 0f, z);
            o[2] = new Vector3(0f, r, z);
            o[3] = new Vector3(-r, 0f, z);
            o[4] = new Vector3(0f, -r, z);
            o[5] = new Vector3(0f, 0f, len);
        }

        public static Vector3 BoneTip(AsiKeyBone b)
        {
            return new Vector3(0f, 0f, b.boneLength);
        }

        public static void BoxCorners(AsiKeyShape s, Vector3[] o)
        {
            float hw = s.width * 0.5f;
            float hh = s.height * 0.5f;
            for (int i = 0; i < 8; i++)
            {
                o[i] = new Vector3((i & 1) != 0 ? hw : -hw, (i & 2) != 0 ? hh : -hh, (i & 4) != 0 ? s.length : 0f) + s.offset;
            }
        }

        public static void RingPoints(AsiKeyShape s, Vector3[] o)
        {
            for (int i = 0; i <= RingSegments; i++)
            {
                float a = i * Mathf.PI * 2f / RingSegments;
                o[i] = new Vector3(Mathf.Cos(a) * s.radiusX, Mathf.Sin(a) * s.radiusY, 0f) + s.offset;
            }
        }

        public static void RectPoints(AsiKeyShape s, Vector3[] o)
        {
            float x = s.radiusX;
            float y = s.radiusY;
            o[0] = new Vector3(-x, -y, 0f) + s.offset;
            o[1] = new Vector3(x, -y, 0f) + s.offset;
            o[2] = new Vector3(x, y, 0f) + s.offset;
            o[3] = new Vector3(-x, y, 0f) + s.offset;
            o[4] = o[0];
        }

        public static void MeshBoxCorners(AsiKeyShape s, Vector3[] o)
        {
            Bounds bounds = s.mesh != null ? s.mesh.bounds : new Bounds(Vector3.zero, Vector3.one * 0.05f);
            Vector3 mn = Vector3.Scale(bounds.min, s.meshScale);
            Vector3 mx = Vector3.Scale(bounds.max, s.meshScale);
            for (int i = 0; i < 8; i++)
            {
                o[i] = new Vector3((i & 1) != 0 ? mx.x : mn.x, (i & 2) != 0 ? mx.y : mn.y, (i & 4) != 0 ? mx.z : mn.z) + s.offset;
            }
        }

        public static float DistanceToSegment(Vector2 p, Vector2 a, Vector2 b)
        {
            Vector2 ab = b - a;
            float len = ab.sqrMagnitude;
            float t = len > 1e-8f ? Mathf.Clamp01(Vector2.Dot(p - a, ab) / len) : 0f;
            return (p - (a + ab * t)).magnitude;
        }

        /// <summary>点是否在凸多边形内（顶点顺序不限方向）。</summary>
        public static bool InsideConvex(Vector2 p, Vector2[] poly, int count)
        {
            float sign = 0f;
            for (int i = 0; i < count; i++)
            {
                Vector2 a = poly[i];
                Vector2 b = poly[(i + 1) % count];
                float cross = (b.x - a.x) * (p.y - a.y) - (b.y - a.y) * (p.x - a.x);
                if (Mathf.Abs(cross) < 1e-6f)
                    continue;
                if (sign == 0f)
                    sign = Mathf.Sign(cross);
                else if (Mathf.Sign(cross) != sign)
                    return false;
            }
            return sign != 0f;
        }
    }
}

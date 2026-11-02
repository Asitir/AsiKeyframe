using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace AsitirTool.Editor
{
    /// <summary>
    /// 姿势库缩略图：在单位坐标系里把骨骼画成方块（Biped 方块外形按外形尺寸，其余沿骨骼方向），离屏渲染成 PNG。
    /// 复制的骨骼用外形颜色，其余画成暗灰；相机从角色右前上方的透视角度对准复制的骨骼（局部姿势会拉近）。
    /// </summary>
    public static class AsiKeyPoseThumb
    {
        public const int Size = 160;
        private const float Fov = 30f;
        private static readonly Vector3 ViewDir = new Vector3(0.45f, 0.3f, 1f).normalized;
        private static readonly Color Background = new Color(0.17f, 0.18f, 0.2f, 1f);
        private static readonly Color Other = new Color(0.36f, 0.37f, 0.4f, 1f);

        private static Mesh s_cube;
        private static Material s_mat;

        /// <param name="bones">工程骨骼下标（外形取自这些骨骼）。</param>
        /// <param name="rot">与 bones 对齐、相对单位坐标系的世界旋转。</param>
        /// <param name="pos">与 bones 对齐、相对单位坐标系的世界位置。</param>
        /// <param name="focus">与 bones 对齐，复制的骨骼为 true。</param>
        public static byte[] Render(AsiKeyProject p, List<int> bones, Quaternion[] rot, Vector3[] pos, bool[] focus)
        {
            if (bones.Count == 0)
                return new byte[0];
            EnsureResources();
            Matrix4x4[] boxes = new Matrix4x4[bones.Count];
            Bounds bounds = new Bounds();
            bool hasBounds = false;
            for (int k = 0; k < bones.Count; k++)
            {
                if (p.bones[bones[k]].fingerTip)
                    continue;
                boxes[k] = BoxMatrix(p, p.bones[bones[k]], pos[k], rot[k]);
                if (!focus[k])
                    continue;
                for (int c = 0; c < 8; c++)
                {
                    Vector3 corner = boxes[k].MultiplyPoint3x4(new Vector3((c & 1) != 0 ? 0.5f : -0.5f, (c & 2) != 0 ? 0.5f : -0.5f, (c & 4) != 0 ? 0.5f : -0.5f));
                    if (!hasBounds)
                        bounds = new Bounds(corner, Vector3.zero);
                    else
                        bounds.Encapsulate(corner);
                    hasBounds = true;
                }
            }
            float radius = Mathf.Max(hasBounds ? bounds.extents.magnitude : 0.5f, 0.04f);
            Vector3 center = hasBounds ? bounds.center : pos[0];
            float dist = radius / Mathf.Sin(Fov * 0.5f * Mathf.Deg2Rad) * 1.05f;
            Vector3 camPos = center + ViewDir * dist;
            Quaternion camRot = Quaternion.LookRotation(center - camPos, Vector3.up);
            Matrix4x4 view = Matrix4x4.TRS(camPos, camRot, new Vector3(1f, 1f, -1f)).inverse;
            Matrix4x4 proj = Matrix4x4.Perspective(Fov, 1f, dist * 0.02f, dist * 4f);

            RenderTexture msaa = RenderTexture.GetTemporary(Size, Size, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Default, 4);
            RenderTexture flat = RenderTexture.GetTemporary(Size, Size, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Default, 1);
            RenderTexture prev = RenderTexture.active;
            Texture2D tex = new Texture2D(Size, Size, TextureFormat.RGB24, false);
            try
            {
                RenderTexture.active = msaa;
                GL.Clear(true, true, Background);
                GL.PushMatrix();
                GL.LoadProjectionMatrix(proj);
                GL.modelview = view;
                for (int pass = 0; pass < 2; pass++)
                {
                    for (int k = 0; k < bones.Count; k++)
                    {
                        if (p.bones[bones[k]].fingerTip || focus[k] != (pass == 1))
                            continue;
                        AsiKeyShape sh = p.bones[bones[k]].shape;
                        Color col = focus[k] ? sh.color : Other;
                        col.a = 1f;
                        s_mat.SetColor("_Color", col);
                        s_mat.SetPass(0);
                        Graphics.DrawMeshNow(s_cube, boxes[k]);
                    }
                }
                GL.PopMatrix();
                Graphics.Blit(msaa, flat);
                RenderTexture.active = flat;
                tex.ReadPixels(new Rect(0, 0, Size, Size), 0, 0);
                tex.Apply();
                return tex.EncodeToPNG();
            }
            finally
            {
                RenderTexture.active = prev;
                RenderTexture.ReleaseTemporary(msaa);
                RenderTexture.ReleaseTemporary(flat);
                Object.DestroyImmediate(tex);
            }
        }

        /// <summary>单位立方体（-0.5..0.5）到单位坐标系的变换。</summary>
        private static Matrix4x4 BoxMatrix(AsiKeyProject p, AsiKeyBone b, Vector3 pos, Quaternion rot)
        {
            Matrix4x4 m = Matrix4x4.TRS(pos, rot, Vector3.one);
            AsiKeyShape sh = b.shape;
            if (b.isCom)
            {
                float c = Mathf.Max(0.03f, Mathf.Abs(b.boneLength) * 0.3f);
                return m * Matrix4x4.TRS(Vector3.zero, Quaternion.Euler(45f, 0f, 45f), Vector3.one * c);
            }
            if (sh.kind == AsiShapeKind.BipBox && Mathf.Abs(sh.length) > 1e-4f)
            {
                Quaternion r = b.frame * Quaternion.Euler(sh.euler);
                float sc = Mathf.Max(1e-4f, p.shapeScale);
                return m * Matrix4x4.TRS(Vector3.zero, r, Vector3.one * sc)
                    * Matrix4x4.TRS(sh.offset + new Vector3(0f, 0f, sh.length * 0.5f), Quaternion.identity, new Vector3(sh.width, sh.height, sh.length));
            }
            float len = Mathf.Abs(b.boneLength) > 1e-3f ? b.boneLength : 0.03f;
            float t = Mathf.Clamp(Mathf.Abs(len) * 0.2f, 0.008f, 0.06f);
            return m * Matrix4x4.TRS(Vector3.zero, b.frame, Vector3.one)
                * Matrix4x4.TRS(new Vector3(0f, 0f, len * 0.5f), Quaternion.identity, new Vector3(t, t, len));
        }

        private static void EnsureResources()
        {
            if (s_mat == null)
            {
                s_mat = new Material(Shader.Find("Hidden/Internal-Colored")) { hideFlags = HideFlags.HideAndDontSave };
                s_mat.SetInt("_SrcBlend", (int)BlendMode.One);
                s_mat.SetInt("_DstBlend", (int)BlendMode.Zero);
                s_mat.SetInt("_Cull", (int)CullMode.Off);
                s_mat.SetInt("_ZWrite", 1);
                s_mat.SetInt("_ZTest", (int)CompareFunction.LessEqual);
            }
            if (s_cube == null)
                s_cube = BuildCube();
        }

        /// <summary>每个面一种亮度的顶点色立方体（顶面最亮），无光照也能看出体积。</summary>
        private static Mesh BuildCube()
        {
            Vector3[] n = { Vector3.up, Vector3.down, Vector3.right, Vector3.left, Vector3.forward, Vector3.back };
            float[] shade = { 1f, 0.45f, 0.8f, 0.6f, 0.9f, 0.7f };
            List<Vector3> v = new List<Vector3>();
            List<Color> c = new List<Color>();
            List<int> tri = new List<int>();
            for (int f = 0; f < 6; f++)
            {
                Vector3 nn = n[f];
                Vector3 a = Mathf.Abs(nn.y) > 0.5f ? Vector3.right : Vector3.up;
                Vector3 b = Vector3.Cross(nn, a);
                int start = v.Count;
                v.Add((nn - a - b) * 0.5f);
                v.Add((nn + a - b) * 0.5f);
                v.Add((nn + a + b) * 0.5f);
                v.Add((nn - a + b) * 0.5f);
                Color col = new Color(shade[f], shade[f], shade[f], 1f);
                for (int k = 0; k < 4; k++)
                    c.Add(col);
                tri.AddRange(new[] { start, start + 1, start + 2, start, start + 2, start + 3 });
            }
            Mesh m = new Mesh { hideFlags = HideFlags.HideAndDontSave, name = "AsiKeyPoseThumbCube" };
            m.SetVertices(v);
            m.SetColors(c);
            m.SetTriangles(tri, 0);
            return m;
        }
    }
}

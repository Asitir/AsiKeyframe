using UnityEngine;

namespace AsitirTool.Editor
{
    /// <summary>
    /// 链 IK：单成员只转朝向，两成员瞄准，三成员解析解，更多成员 FABRIK。
    /// 三成员时中间关节只绕第一节骨骼里的固定铰链轴弯（像 Biped 的膝/肘），再整体转到目标方向，
    /// 这样烘成 Humanoid 时不会多出超出肌肉范围的扭转。
    /// 弯曲平面取零平面（<see cref="ZeroPole"/>，跟随关键帧姿势，仿 Biped）绕「根→目标」轴转 swivel 度，与拖动历史无关；
    /// 中间成员的旋转父级不是链根时（解析稳定解不适用）跟着 FK 第一节骨骼的 poleHint 方向走。
    /// 更多成员时弯曲平面取当前 FK 姿势的中间关节，FK 伸直时用 poleHint。
    /// 结果写成链成员的局部旋转（相对各自的旋转父级）。
    /// </summary>
    public static class AsiKeyIKSolver
    {
        private static Vector3[] s_orig = new Vector3[16];
        private static Vector3[] s_pos = new Vector3[16];
        private static Quaternion[] s_rot = new Quaternion[16];
        private static Quaternion[] s_out = new Quaternion[16];
        private static float[] s_len = new float[16];

        private static void EnsureBuffers(int n)
        {
            if (s_orig.Length >= n)
                return;
            int size = Mathf.NextPowerOfTwo(n);
            s_orig = new Vector3[size];
            s_pos = new Vector3[size];
            s_rot = new Quaternion[size];
            s_out = new Quaternion[size];
            s_len = new float[size];
        }

        public static void Solve(AsiKeyProject p, AsiKeyChain chain, AsiKeyPose pose, Vector3 target, Quaternion targetRot, float swivel, Quaternion[] outLocal)
        {
            int n = chain.bones.Length;
            if (n == 0)
                return;
            EnsureBuffers(n);
            int root = chain.bones[0];
            int parent = p.bones[root].parent;
            Quaternion parentRot = pose.ParentRot(p, root);

            for (int k = 0; k < n; k++)
            {
                int b = chain.bones[k];
                s_orig[k] = pose.wpos[b];
                s_pos[k] = pose.wpos[b];
                s_rot[k] = pose.wrot[b];
            }

            if (n == 1)
            {
                s_out[0] = targetRot;
                outLocal[0] = Quaternion.Inverse(parentRot) * targetRot;
                return;
            }

            Vector3 a = s_orig[0];
            Vector3 axis = target - a;
            if (axis.sqrMagnitude < 1e-10f)
                axis = s_orig[n - 1] - a;
            if (axis.sqrMagnitude < 1e-10f)
                axis = Vector3.up;
            axis.Normalize();

            if (n >= 3 && Mathf.Abs(swivel) > 1e-3f)
            {
                Quaternion sw = Quaternion.AngleAxis(swivel, axis);
                for (int k = 1; k < n - 1; k++)
                    s_pos[k] = a + sw * (s_pos[k] - a);
            }

            if (n == 2)
            {
                Quaternion q = Quaternion.FromToRotation(s_orig[1] - a, target - a);
                s_out[0] = q * s_rot[0];
                s_out[1] = targetRot;
            }
            else if (n == 3)
            {
                if (p.bones[chain.bones[1]].parent == root)
                    SolveStable(p, chain, pose, target, targetRot, axis, swivel);
                else
                    SolveTwoBone(p, chain, parent, parentRot, target, targetRot, axis, swivel);
            }
            else
            {
                SolveFabrik(p, chain, parent, parentRot, n, target, targetRot, axis);
            }

            for (int k = 0; k < n; k++)
                outLocal[k] = Quaternion.Inverse(MemberParentRot(p, chain, pose, k)) * s_out[k];
            if (!AsiKeyFingers.IsFingerChain(p, chain))
                return;
            // 手指除第一节外只能卷曲：末节不跟目标旋转侧摆，中节去掉求解残留的侧向分量
            for (int k = 1; k < n; k++)
                outLocal[k] = AsiKeyFingers.CurlOnly(p, chain.bones[k], outLocal[k]);
        }

        /// <summary>成员 k 的旋转父级在解算结果下的世界旋转（Biped 独立朝向的脚，旋转父级是质心而不是小腿）。</summary>
        private static Quaternion MemberParentRot(AsiKeyProject p, AsiKeyChain chain, AsiKeyPose pose, int k)
        {
            int parent = p.bones[chain.bones[k]].parent;
            if (parent < 0)
                return pose.UnitRot(p.bones[chain.bones[k]].unit);
            for (int m = 0; m < k; m++)
            {
                if (chain.bones[m] == parent)
                    return s_out[m];
            }
            return pose.wrot[parent];
        }

        private static Vector3 PoleFallback(AsiKeyProject p, AsiKeyChain chain, int parent, Quaternion parentRot, Vector3 dir)
        {
            Quaternion parentRest = parent >= 0 ? p.bones[parent].rigRestRot : Quaternion.identity;
            Vector3 hint = parentRot * (Quaternion.Inverse(parentRest) * chain.poleHint);
            Vector3 pole = hint - Vector3.Dot(hint, dir) * dir;
            if (pole.sqrMagnitude < 1e-8f)
                pole = AsiKeyMath.Perpendicular(dir);
            return pole.normalized;
        }

        // 零平面：参考铰链轴与 dir 夹角的正弦低于 Fade0 时用静止零平面，高于 Fade1 时用参考零平面，中间平滑过渡。
        private const float FkZeroFade0 = 0.05f;
        private const float FkZeroFade1 = 0.2f;

        /// <summary>
        /// 零平面方向（角色空间，垂直于 dir），弯曲平面落在这个方向上时 swivel 为 0。
        /// 仿 Biped：把 <see cref="RefRoot"/> 下的铰链轴投影到垂直于 dir 的平面，零平面垂直于它。
        /// 关键帧处就是打帧时的弯曲平面，关键帧之间跟着链根骨骼的插值走；dir 接近铰链轴时过渡到 <see cref="RestZeroPole"/>。
        /// IK 拖动中（<see cref="CaptureDrag"/>）改为拖动开始时的零平面随方向做最小旋转。
        /// </summary>
        public static Vector3 ZeroPole(AsiKeyProject p, AsiKeyChain chain, AsiKeyPose pose, Vector3 dir)
        {
            int c = p.chains.IndexOf(chain);
            if (c >= 0 && c < pose.chainCount && pose.chains[c].hasDrag)
            {
                Quaternion pr = ParentRot(p, chain, pose);
                AsiChainState s = pose.chains[c];
                Vector3 drag = pr * (Quaternion.FromToRotation(s.dragDir, Quaternion.Inverse(pr) * dir) * s.dragPole);
                drag -= Vector3.Dot(drag, dir) * dir;
                if (drag.sqrMagnitude > 1e-8f)
                    return drag.normalized;
            }
            Vector3 rest = RestZeroPole(p, chain, pose, dir);
            float restBend;
            Vector3 m = RefRoot(p, chain, pose) * -AsiKeyHinge.Axis(p, chain, out restBend);
            m -= Vector3.Dot(m, dir) * dir;
            float len = m.magnitude;
            if (len <= FkZeroFade0)
                return rest;
            Vector3 pole = Vector3.Cross(m / len, dir);
            if (len >= FkZeroFade1)
                return pole;
            Vector3 mix = Vector3.Slerp(rest, pole, Mathf.SmoothStep(0f, 1f, (len - FkZeroFade0) / (FkZeroFade1 - FkZeroFade0)));
            mix -= Vector3.Dot(mix, dir) * dir;
            return mix.sqrMagnitude > 1e-8f ? mix.normalized : pole;
        }

        /// <summary>
        /// IK 拖动开始：记下当前「根→末端」方向与零平面（链根父级空间）。之后拖动里零平面由它随方向做最小旋转，
        /// 与 Biped 拖末端时膝/肘平面的走法一致，结果只取决于拖动起点和当前目标。只对三成员链生效，按关键帧重新求值时清掉。
        /// </summary>
        public static void CaptureDrag(AsiKeyProject p, AsiKeyChain chain, AsiKeyPose pose, ref AsiChainState s)
        {
            if (chain.bones.Length != 3)
                return;
            Vector3 dir = pose.wpos[chain.bones[2]] - pose.wpos[chain.bones[0]];
            if (dir.sqrMagnitude < 1e-12f)
                return;
            dir.Normalize();
            Vector3 zero = ZeroPole(p, chain, pose, dir);
            Quaternion inv = Quaternion.Inverse(ParentRot(p, chain, pose));
            s.dragDir = inv * dir;
            s.dragPole = inv * zero;
            s.hasDrag = true;
        }

        private static Quaternion ParentRot(AsiKeyProject p, AsiKeyChain chain, AsiKeyPose pose)
        {
            return pose.ParentRot(p, chain.bones[0]);
        }

        /// <summary>零平面参考：链根在关键帧 FK 姿势下的世界旋转。链状态没有参考时用当前 FK。</summary>
        private static Quaternion RefRoot(AsiKeyProject p, AsiKeyChain chain, AsiKeyPose pose)
        {
            int root = chain.bones[0];
            int c = p.chains.IndexOf(chain);
            bool has = c >= 0 && c < pose.chainCount && pose.chains[c].hasRef;
            return AsiKeyMath.Norm(ParentRot(p, chain, pose) * (has ? pose.chains[c].refRot : pose.fk[root]));
        }

        /// <summary>
        /// 静止零平面（角色空间，垂直于 dir）：静止姿势下链根旋转父级空间里的「根→末端」方向与 poleHint，
        /// 随当前「根→末端」方向做最小旋转（Max HI 解算器的做法）。只取决于当前方向与父级朝向；
        /// 当前方向与静止方向正好相反时没有定义。
        /// </summary>
        private static Vector3 RestZeroPole(AsiKeyProject p, AsiKeyChain chain, AsiKeyPose pose, Vector3 dir)
        {
            int root = chain.bones[0];
            int parent = p.bones[root].parent;
            Quaternion parentRest = parent >= 0 ? p.bones[parent].rigRestRot : Quaternion.identity;
            Quaternion parentRot = pose.ParentRot(p, root);
            Quaternion invRest = Quaternion.Inverse(parentRest);
            Vector3 restAxis = invRest * (p.bones[chain.Effector].rigRestPos - p.bones[root].rigRestPos);
            if (restAxis.sqrMagnitude < 1e-12f)
                restAxis = invRest * (p.bones[root].rigRestRot * p.bones[root].axis);
            restAxis.Normalize();
            Vector3 restPole = invRest * chain.poleHint;
            restPole -= Vector3.Dot(restPole, restAxis) * restAxis;
            if (restPole.sqrMagnitude < 1e-8f)
                restPole = AsiKeyMath.Perpendicular(restAxis);
            restPole.Normalize();
            Vector3 cur = Quaternion.Inverse(parentRot) * dir;
            Vector3 pole = parentRot * (Quaternion.FromToRotation(restAxis, cur) * restPole);
            pole -= Vector3.Dot(pole, dir) * dir;
            if (pole.sqrMagnitude < 1e-8f)
                pole = AsiKeyMath.Perpendicular(dir);
            return pole.normalized;
        }

        /// <summary>
        /// 当前姿势下弯曲平面相对零平面绕「根→末端」轴的角度，是稳定解算的逆运算：用它求解能还原当前弯曲平面。
        /// 弯曲平面由链根的铰链轴定，肢体伸直也量得出；铰链轴与连线平行时返回 fallback。
        /// </summary>
        public static float MeasureSwivel(AsiKeyProject p, AsiKeyChain chain, AsiKeyPose pose, float fallback)
        {
            if (chain == null || chain.bones.Length != 3)
                return fallback;
            Vector3 a = pose.wpos[chain.bones[0]];
            Vector3 dir = pose.wpos[chain.bones[2]] - a;
            if (dir.sqrMagnitude < 1e-12f)
                return fallback;
            dir.Normalize();
            float restBend;
            Vector3 knee = Vector3.Cross(dir, pose.wrot[chain.bones[0]] * AsiKeyHinge.Axis(p, chain, out restBend));
            if (knee.sqrMagnitude < 1e-8f)
                return fallback;
            return Vector3.SignedAngle(ZeroPole(p, chain, pose, dir), knee, dir);
        }

        /// <summary>局部 X 映射到 x、局部 Y 映射到 y 的旋转（x、y 为垂直的单位向量）。</summary>
        private static Quaternion FrameXY(Vector3 x, Vector3 y)
        {
            return Quaternion.LookRotation(Vector3.Cross(x, y), y);
        }

        /// <summary>
        /// 稳定三成员解：先按距离解出中间关节绕铰链轴的弯曲角，再整体转到「根→目标」方向，
        /// 让中间关节正好落在零平面绕该轴转 swivel 度的方向上。结果只取决于目标、swivel 与父级朝向。
        /// </summary>
        private static void SolveStable(AsiKeyProject p, AsiKeyChain chain, AsiKeyPose pose, Vector3 target, Quaternion targetRot, Vector3 dir, float swivel)
        {
            Vector3 a = s_orig[0];
            Vector3 u = p.LinkOffset(chain.bones[1]);
            // 手指中节从绑定姿势起弯，不继承 FK 里的侧摆
            Quaternion seed = AsiKeyHinge.Active(p, chain) || AsiKeyFingers.IsFingerChain(p, chain)
                ? p.bones[chain.bones[1]].restRot
                : AsiKeyMath.Norm(Quaternion.Inverse(s_rot[0]) * s_rot[1]);
            Vector3 w = seed * p.LinkOffset(chain.bones[2]);
            float l1 = u.magnitude;
            float l2 = w.magnitude;
            if (l1 < 1e-6f || l2 < 1e-6f)
            {
                s_out[0] = s_rot[0];
                s_out[1] = s_rot[1];
                s_out[2] = targetRot;
                return;
            }
            float restBend;
            Vector3 h = AsiKeyHinge.Axis(p, chain, out restBend);
            Vector3 f = Vector3.Cross(u, h).normalized;
            float d = ClampReach((target - a).magnitude, l1, l2);

            Vector3 wPar = Vector3.Dot(w, h) * h;
            Vector3 wPerp = w - wPar;
            Vector3 wSide = Vector3.Cross(h, wPerp);
            float A = Vector3.Dot(u, wPerp);
            float B = Vector3.Dot(u, wSide);
            float C = 0.5f * (d * d - l1 * l1 - l2 * l2) - Vector3.Dot(u, wPar);
            float R = Mathf.Sqrt(A * A + B * B);
            float theta = 0f;
            if (R > 1e-8f)
            {
                float mid = Mathf.Atan2(B, A);
                float off = Mathf.Acos(Mathf.Clamp(C / R, -1f, 1f));
                float t1 = mid + off;
                float t2 = mid - off;
                theta = LocalBendScore(u, w, h, t1, f) >= LocalBendScore(u, w, h, t2, f) ? t1 : t2;
            }
            Quaternion bend = Quaternion.AngleAxis(theta * Mathf.Rad2Deg, h);
            Vector3 L = u + bend * w;
            Vector3 lh = L.normalized;
            // 按铰链平面定朝向：末端相对铰链有侧向偏移时，根、中、末三点平面在伸直附近会转到侧面，整条肢体跟着扭。
            Vector3 kl = Vector3.Cross(lh, h);
            if (kl.sqrMagnitude < 1e-10f)
            {
                Vector3 hz = h - Vector3.Dot(h, lh) * lh;
                kl = Vector3.Cross(lh, hz);
            }
            kl.Normalize();

            Vector3 pole = ZeroPole(p, chain, pose, dir);
            if (Mathf.Abs(swivel) > 1e-4f)
                pole = Quaternion.AngleAxis(swivel, dir) * pole;
            Quaternion q = AsiKeyMath.Norm(FrameXY(dir, pole) * Quaternion.Inverse(FrameXY(lh, kl)));
            s_out[0] = q;
            s_out[1] = AsiKeyMath.Norm(q * bend * seed);
            s_out[2] = targetRot;
        }

        /// <summary>
        /// 两节链能解的「根→目标」距离。留一点相对余量让肢体不完全伸直，弯曲方向在够不着时仍有定义；
        /// 余量为链长的百万分之一，伸到头时约剩 0.16° 弯曲（Max Biped 约 0.1°）。
        /// </summary>
        private static float ClampReach(float d, float l1, float l2)
        {
            float eps = (l1 + l2) * 1e-6f;
            return Mathf.Clamp(d, Mathf.Abs(l1 - l2) + eps, l1 + l2 - eps);
        }

        /// <summary>链根局部空间里，弯曲 theta 弧度后中间关节相对「根→末端」连线往 f 那侧凸出多少。</summary>
        private static float LocalBendScore(Vector3 u, Vector3 w, Vector3 h, float theta, Vector3 f)
        {
            Vector3 line = (u + Quaternion.AngleAxis(theta * Mathf.Rad2Deg, h) * w).normalized;
            return Vector3.Dot(u - Vector3.Dot(u, line) * line, f);
        }

        private static void SolveTwoBone(AsiKeyProject p, AsiKeyChain chain, int parent, Quaternion parentRot, Vector3 target, Quaternion targetRot, Vector3 dir, float swivel)
        {
            Vector3 a = s_orig[0];
            Vector3 b = s_orig[1];
            Vector3 c = s_orig[2];
            float l1 = (b - a).magnitude;
            float l2 = (c - b).magnitude;
            if (l1 < 1e-6f || l2 < 1e-6f)
            {
                s_out[0] = s_rot[0];
                s_out[1] = s_rot[1];
                s_out[2] = targetRot;
                return;
            }
            float d = ClampReach((target - a).magnitude, l1, l2);

            Vector3 u = b - a;
            Vector3 v = c - b;
            Vector3 fwd = BendForward(p, chain, u, v);
            Vector3 h = Vector3.Cross(u, fwd).normalized;
            Vector3 vPar = Vector3.Dot(v, h) * h;
            Vector3 vPerp = v - vPar;
            Vector3 vSide = Vector3.Cross(h, vPerp);
            float A = Vector3.Dot(u, vPerp);
            float B = Vector3.Dot(u, vSide);
            float C = 0.5f * (d * d - l1 * l1 - l2 * l2) - Vector3.Dot(u, vPar);
            float R = Mathf.Sqrt(A * A + B * B);
            float phi = 0f;
            if (R > 1e-8f)
            {
                float mid = Mathf.Atan2(B, A);
                float off = Mathf.Acos(Mathf.Clamp(C / R, -1f, 1f));
                float p1 = mid + off;
                float p2 = mid - off;
                phi = BendScore(a, b, v, h, p1, fwd) >= BendScore(a, b, v, h, p2, fwd) ? p1 : p2;
            }
            Quaternion bend = Quaternion.AngleAxis(phi * Mathf.Rad2Deg, h);
            Vector3 c1 = b + bend * v;
            Quaternion aim = Quaternion.FromToRotation(c1 - a, dir);

            Vector3 pole = fwd - Vector3.Dot(fwd, dir) * dir;
            if (pole.sqrMagnitude < 1e-4f)
                pole = PoleFallback(p, chain, parent, parentRot, dir);
            else
                pole.Normalize();
            if (Mathf.Abs(swivel) > 1e-3f)
                pole = Quaternion.AngleAxis(swivel, dir) * pole;
            Vector3 f2 = aim * fwd;
            Vector3 kp = f2 - Vector3.Dot(f2, dir) * dir;
            if (kp.sqrMagnitude < 1e-4f)
            {
                Vector3 knee = aim * u;
                kp = knee - Vector3.Dot(knee, dir) * dir;
            }
            Quaternion roll = kp.sqrMagnitude > 1e-12f ? Quaternion.AngleAxis(Vector3.SignedAngle(kp, pole, dir), dir) : Quaternion.identity;

            Quaternion q = roll * aim;
            s_out[0] = q * s_rot[0];
            s_out[1] = q * bend * s_rot[1];
            s_out[2] = targetRot;
        }

        /// <summary>中间关节自然弯出去的方向（垂直于第一节），由 poleHint 跟着第一节骨骼走。</summary>
        private static Vector3 BendForward(AsiKeyProject p, AsiKeyChain chain, Vector3 u, Vector3 v)
        {
            Vector3 un = u.normalized;
            Vector3 hint = s_rot[0] * (Quaternion.Inverse(p.bones[chain.bones[0]].rigRestRot) * chain.poleHint);
            Vector3 f = hint - Vector3.Dot(hint, un) * un;
            if (f.sqrMagnitude > 1e-8f)
                return f.normalized;
            f = -(v - Vector3.Dot(v, un) * un);
            if (f.sqrMagnitude > 1e-8f * v.sqrMagnitude)
                return f.normalized;
            return AsiKeyMath.Perpendicular(un).normalized;
        }

        /// <summary>中间关节相对「根→末端」连线往 fwd 那侧凸出多少，用来在两个铰链解里挑自然弯曲的那个。</summary>
        private static float BendScore(Vector3 a, Vector3 b, Vector3 v, Vector3 h, float phi, Vector3 fwd)
        {
            Vector3 c = b + Quaternion.AngleAxis(phi * Mathf.Rad2Deg, h) * v;
            Vector3 line = (c - a).normalized;
            Vector3 u = b - a;
            return Vector3.Dot(u - Vector3.Dot(u, line) * line, fwd);
        }

        private static void SolveFabrik(AsiKeyProject p, AsiKeyChain chain, int parent, Quaternion parentRot, int n, Vector3 target, Quaternion targetRot, Vector3 dir)
        {
            float total = 0f;
            for (int k = 0; k < n - 1; k++)
            {
                s_len[k] = (s_orig[k + 1] - s_orig[k]).magnitude;
                total += s_len[k];
            }
            Vector3 a = s_orig[0];
            bool straight = true;
            for (int k = 1; k < n - 1; k++)
            {
                Vector3 off = s_pos[k] - a;
                if ((off - Vector3.Dot(off, dir) * dir).sqrMagnitude > 1e-8f)
                {
                    straight = false;
                    break;
                }
            }
            if (straight)
            {
                Vector3 pole = PoleFallback(p, chain, parent, parentRot, dir);
                for (int k = 1; k < n - 1; k++)
                    s_pos[k] += pole * (0.05f * s_len[k - 1]);
            }

            if ((target - a).magnitude >= total)
            {
                Vector3 d = (target - a).normalized;
                for (int k = 1; k < n; k++)
                    s_pos[k] = s_pos[k - 1] + d * s_len[k - 1];
            }
            else
            {
                for (int it = 0; it < 16; it++)
                {
                    s_pos[n - 1] = target;
                    for (int k = n - 2; k >= 0; k--)
                        s_pos[k] = s_pos[k + 1] + (s_pos[k] - s_pos[k + 1]).normalized * s_len[k];
                    s_pos[0] = a;
                    for (int k = 1; k < n; k++)
                        s_pos[k] = s_pos[k - 1] + (s_pos[k] - s_pos[k - 1]).normalized * s_len[k - 1];
                    if ((s_pos[n - 1] - target).sqrMagnitude < 1e-8f)
                        break;
                }
            }

            Quaternion acc = Quaternion.identity;
            for (int k = 0; k < n - 1; k++)
            {
                Vector3 cur = acc * (s_orig[k + 1] - s_orig[k]);
                Vector3 want = s_pos[k + 1] - s_pos[k];
                acc = Quaternion.FromToRotation(cur, want) * acc;
                s_out[k] = acc * s_rot[k];
            }
            s_out[n - 1] = targetRot;
        }
    }
}

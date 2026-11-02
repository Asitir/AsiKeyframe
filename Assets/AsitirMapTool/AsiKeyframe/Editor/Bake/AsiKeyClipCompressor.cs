using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

/// <summary>
/// AnimationClip 压缩。贝塞尔或 Unity 删键。
/// 只产生内存中的片段，写盘后再用 ReapplyStorage 设置存储方式。
/// </summary>
public static class AsiKeyClipCompressor
{
    // 身体旋转低于关节观感。导出端用 0.25°，这里给重建留余量。
    private const float BodyRotationDegrees = 0.2f;
    // 手指在画面上更小，放宽才能削掉抖动，又不会改手型。
    private const float FingerRotationDegrees = 0.45f;
    // 根旋转会带动全身。
    private const float RootRotationDegrees = 0.12f;
    // 根位移 0.5mm，其它骨骼 1mm。用绝对值，避免人走远了误差跟着放大。
    private const float RootPositionMeters = 0.0005f;
    private const float BonePositionMeters = 0.001f;
    private const float ScaleEpsilon = 0.001f;
    // 肌肉值域大约 [-1, 1]。0.008 约等于大关节上零点几度，用来卡住弧线偏离。
    private const float BodyMuscle = 0.008f;
    private const float FingerMuscle = 0.015f;
    private const float FaceMuscle = 0.006f;

    private static readonly string[] HumanChannels =
    {
        "RootT", "RootQ", "MotionT", "MotionQ",
        "LeftFootT", "LeftFootQ", "RightFootT", "RightFootQ",
        "LeftHandT", "LeftHandQ", "RightHandT", "RightHandQ"
    };

    private static Settings s_opt = Settings.Bezier();

    private enum TolSource
    {
        Given,
        PositionPercent,
        ScalePercent
    }

    public struct Settings
    {
        public bool bezier;
        public bool reduceKeys;
        public bool highQualityOff;
        public bool customProperties;
        public bool removeConstantScale;
        public float rotationError;
        public float positionError;
        public float scaleError;
        public int compression;

        public static Settings Bezier()
        {
            Settings settings = new Settings();
            settings.bezier = true;
            settings.reduceKeys = true;
            settings.highQualityOff = true;
            settings.customProperties = true;
            settings.rotationError = 0.5f;
            settings.positionError = 0.5f;
            settings.scaleError = 0.5f;
            settings.compression = 3;
            return settings;
        }
    }

    private sealed class ChannelCurves
    {
        public AnimationCurve x;
        public AnimationCurve y;
        public AnimationCurve z;
        public AnimationCurve w;
    }

    private sealed class BoneCurves
    {
        public string path;
        public ChannelCurves rotation = new ChannelCurves();
        public ChannelCurves euler = new ChannelCurves();
        public ChannelCurves position = new ChannelCurves();
        public ChannelCurves scale = new ChannelCurves();
        public string eulerPrefix;
        public bool finger;
        public bool rootPosition;
    }

    private sealed class LooseCurve
    {
        public EditorCurveBinding binding;
        public AnimationCurve curve;
    }

    private sealed class BezierFit
    {
        public readonly List<int> index = new List<int>();
        public readonly List<float> inX = new List<float>();
        public readonly List<float> outX = new List<float>();
        public readonly List<float> inY = new List<float>();
        public readonly List<float> outY = new List<float>();
        public readonly List<float> inZ = new List<float>();
        public readonly List<float> outZ = new List<float>();
        public readonly List<float> inW = new List<float>();
        public readonly List<float> outW = new List<float>();
    }

    /// <summary>
    /// 固定两端的值，用最小二乘求两端斜率。这就是 Unity 自由切线的三次贝塞尔。
    /// 左右两段的斜率可以不同，键是断点切线，转角不会被拉成一条光滑切线。
    /// </summary>
    private sealed class CubicReducer
    {
        public float[] times;
        public float[] x;
        public float[] y;
        public float[] z;
        public float[] w;
        public float tolerance;
        public int mode;
        public float m0x, m1x, m0y, m1y, m0z, m1z, m0w, m1w;

        public bool TrySpan(int anchor, int end)
        {
            float dt = times[end] - times[anchor];
            if (dt <= 0.0000001f)
            {
                m0x = m1x = m0y = m1y = m0z = m1z = m0w = m1w = 0f;
                return true;
            }

            SolveSlopes(times, x, anchor, end, dt, out m0x, out m1x);
            if (mode >= 1)
                SolveSlopes(times, y, anchor, end, dt, out m0y, out m1y);
            if (mode >= 1)
                SolveSlopes(times, z, anchor, end, dt, out m0z, out m1z);
            if (mode >= 2)
                SolveSlopes(times, w, anchor, end, dt, out m0w, out m1w);

            if (mode == 0)
                return FloatWithin(anchor, end, dt, x, m0x, m1x);
            if (mode == 1)
                return VectorWithin(anchor, end, dt);
            return QuaternionWithin(anchor, end, dt);
        }

        private bool FloatWithin(int anchor, int end, float dt, float[] values, float m0, float m1)
        {
            float v0 = values[anchor];
            float v1 = values[end];
            float t0 = times[anchor];
            for (int i = anchor + 1; i < end; i++)
            {
                float u = (times[i] - t0) / dt;
                if (Abs(Hermite(v0, v1, m0, m1, u, dt) - values[i]) > tolerance)
                    return false;
            }
            return true;
        }

        private bool VectorWithin(int anchor, int end, float dt)
        {
            float t0 = times[anchor];
            float tolSq = tolerance * tolerance;
            for (int i = anchor + 1; i < end; i++)
            {
                float u = (times[i] - t0) / dt;
                float dx = Hermite(x[anchor], x[end], m0x, m1x, u, dt) - x[i];
                float dy = Hermite(y[anchor], y[end], m0y, m1y, u, dt) - y[i];
                float dz = Hermite(z[anchor], z[end], m0z, m1z, u, dt) - z[i];
                if (dx * dx + dy * dy + dz * dz > tolSq)
                    return false;
            }
            return true;
        }

        private bool QuaternionWithin(int anchor, int end, float dt)
        {
            float t0 = times[anchor];
            for (int i = anchor + 1; i < end; i++)
            {
                float u = (times[i] - t0) / dt;
                float hx = Hermite(x[anchor], x[end], m0x, m1x, u, dt);
                float hy = Hermite(y[anchor], y[end], m0y, m1y, u, dt);
                float hz = Hermite(z[anchor], z[end], m0z, m1z, u, dt);
                float hw = Hermite(w[anchor], w[end], m0w, m1w, u, dt);
                float mag = Mathf.Sqrt(hx * hx + hy * hy + hz * hz + hw * hw);
                if (mag < 1e-8f)
                    return false;
                float inv = 1f / mag;
                float angle = Quaternion.Angle(
                    new Quaternion(hx * inv, hy * inv, hz * inv, hw * inv),
                    new Quaternion(x[i], y[i], z[i], w[i]));
                if (angle > tolerance)
                    return false;
            }
            return true;
        }
    }

    public struct WriteCount
    {
        public int keysBefore;
        public int keysAfter;
        public int dropped;
        public int linearFallback;
    }


    public struct Result
    {
        public AnimationClip clip;
        public WriteCount count;
        public bool keepHighQuality;
    }

    /// <summary>
    /// 压出一份内存中的新片段，不写盘。调用前会记下 settings，供删键过程读取。
    /// </summary>
    public static Result Compress(AnimationClip source, Settings settings)
    {
        s_opt = settings;
        Result result = new Result();
        EditorCurveBinding[] bindings = AnimationUtility.GetCurveBindings(source);
        EditorCurveBinding[] refBindings = AnimationUtility.GetObjectReferenceCurveBindings(source);

        AnimationClip dest = new AnimationClip();
        dest.name = source.name;
        dest.frameRate = source.frameRate;
        dest.legacy = source.legacy;
        dest.wrapMode = source.wrapMode;
        AnimationUtility.SetAnimationClipSettings(dest, AnimationUtility.GetAnimationClipSettings(source));

        Dictionary<string, ChannelCurves> humanCurves = new Dictionary<string, ChannelCurves>();
        List<LooseCurve> muscles = new List<LooseCurve>();
        Dictionary<string, BoneCurves> bones = new Dictionary<string, BoneCurves>();
        List<LooseCurve> others = new List<LooseCurve>();
        int curveKeys = 0;
        SplitBindings(source, bindings, humanCurves, muscles, bones, others, ref curveKeys);

        WriteCount count = new WriteCount();
        count.keysBefore = curveKeys + CountRefKeys(source, refBindings);
        float length = ClipLength(source);
        WriteHumanChannels(dest, source, humanCurves, length, ref count);
        WriteMuscles(dest, muscles, length, ref count);
        WriteBones(dest, bones, length, ref count);
        WriteLoose(dest, others, length, ref count);
        CopyReferenceCurves(dest, source, refBindings, ref count);
        AnimationUtility.SetAnimationEvents(dest, AnimationUtility.GetAnimationEvents(source));
        dest.EnsureQuaternionContinuity();
        result.keepHighQuality = ReadHighQuality(source);
        ApplyCurveStorage(dest, result.keepHighQuality);
        result.count = count;
        result.clip = dest;
        return result;
    }

    /// <summary>
    /// CreateAsset 会冲掉高精度标记，写盘后再套一次。keepHighQuality 用压缩前从源片段读到的值。
    /// </summary>
    public static void ReapplyStorage(AnimationClip clip, Settings settings, bool keepHighQuality)
    {
        s_opt = settings;
        ApplyCurveStorage(clip, keepHighQuality);
    }

    private static void SplitBindings(
        AnimationClip source,
        EditorCurveBinding[] bindings,
        Dictionary<string, ChannelCurves> human,
        List<LooseCurve> muscles,
        Dictionary<string, BoneCurves> bones,
        List<LooseCurve> others,
        ref int keysBefore)
    {
        for (int i = 0; i < bindings.Length; i++)
        {
            EditorCurveBinding binding = bindings[i];
            AnimationCurve curve = AnimationUtility.GetEditorCurve(source, binding);
            if (curve == null || curve.length == 0)
                continue;
            keysBefore += curve.length;

            if (binding.type == typeof(Animator))
            {
                string group;
                int axis;
                if (TrySplitHumanChannel(binding.propertyName, out group, out axis))
                    SetAxis(GetHuman(human, group), axis, curve);
                else
                    muscles.Add(new LooseCurve { binding = binding, curve = curve });
                continue;
            }

            if (binding.type == typeof(Transform))
            {
                if (binding.propertyName.StartsWith("m_LocalEulerAnglesHint", StringComparison.Ordinal))
                    continue;
                BoneCurves bone = GetBone(bones, binding.path);
                int axis;
                if (TryAxis(binding.propertyName, "m_LocalRotation", out axis))
                    SetAxis(bone.rotation, axis, curve);
                else if (TryAxis(binding.propertyName, "localEulerAnglesRaw", out axis))
                {
                    bone.eulerPrefix = "localEulerAnglesRaw";
                    SetAxis(bone.euler, axis, curve);
                }
                else if (TryAxis(binding.propertyName, "localEulerAngles", out axis))
                {
                    if (bone.eulerPrefix != "localEulerAnglesRaw")
                    {
                        bone.eulerPrefix = "localEulerAngles";
                        SetAxis(bone.euler, axis, curve);
                    }
                }
                else if (TryAxis(binding.propertyName, "m_LocalPosition", out axis))
                    SetAxis(bone.position, axis, curve);
                else if (TryAxis(binding.propertyName, "m_LocalScale", out axis))
                    SetAxis(bone.scale, axis, curve);
                else
                    others.Add(new LooseCurve { binding = binding, curve = curve });
                continue;
            }

            others.Add(new LooseCurve { binding = binding, curve = curve });
        }
    }

    private static void WriteHumanChannels(
        AnimationClip dest,
        AnimationClip source,
        Dictionary<string, ChannelCurves> human,
        float length,
        ref WriteCount count)
    {
        foreach (KeyValuePair<string, ChannelCurves> pair in human)
        {
            string group = pair.Key;
            ChannelCurves curves = pair.Value;
            bool quat = group.EndsWith("Q", StringComparison.Ordinal);
            if (!s_opt.reduceKeys)
            {
                CopyChannel(dest, "", typeof(Animator), group, curves, quat, ref count);
                continue;
            }
            if (quat && HasQuat(curves))
            {
                float tolerance = s_opt.bezier ? HumanRotationTolerance(group) : s_opt.rotationError;
                WriteQuaternion(dest, "", typeof(Animator), group, curves, source.frameRate, length, tolerance, ref count);
            }
            else if (!quat && HasVec3(curves))
            {
                float tolerance = s_opt.bezier ? RootPositionMeters : 0f;
                TolSource sourceKind = s_opt.bezier ? TolSource.Given : TolSource.PositionPercent;
                WriteVector(dest, "", typeof(Animator), group, curves, source.frameRate, length, tolerance, sourceKind, ref count);
            }
            else if (quat)
            {
                CopyChannel(dest, "", typeof(Animator), group, curves, true, ref count);
            }
            else
            {
                float tolerance = s_opt.bezier ? RootPositionMeters : 0f;
                TolSource sourceKind = s_opt.bezier ? TolSource.Given : TolSource.PositionPercent;
                WriteEachAxis(dest, "", typeof(Animator), group, curves, source.frameRate, length, tolerance, sourceKind, ref count);
            }
        }
    }

    private static void WriteMuscles(AnimationClip dest, List<LooseCurve> muscles, float length, ref WriteCount count)
    {
        for (int i = 0; i < muscles.Count; i++)
        {
            LooseCurve item = muscles[i];
            if (!s_opt.reduceKeys)
            {
                AnimationUtility.SetEditorCurve(dest, item.binding, item.curve);
                count.keysAfter += item.curve.length;
                continue;
            }
            float tolerance = s_opt.bezier ? MuscleTolerance(item.binding.propertyName) : s_opt.rotationError / 180f;
            float[] times = SampleTimes(length, dest.frameRate, item.curve, null, null, null);
            float[] values = Evaluate(item.curve, times);
            if (HasNonFinite(values))
            {
                AnimationUtility.SetEditorCurve(dest, item.binding, item.curve);
                count.keysAfter += item.curve.length;
                continue;
            }
            // 休息肌肉是 0。只有贝塞尔方案会把整段都在容差内的肌肉删掉。
            if (s_opt.bezier && MaxAbs(values) <= tolerance)
            {
                count.dropped++;
                continue;
            }
            WriteFloat(dest, item.binding, times, values, tolerance, false, ref count);
        }
    }

    private static void WriteBones(AnimationClip dest, Dictionary<string, BoneCurves> bones, float length, ref WriteCount count)
    {
        foreach (KeyValuePair<string, BoneCurves> pair in bones)
        {
            BoneCurves bone = pair.Value;
            float rotTol = s_opt.bezier
                ? (bone.finger ? FingerRotationDegrees : BodyRotationDegrees)
                : s_opt.rotationError;
            if (!s_opt.reduceKeys)
            {
                if (HasAny(bone.rotation))
                    CopyChannel(dest, bone.path, typeof(Transform), "m_LocalRotation", bone.rotation, true, ref count);
                else if (HasAny(bone.euler))
                    CopyChannel(dest, bone.path, typeof(Transform), bone.eulerPrefix, bone.euler, false, ref count);
            }
            else if (HasQuat(bone.rotation))
            {
                WriteQuaternion(dest, bone.path, typeof(Transform), "m_LocalRotation", bone.rotation, dest.frameRate, length, rotTol, ref count);
            }
            else if (HasAny(bone.rotation))
            {
                CopyChannel(dest, bone.path, typeof(Transform), "m_LocalRotation", bone.rotation, true, ref count);
            }
            else if (HasVec3(bone.euler))
            {
                WriteVector(dest, bone.path, typeof(Transform), bone.eulerPrefix, bone.euler, dest.frameRate, length, rotTol, TolSource.Given, ref count);
            }
            else if (HasAny(bone.euler))
            {
                WriteEachAxis(dest, bone.path, typeof(Transform), bone.eulerPrefix, bone.euler, dest.frameRate, length, rotTol, TolSource.Given, ref count);
            }

            float posTol = s_opt.bezier ? (bone.rootPosition ? RootPositionMeters : BonePositionMeters) : 0f;
            TolSource posKind = s_opt.bezier ? TolSource.Given : TolSource.PositionPercent;
            if (!s_opt.reduceKeys)
                CopyChannel(dest, bone.path, typeof(Transform), "m_LocalPosition", bone.position, false, ref count);
            else if (HasVec3(bone.position))
                WriteVector(dest, bone.path, typeof(Transform), "m_LocalPosition", bone.position, dest.frameRate, length, posTol, posKind, ref count);
            else
                WriteEachAxis(dest, bone.path, typeof(Transform), "m_LocalPosition", bone.position, dest.frameRate, length, posTol, posKind, ref count);

            WriteScale(dest, bone, length, ref count);
        }
    }

    private static void WriteScale(AnimationClip dest, BoneCurves bone, float length, ref WriteCount count)
    {
        if (!HasVec3(bone.scale))
        {
            float tol = s_opt.bezier ? ScaleEpsilon : 0f;
            TolSource kind = s_opt.bezier ? TolSource.Given : TolSource.ScalePercent;
            if (!s_opt.reduceKeys)
                CopyChannel(dest, bone.path, typeof(Transform), "m_LocalScale", bone.scale, false, ref count);
            else
                WriteEachAxis(dest, bone.path, typeof(Transform), "m_LocalScale", bone.scale, dest.frameRate, length, tol, kind, ref count);
            return;
        }

        float[] times = SampleTimes(length, dest.frameRate, bone.scale.x, bone.scale.y, bone.scale.z, null);
        float[] sx = Evaluate(bone.scale.x, times);
        float[] sy = Evaluate(bone.scale.y, times);
        float[] sz = Evaluate(bone.scale.z, times);
        if (HasNonFinite(sx) || HasNonFinite(sy) || HasNonFinite(sz))
        {
            CopyChannel(dest, bone.path, typeof(Transform), "m_LocalScale", bone.scale, false, ref count);
            return;
        }

        if (s_opt.bezier)
        {
            // 缺缩放曲线时 Unity 用 1。整段都贴着 1 就不要写回去。
            if (NearOne(sx, sy, sz, ScaleEpsilon))
            {
                count.dropped += CountPresent(bone.scale);
                return;
            }
            WriteVectorSamples(dest, bone.path, typeof(Transform), "m_LocalScale", times, sx, sy, sz, ScaleEpsilon, false, ref count);
            return;
        }

        if (s_opt.removeConstantScale && StaysNearFirst(sx, sy, sz, s_opt.scaleError))
        {
            count.dropped += CountPresent(bone.scale);
            return;
        }
        if (!s_opt.reduceKeys)
            CopyChannel(dest, bone.path, typeof(Transform), "m_LocalScale", bone.scale, false, ref count);
        else
            WriteVectorSamples(dest, bone.path, typeof(Transform), "m_LocalScale", times, sx, sy, sz, s_opt.scaleError, true, ref count);
    }

    private static void WriteLoose(AnimationClip dest, List<LooseCurve> others, float length, ref WriteCount count)
    {
        for (int i = 0; i < others.Count; i++)
        {
            LooseCurve item = others[i];
            if (!s_opt.reduceKeys || !s_opt.customProperties)
            {
                AnimationUtility.SetEditorCurve(dest, item.binding, item.curve);
                count.keysAfter += item.curve.length;
                continue;
            }
            float[] times = SampleTimes(length, dest.frameRate, item.curve, null, null, null);
            float[] values = Evaluate(item.curve, times);
            if (HasNonFinite(values))
            {
                AnimationUtility.SetEditorCurve(dest, item.binding, item.curve);
                count.keysAfter += item.curve.length;
                continue;
            }
            bool relative = !s_opt.bezier;
            float tolerance = relative ? s_opt.positionError : LooseTolerance(item.binding, values);
            WriteFloat(dest, item.binding, times, values, tolerance, relative, ref count);
        }
    }

    private static void WriteQuaternion(
        AnimationClip dest,
        string path,
        Type type,
        string prefix,
        ChannelCurves curves,
        float frameRate,
        float length,
        float tolerance,
        ref WriteCount count)
    {
        float[] times = SampleTimes(length, frameRate, curves.x, curves.y, curves.z, curves.w);
        float[] x = Evaluate(curves.x, times);
        float[] y = Evaluate(curves.y, times);
        float[] z = Evaluate(curves.z, times);
        float[] w = Evaluate(curves.w, times);
        NormalizeQuaternions(x, y, z, w);
        MakeContinuous(x, y, z, w);
        if (HasNonFinite(x) || HasNonFinite(y) || HasNonFinite(z) || HasNonFinite(w))
        {
            CopyChannel(dest, path, type, prefix, curves, true, ref count);
            return;
        }

        if (!s_opt.bezier)
        {
            List<int> keep = Greedy(times.Length, (a, b) => LinearQuaternion(times, x, y, z, w, a, b, tolerance));
            SetCurve(dest, path, type, prefix + ".x", BuildLinear(times, x, keep), ref count);
            SetCurve(dest, path, type, prefix + ".y", BuildLinear(times, y, keep), ref count);
            SetCurve(dest, path, type, prefix + ".z", BuildLinear(times, z, keep), ref count);
            SetCurve(dest, path, type, prefix + ".w", BuildLinear(times, w, keep), ref count);
            return;
        }

        BezierFit bezier = ReduceCubic(times, x, y, z, w, tolerance, 2);
        AnimationCurve cx = BuildBezier(times, x, bezier, 0);
        AnimationCurve cy = BuildBezier(times, y, bezier, 1);
        AnimationCurve cz = BuildBezier(times, z, bezier, 2);
        AnimationCurve cw = BuildBezier(times, w, bezier, 3);
        if (!QuaternionHolds(cx, cy, cz, cw, times, x, y, z, w, tolerance))
        {
            List<int> keep = Greedy(times.Length, (a, b) => LinearQuaternion(times, x, y, z, w, a, b, tolerance));
            cx = BuildLinear(times, x, keep);
            cy = BuildLinear(times, y, keep);
            cz = BuildLinear(times, z, keep);
            cw = BuildLinear(times, w, keep);
            count.linearFallback += 4;
        }
        SetCurve(dest, path, type, prefix + ".x", cx, ref count);
        SetCurve(dest, path, type, prefix + ".y", cy, ref count);
        SetCurve(dest, path, type, prefix + ".z", cz, ref count);
        SetCurve(dest, path, type, prefix + ".w", cw, ref count);
    }

    private static void WriteVector(
        AnimationClip dest,
        string path,
        Type type,
        string prefix,
        ChannelCurves curves,
        float frameRate,
        float length,
        float tolerance,
        TolSource tolSource,
        ref WriteCount count)
    {
        if (!s_opt.reduceKeys)
        {
            CopyChannel(dest, path, type, prefix, curves, false, ref count);
            return;
        }
        float[] times = SampleTimes(length, frameRate, curves.x, curves.y, curves.z, null);
        float[] x = Evaluate(curves.x, times);
        float[] y = Evaluate(curves.y, times);
        float[] z = Evaluate(curves.z, times);
        if (HasNonFinite(x) || HasNonFinite(y) || HasNonFinite(z))
        {
            CopyChannel(dest, path, type, prefix, curves, false, ref count);
            return;
        }
        bool relative = tolSource != TolSource.Given;
        if (tolSource == TolSource.PositionPercent)
            tolerance = s_opt.positionError;
        else if (tolSource == TolSource.ScalePercent)
            tolerance = s_opt.scaleError;
        WriteVectorSamples(dest, path, type, prefix, times, x, y, z, tolerance, relative, ref count);
    }

    private static void WriteEachAxis(
        AnimationClip dest,
        string path,
        Type type,
        string prefix,
        ChannelCurves curves,
        float frameRate,
        float length,
        float tolerance,
        TolSource tolSource,
        ref WriteCount count)
    {
        if (curves == null || string.IsNullOrEmpty(prefix))
            return;
        WriteOneAxis(dest, path, type, prefix + ".x", curves.x, frameRate, length, tolerance, tolSource, ref count);
        WriteOneAxis(dest, path, type, prefix + ".y", curves.y, frameRate, length, tolerance, tolSource, ref count);
        WriteOneAxis(dest, path, type, prefix + ".z", curves.z, frameRate, length, tolerance, tolSource, ref count);
        WriteOneAxis(dest, path, type, prefix + ".w", curves.w, frameRate, length, tolerance, tolSource, ref count);
    }

    private static void WriteOneAxis(
        AnimationClip dest,
        string path,
        Type type,
        string property,
        AnimationCurve curve,
        float frameRate,
        float length,
        float tolerance,
        TolSource tolSource,
        ref WriteCount count)
    {
        if (curve == null || curve.length == 0)
            return;
        EditorCurveBinding binding = EditorCurveBinding.FloatCurve(path, type, property);
        if (!s_opt.reduceKeys)
        {
            AnimationUtility.SetEditorCurve(dest, binding, curve);
            count.keysAfter += curve.length;
            return;
        }
        float[] times = SampleTimes(length, frameRate, curve, null, null, null);
        float[] values = Evaluate(curve, times);
        if (HasNonFinite(values))
        {
            AnimationUtility.SetEditorCurve(dest, binding, curve);
            count.keysAfter += curve.length;
            return;
        }
        bool relative = tolSource == TolSource.PositionPercent || tolSource == TolSource.ScalePercent;
        if (relative)
            tolerance = tolSource == TolSource.PositionPercent ? s_opt.positionError : s_opt.scaleError;
        if (tolSource == TolSource.ScalePercent && s_opt.removeConstantScale && StaysNearScalar(values, tolerance))
        {
            count.dropped++;
            return;
        }
        WriteFloat(dest, binding, times, values, tolerance, relative, ref count);
    }

    private static void WriteVectorSamples(
        AnimationClip dest,
        string path,
        Type type,
        string prefix,
        float[] times,
        float[] x,
        float[] y,
        float[] z,
        float tolerance,
        bool relative,
        ref WriteCount count)
    {
        if (HasNonFinite(x) || HasNonFinite(y) || HasNonFinite(z))
        {
            Debug.LogError("[AsiKeyClipCompressor] 曲线含非有限值，已跳过 " + path + "/" + prefix);
            return;
        }

        if (!s_opt.bezier)
        {
            List<int> keep = relative
                ? Greedy(times.Length, (a, b) => LinearVectorRelative(times, x, y, z, a, b, tolerance))
                : Greedy(times.Length, (a, b) => LinearVector(times, x, y, z, a, b, tolerance));
            SetCurve(dest, path, type, prefix + ".x", BuildLinear(times, x, keep), ref count);
            SetCurve(dest, path, type, prefix + ".y", BuildLinear(times, y, keep), ref count);
            SetCurve(dest, path, type, prefix + ".z", BuildLinear(times, z, keep), ref count);
            return;
        }

        BezierFit bezier = ReduceCubic(times, x, y, z, null, tolerance, 1);
        AnimationCurve cx = BuildBezier(times, x, bezier, 0);
        AnimationCurve cy = BuildBezier(times, y, bezier, 1);
        AnimationCurve cz = BuildBezier(times, z, bezier, 2);
        if (!VectorHolds(cx, cy, cz, times, x, y, z, tolerance))
        {
            List<int> keep = Greedy(times.Length, (a, b) => LinearVector(times, x, y, z, a, b, tolerance));
            cx = BuildLinear(times, x, keep);
            cy = BuildLinear(times, y, keep);
            cz = BuildLinear(times, z, keep);
            count.linearFallback += 3;
        }
        SetCurve(dest, path, type, prefix + ".x", cx, ref count);
        SetCurve(dest, path, type, prefix + ".y", cy, ref count);
        SetCurve(dest, path, type, prefix + ".z", cz, ref count);
    }

    private static void WriteFloat(
        AnimationClip dest,
        EditorCurveBinding binding,
        float[] times,
        float[] values,
        float tolerance,
        bool relative,
        ref WriteCount count)
    {
        AnimationCurve curve;
        if (!s_opt.bezier)
        {
            List<int> keep = relative
                ? Greedy(times.Length, (a, b) => LinearFloatRelative(times, values, a, b, tolerance))
                : Greedy(times.Length, (a, b) => LinearFloat(times, values, a, b, tolerance));
            curve = BuildLinear(times, values, keep);
        }
        else
        {
            BezierFit bezier = ReduceCubic(times, values, null, null, null, tolerance, 0);
            curve = BuildBezier(times, values, bezier, 0);
            if (!CurveHolds(curve, times, values, tolerance))
            {
                List<int> keep = Greedy(times.Length, (a, b) => LinearFloat(times, values, a, b, tolerance));
                curve = BuildLinear(times, values, keep);
                count.linearFallback++;
            }
        }
        AnimationUtility.SetEditorCurve(dest, binding, curve);
        count.keysAfter += curve.length;
    }

    private static void SetCurve(
        AnimationClip dest,
        string path,
        Type type,
        string property,
        AnimationCurve curve,
        ref WriteCount count)
    {
        EditorCurveBinding binding = EditorCurveBinding.FloatCurve(path, type, property);
        AnimationUtility.SetEditorCurve(dest, binding, curve);
        count.keysAfter += curve.length;
    }

    private static void CopyChannel(
        AnimationClip dest,
        string path,
        Type type,
        string prefix,
        ChannelCurves curves,
        bool quat,
        ref WriteCount count)
    {
        CopyOne(dest, path, type, prefix + ".x", curves.x, ref count);
        CopyOne(dest, path, type, prefix + ".y", curves.y, ref count);
        CopyOne(dest, path, type, prefix + ".z", curves.z, ref count);
        if (quat)
            CopyOne(dest, path, type, prefix + ".w", curves.w, ref count);
    }

    private static void CopyOne(AnimationClip dest, string path, Type type, string property, AnimationCurve curve, ref WriteCount count)
    {
        if (curve == null || curve.length == 0)
            return;
        EditorCurveBinding binding = EditorCurveBinding.FloatCurve(path, type, property);
        AnimationUtility.SetEditorCurve(dest, binding, curve);
        count.keysAfter += curve.length;
    }

    private static void CopyReferenceCurves(
        AnimationClip dest,
        AnimationClip source,
        EditorCurveBinding[] bindings,
        ref WriteCount count)
    {
        for (int i = 0; i < bindings.Length; i++)
        {
            ObjectReferenceKeyframe[] keys = AnimationUtility.GetObjectReferenceCurve(source, bindings[i]);
            AnimationUtility.SetObjectReferenceCurve(dest, bindings[i], keys);
            if (keys != null)
                count.keysAfter += keys.Length;
        }
    }

    private static BezierFit ReduceCubic(float[] times, float[] x, float[] y, float[] z, float[] w, float tolerance, int mode)
    {
        CubicReducer reducer = new CubicReducer();
        reducer.times = times;
        reducer.x = x;
        reducer.y = y;
        reducer.z = z;
        reducer.w = w;
        reducer.tolerance = tolerance;
        reducer.mode = mode;

        BezierFit fit = new BezierFit();
        int count = times.Length;
        if (count <= 0)
            return fit;
        fit.index.Add(0);
        fit.inX.Add(0f);
        fit.outX.Add(0f);
        fit.inY.Add(0f);
        fit.outY.Add(0f);
        fit.inZ.Add(0f);
        fit.outZ.Add(0f);
        fit.inW.Add(0f);
        fit.outW.Add(0f);
        if (count == 1)
            return fit;

        int anchor = 0;
        while (anchor < count - 1)
        {
            int end = Farthest(anchor, count - 1, reducer.TrySpan);
            if (!reducer.TrySpan(anchor, end))
                end = anchor + 1;
            reducer.TrySpan(anchor, end);
            int last = fit.outX.Count - 1;
            fit.outX[last] = reducer.m0x;
            fit.outY[last] = reducer.m0y;
            fit.outZ[last] = reducer.m0z;
            fit.outW[last] = reducer.m0w;
            if (last == 0)
            {
                fit.inX[0] = reducer.m0x;
                fit.inY[0] = reducer.m0y;
                fit.inZ[0] = reducer.m0z;
                fit.inW[0] = reducer.m0w;
            }
            fit.index.Add(end);
            fit.inX.Add(reducer.m1x);
            fit.outX.Add(reducer.m1x);
            fit.inY.Add(reducer.m1y);
            fit.outY.Add(reducer.m1y);
            fit.inZ.Add(reducer.m1z);
            fit.outZ.Add(reducer.m1z);
            fit.inW.Add(reducer.m1w);
            fit.outW.Add(reducer.m1w);
            anchor = end;
        }
        return fit;
    }

    private static int Farthest(int anchor, int last, Func<int, int, bool> spanOk)
    {
        if (spanOk(anchor, last))
            return last;
        int best = anchor + 1;
        int step = 2;
        while (best < last)
        {
            int probe = anchor + step;
            if (probe > last)
                probe = last;
            if (probe <= best)
                break;
            if (spanOk(anchor, probe))
            {
                best = probe;
                if (best >= last)
                    return best;
                if (step >= last)
                    break;
                step *= 2;
                continue;
            }
            for (int end = best + 1; end < probe; end++)
            {
                if (!spanOk(anchor, end))
                    return best;
                best = end;
            }
            return best;
        }
        return best;
    }

    private static void SolveSlopes(float[] times, float[] values, int anchor, int end, float dt, out float m0, out float m1)
    {
        float v0 = values[anchor];
        float v1 = values[end];
        float chord = (v1 - v0) / dt;
        if (end <= anchor + 1)
        {
            m0 = chord;
            m1 = chord;
            return;
        }

        double scc = 0;
        double scd = 0;
        double sdd = 0;
        double bc = 0;
        double bd = 0;
        float t0 = times[anchor];
        for (int i = anchor + 1; i < end; i++)
        {
            float u = (times[i] - t0) / dt;
            float u2 = u * u;
            float u3 = u2 * u;
            double c = (u3 - 2.0 * u2 + u) * dt;
            double d = (u3 - u2) * dt;
            double h00 = 2.0 * u3 - 3.0 * u2 + 1.0;
            double h01 = -2.0 * u3 + 3.0 * u2;
            double target = values[i] - (h00 * v0 + h01 * v1);
            scc += c * c;
            scd += c * d;
            sdd += d * d;
            bc += c * target;
            bd += d * target;
        }

        double det = scc * sdd - scd * scd;
        if (det < 1e-18 && det > -1e-18)
        {
            m0 = chord;
            m1 = chord;
            return;
        }
        m0 = (float)((sdd * bc - scd * bd) / det);
        m1 = (float)((scc * bd - scd * bc) / det);
        if (float.IsNaN(m0) || float.IsInfinity(m0))
            m0 = chord;
        if (float.IsNaN(m1) || float.IsInfinity(m1))
            m1 = chord;
        // 斜率超过弦的 4 倍时，两帧之间会鼓出采样网格。压回去后误差检查不通过就会提前断键。
        ClampSlope(ref m0, chord);
        ClampSlope(ref m1, chord);
    }

    private static void ClampSlope(ref float slope, float chord)
    {
        float cap = Abs(chord) * 4f + 0.000001f;
        if (slope > cap)
            slope = cap;
        else if (slope < -cap)
            slope = -cap;
    }

    private static AnimationCurve BuildBezier(float[] times, float[] values, BezierFit fit, int channel)
    {
        List<float> inSlope;
        List<float> outSlope;
        if (channel == 1)
        {
            inSlope = fit.inY;
            outSlope = fit.outY;
        }
        else if (channel == 2)
        {
            inSlope = fit.inZ;
            outSlope = fit.outZ;
        }
        else if (channel == 3)
        {
            inSlope = fit.inW;
            outSlope = fit.outW;
        }
        else
        {
            inSlope = fit.inX;
            outSlope = fit.outX;
        }

        int n = fit.index.Count;
        Keyframe[] keys = new Keyframe[n];
        for (int i = 0; i < n; i++)
        {
            int s = fit.index[i];
            keys[i] = new Keyframe(times[s], values[s], inSlope[i], outSlope[i]);
        }
        AnimationCurve curve = new AnimationCurve(keys);
        LockBroken(curve, inSlope, outSlope);
        return curve;
    }

    private static AnimationCurve BuildLinear(float[] times, float[] values, List<int> keep)
    {
        Keyframe[] keys = new Keyframe[keep.Count];
        for (int i = 0; i < keep.Count; i++)
        {
            int s = keep[i];
            keys[i] = new Keyframe(times[s], values[s]);
        }
        AnimationCurve curve = new AnimationCurve(keys);
        LockLinear(curve);
        return curve;
    }

    private static bool CurveHolds(AnimationCurve curve, float[] times, float[] values, float tolerance)
    {
        float limit = tolerance * 1.05f;
        for (int i = 0; i < times.Length; i++)
        {
            if (Abs(curve.Evaluate(times[i]) - values[i]) > limit)
                return false;
        }
        return true;
    }

    private static bool VectorHolds(
        AnimationCurve cx,
        AnimationCurve cy,
        AnimationCurve cz,
        float[] times,
        float[] x,
        float[] y,
        float[] z,
        float tolerance)
    {
        float tolSq = tolerance * tolerance * 1.1025f;
        for (int i = 0; i < times.Length; i++)
        {
            float dx = cx.Evaluate(times[i]) - x[i];
            float dy = cy.Evaluate(times[i]) - y[i];
            float dz = cz.Evaluate(times[i]) - z[i];
            if (dx * dx + dy * dy + dz * dz > tolSq)
                return false;
        }
        return true;
    }

    private static bool QuaternionHolds(
        AnimationCurve cx,
        AnimationCurve cy,
        AnimationCurve cz,
        AnimationCurve cw,
        float[] times,
        float[] x,
        float[] y,
        float[] z,
        float[] w,
        float tolerance)
    {
        float limit = tolerance * 1.05f;
        for (int i = 0; i < times.Length; i++)
        {
            float hx = cx.Evaluate(times[i]);
            float hy = cy.Evaluate(times[i]);
            float hz = cz.Evaluate(times[i]);
            float hw = cw.Evaluate(times[i]);
            float mag = Mathf.Sqrt(hx * hx + hy * hy + hz * hz + hw * hw);
            if (mag < 1e-8f)
                return false;
            float inv = 1f / mag;
            float angle = Quaternion.Angle(
                new Quaternion(hx * inv, hy * inv, hz * inv, hw * inv),
                new Quaternion(x[i], y[i], z[i], w[i]));
            if (angle > limit)
                return false;
        }
        return true;
    }

    private static bool LinearFloat(float[] times, float[] values, int anchor, int end, float tolerance)
    {
        float dt = times[end] - times[anchor];
        if (dt <= 0.0000001f)
            return true;
        float v0 = values[anchor];
        float v1 = values[end];
        float t0 = times[anchor];
        for (int i = anchor + 1; i < end; i++)
        {
            float u = (times[i] - t0) / dt;
            if (Abs(Mathf.Lerp(v0, v1, u) - values[i]) > tolerance)
                return false;
        }
        return true;
    }

    private static bool LinearVector(float[] times, float[] x, float[] y, float[] z, int anchor, int end, float tolerance)
    {
        float dt = times[end] - times[anchor];
        if (dt <= 0.0000001f)
            return true;
        float tolSq = tolerance * tolerance;
        float t0 = times[anchor];
        for (int i = anchor + 1; i < end; i++)
        {
            float u = (times[i] - t0) / dt;
            float dx = Mathf.Lerp(x[anchor], x[end], u) - x[i];
            float dy = Mathf.Lerp(y[anchor], y[end], u) - y[i];
            float dz = Mathf.Lerp(z[anchor], z[end], u) - z[i];
            if (dx * dx + dy * dy + dz * dz > tolSq)
                return false;
        }
        return true;
    }

    private static bool LinearFloatRelative(float[] times, float[] values, int anchor, int end, float errorPercent)
    {
        float dt = times[end] - times[anchor];
        if (dt <= 0.0000001f)
            return true;
        float frac = errorPercent * 0.01f;
        float v0 = values[anchor];
        float v1 = values[end];
        float t0 = times[anchor];
        for (int i = anchor + 1; i < end; i++)
        {
            float u = (times[i] - t0) / dt;
            float delta = Mathf.Lerp(v0, v1, u) - values[i];
            if (Abs(delta) > Abs(values[i]) * frac)
                return false;
        }
        return true;
    }

    private static bool LinearVectorRelative(
        float[] times,
        float[] x,
        float[] y,
        float[] z,
        int anchor,
        int end,
        float errorPercent)
    {
        float dt = times[end] - times[anchor];
        if (dt <= 0.0000001f)
            return true;
        float frac = errorPercent * 0.01f;
        float t0 = times[anchor];
        for (int i = anchor + 1; i < end; i++)
        {
            float u = (times[i] - t0) / dt;
            float rx = Mathf.Lerp(x[anchor], x[end], u);
            float ry = Mathf.Lerp(y[anchor], y[end], u);
            float rz = Mathf.Lerp(z[anchor], z[end], u);
            if (!WithinRelative(x[i], y[i], z[i], rx, ry, rz, frac))
                return false;
        }
        return true;
    }

    private static bool WithinRelative(float ox, float oy, float oz, float rx, float ry, float rz, float frac)
    {
        float dx = ox - rx;
        float dy = oy - ry;
        float dz = oz - rz;
        float limit = frac * frac;
        float distSq = dx * dx + dy * dy + dz * dz;
        float magSq = ox * ox + oy * oy + oz * oz;
        if (distSq > magSq * limit)
            return false;
        if (dx * dx > ox * ox * limit)
            return false;
        if (dy * dy > oy * oy * limit)
            return false;
        if (dz * dz > oz * oz * limit)
            return false;
        return true;
    }

    private static bool LinearQuaternion(
        float[] times,
        float[] x,
        float[] y,
        float[] z,
        float[] w,
        int anchor,
        int end,
        float tolerance)
    {
        float dt = times[end] - times[anchor];
        if (dt <= 0.0000001f)
            return true;
        float t0 = times[anchor];
        for (int i = anchor + 1; i < end; i++)
        {
            float u = (times[i] - t0) / dt;
            if (NlerpAngle(x[anchor], y[anchor], z[anchor], w[anchor], x[end], y[end], z[end], w[end], u, x[i], y[i], z[i], w[i]) > tolerance)
                return false;
        }
        return true;
    }

    private static List<int> Greedy(int count, Func<int, int, bool> spanOk)
    {
        List<int> keep = new List<int>();
        if (count <= 0)
            return keep;
        keep.Add(0);
        int anchor = 0;
        while (anchor < count - 1)
        {
            if (spanOk(anchor, count - 1))
            {
                keep.Add(count - 1);
                break;
            }

            int best = anchor + 1;
            for (int end = anchor + 2; end < count; end++)
            {
                if (!spanOk(anchor, end))
                    break;
                best = end;
            }
            keep.Add(best);
            anchor = best;
        }
        return keep;
    }

    private static float[] SampleTimes(float length, float frameRate, AnimationCurve a, AnimationCurve b, AnimationCurve c, AnimationCurve d)
    {
        float rate = frameRate < 1f ? 30f : frameRate;
        // 贝塞尔要看半帧，避免切线在两帧之间鼓出容差。Unity 削减按帧对齐。
        if (s_opt.bezier)
            rate *= 2f;
        if (length < 0.0000001f)
            length = 0f;
        int steps = Mathf.CeilToInt(length * rate);
        if (steps < 1)
            steps = length <= 0f ? 0 : 1;
        // 小时级片段把网格封顶，关键帧时间仍会加进来，避免编辑器卡死。
        if (steps > 60000)
        {
            rate = length > 0f ? 60000f / length : rate;
            steps = 60000;
        }

        List<float> raw = new List<float>(steps + 8);
        for (int i = 0; i <= steps; i++)
        {
            float t = i / rate;
            if (t > length)
                t = length;
            raw.Add(t);
            if (t >= length)
                break;
        }
        AddKeyTimes(raw, a);
        AddKeyTimes(raw, b);
        AddKeyTimes(raw, c);
        AddKeyTimes(raw, d);
        raw.Sort();

        int n = 0;
        float[] packed = new float[raw.Count];
        for (int i = 0; i < raw.Count; i++)
        {
            float t = raw[i];
            if (n > 0 && t - packed[n - 1] < 0.00005f)
                continue;
            packed[n++] = t;
        }
        if (n == packed.Length)
            return packed;
        float[] times = new float[n];
        for (int i = 0; i < n; i++)
            times[i] = packed[i];
        return times;
    }

    private static void AddKeyTimes(List<float> times, AnimationCurve curve)
    {
        if (curve == null)
            return;
        int n = curve.length;
        for (int i = 0; i < n; i++)
            times.Add(curve[i].time);
    }

    private static float[] Evaluate(AnimationCurve curve, float[] times)
    {
        float[] values = new float[times.Length];
        if (curve == null || curve.length == 0)
            return values;
        for (int i = 0; i < times.Length; i++)
            values[i] = curve.Evaluate(times[i]);
        return values;
    }

    private static float Hermite(float v0, float v1, float m0, float m1, float u, float dt)
    {
        float u2 = u * u;
        float u3 = u2 * u;
        return (2f * u3 - 3f * u2 + 1f) * v0
            + (u3 - 2f * u2 + u) * (m0 * dt)
            + (-2f * u3 + 3f * u2) * v1
            + (u3 - u2) * (m1 * dt);
    }

    private static float NlerpAngle(
        float ax, float ay, float az, float aw,
        float bx, float by, float bz, float bw,
        float u,
        float sx, float sy, float sz, float sw)
    {
        float x = Mathf.Lerp(ax, bx, u);
        float y = Mathf.Lerp(ay, by, u);
        float z = Mathf.Lerp(az, bz, u);
        float w = Mathf.Lerp(aw, bw, u);
        float mag = Mathf.Sqrt(x * x + y * y + z * z + w * w);
        if (mag < 1e-8f)
            return 180f;
        float inv = 1f / mag;
        return Quaternion.Angle(new Quaternion(x * inv, y * inv, z * inv, w * inv), new Quaternion(sx, sy, sz, sw));
    }

    private static void NormalizeQuaternions(float[] x, float[] y, float[] z, float[] w)
    {
        for (int i = 0; i < x.Length; i++)
        {
            float mag = Mathf.Sqrt(x[i] * x[i] + y[i] * y[i] + z[i] * z[i] + w[i] * w[i]);
            if (mag < 1e-8f)
            {
                x[i] = 0f;
                y[i] = 0f;
                z[i] = 0f;
                w[i] = 1f;
                continue;
            }
            float inv = 1f / mag;
            x[i] *= inv;
            y[i] *= inv;
            z[i] *= inv;
            w[i] *= inv;
        }
    }

    private static void MakeContinuous(float[] x, float[] y, float[] z, float[] w)
    {
        for (int i = 1; i < x.Length; i++)
        {
            float dot = x[i - 1] * x[i] + y[i - 1] * y[i] + z[i - 1] * z[i] + w[i - 1] * w[i];
            if (dot >= 0f)
                continue;
            x[i] = -x[i];
            y[i] = -y[i];
            z[i] = -z[i];
            w[i] = -w[i];
        }
    }

    private static void LockLinear(AnimationCurve curve)
    {
        int n = curve.length;
        for (int i = 0; i < n; i++)
        {
            AnimationUtility.SetKeyBroken(curve, i, false);
            if (i > 0)
                AnimationUtility.SetKeyLeftTangentMode(curve, i, AnimationUtility.TangentMode.Linear);
            if (i < n - 1)
                AnimationUtility.SetKeyRightTangentMode(curve, i, AnimationUtility.TangentMode.Linear);
        }
    }

    private static void LockBroken(AnimationCurve curve, List<float> inSlope, List<float> outSlope)
    {
        int n = curve.length;
        for (int i = 0; i < n; i++)
        {
            bool corner = Abs(inSlope[i] - outSlope[i]) > 0.000001f;
            AnimationUtility.SetKeyBroken(curve, i, corner);
            AnimationUtility.SetKeyLeftTangentMode(curve, i, AnimationUtility.TangentMode.Free);
            AnimationUtility.SetKeyRightTangentMode(curve, i, AnimationUtility.TangentMode.Free);
            Keyframe key = curve[i];
            key.inTangent = inSlope[i];
            key.outTangent = outSlope[i];
            curve.MoveKey(i, key);
        }
    }

    private static float HumanRotationTolerance(string group)
    {
        if (group == "RootQ" || group == "MotionQ")
            return RootRotationDegrees;
        return BodyRotationDegrees;
    }

    private static float MuscleTolerance(string property)
    {
        if (IsFingerMuscle(property))
            return FingerMuscle;
        if (property.IndexOf("Eye", StringComparison.Ordinal) >= 0
            || property.IndexOf("Jaw", StringComparison.Ordinal) >= 0)
            return FaceMuscle;
        return BodyMuscle;
    }

    private static bool IsFingerMuscle(string property)
    {
        if (property.IndexOf("LeftHand.", StringComparison.Ordinal) >= 0
            || property.IndexOf("RightHand.", StringComparison.Ordinal) >= 0)
            return true;
        if (property.IndexOf("Thumb", StringComparison.Ordinal) >= 0)
            return true;
        if (property.IndexOf("Index", StringComparison.Ordinal) >= 0)
            return true;
        if (property.IndexOf("Middle", StringComparison.Ordinal) >= 0)
            return true;
        if (property.IndexOf("Ring", StringComparison.Ordinal) >= 0)
            return true;
        return property.IndexOf("Little", StringComparison.Ordinal) >= 0;
    }

    private static float LooseTolerance(EditorCurveBinding binding, float[] values)
    {
        float min = values[0];
        float max = values[0];
        float peak = Abs(values[0]);
        for (int i = 1; i < values.Length; i++)
        {
            float v = values[i];
            if (v < min) min = v;
            if (v > max) max = v;
            float a = Abs(v);
            if (a > peak) peak = a;
        }
        if (binding.type == typeof(SkinnedMeshRenderer))
            return peak <= 1.5f ? 0.01f : 0.5f;
        float range = max - min;
        float tolerance = range * 0.005f;
        if (tolerance < 0.0005f)
            tolerance = 0.0005f;
        return tolerance;
    }

    private static bool TrySplitHumanChannel(string property, out string group, out int axis)
    {
        group = null;
        axis = 0;
        int dot = property.LastIndexOf('.');
        if (dot <= 0 || dot != property.Length - 2)
            return false;
        char c = property[dot + 1];
        if (c == 'x') axis = 0;
        else if (c == 'y') axis = 1;
        else if (c == 'z') axis = 2;
        else if (c == 'w') axis = 3;
        else return false;
        string prefix = property.Substring(0, dot);
        for (int i = 0; i < HumanChannels.Length; i++)
        {
            if (prefix != HumanChannels[i])
                continue;
            group = prefix;
            return true;
        }
        return false;
    }

    private static bool TryAxis(string property, string prefix, out int axis)
    {
        axis = -1;
        if (property.Length != prefix.Length + 2)
            return false;
        if (!property.StartsWith(prefix, StringComparison.Ordinal))
            return false;
        if (property[prefix.Length] != '.')
            return false;
        char c = property[prefix.Length + 1];
        if (c == 'x') axis = 0;
        else if (c == 'y') axis = 1;
        else if (c == 'z') axis = 2;
        else if (c == 'w') axis = 3;
        else return false;
        return true;
    }

    private static ChannelCurves GetHuman(Dictionary<string, ChannelCurves> human, string group)
    {
        ChannelCurves curves;
        if (!human.TryGetValue(group, out curves))
        {
            curves = new ChannelCurves();
            human.Add(group, curves);
        }
        return curves;
    }

    private static BoneCurves GetBone(Dictionary<string, BoneCurves> bones, string path)
    {
        BoneCurves bone;
        if (!bones.TryGetValue(path, out bone))
        {
            bone = new BoneCurves();
            bone.path = path;
            bone.finger = IsFingerPath(path);
            bone.rootPosition = IsRootPositionPath(path);
            bones.Add(path, bone);
        }
        return bone;
    }

    private static void SetAxis(ChannelCurves curves, int axis, AnimationCurve curve)
    {
        if (axis == 0) curves.x = curve;
        else if (axis == 1) curves.y = curve;
        else if (axis == 2) curves.z = curve;
        else curves.w = curve;
    }

    private static bool HasQuat(ChannelCurves curves)
    {
        return curves.x != null && curves.y != null && curves.z != null && curves.w != null;
    }

    private static bool HasVec3(ChannelCurves curves)
    {
        return curves.x != null && curves.y != null && curves.z != null;
    }

    private static bool HasAny(ChannelCurves curves)
    {
        return curves.x != null || curves.y != null || curves.z != null || curves.w != null;
    }

    private static int CountPresent(ChannelCurves curves)
    {
        int n = 0;
        if (curves.x != null) n++;
        if (curves.y != null) n++;
        if (curves.z != null) n++;
        if (curves.w != null) n++;
        return n;
    }

    private static bool IsFingerPath(string path)
    {
        int slash = path.LastIndexOf('/');
        string name = slash >= 0 ? path.Substring(slash + 1) : path;
        if (name.IndexOf("thumb", StringComparison.OrdinalIgnoreCase) >= 0)
            return true;
        if (name.IndexOf("index", StringComparison.OrdinalIgnoreCase) >= 0)
            return true;
        if (name.IndexOf("middle", StringComparison.OrdinalIgnoreCase) >= 0)
            return true;
        if (name.IndexOf("ring", StringComparison.OrdinalIgnoreCase) >= 0)
            return true;
        if (name.IndexOf("little", StringComparison.OrdinalIgnoreCase) >= 0)
            return true;
        return name.IndexOf("finger", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static bool IsRootPositionPath(string path)
    {
        if (string.IsNullOrEmpty(path))
            return true;
        int slash = path.LastIndexOf('/');
        string name = slash >= 0 ? path.Substring(slash + 1) : path;
        if (name.Equals("hips", StringComparison.OrdinalIgnoreCase))
            return true;
        if (name.Equals("hip", StringComparison.OrdinalIgnoreCase))
            return true;
        if (name.Equals("pelvis", StringComparison.OrdinalIgnoreCase))
            return true;
        return name.Equals("root", StringComparison.OrdinalIgnoreCase);
    }

    private static bool NearOne(float[] x, float[] y, float[] z, float tolerance)
    {
        for (int i = 0; i < x.Length; i++)
        {
            if (Abs(x[i] - 1f) > tolerance)
                return false;
            if (Abs(y[i] - 1f) > tolerance)
                return false;
            if (Abs(z[i] - 1f) > tolerance)
                return false;
        }
        return true;
    }

    private static float MaxAbs(float[] values)
    {
        float max = 0f;
        for (int i = 0; i < values.Length; i++)
        {
            float a = Abs(values[i]);
            if (a > max)
                max = a;
        }
        return max;
    }

    private static bool HasNonFinite(float[] values)
    {
        for (int i = 0; i < values.Length; i++)
        {
            if (float.IsNaN(values[i]) || float.IsInfinity(values[i]))
                return true;
        }
        return false;
    }

    private static float Abs(float v)
    {
        return v < 0f ? -v : v;
    }

    private static int CountRefKeys(AnimationClip clip, EditorCurveBinding[] bindings)
    {
        int n = 0;
        for (int i = 0; i < bindings.Length; i++)
        {
            ObjectReferenceKeyframe[] keys = AnimationUtility.GetObjectReferenceCurve(clip, bindings[i]);
            if (keys != null)
                n += keys.Length;
        }
        return n;
    }

    private static float ClipLength(AnimationClip clip)
    {
        float length = clip.length;
        AnimationClipSettings settings = AnimationUtility.GetAnimationClipSettings(clip);
        if (settings.stopTime > length)
            length = settings.stopTime;
        return length;
    }

    private static bool ReadHighQuality(AnimationClip clip)
    {
        SerializedObject src = new SerializedObject(clip);
        SerializedProperty hq = src.FindProperty("m_UseHighQualityCurve");
        return hq != null && hq.boolValue;
    }

    private static void ApplyCurveStorage(AnimationClip dest, bool keepHighQuality)
    {
        SerializedObject dst = new SerializedObject(dest);
        SerializedProperty hqD = dst.FindProperty("m_UseHighQualityCurve");
        if (hqD != null)
            hqD.boolValue = s_opt.highQualityOff ? false : keepHighQuality;
        if (s_opt.highQualityOff || s_opt.reduceKeys)
        {
            SerializedProperty euler = dst.FindProperty("m_EulerEditorCurves");
            if (euler != null && euler.isArray)
                euler.ClearArray();
        }
        dst.ApplyModifiedPropertiesWithoutUndo();
    }

    private static bool StaysNearFirst(float[] x, float[] y, float[] z, float errorPercent)
    {
        float frac = errorPercent * 0.01f;
        for (int i = 1; i < x.Length; i++)
        {
            if (!WithinRelative(x[i], y[i], z[i], x[0], y[0], z[0], frac))
                return false;
        }
        return true;
    }

    private static bool StaysNearScalar(float[] values, float errorPercent)
    {
        if (values.Length == 0)
            return true;
        float frac = errorPercent * 0.01f;
        float origin = values[0];
        for (int i = 1; i < values.Length; i++)
        {
            float delta = values[i] - origin;
            if (Abs(delta) > Abs(values[i]) * frac)
                return false;
        }
        return true;
    }
}

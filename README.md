# AsiKeyframe

仿 3ds Max Biped 的 Unity 编辑器关键帧工具。在场景里摆控制器、打关键帧，再烘焙成普通的 `AnimationClip`（Humanoid 肌肉曲线，或 Generic 变换曲线）。

界面文字是中文。这是纯编辑器工具，代码都在 `Editor` 目录里，不会进播放器构建。

## 功能

- 人形（Humanoid）、通用骨骼（Generic）、物体（镜头、灯光、道具）
- 一个工程里可以放多个单位，挂在同一个工程父级下
- IK：注册、固定、滑动、自由；身体、世界、物体空间
- 约束：位置、旋转、注视、链接。约束参与求值，可以烘焙进片段
- 动画层，TCB 与曲线插值
- 烘焙 `AnimationClip`，也可以从片段导回关键帧
- 姿势库。仓库里不带姿势数据，第一次打开「K帧姿势库」时会自动建一个空库

## 环境

- Unity **2022.3**。本仓库用 **2022.3.62f3** 创建
- 不依赖渲染管线，也不带第三方包

## 打开这个工程

用 Unity Hub 添加本仓库文件夹，用 Unity 2022.3 打开。

- 关键帧：**AsitirTool → K帧工具**
- 姿势库：**AsitirTool → K帧姿势库**

## 装进已有工程

把下面两个文件夹元数据和工具目录一起复制到目标工程的 `Assets` 下：

- `Assets/AsitirMapTool.meta`（目标工程里还没有 `AsitirMapTool` 时才需要）
- `Assets/AsitirMapTool/AsiKeyframe.meta`
- `Assets/AsitirMapTool/AsiKeyframe/`

姿势库默认写到：

`Assets/AsitirMapTool/AsiKeyframe/Data/AsiKeyPoseLibrary.asset`

请保持这个路径。要换位置时，改 `AsiKeyPoseLibrary.DefaultPath`。

## 许可

[MIT License](LICENSE)。Copyright (c) 2026 Asitir。

## English

AsiKeyframe is a Unity Editor keyframe tool in the spirit of 3ds Max Biped. Pose Humanoid characters, Generic rigs, and objects, then bake a standard `AnimationClip`. Editor-only, Chinese UI, Unity 2022.3, no third-party packages. Open the project and use **AsitirTool → K帧工具**, or copy `Assets/AsitirMapTool/AsiKeyframe` into an existing project. MIT License.

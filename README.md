# VRM Quest Unity

在 Meta Quest 上与 VRM 角色对话。Unity 客户端负责手柄交互、录音、角色动画和摄像头取帧。配套的 `vrm-quest-middleware` 负责连接 AstrBot、处理语音和调用可选动作服务。

本仓库按 **Unity 源码集成包** 发布。已有 Quest 工程可以合并 `Assets/`。新项目需要按下文搭建场景，并准备有权使用的 VRM 角色。角色模型、字体、商业动画、Unity 与 Meta SDK 由使用者自行获取。

## 准备工程

1. 使用支持 Android 的 Unity 编辑器，安装 Android Build Support、SDK、NDK 和 OpenJDK。Meta MR Utility Kit 85 的最低编辑器要求为 2022.3.15f1；同时选择能满足下列 XR 包依赖的编辑器版本。
2. 在 Package Manager 安装 OpenXR、Input System、XR Hands、XR Interaction Toolkit **3.x**、XR Core Utilities、AR Foundation、Unity OpenXR: Meta，以及支持 VRM 1.0 的 UniVRM。真实摄像头脚本引用 `Meta.XR.PassthroughCameraAccess`，对应 Meta XR Core SDK 和 MR Utility Kit **85.0.0**。
3. 将 `Assets/` 合并到工程。如果已有同名脚本，在原位置替换内容并保留已有 `.meta`，使场景继续引用原组件。Android 使用 OpenXR，启用与实测手柄对应的 Touch 交互配置，并启用场景所需的手势、透视和 AR 功能。

## 接好场景

| 场景对象 | 组件和引用 |
|---|---|
| XR Origin | 主相机、左右手柄射线及 XR Interaction Manager。使用随包的 `QuestDualUIInput` 时，绑定 tracking space、头显相机和两支 `XRRayInteractor`。 |
| World Space Canvas | 添加 `Tracked Device Graphic Raycaster`。EventSystem 使用 `XR UI Input Module`。创建文字输入框、状态文本、聊天日志、调试面板，发送按钮绑定 `VRMController.OnSendButton`。 |
| VRM 角色与控制器 | 使用 Humanoid Animator 和 `MotionPlayer`。`VRMController` 绑定角色、Motion Player、XR Origin 和对应 UI。`VRMPresence` 与控制器挂在同一物体。需要行走时设置 `QuestProceduralWalker` 与 `QuestLiveFloorAnchor`，绑定 Animator、地面射线和 XR Origin。 |
| Android 构建 | `Assets/Editor/VRMCameraManifest.cs` 自动声明头显摄像头权限。设备会在请求时弹出授权。项目应允许网络与麦克风访问。若启用 Minify，把 `-keep class com.vrm.settings.VRMDeviceSecret { *; }` 加入项目 ProGuard 配置。 |

## 第一次连接

先启动配套中转，再构建并安装 APK。在应用 Canvas 的 **Settings** 输入中转完整 WebSocket 地址，例如 `wss://quest.example.org/ws`，再输入中转的 `VRM_TOKEN`，保存后重启。Android 使用 Keystore 加密设备口令。AstrBot API Key 在中转管理页配置。

先用文字验证完整来回，再测语音与视觉。**右 A** 开始或结束录音，**右 B** 开关输入框，**左 Y** 切换已绑定的调试面板，**左扳机** 手动截图，**右扳机** 默认用于 UI。升级已有工程时，检查 Inspector 中的 `Right Trigger Voice` 已取消勾选。

## 真实环境观察

在支持物理摄像头访问的 Quest 3 / 3S 上开启 `Observation Enabled`，授予头显摄像头权限。每次可用时，客户端从真实相机画面中裁出 VRM 身体朝向附近的区域，编码为 JPEG 并发送到中转。物理视点位于头显，角色面向摄像头视野之外时跳过该帧。`Body Forward` 的蓝色 Z 轴应指向角色正前方。手动截图走 Unity 相机路径。向使用者说明相机数据会送往其配置的中转与 AstrBot，再开启此功能。

## 真机验收

- 启动后验证 Settings 保存与重启恢复，文字能收到回复。
- 射线移入按钮能高亮，右扳机点选不会开始录音，A 键录音需要麦克风授权。
- 授权相机并对准可辨认物体，从中转日志确认 `/presence/observe` 成功，再让角色描述画面。
- 使用 `adb logcat -s Unity` 查看 `[VRMInput]`、`[QuestUI` 和 `[VRMQuestCamera]`。同名脚本冲突时先检查项目中是否重复导入。

仓库自有代码采用 MIT 许可证。已配好的私有场景未包含在本源码包中。最终发布的 APK、角色、字体、声音及第三方 SDK 应分别核对授权。

## HAST-SEC 来源与许可

动作系统集成参考 [HAST-SEC v0.1.1](https://github.com/sola1039/HAST-SEC/tree/v0.1.1)。Copyright (c) 2026 HAST-SEC 项目贡献者。

本仓库的适配工作：Unity 端接收中转传回的动作帧，将四元数姿态映射至 Humanoid 骨骼并播放。

HAST-SEC 原创部分及其修改版遵循[非商业学习与研究许可证 0.1](third_party/HAST-SEC/LICENSE)。非商业再分发须保留来源、许可和第三方声明，标明修改内容。商业使用须事先取得权利人书面授权。[商用授权联系渠道](https://github.com/sola1039/HAST-SEC/issues)

仓库根目录的 MIT 许可证仅适用于本仓库独立原创代码。上游使用的动作数据、角色、模型和软件各自遵循其原始许可，具体来源和引用见[上游第三方资源声明](third_party/HAST-SEC/THIRD_PARTY_NOTICES.txt)。

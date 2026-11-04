# VRLyrics

在 SteamVR 叠加层中显示当前播放歌曲的实时同步歌词（支持逐字卡拉OK动效、双语翻译、多空间锚定模式、VR仪表盘内控面板与随 SteamVR 自动启动）。

---

## 核心特性

- 🎵 **多源歌词聚合**：优先网易云音乐 (YRC 逐字/双语)、QQ音乐在线检索 (LRC/双语翻译)、LRCLIB (欧美开源曲库)、本地 `.lrc` 搜索目录，支持动态优先级切换。
- ⚡ **毫秒级进度精准同步**：通用 Windows SMTC 会话读取 + 本地时钟外推；针对网易云音乐 3.x PC 端内置专属 `%LocalAppData%\NetEase\CloudMusic\cloudmusic.elog` 毫秒级日志探针，解决中途启动或拖拽进度条不同步问题。
- ✨ **卡拉OK视效与动效**：SkiaSharp 离屏渲染 + 60 FPS GPU D3D11 共享纹理；单字渐变擦除、已唱外发光、正在演唱字微浮正弦动效、Cubic Ease-Out 切行平滑滑动。
- 🌍 **多种空间锚定模式**：
  - **头部跟随 (`HeadLocked`)**：视野下方/左下/右下预设，仰角可调；
  - **空间固定 (`WorldLocked`)**：房间 3D 绝对坐标，支持 **VR 手柄扳机/侧键直接抓取拖动** 与摇杆推拉远近、一键移至当前头显正前方；
  - **手腕跟随 (`LeftHand` / `RightHand`)**：手柄正上方、手腕内侧看表姿态、手背外侧预设。
- 🥽 **SteamVR 仪表盘控制面板 (Dashboard Overlay)**：
  - 在 VR 中按下手柄系统键呼出 SteamVR 菜单，点击底栏 VRLyrics 图标即可直接通过激光笔调节歌词显示、切换位置、调节透明度与尺寸、微调时间偏移、切换歌词源与 5 套配色主题。
- 🖥️ **桌面 WPF 控制中心 & 系统托盘**：
  - 1:1 实时渲染预览画布；
  - 全局时间偏移微调 + **单曲专属时间偏移自动记忆**；
  - 随 SteamVR 启动自动运行 (`vrlyrics.vrmanifest` 注册与自启动)；
  - 随 SteamVR 退出自动关闭联动。

---

## 项目结构

```
e:\VR Dev\VRLyrics\
├─ src\
│  ├─ VRLyrics.Core\          # 核心实体、配置模型 (LyricsOverlaySettings)、LRC/YRC 解析、时间轴与歌曲打分
│  ├─ VRLyrics.Providers\     # 网易云 / QQ音乐 / LRCLIB / 本地歌词提供程序与双层缓存
│  ├─ VRLyrics.Media\         # SMTC 媒体会话追踪、网易云 elog 探针与时钟插值
│  ├─ VRLyrics.Overlay\       # OpenVR 叠加层封装、D3D11 纹理桥接、SkiaSharp 渲染、仪表盘与手柄抓取
│  └─ VRLyrics.App\           # WPF 桌面主程序：设置窗口、系统托盘、单实例互斥、自启动管理
├─ tests\
│  └─ VRLyrics.Core.Tests\    # 单元测试（覆盖解析、渲染、3D矩阵、手柄抓取、仪表盘命中、vrmanifest 等）
├─ publish.ps1                # 一键单文件打包发布脚本
└─ VRLyrics.sln
```

---

## 快速开始

### 1. 运行桌面设置端
```powershell
dotnet run --project "src\VRLyrics.App"
```

### 2. 运行单元测试
```powershell
dotnet test VRLyrics.sln
```

### 3. 一键编译打包发布
```powershell
# 构建轻量单文件便携版（依赖系统 .NET 10 Desktop Runtime，体积仅 ~27MB）
powershell -ExecutionPolicy Bypass -File .\publish.ps1

# 构建完全独立自包含版（免装任何 .NET 运行时）
powershell -ExecutionPolicy Bypass -File .\publish.ps1 -SelfContained
```
构建产物输出于 `dist\VRLyrics-win-x64\`。

---

## Git 版本管理规范

本项目采用 Git 进行版本管理与发布归档：

- **查看当前版本标签**：`git tag -l -n`
- **查看提交历史**：`git log --oneline --graph`
- **创建新功能分支**：`git checkout -b feature/xxx`
- **发布新版本**：
  ```powershell
  git add .
  git commit -m "feat: 发布 v1.x.x 版本"
  git tag -a v1.x.x -m "VRLyrics v1.x.x 正式版发布"
  ```
- **生成全量便携归档包 (Git Bundle)**：
  ```powershell
  git bundle create "VRLyrics-repo-v1.x.x.bundle" --all
  # 恢复方法：git clone VRLyrics-repo-v1.x.x.bundle VRLyrics
  ```


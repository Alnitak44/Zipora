# 解压重构验收：2.0.25

## 实现范围

- 拖放使用 Windows OLE 虚拟文件协议（FileGroupDescriptorW / FileContents / IStream），目标请求内容时才提取；不再在单击、选择或悬停时预解压。
- 目录树与文件列表使用同一份不可变选择计划，保留文件夹名称、子目录与空目录，排除未选择的相邻目录。
- Windows Shell 原生拖放图像；不支持的目标使用不接收鼠标输入的跟随小图标。仅提供复制效果，不删除压缩包内容。
- 工具栏新增“解压到…”。显式解压使用同卷独立暂存目录，完成后才移动到新建的压缩包同名目录，已有目录不覆盖、自动加序号。
- 取消按钮在忙碌时仍可用；关闭窗口先请求取消，等待本应用的提取任务结束再退出。
- 预检目标/临时磁盘空间，运行时保留安全空间；阻止路径穿越、链接条目、Windows 非法路径及同名冲突。
- 临时目录为 %TEMP%/Zipora/s-随机标识（短名，避免长条目叠加后超过 MAX_PATH）；正常结束拖放后清理其提取目录，关闭时清理本窗口目录。被外部程序占用的文件可能保留，下次启动会尝试清理超过一天且不属于活动实例的目录，并回收旧版 `session-` 目录。

## 自动化结果

2026-10-16 至 2026-10-17，本机最终运行通过 49 项检查，输出在 artifacts/regression-20261016-final。

- 真实 ZIP / 加密 7z 创建、目录列举和选择提取。
- 中文、@ 文件名、嵌套目录、空目录、重叠多选、未选项目排除。
- 路径穿越、绝对路径、设备名、大小写冲突、链接拒绝。
- 拖放清单二进制布局、原生文件流、延迟提取仅一次、原生拖放图像初始化。
- Windows Shell 文件夹 IDropTarget 实际复制嵌套文件、空目录；异步模式及延迟内容准备。
- 空选择不提取整包、空间不足阻止、错误密码正常返回。
- 64 MiB 文件在收到提取进度后取消，确认进程退出及输出清理。
- 多实例临时目录隔离。
- 20,000 个根文件的选择计划：最终运行约 76 ms（本机结果，不是性能保证）。

运行方式（Windows、.NET 8 SDK，输出目录每次使用新名称）：

```powershell
dotnet run --project tests/ExtractionRegression -c Release -- artifacts/extraction-test "src/Zipora/7zip/7z.exe"
```

## 界面验收与剩余边界

已检查启动、工具栏、浏览目录、Enter 打开、Backspace 返回、拖到自身无效区域后恢复，不会假报成功。

界面工具不允许鼠标拖出目标窗口边界，故 **跨应用实际鼠标拖放、图标连续跟随、多显示器 DPI 的视觉效果尚需人工验收**。原生 Shell 接口复制测试不等于这项视觉验收。

建议人工检查：

1. 从列表拖单个文件、整个目录、Ctrl 多选到资源管理器空白处。
2. 先点目录，再点另一个文件，拖出结果必须是后者。
3. 进入子目录后拖出、从左侧目录树拖出，检查没有多余祖先目录。
4. 拖到已有同名目标，确认资源管理器冲突提示和取消行为。
5. 大型固实 7z、加密 RAR、分卷包、网络共享、跨盘目标、混合 DPI 显示器。
6. 提取中取消/关闭、目标窗口关闭、目标空间中途耗尽。

当前是兼容 Windows 虚拟文件的普通拖放，仍需要临时空间，并非 Bandizip 的直接定位目标目录“快速拖放”。只接受 CF_HDROP 的第三方应用不在已验证兼容范围；可以使用“解压到…”。

## 参考

- [Microsoft Shell clipboard formats](https://learn.microsoft.com/en-us/windows/win32/shell/clipboard)
- [IDataObjectAsyncCapability](https://learn.microsoft.com/en-us/windows/win32/api/shldisp/nn-shldisp-idataobjectasynccapability)
- [Shell handler binding](https://learn.microsoft.com/en-us/windows/win32/api/shobjidl_core/nf-shobjidl_core-ishellitem-bindtohandler)
- [Bandizip fast drag and drop](https://en.bandisoft.com/bandizip/help/fastdnd/)

未进行线上部署。不能仅凭上述测试声称已经达到全部场景的工业级稳定性。
